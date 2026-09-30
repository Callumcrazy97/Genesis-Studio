using System.Drawing;
using System.Numerics;
using Genesis.Application.Runtime;
using Genesis.Rendering.Core;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Lights;
using Genesis.Rendering.Primitives;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Shadow culling and local-light shadow regressions: cascade casters are chosen by the cascade's
/// light-space footprint (not camera distance), local shadow slots keep their light, cube faces and
/// spot cones cull casters, and up to four point/spot lights shadow through one cached atlas on
/// every GPU backend.
/// </summary>
internal static class ShadowQualitySuite
{
    private static readonly RenderBackendOption[] GpuBackends =
    {
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    };

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Shadows.CascadeCastersUseLightSpaceFootprint", CascadeFootprint);
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Shadows.LocalSlotsKeepTheirLight", SlotHysteresis);
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Shadows.CubeFaceAndSpotConeCulling", FaceAndConeCulling);
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Lights.ClustersSeparateLightsByDepth", ClusterDepthSlices);
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Shadows.SunCastsShadowOnAllBackends", () => SunShadow(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Shadows.FourLocalLightsShareAtlas", () => FourLocalLights(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Shadows.SpotLightConeAndShadow", () => SpotLight(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Lighting.TwoSidedSurfacesLitUnderEitherCamera", () => TwoSided(ctx));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Same construction as ForwardRenderer.ComputeLightViewProj: eye `extent` back from the focus
    // along the light, orthographic width `extent`, depth 0.5..4·extent.
    private static Matrix4x4 CascadeMatrix(Vector3 focus, Vector3 lightDir, float extent)
    {
        Vector3 eye = focus - lightDir * extent;
        Matrix4x4 view = D3dMatrixHelper.CreateLookAtLh(eye, focus, Vector3.UnitY);
        return view * D3dMatrixHelper.CreateOrthographicLh(extent, extent, 0.5f, extent * 4f);
    }

