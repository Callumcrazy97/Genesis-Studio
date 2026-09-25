using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.World.Terrain;

public enum TerrainPathKind
{
    Road,
    Trail,
    RidgeTrail,
}

public static class TerrainPathGeometry
{
    private const int RibbonLanes = 5;

    /// <summary>
    /// Builds the visible path ribbon. When a terrain sampler is supplied, sparse authored
    /// control-point spans are subdivided and both ribbon edges are snapped to the heightfield.
    /// This prevents a long path segment from bridging a hollow and intersecting the player
    /// camera while retaining the compact, editable control-point network in the asset.
    /// </summary>
    public static MeshData BuildRibbon(
        TerrainPathDefinition path,
        Func<float, float, float> heightSampler = null,
        float maximumSegmentLength = 4f)
    {
        if (path?.Points == null || path.Points.Count < 2) return default;
        List<Vector3> points = BuildRenderPoints(path.Points, heightSampler, maximumSegmentLength);
        if (points.Count > ushort.MaxValue / RibbonLanes)
            throw new InvalidOperationException("A terrain path ribbon exceeds the 16-bit mesh index limit.");

        var vertices = new MeshVertex[points.Count * RibbonLanes];
        var indices = new ushort[(points.Count - 1) * (RibbonLanes - 1) * 6];
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector3.Distance(points[i - 1], points[i]);
        length = MathF.Max(length, 0.001f);
        float travelled = 0f;
        Vector4 centreColor = path.Kind switch
        {
            TerrainPathKind.Road => new Vector4(0.30f, 0.24f, 0.18f, 1f),
            TerrainPathKind.RidgeTrail => new Vector4(0.31f, 0.28f, 0.22f, 1f),
            _ => new Vector4(0.29f, 0.20f, 0.12f, 1f),
        };
        Vector4 edgeColor = new(0.20f, 0.22f, 0.105f, 1f);
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 point = points[i];
            if (i > 0) travelled += Vector3.Distance(points[i - 1], point);
            Vector3 tangent = i == 0 ? points[1] - point : i == points.Count - 1 ? point - points[i - 1] : points[i + 1] - points[i - 1];
            tangent.Y = 0f;
            if (tangent.LengthSquared() < 1e-6f) tangent = Vector3.UnitZ;
            tangent = Vector3.Normalize(tangent);
            Vector3 side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, tangent));
            float u = travelled / length;
            for (int lane = 0; lane < RibbonLanes; lane++)
            {
                float across = lane / (float)(RibbonLanes - 1);
                float signedAcross = across * 2f - 1f;
                Vector3 position = point + side * signedAcross * path.Width * 0.5f;
                if (heightSampler != null)
                    position.Y = heightSampler(position.X, position.Z) + 0.04f;
                float edge = MathF.Pow(MathF.Abs(signedAcross), 1.65f);
                float variation = MathF.Sin(position.X * 0.31f + position.Z * 0.23f) * 0.022f;
                Vector4 color = Vector4.Lerp(centreColor, edgeColor, edge);
                color += new Vector4(variation, variation * 0.72f, variation * 0.38f, 0f);
                vertices[i * RibbonLanes + lane] = new MeshVertex
                {
                    Position = position,
                    Normal = Vector3.UnitY,
                    Color = Vector4.Clamp(color, Vector4.Zero, Vector4.One),
                    UV = new Vector2(u * 24f, across),
                };
            }
        }
        int cursor = 0;
        for (int i = 0; i < points.Count - 1; i++)
        for (int lane = 0; lane < RibbonLanes - 1; lane++)
        {
            ushort a = checked((ushort)(i * RibbonLanes + lane));
            ushort b = (ushort)(a + 1);
            ushort c = checked((ushort)((i + 1) * RibbonLanes + lane));
            ushort d = (ushort)(c + 1);
            indices[cursor++] = a; indices[cursor++] = c; indices[cursor++] = b;
            indices[cursor++] = b; indices[cursor++] = c; indices[cursor++] = d;
        }
        return new MeshData { Vertices = vertices, Indices = indices };
    }

    private static List<Vector3> BuildRenderPoints(
        IReadOnlyList<Vector3> controlPoints,
        Func<float, float, float> heightSampler,
        float maximumSegmentLength)
    {
        if (heightSampler == null) return controlPoints.ToList();

        maximumSegmentLength = MathF.Max(0.25f, maximumSegmentLength);
        var result = new List<Vector3>(controlPoints.Count * 2);
        Vector3 first = controlPoints[0];
        first.Y = heightSampler(first.X, first.Z) + 0.04f;
        result.Add(first);
        for (int i = 1; i < controlPoints.Count; i++)
        {
            Vector3 from = controlPoints[i - 1];
            Vector3 to = controlPoints[i];
            float horizontalLength = Vector2.Distance(new Vector2(from.X, from.Z), new Vector2(to.X, to.Z));
            int steps = Math.Max(1, (int)MathF.Ceiling(horizontalLength / maximumSegmentLength));
            for (int step = 1; step <= steps; step++)
            {
                float t = step / (float)steps;
                Vector3 point = Vector3.Lerp(from, to, t);
                point.Y = heightSampler(point.X, point.Z) + 0.04f;
                result.Add(point);
            }
        }
        return result;
    }
}

