namespace Genesis.Shared.ECS.Components
{
    public enum CollisionShape : byte
    {
        Box,
        Sphere,
        Capsule,
        /// <summary>Approximated with a capsule in the physics backend.</summary>
        Cylinder,
        /// <summary>
        /// Issue 7 (declarative physics): arbitrary triangle mesh collider, for terrain/props
        /// authored with <c>RigidBody = "Mesh"</c>. <see cref="Genesis.Physics.PhysicsWorld"/>
        /// does not yet build a true mesh shape for this — it falls back to a bounding box
        /// until a mesh-collider builder lands — but the tag round-trips correctly so content
        /// can be authored ahead of that work.
        /// </summary>
        Mesh,
        /// <summary>
        /// Issue 7 (declarative physics): convex-hull collider, for <c>RigidBody = "ConvexHull"</c>.
        /// Same fallback caveat as <see cref="Mesh"/> applies until a hull builder lands.
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
