using System;
using System.Numerics;

namespace Genesis.World.Water
{
    /// <summary>AF3.2 aperture shape — area scaling matches Fluid lab.</summary>
    public enum ReservoirHoleShape
    {
        Round = 0,
        Slit = 1,
        Crack = 2,
    }

    /// <summary>
    /// Authored leak aperture on a <see cref="WaterBodyKind.Reservoir"/>. JSON-serializable;
    /// discharges only when the free surface is above <see cref="Position"/>.Y.
    /// </summary>
    public sealed class ReservoirHole
    {
        public Vector3 Position { get; set; }
        public Vector3 SurfaceNormal { get; set; } = Vector3.UnitX;
        public float Radius { get; set; } = 0.006f;
        public ReservoirHoleShape Shape { get; set; } = ReservoirHoleShape.Round;
        public float DischargeCoefficient { get; set; } = 0.62f;
        public float DirectionBiasRadians { get; set; }

        public float Area => Shape switch
        {
            ReservoirHoleShape.Slit => MathF.PI * Radius * Radius * 0.55f,
            ReservoirHoleShape.Crack => MathF.PI * Radius * Radius * 0.28f,
            _ => MathF.PI * Radius * Radius,
        };

        public ReservoirHole Clone() => new()
        {
            Position = Position,
            SurfaceNormal = SurfaceNormal,
            Radius = Radius,
            Shape = Shape,
            DischargeCoefficient = DischargeCoefficient,
            DirectionBiasRadians = DirectionBiasRadians,
        };
    }

    /// <summary>
    /// AF3.2 hydrostatic orifice math (Torricelli). Ported from Fluid lab <c>FluidPhysics</c>.
    /// Jet mesh generation is AF3.3 — this type only supplies flow/speed/direction.
    /// </summary>
    public static class FluidPhysics
    {
        public const float Gravity = 9.81f;

        public static float HoleFlowRate(ReservoirHole hole, float headMetres, float obstructionFactor = 1f)
        {
            if (hole == null || headMetres <= 0f || hole.Radius <= 0f) return 0f;
            float speed = MathF.Sqrt(2f * Gravity * headMetres);
            // Angled openings expose less effective cross-section to the hydrostatic head.
            float orientationFactor = 0.45f + 0.55f * MathF.Cos(MathF.Abs(hole.DirectionBiasRadians));
            return hole.DischargeCoefficient
                * hole.Area
                * speed
                * Math.Clamp(obstructionFactor, 0f, 1f)
                * orientationFactor;
        }

        public static float HoleJetSpeed(float headMetres) =>
            headMetres <= 0f ? 0f : MathF.Sqrt(2f * Gravity * headMetres);

        public static Vector3 DeflectJet(Vector3 normal, float directionBiasRadians)
        {
            Vector3 n = Vector3.Normalize(normal.LengthSquared() < 1e-8f ? Vector3.UnitX : normal);
            Vector3 tangent = Vector3.Normalize(
                MathF.Abs(n.Y) < 0.9f
                    ? Vector3.Cross(Vector3.UnitY, n)
                    : Vector3.UnitX);
            return Vector3.Normalize(n * MathF.Cos(directionBiasRadians) + tangent * MathF.Sin(directionBiasRadians));
        }
    }
}
