using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.7 CPU reference for HDR bloom threshold extract plus exposure / contrast / saturation /
/// vignette grading that lands in FogPost before ACES. Pure math for headless gates; no GPU devices.
/// </summary>
/// <remarks>
/// Bloom is a separate half-res pyramid on the GPU; this type only owns the extract and the
/// grading operators so the headless gate can prove identity defaults and that a dim pixel
/// contributes nothing while a bright one does. Soft-knee is intentionally omitted in v1 —
/// a hard luma threshold is enough for the gate and matches the HLSL extract.
///
/// ACES remains the sole tonemap. Nothing here replaces or stacks a second filmic curve.
/// </remarks>
public static class BloomGradingMath
{
    /// <summary>Default exposure scalar — identity (no brightening / darkening).</summary>
    public const float DefaultExposure = 1f;

    /// <summary>Default contrast — identity around <see cref="ContrastMidGrey"/>.</summary>
    public const float DefaultContrast = 1f;

    /// <summary>Default saturation — identity (<c>lerp(luma, color, 1)</c>).</summary>
    public const float DefaultSaturation = 1f;

    /// <summary>Default vignette strength — off (factor is 1 at every UV).</summary>
    public const float DefaultVignette = 0f;

    /// <summary>Default bloom luma threshold; only matters when bloom is enabled.</summary>
    public const float DefaultBloomThreshold = 1f;

    /// <summary>Default bloom add intensity; only matters when bloom is enabled.</summary>
    public const float DefaultBloomIntensity = 0.04f;

    /// <summary>Linear mid-grey used for the contrast pivot (ACES-ish).</summary>
    public const float ContrastMidGrey = 0.18f;

    /// <summary>Half-res bloom pyramid mip count (downsample + upsample).</summary>
    public const int BloomMipCount = 4;

    /// <summary>Rec. 709 luma used by the threshold extract.</summary>
    public static float Luma(Vector3 color) =>
        color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;

    /// <summary>
    /// Hard-threshold bloom extract: pixels at or below <paramref name="threshold"/> contribute
    /// zero; brighter pixels keep a fraction of their colour scaled by how far they sit above it.
    /// </summary>
    public static Vector3 ThresholdExtract(Vector3 color, float threshold = DefaultBloomThreshold)
    {
        float luma = Luma(color);
        if (luma <= threshold)
            return Vector3.Zero;

        float contribution = (luma - threshold) / MathF.Max(luma, 1e-4f);
        return color * contribution;
    }

    /// <summary>Applies exposure: <c>color * exposure</c>. Identity at <see cref="DefaultExposure"/>.</summary>
    public static Vector3 ApplyExposure(Vector3 color, float exposure = DefaultExposure) =>
        color * exposure;

    /// <summary>
    /// Contrast around <see cref="ContrastMidGrey"/> then saturation via luma lerp.
    /// Identity when both <paramref name="contrast"/> and <paramref name="saturation"/> are 1.
    /// </summary>
    public static Vector3 ApplyContrastSaturation(
        Vector3 color,
        float contrast = DefaultContrast,
        float saturation = DefaultSaturation)
    {
        Vector3 contrasted = (color - new Vector3(ContrastMidGrey)) * contrast
            + new Vector3(ContrastMidGrey);
        float luma = Luma(contrasted);
        return Vector3.Lerp(new Vector3(luma), contrasted, saturation);
    }

    /// <summary>
    /// Soft radial vignette factor in [0,1]. Strength 0 returns 1 at every UV (including corners);
    /// strength &gt; 0 darkens edges more than the centre.
    /// </summary>
    public static float ApplyVignette(
        float u,
        float v,
        float strength = DefaultVignette)
    {
        if (strength <= 0.001f)
            return 1f;

        float dx = u - 0.5f;
        float dy = v - 0.5f;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        // Normalise so the corner (~0.707) maps near 1, then soft-curve it.
        float t = Math.Clamp(dist * 1.41421356f, 0f, 1f);
        float attenuation = 1f - t * t;
        return Math.Clamp(MathF.Pow(MathF.Max(attenuation, 0f), Math.Clamp(strength, 0f, 1f)), 0f, 1f);
    }
}