    private static void CascadeFootprint()
    {
        Vector3 light = Vector3.Normalize(new Vector3(-1f, -2f, -0.5f));
        Matrix4x4 vp = CascadeMatrix(Vector3.Zero, light, 20f);
        Check(CascadeShadowMath.CasterReachesCascade(vp, Vector3.Zero, 1f),
            "A caster at the cascade focus was culled.");
        Check(!CascadeShadowMath.CasterReachesCascade(vp, new Vector3(120f, 0f, 120f), 1f),
            "A caster far outside the cascade footprint was kept.");
        // A tall occluder whose centre lies between the light and the near plane used to be clipped
        // away; it is now pancaked onto the near plane, so it must still be drawn.
        Check(CascadeShadowMath.CasterReachesCascade(vp, -light * 35f, 2f),
            "A caster above the cascade's near plane was culled instead of pancaked.");
        // A wide building shell whose centre is outside the footprint still overlaps it.
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, light));
        Check(CascadeShadowMath.CasterReachesCascade(vp, right * 16f, 12f),
            "A large caster overlapping the footprint edge was culled by its centre.");
        Check(!CascadeShadowMath.CasterReachesCascade(vp, right * 16f, 1f),
            "A small caster beyond the footprint edge was kept.");
        Check(!CascadeShadowMath.CasterReachesCascade(vp, light * 120f, 1f),
            "A caster beyond the cascade's far plane was kept.");
    }

    private static ClusterPointLightGpu Light(Vector3 position, float radius, float intensity) => new()
    {
        PosRadius = new Vector4(position, radius),
        ColorIntensity = new Vector4(1f, 1f, 1f, intensity),
        FalloffPad = new Vector4(2f, 0f, 0f, 0f),
    };

    private static void SlotHysteresis()
    {
        Vector3 camera = Vector3.Zero;
        ClusterPointLightGpu a = Light(new Vector3(2f, 1f, 0f), 5f, 1f);
        ClusterPointLightGpu b = Light(new Vector3(-2f, 1f, 0f), 5f, 1f);
        Span<int> assignment = stackalloc int[4];
        Span<Vector4> previous = stackalloc Vector4[4];

        OmniShadowMath.AssignSlots(new[] { a, Light(b.PosRadius.AsVector3(), 5f, 0.9f) }, 1, camera, previous, assignment);
        Check(assignment[0] == 0, $"The stronger light did not win the only slot (slot 0 = {assignment[0]}).");
        previous[0] = a.PosRadius;

        // A challenger 20% stronger does not take the slot from the holder…
        OmniShadowMath.AssignSlots(new[] { a, Light(b.PosRadius.AsVector3(), 5f, 1.2f) }, 1, camera, previous, assignment);
        Check(assignment[0] == 0, "A marginally stronger light stole the slot; the shadow would flicker between lanterns.");
        // …but one twice as strong does.
        OmniShadowMath.AssignSlots(new[] { a, Light(b.PosRadius.AsVector3(), 5f, 2f) }, 1, camera, previous, assignment);
        Check(assignment[0] == 1, "A decisively stronger light could not take the slot.");

        // Continuing lights keep their slot index even when submitted in a different order, so
        // their cached atlas tiles stay valid.
        previous[0] = a.PosRadius;
        previous[1] = b.PosRadius;
        OmniShadowMath.AssignSlots(new[] { b, a }, 2, camera, previous, assignment);
        Check(assignment[0] == 1 && assignment[1] == 0,
            $"Reordered lights changed slots ({assignment[0]}, {assignment[1]}).");
        // A light that moved slightly (a carried torch) is still the same light.
        ClusterPointLightGpu moved = Light(a.PosRadius.AsVector3() + new Vector3(0.2f, 0f, 0f), 5f, 1f);
        OmniShadowMath.AssignSlots(new[] { b, moved }, 2, camera, previous, assignment);
        Check(assignment[0] == 1, "A slightly moved light lost its slot.");
    }

    private static Vector3 AsVector3(this Vector4 value) => new(value.X, value.Y, value.Z);

    private static void FaceAndConeCulling()
    {
        for (int face = 0; face < 6; face++)
        {
            bool expected = face == 0;
            Check(OmniShadowMath.SphereTouchesCubeFace(new Vector3(5f, 0f, 0f), 0.5f, face) == expected,
                $"A caster straight along +X was classified wrongly for face {face}.");
            Check(OmniShadowMath.SphereTouchesCubeFace(Vector3.Zero, 0.5f, face),
                $"A caster around the light was culled from face {face}.");
        }
        Check(OmniShadowMath.SphereTouchesCubeFace(new Vector3(5f, 5f, 0f), 0.3f, 0)
            && OmniShadowMath.SphereTouchesCubeFace(new Vector3(5f, 5f, 0f), 0.3f, 2)
            && !OmniShadowMath.SphereTouchesCubeFace(new Vector3(5f, 5f, 0f), 0.3f, 4),
            "A caster on the +X/+Y edge was not shared by exactly the two faces it straddles.");

        Vector3 down = -Vector3.UnitY;
        float cos25 = MathF.Cos(25f * MathF.PI / 180f);
        Check(OmniShadowMath.SphereTouchesSpotCone(new Vector3(0f, -3f, 0f), 0.2f, down, cos25),
            "A caster on the spot axis was culled.");
        Check(!OmniShadowMath.SphereTouchesSpotCone(new Vector3(3f, -1f, 0f), 0.2f, down, cos25),
            "A caster well outside the spot cone was kept.");
        Check(OmniShadowMath.SphereTouchesSpotCone(new Vector3(1.5f, -3f, 0f), 0.2f, down, cos25),
            "A caster overlapping the cone edge was culled by its centre.");
        Check(!OmniShadowMath.SphereTouchesSpotCone(new Vector3(0f, 3f, 0f), 0.2f, down, cos25),
            "A caster behind the spot light was kept.");
    }

    private static void ClusterDepthSlices()
    {
        // Engine convention: left-handed, clip w = view depth.
        Matrix4x4 view = D3dMatrixHelper.CreateLookAtLh(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);
        Matrix4x4 viewProj = view * D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 3f, 16f / 9f, 0.1f, 500f);
        ClusterPointLightGpu near = Light(new Vector3(0f, 0f, 3f), 1f, 1f);
        ClusterPointLightGpu far = Light(new Vector3(0f, 0f, 60f), 2f, 1f);
        ClusteredLightGrid grid = new();
        grid.Build(new[] { near, far }, viewProj, 1280, 720, Vector3.Zero);

        int centre = ClusteredLightDefaults.TileGridSize / 2;
        int nearSlice = ClusteredLightDefaults.SliceForDepth(3f);
        int farSlice = ClusteredLightDefaults.SliceForDepth(60f);
        Check(farSlice > nearSlice + 4, $"Depth slicing is too coarse ({nearSlice} vs {farSlice}).");
        ReadOnlySpan<uint> atNear = grid.ClusterLights(centre, centre, nearSlice);
        ReadOnlySpan<uint> atFar = grid.ClusterLights(centre, centre, farSlice);
        Check(atNear.Length == 1 && atNear[0] == 0,
            $"The near cluster should list only the near light ({atNear.Length} lights).");
        Check(atFar.Length == 1 && atFar[0] == 1,
            $"The far cluster should list only the far light ({atFar.Length} lights).");
        Check(grid.ClusterLights(centre, centre, (nearSlice + farSlice) / 2).Length == 0,
            "A slice between the two lights still listed a light; clusters are not depth-sliced.");

        // A crowded cluster keeps the strongest lights, not the first submitted.
        var crowd = new ClusterPointLightGpu[ClusteredLightDefaults.MaxLightsPerCluster + 16];
        for (int i = 0; i < crowd.Length; i++)
            crowd[i] = Light(new Vector3(0f, 0f, 5f), 1f, 1f + i);
        grid.Build(crowd, viewProj, 1280, 720, Vector3.Zero);
        ReadOnlySpan<uint> crowded = grid.ClusterLights(centre, centre, ClusteredLightDefaults.SliceForDepth(5f));
        Check(crowded.Length == ClusteredLightDefaults.MaxLightsPerCluster,
            $"A crowded cluster listed {crowded.Length} lights instead of the cap.");
        foreach (uint index in crowded)
            Check(index >= 16, $"A full cluster kept weak light {index} over a stronger one.");

        // Orthographic views have constant clip w, so everything shares one slice.
        Matrix4x4 ortho = view * D3dMatrixHelper.CreateOrthographicLh(20f, 20f, 0.1f, 100f);
        grid.Build(new[] { near, far }, ortho, 1280, 720, Vector3.Zero);
        int orthoSlice = ClusteredLightDefaults.SliceForDepth(1f);
        Check(grid.ClusterLights(centre, centre, orthoSlice).Length == 2,
            "An orthographic view split its lights across depth slices.");
    }

    private static double Luminance(Bitmap bitmap, Point point)
    {
        // Average a 5×5 block so a single dithered pixel cannot decide the result.
        double sum = 0;
        int count = 0;
        for (int y = -2; y <= 2; y++)
        {
            for (int x = -2; x <= 2; x++)
            {
                int px = Math.Clamp(point.X + x, 0, bitmap.Width - 1);
                int py = Math.Clamp(point.Y + y, 0, bitmap.Height - 1);
                Color c = bitmap.GetPixel(px, py);
                sum += 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                count++;
            }
        }
        return sum / count;
    }

    private static void WithBackends(Action<RenderBackendOption, string> test)
    {
        RenderBackendOption previousBackend = RenderBackendSelection.RequestedBackend;
        int previousBudget = PgslCommands.OmniShadowBudget;
        try
        {
            PgslCommands.OmniShadowBudget = 4;
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend switch
                {
                    RenderBackendOption.SilkNetDx11 => "dx11",
                    RenderBackendOption.Direct3D12 => "dx12",
                    RenderBackendOption.Vulkan => "vulkan",
                    _ => "opengl",
                };
                test(backend, name);
            }
        }
        finally
        {
            PgslCommands.OmniShadowBudget = previousBudget;
            RenderBackendSelection.Configure(previousBackend);
        }
    }

    private static void SunShadow(HeadlessContext ctx)
    {
        WithBackends((backend, name) =>
        {
            using RuntimeViewportHarness harness = new();
            string openFile = Path.Combine(ctx.Captures, $"sun-shadow-{name}-off.png");
            string shadowFile = Path.Combine(ctx.Captures, $"sun-shadow-{name}-on.png");
            harness.CaptureFogScene(openFile, fog: false, shadows: false, roof: true);
            harness.CaptureFogScene(shadowFile, fog: false, shadows: true, roof: true);
            using Bitmap open = new(openFile);
            using Bitmap shadowed = new(shadowFile);
            // Floor directly beneath the roof slab, and floor well past it in open sunlight.
            Point under = harness.ProjectFogPoint(new Vector3(0f, 0f, 25f));
            Point beyond = harness.ProjectFogPoint(new Vector3(0f, 0f, 110f));
            double openUnder = Luminance(open, under);
            double shadowUnder = Luminance(shadowed, under);
            Check(shadowUnder < openUnder * 0.75,
                $"{backend}: the roof cast no sun shadow on the floor beneath it ({shadowUnder:F1} vs {openUnder:F1}).");
            Check(Math.Abs(Luminance(shadowed, beyond) - Luminance(open, beyond)) < 6,
                $"{backend}: sun shadows darkened open floor beyond the roof.");
        });
    }

    private static void FourLocalLights(HeadlessContext ctx)
    {
        WithBackends((backend, name) =>
        {
            using RuntimeViewportHarness harness = new();
            string litFile = Path.Combine(ctx.Captures, $"local-shadows-{name}-open.png");
            string shadowFile = Path.Combine(ctx.Captures, $"local-shadows-{name}-occluded.png");
            (_, RenderStats open) = harness.CaptureLocalLights(litFile, pointLights: 4, spotLight: false, casters: false);
            (_, RenderStats occluded) = harness.CaptureLocalLights(shadowFile, pointLights: 4, spotLight: false, casters: true);

            Check(open.LocalShadowLights == 0,
                $"{backend}: lights with nothing to shadow still took atlas slots ({open.LocalShadowLights}).");
            Check(occluded.LocalShadowLights == 4,
                $"{backend}: only {occluded.LocalShadowLights} of 4 occluded lights shadowed through the atlas.");
            // Four point lights own four atlas rows of six cube faces each; once rendered, a static
            // scene keeps them cached (the shadows checked below come from those cached tiles).
            Check(occluded.LocalShadowTilesRendered == 0,
                $"{backend}: a static scene re-rendered {occluded.LocalShadowTilesRendered} local shadow atlas tiles.");
            Check(occluded.ShadowCascadesRendered == 0,
                $"{backend}: a static scene re-rendered {occluded.ShadowCascadesRendered} sun shadow cascades.");

            using Bitmap lit = new(litFile);
            using Bitmap shadowed = new(shadowFile);
            for (int i = 0; i < 4; i++)
            {
                Point point = harness.ProjectLocalLightsPoint(RuntimeViewportHarness.LocalShadowPoint(i));
                double withoutOccluder = Luminance(lit, point);
                double withOccluder = Luminance(shadowed, point);
                Check(withoutOccluder > 20,
                    $"{backend}: floor point {i} was not lit by the ring lights ({withoutOccluder:F1}).");
                Check(withOccluder < withoutOccluder * 0.75 && withoutOccluder - withOccluder > 12,
                    $"{backend}: light {i}'s occluder cast no shadow at {point} ({withOccluder:F1} vs {withoutOccluder:F1}).");
            }
        });
    }

    // Two-sided draws used a fixed clockwise front face, which is only right for the runtime's
    // left-handed camera: under the editors' right-handed cameras (and in water reflections) the
    // forward shader flipped their normals, so floors, foliage and quads lost direct light.
    private static void TwoSided(HeadlessContext ctx)
    {
        WithBackends((backend, name) =>
        {
            using RuntimeViewportHarness harness = new();
            string leftFile = Path.Combine(ctx.Captures, $"two-sided-{name}-left-handed.png");
            string rightFile = Path.Combine(ctx.Captures, $"two-sided-{name}-right-handed.png");
            harness.CaptureTwoSidedLighting(leftFile, rightHanded: false);
            harness.CaptureTwoSidedLighting(rightFile, rightHanded: true);
            using Bitmap left = new(leftFile);
            using Bitmap right = new(rightFile);
            // The slab fills the view's centre in both conventions (mirrored, but symmetric).
            Point centre = new(left.Width / 2, left.Height / 2 + left.Height / 8);
            double lit = Luminance(left, centre);
            double mirrored = Luminance(right, centre);
            Check(lit > 90, $"{backend}: the sunlit two-sided slab was dark under the left-handed camera ({lit:F1}).");
            Check(Math.Abs(mirrored - lit) < 6,
                $"{backend}: the two-sided slab lit differently under a right-handed camera ({mirrored:F1} vs {lit:F1}).");
        });
    }

    private static void SpotLight(HeadlessContext ctx)
    {
        WithBackends((backend, name) =>
        {
            using RuntimeViewportHarness harness = new();
            string openFile = Path.Combine(ctx.Captures, $"spot-{name}-open.png");
            string occludedFile = Path.Combine(ctx.Captures, $"spot-{name}-occluded.png");
            harness.CaptureLocalLights(openFile, pointLights: 0, spotLight: true, casters: false);
            (_, RenderStats occluded) = harness.CaptureLocalLights(occludedFile, pointLights: 0, spotLight: true, casters: true);
            Check(occluded.LocalShadowLights == 1,
                $"{backend}: the occluded spot light did not take a shadow slot ({occluded.LocalShadowLights}).");

            using Bitmap open = new(openFile);
            using Bitmap blocked = new(occludedFile);
            Point centre = harness.ProjectLocalLightsPoint(Vector3.Zero);
            Point insideCone = harness.ProjectLocalLightsPoint(new Vector3(-1.2f, 0f, 0f));
            Point outsideCone = harness.ProjectLocalLightsPoint(new Vector3(-3.2f, 0f, 0f));
            Point shadow = harness.ProjectLocalLightsPoint(RuntimeViewportHarness.SpotShadowPoint);
            double lit = Luminance(open, insideCone);
            Check(lit > 25, $"{backend}: the floor inside the spot cone was not lit ({lit:F1}).");
            Check(Luminance(open, outsideCone) < lit * 0.35,
                $"{backend}: the spot light lit the floor outside its cone ({Luminance(open, outsideCone):F1} vs {lit:F1}).");
            Check(Luminance(open, centre) >= lit * 0.8,
                $"{backend}: the spot centre was darker than its cone ({Luminance(open, centre):F1} vs {lit:F1}).");
            double openShadowPoint = Luminance(open, shadow);
            double blockedShadowPoint = Luminance(blocked, shadow);
            Check(openShadowPoint > 15 && blockedShadowPoint < openShadowPoint * 0.6,
                $"{backend}: the cube under the spot light cast no shadow at {shadow} ({blockedShadowPoint:F1} vs {openShadowPoint:F1}).");
        });
    }
}
