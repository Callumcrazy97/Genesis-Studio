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
