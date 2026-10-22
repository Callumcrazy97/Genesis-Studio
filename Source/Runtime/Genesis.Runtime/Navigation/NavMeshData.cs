#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.Runtime.Navigation;

/// <summary>
/// A navigation mesh on a grid of columns. Each column has a ground floor (<see cref="Heights"/>,
/// <see cref="Walkable"/>) and, in a mesh baked with floors, more floors above it: a bridge over a
/// road, a building's upper storey, the steps of a stair (<see cref="LayerColumns"/>). Every floor
/// is a node; nodes of neighbouring columns join when a step between them can be walked and an agent
/// has headroom across it. Each node also knows how far it is from the nearest edge, so agents
/// larger than the bake's own size keep that much further away (one bake for every size).
/// </summary>
public sealed class NavMeshData
{
    public int Version { get; set; } = 1;
    public int Width { get; set; }
    public int Depth { get; set; }
    public float OriginX { get; set; }
    public float OriginZ { get; set; }
    public float CellSize { get; set; } = 1;
    public float MaxStep { get; set; } = .45f;
    public float MaxSlopeDegrees { get; set; } = 45;
    /// <summary>The agent the mesh was baked for: walls are kept this far away, and floors need this headroom.</summary>
    public float AgentRadius { get; set; } = .35f;
    public float AgentHeight { get; set; } = 1.8f;
    /// <summary>The ground floor of each column.</summary>
    public float[] Heights { get; set; } = [];
    public bool[] Walkable { get; set; } = [];
    /// <summary>The headroom over each column's ground floor (the underside of what is above it); empty when nothing is.</summary>
    public float[] GroundCeilings { get; set; } = [];
    /// <summary>Floors above the ground ones: each one's column, height and headroom. Empty in a one-floor mesh.</summary>
    public int[] LayerColumns { get; set; } = [];
    public float[] LayerHeights { get; set; } = [];
    public float[] LayerCeilings { get; set; } = [];

    /// <summary>The headroom of a floor nothing is above (JSON keeps no infinities).</summary>
    public const float NoCeiling = 1e30f;

    public int Count => Width * Depth;
    /// <summary>Every floor of every column: the ground floors are nodes 0 to Count - 1, the others follow.</summary>
    [JsonIgnore] public int NodeCount => Count + LayerColumns.Length;

    // Worked out from the above when first needed: each column's floors above the ground, and
    // each node's distance from the nearest edge.
    private int[]? _firstLayer, _nextLayer;
    private byte[]? _clearance;

    public void Validate()
    {
        int layers = LayerColumns?.Length ?? -1;
        if (Version is not (1 or 2) || Width <= 0 || Depth <= 0 || (long)Width * Depth > 1_048_576
            || Heights == null || Walkable == null || Heights.Length != Count || Walkable.Length != Count || !float.IsFinite(CellSize) || CellSize <= 0
            || !float.IsFinite(OriginX) || !float.IsFinite(OriginZ) || !float.IsFinite(MaxStep) || MaxStep < 0
            || !float.IsFinite(MaxSlopeDegrees) || MaxSlopeDegrees < 0 || MaxSlopeDegrees >= 90
            || !float.IsFinite(AgentRadius) || AgentRadius < 0 || !float.IsFinite(AgentHeight) || AgentHeight <= 0
            || GroundCeilings == null || (GroundCeilings.Length != 0 && GroundCeilings.Length != Count)
            || layers < 0 || LayerHeights == null || LayerCeilings == null || LayerHeights.Length != layers || LayerCeilings.Length != layers
            || layers > 4 * 1_048_576)
            throw new InvalidDataException("Invalid or oversized navigation mesh.");
        foreach (float height in Heights) if (!float.IsFinite(height)) throw new InvalidDataException("Invalid navigation height.");
        for (int i = 0; i < layers; i++)
            if (LayerColumns![i] < 0 || LayerColumns[i] >= Count || !float.IsFinite(LayerHeights[i]) || float.IsNaN(LayerCeilings[i]))
                throw new InvalidDataException("Invalid navigation floor.");
    }

