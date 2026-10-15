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
            Assert(ResourceNames.Resolve(project.RootPath, project.Manifest.StartRoom)
                == Path.Combine(project.RootPath, TwoDShowcaseTemplate.StartRoom.Replace('/', Path.DirectorySeparatorChar)), "Start room was not installed.");
            Assert(ResourceNames.Name(project.RootPath, project.Manifest.StartRoom, ResourceType.Room) == "Intro",
                "The full game must start with the Intro cinematic.");
            foreach (string room in new[] { "Main Menu", "Overworld", "Hearthollow", TwoDShowcaseTemplate.LevelOneRoomName, "Credits" })
                Assert(ResourceNames.Resolve(project.RootPath, room, ResourceType.Room).Length > 0, "The game is missing its " + room + " room.");
            Assert(ResourceNames.Resolve(project.RootPath, TwoDShowcaseTemplate.LevelOneRoom)
                == Path.Combine(project.RootPath, TwoDShowcaseTemplate.LevelOneRoom.Replace('/', Path.DirectorySeparatorChar)), "Level 1-1 was not installed.");
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

        HeadlessHarness.RunCase(ctx.Report, "Tiles.TurnedBodyStaysOnTileFloorWithGravity", () =>
        {
            // The Mushroom Meadow cavern boss wobbled its angle by up to 3 degrees each step under gravity. The
            // turned mask's box poked a few pixels into the floor, the sweep ignored tiles it already overlapped,
            // and the boss fell through the floor and out of the room.
            RoomTileCollisionMap map = TileFixture(FloorCells());
            Assert(map.SolidCount == 40 && map.Solids[0] == new RectangleF(0, 288, 32, 32), "The floor fixture is not two rows of 32 px tiles at y 288.");
            SpriteRuntimeAsset boss = MaskSprite(128, 96, .5, 1, 4, 16, 120, 80);   // foot origin, 120 x 80 mask
            SpriteRuntimeAsset crate = MaskSprite(64, 64, .5, .5, 0, 0, 64, 64);    // centred 64 x 64 mask
            foreach ((string name, SpriteRuntimeAsset sprite, float x, float rest) in new[] { ("boss", boss, 200f, 288f), ("crate", crate, 336f, 256f) })
            {
                float lowest = Wobble(sprite, x, rest, 600, (box, dy) => map.Sweep(box, dy, false));
                Assert(Math.Abs(lowest - rest) < .01f, $"The wobbling {name} sank into the tile floor: y {rest} reached {lowest}.");
                float before = Wobble(sprite, x, rest, 600, (box, dy) => SweepBefore(map, box, dy, false));
                Assert(before > rest + 32, $"The fixture no longer shows the old fall for the {name} (it reached only {before}).");
            }
            RectangleF poking = SpriteCollisionBounds.Resolve(boss, 0, 200, 288, 1, 1, 3);
            Assert(poking.Bottom > 290 && poking.Bottom < 292, "The 3 degree boss mask no longer pokes about 3 px into the floor.");
            Assert(map.Sweep(poking, 6, false) == 0, "A body poking into the floor was pushed further in.");
            Assert(map.Sweep(poking, -6, false) == -6, "A body poking into the floor could not jump out of it.");
            Assert(map.Sweep(poking, 200, true) == 200 && map.Sweep(poking, -100, true) == -100,
                "A body poking into the floor caught on the seams between floor tiles.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Tiles.OverlappingBodyMovesOutButNotDeeper", () =>
        {
            // Floor at y 288, a wall column at x 384-416 (y 160-288) standing on it, a lone block at x 128-160, y 160-192.
            List<(int X, int Y)> cells = FloorCells().ToList();
            for (int row = 5; row <= 8; row++) cells.Add((12, row));
            cells.Add((4, 5));
            RoomTileCollisionMap map = TileFixture(cells);
            RectangleF intoBlock = new(120, 165, 14, 20);   // 6 px into the block's left side
            Assert(map.Sweep(intoBlock, -10, true) == -10, "A body placed overlapping a block could not move back out.");
            Assert(map.Sweep(intoBlock, 10, true) == 0, "A body overlapping a block's side was pushed further into it.");
            Assert(map.Sweep(intoBlock, 5, false) == 5 && map.Sweep(intoBlock, -5, false) == -5,
                "A body overlapping a block's side could not move along that side.");
            RectangleF insideBlock = new(135, 170, 10, 10);
            Assert(map.Sweep(insideBlock, -40, true) == -40 && map.Sweep(insideBlock, 40, true) == 40
                && map.Sweep(insideBlock, -40, false) == -40, "A body placed inside a block could not leave it.");
            RectangleF intoWall = new(371, 170, 14, 30);    // 1 px into the wall, across the seam at y 192
            Assert(map.Sweep(intoWall, 10, true) == 0 && map.Sweep(intoWall, -10, true) == -10,
                "A body pressed into a wall was pushed through it, or could not step back.");
            Assert(map.Sweep(intoWall, 60, false) == 60 && map.Sweep(intoWall, -6, false) == -6,
                "A body pressed into a wall caught on the seams of the wall's tiles.");
            Assert(Math.Abs(map.Sweep(intoWall, 200, false) - 88) < .001f, "A body sliding down a wall did not land on the floor.");
            RectangleF onFloorAtWall = new(300, 252, 40, 38);   // 2 px into the floor, 44 px short of the wall
            Assert(Math.Abs(map.Sweep(onFloorAtWall, 100, true) - 44) < .001f,
                "A body poking into the floor walked through the wall standing on it, or stopped short at a seam.");
            RectangleF underBlock = new(130, 186, 14, 30);   // head 6 px up into the block from below
            Assert(map.Sweep(underBlock, -5, false) == 0 && map.Sweep(underBlock, 5, false) == 5,
                "A head pushed into a block from below went further up, or could not drop away.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Tiles.PlainSweepsUnchangedAndPassableTilesStayPassable", () =>
        {
            // Bodies that overlap no tile get exactly the old sweep: resting flush, under platforms, against
            // walls, beside a 45 degree tile (still its conservative box, not a slope) and moved or resized tiles.
            List<(int X, int Y)> cells = FloorCells().ToList();
            for (int row = 5; row <= 8; row++) cells.Add((12, row));
            cells.Add((4, 5)); cells.Add((14, 6)); cells.Add((15, 6)); cells.Add((16, 6));
            RoomAsset room = TileRoom(cells);
            RoomTileLayerData layer = room.Nodes.Single(node => node.Name == "Tiles").TileLayer;
            layer.Cells.Add(new RoomTileCell { X = 2, Y = 5, Rotation = 45 });
            layer.Cells.Add(new RoomTileCell { X = 7, Y = 4, OffsetX = 5, OffsetY = -3 });
            layer.Cells.Add(new RoomTileCell { X = 9, Y = 3, ScaleX = 1.5f });
            // A decorative layer without collision: its tiles must stay passable.
            room.Nodes.Add(new RoomNode
            {
                Name = "Decor", Kind = RoomNodeKind.TileLayer, LayerId = room.Layers[0].Id,
                TileLayer = new RoomTileLayerData { CollisionEnabled = false, Cells = [new RoomTileCell { X = 6, Y = 8 }, new RoomTileCell { X = 6, Y = 7 }] },
            });
            RoomTileCollisionMap map = new(room, "", _ => null);
            Assert(map.SolidCount == cells.Count + 3, "The decorative layer added collision, or a moved tile lost it.");
            RectangleF diamond = map.Solids[^3];
            Assert(Math.Abs(diamond.Width - 32 * MathF.Sqrt(2)) < .01f, "A rotated tile is no longer its conservative box.");
            RectangleF falling = new(diamond.X + 8, diamond.Top - 60, 14, 34);
            Assert(Math.Abs(map.Sweep(falling, 100, false) - (diamond.Top - falling.Bottom)) < .001f,
                "A body did not land flush on a rotated tile's box.");
            RectangleF inDecor = new(196, 230, 14, 34);
            Assert(!map.Intersects(inDecor) && map.Sweep(inDecor, 30, true) == 30 && map.Sweep(inDecor, -30, true) == -30,
                "A body in non-colliding tiles was blocked.");
            Random random = new(1015);
            int compared = 0;
            for (int i = 0; i < 4000; i++)
            {
                float width = 2 + (float)random.NextDouble() * 88, height = 2 + (float)random.NextDouble() * 88;
                RectangleF body = new(-20 + (float)random.NextDouble() * 680, 60 + (float)random.NextDouble() * 320, width, height);
                RectangleF solid = map.Solids[random.Next(map.SolidCount)];
                switch (random.Next(4))
                {
                    case 0: body.Y = solid.Top - height; break;       // resting on it
                    case 1: body.Y = solid.Bottom; break;             // under it
                    case 2: body.X = solid.Left - width; break;       // against its left side
                }
                if (map.Intersects(body)) continue;
                foreach (float amount in new[] { (float)(random.NextDouble() * 600 - 300), (float)(random.NextDouble() * 6 - 3) })
                    foreach (bool horizontal in new[] { false, true })
                        Assert(map.Sweep(body, amount, horizontal) == SweepBefore(map, body, amount, horizontal),
                            $"A plain sweep changed: {body} by {amount} {(horizontal ? "across" : "down")}.");
                compared++;
            }
            Assert(compared > 1500, "Too few non-overlapping bodies were compared: " + compared);
        });

        HeadlessHarness.RunCase(ctx.Report, "Instances.TurnedMaskBoundsAreTheTurnedShapes", () =>
        {
            SpriteRuntimeAsset ball = MaskSprite(32, 32, .5, .5, 0, 0, 0, 0);
            ball.CollisionShapes[0] = new SpriteRuntimeCollisionShape { Position = P(16, 16), Radius = 10 };
            Assert(SpriteCollisionBounds.Resolve(ball, 0, 100, 50) == new RectangleF(90, 40, 20, 20), "An unturned circle mask moved.");
            foreach (float angle in new[] { 3f, 30f, 45f, 90f, -170f })
            {
                RectangleF bounds = SpriteCollisionBounds.Resolve(ball, 0, 100, 50, 1, 1, angle);
                Assert(Near(bounds, new RectangleF(90, 40, 20, 20)), $"A circle mask turned {angle} degrees changed size: {bounds}.");
            }
            RectangleF oval = SpriteCollisionBounds.Resolve(ball, 0, 100, 50, 2, 1, 90);
            Assert(Near(oval, new RectangleF(90, 30, 20, 40)), "An unevenly scaled circle turned 90 degrees is not its turned ellipse: " + oval);
            SpriteRuntimeAsset diamond = MaskSprite(32, 32, .5, .5, 0, 0, 0, 0);
            diamond.CollisionShapes[0] = new SpriteRuntimeCollisionShape { Points = [P(16, 0), P(32, 16), P(16, 32), P(0, 16)] };
            float half = 16 * MathF.Sqrt(.5f);
            Assert(Near(SpriteCollisionBounds.Resolve(diamond, 0, 0, 0, 1, 1, 45), RectangleF.FromLTRB(-half, -half, half, half)),
                "A polygon mask turned 45 degrees is not its turned points' bounds.");
            // An L of two rectangles turned 45 degrees: each rectangle turned, not their joint bounding box.
            SpriteRuntimeAsset ell = MaskSprite(32, 32, 0, 0, 0, 0, 32, 4);
            ell.CollisionShapes.Add(new SpriteRuntimeCollisionShape { Position = P(0, 0), Size = P(4, 32) });
            float root2 = MathF.Sqrt(2);
            Assert(Near(SpriteCollisionBounds.Resolve(ell, 0, 0, 0, 1, 1, 45), RectangleF.FromLTRB(-16 * root2, 0, 16 * root2, 18 * root2)),
                "A mask of two rectangles turned 45 degrees is not its turned rectangles' bounds.");
            Assert(SpriteCollisionBounds.Resolve(ell, 0, 10, 20, -1, 2) == RectangleF.FromLTRB(-22, 20, 10, 84),
                "An unturned, mirrored and scaled mask of two rectangles changed.");
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

        HeadlessHarness.RunCase(ctx.Report, "Tiles.TileMoveKeepsAWobblingInstanceOnTheFloor", () =>
        {
            // The script-side repro: image_angle wobbles 3 degrees each step, gravity through TileMoveY and a slow
            // walk through TileMoveX, with the shipped hero's foot-origin mask on a floor of tiles.
            ProjectSession p = Require(project);
            RoomAsset room = TileRoom(FloorCells());
            IGameContext previousGame = PgslCommands.ActiveGameContext;
            string previousProject = PgslCommands.ProjectPath;
            PgslContext instance = new() { InstanceId = -1, SpriteIndex = "Assets/Sprites/Explorer Idle.image.json" };
            PgslContext previousContext = PgslCommands.BindContext(instance);
            try
            {
                PgslCommands.ActiveGameContext = new NullGameContext { Room = room, ProjectPath = p.RootPath };
                PgslCommands.ProjectPath = p.RootPath;
                double x = 100, y = 288, vy = 0, dt = 1.0 / 60;
                for (int frame = 0; frame < 600; frame++)
                {
                    instance.ImageAngle = 3 * Math.Sin(frame * 36.7 * Math.PI / 180);
                    vy = Math.Min(vy + 1500 * dt, 900);
                    double wanted = y + vy * dt;
                    y = PgslCommands.TileMoveY(x, y, vy * dt);
                    if (Math.Abs(y - wanted) > .001) vy = 0;
                    x = PgslCommands.TileMoveX(x, y, 30 * dt);
                    Assert(y <= 288.01, $"TileMoveY let a wobbling instance sink to y {y:0.##} on frame {frame}.");
                }
                Assert(Math.Abs(y - 288) < .01 && PgslCommands.TileMeeting(x, y + 1), "The wobbling instance left the floor.");
                Assert(Math.Abs(x - 400) < .01, $"Walking while wobbling caught on the floor's tile seams (x {x:0.##}, not 400).");
            }
            finally
            {
                PgslCommands.BindContext(previousContext);
                PgslCommands.ActiveGameContext = previousGame; PgslCommands.ProjectPath = previousProject;
            }
        });

        HeadlessHarness.RunCase(ctx.Report, "Display.WorldWindowAndQueuedViewportClip", () =>
        {
            RoomAsset room = LoadRoom(Require(project));
            Assert(room.Settings.Width == 8512 && RoomDisplayLayout.WindowSize(room) == new Size(1280, 720), "World size was used as OS window size.");
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
                PgslCommands.ResetSession(); // standalone (F5) rules: no campaign globals
                ScriptAssetRegistry.ClearCache(); ScriptAssetRegistry.LoadFromProject(p.RootPath);
                RoomBuildResult built = new RoomSceneBuilder(p.RootPath, host).Build(scene.World, room);
                int authored = room.Nodes.Count(node => node.Kind == RoomNodeKind.GameObject);
                // Level Rules may add one script-spawned entity: the ambient petal particle emitter.
                Assert(authored == 66 && built.SpawnedEntities.Count == authored
                    && scene.World.LivingEntityCount >= authored && scene.World.LivingEntityCount <= authored + 1,
                    $"Expected 66 authored objects, not hundreds of invisible tile instances; spawned {built.SpawnedEntities.Count}, living {scene.World.LivingEntityCount}.");
                Entity hero = built.EntitiesByNodeId[room.Nodes.Single(node => node.Name == "Explorer").Id];
                Entity acorn = built.EntitiesByNodeId[room.Nodes.First(node => node.Name == "Acorn 1").Id];
                Assert(scene.World.Has<SpriteComponent>(hero) && scene.World.Has<SpriteComponent>(acorn),
                    "Prefab-level sprites were not materialised as runtime SpriteComponents.");
                Assert(ObjectDrawAssetRegistry.TryGet(acorn, out ObjectDrawAssetEntry acornAssets)
                    && ResourceNames.Resolve(p.RootPath, acornAssets.Image) == Path.Combine(p.AssetsPath, "Sprites", "Acorn Walker Sprite.image.json"),
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
                    && ResourceNames.Resolve(p.RootPath, heroAssets.Image) == Path.Combine(p.AssetsPath, "Sprites", "Explorer Run.image.json"),
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
        MushroomMeadowAcceptanceSuite.Run(ctx, Require(project));
    }

    private static ProjectSession Require(ProjectSession? project) => project ?? throw new InvalidOperationException("Template creation failed; dependent case cannot run.");
    private static RoomAsset LoadRoom(ProjectSession project) => RoomAssetLoader.Parse(Path.Combine(project.RootPath, TwoDShowcaseTemplate.LevelOneRoom.Replace('/', Path.DirectorySeparatorChar)));
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    /// <summary>A room with one colliding layer of plain 32 px tiles (no tileset, so every painted cell is solid).</summary>
    private static RoomAsset TileRoom(IEnumerable<(int X, int Y)> cells)
    {
        RoomAsset room = RoomAsset.Create("Tile collision fixture", RoomDimension.TwoD);
        room.Nodes.Add(new RoomNode
        {
            Name = "Tiles", Kind = RoomNodeKind.TileLayer, LayerId = room.Layers[0].Id,
            TileLayer = new RoomTileLayerData { CollisionEnabled = true, Cells = cells.Select(cell => new RoomTileCell { X = cell.X, Y = cell.Y }).ToList() },
        });
        return room;
    }

    private static RoomTileCollisionMap TileFixture(IEnumerable<(int X, int Y)> cells) => new(TileRoom(cells), "", _ => null);

    /// <summary>A floor two tiles thick from x 0 to 640, its top at y 288.</summary>
    private static IEnumerable<(int X, int Y)> FloorCells() => Enumerable.Range(0, 20).SelectMany(x => new[] { (x, 9), (x, 10) });

    private static SpriteRuntimeAsset MaskSprite(int width, int height, double originX, double originY,
        float maskX, float maskY, float maskWidth, float maskHeight) => new()
    {
        Canvas = new SpriteRuntimeCanvas { Width = width, Height = height },
        Origin = new SpriteRuntimeOrigin { X = originX, Y = originY },
        CollisionShapes = [new SpriteRuntimeCollisionShape { Position = P(maskX, maskY), Size = P(maskWidth, maskHeight) }],
    };

    private static SpriteRuntimePoint P(double x, double y) => new() { X = x, Y = y };

    private static bool Near(RectangleF a, RectangleF b) => Math.Abs(a.Left - b.Left) < .001f && Math.Abs(a.Top - b.Top) < .001f
        && Math.Abs(a.Right - b.Right) < .001f && Math.Abs(a.Bottom - b.Bottom) < .001f;

    /// <summary>Steps a body whose angle wobbles up to 3 degrees under gravity; returns the lowest y it reached.</summary>
    private static float Wobble(SpriteRuntimeAsset sprite, float x, float y, int frames, Func<RectangleF, float, float> sweep)
    {
        float vy = 0, lowest = y;
        for (int frame = 0; frame < frames; frame++)
        {
            float angle = 3 * MathF.Sin(frame * 36.7f * MathF.PI / 180);
            vy = MathF.Min(vy + 1500f / 60, 900);
            float wanted = vy / 60, moved = sweep(SpriteCollisionBounds.Resolve(sprite, 0, x, y, 1, 1, angle), wanted);
            y += moved;
            if (MathF.Abs(moved - wanted) > .001f) vy = 0;
            lowest = MathF.Max(lowest, y);
        }
        return lowest;
    }

    /// <summary>The tile sweep before 15 October 2026, which ignored tiles a body already overlapped.</summary>
    private static float SweepBefore(RoomTileCollisionMap map, RectangleF body, float amount, bool horizontal)
    {
        float permitted = amount;
        foreach (RectangleF solid in map.Solids)
        {
            bool overlap = horizontal ? body.Bottom > solid.Top + .0001f && body.Top < solid.Bottom - .0001f
                : body.Right > solid.Left + .0001f && body.Left < solid.Right - .0001f;
            if (!overlap) continue;
            float lead = horizontal ? body.Right : body.Bottom, trail = horizontal ? body.Left : body.Top;
            float near = horizontal ? solid.Left : solid.Top, far = horizontal ? solid.Right : solid.Bottom;
            if (amount > 0 && lead <= near + .0001f) permitted = MathF.Min(permitted, MathF.Max(0, near - lead));
            if (amount < 0 && trail >= far - .0001f) permitted = MathF.Max(permitted, MathF.Min(0, far - trail));
        }
        return permitted;
    }
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
