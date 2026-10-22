using System;
using System.Numerics;

namespace Genesis.Physics;

/// <summary>Backend-facing world-space fluid volume shared by authored water and ECS physics.</summary>
public sealed class PhysicsWaterVolume
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Water";
    public Vector3 Minimum { get; init; }
    public Vector3 Maximum { get; init; }
    public float SurfaceY { get; init; }
    public float Density { get; init; } = 1000f;
    public float Buoyancy { get; init; } = 1f;
    public float LinearDrag { get; init; } = 2.5f;
    public float AngularDrag { get; init; } = 1f;
    public Vector3 FlowVelocity { get; init; }
    public bool Swimmable { get; init; } = true;
    public bool Damaging { get; init; }
    /// <summary>
    /// The water's own outline and surface, when it is not simply its box: the surface height at a
    /// world x, z, or NaN where the point is outside the water (a river's banks, a round lake's
    /// corners). A river falls along its course, so its surface differs from place to place.
    /// </summary>
    public Func<float, float, float>? SurfaceAt { get; init; }

    /// <summary>True when x, z is over this water (inside its box and its own outline).</summary>
    public bool CoversHorizontal(float x, float z) =>
        x >= Minimum.X && x <= Maximum.X && z >= Minimum.Z && z <= Maximum.Z
        && (SurfaceAt == null || float.IsFinite(SurfaceAt(x, z)));

    /// <summary>The surface height over x, z: the water's own where it has one, else <see cref="SurfaceY"/>.</summary>
    public float SurfaceHeightAt(float x, float z) =>
        SurfaceAt != null && SurfaceAt(x, z) is var height && float.IsFinite(height) ? height : SurfaceY;

    public bool Contains(Vector3 point) =>
        CoversHorizontal(point.X, point.Z) && point.Y >= Minimum.Y && point.Y <= SurfaceHeightAt(point.X, point.Z);

    /// <summary>
    /// The water over x, z (and, given a height, the water that point is in): of several, the one
    /// covering the least ground, so a pond within the sea's reach is the pond.
    /// </summary>
    public static PhysicsWaterVolume? Find(System.Collections.Generic.IReadOnlyList<PhysicsWaterVolume> volumes, float x, float z, float? y = null)
    {
        PhysicsWaterVolume? best = null;
        float bestArea = float.MaxValue;
        for (int i = volumes.Count - 1; i >= 0; i--)
        {
            PhysicsWaterVolume volume = volumes[i];
            if (!(y is float height ? volume.Contains(new Vector3(x, height, z)) : volume.CoversHorizontal(x, z))) continue;
            float area = (volume.Maximum.X - volume.Minimum.X) * (volume.Maximum.Z - volume.Minimum.Z);
            if (area < bestArea) { best = volume; bestArea = area; }
        }
        return best;
    }

    public float SubmergedFraction(Vector3 centre, Vector3 halfExtents)
    {
        if (centre.X + halfExtents.X < Minimum.X || centre.X - halfExtents.X > Maximum.X ||
            centre.Z + halfExtents.Z < Minimum.Z || centre.Z - halfExtents.Z > Maximum.Z)
            return 0f;
        float bottom = MathF.Max(centre.Y - halfExtents.Y, Minimum.Y);
        float top = MathF.Min(centre.Y + halfExtents.Y, SurfaceY);
        return Math.Clamp((top - bottom) / MathF.Max(halfExtents.Y * 2f, 1e-5f), 0f, 1f);
    }
}
