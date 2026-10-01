using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;

namespace Genesis.World.Terrain;

/// <summary>Where a terrain is being viewed from, in the terrain's own space.</summary>
public readonly struct TerrainLodView
{
    public TerrainLodView(Vector3 cameraLocal, in Matrix4x4 localViewProjection, float detail = 1f)
    {
        CameraLocal = cameraLocal;
        LocalViewProjection = localViewProjection;
        Detail = detail;
        HasFrustum = true;
    }

    /// <summary>Camera position in terrain space (before the Room places the terrain).</summary>
    public Vector3 CameraLocal { get; }

    /// <summary>Terrain space to clip space; used to skip nodes outside the view.</summary>
    public Matrix4x4 LocalViewProjection { get; }

    /// <summary>Detail multiplier: 1 is the default, 2 keeps each level twice as far out.</summary>
    public float Detail { get; }

    public bool HasFrustum { get; }

    /// <summary>A view with no frustum: every node is a candidate, detail by distance only.</summary>
    public static TerrainLodView From(Vector3 cameraLocal, float detail = 1f) => new(cameraLocal, detail);

    private TerrainLodView(Vector3 cameraLocal, float detail)
    {
        CameraLocal = cameraLocal;
        LocalViewProjection = Matrix4x4.Identity;
        Detail = detail;
        HasFrustum = false;
    }
}

/// <summary>Counts from the last selection, for diagnostics and tests.</summary>
public readonly record struct TerrainLodStatistics(
    int NodesDrawn, int TrianglesDrawn, int MeshesResident, int BuildsPending, int FinestLevelDrawn, int CoarsestLevelDrawn);

/// <summary>
/// Draws a large <see cref="TerrainAsset"/> as a quadtree of fixed-size grids whose spacing doubles
/// with distance, so an 8 km terrain costs about as much to draw as a small one.
/// </summary>
/// <remarks>
/// <para>
/// Every node is a <see cref="NodeCells"/> x <see cref="NodeCells"/> grid. A level-0 node samples
/// every terrain cell; each level above samples every second cell of the one below and covers twice
/// the ground. A node is replaced by its four children when the camera comes within
/// <see cref="SplitDistanceFactor"/> node widths, which keeps the on-screen triangle size roughly
/// constant.
/// </para>
/// <para>
/// Meshes are built on worker threads and uploaded a few per frame. A node only hands over to its
/// children once all four are uploaded, so the ground is never missing while detail streams in.
/// Neighbouring nodes of different levels do not share edge vertices; each node carries a skirt
/// that drops below its edge and hides the gap.
/// </para>
/// </remarks>
public sealed class TerrainLodGround : IDisposable
{
    /// <summary>Grid cells along one side of every node.</summary>
    public const int NodeCells = 64;

    /// <summary>A node splits when the camera is closer than this many node widths.</summary>
    public const float SplitDistanceFactor = 2.2f;

    /// <summary>Terrains with more cells than this along a side are drawn with level of detail.</summary>
    public const int MinimumCellsForLod = 1024;

    private const int MaximumConcurrentBuilds = 4;
    private const int MaximumUploadsPerFrame = 6;
    private const int MaximumBuildStartsPerFrame = 8;
    private const int IdleFramesBeforeRelease = 240;
    private const int ResidentMeshBudget = 500;

    private enum NodeState : byte { Empty, Building, Ready }

    private sealed class Node
    {
        public int Level, X, Z;          // X/Z in units of this level's node span
        public float MinY, MaxY;
        public NodeState State;
        public MeshHandle Mesh;
        public int TriangleCount;
        public int LastUsedFrame;
        public bool Dirty;               // heights changed while a mesh exists
        public int BuildGeneration;      // discards results from before an edit or release
        public Node[] Children;          // null until first needed; entries may be null (outside terrain)
        public float Priority;
    }

