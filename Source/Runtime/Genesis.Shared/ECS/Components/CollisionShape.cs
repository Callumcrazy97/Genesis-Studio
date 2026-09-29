namespace Genesis.Shared.ECS.Components
{
    public enum CollisionShape : byte
    {
        Box,
        Sphere,
        Capsule,
        /// <summary>A native cylinder shape in the physics backend.</summary>
        Cylinder,
        /// <summary>
        /// Triangle mesh collider, using the saved geometry in <see cref="MeshColliderComponent"/>.
        /// </summary>
        Mesh,
        /// <summary>
        /// Native convex hull computed from <see cref="MeshColliderComponent"/> vertices.
        /// </summary>
        ConvexHull,
    }

    public enum PhysicsMotionType : byte
    {
        Static,
        Dynamic,
        /// <summary>
        /// Issue 7: moves under script/animation control (e.g. moving platforms) rather than
        /// gravity/forces, but still pushes dynamic bodies it touches. Bepu registration treats
        /// this like <see cref="Dynamic"/> with gravity/forces suppressed — see
        /// <see cref="Genesis.Physics.PhysicsDeclarativeBinding"/>.
        /// </summary>
        Kinematic,
    }
}
