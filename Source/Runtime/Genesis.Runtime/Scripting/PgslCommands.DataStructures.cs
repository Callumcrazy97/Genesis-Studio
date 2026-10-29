using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Lists, maps, stacks, queues, grids and named arrays.
//
// All of them live in PgslContext.Variables under reserved "__ds_*" keys rather than in a static
// table. That is deliberate: Variables has exactly the right lifetime (per instance, per context),
// so a destroyed instance takes its data structures with it instead of leaking them for the rest of
// the session.
//
// Positions are 0-BASED here, matching ds_* in GameMaker — unlike the Strings family, which is
// 1-based for the same reason. Each follows its own heritage rather than inventing a third.
//
// Every allocation is capped. A script asking for DsGridCreate(1e9, 1e9) is a bug, and a bug in
// game script must not be able to exhaust the process.
public static partial class PgslCommands
{
    #region Data structures

    private const int MaxCollectionElements = 1_000_000;
    private const int MaxGridDimension = 4096;

    // ── Shared storage ──────────────────────────────────────────────────────────

    private static Dictionary<string, object> Store => GetContext()?.Variables;

    private static int NextHandle(string family)
    {
        Dictionary<string, object> store = Store;
        if (store is null) return 0;

        string key = "__ds_next_" + family;
        int next = store.TryGetValue(key, out object existing) && existing is int value
            ? checked(value + 1) : 1;
        store[key] = next;
        if (_dataOwnerChecks && !InJob) OwnedHandles.AddOrUpdate((family, next), 1, static (_, count) => count + 1);
        return next;
    }

