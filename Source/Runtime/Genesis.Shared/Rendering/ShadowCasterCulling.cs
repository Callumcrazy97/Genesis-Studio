using System;
using System.Numerics;

namespace Genesis.Shared.Rendering;

/// <summary>
/// Which things outside the camera's view still cast a shadow into it. Culling by the view alone
/// threw away a roof above the camera or a wall behind it, so the sun shone through them and the
/// shadows in view changed as the camera turned (GenesisCraft's house, 28 Oct). Every system that
/// culls before it submits (Objects, static model and mesh layers, voxel chunks, the renderer's own
/// chunk test) keeps a draw this says can shadow the view; the renderer then draws it only into the
/// shadow maps. The renderer publishes the light it shadows with at the end of each frame.
/// </summary>
public static class ShadowCasterCulling
{
    /// <summary>Lamps whose shadows are drawn (the renderer's local shadow slots).</summary>
    public const int MaxLamps = 4;

    private sealed class Snapshot
    {
        public Vector3 SunDirection;
        public float SunReach;
        public Vector3 Camera;
        public readonly Vector4[] Lamps = new Vector4[MaxLamps];
        public int LampCount;
    }

    private static Snapshot _current;

    /// <summary>
    /// The light the renderer shadows with: the sun's direction (from the sun into the scene) and how
    /// far from the camera its shadows reach (0 when there are none), the camera, and the lamps
    /// holding a shadow (xyz where, w their reach).
    /// </summary>
    public static void Publish(Vector3 sunDirection, float sunReach, Vector3 camera, ReadOnlySpan<Vector4> lamps)
    {
        var next = new Snapshot { Camera = camera };
        if (sunReach > 0f && float.IsFinite(sunReach) && sunDirection.LengthSquared() > 1e-8f)
        {
            next.SunDirection = Vector3.Normalize(sunDirection);
            next.SunReach = sunReach;
        }
        foreach (Vector4 lamp in lamps)
        {
            if (next.LampCount == MaxLamps) break;
            if (lamp.W > 0f) next.Lamps[next.LampCount++] = lamp;
        }
        _current = next;
    }

    /// <summary>Forgets the published light (a room's end, tests): nothing outside the view is kept.</summary>
    public static void Clear() => _current = null;

    /// <summary>
    /// True when a sphere outside the view can still throw a shadow into it: the sun's shadow of it,
    /// swept away from the sun as far as the sun's shadows reach, touches the view; or it lies within
    /// the reach of a lamp that casts shadows.
    /// </summary>
    public static bool CastsIntoView(in CameraFrustum frustum, Vector3 centre, float radius)
    {
        Snapshot light = _current;
        if (light is null) return false;
        radius = MathF.Max(radius, 0f);
        for (int i = 0; i < light.LampCount; i++)
        {
            Vector4 lamp = light.Lamps[i];
            float reach = lamp.W + radius;
            if (Vector3.DistanceSquared(centre, new Vector3(lamp.X, lamp.Y, lamp.Z)) <= reach * reach) return true;
        }
        if (light.SunReach <= 0f) return false;
        // Shadows fall on what lies within the reach of the camera, so their casters lie within
        // twice the reach: what is further cannot shade anything that receives a shadow.
        float limit = light.SunReach * 2f + radius;
        if (Vector3.DistanceSquared(centre, light.Camera) > limit * limit) return false;
        return frustum.ContainsSweptSphere(centre, radius, light.SunDirection, SweepLength(radius, light.SunReach));
    }

    /// <summary>
    /// How far a caster's shadow is followed from it: about twelve times its size (a low evening
    /// sun's shadow) and ten metres more, never past the shadows' reach. It bounds how much outside
    /// the view is kept: a roof, a wall or a hill behind the camera, not everything upwind of it.
    /// </summary>
    public static float SweepLength(float radius, float reach) => MathF.Min(reach, MathF.Max(radius, 0f) * 12f + 10f);
}
