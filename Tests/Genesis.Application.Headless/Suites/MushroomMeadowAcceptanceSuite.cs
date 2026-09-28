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
            HeadlessHarness.Step("damage, checkpoint respawn, timeout and game over are complete", () =>
            {
                Entity acorn = game.Actor("Acorn Walker");
                var enemy = game.Scene.World.GetRef<TransformComponent>(acorn);
                game.Place(enemy.X, enemy.Y); game.Set("invulnerable", 0); game.Frame();
                Check(game.Value("lives") == 2 && Math.Abs(game.Position.X - 112) < .01f, "Side contact did not cost one life and respawn.");
                game.Frames(4);
                Check(game.Value("lives") == 2, "Invulnerability did not prevent repeated loss.");
                Entity flag = game.Actor("Checkpoint");
                var checkpoint = game.Scene.World.GetRef<TransformComponent>(flag);
                game.Place(checkpoint.X, checkpoint.Y); game.Frame();
                Check(game.Value("checkpoint") == 1 && PgslCommands.RoomValueGet("checkpoint") == 1, "Checkpoint did not activate.");
                game.Place(checkpoint.X + 100, game.Game.Room.Settings.Height + 100); game.Frame();
                Check(game.Value("lives") == 1 && Math.Abs(game.Position.X - (checkpoint.X + 24)) < .01f,
                    "Falling did not respawn at the saved flag.");
                game.Set("timeLeft", .001); game.Frame();
                Check(game.Value("state") == 4 && game.Value("lives") == 0 && PgslCommands.RoomValueGet("frozen") == 1,
                    "Timeout did not complete the game-over transition.");
            });
            HeadlessHarness.Step("restart clears instances, rewards and mutated tiles through the production switcher", () =>
            {
                game.Tap(Key.Enter);
                Check(game.Value("state") == 0 && game.Value("lives") == 3 && game.Value("coins") == 0,
                    "Restart retained game-over state or collected rewards.");
                Check(game.Scene.World.LivingEntityCount == 47 && game.Host.Instances.Count == 47, "Restart duplicated or lost actors.");
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
                Check(game.Value("state") == 0 && game.Scene.World.LivingEntityCount == 47, "Win-screen restart did not create a clean run.");
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
                Check(game.Value("state") == 3, $"Input-only traversal failed after {frames} frames at {game.Position}; lives={game.Value("lives")}, state={game.Value("state")}, timer={game.Value("timeLeft")}, vx={game.Value("vx")}, right={game.Scene.Input.IsDown(Key.D)}.");
                Check(game.Value("checkpoint") == 1 && game.Value("coins") > 0, "Traversal omitted checkpoint or collectibles.");
            });
            Check(game.Diagnostics.Count == 0, "Game script diagnostics: " + string.Join("; ", game.Diagnostics));
            Check(File.ReadAllBytes(game.RoomFile).SequenceEqual(authoredRoom), "Gameplay or restart modified the saved room.");
        });
        HeadlessHarness.RunCase(context.Report, "Acceptance.MushroomMeadow.ImageRigLiveAuthoring", () =>
        {
            string file = Path.Combine(project.AssetsPath, "Sprites", "Checkpoint.image.json");
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

        public MeadowGame(ProjectSession project)
        {
            RoomFile = Path.Combine(project.RootPath, TwoDShowcaseTemplate.StartRoom.Replace('/', Path.DirectorySeparatorChar));
            RoomAsset room = RoomAssetLoader.Parse(RoomFile);
            Game = new(project.RootPath, Scene, null, null, room, null);
            Host.SetContext(Game); Host.DiagnosticReported += diagnostic => Diagnostics.Add(diagnostic.Message);
            PgslCommands.ActiveGameContext = Game; PgslCommands.ProjectPath = project.RootPath;
            ScriptAssetRegistry.ClearCache(); ScriptAssetRegistry.LoadFromProject(project.RootPath);
            new RoomSceneBuilder(project.RootPath, Host).Build(Scene, room);
            Host.BeginRoom(beginGame: true);
            _switcher = new(project.RootPath, Game, Host, null, null, null, room.Name);
        }

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
        public Entity Actor(string name) => Host.Instances.First(behavior => ObjectDrawAssetRegistry.TryGet(behavior.Entity, out ObjectDrawAssetEntry asset)
            && asset.Prefab.Contains(name, StringComparison.OrdinalIgnoreCase)).Entity;
        public void Dispose()
        {
            Host.EndRoom(endGame: true); Host.Clear(); _switcher.Dispose(); Scene.Dispose();
            PgslCommands.ActiveGameContext = _previousGame; PgslCommands.ProjectPath = _previousProject;
            ObjectDrawAssetRegistry.Clear(); ScriptAssetRegistry.ClearCache();
        }
    }
}
