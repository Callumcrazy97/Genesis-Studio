using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Spatial;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    [PgslCommand("WindowGetWidth", "WindowGetWidth() -> number", "Current render/window width for responsive DrawGui layouts", "Display")]
    public static double WindowGetWidth() => Math.Max(1, ActiveGameContext?.RenderWidth ?? 1280);
    [PgslCommand("WindowGetHeight", "WindowGetHeight() -> number", "Current render/window height for responsive DrawGui layouts", "Display")]
    public static double WindowGetHeight() => Math.Max(1, ActiveGameContext?.RenderHeight ?? 720);

    private static RectangleF GuiRect() => RoomDisplayLayout.Gui(ActiveGameContext?.Room, (int)WindowGetWidth(), (int)WindowGetHeight());
    [PgslCommand("WindowGetGuiX", "WindowGetGuiX() -> number", "Left of the GUI area: where the room's window lands when it is letterboxed (scaleViewportsWithWindow), else 0", "Display")]
    public static double WindowGetGuiX() => GuiRect().X;
    [PgslCommand("WindowGetGuiY", "WindowGetGuiY() -> number", "Top of the GUI area: where the room's window lands when it is letterboxed, else 0", "Display")]
    public static double WindowGetGuiY() => GuiRect().Y;
    [PgslCommand("WindowGetGuiWidth", "WindowGetGuiWidth() -> number", "Width of the GUI area, the letterboxed room window (the whole window when not letterboxed)", "Display")]
    public static double WindowGetGuiWidth() => GuiRect().Width;
    [PgslCommand("WindowGetGuiHeight", "WindowGetGuiHeight() -> number", "Height of the GUI area, the letterboxed room window (the whole window when not letterboxed)", "Display")]
    public static double WindowGetGuiHeight() => GuiRect().Height;
    [PgslCommand("WindowGetGuiScale", "WindowGetGuiScale() -> number", "GUI area width over the room's authored window width: multiply design-size GUI by this to match the world", "Display")]
    public static double WindowGetGuiScale()
    {
        RoomAsset room = ActiveGameContext?.Room;
        return GuiRect().Width / Math.Max(1, room == null ? WindowGetWidth() : RoomDisplayLayout.WindowSize(room).Width);
    }

    private static readonly ConditionalWeakTable<RoomAsset, Dictionary<string, double>> RoomValues = new();
    private static RoomTileCollisionMap TileMap => RoomTileCollisionMap.Get(ActiveGameContext?.Room, ProjectPath);
    private static RectangleF CallerBounds(double x, double y)
    {
        PgslContext context = GetContext();
        return SpriteCollisionBounds.Resolve(SpriteCollisionBounds.Load(ProjectPath, context?.SpriteIndex),
            (int)(context?.ImageIndex ?? 0), (float)x, (float)y,
            (float)(context?.ImageXScale ?? 1), (float)(context?.ImageYScale ?? 1), (float)(context?.ImageAngle ?? 0));
    }

    [PgslCommand("TileMeeting", "TileMeeting(x,y) -> bool", "Test the caller's authored sprite mask against solid room tiles", "Tiles")]
    public static bool TileMeeting(double x, double y) => double.IsFinite(x) && double.IsFinite(y)
        && (TileMap?.Intersects(CallerBounds(x, y)) ?? false);

    [PgslCommand("TileMoveX", "TileMoveX(x,y,amount) -> number", "Sweep the sprite against tiles and return its allowed new X; assign x to the result", "Tiles")]
    public static double TileMoveX(double x, double y, double amount) => TileMove(x, y, amount, true);

    [PgslCommand("TileMoveY", "TileMoveY(x,y,amount) -> number", "Sweep the sprite against tiles and return its allowed new Y; assign y to the result", "Tiles")]
    public static double TileMoveY(double x, double y, double amount) => TileMove(x, y, amount, false);

    private static double TileMove(double x, double y, double amount, bool horizontal)
    {
        double original = horizontal ? x : y;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(amount)) return original;
        float delta = (float)Math.Clamp(amount, -1000000, 1000000);
        return original + (TileMap?.Sweep(CallerBounds(x, y), delta, horizontal) ?? delta);
    }

    [PgslCommand("TileIndexAt", "TileIndexAt(x,y,layer?) -> number", "Read a painted tile index, or -1 for empty; layer is the tile node name or id", "Tiles")]
    public static double TileIndexAt(double x, double y, string layer = "")
    {
        RoomTileCollisionMap map = TileMap;
        return map != null && map.TryCell((float)x, (float)y, layer, out _, out _, out int index) ? index : -1;
    }

    [PgslCommand("TileSetAt", "TileSetAt(x,y,tileX,tileY,layer?) -> bool", "Replace an existing live tile and invalidate collision; does not edit the saved room", "Tiles")]
    public static bool TileSetAt(double x, double y, int tileX, int tileY, string layer = "")
    {
        RoomTileCollisionMap map = TileMap;
        if (tileX < 0 || tileY < 0 || map == null || !map.TryCell((float)x, (float)y, layer, out _, out RoomTileCell cell, out _)) return false;
        cell.TileX = tileX; cell.TileY = tileY;
        RoomTileCollisionMap.Invalidate(ActiveGameContext.Room);
        return true;
    }

    [PgslCommand("TileRemoveAt", "TileRemoveAt(x,y,layer?) -> bool", "Remove a live tile and its collision without changing the saved room", "Tiles")]
    public static bool TileRemoveAt(double x, double y, string layer = "")
    {
        RoomTileCollisionMap map = TileMap;
        if (map == null || !map.TryCell((float)x, (float)y, layer, out RoomNode node, out RoomTileCell cell, out _)) return false;
        node.TileLayer.Cells.Remove(cell);
        RoomTileCollisionMap.Invalidate(ActiveGameContext.Room);
        return true;
    }

    [PgslCommand("RoomValueSet", "RoomValueSet(name,value)", "Set a numeric value shared by scripts in the current room; not a global or a save file", "Rooms")]
    public static void RoomValueSet(string name, double value)
    {
        RoomAsset room = ActiveGameContext?.Room;
        if (room == null || string.IsNullOrWhiteSpace(name) || !double.IsFinite(value)) return;
        RoomValues.GetValue(room, _ => new Dictionary<string, double>(StringComparer.Ordinal))[name] = value;
    }

    [PgslCommand("RoomValueGet", "RoomValueGet(name,default?) -> number", "Read a room-scoped numeric script value", "Rooms")]
    public static double RoomValueGet(string name, double fallback = 0)
    {
        RoomAsset room = ActiveGameContext?.Room;
        return room != null && name != null && RoomValues.TryGetValue(room, out Dictionary<string, double> values)
            && values.TryGetValue(name, out double result) ? result : fallback;
    }
}
