using System.Numerics;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Rooms;

public readonly record struct RoomSurfaceHit(Vector3 Position, Vector3 Normal, float Distance);

/// <summary>Exact heightfield triangle picking in the same reference frame as the placed terrain.</summary>
public static class RoomSurfacePlacement
{
    public static bool Raycast(TerrainAsset terrain, Matrix4x4 world, Vector3 origin, Vector3 direction,
        out RoomSurfaceHit hit, float maximumDistance = float.MaxValue)
    {
        bool found = TerrainSurfaceRaycast.Raycast(terrain, world, origin, direction, out TerrainSurfaceHit surface, maximumDistance);
        hit = new RoomSurfaceHit(surface.Position, surface.Normal, surface.Distance);
        return found;
    }
    /// <summary>Rotation with local up on the slope and the requested horizontal heading retained.</summary>
    public static Matrix4x4 SurfaceRotation(Vector3 normal, float yawDegrees)
    {
        normal = Vector3.Normalize(normal);
        float yaw = yawDegrees * MathF.PI / 180;
        Vector3 forward = new(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        // Preserve azimuth instead of projecting the heading sideways along the slope.
        if (MathF.Abs(normal.Y) > .0001f)
            forward.Y = -(normal.X * forward.X + normal.Z * forward.Z) / normal.Y;
        else
            forward -= normal * Vector3.Dot(forward, normal);
        if (forward.LengthSquared() < 1e-10f)
            forward = Vector3.Cross(MathF.Abs(normal.X) < .9f ? Vector3.UnitX : Vector3.UnitZ, normal);
        forward = Vector3.Normalize(forward);
        Vector3 right = Vector3.Normalize(Vector3.Cross(normal, forward));
        forward = Vector3.Normalize(Vector3.Cross(right, normal));
        return new Matrix4x4(right.X, right.Y, right.Z, 0,
            normal.X, normal.Y, normal.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1);
    }

    /// <summary>Y offset putting the transformed model's support point on the hit plane.</summary>
    public static float ContactOffset(Vector3 min, Vector3 max, Matrix4x4 scaleRotation, Vector3 normal)
    {
        if (MathF.Abs(normal.Y) < .0001f) return 0;
        float support = float.PositiveInfinity;
        for (int mask = 0; mask < 8; mask++)
        {
            Vector3 corner = new((mask & 1) == 0 ? min.X : max.X,
                (mask & 2) == 0 ? min.Y : max.Y, (mask & 4) == 0 ? min.Z : max.Z);
            support = MathF.Min(support, Vector3.Dot(Vector3.TransformNormal(corner, scaleRotation), normal));
        }
        return -support / normal.Y;
    }

    public static Matrix4x4 Transform(Genesis.Runtime.Scene.RoomTransform transform) =>
        Matrix4x4.CreateScale(transform.ScaleX, transform.ScaleY, transform.ScaleZ)
        * Matrix4x4.CreateFromYawPitchRoll(transform.RotationY * MathF.PI / 180,
            transform.RotationX * MathF.PI / 180, transform.RotationZ * MathF.PI / 180)
        * Matrix4x4.CreateTranslation(transform.X, transform.Y, transform.Z);

}
