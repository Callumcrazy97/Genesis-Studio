using Genesis.Application.Core.Terrain;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Rooms;

// A terrain too large to load at once made into tiles the room reads as the camera nears each:
// the selected Terrain's resource is split beside it (TerrainTileSplit) and the node replaced, in
// one undoable step, by a node for each tile where the original was. A room with no terrain
// distance yet is given one of a tile and a half, so the tiles stream from the first run.
public sealed partial class RoomEditorControl
{
    /// <summary>Tiles a side the outliner's "Split terrain into tiles" offers.</summary>
    internal static readonly int[] TerrainTileCounts = [2, 3, 4, 8];

    /// <summary>
    /// Splits the selected Terrain into <paramref name="perSide"/> by <paramref name="perSide"/>
    /// tiles and places them in its stead; the new nodes, or none when it cannot (the status line says why).
    /// </summary>
    internal IReadOnlyList<RoomNode> SplitSelectedTerrain(int perSide) => SplitSelectedTerrain(perSide, out _);

    /// <summary>As <see cref="SplitSelectedTerrain(int)"/>, with what the status line said.</summary>
    internal IReadOnlyList<RoomNode> SplitSelectedTerrain(int perSide, out string message)
    {
        if (_selected is not { Kind: RoomNodeKind.Terrain, Terrain: { } data } original || EditableSelection().All(node => node != original))
        {
            UpdateStatus(message = "Select one Terrain on an unlocked layer to split it into tiles.");
            return [];
        }
        string resource = ResourceNames.Resolve(ProjectRoot, data.Asset, ResourceType.Terrain);
        IReadOnlyList<TerrainTileSplit.Tile> tiles;
        try
        {
            tiles = TerrainTileSplit.Split(ProjectAssetIndex.OpenResourceService(ProjectRoot), resource, perSide, perSide);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            UpdateStatus(message = $"'{original.Name}' could not be split: {exception.Message}");
            return [];
        }

        int index = _room.Nodes.IndexOf(original);
        List<RoomNode> placed = [];
        foreach (TerrainTileSplit.Tile tile in tiles)
        {
            RoomNode node = CloneNode(original);
            node.Id = RoomAsset.NewId();
            node.Name = $"{original.Name} {tile.Column + 1}-{tile.Row + 1}";
            node.Terrain!.Asset = ResourceNames.Name(ProjectRoot, tile.ResourcePath);
            placed.Add(node);
        }
        // Children of the original (a cabin on the island) stay where they are, on the first tile.
        List<(RoomNode Node, string Parent)> children = _room.Nodes.Where(node => node.ParentId == original.Id).Select(node => (node, node.ParentId)).ToList();
        float distanceBefore = _room.Environment?.TerrainDistance ?? 0f;
        float tileDistance = TileDistance(original, tiles.Count > 0 ? tiles[0].ResourcePath : null);

        void Apply()
        {
            _room.Nodes.Remove(original);
            _room.Nodes.InsertRange(Math.Clamp(index, 0, _room.Nodes.Count), placed);
            foreach ((RoomNode child, _) in children) child.ParentId = placed[0].Id;
            if (_room.Environment is { TerrainDistance: <= 0f } environment && tileDistance > 0f) environment.TerrainDistance = tileDistance;
            RefreshSceneViews();
            RefreshPhase4Ui();
            ChooseTerrain(placed[0]);
        }

        void Revert()
        {
            foreach (RoomNode node in placed) _room.Nodes.Remove(node);
            _room.Nodes.Insert(Math.Clamp(index, 0, _room.Nodes.Count), original);
            foreach ((RoomNode child, string parent) in children) child.ParentId = parent;
            if (_room.Environment is { } environment) environment.TerrainDistance = distanceBefore;
            RefreshSceneViews();
            RefreshPhase4Ui();
            ChooseTerrain(original);
        }

        Apply();
        PushEdit($"Split '{original.Name}' into {placed.Count} tiles", Apply, Revert);
        UpdateStatus(message = $"'{original.Name}' is now {placed.Count} tiles that load as the camera nears them.");
        return placed;
    }

    /// <summary>Makes <paramref name="node"/> the terrain the Terrain &amp; tiles section works on (its list's choice) and selects it.</summary>
    internal void ChooseTerrain(RoomNode node)
    {
        if (_workspaceTerrains is not null)
        {
            RefreshWorkspaceTerrains();
            _refreshingTerrainList = true;
            try { _workspaceTerrains.SelectedItem = node; }
            finally { _refreshingTerrainList = false; }
            ActiveRoomEditContextChanged(preservePlacement: false);
        }
        Select(node);
    }

    // A tile and a half across, in the room's units: far enough that the tiles round the camera are there.
    private static float TileDistance(RoomNode original, string? firstTile)
    {
        if (firstTile is null || !File.Exists(firstTile + ".gterrain")) return 0f;
        try
        {
            Genesis.World.Terrain.TerrainAsset tile = Genesis.World.Terrain.TerrainAsset.Load(firstTile + ".gterrain");
            float across = MathF.Max((tile.ResolutionX - 1) * MathF.Abs(original.Transform.ScaleX), (tile.ResolutionZ - 1) * MathF.Abs(original.Transform.ScaleZ)) * tile.CellSize;
            return MathF.Round(across * 1.5f);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return 0f;
        }
    }
}
