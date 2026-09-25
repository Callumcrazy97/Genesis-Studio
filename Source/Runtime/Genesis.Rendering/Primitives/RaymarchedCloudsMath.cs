using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF2.3 CPU reference for density-driven raymarched clouds. Weather channels match the AF2.2
/// RGBA8 upload (Coverage / Type / Erosion / Vertical). No GPU devices — headless gates only.
/// </summary>
/// <remarks>
/// <see cref="UpdateEveryFrame"/> is always true: SkyForge / Aetherforge skipped the cloud pass
/// three frames in four and reprojected, which made layers drag with the camera and snap back.
/// Cost control is the bounded step count (≤ <see cref="MaxStepCount"/>) and AF2.4's internal
/// resolution / blend ladder — never a reduced frame schedule. Software skips the GPU pass and
/// keeps FogVolumes as the analytic fallback.
/// </remarks>
public static class RaymarchedCloudsMath
{
    /// <summary>Default march steps for the half-res GPU pass.</summary>
    public const int DefaultStepCount = 24;

    /// <summary>Hard ceiling on the HLSL loop and CPU integrator.</summary>
    public const int MaxStepCount = 48;

    /// <summary>World Y of the cloud slab base (metres).</summary>
    public const float DefaultCloudBase = 180f;

    /// <summary>Cloud slab thickness above <see cref="DefaultCloudBase"/>.</summary>
    public const float DefaultThickness = 85f;

    /// <summary>Composite additive intensity for FogPost.</summary>
    public const float DefaultIntensity = 0.85f;

    /// <summary>Coverage below this is treated as empty space (skip density / larger GPU steps).</summary>
    public const float EmptyCoverageThreshold = 0.16f;

    /// <summary>
    /// Always true — the raymarch runs every frame when enabled. Never skip 3-in-4.
    /// </summary>
    public const bool UpdateEveryFrame = true;

    /// <summary>Clamp authored step count into the AF2.3 bound.</summary>
    public static int ClampStepCount(int stepCount) =>
        Math.Clamp(stepCount, 1, MaxStepCount);

    /// <summary>
    /// Soft-slab density at <paramref name="worldPos"/>. Returns 0 outside the slab, when coverage
    /// is below <see cref="EmptyCoverageThreshold"/>, or when erosion fully carves the sample.
    /// </summary>
    /// <param name="weatherSample">
    /// Packed AF2.2 channels: X=Coverage, Y=CloudType, Z=Erosion, W=VerticalDevelopment (0..1).
    /// </param>
    public static float SampleDensity(
        Vector3 worldPos,
        Vector4 weatherSample,
        float cloudBase = DefaultCloudBase,
        float thickness = DefaultThickness)
    {
        float coverage = Math.Clamp(weatherSample.X, 0f, 1f);
        if (coverage < EmptyCoverageThreshold)
            return 0f;

        float baseY = cloudBase;
        float thick = MathF.Max(thickness, 1e-3f);
        float vertical = Math.Clamp(weatherSample.W, 0f, 1f);
        // Vertical development boosts effective height so storm towers reach higher.
        float topY = baseY + thick * (0.55f + 0.45f * vertical);
        if (worldPos.Y < baseY || worldPos.Y > topY)
            return 0f;

        float h = (worldPos.Y - baseY) / MathF.Max(topY - baseY, 1e-3f);
        // Soft slab falloff — dense mid-layer, soft edges at floor/ceiling.
        float slab = MathF.Pow(MathF.Max(0f, 1f - MathF.Abs(h * 2f - 1f)), 1.35f);
        if (slab <= 1e-5f)
            return 0f;

        float cloudType = Math.Clamp(weatherSample.Y, 0f, 1f);
        float erosion = Math.Clamp(weatherSample.Z, 0f, 1f);

        // Cheap hash noise substitute for FBM (GPU uses a fuller FBM; this is the gate reference).
        float noise = Hash01(
            worldPos.X * 0.031f + cloudType * 17f,
            worldPos.Y * 0.047f,
            worldPos.Z * 0.029f);

        // Soft coverage threshold: high coverage always keeps a floor so the headless gate is
        // deterministic; low coverage still thins via the empty-space early-out above.
        float threshold = 1f - coverage * 0.92f;
        float shaped = Math.Clamp((noise - threshold) / MathF.Max(1f - threshold, 1e-3f), 0f, 1f);
        float density = slab * coverage * MathF.Max(shaped, 0.25f + 0.55f * noise);

        // Erosion carves: high erosion removes wispy edges first.
        float carve = Math.Clamp(1f - erosion * (0.35f + 0.55f * (1f - density)), 0f, 1f);
        density *= carve;

        return Math.Clamp(density, 0f, 1f);
    }

    /// <summary>
    /// Integrates optical depth along a short ray for the headless gate. Dense clouds accumulate
    /// more depth as path length grows.
    /// </summary>
    public static float IntegrateOpticalDepth(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float pathLength,
        Vector4 weatherSample,
        float cloudBase = DefaultCloudBase,
        float thickness = DefaultThickness,
        int stepCount = DefaultStepCount)
    {
        float length = MathF.Max(pathLength, 0f);
        if (length <= 1e-5f)
            return 0f;

        float dirLen = rayDirection.Length();
        if (dirLen < 1e-8f)
            return 0f;
        Vector3 direction = rayDirection / dirLen;

        int steps = ClampStepCount(stepCount);
        float segment = length / steps;
        float optical = 0f;

        for (int i = 0; i < steps; i++)
        {
            float t = (i + 0.5f) / steps;
            Vector3 samplePos = rayOrigin + direction * (length * t);
            float density = SampleDensity(samplePos, weatherSample, cloudBase, thickness);
            optical += density * segment;
        }

        return optical;
    }

    private static float Hash01(float x, float y, float z)
    {
        // Integer lattice hash — stable, cheap, no System.Random.
        int xi = (int)MathF.Floor(x * 12.9898f);
        int yi = (int)MathF.Floor(y * 78.233f);
        int zi = (int)MathF.Floor(z * 37.719f);
        uint h = unchecked((uint)(xi * 374761393 + yi * 668265263 + zi * 2147483647));
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }
}