public sealed class TerrainPathSettings
{
    public int Seed { get; set; } = 1337;
    public int PathCount { get; set; } = 3;
    public float Width { get; set; } = 4f;
    public float GradeStrength { get; set; } = 0.78f;
    public int SplatChannel { get; set; } = 1;
    public TerrainPathKind Kind { get; set; } = TerrainPathKind.Trail;

    public void Normalize()
    {
        PathCount = Math.Clamp(PathCount, 1, 8);
        Width = Math.Clamp(Width, 0.25f, 64f);
        GradeStrength = Math.Clamp(GradeStrength, 0f, 1f);
        SplatChannel = Math.Clamp(SplatChannel, 0, 3);
    }
}

public sealed class TerrainPathDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Trail";
    public TerrainPathKind Kind { get; set; } = TerrainPathKind.Trail;
    public float Width { get; set; } = 4f;
    public List<Vector3> Points { get; set; } = new();

    public float Length
    {
        get
        {
            float length = 0f;
            for (int i = 1; i < Points.Count; i++) length += Vector3.Distance(Points[i - 1], Points[i]);
            return length;
        }
    }
}

public readonly record struct TerrainPathSample(
    float Distance,
    float TargetHeight,
    float Width,
    TerrainPathKind Kind,
    Vector3 ClosestPoint)
{
    public static TerrainPathSample None => new(float.MaxValue, 0f, 0f, TerrainPathKind.Trail, default);
    public bool IsValid => Distance < float.MaxValue;
    public float Mask(float shoulder = 1.5f)
    {
        if (!IsValid) return 0f;
        float outer = MathF.Max(Width * shoulder, Width + 0.01f);
        float t = Math.Clamp((outer - Distance) / MathF.Max(outer - Width * 0.45f, 0.001f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>
/// Connected, deterministic terrain routes. The path finder samples the authoritative heightfield,
/// penalises slope and excessive climbing, then relaxes and resamples the route for editor/runtime use.
/// </summary>
public sealed class TerrainPathNetwork
{
    private const float IndexCellSize = 24f;
    private readonly Dictionary<(int X, int Z), List<Segment>> _index = new();

    public TerrainPathNetwork(IEnumerable<TerrainPathDefinition> paths)
    {
        Paths = paths?.Where(path => path?.Points?.Count >= 2).ToList() ?? new List<TerrainPathDefinition>();
        BuildIndex();
    }

    public IReadOnlyList<TerrainPathDefinition> Paths { get; }

    public static TerrainPathNetwork Generate(TerrainAsset terrain, TerrainPathSettings settings)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        settings ??= new TerrainPathSettings();
        settings.Normalize();

        Vector2 min = new(terrain.OriginX, terrain.OriginZ);
        Vector2 max = new(
            terrain.OriginX + (terrain.ResolutionX - 1) * terrain.CellSize,
            terrain.OriginZ + (terrain.ResolutionZ - 1) * terrain.CellSize);
        Vector2 size = max - min;
        Vector2 centre = (min + max) * 0.5f;
        var paths = new List<TerrainPathDefinition>(settings.PathCount);
        var rng = new DeterministicRandom(unchecked((uint)settings.Seed));

        Vector2 mainStart = new(min.X + size.X * 0.06f, centre.Y - size.Y * 0.22f);
        Vector2 mainEnd = new(max.X - size.X * 0.06f, centre.Y + size.Y * 0.18f);
        List<Vector2> main = FindRoute(terrain, mainStart, mainEnd, settings.Kind, settings.Seed);
        paths.Add(MakePath(terrain, main, "Main Trail", settings.Kind, settings.Width));

        for (int i = 1; i < settings.PathCount; i++)
        {
            float side = (i & 1) == 0 ? -1f : 1f;
            float edgeT = 0.14f + 0.72f * rng.NextFloat();
            Vector2 edge = side < 0f
                ? new Vector2(min.X + size.X * edgeT, min.Y + size.Y * 0.05f)
                : new Vector2(min.X + size.X * edgeT, max.Y - size.Y * 0.05f);
            int junctionIndex = Math.Clamp((int)((0.18f + rng.NextFloat() * 0.64f) * (main.Count - 1)), 0, main.Count - 1);
            Vector2 junction = main[junctionIndex];
            TerrainPathKind kind = i % 3 == 0 ? TerrainPathKind.RidgeTrail : settings.Kind;
            List<Vector2> branch = FindRoute(terrain, edge, junction, kind, settings.Seed + i * 7919);
            paths.Add(MakePath(terrain, branch, $"{kind} {i + 1}", kind, settings.Width * (kind == TerrainPathKind.Road ? 1.25f : 0.72f)));
        }

        return new TerrainPathNetwork(paths);
    }

    public TerrainPathSample Sample(float worldX, float worldZ)
    {
        int cx = FastFloor(worldX / IndexCellSize);
        int cz = FastFloor(worldZ / IndexCellSize);
        float best = float.MaxValue;
        Segment selected = default;
        float selectedT = 0f;
        bool found = false;
        for (int oz = -1; oz <= 1; oz++)
        for (int ox = -1; ox <= 1; ox++)
        {
            if (!_index.TryGetValue((cx + ox, cz + oz), out List<Segment> segments)) continue;
            foreach (Segment segment in segments)
            {
                float distance = DistanceToSegmentSquared(new Vector2(worldX, worldZ), segment.A2, segment.B2, out float t);
                if (distance >= best) continue;
                best = distance;
                selected = segment;
                selectedT = t;
                found = true;
            }
        }

        if (!found) return TerrainPathSample.None;
        Vector3 point = Vector3.Lerp(selected.A, selected.B, selectedT);
        return new TerrainPathSample(MathF.Sqrt(best), point.Y, selected.Width, selected.Kind, point);
    }

    /// <summary>Grades the path bed and paints its material channel onto the terrain.</summary>
    public void ApplyTo(TerrainAsset terrain, float gradeStrength, int splatChannel)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        float strength = Math.Clamp(gradeStrength, 0f, 1f);
        splatChannel = Math.Clamp(splatChannel, 0, 3);
        for (int z = 0; z < terrain.ResolutionZ; z++)
        for (int x = 0; x < terrain.ResolutionX; x++)
        {
            float wx = terrain.OriginX + x * terrain.CellSize;
            float wz = terrain.OriginZ + z * terrain.CellSize;
            TerrainPathSample sample = Sample(wx, wz);
            if (!sample.IsValid) continue;
            float mask = sample.Mask();
            if (mask <= 0f) continue;
            float current = terrain.GetHeight(x, z);
            terrain.SetHeight(x, z, current + (sample.TargetHeight - current) * mask * strength);
            if (mask > 0.06f)
                terrain.ApplyPaintBrush(wx, wz, terrain.CellSize * 0.75f, mask * 0.85f, splatChannel);
        }
    }

    private void BuildIndex()
    {
        foreach (TerrainPathDefinition path in Paths)
        for (int i = 1; i < path.Points.Count; i++)
        {
            var segment = new Segment(path.Points[i - 1], path.Points[i], path.Width, path.Kind);
            float margin = path.Width * 2f + 2f;
            int minX = FastFloor((MathF.Min(segment.A.X, segment.B.X) - margin) / IndexCellSize);
            int maxX = FastFloor((MathF.Max(segment.A.X, segment.B.X) + margin) / IndexCellSize);
            int minZ = FastFloor((MathF.Min(segment.A.Z, segment.B.Z) - margin) / IndexCellSize);
            int maxZ = FastFloor((MathF.Max(segment.A.Z, segment.B.Z) + margin) / IndexCellSize);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                if (!_index.TryGetValue((x, z), out List<Segment> list))
                    _index[(x, z)] = list = new List<Segment>();
                list.Add(segment);
            }
        }
    }

    private static TerrainPathDefinition MakePath(
        TerrainAsset terrain, List<Vector2> route, string name, TerrainPathKind kind, float width)
    {
        List<Vector2> relaxed = Relax(route, 2);
        var points = new List<Vector3>(relaxed.Count);
        foreach (Vector2 point in relaxed)
            points.Add(new Vector3(point.X, terrain.SampleHeight(point.X, point.Y) + 0.03f, point.Y));
        return new TerrainPathDefinition { Name = name, Kind = kind, Width = width, Points = points };
    }

    private static List<Vector2> FindRoute(
        TerrainAsset terrain, Vector2 start, Vector2 end, TerrainPathKind kind, int seed)
    {
        int gridX = Math.Clamp((terrain.ResolutionX - 1) / 3 + 1, 17, 65);
        int gridZ = Math.Clamp((terrain.ResolutionZ - 1) / 3 + 1, 17, 65);
        float extentX = (terrain.ResolutionX - 1) * terrain.CellSize;
        float extentZ = (terrain.ResolutionZ - 1) * terrain.CellSize;
        Vector2 ToWorld(int x, int z) => new(
            terrain.OriginX + x / (float)(gridX - 1) * extentX,
            terrain.OriginZ + z / (float)(gridZ - 1) * extentZ);
        int ToX(float x) => Math.Clamp((int)MathF.Round((x - terrain.OriginX) / MathF.Max(extentX, 0.001f) * (gridX - 1)), 0, gridX - 1);
        int ToZ(float z) => Math.Clamp((int)MathF.Round((z - terrain.OriginZ) / MathF.Max(extentZ, 0.001f) * (gridZ - 1)), 0, gridZ - 1);
        int Index(int x, int z) => z * gridX + x;

        int startIndex = Index(ToX(start.X), ToZ(start.Y));
        int endIndex = Index(ToX(end.X), ToZ(end.Y));
        int count = gridX * gridZ;
        var costs = new float[count];
        var previous = new int[count];
        var closed = new bool[count];
        Array.Fill(costs, float.PositiveInfinity);
        Array.Fill(previous, -1);
        costs[startIndex] = 0f;
        var queue = new PriorityQueue<int, float>();
        queue.Enqueue(startIndex, 0f);
        int[] offsets = { -1, -1, 0, -1, 1, -1, -1, 0, 1, 0, -1, 1, 0, 1, 1, 1 };

        while (queue.TryDequeue(out int current, out _))
        {
            if (closed[current]) continue;
            closed[current] = true;
            if (current == endIndex) break;
            int cx = current % gridX;
            int cz = current / gridX;
            Vector2 currentWorld = ToWorld(cx, cz);
            float currentHeight = terrain.SampleHeight(currentWorld.X, currentWorld.Y);
            for (int o = 0; o < offsets.Length; o += 2)
            {
                int nx = cx + offsets[o];
                int nz = cz + offsets[o + 1];
                if ((uint)nx >= (uint)gridX || (uint)nz >= (uint)gridZ) continue;
                int next = Index(nx, nz);
                if (closed[next]) continue;
                Vector2 world = ToWorld(nx, nz);
                float height = terrain.SampleHeight(world.X, world.Y);
                float horizontal = Vector2.Distance(currentWorld, world);
                float slope = MathF.Abs(height - currentHeight) / MathF.Max(horizontal, 0.001f);
                float slopeWeight = kind == TerrainPathKind.RidgeTrail ? 2.2f : 8f;
                uint hash = Hash(unchecked((uint)(next * 31 + seed)));
                float variation = (hash & 1023u) / 1023f * 0.08f;
                float nextCost = costs[current] + horizontal * (1f + slope * slope * slopeWeight + variation);
                if (nextCost >= costs[next]) continue;
                costs[next] = nextCost;
                previous[next] = current;
                float heuristic = Vector2.Distance(world, end) * 0.92f;
                queue.Enqueue(next, nextCost + heuristic);
            }
        }

        var reverse = new List<Vector2>();
        int cursor = endIndex;
        while (cursor >= 0)
        {
            reverse.Add(ToWorld(cursor % gridX, cursor / gridX));
            if (cursor == startIndex) break;
            cursor = previous[cursor];
        }
        if (reverse.Count < 2) return new List<Vector2> { start, end };
        reverse.Reverse();
        reverse[0] = start;
        reverse[^1] = end;
        return Simplify(reverse, MathF.Min(extentX, extentZ) / 60f);
    }

    private static List<Vector2> Relax(List<Vector2> source, int iterations)
    {
        if (source.Count < 3) return source;
        var current = new List<Vector2>(source);
        for (int pass = 0; pass < iterations; pass++)
        {
            var next = new List<Vector2>(current.Count * 2) { current[0] };
            for (int i = 1; i < current.Count; i++)
            {
                Vector2 a = current[i - 1];
                Vector2 b = current[i];
                next.Add(Vector2.Lerp(a, b, 0.35f));
                next.Add(Vector2.Lerp(a, b, 0.65f));
            }
            next.Add(current[^1]);
            current = next;
        }
        return current;
    }

    private static List<Vector2> Simplify(List<Vector2> source, float minimumDistance)
    {
        var result = new List<Vector2> { source[0] };
        for (int i = 1; i < source.Count - 1; i++)
            if (Vector2.DistanceSquared(result[^1], source[i]) >= minimumDistance * minimumDistance)
                result.Add(source[i]);
        result.Add(source[^1]);
        return result;
    }

    private static float DistanceToSegmentSquared(Vector2 point, Vector2 a, Vector2 b, out float t)
    {
        Vector2 delta = b - a;
        float length = delta.LengthSquared();
        t = length <= 1e-8f ? 0f : Math.Clamp(Vector2.Dot(point - a, delta) / length, 0f, 1f);
        return Vector2.DistanceSquared(point, a + delta * t);
    }

    private static int FastFloor(float value)
    {
        int integer = (int)value;
        return value < integer ? integer - 1 : integer;
    }

    private static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }

    private readonly record struct Segment(Vector3 A, Vector3 B, float Width, TerrainPathKind Kind)
    {
        public Vector2 A2 => new(A.X, A.Z);
        public Vector2 B2 => new(B.X, B.Z);
    }

    private struct DeterministicRandom
    {
        private uint _state;
        public DeterministicRandom(uint seed) => _state = seed == 0 ? 0x853c49e6u : seed;
        public float NextFloat()
        {
            _state = Hash(_state + 0x9e3779b9u);
            return (_state >> 8) * (1f / 16777216f);
        }
    }
}
