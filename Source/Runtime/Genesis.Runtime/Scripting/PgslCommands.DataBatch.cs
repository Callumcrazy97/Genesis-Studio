using System;
using System.Collections.Generic;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Whole-region grid and list work in one command, so a script that would otherwise read or write
// thousands of cells one call at a time (a generated heightmap, a chunk of a voxel world, a tile
// map) does it natively. Regions are inclusive corners as in DsGridSetRegion, positions 0-based;
// cells outside the grid are skipped, a bad handle does nothing and returns 0 (or -1 for a find).
public static partial class PgslCommands
{
    #region Data structures: batch

    /// <summary>The part of a region inside the grid; empty when it misses the grid.</summary>
    private static (int Left, int Top, int Right, int Bottom) ClipRegion(PgslGrid grid, double x1, double y1, double x2, double y2)
    {
        (int left, int top, int right, int bottom) = NormaliseRegion(x1, y1, x2, y2);
        return (Math.Max(left, 0), Math.Max(top, 0), Math.Min(right, grid.Width - 1), Math.Min(bottom, grid.Height - 1));
    }

    private static bool SameValue(double a, double b) => Math.Abs(a - b) < 1e-9;

    [PgslCommand("DsGridCopyRegion", "DsGridCopyRegion(destination, dx, dy, source, x1, y1, x2, y2) -> number",
        "Copy a region of one grid into another (or the same) grid with its top-left at (dx, dy); returns the cells copied", "Grids")]
    public static double DsGridCopyRegion(double destination, double dx, double dy, double source, double x1, double y1, double x2, double y2)
    {
        PgslGrid to = Resolve<PgslGrid>("grid", destination);
        PgslGrid from = ResolveRead<PgslGrid>("grid", source);
        if (to is null || from is null || !(Math.Abs(dx) < 1e9) || !(Math.Abs(dy) < 1e9)) return 0;
        (int left, int top, int right, int bottom) = ClipRegion(from, x1, y1, x2, y2);
        // Where the clipped region lands: the requested corner keeps its place relative to it.
        (int requestLeft, int requestTop, _, _) = NormaliseRegion(x1, y1, x2, y2);
        long shiftX = (long)Math.Floor(dx) - requestLeft, shiftY = (long)Math.Floor(dy) - requestTop;
        int copied = 0;
        // Rows and cells run in the order that never reads a cell this copy has already written.
        bool backwardY = ReferenceEquals(to, from) && shiftY > 0;
        bool backwardX = ReferenceEquals(to, from) && shiftY == 0 && shiftX > 0;
        for (int row = 0; row <= bottom - top; row++)
        {
            int y = backwardY ? bottom - row : top + row;
            long ty = y + shiftY;
            if (ty < 0 || ty >= to.Height) continue;
            for (int column = 0; column <= right - left; column++)
            {
                int x = backwardX ? right - column : left + column;
                long tx = x + shiftX;
                if (tx < 0 || tx >= to.Width) continue;
                to.Cells[(ty * to.Width) + tx] = from.Cells[(y * from.Width) + x];
                copied++;
            }
        }
        return copied;
    }

