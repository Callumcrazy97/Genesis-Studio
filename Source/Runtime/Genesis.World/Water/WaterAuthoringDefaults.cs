using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.World.Water
{
    /// <summary>
    /// AF3.2 headless / PGSL scratchpad for hole authoring. Full Terrain/Water editor wiring is AF3.7;
    /// this holds tool state and punched holes so <c>Engine.Terrain.Water.PunchHole</c> works without
    /// a live Player scene.
    /// </summary>
    public static class WaterAuthoringDefaults
    {
        private static readonly List<ReservoirHole> HolesStorage = new();

        public static float HoleRadius { get; set; } = 0.006f;
        public static ReservoirHoleShape HoleShape { get; set; } = ReservoirHoleShape.Round;
        public static float HoleDischargeCoefficient { get; set; } = 0.62f;
        public static float HoleAngleBiasRadians { get; set; }

        public static int HoleCount => HolesStorage.Count;
        public static IReadOnlyList<ReservoirHole> Holes => HolesStorage;

        public static void Reset()
        {
            HoleRadius = 0.006f;
            HoleShape = ReservoirHoleShape.Round;
            HoleDischargeCoefficient = 0.62f;
            HoleAngleBiasRadians = 0f;
            HolesStorage.Clear();
        }

        public static ReservoirHole PunchHole(Vector3 position, Vector3 surfaceNormal)
        {
            ReservoirHole hole = new()
            {
                Position = position,
                SurfaceNormal = surfaceNormal.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(surfaceNormal),
                Radius = Math.Clamp(HoleRadius, 0.0005f, 0.08f),
                Shape = HoleShape,
                DischargeCoefficient = Math.Clamp(HoleDischargeCoefficient, 0.05f, 1f),
                DirectionBiasRadians = Math.Clamp(HoleAngleBiasRadians, -MathF.PI * 0.5f, MathF.PI * 0.5f),
            };
            HolesStorage.Add(hole);
            return hole;
        }

        public static void ClearHoles() => HolesStorage.Clear();

        /// <summary>Copy authored holes onto a reservoir body (does not clear the scratchpad).</summary>
        public static void ApplyHolesTo(WaterBody body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            body.Holes ??= new List<ReservoirHole>();
            body.Holes.Clear();
            foreach (ReservoirHole hole in HolesStorage)
                body.Holes.Add(hole.Clone());
        }
    }
}
