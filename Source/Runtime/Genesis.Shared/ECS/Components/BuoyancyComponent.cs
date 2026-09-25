using System.Numerics;

namespace Genesis.Shared.ECS.Components
{
    /// <summary>
    /// Issue 7 (declarative physics): attached instead of a solid <see cref="RigidBodyComponent"/>
    /// when an object is authored with <c>PhysicsType = "WaterBody"</c> — per the terrain/rendering
    /// fix plan, a water body gets "no solid collider", it just floats whatever dynamic bodies
    /// enter its box. The box is the entity's <see cref="Transform3DComponent.Position"/> ±
    /// <see cref="HalfExtents"/>.
    ///
    /// Consumed by <see cref="Genesis.Physics.Systems.BuoyancySystem"/>, which reuses the same
    /// formula as <see cref="Genesis.Physics.Buoyancy.BuoyancyVolume"/>/<c>WaterPhysicsIntegration</c>
    /// (the pre-existing sandbox-only buoyancy system) but applies it to entities registered with
    /// the ECS-backed <see cref="Genesis.Physics.PhysicsWorld"/> instead of a standalone
    /// <c>SandboxPhysicsWorld</c>, since that's what actual game objects use.
    /// </summary>
    public struct BuoyancyComponent : IComponent
    {
        /// <summary>Half-extents of the water box, in the same local units as <see cref="RigidBodyComponent.Size"/>.</summary>
        public Vector3 HalfExtents;

        /// <summary>Fluid density in kg/m³ (fresh water ≈ 1000, sea water ≈ 1025). Kept for authoring/serialization parity with <see cref="Genesis.Physics.Buoyancy.BuoyancyVolume"/>; not currently a multiplier in the buoyancy formula (matches that type's existing behavior).</summary>
        public float FluidDensity;

        /// <summary>Fraction of gravity cancelled per unit of submersion depth.</summary>
        public float BuoyancyStrength;

        /// <summary>Linear drag coefficient applied to submerged bodies.</summary>
        public float LinearDrag;

        /// <summary>Angular drag applied to submerged bodies.</summary>
        public float AngularDrag;

        public static BuoyancyComponent Default(Vector3 halfExtents) => new BuoyancyComponent
        {
            HalfExtents      = halfExtents,
            FluidDensity     = 1000f,
            BuoyancyStrength = 1f,
            LinearDrag       = 2.5f,
            AngularDrag      = 1f,
        };
    }
}
