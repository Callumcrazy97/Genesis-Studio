using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Part of a grid lent to a worker job (JobScriptGridRegion), so several jobs can fill parts of one
// big grid at once: tiles of a map, strips of a field, areas of a world.
//
// The job is given its own copy of the whole grid, made when it starts into memory kept for the next
// job (so a big grid lent again and again is not allocated again), and reads it as it was then. It
// may change any cell of its copy, but only the rectangle comes back: JobTake writes that rectangle
// into the game's grid and leaves every other cell as the game has it, the game's own changes
// meanwhile included. The game's grid is not held by the job, so the game goes on writing it in
// place while the job runs. Jobs lent rectangles of one grid that do not overlap all come back
// whole; where rectangles overlap, the one taken last wins.
public static partial class PgslCommands
{
    /// <summary>A rectangle of grid cells, corners included.</summary>
    internal readonly record struct GridRect(int X1, int Y1, int X2, int Y2);

    // Cell arrays of lent copies no job is using any more, by kind and cell count, kept for the
    // next job given a grid of that size (a few of each; the rest are left to the collector).
    private static readonly ConcurrentDictionary<(GridKind Kind, int Count), ConcurrentBag<Array>> LentGridArrays = new();
    private const int LentGridArraysKept = 8;
    private static long _lentGridArraysMade;

    /// <summary>Cell arrays made for lent grids so far, not reused ones (for tests and speed reports).</summary>
    internal static long LentGridArraysMade => System.Threading.Interlocked.Read(ref _lentGridArraysMade);

    /// <summary>A copy of a grid for a job, in an array kept from an earlier job where one is free.</summary>
    private static PgslGrid RentGridCopy(PgslGrid source)
    {
        int count = source.Width * source.Height;
        Array storage = null;
        if (LentGridArrays.TryGetValue((source.Kind, count), out ConcurrentBag<Array> free)) free.TryTake(out storage);
        if (storage == null)
        {
            storage = Array.CreateInstance(source.Storage.GetType().GetElementType(), count);
            System.Threading.Interlocked.Increment(ref _lentGridArraysMade);
        }
        Array.Copy(source.Storage, storage, count);
        return PgslGrid.Over(source.Kind, source.Width, source.Height, storage);
    }

    /// <summary>Gives the cell arrays of lent copies back for later jobs; the copies are emptied, so a stray use fails.</summary>
    private static void ReturnGridCopies(List<object> copies)
    {
        if (copies == null) return;
        foreach (object item in copies)
        {
            if (item is not PgslGrid grid || grid.Width * grid.Height == 0) continue;
            Array storage = grid.Storage;
            ConcurrentBag<Array> free = LentGridArrays.GetOrAdd((grid.Kind, storage.Length), _ => []);
            if (free.Count < LentGridArraysKept) free.Add(storage);
            grid.Empty();
        }
    }

    /// <summary>
    /// Writes a job's rectangle into the game's grid, cell for cell. Nothing is written when either
    /// side changed the grid's size or kind since the job started.
    /// </summary>
    private static void MergeGridRegion(int handle, PgslGrid from, GridRect rect)
    {
        // The game's own grid, to write: copied first only if another job holds it, as any change is.
        PgslGrid live = Resolve<PgslGrid>("grid", handle);
        if (live == null || live.Kind != from.Kind || live.Width != from.Width || live.Height != from.Height) return;
        int width = rect.X2 - rect.X1 + 1;
        for (int y = rect.Y1; y <= rect.Y2; y++)
            PgslGrid.CopyRun(from, (y * from.Width) + rect.X1, live, (y * live.Width) + rect.X1, width);
    }

    /// <summary>
    /// Writes a job's range of a list (first to last, as the job's list has them) into the game's,
    /// entry for entry where both lists have the entry: a range lent cannot grow or shrink the list.
    /// </summary>
    private static void MergeListRange(int handle, List<object> from, int first, int last)
    {
        List<object> live = Resolve<List<object>>("list", handle);
        if (live == null) return;
        int end = Math.Min(last, Math.Min(from.Count, live.Count) - 1);
        for (int i = first; i <= end; i++) live[i] = from[i];
    }

