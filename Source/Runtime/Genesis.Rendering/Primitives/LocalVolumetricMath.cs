using System;
using System.Numerics;
using Genesis.Rendering.Lights;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.5 CPU reference for bounded local-light volumetric scatter
/// around the strongest few clustered lights. Pure math for headless gates; no GPU devices.
/// </summary>
/// <remarks>
/// This is deliberately *not* ForestLight's 24-step half-res atmosphere. Height fog, distance haze
/// and CSM sun shafts already exist in the fog composite and this term never replaces them: the
/// result is additive inscatter, so a scene with no local lights composites bit-identically to one
/// with the pass switched off.
///
/// The march is bounded three ways — a fixed <see cref="StepCount"/> (never above
/// <see cref="MaxStepCount"/>), a short <see cref="DefaultMaxDistance"/> segment along the view
/// ray, and a strongest-N light budget capped at <see cref="MaxBudget"/>. Selection reuses
/// <see cref="OmniShadowMath.SelectSlots"/> so the light AF1.3 gives a cubemap to is the same light
/// this pass beams, rather than a second ranking that can disagree with it.
///
/// Screen-space temporal skipping is forbidden here. Running the pass on one frame in four and
/// reprojecting the rest is what makes SkyForge's clouds and ForestLight's atmosphere swim under
/// camera motion; a bounded budget is the cost control, not a reduced frame rate for the effect.
/// </remarks>
public static class LocalVolumetricMath
{
    /// <summary>Samples marched along the view ray by default.</summary>
    public const int StepCount = 8;

    /// <summary>Hard ceiling on the march; the HLSL loop bound never exceeds this.</summary>
    public const int MaxStepCount = 12;

    /// <summary>Length of the view-ray segment sampled for local scatter, in world units.</summary>
    public const float DefaultMaxDistance = 12f;

    /// <summary>Composite strength for the additive inscatter.</summary>
    public const float DefaultStrength = 0.55f;

    public const int MinBudget = 0;
    public const int MaxBudget = 4;

    /// <summary>One source by default — never a beam per local light.</summary>
    public const int DefaultBudget = 1;

    /// <summary>Point-light falloff exponent: <c>(1 - saturate(d / radius))^Falloff</c>.</summary>
    public const float Falloff = 2f;

    /// <summary>Scales accumulated inscatter into a sane HDR range for the additive composite.</summary>
    public const float DensityScale = 0.25f;

    /// <summary>Ceiling on accumulated scatter so a bright torch cannot blow the composite out.</summary>
    public const float MaxScatter = 4f;

    /// <summary>Clamp an authored local-volumetric light budget to the AF1.5 range.</summary>
    public static int ClampBudget(int budget) => Math.Clamp(budget, MinBudget, MaxBudget);

    /// <summary>Clamp a step count to the bounded march.</summary>
    public static int ClampStepCount(int stepCount) => Math.Clamp(stepCount, 1, MaxStepCount);

    /// <summary>
    /// Picks the strongest <paramref name="budget"/> lights for this frame's beams. Delegates to
    /// <see cref="OmniShadowMath.SelectSlots"/> — the AF1.3 camera weight is the ranking, so the
    /// budgeted cubemap and the budgeted beam always land on the same torch.
    /// </summary>
    public static int SelectLights(
        ReadOnlySpan<ClusterPointLightGpu> lights,
        int budget,
        Vector3 cameraPos,
        Span<int> slotIndices) =>
        OmniShadowMath.SelectSlots(lights, ClampBudget(budget), cameraPos, slotIndices);

    /// <summary>
    /// Integrates local inscatter along a view ray and returns the scalar (colourless) magnitude
    /// the GPU pass adds per channel. Zero when no light is selected or every sample falls outside
    /// every light's radius.
    /// </summary>
    /// <param name="rayOrigin">Camera position; also the weight origin for strongest-N selection.</param>
    /// <param name="rayDirection">Direction through the pixel; normalised internally.</param>
    /// <param name="rayLength">Distance to the shaded surface, clamped to <paramref name="maxDistance"/>.</param>
    public static float EvaluateScatter(
        ReadOnlySpan<ClusterPointLightGpu> lights,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float rayLength,
        int budget = DefaultBudget,
        float strength = DefaultStrength,
        int stepCount = StepCount,
        float maxDistance = DefaultMaxDistance)
    {
        int cap = ClampBudget(budget);
        if (cap <= 0 || lights.IsEmpty)
            return 0f;

        float length = MathF.Min(MathF.Max(rayLength, 0f), MathF.Max(maxDistance, 0f));
        if (length <= 1e-5f)
            return 0f;

        float dirLength = rayDirection.Length();
        if (dirLength < 1e-8f)
            return 0f;
        Vector3 direction = rayDirection / dirLength;

        Span<int> slots = stackalloc int[MaxBudget];
        int selected = SelectLights(lights, cap, rayOrigin, slots);
        if (selected <= 0)
            return 0f;

        int steps = ClampStepCount(stepCount);
        float segment = length / steps;
        float scatter = 0f;

        for (int step = 0; step < steps; step++)
        {
            float t = (step + 0.5f) / steps;
            Vector3 samplePos = rayOrigin + direction * (length * t);

            for (int i = 0; i < selected; i++)
            {
                ref readonly ClusterPointLightGpu light = ref lights[slots[i]];
                Vector3 lightPos = new(light.PosRadius.X, light.PosRadius.Y, light.PosRadius.Z);
                float radius = MathF.Max(light.PosRadius.W, 1e-3f);
                float intensity = MathF.Max(light.ColorIntensity.W, 0f);
                if (intensity <= 0f)
                    continue;

                float distance = Vector3.Distance(samplePos, lightPos);
                float falloff = 1f - Math.Clamp(distance / radius, 0f, 1f);
                if (falloff <= 0f)
                    continue;

                scatter += intensity * MathF.Pow(falloff, Falloff) * DensityScale * segment;
            }
        }

        return MathF.Min(scatter * MathF.Max(strength, 0f), MaxScatter);
    }
}
