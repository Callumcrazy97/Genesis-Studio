using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// The sun's cascades for one view: each fitted to a sphere round its slice of the view (so its
/// size never changes as the camera turns), how far the shadows reach and where they fade out.
/// Extents are the cascades' widths; texels are measured as two shadow-map texels, as the bias is.
/// </summary>
public readonly struct CascadeShadowFrame
{
    public float NearExtent { get; init; }
    public float MidExtent { get; init; }
    public float FarExtent { get; init; }
    /// <summary>World-space distance the near cascade covers whatever way the camera looks.</summary>
    public float NearSplitDistance { get; init; }
    /// <summary>World-space distance the mid cascade covers when there are three.</summary>
    public float MidSplitDistance { get; init; }
    public float FarTexelWorld { get; init; }
    public float MidTexelWorld { get; init; }
    public float NearTexelWorld { get; init; }
    public float FarDepthBias { get; init; }
    public float MidDepthBias { get; init; }
    public float NearDepthBias { get; init; }
    public int CascadeCount { get; init; }
    /// <summary>How far from the camera the sun's shadows reach.</summary>
    public float ShadowDistance { get; init; }
    /// <summary>Where the shadows start fading out; they are gone at <see cref="ShadowDistance"/>.</summary>
    public float FadeStart { get; init; }
    /// <summary>How far ahead of the camera, along its view, each cascade's sphere is centred.</summary>
    public float NearCentreDistance { get; init; }
    public float MidCentreDistance { get; init; }
    public float FarCentreDistance { get; init; }
}

/// <summary>
/// Stable cascaded shadows: each cascade is the bounding sphere of its slice of the view, whose
/// radius depends only on the slice and the lens, so turning the camera never resizes it; its centre
/// is snapped to whole shadow-map texels in the light's space, so a shadow never moves when only the
/// camera turns or crawls when it moves.
/// </summary>
public static class CascadeShadowMath
{
    /// <summary>Automatic shadows reach as far as the view, but no further than this (metres).</summary>
    public const float AutomaticDistanceCap = 300f;

    /// <summary>The share of the shadow distance over which shadows fade out unless a room sets another.</summary>
    public const float DefaultFade = 0.15f;

    /// <summary>
    /// The distance the sun's shadows reach: the one asked for, else the view's (camera far plane)
    /// up to <see cref="AutomaticDistanceCap"/>.
    /// </summary>
    public static float ResolveDistance(float requested, float cameraFar) =>
        requested > 0f && float.IsFinite(requested)
            ? Math.Clamp(requested, 2f, 20000f)
            : Math.Clamp(float.IsFinite(cameraFar) && cameraFar > 0f ? cameraFar : 200f, 2f, AutomaticDistanceCap);

    public static CascadeShadowFrame Compute(
        float cameraNear,
        float cameraFar,
        float fieldOfViewY,
        float aspectRatio,
        int mapSize,
        float authoredBias,
        int cascadeCount,
        float shadowDistance,
        float fade = DefaultFade)
    {
        int count = cascadeCount >= 3 ? 3 : 2;
        float nearPlane = MathF.Max(0.01f, cameraNear);
        float distance = MathF.Max(nearPlane + 1f, ResolveDistance(shadowDistance, cameraFar));
        float fov = Math.Clamp(fieldOfViewY, 0.1f, 2.8f);
        float aspect = Math.Clamp(aspectRatio, 0.25f, 4f);
        // Splits: the near cascade covers the first tenth (3 to 20 m), the mid one about a third.
        float nearSplit, midSplit;
        if (count == 3)
        {
            nearSplit = Math.Clamp(distance * 0.1f, MathF.Min(3f, distance * 0.5f), 20f);
            midSplit = Math.Clamp(distance * 0.35f, MathF.Min(nearSplit * 2f, distance), MathF.Max(nearSplit * 2f, distance * 0.6f));
        }
        else
        {
            nearSplit = Math.Clamp(distance * 0.15f, MathF.Min(3f, distance * 0.5f), 30f);
            midSplit = distance;
        }

        SliceSphere(nearPlane, nearSplit, fov, aspect, out float nearCentre, out float nearRadius);
        SliceSphere(nearSplit, midSplit, fov, aspect, out float midCentre, out float midRadius);
        SliceSphere(count == 3 ? midSplit : nearSplit, distance, fov, aspect, out float farCentre, out float farRadius);

        int size = Math.Max(64, mapSize);
        float nearExtent = nearRadius * 2f, midExtent = midRadius * 2f, farExtent = farRadius * 2f;
        float nearTexel = 2f * nearExtent / size, midTexel = 2f * midExtent / size, farTexel = 2f * farExtent / size;
        // The bias is a share of each cascade's depth range, which (as its texels) grows with its
        // width, so one share is the same number of texels in every cascade: the authored bias
        // plus half a texel. A bias that also grew with the texel's size in metres, as it did, put
        // the far cascade's shadows tens of metres off their casters once it reached the view's end.
        float baseBias = MathF.Max(1e-5f, authoredBias) + 0.5f / (4f * size);
        float fadeShare = float.IsFinite(fade) ? Math.Clamp(fade, 0f, 1f) : DefaultFade;

        return new CascadeShadowFrame
        {
            NearExtent = nearExtent,
            MidExtent = midExtent,
            FarExtent = farExtent,
            NearSplitDistance = nearSplit,
            MidSplitDistance = midSplit,
            FarTexelWorld = farTexel,
            MidTexelWorld = midTexel,
            NearTexelWorld = nearTexel,
            FarDepthBias = baseBias,
            MidDepthBias = baseBias * 0.75f,
            NearDepthBias = baseBias * 0.55f,
            CascadeCount = count,
            ShadowDistance = distance,
            FadeStart = distance * (1f - fadeShare),
            NearCentreDistance = nearCentre,
            MidCentreDistance = midCentre,
            FarCentreDistance = farCentre,
        };
    }

