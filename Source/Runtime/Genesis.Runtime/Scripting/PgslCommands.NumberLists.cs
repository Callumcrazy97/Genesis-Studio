using System;
using System.Collections.Generic;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// A list of numbers kept as numbers (PGSL NumList*): no boxing per entry, so a chunk's 40 000 vertex
/// numbers are one array, filled and read without garbage and copied in one move.
/// </summary>
internal sealed class PgslNumberList
{
    public double[] Items;
    public int Count;

    public PgslNumberList(int capacity) => Items = capacity > 0 ? new double[capacity] : [];

    public Span<double> Span => Items.AsSpan(0, Count);

    /// <summary>Room for at least this many entries (capped at a collection's limit).</summary>
    public bool EnsureCapacity(int wanted, int max)
    {
        if (wanted > max) return false;
        if (wanted <= Items.Length) return true;
        int size = Math.Max(wanted, Math.Max(16, Items.Length * 2));
        Array.Resize(ref Items, Math.Min(size, max));
        return true;
    }

    /// <summary>The list made this long: new entries are 0.</summary>
    public bool Resize(int count, int max)
    {
        if (count < 0 || !EnsureCapacity(count, max)) return false;
        if (count > Count) Array.Clear(Items, Count, count - Count);
        Count = count;
        return true;
    }

    public PgslNumberList Clone()
    {
        var copy = new PgslNumberList(Math.Max(Items.Length, Count));
        Array.Copy(Items, copy.Items, Count);
        copy.Count = Count;
        return copy;
    }
}

// Number lists: lists that hold only numbers, unboxed, with commands that work on a whole range at
// once (fill, copy, add a range, sum, scale, sort). They are 0-based like the other lists, live in the
// Object's store like them (family "nlist"), go to worker jobs the same way (JobScriptNumList) and
// build meshes in one call (MeshAddQuadsFromNumList). Ranges are clamped to the list, so a range that
// runs past the end covers what there is.
public static partial class PgslCommands
{
    #region Number lists

    private static PgslNumberList NumWrite(double id) => Resolve<PgslNumberList>("nlist", id);
    private static PgslNumberList NumRead(double id) => ResolveRead<PgslNumberList>("nlist", id);

    // A start and count clamped to a list of this length; false when nothing is left.
    private static bool NumRange(int length, double start, double count, out int from, out int n)
    {
        from = 0; n = 0;
        if (!double.IsFinite(start) || !double.IsFinite(count) || count <= 0) return false;
        from = (int)Math.Clamp(Math.Floor(start), 0, length);
        n = (int)Math.Min(Math.Floor(count), length - from);
        return n > 0;
    }

    [PgslCommand("NumListCreate", "NumListCreate() -> id", "Create a list of numbers kept unboxed (fast to fill, copy and give to jobs); returns its handle", "Lists")]
    public static double NumListCreate()
    {
        int handle = NextHandle("nlist");
        if (handle == 0) return 0;
        Bind("nlist", handle, new PgslNumberList(0));
        return handle;
    }

    [PgslCommand("NumListDestroy", "NumListDestroy(id)", "Release a number list", "Lists")]
    public static void NumListDestroy(double id) => Unbind("nlist", id);

    [PgslCommand("NumListClear", "NumListClear(id)", "Remove every entry (the list keeps its room)", "Lists")]
    public static void NumListClear(double id)
    {
        if (ReplaceHeld("nlist", id, held => held is PgslNumberList list ? new PgslNumberList(list.Items.Length) : null)) return;
        if (NumWrite(id) is { } list) list.Count = 0;
    }

    [PgslCommand("NumListReserve", "NumListReserve(id, count)", "Make room for this many entries (up to 1 000 000) so adding them grows nothing", "Lists")]
    public static void NumListReserve(double id, double count)
    {
        if (!double.IsFinite(count) || count <= 0) return;
        NumWrite(id)?.EnsureCapacity((int)Math.Min(count, MaxCollectionElements), MaxCollectionElements);
    }

    [PgslCommand("NumListSize", "NumListSize(id) -> number", "Entry count", "Lists")]
    public static double NumListSize(double id) => NumRead(id)?.Count ?? 0;

    [PgslCommand("NumListResize", "NumListResize(id, count) -> bool", "Make the list this long: entries past the end go, new ones are 0", "Lists")]
    public static bool NumListResize(double id, double count) =>
        double.IsFinite(count) && count >= 0 && NumWrite(id) is { } list && list.Resize((int)Math.Min(count, int.MaxValue), MaxCollectionElements);

