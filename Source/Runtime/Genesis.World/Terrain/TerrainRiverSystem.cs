using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.World.Water;

namespace Genesis.World.Terrain;

/// <summary>Shared terrain-conforming river, cascade detection, carving, and basin-fill authoring.</summary>
public static class TerrainRiverSystem
{
    private readonly record struct RiverSample(Vector3 Position, float Width, float Depth);

    public static TerrainWaterDefinition CreateRiver(TerrainAsset terrain, IReadOnlyList<WaterSplinePoint> controlPoints,
        bool carveRiverbed, float waterfallDropThreshold = 1.5f)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        if (controlPoints is null || controlPoints.Count < 2)
            throw new ArgumentException("A river needs at least two control points.", nameof(controlPoints));

        List<WaterSplinePoint> conformed = controlPoints.Select(point => new WaterSplinePoint
        {
            Position = point.Position with { Y = terrain.SampleHeight(point.Position.X, point.Position.Z) + .045f },
            Width = Math.Clamp(point.Width, .1f, 10000f),
            Depth = Math.Clamp(point.Depth, .05f, 1000f),
        }).ToList();
        List<RiverSample> samples = Sample(conformed, 12);
        // Detect the actual cliff in the source heightfield rather than spreading a
        // drop over the whole authored spline span. This also makes detection stable
        // when a user places only one point above and one point below a cliff lip.
        List<RiverSample> terrainSamples = samples.Select(sample => sample with
        {
            Position = sample.Position with
            {
                Y = terrain.SampleHeight(sample.Position.X, sample.Position.Z) + .045f,
            },
        }).ToList();
        DetectCascades(terrainSamples, Math.Clamp(waterfallDropThreshold, .1f, 1000f), out List<WaterfallParams> cascades,
            out List<WaterImpactHook> hooks);

