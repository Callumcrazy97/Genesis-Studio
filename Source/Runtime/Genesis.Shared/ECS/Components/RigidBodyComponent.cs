using System;
using System.Numerics;
using Genesis.Shared.ECS;

namespace Genesis.Shared.ECS.Components
{
    [Flags]
    public enum RigidBodyFlags
    {
        None         = 0,
        UseGravity   = 1,
        LockRotation = 2,
        Collision    = 4,
        /// <summary>
        /// Issue 7 (declarative physics): non-solid overlap volume — generates contact/overlap
        /// events but applies no collision response. Used for <c>PhysicsType = "Trigger"</c> /
        /// <c>"Sensor"</c> authoring.
        /// </summary>
        Sensor       = 8,
    }

    public struct RigidBodyComponent : IComponent
    {
        public CollisionShape    Shape;
        public PhysicsMotionType Motion;
        public Vector3           Size;
        public float             Mass;
        public float             Weight;
        public float             Friction;
        public float             SpeculativeMargin;
        public RigidBodyFlags    Flags;
        public int               RegistrationId;
        /// <summary>Zero-based authored collision layer. Genesis currently exposes layers A-G.</summary>
        public byte              CollisionLayer;
        /// <summary>Bit mask of layers this body accepts. Zero is treated as all layers for legacy data.</summary>
        public uint              CollisionMask;

        /// <summary>
        /// Issue 7 (declarative physics): bounciness passed through to the physics backend's
        /// material/contact settings (0 = no bounce, 1 = perfectly elastic). Defaults to 0 for
        /// all existing factory methods, matching prior (implicit) behavior.
        /// </summary>
        public float Restitution;

        /// <summary>
        /// Issue 7 (declarative physics): multiplier applied to world gravity for this body
        /// (0 = unaffected by gravity, 1 = normal gravity, negative = inverted). Every factory
        /// method below sets this explicitly to 1f so the struct-default of 0f never silently
        /// disables gravity for bodies created before this field existed.
        /// </summary>
        public float GravityScale;

        public bool Collision => (Flags & RigidBodyFlags.Collision) != 0;
        public bool UseGravity => (Flags & RigidBodyFlags.UseGravity) != 0;
        public bool LockRotation => (Flags & RigidBodyFlags.LockRotation) != 0;
        public bool IsSensor => (Flags & RigidBodyFlags.Sensor) != 0;

        public Vector3 HalfExtents => Shape switch
        {
            CollisionShape.Box => Size,
            CollisionShape.Sphere => new Vector3(Size.X),
            CollisionShape.Capsule or CollisionShape.Cylinder => new Vector3(Size.X, Size.Y, Size.X),
            _ => Size,
        };

        public static RigidBodyComponent StaticBox(Vector3 halfExtents) => new RigidBodyComponent
        {
            Shape             = CollisionShape.Box,
            Motion            = PhysicsMotionType.Static,
            Size              = halfExtents,
            Friction          = 0.8f,
            SpeculativeMargin = 0.06f,
            Flags             = RigidBodyFlags.Collision,
            RegistrationId    = 0,
            GravityScale      = 1f,
            CollisionMask     = 0x7Fu,
        };

        public static RigidBodyComponent InfiniteFloor(float halfExtent = 5000f, float halfHeight = 0.5f) => new RigidBodyComponent
        {
            Shape             = CollisionShape.Box,
            Motion            = PhysicsMotionType.Static,
            Size              = new Vector3(halfExtent, halfHeight, halfExtent),
            Friction          = 0.8f,
            SpeculativeMargin = 0.06f,
            Flags             = RigidBodyFlags.Collision,
            RegistrationId    = 0,
            GravityScale      = 1f,
            CollisionMask     = 0x7Fu,
        };

        public static RigidBodyComponent DynamicBox(Vector3 halfExtents, float mass = 1f, float weight = 0f) => new RigidBodyComponent
        {
            Shape             = CollisionShape.Box,
            Motion            = PhysicsMotionType.Dynamic,
            Size              = halfExtents,
            Mass              = mass,
            Weight            = weight,
            Friction          = 0.8f,
            SpeculativeMargin = 0.06f,
            Flags             = RigidBodyFlags.Collision | RigidBodyFlags.UseGravity,
            RegistrationId    = 0,
            GravityScale      = 1f,
            CollisionMask     = 0x7Fu,
        };

        public static RigidBodyComponent Character(float radius = 0.35f, float halfHeight = 0.9f, float mass = 75f, float weight = 75f) => new RigidBodyComponent
        {
            Shape             = CollisionShape.Capsule,
            Motion            = PhysicsMotionType.Dynamic,
            Size              = new Vector3(radius, halfHeight, 0f),
            Mass              = mass,
            Weight            = weight,
            Friction          = 0.9f,
            SpeculativeMargin = 0.15f,
            Flags             = RigidBodyFlags.Collision | RigidBodyFlags.UseGravity | RigidBodyFlags.LockRotation,
            RegistrationId    = 0,
            GravityScale      = 1f,
            CollisionMask     = 0x7Fu,
        };
    }
}
