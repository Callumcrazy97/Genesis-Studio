using System.Globalization;
using System.Numerics;
using System.Text;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Physics;
using Genesis.Runtime;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The one standard way to reach the engine (PgslApi): every command as Engine.Area.Name, every
/// value as a property read and set like a variable, and a body's values on the current instance,
/// in with and for, in events and in compiled functions.
/// </summary>
internal static class PgslEngineApiChecks
{
    /// <summary>Run by pgsl-logic's sandbox (one instance, no room): t_ names must be 1.</summary>
    public const string Script = """
        // Each command by its standard name is the command itself.
        var l = Engine.List.Create();
        Engine.List.Add(l, 5);
        Engine.List.Add(l, 7);
        t_listCommands = Engine.List.Size(l) == 2 && DsListSize(l) == 2 && Engine.List.Get(l, 1) == 7;
        t_math = Engine.Math.Clamp(12, 0, 10) == 10 && Engine.Math.Max(2, 9) == 9;
        t_text = Engine.Text.Upper("ab") == "AB" && Engine.Text.Length("abc") == 3;
        var m = Engine.Map.Create();
        Engine.Map.Set(m, "k", 4);
        t_map = Engine.Map.Get(m, "k") == 4;
        // A value is a property: read, set and += reach the same value as its commands.
        Engine.Image.Frame = 3;
        t_frame = Engine.Image.Frame == 3 && SpriteGetFrame() == 3 && image_index == 3;
        Engine.Image.Frame += 2;
        t_frameAdd = image_index == 5 && Engine.Image.GetFrame() == 5;
        Engine.Image.Speed = 0.5;
        t_speed = Engine.Image.Speed == 0.5 && SpriteGetSpeed() == 0.5;
        Engine.Image.Alpha = 0.25;
        t_alpha = image_alpha == 0.25 && Engine.Image.Alpha == 0.25;
        Engine.Image.ScaleX = 2;
        t_scale = image_xscale == 2 && Engine.Image.ScaleX == 2;
        Engine.Instance.X = 40;
        Engine.Instance.Y += 6;
        t_instance = x == 40 && Engine.Instance.X == 40 && Engine.Instance.Y == y;
        // Inside a namespace block the standard names are short.
        from Engine.Math: { clamped = Clamp(-3, 0, 1); }
        t_from = clamped == 0;
        // Bare words stay the script's own variables.
        Mass = 5;
        Friction = 2;
        t_bareWords = Mass == 5 && Friction == 2;
        // No body and no physics world: a body's values read 0 and setting one does nothing.
        Engine.Physics.Mass = 9;
        t_noBody = !Engine.Physics.HasBody && Engine.Physics.Mass == 0 && Engine.Physics.BodyType == "";
        // A function setting and reading one, called often enough to run compiled.
        function ApiStep(k) { Engine.Image.Frame = k; return Engine.Image.Frame + Engine.Math.Max(k, 1); }
        var total = 0;
        for (var k = 0; k < 60; k++) { total = ApiStep(k); }
        t_compiled = total == 118 && image_index == 59;
        // The engine's threads: the number in use, one, and back to automatic.
        var threadsAuto = Engine.Game.Threads;
        Engine.Game.Threads = 1;
        var threadsOne = Engine.Game.Threads;
        Engine.Game.Threads = 0;
        t_threads = threadsAuto >= 1 && threadsOne == 1 && Engine.Game.Threads == threadsAuto;
        // Lighting's parts: on unless turned off, values kept in range.
        var partsOn = Engine.Lighting.Reflections.Enabled && Engine.Lighting.Bounce.Enabled;
        Engine.Lighting.Reflections.Enabled = false;
        var reflectionsOff = !Engine.Lighting.Reflections.Enabled;
        Engine.Lighting.Reflections.Enabled = true;
        Engine.Lighting.Probe.Quality = 9;
        var detail = Engine.Lighting.Probe.Quality;
        Engine.Lighting.Probe.Quality = 2;
        Engine.Lighting.Bounce.Strength = 0.25;
        var bounce = Engine.Lighting.Bounce.Strength;
        Engine.Lighting.Bounce.Strength = 1;
        Engine.Lighting.Probe.Distance = 2;
        var distance = Engine.Lighting.Probe.Distance;
        Engine.Lighting.Probe.Distance = 40;
        Engine.Lighting.Probe.Recapture();
        t_lighting = partsOn && reflectionsOff && detail == 3 && bounce == 0.25 && distance == 5 && Engine.Lighting.Reflections.Enabled;
        // Turning a part on turns its area on.
        Engine.Lighting.Enabled = false;
        var lightingOff = !Engine.Lighting.Enabled;
        Engine.Lighting.Bounce.Enabled = true;
        t_partTurnsAreaOn = lightingOff && Engine.Lighting.Enabled;
        """;

    private const string Controller = """
        with (ApiCrate) { Engine.Physics.GravityScale = 0; Engine.Physics.Mass = 4; Engine.Physics.Bounciness = 0.75; Engine.Physics.Friction = 0.3; }
        var n = InstanceNumber("ApiCrate");
        for (var i = 0; i < n; i++) { with (InstanceFind("ApiCrate", i)) { Engine.Physics.VelocityX = (i + 1) * 2; } }
        count = n;
        mine = Engine.Physics.HasBody;
        Engine.Physics.AirDrag = 0.05;
        drag = PhysicsGetAirDrag();
        Engine.Physics.GravityY = -19.62;
        strength = Engine.Physics.GravityStrength;
        var lead = InstanceFind("ApiCrate", 0);
        var leadSpeed = 0;
        var leadMass = 0;
        var leadType = "";
        with (lead) { Engine.Physics.ApplyImpulse(4, 0, 0); leadSpeed = Engine.Physics.VelocityX; leadMass = Engine.Physics.Mass; leadType = Engine.Physics.BodyType; }
        speedA = leadSpeed;
        massA = leadMass;
        typeA = leadType;
        with (InstanceFind("ApiCrate", 2)) { Engine.Physics.BodyType = "static"; }
        function ApiDamp(k) { Engine.Physics.LinearDamping = k * 0.01; return Engine.Physics.LinearDamping; }
        var d = 0;
        with (lead) { for (var j = 0; j < 60; j++) { d = ApiDamp(j); } }
        damping = d;
        """;

    private const string Ball = """
        used = Engine.Physics.Use("ApiBouncy");
        shape = Engine.Physics.Shape;
        bounce = Engine.Physics.Bounciness;
        had = Engine.Physics.HasBody;
        removed = Engine.Physics.Remove();
        after = Engine.Physics.HasBody;
        """;

