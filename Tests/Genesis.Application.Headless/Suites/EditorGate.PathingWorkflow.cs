using System.Drawing;
using System.Reflection;
using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Rendering.Core;
using Genesis.Physics;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Headless.Suites;

internal static partial class EditorGate
{
    private static void CheckPathingNoviceWorkflow(HeadlessContext ctx, GateSuite.GateFixture fixture, PathingEditorControl editor, Form host)
    {
        HeadlessHarness.Step("Pathing Quick setup edits points and shares typed history and draft recovery", () =>
        {
            ResourceService resources = fixture.Resources(fixture.TwoD);
            string fresh = resources.CreateResource(ResourceFolderPolicy.RootFor(fixture.TwoD, ResourceKind.Pathing), ResourceKind.Pathing, "New route");
            PathingAsset defaults = PathingAssetSerializer.Load(fresh);
            HeadlessHarness.Assert(defaults.Dimension == PathingDimension.TwoD && defaults.Route.Speed == 80 && defaults.PreviewAgentCount == 1
                && defaults.Route.Waypoints.Count == 2 && defaults.Route.LoopMode == PathingLoopMode.PingPong, "New Pathing does not start as a usable 2D patrol.");
            string[] primary = editor.CommandBar.Items.OfType<ToolStripButton>().Where(button => button.Alignment != ToolStripItemAlignment.Right).Select(button => button.Text ?? string.Empty).ToArray();
            HeadlessHarness.Assert(primary.SequenceEqual(new[] { "Quick setup", "Code", "Use in game" })
                && !editor.CommandBar.Items.OfType<ToolStripDropDownButton>().Any(menu => menu.Available && menu.Text is "File" or "Edit" or "History"), "The Pathing primary toolbar remains crowded or duplicates document actions.");
            editor.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Quick setup").PerformClick();
            ComboBox points = SurfaceControls(editor).OfType<ComboBox>().Single(input => input.Name == "PathingPointChoice");
            points.SelectedIndex = 1;
            NumericUpDown x = SurfaceControls(editor).OfType<NumericUpDown>().Single(input => input.Name == "PathingPointX");
            decimal before = x.Value; x.Value += 16;
            HeadlessHarness.Assert(editor.IsDirty && editor.Code.CodeText.Contains((before + 16).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), "The selected point edit did not change the shared route.");
            editor.Undo(); HeadlessHarness.Assert(x.Value == before, "Point field Undo did not restore its selected point.");
            editor.Redo(); HeadlessHarness.Assert(x.Value == before + 16, "Point field Redo did not restore its value.");
            editor.Save(); HeadlessHarness.Assert(!editor.IsDirty, "Saving Pathing did not clear document state.");
            editor.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Code").PerformClick();
            string source = editor.Code.CodeText;
            int open = source.IndexOf('(', source.IndexOf("waypoint ", StringComparison.Ordinal));
            editor.Code.MoveCaret(source.IndexOf(',', open) + 2);
            HeadlessHarness.Assert(editor.Code.SignatureVisible && editor.Code.ActiveParameterIndex == 1
                && editor.Code.SignatureText.Contains("number y", StringComparison.Ordinal) && editor.Code.CaretStatusText.Contains("Argument 2", StringComparison.Ordinal),
                "Pathing did not show the selected waypoint argument type and caret context in the bottom bar.");
            editor.Code.MoveCaret(source.Length);
            editor.Code.TextBox.AppendText("\n// Authored route comment\n"); GateSuite.Pump(2, 15);
            HeadlessHarness.Assert(editor.IsDirty, "Typed route code did not mark the document dirty.");
            editor.Undo(); HeadlessHarness.Assert(editor.Code.CodeText == source && !editor.IsDirty, "Typed Undo bypassed document history or left saved content dirty.");
            editor.Redo(); editor.Save();
            using (PathingEditorControl reopened = new(editor.ResourcePath, fixture.TwoD.RootPath))
                HeadlessHarness.Assert(reopened.Code.CodeText.Contains("Authored route comment", StringComparison.Ordinal), "Save/reopen discarded authored definition comments.");
            source = editor.Code.CodeText; byte[] saved = File.ReadAllBytes(editor.ResourcePath);
            editor.Code.TextBox.AppendText("\nunknown: broken\n");
            bool rejected = false; try { editor.Save(); } catch (InvalidDataException) { rejected = true; }
            HeadlessHarness.Assert(rejected && editor.IsDirty && editor.Code.CodeText.Contains("unknown: broken", StringComparison.Ordinal)
                && File.ReadAllBytes(editor.ResourcePath).SequenceEqual(saved), "Invalid draft was lost or wrote the saved route.");
            editor.Undo(); HeadlessHarness.Assert(editor.Code.CodeText == source && !editor.IsDirty, "Invalid draft Undo did not return to the saved route.");
            editor.Redo(); HeadlessHarness.Assert(editor.Code.CodeText.Contains("unknown: broken", StringComparison.Ordinal) && editor.Code.Visible, "Invalid draft Redo did not retain code and its visible recovery surface.");
            editor.Undo();
            HeadlessHarness.Assert(!editor.TryApplyInspectorValue("Pathing.Speed", "NaN") && !editor.TryApplyInspectorValue("Pathing.Mode", "99"), "Invalid Inspector input was accepted as movement data.");
            editor.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Quick setup").PerformClick();
        });
        HeadlessHarness.Step("Use in game creates a real saved sprite patrol whose live route edits move the Room instance", () =>
        {
            editor.Code.CodeText = """
                pathing "Generated sprite patrol" {
                    dimension: TwoD
                    object: "Assets/Objects/Coin.object.json"
                    preview_agents: 1
                    mode: WaypointPatrol
                    loop: PingPong
                    speed: 80
                    waypoint "Start" (32, 96, 5) wait 0 curve false
                    waypoint "End" (400, 96, 5) wait 0 curve false
                }
                """;
            editor.Save();
            editor.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Use in game").PerformClick();
            SurfaceControls(editor).OfType<TextBox>().Single(input => input.Name == "PathingObjectName").Text = "Generated patrol proof";
            string? created = null; editor.OpenLinkedResourceRequested += (_, path) => created = path;
            SurfaceControls(editor).OfType<Button>().Single(button => button.Name == "PathingCreateObject").PerformClick();
            HeadlessHarness.Assert(created is not null && File.Exists(created), "The actual Use in game button did not create a saved Object.");
            JObject document = JObject.Parse(File.ReadAllText(created!));
            string routeName = ResourceNames.Name(fixture.TwoD.RootPath, editor.ResourcePath);
            string boundImage = ResourceNames.Resolve(fixture.TwoD.RootPath, (string?)document["sprite"] ?? string.Empty);
            string createSource = ObjectEventStore.Load(created!).GetValueOrDefault("Create", string.Empty);
            HeadlessHarness.Assert(File.Exists(boundImage) && createSource.Contains(routeName, StringComparison.Ordinal),
                "Generated Object does not bind the saved Image and route: sprite=" + document["sprite"] + ", Create=" + createSource + ", route=" + routeName);
            RoomAsset room = RoomAsset.Create("Generated patrol Room", RoomDimension.TwoD);
            RoomNode node = new() { Kind = RoomNodeKind.GameObject, Name = "Patrol instance",
                Transform = new() { X = 32, Y = 96, Z = 5 }, GameObject = new() { Prefab = ResourceNames.Name(fixture.TwoD.RootPath, created!) } };
            room.Nodes.Add(node);
            using RuntimeScene scene = new("Generated patrol gameplay");
            ProjectGameContext game = new(fixture.TwoD.RootPath, scene, null, null, room, null);
            VMEngine.Initialize(); ScriptHostSystem scripts = new(); scripts.SetContext(game);
            var oldGame = PgslCommands.ActiveGameContext; string oldPath = PgslCommands.ProjectPath; PgslContext? oldContext = PgslCommands.BindContext(new PgslContext());
            PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = fixture.TwoD.RootPath;
            try
            {
                RoomBuildResult built = new RoomSceneBuilder(fixture.TwoD.RootPath, scripts).Build(scene, room);
                var entity = built.EntitiesByNodeId[node.Id];
                HeadlessHarness.Assert(scripts.RecentDiagnostics.Count == 0 && scene.World.Has<NavMeshAgentComponent>(entity),
                    "The generated Object Create event did not start real gameplay navigation: " + string.Join(';', scripts.RecentDiagnostics));
                MethodInfo tick = typeof(PgslCommands).GetMethod("UpdateNavigation", BindingFlags.NonPublic | BindingFlags.Static)!;
                for (int frame = 0; frame < 60; frame++) tick.Invoke(null, [scene, 1f / 60]);
                TransformComponent moved = scene.World.GetRef<TransformComponent>(entity);
                HeadlessHarness.Assert(moved.X > 105 && Math.Abs(moved.Y - 96) < .01 && moved.Z == 5, "The generated patrol did not visibly advance its actual Room transform.");
                editor.TryApplyInspectorValue("Pathing.Speed", 160f); editor.Save();
                for (int frame = 0; frame < 30; frame++) tick.Invoke(null, [scene, 1f / 60]);
                TransformComponent faster = scene.World.GetRef<TransformComponent>(entity);
                HeadlessHarness.Assert(scene.World.GetRef<NavMeshAgentComponent>(entity).Agent.Speed == 160 && faster.X - moved.X > 60,
                    "Saving the open route did not refresh its already-running gameplay speed.");
                byte[] valid = File.ReadAllBytes(editor.ResourcePath); File.WriteAllText(editor.ResourcePath, "{ broken");
                for (int frame = 0; frame < 20; frame++) tick.Invoke(null, [scene, 1f / 60]);
                HeadlessHarness.Assert(scene.World.GetRef<NavMeshAgentComponent>(entity).Agent.Speed == 160 && scene.World.GetRef<TransformComponent>(entity).X > faster.X,
                    "An invalid live replacement discarded the last working route.");
                File.WriteAllBytes(editor.ResourcePath, valid);
                for (int frame = 0; frame < 90; frame++) tick.Invoke(null, [scene, 1f / 60]);
                HeadlessHarness.Assert(scene.World.GetRef<TransformComponent>(entity).X < 390, "PingPong failed to return after reaching the far point.");
                ctx.Report.Images.Add(ImageResult.From("Pathing Use in game", "pathing-use-in-game.png",
                    VisualCapture.CaptureOpenForm(host, Path.Combine(ctx.Captures, "pathing-use-in-game.png"), includeViewports: true)));
                editor.CommandBar.Items.OfType<ToolStripButton>().Single(button => button.Text == "Quick setup").PerformClick();
                CheckPathingObjectFrames(ctx, fixture.TwoD.RootPath, editor, room, scene, scene.World.GetRef<TransformComponent>(entity));
                ResourceService resources = fixture.Resources(fixture.TwoD);
                string physics = resources.CreateResource(ResourceFolderPolicy.RootFor(fixture.TwoD, ResourceKind.Physics), ResourceKind.Physics, "Pathing saved obstacle");
                new PhysicsSceneConfig { Dimension = PhysicsDimension.TwoD, BodyType = PhysicsBodyKind.Static, Shape = PhysicsBodyShape.Box }.SaveToFile(physics);
                var obstacle = scene.World.CreateEntity();
                TransformComponent obstaclePose = new() { X = 52, Y = 48, Z = 5, ScaleX = 1, ScaleY = 3, ScaleZ = 1 };
                scene.World.Set(obstacle, obstaclePose);
                ObjectDrawAssetRegistry.Set(obstacle, new ObjectDrawAssetEntry { Image = (string?)document["sprite"] });
                HeadlessHarness.Assert(SpritePhysicsBinding.Attach(scene.World, obstacle, fixture.TwoD.RootPath,
                    new JObject { ["physics"] = ResourceNames.Name(fixture.TwoD.RootPath, physics) }, obstaclePose), "The actual saved 2D static body could not be attached for navigation.");
                string tileImage = resources.CreateResource(ResourceFolderPolicy.RootFor(fixture.TwoD, ResourceKind.Image), ResourceKind.Image, "Pathing solid tile");
                ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(tileImage).Document, tileImage, ImageDocumentAccess.Editor);
                session.Document.Usage.Allowed = ImageUsage.Tileset;
                session.Document.Usage.Tileset.TileWidth = session.Document.Usage.Tileset.TileHeight = 16;
                session.Document.Usage.Tileset.Collision.Add(0);
                ImageWorkspaceStorage.Save(session, ImageWorkspace.CreateBlank(16, 16, Color.Gray));
                RoomTileLayerData tiles = new() { Tileset = ResourceNames.Name(fixture.TwoD.RootPath, tileImage), CellWidth = 16, CellHeight = 16, CollisionEnabled = true };
                tiles.Cells.Add(new RoomTileCell { X = 3, Y = 4 }); room.Nodes.Add(new RoomNode { Kind = RoomNodeKind.TileLayer, TileLayer = tiles });
                PgslCommands.NavMeshBakeGrid(0, 0, 128, 128, 1);
                double around = PgslCommands.NavMeshPathFind(8, 12, 5, 104, 96, 5);
                HeadlessHarness.Assert(PgslCommands.NavMeshPathFind(52, 48, 5, 104, 96, 5) < 0
                    && PgslCommands.NavMeshPathFind(56, 72, 5, 104, 96, 5) < 0 && around > 0 && PgslCommands.NavMeshPathGetWaypointCount(around) >= 3,
                    "Navigation did not project the saved Physics body and authored solid tile into the same Room pixel plane.");
                ObjectDrawAssetRegistry.Remove(obstacle);
                foreach (var live in built.SpawnedEntities) ObjectDrawAssetRegistry.Remove(live);
            }
            finally { PgslCommands.BindContext(oldContext); PgslCommands.ActiveGameContext = oldGame; PgslCommands.ProjectPath = oldPath; }
        });
    }

    private static void CheckPathingObjectFrames(HeadlessContext ctx, string project, PathingEditorControl editor, RoomAsset room, RuntimeScene scene, TransformComponent moved)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object preview = typeof(PathingEditorControl).GetField("_viewport", flags)!.GetValue(editor)!;
        EditorViewport3D viewport = (EditorViewport3D)preview.GetType().GetField("_viewport", flags)!.GetValue(preview)!;
        try
        {
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                viewport.Host.BackendOverride = backend.Backend; GateSuite.Pump(3, 15);
                using (Bitmap? warm = viewport.CaptureFrame(3)) { }
                IRenderController renderer = viewport.Host.Renderer ?? throw new InvalidOperationException("No Pathing gameplay renderer.");
                string name = backend.Backend switch { RenderBackendOption.OpenGL => "OpenGL", RenderBackendOption.Software => "Software", _ => backend.DisplayName };
                HeadlessHarness.Assert(renderer.BackendName == name, "A different backend drew the generated Pathing Object.");
                room.Settings.Width = renderer.PixelWidth; room.Settings.Height = renderer.PixelHeight;
                renderer.ClearPreviewShaderOverride(); renderer.BeginFrame(); renderer.Clear(.02f, .03f, .05f, 1);
                FrameRenderQueue queue = new();
                using RoomRenderSubsystem presentation = new(project, room);
                presentation.Render2D(scene, renderer, queue); queue.Flush(renderer, includeMeshes: false); renderer.EndFrame();
                HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] bytes), "Pathing gameplay has no physical frame readback.");
                using Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var data = bitmap.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                for (int row = 0; row < height; row++) System.Runtime.InteropServices.Marshal.Copy(bytes, row * width * 4, data.Scan0 + row * data.Stride, width * 4);
                bitmap.UnlockBits(data);
                int gold = 0, atStart = 0;
                for (int y = Math.Max(0, (int)moved.Y - 30); y < Math.Min(height, moved.Y + 30); y++)
                for (int x = 0; x < width; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    if (pixel.R < 180 || pixel.G < 130 || pixel.B > 100) continue;
                    if (Math.Abs(x - moved.X) < 30) gold++;
                    if (Math.Abs(x - 32) < 30) atStart++;
                }
                HeadlessHarness.Assert(gold > 80 && atStart == 0, name + ": the generated Object's saved sprite pixels did not move to its gameplay transform.");
                string file = "pathing-saved-object-" + backend.ShortName.ToLowerInvariant() + ".png";
                bitmap.Save(Path.Combine(ctx.Captures, file)); ctx.Report.Images.Add(ImageResult.From("Generated Pathing gameplay · " + name, file, VisualCapture.Measure(bitmap)));
            }
        }
        finally { viewport.Host.BackendOverride = null; }
    }
}