    private readonly struct BuiltMesh
    {
        public readonly Node Node; public readonly MeshVertex[] Vertices; public readonly ushort[] Indices; public readonly int Generation;
        public BuiltMesh(Node node, MeshVertex[] vertices, ushort[] indices, int generation)
        { Node = node; Vertices = vertices; Indices = indices; Generation = generation; }
    }

    private readonly TerrainAsset _asset;
    private readonly int _cellsX, _cellsZ, _topLevel;
    private readonly float[] _leafMin, _leafMax;   // min/max height per level-0 node
    private readonly int _leavesX, _leavesZ;
    private readonly Node _root;
    private readonly ConcurrentQueue<BuiltMesh> _built = new();
    private readonly List<Node> _wanted = new();
    private readonly List<Node> _drawn = new();
    private readonly List<Node> _resident = new();
    private readonly Dictionary<long, ushort[]> _indexCache = new();
    private readonly CancellationTokenSource _lifetime = new();
    private IRenderController _render;
    private float _uvScale = 6f;
    private int _frame;
    private int _activeBuilds;
    private bool _disposed;
    private bool _boundWithMaterial, _boundWithBiome;

    public TerrainLodGround(TerrainAsset asset)
    {
        _asset = asset ?? throw new ArgumentNullException(nameof(asset));
        _cellsX = Math.Max(1, asset.ResolutionX - 1);
        _cellsZ = Math.Max(1, asset.ResolutionZ - 1);
        int largest = Math.Max(_cellsX, _cellsZ);
        int level = 0;
        while ((NodeCells << level) < largest) level++;
        _topLevel = level;
        _leavesX = (_cellsX + NodeCells - 1) / NodeCells;
        _leavesZ = (_cellsZ + NodeCells - 1) / NodeCells;
        _leafMin = new float[_leavesX * _leavesZ];
        _leafMax = new float[_leavesX * _leavesZ];
        RefreshLeafBounds(0, 0, _leavesX - 1, _leavesZ - 1);
        _root = new Node { Level = _topLevel };
        (_root.MinY, _root.MaxY) = BoundsOf(_root);
    }

    /// <summary>True when a terrain is large enough that drawing every cell would be wasteful.</summary>
    public static bool Applies(TerrainAsset asset) =>
        asset != null && Math.Max(asset.ResolutionX, asset.ResolutionZ) - 1 > MinimumCellsForLod;

    public TerrainAsset Asset => _asset;
    public MeshDrawCall? SurfaceMaterial { get; set; }
    public bool BiomeShading { get; set; }
    public TextureHandle Albedo { get; set; } = TextureHandle.Invalid;
    public TerrainLodStatistics Statistics { get; private set; }

    /// <summary>Levels in the tree: 0 is full resolution.</summary>
    public int LevelCount => _topLevel + 1;

    /// <summary>The most draw calls one selection can produce; callers size their buffers with it.</summary>
    public int MaximumDrawCalls => 1024;

    public void Bind(IRenderController render, TextureHandle albedo, float uvScale)
    {
        if (!ReferenceEquals(_render, render)) ReleaseAll();
        _render = render;
        Albedo = albedo;
        // Vertex colours and UVs depend on these, so a change re-skins what is already resident.
        bool material = SurfaceMaterial.HasValue;
        if (MathF.Abs(_uvScale - uvScale) > 1e-6f || material != _boundWithMaterial || BiomeShading != _boundWithBiome)
        {
            _uvScale = uvScale;
            _boundWithMaterial = material;
            _boundWithBiome = BiomeShading;
            MarkAllDirty();
        }

        // The coarsest node is built at once so there is always ground to draw.
        if (_root.State != NodeState.Ready)
        {
            (MeshVertex[] vertices, ushort[] indices) = BuildMesh(_root);
            Install(_root, vertices, indices);
        }
    }

    /// <summary>Rebuilds every resident mesh (vertex colours depend on the material mode).</summary>
    public void Invalidate() => MarkAllDirty();

