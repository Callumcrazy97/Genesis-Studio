namespace Genesis.Physics;

/// <summary>Collision primitives for BepuPhysics registration (aligned with <see cref="Genesis.Shared.ECS.Components.CollisionShape"/>).</summary>
public enum CollisionShape : byte
{
    Box = 0,
    Sphere = 1,
    Capsule = 2,
    Cylinder = 3,
    Mesh = 4,
    ConvexHull = 5,
}

public enum PhysicsMotionType : byte
{
    Static = 0,
    Dynamic = 1,
    Kinematic = 2,
}
