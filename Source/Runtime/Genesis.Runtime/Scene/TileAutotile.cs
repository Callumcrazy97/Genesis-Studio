#nullable enable
using System.Collections.Generic;

namespace Genesis.Runtime.Scene;

/// <summary>
/// Autotiling: a tile on an autotiled tile set takes the tile that fits its neighbours on the same
/// layer, so a wall or a path is painted in one stroke and its ends, edges and corners choose
/// themselves. Each neighbour present adds to a number (above 1, right 2, below 4, left 8); the tile set
/// keeps the 16 tiles for those numbers in a 4×4 block, number n at column + n % 4, row + n / 4.
/// </summary>
public static class TileAutotile
{
    /// <summary>The tile for a cell whose neighbours make <paramref name="mask"/>.</summary>
    public static (int TileX, int TileY) TileFor(int mask, int column, int row) => (column + (mask & 3), row + ((mask >> 2) & 3));

    /// <summary>
    /// Gives the cell at (x, y), when there is one, and its four neighbours the tiles their
    /// neighbours call for; call after a cell was added or taken away.
    /// </summary>
    public static void Refresh(RoomTileLayerData layer, int column, int row, int x, int y)
    {
        if (layer?.Cells == null) return;
        Dictionary<(int, int), RoomTileCell> cells = Index(layer);
        Refresh(cells, column, row, x, y);
        Refresh(cells, column, row, x, y - 1);
        Refresh(cells, column, row, x + 1, y);
        Refresh(cells, column, row, x, y + 1);
        Refresh(cells, column, row, x - 1, y);
    }

    /// <summary>Every cell of the layer given the tile its neighbours call for.</summary>
    public static void RefreshAll(RoomTileLayerData layer, int column, int row)
    {
        if (layer?.Cells == null) return;
        Dictionary<(int, int), RoomTileCell> cells = Index(layer);
        foreach (RoomTileCell cell in layer.Cells) Refresh(cells, column, row, cell.X, cell.Y);
    }

    private static Dictionary<(int, int), RoomTileCell> Index(RoomTileLayerData layer)
    {
        var cells = new Dictionary<(int, int), RoomTileCell>(layer.Cells.Count);
        foreach (RoomTileCell cell in layer.Cells) cells[(cell.X, cell.Y)] = cell;
        return cells;
    }

    private static void Refresh(Dictionary<(int, int), RoomTileCell> cells, int column, int row, int x, int y)
    {
        if (!cells.TryGetValue((x, y), out RoomTileCell? cell)) return;
        int mask = (cells.ContainsKey((x, y - 1)) ? 1 : 0) | (cells.ContainsKey((x + 1, y)) ? 2 : 0)
            | (cells.ContainsKey((x, y + 1)) ? 4 : 0) | (cells.ContainsKey((x - 1, y)) ? 8 : 0);
        (cell.TileX, cell.TileY) = TileFor(mask, column, row);
    }
}