    /// <summary>Marks the nodes a height or paint edit can change, at every level.</summary>
    public void InvalidateRegion(int minX, int minZ, int maxX, int maxZ)
    {
        int leafMinX = Math.Clamp((minX - 1) / NodeCells, 0, _leavesX - 1);
        int leafMaxX = Math.Clamp((maxX + 1) / NodeCells, 0, _leavesX - 1);
        int leafMinZ = Math.Clamp((minZ - 1) / NodeCells, 0, _leavesZ - 1);
        int leafMaxZ = Math.Clamp((maxZ + 1) / NodeCells, 0, _leavesZ - 1);
        RefreshLeafBounds(leafMinX, leafMinZ, leafMaxX, leafMaxZ);
        MarkDirty(_root, minX, minZ, maxX, maxZ);
    }

    /// <summary>
    /// Chooses the nodes for a view, starts any builds they need and appends one draw per node.
    /// </summary>
    public void AppendDrawCalls(in TerrainLodView view, MeshDrawCall[] buffer, ref int count, MeshDrawFlags extraFlags)
    {
        if (_render == null || _disposed) return;
        _frame++;
        DrainBuilt();

        _wanted.Clear();
        _drawn.Clear();
        CameraFrustum frustum = view.HasFrustum ? new CameraFrustum(view.LocalViewProjection) : default;
        float detail = Math.Clamp(view.Detail <= 0f ? 1f : view.Detail, 0.25f, 8f);
        Select(_root, view.CameraLocal, view.HasFrustum, frustum, detail);

        int triangles = 0, finest = int.MaxValue, coarsest = -1;
        foreach (Node node in _drawn)
        {
            if (count >= buffer.Length) break;
            MeshDrawCall draw = SurfaceMaterial ?? new MeshDrawCall
            {
                Texture = Albedo,
                World = Matrix4x4.Identity,
                Tint = RenderColor.White,
                Alpha = 1f,
                Flags = MeshDrawFlags.TerrainGround,
            };
            draw.Mesh = node.Mesh;
            draw.Flags |= extraFlags;
            buffer[count++] = draw;
            triangles += node.TriangleCount;
            finest = Math.Min(finest, node.Level);
            coarsest = Math.Max(coarsest, node.Level);
        }

        StartBuilds();
        ReleaseIdle();
        Statistics = new TerrainLodStatistics(_drawn.Count, triangles, _resident.Count,
            _wanted.Count + _activeBuilds, finest == int.MaxValue ? -1 : finest, coarsest);
    }