    [PgslCommand("DsGridToList", "DsGridToList(grid, x1, y1, x2, y2, list) -> number",
        "Replace a list's entries with a region's cells, row by row (cells outside the grid read 0); returns the entries written", "Grids")]
    public static double DsGridToList(double grid, double x1, double y1, double x2, double y2, double list)
    {
        PgslGrid from = ResolveRead<PgslGrid>("grid", grid);
        List<object> to = Resolve<List<object>>("list", list);
        if (from is null || to is null) return 0;
        (int left, int top, int right, int bottom) = NormaliseRegion(x1, y1, x2, y2);
        long count = ((long)right - left + 1) * ((long)bottom - top + 1);
        if (count <= 0 || count > MaxCollectionElements) return 0;
        to.Clear();
        if (to.Capacity < count) to.Capacity = (int)count;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++) to.Add(BoxNumber(from.Get(x, y)));
        }
        return count;
    }

    [PgslCommand("DsGridFromList", "DsGridFromList(grid, x1, y1, x2, y2, list) -> number",
        "Write a list's entries into a region, row by row, until the list runs out (cells outside the grid are skipped); returns the entries read", "Grids")]
    public static double DsGridFromList(double grid, double x1, double y1, double x2, double y2, double list)
    {
        PgslGrid to = Resolve<PgslGrid>("grid", grid);
        List<object> from = ResolveRead<List<object>>("list", list);
        if (to is null || from is null) return 0;
        (int left, int top, int right, int bottom) = NormaliseRegion(x1, y1, x2, y2);
        long width = (long)right - left + 1;
        int read = 0;
        for (long y = top; y <= bottom && read < from.Count; y++)
        {
            if (y < 0 || y >= to.Height)
            {
                read = (int)Math.Min(from.Count, read + width);
                continue;
            }
            for (long x = left; x <= right && read < from.Count; x++)
            {
                double value = AsNumber(from[read++]);
                if (x >= 0 && x < to.Width) to.Cells[(y * to.Width) + x] = value;
            }
        }
        return read;
    }

    [PgslCommand("DsGridCount", "DsGridCount(grid, x1, y1, x2, y2, value) -> number",
        "How many cells of a region hold the value", "Grids")]
    public static double DsGridCount(double grid, double x1, double y1, double x2, double y2, double value)
    {
        PgslGrid target = ResolveRead<PgslGrid>("grid", grid);
        if (target is null) return 0;
        (int left, int top, int right, int bottom) = ClipRegion(target, x1, y1, x2, y2);
        int count = 0;
        for (int y = top; y <= bottom; y++)
        {
            int row = y * target.Width;
            for (int x = left; x <= right; x++)
                if (SameValue(target.Cells[row + x], value)) count++;
        }
        return count;
    }

    [PgslCommand("DsGridFind", "DsGridFind(grid, x1, y1, x2, y2, value) -> number",
        "The first cell holding the value, as x + y * width, searching rows from y1 towards y2 and each row from x1 towards x2 (so a top-down search gives y1 > y2); -1 when none", "Grids")]
    public static double DsGridFind(double grid, double x1, double y1, double x2, double y2, double value) =>
        GridSearch(grid, x1, y1, x2, y2, value, wantEqual: true);

    [PgslCommand("DsGridFindOther", "DsGridFindOther(grid, x1, y1, x2, y2, value) -> number",
        "The first cell NOT holding the value, as x + y * width, searching as DsGridFind does (the highest solid block of a column: from the top, past the air); -1 when none", "Grids")]
    public static double DsGridFindOther(double grid, double x1, double y1, double x2, double y2, double value) =>
        GridSearch(grid, x1, y1, x2, y2, value, wantEqual: false);

    private static double GridSearch(double grid, double x1, double y1, double x2, double y2, double value, bool wantEqual)
    {
        PgslGrid target = ResolveRead<PgslGrid>("grid", grid);
        if (target is null || !double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) || !double.IsFinite(y2)) return -1;
        (int left, int top, int right, int bottom) = ClipRegion(target, x1, y1, x2, y2);
        if (left > right || top > bottom) return -1;
        bool upY = y1 <= y2, upX = x1 <= x2;
        for (int row = 0; row <= bottom - top; row++)
        {
            int y = upY ? top + row : bottom - row;
            int start = y * target.Width;
            for (int column = 0; column <= right - left; column++)
            {
                int x = upX ? left + column : right - column;
                if (SameValue(target.Cells[start + x], value) == wantEqual) return start + x;
            }
        }
        return -1;
    }

    [PgslCommand("DsGridAddRegion", "DsGridAddRegion(grid, x1, y1, x2, y2, value)", "Add a value to every cell of a region", "Grids")]
    public static void DsGridAddRegion(double grid, double x1, double y1, double x2, double y2, double value) =>
        ForRegion(grid, x1, y1, x2, y2, cell => cell + value);

    [PgslCommand("DsGridMultiplyRegion", "DsGridMultiplyRegion(grid, x1, y1, x2, y2, value)", "Multiply every cell of a region by a value", "Grids")]
    public static void DsGridMultiplyRegion(double grid, double x1, double y1, double x2, double y2, double value) =>
        ForRegion(grid, x1, y1, x2, y2, cell => cell * value);

    [PgslCommand("DsGridClampRegion", "DsGridClampRegion(grid, x1, y1, x2, y2, min, max)", "Clamp every cell of a region between min and max", "Grids")]
    public static void DsGridClampRegion(double grid, double x1, double y1, double x2, double y2, double min, double max)
    {
        if (double.IsNaN(min) || double.IsNaN(max)) return;
        double low = Math.Min(min, max), high = Math.Max(min, max);
        ForRegion(grid, x1, y1, x2, y2, cell => Math.Clamp(cell, low, high));
    }

    [PgslCommand("DsGridFloorRegion", "DsGridFloorRegion(grid, x1, y1, x2, y2)", "Round every cell of a region down to a whole number (Floor)", "Grids")]
    public static void DsGridFloorRegion(double grid, double x1, double y1, double x2, double y2) =>
        ForRegion(grid, x1, y1, x2, y2, Math.Floor);

    private static void ForRegion(double grid, double x1, double y1, double x2, double y2, Func<double, double> change)
    {
        PgslGrid target = Resolve<PgslGrid>("grid", grid);
        if (target is null) return;
        (int left, int top, int right, int bottom) = ClipRegion(target, x1, y1, x2, y2);
        for (int y = top; y <= bottom; y++)
        {
            int row = y * target.Width;
            for (int x = left; x <= right; x++) target.Cells[row + x] = change(target.Cells[row + x]);
        }
    }

    [PgslCommand("DsGridAddGrid", "DsGridAddGrid(destination, source, factor) -> number",
        "Add source * factor to destination, cell by cell, where both grids have the cell (layering noise fields, masks); returns the cells changed", "Grids")]
    public static double DsGridAddGrid(double destination, double source, double factor)
    {
        PgslGrid to = Resolve<PgslGrid>("grid", destination);
        PgslGrid from = ResolveRead<PgslGrid>("grid", source);
        if (to is null || from is null) return 0;
        int width = Math.Min(to.Width, from.Width), height = Math.Min(to.Height, from.Height);
        for (int y = 0; y < height; y++)
        {
            int toRow = y * to.Width, fromRow = y * from.Width;
            for (int x = 0; x < width; x++) to.Cells[toRow + x] += from.Cells[fromRow + x] * factor;
        }
        return (double)width * height;
    }

    [PgslCommand("DsListFill", "DsListFill(list, count, value)", "Make a list hold count copies of a number (up to 1 000 000)", "Lists")]
    public static void DsListFill(double list, double count, double value)
    {
        List<object> target = Resolve<List<object>>("list", list);
        if (target is null || double.IsNaN(count)) return;
        int entries = (int)Math.Clamp(Math.Floor(count), 0, MaxCollectionElements);
        target.Clear();
        if (target.Capacity < entries) target.Capacity = entries;
        object boxed = value;
        for (int index = 0; index < entries; index++) target.Add(boxed);
    }

    [PgslCommand("DsListCopy", "DsListCopy(destination, source)", "Replace one list's entries with another's", "Lists")]
    public static void DsListCopy(double destination, double source)
    {
        List<object> to = Resolve<List<object>>("list", destination);
        List<object> from = ResolveRead<List<object>>("list", source);
        if (to is null || from is null || ReferenceEquals(to, from)) return;
        to.Clear();
        to.AddRange(from);
    }

    #endregion
}