    [PgslCommand("NumListAdd", "NumListAdd(id, value) -> index", "Add a number at the end; returns its index, -1 when the list is full", "Lists")]
    public static double NumListAdd(double id, double value)
    {
        PgslNumberList list = NumWrite(id);
        if (list is null || !list.EnsureCapacity(list.Count + 1, MaxCollectionElements)) return -1;
        list.Items[list.Count] = value;
        return list.Count++;
    }

    [PgslCommand("NumListAdd3", "NumListAdd3(id, a, b, c) -> index", "Add three numbers at the end (a position, a normal, a colour); returns the first one's index, -1 when full", "Lists")]
    public static double NumListAdd3(double id, double a, double b, double c)
    {
        PgslNumberList list = NumWrite(id);
        if (list is null || !list.EnsureCapacity(list.Count + 3, MaxCollectionElements)) return -1;
        int at = list.Count;
        list.Items[at] = a; list.Items[at + 1] = b; list.Items[at + 2] = c;
        list.Count += 3;
        return at;
    }

    [PgslCommand("NumListGet", "NumListGet(id, index) -> number", "The number at an index (0 outside the list)", "Lists")]
    public static double NumListGet(double id, double index)
    {
        PgslNumberList list = NumRead(id);
        return list is not null && double.IsFinite(index) && index >= 0 && index < list.Count ? list.Items[(int)index] : 0;
    }

    [PgslCommand("NumListSet", "NumListSet(id, index, value) -> bool", "Set the number at an index; an index past the end grows the list (new entries 0)", "Lists")]
    public static bool NumListSet(double id, double index, double value)
    {
        PgslNumberList list = NumWrite(id);
        if (list is null || !double.IsFinite(index) || index < 0 || index >= MaxCollectionElements) return false;
        int at = (int)index;
        if (at >= list.Count && !list.Resize(at + 1, MaxCollectionElements)) return false;
        list.Items[at] = value;
        return true;
    }

    [PgslCommand("NumListFill", "NumListFill(id, start, count, value) -> bool", "Set a range to one number, growing the list when the range runs past its end", "Lists")]
    public static bool NumListFill(double id, double start, double count, double value)
    {
        PgslNumberList list = NumWrite(id);
        if (list is null || !double.IsFinite(start) || !double.IsFinite(count) || start < 0 || count <= 0) return false;
        long end = (long)start + (long)count;
        if (end > MaxCollectionElements) return false;
        if (end > list.Count && !list.Resize((int)end, MaxCollectionElements)) return false;
        list.Items.AsSpan((int)start, (int)count).Fill(value);
        return true;
    }

    [PgslCommand("NumListCopy", "NumListCopy(dest, destStart, source, sourceStart, count) -> copied",
        "Copy a range of one number list into another (or within one) at destStart, growing dest when needed; returns how many were copied", "Lists")]
    public static double NumListCopy(double dest, double destStart, double source, double sourceStart, double count)
    {
        PgslNumberList from = NumRead(source);
        if (from is null || !NumRange(from.Count, sourceStart, count, out int s, out int n)) return 0;
        if (!double.IsFinite(destStart) || destStart < 0) return 0;
        // Read the range first: writing dest may make it a copy of its own when a job holds it.
        double[] items = from.Items;
        PgslNumberList to = NumWrite(dest);
        if (to is null) return 0;
        if (ReferenceEquals(to, from)) items = to.Items;
        long end = (long)destStart + n;
        if (end > MaxCollectionElements) return 0;
        if (end > to.Count && !to.Resize((int)end, MaxCollectionElements)) return 0;
        Array.Copy(items, s, to.Items, (int)destStart, n);
        return n;
    }

    [PgslCommand("NumListAddRange", "NumListAddRange(dest, source, start, count) -> added",
        "Add a range of another number list at the end of this one in one move; returns how many were added", "Lists")]
    public static double NumListAddRange(double dest, double source, double start, double count)
    {
        PgslNumberList to = NumRead(dest);
        return to is null ? 0 : NumListCopy(dest, to.Count, source, start, count);
    }

