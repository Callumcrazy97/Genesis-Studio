using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Rendering.Core;
using Genesis.Rendering.Primitives;
using Genesis.Rendering.Viewport;
using Genesis.Runtime;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Request 64: the hemisphere sky light (shade lit by the whole drawn sky dome and the ground,
/// brighter and less blue than the zenith colour) and eye adaptation (the exposure follows the
/// scene's brightness). Both are room settings, off by default; with them off a frame is pixel for
/// pixel what it was. Checked on every renderer the suite pattern allows, under the Clear Day sky
/// of a dynamic-sky room at three in the afternoon (the island's light).
/// </summary>
internal static class SkyLightExposureSuite
{
    private const int Width = 640, Height = 360;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "SkyLightExposure");
        ProjectSession project = ctx.Project ?? CreateProject(ctx);
        RunSettings(ctx);
        RunSkyLightMath(ctx);
        RunRendering(ctx, project);
    }

    // ── Settings: room file → scene → frame state, and the script commands ──────────────────────

    private static void RunSettings(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Lighting.SkyLight.RoomSettingsReachTheFrameAndScripts", () =>
        {
            RoomAsset room = RoomAsset.Create("Sky light", RoomDimension.ThreeD);
            Check(room.Environment.SkyLight == "zenith" && !room.Environment.AutoExposure,
                "A new room does not start with the zenith sky light and auto exposure off.");
            using RuntimeScene scene = new("Sky light");
            RoomSceneBuilder.ApplySceneSettings(scene, room);
            Mesh3DState off = EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D, new RendererOptions(), false, default);
            Check(off.SkyLightMode == SkyLightModes.Zenith && !off.AutoExposureEnabled,
                "With the room's defaults the frame state asks for a sky light mode or auto exposure.");

            room.Environment.SkyLight = "Hemisphere";
            room.Environment.SkyLightStrength = 1.4f;
            room.Environment.SkyLightTint = [1f, 0.9f, 0.8f];
            room.Environment.SkyLightSaturation = 0.3f;
            room.Environment.AutoExposure = true;
            room.Environment.AutoExposureKey = 0.25f;
            room.Environment.AutoExposureDarkenSeconds = 0.5f;
            room.Environment.AutoExposureBrightenSeconds = 3f;
            room.Environment.AutoExposureMinEv = -2f;
            room.Environment.AutoExposureMaxEv = 3f;
            int resetBefore = scene.Environment.AutoExposureResetId;
            RoomSceneBuilder.ApplySceneSettings(scene, room);
            Mesh3DState on = EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D, new RendererOptions(), false, default);
            Check(on.SkyLightMode == SkyLightModes.Hemisphere && on.SkyLightStrength == 1.4f
                && on.SkyLightTint == new Vector3(1f, 0.9f, 0.8f) && on.SkyLightSaturation == 0.3f,
                "The room's sky light settings did not reach the frame state.");
            Check(on.AutoExposureEnabled && on.AutoExposureKey == 0.25f && on.AutoExposureDarkenSeconds == 0.5f
                && on.AutoExposureBrightenSeconds == 3f && on.AutoExposureMinEv == -2f && on.AutoExposureMaxEv == 3f,
                "The room's auto exposure settings did not reach the frame state.");
            Check(scene.Environment.AutoExposureResetId != resetBefore, "Loading a room does not start its exposure afresh.");

            RoomEnvironment json = Newtonsoft.Json.JsonConvert.DeserializeObject<RoomEnvironment>(
                "{\"skyLight\":\"hemisphere\",\"skyLightStrength\":99,\"autoExposure\":true,\"autoExposureMinEv\":3,\"autoExposureMaxEv\":-2}")!;
            Check(json.SkyLight == "hemisphere" && json.AutoExposure, "The room file's sky light and auto exposure are not read.");
            room.Environment = json;
            RoomSceneBuilder.ApplySceneSettings(scene, room);
            Mesh3DState clamped = EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D, new RendererOptions(), false, default);
            Check(clamped.SkyLightStrength == 16f, "An out-of-range sky light strength was not limited to 16.");
            Check(clamped.AutoExposureMinEv == -2f && clamped.AutoExposureMaxEv == 3f, "Swapped exposure limits were not put in order.");

            scene.Environment.AmbientOverrideColor = new Vector3(0.2f, 0.2f, 0.2f);
            Mesh3DState overridden = EnvironmentMapper.ToMesh3DState(scene.Environment, scene.Camera3D, new RendererOptions(), false, default);
            Check(overridden.SkyLightMode == SkyLightModes.Zenith, "A script's own ambient colour does not outrank the hemisphere sky light.");
            scene.Environment.AmbientOverrideColor = null;

            ProjectGameContext game = new(ctx.Workspace, scene, null, null, room, null);
            IGameContext? previousGame = PgslCommands.ActiveGameContext;
            PgslCommands.ActiveGameContext = game;
            try
            {
                Check(PgslCommands.SkySetAmbientMode("zenith") && PgslCommands.SkyGetAmbientMode() == "zenith",
                    "SkySetAmbientMode(\"zenith\") did not switch the room to the zenith sky light.");
                Check(!PgslCommands.SkySetAmbientMode("sideways") && scene.Environment.SkyLightMode == SkyLightModes.Zenith,
                    "An unknown sky light mode was accepted.");
                PgslCommands.SkyAmbientMode = "hemisphere";
                PgslCommands.SkySetAmbientStrength(1.5);
                PgslCommands.SkySetAmbientTint(1, 0.95, 0.9);
                PgslCommands.SkySetAmbientSaturation(0.4);
                Check(scene.Environment.SkyLightMode == SkyLightModes.Hemisphere && scene.Environment.SkyLightStrength == 1.5f
                    && scene.Environment.SkyLightTint == new Vector3(1f, 0.95f, 0.9f) && Math.Abs(scene.Environment.SkyLightSaturation - 0.4f) < 1e-6f,
                    "The Sky commands did not change the room's sky light.");
                PgslCommands.RenderSetAutoExposure(false, 0.18, 1, -4, 4);
                int reset = scene.Environment.AutoExposureResetId;
                PgslCommands.RenderSetAutoExposure(true, 0.3, 1.5, -3, 2);
                Check(scene.Environment.AutoExposureEnabled && scene.Environment.AutoExposureKey == 0.3f
                    && scene.Environment.AutoExposureDarkenSeconds == 1.5f && scene.Environment.AutoExposureBrightenSeconds == 1.5f
                    && scene.Environment.AutoExposureMinEv == -3f && scene.Environment.AutoExposureMaxEv == 2f,
                    "RenderSetAutoExposure did not set the room's eye adaptation.");
                Check(scene.Environment.AutoExposureResetId != reset, "Turning auto exposure on does not start it from the scene as it is.");
                PgslCommands.RenderSetAutoExposureSpeed(0.25, 4);
                PgslCommands.RenderSetAutoExposureMetering(0);
                reset = scene.Environment.AutoExposureResetId;
                PgslCommands.RenderResetAutoExposure();
                Check(scene.Environment.AutoExposureDarkenSeconds == 0.25f && scene.Environment.AutoExposureBrightenSeconds == 4f
                    && scene.Environment.AutoExposureCenterWeight == 0f && scene.Environment.AutoExposureResetId != reset,
                    "The auto exposure speed, metering or reset commands did not reach the room.");
                PgslCommands.RenderingAutoExposureEnabled = false;
                Check(!PgslCommands.RenderGetAutoExposure(), "Engine.Rendering.AutoExposureEnabled = false did not turn it off.");
                PgslCommands.RenderingAutoExposure(0.2f, 2f);
                Check(scene.Environment.AutoExposureEnabled && scene.Environment.AutoExposureKey == 0.2f
                    && scene.Environment.AutoExposureBrightenSeconds == 2f,
                    "Engine.Rendering.AutoExposure(key, adaptSeconds) did not turn it on with that key and speed.");
            }
            finally { PgslCommands.ActiveGameContext = previousGame; }

            // With no game running the commands do nothing and do not throw.
            Check(PgslCommands.SkySetAmbientMode("hemisphere"), "A known mode is refused with no game running.");
            PgslCommands.RenderSetAutoExposure(true, double.NaN, double.PositiveInfinity, 0, 0);
            Check(!PgslCommands.RenderGetAutoExposure(), "Auto exposure reports on with no game running.");
        });
    }

    // ── The sky dome's light, worked out on the CPU ──────────────────────────────────────────────

    private static void RunSkyLightMath(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Lighting.SkyLight.ClearDayLightIsBrighterAndPalerThanTheZenith", () =>
        {
            Mesh3DState state = ClearDayState(skyLight: SkyLightModes.Hemisphere);
            SkyDome dome = default;
            foreach (bool linear in new[] { true, false })
            {
                // The renderer lights in linear light, or with the authored colours as they are (the default pipeline).
                Vector3 Light(Vector3 c) => linear ? Genesis.Rendering.Textures.SrgbColor.ToLinear(c) : c;
                dome = new(Light(state.SkyZenithColor), Light(state.SkyHorizonColor), Light(state.BackgroundColor),
                    true, true, -state.SkySunDirection.Y, -state.LightDirection.Y, Light(state.SunColor) * state.SunIntensity);
                SkyLightTerms terms = SkyLightMath.Compute(dome, 1f, Vector3.One, SkyLightDefaults.Saturation);
                Vector3 zenithAmbient = Light(state.AmbientColor);
                Vector3 sun = Light(state.SunColor) * state.SunIntensity * MathF.Max(0f, -state.LightDirection.Y);
                float oldShare = SkyLightMath.Luminance(zenithAmbient) / (SkyLightMath.Luminance(zenithAmbient) + SkyLightMath.Luminance(sun));
                float newShare = SkyLightMath.Luminance(terms.Up) / (SkyLightMath.Luminance(terms.Up) + SkyLightMath.Luminance(sun));
                Vector3 sunlit = sun + terms.Up;
                string measured = (linear ? "linear: " : "default pipeline: ")
                    + $"shade share of sunlight {oldShare:P1} -> {newShare:P1}; up {terms.Up} (B/R {terms.Up.Z / terms.Up.X:F2}); down {terms.Down}; "
                    + $"zenith ambient {zenithAmbient} (B/R {zenithAmbient.Z / zenithAmbient.X:F2}); sunlit ground {sunlit} (B/R {sunlit.Z / sunlit.X:F2}); "
                    + $"mirror zenith {terms.MirrorZenith}, horizon {terms.MirrorHorizon}";
                Console.WriteLine("Sky light (Clear Day, 15:00), " + measured);
                File.AppendAllText(Path.Combine(ctx.Logs, "sky-light-terms.txt"), measured + Environment.NewLine);
                Check(newShare > oldShare * (linear ? 2f : 1.25f), "The hemisphere sky light is not brighter against the sun than the zenith colour: " + measured);
                Check(terms.Up.Z / terms.Up.X < (zenithAmbient.Z / zenithAmbient.X) * 0.4f, "The hemisphere sky light is not much less blue than the zenith colour: " + measured);
                Check(sunlit.Z / sunlit.X is > 0.9f and < 1.12f, "Sunlit ground under a clear sky is not near neutral: " + measured);
                Check(terms.Down.X > terms.Down.Z * 0.85f, "The ground's light is not the warm-grey bounce of a sunlit ground: " + measured);
                Check(terms.MirrorHorizon.X > terms.MirrorZenith.X, "The reflected horizon is not paler than the zenith: " + measured);
            }

            // Recomputed whenever the sky changes: two hundred evaluations must cost next to nothing.
            Stopwatch clock = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) SkyLightMath.Compute(dome with { SunHeight = 0.2f + i * 0.003f }, 1f, Vector3.One, 0.5f);
            double microseconds = clock.Elapsed.TotalMilliseconds * 1000.0 / 200.0;
            Console.WriteLine($"Sky light recomputation: {microseconds:F1} us each (CPU, 32 elevation samples)");
            File.AppendAllText(Path.Combine(ctx.Logs, "sky-light-terms.txt"), $"\nrecompute {microseconds:F1} us");
            Check(microseconds < 500, $"Working out the sky light took {microseconds:F0} us; it runs whenever the sky changes.");
        });
    }

    /// <summary>The frame state a dynamic-sky room gives: Clear Day at 15:00 on 3 August at 54° N, ambient intensity 2.2.</summary>
    private static Mesh3DState ClearDayState(int skyLight, float strength = 1f)
    {
        SceneEnvironment environment = new() { EnvironmentReflection = 1f };
        EnvironmentService climate = new(new EnvironmentOptions
        {
            StartTimeHours = 15f, TimeScale = 0f, DayOfYear = 215, LatitudeDegrees = 53.9f, AutomaticWeather = false,
        });
        climate.SetWeather(WeatherKind.Clear, 0f);
        climate.Update(0f, Vector3.Zero);
        climate.ApplyTo(environment);
        AtmosphereService atmosphere = new(new AtmosphereOptions
        {
            Preset = AtmospherePreset.ClearDay, AmbientScale = 2.2f, VolumetricClouds = false,
        });
        atmosphere.Update(climate.Current, Vector3.Zero);
        environment.SkyLightMode = skyLight;
        environment.SkyLightStrength = strength;
        Camera3D camera = new() { FarPlane = 500f };
        Mesh3DState state = EnvironmentMapper.ToMesh3DState(environment, camera, new RendererOptions(), false, RenderDebugView.Shaded);
        EnvironmentMapper.StampClimateAtmosphere(ref state, climate, atmosphere);
        state.RaymarchedCloudsEnabled = false;
        state.CloudTemporalEnabled = false;
        return state;
    }

    // ── Pixels on every renderer ─────────────────────────────────────────────────────────────────

    private sealed class SceneSetup
    {
        public Mesh3DState State;
        public int Ground, Slab;
        public Matrix4x4 View, Projection;
        public Vector3 ShadeAt, SunlitAt;
    }

    private static void RunRendering(HeadlessContext ctx, ProjectSession project)
    {
        Mesh3DState zenith = ClearDayState(SkyLightModes.Zenith);
        Mesh3DState hemisphere = ClearDayState(SkyLightModes.Hemisphere);
        // The shade of a 6 m slab 3 m up, and a sunlit patch 9 m to its side; the camera looks back
        // towards the sun from the shadow's far side, so the slab never hides either.
        Vector3 light = Vector3.Normalize(zenith.LightDirection);
        Vector3 slabCentre = new(0f, 3f, 0f);
        Vector3 shade = slabCentre + light * (3f / -light.Y);
        Vector3 away = Vector3.Normalize(new Vector3(light.X, 0f, light.Z));
        Vector3 side = new(-away.Z, 0f, away.X);
        Vector3 sunlit = shade + side * 9f;
        Vector3 target = (shade + sunlit) * 0.5f;
        Vector3 eye = target + away * 14f + new Vector3(0f, 10f, 0f);
        SceneSetup setup = new()
        {
            State = zenith,
            View = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY),
            Projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, Width / (float)Height, 0.1f, 500f),
            ShadeAt = shade,
            SunlitAt = sunlit,
        };

        using Form host = GateSuite.NewHost(Width, Height);
        using D3DViewportControl viewport = new() { Dock = DockStyle.Fill, DriveMode = ViewportDriveMode.External, VSync = false };
        PgslContext scriptContext = new() { RoomWidth = Width, RoomHeight = Height };
        viewport.OnRender += renderer =>
        {
            renderer.SetMesh3DState(setup.State);
            renderer.Clear(setup.State.BackgroundColor.X, setup.State.BackgroundColor.Y, setup.State.BackgroundColor.Z);
            renderer.Set3DFrameActive(true);
            renderer.SetCamera3D(setup.View, setup.Projection);
            ScriptMeshes.BeginFrame();
            scriptContext.DrawSurface = new PgslRenderDrawSurface(renderer, null, renderer.PixelWidth, renderer.PixelHeight,
                projectPath: project.RootPath, is3DActive: true);
            PgslContext? previous = PgslCommands.BindContext(scriptContext);
            try
            {
                PgslCommands.DrawMeshResetState();
                PgslCommands.DrawMeshSetCull(false);
                PgslCommands.DrawMesh3D(setup.Ground, 0, 0, 0, "");
                PgslCommands.DrawMesh3D(setup.Slab, slabCentre.X, slabCentre.Y, slabCentre.Z, "");
            }
            finally { PgslCommands.BindContext(previous); }
        };
        host.Controls.Add(viewport);
        GateSuite.ShowHost(host);

        PgslContext? setupContext = PgslCommands.BindContext(scriptContext);
        try
        {
            ScriptMeshes.Reset();
            setup.Ground = (int)PgslCommands.MeshCreate();
            // Wide enough that no sky shows at the top of the view (the meter then sees ground only).
            // Wound so the face seen from above is the front: a back face is lit with its normal flipped.
            PgslCommands.MeshAddQuad(setup.Ground, -400, 0, -400, -400, 0, 400, 400, 0, 400, 400, 0, -400, 0, 1, 0, 0, 0, 1, 1, 150, 150, 150, 1);
            setup.Slab = (int)PgslCommands.MeshCreate();
            PgslCommands.MeshAddQuad(setup.Slab, -3, 0, -3, -3, 0, 3, 3, 0, 3, 3, 0, -3, 0, 1, 0, 0, 0, 1, 1, 150, 150, 150, 1);
        }
        finally { PgslCommands.BindContext(setupContext); }

        try
        {
            HeadlessHarness.RunCase(ctx.Report, "Engine.Lighting.SkyLight.ShadeIsBrighterAndNeutralOnEveryBackend", () =>
                SkyLightPixels(ctx, viewport, setup, zenith, hemisphere));
            HeadlessHarness.RunCase(ctx.Report, "Engine.Rendering.AutoExposure.AdaptsTowardTheKeyOverTimeOnEveryBackend", () =>
                AutoExposurePixels(ctx, viewport, setup, zenith));
            HeadlessHarness.RunCase(ctx.Report, "Engine.Rendering.AutoExposure.GpuCostAt1080pOnDx11", () =>
                MeasureCost(ctx, host, viewport, setup, zenith, hemisphere));
        }
        finally
        {
            ScriptMeshes.Reset();
        }
    }

    private static void SkyLightPixels(HeadlessContext ctx, D3DViewportControl viewport, SceneSetup setup,
        Mesh3DState zenith, Mesh3DState hemisphere)
    {
        List<string> failures = [];
        List<string> readings = [];
        bool pipelineBefore = EngineRenderingDefaults.LinearColorPipeline;
        try
        {
            foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
            {
                viewport.BackendOverride = backend.Backend;
                GateSuite.Pump(3, 15);
                // Both colour pipelines: the default (authored colours lit as they are) and linear light.
                foreach (bool linear in new[] { false, true })
                {
                    EngineRenderingDefaults.LinearColorPipeline = linear;
                    string label = backend.ShortName + (linear ? " linear" : " gamma");
                    setup.State = zenith;
                    using Bitmap? before = viewport.ReadbackFrameToBitmap(4);
                    setup.State = hemisphere;
                    using Bitmap? lit = viewport.ReadbackFrameToBitmap(3);
                    setup.State = zenith;
                    using Bitmap? after = viewport.ReadbackFrameToBitmap(3);
                    if (before is null || lit is null || after is null || viewport.RenderFaultCount != 0)
                    {
                        failures.Add(label + ": no frame " + viewport.LastRenderException);
                        continue;
                    }
                    string name = backend.ShortName.ToLowerInvariant() + (linear ? "-linear" : "");
                    Save(ctx, before, $"skylight-zenith-{name}.png", "Engine.SkyLight.Zenith." + label);
                    Save(ctx, lit, $"skylight-hemisphere-{name}.png", "Engine.SkyLight.Hemisphere." + label);

                    int changed = CountDifferent(before, after, 0);
                    if (changed != 0) failures.Add($"{label}: the zenith frame changed after the hemisphere light was used ({changed} samples)");

                    Vector3 shadeOld = Sample(before, setup, setup.ShadeAt), sunOld = Sample(before, setup, setup.SunlitAt);
                    Vector3 shadeNew = Sample(lit, setup, setup.ShadeAt), sunNew = Sample(lit, setup, setup.SunlitAt);
                    float ratioOld = Lum(shadeOld) / MathF.Max(Lum(sunOld), 1e-4f), ratioNew = Lum(shadeNew) / MathF.Max(Lum(sunNew), 1e-4f);
                    float blueOld = shadeOld.Z / MathF.Max(shadeOld.X, 1e-4f), blueNew = shadeNew.Z / MathF.Max(shadeNew.X, 1e-4f);
                    float sunBlueOld = sunOld.Z / MathF.Max(sunOld.X, 1e-4f), sunBlueNew = sunNew.Z / MathF.Max(sunNew.X, 1e-4f);
                    readings.Add($"{label}: shade/sun (display, decoded) {ratioOld:F3} -> {ratioNew:F3}; shade B/R {blueOld:F2} -> {blueNew:F2}; "
                        + $"sunlit B/R {sunBlueOld:F2} -> {sunBlueNew:F2}; shade {Display(shadeOld)} -> {Display(shadeNew)}; sunlit {Display(sunOld)} -> {Display(sunNew)}");
                    // The CPU rasterizer draws script meshes unlit, so the light cannot show there; it
                    // only has to draw the frame.
                    if (string.Equals(backend.ShortName, "Software", StringComparison.OrdinalIgnoreCase)) continue;
                    if (CountDifferent(before, lit, 4) == 0) { failures.Add($"{label}: the hemisphere sky light changed nothing"); continue; }
                    if (!(ratioOld < 0.6f))
                        failures.Add($"{label}: the shade sample is not in shade (shade/sun {ratioOld:F2})");
                    // Linear light brightens shade from about a twentieth to about a quarter of the
                    // sunlit ground as shown; the default pipeline's zenith light was less dark to begin with.
                    float brighter = linear ? 2.5f : 1.4f;
                    if (!(ratioNew > ratioOld * brighter))
                        failures.Add($"{label}: shade is not brighter against sunlight ({ratioOld:F3} -> {ratioNew:F3}, wanted x{brighter})");
                    // The tonemap's toe deepens the colour of dark shade, so it is judged loosely.
                    if (!(blueNew < blueOld * 0.6f && blueNew < 3f))
                        failures.Add($"{label}: shade is still deep blue (B/R {blueOld:F2} -> {blueNew:F2})");
                    if (!(sunBlueNew is > 0.88f and < 1.12f))
                        failures.Add($"{label}: sunlit ground is not neutral (B/R {sunBlueNew:F2})");
                }
            }
        }
        finally { EngineRenderingDefaults.LinearColorPipeline = pipelineBefore; }
        File.WriteAllLines(Path.Combine(ctx.Logs, "sky-light-readings.txt"), readings);
        Console.WriteLine("Sky light readings: " + string.Join(" | ", readings));
        HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
    }

    private static void AutoExposurePixels(HeadlessContext ctx, D3DViewportControl viewport, SceneSetup setup, Mesh3DState zenith)
    {
        Mesh3DState bright = zenith;
        bright.FogEnabled = false;      // fog's own light would not scale with the scene
        Mesh3DState dark = Scaled(bright, 1f / 8f);
        Mesh3DState veryDark = Scaled(bright, 1f / 64f);
        Mesh3DState Exposed(Mesh3DState source, float key = AutoExposureDefaults.Key, float maxEv = 4f, int reset = 0)
        {
            source.AutoExposureEnabled = true;
            source.AutoExposureKey = key;
            source.AutoExposureDarkenSeconds = 1f;
            source.AutoExposureBrightenSeconds = 1f;
            source.AutoExposureMinEv = -4f;
            source.AutoExposureMaxEv = maxEv;
            source.AutoExposureCenterWeight = 0f;
            source.AutoExposureResetId = reset;
            return source;
        }

        List<string> failures = [];
        List<string> readings = [];
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            viewport.BackendOverride = backend.Backend;
            GateSuite.Pump(3, 15);
            bool software = string.Equals(backend.ShortName, "Software", StringComparison.OrdinalIgnoreCase);
            string name = backend.ShortName.ToLowerInvariant();
            double Capture(Mesh3DState state, string? file = null, int frames = 2)
            {
                setup.State = state;
                using Bitmap? frame = viewport.ReadbackFrameToBitmap(frames);
                if (frame is null || viewport.RenderFaultCount != 0)
                    throw new InvalidOperationException(backend.ShortName + ": no frame " + viewport.LastRenderException);
                if (file != null) Save(ctx, frame, file, "Engine.AutoExposure." + file);
                return SceneLogAverage(frame);
            }

            try
            {
                double plainBright = Capture(bright, $"exposure-off-bright-{name}.png", 4);
                double plainDark = Capture(dark, $"exposure-off-dark-{name}.png");
                double adaptedBright = Capture(Exposed(bright, reset: 1), $"exposure-on-bright-{name}.png", 3);
                if (software)
                {
                    // The CPU rasterizer has no eye adaptation: the setting must change nothing there.
                    if (Math.Abs(adaptedBright - plainBright) > 0.02) failures.Add($"Software: auto exposure changed the frame ({plainBright:F3} -> {adaptedBright:F3})");
                    readings.Add($"Software: unchanged {plainBright:F3} / {adaptedBright:F3}");
                    continue;
                }
                // The dark scene, just after the switch: still exposed for the bright one.
                double darkAtOnce = Capture(Exposed(dark, reset: 1));
                List<double> brightening = [];
                for (int step = 0; step < 10; step++)
                {
                    viewport.AdvanceSceneTime(0.2f);
                    brightening.Add(Capture(Exposed(dark, reset: 1), step == 2 ? $"exposure-on-dark-0.6s-{name}.png" : null, 1));
                }
                Capture(Exposed(dark, reset: 1), $"exposure-on-dark-adapted-{name}.png");
                // Back into the bright scene: too bright at first, then darker.
                double brightAtOnce = Capture(Exposed(bright, reset: 1), $"exposure-on-bright-at-once-{name}.png");
                List<double> darkening = [];
                for (int step = 0; step < 10; step++)
                {
                    viewport.AdvanceSceneTime(0.2f);
                    darkening.Add(Capture(Exposed(bright, reset: 1), null, 1));
                }
                double brighterKey = Capture(Exposed(bright, key: 0.36f, reset: 2), null, 3);
                // 64 times darker (6 stops) needs more brightening than a +3 stop limit allows for any
                // scene up to eight times brighter than the key, so the exposure must stop at 8.
                double limited = Capture(Exposed(veryDark, maxEv: 3f, reset: 3), $"exposure-on-limited-{name}.png", 3);
                double plainVeryDark = Capture(veryDark, null, 3);
                double offAgain = Capture(bright, $"exposure-off-again-{name}.png", 3);

                double Stops(double a, double b) => Math.Log2(a / b);
                readings.Add($"{backend.ShortName}: off bright {plainBright:F3}, off dark {plainDark:F3} ({Stops(plainDark, plainBright):F2} stops); "
                    + $"on bright {adaptedBright:F3} (key 0.18); dark at once {darkAtOnce:F3}; brightening "
                    + string.Join(",", brightening.Select(v => v.ToString("F3"))) + $"; bright at once {brightAtOnce:F3}; darkening "
                    + string.Join(",", darkening.Select(v => v.ToString("F3"))) + $"; key 0.36 {brighterKey:F3}; "
                    + $"64x darker limited to +3 {limited:F3} ({Stops(limited, plainBright):F2} stops from off bright; off {plainVeryDark:F3}); off again {offAgain:F3}");

                if (Math.Abs(Stops(adaptedBright, AutoExposureDefaults.Key)) > 0.45)
                    failures.Add($"{backend.ShortName}: the adapted scene's average is {adaptedBright:F3}, not near the key 0.18");
                if (Math.Abs(Stops(brighterKey, 0.36)) > 0.45)
                    failures.Add($"{backend.ShortName}: with key 0.36 the scene's average is {brighterKey:F3}");
                if (!(Stops(darkAtOnce, adaptedBright) < -2.2))
                    failures.Add($"{backend.ShortName}: the dark scene was not dark at the moment of the switch ({darkAtOnce:F3})");
                for (int i = 1; i < brightening.Count; i++)
                    if (brightening[i] < brightening[i - 1] * 0.995) failures.Add($"{backend.ShortName}: the dark scene got darker while adapting (step {i})");
                if (!(brightening[0] > darkAtOnce * 1.3 && brightening[1] < adaptedBright * 0.85))
                    failures.Add($"{backend.ShortName}: adapting to the dark did not take time ({darkAtOnce:F3}, {brightening[0]:F3}, {brightening[1]:F3})");
                if (Math.Abs(Stops(brightening[^1], adaptedBright)) > 0.25)
                    failures.Add($"{backend.ShortName}: two seconds on, the dark scene ({brightening[^1]:F3}) is not exposed like the bright one ({adaptedBright:F3})");
                if (!(Stops(brightAtOnce, adaptedBright) > 1.5))
                    failures.Add($"{backend.ShortName}: back in the bright scene the picture was not too bright at first ({brightAtOnce:F3})");
                for (int i = 1; i < darkening.Count; i++)
                    if (darkening[i] > darkening[i - 1] * 1.005) failures.Add($"{backend.ShortName}: the bright scene got brighter while adapting (step {i})");
                if (Math.Abs(Stops(darkening[^1], adaptedBright)) > 0.25)
                    failures.Add($"{backend.ShortName}: two seconds on, the bright scene ({darkening[^1]:F3}) is not back to {adaptedBright:F3}");
                // The limit is on the exposure itself (8 times at +3), not relative to the bright
                // scene's adapted exposure, so it is measured against the bright scene unexposed: 64
                // times darker, exposed 8 times, is 3 stops below it whatever the scene's brightness.
                // (The unexposed very dark frame is too near black to measure to a fraction of a stop.)
                if (!(Stops(limited, adaptedBright) < -0.5))
                    failures.Add($"{backend.ShortName}: the very dark scene reached the key ({limited:F3}), so the limit was not tested");
                if (Math.Abs(Stops(limited, plainBright) + 3.0) > 0.35)
                    failures.Add($"{backend.ShortName}: the +3 stop limit did not hold: 64x darker and exposed is {Stops(limited, plainBright):F2} stops from the unexposed bright scene, not -3 ({plainBright:F3} -> {limited:F3})");
                if (!(Stops(limited, plainVeryDark) > 1.5))
                    failures.Add($"{backend.ShortName}: the very dark scene was not brightened up to the limit ({plainVeryDark:F3} -> {limited:F3})");
                if (Math.Abs(offAgain - plainBright) > 1e-6)
                    failures.Add($"{backend.ShortName}: with auto exposure off again the frame is not as it was ({plainBright:F4} -> {offAgain:F4})");
            }
            catch (InvalidOperationException exception)
            {
                failures.Add(exception.Message);
            }
        }
        File.WriteAllLines(Path.Combine(ctx.Logs, "auto-exposure-readings.txt"), readings);
        Console.WriteLine("Auto exposure readings: " + string.Join(" | ", readings));
        HeadlessHarness.Assert(failures.Count == 0, string.Join(" | ", failures));
    }

    private static void MeasureCost(HeadlessContext ctx, Form host, D3DViewportControl viewport, SceneSetup setup,
        Mesh3DState zenith, Mesh3DState hemisphere)
    {
        viewport.BackendOverride = RenderBackendOption.SilkNetDx11;
        host.ClientSize = new Size(1920, 1080);
        viewport.SyncClientSize();
        GateSuite.Pump(4, 20);
        using (Bitmap? warm = viewport.ReadbackFrameToBitmap(4))
            HeadlessHarness.Assert(warm is not null, "No DX11 frame at 1920x1080.");
        Mesh3DState exposed = zenith;
        exposed.AutoExposureEnabled = true;
        exposed.AutoExposureKey = 0.18f;
        exposed.AutoExposureDarkenSeconds = exposed.AutoExposureBrightenSeconds = 1f;
        exposed.AutoExposureMinEv = -4f;
        exposed.AutoExposureMaxEv = 4f;
        exposed.AutoExposureCenterWeight = 0.5f;

        double Block(Mesh3DState state, List<double> into)
        {
            setup.State = state;
            for (int i = 0; i < 70; i++)
            {
                viewport.AdvanceSceneTime(1f / 60f);
                viewport.RenderFrame();
                if (i >= 10 && viewport.LastGpuMs > 0) into.Add(viewport.LastGpuMs);
            }
            return into.Count > 0 ? into.Average() : 0;
        }

        List<double> plain = [], adapted = [], skyLit = [];
        for (int round = 0; round < 4; round++)
        {
            Block(zenith, plain);
            Block(exposed, adapted);
            Block(hemisphere, skyLit);
        }
        static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            List<double> sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }
        static double Quantile(List<double> values, double q)
        {
            if (values.Count == 0) return 0;
            List<double> sorted = values.OrderBy(v => v).ToList();
            return sorted[(int)Math.Clamp(q * (sorted.Count - 1), 0, sorted.Count - 1)];
        }
        int width = viewport.ClientSize.Width, height = viewport.ClientSize.Height;
        string report = $"DX11 {width}x{height} GPU frame time (median, 25th-75th percentile over {plain.Count} frames each): "
            + $"plain {Median(plain):F3} ms ({Quantile(plain, 0.25):F3}-{Quantile(plain, 0.75):F3}); "
            + $"auto exposure {Median(adapted):F3} ms ({Quantile(adapted, 0.25):F3}-{Quantile(adapted, 0.75):F3}); "
            + $"hemisphere sky light {Median(skyLit):F3} ms ({Quantile(skyLit, 0.25):F3}-{Quantile(skyLit, 0.75):F3}); "
            + $"auto exposure costs {Median(adapted) - Median(plain):+0.000;-0.000} ms, sky light {Median(skyLit) - Median(plain):+0.000;-0.000} ms";
        Console.WriteLine(report);
        File.WriteAllText(Path.Combine(ctx.Logs, "sky-light-exposure-cost.txt"), report);
        HeadlessHarness.Assert(plain.Count > 50 && adapted.Count > 50, "The GPU timer gave too few readings: " + report);
        // Three passes over a 64x32 grid, an 8x4 grid and one texel: far below a tenth of a millisecond
        // on a desktop card; the bound allows for other work on the machine.
        HeadlessHarness.Assert(Median(adapted) - Median(plain) < 0.35, "Auto exposure costs too much GPU time: " + report);
        host.ClientSize = new Size(Width, Height);
        viewport.SyncClientSize();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static ProjectSession CreateProject(HeadlessContext ctx)
    {
        string parent = Path.Combine(ctx.Workspace, "SkyLightExposure");
        Directory.CreateDirectory(parent);
        return new ProjectService().CreateProject(parent, "Sky Light", "Blank");
    }

    private static Mesh3DState Scaled(Mesh3DState state, float factor)
    {
        state.SunIntensity *= factor;
        state.AmbientColor = ScaleLinear(state.AmbientColor, factor);
        state.AmbientGroundColor = ScaleLinear(state.AmbientGroundColor, factor);
        state.SkyZenithColor = ScaleLinear(state.SkyZenithColor, factor);
        state.SkyHorizonColor = ScaleLinear(state.SkyHorizonColor, factor);
        state.BackgroundColor = ScaleLinear(state.BackgroundColor, factor);
        return state;
    }

    /// <summary>Scales a colour's light (authored colours are sRGB, the lighting is linear).</summary>
    private static Vector3 ScaleLinear(Vector3 srgb, float factor)
    {
        if (!EngineRenderingDefaults.LinearColorPipeline) return srgb * factor;
        Vector3 linear = Genesis.Rendering.Textures.SrgbColor.ToLinear(srgb) * factor;
        return new Vector3(ToSrgb(linear.X), ToSrgb(linear.Y), ToSrgb(linear.Z));
    }

    private static float ToSrgb(float v) =>
        v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;

    private static float FromSrgb(float v) =>
        v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static float Lum(Vector3 linear) => 0.2126f * linear.X + 0.7152f * linear.Y + 0.0722f * linear.Z;

    /// <summary>A decoded colour as the 8-bit display value it came from.</summary>
    private static string Display(Vector3 decoded)
    {
        Vector3 shown = EngineRenderingDefaults.LinearColorPipeline
            ? new Vector3(ToSrgb(decoded.X), ToSrgb(decoded.Y), ToSrgb(decoded.Z))
            : decoded;
        return $"{(int)MathF.Round(shown.X * 255)}/{(int)MathF.Round(shown.Y * 255)}/{(int)MathF.Round(shown.Z * 255)}";
    }

    /// <summary>A pixel's display colour back in linear light (after the tonemap).</summary>
    private static Vector3 Decode(Color c) => EngineRenderingDefaults.LinearColorPipeline
        ? new Vector3(FromSrgb(c.R / 255f), FromSrgb(c.G / 255f), FromSrgb(c.B / 255f))
        : new Vector3(c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>The mean linear colour of a 7x7 block where a world point lands.</summary>
    private static Vector3 Sample(Bitmap image, SceneSetup setup, Vector3 world)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), setup.View * setup.Projection);
        int cx = (int)MathF.Round((clip.X / clip.W * 0.5f + 0.5f) * image.Width);
        int cy = (int)MathF.Round((0.5f - clip.Y / clip.W * 0.5f) * image.Height);
        Vector3 sum = Vector3.Zero;
        int count = 0;
        for (int y = cy - 3; y <= cy + 3; y++)
            for (int x = cx - 3; x <= cx + 3; x++)
                if (x >= 0 && y >= 0 && x < image.Width && y < image.Height) { sum += Decode(image.GetPixel(x, y)); count++; }
        return count > 0 ? sum / count : Vector3.Zero;
    }

    /// <summary>
    /// The frame's log-average scene luminance as the composite exposed it: each pixel decoded and
    /// taken back through the ACES curve, then averaged in log2 like the meter.
    /// </summary>
    private static double SceneLogAverage(Bitmap image)
    {
        double sum = 0;
        int count = 0;
        for (int y = 8; y < image.Height - 8; y += 12)
        {
            for (int x = 8; x < image.Width - 8; x += 12)
            {
                float display = Lum(Decode(image.GetPixel(x, y)));
                sum += Math.Log2(Math.Max(InverseAces(display), 1e-5));
                count++;
            }
        }
        return Math.Pow(2, sum / Math.Max(count, 1));
    }

    private static float Aces(float x) => Math.Clamp(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f);

    private static float InverseAces(float y)
    {
        if (y <= 0f) return 0f;
        if (y >= 0.999f) return 64f;
        float lo = 0f, hi = 64f;
        for (int i = 0; i < 40; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Aces(mid) < y) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }

    private static int CountDifferent(Bitmap a, Bitmap b, int tolerance)
    {
        int different = 0;
        for (int y = 4; y < Math.Min(a.Height, b.Height) - 4; y += 9)
        {
            for (int x = 4; x < Math.Min(a.Width, b.Width) - 4; x += 9)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                if (Math.Abs(p.R - q.R) > tolerance || Math.Abs(p.G - q.G) > tolerance || Math.Abs(p.B - q.B) > tolerance) different++;
            }
        }
        return different;
    }

    private static void Save(HeadlessContext ctx, Bitmap frame, string file, string label)
    {
        frame.Save(Path.Combine(ctx.Captures, file), ImageFormat.Png);
        ctx.Report.Images.Add(ImageResult.From(label, file, VisualCapture.Measure(frame)));
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
