using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Physics;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS.Components;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void RoomNoviceWorkflow(HeadlessContext ctx, GateSuite.GateFixture fixture)
    {
        var project = fixture.TwoD; ResourceService resources = fixture.Resources(project);
        string path = resources.CreateResource(fixture.Folder(project, "Rooms"), ResourceKind.Room, "Gate Room workflow");
        using RoomEditorControl editor = new(path, project.RootPath);
        using Form host = GateSuite.NewHost(1280, 800); host.Controls.Add(editor); GateSuite.ShowHost(host);
        EditorCommandBar bar = editor.Controls.OfType<EditorCommandBar>().Single();
        HeadlessHarness.Step("Room primary commands and guide lead to real authoring and the retained project starting Room", () =>
        {
            string[] primary = bar.Items.Cast<ToolStripItem>().Where(item => item.Available && item.Alignment != ToolStripItemAlignment.Right)
                .Select(item => item.Text ?? string.Empty).ToArray();
            HeadlessHarness.Assert(primary.SequenceEqual(new[] { "Select", "Move", "Rotate", "Scale", "Use in game", "Options" }),
                "Room keeps repeated or crowded primary commands: " + string.Join(',', primary));
            bar.Items.OfType<ToolStripButton>().Single(item => item.Text == "Use in game").PerformClick();
            FlowLayoutPanel guide = SurfaceControls(editor).OfType<FlowLayoutPanel>().Single(control => control.Name == "RoomUseInGame");
            HeadlessHarness.Assert(guide.Visible && guide.Controls.OfType<Label>().Any(label => label.Text.Contains("saved origin", StringComparison.Ordinal)),
                "Room guide omitted the shared Image origin or did not open.");
            HeadlessHarness.Assert(!guide.Controls.OfType<Button>().Any(button => button.Name == "RoomMakeStartingRoom"),
                "The Room guide still offers a Starting Room command; the game starts in the first Room of the room order.");
            guide.Controls.OfType<Button>().Single(button => button.Name == "RoomGuideTiles").PerformClick();
            HeadlessHarness.Assert(!guide.Visible && editor.Navigation.CurrentSection == RoomNavSection.Tilesets,
                "Guide did not return to the actual tile authoring panel.");
        });
        HeadlessHarness.Step("Instances selection exposes the actual node Inspector and preserves locking and context isolation", () =>
        {
            RoomNode node = new() { Kind = RoomNodeKind.GameObject, Name = "Hierarchy proof",
                GameObject = new() { Prefab = ResourceNames.Name(project.RootPath,
                    ProjectAssetIndex.Enumerate(project.RootPath, ResourceKind.GameObject).First().FullPath) } };
            editor.Room.Nodes.Add(node); editor.FlushPendingRoomUiRefresh();
            editor.Navigation.SetSection(RoomNavSection.Instances); editor.Select(node); GateSuite.Pump(2, 10);
            HeadlessHarness.Assert(ReferenceEquals(editor.SelectedNode, node) && editor.CanEditNodeInActiveContext(node)
                && SurfaceControls(editor.Inspector).Any(control => control.Name == "InspectorProperty_Context_Selection_Name"),
                "The Instances hierarchy selection did not own its real editable Inspector.");
            editor.SetNodeLocked(node, true);
            HeadlessHarness.Assert(editor.CanInspectNodeInActiveContext(node) && !editor.CanEditNodeInActiveContext(node), "Locked hierarchy selection became editable.");
            editor.Navigation.SetSection(RoomNavSection.Tilesets);
            HeadlessHarness.Assert(!editor.CanInspectNodeInActiveContext(node), "Inactive Objects leaked into the Tileset Inspector.");
        });
        HeadlessHarness.Step("visible tile layer Add and Delete share Room Undo and preserve tiles on save and reopen", () =>
        {
            RoomTilesetsPanel tiles = editor.Navigation.TilesetsPanel;
            string sheet = ProjectAssetIndex.Enumerate(project.RootPath, ResourceKind.Image)
                .First(asset => TileSetInfo.Load(asset.FullPath) is not null).FullPath;
            HeadlessHarness.Assert(tiles.SelectTileset(sheet), "Visible tileset selector did not arm painting.");
            int initial = editor.Room.Nodes.Count;
            SurfaceControls(tiles).OfType<Button>().Single(button => button.Text == "+ Add").PerformClick();
            RoomNode layer = tiles.ActiveTileLayer ?? throw new InvalidOperationException("Add did not select a layer.");
            HeadlessHarness.Assert(editor.Room.Nodes.Count == initial + 1 && layer.TileLayer?.Tileset == ResourceNames.Name(project.RootPath, sheet),
                "Add did not create a separate layer with the chosen tileset.");
            editor.Undo(); editor.FlushPendingRoomUiRefresh();
            HeadlessHarness.Assert(!editor.Room.Nodes.Contains(layer), "Undo did not remove the added layer.");
            editor.Redo(); editor.FlushPendingRoomUiRefresh(); tiles.SelectLayer(layer);
            SurfaceControls(tiles).OfType<Button>().Single(button => button.Text == "Delete").PerformClick();
            HeadlessHarness.Assert(!editor.Room.Nodes.Contains(layer), "Delete did not remove the selected empty layer.");
            editor.Undo(); editor.FlushPendingRoomUiRefresh();
            HeadlessHarness.Assert(editor.Room.Nodes.Contains(layer), "Undo did not restore the deleted layer.");
            editor.Save();
            HeadlessHarness.Assert(RoomAssetLoader.Parse(path).Nodes.Any(node => node.Id == layer.Id && node.TileLayer?.Tileset == layer.TileLayer?.Tileset),
                "Save/reopen lost the restored layer or tileset.");
        });
        HeadlessHarness.Step("background colour selection shares history, locked-layer protection and saved Room state", () =>
        {
            editor.Navigation.SetSection(RoomNavSection.Backgrounds);
            RoomBackgroundsPanel backgrounds = editor.Navigation.BackgroundsPanel;
            HeadlessHarness.Assert(backgrounds.AssignBackgroundColor(Color.FromArgb(255, 36, 92, 164)), "Colour authoring did not create a visible background.");
            RoomNode node = backgrounds.ActiveBackgroundLayer ?? throw new InvalidOperationException("No colour background was selected.");
            RoomBackgroundData background = node.Background ?? throw new InvalidOperationException("Missing colour background data.");
            HeadlessHarness.Assert(node.Enabled && background.Asset == string.Empty && background.TintArgb == Color.FromArgb(255, 36, 92, 164).ToArgb(), "Colour authoring retained an Image or incorrect colour.");
            editor.Undo(); HeadlessHarness.Assert(!editor.Room.Nodes.Contains(node), "Colour creation Undo failed.");
            editor.Redo(); editor.Save();
            HeadlessHarness.Assert(RoomAssetLoader.Parse(path).Nodes.Single(item => item.Id == node.Id).Background?.TintArgb == background.TintArgb, "Saved colour did not reopen.");
            editor.SetNodeLocked(node, true);
            HeadlessHarness.Assert(!backgrounds.AssignBackgroundColor(Color.Red) && node.Background!.TintArgb != Color.Red.ToArgb(), "Colour authoring ignored a locked layer.");
        });
        HeadlessHarness.Step("2D Room gravity uses understandable units, real Inspector controls, history and sprite physics", () =>
        {
            editor.Navigation.SetSection(RoomNavSection.Settings); GateSuite.Pump(3, 20);
            RoomInspectorPanel settings = SurfaceControls(editor.Navigation).OfType<RoomInspectorPanel>().Single(panel => panel.Visible);
            ResourceInspectorPropertySurface surface = SurfaceControls(settings).OfType<ResourceInspectorPropertySurface>().Single();
            HeadlessHarness.Assert(surface.SetGroupExpanded("Physics", true), "2D Room omitted its Physics settings.");
            NumericUpDown down = SurfaceControls(settings).OfType<NumericUpDown>().Single(control => control.Name == "InspectorProperty_Context_Room_GravityDown2D");
            NumericUpDown horizontal = SurfaceControls(settings).OfType<NumericUpDown>().Single(control => control.Name == "InspectorProperty_Context_Room_GravityHorizontal2D");
            down.Value = 160; GateSuite.Pump(2, 10);
            HeadlessHarness.Assert(Math.Abs(editor.Room.Environment.Gravity[1] + 5) < .001, "Downward pixel gravity was not mapped to the solver's metre/Y-up convention.");
            editor.Undo(); HeadlessHarness.Assert(Math.Abs(editor.Room.Environment.Gravity[1] + 9.81f) < .001, "Gravity undo failed.");
            editor.Redo(); horizontal.Value = 64; editor.Save();
            RoomAsset room = RoomAssetLoader.Parse(path);
            HeadlessHarness.Assert(room.Environment.Gravity.SequenceEqual(new[] { 2f, -5f, 0f }), "Saved 2D gravity lost units or axis direction.");
            string physics = resources.CreateResource(fixture.Folder(project, "Physics"), ResourceKind.Physics, "Room falling body");
            string objectPath;
            using (PhysicsEditorControl author = new(physics, project.RootPath))
            {
                PhysicsSceneConfig body = PhysicsScenePresets.Create("2D Platformer"); body.BodyType = PhysicsBodyKind.Dynamic;
                HeadlessHarness.Assert(author.SetDefinition(PhysicsCodeCodec.Serialize(body)), "The body definition failed.");
                HeadlessHarness.Assert(author.ChooseSpriteImage(ResourceNames.Name(project.RootPath,
                    ProjectAssetIndex.Enumerate(project.RootPath, ResourceKind.Image).First(asset => TileSetInfo.Load(asset.FullPath) is null).FullPath)), "The saved sprite was not selected.");
                objectPath = author.CreatePhysicsObject("Room gravity Object");
            }
            RoomNode falling = new() { Kind = RoomNodeKind.GameObject, GameObject = new() { Prefab = ResourceNames.Name(project.RootPath, objectPath) }, Transform = new() { X = 100, Y = 100 } };
            room.Nodes.Add(falling);
            using RuntimeScene scene = new("Authored Room gravity");
            var entity = new RoomSceneBuilder(project.RootPath).Build(scene, room).EntitiesByNodeId[falling.Id];
            HeadlessHarness.Assert(scene.Physics is not null && scene.IsSpritePhysicsScene
                && Vector3.Distance(scene.Physics.GetGravityAcceleration(), new Vector3(2, -5, 0)) < .001, "Room gravity did not reach the real sprite solver.");
            scene.Physics!.EnableThreadDispatcher = false;
            for (int i = 0; i < 30; i++) scene.UpdateFixed(1f / 60);
            TransformComponent position = scene.World.GetRef<TransformComponent>(entity);
            HeadlessHarness.Assert(position.X > 105 && position.Y > 115, $"Authored horizontal/downward gravity did not move the sprite: {position.X},{position.Y}.");
        });
        const string capture = "room-novice-workflow.png";
        ctx.Report.Images.Add(ImageResult.From("Room novice workflow", capture, VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, capture))));
        host.Close();
    }
}