    /// <summary>Blocks until every mesh the last selection asked for is uploaded (tests, captures).</summary>
    public void WaitUntilSettled(in TerrainLodView view, int timeoutMilliseconds = 30000)
    {
        MeshDrawCall[] scratch = new MeshDrawCall[MaximumDrawCalls];
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            int count = 0;
            AppendDrawCalls(view, scratch, ref count, MeshDrawFlags.None);
            if (_wanted.Count == 0 && Volatile.Read(ref _activeBuilds) == 0 && _built.IsEmpty) return;
            Thread.Sleep(2);
        }
    }

    public float SampleHeight(float worldX, float worldZ) => _asset.SampleHeight(worldX, worldZ);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        ReleaseAll();
        _render = null;
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private void Select(Node node, Vector3 camera, bool cull, in CameraFrustum frustum, float detail)
    {
        node.LastUsedFrame = _frame;
        (Vector3 min, Vector3 max) = WorldBounds(node);
        if (cull && !frustum.IntersectsAabb(min, max)) return;

        float span = (NodeCells << node.Level) * _asset.CellSize;
        bool split = node.Level > 0 && DistanceToBox(camera, min, max) < span * SplitDistanceFactor * detail;
        if (split)
        {
            Node[] children = EnsureChildren(node);
            bool ready = true;
            foreach (Node child in children)
            {
                if (child == null) continue;
                child.LastUsedFrame = _frame;
                if (child.State == NodeState.Ready && !child.Dirty) continue;
                if (child.State != NodeState.Ready) ready = false;
                Want(child, camera);
            }

            if (ready)
            {
                foreach (Node child in children)
                    if (child != null) Select(child, camera, cull, frustum, detail);
                return;
            }
        }

        if (node.Dirty) Want(node, camera);
        if (node.State == NodeState.Ready) _drawn.Add(node);
    }

    private void Want(Node node, Vector3 camera)
    {
        if (node.State == NodeState.Building) return;
        (Vector3 min, Vector3 max) = WorldBounds(node);
        node.Priority = DistanceToBox(camera, min, max);
        _wanted.Add(node);
    }

    private Node[] EnsureChildren(Node node)
    {
        if (node.Children != null) return node.Children;
        var children = new Node[4];
        int level = node.Level - 1;
        int span = NodeCells << level;
        for (int i = 0; i < 4; i++)
        {
            int x = node.X * 2 + (i & 1), z = node.Z * 2 + (i >> 1);
            if (x * span >= _cellsX || z * span >= _cellsZ) continue; // wholly outside the terrain
            var child = new Node { Level = level, X = x, Z = z };
            (child.MinY, child.MaxY) = BoundsOf(child);
            children[i] = child;
        }

        node.Children = children;
        return children;
    }

    private (Vector3 Min, Vector3 Max) WorldBounds(Node node)
    {
        int span = NodeCells << node.Level;
        int startX = node.X * span, startZ = node.Z * span;
        int endX = Math.Min(startX + span, _cellsX), endZ = Math.Min(startZ + span, _cellsZ);
        return (
            new Vector3(_asset.OriginX + startX * _asset.CellSize, node.MinY, _asset.OriginZ + startZ * _asset.CellSize),
            new Vector3(_asset.OriginX + endX * _asset.CellSize, node.MaxY, _asset.OriginZ + endZ * _asset.CellSize));
    }

    private static float DistanceToBox(Vector3 point, Vector3 min, Vector3 max)
    {
        Vector3 nearest = Vector3.Clamp(point, min, max);
        return Vector3.Distance(point, nearest);
    }

    // ── Bounds ────────────────────────────────────────────────────────────────

    private void RefreshLeafBounds(int minLeafX, int minLeafZ, int maxLeafX, int maxLeafZ)
    {
        ushort[] heights = _asset.HeightsData;
        int resX = _asset.ResolutionX, resZ = _asset.ResolutionZ;
        float range = _asset.MaxHeight - _asset.MinHeight;
        Parallel.For(minLeafZ, maxLeafZ + 1, leafZ =>
        {
            for (int leafX = minLeafX; leafX <= maxLeafX; leafX++)
            {
                int x0 = leafX * NodeCells, z0 = leafZ * NodeCells;
                int x1 = Math.Min(x0 + NodeCells, resX - 1), z1 = Math.Min(z0 + NodeCells, resZ - 1);
                ushort low = ushort.MaxValue, high = 0;
                for (int z = z0; z <= z1; z++)
                {
                    int row = z * resX;
                    for (int x = x0; x <= x1; x++)
                    {
                        ushort value = heights[row + x];
                        if (value < low) low = value;
                        if (value > high) high = value;
                    }
                }

                int index = leafZ * _leavesX + leafX;
                _leafMin[index] = _asset.MinHeight + low / (float)ushort.MaxValue * range;
                _leafMax[index] = _asset.MinHeight + high / (float)ushort.MaxValue * range;
            }
        });
    }

    private (float Min, float Max) BoundsOf(Node node)
    {
        int leaves = 1 << node.Level;
        int x0 = node.X * leaves, z0 = node.Z * leaves;
        int x1 = Math.Min(x0 + leaves, _leavesX) - 1, z1 = Math.Min(z0 + leaves, _leavesZ) - 1;
        float min = float.MaxValue, max = float.MinValue;
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            int index = z * _leavesX + x;
            min = MathF.Min(min, _leafMin[index]);
            max = MathF.Max(max, _leafMax[index]);
        }

        return min <= max ? (min, max) : (_asset.MinHeight, _asset.MaxHeight);
    }

    private void MarkDirty(Node node, int minX, int minZ, int maxX, int maxZ)
    {
        int span = NodeCells << node.Level;
        int stride = 1 << node.Level;
        int startX = node.X * span, startZ = node.Z * span;
        // Normals read one sample either side, at this level's spacing.
        if (startX - stride > maxX || startX + span + stride < minX
            || startZ - stride > maxZ || startZ + span + stride < minZ) return;
        (node.MinY, node.MaxY) = BoundsOf(node);
        if (node.State != NodeState.Empty) { node.Dirty = true; node.BuildGeneration++; }
        if (node.Children == null) return;
        foreach (Node child in node.Children)
            if (child != null) MarkDirty(child, minX, minZ, maxX, maxZ);
    }

    private void MarkAllDirty()
    {
        foreach (Node node in _resident) { node.Dirty = true; node.BuildGeneration++; }
    }

    // ── Building ──────────────────────────────────────────────────────────────

    private void StartBuilds()
    {
        if (_wanted.Count == 0) return;
        _wanted.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));
        int started = 0;
        foreach (Node node in _wanted)
        {
            if (started >= MaximumBuildStartsPerFrame || Volatile.Read(ref _activeBuilds) >= MaximumConcurrentBuilds) break;
            if (node.State == NodeState.Building) continue;
            bool refresh = node.State == NodeState.Ready;
            if (refresh && !node.Dirty) continue; // listed twice this frame; already started
            if (!refresh) node.State = NodeState.Building;
            node.Dirty = false;
            int generation = node.BuildGeneration;
            Interlocked.Increment(ref _activeBuilds);
            started++;
            CancellationToken token = _lifetime.Token;
            Task.Run(() =>
            {
                try
                {
                    if (token.IsCancellationRequested) return;
                    (MeshVertex[] vertices, ushort[] indices) = BuildMesh(node);
                    _built.Enqueue(new BuiltMesh(node, vertices, indices, generation));
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[Terrain] Level-of-detail mesh build failed: {exception.Message}");
                    _built.Enqueue(new BuiltMesh(node, null, null, generation));
                }
                finally { Interlocked.Decrement(ref _activeBuilds); }
            }, token);
        }
    }

    private void DrainBuilt()
    {
        int uploads = 0;
        while (uploads < MaximumUploadsPerFrame && _built.TryDequeue(out BuiltMesh built))
        {
            Node node = built.Node;
            if (_disposed || _render == null) continue;
            if (built.Vertices == null)
            {
                if (node.State == NodeState.Building) node.State = NodeState.Empty;
                continue;
            }

            if (built.Generation != node.BuildGeneration)
            {
                // The heights changed while this was building: keep what is shown and build again.
                if (node.State == NodeState.Building) node.State = NodeState.Empty;
                else if (node.State == NodeState.Ready) node.Dirty = true;
                continue;
            }

            Install(node, built.Vertices, built.Indices);
            uploads++;
        }
    }

    private void Install(Node node, MeshVertex[] vertices, ushort[] indices)
    {
        if (node.State == NodeState.Ready && node.Mesh.IsValid)
        {
            _render.UpdateMesh(node.Mesh, vertices);
        }
        else
        {
            node.Mesh = _render.RegisterMesh(vertices, indices);
            node.TriangleCount = indices.Length / 3;
            node.State = NodeState.Ready;
            node.LastUsedFrame = _frame;
            _resident.Add(node);
        }
    }

    private void ReleaseIdle()
    {
        if (_resident.Count <= ResidentMeshBudget / 2 && (_frame & 31) != 0) return;
        for (int i = _resident.Count - 1; i >= 0; i--)
        {
            Node node = _resident[i];
            bool over = _resident.Count > ResidentMeshBudget;
            if (ReferenceEquals(node, _root) || HasResidentChild(node)) continue;
            if (_frame - node.LastUsedFrame < (over ? 8 : IdleFramesBeforeRelease)) continue;
            Release(node);
            _resident.RemoveAt(i);
        }
    }

    private static bool HasResidentChild(Node node)
    {
        if (node.Children == null) return false;
        foreach (Node child in node.Children)
            if (child != null && child.State != NodeState.Empty) return true;
        return false;
    }

    private void Release(Node node)
    {
        if (node.Mesh.IsValid) _render?.ReleaseMesh(node.Mesh);
        node.Mesh = default;
        node.State = NodeState.Empty;
        node.Dirty = false;
        node.BuildGeneration++;
        node.Children = HasResidentChild(node) ? node.Children : null;
    }

    private void ReleaseAll()
    {
        foreach (Node node in _resident)
        {
            if (node.Mesh.IsValid) _render?.ReleaseMesh(node.Mesh);
            node.Mesh = default;
            node.State = NodeState.Empty;
            node.Dirty = false;
            node.BuildGeneration++;
        }

        _resident.Clear();
        while (_built.TryDequeue(out _)) { }
        _root.Children = null;
    }

    /// <summary>Builds one node's grid and skirt. Safe to call from a worker thread.</summary>
    private (MeshVertex[] Vertices, ushort[] Indices) BuildMesh(Node node)
    {
        TerrainAsset asset = _asset;
        int stride = 1 << node.Level;
        int span = NodeCells << node.Level;
        int startX = node.X * span, startZ = node.Z * span;
        int lastX = Math.Min(startX + span, _cellsX), lastZ = Math.Min(startZ + span, _cellsZ);
        int cellsX = (lastX - startX + stride - 1) / stride, cellsZ = (lastZ - startZ + stride - 1) / stride;
        int pointsX = cellsX + 1, pointsZ = cellsZ + 1;
        int gridVertices = pointsX * pointsZ;
        int skirtVertices = 2 * (pointsX + pointsZ);
        var vertices = new MeshVertex[gridVertices + skirtVertices];

        bool material = SurfaceMaterial.HasValue;
        bool biome = BiomeShading;
        float cell = asset.CellSize;
        float invX = _uvScale / Math.Max(1, asset.ResolutionX - 1), invZ = _uvScale / Math.Max(1, asset.ResolutionZ - 1);
        float heightRange = Math.Max(0.0001f, asset.MaxHeight - asset.MinHeight);
        int maxX = asset.ResolutionX - 1, maxZ = asset.ResolutionZ - 1;

        for (int localZ = 0; localZ < pointsZ; localZ++)
        {
            int z = Math.Min(startZ + localZ * stride, lastZ);
            for (int localX = 0; localX < pointsX; localX++)
            {
                int x = Math.Min(startX + localX * stride, lastX);
                float y = asset.GetHeight(x, z);
                float left = asset.GetHeight(Math.Max(x - stride, 0), z);
                float right = asset.GetHeight(Math.Min(x + stride, maxX), z);
                float north = asset.GetHeight(x, Math.Max(z - stride, 0));
                float south = asset.GetHeight(x, Math.Min(z + stride, maxZ));
                Vector4 colour;
                if (material) colour = Vector4.One;
                else if (biome) colour = new Vector4(Math.Clamp((y - asset.MinHeight) / heightRange, 0f, 1f), 0f, 0f, 0f);
                else
                {
                    (byte r, byte g, byte b, byte a) = asset.GetSplat(x, z);
                    colour = new Vector4(r, g, b, a) * (1f / 255f);
                    float weight = colour.X + colour.Y + colour.Z + colour.W;
                    colour = weight > 0.0001f ? colour / weight : new Vector4(1f, 0f, 0f, 0f);
                }

                vertices[localZ * pointsX + localX] = new MeshVertex
                {
                    Position = new Vector3(asset.OriginX + x * cell, y, asset.OriginZ + z * cell),
                    Normal = Vector3.Normalize(new Vector3(left - right, cell * 2f * stride, north - south)),
                    Color = colour,
                    UV = new Vector2(x * invX, z * invZ),
                };
            }
        }

        // Skirt: a copy of each edge vertex dropped far enough to cover the gap to a coarser
        // neighbour, whose edge is a straight line between every second vertex of this one.
        float drop = MathF.Max(0.5f, cell * stride * 1.5f);
        int next = gridVertices;
        int skirtNorth = next; for (int x = 0; x < pointsX; x++) vertices[next++] = Lowered(vertices[x], drop);
        int skirtSouth = next; for (int x = 0; x < pointsX; x++) vertices[next++] = Lowered(vertices[(pointsZ - 1) * pointsX + x], drop);
        int skirtWest = next; for (int z = 0; z < pointsZ; z++) vertices[next++] = Lowered(vertices[z * pointsX], drop);
        int skirtEast = next; for (int z = 0; z < pointsZ; z++) vertices[next++] = Lowered(vertices[z * pointsX + pointsX - 1], drop);

        long key = ((long)pointsX << 32) | (uint)pointsZ;
        ushort[] indices;
        lock (_indexCache)
        {
            if (!_indexCache.TryGetValue(key, out indices))
            {
                indices = BuildIndices(cellsX, cellsZ, skirtNorth, skirtSouth, skirtWest, skirtEast);
                _indexCache[key] = indices;
            }
        }

        return (vertices, indices);
    }

    private static MeshVertex Lowered(MeshVertex vertex, float drop)
    {
        vertex.Position.Y -= drop;
        return vertex;
    }

    private static ushort[] BuildIndices(int cellsX, int cellsZ, int skirtNorth, int skirtSouth, int skirtWest, int skirtEast)
    {
        int pointsX = cellsX + 1, pointsZ = cellsZ + 1;
        var indices = new ushort[(cellsX * cellsZ + 2 * (cellsX + cellsZ)) * 6];
        int at = 0;
        for (int z = 0; z < cellsZ; z++)
        {
            for (int x = 0; x < cellsX; x++)
            {
                int i00 = z * pointsX + x, i10 = i00 + 1, i01 = i00 + pointsX, i11 = i01 + 1;
                indices[at++] = (ushort)i00; indices[at++] = (ushort)i01; indices[at++] = (ushort)i10;
                indices[at++] = (ushort)i10; indices[at++] = (ushort)i01; indices[at++] = (ushort)i11;
            }
        }

        // Each skirt quad faces away from the node, matching the ground's winding.
        void Quad(int a, int b, int lowA, int lowB)
        {
            indices[at++] = (ushort)a; indices[at++] = (ushort)b; indices[at++] = (ushort)lowA;
            indices[at++] = (ushort)b; indices[at++] = (ushort)lowB; indices[at++] = (ushort)lowA;
        }

        for (int x = 0; x < cellsX; x++)
        {
            Quad(x, x + 1, skirtNorth + x, skirtNorth + x + 1);                               // low-z edge, faces -z
            int top = (pointsZ - 1) * pointsX + x;
            Quad(top + 1, top, skirtSouth + x + 1, skirtSouth + x);                           // high-z edge, faces +z
        }

        for (int z = 0; z < cellsZ; z++)
        {
            int west = z * pointsX, east = z * pointsX + pointsX - 1;
            Quad(west + pointsX, west, skirtWest + z + 1, skirtWest + z);                     // low-x edge, faces -x
            Quad(east, east + pointsX, skirtEast + z, skirtEast + z + 1);                     // high-x edge, faces +x
        }

        return indices;
    }
}
