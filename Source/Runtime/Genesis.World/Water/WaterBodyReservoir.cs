using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Genesis.World.Water
{
    /// <summary>
    /// Binds an AF3.1 <see cref="Reservoir"/> to a <see cref="WaterBody"/> so fill level and
    /// conserved volume persist on the body (<see cref="WaterBody.SurfaceY"/> /
    /// <see cref="WaterBody.VolumeCubicMetres"/>). AF3.2 adds hydrostatic hole drain.
    /// </summary>
    public sealed class WaterBodyReservoir
    {
        private readonly Reservoir _reservoir;

        private WaterBodyReservoir(WaterBody body, Reservoir reservoir)
        {
            Body = body;
            _reservoir = reservoir;
            Body.Holes ??= new List<ReservoirHole>();
            SyncToBody();
        }

        public WaterBody Body { get; }
        public Reservoir Solver => _reservoir;
        public float Level => _reservoir.Level;
        public float Volume => _reservoir.Volume;
        public float MaxLevel => _reservoir.MaxLevel;
        public float OverflowVolume => _reservoir.OverflowVolume();

        /// <summary>Last <see cref="DrainHoles"/> volumetric flow rate (m³/s).</summary>
        public float LastOutflowRate { get; private set; }

        /// <summary>
        /// Attach a conserved fill solver. Prefer body volume when set; otherwise seed from
        /// <see cref="WaterBody.SurfaceY"/>.
        /// </summary>
        public static WaterBodyReservoir Attach(WaterBody body)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (body.Kind != WaterBodyKind.Reservoir)
                throw new ArgumentException("WaterBody.Kind must be Reservoir.", nameof(body));

            float baseY = body.ReservoirBaseY;
            float maxHeight = MathF.Max(0.001f, body.ReservoirMaxHeight);
            float areaBase = MathF.Max(1e-6f, body.ReservoirAreaAtBase);
            float areaRim = MathF.Max(1e-6f, body.ReservoirAreaAtRim);

            Func<float, float> areaAtLevel = h =>
            {
                float t = Math.Clamp(h / maxHeight, 0f, 1f);
                return areaBase + (areaRim - areaBase) * t;
            };

            float initialLevel = Math.Clamp(body.SurfaceY, baseY, baseY + maxHeight);
            Reservoir reservoir = new(baseY, maxHeight, areaAtLevel, initialLevel);
            if (body.VolumeCubicMetres > 1e-9f)
            {
                reservoir.Reset(baseY);
                reservoir.AddVolume(body.VolumeCubicMetres);
            }

            return new WaterBodyReservoir(body, reservoir);
        }

        public void AddVolume(float cubicMetres)
        {
            _reservoir.AddVolume(cubicMetres);
            SyncToBody();
        }

        public float RemoveVolume(float cubicMetres)
        {
            float removed = _reservoir.RemoveVolume(cubicMetres);
            SyncToBody();
            return removed;
        }

        public float RemoveOverflow()
        {
            float overflow = _reservoir.RemoveOverflow();
            SyncToBody();
            return overflow;
        }

        public void Reset(float level)
        {
            _reservoir.Reset(level);
            SyncToBody();
        }

        /// <summary>AF3.2: punch a leak aperture on this body (persists on <see cref="WaterBody.Holes"/>).</summary>
        public ReservoirHole PunchHole(
            Vector3 position,
            Vector3 surfaceNormal,
            float radius = 0.006f,
            ReservoirHoleShape shape = ReservoirHoleShape.Round,
            float dischargeCoefficient = 0.62f,
            float directionBiasRadians = 0f)
        {
            Body.Holes ??= new List<ReservoirHole>();
            ReservoirHole hole = new()
            {
                Position = position,
                SurfaceNormal = surfaceNormal.LengthSquared() < 1e-8f
                    ? Vector3.UnitX
                    : Vector3.Normalize(surfaceNormal),
                Radius = Math.Clamp(radius, 0.0005f, 0.08f),
                Shape = shape,
                DischargeCoefficient = Math.Clamp(dischargeCoefficient, 0.05f, 1f),
                DirectionBiasRadians = Math.Clamp(directionBiasRadians, -MathF.PI * 0.5f, MathF.PI * 0.5f),
            };
            Body.Holes.Add(hole);
            return hole;
        }

        /// <summary>
        /// AF3.2: Torricelli drain through authored holes. No discharge when the surface is at or
        /// below a hole. Returns volume removed this step (m³).
        /// </summary>
        public float DrainHoles(float dt, float obstructionFactor = 1f)
        {
            LastOutflowRate = 0f;
            if (dt <= 0f || Body.Holes == null || Body.Holes.Count == 0)
                return 0f;

            float removedTotal = 0f;
            foreach (ReservoirHole hole in Body.Holes)
            {
                float head = _reservoir.HeadAbove(hole.Position.Y);
                if (head <= 0f) continue;
                float q = FluidPhysics.HoleFlowRate(hole, head, obstructionFactor);
                removedTotal += _reservoir.RemoveVolume(q * dt);
            }

            LastOutflowRate = removedTotal / MathF.Max(dt, 1e-4f);
            SyncToBody();
            return removedTotal;
        }

        public void SyncToBody()
        {
            Body.SurfaceY = _reservoir.Level;
            Body.VolumeCubicMetres = _reservoir.Volume;
            Body.VisualDepth = MathF.Max(Body.VisualDepth, _reservoir.Level - _reservoir.BaseY);
            Body.Holes ??= new List<ReservoirHole>();
        }

        /// <summary>Round-trip body JSON and re-attach so persistence is exercised.</summary>
        public static WaterBodyReservoir RoundTrip(WaterBody body, string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"{body.Id}.water.json");
            body.Save(path);
            WaterBody loaded = WaterBody.Load(path)
                ?? throw new InvalidOperationException("WaterBody.Load returned null.");
            return Attach(loaded);
        }
    }
}
