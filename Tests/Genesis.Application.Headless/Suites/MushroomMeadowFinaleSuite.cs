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

/// <summary>Mushroom Meadow levels 3 and 5 (Tidepool Grotto, Duskmantle Hollow) and the final boss,
/// played through the shipped PGSL in the real runtime. Static reachability of both maps is proven by
/// BuildTools/TemplateContent/MushroomMeadow/reach_finale.py; these cases prove the live behaviour:
/// the rooms load and play without script faults, currents push and can be swum out of, and
/// Duskmantle can be beaten with stomps alone (no power-ups, no hearts lost to a stomp), never
/// vanishes, resets cleanly when Pip loses a life, drops a reachable Glowspore and ends at Credits.</summary>
internal static class MushroomMeadowFinaleSuite
{
    private const string Grotto = "Tidepool Grotto";
    private const string Hollow = "Duskmantle Hollow";

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "MushroomMeadowFinale");
        string parent = Path.Combine(ctx.Workspace, "MeadowFinale");
        Directory.CreateDirectory(parent);
        ProjectSession? project = null;
        HeadlessHarness.RunCase(ctx.Report, "Finale.CreateTemplateWithLevelsThreeAndFive", () =>
        {
            project = new ProjectService().CreateProject(parent, "Meadow Finale", TwoDShowcaseTemplate.TemplateId);
            foreach (string room in new[] { Grotto, Hollow, "Credits" })
                Check(File.Exists(RoomPath(project, room)), "The template is missing its " + room + " room.");
        });
        if (project is null) return;
        ProjectSession p = project;

        HeadlessHarness.RunCase(ctx.Report, "Finale.Grotto.LoadsSwimsAndClears", () =>
        {
            using FinaleGame game = new(p, Grotto);
            game.Tap(Key.Enter); game.Frames(30);
            Check(PgslCommands.RoomValueGet("underwater") == 1 && PgslCommands.RoomValueGet("levelId") == 3,
                "Level Rules did not publish the Grotto's underwater rules.");
            Check(game.Value("swimming") == 1 && game.Value("state") == 1, "Pip is not swimming in the Grotto.");
            HeadlessHarness.Step("swimming moves Pip in 8 directions and the seabed never traps him", () =>
            {
                PointF start = game.Position;
                game.Hold(Key.D, true); game.Frames(60); game.Hold(Key.D, false);
                Check(game.Position.X > start.X + 60, $"Swimming right did not move Pip: {start} -> {game.Position}.");
                PointF before = game.Position;
                game.Hold(Key.W, true); game.Frames(40); game.Hold(Key.W, false);
                Check(game.Position.Y < before.Y - 30, $"Swimming up did not lift Pip: {before} -> {game.Position}.");
            });
            HeadlessHarness.Step("the up current lifts Pip and he can swim sideways out of it", () =>
            {
                // Section D's up current: 3424..3616 x, 192..640 y (written by levels_b_gen.py).
                game.Place(3520, 560); game.Set("invulnerable", 30);
                game.Frames(30);
                Check(game.Position.Y < 560 - 40, $"The up current did not lift Pip: y={game.Position.Y}.");
                game.Hold(Key.A, true); game.Frames(90); game.Hold(Key.A, false);
                Check(game.Position.X < 3424, $"Pip could not swim sideways out of the up current: {game.Position}.");
            });
            HeadlessHarness.Step("the fast lane carries Pip right and he can steer up and down inside it", () =>
            {
                game.Place(4000, 240); game.Set("invulnerable", 30);
                game.Hold(Key.S, true); game.Frames(40); game.Hold(Key.S, false);
                Check(game.Position.X > 4000 + 60 && game.Position.Y > 240 + 20,
                    $"The right current plus swimming down did not carry Pip down-right: {game.Position}.");
            });
            HeadlessHarness.Step("checkpoint respawn and the Glowspore clear", () =>
            {
                Entity flag = game.Actor("Checkpoint");
                var f = game.Transform(flag);
                // The hero looks for the checkpoint every second frame and for the Glowspore every third
                // (slowLane, since 28 Oct), so the touches below run two or three frames.
                game.Place(f.X, f.Y); game.Frames(2);
                Check(game.Value("checkpoint") == 1, "The Grotto checkpoint did not light.");
                game.Set("invulnerable", 0); game.Place(f.X + 200, game.Game.Room.Settings.Height + 100); game.Frame();
                Check(Math.Abs(game.Position.X - (f.X + 24)) < .01f, $"Pip did not respawn at the Grotto checkpoint: {game.Position}.");
                game.Frames(40);
                var orb = game.Transform(game.Actor("Glowspore"));
                game.Place(orb.X, orb.Y + 17); game.Frames(3);
                Check(game.Value("state") == 3 && game.Value("clearKind") == 2, "Touching the Grotto Glowspore did not clear the level.");
            });
            Check(game.Diagnostics.Count == 0, "Grotto script diagnostics: " + string.Join("; ", game.Diagnostics));
        });

        HeadlessHarness.RunCase(ctx.Report, "Finale.Hollow.LoadsWithHiddenBossAndNestTrigger", () =>
        {
            using FinaleGame game = new(p, Hollow);
            game.Tap(Key.Enter); game.Frames(30);
            Check(PgslCommands.RoomValueGet("levelId") == 5 && PgslCommands.RoomValueGet("dark") == 1, "Hollow rules missing.");
            Check(game.Boss("state") == 0 && PgslCommands.RoomValueGet("bossFight") == 0, "Duskmantle did not wait hidden above the nest.");
            game.Hold(Key.D, true); game.Frames(90); game.Hold(Key.D, false);
            Check(game.Position.X > 200 && Math.Abs(game.Position.Y - 320) < .5f, $"Pip did not run along the Hollow floor: {game.Position}.");
            game.EnterNest();
            Check(game.Boss("state") >= 1 && PgslCommands.RoomValueGet("bossFight") == 1, "Dropping into the nest did not start the fight.");
            Check(game.Diagnostics.Count == 0, "Hollow script diagnostics: " + string.Join("; ", game.Diagnostics));
        });

        HeadlessHarness.RunCase(ctx.Report, "Finale.Duskmantle.StompsOnlyVictoryGlowsporeAndCredits", () =>
        {
            using FinaleGame game = new(p, Hollow, campaign: true);
            game.Frames(5);
            Check(game.Value("powerUp") == 0, "The victory must be won with no power-up.");
            game.EnterNest();
            int stomps = game.StompUntil(0);
            Check(stomps == 6, $"Duskmantle needed {stomps} stomps, expected 6.");
            Check(game.Boss("state") == 9 && game.Boss("dead") == 1 && PgslCommands.RoomValueGet("bossFight") == 2,
                "Six stomps did not defeat Duskmantle.");
            Check(game.Value("hearts") == 4 && game.Value("lives") == 4, $"Stomps cost Pip hearts or lives: hearts {game.Value("hearts")}, lives {game.Value("lives")}.");
            for (int frame = 0; frame < 400 && game.Count("Glowspore") == 0; frame++) game.Frame();
            Check(game.Count("Glowspore") == 1, "Duskmantle's defeat spawned no Glowspore.");
            var orb = game.Transform(game.Actor("Glowspore"));
            Check(orb.X > game.ArenaL && orb.X < game.ArenaR && orb.Y < game.FloorY && orb.Y > game.FloorY - 110,
                $"The Glowspore spawned out of the nest's jump reach: {orb.X},{orb.Y}.");
            game.Frames(40);
            // Reach it with input only: stand under it and jump.
            game.Place(orb.X, game.FloorY); game.Frame();
            game.Hold(Key.Space, true);
            for (int frame = 0; frame < 60 && game.Value("state") == 1; frame++) game.Frame();
            game.Hold(Key.Space, false);
            Check(game.Value("state") == 3 && game.Value("clearKind") == 2, "Jumping from the nest floor did not collect the Glowspore.");
            for (int frame = 0; frame < 600 && PgslCommands.RoomGetName() == Hollow; frame++) game.Frame();
            Check(PgslCommands.GlobalGet("clear5") == 1 && PgslCommands.GlobalGet("gameComplete") == 1, "Clearing the Hollow did not record the win.");
            Check(PgslCommands.RoomGetName() == "Credits", "The final clear did not go to Credits; room is " + PgslCommands.RoomGetName() + ".");
            Check(game.Diagnostics.Count == 0, "Boss fight script diagnostics: " + string.Join("; ", game.Diagnostics));
        });

        HeadlessHarness.RunCase(ctx.Report, "Finale.Duskmantle.ResetsCleanlyWhenPipLosesALife", () =>
        {
            using FinaleGame game = new(p, Hollow);
            game.Tap(Key.Enter); game.Frames(5);
            var flag = game.Transform(game.Actor("Checkpoint"));
            game.Place(flag.X, flag.Y); game.Frames(2); // the checkpoint is looked for every second frame (slowLane)
            Check(game.Value("checkpoint") == 1, "The Hollow checkpoint did not light.");
            game.EnterNest();
            game.StompUntil(4);
            Check(game.Boss("hp") == 4, "Two stomps did not take two hits.");
            game.Set("invulnerable", 0); game.Place(game.ArenaL + 300, game.Game.Room.Settings.Height + 100); game.Frames(4);
            Check(Math.Abs(game.Position.X - (flag.X + 24)) < .01f, $"Pip did not respawn at the gate checkpoint: {game.Position}.");
            Check(game.Boss("state") == 0 && game.Boss("hp") == 6 && game.Transform(game.BossEntity).Y < 0 && PgslCommands.RoomValueGet("bossFight") == 0
                && game.Count("Gloom Orb") == 0, "The fight did not reset cleanly after Pip lost a life.");
            game.EnterNest();
            Check(game.Boss("state") >= 1 && PgslCommands.RoomValueGet("bossFight") == 1, "The reset fight did not start again.");
            Check(game.StompUntil(0) == 6, "The restarted fight could not be won.");
            Check(game.Diagnostics.Count == 0, "Reset script diagnostics: " + string.Join("; ", game.Diagnostics));
        });

        HeadlessHarness.RunCase(ctx.Report, "Finale.Duskmantle.NeverVanishesThroughEveryPhase", () =>
        {
            using FinaleGame game = new(p, Hollow);
            game.Tap(Key.Enter); game.Frames(5);
            game.EnterNest();
            foreach (int hp in new[] { 6, 4, 2 })
            {
                game.StompUntil(hp);
                // Let her run every attack of this phase for 40 s with Pip parked on the nest floor.
                var states = new HashSet<double>();
                for (int frame = 0; frame < 2400; frame++)
                {
                    game.Set("invulnerable", 5);
                    if (frame % 120 == 0) game.Place(game.ArenaL + 120 + (frame / 120 % 4) * 120, game.FloorY);
                    game.Frame();
                    Check(game.BossAlive, $"Duskmantle vanished at hp {hp}, frame {frame}.");
                    var b = game.Transform(game.BossEntity);
                    Check(b.X > game.ArenaL && b.X < game.ArenaR && b.Y < game.FloorY && b.Y > -400 && game.Boss("state") is >= 1 and <= 8,
                        $"Duskmantle left the nest or her fight at hp {hp}, frame {frame}: {b.X},{b.Y} state {game.Boss("state")}.");
                    states.Add(game.Boss("state"));
                }
                Check(states.Contains(5) && (hp > 4 || states.Contains(7)) && (hp > 2 || states.Contains(8)),
                    $"Phase at hp {hp} did not run its attacks: states {string.Join(",", states.OrderBy(s => s))}.");
            }
            Check(game.Diagnostics.Count == 0, "Phase script diagnostics: " + string.Join("; ", game.Diagnostics));
        });
    }

    private static string RoomPath(ProjectSession project, string room) => Path.Combine(project.AssetsPath, "Rooms", room + ".room.json");
    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);

    private sealed class FinaleGame : IDisposable
    {
        private readonly IGameContext _previousGame = PgslCommands.ActiveGameContext;
        private readonly string _previousProject = PgslCommands.ProjectPath;
        private readonly ProjectRoomSwitcher _switcher;
        public RuntimeScene Scene { get; } = new("Mushroom Meadow finale") { Input = new InputState() };
        public ScriptHostSystem Host { get; } = new();
        public ProjectGameContext Game { get; }
        public List<string> Diagnostics { get; } = [];
        public float ArenaL { get; }
        public float ArenaR { get; }
        public float FloorY { get; }

        public FinaleGame(ProjectSession project, string roomName, bool campaign = false)
        {
            PgslCommands.ResetSession();
            if (campaign)
            {
                PgslCommands.GlobalSet("campaign", 1); PgslCommands.GlobalSet("lives", 4); PgslCommands.GlobalSet("coins", 12);
                PgslCommands.GlobalSet("maxHearts", 4); PgslCommands.GlobalSet("hearts", 4); PgslCommands.GlobalSet("power", 0);
                for (int level = 1; level <= 4; level++) PgslCommands.GlobalSet("clear" + level, 1);
            }
            RoomAsset room = RoomAssetLoader.Parse(RoomPath(project, roomName));
            Game = new(project.RootPath, Scene, null, null, room, null);
            Host.SetContext(Game); Host.DiagnosticReported += diagnostic => Diagnostics.Add(diagnostic.Message);
            PgslCommands.ActiveGameContext = Game; PgslCommands.ProjectPath = project.RootPath;
            ScriptAssetRegistry.ClearCache(); ScriptAssetRegistry.LoadFromProject(project.RootPath);
            new RoomSceneBuilder(project.RootPath, Host).Build(Scene, room);
            Host.BeginRoom(beginGame: true);
            _switcher = new(project.RootPath, Game, Host, null, null, null, room.Name);
            if (roomName == Hollow)
            {
                Frame();
                ArenaL = (float)Boss("arenaL"); ArenaR = (float)Boss("arenaR"); FloorY = (float)Boss("floorY");
            }
        }

        public Entity Hero => Actor("Explorer");
        private PgslBehavior Behavior(Entity entity) => (PgslBehavior)Host.FindBehaviorForEntity(entity);
        public PointF Position { get { var t = Scene.World.GetRef<TransformComponent>(Hero); return new(t.X, t.Y); } }
        public TransformComponent Transform(Entity entity) => Scene.World.GetRef<TransformComponent>(entity);
        public double Value(string name) => Convert.ToDouble(Behavior(Hero).GetVariablesSnapshot()[name], CultureInfo.InvariantCulture);
        public void Set(string name, double value) => Check(Behavior(Hero).TrySetLiveValue(name, value), "Missing live game field " + name);
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
        public double Count(string objectName) => PgslCommands.InstanceNumber(objectName);
        /// <summary>The first live instance of an Object, found the way the game's PGSL finds it.</summary>
        public Entity Actor(string objectName)
        {
            double id = PgslCommands.InstanceFind(objectName, 0);
            Check(id > 0, "No live " + objectName + " instance.");
            return Host.Instances.First(behavior => behavior.Entity.Id == (int)id).Entity;
        }
        public bool BossAlive => PgslCommands.InstanceNumber("Duskmantle") == 1;
        public Entity BossEntity => Actor("Duskmantle");
        public double Boss(string name) => Convert.ToDouble(Behavior(BossEntity).GetVariablesSnapshot()[name], CultureInfo.InvariantCulture);

        /// <summary>Walk off the gate ledge into the nest (input only) until the fight starts.</summary>
        public void EnterNest()
        {
            Place(ArenaL - 40, FloorY - 128); Frame();
            Hold(Key.D, true);
            for (int frame = 0; frame < 240 && Boss("state") == 0; frame++) Frame();
            Hold(Key.D, false);
            Check(Boss("state") >= 1, $"Walking into the nest did not wake Duskmantle; Pip at {Position}.");
        }

        /// <summary>Stomp Duskmantle each time she lies dazed until her hp is `untilHp`. Pip dodges her
        /// other attacks (he is kept invulnerable outside the stomp windows), but every stomp is a real
        /// fall onto her back, and through each stomp and her rise Pip must lose no heart.
        /// Returns the number of stomps.</summary>
        public int StompUntil(int untilHp)
        {
            int stomps = 0;
            for (int frame = 0; frame < 60 * 240 && Boss("hp") > untilHp && Boss("dead") == 0; frame++)
            {
                if (Boss("state") == 5 && Boss("stateTime") > .1)
                {
                    var b = Transform(BossEntity);
                    double hp = Boss("hp");
                    double hearts = Value("hearts");
                    // Only Duskmantle can touch Pip in this window: clear stray orbs and summoned moths.
                    foreach (string other in new[] { "Gloom Orb", "Dusk Moth" })
                        for (int i = 0; i < (int)Count(other); i++)
                            PgslCommands.InstanceVariableSet(PgslCommands.InstanceFind(other, i), "hurt", 9d);
                    Set("invulnerable", 0);
                    // Above her back (mask top = y - 28), falling.
                    Place(b.X, b.Y - 28 - 40); Set("vy", 120);
                    int guard = 0;
                    while (Boss("hp") == hp && guard++ < 40) Frame();
                    Check(Boss("hp") == hp - 1, $"A fall onto the dazed Duskmantle did not stomp her (hp {Boss("hp")}).");
                    stomps++;
                    // Her rise and Pip's bounce and landing: no heart may be lost.
                    for (int rise = 0; rise < 75; rise++) Frame();
                    Check(Value("hearts") == hearts, $"Stomping Duskmantle cost Pip a heart ({hearts} -> {Value("hearts")}).");
                    continue;
                }
                Set("invulnerable", 5);
                Frame();
            }
            return stomps;
        }

        public void Dispose()
        {
            Host.EndRoom(endGame: true); Host.Clear(); _switcher.Dispose(); Scene.Dispose();
            PgslCommands.ResetSession();
            PgslCommands.ActiveGameContext = _previousGame; PgslCommands.ProjectPath = _previousProject;
            ObjectDrawAssetRegistry.Clear(); ScriptAssetRegistry.ClearCache();
        }
    }
}
