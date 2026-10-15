using System.Drawing;
using System.Globalization;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>Mushroom Meadow levels 2 and 4 (Rootdeep Caverns, Windswept Canopy) and the Grumblecrag
/// fight, played through the shipped PGSL in the real runtime. Static reachability (no power-ups, from
/// the start and the checkpoint, no soft-locks) is proven by
/// BuildTools/TemplateContent/MushroomMeadow/reach_levels_a.py; these cases prove the live behaviour:
/// both rooms load with Pip standing on the ground and run without script faults, and Grumblecrag is
/// beaten with real stomps, never leaves its arena floor before dying, resets when Pip goes back to the
/// checkpoint, and drops a Glowspore that walking into clears the level.</summary>
internal static class MushroomMeadowLevelsSuite
{
    private const string Caverns = "Rootdeep Caverns";
    private const string Canopy = "Windswept Canopy";

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "MushroomMeadowLevels");
        string parent = Path.Combine(ctx.Workspace, "MeadowLevels");
        Directory.CreateDirectory(parent);
        ProjectSession? project = null;
        HeadlessHarness.RunCase(ctx.Report, "Levels.CreateTemplateWithLevelsTwoAndFour", () =>
        {
            project = new ProjectService().CreateProject(parent, "Meadow Levels", TwoDShowcaseTemplate.TemplateId);
            foreach (string room in new[] { Caverns, Canopy })
                Check(File.Exists(RoomPath(project, room)), "The template is missing its " + room + " room.");
        });
        if (project is null) return;
        ProjectSession p = project;

        foreach (string roomName in new[] { Caverns, Canopy })
        {
            HeadlessHarness.RunCase(ctx.Report, "Levels." + roomName.Replace(" ", "") + ".LoadsWithPipOnGroundAndRunsClean", () =>
            {
                using LevelGame game = new(p, roomName);
                PointF spawn = game.Position;
                game.Frames(10);
                Check(game.Value("state") == 1 && PgslCommands.RoomValueGet("frozen") == 0,
                    "A campaign level did not start playing at once.");
                Check(game.Value("grounded") == 1 && Math.Abs(game.Position.Y - spawn.Y) < .5f && Math.Abs(game.Position.X - spawn.X) < 2,
                    $"Pip is not standing on the ground at the start: spawn {spawn}, now {game.Position}, grounded {game.Value("grounded")}.");
                Entity flag = game.Actor("Checkpoint");
                var f = game.Transform(flag);
                game.Place(f.X + 24, f.Y); game.Set("invulnerable", 5); game.Frames(5);
                Check(game.Value("grounded") == 1 && Math.Abs(game.Position.Y - f.Y) < .5f,
                    $"The checkpoint respawn ({f.X + 24}, {f.Y}) is not on the ground: Pip at {game.Position}.");
                game.Place(spawn.X, spawn.Y); game.Set("invulnerable", 5);
                game.Frames(600);
                Check(game.Diagnostics.Count == 0, "Script diagnostics: " + string.Join("; ", game.Diagnostics.Distinct()));
                Check(game.Value("state") == 1 || game.Value("state") == 4, "The level left the playing state on its own: " + game.Value("state"));
            });
        }

        HeadlessHarness.RunCase(ctx.Report, "Levels.RootdeepCaverns.GrumblecragStompedToVictoryNeverVanishes", () =>
        {
            using LevelGame game = new(p, Caverns);
            game.Frames(2);
            double width = PgslCommands.RoomGetWidth();
            float gateX = (float)(width - 640);
            Entity boss = game.Actor("Grumblecrag");
            float homeX = game.Transform(boss).X, homeY = game.Transform(boss).Y;
            Check(game.Boss(boss, "state") == 0 && PgslCommands.TileIndexAt(gateX + 16, 200, "Gate") == 39,
                "Grumblecrag must sleep with the gate open until Pip walks in.");
            // Pip waits on the left scaffold (feet 192): the beetle (top 232) passes under it and charges
            // the closed gate, which flips it. Pebbles are kept off him so the fight runs to the end; every
            // hit is a real Explorer stomp from above.
            PointF perch = new(gateX + 4 * 32 + 16, 192);
            int hits = 0, frames = 0, lastState = 0;
            bool resetChecked = false, stomping = false;
            int stompFrame = -1000;
            game.Place(perch.X, perch.Y);
            for (; frames < 9000; frames++)
            {
                if (!game.Scene.World.IsAlive(boss) || !game.Host.Instances.Any(b => b.Entity == boss)) break;
                double state = game.Boss(boss, "state");
                var t = game.Transform(boss);
                Check(t.Y >= 180 && t.Y <= homeY + .5f && t.X >= gateX + 60 && t.X <= width - 60,
                    $"Grumblecrag left its arena floor at frame {frames}: ({t.X:0.0}, {t.Y:0.0}) state {state} angle {t.Rotation:0.00}.");
                if ((int)state != lastState)
                    Console.WriteLine($"Grumblecrag frame {frames}: state {lastState} -> {state} at ({t.X:0}, {t.Y:0}), hp {game.Boss(boss, "hp")}, Pip {game.Position}");
                lastState = (int)state;
                game.Set("invulnerable", 2);
                if (state == 5 && game.Boss(boss, "t") > .35 && !stomping && frames - stompFrame > 60)
                {
                    // Drop onto the flipped shell from just above it.
                    game.Place(t.X, t.Y - 60); game.Set("vy", 120); stomping = true; stompFrame = frames;
                }
                double hpBefore = game.Boss(boss, "hp");
                game.Frame();
                if (!game.Scene.World.IsAlive(boss)) break;
                if (game.Boss(boss, "hp") < hpBefore)
                {
                    hits++;
                    Check(game.Boss(boss, "state") == 7, "A stomp on the flipped shell did not hurt Grumblecrag.");
                    if (hits == 1 && !resetChecked)
                    {
                        // Back to the checkpoint (what a lost life does): the fight resets and the gate opens.
                        game.Frames(20);
                        game.Place(gateX - 100, 288); game.Frames(3);
                        var r = game.Transform(boss);
                        Check(game.Boss(boss, "state") == 0 && game.Boss(boss, "hp") == 3 && Math.Abs(r.X - homeX) < .5f
                            && Math.Abs(r.Y - homeY) < .5f && PgslCommands.TileIndexAt(gateX + 16, 200, "Gate") == 39
                            && PgslCommands.RoomValueGet("bossFight") == 0,
                            $"Leaving the arena did not reset the fight: state {game.Boss(boss, "state")}, hp {game.Boss(boss, "hp")}, at ({r.X}, {r.Y}).");
                        resetChecked = true;
                        hits = 0;
                        game.Place(perch.X, perch.Y);
                    }
                }
                if (stomping && frames - stompFrame < 12)
                    Console.WriteLine($"  stomp frame {frames - stompFrame}: Pip {game.Position} vy {game.Value("vy"):0}, boss state {game.Boss(boss, "state")} stompable {game.Boss(boss, "stompable")} at ({game.Transform(boss).X:0}, {game.Transform(boss).Y:0})");
                if (stomping && frames - stompFrame > 40) { game.Place(perch.X, perch.Y); stomping = false; }
                if (game.Boss(boss, "state") >= 1 && game.Boss(boss, "state") <= 8)
                    Check(PgslCommands.TileIndexAt(gateX + 16, 200, "Gate") == 33, "The gate is open during the fight.");
            }
            Check(resetChecked, "The fight never reached a first stomp.");
            Check(frames < 9000, $"The fight did not end within 150 s (state {lastState}, hits after the reset {hits}).");
            Check(lastState == 9, $"Grumblecrag disappeared without dying (last state {lastState}) after {frames} frames; hits {hits}.");
            Check(game.Count("Glowspore") == 1, "Grumblecrag's defeat did not drop a Glowspore.");
            Check(PgslCommands.TileIndexAt(gateX + 16, 200, "Gate") == 39 && PgslCommands.RoomValueGet("bossFight") == 2,
                "The gate did not open after the fight.");
            var orb = game.Transform(game.Actor("Glowspore"));
            Check(orb.X > gateX + 32 && orb.X < width - 32 && orb.Y > 200 && orb.Y < 288,
                $"The Glowspore spawned out of reach at ({orb.X}, {orb.Y}).");
            game.Frames(40);
            // Walk into it along the arena floor (input only).
            game.Place(orb.X - 90, 288); game.Frames(2);
            game.Hold(Key.D, true);
            for (int frame = 0; frame < 180 && game.Value("state") == 1; frame++) game.Frame();
            game.Hold(Key.D, false);
            Check(game.Value("state") == 3 && game.Value("clearKind") == 2, $"Walking into the Glowspore did not clear the level (Pip at {game.Position}).");
            Check(game.Diagnostics.Count == 0, "Script diagnostics: " + string.Join("; ", game.Diagnostics.Distinct()));
        });
    }

    private static string RoomPath(ProjectSession project, string room) => Path.Combine(project.AssetsPath, "Rooms", room + ".room.json");
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    private sealed class LevelGame : IDisposable
    {
        private readonly IGameContext _previousGame = PgslCommands.ActiveGameContext;
        private readonly string _previousProject = PgslCommands.ProjectPath;
        private readonly ProjectRoomSwitcher _switcher;
        public RuntimeScene Scene { get; } = new("Mushroom Meadow levels") { Input = new InputState() };
        public ScriptHostSystem Host { get; } = new();
        public ProjectGameContext Game { get; }
        public List<string> Diagnostics { get; } = [];

        /// <summary>Plays a level as if entered from the Overworld (campaign: no title card).</summary>
        public LevelGame(ProjectSession project, string roomName)
        {
            PgslCommands.ResetSession();
            PgslCommands.GlobalSet("campaign", 1); PgslCommands.GlobalSet("lives", 4); PgslCommands.GlobalSet("coins", 12);
            PgslCommands.GlobalSet("maxHearts", 4); PgslCommands.GlobalSet("hearts", 4); PgslCommands.GlobalSet("power", 0);
            PgslCommands.GlobalSet("clear1", 1);
            RoomAsset room = RoomAssetLoader.Parse(RoomPath(project, roomName));
            Game = new(project.RootPath, Scene, null, null, room, null);
            Host.SetContext(Game); Host.DiagnosticReported += diagnostic => Diagnostics.Add(diagnostic.Message);
            PgslCommands.ActiveGameContext = Game; PgslCommands.ProjectPath = project.RootPath;
            ScriptAssetRegistry.ClearCache(); ScriptAssetRegistry.LoadFromProject(project.RootPath);
            new RoomSceneBuilder(project.RootPath, Host).Build(Scene, room);
            Host.BeginRoom(beginGame: true);
            _switcher = new(project.RootPath, Game, Host, null, null, null, room.Name);
        }

        public Entity Hero => Actor("Explorer");
        private PgslBehavior Behavior(Entity entity) => (PgslBehavior)Host.FindBehaviorForEntity(entity);
        public PointF Position { get { var t = Scene.World.GetRef<TransformComponent>(Hero); return new(t.X, t.Y); } }
        public TransformComponent Transform(Entity entity) => Scene.World.GetRef<TransformComponent>(entity);
        public double Value(string name) => Convert.ToDouble(Behavior(Hero).GetVariablesSnapshot()[name], CultureInfo.InvariantCulture);
        public void Set(string name, double value) => Check(Behavior(Hero).TrySetLiveValue(name, value), "Missing live game field " + name);
        public void Place(float x, float y) { Set("x", x); Set("y", y); Set("vx", 0); Set("vy", 0); }
        public void Hold(Key key, bool down) { if (down) Scene.Input.OnKeyDown(key); else Scene.Input.OnKeyUp(key); }
        public void Frames(int count) { for (int index = 0; index < count; index++) Frame(); }
        public void Frame()
        {
            Scene.GameTime.Advance(1f / 60f); Host.Update(1f / 60f); Scene.World.FlushDeferred();
            foreach (var behavior in Host.Instances) Genesis.Runtime.ECS.ComponentLifecycle.OnUpdate(Scene.World, behavior.Entity, 1f / 60f);
            _switcher.Update(Scene, Scene.GameTime); Scene.Input.NextFrame();
        }
        public double Count(string objectName) => PgslCommands.InstanceNumber(objectName);
        /// <summary>The first live instance of an object; the registry holds its name ("Explorer") or its
        /// prefab path ("Assets/Objects/Explorer.object.json").</summary>
        public Entity Actor(string objectName) => Host.Instances.FirstOrDefault(behavior => ObjectDrawAssetRegistry.TryGet(behavior.Entity, out ObjectDrawAssetEntry asset)
            && asset.Prefab is string prefab
            && (string.Equals(prefab, objectName, StringComparison.OrdinalIgnoreCase)
                || prefab.EndsWith("/" + objectName + ".object.json", StringComparison.OrdinalIgnoreCase)))?.Entity
            ?? throw new InvalidOperationException($"No live instance of {objectName}; {Host.Instances.Count} scripted instances: "
                + string.Join(", ", Host.Instances.Take(12).Select(b => ObjectDrawAssetRegistry.TryGet(b.Entity, out ObjectDrawAssetEntry a) ? a.Prefab ?? "(no prefab)" : "(no entry)")));
        public double Boss(Entity boss, string name) => Convert.ToDouble(Behavior(boss).GetVariablesSnapshot()[name], CultureInfo.InvariantCulture);
        public void Dispose()
        {
            Host.EndRoom(endGame: true); Host.Clear(); _switcher.Dispose(); Scene.Dispose();
            PgslCommands.ResetSession();
            PgslCommands.ActiveGameContext = _previousGame; PgslCommands.ProjectPath = _previousProject;
            ObjectDrawAssetRegistry.Clear(); ScriptAssetRegistry.ClearCache();
        }
    }
}