    /// <summary>
    /// The smallest sphere round the slice of a view between distances <paramref name="from"/> and
    /// <paramref name="to"/>, centred on the view's axis: its centre's distance along the view and its
    /// radius. Both depend only on the slice and the lens, never on where the camera looks.
    /// </summary>
    public static void SliceSphere(float from, float to, float fieldOfViewY, float aspectRatio, out float centre, out float radius)
    {
        float a = MathF.Max(0f, MathF.Min(from, to)), b = MathF.Max(from, to);
        float tanY = MathF.Tan(Math.Clamp(fieldOfViewY, 0.1f, 2.8f) * 0.5f);
        float tanX = tanY * Math.Clamp(aspectRatio, 0.25f, 4f);
        float k2 = tanX * tanX + tanY * tanY;
        // Equal distance to the near and far corners; a wide lens puts it at (or past) the far face.
        centre = MathF.Min(b, (a + b) * (1f + k2) * 0.5f);
        float toNear = (centre - a) * (centre - a) + a * a * k2;
        float toFar = (centre - b) * (centre - b) + b * b * k2;
        radius = MathF.Sqrt(MathF.Max(toNear, toFar));
    }

    /// <summary>
    /// How much the sun's shadow still shows at <paramref name="distance"/> from the camera: 1 before
    /// the fade starts, falling smoothly to 0 at the shadow distance.
    /// </summary>
    public static float FadeAt(in CascadeShadowFrame frame, float distance)
    {
        if (distance <= frame.FadeStart) return 1f;
        float span = frame.ShadowDistance - frame.FadeStart;
        if (span <= 1e-4f) return distance < frame.ShadowDistance ? 1f : 0f;
        float t = Math.Clamp((distance - frame.FadeStart) / span, 0f, 1f);
        return 1f - t * t * (3f - 2f * t);
    }

    /// <summary>
    /// True when a caster's bounding sphere can throw a shadow into the orthographic cascade
    /// described by <paramref name="lightViewProjection"/>. Only the light-space footprint and the
    /// far plane are tested: casters between the light and the near plane are pancaked onto it by
    /// the shadow vertex shader, so they still cast.
    /// </summary>
    public static bool CasterReachesCascade(in Matrix4x4 lightViewProjection, Vector3 center, float radius)
    {
        Matrix4x4 m = lightViewProjection;
        Vector4 clip = Vector4.Transform(new Vector4(center, 1f), m);
        float r = MathF.Max(radius, 0f);
        float scaleX = new Vector3(m.M11, m.M21, m.M31).Length();
        float scaleY = new Vector3(m.M12, m.M22, m.M32).Length();
        float scaleZ = new Vector3(m.M13, m.M23, m.M33).Length();
        return MathF.Abs(clip.X) <= 1f + r * scaleX
            && MathF.Abs(clip.Y) <= 1f + r * scaleY
            && clip.Z - r * scaleZ <= 1f;
    }

    /// <summary>
    /// The up direction the sun's light view is built with: world up, or world forward when the
    /// sun is nearly overhead. The snap below uses the same, so it rounds along the map's own axes.
    /// </summary>
    public static Vector3 LightViewUp(Vector3 lightDirection) =>
        MathF.Abs(Vector3.Dot(Vector3.Normalize(lightDirection), Vector3.UnitY)) > 0.95f ? Vector3.UnitZ : Vector3.UnitY;

    /// <summary>
    /// Rounds a cascade's centre to whole texels along the light view's right and up axes (and its
    /// depth axis), so static receivers do not crawl under camera motion and an unchanged cascade
    /// keeps the very same matrix.
    /// </summary>
    public static Vector3 SnapCascadeCentre(Vector3 centre, Vector3 lightDirection, float texelWorld)
    {
        if (lightDirection.LengthSquared() < 1e-8f)
            return centre;
        Vector3 dir = Vector3.Normalize(lightDirection);
        Vector3 lightRight = Vector3.Normalize(Vector3.Cross(LightViewUp(dir), dir));
        Vector3 lightUp = Vector3.Normalize(Vector3.Cross(dir, lightRight));
        float texel = MathF.Max(1e-5f, texelWorld);

        float lightX = Vector3.Dot(centre, lightRight);
        float lightY = Vector3.Dot(centre, lightUp);
        float lightZ = Vector3.Dot(centre, dir);
        float snappedX = MathF.Round(lightX / texel) * texel;
        float snappedY = MathF.Round(lightY / texel) * texel;
        float snappedZ = MathF.Round(lightZ / texel) * texel;
        return centre + lightRight * (snappedX - lightX) + lightUp * (snappedY - lightY) + dir * (snappedZ - lightZ);
    }
}
