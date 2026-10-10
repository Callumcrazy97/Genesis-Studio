using System;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Priority queues (ds_priority in GameMaker): the open set of a script's A* search, events by time,
// jobs by urgency. A binary heap of (priority, value) entries held in one array of structs, so adding
// allocates nothing once the array has grown to the queue's largest size. Values are numbers or
// text, as in a list. The smallest priority comes out first; among equal priorities the entry added
// first comes out first (FIFO), so an A* search with ties explores in the order it found the nodes.
// A priority that is not a number counts as the largest of all (it comes out last).
//
// Lives in the Object's store like a list (family "priority"), so worker jobs can be given one
// (JobScriptPriority, JobScriptShareAll) and copy-on-write applies: commands that change a queue use
// Resolve, commands that only read it use ResolveRead.
public static partial class PgslCommands
{
    #region Data structures: priority queues

    private sealed class PgslPriority
    {
        private struct Entry
        {
            public double Priority;
            public long Order;
            public double Number;
            public string Text;
        }

        private Entry[] _heap = new Entry[8];
        private long _added;

        public int Count { get; private set; }

        public PgslPriority Clone()
        {
            var copy = new PgslPriority { _heap = (Entry[])_heap.Clone(), _added = _added, Count = Count };
            return copy;
        }

        public void Clear()
        {
            Array.Clear(_heap, 0, Count);
            Count = 0;
            _added = 0;
        }

        public bool Add(double priority, double number, string text)
        {
            if (Count >= MaxCollectionElements) return false;
            if (Count == _heap.Length) Array.Resize(ref _heap, Math.Min(_heap.Length * 2, MaxCollectionElements));
            var entry = new Entry { Priority = double.IsNaN(priority) ? double.PositiveInfinity : priority, Order = _added++, Number = number, Text = text };
            // Sift up: the new entry rises past every parent that comes out after it.
            int at = Count++;
            while (at > 0)
            {
                int parent = (at - 1) >> 1;
                if (!Before(entry, _heap[parent])) break;
                _heap[at] = _heap[parent];
                at = parent;
            }
            _heap[at] = entry;
            return true;
        }

        /// <summary>The entry that comes out next; false when the queue is empty.</summary>
        public bool PeekMin(out double priority, out double number, out string text)
        {
            if (Count == 0) { priority = 0; number = 0; text = null; return false; }
            priority = _heap[0].Priority; number = _heap[0].Number; text = _heap[0].Text;
            return true;
        }

        public bool TakeMin(out double number, out string text)
        {
            if (!PeekMin(out _, out number, out text)) return false;
            Entry last = _heap[--Count];
            _heap[Count] = default;
            if (Count == 0) return true;
            // Sift down: the last entry sinks from the top past every child that comes out before it.
            int at = 0;
            while (true)
            {
                int child = (at << 1) + 1;
                if (child >= Count) break;
                if (child + 1 < Count && Before(_heap[child + 1], _heap[child])) child++;
                if (!Before(_heap[child], last)) break;
                _heap[at] = _heap[child];
                at = child;
            }
            _heap[at] = last;
            return true;
        }

        private static bool Before(in Entry a, in Entry b) =>
            a.Priority < b.Priority || (a.Priority == b.Priority && a.Order < b.Order);
    }

    private static double PriorityNumber(double number, string text) =>
        text is null ? number : AsNumber(text);

    private static string PriorityText(double number, string text) => text ?? StringOf(number);

    [PgslCommand("DsPriorityCreate", "DsPriorityCreate() -> id",
        "Create a priority queue: entries come out smallest priority first, equal priorities in the order added", "Data Structures")]
    public static double DsPriorityCreate()
    {
        int handle = NextHandle("priority");
        if (handle == 0) return 0;
        Bind("priority", handle, new PgslPriority());
        return handle;
    }

    [PgslCommand("DsPriorityDestroy", "DsPriorityDestroy(id)", "Release a priority queue", "Data Structures")]
    public static void DsPriorityDestroy(double id) => Unbind("priority", id);

    [PgslCommand("DsPriorityClear", "DsPriorityClear(id)", "Remove every entry", "Data Structures")]
    public static void DsPriorityClear(double id) => Resolve<PgslPriority>("priority", id)?.Clear();

    [PgslCommand("DsPrioritySize", "DsPrioritySize(id) -> number", "Entry count", "Data Structures")]
    public static double DsPrioritySize(double id) => ResolveRead<PgslPriority>("priority", id)?.Count ?? 0;

    [PgslCommand("DsPriorityEmpty", "DsPriorityEmpty(id) -> bool", "True when the queue has no entries", "Data Structures")]
    public static bool DsPriorityEmpty(double id) => (ResolveRead<PgslPriority>("priority", id)?.Count ?? 0) == 0;

    [PgslCommand("DsPriorityAdd", "DsPriorityAdd(id, value, priority)",
        "Add a number with a priority (smaller comes out first; not a number counts as the largest); up to 1 000 000 entries", "Data Structures")]
    public static void DsPriorityAdd(double id, double value, double priority) =>
        Resolve<PgslPriority>("priority", id)?.Add(priority, value, null);

    [PgslCommand("DsPriorityAddString", "DsPriorityAddString(id, value, priority)", "Add text with a priority", "Data Structures")]
    public static void DsPriorityAddString(double id, string value, double priority) =>
        Resolve<PgslPriority>("priority", id)?.Add(priority, 0, value ?? string.Empty);

    [PgslCommand("DsPriorityDeleteMin", "DsPriorityDeleteMin(id) -> number",
        "Take out the entry of smallest priority (the earliest added among equals) and return it as a number; 0 when empty", "Data Structures")]
    public static double DsPriorityDeleteMin(double id) =>
        Resolve<PgslPriority>("priority", id) is { } queue && queue.TakeMin(out double number, out string text) ? PriorityNumber(number, text) : 0;

    [PgslCommand("DsPriorityDeleteMinString", "DsPriorityDeleteMinString(id) -> string",
        "Take out the entry of smallest priority and return it as text; empty when the queue is empty", "Data Structures")]
    public static string DsPriorityDeleteMinString(double id) =>
        Resolve<PgslPriority>("priority", id) is { } queue && queue.TakeMin(out double number, out string text) ? PriorityText(number, text) : string.Empty;

    [PgslCommand("DsPriorityFindMin", "DsPriorityFindMin(id) -> number",
        "The entry DsPriorityDeleteMin would take out, as a number, left in the queue; 0 when empty", "Data Structures")]
    public static double DsPriorityFindMin(double id) =>
        ResolveRead<PgslPriority>("priority", id) is { } queue && queue.PeekMin(out _, out double number, out string text) ? PriorityNumber(number, text) : 0;

    [PgslCommand("DsPriorityFindMinString", "DsPriorityFindMinString(id) -> string",
        "The entry DsPriorityDeleteMin would take out, as text; empty when the queue is empty", "Data Structures")]
    public static string DsPriorityFindMinString(double id) =>
        ResolveRead<PgslPriority>("priority", id) is { } queue && queue.PeekMin(out _, out double number, out string text) ? PriorityText(number, text) : string.Empty;

    [PgslCommand("DsPriorityMinPriority", "DsPriorityMinPriority(id) -> number",
        "The priority of the entry that comes out next; 0 when empty", "Data Structures")]
    public static double DsPriorityMinPriority(double id) =>
        ResolveRead<PgslPriority>("priority", id) is { } queue && queue.PeekMin(out double priority, out _, out _) ? priority : 0;

    #endregion
}
