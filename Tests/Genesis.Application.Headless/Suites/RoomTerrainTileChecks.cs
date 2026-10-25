using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Genesis.World.Terrain;
namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The Room editor's "Split terrain into tiles": the selected Terrain becomes a node for each tile
/// where it was, its resource split beside it, the room given a terrain distance so they stream;
/// one undo puts the original back.
/// </summary>
internal static class RoomTerrainTileChecks
{
    public static void SplitIntoTiles(ProjectSession project, ResourceService resources)
    {
        string terrainPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Terrain), ResourceKind.Terrain, "Split Isle");
        new TerrainAsset(65, 65, 1f, -32f, -32f, 0f, 20f).Save(terrainPath + ".gterrain");
        string roomPath = resources.CreateResource(Path.Combine(resources.AssetsRoot, "Rooms"), ResourceKind.Room, "Split Room");
        RoomAsset room = RoomAssetLoader.Parse(roomPath);
        room.Dimension = RoomDimension.ThreeD;
        room.Nodes.Add(new RoomNode
        {
            Name = "Isle", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id, EnabledIn2D = false,
            Terrain = new RoomTerrainData { Asset = ResourceNames.Name(project.RootPath, terrainPath) },
            Transform = { X = 100f, Y = 2f, Z = -50f },
        });
        RoomAssetLoader.Save(room, roomPath);
        ResourceCatalog.Invalidate(project.RootPath);

        using Form host = new() { Width = 1280, Height = 800, ShowInTaskbar = false };
        var editor = new Genesis.Application.Editors.Suite.Rooms.RoomEditorControl(roomPath, project.RootPath) { Dock = DockStyle.Fill };
        host.Controls.Add(editor);
        GateSuite.ShowHost(host);
        System.Windows.Forms.Application.DoEvents();
        // Terrains are chosen in the Terrain & tiles section of the 3D view.
        editor.ViewMode3D = true;
        editor.Navigation.SetSection(Genesis.Application.Editors.Suite.Rooms.RoomNavSection.Tilesets);
        RoomNode isle = editor.Room.Nodes.Single(node => node.Kind == RoomNodeKind.Terrain);
        editor.ChooseTerrain(isle);
        var tiles = editor.SplitSelectedTerrain(2, out string said);
        var terrains = editor.Room.Nodes.Where(node => node.Kind == RoomNodeKind.Terrain).ToList();
        bool placed = tiles.Count == 4 && terrains.Count == 4 && !terrains.Contains(isle) && editor.SelectedNode == tiles[0]
            && terrains.All(node => node.Transform.X == 100f && node.Transform.Y == 2f && node.Transform.Z == -50f)
            && terrains.All(node => RoomTerrainSubsystem.ResolveTerrainFile(project.RootPath, node.Terrain!.Asset) is { } file && TerrainAsset.Load(file).ResolutionX == 33)
            && terrains.Select(node => node.Name).OrderBy(name => name).SequenceEqual(["Isle 1-1", "Isle 1-2", "Isle 2-1", "Isle 2-2"]);
        float distance = editor.Room.Environment.TerrainDistance;
        editor.Undo();
        var afterUndo = editor.Room.Nodes.Where(node => node.Kind == RoomNodeKind.Terrain).ToList();
        HeadlessHarness.Assert(placed && distance == 48f && afterUndo.Count == 1 && afterUndo[0] == isle && editor.Room.Environment.TerrainDistance == 0f,
            $"'{said}' Split into {tiles.Count} nodes ({string.Join(", ", terrains.Select(node => node.Name))}), placed where the isle was {placed}; terrain distance {distance} (48, a tile and a half); " +
            $"undo left {afterUndo.Count} terrain(s), the isle {afterUndo.FirstOrDefault() == isle}, distance {editor.Room.Environment.TerrainDistance}.");
    }
}
