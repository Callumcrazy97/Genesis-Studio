using System.Drawing;
using System.Globalization;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Imaging;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>Exercises the shipped PGSL game and production room switcher. Scenario positioning
/// tests collision branches; the final traversal uses input only, with no position or tuning edits.</summary>
internal static class MushroomMeadowAcceptanceSuite
{
    public static void Run(HeadlessContext context, ProjectSession project)
    {
        HeadlessHarness.RunCase(context.Report, "Acceptance.MushroomMeadow.CompleteGame", () =>
        {
            using MeadowGame game = new(project);
            byte[] authoredRoom = File.ReadAllBytes(game.RoomFile);
            HeadlessHarness.Step("title, movement, jump and pause operate on the authored game", () =>
            {
                game.Hold(Key.D, true); game.Frames(12);
                Check(game.Position.X == 112 && game.Value("state") == 0, "Title did not freeze input.");
                game.Tap(Key.Enter); game.Frames(20);
                Check(game.Position.X > 140, "Start did not enable movement.");
                game.Hold(Key.D, false); game.Hold(Key.Space, true); game.Frames(8);
                Check(game.Position.Y < 260, "Jump did not lift the player off the solid tiles.");
                game.Hold(Key.Space, false);
                game.Tap(Key.P);
                var paused = game.Position;
                double clock = game.Value("timeLeft");
                var actors = game.Host.Instances.Select(behavior => game.Scene.World.GetRef<TransformComponent>(behavior.Entity)).ToArray();
                game.Frames(30);
                Check(game.Position == paused && game.Value("timeLeft") == clock, "Pause advanced player or timer.");
                Check(game.Host.Instances.Select(behavior => game.Scene.World.GetRef<TransformComponent>(behavior.Entity)).SequenceEqual(actors),
                    "Pause advanced an enemy or collectible.");
                game.Tap(Key.P); game.Frames(5);
                Check(game.Value("state") == 1 && game.Value("timeLeft") < clock, "Resume did not restart simulation.");
            });
            HeadlessHarness.Step("coin and question block rewards consume only live state", () =>
            {
                Entity coin = game.Actor("Sun Coin");
                var position = game.Scene.World.GetRef<TransformComponent>(coin);
                game.Place(position.X, position.Y); game.Frame();
                Check(game.Value("coins") == 1 && !game.Host.Instances.Any(behavior => behavior.Entity == coin),
                    "Coin pickup did not remove exactly the collided collectible.");
                RoomNode blocks = game.Game.Room.Nodes.Single(node => node.Name == "Blocks");
                RoomTileCell block = blocks.TileLayer.Cells.First(cell => cell.TileX == 3 && cell.TileY == 0);
                float x = block.X * 32 + 16, y = (block.Y + 1) * 32 + 35;
                game.Place(x, y); game.Set("vy", -220); game.Frame();
                Check(game.Value("coins") == 2 && PgslCommands.TileIndexAt(x, block.Y * 32 + 16, "Blocks") == 4,
                    "The question block did not pay one reward and become used.");
                game.Place(x, y); game.Set("vy", -220); game.Frame();
                Check(game.Value("coins") == 2, "A used question block paid twice.");
            });
            HeadlessHarness.Step("hearts, damage, checkpoint respawn, timeout and game over are complete", () =>
            {
                Check(game.Value("hearts") == 3 && game.Value("maxHearts") == 3 && game.Value("lives") == 5 && game.Value("campaign") == 0,
                    "A standalone run did not start with 3 of 3 hearts and 5 lives.");
                Entity acorn = game.Actor("Acorn Walker");
                var enemy = game.Scene.World.GetRef<TransformComponent>(acorn);
                game.Place(enemy.X, enemy.Y); game.Set("invulnerable", 0); game.Frame();
                Check(game.Value("hearts") == 2 && game.Value("lives") == 5 && game.Value("invulnerable") > 1,
                    "Side contact did not cost exactly one heart.");
                game.Frames(4);
                Check(game.Value("hearts") == 2, "Invulnerability did not prevent repeated loss.");
                enemy = game.Scene.World.GetRef<TransformComponent>(acorn);
                game.Place(enemy.X, enemy.Y); game.Set("hearts", 1); game.Set("invulnerable", 0); game.Frame();
                Check(game.Value("lives") == 4 && game.Value("hearts") == 3 && Math.Abs(game.Position.X - 112) < .01f,
                    "Losing the last heart did not cost one life and respawn with full hearts.");
                Check(PgslCommands.GlobalGet("lives") == 4, "Lives were not mirrored into the lives global.");
                Entity flag = game.Actor("Checkpoint");
                var checkpoint = game.Scene.World.GetRef<TransformComponent>(flag);
                game.Place(checkpoint.X, checkpoint.Y); game.Frame();
                Check(game.Value("checkpoint") == 1 && PgslCommands.RoomValueGet("checkpoint") == 1, "Checkpoint did not activate.");
                game.Place(checkpoint.X + 100, game.Game.Room.Settings.Height + 100); game.Frame();
                Check(game.Value("lives") == 3 && game.Value("hearts") == 3 && Math.Abs(game.Position.X - (checkpoint.X + 24)) < .01f,
                    "Falling did not cost a life at once and respawn at the saved flag.");
                game.Set("lives", 1); game.Set("timeLeft", .001); game.Frame();
                Check(game.Value("state") == 4 && game.Value("lives") == 0 && PgslCommands.RoomValueGet("frozen") == 1,
                    "Timeout did not complete the game-over transition.");
            });
            HeadlessHarness.Step("restart clears instances, rewards and mutated tiles through the production switcher", () =>
            {
                game.Tap(Key.Enter);
                Check(game.Value("state") == 0 && game.Value("lives") == 5 && game.Value("hearts") == 3 && game.Value("coins") == 0,
                    "Restart retained game-over state or collected rewards.");
                Check(game.CleanRun, $"Restart duplicated or lost actors: living {game.Scene.World.LivingEntityCount}, scripted {game.Host.Instances.Count}, authored {game.Authored}.");
                Check(game.Game.Room.Nodes.Single(node => node.Name == "Blocks").TileLayer.Cells.Any(cell => cell.TileX == 3),
                    "Restart did not restore the authored question blocks.");
                game.Tap(Key.Enter);
                Entity finish = game.Actor("Finish");
                var flag = game.Scene.World.GetRef<TransformComponent>(finish);
                game.Place(flag.X, flag.Y); game.Frame();
                Check(game.Value("state") == 3 && game.Value("score") > 1000 && PgslCommands.RoomValueGet("frozen") == 1,
                    "Finish did not award a time bonus and freeze the win screen.");
                PgslRecordingDrawSurface hud = new();
                game.Host.DispatchPgslGuiDraw(null, null, hud);
                foreach (string label in new[] { "COINS ", "LIVES ", "TIME " })
                    Check(hud.Texts.Any(text => text.Text.StartsWith(label, StringComparison.Ordinal)),
                        "Win HUD omitted " + label + "; actual: " + string.Join(" | ", hud.Texts.Select(text => text.Text)));
                game.Tap(Key.R);
                Check(game.Value("state") == 0 && game.CleanRun, "Win-screen restart did not create a clean run.");
            });
            HeadlessHarness.Step("the full level can be completed using only movement and jump input", () =>
            {
                game.Tap(Key.Enter); game.Hold(Key.D, true); game.Hold(Key.Shift, true);
                RoomTileCollisionMap tiles = new(game.Game.Room, project.RootPath);
                float checkpointX = game.Scene.World.GetRef<TransformComponent>(game.Actor("Checkpoint")).X;
                bool crossingPit = false;
                int frames = 0;
                for (; frames < 10800 && game.Value("state") == 1; frames++)
                {
                    var player = game.Position;
                    bool ground = tiles.Intersects(new RectangleF(player.X - 7, player.Y - 33, 14, 34));
                    bool obstacle = tiles.Intersects(new RectangleF(player.X + 34, player.Y - 33, 14, 32));
                    bool gap = !tiles.Intersects(new RectangleF(player.X + 44, player.Y + 1, 14, 3));
                    bool enemy = game.Host.Instances.Any(behavior => behavior.Entity != game.Hero
                        && ObjectDrawAssetRegistry.TryGet(behavior.Entity, out ObjectDrawAssetEntry asset)
                        && asset.Prefab.Contains("Acorn Walker", StringComparison.OrdinalIgnoreCase)
                        && game.Scene.World.GetRef<TransformComponent>(behavior.Entity).X is float ex
                        && ex > player.X && ex - player.X < 50);
                    bool checkpointStop = game.Value("checkpoint") == 0 && Math.Abs(player.X - checkpointX) < 16;
                    bool pitAhead = !tiles.Intersects(new RectangleF(player.X + 52, 289, 14, 2));
                    if (ground)
                    {
                        bool jump = !checkpointStop && !game.Scene.Input.IsDown(Key.Space) && (obstacle || gap || enemy);
                        crossingPit = jump && pitAhead;
                        game.Hold(Key.Space, jump);
                    }
                    // Land an enemy bounce before a pit, then jump deliberately from its edge.
                    game.Hold(Key.D, !checkpointStop && (!pitAhead || ground || crossingPit));
                    double previousLives = game.Value("lives");
                    game.Frame();
                    if (game.Value("lives") != previousLives)
                        Console.WriteLine($"Traversal damage: frame={frames} from={player} to={game.Position}; lives={game.Value("lives")}");
                    Check(game.Diagnostics.Count == 0, "Traversal script fault: " + game.Host.LastError);
                    Check(game.Scene.World.IsAlive(game.Hero), $"The player entity died without a game-over transition at frame {frames}.");
                }
                Check(game.Value("state") == 3, $"Input-only traversal failed after {frames} frames at {game.Position}; lives={game.Value("lives")}, hearts={game.Value("hearts")}, state={game.Value("state")}, timer={game.Value("timeLeft")}, vx={game.Value("vx")}, right={game.Scene.Input.IsDown(Key.D)}.");
                Check(game.Value("checkpoint") == 1 && game.Value("coins") > 0, "Traversal omitted checkpoint or collectibles.");
            });
            Check(game.Diagnostics.Count == 0, "Game script diagnostics: " + string.Join("; ", game.Diagnostics));
            Check(File.ReadAllBytes(game.RoomFile).SequenceEqual(authoredRoom), "Gameplay or restart modified the saved room.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.MushroomMeadow.PowerUpsHeartsAndRoomRules", () =>
        {
            using MeadowGame game = new(project);
            Check(PgslCommands.RoomValueGet("levelRules") == 1 && PgslCommands.RoomValueGet("levelId") == 1
                && PgslCommands.RoomValueGet("timeLimit") == 300 && PgslCommands.RoomValueGet("hub") == 0,
                "Level Rules did not publish the 1-1 room rules.");
            game.Tap(Key.Enter); game.Frames(2);
            HeadlessHarness.Step("a ? block over a Prize marker sprouts its power-up instead of a coin", () =>
            {
                float x = 37 * 32 + 16, y = 7 * 32 + 35;
                double coins = game.Value("coins");
                Check(game.Count("Prize Shield") == 1 && game.Count("Bark Shield") == 0, "The 1-1 room lost its Prize Shield marker.");
                game.Place(x, y); game.Set("vy", -220); game.Frame();
                Check(PgslCommands.TileIndexAt(x, 6 * 32 + 16, "Blocks") == 4 && game.Value("coins") == coins,
                    "The prize block paid a coin or did not become used.");
                Check(game.Count("Bark Shield") == 1 && game.Count("Prize Shield") == 0, "The Bark Shield did not sprout from its block.");
                game.Place(200, 288); game.Frames(40);
                var item = game.Scene.World.GetRef<TransformComponent>(game.Actor("Bark Shield"));
                Check(item.Y < 6 * 32 - 8, "The sprouted power-up did not rise out of its block.");
                game.Place(item.X, item.Y + 10); game.Frame(); game.Frame();
                Check(game.Value("powerUp") == 3 && PgslCommands.GlobalGet("power") == 3 && game.Count("Bark Shield") == 0
                    && game.Count("Shield Ring") == 1, "Touching the Bark Shield did not give power 3 and its ring.");
            });
            HeadlessHarness.Step("the Bark Shield absorbs one hit, then hits cost hearts and the power-up", () =>
            {
                var enemy = game.Scene.World.GetRef<TransformComponent>(game.Actor("Acorn Walker"));
                game.Place(enemy.X, enemy.Y); game.Set("invulnerable", 0); game.Frame(); game.Frame();
                Check(game.Value("powerUp") == 0 && game.Value("hearts") == 3 && game.Count("Shield Ring") == 0,
                    "The shield did not break instead of a heart.");
                game.Set("powerUp", 2); game.Set("invulnerable", 0);
                enemy = game.Scene.World.GetRef<TransformComponent>(game.Actor("Acorn Walker"));
                game.Place(enemy.X, enemy.Y); game.Frame();
                Check(game.Value("powerUp") == 0 && game.Value("hearts") == 2, "A hit did not cost one heart and the power-up.");
            });
            HeadlessHarness.Step("Spore Puff throws a spore that defeats an enemy by the enemy convention", () =>
            {
                game.Place(60, 288); game.Frames(100);
                Entity acorn = game.Actor("Acorn Walker");
                var enemy = game.Scene.World.GetRef<TransformComponent>(acorn);
                game.Set("powerUp", 2); game.Set("facing", 1); game.Set("invulnerable", 0);
                game.Place(enemy.X - 120, enemy.Y);
                double score = game.Value("score");
                game.Tap(Key.J);
                Check(game.Count("Spore Shot") == 1, "J did not throw exactly one Spore Shot.");
                for (int frame = 0; frame < 60 && game.Scene.World.IsAlive(acorn); frame++) game.Frame();
                Check(!game.Scene.World.IsAlive(acorn) && game.Value("score") >= score + 200 && game.Value("hearts") == 2,
                    "The spore did not knock out the acorn through `hurt` and award its points.");
            });
            HeadlessHarness.Step("a Heart Berry restores one heart up to the maximum", () =>
            {
                var berry = game.Scene.World.GetRef<TransformComponent>(game.Actor("Heart Berry"));
                game.Set("hearts", 1); game.Place(berry.X, berry.Y + 10); game.Frame();
                Check(game.Value("hearts") == 2 && game.Count("Heart Berry") == 0, "The Heart Berry did not restore one heart.");
            });
            HeadlessHarness.Step("Starlit Firefly: invincible, enemies fall on contact, the music swaps", () =>
            {
                float x = 38 * 32 + 16, y = 4 * 32 + 35;
                game.Place(x, y); game.Set("vy", -220); game.Frame();
                Check(game.Count("Starlit Firefly") == 1, "The Prize Star block did not sprout a Starlit Firefly.");
                game.Place(200, 288); game.Frames(40);
                for (int frame = 0; frame < 3 && game.Value("starTimer") <= 0; frame++)
                {
                    var firefly = game.Scene.World.GetRef<TransformComponent>(game.Actor("Starlit Firefly"));
                    game.Place(firefly.X, firefly.Y + 10); game.Frame();
                }
                Check(game.Value("starTimer") > 9 && PgslCommands.RoomValueGet("starlit") == 1, "The firefly did not start Starlit.");
                game.Frame();
                Check(PgslCommands.RoomValueGet("musicChannel") >= 0, "Level Rules published no music channel.");
                Entity foe = game.Actor("Acorn Walker");
                var enemy = game.Scene.World.GetRef<TransformComponent>(foe);
                double hearts = game.Value("hearts");
                game.Place(enemy.X, enemy.Y); game.Set("invulnerable", 0); game.Frames(3);
                Check(game.Value("hearts") == hearts
                    && (!game.Scene.World.IsAlive(foe) || Convert.ToDouble(PgslCommands.InstanceVariableGet(foe.Id, "dead"), CultureInfo.InvariantCulture) == 1),
                    "A Starlit touch did not knock the enemy out harmlessly.");
                game.Set("starTimer", .01); game.Frames(2);
                Check(PgslCommands.RoomValueGet("starlit") == 0, "Starlit did not end.");
            });
            HeadlessHarness.Step("Glide Seed: holding jump while falling glides", () =>
            {
                game.Set("powerUp", 1); game.Place(1500, 60);
                game.Hold(Key.Space, true); game.Frames(30);
                Check(game.Value("gliding") == 1 && game.Value("vy") <= 75, "Holding jump in the air did not glide.");
                game.Hold(Key.Space, false); game.Frames(3);
                Check(game.Value("gliding") == 0, "Releasing jump did not stop gliding.");
            });
            HeadlessHarness.Step("the pause menu restarts the level", () =>
            {
                game.Tap(Key.Escape);
                Check(game.Value("state") == 2 && game.Value("menuIndex") == 0, "Escape did not open the pause menu.");
                game.Tap(Key.S); game.Tap(Key.Enter);
                Check(game.Value("state") == 0 && game.Value("hearts") == 3 && game.Value("powerUp") == 0 && game.CleanRun,
                    "Pause menu Restart level did not restart the standalone level.");
            });
            Check(game.Diagnostics.Count == 0, "Game script diagnostics: " + string.Join("; ", game.Diagnostics));
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.MushroomMeadow.HeroForgivesCornersAndNeverSticks", () =>
        {
            // The Explorer forgiveness rules mirrored by BuildTools/TemplateContent/MushroomMeadow/hero_physics.py.
            using MeadowGame game = new(project);
            game.Tap(Key.Enter); game.Frames(2);
            HeadlessHarness.Step("a head that clips a seed pod's corner slides past it without bumping it", () =>
            {
                // Seed pod at column 10 row 6 (x 320..352): Pip's box overlaps its left edge by 3 px.
                game.Place(316, 7 * 32 + 35); game.Set("vy", -300); game.Frame();
                Check(Math.Abs(game.Position.X - 313) < .01f && game.Position.Y < 7 * 32 + 34 && game.Value("vy") < 0,
                    $"The rising head did not slide past the corner: {game.Position}, vy={game.Value("vy")}.");
                Check(PgslCommands.TileIndexAt(336, 6 * 32 + 16, "Blocks") == 3, "A corner clip bumped the seed pod.");
                game.Place(336, 7 * 32 + 35); game.Set("vy", -300); game.Frame();
                Check(PgslCommands.TileIndexAt(336, 6 * 32 + 16, "Blocks") == 4, "A square head bump no longer opened the seed pod.");
            });
            HeadlessHarness.Step("a ledge clipped by a few pixels is stepped onto instead of stopping Pip", () =>
            {
                // Pipe at columns 50-51 (top y = 224): feet 2 px below its top, moving right into it.
                game.Place(1592, 226); game.Set("vx", 200); game.Frame();
                Check(game.Position.Y <= 224.01f && game.Position.X > 1593,
                    $"Pip did not step up onto the pipe: {game.Position}.");
                game.Frames(3);
                Check(Math.Abs(game.Position.Y - 224) < .01f, $"Pip did not stay standing on the pipe: {game.Position}.");
            });
            HeadlessHarness.Step("Pip embedded in solid ground steps out instead of staying stuck", () =>
            {
                game.Place(200, 300); game.Frame();
                RoomTileCollisionMap tiles = new(game.Game.Room, project.RootPath);
                bool embedded = tiles.Intersects(new RectangleF(game.Position.X - 7, game.Position.Y - 34, 14, 34));
                Check(game.Position.Y <= 288.01f && !embedded && game.Value("grounded") == 1,
                    $"Pip stayed inside the ground: {game.Position}, grounded={game.Value("grounded")}.");
            });
            Check(game.Diagnostics.Count == 0, "Game script diagnostics: " + string.Join("; ", game.Diagnostics));
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.MushroomMeadow.CampaignGlowsporeReturnsToMap", () =>
        {
            using MeadowGame game = new(project, campaign: true);
            game.Frames(40); // the Glowspore grows in over half a second before it can be taken
            Check(game.Value("campaign") == 1 && game.Value("state") == 1 && game.Value("lives") == 4 && game.Value("maxHearts") == 4
                && game.Value("hearts") == 4 && game.Value("coins") == 12 && PgslCommands.RoomValueGet("frozen") == 0,
                "A campaign level did not skip the title and carry the campaign globals.");
            var orb = game.Scene.World.GetRef<TransformComponent>(game.Actor("Glowspore"));
            game.Place(orb.X, orb.Y + 17); game.Frame();
            Check(game.Value("state") == 3 && game.Value("clearKind") == 2 && game.Count("Glowspore") == 0,
                "Touching the Glowspore did not clear the level.");
            Check(game.Diagnostics.Count == 0, "Game script diagnostics: " + string.Join("; ", game.Diagnostics));
            game.Frames(100);
            game.Hold(Key.Enter, true); game.Frame(); game.Hold(Key.Enter, false);
            // The Overworld's Map Marker consumes returnFromLevel and places itself (mapNode) once it loads.
            Check(PgslCommands.GlobalGet("clear1") == 1 && PgslCommands.GlobalGet("glowspores") == 1
                && PgslCommands.GlobalGet("lives") == 4 && PgslCommands.GlobalGet("coins") == 12 && PgslCommands.GlobalGet("maxHearts") == 4,
                $"Leaving a cleared level did not record the Glowspore: clear1={PgslCommands.GlobalGet("clear1")}, glowspores={PgslCommands.GlobalGet("glowspores")}, lives={PgslCommands.GlobalGet("lives")}, coins={PgslCommands.GlobalGet("coins")}.");
            for (int frame = 0; frame < 120 && PgslCommands.RoomGetName() != "Overworld"; frame++) game.Frame();
            Check(PgslCommands.RoomGetName() == "Overworld", "A campaign clear did not return to the Overworld; room is " + PgslCommands.RoomGetName() + ".");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.MushroomMeadow.ImageRigLiveAuthoring", () =>
        {
            string file = Path.Combine(project.AssetsPath, "Sprites", "Checkpoint Sprite.image.json");
            byte[] original = File.ReadAllBytes(file);
            using MeadowGame game = new(project);
            Entity checkpoint = game.Actor("Checkpoint");
            PixelRigSprite initial = game.Scene.World.GetRef<PixelRigSpriteComponent>(checkpoint).Binding
                ?? throw new InvalidOperationException("Title did not bind the saved Image rig.");
            Check(!initial.Player.Playing, "Title did not pause the saved Image rig.");
            game.Tap(Key.Enter); game.Frames(12);
            byte[] first = initial.Player.GetPixels().ToArray();
            game.Frames(12);
            Check(initial.Player.Playing && !first.SequenceEqual(initial.Player.GetPixels().ToArray()),
                "Gameplay code advanced rig metadata without changing the pennant's actual pixels.");
            game.Tap(Key.P);
            int pausedFrame = initial.Player.Frame;
            game.Frames(12);
            Check(initial.Player.Frame == pausedFrame, "Paused gameplay continued rig motion.");
            try
            {
                ImageDocument document = ImageDocumentSerializer.LoadAtomic(file).Document;
                ImageDocumentSession session = new(document, file, ImageDocumentAccess.Editor);
                using ImageEditorControl editor = new(session, ImageWorkspaceStorage.Load(session));
                ImagePixelRig rig = Genesis.Application.Editors.Image.Rigging.PixelRigRasterizer.Copy(document.PixelRigs.Single());
                rig.Poses.Single(pose => pose.Name == "Wind up").Bones.Single(bone => bone.Name == "Pennant").End.Y = 6;
                initial.Player.Seek(7);
                byte[] before = initial.Player.GetPixels().ToArray();
                editor.SavePixelRig(rig); editor.Save(); game.Frame();
                PixelRigSprite changed = game.Scene.World.GetRef<PixelRigSpriteComponent>(checkpoint).Binding!;
                Check(!ReferenceEquals(initial, changed) && initial.IsDisposed && !changed.Player.Playing,
                    "Saving the Image rig did not replace the live binding or preserve its paused state.");
                changed.Player.Seek(7);
                Check(!before.SequenceEqual(changed.Player.GetPixels().ToArray())
                    && ImageDocumentSerializer.LoadAtomic(file).Document.PixelRigs.Single().Poses.Single(pose => pose.Name == "Wind up")
                        .Bones.Single(bone => bone.Name == "Pennant").End.Y == 6,
                    "Saved pose edits did not propagate to actual runtime pixels and reopen correctly.");
                Check(session.History.CanUndo, "Rig editing did not create an authoring undo entry.");
                editor.Undo(); editor.Save(); game.Frame();
                PixelRigSprite undone = game.Scene.World.GetRef<PixelRigSpriteComponent>(checkpoint).Binding!;
                undone.Player.Seek(7);
                Check(before.SequenceEqual(undone.Player.GetPixels().ToArray()), "Undo/save did not restore live authored rig pixels.");
                File.WriteAllText(file, "{ broken rig"); game.Frame();
                Check(ReferenceEquals(undone, game.Scene.World.GetRef<PixelRigSpriteComponent>(checkpoint).Binding)
                    && !undone.IsDisposed, "A failed live edit destroyed the last working rig.");
            }
            finally { File.WriteAllBytes(file, original); }
            Check(game.Diagnostics.Count == 0, "Rig gameplay emitted script faults.");
        });
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    private sealed class MeadowGame : IDisposable
    {
        private readonly IGameContext _previousGame = PgslCommands.ActiveGameContext;
        private readonly string _previousProject = PgslCommands.ProjectPath;
        private readonly ProjectRoomSwitcher _switcher;
        public RuntimeScene Scene { get; } = new("Mushroom Meadow acceptance") { Input = new InputState() };
        public ScriptHostSystem Host { get; } = new();
        public ProjectGameContext Game { get; }
        public string RoomFile { get; }
        public List<string> Diagnostics { get; } = [];
        public Entity Hero => Host.Instances.OfType<PgslBehavior>().Single(behavior => ObjectDrawAssetRegistry.TryGet(behavior.Entity, out ObjectDrawAssetEntry asset)
            && asset.Prefab.Contains("Explorer", StringComparison.OrdinalIgnoreCase)).Entity;
        private PgslBehavior Player => (PgslBehavior)Host.FindBehaviorForEntity(Hero);
        public PointF Position { get { var transform = Scene.World.GetRef<TransformComponent>(Hero); return new(transform.X, transform.Y); } }

        /// <summary>Plays level 1-1 directly. campaign: as if entered from the Overworld with a saved
        /// adventurer (4 of 4 hearts, 4 lives, 12 coins); otherwise standalone (F5) rules.</summary>
        public MeadowGame(ProjectSession project, bool campaign = false)
        {
            // A fresh play session: no globals left over from another game.
            PgslCommands.ResetSession();
            if (campaign)
            {
                PgslCommands.GlobalSet("campaign", 1); PgslCommands.GlobalSet("lives", 4); PgslCommands.GlobalSet("coins", 12);
                PgslCommands.GlobalSet("maxHearts", 4); PgslCommands.GlobalSet("hearts", 4); PgslCommands.GlobalSet("power", 0);
            }
            RoomFile = Path.Combine(project.RootPath, TwoDShowcaseTemplate.LevelOneRoom.Replace('/', Path.DirectorySeparatorChar));
            RoomAsset room = RoomAssetLoader.Parse(RoomFile);
            Game = new(project.RootPath, Scene, null, null, room, null);
            Host.SetContext(Game); Host.DiagnosticReported += diagnostic => Diagnostics.Add(diagnostic.Message);
            PgslCommands.ActiveGameContext = Game; PgslCommands.ProjectPath = project.RootPath;
            ScriptAssetRegistry.ClearCache(); ScriptAssetRegistry.LoadFromProject(project.RootPath);
            new RoomSceneBuilder(project.RootPath, Host).Build(Scene, room);
            Host.BeginRoom(beginGame: true);
            _switcher = new(project.RootPath, Game, Host, null, null, null, room.Name);
            // The room as it starts: its authored objects plus what their Create events make (a
            // Glowspore since the 15 October content). A restart must come back to exactly this.
            Scene.World.FlushDeferred();
            _startScripted = Host.Instances.Count;
            _startLiving = Scene.World.LivingEntityCount;
        }

        private readonly int _startScripted, _startLiving;

        public double Value(string name) => Convert.ToDouble(Player.GetVariablesSnapshot()[name], CultureInfo.InvariantCulture);
        public void Set(string name, double value) => Check(Player.TrySetLiveValue(name, value), "Missing live game field " + name);
        public void Place(float x, float y) { Set("x", x); Set("y", y); Set("vx", 0); Set("vy", 0); }
        public void Hold(Key key, bool down) { if (down) Scene.Input.OnKeyDown(key); else Scene.Input.OnKeyUp(key); }
        public void Tap(Key key) { Hold(key, true); Frame(); Hold(key, false); Frame(); }
        public void Frames(int count) { for (int index = 0; index < count; index++) Frame(); }
        public void Frame()
        {
            Scene.GameTime.Advance(1f / 60f); Host.Update(1f / 60f); Scene.World.FlushDeferred();
            foreach (var behavior in Host.Instances) Genesis.Runtime.ECS.ComponentLifecycle.OnUpdate(Scene.World, behavior.Entity, 1f / 60f);
            _switcher.Update(Scene, Scene.GameTime); Scene.Input.NextFrame();
        }
        /// <summary>Authored room objects; their Create events may make a few more (a Glowspore, a particle emitter).</summary>
        public int Authored => Game.Room.Nodes.Count(node => node.Kind == RoomNodeKind.GameObject);
        /// <summary>The same scripted instances as when the room first started, and within one living entity (the ambient emitter).</summary>
        public bool CleanRun => Host.Instances.Count == _startScripted
            && Math.Abs(Scene.World.LivingEntityCount - _startLiving) <= 1;
        public double Count(string objectName) => PgslCommands.InstanceNumber(objectName);
        public Entity Actor(string name) => Host.Instances.First(behavior => ObjectDrawAssetRegistry.TryGet(behavior.Entity, out ObjectDrawAssetEntry asset)
            && asset.Prefab.Contains(name, StringComparison.OrdinalIgnoreCase)).Entity;
        public void Dispose()
        {
            Host.EndRoom(endGame: true); Host.Clear(); _switcher.Dispose(); Scene.Dispose();
            PgslCommands.ResetSession();
            PgslCommands.ActiveGameContext = _previousGame; PgslCommands.ProjectPath = _previousProject;
            ObjectDrawAssetRegistry.Clear(); ScriptAssetRegistry.ClearCache();
        }
    }
}
