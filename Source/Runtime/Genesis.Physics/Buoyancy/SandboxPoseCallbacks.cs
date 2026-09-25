using System;
using System.Numerics;

namespace Genesis.Physics.Buoyancy
{
    /// <summary>
    /// Thin bridge that plugs <see cref="WaterPhysicsIntegration"/> into the
    /// existing <see cref="SandboxPhysicsWorld"/> step loop.
    ///
    /// <para>
    /// Because Bepu's <c>IPoseIntegratorCallbacks.IntegrateVelocity</c> works on
    /// SIMD-wide body bundles it is difficult to do per-body dictionary lookups there.
    /// Instead, this class is called <em>before</em> each <c>SandboxPhysicsWorld.Step</c>
    /// to apply buoyancy and flow impulses via the public <c>BodyReference</c> API.
    /// The result is indistinguishable from integrating the forces inside the callback
    /// at the single-dt level of accuracy we need.
    /// </para>
    ///
    /// Usage:
    /// <code>
    ///   var world      = new SandboxPhysicsWorld();
    ///   var water      = new WaterPhysicsIntegration(world);
    ///   var callbacks  = new SandboxWaterCallbacks(world, water);
    ///
    ///   // Lake at y = 0, bounded 50 m each side
    ///   water.AddLake(0f, new Bounds2D(-50, -50, 50, 50));
    ///
    ///   // Game loop:
    ///   callbacks.PreStep(dt);   // apply buoyancy
    ///   world.Step(dt);          // Bepu integrates
    /// </code>
    /// </summary>
    public sealed class SandboxWaterCallbacks
    {
        private readonly SandboxPhysicsWorld _world;
        private readonly WaterPhysicsIntegration _water;

        public SandboxWaterCallbacks(SandboxPhysicsWorld world, WaterPhysicsIntegration water)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _water = water ?? throw new ArgumentNullException(nameof(water));
        }

        /// <summary>
        /// Call this once per frame <em>before</em> <c>SandboxPhysicsWorld.Step(dt)</c>.
        /// It applies buoyancy, drag, and flow impulses to all dynamic bodies.
        /// </summary>
        public void PreStep(float dt) => _water.Step(dt);

        /// <summary>Convenience: PreStep + World.Step in one call.</summary>
        public void FullStep(float dt)
        {
            PreStep(dt);
            _world.Step(dt);
        }

        // ── Character water detection helpers ─────────────────────────────────────

        /// <summary>
        /// Returns true if the body's centre is inside any registered water volume.
        /// Call after <see cref="PreStep"/> or <see cref="FullStep"/> so poses are current.
        /// </summary>
        public bool IsBodyInWater(PhysicsBody body) => _water.IsBodyInWater(body);

        /// <summary>
        /// Returns the <see cref="BuoyancyVolume"/> the body is currently inside, or null.
        /// Lets character controllers read volume-specific properties (drag, flow speed, etc.).
        /// </summary>
        public BuoyancyVolume? GetBodyVolume(PhysicsBody body)
        {
            if (!body.IsValid) return null;
            _world.GetPose(body, out Vector3 pos, out _);
            return _water.TryGetVolumeAt(pos);
        }

        /// <summary>
        /// Returns the surface Y of the water at <paramref name="worldXZ"/>, or null if
        /// the position is not above (or inside) any registered water volume.
        /// Useful for partial submersion tests (head above water, feet below).
        /// </summary>
        public float? GetWaterSurfaceY(float worldX, float worldZ)
        {
            if (_water.Volumes.Count == 0)
                return null;

            BuoyancyVolume[] snapshot = new BuoyancyVolume[_water.Volumes.Count];
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i] = _water.Volumes[i];

            for (int i = 0; i < snapshot.Length; i++)
            {
                BuoyancyVolume v = snapshot[i];
                if (v.HorizontalBounds.Contains(worldX, worldZ))
                    return v.SurfaceY;
            }

            return null;
        }
    }
}