    [PgslCommand("NumListSum", "NumListSum(id, start, count) -> number", "The sum of a range", "Lists")]
    public static double NumListSum(double id, double start, double count)
    {
        PgslNumberList list = NumRead(id);
        if (list is null || !NumRange(list.Count, start, count, out int s, out int n)) return 0;
        double sum = 0;
        foreach (double value in list.Items.AsSpan(s, n)) sum += value;
        return sum;
    }

    [PgslCommand("NumListMin", "NumListMin(id, start, count) -> number", "The smallest number in a range (0 for an empty range)", "Lists")]
    public static double NumListMin(double id, double start, double count)
    {
        PgslNumberList list = NumRead(id);
        if (list is null || !NumRange(list.Count, start, count, out int s, out int n)) return 0;
        double best = double.PositiveInfinity;
        foreach (double value in list.Items.AsSpan(s, n)) if (value < best) best = value;
        return best;
    }

    [PgslCommand("NumListMax", "NumListMax(id, start, count) -> number", "The largest number in a range (0 for an empty range)", "Lists")]
    public static double NumListMax(double id, double start, double count)
    {
        PgslNumberList list = NumRead(id);
        if (list is null || !NumRange(list.Count, start, count, out int s, out int n)) return 0;
        double best = double.NegativeInfinity;
        foreach (double value in list.Items.AsSpan(s, n)) if (value > best) best = value;
        return best;
    }

    [PgslCommand("NumListScale", "NumListScale(id, start, count, multiply, add) -> bool",
        "Change every number in a range to number * multiply + add in one move (move a chunk's vertices, convert units)", "Lists")]
    public static bool NumListScale(double id, double start, double count, double multiply, double add)
    {
        if (!double.IsFinite(multiply) || !double.IsFinite(add)) return false;
        PgslNumberList list = NumWrite(id);
        if (list is null || !NumRange(list.Count, start, count, out int s, out int n)) return false;
        Span<double> span = list.Items.AsSpan(s, n);
        for (int i = 0; i < span.Length; i++) span[i] = (span[i] * multiply) + add;
        return true;
    }

    [PgslCommand("NumListSort", "NumListSort(id, start, count, ascending) -> bool", "Sort a range of numbers", "Lists")]
    public static bool NumListSort(double id, double start, double count, bool ascending)
    {
        PgslNumberList list = NumWrite(id);
        if (list is null || !NumRange(list.Count, start, count, out int s, out int n)) return false;
        Span<double> span = list.Items.AsSpan(s, n);
        span.Sort();
        if (!ascending) span.Reverse();
        return true;
    }

    [PgslCommand("NumListIndexOf", "NumListIndexOf(id, value) -> index", "The first index holding this number, -1 when none does", "Lists")]
    public static double NumListIndexOf(double id, double value)
    {
        PgslNumberList list = NumRead(id);
        return list is null ? -1 : list.Span.IndexOf(value);
    }

    [PgslCommand("NumListFromList", "NumListFromList(numList, list) -> count",
        "Fill a number list from an ordinary list (entries that are not numbers become 0), replacing what it held", "Lists")]
    public static double NumListFromList(double numList, double list)
    {
        List<object> from = ResolveRead<List<object>>("list", list);
        PgslNumberList to = NumWrite(numList);
        if (from is null || to is null || !to.EnsureCapacity(from.Count, MaxCollectionElements)) return 0;
        for (int i = 0; i < from.Count; i++) to.Items[i] = AsNumber(from[i]);
        to.Count = from.Count;
        return to.Count;
    }

    [PgslCommand("NumListToList", "NumListToList(numList, list) -> count", "Replace an ordinary list's entries with a number list's numbers", "Lists")]
    public static double NumListToList(double numList, double list)
    {
        PgslNumberList from = NumRead(numList);
        List<object> to = Resolve<List<object>>("list", list);
        if (from is null || to is null) return 0;
        to.Clear();
        if (to.Capacity < from.Count) to.Capacity = from.Count;
        foreach (double value in from.Span) to.Add(value);
        return to.Count;
    }

    [PgslCommand("JobScriptNumList", "JobScriptNumList(job, numList, copyBack) -> bool",
        "Give a prepared job a number list as it is when the job starts (same handle inside the job; shared, not copied); copyBack true puts the job's list into this one at JobTake if the job changed it", "Native Jobs")]
    public static bool JobScriptNumList(double job, double numList, bool copyBack) => ShareWithJob(job, "nlist", numList, copyBack);

    #endregion
}