    public static void Run(HeadlessContext ctx, ProjectSession project, Action<string, string, string, string> row)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiNamesEveryCommand", () => Names(ctx, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiPhysicsWithAndFor", () => Physics(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.AutotilesAndAcoustics", () => AutotilesAndAcoustics(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.ThreadsShareCullingAndDrawTheSame", () => SharedCulling(row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiPartsDefaultsAndValues", () => Parts(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiWeatherPartsHoldAndRelease", () => WeatherParts(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiTileLayersUpdateDistanceAndFrameCosts", () => TilesAndUpdate(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiWaterSurfaceLook", () => WaterSurface(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiWaterSwimmingSwitchAndReflections", () => WaterSwimming(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiTerrainEditsTheGroundAsItPlays", () => TerrainEdits(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiTerrainGroundGrassScatterAndQueries", () => TerrainLook(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiParticleEmitterLookAndQuality", () => ParticleEmitterLook(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiParticleSwitches", () => ParticleSwitches(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.DirectedBurstsAndGlowingBeams", () => DirectedBurstsAndBeams(row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.AHandleUsedInAnotherInstanceIsAnErrorWhenAsked", () => DataOwnerChecks(row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiInstanceFrozenAndBack", () => InstanceFreeze(project, row));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Pgsl.Logic.EngineApiNavigationBakeAndPriority", () => NavigationParts(project, row));
    }

    // Engine.Area.Part.Value in a running room: each value reads its default until set, keeps what a
    // script sets (kept in range), and a part turned on turns its area on.
    private const string PartsScript = """
        dShadow = Engine.Rendering.Shadows.Strength;
        dFogStart = Engine.Rendering.Fog.Start;
        dSun = Engine.Lighting.Sun.Intensity;
        dSteps = Engine.Lighting.Toon.Steps;
        dOrbit = Engine.Camera.Orbit.Distance;
        dOrbitOn = Engine.Camera.Orbit.Enabled;
        Engine.Rendering.Fog.Enabled = true;
        Engine.Rendering.Fog.Start = 12;
        Engine.Rendering.Fog.Color = "#ff8000";
        fogOn = Engine.Rendering.Fog.Enabled;
        fogStart = Engine.Rendering.Fog.Start;
        fogColor = Engine.Rendering.Fog.Color;
        Engine.Lighting.Sun.Pitch = 120;
        pitch = Engine.Lighting.Sun.Pitch;
        Engine.Lighting.Enabled = false;
        Engine.Lighting.Toon.Enabled = true;
        lightingAfterToon = Engine.Lighting.Enabled;
        toonOn = Engine.Lighting.Toon.Enabled;
        Engine.Game.Paused = true;
        paused = Engine.Game.Paused && Engine.Game.Speed == 0;
        Engine.Game.Paused = false;
        resumed = Engine.Game.Speed;
        Engine.Audio.Acoustics.Place = "hall";
        Engine.Audio.Acoustics.Enabled = false;
        offPlace = Engine.Audio.Acoustics.Place;
        Engine.Audio.Acoustics.Enabled = true;
        backPlace = Engine.Audio.Acoustics.Place;
        Engine.Camera.Orbit.Distance = 3;
        orbit = Engine.Camera.Orbit.Distance;
        Engine.Rendering.Detail.Quality = 2;
        detail = Engine.Rendering.Detail.Quality;
        Engine.Rendering.Detail.Quality = 1;
        dTransition = Engine.Weather.Transition;
        Engine.Weather.Transition = 900;
        transition = Engine.Weather.Transition;
        Engine.Weather.Transition = 20;
        dFade = Engine.Room.Change.Fade;
        Engine.Room.Change.Fade = 9;
        fade = Engine.Room.Change.Fade;
        Engine.Room.Change.Fade = 0.2;
        dBursts = Engine.Particle.Bursts.Limit;
        """;

    private static void Parts(ProjectSession project, Action<string, string, string, string> row)
    {
        RoomAsset room = RoomAsset.Create("PartsRoom", RoomDimension.ThreeD);
        using var scene = new RuntimeScene("Parts");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        string previousPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game;
        PgslCommands.ProjectPath = project.RootPath;
        try
        {
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = PartsScript })) host.Attach(scene.World, entity, "PartsProbe");
            var vars = host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot();
            double Number(string name) => vars.TryGetValue(name, out object? value) && value is not string ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            // A comparison or && gives 1 or 0; a property read gives true or false.
            bool Flag(string name) => vars.TryGetValue(name, out object? value)
                && (value is true || value is not (string or bool or null) && Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0);
            string Text(string name) => vars.TryGetValue(name, out object? value) ? value as string ?? "" : "";
            string diagnostics = string.Join("; ", host.RecentDiagnostics.Select(d => d.Message));
            void Check(string name, bool pass, string detail)
            {
                row("Engine API parts", name, pass ? "PASS" : "FAIL", detail);
                HeadlessHarness.Assert(pass, $"{name}: {detail} {diagnostics}");
            }

            Check("values read their defaults until set",
                Number("dShadow") == 1 && Number("dFogStart") == 35 && Math.Abs(Number("dSun") - 1.1) < 1e-6 && Number("dSteps") == 1
                && Number("dOrbit") == 7.5 && !Flag("dOrbitOn"),
                $"shadow strength {Number("dShadow")}, fog start {Number("dFogStart")}, sun {Number("dSun")}, toon steps {Number("dSteps")}, orbit {Number("dOrbit")} (attached {Flag("dOrbitOn")})");
            Check("values keep what a script sets, kept in range",
                Flag("fogOn") && Number("fogStart") == 12 && Text("fogColor") == "#ff8000" && Number("pitch") == 90 && Number("orbit") == 3 && Number("detail") == 2
                && Number("dTransition") == 20 && Number("transition") == 600
                // The room-change fade is a game-wide value other checks change, so only its range is checked.
                && Number("dFade") is >= 0 and <= 5 && Number("fade") == 5 && Number("dBursts") == 256,
                $"fog {Flag("fogOn")} from {Number("fogStart")} in {Text("fogColor")}, sun pitch {Number("pitch")} (90), orbit {Number("orbit")}, detail {Number("detail")}, weather transition {Number("dTransition")} then {Number("transition")} (20, 600), room fade {Number("dFade")} then {Number("fade")} (0.2, 5), burst limit {Number("dBursts")} (256)");
            Check("a part turned on turns its area on", Flag("lightingAfterToon") && Flag("toonOn"),
                $"lighting {Flag("lightingAfterToon")} and toon {Flag("toonOn")} after Engine.Lighting.Toon.Enabled = true (game lighting now {game.Scene?.Environment.LightingEnabled})");
            Check("pausing keeps the speed for when it resumes", Flag("paused") && Number("resumed") == 1, $"paused {Flag("paused")}, speed after {Number("resumed")}");
            Check("a part turned off comes back as it was", Text("offPlace") == "outdoors" && Text("backPlace") == "hall",
                $"acoustics off: {Text("offPlace")}, on again: {Text("backPlace")}");

            // A setting added in the Inspector starts at the value's own default.
            string Default(string name) => EngineSettings.TryFind(name, out EngineSettings.Setting setting) ? EngineSettings.DefaultText(setting) : "(not a setting)";
            PgslCommands.ActiveGameContext = previousGame;
            string fogStart = Default("Engine.Rendering.Fog.Start"), bounce = Default("Engine.Lighting.Bounce.Strength"),
                quality = Default("Engine.Lighting.Probe.Quality"), muffle = Default("Engine.Audio.Music.Muffle"), sun = Default("Engine.Lighting.Sun.Color");
            Check("a setting added in the Inspector starts at its default",
                fogStart == "35" && bounce == "1" && quality == "2" && muffle == "0" && sun == "#fff2d1",
                $"fog start {fogStart} (35), bounce {bounce} (1), probe quality {quality} (2), music muffle {muffle} (0), sun colour {sun} (#fff2d1)");
        }
        finally
        {
            PgslCommands.AudioApiAcoustics = "outdoors";
            Genesis.Runtime.Modeling.ModelLodView.Quality = 1f;
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousPath;
            PgslCommands.BindContext(previousContext);
        }
    }

    // A 2D room's tile layers by name, how far instances update, and what the last frame cost.
    private const string TilesScript = """
        backVisible = Engine.Tile.Layer.Visible("Back");
        Engine.Tile.Layer.SetVisible("Back", false);
        hidden = Engine.Tile.Layer.Visible("Back");
        missing = Engine.Tile.Layer.SetAlpha("Nope", 0.5);
        Engine.Tile.Layer.SetAlpha("Back", 2);
        alpha = Engine.Tile.Layer.Alpha("Back");
        Engine.Tile.Layer.SetOffset("Back", 12, -4);
        ox = Engine.Tile.Layer.OffsetX("Back");
        oy = Engine.Tile.Layer.OffsetY("Back");
        Engine.Instance.Update.Distance = 250;
        updateDistance = Engine.Instance.Update.Distance;
        drawCalls = Engine.Rendering.DrawCalls;
        backendLength = StringLength(Engine.Rendering.Backend);
        Engine.Input.Mouse.Sensitivity = 2;
        Engine.Input.Mouse.InvertY = true;
        Engine.Input.Gamepad.VibrationStrength = 0.5;
        GamepadVibrate(1, 0.8, 1);
        sensitivity = Engine.Input.Mouse.Sensitivity;
        CharacterCreate(0.4, 1.8, 0.3, 45);
        Engine.Physics.Character.StepHeight = 0.5;
        step = Engine.Physics.Character.StepHeight;
        Engine.Physics.Character.SlopeLimit = 120;
        slope = Engine.Physics.Character.SlopeLimit;
        radius = Engine.Physics.Character.Radius;
        rate = Engine.Network.Replication.Rate;
        dDepth = Engine.Tile.Layer.Depth("Back");
        Engine.Tile.Layer.SetDepth("Back", -40);
        layerDepth = Engine.Tile.Layer.Depth("Back");
        dSolid = Engine.Tile.Layer.Solid("Back");
        Engine.Tile.Layer.SetSolid("Back", false);
        solidAfter = Engine.Tile.Layer.Solid("Back");
        Engine.Tile.Enabled = false;
        tilesOn = Engine.Tile.Enabled;
        """;

    private static void TilesAndUpdate(ProjectSession project, Action<string, string, string, string> row)
    {
        RoomAsset room = RoomAsset.Create("TilesRoom", RoomDimension.TwoD);
        // One solid tile, so the tile tests have something to meet until the layer is made not solid.
        room.Nodes.Add(new RoomNode { Name = "Back", Kind = RoomNodeKind.TileLayer, LayerId = room.Layers[0].Id,
            TileLayer = new RoomTileLayerData { CollisionEnabled = true, Cells = { new RoomTileCell { X = 0, Y = 0 } } } });
        using var scene = new RuntimeScene("Tile layers") { Input = new Genesis.Runtime.Input.InputState() };
        scene.Physics = Genesis.Physics.PhysicsWorld.Create(new PhysicsWorldAsset());
        scene.AddSubsystem(new Genesis.Runtime.Rendering.RoomRenderSubsystem(project.RootPath, room));
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = game;
        try
        {
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            int solidsBefore = Genesis.Runtime.Scene.RoomTileCollisionMap.Get(room, project.RootPath).SolidCount;
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = TilesScript })) host.Attach(scene.World, entity, "TilesProbe");
            int solidsAfter = Genesis.Runtime.Scene.RoomTileCollisionMap.Get(room, project.RootPath).SolidCount;
            var vars = host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot();
            double Number(string name) => vars.TryGetValue(name, out object? value) && value is not (string or null) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            string diagnostics = string.Join("; ", host.RecentDiagnostics.Select(d => d.Message));
            System.Numerics.Vector2 look = Genesis.Runtime.Input.InputSettings.Look(new System.Numerics.Vector2(1f, 1f));
            var rumble = scene.Input.GetRumble(0);
            bool pass = Number("backVisible") == 1 && Number("hidden") == 0 && Number("missing") == 0 && Number("alpha") == 1
                && Number("ox") == 12 && Number("oy") == -4 && Number("updateDistance") == 250 && scene.Environment.SimulationDistance == 250
                && Number("drawCalls") == 0 && Number("backendLength") == 0
                && Number("sensitivity") == 2 && look == new System.Numerics.Vector2(2f, -2f)
                && Math.Abs(rumble.Low - 0.5f) < 1e-4 && Math.Abs(rumble.High - 0.4f) < 1e-4
                && Math.Abs(Number("step") - 0.5) < 1e-4 && Number("slope") == 89 && Math.Abs(Number("radius") - 0.4) < 1e-4 && Number("rate") == 20
                && Number("dDepth") == 0 && Number("layerDepth") == -40 && Number("dSolid") == 1 && Number("solidAfter") == 0
                && solidsBefore == 1 && solidsAfter == 0 && Number("tilesOn") == 0;
            string detail = $"visible {Number("backVisible")} then {Number("hidden")}, missing layer {Number("missing")}, alpha {Number("alpha")} (1), offset {Number("ox")},{Number("oy")} (12,-4), update distance {Number("updateDistance")} (250), draw calls {Number("drawCalls")} and backend length {Number("backendLength")} with no renderer (0, 0), "
                + $"mouse movement 1,1 looks {look} (2,-2), rumble {rumble.Low},{rumble.High} (0.5,0.4), character step {Number("step")} (0.5), slope {Number("slope")} (89), radius {Number("radius")} (0.4), replication rate {Number("rate")} (20 without a network), "
                + $"layer depth {Number("dDepth")} then {Number("layerDepth")} (-40), solid {Number("dSolid")} then {Number("solidAfter")} with {solidsBefore} solid tile then {solidsAfter}, tiles drawn {Number("tilesOn")} (0)";
            row("Engine API parts", "tile layers, update distance and frame costs", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, $"{detail} {diagnostics}");
        }
        finally
        {
            Genesis.Runtime.Input.InputSettings.Reset();
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.BindContext(previousContext);
        }
    }

    // Engine.Water.Surface sets the look of the water the room's terrains have, read back as set.
    private const string WaterScript = """
        dWaves = Engine.Water.Surface.Waves;
        Engine.Water.Surface.Waves = 0.3;
        Engine.Water.Surface.Clarity = 0.9;
        Engine.Water.Surface.DeepColor = "#102030";
        Engine.Water.Surface.FoamWidth = 50;
        waves = Engine.Water.Surface.Waves;
        clarity = Engine.Water.Surface.Clarity;
        deep = Engine.Water.Surface.DeepColor;
        foam = Engine.Water.Surface.FoamWidth;
        """;

    private static void WaterSurface(ProjectSession project, Action<string, string, string, string> row)
    {
        var resources = new ResourceService(project);
        string terrainPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Terrain), ResourceKind.Terrain, "Water Look Vale");
        // The heights a terrain resource keeps beside it (the Terrain editor writes them on its first save).
        new Genesis.World.Terrain.TerrainAsset(33, 33, 2f, -32f, -32f, 0f, 20f).Save(terrainPath + ".gterrain");
        var nature = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(terrainPath);
        nature.WaterBodies.Add(new Genesis.World.Terrain.TerrainWaterDefinition { Name = "Tarn", Kind = Genesis.World.Terrain.TerrainWaterKind.Water });
        Genesis.World.Terrain.TerrainNatureSerializer.Save(terrainPath, nature);
        ResourceCatalog.Invalidate(project.RootPath);
        RoomAsset room = RoomAsset.Create("WaterRoom", RoomDimension.ThreeD);
        room.Nodes.Add(new RoomNode
        {
            Name = "Vale", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = Path.GetRelativePath(project.RootPath, terrainPath).Replace('\\', '/') },
        });
        using var scene = new RuntimeScene("Water look");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        var terrains = scene.AddSubsystem(new RoomTerrainSubsystem(project.RootPath, room, game));
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = game;
        try
        {
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = WaterScript })) host.Attach(scene.World, entity, "WaterProbe");
            var vars = host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot();
            double Number(string name) => vars.TryGetValue(name, out object? value) && value is not (string or null) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            string Text(string name) => vars.TryGetValue(name, out object? value) ? value as string ?? "" : "";
            int bodies = 0;
            float drawnWaves = float.NaN;
            terrains.ForEachWaterBody(body => { bodies++; drawnWaves = body.Material.WaveAmplitude; });
            bool pass = bodies == 1 && Math.Abs(Number("dWaves") - 0.18) < 1e-4 && Math.Abs(Number("waves") - 0.3) < 1e-4 && Math.Abs(drawnWaves - 0.3f) < 1e-4
                && Math.Abs(Number("clarity") - 0.9) < 1e-4 && Text("deep") == "#102030" && Number("foam") == 10;
            string detail = $"{bodies} water body; waves {Number("dWaves")} (a lake's 0.18) then {Number("waves")} (0.3, drawn {drawnWaves}), clarity {Number("clarity")} (0.9), deep {Text("deep")} (#102030), foam {Number("foam")} (10, kept in range)";
            row("Engine API parts", "the water's look", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, $"{detail} {string.Join("; ", host.RecentDiagnostics.Select(d => d.Message))}");
        }
        finally
        {
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.BindContext(previousContext);
        }
    }

    // Engine.Water.Swimming and Engine.Water.Enabled on a crate held under water: at buoyancy 2 it
    // rises, at 1 it hovers, at 0 it sinks slowed by the water, with the water off it falls freely;
    // the switch also stops a terrain's water being drawn, foam goes and comes back, and the
    // reflection's resolution and distortion keep to their ranges.
    private static void WaterSwimming(ProjectSession project, Action<string, string, string, string> row)
    {
        float Height(string script)
        {
            using var scene = new RuntimeScene("Water swimming");
            scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
            scene.Physics.EnableThreadDispatcher = false;
            scene.RegisterWaterVolume(new PhysicsWaterVolume { Minimum = new Vector3(-20, -20, -20), Maximum = new Vector3(20, 0, 20), SurfaceY = 0 });
            var game = new ProjectGameContext(project.RootPath, scene, null, null, RoomAsset.Create("Pool", RoomDimension.ThreeD), null);
            PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
            var previousGame = PgslCommands.ActiveGameContext;
            PgslCommands.ActiveGameContext = game;
            try
            {
                VMEngine.Initialize();
                var host = new ScriptHostSystem();
                host.SetContext(game);
                Entity crate = scene.World.CreateEntity();
                scene.World.Set(crate, new TransformComponent { Y = -5, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                scene.World.Set(crate, new Transform3DComponent { Position = new Vector3(0, -5, 0), Rotation = Quaternion.Identity, Scale = Vector3.One });
                scene.World.Set(crate, RigidBodyComponent.DynamicBox(new Vector3(0.5f), 1f));
                scene.Physics.RegisterEntity(scene.World, crate, ref scene.World.GetRef<RigidBodyComponent>(crate), ref scene.World.GetRef<Transform3DComponent>(crate));
                using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = script })) host.Attach(scene.World, crate, "SwimCrate");
                for (int i = 0; i < 30; i++) scene.UpdateFixed(1f / 60);
                return scene.World.GetRef<Transform3DComponent>(crate).Position.Y;
            }
            finally
            {
                PgslCommands.ActiveGameContext = previousGame;
                PgslCommands.BindContext(previousContext);
            }
        }
        float salt = Height("Engine.Water.Swimming.Buoyancy = 2;"), usual = Height(""), stone = Height("Engine.Water.Swimming.Buoyancy = 0;");
        float drained = Height("Engine.Water.Enabled = false;"), swamp = Height("Engine.Water.Swimming.Buoyancy = 0; Engine.Water.Swimming.Drag = 10;");

        // A terrain's water: not drawn with the switch off; foam off and back keeps its width.
        var resources = new ResourceService(project);
        string terrainPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Terrain), ResourceKind.Terrain, "Water Switch Vale");
        new Genesis.World.Terrain.TerrainAsset(33, 33, 2f, -32f, -32f, 0f, 20f).Save(terrainPath + ".gterrain");
        var nature = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(terrainPath);
        nature.WaterBodies.Add(new Genesis.World.Terrain.TerrainWaterDefinition { Name = "Mere", Kind = Genesis.World.Terrain.TerrainWaterKind.Water });
        Genesis.World.Terrain.TerrainNatureSerializer.Save(terrainPath, nature);
        ResourceCatalog.Invalidate(project.RootPath);
        RoomAsset room = RoomAsset.Create("SwitchRoom", RoomDimension.ThreeD);
        room.Nodes.Add(new RoomNode
        {
            Name = "Vale", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = Path.GetRelativePath(project.RootPath, terrainPath).Replace('\\', '/') },
        });
        using var valeScene = new RuntimeScene("Water switch");
        valeScene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
        var valeGame = new ProjectGameContext(project.RootPath, valeScene, null, null, room, null);
        var terrains = valeScene.AddSubsystem(new RoomTerrainSubsystem(project.RootPath, room, valeGame));
        PgslContext before = PgslCommands.BindContext(new PgslContext());
        var beforeGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = valeGame;
        bool foamOn, foamOff, foamBack, drawn, hidden;
        float width = float.NaN, widthOff = float.NaN, widthBack = float.NaN;
        double resolution, distortion;
        try
        {
            void Width(ref float into) { float seen = float.NaN; terrains.ForEachWaterBody(body => seen = body.Material.FoamWidth); into = seen; }
            foamOn = PgslCommands.WaterSurfaceFoamEnabled; Width(ref width);
            PgslCommands.WaterSurfaceFoamEnabled = false; foamOff = !PgslCommands.WaterSurfaceFoamEnabled; Width(ref widthOff);
            PgslCommands.WaterSurfaceFoamEnabled = true; foamBack = PgslCommands.WaterSurfaceFoamEnabled; Width(ref widthBack);
            drawn = terrains.WaterEnabled;
            PgslCommands.WaterEnabled = false;
            hidden = !terrains.WaterEnabled && !valeScene.Physics.WaterEnabled && !PgslCommands.WaterEnabled;
            PgslCommands.WaterReflectionsResolution = 4;
            PgslCommands.WaterReflectionsDistortion = -1;
            resolution = PgslCommands.WaterReflectionsResolution;
            distortion = PgslCommands.WaterReflectionsDistortion;
        }
        finally
        {
            PgslCommands.ActiveGameContext = beforeGame;
            PgslCommands.BindContext(before);
            Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflectionResolution = 0.5f;
            Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflectionDistortion = 1f;
        }

        bool pass = salt > -4.5f && Math.Abs(usual + 5f) < 0.1f && stone < -5.05f && drained < stone - 0.3f && swamp > stone
            && foamOn && foamOff && foamBack && width > 0 && widthOff == 0 && widthBack == width && drawn && hidden
            && resolution == 1 && distortion == 0;
        string detail = $"from -5 m: buoyancy 2 to {salt:0.##}, 1 to {usual:0.##}, 0 to {stone:0.##}, water off to {drained:0.##}, 0 in a swamp to {swamp:0.##}; " +
            $"foam {foamOn}/{foamOff}/{foamBack} width {width} then {widthOff} then {widthBack}; water drawn {drawn} then off {hidden}; reflection {resolution} (1) and {distortion} (0), kept in range";
        row("Engine API parts", "swimming, the water switch and reflections", pass ? "PASS" : "FAIL", detail);
        HeadlessHarness.Assert(pass, detail);
    }

    // "#rrggbb" the way Engine.Terrain.Grass.Color.Tip reports a colour (the command's own rounding).
    private static string HexColour(System.Numerics.Vector3 colour) =>
        $"#{(int)Math.Clamp(colour.X * 255f + 0.5f, 0, 255):x2}{(int)Math.Clamp(colour.Y * 255f + 0.5f, 0, 255):x2}{(int)Math.Clamp(colour.Z * 255f + 0.5f, 0, 255):x2}";

    // Engine.Terrain's other values on a 64 m terrain sloping 1 in 2 with a patch of layer 3: the
    // slope, facing and layer at a point, the terrains' size, and the ground, grass, scatter and
    // collision a game changes, each a multiple of the terrain's own.
    private const string TerrainLookScript = """
        slope = Engine.Terrain.SlopeAt(0, 0);
        up = Engine.Terrain.NormalY(0, 0);
        across = Engine.Terrain.NormalX(0, 0);
        patch = Engine.Terrain.MaterialAt(20, 20);
        plain = Engine.Terrain.MaterialAt(-20, -20);
        offGround = Engine.Terrain.MaterialAt(500, 500);
        wide = Engine.Terrain.Width;
        deep = Engine.Terrain.Depth;
        dGrass = Engine.Terrain.Grass.Density;
        dReach = Engine.Terrain.Collision.Distance;
        Engine.Terrain.Enabled = false;
        Engine.Terrain.Grass.Density = 2;
        Engine.Terrain.Grass.Height = 1.5;
        Engine.Terrain.Scatter.Density = 2;
        Engine.Terrain.Scatter.Distance = 0.5;
        Engine.Terrain.Scatter.Shadows = false;
        Engine.Terrain.Collision.Distance = 1000;
        Engine.Terrain.Detail.Distance = 300;
        reach = Engine.Terrain.Collision.Distance;
        gustsBefore = Engine.Terrain.Grass.Wind.Gusts;
        tipBefore = Engine.Terrain.Grass.Color.Tip;
        Engine.Terrain.Grass.Wind.Gusts = 0.5;
        Engine.Terrain.Grass.Color.Tip = "#c8b450";
        Engine.Terrain.Grass.Pushers.Set(4, 0, 0, 0, 1.5);
        pushers = Engine.Terrain.Grass.Pushers.Count;
        tipAfter = Engine.Terrain.Grass.Color.Tip;
        """;

    private static void TerrainLook(ProjectSession project, Action<string, string, string, string> row)
    {
        var resources = new ResourceService(project);
        string terrainPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Terrain), ResourceKind.Terrain, "Look Slope");
        var slope = new Genesis.World.Terrain.TerrainAsset(33, 33, 2f, -32f, -32f, 0f, 40f);
        for (int z = 0; z < 33; z++)
            for (int x = 0; x < 33; x++)
            {
                slope.SetHeight(x, z, x * 1f);
                if (x > 20 && z > 20) slope.SetLayerAt(z * 33 + x, 2);
            }
        slope.Save(terrainPath + ".gterrain");
        var nature = Genesis.World.Terrain.TerrainNatureSerializer.LoadOrDefault(terrainPath);
        nature.GrassRule.Enabled = true;
        nature.ScatterLayers.Add(new Genesis.World.Terrain.TerrainScatterLayer { Name = "Stones", DensityPerHectare = 50 });
        Genesis.World.Terrain.TerrainNatureSerializer.Save(terrainPath, nature);
        ResourceCatalog.Invalidate(project.RootPath);
        RoomAsset room = RoomAsset.Create("LookRoom", RoomDimension.ThreeD);
        room.Nodes.Add(new RoomNode
        {
            Name = "Slope", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = Path.GetRelativePath(project.RootPath, terrainPath).Replace('\\', '/') },
        });
        using var scene = new RuntimeScene("Terrain look");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        var terrains = scene.AddSubsystem(new RoomTerrainSubsystem(project.RootPath, room, game));
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = game;
        try
        {
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            var before = terrains.LookNow().Single();
            float authoredSpacing = before.Grass.Spacing, authoredNear = before.ScatterNear;
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = TerrainLookScript })) host.Attach(scene.World, entity, "LookProbe");
            var vars = host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot();
            double Number(string name) => vars.TryGetValue(name, out object? value) && value is not (string or null) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            string Text(string name) => vars.TryGetValue(name, out object? value) && value is string text ? text : "";
            var after = terrains.LookNow().Single();
            double expectedSlope = Math.Atan(0.5) * 180 / Math.PI;
            string ownTip = HexColour(nature.GrassRule.TipColor);
            bool pass = Math.Abs(Number("slope") - expectedSlope) < 0.5 && Math.Abs(Number("up") - Math.Cos(Math.Atan(0.5))) < 0.01 && Number("across") < -0.4
                && Number("patch") == 3 && Number("plain") == 1 && Number("offGround") == 0 && Number("wide") == 64 && Number("deep") == 64
                && Number("dGrass") == 1 && Number("dReach") == 24 && Number("reach") == 500
                && !terrains.GroundDrawn && Math.Abs(after.Grass.Spacing - authoredSpacing / MathF.Sqrt(2)) < 1e-3 && Math.Abs(after.Grass.MaximumScale - before.Grass.MaximumScale * 1.5f) < 1e-3
                && after.Scatter.Single().DensityPerHectare == 100 && Math.Abs(after.ScatterNear - authoredNear * 0.5f) < 1e-3 && after.ScatterShadow == 0
                && terrains.TerrainDistance == 300
                // The grass shader and pushers: each terrain's own until set, then the game's. The
                // terrain's own tip is whatever green the built-in rule defaults to (it was retuned
                // on 29 October), read back the way the command reports it, not a pinned colour.
                && Number("gustsBefore") == 0 && Text("tipBefore") == ownTip && Text("tipAfter") == "#c8b450"
                && terrains.GameGrassLook.WindGusts == 0.5f && Number("pushers") == 1 && terrains.GrassPusherCount == 1;
            string detail = $"slope {Number("slope"):0.##}° ({expectedSlope:0.##}), facing up {Number("up"):0.###} and across {Number("across"):0.###}; layer {Number("patch")} on the patch (3), {Number("plain")} elsewhere (1), {Number("offGround")} off it; " +
                $"{Number("wide")} by {Number("deep")} m; ground drawn {terrains.GroundDrawn}; grass spacing {authoredSpacing:0.###} to {after.Grass.Spacing:0.###}, height {before.Grass.MaximumScale:0.##} to {after.Grass.MaximumScale:0.##}; " +
                $"stones {after.Scatter.Single().DensityPerHectare}/ha (100), near {authoredNear} to {after.ScatterNear}, shadows to {after.ScatterShadow}; collision {Number("dReach")} to {Number("reach")} m (kept to 500); distance {terrains.TerrainDistance}; " +
                $"gusts {Number("gustsBefore")} then {terrains.GameGrassLook.WindGusts} (0.5); tip {Text("tipBefore")} ({ownTip}) then {Text("tipAfter")} (#c8b450); pushers {Number("pushers")} and {terrains.GrassPusherCount} (1)";
            row("Engine API parts", "the ground, grass, scatter and what is at a point", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, $"{detail} {string.Join("; ", host.RecentDiagnostics.Select(d => d.Message))}");
        }
        finally
        {
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.BindContext(previousContext);
        }
    }

    // Engine.Terrain.Edit in a running room (its ground starts at 10): the ground rises, falls, levels and takes paint where a
    // script says, its height reads back at once, and an edit off the terrain reports false.
    private const string TerrainEditScript = """
        before = Engine.Terrain.GetHeight(0, 0);
        raised = Engine.Terrain.Edit.Raise(0, 0, 6, 3);
        mound = Engine.Terrain.GetHeight(0, 0);
        Engine.Terrain.Edit.Raise(12, 12, 6, 4);
        Engine.Terrain.Edit.Lower(12, 12, 6, 1);
        dug = Engine.Terrain.GetHeight(12, 12);
        Engine.Terrain.Edit.Flatten(-12, -12, 5, 5);
        level = Engine.Terrain.GetHeight(-12, -12);
        painted = Engine.Terrain.Edit.Paint(0, 0, 4, 2, 1);
        offTerrain = Engine.Terrain.Edit.Raise(500, 500, 6, 3);
        dDetail = Engine.Terrain.Detail.Quality;
        Engine.Terrain.Detail.Quality = 9;
        detail = Engine.Terrain.Detail.Quality;
        """;

    private static void TerrainEdits(ProjectSession project, Action<string, string, string, string> row)
    {
        var resources = new ResourceService(project);
        string terrainPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Terrain), ResourceKind.Terrain, "Edit Vale");
        new Genesis.World.Terrain.TerrainAsset(33, 33, 2f, -32f, -32f, 0f, 20f).Save(terrainPath + ".gterrain");
        ResourceCatalog.Invalidate(project.RootPath);
        RoomAsset room = RoomAsset.Create("EditRoom", RoomDimension.ThreeD);
        room.Nodes.Add(new RoomNode
        {
            Name = "Vale", Kind = RoomNodeKind.Terrain, LayerId = room.Layers[0].Id,
            Terrain = new RoomTerrainData { Asset = Path.GetRelativePath(project.RootPath, terrainPath).Replace('\\', '/') },
        });
        using var scene = new RuntimeScene("Terrain edits");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        var terrains = scene.AddSubsystem(new RoomTerrainSubsystem(project.RootPath, room, game));
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = game;
        try
        {
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = TerrainEditScript })) host.Attach(scene.World, entity, "GroundProbe");
            var vars = host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot();
            double Number(string name) => vars.TryGetValue(name, out object? value) && value is not (string or null) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            bool Flag(string name) => vars.TryGetValue(name, out object? value) && value is true;
            var terrain = terrains.LoadedTerrains.Single();
            var splat = terrain.GetSplat(16, 16);
            double ground = Number("before");
            bool pass = Flag("raised") && Math.Abs(Number("mound") - ground - 3) < 0.05
                && Math.Abs(Number("dug") - ground - 3) < 0.05 && Math.Abs(Number("level") - 5) < 0.05
                && Flag("painted") && splat.G > 240 && !Flag("offTerrain") && terrains.GroundEdits == 5
                && Number("dDetail") == 1 && Number("detail") == 4 && Math.Abs(terrains.TerrainDetail - 4f) < 1e-4;
            string detail = $"ground {Number("before"):0.##} raised to {Number("mound"):0.##} (+3), raised 4 and lowered 1 to {Number("dug"):0.##} (+3), levelled to {Number("level"):0.##} (5); " +
                $"layer 2 painted {splat.G}/255, off the terrain {Flag("offTerrain")}, {terrains.GroundEdits} edits (5); detail {Number("dDetail")} then {Number("detail")} (4, kept in range)";
            row("Engine API parts", "the ground changed as it plays", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, $"{detail} {string.Join("; ", host.RecentDiagnostics.Select(d => d.Message))}");
        }
        finally
        {
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.BindContext(previousContext);
            Genesis.Runtime.Project.RoomTerrainSubsystem.GameTerrainDetail = 1f;
        }
    }

    // Engine.Particle.Emitter on an instance with an explosion attached: each value reads its default,
    // the running effect takes the change (size, life, gravity, colour times the effect's own), and
    // Engine.Particle.Quality thins every effect's rate.
    private const string ParticleLookScript = """
        dSize = Engine.Particle.Emitter.Size;
        dColor = Engine.Particle.Emitter.Color;
        dQuality = Engine.Particle.Quality;
        Engine.Particle.Emitter.Size = 2;
        Engine.Particle.Emitter.Life = 0.5;
        Engine.Particle.Emitter.Gravity = 0;
        Engine.Particle.Emitter.Color = "#ff0000";
        Engine.Particle.Emitter.Speed = 50;
        Engine.Particle.Emitter.Rate = 40;
        Engine.Particle.Quality = 0.5;
        size = Engine.Particle.Emitter.Size;
        emitSpeed = Engine.Particle.Emitter.Speed;
        color = Engine.Particle.Emitter.Color;
        on = Engine.Particle.Emitter.Enabled;
        """;

    private static void ParticleEmitterLook(ProjectSession project, Action<string, string, string, string> row)
    {
        RoomAsset room = RoomAsset.Create("ParticleRoom", RoomDimension.ThreeD);
        using var scene = new RuntimeScene("Particle look");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        using var effects = new Genesis.Runtime.Rendering.ObjectCompositionSubsystem(project.RootPath);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = game;
        try
        {
            VMEngine.Initialize();
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            scene.World.Set(entity, new Genesis.Runtime.ECS.Components.ParticleComponent { Asset = "builtin://Explosion", ParticleTypeId = -1, RateScale = 1f, FollowEntity = true, Emitting = true });
            var time = new Genesis.Runtime.Core.GameTime();
            time.Advance(1f / 30f);
            effects.Update(scene, time);
            var authored = effects.RunningEmitter(entity)?.CloneEmitter();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = ParticleLookScript })) host.Attach(scene.World, entity, "SparkProbe");
            time.Advance(1f / 30f);
            effects.Update(scene, time);
            var running = effects.RunningEmitter(entity);
            var vars = host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot();
            double Number(string name) => vars.TryGetValue(name, out object? value) && value is not (string or null) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            string Text(string name) => vars.TryGetValue(name, out object? value) ? value as string ?? "" : "";
            bool pass = authored != null && running != null
                && Number("dSize") == 1 && Text("dColor") == "#ffffff" && Number("dQuality") == 1
                && Number("size") == 2 && Number("emitSpeed") == 10 && Text("color") == "#ff0000" && vars.TryGetValue("on", out object? on) && on is true
                && Math.Abs(running.StartSize - authored.StartSize * 2) < 1e-6 && Math.Abs(running.Lifetime - authored.Lifetime * 0.5) < 1e-6
                && running.Gravity == 0 && running.StartColor.G == 0 && Math.Abs(running.StartColor.R - authored.StartColor.R) < 1e-6
                && Math.Abs(running.EmitRate - 20) < 1e-6;
            string detail = authored == null || running == null ? "the explosion did not run" :
                $"size {Number("dSize")} then {Number("size")} (start size {authored.StartSize:0.###} to {running.StartSize:0.###}), life {authored.Lifetime:0.###} to {running.Lifetime:0.###}, " +
                $"gravity {authored.Gravity:0.###} to {running.Gravity:0.###}, colour {Text("dColor")} then {Text("color")} (green {running.StartColor.G:0.##}), speed kept to {Number("emitSpeed")} (10), " +
                $"rate 40 at quality 0.5 runs at {running.EmitRate:0.##} (20)";
            row("Engine API parts", "a particle emitter's look and quality", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, $"{detail} {string.Join("; ", host.RecentDiagnostics.Select(d => d.Message))}");
        }
        finally
        {
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.BindContext(previousContext);
            Genesis.Runtime.ECS.Components.ParticleQuality.Reset();
        }
    }

    // Engine.Instance.Enabled: an instance that freezes itself in Create counts no Steps while the
    // other counts five; turned back on, it counts the next three.
    private static void InstanceFreeze(ProjectSession project, Action<string, string, string, string> row)
    {
        using var scene = new RuntimeScene("Instance freeze");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, RoomAsset.Create("Freeze", RoomDimension.ThreeD), null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = game;
        try
        {
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            Entity Counter(string name, string create)
            {
                Entity counter = scene.World.CreateEntity();
                scene.World.Set(counter, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = create, ["Step"] = "steps += 1;" })) host.Attach(scene.World, counter, name);
                return counter;
            }
            Entity statue = Counter("FrozenCounter", "steps = 0; Engine.Instance.Enabled = false; frozenRead = Engine.Instance.Enabled;");
            Entity walker = Counter("LiveCounter", "steps = 0;");
            host.BeginRoom(beginGame: true);
            for (int i = 0; i < 5; i++) host.Update(1f / 60);
            double Steps(Entity entity) => host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot()
                .TryGetValue("steps", out object? value) && value is not (string or null) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            double frozenSteps = Steps(statue), liveSteps = Steps(walker);
            bool readFrozen = host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == statue).GetVariablesSnapshot()
                .TryGetValue("frozenRead", out object? read) && read is false;
            PgslContext back = PgslCommands.BindContext(new PgslContext { InstanceId = statue.Id });
            PgslCommands.InstanceEnabled = true;
            PgslCommands.BindContext(back);
            for (int i = 0; i < 3; i++) host.Update(1f / 60);
            double thawed = Steps(statue);
            bool pass = frozenSteps == 0 && liveSteps == 5 && readFrozen && thawed == 3;
            string detail = $"frozen counted {frozenSteps} (0) and read itself off {readFrozen}, the other {liveSteps} (5); turned on, it counted {thawed} (3)";
            row("Engine API parts", "an instance frozen and back", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, $"{detail} {string.Join("; ", host.RecentDiagnostics.Select(d => d.Message))}");
        }
        finally
        {
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.BindContext(previousContext);
        }
    }

    // Engine.Particle's switches: off, a running effect stops (it does not age); bursts off start
    // none; 2D particles off start none; collisions off make the running effect pass through the
    // world and on again meet it as it was made to.
    private static void ParticleSwitches(ProjectSession project, Action<string, string, string, string> row)
    {
        using var scene = new RuntimeScene("Particle switches");
        using var effects = new Genesis.Runtime.Rendering.ObjectCompositionSubsystem(project.RootPath);
        try
        {
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            // An effect whose particles bounce off the ground.
            Directory.CreateDirectory(Path.Combine(project.RootPath, "Assets"));
            File.WriteAllText(Path.Combine(project.RootPath, "Assets", "Bouncing.particle.json"), "{\"emitRate\":20,\"collisionMode\":1}");
            ResourceCatalog.Invalidate(project.RootPath);
            scene.World.Set(entity, new Genesis.Runtime.ECS.Components.ParticleComponent { Asset = "Assets/Bouncing.particle.json", ParticleTypeId = -1, RateScale = 1f, FollowEntity = true, Emitting = true });
            var time = new Genesis.Runtime.Core.GameTime();
            void Step() { time.Advance(1f / 30f); effects.Update(scene, time); }
            Step();
            float Age() => scene.World.GetRef<Genesis.Runtime.ECS.Components.ParticleComponent>(entity).Age;
            float ageOn = Age();
            PgslCommands.ParticleEnabledPart = false;
            Step(); Step();
            float ageOff = Age();
            PgslCommands.ParticleEnabledPart = true;
            Step();
            bool stopped = ageOn > 0 && ageOff == ageOn && Age() > ageOff;

            var authored = effects.RunningEmitter(entity)!.CollisionMode;
            PgslCommands.ParticleGpuCollisionEnabled = false;
            Step();
            var passing = effects.RunningEmitter(entity)!.CollisionMode;
            PgslCommands.ParticleGpuCollisionEnabled = true;
            Step();
            var back = effects.RunningEmitter(entity)!.CollisionMode;

            PgslCommands.ParticleBurstsEnabled = false;
            Entity none = Genesis.Runtime.Particles.ParticleBursts.Play(scene.World, "builtin://Explosion", Vector3.Zero);
            bool batchedNone = !Genesis.Runtime.Particles.ParticleBursts.PlayBatched(scene.World, "builtin://Explosion", Vector3.Zero);
            PgslCommands.ParticleBurstsEnabled = true;
            Entity some = Genesis.Runtime.Particles.ParticleBursts.Play(scene.World, "builtin://Explosion", Vector3.Zero);

            using var room2D = new Genesis.Runtime.Rendering.RoomEffects2D();
            PgslCommands.ParticleParticles2DEnabled = false;
            room2D.Emit(project.RootPath, "builtin://Sparks", 10, 10, 20, 0);
            int off2D = room2D.ParticleCount;
            PgslCommands.ParticleParticles2DEnabled = true;
            room2D.Emit(project.RootPath, "builtin://Sparks", 10, 10, 20, 0);
            int on2D = room2D.ParticleCount;

            bool pass = stopped && authored == Genesis.Runtime.Particles.ParticleCollisionMode.Bounce && passing == Genesis.Runtime.Particles.ParticleCollisionMode.None && back == authored
                && none.IsNull && batchedNone && !some.IsNull && off2D == 0 && on2D > 0;
            string detail = $"age {ageOn:0.###} held at {ageOff:0.###} while off, then {Age():0.###}; collision {authored} to {passing} and back to {back}; " +
                $"bursts off made {(none.IsNull ? "none" : "one")} (batched none {batchedNone}), on {(some.IsNull ? "none" : "one")}; 2D particles off {off2D}, on {on2D}";
            row("Engine API parts", "the particle switches", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, detail);
        }
        finally
        {
            Genesis.Runtime.ECS.Components.ParticleQuality.Reset();
        }
    }

    // Data structures belong to the instance that made them: another instance using the handle reads an empty
    // list and says nothing (as ever, with the check off); with Engine.Game.Scripts.DataOwnerChecks on, a read, a
    // write and a free of a number another instance holds is an error, and the owner, and a number nobody holds, are not.
    private static void DataOwnerChecks(Action<string, string, string, string> row)
    {
        PgslContext maker = new() { InstanceId = 11 }, other = new() { InstanceId = 12 };
        PgslContext? previous = PgslCommands.BindContext(maker);
        bool was = PgslCommands.DataOwnerChecks;
        string Attempt(Action use)
        {
            try { use(); return ""; }
            catch (InvalidOperationException error) { return error.Message; }
        }
        try
        {
            PgslCommands.DataOwnerChecks = false;
            double first = PgslCommands.DsListCreate();
            PgslCommands.DsListAdd(first, 5);
            PgslCommands.BindContext(other);
            string offRead = Attempt(() => PgslCommands.DsListSize(first)), offWrite = Attempt(() => PgslCommands.DsListAdd(first, 1));
            double offSize = PgslCommands.DsListSize(first);
            bool silent = offRead.Length == 0 && offWrite.Length == 0 && offSize == 0;

            PgslCommands.DataOwnerChecks = true;
            PgslCommands.BindContext(maker);
            double owned = PgslCommands.DsListCreate();
            double grid = PgslCommands.DsGridCreate(4, 4);
            PgslCommands.DsListAdd(owned, 7);
            PgslCommands.BindContext(other);
            double mine = PgslCommands.DsListCreate();
            PgslCommands.DsListAdd(mine, 3);
            string read = Attempt(() => PgslCommands.DsListSize(owned)), write = Attempt(() => PgslCommands.DsListAdd(owned, 1)),
                free = Attempt(() => PgslCommands.DsListDestroy(owned)), gridWrite = Attempt(() => PgslCommands.DsGridSet(grid, 1, 1, 5));
            bool errors = new[] { read, write, free, gridWrite }.All(message => message.Contains("belong to the instance that made them", StringComparison.Ordinal));
            // Its own, and a number no instance holds: no error.
            bool fine = Attempt(() => PgslCommands.DsListAdd(mine, 4)) == "" && PgslCommands.DsListSize(mine) == 2 && Attempt(() => PgslCommands.DsListSize(9999)) == "";
            // The owner still has it, untouched by the refused writes, and may free it; once freed, nobody holds the number.
            PgslCommands.BindContext(maker);
            bool owner = PgslCommands.DsListSize(owned) == 1 && Attempt(() => PgslCommands.DsListDestroy(owned)) == "" && Attempt(() => PgslCommands.DsListSize(owned)) == "";
            PgslCommands.DataOwnerChecks = false;
            bool pass = silent && errors && fine && owner;
            string detail = $"off: the other instance read {offSize} and wrote with no word ({silent}); on: errors for read/write/free/grid {errors}, its own and an unheld number fine {fine}, the owner's use and free {owner}";
            row("Engine API parts", "a data structure handle used in another instance is an error when asked", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, detail + $" [read '{read}', write '{write}', free '{free}', grid '{gridWrite}']");
        }
        finally
        {
            PgslCommands.DataOwnerChecks = was;
            PgslCommands.BindContext(previous);
        }
    }

    // ParticleBurstAtDir turns the effect's up to the direction (the emitter's turn carries its particles,
    // as the composition makes the emitter's world matrix), a zero or broken direction plays it upright;
    // DrawBeam3D reaches the draw surface with its ends, width, colour and strength, in a 3D pass only.
    private static void DirectedBurstsAndBeams(Action<string, string, string, string> row)
    {
        using var scene = new RuntimeScene("Directed bursts");
        const float ToRadians = MathF.PI / 180f;
        List<string> wrong = [];
        Vector3 position = new(1f, 2f, 3f);
        foreach (Vector3 direction in new[]
        {
            Vector3.UnitY, -Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ, new Vector3(0f, 0f, 5f), new Vector3(1f, 2f, -3f), new Vector3(-0.3f, -0.2f, 0.9f), new Vector3(-4f, 0.5f, 0f),
        })
        {
            Entity burst = Genesis.Runtime.Particles.ParticleBursts.Play(scene.World, "builtin://Explosion", position, 1f, 0f, direction);
            if (burst.IsNull) { wrong.Add($"{direction}: no burst"); continue; }
            TransformComponent turn = scene.World.GetRef<TransformComponent>(burst);
            // The emitter's world matrix as ObjectCompositionSubsystem.ResolveParticleWorld makes it.
            Matrix4x4 world = Matrix4x4.CreateRotationX(turn.RotationX * ToRadians) * Matrix4x4.CreateRotationY(turn.RotationY * ToRadians)
                * Matrix4x4.CreateRotationZ((turn.RotationZ + turn.Rotation) * ToRadians);
            Vector3 up = Vector3.TransformNormal(Vector3.UnitY, world), wanted = Vector3.Normalize(direction);
            if (Vector3.Distance(up, wanted) > 1e-3f || turn.X != position.X || turn.Y != position.Y || turn.Z != position.Z)
                wrong.Add($"{direction}: up {up} at {turn.X},{turn.Y},{turn.Z}");
        }
        foreach (Vector3? direction in new Vector3?[] { null, Vector3.Zero, new Vector3(float.NaN, 1f, 0f), new Vector3(float.PositiveInfinity, 0f, 0f) })
        {
            Entity burst = Genesis.Runtime.Particles.ParticleBursts.Play(scene.World, "builtin://Explosion", position, 1f, 0f, direction);
            TransformComponent turn = scene.World.GetRef<TransformComponent>(burst);
            if (turn.RotationX != 0f || turn.RotationY != 0f || turn.RotationZ != 0f) wrong.Add($"{(direction?.ToString() ?? "none")} should play upright, turned {turn.RotationX},{turn.RotationY},{turn.RotationZ}");
        }

        PgslRecordingDrawSurface surface = new() { Is3DActive = true };
        PgslContext? previous = PgslCommands.BindContext(new PgslContext { DrawSurface = surface });
        try
        {
            PgslCommands.DrawSetColorRgb(255, 128, 0);
            PgslCommands.DrawSetAlpha(0.5);
            PgslCommands.DrawBeam3D(0, 1, 2, 10, 1, 2, 0.05);
            PgslCommands.DrawBeam3D(1, 1, 1, 2, 2, 2, -3);
            PgslCommands.DrawBeam3D(0, 0, 0, double.NaN, 1, 1, 0.1);
            surface.Is3DActive = false;
            PgslCommands.DrawBeam3D(0, 0, 0, 1, 1, 1, 0.1);
        }
        finally { PgslCommands.BindContext(previous); }
        var beams = surface.Beams;
        bool beamsRight = beams.Count == 2
            && beams[0] is { X1: 0, Y1: 1, Z1: 2, X2: 10, Y2: 1, Z2: 2, Alpha: 0.5f } first && Math.Abs(first.Width - 0.05f) < 1e-6f
            && first.Color.R == 255 && first.Color.G == 128 && first.Color.B == 0 && Math.Abs(beams[1].Width - 3f) < 1e-6f;
        bool pass = wrong.Count == 0 && beamsRight;
        string detail = wrong.Count == 0 ? $"eight directions carried the effect's up to them, four broken ones played upright; {beams.Count} beams recorded"
            : string.Join("; ", wrong) + $"; {beams.Count} beams recorded";
        row("Engine API parts", "ParticleBurstAtDir turns the effect, DrawBeam3D reaches the surface", pass ? "PASS" : "FAIL", detail);
        HeadlessHarness.Assert(pass, detail + (beamsRight ? "" : " (the beams recorded were not as asked for)"));
    }

    // Engine.Navigation.Mesh shapes the next bake, Engine.Navigation.Baked reads it, and an agent's
    // Engine.Navigation.Avoidance.Priority decides who gives way.
    private const string NavigationScript = """
        dRadius = Engine.Navigation.Mesh.AgentRadius;
        dSlope = Engine.Navigation.Mesh.MaxSlope;
        dCell = Engine.Navigation.Mesh.CellSize;
        dBaked = Engine.Navigation.Baked;
        dPriority = Engine.Navigation.Avoidance.Priority;
        dKeep = Engine.Navigation.Avoidance.Radius;
        Engine.Navigation.Mesh.AgentRadius = 0.8;
        Engine.Navigation.Mesh.AgentHeight = 2.5;
        Engine.Navigation.Mesh.MaxSlope = 120;
        Engine.Navigation.Mesh.StepHeight = 0.3;
        Engine.Navigation.Mesh.CellSize = 2;
        Engine.Navigation.Bake();
        baked = Engine.Navigation.Baked;
        Engine.Navigation.Avoidance.Priority = 90;
        Engine.Navigation.Avoidance.Radius = 0.6;
        priority = Engine.Navigation.Avoidance.Priority;
        keep = Engine.Navigation.Avoidance.Radius;
        """;

    private static void NavigationParts(ProjectSession project, Action<string, string, string, string> row)
    {
        RoomAsset room = RoomAsset.Create("NavigationRoom", RoomDimension.ThreeD);
        room.Settings.Width = 64;
        room.Settings.Depth = 48;
        using var scene = new RuntimeScene("Navigation parts");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = game;
        try
        {
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = NavigationScript })) host.Attach(scene.World, entity, "NavigationProbe");
            var vars = host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot();
            double Number(string name) => vars.TryGetValue(name, out object? value) && value is not (string or null) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            bool Flag(string name) => vars.TryGetValue(name, out object? value) && value is true;
            var mesh = PgslCommands.NavigationMeshData;

            // Two agents face to face: alike, each steers aside as hard as the other; at 90 against 50
            // the higher one barely moves and the lower one gives way.
            static (float First, float Second) Steering(float firstPriority, float secondPriority)
            {
                var solver = new Genesis.Runtime.Navigation.CrowdNavigationSolver();
                solver.Solve([
                    new Genesis.Runtime.Navigation.CrowdAgentSample(1, new Vector3(0, 0, 0), Vector3.Zero, .35f, 3f, .75f, 12, true, Vector3.Zero, firstPriority),
                    new Genesis.Runtime.Navigation.CrowdAgentSample(2, new Vector3(.5f, 0, 0), Vector3.Zero, .35f, 3f, .75f, 12, true, Vector3.Zero, secondPriority),
                ]);
                solver.TryGetSteering(1, out Vector3 first);
                solver.TryGetSteering(2, out Vector3 second);
                return (first.Length(), second.Length());
            }
            (float alikeFirst, float alikeSecond) = Steering(50, 50);
            (float guard, float crowd) = Steering(90, 50);

            // Two agents sent across the room: one switched off holds where it is; the other holds while
            // navigation is off and walks once it is back on.
            Entity Walker(string name, string create)
            {
                Entity walker = scene.World.CreateEntity();
                scene.World.Set(walker, new TransformComponent { X = 10, Z = 10, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = create })) host.Attach(scene.World, walker, name);
                return walker;
            }
            Entity held = Walker("HeldAgent", "Engine.Navigation.Agent.Enabled = false; NavMeshAgentSetDestination(40, 0, 30);");
            Entity walking = Walker("WalkingAgent", "NavMeshAgentSetDestination(40, 0, 30);");
            float Along(Entity walker) => scene.World.GetRef<TransformComponent>(walker).X;
            PgslCommands.NavigationEnabled = false;
            for (int i = 0; i < 10; i++) PgslCommands.UpdateNavigation(scene, 1f / 30);
            float pausedAt = Along(walking);
            PgslCommands.NavigationEnabled = true;
            for (int i = 0; i < 30; i++) PgslCommands.UpdateNavigation(scene, 1f / 30);
            float heldAt = Along(held), walkedTo = Along(walking);

            bool pass = Number("dRadius") == 0.35 && Number("dSlope") == 45 && Number("dCell") == 0 && !Flag("dBaked")
                && Number("dPriority") == 50 && Math.Abs(Number("dKeep") - 0.35) < 1e-6 && Flag("baked")
                && mesh != null && Math.Abs(mesh.AgentRadius - 0.8f) < 1e-6 && Math.Abs(mesh.AgentHeight - 2.5f) < 1e-6
                && mesh.MaxSlopeDegrees == 89 && Math.Abs(mesh.MaxStep - 0.3f) < 1e-6 && mesh.CellSize == 2 && mesh.Width == 32 && mesh.Depth == 24
                && Number("priority") == 90 && Math.Abs(Number("keep") - 0.6) < 1e-6
                && Math.Abs(alikeFirst - .75f) < 1e-4 && Math.Abs(alikeSecond - .75f) < 1e-4 && guard < .2f && crowd > 1.3f
                && heldAt == 10 && pausedAt == 10 && walkedTo > 11;
            string detail = $"bake agent {Number("dRadius")} m, {Number("dSlope")}°, cell {Number("dCell")} (auto); baked {Flag("dBaked")} then {Flag("baked")}; " +
                (mesh == null ? "no mesh; " : $"mesh for {mesh.AgentRadius} m by {mesh.AgentHeight} m, {mesh.MaxSlopeDegrees}° (89, kept in range), {mesh.MaxStep} m steps, {mesh.Width}x{mesh.Depth} cells of {mesh.CellSize} m; ") +
                $"priority {Number("dPriority")} then {Number("priority")}, room kept {Number("dKeep"):0.###} then {Number("keep"):0.###}; " +
                $"alike both steer {alikeFirst:0.##}/{alikeSecond:0.##} (0.75), at 90 against 50 {guard:0.##} and {crowd:0.##}; " +
                $"agent off held at x {heldAt}, the other at {pausedAt} while navigation was off and then walked to {walkedTo:0.##}";
            row("Engine API parts", "navigation bake and who gives way", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, $"{detail} {string.Join("; ", host.RecentDiagnostics.Select(d => d.Message))}");
        }
        finally
        {
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.BindContext(previousContext);
            PgslCommands.ResetNavigationBake();
            PgslCommands.ClearNavigation(scene);
        }
    }

    // Engine.Weather's parts in a rainy room: each follows the weather until set, then holds whatever
    // the weather does; a part turned off holds its amount at 0; Release() hands them back.
    private const string WeatherScript = """
        dWind = Engine.Weather.Wind.Strength;
        dRain = Engine.Weather.Rain.Amount;
        dCelsius = Engine.Weather.Temperature.Celsius;
        Engine.Weather.Wind.Strength = 0;
        Engine.Weather.Wind.Direction = 450;
        Engine.Weather.Rain.Amount = 2;
        Engine.Weather.Rain.Wetness = 0.5;
        Engine.Weather.Snow.Enabled = false;
        Engine.Weather.Snow.Amount = 0.4;
        Engine.Weather.Temperature.Celsius = -5;
        wind = Engine.Weather.Wind.Strength;
        windTowards = Engine.Weather.Wind.Direction;
        rain = Engine.Weather.Rain.Amount;
        wet = Engine.Weather.Rain.Wetness;
        snowOn = Engine.Weather.Snow.Enabled;
        snowKept = Engine.Weather.Snow.Amount;
        """;

    private static void WeatherParts(ProjectSession project, Action<string, string, string, string> row)
    {
        RoomAsset room = RoomAsset.Create("WeatherRoom", RoomDimension.ThreeD);
        using var scene = new RuntimeScene("Weather parts");
        scene.ConfigureClimate(new Genesis.Runtime.Climate.EnvironmentOptions { AutomaticWeather = false }, Genesis.Runtime.Climate.WeatherKind.Rain);
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = game;
        try
        {
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            Entity entity = scene.World.CreateEntity();
            scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = WeatherScript })) host.Attach(scene.World, entity, "WeatherProbe");
            var vars = host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot();
            double Number(string name) => vars.TryGetValue(name, out object? value) && value is not (string or bool or null) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            bool Flag(string name) => vars.TryGetValue(name, out object? value) && value is true;
            string diagnostics = string.Join("; ", host.RecentDiagnostics.Select(d => d.Message));
            void Check(string name, bool pass, string detail)
            {
                row("Engine API parts", name, pass ? "PASS" : "FAIL", detail);
                HeadlessHarness.Assert(pass, $"{name}: {detail} {diagnostics}");
            }

            Genesis.Runtime.Climate.EnvironmentService climate = scene.Climate;
            climate.Update(0.1f, Vector3.Zero);
            Genesis.Runtime.Climate.EnvironmentFrame held = climate.Current;
            Check("weather values follow the weather until set",
                Math.Abs(Number("dWind") - 5.2) < 1e-4 && Math.Abs(Number("dRain") - 0.72) < 1e-4 && Math.Abs(Number("dCelsius") - 11) < 1e-4,
                $"wind {Number("dWind")} (5.2), rain {Number("dRain")} (0.72), temperature {Number("dCelsius")} (11) in rain");
            Check("weather values hold what a script sets, kept in range",
                Number("wind") == 0 && Math.Abs(Number("windTowards") - 90) < 1e-4 && Number("rain") == 1 && Math.Abs(Number("wet") - 0.5) < 1e-3
                && !Flag("snowOn") && Math.Abs(Number("snowKept") - 0.4) < 1e-4
                && held.Weather.WindSpeed == 0 && held.LocalWind.Length() < 1e-6 && held.Weather.Rain == 1 && held.Weather.Snow == 0
                && Math.Abs(held.Weather.TemperatureC + 5) < 1e-4 && Math.Abs(held.Weather.WindDirection.X - 1) < 1e-4,
                $"script read wind {Number("wind")}, direction {Number("windTowards")} (90), rain {Number("rain")} (1), wetness {Number("wet")}, snow on {Flag("snowOn")} keeping {Number("snowKept")}; "
                + $"the world had wind {held.Weather.WindSpeed} towards {held.Weather.WindDirection}, rain {held.Weather.Rain}, snow {held.Weather.Snow}, {held.Weather.TemperatureC} C");
            PgslCommands.WeatherRelease();
            climate.Update(0.1f, Vector3.Zero);
            Check("Engine.Weather.Release() hands the values back to the weather",
                Math.Abs(climate.Current.Weather.WindSpeed - 5.2f) < 1e-4 && Math.Abs(climate.Current.Weather.Rain - 0.72f) < 1e-4
                && Math.Abs(climate.Current.Weather.TemperatureC - 11) < 1e-4 && PgslCommands.WeatherSnowEnabled,
                $"wind {climate.Current.Weather.WindSpeed}, rain {climate.Current.Weather.Rain}, {climate.Current.Weather.TemperatureC} C, snow on {PgslCommands.WeatherSnowEnabled}");
        }
        finally
        {
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.BindContext(previousContext);
        }
    }

    // Engine.Game.Threads shares the culling of a frame's Objects between threads: what is drawn, in
    // what order, and how many are left out are the same as on one thread.
    private static void SharedCulling(Action<string, string, string, string> row)
    {
        using var scene = new RuntimeScene("Shared culling");
        var world = scene.World;
        var entities = new List<Entity>();
        bool frustumCulling = Genesis.Shared.Rendering.RenderAutoState.FrustumCulling;
        bool occlusionCulling = Genesis.Shared.Rendering.RenderAutoState.OcclusionCulling;
        try
        {
            // 2000 Objects on a grid in front of the camera and well beyond its sides.
            for (int i = 0; i < 2000; i++)
            {
                Entity entity = world.CreateEntity();
                float x = (i % 50 - 25) * 4f, z = -10f - i / 50 * 4f;
                world.Set(entity, new TransformComponent { X = x, Z = z, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                world.Set(entity, new Genesis.Runtime.ECS.Components.Draw3DComponent { Visible = true, Procedural = true });
                Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.Remove(entity);
                Genesis.Runtime.Rendering.ProceduralMeshDrawRegistry.Set(entity,
                    [new Genesis.Shared.Interfaces.MeshDrawCall { SurfaceParams = new Vector4(x, z, 0, 0) }]);
                entities.Add(entity);
            }
            Genesis.Shared.Rendering.RenderAutoState.FrustumCulling = true;
            Genesis.Shared.Rendering.RenderAutoState.OcclusionCulling = false;
            Vector3 eye = new(0, 2, 0), forward = -Vector3.UnitZ;
            Matrix4x4 viewProjection = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY)
                * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 16f / 9f, 0.1f, 120f);
            var renderer = new EditorInteractionRenderProbe();
            string Submit(int threads, out int culled)
            {
                Genesis.Shared.Threading.EngineJobs.ThreadLimit = threads;
                int before = Genesis.Shared.Rendering.RenderAutoState.CulledByFrustum;
                var draws = new Genesis.Shared.Interfaces.MeshDrawCall[4096];
                int count = 0;
                Genesis.Runtime.Rendering.ObjectDrawPass.SubmitMeshes3D(world, "", draws, ref count, renderer, eye, forward, viewProjection);
                culled = Genesis.Shared.Rendering.RenderAutoState.CulledByFrustum - before;
                return string.Join(";", draws.Take(count).Select(draw => $"{draw.SurfaceParams.X},{draw.SurfaceParams.Y}"));
            }

            string one = Submit(1, out int culledOne);
            string shared = Submit(0, out int culledShared);
            int threads = Genesis.Shared.Threading.EngineJobs.Threads;
            int drawn = one.Length == 0 ? 0 : one.Split(';').Length;
            bool pass = one == shared && culledOne == culledShared && drawn > 0 && culledOne > 0 && drawn + culledOne == entities.Count
                && (Environment.ProcessorCount < 3 || threads > 1);
            string detail = $"{threads} threads: {drawn} drawn and {culledOne} left out on one thread, the same {(one == shared ? "draws" : "NOT the same draws")} and {culledShared} left out shared";
            row("Threads", "culling shared between threads draws the same", pass ? "PASS" : "FAIL", detail);
            HeadlessHarness.Assert(pass, detail);
        }
        finally
        {
            Genesis.Shared.Threading.EngineJobs.ThreadLimit = 0;
            Genesis.Shared.Rendering.RenderAutoState.FrustumCulling = frustumCulling;
            Genesis.Shared.Rendering.RenderAutoState.OcclusionCulling = occlusionCulling;
            foreach (Entity entity in entities) Genesis.Runtime.Rendering.ProceduralMeshDrawRegistry.Remove(entity);
        }
    }

    // A wall painted three tiles long takes its two ends and its middle from an autotiled tile set;
    // erasing the middle leaves two single tiles. Then the acoustics: set, refused, clamped, and played.
    private static void AutotilesAndAcoustics(ProjectSession project, Action<string, string, string, string> row)
    {
        var resources = new ResourceService(project);
        string tiles = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, "AutotileWalls");
        var session = new Genesis.Application.Core.Images.ImageDocumentSession(
            Genesis.Application.Core.Images.ImageDocumentSerializer.LoadAtomic(tiles).Document, tiles, Genesis.Application.Core.Images.ImageDocumentAccess.Editor);
        session.Document.Usage.Allowed = Genesis.Application.Core.Images.ImageUsage.Tileset;
        session.Document.Usage.Tileset.TileWidth = session.Document.Usage.Tileset.TileHeight = 16;
        session.Document.Usage.Tileset.Autotile = true;
        session.Document.Usage.Tileset.AutotileColumn = 1;
        Genesis.Application.Editors.Image.Imaging.ImageWorkspaceStorage.Save(session,
            Genesis.Application.Editors.Image.Imaging.ImageWorkspace.CreateBlank(96, 64, System.Drawing.Color.Gray));
        ResourceCatalog.Invalidate(project.RootPath);

        RoomAsset room = RoomAsset.Create("AutotileRoom", RoomDimension.TwoD);
        var walls = new RoomNode
        {
            Name = "Walls", Kind = RoomNodeKind.TileLayer,
            TileLayer = new RoomTileLayerData { Tileset = ResourceCatalog.Name(project.RootPath, tiles), CellWidth = 16, CellHeight = 16 },
        };
        room.Nodes.Add(walls);
        using var scene = new RuntimeScene("Autotiles");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, room, null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        string previousPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game;
        PgslCommands.ProjectPath = project.RootPath;
        try
        {
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            IReadOnlyDictionary<string, object> Run(string name, string code)
            {
                Entity entity = scene.World.CreateEntity();
                scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = code })) host.Attach(scene.World, entity, name);
                return host.Instances.OfType<PgslBehavior>().Single(behavior => behavior.Entity == entity).GetVariablesSnapshot();
            }
            double Number(IReadOnlyDictionary<string, object> vars, string name) =>
                vars.TryGetValue(name, out object? value) && value is not string ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            string Tile(int x) => walls.TileLayer.Cells.FirstOrDefault(cell => cell.X == x && cell.Y == 0) is { } cell ? $"{cell.TileX},{cell.TileY}" : "none";
            string diagnostics() => string.Join("; ", host.RecentDiagnostics.Select(d => d.Message));
            void Check(string name, bool pass, string detail)
            {
                row("Autotiles and acoustics", name, pass ? "PASS" : "FAIL", detail);
                HeadlessHarness.Assert(pass, $"{name}: {detail} {diagnostics()}");
            }

            var painted = Run("TilePainter", "a = Engine.Tile.PaintAt(8, 8); b = Engine.Tile.PaintAt(24, 8); c = Engine.Tile.PaintAt(40, 8); again = Engine.Tile.PaintAt(24, 8);");
            // The block starts at column 1: an end with a neighbour to its right is 2 (1 + 2, 0), the middle
            // 2 + 8 = 10 (1 + 2, 2), the other end 8 (1 + 0, 2).
            Check("painting picks the ends and the middle", Number(painted, "a") == 1 && Number(painted, "b") == 1 && Number(painted, "c") == 1
                && Number(painted, "again") == 0 && Tile(0) == "3,0" && Tile(1) == "3,2" && Tile(2) == "1,2",
                $"tiles {Tile(0)} {Tile(1)} {Tile(2)} (3,0 3,2 1,2), again {Number(painted, "again")}");
            var erased = Run("TileEraser", "mid = Engine.Tile.EraseAt(24, 8);");
            Check("erasing the middle leaves two single tiles", Number(erased, "mid") == 1 && Tile(1) == "none" && Tile(0) == "1,0" && Tile(2) == "1,0",
                $"tiles {Tile(0)} {Tile(1)} {Tile(2)} (1,0 none 1,0)");

            var sound = Run("SoundPlace", "Engine.Audio.Acoustics = \"cave\"; Engine.Audio.Acoustics = \"spaceship\"; acoustics = Engine.Audio.Acoustics;"
                + " Engine.Audio.AcousticsAmount = 2; amount = Engine.Audio.AcousticsAmount; Engine.Audio.MusicMuffle = 0.5; muffle = Engine.Audio.MusicMuffle;");
            Check("acoustics are set, a name it does not know is refused, amounts are kept in range",
                sound.TryGetValue("acoustics", out object? place) && place as string == "cave" && Number(sound, "amount") == 1 && Number(sound, "muffle") == 0.5,
                $"acoustics {place}, amount {Number(sound, "amount")}, muffle {Number(sound, "muffle")}");
            PgslCommands.AudioApiAcoustics = "outdoors";
            PgslCommands.AudioApiAcousticsAmount = 0.5;
            PgslCommands.AudioApiMusicMuffle = 0;

            // The sound device itself: effects through the reverb, music through the filter (played silently).
            Genesis.Audio.AudioEngine? engine = null;
            try { engine = new Genesis.Audio.AudioEngine(); }
            catch (Exception exception) { row("Autotiles and acoustics", "sound device", "NOT RUN", "no sound device here: " + exception.GetBaseException().Message); }
            if (engine != null)
            {
                using (engine)
                {
                    engine.Acoustics = "hall";
                    engine.AcousticsAmount = 0.7f;
                    engine.MusicMuffle = 1f;
                    var tone = Genesis.Audio.SoundEffect.Tone(440, 0.05f);
                    bool effect = tone.PlayVoice(engine, 0f, 1f, false, music: false) != null;
                    bool music = tone.PlayVoice(engine, 0f, 1f, false, music: true) != null;
                    engine.Acoustics = "outdoors";
                    Check("the sound device plays through the acoustics and the muffled music", effect && music && engine.ActiveVoices == 2,
                        $"effect {effect}, music {music}, voices {engine.ActiveVoices}");
                }
            }
        }
        finally
        {
            PgslCommands.BindContext(previousContext);
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousPath;
        }
    }

    private static void Names(HeadlessContext ctx, Action<string, string, string, string> row)
    {
        var bridge = new PgslEngineBridge();
        bridge.LoadFromRegistry();
        IReadOnlyDictionary<string, int> ids = bridge.NativeIdMap;
        IReadOnlyList<PgslApi.Entry> entries = PgslApi.Entries;
        string skipped = string.Join("\n", PgslApi.Skipped);
        // A command is named by its standard name, or as the getter or setter of a property (ParticleBurstLimit is Engine.Particle.BurstLimit).
        var named = new HashSet<string>(entries.SelectMany(e => new[] { e.Command, e.GetterCommand, e.SetterCommand }).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        List<string> missing = PgslCommandRegistry.GetCatalog()
            .Where(info => string.IsNullOrWhiteSpace(info.Namespace) && info.IsImplemented && !info.Name.Contains('.')
                && !named.Contains(info.Name) && !skipped.Contains(": " + info.Name + " (", StringComparison.Ordinal))
            .Select(info => info.Name).ToList();
        List<string> unshared = entries
            .Where(e => !ids.TryGetValue(e.Name, out int id) || (e.Command != null && (!ids.TryGetValue(e.Command, out int target) || target != id)))
            .Select(e => e.Name).ToList();
        List<string> badNames = entries.Where(e => !e.Name.Split('.').All(part => part.Length > 0 && char.IsLetter(part[0]) && part.All(char.IsLetterOrDigit)))
            .Select(e => e.Name).ToList();
        int properties = entries.Count(e => e.IsProperty);
        row("Engine API", "every command has an Engine.Area.Name", missing.Count == 0 ? "PASS" : "FAIL", missing.Count == 0 ? $"{entries.Count} names, {properties} properties" : string.Join(", ", missing.Take(20)));
        row("Engine API", "a standard name is the same call as its command", unshared.Count == 0 ? "PASS" : "FAIL", string.Join(", ", unshared.Take(20)));
        row("Engine API", "names are Engine.Area.Name", badNames.Count == 0 ? "PASS" : "FAIL", string.Join(", ", badNames.Take(20)));
        File.WriteAllText(Path.Combine(ctx.Captures, "EngineApi.md"), Reference(), new UTF8Encoding(false));
        HeadlessHarness.Assert(missing.Count == 0, "Commands with no standard name: " + string.Join(", ", missing));
        HeadlessHarness.Assert(unshared.Count == 0, "Standard names that are not the same native call as their command: " + string.Join(", ", unshared));
        HeadlessHarness.Assert(badNames.Count == 0, "Standard names not of the form Engine.Area.Name: " + string.Join(", ", badNames));
    }

    /// <summary>The reference page: every Engine.Area.Name by area (Documentation/EngineApi.md).</summary>
    private static string Reference()
    {
        var text = new StringBuilder();
        var rows = PgslApi.Reference();
        text.AppendLine("# The engine API: Engine.Area.Name");
        text.AppendLine();
        text.AppendLine("Every engine command and value by its standard name, made by `PgslApi` and checked by");
        text.AppendLine("`--test pgsl-logic` (which writes this page beside its captures). A **property** is read like a");
        text.AppendLine("variable and, when it can be set, set like one (`Engine.Physics.Mass = 4;`, `Engine.Image.Frame += 1;`);");
        text.AppendLine("an instance's property is the current instance's, each target's in turn inside `with`. The flat");
        text.AppendLine("names (`PhysicsApplyForce`) still work. See PgslLanguage.md, \"The engine API\".");
        text.AppendLine();
        text.AppendLine($"{rows.Count} names: {rows.Count(r => r.IsProperty)} properties ({rows.Count(r => r.CanSet)} can be set), {rows.Count(r => !r.IsProperty)} commands.");
        foreach (var area in rows.GroupBy(r => string.Join('.', r.Name.Split('.').Take(r.Name.Split('.').Length - 1))))
        {
            text.AppendLine();
            text.AppendLine($"## {area.Key}");
            text.AppendLine();
            text.AppendLine("| Name | Kind | What it is |");
            text.AppendLine("|---|---|---|");
            foreach (var r in area)
            {
                string kind = r.IsProperty ? (r.CanSet ? "property" : "read only") : "command";
                string what = (r.Description ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
                text.AppendLine($"| `{r.Name[(area.Key.Length + 1)..]}` | {kind} | {what} |");
            }
        }
        return text.ToString();
    }

    private static void Physics(ProjectSession project, Action<string, string, string, string> row)
    {
        // A 3D Physics resource for Engine.Physics.Use: a bouncy sphere.
        string path = new ResourceService(project).CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Physics), ResourceKind.Physics, "ApiBouncy");
        new PhysicsSceneConfig { Dimension = PhysicsDimension.ThreeD, BodyType = PhysicsBodyKind.Dynamic, Shape = PhysicsBodyShape.Sphere, Restitution = 0.9, Friction = 0.2, Density = 2 }
            .SaveToFile(path);
        ResourceCatalog.Invalidate(project.RootPath);

        using var scene = new RuntimeScene("Engine API physics");
        var game = new ProjectGameContext(project.RootPath, scene, null, null, RoomAsset.Create("Api", RoomDimension.ThreeD), null);
        PgslContext previousContext = PgslCommands.BindContext(new PgslContext());
        var previousGame = PgslCommands.ActiveGameContext;
        string previousPath = PgslCommands.ProjectPath;
        PgslCommands.ActiveGameContext = game;
        PgslCommands.ProjectPath = project.RootPath;
        try
        {
            scene.Physics = PhysicsWorld.Create(new PhysicsWorldAsset());
            scene.Physics.EnableThreadDispatcher = false;
            VMEngine.Initialize();
            var host = new ScriptHostSystem();
            host.SetContext(game);
            var world = scene.World;
            Entity Make(float x, bool body)
            {
                Entity entity = world.CreateEntity();
                world.Set(entity, new TransformComponent { X = x, Y = 5, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
                world.Set(entity, new Transform3DComponent { Position = new Vector3(x, 5, 0), Rotation = Quaternion.Identity, Scale = Vector3.One });
                if (body) world.Set(entity, RigidBodyComponent.DynamicBox(new Vector3(0.5f), 1f));
                return entity;
            }
            Entity[] crates = { Make(0, true), Make(10, true), Make(20, true) };
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = "" }))
                foreach (Entity crate in crates) host.Attach(world, crate, "ApiCrate");
            Entity controller = Make(-10, false);
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = Controller })) host.Attach(world, controller, "ApiController");
            Entity ball = Make(-20, false);
            using (host.UseEventSources(new Dictionary<string, string> { ["Create"] = Ball })) host.Attach(world, ball, "ApiBall");
            string diagnostics = string.Join("; ", host.RecentDiagnostics.Select(d => d.Message));

            for (int i = 0; i < 60; i++) scene.UpdateFixed(1f / 60);

            var scripts = host.Instances.OfType<PgslBehavior>().ToList();
            IReadOnlyDictionary<string, object> Vars(Entity entity) =>
                scripts.FirstOrDefault(s => s.Context?.InstanceId == entity.Id)?.GetVariablesSnapshot() ?? new Dictionary<string, object>();
            double Number(IReadOnlyDictionary<string, object> vars, string name) =>
                vars.TryGetValue(name, out object? value) && value is not string ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : double.NaN;
            string Text(IReadOnlyDictionary<string, object> vars, string name) => vars.TryGetValue(name, out object? value) ? value?.ToString() ?? "" : "";
            var boss = Vars(controller);
            var thrown = Vars(ball);
            Vector3 Position(Entity entity) => world.GetRef<Transform3DComponent>(entity).Position;
            RigidBodyComponent Body(Entity entity) => world.GetRef<RigidBodyComponent>(entity);

            void Check(string name, bool pass, string detail)
            {
                row("Engine API physics", name, pass ? "PASS" : "FAIL", detail);
                HeadlessHarness.Assert(pass, $"{name}: {detail} {diagnostics}");
            }
            Check("with sets every target's body", crates.All(c => Body(c).Mass == 4 && Math.Abs(Body(c).Restitution - 0.75f) < 1e-6 && Math.Abs(Body(c).Friction - 0.3f) < 1e-6 && Body(c).GravityScale == 0),
                string.Join(" / ", crates.Select(c => $"mass {Body(c).Mass} bounce {Body(c).Restitution} friction {Body(c).Friction} gravity {Body(c).GravityScale}")));
            // The lead moves at 3 a second slowed by its damping (0.59) and the air (0.05): 3 (1 - e^-0.64) / 0.64; the
            // second at 4 slowed by the air alone.
            double leadX = 3 * (1 - Math.Exp(-0.64)) / 0.64, secondX = 10 + 4 * (1 - Math.Exp(-0.05)) / 0.05;
            Check("for + with gives each its own velocity, gravity 0 floats", Math.Abs(Position(crates[0]).X - leadX) < 0.1 && Math.Abs(Position(crates[1]).X - secondX) < 0.15
                && Math.Abs(Position(crates[0]).Y - 5) < 0.05 && Math.Abs(Position(crates[1]).Y - 5) < 0.05,
                $"crate 1 at {Position(crates[0])} ({leadX:0.00}, 5), crate 2 at {Position(crates[1])} ({secondX:0.00}, 5)");
            Check("a body made static stays", Vector3.Distance(Position(crates[2]), new Vector3(20, 5, 0)) < 0.01 && Body(crates[2]).Motion == Genesis.Shared.ECS.Components.PhysicsMotionType.Static && Body(crates[2]).Mass == 4,
                $"at {Position(crates[2])}, {Body(crates[2]).Motion}");
            Check("world values and self commands", Number(boss, "count") == 3 && Number(boss, "mine") == 0 && Math.Abs(Number(boss, "drag") - 0.05) < 1e-6
                && Math.Abs(Number(boss, "strength") - 2) < 1e-4 && Math.Abs(Number(boss, "speedA") - 3) < 1e-3 && Number(boss, "massA") == 4 && Text(boss, "typeA") == "dynamic",
                $"count {Number(boss, "count")} mine {Number(boss, "mine")} drag {Number(boss, "drag")} strength {Number(boss, "strength")} speed {Number(boss, "speedA")} mass {Number(boss, "massA")} type {Text(boss, "typeA")}");
            Check("a compiled function sets the target's body", Math.Abs(Number(boss, "damping") - 0.59) < 1e-4 && Math.Abs(Body(crates[0]).LinearDamping - 0.59f) < 1e-4,
                $"returned {Number(boss, "damping")}, body {Body(crates[0]).LinearDamping} (0.59)");
            Check("Use gives a Physics resource's body, Remove takes it", Number(thrown, "used") == 1 && Text(thrown, "shape") == "sphere" && Math.Abs(Number(thrown, "bounce") - 0.9) < 1e-4
                && Number(thrown, "had") == 1 && Number(thrown, "removed") == 1 && Number(thrown, "after") == 0,
                $"used {Number(thrown, "used")} shape {Text(thrown, "shape")} bounce {Number(thrown, "bounce")} had {Number(thrown, "had")} removed {Number(thrown, "removed")} after {Number(thrown, "after")}");
            Check("no script errors", diagnostics.Length == 0, diagnostics);
        }
        finally
        {
            PgslCommands.BindContext(previousContext);
            PgslCommands.ActiveGameContext = previousGame;
            PgslCommands.ProjectPath = previousPath;
        }
    }
}
