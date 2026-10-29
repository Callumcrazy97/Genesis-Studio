using System.Numerics;
using Genesis.Application.Core.Projects;
using Genesis.Rendering.Core;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Water;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// T7, the Nature Walk's look gaps (29 Oct): the dynamic sky's glow round the sun (a room setting the
/// sunny scenes switch on), water that reflects the dynamic sky it lies under rather than a fixed
/// midday blue, and the built-in grass's deeper green. Pixels on DX11.
/// </summary>
internal static class LookGapsSuite
{
    private const int Width = 320, Height = 180;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "LookGaps");
        HeadlessHarness.RunCase(ctx.Report, "Render.Sky.SunGlowWhitensTheSkyRoundTheSun", () => SunGlowPixels(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Render.Water.ReflectsTheDynamicSkyItLiesUnder", () => WaterReflectsTheSky(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Sky.SunGlowIsKeptWithTheRoomAndSetBySunnyScenes", SunGlowSetting);
        HeadlessHarness.RunCase(ctx.Report, "Runtime.Look.BuiltInGrassAndStandardWaterKeepTheirColours", GrassAndWaterDefaults);
    }

    private static readonly Matrix4x4 Projection =
        D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 3f, Width / (float)Height, 0.1f, 1000f);

    /// <summary>Where a direction from the camera lands on the screen, in pixels.</summary>
    private static (int X, int Y) OnScreen(Vector3 camera, Matrix4x4 view, Vector3 direction)
    {
        Vector4 clip = Vector4.Transform(new Vector4(camera + direction * 50f, 1f), view * Projection);
        float x = (clip.X / clip.W * 0.5f + 0.5f) * Width, y = (0.5f - clip.Y / clip.W * 0.5f) * Height;
        return ((int)MathF.Round(x), (int)MathF.Round(y));
    }

    private static (int R, int G, int B) At(byte[] bgra, (int X, int Y) p)
    {
        int offset = (Math.Clamp(p.Y, 0, Height - 1) * Width + Math.Clamp(p.X, 0, Width - 1)) * 4;
        return (bgra[offset + 2], bgra[offset + 1], bgra[offset]);
    }

    private static float Luma((int R, int G, int B) c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    /// <summary>
    /// Renders frames until the pixels at <paramref name="probe"/> hold still (shaders compile off the
    /// frame), and returns the last frame.
    /// </summary>
    private static byte[] Settle(IRenderController renderer, Action draw, (int X, int Y) probe)
    {
        byte[] pixels = [];
        (int, int, int) previous = (-1, -1, -1);
        int still = 0;
        for (int frame = 0; frame < 240 && still < 4; frame++)
        {
            draw();
            HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out _, out pixels) && width == Width,
                "No frame was read back.");
            renderer.Present();
            (int, int, int) now = At(pixels, probe);
            still = frame > 8 && now == previous ? still + 1 : 0;
            previous = now;
            if (still < 4) Thread.Sleep(15);
        }
        return pixels;
    }

    /// <summary>
    /// The sky with the sun 20 degrees up, straight ahead: with the glow on, the sky 8 degrees from the
    /// sun is markedly brighter and whiter than with it off, while sky 40 degrees away barely changes.
    /// Off (a room saved before the glow), the sky is drawn as it was.
    /// </summary>
    private static void SunGlowPixels(HeadlessContext ctx)
    {
        Vector3 towardSun = Vector3.Normalize(new Vector3(0f, MathF.Sin(20f * MathF.PI / 180f), MathF.Cos(20f * MathF.PI / 180f)));
        Vector3 camera = new(0f, 2f, 0f);
        Matrix4x4 view = D3dMatrixHelper.CreateLookAtLh(camera, camera + towardSun, Vector3.UnitY);
        static Vector3 Turned(Vector3 direction, float yawDegrees, float pitchDegrees)
        {
            Matrix4x4 turn = Matrix4x4.CreateRotationY(yawDegrees * MathF.PI / 180f) * Matrix4x4.CreateRotationX(-pitchDegrees * MathF.PI / 180f);
            return Vector3.Normalize(Vector3.TransformNormal(direction, turn));
        }
        (int X, int Y) sun = OnScreen(camera, view, towardSun);
        (int X, int Y) near = OnScreen(camera, view, Turned(towardSun, 8f, 0f));
        (int X, int Y) far = OnScreen(camera, view, Turned(towardSun, -40f, -12f));
        HeadlessHarness.Assert(Math.Abs(sun.X - Width / 2) <= 2 && Math.Abs(near.X - sun.X) > 10 && far.X >= 0 && far.X < Width && far.Y >= 0 && far.Y < Height,
            $"The check itself is wrong: sun at {sun}, near at {near}, far at {far}.");

        using var host = UnattendedWindowing.NewHost(Width, Height);
        UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11);
        renderer.Initialize(host.Handle, Width, Height);
        Vector3 horizon = new(0.62f, 0.76f, 0.9f), zenith = new(0.2f, 0.42f, 0.8f);
        byte[] Frame(float glow) => Settle(renderer, () =>
        {
            Mesh3DState state = Mesh3DState.Default;
            state.ShowFloor = false; state.FogEnabled = false; state.ShadowsEnabled = false;
            state.AuthoredSkyEnabled = true; state.AtmosphereLutEnabled = true; state.ShowSunVisual = true;
            state.LightDirection = -towardSun; state.SkySunDirection = -towardSun;
            state.SunColor = new Vector3(1f, 0.96f, 0.86f); state.SunIntensity = 1.15f;
            state.SkyHorizonColor = horizon; state.SkyZenithColor = zenith; state.BackgroundColor = horizon;
            state.SunGlow = glow;
            renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
            renderer.SetCamera3D(view, Projection);
            renderer.BeginFrame(); renderer.Clear(horizon.X, horizon.Y, horizon.Z);
            renderer.EndFrame();
        }, near);

        byte[] off = Frame(0f), on = Frame(1f);
        SaveFrame(ctx, on, "look-gaps-sun-glow-dx11.png");
        SaveFrame(ctx, off, "look-gaps-sun-glow-off-dx11.png");
        var nearOff = At(off, near); var nearOn = At(on, near);
        var farOff = At(off, far); var farOn = At(on, far);
        Console.WriteLine($"[LookGaps] sun glow: 8 deg {nearOff} -> {nearOn}; 40 deg {farOff} -> {farOn}");
        float nearGain = Luma(nearOn) - Luma(nearOff), farGain = Luma(farOn) - Luma(farOff);
        HeadlessHarness.Assert(nearGain >= 15f,
            $"With the glow on, the sky 8 degrees from the sun should be clearly brighter: {nearOff} off, {nearOn} on.");
        HeadlessHarness.Assert(nearOn.B - nearOn.R < nearOff.B - nearOff.R,
            $"The glow should whiten the sky round the sun (less blue than red): {nearOff} off, {nearOn} on.");
        HeadlessHarness.Assert(farGain <= nearGain * 0.25f,
            $"The glow should fade with distance from the sun: +{nearGain:F0} at 8 degrees but +{farGain:F0} at 40.");
    }

    /// <summary>
    /// A lake seen at a low angle under an orange dynamic sky reflects orange; with no dynamic sky it
    /// reflects the water's own blue-green sky, as before.
    /// </summary>
    private static void WaterReflectsTheSky(HeadlessContext ctx)
    {
        Vector3 camera = new(0f, 1.5f, -40f);
        Vector3 forward = Vector3.Normalize(new Vector3(0f, -0.06f, 1f));
        Matrix4x4 view = D3dMatrixHelper.CreateLookAtLh(camera, camera + forward, Vector3.UnitY);
        // The band of water from 20 to 100 metres ahead: a grazing view, mostly reflection. Ripples
        // turn single pixels to the low or the high sky, so the band's mean colour is compared.
        (int X, int Y) water = OnScreen(camera, view, Vector3.Normalize(new Vector3(0f, -1.5f, 20f)));
        (int X, int Y) far = OnScreen(camera, view, Vector3.Normalize(new Vector3(0f, -1.5f, 100f)));
        HeadlessHarness.Assert(water.Y < Height && far.Y < water.Y - 5, $"The check itself is wrong: the water band is rows {far.Y} to {water.Y}.");
        (float R, float G, float B) Band(byte[] bgra)
        {
            float r = 0f, g = 0f, b = 0f;
            int count = 0;
            for (int y = far.Y + 1; y <= water.Y; y++)
                for (int x = 10; x < Width - 10; x++)
                {
                    var c = At(bgra, (x, y));
                    r += c.R; g += c.G; b += c.B; count++;
                }
            return (r / count, g / count, b / count);
        }

        using var host = UnattendedWindowing.NewHost(Width, Height);
        UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11);
        renderer.Initialize(host.Handle, Width, Height);
        var (vertices, indices) = MeshGeometry.BuildCheckerFloor(RenderColor.White, RenderColor.White, tiles: 2, tileSize: 100f);
        MeshHandle lake = renderer.RegisterMesh(vertices, indices);
        WaterMaterialSettings material = WaterMaterialSettings.Default;
        material.WaveAmplitude = 0f;
        // The see-through water lies over a plain grey background, the same in both frames; only what
        // the water reflects differs.
        Vector3 orange = new(0.95f, 0.45f, 0.15f), zenith = new(0.25f, 0.3f, 0.8f), grey = new(0.3f, 0.3f, 0.3f);
        byte[] Frame(bool dynamicSky) => Settle(renderer, () =>
        {
            Mesh3DState state = Mesh3DState.Default;
            state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false; state.ShadowsEnabled = false;
            state.AuthoredSkyEnabled = dynamicSky;
            state.LightDirection = Vector3.Normalize(new Vector3(0.3f, -0.6f, -0.7f));
            state.SkySunDirection = state.LightDirection;
            state.SkyHorizonColor = orange; state.SkyZenithColor = zenith; state.BackgroundColor = grey;
            renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
            renderer.SetCamera3D(view, Projection);
            renderer.BeginFrame(); renderer.Clear(grey.X, grey.Y, grey.Z);
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = lake,
                World = Matrix4x4.Identity,
                Tint = new RenderColor(material.ShallowColor.X, material.ShallowColor.Y, material.ShallowColor.Z, material.Opacity),
                DeepTint = new RenderColor(material.DeepColor.X, material.DeepColor.Y, material.DeepColor.Z, material.DepthFade),
                WaterParams = material.PackParams(),
                SkyHorizon = material.PackSkyHorizon(),
                SkyZenith = material.PackSkyZenith(),
                Flags = MeshDrawFlags.Water | MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow,
                Alpha = material.Opacity,
            });
            renderer.EndFrame();
        }, water);

        byte[] live = Frame(dynamicSky: true), own = Frame(dynamicSky: false);
        SaveFrame(ctx, live, "look-gaps-water-dynamic-sky-dx11.png");
        SaveFrame(ctx, own, "look-gaps-water-own-sky-dx11.png");
        var underSky = Band(live); var underOwn = Band(own);
        Console.WriteLine($"[LookGaps] water rows {far.Y}-{water.Y}: dynamic sky {underSky}, own sky {underOwn}");
        HeadlessHarness.Assert(underSky.R > underSky.B + 15,
            $"Under an orange dynamic sky the far lake should reflect orange: it reads {underSky}.");
        HeadlessHarness.Assert(underOwn.B > underOwn.R && underSky.R > underOwn.R + 25,
            $"With no dynamic sky the lake should keep its own blue-green sky ({underOwn}), and the orange sky should redden it ({underSky}).");
    }

    /// <summary>
    /// The glow is a room setting: 0 for a room saved before it (the sky as it was), 1 from the Sunny
    /// day and Golden hour scenes and 0 from the cloudy ones, kept through save and reopen, clamped to
    /// 0 to 4, and handed to the frame by the atmosphere.
    /// </summary>
    private static void SunGlowSetting()
    {
        HeadlessHarness.Assert(RoomAsset.Create("Plain", RoomDimension.ThreeD).Environment.SunGlow == 0f
            && new AtmosphereOptions().SunGlow == 0f && Mesh3DState.Default.SunGlow == 0f,
            "A room, an atmosphere or a frame should start with no sun glow, so older rooms look as they did.");
        RoomEnvironment environment = new();
        RoomScenePresets.Find("SunnyDay")!.Apply(environment);
        float sunny = environment.SunGlow;
        RoomScenePresets.Find("GoldenHour")!.Apply(environment);
        float golden = environment.SunGlow;
        RoomScenePresets.Find("Overcast")!.Apply(environment);
        HeadlessHarness.Assert(sunny == 1f && golden == 1f && environment.SunGlow == 0f,
            $"Sunny day and Golden hour should set a glow of 1 and Overcast clear it ({sunny}, {golden}, {environment.SunGlow}).");

        RoomAsset room = RoomAsset.Create("Glow", RoomDimension.ThreeD);
        room.Environment.SunGlow = 1.5f;
        string file = Path.Combine(Path.GetTempPath(), "genesis-look-gaps-" + Environment.ProcessId + ".room.json");
        try
        {
            File.WriteAllText(file, Newtonsoft.Json.JsonConvert.SerializeObject(room));
            HeadlessHarness.Assert(RoomAssetLoader.Parse(file).Environment.SunGlow == 1.5f, "A room's sun glow was not kept through save and reopen.");
        }
        finally { File.Delete(file); }

        var atmosphere = new AtmosphereService(new AtmosphereOptions { SunGlow = 9f });
        HeadlessHarness.Assert(atmosphere.Options.SunGlow == 4f && AtmosphereService.ClampSunGlow(float.NaN) == 0f,
            $"The glow should clamp to 0 to 4 ({atmosphere.Options.SunGlow}).");
        Mesh3DState state = Mesh3DState.Default;
        EnvironmentMapper.StampClimateAtmosphere(ref state, null, new AtmosphereService(new AtmosphereOptions { SunGlow = 1.25f }));
        HeadlessHarness.Assert(state.SunGlow == 1.25f, $"The atmosphere's glow did not reach the frame ({state.SunGlow}).");
    }

    /// <summary>
    /// A new grass rule grows the deeper meadow green the reference picture shows (T7), with dark
    /// roots, and a lake with no look of its own is a clear teal rather than a pale turquoise.
    /// </summary>
    private static void GrassAndWaterDefaults()
    {
        TerrainGrassRuleSettings rule = new();
        static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        HeadlessHarness.Assert(rule.BaseColor == new Vector3(0.08f, 0.15f, 0.05f) && rule.TipColor == new Vector3(0.27f, 0.46f, 0.18f)
            && rule.ColorCurve == 1.25f,
            $"The built-in grass's colours changed: root {rule.BaseColor}, tip {rule.TipColor}, curve {rule.ColorCurve}.");
        HeadlessHarness.Assert(Luma(rule.TipColor) < 0.4f && Luma(rule.TipColor) > Luma(rule.BaseColor) * 2.5f
            && rule.TipColor.Y > rule.TipColor.X * 1.5f,
            "The built-in grass should be a deep green with roots well darker than its tips.");

        // The standard water look a lake takes: a clear teal that hides most of its bed.
        WaterMaterialSettings water = WaterMaterialSettings.Default;
        HeadlessHarness.Assert(water.Opacity >= 0.7f && water.ShallowColor.Z > water.ShallowColor.Y && water.ShallowColor.Y < 0.5f,
            $"The standard water should be a clear teal that hides most of its bed ({water.ShallowColor}, opacity {water.Opacity}).");
        WaterBody lake = new();
        HeadlessHarness.Assert(lake.GroundMistEnabled && lake.GroundMistDensity <= 0.15f,
            $"A lake's mist should be a breath over the water, not a white slab (density {lake.GroundMistDensity}).");
    }

    private static void SaveFrame(HeadlessContext ctx, byte[] bgra, string name)
    {
        using var bitmap = new System.Drawing.Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, Width, Height), System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bgra, y * Width * 4, data.Scan0 + y * data.Stride, Width * 4);
                for (int x = 0; x < Width; x++)
                    System.Runtime.InteropServices.Marshal.WriteByte(data.Scan0 + y * data.Stride + x * 4 + 3, 255);
            }
        }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(Path.Combine(ctx.Captures, name), System.Drawing.Imaging.ImageFormat.Png);
    }
}