        float minX = conformed.Min(point => point.Position.X - point.Width * .5f);
        float maxX = conformed.Max(point => point.Position.X + point.Width * .5f);
        float minZ = conformed.Min(point => point.Position.Z - point.Width * .5f);
        float maxZ = conformed.Max(point => point.Position.Z + point.Width * .5f);
        TerrainWaterDefinition definition = new()
        {
            Name = "River",
            Kind = TerrainWaterKind.River,
            Center = new Vector3((minX + maxX) * .5f, conformed.Average(point => point.Position.Y), (minZ + maxZ) * .5f),
            SizeX = MathF.Max(.5f, maxX - minX),
            SizeZ = MathF.Max(.5f, maxZ - minZ),
            SurfaceHeight = conformed.Average(point => point.Position.Y),
            SimulationEnabled = false,
            FlowSpeed = .65f,
            FlowDirection = RiverFlowDirection(conformed),
            WaveAmplitude = .035f,
            PhysicsDepth = conformed.Average(point => point.Depth),
            RiverPoints = conformed,
            CarveRiverbed = carveRiverbed,
            WaterfallDropThreshold = waterfallDropThreshold,
            Cascades = cascades,
            ImpactHooks = hooks,
        };
        definition.Normalize();
        return definition;
    }

    /// <summary>
    /// Creates a connected occupancy footprint for standing water from the terrain contour.
    /// This upgrades legacy rectangular water definitions without modifying the heightfield.
    /// </summary>
    public static bool ConformStandingWater(TerrainAsset terrain, TerrainWaterDefinition water, float contourTolerance = .08f)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(water);
        if (water.Kind != TerrainWaterKind.Water || !water.ConformToTerrain || water.HasFootprint)
            return false;

        int width = terrain.ResolutionX;
        int height = terrain.ResolutionZ;
        var candidate = new bool[width * height];
        float halfX = MathF.Max(terrain.CellSize, water.SizeX * .5f);
        float halfZ = MathF.Max(terrain.CellSize, water.SizeZ * .5f);
        float ceiling = water.SurfaceHeight + MathF.Max(.01f, contourTolerance);
        int minX = Math.Clamp((int)MathF.Floor((water.Center.X - halfX - terrain.OriginX) / terrain.CellSize), 0, width - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling((water.Center.X + halfX - terrain.OriginX) / terrain.CellSize), 0, width - 1);
        int minZ = Math.Clamp((int)MathF.Floor((water.Center.Z - halfZ - terrain.OriginZ) / terrain.CellSize), 0, height - 1);
        int maxZ = Math.Clamp((int)MathF.Ceiling((water.Center.Z + halfZ - terrain.OriginZ) / terrain.CellSize), 0, height - 1);

        int seed = -1;
        float seedDistance = float.MaxValue;
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            float wx = terrain.OriginX + x * terrain.CellSize;
            float wz = terrain.OriginZ + z * terrain.CellSize;
            float nx = (wx - water.Center.X) / halfX;
            float nz = (wz - water.Center.Z) / halfZ;
            if (nx * nx + nz * nz > 1f || terrain.GetHeight(x, z) > ceiling) continue;
            int index = z * width + x;
            candidate[index] = true;
            float distance = nx * nx + nz * nz;
            if (distance < seedDistance) { seedDistance = distance; seed = index; }
        }

        if (seed < 0) return false;
        var accepted = new bool[candidate.Length];
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        accepted[seed] = true;
        while (queue.TryDequeue(out int index))
        {
            int x = index % width;
            int z = index / width;
            Visit(x - 1, z); Visit(x + 1, z); Visit(x, z - 1); Visit(x, z + 1);
        }

        water.CaptureFootprint(terrain.OriginX, terrain.OriginZ, terrain.CellSize, width, height, accepted);
        return water.HasFootprint;

        void Visit(int x, int z)
        {
            if ((uint)x >= (uint)width || (uint)z >= (uint)height) return;
            int next = z * width + x;
            if (!candidate[next] || accepted[next]) return;
            accepted[next] = true;
            queue.Enqueue(next);
        }
    }

    private static Vector2 RiverFlowDirection(IReadOnlyList<WaterSplinePoint> points)
    {
        Vector3 delta = points[^1].Position - points[0].Position;
        Vector2 direction = new(delta.X, delta.Z);
        return direction.LengthSquared() < .0001f ? Vector2.UnitY : Vector2.Normalize(direction);
    }

    public static void CarveRiverbed(TerrainAsset terrain, IReadOnlyList<WaterSplinePoint> controlPoints)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        if (controlPoints is null || controlPoints.Count < 2) return;
        List<RiverSample> samples = Sample(controlPoints, 12);
        float maximumWidth = samples.Max(sample => sample.Width);
        float padding = MathF.Max(terrain.CellSize * 2f, maximumWidth * .35f);
        float minX = samples.Min(sample => sample.Position.X - sample.Width * .5f - padding);
        float maxX = samples.Max(sample => sample.Position.X + sample.Width * .5f + padding);
        float minZ = samples.Min(sample => sample.Position.Z - sample.Width * .5f - padding);
        float maxZ = samples.Max(sample => sample.Position.Z + sample.Width * .5f + padding);
        int x0 = Math.Clamp((int)MathF.Floor((minX - terrain.OriginX) / terrain.CellSize), 0, terrain.ResolutionX - 1);
        int x1 = Math.Clamp((int)MathF.Ceiling((maxX - terrain.OriginX) / terrain.CellSize), 0, terrain.ResolutionX - 1);
        int z0 = Math.Clamp((int)MathF.Floor((minZ - terrain.OriginZ) / terrain.CellSize), 0, terrain.ResolutionZ - 1);
        int z1 = Math.Clamp((int)MathF.Ceiling((maxZ - terrain.OriginZ) / terrain.CellSize), 0, terrain.ResolutionZ - 1);

        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            Vector2 position = new(terrain.OriginX + x * terrain.CellSize, terrain.OriginZ + z * terrain.CellSize);
            RiverSample nearest = samples[0];
            float nearestDistance = float.MaxValue;
            foreach (RiverSample sample in samples)
            {
                float distance = Vector2.Distance(position, new Vector2(sample.Position.X, sample.Position.Z));
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = sample;
            }

            float halfWidth = MathF.Max(terrain.CellSize, nearest.Width * .5f);
            float bankWidth = MathF.Max(terrain.CellSize * 1.5f, nearest.Width * .22f);
            if (nearestDistance >= halfWidth + bankWidth) continue;
            float current = terrain.GetHeight(x, z);
            float radial = Math.Clamp(nearestDistance / halfWidth, 0f, 1f);
            float bed = nearest.Position.Y - .045f - nearest.Depth * (1f - radial * radial);
            float target = bed;
            if (nearestDistance > halfWidth)
            {
                float t = SmoothStep(halfWidth, halfWidth + bankWidth, nearestDistance);
                target = float.Lerp(nearest.Position.Y - .045f, current, t);
            }
            terrain.SetHeight(x, z, MathF.Min(current, target));
        }
    }

    /// <summary>
    /// Evaluates the automatically-tangent cubic Bezier curve used by both authoring
    /// overlays and generated river geometry. Width and channel depth are interpolated
    /// along the same parameter so the viewport is an exact preview of the saved path.
    /// </summary>
    public static IReadOnlyList<WaterSplinePoint> SampleSpline(
        IReadOnlyList<WaterSplinePoint> controlPoints,
        int segmentsPerSpan = 12)
    {
        if (controlPoints is null || controlPoints.Count == 0) return [];
        if (controlPoints.Count == 1) return [controlPoints[0].Clone()];
        return Sample(controlPoints, Math.Clamp(segmentsPerSpan, 2, 64))
            .Select(sample => new WaterSplinePoint
            {
                Position = sample.Position,
                Width = sample.Width,
                Depth = sample.Depth,
            })
            .ToArray();
    }

    /// <summary>Floods cells below a contour from a clicked valley/crater seed.</summary>
    public static bool[] TraceBasin(TerrainAsset terrain, float worldX, float worldZ, float surfaceHeight,
        float contourTolerance = .08f)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        int startX = Math.Clamp((int)MathF.Round((worldX - terrain.OriginX) / terrain.CellSize), 0, terrain.ResolutionX - 1);
        int startZ = Math.Clamp((int)MathF.Round((worldZ - terrain.OriginZ) / terrain.CellSize), 0, terrain.ResolutionZ - 1);
        bool[] accepted = new bool[terrain.ResolutionX * terrain.ResolutionZ];
        bool[] visited = new bool[accepted.Length];
        Queue<(int X, int Z)> queue = new();
        queue.Enqueue((startX, startZ));
        float ceiling = surfaceHeight + MathF.Max(.01f, contourTolerance);
        while (queue.TryDequeue(out var cell))
        {
            if ((uint)cell.X >= terrain.ResolutionX || (uint)cell.Z >= terrain.ResolutionZ) continue;
            int index = cell.Z * terrain.ResolutionX + cell.X;
            if (visited[index]) continue;
            visited[index] = true;
            if (terrain.GetHeight(cell.X, cell.Z) > ceiling) continue;
            accepted[index] = true;
            queue.Enqueue((cell.X - 1, cell.Z)); queue.Enqueue((cell.X + 1, cell.Z));
            queue.Enqueue((cell.X, cell.Z - 1)); queue.Enqueue((cell.X, cell.Z + 1));
        }
        return accepted;
    }

    private static void DetectCascades(IReadOnlyList<RiverSample> samples, float threshold,
        out List<WaterfallParams> cascades, out List<WaterImpactHook> hooks)
    {
        cascades = [];
        hooks = [];
        int i = 0;
        while (i < samples.Count - 1)
        {
            if (!IsCliffStep(samples[i], samples[i + 1]))
            {
                i++;
                continue;
            }

            int start = i;
            float accumulatedDrop = 0f;
            float horizontalLength = 0f;
            while (i < samples.Count - 1 && IsCliffStep(samples[i], samples[i + 1]))
            {
                Vector3 step = samples[i + 1].Position - samples[i].Position;
                accumulatedDrop += MathF.Max(0f, -step.Y);
                horizontalLength += new Vector2(step.X, step.Z).Length();
                i++;
            }

            RiverSample top = samples[start], bottom = samples[i];
            float grade = accumulatedDrop / MathF.Max(.001f, horizontalLength);
            if (accumulatedDrop < threshold || grade < .55f) continue;
            Vector3 flow = bottom.Position - top.Position;
            flow.Y = 0f;
            flow = flow.LengthSquared() > 1e-8f ? Vector3.Normalize(flow) : Vector3.UnitZ;
            Vector3 side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, flow));
            float width = MathF.Max(.2f, (top.Width + bottom.Width) * .25f);
            Vector3 topCenter = top.Position;
            Vector3 bottomCenter = bottom.Position;
            WaterfallParams cascade = new()
            {
                TopLeft = topCenter - side * width,
                TopRight = topCenter + side * width,
                BottomLeft = bottomCenter - side * width,
                BottomRight = bottomCenter + side * width,
                FlowDirection = flow,
                CrestRadius = Math.Clamp(width * .16f, .15f, 2.5f),
                VerticalSegments = Math.Clamp((int)MathF.Ceiling(accumulatedDrop * 4f), 8, 64),
                ImpactPosition = bottomCenter,
            };
            cascades.Add(cascade);
            hooks.Add(new WaterImpactHook { Kind = WaterImpactHookKind.Mist, Position = bottomCenter, Radius = MathF.Max(.5f, width), ParticleAsset = "builtin://Waterfall Mist" });
            hooks.Add(new WaterImpactHook { Kind = WaterImpactHookKind.Ripple, Position = bottomCenter + Vector3.UnitY * .02f, Radius = MathF.Max(1f, width * 1.4f), ParticleAsset = "builtin://Waterfall Ripple" });
        }

        static bool IsCliffStep(RiverSample from, RiverSample to)
        {
            Vector3 delta = to.Position - from.Position;
            float drop = -delta.Y;
            if (drop <= .01f) return false;
            float horizontal = new Vector2(delta.X, delta.Z).Length();
            return drop / MathF.Max(.001f, horizontal) >= .45f;
        }
    }

    private static List<RiverSample> Sample(IReadOnlyList<WaterSplinePoint> points, int segmentsPerSpan)
    {
        List<RiverSample> output = [];
        for (int span = 0; span < points.Count - 1; span++)
        {
            WaterSplinePoint p0 = points[Math.Max(0, span - 1)];
            WaterSplinePoint p1 = points[span];
            WaterSplinePoint p2 = points[span + 1];
            WaterSplinePoint p3 = points[Math.Min(points.Count - 1, span + 2)];
            int start = span == 0 ? 0 : 1;
            for (int step = start; step <= segmentsPerSpan; step++)
            {
                float t = step / (float)segmentsPerSpan;
                output.Add(new RiverSample(
                    BezierSpline(p0.Position, p1.Position, p2.Position, p3.Position, t),
                    MathF.Max(.1f, float.Lerp(p1.Width, p2.Width, t)),
                    MathF.Max(.05f, float.Lerp(p1.Depth, p2.Depth, t))));
            }
        }
        return output;
    }

    private static Vector3 BezierSpline(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        // Catmull-Rom's automatic tangents expressed as cubic Bezier handles.
        // Authors place only the meaningful river waypoints; smooth handles follow
        // from neighboring points and remain stable while points are dragged.
        Vector3 b0 = p1;
        Vector3 b1 = p1 + (p2 - p0) / 6f;
        Vector3 b2 = p2 - (p3 - p1) / 6f;
        Vector3 b3 = p2;
        float u = 1f - t;
        return u * u * u * b0
            + 3f * u * u * t * b1
            + 3f * u * t * t * b2
            + t * t * t * b3;
    }

    private static float SmoothStep(float minimum, float maximum, float value)
    {
        float t = Math.Clamp((value - minimum) / MathF.Max(.0001f, maximum - minimum), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
