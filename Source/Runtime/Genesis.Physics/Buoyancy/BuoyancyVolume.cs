using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Physics.Buoyancy
{
    /// <summary>
    /// Describes the physics properties of a water region.
    /// A <see cref="BuoyancyVolume"/> is an axis-aligned box (or infinite horizontal slab)
    /// that applies Archimedes buoyancy and velocity damping to every dynamic body
    /// whose centre-of-mass falls inside it each physics step.
    ///
    /// Water rendering is handled separately (see WaterBody in Genesis.World).
    /// This record only governs the simulation side.
    /// </summary>
    public sealed class BuoyancyVolume
    {
        // ── Identity ────────────────────────────────────────────────────────────

        /// <summary>Unique identifier assigned when registered with <see cref="WaterPhysicsIntegration"/>.</summary>
        public int Id { get; internal set; }

        /// <summary>Human-readable name (used in editor and save data).</summary>
        public string Name { get; set; } = "Water";

        // ── Geometry ────────────────────────────────────────────────────────────

        /// <summary>
        /// World-space Y coordinate of the water surface.
        /// Bodies whose centre is below this level and above <see cref="BottomY"/> are considered submerged.
        /// </summary>
        public float SurfaceY { get; set; }

        /// <summary>
        /// World-space Y of the volume floor (−∞ for an ocean / lake with no visible bottom).
        /// Set to <see cref="float.NegativeInfinity"/> to allow unlimited depth.
        /// </summary>
        public float BottomY { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// Horizontal AABB of the volume. Bodies outside this rectangle are skipped even if
        /// their Y is inside the vertical range. Use <see cref="Bounds2D.Infinite"/> for oceans.
        /// </summary>
        public Bounds2D HorizontalBounds { get; set; } = Bounds2D.Infinite;

        // ── Fluid properties ────────────────────────────────────────────────────

        /// <summary>Fluid density in kg/m³ (fresh water ≈ 1000, sea water ≈ 1025).</summary>
        public float FluidDensity { get; set; } = 1000f;

        /// <summary>
        /// Fraction of gravity cancelled per unit of submersion depth (0–1 range is typical).
        /// The actual upward force is: <c>gravity * FluidDensity * submergedVolume</c>
        /// where submergedVolume is approximated from the body's AABB half-extents.
        /// </summary>
        public float BuoyancyStrength { get; set; } = 1.0f;

        /// <summary>
        /// Linear drag coefficient applied to submerged bodies.
        /// Higher values = body slows down faster in water.
        /// Typical range: 0.1 (gentle current) – 5.0 (thick sludge).
        /// </summary>
        public float LinearDrag { get; set; } = 2.5f;

        /// <summary>Angular drag applied to submerged bodies (reduces spinning).</summary>
        public float AngularDrag { get; set; } = 1.0f;

        /// <summary>
        /// Optional flow field that adds a directional push to submerged bodies
        /// (rivers, waterfalls). <c>null</c> = still water.
        /// </summary>
        public FlowField? FlowField { get; set; }

        // ── Character detection ─────────────────────────────────────────────────

        /// <summary>
        /// Y height above <see cref="SurfaceY"/> at which a character is considered "entering"
        /// the water (prevents snap-in at exactly the surface).
        /// </summary>
        public float CharacterEntryThreshold { get; set; } = 0.1f;

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if <paramref name="worldPos"/> is inside this volume.
        /// </summary>
        public bool Contains(Vector3 worldPos)
        {
            if (worldPos.Y > SurfaceY) return false;
            if (worldPos.Y < BottomY)  return false;
            return HorizontalBounds.Contains(worldPos.X, worldPos.Z);
        }

        /// <summary>
        /// Computes the fraction of a body (approximated as a box with <paramref name="halfExtents"/>)
        /// that is submerged in this volume. Returns 0–1.
        /// </summary>
        public float SubmergedFraction(Vector3 centre, Vector3 halfExtents)
        {
            float bodyTop    = centre.Y + halfExtents.Y;
            float bodyBottom = centre.Y - halfExtents.Y;
            float bodyHeight = halfExtents.Y * 2f;

            if (bodyTop <= BottomY || bodyBottom >= SurfaceY)
                return 0f;

            float submergedTop    = MathF.Min(bodyTop,    SurfaceY);
            float submergedBottom = MathF.Max(bodyBottom, BottomY);
            float submergedHeight = submergedTop - submergedBottom;

            return Math.Clamp(submergedHeight / MathF.Max(bodyHeight, 1e-5f), 0f, 1f);
        }

        public override string ToString() =>
            $"BuoyancyVolume '{Name}' surfaceY={SurfaceY:F2} density={FluidDensity} bounds={HorizontalBounds}";
    }

    // ── Supporting types ─────────────────────────────────────────────────────────

    /// <summary>
    /// Axis-aligned horizontal rectangle used to bound a <see cref="BuoyancyVolume"/>.
    /// Infinity sentinels are handled via <see cref="Infinite"/>.
    /// </summary>
    public readonly struct Bounds2D
    {
        public readonly float MinX, MinZ, MaxX, MaxZ;

        public Bounds2D(float minX, float minZ, float maxX, float maxZ)
        {
            MinX = minX; MinZ = minZ;
            MaxX = maxX; MaxZ = maxZ;
        }

        /// <summary>Covers the entire XZ plane — used for oceans.</summary>
        public static readonly Bounds2D Infinite = new(
            float.NegativeInfinity, float.NegativeInfinity,
            float.PositiveInfinity, float.PositiveInfinity);

        public bool Contains(float x, float z) =>
            x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

        public override string ToString() =>
            float.IsNegativeInfinity(MinX) ? "Infinite" :
            $"({MinX:F1},{MinZ:F1})→({MaxX:F1},{MaxZ:F1})";
    }
}
