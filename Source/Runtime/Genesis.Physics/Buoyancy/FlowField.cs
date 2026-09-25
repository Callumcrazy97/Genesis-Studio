using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Physics.Buoyancy
{
    /// <summary>
    /// A directional flow field that applies a push force to bodies inside a <see cref="BuoyancyVolume"/>.
    /// Used for rivers, waterfall zones, and whirlpools.
    ///
    /// Two modes are supported:
    ///  - <see cref="FlowMode.Uniform"/>  — constant direction + strength everywhere in the volume.
    ///  - <see cref="FlowMode.Spline"/>   — direction and strength vary along a polyline (rivers).
    /// </summary>
    public sealed class FlowField
    {
        // ── Core properties ──────────────────────────────────────────────────────

        public FlowMode Mode { get; set; } = FlowMode.Uniform;

        /// <summary>
        /// Uniform-mode: world-space direction vector (need not be normalised; length encodes speed).
        /// </summary>
        public Vector3 UniformFlow { get; set; } = Vector3.Zero;

        /// <summary>
        /// Drag scalar applied on top of the volume's LinearDrag specifically to push bodies
        /// in the flow direction (0 = no push, 1 = full drag toward flow velocity).
        /// </summary>
        public float FlowDragScale { get; set; } = 0.6f;

        /// <summary>Maximum force magnitude (N) applied per body per step. Prevents tunnelling.</summary>
        public float MaxForce { get; set; } = 200f;

        // ── Spline-mode ──────────────────────────────────────────────────────────

        /// <summary>World-space polyline points for spline-mode rivers/waterfalls.</summary>
        public List<FlowSplinePoint> SplinePoints { get; } = new();

        /// <summary>Radius around the spline centre-line within which flow is active.</summary>
        public float SplineRadius { get; set; } = 4f;

        // ── Runtime API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the flow velocity vector at <paramref name="worldPos"/>.
        /// Returns <see cref="Vector3.Zero"/> if the position is outside the active region.
        /// </summary>
        public Vector3 SampleFlow(Vector3 worldPos)
        {
            return Mode switch
            {
                FlowMode.Uniform => UniformFlow,
                FlowMode.Spline  => SampleSplineFlow(worldPos),
                _                => Vector3.Zero,
            };
        }

        /// <summary>
        /// Computes the impulse (in N·s = kg·m/s) to apply to a body at <paramref name="worldPos"/>
        /// with current velocity <paramref name="bodyVelocity"/> over timestep <paramref name="dt"/>.
        /// </summary>
        public Vector3 ComputeImpulse(Vector3 worldPos, Vector3 bodyVelocity, float dt)
        {
            Vector3 target = SampleFlow(worldPos);
            if (target.LengthSquared() < 1e-10f) return Vector3.Zero;

            // Drag-toward-target: accelerate body toward the flow direction
            Vector3 delta  = target - bodyVelocity;
            Vector3 force  = delta * FlowDragScale;

            // Clamp magnitude
            float mag = force.Length();
            if (mag > MaxForce)
                force = force / mag * MaxForce;

            return force * dt;
        }

        // ── Internals ────────────────────────────────────────────────────────────

        private Vector3 SampleSplineFlow(Vector3 worldPos)
        {
            if (SplinePoints.Count < 2) return Vector3.Zero;

            // Find the closest segment on the polyline
            float   bestDistSq  = float.MaxValue;
            Vector3 bestFlow    = Vector3.Zero;
            float   bestBlend   = 0f;

            for (int i = 0; i < SplinePoints.Count - 1; i++)
            {
                Vector3 a = SplinePoints[i].Position;
                Vector3 b = SplinePoints[i + 1].Position;

                Vector3 ab = b - a;
                float   t  = MathF.Max(0f, MathF.Min(1f,
                    Vector3.Dot(worldPos - a, ab) / MathF.Max(ab.LengthSquared(), 1e-8f)));

                Vector3 closest = a + ab * t;
                float   distSq  = (worldPos - closest).LengthSquared();

                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    float speed   = SplinePoints[i].Speed + (SplinePoints[i + 1].Speed - SplinePoints[i].Speed) * t;
                    Vector3 dir   = ab.LengthSquared() > 1e-8f ? Vector3.Normalize(ab) : Vector3.UnitZ;
                    bestFlow = dir * speed;
                    bestBlend = MathF.Sqrt(distSq) / MathF.Max(SplineRadius, 1e-5f);
                }
            }

            // Fade out toward the edge of the radius
            float influence = Math.Clamp(1f - bestBlend, 0f, 1f);
            return bestFlow * influence;
        }
    }

    /// <summary>Mode of a <see cref="FlowField"/>.</summary>
    public enum FlowMode
    {
        /// <summary>Same direction and speed everywhere inside the <see cref="BuoyancyVolume"/>.</summary>
        Uniform,

        /// <summary>Direction and speed vary along a polyline — used for rivers.</summary>
        Spline,
    }

    /// <summary>One point on a river/waterfall spline.</summary>
    public readonly struct FlowSplinePoint
    {
        /// <summary>World-space position of this control point.</summary>
        public readonly Vector3 Position;

        /// <summary>Desired flow speed in m/s at this point.</summary>
        public readonly float Speed;

        public FlowSplinePoint(Vector3 position, float speed)
        {
            Position = position;
            Speed    = speed;
        }

        public override string ToString() => $"({Position.X:F1},{Position.Y:F1},{Position.Z:F1}) @ {Speed:F1} m/s";
    }
}
