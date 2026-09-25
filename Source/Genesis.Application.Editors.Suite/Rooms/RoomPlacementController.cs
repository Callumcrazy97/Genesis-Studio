using System.Drawing;
using System.Numerics;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public enum PlacementKind
{
    None,
    Object,
    Tile,
}

/// <summary>
/// Controls object and tile placement state, snapping, ghost preview coordinates,
/// and continuous drag-placement with duplicate-in-cell prevention.
/// </summary>
public sealed class RoomPlacementController
{
    private readonly RoomEditorControl _editor;
    private Point? _lastPlacedCell;
    private string? _lastPlacedResource;

    public RoomPlacementController(RoomEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    public PlacementKind Kind { get; private set; } = PlacementKind.None;

    public string? ArmedObjectPath { get; private set; }

    public TileSetInfo? ArmedTileSet { get; private set; }

    public int ArmedTileIndex { get; private set; } = -1;

    public string? TargetLayerId { get; set; }

    public bool SnapToGrid { get; set; } = true;

    public bool AlignToGrid { get; set; } = true;

    public float PlacementRotation { get; set; }

    public Vector2 PlacementScale { get; set; } = Vector2.One;
    public float PlacementScaleZ { get; set; } = 1f;

    public Vector2? GhostWorld { get; private set; }

    public Point? GhostTileCell { get; private set; }

    public bool IsArmed => Kind != PlacementKind.None;

    public void ArmObject(string objectPath, string? layerId = null)
    {
        Kind = PlacementKind.Object;
        ArmedObjectPath = objectPath;
        ArmedTileSet = null;
        ArmedTileIndex = -1;
        if (!string.IsNullOrWhiteSpace(layerId))
        {
            TargetLayerId = layerId;
        }

        ResetContinuousPlacement();
    }

    public void ArmTile(TileSetInfo tileSet, int tileIndex, string? layerId = null)
    {
        Kind = PlacementKind.Tile;
        ArmedTileSet = tileSet;
        ArmedTileIndex = tileIndex;
        ArmedObjectPath = null;
        if (!string.IsNullOrWhiteSpace(layerId))
        {
            TargetLayerId = layerId;
        }

        ResetContinuousPlacement();
    }

    public void Disarm()
    {
        Kind = PlacementKind.None;
        ArmedObjectPath = null;
        ArmedTileSet = null;
        ArmedTileIndex = -1;
        GhostWorld = null;
        GhostTileCell = null;
        ResetContinuousPlacement();
    }

    public void ResetContinuousPlacement()
    {
        _lastPlacedCell = null;
        _lastPlacedResource = null;
    }

    public void UpdateGhost(Vector2 worldPos, float gridSize)
    {
        if (!IsArmed)
        {
            GhostWorld = null;
            GhostTileCell = null;
            return;
        }

        float grid = MathF.Max(1f, gridSize);
        if (Kind == PlacementKind.Object)
        {
            Vector2 snapped = SnapToGrid
                ? new Vector2(MathF.Round(worldPos.X / grid) * grid, MathF.Round(worldPos.Y / grid) * grid)
                : worldPos;
            GhostWorld = snapped;
            GhostTileCell = new Point((int)MathF.Floor(snapped.X / grid), (int)MathF.Floor(snapped.Y / grid));
        }
        else if (Kind == PlacementKind.Tile)
        {
            int tileW = ArmedTileSet?.TileWidth ?? (int)grid;
            int tileH = ArmedTileSet?.TileHeight ?? (int)grid;
            int cellX = (int)MathF.Floor(worldPos.X / MathF.Max(1, tileW));
            int cellY = (int)MathF.Floor(worldPos.Y / MathF.Max(1, tileH));
            GhostTileCell = new Point(cellX, cellY);
            GhostWorld = new Vector2(cellX * tileW, cellY * tileH);
        }
    }

    /// <summary>
    /// Checks whether continuous placement should skip this cell to avoid repeatedly stacking identical
    /// instances or tiles into the exact same cell/location.
    /// </summary>
    public bool ShouldPlaceObject(Vector2 snappedPos, float gridSize, string objectPath)
    {
        float grid = MathF.Max(1f, gridSize);
        Point currentCell = new((int)MathF.Round(snappedPos.X / grid), (int)MathF.Round(snappedPos.Y / grid));
        if (_lastPlacedCell == currentCell && string.Equals(_lastPlacedResource, objectPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Also check if an instance with the same prefab already exists at this exact position (within 1px tolerance)
        string targetRelative = ResourceNames.Name(_editor.ProjectRoot, objectPath);
        foreach (RoomNode node in _editor.Room.Nodes)
        {
            if (node.Kind == RoomNodeKind.GameObject
                && node.GameObject is not null
                && string.Equals(node.GameObject.Prefab, targetRelative, StringComparison.OrdinalIgnoreCase))
            {
                float dx = MathF.Abs(node.Transform.X - snappedPos.X);
                float dy = MathF.Abs(node.Transform.Y - snappedPos.Y);
                if (dx < 1f && dy < 1f)
                {
                    return false; // Already occupied by same entity at this spot
                }
            }
        }

        _lastPlacedCell = currentCell;
        _lastPlacedResource = objectPath;
        return true;
    }

    /// <summary>
    /// Checks whether continuous tile painting should skip this cell to avoid duplicating identical tiles.
    /// </summary>
    public bool ShouldPlaceTile(int cellX, int cellY, int tileIndex, RoomNode? targetLayerNode)
    {
        Point cell = new(cellX, cellY);
        string resourceId = $"{ArmedTileSet?.ImagePath}:{tileIndex}";
        if (_lastPlacedCell == cell && string.Equals(_lastPlacedResource, resourceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (targetLayerNode?.TileLayer is not null)
        {
            int columns = ArmedTileSet is not null ? Math.Max(1, ArmedTileSet.ColumnsFor(ArmedTileSet.TileWidth * 16)) : 16;
            int tileX = tileIndex % columns;
            int tileY = tileIndex / columns;

            RoomTileCell? existing = targetLayerNode.TileLayer.Cells.Find(c => c.X == cellX && c.Y == cellY);
            if (existing is not null && existing.TileX == tileX && existing.TileY == tileY)
            {
                return false; // Already contains this exact tile
            }
        }

        _lastPlacedCell = cell;
        _lastPlacedResource = resourceId;
        return true;
    }
}
