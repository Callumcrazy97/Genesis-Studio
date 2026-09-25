using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;

namespace Genesis.Physics.Buoyancy
{
    /// <summary>
    /// Central manager that links <see cref="BuoyancyVolume"/> definitions to a
    /// <see cref="SandboxPhysicsWorld"/> (or <see cref="PhysicsWorld"/>).
    ///
    /// Each physics step, call <see cref="Step"/> before or after
    /// <c>SandboxPhysicsWorld.Step</c> — it applies buoyancy and flow impulses
    /// directly to body velocities via <c>BodyReference</c>.
    ///
    /// <para><b>Design note:</b> Bepu's <c>IPoseIntegratorCallbacks.IntegrateVelocity</c>
    /// operates on SIMD-wide bundles and cannot easily access per-body state from a
    /// managed dictionary.  Instead we apply impulses the frame <em>before</em>
    /// Bepu integrates, using the public <c>BodyReference</c> API.  This is
    /// equivalent to a sub-step force application at dt precision.</para>
    ///
    /// <para><b>Character detection</b> — The integration additionally exposes
    /// <see cref="IsInWater"/> and <see cref="TryGetVolumeAt"/> so character
    /// controllers can switch to swim/buoyant mode without scanning the volume list
    /// themselves.</para>
    /// </summary>
    public sealed class WaterPhysicsIntegration
    {
        // ── State ────────────────────────────────────────────────────────────────

        private readonly SandboxPhysicsWorld _world;
        private readonly List<BuoyancyVolume> _volumes = new();
        private int _nextId = 1;

        // Cache gravity direction for efficiency (updated each Step call).
        private Vector3 _gravity;

        // ── Events ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired when a tracked <see cref="PhysicsBody"/> enters a <see cref="BuoyancyVolume"/>.
        /// <br/>Note: only raised for bodies registered via <see cref="TrackBody"/>.
        /// </summary>
        public event Action<PhysicsBody, BuoyancyVolume>? BodyEntered;

        /// <summary>Fired when a tracked body exits all water volumes.</summary>
        public event Action<PhysicsBody, BuoyancyVolume>? BodyExited;

        // ── Tracking (for character controller notifications) ────────────────────

        private readonly Dictionary<PhysicsBody, BuoyancyVolume?> _trackedBodies = new();

        // ── Constructor ──────────────────────────────────────────────────────────

