using System.Collections.Generic;
using System.Threading;

namespace Genesis.Runtime.Scripting;

// Grids, lists, maps, stacks and queues shared with worker jobs without copying them.
//
// When a job is given a structure (JobScriptGrid / JobScriptList / JobScriptMap / JobScriptShareAll),
// the Object's store and the job's store both hold the same SharedData, which holds the structure
// itself. From then on nobody writes that structure while any job holds it: either side may read it
// as it is, and the side that changes it first (the game, or a job) takes a copy of its own and keeps
// that copy from then on. So a job sees the data exactly as it was when it started, the game's
// changes meanwhile never reach it, and a job's changes reach the game only through JobTake. With no
// job holding it any more, the game's next change takes the structure back without a copy.
//
// Every write goes through Resolve (which takes the copy); only commands that read and never change a
// structure use ResolveRead. A command that writes must never use ResolveRead: it would change the
// structure a job is reading.
public static partial class PgslCommands
{
    /// <summary>A data structure shared between an Object and the worker jobs it started (see above).</summary>
    internal sealed class SharedData(object value)
    {
        /// <summary>The structure; never written while <see cref="Holders"/> is above 0.</summary>
        public readonly object Value = value;

        /// <summary>Jobs holding it: raised on the game's thread when a job starts, lowered when the job's work has ended.</summary>
        public int Holders;
    }

    /// <summary>A structure to read only: a shared one is read where it is, never copied.</summary>
    private static T ResolveRead<T>(string family, double handle) where T : class
    {
        Dictionary<string, object> store = Store;
        if (store is null) return null;
        string key = DsKey(family, (int)handle);
        if (!store.TryGetValue(key, out object existing)) return null;
        if (existing is not SharedData shared) return existing as T;
        // Held by no job any more: the Object has it back as it was (reads stay quick from now on).
        if (Volatile.Read(ref shared.Holders) == 0) store[key] = shared.Value;
        return shared.Value as T;
    }

    /// <summary>
    /// The structure behind a shared entry, made this side's own so it may be changed: a copy while
    /// any job holds it, the structure itself once none does. The store holds the result from now on.
    /// </summary>
    private static T Unshare<T>(Dictionary<string, object> store, string key, SharedData shared) where T : class
    {
        if (shared.Value is not T) return null;
        object own = shared.Value;
        if (Volatile.Read(ref shared.Holders) > 0)
        {
            own = CopyStructure(shared.Value);
            Interlocked.Increment(ref _sharedCopies);
            if (PgslAllocationLedger.Enabled)
                PgslAllocationLedger.AddCopy(StructureFamily(own), StructureBytes(own), byJob: GetContext()?.ActiveVm is VM.PgslVm { JobBudget: not null });
        }
        store[key] = own;
        return (T)own;
    }

    /// <summary>
    /// For a command that empties a structure: while a job holds it, this side's own becomes
    /// <paramref name="fresh"/>'s new structure instead of a copy of the old (whose entries the command
    /// would throw away). False when no job holds it, or it is not of that kind: the command goes on as usual.
    /// </summary>
    private static bool ReplaceHeld(string family, double handle, System.Func<object, object> fresh)
    {
        Dictionary<string, object> store = Store;
        if (store is null) return false;
        string key = DsKey(family, (int)handle);
        if (!store.TryGetValue(key, out object existing) || existing is not SharedData shared || Volatile.Read(ref shared.Holders) == 0)
            return false;
        object replacement = fresh(shared.Value);
        if (replacement is null) return false;
        store[key] = replacement;
        return true;
    }

    private static long _sharedCopies;

    /// <summary>Copies made so far because one side changed a structure a job still held (for tests and speed reports).</summary>
    internal static long SharedCopies => Interlocked.Read(ref _sharedCopies);

    /// <summary>How many jobs hold a structure of the bound Object; 0 when none does (or it is not shared).</summary>
    internal static int SharedHolders(string family, double handle) =>
        Store is { } store && store.TryGetValue(DsKey(family, (int)handle), out object value) && value is SharedData shared
            ? Volatile.Read(ref shared.Holders) : 0;

    private static string StructureFamily(object value) => value switch
    {
        PgslGrid => "grids",
        List<object> => "lists",
        PgslMap => "maps",
        PgslPriority => "priority queues",
        _ => "other",
    };

    // About what a copy takes: the cells, the list's slots, a map's entries.
    private static long StructureBytes(object value) => value switch
    {
        PgslGrid grid => (long)grid.Width * grid.Height * CellBytes(grid.Kind),
        List<object> list => (long)list.Capacity * 8,
        PgslMap map => (long)map.Values.Count * 48,
        _ => 0,
    };

    /// <summary>A copy of a grid, list (stack, queue) or map that shares nothing it can change with the original.</summary>
    private static object CopyStructure(object value) => value switch
    {
        PgslGrid grid => grid.Clone(),
        // Entries are numbers, text, true/false or collection references, none of which change.
        List<object> list => new List<object>(list),
        PgslMap map => map.Copy(),
        PgslPriority priority => priority.Clone(),
        _ => value,
    };

    /// <summary>
    /// Shares one entry of the Object's store with a job: the entry becomes (or already is) a
    /// SharedData, held once more. Null when the entry is not a grid, list, stack, queue or map.
    /// </summary>
    private static SharedData ShareEntry(Dictionary<string, object> store, string key, object value)
    {
        if (value is not SharedData shared)
        {
            if (value is not (PgslGrid or List<object> or PgslMap or PgslPriority)) return null;
            shared = new SharedData(value);
            store[key] = shared;
        }
        Interlocked.Increment(ref shared.Holders);
        return shared;
    }

    /// <summary>Lets go of a job's hold on what it was given; called once, when its work has ended.</summary>
    private static void ReleaseShared(List<SharedData> held)
    {
        if (held is null) return;
        foreach (SharedData shared in held) Interlocked.Decrement(ref shared.Holders);
    }

    // The store's keys listed by JobScriptStart before sharing them, kept for the next job.
    [System.ThreadStatic] private static List<KeyValuePair<string, object>> _shareAllScratch;
    private static List<KeyValuePair<string, object>> ShareAllScratch => _shareAllScratch ??= [];

    /// <summary>A store key of a grid, list, map, stack or queue (not a family's next-handle counter).</summary>
    private static bool IsStructureKey(string key) =>
        key.StartsWith("__ds_", System.StringComparison.Ordinal) && !key.StartsWith("__ds_next_", System.StringComparison.Ordinal);
}