    private static void MergeNumListRange(int handle, PgslNumberList from, int first, int last)
    {
        PgslNumberList live = NumWrite(handle);
        if (live == null) return;
        int end = Math.Min(last, Math.Min(from.Count, live.Count) - 1);
        if (end >= first) Array.Copy(from.Items, first, live.Items, first, end - first + 1);
    }

    // A range of a list, as a rectangle one entry high (X1 to X2).
    private static bool LendRange(double job, string family, double handle, int length, double start, double count, bool copyBack)
    {
        if (!double.IsFinite(start) || !double.IsFinite(count) || count < 1) return Refuse("The range's start and count must be numbers, the count at least 1.");
        int first = (int)Math.Max(0, Math.Floor(start));
        int last = (int)Math.Min(length - 1L, Math.Floor(start) + Math.Floor(count) - 1);
        if (first > last) return Refuse($"The range {StringOf(start)} + {StringOf(count)} is outside the {length}-entry list.");
        return ShareWithJob(job, family, handle, copyBack, new GridRect(first, 0, last, 0));
    }

    [PgslCommand("JobScriptListRegion", "JobScriptListRegion(job, list, start, count, copyBack) -> bool",
        "Give a prepared job a list it may fill part of (count entries from start): the job reads the whole list as it is when the job starts (shared, copied only if the job writes it), and with copyBack JobTake writes only that range into this list, keeping every other entry as the game has it; jobs given ranges that do not overlap all come back. The range cannot grow or shrink the list", "Native Jobs")]
    public static bool JobScriptListRegion(double job, double list, double start, double count, bool copyBack) =>
        ResolveRead<List<object>>("list", list) is { } found
            ? LendRange(job, "list", list, found.Count, start, count, copyBack)
            : Refuse($"No list with handle {StringOf(list)} in this Object.");

    [PgslCommand("JobScriptNumListRegion", "JobScriptNumListRegion(job, numList, start, count, copyBack) -> bool",
        "The same for a number list (NumListCreate): the job reads all of it, JobTake writes back only its range", "Native Jobs")]
    public static bool JobScriptNumListRegion(double job, double numList, double start, double count, bool copyBack) =>
        ResolveRead<PgslNumberList>("nlist", numList) is { } found
            ? LendRange(job, "nlist", numList, found.Count, start, count, copyBack)
            : Refuse($"No number list with handle {StringOf(numList)} in this Object.");

    [PgslCommand("JobScriptGridRegion", "JobScriptGridRegion(job, grid, x1, y1, x2, y2, copyBack) -> bool",
        "Give a prepared job a grid it may fill part of (x1, y1 to x2, y2, corners included): the job reads the whole grid as it is when the job starts, and with copyBack JobTake writes only that rectangle into this grid, keeping every other cell as the game has it. The game goes on changing its grid while the job runs, and jobs given parts that do not overlap all come back. The job's copy is made when it starts, in memory kept for the next job", "Native Jobs")]
    public static bool JobScriptGridRegion(double job, double grid, double x1, double y1, double x2, double y2, bool copyBack)
    {
        PgslGrid found = ResolveRead<PgslGrid>("grid", grid);
        if (found == null) return Refuse($"No grid with handle {StringOf(grid)} in this Object.");
        if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2))
            return Refuse("The rectangle's corners must be numbers.");
        if (x1 > x2) (x1, x2) = (x2, x1);
        if (y1 > y2) (y1, y2) = (y2, y1);
        var rect = new GridRect((int)Math.Max(0, Math.Floor(x1)), (int)Math.Max(0, Math.Floor(y1)),
            (int)Math.Min(found.Width - 1, Math.Floor(x2)), (int)Math.Min(found.Height - 1, Math.Floor(y2)));
        if (rect.X1 > rect.X2 || rect.Y1 > rect.Y2)
            return Refuse($"The rectangle {StringOf(x1)}, {StringOf(y1)} to {StringOf(x2)}, {StringOf(y2)} is outside the {found.Width} x {found.Height} grid.");
        return ShareWithJob(job, "grid", grid, copyBack, rect);
    }
}