    // ── Who owns a handle (Engine.Game.Scripts.DataOwnerChecks) ─────────────────
    //
    // A structure lives in the instance that made it and each instance numbers its own from 1, so a
    // handle used in another instance reads as empty (or is a different structure of that number),
    // with no word said. With the check on, the handle numbers made by every instance are counted, and
    // a read, write or free that finds nothing under a number some instance holds is an error saying so.
    // Off by default: a game that has always worked this way is not stopped by it, and nothing is counted.
    private static volatile bool _dataOwnerChecks;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Family, int Handle), int> OwnedHandles = new();

    /// <summary>Whether a handle used where it was not made is an error (false unless a game asks).</summary>
    public static bool DataOwnerChecks
    {
        get => _dataOwnerChecks;
        set { if (value && !_dataOwnerChecks) OwnedHandles.Clear(); _dataOwnerChecks = value; }
    }

    // A worker job has stores of its own (copies of what it was given): never checked.
    private static bool InJob => GetContext()?.ActiveVm is VM.PgslVm { JobBudget: not null };

    // Nothing under this handle here: with the check on, a number another instance holds is an error.
    private static void NotThisInstancesHandle(string family, double handle, string what)
    {
        if (!double.IsFinite(handle) || InJob || !OwnedHandles.TryGetValue((family, (int)handle), out int holders) || holders <= 0) return;
        throw new InvalidOperationException($"This instance has no {family} {(int)handle} to {what}, but another instance has a {family} numbered {(int)handle}: "
            + "data structures belong to the instance that made them, and a handle given to another instance names that instance's own. "
            + "Run the code in the owner (with (owner) { ... }) or share the data another way (GlobalSet values, a [ ] list, an Object that keeps it).");
    }

    // A structure to change (or one whose command may change it). One shared with a worker job is
    // made this side's own first (PgslCommands.SharedData.cs); commands that only read use ResolveRead.
    private static T Resolve<T>(string family, double handle) where T : class
    {
        Dictionary<string, object> store = Store;
        if (store is null) return null;
        string key = DsKey(family, (int)handle);
        if (!store.TryGetValue(key, out object existing))
        {
            if (_dataOwnerChecks) NotThisInstancesHandle(family, handle, "change");
            return null;
        }
        T result = existing is SharedData shared ? Unshare<T>(store, key, shared) : existing as T;
        // A grid lent in part to jobs not started yet: their copies are made before it changes.
        if (result is PgslGrid { Pending: not null } lent) SettleAll(lent);
        return result;
    }

    private static void Bind(string family, int handle, object value)
    {
        Dictionary<string, object> store = Store;
        if (store is not null) store[DsKey(family, handle)] = value;
    }

    private static void Unbind(string family, double handle)
    {
        bool had = Store?.Remove(DsKey(family, (int)handle)) ?? false;
        ForgetDsKey(family, (int)handle);
        if (!_dataOwnerChecks) return;
        if (had) { if (!InJob) OwnedHandles.AddOrUpdate((family, (int)handle), 0, static (_, count) => Math.Max(0, count - 1)); }
        else NotThisInstancesHandle(family, handle, "free");
    }

    // Every list or grid read looks its structure up by key; each handle's key is made once instead of
    // formatted (and allocated) on every call. Handles are never reused, so a game that keeps creating
    // structures (a worker job's per-run lists and grids) soon passes any fixed table: the table grows
    // with the handles (up to MaxCachedDsKeys), and a destroyed structure's key is let go. Worker
    // threads share it: a lost write only means a key is formatted again.
    private const int MaxCachedDsKeys = 1 << 21;
    private static readonly string[][] DsKeys = [new string[4096], new string[4096], new string[4096], new string[4096], new string[4096], new string[4096], new string[4096]];
    private static readonly object DsKeysGrow = new();

    private static int DsFamilyIndex(string family) => family switch
    {
        "list" => 0, "grid" => 1, "map" => 2, "stack" => 3, "queue" => 4, "priority" => 5, "nlist" => 6, _ => -1,
    };

    private static string DsKey(string family, int handle)
    {
        int f = DsFamilyIndex(family);
        if (f < 0 || handle < 0 || handle >= MaxCachedDsKeys) return $"__ds_{family}_{handle}";
        string[] keys = DsKeys[f];
        if (handle >= keys.Length)
        {
            lock (DsKeysGrow)
            {
                keys = DsKeys[f];
                if (handle >= keys.Length)
                {
                    int size = keys.Length;
                    while (size <= handle) size *= 2;
                    Array.Resize(ref keys, Math.Min(size, MaxCachedDsKeys));
                    DsKeys[f] = keys;
                }
            }
        }
        return keys[handle] ??= $"__ds_{family}_{handle}";
    }

    private static void ForgetDsKey(string family, int handle)
    {
        int f = DsFamilyIndex(family);
        if (f < 0 || handle < 0) return;
        string[] keys = DsKeys[f];
        if (handle < keys.Length) keys[handle] = null;
    }

    private static double AsNumber(object value) => value switch
    {
        CollectionReference reference => reference.Handle,
        double d => d,
        int i => i,
        float f => f,
        long l => l,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0,
        _ => 0,
    };

    private static string AsText(object value) => value switch
    {
        null => string.Empty,
        string s => s,
        double d => StringOf(d),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    // ── Lists ───────────────────────────────────────────────────────────────────

    [PgslCommand("DsListCreate", "DsListCreate() -> id", "Create a list; returns its handle", "Lists")]
    public static double DsListCreate()
    {
        int handle = NextHandle("list");
        if (handle == 0) return 0;
        Bind("list", handle, new List<object>());
        return handle;
    }

    [PgslCommand("DsListDestroy", "DsListDestroy(id)", "Release a list", "Lists")]
    public static void DsListDestroy(double id)
    {
        // A large list's own storage (not one a job still reads) is kept for a later DsListReserve.
        if (double.IsFinite(id) && Store is { } store && store.TryGetValue(DsKey("list", (int)id), out object held) && held is List<object> list)
            ListStorage.Return(list);
        Unbind("list", id);
    }

    [PgslCommand("DsListClear", "DsListClear(id)", "Remove every entry", "Lists")]
    public static void DsListClear(double id)
    {
        // Held by a running job: this side's own is a new empty list (as roomy as the old one, since
        // it is usually filled again), not a copy of entries that are about to go.
        if (ReplaceHeld("list", id, held => held is List<object> list ? new List<object>(list.Capacity) : null)) return;
        Resolve<List<object>>("list", id)?.Clear();
    }

    [PgslCommand("DsListReserve", "DsListReserve(id, count)",
        "Make room for this many entries (up to 1 000 000), so adding them grows nothing: a list a mesh is built from, filled again for every chunk", "Lists")]
    public static void DsListReserve(double id, double count)
    {
        if (!double.IsFinite(count) || count <= 0) return;
        List<object> list = Resolve<List<object>>("list", id);
        if (list is null) return;
        int wanted = (int)Math.Min(count, MaxCollectionElements);
        if (list.Capacity >= wanted) return;
        // An empty list takes the storage of one destroyed earlier when there is one big enough.
        if (list.Count == 0 && ListStorage.Take(wanted) is { } kept)
        {
            Bind("list", (int)id, kept);
            return;
        }
        list.Capacity = wanted;
    }

    [PgslCommand("DsListSize", "DsListSize(id) -> number", "Entry count", "Lists")]
    public static double DsListSize(double id) => ResolveRead<List<object>>("list", id)?.Count ?? 0;

    [PgslCommand("DsListEmpty", "DsListEmpty(id) -> bool", "True when the list has no entries", "Lists")]
    public static bool DsListEmpty(double id) => (ResolveRead<List<object>>("list", id)?.Count ?? 0) == 0;

    [PgslCommand("DsListAdd", "DsListAdd(id, value)", "Append a number", "Lists")]
    public static void DsListAdd(double id, double value) => ListAppend(id, BoxNumber(value));

    [PgslCommand("DsListAddString", "DsListAddString(id, value)", "Append a string", "Lists")]
    public static void DsListAddString(double id, string value) => ListAppend(id, value ?? string.Empty);

    private static void ListAppend(double id, object value)
    {
        List<object> list = Resolve<List<object>>("list", id);
        if (list is null || list.Count >= MaxCollectionElements) return;
        list.Add(value);
    }

    [PgslCommand("DsListAddNumbers", "DsListAddNumbers(id, text) -> added",
        "Append every number in a text (\"0.5, 1, -2.25\": commas, semicolons or spaces between them) in one call; returns how many were added. Generated data loads many times faster than with a DsListAdd for each number", "Lists")]
    public static double DsListAddNumbers(double id, string text)
    {
        List<object> list = Resolve<List<object>>("list", id);
        if (list is null) return 0;
        return ForEachNumber(text, "DsListAddNumbers", value =>
        {
            if (list.Count >= MaxCollectionElements) return false;
            list.Add(BoxNumber(value));
            return true;
        });
    }

    private static bool IsNumberSeparator(char c) => c is ',' or ';' || char.IsWhiteSpace(c);

    /// <summary>
    /// Gives <paramref name="add"/> each number in <paramref name="text"/>, read the same in every
    /// culture, until it returns false; returns how many it took. An entry that is not a number
    /// stops it there, and the log says which.
    /// </summary>
    private static int ForEachNumber(string text, string command, Func<double, bool> add)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        ReadOnlySpan<char> rest = text;
        int taken = 0;
        while (true)
        {
            int start = 0;
            while (start < rest.Length && IsNumberSeparator(rest[start])) start++;
            if (start == rest.Length) return taken;
            rest = rest[start..];
            int end = 0;
            while (end < rest.Length && !IsNumberSeparator(rest[end])) end++;
            if (!double.TryParse(rest[..end], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
            {
                Genesis.Rendering.Diagnostics.RenderLog.Line($"{command}: '{rest[..Math.Min(end, 40)]}' is not a number; the {taken} before it were added.");
                return taken;
            }
            if (!add(value)) return taken;
            taken++;
            rest = rest[end..];
        }
    }

    // Takes text as well as numbers: Ember games insert a string here, then overwrite it with
    // DsListSetString, and a number-only value stopped them with a script error.
    [PgslCommand("DsListInsert", "DsListInsert(id, position, value)", "Insert a number or a string at a 0-based position", "Lists")]
    public static void DsListInsert(double id, double position, object value)
    {
        List<object> list = Resolve<List<object>>("list", id);
        if (list is null || list.Count >= MaxCollectionElements) return;
        object entry = value is string text ? text : BoxNumber(Num(value));
        list.Insert((int)Math.Clamp(position, 0, list.Count), entry);
    }

    [PgslCommand("DsListDelete", "DsListDelete(id, position)", "Remove the entry at a 0-based position", "Lists")]
    public static void DsListDelete(double id, double position)
    {
        List<object> list = Resolve<List<object>>("list", id);
        int at = (int)position;
        if (list is null || at < 0 || at >= list.Count) return;
        list.RemoveAt(at);
    }

    [PgslCommand("DsListSet", "DsListSet(id, position, value)", "Overwrite a number at a 0-based position", "Lists")]
    public static void DsListSet(double id, double position, double value)
    {
        List<object> list = Resolve<List<object>>("list", id);
        int at = (int)position;
        if (list is null || at < 0 || at >= list.Count) return;
        list[at] = BoxNumber(value);
    }

    [PgslCommand("DsListSetString", "DsListSetString(id, position, value)", "Overwrite a string at a 0-based position", "Lists")]
    public static void DsListSetString(double id, double position, string value)
    {
        List<object> list = Resolve<List<object>>("list", id);
        int at = (int)position;
        if (list is null || at < 0 || at >= list.Count) return;
        list[at] = value ?? string.Empty;
    }

    [PgslCommand("DsListGet", "DsListGet(id, position) -> number", "Read as a number; 0 when out of range", "Lists")]
    public static double DsListGet(double id, double position)
    {
        List<object> list = ResolveRead<List<object>>("list", id);
        int at = (int)position;
        return list is not null && at >= 0 && at < list.Count ? AsNumber(list[at]) : 0;
    }

    [PgslCommand("DsListGetString", "DsListGetString(id, position) -> string", "Read as text; empty when out of range", "Lists")]
    public static string DsListGetString(double id, double position)
    {
        List<object> list = ResolveRead<List<object>>("list", id);
        int at = (int)position;
        return list is not null && at >= 0 && at < list.Count ? AsText(list[at]) : string.Empty;
    }

    [PgslCommand("DsListFind", "DsListFind(id, value) -> number", "0-based position of a number, -1 when absent", "Lists")]
    public static double DsListFind(double id, double value)
    {
        List<object> list = ResolveRead<List<object>>("list", id);
        if (list is null) return -1;
        for (int index = 0; index < list.Count; index++)
        {
            if (Math.Abs(AsNumber(list[index]) - value) < 1e-9) return index;
        }

        return -1;
    }

    [PgslCommand("DsListSort", "DsListSort(id, ascending)", "Sort numerically", "Lists")]
    public static void DsListSort(double id, bool ascending)
    {
        List<object> list = Resolve<List<object>>("list", id);
        if (list is null) return;
        list.Sort((a, b) => ascending
            ? AsNumber(a).CompareTo(AsNumber(b))
            : AsNumber(b).CompareTo(AsNumber(a)));
    }

    [PgslCommand("DsListShuffle", "DsListShuffle(id)", "Randomise the order", "Lists")]
    public static void DsListShuffle(double id)
    {
        List<object> list = Resolve<List<object>>("list", id);
        if (list is null) return;
        for (int index = list.Count - 1; index > 0; index--)
        {
            int swap = NextRandomInt(index + 1);
            (list[index], list[swap]) = (list[swap], list[index]);
        }
    }

    [PgslCommand("DsListSum", "DsListSum(id) -> number", "Sum of the numeric entries", "Lists")]
    public static double DsListSum(double id) =>
        ResolveRead<List<object>>("list", id)?.Sum(AsNumber) ?? 0;

    // ── Maps ────────────────────────────────────────────────────────────────────
    // A parallel key list preserves insertion order, so DsMapKeyAt is deterministic. Relying on
    // Dictionary enumeration order would make script behaviour depend on hash internals.

    private sealed class PgslMap
    {
        public Dictionary<string, object> Values { get; }
        public List<string> Order { get; }

        public PgslMap()
        {
            Values = new(StringComparer.Ordinal);
            Order = [];
        }

        // A copy in one allocation each (the copy a job's shared map costs when one side changes it),
        // rather than the entries added one by one and the table regrown as it filled.
        private PgslMap(PgslMap source)
        {
            Values = new Dictionary<string, object>(source.Values, StringComparer.Ordinal);
            Order = new List<string>(source.Order);
        }

        public PgslMap Copy() => new(this);
    }

    [PgslCommand("DsMapCreate", "DsMapCreate() -> id", "Create a key/value map", "Maps")]
    public static double DsMapCreate()
    {
        int handle = NextHandle("map");
        if (handle == 0) return 0;
        Bind("map", handle, new PgslMap());
        return handle;
    }

    [PgslCommand("DsMapDestroy", "DsMapDestroy(id)", "Release a map", "Maps")]
    public static void DsMapDestroy(double id) => Unbind("map", id);

    [PgslCommand("DsMapClear", "DsMapClear(id)", "Remove every entry", "Maps")]
    public static void DsMapClear(double id)
    {
        if (ReplaceHeld("map", id, held => held is PgslMap ? new PgslMap() : null)) return;
        PgslMap map = Resolve<PgslMap>("map", id);
        if (map is null) return;
        map.Values.Clear();
        map.Order.Clear();
    }

    [PgslCommand("DsMapSize", "DsMapSize(id) -> number", "Entry count", "Maps")]
    public static double DsMapSize(double id) => ResolveRead<PgslMap>("map", id)?.Values.Count ?? 0;

    [PgslCommand("DsMapEmpty", "DsMapEmpty(id) -> bool", "True when the map has no entries", "Maps")]
    public static bool DsMapEmpty(double id) => (ResolveRead<PgslMap>("map", id)?.Values.Count ?? 0) == 0;

    [PgslCommand("DsMapExists", "DsMapExists(id, key) -> bool", "True when the key is present", "Maps")]
    public static bool DsMapExists(double id, string key) =>
        key is not null && (ResolveRead<PgslMap>("map", id)?.Values.ContainsKey(key) ?? false);

    [PgslCommand("DsMapSet", "DsMapSet(id, key, value)", "Store a number under a key", "Maps")]
    public static void DsMapSet(double id, string key, double value) => MapStore(id, key, BoxNumber(value));

    [PgslCommand("DsMapSetString", "DsMapSetString(id, key, value)", "Store a string under a key", "Maps")]
    public static void DsMapSetString(double id, string key, string value) => MapStore(id, key, value ?? string.Empty);

    private static void MapStore(double id, string key, object value)
    {
        PgslMap map = Resolve<PgslMap>("map", id);
        if (map is null || key is null) return;
        if (!map.Values.ContainsKey(key))
        {
            if (map.Values.Count >= MaxCollectionElements) return;
            map.Order.Add(key);
        }

        map.Values[key] = value;
    }

    [PgslCommand("DsMapGet", "DsMapGet(id, key) -> number", "Read as a number; 0 when absent", "Maps")]
    public static double DsMapGet(double id, string key)
    {
        PgslMap map = ResolveRead<PgslMap>("map", id);
        return map is not null && key is not null && map.Values.TryGetValue(key, out object value) ? AsNumber(value) : 0;
    }

    [PgslCommand("DsMapGetString", "DsMapGetString(id, key) -> string", "Read as text; empty when absent", "Maps")]
    public static string DsMapGetString(double id, string key)
    {
        PgslMap map = ResolveRead<PgslMap>("map", id);
        return map is not null && key is not null && map.Values.TryGetValue(key, out object value)
            ? AsText(value)
            : string.Empty;
    }

    [PgslCommand("DsMapDelete", "DsMapDelete(id, key)", "Remove one key", "Maps")]
    public static void DsMapDelete(double id, string key)
    {
        PgslMap map = Resolve<PgslMap>("map", id);
        if (map is null || key is null) return;
        if (map.Values.Remove(key)) map.Order.Remove(key);
    }

    [PgslCommand("DsMapKeyAt", "DsMapKeyAt(id, index) -> string", "Key at a 0-based insertion-order index", "Maps")]
    public static string DsMapKeyAt(double id, double index)
    {
        PgslMap map = ResolveRead<PgslMap>("map", id);
        int at = (int)index;
        return map is not null && at >= 0 && at < map.Order.Count ? map.Order[at] : string.Empty;
    }

    // ── Stacks ──────────────────────────────────────────────────────────────────

    [PgslCommand("DsStackCreate", "DsStackCreate() -> id", "Create a stack", "Data Structures")]
    public static double DsStackCreate()
    {
        int handle = NextHandle("stack");
        if (handle == 0) return 0;
        Bind("stack", handle, new List<object>());
        return handle;
    }

    [PgslCommand("DsStackDestroy", "DsStackDestroy(id)", "Release a stack", "Data Structures")]
    public static void DsStackDestroy(double id) => Unbind("stack", id);

    [PgslCommand("DsStackPush", "DsStackPush(id, value)", "Push a number", "Data Structures")]
    public static void DsStackPush(double id, double value)
    {
        List<object> stack = Resolve<List<object>>("stack", id);
        if (stack is null || stack.Count >= MaxCollectionElements) return;
        stack.Add(BoxNumber(value));
    }

    [PgslCommand("DsStackPop", "DsStackPop(id) -> number", "Pop the top value; 0 when empty", "Data Structures")]
    public static double DsStackPop(double id)
    {
        List<object> stack = Resolve<List<object>>("stack", id);
        if (stack is null || stack.Count == 0) return 0;
        double value = AsNumber(stack[^1]);
        stack.RemoveAt(stack.Count - 1);
        return value;
    }

    [PgslCommand("DsStackTop", "DsStackTop(id) -> number", "Peek the top value; 0 when empty", "Data Structures")]
    public static double DsStackTop(double id)
    {
        List<object> stack = ResolveRead<List<object>>("stack", id);
        return stack is null || stack.Count == 0 ? 0 : AsNumber(stack[^1]);
    }

    [PgslCommand("DsStackSize", "DsStackSize(id) -> number", "Entry count", "Data Structures")]
    public static double DsStackSize(double id) => ResolveRead<List<object>>("stack", id)?.Count ?? 0;

    // ── Queues ──────────────────────────────────────────────────────────────────

    [PgslCommand("DsQueueCreate", "DsQueueCreate() -> id", "Create a queue", "Data Structures")]
    public static double DsQueueCreate()
    {
        int handle = NextHandle("queue");
        if (handle == 0) return 0;
        Bind("queue", handle, new List<object>());
        return handle;
    }

    [PgslCommand("DsQueueDestroy", "DsQueueDestroy(id)", "Release a queue", "Data Structures")]
    public static void DsQueueDestroy(double id) => Unbind("queue", id);

    [PgslCommand("DsQueueEnqueue", "DsQueueEnqueue(id, value)", "Add to the tail", "Data Structures")]
    public static void DsQueueEnqueue(double id, double value)
    {
        List<object> queue = Resolve<List<object>>("queue", id);
        if (queue is null || queue.Count >= MaxCollectionElements) return;
        queue.Add(BoxNumber(value));
    }

    [PgslCommand("DsQueueDequeue", "DsQueueDequeue(id) -> number", "Take from the head; 0 when empty", "Data Structures")]
    public static double DsQueueDequeue(double id)
    {
        List<object> queue = Resolve<List<object>>("queue", id);
        if (queue is null || queue.Count == 0) return 0;
        double value = AsNumber(queue[0]);
        queue.RemoveAt(0);
        return value;
    }

    [PgslCommand("DsQueueHead", "DsQueueHead(id) -> number", "Peek the head; 0 when empty", "Data Structures")]
    public static double DsQueueHead(double id)
    {
        List<object> queue = ResolveRead<List<object>>("queue", id);
        return queue is null || queue.Count == 0 ? 0 : AsNumber(queue[0]);
    }

    [PgslCommand("DsQueueSize", "DsQueueSize(id) -> number", "Entry count", "Data Structures")]
    public static double DsQueueSize(double id) => ResolveRead<List<object>>("queue", id)?.Count ?? 0;

    // ── Grids ───────────────────────────────────────────────────────────────────
    // The grid itself, and the compact kinds, are in PgslCommands.GridCells.cs.

    [PgslCommand("DsGridCreate", "DsGridCreate(width, height, kind?) -> id",
        "Create a 2D grid of numbers; kind \"u8\", \"u16\", \"i32\" or \"f32\" makes a compact grid of small whole numbers (1, 2 or 4 bytes a cell, values clamped to the kind) or single-precision numbers. Up to 4096 a side and 8 MB of cells; 0 when too big or the kind is unknown", "Grids")]
    public static double DsGridCreate(double width, double height, string kind = null)
    {
        int w = (int)Math.Clamp(width, 0, MaxGridDimension);
        int h = (int)Math.Clamp(height, 0, MaxGridDimension);
        if (!TryGridKind(kind, out GridKind cells) || !GridFits(cells, w, h)) return 0;

        int handle = NextHandle("grid");
        if (handle == 0) return 0;
        Bind("grid", handle, PgslGrid.Create(cells, w, h));
        return handle;
    }

    [PgslCommand("DsGridDestroy", "DsGridDestroy(id)", "Release a grid", "Grids")]
    public static void DsGridDestroy(double id) => Unbind("grid", id);

    [PgslCommand("DsGridWidth", "DsGridWidth(id) -> number", "Columns", "Grids")]
    public static double DsGridWidth(double id) => ResolveRead<PgslGrid>("grid", id)?.Width ?? 0;

    [PgslCommand("DsGridHeight", "DsGridHeight(id) -> number", "Rows", "Grids")]
    public static double DsGridHeight(double id) => ResolveRead<PgslGrid>("grid", id)?.Height ?? 0;

    [PgslCommand("DsGridClear", "DsGridClear(id, value)", "Fill every cell", "Grids")]
    public static void DsGridClear(double id, double value)
    {
        Resolve<PgslGrid>("grid", id)?.Fill(value);
    }

    [PgslCommand("DsGridSet", "DsGridSet(id, x, y, value)", "Write one cell", "Grids")]
    public static void DsGridSet(double id, double x, double y, double value) =>
        Resolve<PgslGrid>("grid", id)?.Set((int)x, (int)y, value);

    [PgslCommand("DsGridGet", "DsGridGet(id, x, y) -> number", "Read one cell; 0 when out of range", "Grids")]
    public static double DsGridGet(double id, double x, double y) =>
        ResolveRead<PgslGrid>("grid", id)?.Get((int)x, (int)y) ?? 0;

    [PgslCommand("DsGridAdd", "DsGridAdd(id, x, y, value)", "Add to one cell", "Grids")]
    public static void DsGridAdd(double id, double x, double y, double value)
    {
        PgslGrid grid = Resolve<PgslGrid>("grid", id);
        if (grid is null) return;
        grid.Set((int)x, (int)y, grid.Get((int)x, (int)y) + value);
    }

    [PgslCommand("DsGridMultiply", "DsGridMultiply(id, x, y, value)", "Multiply one cell", "Grids")]
    public static void DsGridMultiply(double id, double x, double y, double value)
    {
        PgslGrid grid = Resolve<PgslGrid>("grid", id);
        if (grid is null) return;
        grid.Set((int)x, (int)y, grid.Get((int)x, (int)y) * value);
    }

    [PgslCommand("DsGridSetRegion", "DsGridSetRegion(id, x1, y1, x2, y2, value)", "Fill a rectangular region", "Grids")]
    public static void DsGridSetRegion(double id, double x1, double y1, double x2, double y2, double value)
    {
        PgslGrid grid = Resolve<PgslGrid>("grid", id);
        if (grid is null) return;
        (int left, int top, int right, int bottom) = NormaliseRegion(x1, y1, x2, y2);
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++) grid.Set(x, y, value);
        }
    }

    [PgslCommand("DsGridGetSum", "DsGridGetSum(id, x1, y1, x2, y2) -> number", "Sum over a region", "Grids")]
    public static double DsGridGetSum(double id, double x1, double y1, double x2, double y2) =>
        RegionAggregate(id, x1, y1, x2, y2, static (accumulator, value) => accumulator + value, 0d);

    [PgslCommand("DsGridGetMax", "DsGridGetMax(id, x1, y1, x2, y2) -> number", "Largest value in a region", "Grids")]
    public static double DsGridGetMax(double id, double x1, double y1, double x2, double y2) =>
        RegionAggregate(id, x1, y1, x2, y2, Math.Max, double.NegativeInfinity);

    [PgslCommand("DsGridGetMin", "DsGridGetMin(id, x1, y1, x2, y2) -> number", "Smallest value in a region", "Grids")]
    public static double DsGridGetMin(double id, double x1, double y1, double x2, double y2) =>
        RegionAggregate(id, x1, y1, x2, y2, Math.Min, double.PositiveInfinity);

    [PgslCommand("DsGridValueExists", "DsGridValueExists(id, x1, y1, x2, y2, value) -> bool", "True when a region holds the value", "Grids")]
    public static bool DsGridValueExists(double id, double x1, double y1, double x2, double y2, double value)
    {
        PgslGrid grid = ResolveRead<PgslGrid>("grid", id);
        if (grid is null) return false;
        (int left, int top, int right, int bottom) = NormaliseRegion(x1, y1, x2, y2);
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                if (grid.Inside(x, y) && Math.Abs(grid.Get(x, y) - value) < 1e-9) return true;
            }
        }

        return false;
    }

    [PgslCommand("DsGridResize", "DsGridResize(id, width, height)", "Resize, preserving overlapping cells", "Grids")]
    public static void DsGridResize(double id, double width, double height)
    {
        PgslGrid grid = Resolve<PgslGrid>("grid", id);
        if (grid is null) return;

        int w = (int)Math.Clamp(width, 0, MaxGridDimension);
        int h = (int)Math.Clamp(height, 0, MaxGridDimension);
        if (!GridFits(grid.Kind, w, h)) return;
        grid.Resize(w, h);
    }

    [PgslCommand("DsGridCopy", "DsGridCopy(destinationId, sourceId)",
        "Make one grid an exact copy of another: its size, its kind (DsGridCreate) and its cells", "Grids")]
    public static void DsGridCopy(double destinationId, double sourceId)
    {
        PgslGrid destination = Resolve<PgslGrid>("grid", destinationId);
        PgslGrid source = ResolveRead<PgslGrid>("grid", sourceId);
        if (destination is null || source is null) return;
        destination.CopyFrom(source);
    }

    private static (int Left, int Top, int Right, int Bottom) NormaliseRegion(double x1, double y1, double x2, double y2) =>
        ((int)Math.Min(x1, x2), (int)Math.Min(y1, y2), (int)Math.Max(x1, x2), (int)Math.Max(y1, y2));

    private static double RegionAggregate(
        double id, double x1, double y1, double x2, double y2, Func<double, double, double> combine, double seed)
    {
        PgslGrid grid = ResolveRead<PgslGrid>("grid", id);
        if (grid is null) return 0;

        (int left, int top, int right, int bottom) = NormaliseRegion(x1, y1, x2, y2);
        double accumulator = seed;
        bool any = false;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                if (!grid.Inside(x, y)) continue;
                accumulator = combine(accumulator, grid.Get(x, y));
                any = true;
            }
        }

        return any ? accumulator : 0;
    }

    // ── Named arrays ────────────────────────────────────────────────────────────
    // Addressed by NAME rather than by handle, which is how the legacy catalogue used them.
    // Writing past the end grows the array with zeros, so ArraySet never fails — the useful
    // behaviour for a scripting language.

    private static List<object> ArrayFor(string name, bool create)
    {
        Dictionary<string, object> store = Store;
        if (store is null || string.IsNullOrEmpty(name)) return null;

        string key = "__arr_" + name;
        if (store.TryGetValue(key, out object existing) && existing is List<object> list) return list;
        if (!create) return null;

        List<object> created = [];
        store[key] = created;
        return created;
    }

    [PgslCommand("ArraySet", "ArraySet(name, index, value)", "Write a number, growing the array as needed", "Arrays")]
    public static void ArraySet(string name, double index, double value) => ArrayWrite(name, index, BoxNumber(value));

    [PgslCommand("ArraySetString", "ArraySetString(name, index, value)", "Write a string, growing the array as needed", "Arrays")]
    public static void ArraySetString(string name, double index, string value) => ArrayWrite(name, index, value ?? string.Empty);

    private static void ArrayWrite(string name, double index, object value)
    {
        List<object> array = ArrayFor(name, create: true);
        int at = (int)index;
        if (array is null || at < 0 || at >= MaxCollectionElements) return;
        // Slots below the written one were never set: they read as 0 and as empty text.
        while (array.Count <= at) array.Add(null);
        array[at] = value;
    }

    [PgslCommand("ArrayGet", "ArrayGet(name, index) -> number", "Read as a number; 0 when unset", "Arrays")]
    public static double ArrayGet(string name, double index)
    {
        List<object> array = ArrayFor(name, create: false);
        int at = (int)index;
        return array is not null && at >= 0 && at < array.Count ? AsNumber(array[at]) : 0;
    }

    [PgslCommand("ArrayGetString", "ArrayGetString(name, index) -> string", "Read as text; empty when unset", "Arrays")]
    public static string ArrayGetString(string name, double index)
    {
        List<object> array = ArrayFor(name, create: false);
        int at = (int)index;
        return array is not null && at >= 0 && at < array.Count ? AsText(array[at]) : string.Empty;
    }

    [PgslCommand("ArrayLength", "ArrayLength(name) -> number", "Entry count", "Arrays")]
    public static double ArrayLength(string name) => ArrayFor(name, create: false)?.Count ?? 0;

    [PgslCommand("ArrayClear", "ArrayClear(name)", "Remove every entry", "Arrays")]
    public static void ArrayClear(string name) => ArrayFor(name, create: false)?.Clear();

    [PgslCommand("ArrayPush", "ArrayPush(name, value)", "Append a number", "Arrays")]
    public static void ArrayPush(string name, double value)
    {
        List<object> array = ArrayFor(name, create: true);
        if (array is null || array.Count >= MaxCollectionElements) return;
        array.Add(BoxNumber(value));
    }

    [PgslCommand("ArrayPop", "ArrayPop(name) -> number", "Remove and return the last entry; 0 when empty", "Arrays")]
    public static double ArrayPop(string name)
    {
        List<object> array = ArrayFor(name, create: false);
        if (array is null || array.Count == 0) return 0;
        double value = AsNumber(array[^1]);
        array.RemoveAt(array.Count - 1);
        return value;
    }

    [PgslCommand("ArrayIndexOf", "ArrayIndexOf(name, value) -> number", "0-based position of a value, -1 when absent", "Arrays")]
    public static double ArrayIndexOf(string name, double value)
    {
        List<object> array = ArrayFor(name, create: false);
        if (array is null) return -1;
        for (int index = 0; index < array.Count; index++)
        {
            if (Math.Abs(AsNumber(array[index]) - value) < 1e-9) return index;
        }

        return -1;
    }

    [PgslCommand("ArrayContains", "ArrayContains(name, value) -> bool", "True when the array holds the value", "Arrays")]
    public static bool ArrayContains(string name, double value) => ArrayIndexOf(name, value) >= 0;

    [PgslCommand("ArraySort", "ArraySort(name, ascending)", "Sort numerically", "Arrays")]
    public static void ArraySort(string name, bool ascending)
    {
        List<object> array = ArrayFor(name, create: false);
        if (array is null) return;
        array.Sort((a, b) => ascending
            ? AsNumber(a).CompareTo(AsNumber(b))
            : AsNumber(b).CompareTo(AsNumber(a)));
    }

    [PgslCommand("ArrayReverse", "ArrayReverse(name)", "Reverse the order", "Arrays")]
    public static void ArrayReverse(string name) => ArrayFor(name, create: false)?.Reverse();

    [PgslCommand("ArraySum", "ArraySum(name) -> number", "Sum of the entries", "Arrays")]
    public static double ArraySum(string name) => ArrayFor(name, create: false)?.Sum(AsNumber) ?? 0;

    [PgslCommand("ArrayMean", "ArrayMean(name) -> number", "Average of the entries; 0 when empty", "Arrays")]
    public static double ArrayMean(string name)
    {
        List<object> array = ArrayFor(name, create: false);
        return array is null || array.Count == 0 ? 0 : array.Sum(AsNumber) / array.Count;
    }

    [PgslCommand("ArrayMinValue", "ArrayMinValue(name) -> number", "Smallest entry; 0 when empty", "Arrays")]
    public static double ArrayMinValue(string name)
    {
        List<object> array = ArrayFor(name, create: false);
        return array is null || array.Count == 0 ? 0 : array.Min(AsNumber);
    }

    [PgslCommand("ArrayMaxValue", "ArrayMaxValue(name) -> number", "Largest entry; 0 when empty", "Arrays")]
    public static double ArrayMaxValue(string name)
    {
        List<object> array = ArrayFor(name, create: false);
        return array is null || array.Count == 0 ? 0 : array.Max(AsNumber);
    }

    #endregion
}
