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

    public bool Contains(Vector3 point) =>
        point.X >= Minimum.X && point.X <= Maximum.X &&
        point.Z >= Minimum.Z && point.Z <= Maximum.Z &&
        point.Y >= Minimum.Y && point.Y <= SurfaceY;

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
