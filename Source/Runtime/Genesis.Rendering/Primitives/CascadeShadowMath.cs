using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// Cascaded-shadow splits, texel-scaled bias, and ForestLight-style light-space centre snap.
/// Cascade count 2 matches R7.11; count 3 adds a far envelope (AF1.1).
/// </summary>
public readonly struct CascadeShadowFrame
{
    public float NearExtent { get; init; }
    public float MidExtent { get; init; }
    public float FarExtent { get; init; }
    /// <summary>World-space distance used by terrain/fog to prefer the near cascade.</summary>
    public float NearSplitDistance { get; init; }
    /// <summary>World-space distance preferring mid over far when cascade count is 3.</summary>
    public float MidSplitDistance { get; init; }
    public float FarTexelWorld { get; init; }
    public float MidTexelWorld { get; init; }
    public float NearTexelWorld { get; init; }
    public float FarDepthBias { get; init; }
    public float MidDepthBias { get; init; }
    public float NearDepthBias { get; init; }
    public int CascadeCount { get; init; }
}

/// <summary>Dynamic frustum-aware cascade extents + adaptive receiver bias + texel snap.</summary>
public static class CascadeShadowMath
{
    /// <summary>Blend between uniform and logarithmic split (Practical Split Scheme).</summary>
    public const float PracticalLambda = 0.65f;

    public static CascadeShadowFrame Compute(
        float cameraNear,
        float cameraFar,
        float orthoSize,
        float fieldOfViewY,
        float aspectRatio,
        int farMapSize,
        int nearMapSize,
        float authoredBias,
        int cascadeCount = 2)
    {
        int count = cascadeCount >= 3 ? 3 : 2;
        float farExtent = MathF.Max(orthoSize, 16f);
        float nearPlane = MathF.Max(0.05f, cameraNear);
        float farPlane = MathF.Max(nearPlane + 1f, cameraFar);
        float shadowFar = MathF.Min(farPlane, farExtent * 1.35f);

        float logSplit = nearPlane * MathF.Pow(shadowFar / nearPlane, 0.5f);
        float uniSplit = nearPlane + (shadowFar - nearPlane) * 0.5f;
        float split = Lerp(uniSplit, logSplit, PracticalLambda);

        float fov = Math.Clamp(fieldOfViewY, 0.1f, 2.8f);
        float aspect = Math.Clamp(aspectRatio, 0.25f, 4f);
        float halfH = split * MathF.Tan(fov * 0.5f);
        float halfW = halfH * aspect;
        float frustumSpan = 2f * MathF.Max(halfW, halfH);

        float frustumNear = Math.Clamp(
            MathF.Max(frustumSpan * 0.5f, split * 0.4f),
            8f,
            farExtent * 0.5f);
        // Prefer legacy ortho ratios so existing scenes/goldens stay stable; frustum widens only gently.
        float legacyNear = MathF.Max(orthoSize * 0.25f, 8f);
        float nearExtent = Math.Clamp(Lerp(legacyNear, frustumNear, 0.2f), 8f, farExtent * 0.5f);

        float midExtent = farExtent;
        float midSplit = farExtent * 100f;
        if (count == 3)
        {
            // Today's outer cascade becomes mid; far is a larger ForestLight-style envelope.
            midExtent = farExtent;
            farExtent = MathF.Max(midExtent * 2.2f, MathF.Max(orthoSize * 1.85f, 48f));
            midSplit = MathF.Max(midExtent * 0.9f, nearExtent * 1.35f);
        }

        int farSize = Math.Max(64, farMapSize);
        int nearSize = Math.Max(64, nearMapSize);
        int midSize = farSize;
        float farTexel = (2f * farExtent) / farSize;
        float midTexel = (2f * midExtent) / midSize;
        float nearTexel = (2f * nearExtent) / nearSize;
        float baseBias = MathF.Max(1e-5f, authoredBias);

        return new CascadeShadowFrame
        {
            NearExtent = nearExtent,
            MidExtent = midExtent,
            FarExtent = farExtent,
            NearSplitDistance = MathF.Max(nearExtent, split * 0.85f),
            MidSplitDistance = midSplit,
            FarTexelWorld = farTexel,
            MidTexelWorld = midTexel,
            NearTexelWorld = nearTexel,
            FarDepthBias = AdaptiveDepthBias(baseBias, farTexel),
            MidDepthBias = AdaptiveDepthBias(baseBias, midTexel) * 0.75f,
            NearDepthBias = AdaptiveDepthBias(baseBias, nearTexel) * 0.55f,
            CascadeCount = count,
        };
    }

    /// <summary>
    /// Scales authored bias gently with cascade texel size. Stays at/near the authored floor for
    /// typical 1024² maps so goldens do not wash out; grows when texels get coarse.
    /// </summary>
    public static float AdaptiveDepthBias(float authoredBias, float texelWorld)
    {
        float bias = MathF.Max(1e-5f, authoredBias);
        float texel = MathF.Max(1e-5f, texelWorld);
        // ~0.05 world units/texel is common for ortho≈26 on 1024²; only lift above that.
        float coarseness = Math.Clamp((texel - 0.045f) / 0.12f, 0f, 1f);
        return bias * (1f + 0.35f * coarseness) + texel * 0.008f * coarseness;
    }

    /// <summary>
    /// ForestLight light-space snap: round cascade centre on lightRight/lightUp so static
    /// receivers do not crawl under camera motion.
    /// </summary>
    public static Vector3 SnapCascadeCentre(Vector3 centre, Vector3 lightDirection, float texelWorld)
    {
        Vector3 dir = Vector3.Normalize(lightDirection);
        if (dir.LengthSquared() < 1e-8f)
            return centre;

        Vector3 provisionalUp = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.94f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        Vector3 lightRight = Vector3.Normalize(Vector3.Cross(provisionalUp, dir));
        Vector3 lightUp = Vector3.Normalize(Vector3.Cross(dir, lightRight));
        float texel = MathF.Max(1e-5f, texelWorld);

        float lightX = Vector3.Dot(centre, lightRight);
        float lightY = Vector3.Dot(centre, lightUp);
        float snappedX = MathF.Round(lightX / texel) * texel;
        float snappedY = MathF.Round(lightY / texel) * texel;
        return centre + lightRight * (snappedX - lightX) + lightUp * (snappedY - lightY);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
