using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF2.4 quality ladder and temporal-reprojection helpers for raymarched clouds.
/// </summary>
/// <remarks>
/// Cost is controlled by internal resolution scale and history blend only.
/// <see cref="RaymarchedCloudsMath.UpdateEveryFrame"/> remains true — never skip the march
/// three frames in four (SkyForge / Aetherforge schedule is intentionally not ported).
/// FogPost bilinear-samples the cloud map for Performance/Balanced/High; Cinematic may run an
/// optional alpha-aware bilateral upsample before composite.
/// </remarks>
public enum CloudQualityTier
{
    Performance = 0,
    Balanced = 1,
    High = 2,
    Cinematic = 3,
}

/// <summary>AF2.4 CPU reference for cloud temporal resolve (headless gates + shared constants).</summary>
public static class CloudTemporalMath
{
    public const float ScalePerformance = 0.25f;
    public const float ScaleBalanced = 0.40f;
    public const float ScaleHigh = 0.50f;
    public const float ScaleCinematic = 0.625f;

    /// <summary>Default history blend when motion/depth/silhouette checks pass.</summary>
    public const float DefaultBaseBlend = 0.85f;

    /// <summary>Screen-space UV motion above this rejects history entirely.</summary>
    public const float DefaultMotionThreshold = 0.34f;

    /// <summary>Linearised depth delta above this rejects history.</summary>
    public const float DefaultDepthThreshold = 0.08f;

    /// <summary>Neighbourhood alpha range above this is treated as a silhouette edge.</summary>
    public const float DefaultSilhouetteAlphaRange = 0.05f;

    /// <summary>UV margin for history fetches (matches SkyForge 0.002..0.998).</summary>
    public const float UvMarginMin = 0.002f;
    public const float UvMarginMax = 0.998f;

    /// <summary>Camera translation (metres) that invalidates the whole history buffer.</summary>
    public const float CameraJumpMetres = 40f;

    public static float ScaleFor(CloudQualityTier tier) => tier switch
    {
        CloudQualityTier.Performance => ScalePerformance,
        CloudQualityTier.Balanced => ScaleBalanced,
        CloudQualityTier.High => ScaleHigh,
        CloudQualityTier.Cinematic => ScaleCinematic,
        _ => ScaleHigh,
    };

    /// <summary>Clamp an authored quality int into 0..3 (High when out of range).</summary>
    public static CloudQualityTier ClampTier(int quality)
    {
        if (quality < 0 || quality > 3)
            return CloudQualityTier.High;
        return (CloudQualityTier)quality;
    }

    /// <summary>Internal cloud RT size from full-frame dimensions and a quality scale.</summary>
    public static (int Width, int Height) ResolveInternalSize(int fullW, int fullH, float scale)
    {
        float s = Math.Clamp(scale, 0.05f, 1f);
        int w = Math.Max(1, (int)MathF.Round(Math.Max(1, fullW) * s));
        int h = Math.Max(1, (int)MathF.Round(Math.Max(1, fullH) * s));
        return (w, h);
    }

    /// <summary>
    /// Project a world-space anchor through the previous view-projection into UV
    /// (NDC → UV with the same Y convention as raymarched cloud reconstruct).
    /// </summary>
    public static Vector2 ReprojectUv(Vector3 worldPos, Matrix4x4 prevVP)
    {
        Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1f), prevVP);
        float w = MathF.Max(MathF.Abs(clip.W), 1e-5f);
        float ndcX = clip.X / w;
        float ndcY = clip.Y / w;
        return new Vector2(ndcX * 0.5f + 0.5f, 0.5f - ndcY * 0.5f);
    }

    /// <summary>True when UV is safely inside the history texture (margin 0.002..0.998).</summary>
    public static bool UvInBounds(Vector2 uv) =>
        uv.X >= UvMarginMin && uv.X <= UvMarginMax
        && uv.Y >= UvMarginMin && uv.Y <= UvMarginMax;

    /// <summary>Clamp history RGB into the current neighbourhood AABB.</summary>
    public static Vector3 NeighbourhoodClamp(Vector3 historyRgb, Vector3 minRgb, Vector3 maxRgb) =>
        Vector3.Clamp(historyRgb, minRgb, maxRgb);

    /// <summary>Clamp history alpha into the current neighbourhood range.</summary>
    public static float NeighbourhoodClampAlpha(float historyAlpha, float minAlpha, float maxAlpha) =>
        Math.Clamp(historyAlpha, minAlpha, maxAlpha);

    /// <summary>
    /// History blend weight: invalid UV / large motion / large depth delta → 0;
    /// high alpha-range silhouette → reduced; otherwise <paramref name="baseBlend"/>.
    /// </summary>
    public static float HistoryWeight(
        Vector2 historyUv,
        float motionLength,
        float depthDelta,
        float alphaRange,
        float baseBlend = DefaultBaseBlend,
        float motionThreshold = DefaultMotionThreshold,
        float depthThreshold = DefaultDepthThreshold,
        float silhouetteAlphaRange = DefaultSilhouetteAlphaRange)
    {
        if (!UvInBounds(historyUv))
            return 0f;
        if (motionLength > motionThreshold)
            return 0f;
        if (MathF.Abs(depthDelta) > depthThreshold)
            return 0f;

        float blend = Math.Clamp(baseBlend, 0f, 0.95f);
        if (alphaRange > silhouetteAlphaRange)
        {
            // Soft silhouette: keep a little history but favour the fresh march.
            float t = Math.Clamp(
                (alphaRange - silhouetteAlphaRange) / MathF.Max(silhouetteAlphaRange * 2f, 1e-4f),
                0f,
                1f);
            blend *= 1f - 0.75f * t;
        }

        return blend;
    }
}
