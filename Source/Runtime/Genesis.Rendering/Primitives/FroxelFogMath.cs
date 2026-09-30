using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// CPU reference for the froxel volumetric fog (<see cref="FroxelFogShaders"/>). The volume is a
/// camera-aligned grid of screen tiles × depth slices, with slices distributed exponentially from
/// <see cref="NearDepth"/> to the far depth; beyond it, fog is the closed-form exponential height-fog
/// integral. These functions mirror the HLSL line for line so headless tests can check the model.
/// </summary>
public static class FroxelFogMath
{
    /// <summary>Clip-w (view depth) where the first slice starts.</summary>
    public const float NearDepth = 0.25f;

    /// <summary>Depth covered by froxels; farther fog uses the analytic far field.</summary>
    public const float DefaultFarDepth = 200f;

    /// <summary>Grid for VolumetricFogQuality 0/1/2 (width × height × slices).</summary>
    public static (int Width, int Height, int Slices) GridForQuality(int quality) => Math.Clamp(quality, 0, 2) switch
    {
        0 => (128, 72, 48),
        1 => (160, 90, 64),
        _ => (192, 108, 96),
    };

    /// <summary>View depth of a (fractional) slice coordinate: slice k spans [SliceDepth(k), SliceDepth(k + 1)].</summary>
    public static float SliceDepth(float slice, int slices, float farDepth) =>
        NearDepth * MathF.Pow(farDepth / NearDepth, slice / slices);

    /// <summary>Continuous slice coordinate of a view depth, clamped to [0, slices].</summary>
    public static float SliceCoordinate(float depth, int slices, float farDepth) =>
        Math.Clamp(MathF.Log2(MathF.Max(depth, 1e-4f) / NearDepth) / MathF.Log2(farDepth / NearDepth) * slices, 0f, slices);

    /// <summary>
    /// Front-to-back, energy-conserving integration of one homogeneous slice (Hillaire 2015).
    /// <paramref name="scattering"/> is in-scattered radiance per unit length (σs·L) and
    /// <paramref name="extinction"/> is σt. Matches the old per-step march exactly when σs = σt.
    /// </summary>
    public static void IntegrateSlice(ref Vector3 inscatter, ref float transmittance, Vector3 scattering, float extinction, float length)
    {
        float sigma = MathF.Max(extinction, 1e-6f);
        float sliceTransmittance = MathF.Exp(-sigma * MathF.Max(length, 0f));
        inscatter += transmittance * (scattering - scattering * sliceTransmittance) / sigma;
        transmittance *= sliceTransmittance;
    }

    /// <summary>Height-fog density at altitude <paramref name="y"/> (FogDensityAt without its noise term).</summary>
    public static float HeightFogDensity(float y, float density, float heightBase, float falloff, float aerialBlend)
    {
        float heightMul = MathF.Exp(-MathF.Max(falloff, 1e-4f) * MathF.Max(y - heightBase, 0f));
        return MathF.Max(density, 0f) * Lerp(1f, heightMul, Math.Clamp(aerialBlend, 0f, 1f));
    }

    /// <summary>
    /// Closed-form optical depth ∫ρ dt of the height fog along a segment. With e(y) = exp(-k·max(y−b, 0))
    /// its antiderivative is F(y) = y − b below the base and (1 − exp(−k(y − b)))/k above it, so the
    /// mean of e over the segment is (F(y1) − F(y0))/(y1 − y0).
    /// </summary>
    public static float HeightFogOpticalDepth(Vector3 from, Vector3 to, float density, float heightBase, float falloff, float aerialBlend)
    {
        float length = Vector3.Distance(from, to);
        if (length <= 0f || density <= 0f) return 0f;
        float k = MathF.Max(falloff, 1e-4f);
        float a = Math.Clamp(aerialBlend, 0f, 1f);
        float dy = to.Y - from.Y;
        float meanHeightTerm = MathF.Abs(dy) < 1e-4f
            ? MathF.Exp(-k * MathF.Max(0.5f * (from.Y + to.Y) - heightBase, 0f))
            : (HeightAntiderivative(to.Y, heightBase, k) - HeightAntiderivative(from.Y, heightBase, k)) / dy;
        return length * density * ((1f - a) + a * meanHeightTerm);
    }

    private static float HeightAntiderivative(float y, float heightBase, float k) =>
        y <= heightBase ? y - heightBase : (1f - MathF.Exp(-k * (y - heightBase))) / k;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
