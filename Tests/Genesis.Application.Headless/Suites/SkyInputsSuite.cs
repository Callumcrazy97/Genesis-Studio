using System.Numerics;
using Genesis.Rendering.Core;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Primitives;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// GS5: what a game's look reads of the sky. Project post effects get the sun's direction and
/// colour and the horizon and zenith colours (GenesisSky, b7); the hemisphere sky light takes a
/// tint of its own for the light from below.
/// </summary>
internal static class SkyInputsSuite
{
    private const int Width = 300, Height = 120;

    private const string SkySource = """
        cbuffer GenesisSky : register(b7) { float4 SunDirection; float4 SunColor; float4 HorizonColor; float4 ZenithColor; };
        struct PreviewVSOut { float4 SvPos : SV_Position; float2 UV : TEXCOORD0; };
        float4 MainPS(PreviewVSOut IN) : SV_Target
        {
            if (IN.SvPos.x < 100.0) return float4(SunColor.rgb, 1.0);
            if (IN.SvPos.x < 200.0) return float4(HorizonColor.rgb, 1.0);
            return float4(SunDirection.xyz * 0.5 + 0.5, 1.0);
        }
        """;

    /// <summary>A post effect reading GenesisSky draws the sun's colour, the horizon colour and the direction to the sun.</summary>
    public static void PostEffectsReadTheSky(HeadlessContext ctx)
    {
        Vector3 sunColor = new(1f, 0.5f, 0.25f), horizon = new(0.2f, 0.8f, 0.4f);
        Vector3 lightDirection = Vector3.Normalize(new Vector3(0.3f, -0.8f, 0.5f));
        Vector3 towardSun = -lightDirection;
        int Byte(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);

        List<string> failures = [];
        foreach (RenderBackendOption backend in new[] { RenderBackendOption.SilkNetDx11, RenderBackendOption.Vulkan })
        {
            using var host = UnattendedWindowing.NewHost(Width, Height);
            UnattendedWindowing.ShowWithoutFocus(host);
            using IRenderController renderer = RenderControllerFactory.Create(backend);
            renderer.Initialize(host.Handle, Width, Height);
            PostEffectRequest effect = new("Sky inputs", SkySource, "MainPS", string.Empty, string.Empty,
                Vector4.Zero, Vector4.Zero, Vector4.Zero, Vector4.Zero);
            byte[] pixels = [];
            bool drawn = false;
            // The effect compiles off the frame: draw until it shows (at most about ten seconds).
            for (int frame = 0; frame < 200 && !drawn; frame++)
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false; state.ShadowsEnabled = false;
                state.LightDirection = lightDirection; state.SunColor = sunColor; state.SunIntensity = 1.5f;
                state.SkyHorizonColor = horizon; state.SkyZenithColor = new Vector3(0.1f, 0.2f, 0.9f);
                renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(D3dMatrixHelper.CreateLookAtLh(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY),
                    D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 3f, Width / (float)Height, 0.1f, 100f));
                renderer.BeginFrame(); renderer.Clear(0.02f, 0.02f, 0.02f);
                renderer.SetPostEffects([effect]);
                renderer.EndFrame();
                HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out _, out pixels) && width == Width,
                    backend + ": no frame was read back.");
                renderer.Present();
                HeadlessHarness.Assert(string.IsNullOrEmpty(renderer.LastPostEffectError), backend + ": the effect did not compile: " + renderer.LastPostEffectError);
                int first = (Height / 2 * Width + 50) * 4;
                drawn = Math.Abs(pixels[first + 2] - Byte(sunColor.X)) <= 3 && Math.Abs(pixels[first + 1] - Byte(sunColor.Y)) <= 3;
                if (!drawn) Thread.Sleep(50);
            }
            (int R, int G, int B) At(int x)
            {
                int offset = (Height / 2 * Width + x) * 4;
                return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
            }
            bool Near((int R, int G, int B) c, Vector3 expected) =>
                Math.Abs(c.R - Byte(expected.X)) <= 3 && Math.Abs(c.G - Byte(expected.Y)) <= 3 && Math.Abs(c.B - Byte(expected.Z)) <= 3;
            if (!Near(At(50), sunColor)) failures.Add($"{backend}: the sun's colour read {At(50)}");
            if (!Near(At(150), horizon)) failures.Add($"{backend}: the horizon colour read {At(150)}");
            if (!Near(At(250), towardSun * 0.5f + new Vector3(0.5f))) failures.Add($"{backend}: the direction to the sun read {At(250)}");
        }
        HeadlessHarness.Assert(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>
    /// A game's sun and fog colours by the hour (Engine.Sky.SetSunColorAt, SetFogColorAt) are what
    /// the climate stamps on the frame, blended between the hours given and round the clock; with
    /// none set, or after clearing, the climate's own colours are back.
    /// </summary>
    public static void ClimateKeepsTheGameColoursByHour()
    {
        var climate = new Genesis.Runtime.Climate.EnvironmentService(new Genesis.Runtime.Climate.EnvironmentOptions { StartTimeHours = 12f, Paused = true });
        var atmosphere = new Genesis.Runtime.Climate.AtmosphereService();
        atmosphere.Update(climate.Current, Vector3.Zero);
        Mesh3DState Stamp()
        {
            Mesh3DState state = Mesh3DState.Default;
            EnvironmentMapper.StampClimateAtmosphere(ref state, climate, atmosphere);
            return state;
        }
        static bool Same(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;

        Mesh3DState own = Stamp();
        Vector3 dawn = new(1f, 0.5f, 0.2f), dusk = new(0.2f, 0.3f, 1f), mist = new(0.4f, 0.8f, 0.4f);
        var options = atmosphere.Options;
        options.SunColorHours = Genesis.Runtime.Climate.HourColors.Set(Genesis.Runtime.Climate.HourColors.Set([], 6f, dawn), 18f, dusk);
        options.FogColorHours = Genesis.Runtime.Climate.HourColors.Set([], 12f, mist);
        Mesh3DState set = Stamp();
        HeadlessHarness.Assert(Same(set.SunColor, Vector3.Lerp(dawn, dusk, 0.5f)),
            $"At noon, halfway between the dawn and dusk colours, the sun was {set.SunColor}.");
        HeadlessHarness.Assert(Same(new Vector3(set.FogColor.X, set.FogColor.Y, set.FogColor.Z), mist) && set.FogColor.W == own.FogColor.W,
            $"The fog colour was {set.FogColor}, not the game's.");

        // Round the clock: 22:00 and 02:00 meet at midnight.
        HeadlessHarness.Assert(Genesis.Runtime.Climate.HourColors.TryEvaluate(
                Genesis.Runtime.Climate.HourColors.Set(Genesis.Runtime.Climate.HourColors.Set([], 22f, dawn), 2f, dusk), 0f, out Vector3 midnight)
            && Same(midnight, Vector3.Lerp(dawn, dusk, 0.5f)), $"The colours do not blend round midnight ({midnight}).");

        options.SunColorHours = [];
        options.FogColorHours = [];
        Mesh3DState cleared = Stamp();
        HeadlessHarness.Assert(Same(cleared.SunColor, own.SunColor) && cleared.FogColor == own.FogColor,
            "With no colours by the hour the climate's own did not come back.");
    }

    /// <summary>
    /// The lighting term views: a sunlit wall with no ambient light reads black in the ambient view
    /// and lit in the direct view; with ambient light and no sun, the other way round; the albedo
    /// view shows the wall's own colour either way.
    /// </summary>
    public static void LightingTermViews(HeadlessContext ctx)
    {
        using var host = UnattendedWindowing.NewHost(Width, Height);
        UnattendedWindowing.ShowWithoutFocus(host);
        using IRenderController renderer = RenderControllerFactory.Create(RenderBackendOption.SilkNetDx11);
        renderer.Initialize(host.Handle, Width, Height);
        var (vertices, indices) = Genesis.Rendering.Meshes.MeshGeometry.BuildCube(RenderColor.White, 1f);
        MeshHandle wall = renderer.RegisterMesh(vertices, indices);
        try
        {
            double Luma(RenderDebugView view, bool sun, bool ambient)
            {
                Mesh3DState state = Mesh3DState.Default;
                state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false; state.ShadowsEnabled = false;
                state.LightingEnabled = true; state.DebugView = view;
                state.LightDirection = new Vector3(0f, 0f, 1f); state.SunColor = Vector3.One; state.SunIntensity = sun ? 1.2f : 0f;
                state.AmbientColor = new Vector3(ambient ? 0.8f : 0f); state.AmbientGroundColor = new Vector3(ambient ? 0.8f : 0f);
                renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                renderer.SetCamera3D(D3dMatrixHelper.CreateLookAtLh(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY),
                    D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 3f, Width / (float)Height, 0.1f, 100f));
                renderer.BeginFrame(); renderer.Clear(0.3f, 0.3f, 0.3f);
                renderer.ClearPointLights();
                renderer.DrawMesh(new MeshDrawCall
                {
                    Mesh = wall, World = Matrix4x4.CreateScale(20f, 20f, 0.1f) * Matrix4x4.CreateTranslation(0f, 0f, 4f),
                    Tint = new RenderColor(0.8f, 0.5f, 0.3f), Alpha = 1f, Flags = MeshDrawFlags.NoShadow | MeshDrawFlags.NoFog,
                });
                renderer.EndFrame();
                HeadlessHarness.Assert(renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels), "No frame was read back.");
                renderer.Present();
                int offset = (height / 2 * width + width / 2) * 4;
                return 0.0722 * pixels[offset] + 0.7152 * pixels[offset + 1] + 0.2126 * pixels[offset + 2];
            }

            double sunAmbient = Luma(RenderDebugView.AmbientLight, sun: true, ambient: false);
            double sunDirect = Luma(RenderDebugView.DirectLight, sun: true, ambient: false);
            double skyAmbient = Luma(RenderDebugView.AmbientLight, sun: false, ambient: true);
            double skyDirect = Luma(RenderDebugView.DirectLight, sun: false, ambient: true);
            double albedoLit = Luma(RenderDebugView.Albedo, sun: true, ambient: false);
            double albedoDark = Luma(RenderDebugView.Albedo, sun: false, ambient: false);
            HeadlessHarness.Assert(sunAmbient < 8 && sunDirect > 40,
                $"A sunlit wall with no ambient light read {sunAmbient:F0} in the ambient view and {sunDirect:F0} in the direct view.");
            HeadlessHarness.Assert(skyAmbient > 40 && skyDirect < 8,
                $"A wall under ambient light alone read {skyAmbient:F0} in the ambient view and {skyDirect:F0} in the direct view.");
            HeadlessHarness.Assert(albedoLit > 60 && Math.Abs(albedoLit - albedoDark) < 3,
                $"The albedo view depends on the light ({albedoLit:F0} lit, {albedoDark:F0} unlit).");
        }
        finally { renderer.ReleaseMesh(wall); }
    }

    /// <summary>The hemisphere sky light's ground tint tints the light from below and leaves the light from above to the tint.</summary>
    public static void GroundTintTintsTheLightFromBelow()
    {
        var dome = new SkyDome(new Vector3(0.2f, 0.4f, 0.9f), new Vector3(0.6f, 0.7f, 0.9f), new Vector3(0.3f, 0.3f, 0.3f),
            false, false, 0.6f, 0.6f, new Vector3(1f, 0.95f, 0.9f));
        Vector3 tint = new(0.8f, 0.9f, 1.2f), groundTint = new(1.3f, 1.0f, 0.6f);
        SkyLightTerms one = SkyLightMath.Compute(dome, 1.2f, tint, 0.6f);
        SkyLightTerms two = SkyLightMath.Compute(dome, 1.2f, tint, 0.6f, groundTint);
        static bool Same(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-5f;
        HeadlessHarness.Assert(Same(one.Up, two.Up) && Same(one.MirrorZenith, two.MirrorZenith) && Same(one.MirrorHorizon, two.MirrorHorizon),
            "The ground tint changed the light from above.");
        HeadlessHarness.Assert(Same(two.Down, one.Down / tint * groundTint) && Same(two.MirrorGround, one.MirrorGround / tint * groundTint),
            "The light from below does not carry the ground tint.");

        var environment = new SceneEnvironment { SkyLightTint = tint };
        Mesh3DState state = Mesh3DState.Default;
        EnvironmentMapper.ApplySkyLightAndExposure(ref state, environment);
        HeadlessHarness.Assert(state.SkyLightGroundTint == Vector3.Zero, "With no ground tint the state should keep the one tint for both.");
        environment.SkyLightGroundTint = groundTint;
        EnvironmentMapper.ApplySkyLightAndExposure(ref state, environment);
        HeadlessHarness.Assert(Same(state.SkyLightGroundTint, groundTint), "The room's ground tint did not reach the renderer's state.");
    }
}
