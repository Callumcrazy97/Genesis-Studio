using System.Drawing;
using System.Numerics;
using System.Text.Json;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Core.Resources;
using Genesis.Runtime;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Runtime.Spatial;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Application.Headless.Suites;

/// <summary>Native regression coverage for protected resources and the shipped, editable 2D level.
/// No screenshot is substituted for an executable assertion. GPU/audio acceptance remains separate.</summary>
internal static class TwoDShowcaseSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "TwoDShowcase");
        string parent = Path.Combine(ctx.Workspace, "ShowcaseRegression");
        Directory.CreateDirectory(parent);
        ProjectSession? project = null;
        HeadlessHarness.RunCase(ctx.Report, "Showcase.CreateNativeTemplate", () =>
        {
            project = new ProjectService().CreateProject(parent, "Mushroom Meadow", TwoDShowcaseTemplate.TemplateId);
            Assert(project.Manifest.StartRoom == TwoDShowcaseTemplate.StartRoom, "Start room was not installed.");
            Assert(!project.Manifest.Runtime.AllowEscapeToClose, "Escape must pause, not close the showcase.");
            Assert(Directory.GetFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories).Length == 0,
                "Showcase gameplay must be PGSL, not an alternate C# game.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Resources.ProtectedRootsAndTypedCreation", () =>
        {
            ProjectSession p = Require(project);
            ResourceService resources = new(p);
            Assert(!Directory.Exists(Path.Combine(p.AssetsPath, "Voxels")), "A voxel root was created.");
            Assert(ResourceFolderPolicy.Roots.Count == 13, "The supported standalone resource kinds need thirteen roots.");
            foreach (ResourceRootFolder root in ResourceFolderPolicy.Roots)
            {
                string folder = Path.Combine(p.AssetsPath, root.Name);
                Assert(Directory.Exists(folder) && ResourceFolderPolicy.IsProtected(p, folder), root.Name + " is not protected.");
                ResourceItem item = resources.BuildTree().Children.Single(child => child.FullPath == folder);
                Assert(item.IsProtectedRoot && item.AllowedResourceKind == root.Kind, root.Name + " lost tree typing.");
                string created = resources.CreateResource(p.AssetsPath, root.Kind, "Root probe " + root.Kind);
                Assert(Path.GetDirectoryName(created) == folder, "Assets-heading creation did not route " + root.Kind);
                string nested = resources.CreateFolder(folder, "Editable subfolder");
                string moved = resources.Move(created, nested);
                Assert(File.Exists(moved), "Typed subfolder move failed: " + root.Kind);
                Denied(() => resources.Rename(folder, "Renamed"));
                Denied(() => resources.MoveToTrash(folder));
                Denied(() => resources.Move(folder, nested));
                Denied(() => resources.Copy(folder, nested));
                Denied(() => resources.SetClipboard(ResourceClipboardOperation.Cut, [folder]));
                ResourceKind other = root.Kind == ResourceKind.Audio ? ResourceKind.Image : ResourceKind.Audio;
                Denied(() => resources.CreateResource(folder, other, "Wrong kind"));
                string wrong = ResourceFolderPolicy.RootFor(p, other);
                Assert(!resources.CanTransfer(moved, wrong), "Drag/drop offered a mismatched target.");
                Denied(() => resources.Move(moved, wrong));
                Assert(File.Exists(moved), "A refused move removed its source.");
            }
            Denied(() => resources.CreateFolder(p.AssetsPath, "Mixed assets"));
            Denied(() => resources.CreateResource(p.AssetsPath, ResourceKind.TerrainEntity, "Standalone part"));
        });

        HeadlessHarness.RunCase(ctx.Report, "Resources.OpenOldProjectAddsRootsWithoutMovingAssets", () =>
        {
            ProjectSession legacy = new ProjectService().CreateProject(parent, "Legacy");
            foreach (ResourceRootFolder root in ResourceFolderPolicy.Roots)
            {
                string directory = Path.Combine(legacy.AssetsPath, root.Name);
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            string old = Path.Combine(legacy.AssetsPath, "Images", "Old.image.json");
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            File.WriteAllText(old, ImageDocumentSerializer.Serialize(ImageDocument.CreateDefault()));
            byte[] original = File.ReadAllBytes(old);
            ProjectSession reopened = new ProjectService().OpenProject(legacy.ProjectFile);
            Assert(File.ReadAllBytes(old).SequenceEqual(original), "Opening rewrote/moved a legacy image.");
            Assert(ResourceFolderPolicy.Roots.All(root => Directory.Exists(Path.Combine(reopened.AssetsPath, root.Name))),
                "Reopening did not recreate all missing roots.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Showcase.EditableImagesAndOwnedPixels", () =>
        {
            ProjectSession p = Require(project);
            foreach (string file in Directory.GetFiles(Path.Combine(p.AssetsPath, "Sprites"), "*.image.json"))
            {
                ImageDocument document = ImageDocumentSerializer.Deserialize(File.ReadAllText(file)).Document;
                SpriteRuntimeAsset runtime = SpriteAssetLoader.Load(file);
                Assert(runtime.Canvas.Width == document.Canvas.Width && runtime.Frames.Count == document.Frames.Count,
                    "Editor/runtime sprite mismatch: " + file);
                foreach (SpriteRuntimeFrame frame in runtime.Frames)
                {
                    string texture = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, frame.Source));
                    Assert(File.Exists(texture), "Missing owned frame: " + texture);
                    using Bitmap bitmap = new(texture);
                    Assert(bitmap.Width >= frame.SourceRectangle.Width && bitmap.Height >= frame.SourceRectangle.Height,
                        "Frame extends beyond its owned PNG.");
                }
            }
            SpriteRuntimeAsset hero = SpriteAssetLoader.Load(p.RootPath, "Assets/Sprites/Explorer Idle.image.json");
            RectangleF bounds = SpriteCollisionBounds.Resolve(hero, 0, 100, 288);
            Assert(bounds == new RectangleF(93, 254, 14, 34), "The authored foot-origin mask did not reach the runtime.");
            Assert(SpriteCollisionBounds.Resolve(hero, 0, 100, 288, -1, 1) == bounds,
                "Horizontal flip moved the symmetric hero mask.");
            RectangleF rotated = SpriteCollisionBounds.Resolve(hero, 0, 100, 288, 1, 1, 90);
            Assert(Math.Abs(rotated.Width - 34) < .001f && Math.Abs(rotated.Height - 14) < .001f,
                "Rotated mask bounds ignored the authored geometry.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Showcase.F5CompilerAcceptsEveryEvent", () =>
        {
            ProjectSession p = Require(project);
            ProjectRunLauncher.CompileOutcome result = ProjectRunLauncher.CompileScripts(p.RootPath, Path.Combine(p.RootPath, "Build", "RegressionPlayer"));
            Assert(result.Success, result.ErrorMessage ?? "F5 compile gate failed.");
            string previous = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ProjectPath = p.RootPath;
                foreach (string file in Directory.GetFiles(Path.Combine(p.AssetsPath, "Objects"), "*.pgsl", SearchOption.AllDirectories))
                    Assert(VMEngine.Compile(File.ReadAllText(file)) is not null, "VM compilation produced nothing for " + file);
            }
            finally { PgslCommands.ProjectPath = previous; }
        });

        HeadlessHarness.RunCase(ctx.Report, "Tiles.AuthoredSoliditySweepsAndMutation", () =>
        {
            ProjectSession p = Require(project);
            RoomAsset room = LoadRoom(p);
            RoomTileCollisionMap map = new(room, p.RootPath);
            Assert(map.SolidCount > 450, "The level lost its solid tile selection.");
            Assert(map.Intersects(new RectangleF(105, 255, 14, 34)), "A one-pixel ground probe did not hit.");
            Assert(!map.Intersects(new RectangleF(105, 254, 14, 34)), "Standing flush on ground counts as penetration.");
            Assert(Math.Abs(map.Sweep(new RectangleF(105, 66, 14, 34), 1000, false) - 188) < .001f,
                "High-speed falling tunneled through row nine.");
            Assert(!map.Intersects(new RectangleF(31 * 32 + 5, 300, 14, 34)), "The first pit has invisible ground.");
            IGameContext previousGame = PgslCommands.ActiveGameContext;
            string previousProject = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ActiveGameContext = new NullGameContext { Room = room, ProjectPath = p.RootPath };
                PgslCommands.ProjectPath = p.RootPath;
                RoomNode blocks = room.Nodes.Single(node => node.Name == "Blocks");
                RoomTileCell coinBlock = blocks.TileLayer.Cells.First(cell => cell.TileY == 0 && cell.TileX == 3);
                double x = coinBlock.X * 32 + 16, y = coinBlock.Y * 32 + 16;
                Assert(PgslCommands.TileIndexAt(x, y, "Blocks") == 3, "Question block query failed.");
                Assert(PgslCommands.TileSetAt(x, y, 4, 0, "Blocks") && PgslCommands.TileIndexAt(x, y, "Blocks") == 4,
                    "Used-block replacement failed.");
                Assert(PgslCommands.TileRemoveAt(x, y, "Blocks") && PgslCommands.TileIndexAt(x, y, "Blocks") == -1,
                    "Live tile removal failed.");
                Assert(!PgslCommands.TileSetAt(x, y, -1, 0, "Blocks"), "Negative tile coordinates were accepted.");
                Assert(PgslCommands.TileIndexAt(double.NaN, 0) == -1, "Nonfinite query should miss safely.");
                Assert(LoadRoom(p).Nodes.Single(node => node.Name == "Blocks").TileLayer.Cells.Any(cell => cell.X == coinBlock.X && cell.Y == coinBlock.Y && cell.TileX == 3),
                    "Runtime tile edits modified the saved authoring room.");
            }
            finally { PgslCommands.ActiveGameContext = previousGame; PgslCommands.ProjectPath = previousProject; }
        });

        HeadlessHarness.RunCase(ctx.Report, "Tiles.DisabledParentAndEmptySolidSelection", () =>
        {
            RoomAsset room = RoomAsset.Create("Collision fixture", RoomDimension.TwoD);
            RoomNode parentNode = new() { Name = "Parent", Kind = RoomNodeKind.GameObject, LayerId = room.Layers[0].Id, Enabled = false };
            RoomNode tile = new()
            {
                Name = "Tiles", Kind = RoomNodeKind.TileLayer, LayerId = room.Layers[0].Id, ParentId = parentNode.Id,
                TileLayer = new RoomTileLayerData { CellWidth = 32, CellHeight = 32, CollisionEnabled = true, Tileset = "test.image.json", Cells = [new RoomTileCell { X = 2, Y = 3, TileX = 0, TileY = 0 }] },
            };
            room.Nodes.Add(parentNode); room.Nodes.Add(tile);
            SpriteRuntimeAsset image = new() { Canvas = new SpriteRuntimeCanvas { Width = 64, Height = 32 } };
            image.Usage.Tileset.Collision.Add(0);
            Assert(new RoomTileCollisionMap(room, "", _ => image).SolidCount == 0, "Disabled parent still collides.");
            parentNode.Enabled = true;
            Assert(new RoomTileCollisionMap(room, "", _ => image).SolidCount == 1, "Enabled parent lost its child collision.");
            image.Usage.Tileset.Collision.Clear();
            Assert(new RoomTileCollisionMap(room, "", _ => image).SolidCount == 0, "Empty authored selection treated every tile as solid.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Instances.PositiveIdsAndNamedMaskQueriesWithoutGrid", () =>
        {
            ProjectSession p = Require(project);
            using EcsWorld world = new(1);
            Entity entity = world.CreateEntity();
            Assert(entity.Id > 0, "A real entity was assigned PGSL's no-instance sentinel.");
            world.Set(entity, new TransformComponent { X = 100, Y = 288, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            ObjectDrawAssetRegistry.Set(entity, new ObjectDrawAssetEntry { Prefab = "Assets/Objects/Explorer.object.json", Image = "Assets/Sprites/Explorer Idle.image.json", Is3D = false });
            IGameContext previousGame = PgslCommands.ActiveGameContext;
            string previousProject = PgslCommands.ProjectPath;
            PgslContext previousContext = PgslCommands.BindContext(new PgslContext { InstanceId = -1, SpriteIndex = "Assets/Sprites/Explorer Idle.image.json" });
            try
            {
                PgslCommands.ActiveGameContext = new NullGameContext { World = world, ProjectPath = p.RootPath };
                PgslCommands.ProjectPath = p.RootPath;
                Assert(PgslCommands.CollisionRectangle(94, 255, 106, 287, "Explorer") == entity.Id, "Named overlap needs an unpopulated spatial grid.");
                Assert(PgslCommands.CollisionRectangle(94, 255, 106, 287, "Acorn Walker") == 0, "Object-name filter was ignored.");
                Assert(PgslCommands.CollisionRectangle(80, 254, 92, 287, "Explorer") == 0, "Collision used a generic 32px box, not the narrow mask.");
                Assert(PgslCommands.PlaceMeeting(100, 288, "all"), "PlaceMeeting did not resolve the caller's mask.");
                PgslCommands.RoomValueSet("no-room", 1);
                Assert(PgslCommands.RoomValueGet("no-room", 9) == 9, "No-room values leaked globally.");
                world.DestroyEntity(entity); world.FlushDeferred();
                Assert(PgslCommands.CollisionRectangle(94, 255, 106, 287, "Explorer") == 0, "Destroyed instance is still queryable.");
            }
            finally
            {
                ObjectDrawAssetRegistry.Remove(entity);
                PgslCommands.BindContext(previousContext);
                PgslCommands.ActiveGameContext = previousGame; PgslCommands.ProjectPath = previousProject;
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Display.WorldWindowAndQueuedViewportClip", () =>
        {
            RoomAsset room = LoadRoom(Require(project));
            Assert(room.Settings.Width == 5120 && RoomDisplayLayout.WindowSize(room) == new Size(1280, 720), "World size was used as OS window size.");
            Rectangle port = RoomDisplayLayout.Port(room, room.Viewports[0], 1280, 800);
            Assert(port == new Rectangle(0, 40, 1280, 720), "Resize did not preserve the 16:9 view with letterboxing.");
            RecordingSink sink = new();
            ViewportSpriteSink viewport = new(sink, port);
            viewport.DrawSprite(new SpriteDrawCall { X = 10, Y = 20, Width = 32, Height = 32 });
            Assert(sink.Calls.Count == 1 && sink.Calls[0].X == 10 && sink.Calls[0].Y == 60
                && sink.Calls[0].ClipRect == new Vector4(0, 40, 1280, 720), "Queued sprite lost viewport offset/clip.");
            viewport.DrawSprite(new SpriteDrawCall { ClipRect = new Vector4(1400, 0, 32, 32) });
            Assert(sink.Calls.Count == 1, "An empty intersection was submitted.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Showcase.RealRoomCreateTitleStartMovementAndPause", () =>
        {
            ProjectSession p = Require(project);
            RoomAsset room = LoadRoom(p);
            using RuntimeScene scene = new("Showcase PGSL regression") { Input = new InputState() };
            ProjectGameContext game = new(p.RootPath, scene, null, null, room, null);
            ScriptHostSystem host = new(); host.SetContext(game);
            int diagnostics = 0;
            host.DiagnosticReported += _ => diagnostics++;
            IGameContext previousGame = PgslCommands.ActiveGameContext;
            string previousProject = PgslCommands.ProjectPath;
            try
            {
                PgslCommands.ActiveGameContext = game; PgslCommands.ProjectPath = p.RootPath;
                ScriptAssetRegistry.ClearCache(); ScriptAssetRegistry.LoadFromProject(p.RootPath);
                RoomBuildResult built = new RoomSceneBuilder(p.RootPath, host).Build(scene.World, room);
                Assert(built.SpawnedEntities.Count == 47 && scene.World.LivingEntityCount == 47,
                    "Expected 47 authored objects, not hundreds of invisible tile instances.");
                Entity hero = built.EntitiesByNodeId[room.Nodes.Single(node => node.Name == "Explorer").Id];
                Entity acorn = built.EntitiesByNodeId[room.Nodes.First(node => node.Name.StartsWith("Acorn Walker", StringComparison.Ordinal)).Id];
                Assert(scene.World.Has<SpriteComponent>(hero) && scene.World.Has<SpriteComponent>(acorn),
                    "Prefab-level sprites were not materialised as runtime SpriteComponents.");
                Assert(ObjectDrawAssetRegistry.TryGet(acorn, out ObjectDrawAssetEntry acornAssets)
                    && acornAssets.Image.EndsWith("Acorn Walker.image.json", StringComparison.OrdinalIgnoreCase),
                    "Acorn root sprite was not retained as its authoritative runtime image.");
                float initial = scene.World.GetRef<TransformComponent>(hero).X;
                void Frame() { scene.GameTime.Advance(1f / 60f); host.Update(1f / 60f); scene.World.FlushDeferred(); scene.Input.NextFrame(); }
                scene.Input.OnKeyDown(Key.D); for (int i = 0; i < 20; i++) Frame();
                Assert(scene.World.GetRef<TransformComponent>(hero).X == initial, "Title screen did not freeze movement.");
                Assert(diagnostics == 0, "Normal top-level PGSL return was reported as a runtime failure.");
                scene.Input.OnKeyDown(Key.Enter); Frame(); scene.Input.OnKeyUp(Key.Enter);
                for (int i = 0; i < 15; i++) Frame();
                Assert(scene.World.GetRef<TransformComponent>(hero).X > initial + 20, "Editable PGSL controller did not move after Enter.");
                ref SpriteComponent heroSprite = ref scene.World.GetRef<SpriteComponent>(hero);
                Assert(heroSprite.ImageSpeed > 0f, "Moving hero did not publish live animation speed.");
                Assert(ObjectDrawAssetRegistry.TryGet(hero, out ObjectDrawAssetEntry heroAssets)
                    && heroAssets.Image.EndsWith("Explorer Run.image.json", StringComparison.OrdinalIgnoreCase),
                    "Moving hero did not switch to the authored run sprite.");
                Assert(Math.Abs(scene.World.GetRef<TransformComponent>(hero).Y - 288) < .01, "Player fell through authored ground.");
                scene.Input.OnKeyDown(Key.P); Frame(); scene.Input.OnKeyUp(Key.P);
                float paused = scene.World.GetRef<TransformComponent>(hero).X;
                for (int i = 0; i < 15; i++) Frame();
                Assert(scene.World.GetRef<TransformComponent>(hero).X == paused && PgslCommands.RoomValueGet("frozen") == 1,
                    "Pause did not freeze the game state.");
            }
            finally
            {
                PgslCommands.ActiveGameContext = previousGame; PgslCommands.ProjectPath = previousProject;
                ObjectDrawAssetRegistry.Clear(); ScriptAssetRegistry.ClearCache();
            }
        });
    }

    private static ProjectSession Require(ProjectSession? project) => project ?? throw new InvalidOperationException("Template creation failed; dependent case cannot run.");
    private static RoomAsset LoadRoom(ProjectSession project) => RoomAssetLoader.Parse(Path.Combine(project.RootPath, TwoDShowcaseTemplate.StartRoom.Replace('/', Path.DirectorySeparatorChar)));
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
    private static void Denied(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("An invalid resource operation was accepted.");
    }
    private sealed class RecordingSink : IRenderCommandSink
    {
        public List<SpriteDrawCall> Calls { get; } = [];
        public void DrawSprite(in SpriteDrawCall call) => Calls.Add(call);
        public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls) { foreach (SpriteDrawCall call in calls) Calls.Add(call); }
        public void DrawMesh(in MeshDrawCall call) { }
        public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls) { }
    }
}
