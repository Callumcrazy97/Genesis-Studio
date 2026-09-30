using System.Drawing;
using System.Numerics;
using Genesis.Application.Runtime;
using Genesis.Rendering.Core;
using Genesis.Rendering.Primitives;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Froxel volumetric fog: the CPU reference model (slice integration, closed-form height fog) and,
/// on every GPU backend, that height fog thickens toward the ground, sun shadows carve the fog, and
/// a surface that fogs itself (no depth write) receives the same fog as one the composite fogs.
/// </summary>
internal static class FroxelFogSuite
{
    private static readonly RenderBackendOption[] GpuBackends =
    {
        RenderBackendOption.SilkNetDx11, RenderBackendOption.Direct3D12,
        RenderBackendOption.Vulkan, RenderBackendOption.OpenGL,
    };

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Froxel.SliceIntegrationMatchesAnalytic", SliceIntegration);
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Froxel.HeightFogIntegralMatchesNumeric", HeightFogIntegral);
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Froxel.HeightFogShadowsAndSurfacesOnAllBackends", () => GpuFog(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Froxel.GoldenSceneRepeatsExactly", () => GoldenRepeats(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Engine.Render.Froxel.GpuParticlesFogAtTheirOwnDepth", () => GpuParticleFog(ctx));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void SliceIntegration()
    {
        // A homogeneous medium split into the froxel slices must integrate to the closed form.
        const int slices = 64;
        const float far = FroxelFogMath.DefaultFarDepth;
        const float sigma = 0.02f;
        Vector3 radiance = new(0.6f, 0.7f, 0.8f);
        Vector3 inscatter = Vector3.Zero;
        float transmittance = 1f;
        float previous = 0f;
        for (int s = 0; s < slices; s++)
        {
            float end = FroxelFogMath.SliceDepth(s + 1, slices, far);
            FroxelFogMath.IntegrateSlice(ref inscatter, ref transmittance, radiance * sigma, sigma, end - previous);
            previous = end;
        }
        float expected = MathF.Exp(-sigma * far);
        Check(MathF.Abs(transmittance - expected) < 1e-4f,
            $"Slice transmittance {transmittance} differs from exp(-σd) = {expected}.");
        Vector3 expectedScatter = radiance * (1f - expected);
        Check(Vector3.Distance(inscatter, expectedScatter) < 1e-3f,
            $"Slice in-scatter {inscatter} differs from L(1 - T) = {expectedScatter}.");

        // Slice mapping round-trips and is exponential (fine near the camera, coarse far away).
        for (float depth = 0.5f; depth < far; depth *= 1.7f)
        {
            float coordinate = FroxelFogMath.SliceCoordinate(depth, slices, far);
            Check(MathF.Abs(FroxelFogMath.SliceDepth(coordinate, slices, far) - depth) < depth * 1e-3f,
                $"Slice mapping does not round-trip at {depth} m.");
        }
        float nearThickness = FroxelFogMath.SliceDepth(1, slices, far) - FroxelFogMath.SliceDepth(0, slices, far);
        float farThickness = FroxelFogMath.SliceDepth(slices, slices, far) - FroxelFogMath.SliceDepth(slices - 1, slices, far);
        Check(nearThickness < 0.05f && farThickness > 10f,
            $"Slices are not exponential (near {nearThickness:F3} m, far {farThickness:F1} m).");
    }

    private static void HeightFogIntegral()
    {
        (Vector3 From, Vector3 To)[] segments =
        {
            (new Vector3(0f, 2f, 0f), new Vector3(0f, 2f, 300f)),    // level, above the base
            (new Vector3(0f, -5f, 0f), new Vector3(0f, -5f, 100f)),  // level, below the base
            (new Vector3(0f, -3f, 0f), new Vector3(40f, 60f, 400f)), // climbing through the base
            (new Vector3(0f, 80f, 0f), new Vector3(0f, 1f, 50f)),    // descending
        };
        foreach ((Vector3 from, Vector3 to) in segments)
        {
            float analytic = FroxelFogMath.HeightFogOpticalDepth(from, to, 0.02f, 0f, 0.15f, 0.9f);
            double numeric = 0;
            const int steps = 20000;
            float length = Vector3.Distance(from, to);
            for (int i = 0; i < steps; i++)
            {
                Vector3 point = Vector3.Lerp(from, to, (i + 0.5f) / steps);
                numeric += FroxelFogMath.HeightFogDensity(point.Y, 0.02f, 0f, 0.15f, 0.9f) * length / steps;
            }
            Check(Math.Abs(analytic - numeric) <= Math.Max(1e-5, numeric * 0.002),
                $"Closed-form height fog {analytic} differs from the numeric integral {numeric} for {from} → {to}.");
        }
        // Height fog must be densest at the ground (the pre-rewrite model was inverted).
        Check(FroxelFogMath.HeightFogDensity(0f, 0.02f, 0f, 0.15f, 1f) > FroxelFogMath.HeightFogDensity(20f, 0.02f, 0f, 0.15f, 1f) * 5f,
            "Height fog is not denser at the ground than 20 m up.");
    }

    // With VolumetricTemporalBlend = 1 the fog volume has no jitter or history, and every cached
    // shadow map must reproduce exactly, so repeated captures of the golden scene are identical.
    private static void GoldenRepeats(HeadlessContext ctx)
    {
        using RenderParityHarness harness = new(RenderBackendOption.SilkNetDx11);
        var captures = new List<RuntimeImageMetrics>();
        for (int i = 0; i < 6; i++)
            captures.Add(harness.Capture(Path.Combine(ctx.Captures, $"golden-repeat-{i}.png")));
        var deltas = new List<string>();
        for (int i = 1; i < captures.Count; i++)
            deltas.Add($"{i}: {RuntimeImageMetrics.MaxTileDelta(captures[i - 1], captures[i]):F3} vs previous, "
                + $"{RuntimeImageMetrics.MaxTileDelta(captures[0], captures[i]):F3} vs first");
        bool identical = captures.Skip(1).All(capture => RuntimeImageMetrics.MaxTileDelta(captures[0], capture) == 0);
        Check(identical, "Repeated golden captures differ: " + string.Join("; ", deltas));
    }

    // GPU particles write no depth. Fogged in the scene target, the composite fogged them at the far
    // wall 105 m behind them; the particle layer fogs them at their own depth, like an opaque box of
    // the same colour at the same depth.
    private static void GpuParticleFog(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend.ToString().ToLowerInvariant();
                using RuntimeViewportHarness harness = new();
                string clearFile = Path.Combine(ctx.Captures, $"froxel-{name}-particles-clear.png");
                string fogFile = Path.Combine(ctx.Captures, $"froxel-{name}-particles-fog.png");
                harness.CaptureFogParticles(clearFile, fog: false);
                harness.CaptureFogParticles(fogFile, fog: true);

                using Bitmap clear = new(clearFile);
                using Bitmap fog = new(fogFile);
                Point particle = harness.ProjectFogPoint(RuntimeViewportHarness.FogParticlePoint);
                Point reference = harness.ProjectFogPoint(RuntimeViewportHarness.FogParticleReference);
                double particleClear = Luminance(clear, particle);
                double referenceClear = Luminance(clear, reference);
                Check(Math.Abs(particleClear - referenceClear) < 8,
                    $"{backend}: the particles did not draw in their colour ({particleClear:F1} vs box {referenceClear:F1}).");

                double particleFog = Luminance(fog, particle);
                double referenceFog = Luminance(fog, reference);
                double added = Math.Abs(referenceFog - referenceClear);
                Check(added > 4, $"{backend}: fog did not reach the reference box ({referenceClear:F1} → {referenceFog:F1}).");
                Check(Math.Abs(particleFog - referenceFog) <= Math.Max(4.0, added * 0.2),
                    $"{backend}: particles were not fogged at their own depth ({particleFog:F1} vs box {referenceFog:F1}; "
                    + $"both {particleClear:F1}/{referenceClear:F1} without fog).");
            }
        }
        finally { RenderBackendSelection.Configure(previous); }
    }

    private static double Luminance(Bitmap bitmap, Point point)
    {
        double sum = 0;
        int count = 0;
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                Color c = bitmap.GetPixel(Math.Clamp(point.X + x, 0, bitmap.Width - 1), Math.Clamp(point.Y + y, 0, bitmap.Height - 1));
                sum += 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                count++;
            }
        return sum / count;
    }

    private static void GpuFog(HeadlessContext ctx)
    {
        RenderBackendOption previous = RenderBackendSelection.RequestedBackend;
        try
        {
            foreach (RenderBackendOption backend in GpuBackends)
            {
                RenderBackendSelection.Configure(backend);
                string name = backend.ToString().ToLowerInvariant();
                using RuntimeViewportHarness harness = new();
                string clearFile = Path.Combine(ctx.Captures, $"froxel-{name}-clear.png");
                string fogFile = Path.Combine(ctx.Captures, $"froxel-{name}-fog.png");
                string litFile = Path.Combine(ctx.Captures, $"froxel-{name}-roof-unshadowed.png");
                string shadowFile = Path.Combine(ctx.Captures, $"froxel-{name}-roof-shadowed.png");
                harness.CaptureFogScene(clearFile, fog: false, shadows: false, roof: false);
                harness.CaptureFogScene(fogFile, fog: true, shadows: false, roof: false);
                harness.CaptureFogScene(litFile, fog: true, shadows: false, roof: true);
                harness.CaptureFogScene(shadowFile, fog: true, shadows: true, roof: true);
                var shadowStats = harness.LastStats;

                using Bitmap clear = new(clearFile);
                using Bitmap fog = new(fogFile);
                double FogAdded(Vector3 point)
                {
                    Point pixel = harness.ProjectFogPoint(point);
                    return Luminance(fog, pixel) - Luminance(clear, pixel);
                }

                double low = FogAdded(RuntimeViewportHarness.FogLowPanel);
                double high = FogAdded(RuntimeViewportHarness.FogHighPanel);
                Check(low > 12, $"{backend}: fog did not reach the ground-level panel (+{low:F1}).");
                Check(low > high * 1.3,
                    $"{backend}: height fog is not thicker near the ground (low +{low:F1}, high +{high:F1}).");

                // The forward pass fogs the no-depth-write panel itself; the composite fogs its twin.
                // Both now read the same froxel volume, so they must agree (they used to use two models).
                double surface = FogAdded(RuntimeViewportHarness.FogSurfacePanel);
                double post = FogAdded(RuntimeViewportHarness.FogPostPanel);
                Check(Math.Abs(surface - post) <= Math.Max(4.0, post * 0.2),
                    $"{backend}: self-fogged and composite-fogged surfaces disagree (+{surface:F1} vs +{post:F1}).");

                using Bitmap lit = new(litFile);
                using Bitmap shadowed = new(shadowFile);
                Point underRoof = harness.ProjectFogPoint(RuntimeViewportHarness.FogUnderRoof);
                double unshadowed = Luminance(lit, underRoof);
                double inShadow = Luminance(shadowed, underRoof);
                Check(inShadow < unshadowed * 0.93,
                    $"{backend}: the roof's sun shadow did not darken the fog beneath it ({inShadow:F1} vs {unshadowed:F1}; "
                    + $"cascades rendered {shadowStats.ShadowCascadesRendered}, caster draws {shadowStats.ShadowCasterDraws}, draws {shadowStats.DrawCalls3D}).");
            }
        }
        finally { RenderBackendSelection.Configure(previous); }
    }
}
