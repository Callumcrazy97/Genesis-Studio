#nullable disable
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>
/// Where scripts spend their time, by function: calls, time (including the functions it calls) and
/// how many calls ran compiled (<see cref="PgslCompiledTier"/>); a function called from a compiled
/// function runs inside it and counts in its caller's time. Off unless asked for
/// (GENESIS_SCRIPT_PROFILE=1, or ScriptProfileEnable); with the variable set, the report is written
/// to Debug/Logs/script_profile.txt when the game closes.
/// </summary>
internal static class PgslScriptProfile
{
    internal sealed class Stat
    {
        public long Calls, Ticks, CompiledCalls;
    }

    private static readonly ConcurrentDictionary<string, Stat> Stats = new(StringComparer.Ordinal);
    private static volatile bool _enabled = InitialState();

    internal static bool Enabled { get => _enabled; set => _enabled = value; }

    private static bool InitialState()
    {
        bool on = Environment.GetEnvironmentVariable("GENESIS_SCRIPT_PROFILE") == "1";
        if (on) AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteReport();
        return on;
    }

    public static void Record(string function, long ticks, bool compiled)
    {
        Stat stat = Stats.GetOrAdd(function ?? "?", _ => new Stat());
        Interlocked.Increment(ref stat.Calls);
        Interlocked.Add(ref stat.Ticks, ticks);
        if (compiled) Interlocked.Increment(ref stat.CompiledCalls);
    }

    public static void Reset() => Stats.Clear();

    internal readonly record struct FunctionTime(string Function, long Calls, double TotalMilliseconds, double AverageMicroseconds, long CompiledCalls);

    /// <summary>The functions that took longest, most first (the profile report's table).</summary>
    internal static FunctionTime[] Top(int top) =>
    [
        .. Stats.OrderByDescending(pair => pair.Value.Ticks).Take(Math.Max(1, top)).Select(pair =>
        {
            double total = pair.Value.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return new FunctionTime(pair.Key, pair.Value.Calls, total, total * 1000.0 / Math.Max(1, pair.Value.Calls), pair.Value.CompiledCalls);
        }),
    ];

    /// <summary>The functions that took longest, most first: one line each.</summary>
    public static string Report(int top)
    {
        var text = new StringBuilder();
        text.AppendLine("function | calls | total ms | average us | compiled calls");
        foreach ((string name, Stat stat) in Stats.OrderByDescending(pair => pair.Value.Ticks).Take(Math.Max(1, top)).Select(pair => (pair.Key, pair.Value)))
        {
            double total = stat.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            text.Append(name).Append(" | ").Append(stat.Calls.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(total.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" | ").Append((total * 1000.0 / Math.Max(1, stat.Calls)).ToString("F2", CultureInfo.InvariantCulture))
                .Append(" | ").Append(stat.CompiledCalls.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }
        return text.ToString();
    }

    private static void WriteReport()
    {
        try
        {
            string project = PgslCommands.ProjectPath;
            if (string.IsNullOrWhiteSpace(project)) return;
            string folder = System.IO.Path.Combine(project, "Debug", "Logs");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "script_profile.txt"), Report(200));
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { }
    }
}