    public Vector3 Center(int cell) => new(OriginX + (cell % Width + .5f) * CellSize,
        Heights[cell], OriginZ + (cell / Width + .5f) * CellSize);
    public int Cell(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return -1;
        float x = (position.X - OriginX) / CellSize, z = (position.Z - OriginZ) / CellSize;
        return x < 0 || z < 0 || x >= Width || z >= Depth ? -1 : (int)z * Width + (int)x;
    }
    public IEnumerable<int> Neighbors(int cell)
    {
        int x = cell % Width, z = cell / Width;
        if (x > 0 && Connected(cell, cell - 1)) yield return cell - 1;
        if (x + 1 < Width && Connected(cell, cell + 1)) yield return cell + 1;
        if (z > 0 && Connected(cell, cell - Width)) yield return cell - Width;
        if (z + 1 < Depth && Connected(cell, cell + Width)) yield return cell + Width;
    }
    /// <summary>Whether two neighbouring columns' ground floors join.</summary>
    public bool Connected(int a, int b) => a >= 0 && b >= 0 && a < Count && b < Count && NodesConnected(a, b);

    // ── Floors as nodes ─────────────────────────────────────────────────────

    public int Column(int node) => node < Count ? node : LayerColumns[node - Count];
    public float NodeHeight(int node) => node < Count ? Heights[node] : LayerHeights[node - Count];
    public float NodeCeiling(int node) => node < Count
        ? (GroundCeilings.Length == Count ? GroundCeilings[node] : NoCeiling)
        : LayerCeilings[node - Count];
    public bool NodeWalkable(int node) => node >= 0 && node < NodeCount && (node >= Count || Walkable[node]);
    public Vector3 NodeCenter(int node)
    {
        Vector3 centre = Center(Column(node));
        centre.Y = NodeHeight(node);
        return centre;
    }

    private void EnsureLayerIndex()
    {
        if (_firstLayer != null) return;
        int[] first = new int[Count], next = new int[LayerColumns.Length];
        Array.Fill(first, -1);
        for (int i = LayerColumns.Length - 1; i >= 0; i--) { next[i] = first[LayerColumns[i]]; first[LayerColumns[i]] = Count + i; }
        _nextLayer = next;
        _firstLayer = first;
    }

    /// <summary>A column's floors, ground first: its nodes, into <paramref name="into"/> (as many as fit); how many there are.</summary>
    public int Floors(int column, Span<int> into)
    {
        int count = 0;
        if (into.Length > 0) into[count] = column;
        count++;
        if (LayerColumns.Length == 0) return count;
        EnsureLayerIndex();
        for (int node = _firstLayer![column]; node >= 0; node = _nextLayer![node - Count])
        {
            if (count < into.Length) into[count] = node;
            count++;
        }
        return count;
    }

    /// <summary>
    /// The floor a point stands on: of its column's walkable floors, the highest no more than a step
    /// above it (the lowest when it is below them all). In a one-floor mesh, the column itself.
    /// </summary>
    public int NodeAt(Vector3 position)
    {
        int column = Cell(position);
        if (column < 0 || LayerColumns.Length == 0) return column;
        Span<int> floors = stackalloc int[16];
        int count = Math.Min(Floors(column, floors), floors.Length);
        int best = -1, lowest = -1;
        for (int i = 0; i < count; i++)
        {
            int node = floors[i];
            if (!NodeWalkable(node)) continue;
            float height = NodeHeight(node);
            if (lowest < 0 || height < NodeHeight(lowest)) lowest = node;
            if (height <= position.Y + MaxStep + .1f && (best < 0 || height > NodeHeight(best))) best = node;
        }
        return best >= 0 ? best : lowest >= 0 ? lowest : column;
    }

    private float StepLimit => MaxStep + CellSize * MathF.Tan(MaxSlopeDegrees * MathF.PI / 180);

    /// <summary>
    /// Whether two floors of neighbouring columns join: both walkable, the step between them can be
    /// walked, and an agent standing on either has headroom across the step.
    /// </summary>
    public bool NodesConnected(int a, int b)
    {
        if (!NodeWalkable(a) || !NodeWalkable(b)) return false;
        float ha = NodeHeight(a), hb = NodeHeight(b);
        if (Math.Abs(ha - hb) > StepLimit) return false;
        return MathF.Min(NodeCeiling(a), NodeCeiling(b)) - MathF.Max(ha, hb) >= AgentHeight * .95f;
    }

    /// <summary>The floors of the four neighbouring columns that a node joins.</summary>
    public IEnumerable<int> NodeNeighbors(int node)
    {
        if (LayerColumns.Length == 0) return Neighbors(node);
        return LayeredNeighbors(node);
    }

