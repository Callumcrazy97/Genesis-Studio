using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Scene;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.SilkNet.DX11;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

internal static class RoomEnvironmentPreviewSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Room.EnvironmentPreview");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Environment.DynamicWeatherTextureRoundTrip", CheckDynamicTexture);
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Environment.AuthoredRuntimeStateAndQuality", CheckRuntimeState);
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Environment.PausedNoonMidnightCloudsAndPersistence", () => CheckPreview(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Environment.RuntimeCloudSubmissionCapture", () => CheckRuntimeCapture(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Room.Environment.CloudForegroundAndZeroDensity", () => CheckCloudForeground(ctx));
    }

    private static void CheckDynamicTexture()
    {
        using IGpuDevice device = new Dx11GpuDevice();
        const int width = 7, height = 5;
        byte[] expected = new byte[width * height * 4];
        for (int index = 0; index < expected.Length; index++) expected[index] = (byte)(index * 17 + 31);
        GpuTextureHandle texture = device.CreateTexture(new GpuTextureDesc
        {
            Width = width, Height = height, MipLevels = 1, ArrayLayers = 1,
            Format = GpuFormat.R8G8B8A8UNorm, Usage = GpuBufferUsage.Dynamic,
            BindFlags = GpuBindFlags.ShaderResource, DebugName = "Weather upload regression",
        }, expected);
        Check("initial data");
        for (int index = 0; index < expected.Length; index++) expected[index] = (byte)(255 - expected[index]);
        device.UpdateTexture(texture, 0, 0, width, height, expected); Check("full update");
        byte[] patch = Enumerable.Range(0, 3 * 2 * 4).Select(index => (byte)(index * 3)).ToArray();
        device.UpdateTexture(texture, 2, 1, 3, 2, patch);
        for (int row = 0; row < 2; row++) patch.AsSpan(row * 12, 12).CopyTo(expected.AsSpan(((row + 1) * width + 2) * 4, 12));
        Check("partial update preserving adjacent pixels");
        device.ReleaseTexture(texture);

        void Check(string step)
        {
            Assert(device.TryReadTexture(texture, out int actualWidth, out int actualHeight, out byte[] actual)
                && actualWidth == width && actualHeight == height, "Dynamic texture readback failed after " + step);
            for (int index = 0; index < actual.Length; index += 4)
                Assert(actual[index] == expected[index + 2] && actual[index + 1] == expected[index + 1]
                    && actual[index + 2] == expected[index] && actual[index + 3] == expected[index + 3],
                    "Dynamic weather texture lost pixels/row pitch after " + step);
        }
    }

    private static void CheckRuntimeState()
    {
        RoomAsset room = Fixture();
        using var runtime = new RuntimeScene();
        RoomSceneBuilder.ApplySceneSettings(runtime, room);
        Mesh3DState state = RuntimeState(runtime);
        Assert(state.AuthoredSkyEnabled && state.AtmosphereLutEnabled && state.CelestialExtrasEnabled
            && state.RaymarchedCloudsEnabled && state.ShowSunVisual,
            "Authored room sky did not activate the real runtime sky/cloud/celestial paths.");
        Assert(state.SkyTimeOfDayHours == 12f && state.SkySunDirection.Y < 0f,
            "Runtime did not use the authored noon solar direction and clock.");
        Assert(state.CloudBaseHeight == 180f && state.CloudThickness == 85f && state.CloudCoverageScale == 1.2f,
            "Runtime cloud slab or coverage diverged from authored room values.");
        byte[] firstMap = new byte[runtime.WeatherMap.Resolution * runtime.WeatherMap.Resolution * 4];
        Assert(runtime.WeatherMap.TryCopyRgba8(firstMap, out _, out _, out _) && firstMap.Any(value => value != 0),
            "The first runtime frame has no weather map until a simulation tick occurs.");
        var expectedMap = new WeatherMapService(runtime.WeatherMap.Resolution);
        expectedMap.ConfigureViewCoverage(runtime.Camera3D.Position, room.Environment.CloudBaseHeight, room.Environment.CloudThickness);
        expectedMap.Update(runtime.Climate.Current, room.Environment.ClimateSeed);
        byte[] expectedPixels = new byte[firstMap.Length]; expectedMap.TryCopyRgba8(expectedPixels, out _, out _, out _);
        Assert(firstMap.SequenceEqual(expectedPixels), "The runtime weather map ignored the authored climate seed.");
        Assert(runtime.WeatherMap.WorldHalfExtent >= 4096, "Cloud map only covers nearby ground, leaving normal sky rays empty.");
        Vector3 distant = new(15000, 12, -21000);
        Assert(expectedMap.ConfigureViewCoverage(distant, 180, 85), "Weather map did not follow a distant camera.");
        expectedMap.Update(runtime.Climate.Current, room.Environment.ClimateSeed);
        Assert(Vector2.Distance(expectedMap.WorldCenter, new Vector2(distant.X, distant.Z)) < expectedMap.WorldHalfExtent / 8
            && !expectedMap.ConfigureViewCoverage(distant + Vector3.UnitX, 180, 85),
            "Camera-relative weather coverage either stayed at the origin or rebuilt for a one-metre orbit.");

        foreach (int quality in new[] { 0, 1, 2, 3 })
        {
            room.Environment.CloudQuality = quality;
            RoomSceneBuilder.ApplySceneSettings(runtime, room);
            Mesh3DState authored = RuntimeState(runtime);
            MeshLightingDefaults.Apply(ref authored);
            Assert(authored.CloudQuality == quality, "Installation defaults replaced authored cloud quality " + quality + ".");
        }
        room.Environment.TimeOfDayHours = 0;
        RoomSceneBuilder.ApplySceneSettings(runtime, room);
        state = RuntimeState(runtime);
        Assert(state.SkySunDirection.Y > 0f && state.LightDirection.Y < 0f && state.SkyTimeOfDayHours == 0f,
            "Midnight sky incorrectly follows the above-horizon moon light instead of the below-horizon sun.");
        room.Environment.VolumetricClouds = false;
        RoomSceneBuilder.ApplySceneSettings(runtime, room);
        state = RuntimeState(runtime); MeshLightingDefaults.Apply(ref state);
        Assert(!state.RaymarchedCloudsEnabled && !state.CloudTemporalEnabled && runtime.Atmosphere.Current.CloudVolumes.Count == 0,
            "Authored clouds off left a cloud renderer or volume source enabled.");
        room.Environment.DynamicSky = false;
        RoomSceneBuilder.ApplySceneSettings(runtime, room);
        Assert(runtime.Climate is null && runtime.Atmosphere is null, "Disabling dynamic sky left runtime evaluators active.");
        Mesh3DState before = EnvironmentMapper.ToMesh3DState(runtime.Environment, runtime.Camera3D, new(), false, RenderDebugView.Shaded);
        state = before;
        EnvironmentMapper.StampClimateAtmosphere(ref state, runtime.Climate!, runtime.Atmosphere!);
        Assert(state.Equals(before) && !state.AuthoredSkyEnabled, "DynamicSky=false changed the legacy render state.");
    }

    private static void CheckPreview(HeadlessContext ctx)
    {
        string directory = Path.Combine(ctx.Workspace, "RoomEnvironmentPreview"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Sky.room.json");
        RoomAssetLoader.Save(Fixture(), path);
        using var editor = new RoomEditorControl(path, directory);
        using var host = UnattendedWindowing.NewHost(1440, 900);
        editor.Dock = DockStyle.Fill; host.Controls.Add(editor); ThemeService.Apply(host);
        editor.ViewMode3D = true; editor.SetGridVisible(false);
        editor.Navigation.SetSection(RoomNavSection.Backgrounds);
        editor.Viewport.Camera.Target = new Vector3(0, 12, 0);
        editor.Viewport.Camera.Distance = 24;
        editor.Viewport.Camera.Pitch = .28f; editor.Viewport.Camera.Yaw = .35f;
        IRenderController? renderer = null;
        editor.Viewport.DrawScene += value => renderer = value;
        UnattendedWindowing.ShowWithoutFocus(host);
        using Bitmap noon = Frame(editor.Viewport);
        Assert(renderer is not null, "Room sky preview never rendered.");
        Assert(editor.IsModelPreviewPaused, "Room environment started playing on open.");
        Mesh3DState noonState = editor.Viewport.InspectionState;
        CompareRuntime(editor);
        Assert(noonState.AuthoredSkyEnabled && editor.SubmittedEnvironmentWeatherMap && editor.SubmittedEnvironmentCloudCount > 0,
            "Room sky was only a background colour; real cloud inputs were not submitted.");
        CheckPasses(renderer!, true);
        Capture(ctx, host, "room-environment-noon");
        noon.Save(Path.Combine(ctx.Captures, "room-environment-noon-viewport.png"));

        editor.SetAtmosphereFields(AtmospherePreset.Natural, false, 1, 180, 85, 1.2f, .15f);
        using (Bitmap withoutClouds = Frame(editor.Viewport))
        {
            if (!renderer!.BackendName.Equals("Software", StringComparison.OrdinalIgnoreCase))
                Assert(ChangedPixels(noon, withoutClouds) > .025,
                    "Cloud passes ran but their output was invisible in the noon sky.");
            withoutClouds.Save(Path.Combine(ctx.Captures, "room-environment-noon-clouds-off-viewport.png"));
        }
        Capture(ctx, host, "room-environment-noon-clouds-off");
        editor.SetAtmosphereFields(AtmospherePreset.Natural, true, 1, 180, 85, 1.2f, .15f);
        using (Frame(editor.Viewport)) { }

        editor.SetEnvironmentFields(true, WeatherKind.Wind, 12, 24, false);
        editor.SetAtmosphereFields(AtmospherePreset.Natural, true, 1, 180, 85, 1f, .04f);
        using (Bitmap partlyCloudy = Frame(editor.Viewport))
        {
            Capture(ctx, host, "room-environment-partly-cloudy-noon");
            partlyCloudy.Save(Path.Combine(ctx.Captures, "room-environment-partly-cloudy-noon-viewport.png"));
            editor.SetAtmosphereFields(AtmospherePreset.Natural, true, 1, 180, 85, 0f, .04f);
            using Bitmap zeroCoverage = Frame(editor.Viewport);
            zeroCoverage.Save(Path.Combine(ctx.Captures, "room-environment-partly-cloudy-zero-coverage-viewport.png"));
            Capture(ctx, host, "room-environment-partly-cloudy-zero-coverage");
            if (!renderer!.BackendName.Equals("Software", StringComparison.OrdinalIgnoreCase))
                Assert(ChangedPixels(partlyCloudy, zeroCoverage) > .025,
                    "Authored cloud coverage slider did not change visible clouds.");
        }
        editor.SetEnvironmentFields(true, WeatherKind.Clear, 12, 24, false);
        editor.SetAtmosphereFields(AtmospherePreset.Natural, true, 1, 180, 85, 1f, .04f);
        using (Bitmap clear = Frame(editor.Viewport))
        {
            Capture(ctx, host, "room-environment-clear-noon");
            if (!renderer!.BackendName.Equals("Software", StringComparison.OrdinalIgnoreCase))
            {
                Color upper = clear.GetPixel(clear.Width * 3 / 4, clear.Height / 6);
                Color horizon = clear.GetPixel(clear.Width * 3 / 4, clear.Height * 2 / 3);
                Assert(Math.Abs(upper.R - horizon.R) + Math.Abs(upper.G - horizon.G) + Math.Abs(upper.B - horizon.B) > 20,
                    "The authored sky lost its zenith/horizon colours and became a flat clear colour.");
            }
        }
        Vector3 target = editor.Viewport.Camera.Target, eye = editor.Viewport.Camera.Eye;
        float yaw = editor.Viewport.Camera.Yaw, pitch = editor.Viewport.Camera.Pitch;
        Vector3 towardSun = -Vector3.Normalize(editor.Viewport.InspectionState.SkySunDirection);
        editor.Viewport.Camera.Target = eye + towardSun * editor.Viewport.Camera.Distance;
        editor.Viewport.Camera.Yaw = MathF.Atan2(towardSun.X, towardSun.Z);
        editor.Viewport.Camera.Pitch = MathF.Asin(towardSun.Y);
        using (Bitmap sunFacing = Frame(editor.Viewport))
        {
            sunFacing.Save(Path.Combine(ctx.Captures, "room-environment-clear-sun-facing-viewport.png"));
            if (!renderer!.BackendName.Equals("Software", StringComparison.OrdinalIgnoreCase))
            {
                Color center = sunFacing.GetPixel(sunFacing.Width / 2, sunFacing.Height / 2);
                Assert(center.R > 220 && center.G > 205 && center.B > 150,
                    $"Noon solar disc lost its emissive radiance ({center.R}, {center.G}, {center.B}).");
            }
        }
        Capture(ctx, host, "room-environment-clear-sun-facing");
        editor.Viewport.Camera.Target = target; editor.Viewport.Camera.Yaw = yaw; editor.Viewport.Camera.Pitch = pitch;
        editor.SetEnvironmentFields(true, WeatherKind.Overcast, 12, 24, false);
        editor.SetAtmosphereFields(AtmospherePreset.Natural, true, 1, 180, 85, 1.2f, .15f);
        using (Frame(editor.Viewport)) { }

        EnvironmentFrame paused = editor.EnvironmentPreviewFrame!.Value;
        int evaluations = editor.EnvironmentPreviewEvaluationCount;
        int maps = editor.EnvironmentWeatherMapGenerationCount;
        for (int index = 0; index < 10; index++) _ = editor.Viewport.InspectionState;
        using (Frame(editor.Viewport)) { }
        Assert(editor.EnvironmentPreviewFrame == paused && editor.EnvironmentPreviewEvaluationCount == evaluations
            && editor.EnvironmentWeatherMapGenerationCount == maps,
            "Paused repaint rebuilt the climate/weather map or advanced authored time.");
        editor.Viewport.Camera.Yaw += .1f;
        _ = editor.Viewport.InspectionState;
        Assert(editor.EnvironmentWeatherMapGenerationCount == maps && editor.EnvironmentPreviewFrame!.Value.ElapsedRealSeconds == paused.ElapsedRealSeconds,
            "Orbiting rebuilt the weather map or advanced the environment clock.");
        editor.Viewport.Camera.Yaw -= .1f;

        editor.SetEnvironmentFields(true, WeatherKind.Overcast, 0, 24, false);
        using Bitmap midnight = Frame(editor.Viewport);
        Mesh3DState midnightState = editor.Viewport.InspectionState;
        Assert(midnightState.SkySunDirection.Y > 0 && midnightState.SkyTimeOfDayHours == 0,
            "Clock edit did not change the preview's actual sky sun direction.");
        Assert(ChangedPixels(noon, midnight) > .15, "Noon and midnight did not visibly change the rendered sky.");
        CompareRuntime(editor); CheckPasses(renderer!, true);
        Capture(ctx, host, "room-environment-midnight");
        editor.Undo(); _ = editor.Viewport.InspectionState;
        Assert(editor.Viewport.InspectionState.SkyTimeOfDayHours == 12f, "Clock undo did not restore the evaluated noon sky.");
        editor.Redo(); _ = editor.Viewport.InspectionState;
        Assert(editor.Viewport.InspectionState.SkyTimeOfDayHours == 0f, "Clock redo did not restore midnight.");

        editor.SetAtmosphereFields(AtmospherePreset.Natural, false, 1, 180, 85, 1.2f, .15f);
        using (Frame(editor.Viewport)) { }
        Assert(!editor.SubmittedEnvironmentWeatherMap && editor.SubmittedEnvironmentCloudCount == 0
            && !editor.Viewport.InspectionState.RaymarchedCloudsEnabled, "Cloud visibility edit retained cloud submissions.");
        CheckPasses(renderer!, false); Capture(ctx, host, "room-environment-clouds-off");
        editor.SetModelPreviewTime(.5f);
        _ = editor.Viewport.InspectionState;
        Assert(editor.EnvironmentPreviewFrame!.Value.ElapsedRealSeconds > 0,
            "Explicit Room transport advance never reached the cached climate.");
        editor.SetModelPreviewTime(0);
        _ = editor.Viewport.InspectionState;
        Assert(editor.EnvironmentPreviewFrame!.Value.ElapsedRealSeconds == 0
            && editor.Viewport.InspectionState.SkyTimeOfDayHours == 0, "Stop/rewind did not reset the sky to its authored clock.");
        editor.Save();
        using var reopened = new RoomEditorControl(path, directory);
        Mesh3DState reopenedState = reopened.Viewport.InspectionState;
        Assert(reopenedState.AuthoredSkyEnabled && reopenedState.SkyTimeOfDayHours == 0
            && reopenedState.CloudQuality == 1 && !reopenedState.RaymarchedCloudsEnabled,
            "Sky time, cloud visibility or quality did not survive save/reopen.");

        editor.SetEnvironmentFields(false, WeatherKind.Overcast, 0, 24, false);
        using Bitmap legacy = Frame(editor.Viewport);
        Mesh3DState legacyState = editor.Viewport.InspectionState;
        Mesh3DState expectedLegacy = EditorSceneLighting.Create(showFloor: legacyState.ShowFloor);
        Assert(!legacyState.AuthoredSkyEnabled && legacyState.LightDirection == expectedLegacy.LightDirection
            && legacyState.BackgroundColor == expectedLegacy.BackgroundColor && editor.EnvironmentPreviewFrame is null
            && !editor.SubmittedEnvironmentWeatherMap && editor.SubmittedEnvironmentCloudCount == 0,
            "Disabling dynamic sky did not restore unchanged editor reference lighting.");
        Capture(ctx, host, "room-environment-disabled");
    }

    private static void CheckRuntimeCapture(HeadlessContext ctx)
    {
        using var runtime = new RuntimeScene();
        RoomAsset room = Fixture();
        RoomSceneBuilder.ApplySceneSettings(runtime, room);
        using var viewport = new EditorViewport3D { Dock = DockStyle.Fill, Mode2D = false };
        using var host = UnattendedWindowing.NewHost(1000, 680); host.Controls.Add(viewport); ThemeService.Apply(host);
        viewport.Camera.Target = new Vector3(0, 12, 0); viewport.Camera.Distance = 24;
        viewport.Camera.Pitch = .28f; viewport.Camera.Yaw = .35f;
        viewport.SceneStateFactory = () => RuntimeState(runtime);
        IRenderController? renderer = null;
        viewport.DrawScene += active =>
        {
            renderer = active;
            int count = 0; runtime.CollectMeshes([], ref count, active);
        };
        UnattendedWindowing.ShowWithoutFocus(host);
        using Bitmap noon = Frame(viewport);
        Assert(renderer is not null, "Runtime environment capture did not render.");
        CheckPasses(renderer!, true); Capture(ctx, host, "room-runtime-environment-noon");
        room.Environment.VolumetricClouds = false;
        RoomSceneBuilder.ApplySceneSettings(runtime, room);
        using (Bitmap withoutClouds = Frame(viewport))
        {
            if (!renderer!.BackendName.Equals("Software", StringComparison.OrdinalIgnoreCase))
                Assert(ChangedPixels(noon, withoutClouds) > .025,
                    "Runtime submits cloud telemetry without visible clouds in the sky.");
        }
        Capture(ctx, host, "room-runtime-environment-noon-clouds-off");
        room.Environment.VolumetricClouds = true;
        room.Environment.TimeOfDayHours = 0;
        RoomSceneBuilder.ApplySceneSettings(runtime, room);
        using Bitmap midnight = Frame(viewport);
        Assert(ChangedPixels(noon, midnight) > .15, "Runtime authoring path rendered identical noon and midnight skies.");
        CheckPasses(renderer!, true); Capture(ctx, host, "room-runtime-environment-midnight");
    }

    private static void CheckCloudForeground(HeadlessContext ctx)
    {
        using var runtime = new RuntimeScene();
        RoomAsset room = Fixture(); RoomSceneBuilder.ApplySceneSettings(runtime, room);
        using var viewport = new EditorViewport3D { Dock = DockStyle.Fill, Mode2D = false };
        using var host = UnattendedWindowing.NewHost(960, 640); host.Controls.Add(viewport);
        viewport.Camera.Target = new Vector3(0, 12, 0); viewport.Camera.Distance = 24;
        viewport.Camera.Pitch = .28f; viewport.Camera.Yaw = .35f;
        float density = 1;
        viewport.SceneStateFactory = () => { Mesh3DState state = RuntimeState(runtime); state.CloudDensityScale = density; return state; };
        IRenderController? renderer = null; MeshHandle panel = default;
        viewport.DrawScene += active =>
        {
            renderer = active; int count = 0; runtime.CollectMeshes([], ref count, active);
            if (!panel.IsValid)
            {
                Vector3 forward = viewport.Camera.Forward, right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
                Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward)), center = viewport.Camera.Eye + forward * 16;
                Vector3[] corners = [center - right * 5 - up * 3.5f, center + right * 5 - up * 3.5f,
                    center + right * 5 + up * 3.5f, center - right * 5 + up * 3.5f];
                panel = active.RegisterMesh(corners.Select(position => new MeshVertex
                { Position = position, Normal = -forward, Color = new Vector4(.05f, .7f, .1f, 1) }).ToArray(), [0, 1, 2, 0, 2, 3]);
            }
            active.DrawMesh(new MeshDrawCall { Mesh = panel, World = Matrix4x4.Identity, Tint = new RenderColor(1, 1, 1), Alpha = 1,
                Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog | MeshDrawFlags.Foliage });
        };
        UnattendedWindowing.ShowWithoutFocus(host);
        using Bitmap clouds = Frame(viewport);
        density = 0; using Bitmap zeroDensity = Frame(viewport);
        room.Environment.VolumetricClouds = false; RoomSceneBuilder.ApplySceneSettings(runtime, room);
        using Bitmap cloudsOff = Frame(viewport);
        Assert(renderer is not null, "Cloud occlusion fixture did not render.");
        if (!renderer!.BackendName.Equals("Software", StringComparison.OrdinalIgnoreCase))
        {
            Assert(ChangedPixels(clouds, cloudsOff) > .01, "Occlusion fixture lacks visible clouds outside its foreground panel.");
            Assert(ChangedPixels(zeroDensity, cloudsOff) < .01, "Density zero left dark opaque clouds in the sky.");
        }
        int foreground = 0, contaminated = 0;
        for (int y = 0; y < cloudsOff.Height; y++)
        for (int x = 0; x < cloudsOff.Width; x++)
        {
            Color clear = cloudsOff.GetPixel(x, y), cloudy = clouds.GetPixel(x, y);
            if (clear.G < clear.R * 1.8 || clear.G < clear.B * 1.4 || clear.G < 70) continue;
            foreground++;
            if (Math.Abs(clear.R - cloudy.R) + Math.Abs(clear.G - cloudy.G) + Math.Abs(clear.B - cloudy.B) > 6) contaminated++;
        }
        Assert(foreground > 2000 && contaminated == 0,
            $"Cloud upsampling affected the foreground silhouette ({contaminated}/{foreground} pixels).");
        clouds.Save(Path.Combine(ctx.Captures, "room-cloud-foreground.png"));
        zeroDensity.Save(Path.Combine(ctx.Captures, "room-cloud-density-zero.png"));
        if (panel.IsValid) renderer!.ReleaseMesh(panel);
    }

    private static RoomAsset Fixture()
    {
        RoomAsset room = RoomAsset.Create("Authored environment", RoomDimension.ThreeD);
        room.Environment.DynamicSky = true; room.Environment.AutomaticWeather = false;
        room.Environment.TimeOfDayHours = 12; room.Environment.TimeScale = 24;
        room.Environment.Weather = "Overcast"; room.Environment.ClimateSeed = 731;
        room.Environment.CloudQuality = 1; room.Environment.CloudCoverageScale = 1.2f;
        return room;
    }

    private static Mesh3DState RuntimeState(RuntimeScene runtime)
    {
        Mesh3DState state = EnvironmentMapper.ToMesh3DState(runtime.Environment, runtime.Camera3D, new(), false, RenderDebugView.Shaded);
        EnvironmentMapper.StampClimateAtmosphere(ref state, runtime.Climate!, runtime.Atmosphere!);
        return state;
    }

    private static void CompareRuntime(RoomEditorControl editor)
    {
        using var runtime = new RuntimeScene();
        runtime.Camera3D.Position = editor.Viewport.Camera.Eye;
        RoomSceneBuilder.ApplySceneSettings(runtime, editor.Room);
        Mesh3DState actual = editor.Viewport.InspectionState, expected = RuntimeState(runtime);
        Assert(actual.AuthoredSkyEnabled == expected.AuthoredSkyEnabled && actual.SkySunDirection == expected.SkySunDirection
            && actual.LightDirection == expected.LightDirection && actual.SkyTimeOfDayHours == expected.SkyTimeOfDayHours
            && actual.CloudQuality == expected.CloudQuality && actual.RaymarchedCloudsEnabled == expected.RaymarchedCloudsEnabled
            && actual.SkyZenithColor == expected.SkyZenithColor && actual.SkyHorizonColor == expected.SkyHorizonColor
            && actual.BackgroundColor == expected.BackgroundColor && actual.SunColor == expected.SunColor,
            "Room viewport and F5 disagree on authored environment rendering state.");
    }

    private static void CheckPasses(IRenderController renderer, bool clouds)
    {
        RenderStats stats = renderer.GetStats();
        if (renderer.BackendName.Equals("Software", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("      Software uses analytic cloud volumes; atmosphere LUT, celestial extras and raymarch are GPU-only.");
            return;
        }
        Assert(stats.AtmosphereLutMs > 0 && stats.CelestialExtrasMs > 0,
            "Authored sky never executed the atmosphere/celestial renderer passes on " + renderer.BackendName + ".");
        Assert(clouds ? stats.RaymarchedCloudsMs > 0 : stats.RaymarchedCloudsMs == 0,
            "Authored cloud visibility did not control the real raymarch pass on " + renderer.BackendName + ".");
    }

    private static Bitmap Frame(EditorViewport3D viewport)
    {
        GateSuite.Pump(3, 25);
        return viewport.CaptureFrame(8) ?? throw new InvalidOperationException("Room environment frame capture failed.");
    }
    private static double ChangedPixels(Bitmap a, Bitmap b)
    {
        int total = 0, changed = 0;
        for (int y = 0; y < Math.Min(a.Height, b.Height) * .6; y += 4)
        for (int x = 0; x < Math.Min(a.Width, b.Width); x += 4)
        {
            Color first = a.GetPixel(x, y), second = b.GetPixel(x, y); total++;
            if (Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B) > 24) changed++;
        }
        return changed / (double)Math.Max(1, total);
    }
    private static void Capture(HeadlessContext ctx, Form host, string name)
    {
        using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
        bitmap.Save(Path.Combine(ctx.Captures, name + ".png"));
        var metrics = Genesis.Application.Runtime.ImageMetrics.Measure(bitmap);
        ctx.Report.Images.Add(new ImageResult("Room environment", name + ".png", metrics.Width, metrics.Height,
            metrics.UniqueSampledColors, metrics.AverageLuminance));
    }
    private static void Assert(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
