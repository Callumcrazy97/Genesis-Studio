using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Where a game's scripts allocate, exactly: the bytes each PGSL command allocates (StringOf,
/// DsGridGetRegion...), and the bytes each event and worker job allocates outside the commands it
/// calls (joining text with +, new values: the script's own work). Off unless
/// <c>GENESIS_ALLOCATION_LOG=commands</c>; then the allocation log line every few seconds names
/// them, and a recording's report has a table of them. It reads the thread's allocation counter
/// around each command, which costs some tens of nanoseconds a call, so it is for finding where
/// memory goes, not for timing.
/// </summary>
/// <remarks>
/// A command that runs script code (an event it raises, a Script it calls) counts what that code
/// allocates as its own. An event raised inside another event is counted in both.
/// </remarks>
public static class PgslAllocationLedger
{
    /// <summary>The value of GENESIS_ALLOCATION_LOG that turns the ledger on (with the allocation log).</summary>
    public const string Mode = "commands";

    /// <summary>Whether commands, events and jobs are measured. Set from the environment at start.</summary>
    public static bool Enabled = string.Equals(Environment.GetEnvironmentVariable("GENESIS_ALLOCATION_LOG"), Mode, StringComparison.OrdinalIgnoreCase);

    // Each thread adds to a sheet of its own (the game's thread, each job worker), so measuring takes
    // no lock; a report reads them all, a little stale at worst.
    private sealed class Sheet
    {
        public long[] Bytes = new long[2048];
        public long[] Calls = new long[2048];
        public long CommandTotal;
        public readonly Dictionary<string, Scope> Scopes = new(StringComparer.Ordinal);
    }

    private sealed class Scope
    {
        public long Total;
        public long InCommands;
        public long Runs;
    }

    /// <summary>A command's allocations: bytes in all, and how many calls made them.</summary>
    public readonly record struct CommandAllocation(string Command, long Bytes, long Calls);

    /// <summary>An event's or job's allocations: in all, in the commands it called, and how many runs.</summary>
    public readonly record struct ScopeAllocation(string Name, long Bytes, long InCommands, long Runs)
    {
        /// <summary>What the script's own code allocated (joining text, new values), outside commands.</summary>
        public long Outside => Math.Max(0, Bytes - InCommands);
    }

    [ThreadStatic] private static Sheet _sheet;
    private static readonly List<Sheet> Sheets = new();
    private static long[] _reportedBytes = Array.Empty<long>();
    private static long[] _reportedCalls = Array.Empty<long>();
    private static Dictionary<string, (long Total, long InCommands, long Runs)> _reportedScopes = new(StringComparer.Ordinal);

    private static Sheet Mine
    {
        get
        {
            Sheet sheet = _sheet;
            if (sheet != null) return sheet;
            sheet = new Sheet();
            lock (Sheets) Sheets.Add(sheet);
            return _sheet = sheet;
        }
    }

    /// <summary>The bytes the commands this thread ran have allocated so far (to subtract from an event's).</summary>
    internal static long CommandBytesSoFar => Mine.CommandTotal;

    internal static void AddCommand(int id, long bytes)
    {
        if (id < 0 || bytes < 0) return;
        Sheet sheet = Mine;
        if (id >= sheet.Bytes.Length)
        {
            int size = Math.Max(id + 1, sheet.Bytes.Length * 2);
            long[] bytesBySlot = sheet.Bytes, callsBySlot = sheet.Calls;
            Array.Resize(ref bytesBySlot, size);
            Array.Resize(ref callsBySlot, size);
            sheet.Bytes = bytesBySlot;
            sheet.Calls = callsBySlot;
        }
        sheet.Bytes[id] += bytes;
        sheet.Calls[id]++;
        sheet.CommandTotal += bytes;
    }

    /// <summary>Adds one run of an event ("Game.Step") or job ("job McMeshChunk"): its bytes in all and in commands.</summary>
    internal static void AddScope(string name, long total, long inCommands)
    {
        if (string.IsNullOrEmpty(name) || total < 0) return;
        Sheet sheet = Mine;
        lock (sheet.Scopes)
        {
            if (!sheet.Scopes.TryGetValue(name, out Scope scope)) sheet.Scopes[name] = scope = new Scope();
            scope.Total += total;
            scope.InCommands += Math.Max(0, inCommands);
            scope.Runs++;
        }
    }

    /// <summary>Forgets everything measured so far (a recording starting).</summary>
    public static void Reset()
    {
        lock (Sheets)
        {
            foreach (Sheet sheet in Sheets)
            {
                Array.Clear(sheet.Bytes);
                Array.Clear(sheet.Calls);
                sheet.CommandTotal = 0;
                lock (sheet.Scopes) sheet.Scopes.Clear();
            }
            _reportedBytes = Array.Empty<long>();
            _reportedCalls = Array.Empty<long>();
            _reportedScopes = new Dictionary<string, (long, long, long)>(StringComparer.Ordinal);
        }
    }