    private IEnumerable<int> LayeredNeighbors(int node)
    {
        int column = Column(node), x = column % Width, z = column / Width;
        if (x > 0) foreach (int joined in JoinedIn(node, column - 1)) yield return joined;
        if (x + 1 < Width) foreach (int joined in JoinedIn(node, column + 1)) yield return joined;
        if (z > 0) foreach (int joined in JoinedIn(node, column - Width)) yield return joined;
        if (z + 1 < Depth) foreach (int joined in JoinedIn(node, column + Width)) yield return joined;
    }

    private IEnumerable<int> JoinedIn(int node, int column)
    {
        if (NodesConnected(node, column)) yield return column;
        if (LayerColumns.Length == 0) yield break;
        EnsureLayerIndex();
        for (int other = _firstLayer![column]; other >= 0; other = _nextLayer![other - Count])
            if (NodesConnected(node, other)) yield return other;
    }

    /// <summary>The floor of a neighbouring column a node joins, nearest it in height; -1 when none does.</summary>
    public int JoinedFloor(int node, int column)
    {
        // One floor a column (the common case, walked every frame by every agent): nothing to choose.
        if (LayerColumns.Length == 0) return NodesConnected(node, column) ? column : -1;
        int best = -1;
        float height = NodeHeight(node);
        foreach (int other in JoinedIn(node, column))
            if (best < 0 || Math.Abs(NodeHeight(other) - height) < Math.Abs(NodeHeight(best) - height)) best = other;
        return best;
    }

    /// <summary>How many floors a node is from the nearest edge (0 on an edge; 255 at most).</summary>
    public int Clearance(int node)
    {
        if (_clearance == null)
        {
            byte[] clearance = new byte[NodeCount];
            Array.Fill(clearance, (byte)255);
            var frontier = new Queue<int>();
            for (int n = 0; n < NodeCount; n++)
            {
                if (!NodeWalkable(n)) { clearance[n] = 0; continue; }
                int directions = 0, column = Column(n), x = column % Width, z = column / Width;
                if (x > 0 && JoinedFloor(n, column - 1) >= 0) directions++;
                if (x + 1 < Width && JoinedFloor(n, column + 1) >= 0) directions++;
                if (z > 0 && JoinedFloor(n, column - Width) >= 0) directions++;
                if (z + 1 < Depth && JoinedFloor(n, column + Width) >= 0) directions++;
                if (directions < 4) { clearance[n] = 0; frontier.Enqueue(n); }
            }
            while (frontier.Count > 0)
            {
                int n = frontier.Dequeue();
                foreach (int next in NodeNeighbors(n))
                {
                    if (clearance[next] <= clearance[n] + 1) continue;
                    clearance[next] = (byte)Math.Min(254, clearance[n] + 1);
                    frontier.Enqueue(next);
                }
            }
            _clearance = clearance;
        }
        return node >= 0 && node < _clearance.Length ? _clearance[node] : 0;
    }