        public WaterPhysicsIntegration(SandboxPhysicsWorld world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        // ── Volume registration ──────────────────────────────────────────────────

        /// <summary>
        /// Registers a new buoyancy volume and returns it with a unique ID assigned.
        /// The volume is active from the next <see cref="Step"/> call onward.
        /// </summary>
        public BuoyancyVolume AddVolume(BuoyancyVolume volume)
        {
            if (volume == null) throw new ArgumentNullException(nameof(volume));
            volume.Id = _nextId++;
            _volumes.Add(volume);
            return volume;
        }

        /// <summary>Creates and registers a simple lake / ocean slab.</summary>
        public BuoyancyVolume AddLake(float surfaceY, Bounds2D bounds,
            float fluidDensity = 1000f, float linearDrag = 2.5f)
        {
            return AddVolume(new BuoyancyVolume
            {
                Name           = "Lake",
                SurfaceY       = surfaceY,
                HorizontalBounds = bounds,
                FluidDensity   = fluidDensity,
                LinearDrag     = linearDrag,
            });
        }

        /// <summary>Creates and registers a river volume with a flow field along a spline.</summary>
        public BuoyancyVolume AddRiver(float surfaceY, Bounds2D bounds,
            FlowField flow, float fluidDensity = 1000f)
        {
            return AddVolume(new BuoyancyVolume
            {
                Name           = "River",
                SurfaceY       = surfaceY,
                HorizontalBounds = bounds,
                FluidDensity   = fluidDensity,
                FlowField      = flow,
            });
        }

        /// <summary>Removes a volume by its ID. Returns true if found and removed.</summary>
        public bool RemoveVolume(int volumeId)
        {
            int idx = _volumes.FindIndex(v => v.Id == volumeId);
            if (idx < 0) return false;
            _volumes.RemoveAt(idx);
            return true;
        }

        /// <summary>Read-only view over all registered volumes.</summary>
        public IReadOnlyList<BuoyancyVolume> Volumes => _volumes;

        // ── Body tracking (for character controller / events) ────────────────────

        /// <summary>
        /// Opts a body into the enter/exit event system so controllers can react
        /// to water transitions. Call after <see cref="SandboxPhysicsWorld.AddDynamicBox"/>
        /// or similar.
        /// </summary>
        public void TrackBody(PhysicsBody body)
        {
            _trackedBodies.TryAdd(body, null);
        }

        /// <summary>Stops tracking a body. Call after it is removed from the world.</summary>
        public void UntrackBody(PhysicsBody body)
        {
            _trackedBodies.Remove(body);
        }

        // ── Per-step API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Applies buoyancy and flow forces to all dynamic bodies in the world.
        /// Call once per physics tick, <em>before</em> <c>SandboxPhysicsWorld.Step</c>
        /// so that Bepu integrates the modified velocities in the same frame.
        /// </summary>
        /// <param name="dt">Physics timestep in seconds (must be > 0).</param>
        public void Step(float dt)
        {
            if (dt <= 0f || _volumes.Count == 0) return;

            _gravity = _world.Gravity;

            // Snapshot so UI-thread lake toggles cannot mutate _volumes mid-iteration.
            BuoyancyVolume[] volumes = _volumes.Count > 0 ? _volumes.ToArray() : Array.Empty<BuoyancyVolume>();

            int bodyCount = _world.DynamicBodyCount;
            for (int slot = 0; slot < bodyCount; slot++)
            {
                PhysicsBody body = _world.GetDynamicBody(slot);
                if (!body.IsValid) continue;

                _world.GetPose(body, out Vector3 position, out _);

                BuoyancyVolume? activeVolume = FindVolumeAt(position, volumes);
                if (activeVolume == null)
                {
                    NotifyExited(body);
                    continue;
                }

                NotifyEntered(body, activeVolume);
                ApplyBuoyancyAndFlow(body, position, activeVolume, dt);
            }
        }

        // ── Character / query helpers ─────────────────────────────────────────────

        /// <summary>
        /// Returns true if <paramref name="worldPos"/> is inside any registered water volume.
        /// Cheap O(n) scan — fine for ≤ dozens of volumes per scene.
        /// </summary>
        public bool IsInWater(Vector3 worldPos) => FindVolumeAt(worldPos) != null;

        /// <summary>
        /// Returns the <see cref="BuoyancyVolume"/> containing <paramref name="worldPos"/>,
        /// or null if outside all volumes.
        /// </summary>
        public BuoyancyVolume? TryGetVolumeAt(Vector3 worldPos) => FindVolumeAt(worldPos);

        /// <summary>
        /// Convenience overload for character body checks.
        /// Reads the body's pose from the world and delegates to <see cref="IsInWater(Vector3)"/>.
        /// </summary>
        public bool IsBodyInWater(PhysicsBody body)
        {
            if (!body.IsValid) return false;
            _world.GetPose(body, out Vector3 pos, out _);
            return IsInWater(pos);
        }

        // ── Internals ────────────────────────────────────────────────────────────

        private BuoyancyVolume? FindVolumeAt(Vector3 pos)
        {
            if (_volumes.Count == 0)
                return null;

            BuoyancyVolume[] snapshot = _volumes.ToArray();
            return FindVolumeAt(pos, snapshot);
        }

        private static BuoyancyVolume? FindVolumeAt(Vector3 pos, BuoyancyVolume[] volumes)
        {
            for (int i = 0; i < volumes.Length; i++)
            {
                BuoyancyVolume v = volumes[i];
                if (v.Contains(pos))
                    return v;
            }

            return null;
        }

        private void ApplyBuoyancyAndFlow(PhysicsBody body, Vector3 position,
            BuoyancyVolume volume, float dt)
        {
            Vector3 vel = _world.GetLinearVelocity(body);
            float   mass = _world.GetBodyMass(body);

            // ── Submersion fraction ───────────────────────────────────────────
            float fraction = volume.SubmergedFraction(position, body.HalfExtents);
            if (fraction <= 0f) return;

            // ── Buoyancy: counteract gravity proportionally ───────────────────
            // F_buoyancy = -gravity * fraction * buoyancyStrength (upward force)
            // Impulse = F * dt (applied to velocity as dv = F/mass * dt)
            float buoyancyAccel = _gravity.Length() * fraction * volume.BuoyancyStrength;
            Vector3 buoyancyImpulse = -Vector3.Normalize(_gravity) * (buoyancyAccel * dt);

            // ── Drag: slow the body down ──────────────────────────────────────
            // Apply exponential decay: v' = v * (1 - drag * fraction * dt)
            float dragFactor = MathF.Max(0f, 1f - volume.LinearDrag * fraction * dt);
            Vector3 newVel   = vel * dragFactor + buoyancyImpulse;

            // ── Flow field push ───────────────────────────────────────────────
            if (volume.FlowField != null)
            {
                Vector3 flowImpulse = volume.FlowField.ComputeImpulse(position, newVel, dt);
                float   invMass     = mass > 0f ? 1f / mass : 0f;
                newVel += flowImpulse * invMass;
            }

            _world.SetLinearVelocity(body, newVel);

            // ── Angular drag ──────────────────────────────────────────────────
            float angDragFactor = MathF.Max(0f, 1f - volume.AngularDrag * fraction * dt);
            _world.ScaleAngularVelocity(body, angDragFactor);
        }

        private void NotifyEntered(PhysicsBody body, BuoyancyVolume volume)
        {
            if (!_trackedBodies.TryGetValue(body, out BuoyancyVolume? prev)) return;
            if (ReferenceEquals(prev, volume)) return;           // already in this volume
            _trackedBodies[body] = volume;
            if (prev == null) BodyEntered?.Invoke(body, volume); // first water entry
        }

        private void NotifyExited(PhysicsBody body)
        {
            if (!_trackedBodies.TryGetValue(body, out BuoyancyVolume? prev)) return;
            if (prev == null) return;                            // already not in water
            _trackedBodies[body] = null;
            BodyExited?.Invoke(body, prev);
        }
    }
}