    /// <summary>Every command's allocations since the last reset, most bytes first.</summary>
    public static IReadOnlyList<CommandAllocation> Commands(int count, Func<int, string> name)
    {
        (long[] bytes, long[] calls) = SumCommands();
        return Rank(bytes, calls, count, name);
    }

    /// <summary>Every event's and job's allocations since the last reset, most allocated outside commands first.</summary>
    public static IReadOnlyList<ScopeAllocation> Scopes(int count) =>
        SumScopes().Select(pair => new ScopeAllocation(pair.Key, pair.Value.Total, pair.Value.InCommands, pair.Value.Runs))
            .OrderByDescending(scope => scope.Outside).Take(Math.Max(0, count)).ToList();

    /// <summary>
    /// The allocation log's lines: what commands, events and jobs allocated since the last call
    /// (empty when nothing was measured).
    /// </summary>
    public static string TakeSummary(Func<int, string> name, int count = 10)
    {
        (long[] bytes, long[] calls) = SumCommands();
        Dictionary<string, (long Total, long InCommands, long Runs)> scopes = SumScopes();
        long[] newBytes = new long[bytes.Length], newCalls = new long[calls.Length];
        lock (Sheets)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                newBytes[i] = bytes[i] - (i < _reportedBytes.Length ? _reportedBytes[i] : 0);
                newCalls[i] = calls[i] - (i < _reportedCalls.Length ? _reportedCalls[i] : 0);
            }
            _reportedBytes = bytes;
            _reportedCalls = calls;
            var newScopes = new List<ScopeAllocation>();
            foreach (KeyValuePair<string, (long Total, long InCommands, long Runs)> pair in scopes)
            {
                _reportedScopes.TryGetValue(pair.Key, out var before);
                newScopes.Add(new ScopeAllocation(pair.Key, pair.Value.Total - before.Total, pair.Value.InCommands - before.InCommands, pair.Value.Runs - before.Runs));
            }
            _reportedScopes = scopes;

            var text = new StringBuilder();
            IReadOnlyList<CommandAllocation> top = Rank(newBytes, newCalls, count, name);
            if (top.Count > 0)
                text.Append("Allocated by PGSL command: ").Append(string.Join(", ", top.Select(Describe)));
            List<ScopeAllocation> outside = newScopes.Where(scope => scope.Outside > 0).OrderByDescending(scope => scope.Outside).Take(count).ToList();
            if (outside.Count > 0)
            {
                if (text.Length > 0) text.Append(". ");
                text.Append("Allocated by scripts outside commands (joining text, new values): ")
                    .Append(string.Join(", ", outside.Select(scope => string.Create(CultureInfo.InvariantCulture,
                        $"{scope.Name} {Megabytes(scope.Outside)} in {scope.Runs} runs (its commands {Megabytes(scope.InCommands)})"))));
            }
            return text.ToString();
        }
    }

    private static string Describe(CommandAllocation command) => string.Create(CultureInfo.InvariantCulture,
        $"{command.Command} {Megabytes(command.Bytes)} in {command.Calls} calls ({command.Bytes / Math.Max(1, command.Calls)} B each)");

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB";

    private static IReadOnlyList<CommandAllocation> Rank(long[] bytes, long[] calls, int count, Func<int, string> name)
    {
        var ranked = new List<CommandAllocation>();
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] > 0) ranked.Add(new CommandAllocation(name?.Invoke(i) ?? ("command " + i.ToString(CultureInfo.InvariantCulture)), bytes[i], calls[i]));
        return ranked.OrderByDescending(command => command.Bytes).Take(Math.Max(0, count)).ToList();
    }

    private static (long[] Bytes, long[] Calls) SumCommands()
    {
        lock (Sheets)
        {
            int length = Sheets.Count == 0 ? 0 : Sheets.Max(sheet => sheet.Bytes.Length);
            long[] bytes = new long[length], calls = new long[length];
            foreach (Sheet sheet in Sheets)
            {
                long[] sheetBytes = sheet.Bytes, sheetCalls = sheet.Calls;
                for (int i = 0; i < sheetBytes.Length && i < sheetCalls.Length; i++)
                {
                    bytes[i] += sheetBytes[i];
                    calls[i] += sheetCalls[i];
                }
            }
            return (bytes, calls);
        }
    }

    private static Dictionary<string, (long Total, long InCommands, long Runs)> SumScopes()
    {
        var sums = new Dictionary<string, (long Total, long InCommands, long Runs)>(StringComparer.Ordinal);
        lock (Sheets)
        {
            foreach (Sheet sheet in Sheets)
                lock (sheet.Scopes)
                    foreach (KeyValuePair<string, Scope> pair in sheet.Scopes)
                    {
                        sums.TryGetValue(pair.Key, out var sum);
                        sums[pair.Key] = (sum.Total + pair.Value.Total, sum.InCommands + pair.Value.InCommands, sum.Runs + pair.Value.Runs);
                    }
        }
        return sums;
    }
}