    public void Save(string path)
    {
        Validate();
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static NavMeshData Load(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Navigation sidecar is too large.");
        var data = JsonSerializer.Deserialize<NavMeshData>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty navmesh.");
        data.Validate(); return data;
    }
}

public readonly record struct NavigationObstacle(Vector3 Min, Vector3 Max);

public static class NavMeshBuilder
{
    /// <summary>
    /// Bakes a mesh over a height field and box obstacles. With <paramref name="floors"/>, the tops of
    /// boxes an agent fits on become floors of their own (decks, upper storeys, stair steps), and
    /// ground under something with headroom stays walkable.
    /// </summary>
    public static NavMeshData Build(float originX, float originZ, int width, int depth, float cellSize,
        Func<float, float, float> sampleHeight, IReadOnlyList<NavigationObstacle>? obstacles = null,
        float agentRadius = .35f, float agentHeight = 1.8f, float maxStep = .45f, float maxSlope = 45, bool floors = false)
    {
        if (width <= 0 || depth <= 0 || (long)width * depth > 1_048_576 || !float.IsFinite(cellSize) || cellSize <= 0
            || !float.IsFinite(agentRadius) || agentRadius < 0 || !float.IsFinite(agentHeight) || agentHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Invalid navigation bounds or agent dimensions.");
        var data = new NavMeshData { OriginX = originX, OriginZ = originZ, Width = width, Depth = depth,
            CellSize = cellSize, MaxStep = maxStep, MaxSlopeDegrees = maxSlope, AgentRadius = agentRadius, AgentHeight = agentHeight,
            Heights = new float[width * depth], Walkable = new bool[width * depth] };
        data.Validate();
        float gradientLimit = MathF.Tan(maxSlope * MathF.PI / 180);
        float[]? ceilings = floors && obstacles is { Count: > 0 } ? new float[width * depth] : null;
        var layerColumns = new List<int>();
        var layerHeights = new List<float>();
        var layerCeilings = new List<float>();
        var tops = new List<float>();
        for (int i = 0; i < data.Count; i++)
        {
            Vector3 p = data.Center(i);
            float h = sampleHeight(p.X, p.Z);
            float half = cellSize / 2;
            float a = sampleHeight(p.X - half, p.Z), b = sampleHeight(p.X + half, p.Z);
            float c = sampleHeight(p.X, p.Z - half), d = sampleHeight(p.X, p.Z + half);
            bool valid = float.IsFinite(h) && float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(c) && float.IsFinite(d);
            // With floors, a box low enough to step onto (a kerb, a stair's first step) is ground at
            // its top, so the next step up is a step from it.
            if (valid && floors && obstacles != null)
                for (bool raised = true; raised;)
                {
                    raised = false;
                    foreach (NavigationObstacle obstacle in obstacles)
                        if (p.X >= obstacle.Min.X && p.X <= obstacle.Max.X && p.Z >= obstacle.Min.Z && p.Z <= obstacle.Max.Z
                            && obstacle.Max.Y > h + 1e-3f && obstacle.Max.Y <= h + maxStep && obstacle.Min.Y <= h + 1e-3f)
                        { h = obstacle.Max.Y; raised = true; }
                }
            data.Heights[i] = valid ? h : 0;
            float gradient = MathF.Sqrt((b - a) * (b - a) + (d - c) * (d - c)) / cellSize;
            bool walkable = valid && gradient <= gradientLimit;
            if (walkable && obstacles != null)
                foreach (NavigationObstacle obstacle in obstacles)
                    if (p.X + half + agentRadius >= obstacle.Min.X && p.X - half - agentRadius <= obstacle.Max.X
                        && p.Z + half + agentRadius >= obstacle.Min.Z && p.Z - half - agentRadius <= obstacle.Max.Z
                        && h + agentHeight > obstacle.Min.Y && h + maxStep < obstacle.Max.Y) { walkable = false; break; }
            data.Walkable[i] = walkable;
            if (ceilings == null) continue;

            // Headroom over the ground: the underside of the lowest box above it.
            float ceiling = NavMeshData.NoCeiling;
            tops.Clear();
            foreach (NavigationObstacle obstacle in obstacles!)
            {
                if (p.X < obstacle.Min.X || p.X > obstacle.Max.X || p.Z < obstacle.Min.Z || p.Z > obstacle.Max.Z) continue;
                if (obstacle.Min.Y >= h + maxStep) ceiling = MathF.Min(ceiling, obstacle.Min.Y);
                if (obstacle.Max.Y > h + maxStep) tops.Add(obstacle.Max.Y);
            }
            ceilings[i] = ceiling;
            // Each box top over the column is a floor, unless another box fills it or leaves no headroom.
            tops.Sort();
            float lastFloor = float.NegativeInfinity;
            foreach (float top in tops)
            {
                if (top - lastFloor < .05f) continue;
                float headroom = NavMeshData.NoCeiling;
                bool buried = false;
                foreach (NavigationObstacle other in obstacles!)
                {
                    if (p.X < other.Min.X || p.X > other.Max.X || p.Z < other.Min.Z || p.Z > other.Max.Z) continue;
                    if (other.Min.Y < top - 1e-3f && other.Max.Y > top + 1e-3f) { buried = true; break; }
                    if (other.Min.Y >= top - 1e-3f && other.Max.Y > top + 1e-3f) headroom = MathF.Min(headroom, other.Min.Y);
                }
                if (buried || headroom - top < agentHeight) continue;
                layerColumns.Add(i);
                layerHeights.Add(top);
                layerCeilings.Add(headroom);
                lastFloor = top;
            }
        }
        if (ceilings != null)
        {
            data.GroundCeilings = ceilings;
            data.LayerColumns = [.. layerColumns];
            data.LayerHeights = [.. layerHeights];
            data.LayerCeilings = [.. layerCeilings];
            data.Version = 2;
        }
        data.Validate();
        return data;
    }
}
