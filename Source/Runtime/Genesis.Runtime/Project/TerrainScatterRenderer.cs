using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Geometry;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Runtime.Project;

/// <summary>What the scatter drew in the last frame, for tests and performance logs.</summary>
public readonly record struct TerrainScatterStatistics(
    int Layers,
    int CellsWithCopies,
    int CopiesPlaced,
    int PendingWork,
    int NearCopies,
    int ShadowCopies,
    int DroppedCopies,
    int MergedDrawCalls,
    int MergedTriangles,
    long MergedBytes);

/// <summary>
/// Draws a terrain's scatter layers: forests, rocks and undergrowth by the hundred thousand.
/// </summary>
/// <remarks>
/// <para>
/// The terrain is divided into square cells. Where each layer's copies stand is worked out per
/// cell on worker threads from the layer's rule, so nothing is stored and nothing is loaded up
/// front. Each cell is then drawn in the cheapest form that looks right from where the camera is:
/// </para>
/// <list type="bullet">
/// <item>Near: every copy is its own instance, at the level of detail its size on screen calls
/// for, casting shadows close to the camera.</item>
/// <item>Middle distance: the whole cell is one merged mesh of the model's coarsest level.</item>
/// <item>Far: one merged mesh of a few triangles per copy, enough to keep a forest's outline and
/// colour on a distant hillside.</item>
/// </list>
/// <para>
/// Merged meshes are built on workers and uploaded a few megabytes a frame; ones not seen for a
/// while are released, so memory follows what is in view rather than the size of the world.
/// </para>
/// </remarks>
internal sealed class TerrainScatterRenderer : IDisposable
{
    private const int Mid = 0, Far = 1;

    private sealed class Part
    {
        public MeshDrawCall Template;
        public MeshVertex[] Vertices;
        public ushort[] Indices;
    }

    /// <summary>One copy's geometry for one merged tier, grouped by the texture it draws with.</summary>
    private sealed class ProxySource
    {
        public Part[] Groups;
        public int Triangles;
    }

    /// <summary>
    /// A merged mesh waiting to be uploaded. Its arrays are rented: megabytes of new arrays for
    /// every cell that comes into view would otherwise swing the heap by a gigabyte.
    /// </summary>
    private sealed class BuiltProxy
    {
        public int Group;
        public MeshVertex[] Vertices;
        public int VertexCount;
        public ushort[] Indices;
        public int IndexCount;
    }

    private sealed class ProxyMesh
    {
        public MeshHandle Handle;
        public MeshDrawCall Template;
        public int Triangles;
        public int Bytes;
    }

    private sealed class Generated
    {
        public TerrainScatterInstance[] Instances;
        public Matrix4x4[] Matrices;
        public Vector3 Min, Max;
    }

    private sealed class Cell
    {
        public int X, Z;
        public Task<Generated> Generating;
        public Generated Content;
        public readonly Task<BuiltProxy[]>[] Building = new Task<BuiltProxy[]>[2];
        public readonly List<ProxyMesh>[] Proxies = new List<ProxyMesh>[2];
        public readonly double[] LastUsed = new double[2];
    }

    private sealed class LayerState
    {
        public TerrainScatterLayer Layer;
        public GModelAsset Asset;
        public float Radius;
        public Vector3 Centre;
        public Task<ProxySource[]> SourceBuild;
        public ProxySource[] Sources;
        public bool SourcesFailed;
        public readonly Dictionary<long, Cell> Cells = new();
        public readonly List<(Cell Cell, float Distance)> NearCells = new();
        public readonly List<MeshInstanceData>[] Lit = NewLists();
        public readonly List<MeshInstanceData>[] Shadowed = NewLists();

        private static List<MeshInstanceData>[] NewLists()
        {
            var lists = new List<MeshInstanceData>[4];
            for (int i = 0; i < lists.Length; i++) lists[i] = new List<MeshInstanceData>();
            return lists;
        }
    }

    private readonly string _projectPath;
    private readonly TerrainAsset _terrain;
    private readonly List<LayerState> _layers = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(LayerState Layer, Cell Cell, float Distance)> _wanted = new();
    private readonly List<ModelGpuCache.CachedMesh> _drawnScratch = new();
    private MeshDrawCall[] _templateScratch = new MeshDrawCall[16];
    private MeshInstanceData[] _instanceScratch = Array.Empty<MeshInstanceData>();
    private IRenderController _renderer;
    private int _generating, _building;
    private double _lastSweep;
    private long _mergedBytes;
    private readonly int _cellsX, _cellsZ;

    public TerrainScatterRenderer(string projectPath, TerrainAsset terrain, IEnumerable<TerrainScatterLayer> layers)
    {
        _projectPath = projectPath;
        _terrain = terrain;
        (_cellsX, _cellsZ) = TerrainScatterPlacement.CellCount(terrain);
        foreach (TerrainScatterLayer layer in layers)
        {
            if (layer == null || !layer.Enabled || string.IsNullOrWhiteSpace(layer.Model) || layer.DensityPerHectare <= 0f) continue;
            _layers.Add(new LayerState { Layer = layer });
            // Read the model now, while the room is loading, rather than in the first frame that draws it.
            try { ObjectDrawPass.Models.LoadAsset(projectPath, layer.Model); }
            catch (Exception exception) when (exception is System.IO.IOException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Console.WriteLine($"[Terrain] Scatter model '{layer.Model}' could not be loaded: {exception.Message}");
            }
        }
    }

    /// <summary>Cells nearer than this draw every copy as its own instance.</summary>
    public float NearDistance { get; set; } = 300f;

    /// <summary>Beyond this, cells draw as the few-triangle far tier.</summary>
    public float MidDistance { get; set; } = 1300f;

    /// <summary>Reach of a layer that sets no draw distance of its own.</summary>
    public float MaximumDistance { get; set; } = 12000f;

    /// <summary>Copies nearer than this cast shadows.</summary>
    public float ShadowDistance { get; set; } = 110f;

    /// <summary>Most instances submitted in one frame; the renderer's shared buffer holds 32,768.</summary>
    public int InstanceBudget { get; set; } = 22000;

    /// <summary>Most merged-mesh bytes uploaded in one frame.</summary>
    public int UploadBytesPerFrame { get; set; } = 8 * 1024 * 1024;

    /// <summary>Seconds a merged mesh may go unseen before it is released.</summary>
    public double IdleSeconds { get; set; } = 25.0;

    private double _phaseModels, _phaseCells, _phaseNear, _phaseInstances;

    /// <summary>Where the last frame's time went, for the slow-frame report.</summary>
    public string DescribeLastPhases() =>
        $"models {_phaseModels:F0} ms, cells and merged meshes {_phaseCells:F0} ms, choosing copies {_phaseNear:F0} ms, "
        + $"submitting instances {_phaseInstances:F0} ms";

    private static double Lap(ref long start)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(start, now).TotalMilliseconds;
        start = now;
        return elapsed;
    }

    public int LayerCount => _layers.Count;
    public TerrainScatterStatistics Statistics { get; private set; }

    /// <summary>True while cells or merged meshes the last frame wanted are still being made.</summary>
    public bool Busy { get; private set; }

    /// <summary>Draw calls to reserve for merged meshes.</summary>
    public int MaximumDrawCalls => _layers.Count == 0 ? 0 : 4096;

    public void Submit(IRenderController renderer, Vector3 camera, in Matrix4x4 viewProjection, in Matrix4x4 placement,
        MeshDrawCall[] buffer, ref int count)
    {
        if (_layers.Count == 0 || renderer == null) return;
        if (!ReferenceEquals(_renderer, renderer))
        {
            ReleaseMeshes();
            _renderer = renderer;
        }

        Vector3 cameraLocal = Matrix4x4.Invert(placement, out Matrix4x4 inverse) ? Vector3.Transform(camera, inverse) : camera;
        var frustum = new Frustum(placement * viewProjection);
        double now = _clock.Elapsed.TotalSeconds;
        int uploadBudget = UploadBytesPerFrame;
        int instanceBudget = InstanceBudget;
        int cellsWithCopies = 0, copies = 0, near = 0, shadow = 0, dropped = 0, mergedDraws = 0, mergedTriangles = 0;
        bool waiting = false;
        _wanted.Clear();
        float projectionScale = ModelLodView.Active ? ModelLodView.ProjectionScale : 1.73f;

        long phaseStart = Stopwatch.GetTimestamp();
        double modelMs = 0, cellMs = 0, nearMs = 0, instanceMs = 0;
        foreach (LayerState state in _layers)
        {
            phaseStart = Stopwatch.GetTimestamp();
            if (!EnsureModel(state)) continue;
            float reach = state.Layer.DrawDistance > 0f ? state.Layer.DrawDistance : MaximumDistance;
            bool merged = reach > NearDistance;
            if (merged && state.Sources == null && !state.SourcesFailed) waiting |= !EnsureSources(state, renderer);

            float originX = cameraLocal.X - _terrain.OriginX, originZ = cameraLocal.Z - _terrain.OriginZ;
            int x0 = Math.Max(0, (int)MathF.Floor((originX - reach) / TerrainScatterPlacement.CellSize));
            int x1 = Math.Min(_cellsX - 1, (int)MathF.Floor((originX + reach) / TerrainScatterPlacement.CellSize));
            int z0 = Math.Max(0, (int)MathF.Floor((originZ - reach) / TerrainScatterPlacement.CellSize));
            int z1 = Math.Min(_cellsZ - 1, (int)MathF.Floor((originZ + reach) / TerrainScatterPlacement.CellSize));
            state.NearCells.Clear();
            modelMs += Lap(ref phaseStart);

            for (int cz = z0; cz <= z1; cz++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    float minX = cx * TerrainScatterPlacement.CellSize, minZ = cz * TerrainScatterPlacement.CellSize;
                    float dx = MathF.Max(0f, MathF.Max(minX - originX, originX - (minX + TerrainScatterPlacement.CellSize)));
                    float dz = MathF.Max(0f, MathF.Max(minZ - originZ, originZ - (minZ + TerrainScatterPlacement.CellSize)));
                    float distance = MathF.Sqrt(dx * dx + dz * dz);
                    if (distance > reach) continue;

                    long key = ((long)cz << 32) | (uint)cx;
                    if (!state.Cells.TryGetValue(key, out Cell cell))
                        state.Cells[key] = cell = new Cell { X = cx, Z = cz };
                    if (cell.Content == null)
                    {
                        if (cell.Generating is { IsCompleted: true })
                        {
                            cell.Content = cell.Generating.IsCompletedSuccessfully ? cell.Generating.Result : new Generated
                            {
                                Instances = Array.Empty<TerrainScatterInstance>(), Matrices = Array.Empty<Matrix4x4>(),
                            };
                            cell.Generating = null;
                        }
                        else
                        {
                            if (cell.Generating == null) _wanted.Add((state, cell, distance));
                            waiting = true;
                            continue;
                        }
                    }

                    int population = cell.Content.Instances.Length;
                    if (population == 0) continue;
                    cellsWithCopies++;
                    copies += population;
                    bool visible = frustum.IntersectsAabb(cell.Content.Min, cell.Content.Max);
                    if (distance < NearDistance || !merged)
                    {
                        // A cell behind the camera can still throw shadows into view.
                        if (visible || distance < ShadowDistance) state.NearCells.Add((cell, distance));
                        continue;
                    }

                    if (!visible) continue;
                    int tier = distance < MidDistance ? Mid : Far;
                    waiting |= !DrawMerged(state, cell, tier, placement, buffer, ref count, now, ref uploadBudget,
                        ref mergedDraws, ref mergedTriangles);
                }
            }

            cellMs += Lap(ref phaseStart);
            state.NearCells.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            foreach ((Cell cell, float _) in state.NearCells)
            {
                Generated content = cell.Content;
                float radius = state.Radius;
                for (int i = 0; i < content.Instances.Length; i++)
                {
                    ref readonly TerrainScatterInstance instance = ref content.Instances[i];
                    Vector3 centre = instance.Position + state.Centre * instance.Scale;
                    float size = radius * instance.Scale;
                    float distance = MathF.Max(Vector3.Distance(centre, cameraLocal), size);
                    if (state.Layer.DrawDistance > 0f && distance > state.Layer.DrawDistance) continue;
                    int level = ModelLodView.LevelForSize(size * projectionScale / distance);
                    if (level < 0) continue;
                    bool casts = state.Layer.CastShadows && distance < ShadowDistance;
                    if (!casts && !frustum.ContainsSphere(centre, size)) continue;
                    if (instanceBudget <= 0) { dropped++; continue; }
                    instanceBudget--;
                    float tone = instance.Tone;
                    var tint = new RenderColor(1f + tone * 0.07f, 1f - MathF.Abs(tone) * 0.04f, 1f - tone * 0.09f, 1f);
                    (casts ? state.Shadowed : state.Lit)[level].Add(new MeshInstanceData(content.Matrices[i] * placement, tint));
                    near++;
                    if (casts) shadow++;
                }
            }

            nearMs += Lap(ref phaseStart);
            for (int level = 0; level < 4; level++)
            {
                SubmitInstances(state, renderer, level, true, state.Shadowed[level]);
                SubmitInstances(state, renderer, level, false, state.Lit[level]);
                state.Shadowed[level].Clear();
                state.Lit[level].Clear();
            }

            instanceMs += Lap(ref phaseStart);
        }

        _phaseModels = modelMs;
        _phaseCells = cellMs;
        _phaseNear = nearMs;
        _phaseInstances = instanceMs;

        // Nearest first, a few at a time, so workers are not swamped when a new view opens.
        if (_wanted.Count > 0 && _generating < 8)
        {
            _wanted.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            foreach ((LayerState state, Cell cell, float _) in _wanted)
            {
                if (_generating >= 8) break;
                TerrainAsset terrain = _terrain;
                TerrainScatterLayer layer = state.Layer;
                float radius = state.Radius;
                int cellX = cell.X, cellZ = cell.Z;
                Interlocked.Increment(ref _generating);
                cell.Generating = Task.Run(() =>
                {
                    try { return Generate(terrain, layer, cellX, cellZ, radius); }
                    finally { Interlocked.Decrement(ref _generating); }
                });
            }
        }

        if (now - _lastSweep > 2.0)
        {
            _lastSweep = now;
            ReleaseIdle(now);
        }

        Busy = waiting || _generating > 0 || _building > 0;
        Statistics = new TerrainScatterStatistics(_layers.Count, cellsWithCopies, copies, _generating + _building,
            near, shadow, dropped, mergedDraws, mergedTriangles, _mergedBytes);
    }

    private static Generated Generate(TerrainAsset terrain, TerrainScatterLayer layer, int cellX, int cellZ, float radius)
    {
        TerrainScatterInstance[] instances = TerrainScatterPlacement.Generate(terrain, layer, cellX, cellZ);
        var matrices = new Matrix4x4[instances.Length];
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < instances.Length; i++)
        {
            matrices[i] = instances[i].ToMatrix();
            Vector3 reach = new(radius * instances[i].Scale * 2f);
            min = Vector3.Min(min, instances[i].Position - reach);
            max = Vector3.Max(max, instances[i].Position + reach);
        }

        return new Generated { Instances = instances, Matrices = matrices, Min = min, Max = max };
    }

    private bool EnsureModel(LayerState state)
    {
        GModelAsset asset = ObjectDrawPass.Models.LoadAsset(_projectPath, state.Layer.Model);
        if (asset == null || !asset.HasRenderableMeshes || asset.ImportRequired) return false;
        if (ReferenceEquals(asset, state.Asset)) return true;

        // The model changed on disk: everything made from the old one is stale.
        if (state.Asset != null)
        {
            ReleaseLayerMeshes(state);
            state.Cells.Clear();
        }

        state.Asset = asset;
        state.Sources = null;
        state.SourceBuild = null;
        state.SourcesFailed = false;
        Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
        Vector3 min = (asset.Bounds?.Min ?? new Vector3(-0.5f)) - pivot, max = (asset.Bounds?.Max ?? new Vector3(0.5f)) - pivot;
        state.Centre = (min + max) * 0.5f;
        state.Radius = MathF.Max(0.01f, Vector3.Distance(min, max) * 0.5f);
        return true;
    }

    private void SubmitInstances(LayerState state, IRenderController renderer, int level, bool castShadows, List<MeshInstanceData> instances)
    {
        if (instances.Count == 0) return;
        ModelRenderQueue queue = ModelRenderQueue.Rent();
        if (!ObjectDrawPass.Models.EnqueueAtLevel(queue, _projectPath, state.Layer.Model, level, castShadows, renderer))
            return;
        if (_templateScratch.Length < queue.Count) _templateScratch = new MeshDrawCall[queue.Count * 2];
        int templates = queue.CopyTo(_templateScratch, 0);
        Span<MeshInstanceData> source = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(instances);
        for (int t = 0; t < templates; t++)
        {
            MeshDrawCall template = _templateScratch[t];
            if (!castShadows) template.Flags |= MeshDrawFlags.NoShadow;
            RenderColor tint = template.Tint;
            bool plain = template.World.IsIdentity && tint.R == 1f && tint.G == 1f && tint.B == 1f;
            if (plain)
            {
                renderer.DrawMeshInstances(template, source);
                continue;
            }

            if (_instanceScratch.Length < source.Length) _instanceScratch = new MeshInstanceData[Math.Max(source.Length, _instanceScratch.Length * 2)];
            for (int i = 0; i < source.Length; i++)
            {
                RenderColor tone = source[i].Tint;
                _instanceScratch[i] = new MeshInstanceData(template.World * source[i].World,
                    new RenderColor(tint.R * tone.R, tint.G * tone.G, tint.B * tone.B, template.Alpha));
            }

            renderer.DrawMeshInstances(template, _instanceScratch.AsSpan(0, source.Length));
        }
    }

    /// <summary>Starts, or collects, the per-copy geometry the merged tiers are made from.</summary>
    private bool EnsureSources(LayerState state, IRenderController renderer)
    {
        if (state.SourceBuild != null)
        {
            if (!state.SourceBuild.IsCompleted) return false;
            if (state.SourceBuild.IsCompletedSuccessfully) state.Sources = state.SourceBuild.Result;
            else state.SourcesFailed = true;
            state.SourceBuild = null;
            return true;
        }

        ModelRenderQueue queue = ModelRenderQueue.Rent();
        _drawnScratch.Clear();
        if (!ObjectDrawPass.Models.EnqueueAtLevel(queue, _projectPath, state.Layer.Model, 0, false, renderer, _drawnScratch))
        {
            state.SourcesFailed = true;
            return true;
        }

        if (_templateScratch.Length < queue.Count) _templateScratch = new MeshDrawCall[queue.Count * 2];
        int templates = queue.CopyTo(_templateScratch, 0);
        var parts = new List<Part>();
        for (int i = 0; i < templates && i < _drawnScratch.Count; i++)
        {
            ModelGpuCache.CachedMesh cached = _drawnScratch[i];
            if (cached.IsSkinned || cached.SourceIndex < 0 || cached.SourceIndex >= state.Asset.Meshes.Count) continue;
            GModelMesh mesh = state.Asset.Meshes[cached.SourceIndex];
            if (mesh?.Vertices == null || mesh.Vertices.Length == 0) continue;
            foreach ((int material, ushort[] indices) in ModelGpuCache.MaterialBatches(mesh))
            {
                if (material != cached.MaterialIndex) continue;
                parts.Add(new Part { Template = _templateScratch[i], Vertices = mesh.Vertices, Indices = indices });
                break;
            }
        }

        if (parts.Count == 0)
        {
            state.SourcesFailed = true;
            return true;
        }

        state.SourceBuild = Task.Run(() => BuildSources(parts));
        return false;
    }

    private const int MidTrianglesPerPart = 220;
    private const int FarTrianglesPerPart = 14;

    private static ProxySource[] BuildSources(List<Part> parts)
    {
        var mid = new List<Part>();
        var far = new List<Part>();
        foreach (Part part in parts)
        {
            var positions = new Vector3[part.Vertices.Length];
            for (int i = 0; i < positions.Length; i++) positions[i] = part.Vertices[i].Position;
            int[] full = Array.ConvertAll(part.Indices, index => (int)index);

            // The same steps the automatic levels take, ending at the coarsest that still holds
            // the model's shape within its error limits.
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (Vector3 position in positions) { min = Vector3.Min(min, position); max = Vector3.Max(max, position); }
            float size = positions.Length == 0 ? 0f : Vector3.Distance(min, max) * 0.5f;
            int[] coarse = full;
            for (int level = 0; level < ModelGpuCache.AutoLodRatios.Length; level++)
            {
                int wanted = Math.Max(8, (int)(full.Length / 3 * ModelGpuCache.AutoLodRatios[level]));
                int[] simplified = MeshSimplifier.Simplify(positions, coarse, wanted, size * ModelGpuCache.AutoLodErrors[level]);
                if (simplified.Length >= 12) coarse = simplified;
            }

            if (coarse.Length / 3 > MidTrianglesPerPart)
                coarse = MeshSimplifier.Simplify(positions, coarse, MidTrianglesPerPart);
            mid.Add(Compact(part, coarse));

            int[] tiny = MeshSimplifier.Simplify(positions, coarse, FarTrianglesPerPart);
            far.Add(tiny.Length / 3 <= FarTrianglesPerPart * 3 && tiny.Length >= 12 ? Compact(part, tiny) : Hull(part));
        }

        return new[] { Group(mid), Group(far) };
    }

    private static Part Compact(Part part, int[] indices)
    {
        MeshSimplifier.Compact<MeshVertex>(part.Vertices, indices, out MeshVertex[] vertices, out ushort[] compact);
        return new Part { Template = part.Template, Vertices = vertices, Indices = compact };
    }

    /// <summary>
    /// A double pyramid the size of the part, in its average colour: the stand-in for geometry the
    /// simplifier cannot reduce (loose cards, for instance), so no model can make the far tier heavy.
    /// </summary>
    private static Part Hull(Part part)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        Vector4 colour = Vector4.Zero;
        Vector2 uv = Vector2.Zero;
        foreach (MeshVertex vertex in part.Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
            colour += vertex.Color;
            uv += vertex.UV;
        }

        int count = Math.Max(1, part.Vertices.Length);
        colour /= count;
        uv /= count;
        Vector3 centre = (min + max) * 0.5f, half = (max - min) * 0.5f;
        float waist = min.Y + (max.Y - min.Y) * 0.4f;
        float reach = 0.8f;
        Vector3[] corners =
        {
            new(centre.X, max.Y, centre.Z), new(centre.X, min.Y, centre.Z),
            new(centre.X + half.X * reach, waist, centre.Z), new(centre.X, waist, centre.Z + half.Z * reach),
            new(centre.X - half.X * reach, waist, centre.Z), new(centre.X, waist, centre.Z - half.Z * reach),
        };
        int[] faces = { 0, 3, 2, 0, 4, 3, 0, 5, 4, 0, 2, 5, 1, 2, 3, 1, 3, 4, 1, 4, 5, 1, 5, 2 };
        var vertices = new MeshVertex[faces.Length];
        var indices = new ushort[faces.Length];
        for (int i = 0; i < faces.Length; i += 3)
        {
            Vector3 a = corners[faces[i]], b = corners[faces[i + 1]], c = corners[faces[i + 2]];
            Vector3 normal = Vector3.Cross(b - a, c - a);
            normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            for (int k = 0; k < 3; k++)
            {
                vertices[i + k] = new MeshVertex { Position = corners[faces[i + k]], Normal = normal, Color = colour, UV = uv };
                indices[i + k] = (ushort)(i + k);
            }
        }

        return new Part { Template = part.Template, Vertices = vertices, Indices = indices };
    }

    /// <summary>
    /// Joins parts that draw with the same texture into one, with each part's tint and pivot
    /// baked into its vertices, so a copy costs one piece of geometry per texture.
    /// </summary>
    private static ProxySource Group(List<Part> parts)
    {
        var groups = new List<(int Texture, List<MeshVertex> Vertices, List<ushort> Indices, MeshDrawCall Template)>();
        int triangles = 0;
        foreach (Part part in parts)
        {
            int slot = groups.FindIndex(group => group.Texture == part.Template.Texture.Id
                && group.Vertices.Count + part.Vertices.Length <= ushort.MaxValue);
            if (slot < 0)
            {
                groups.Add((part.Template.Texture.Id, new List<MeshVertex>(), new List<ushort>(), part.Template));
                slot = groups.Count - 1;
            }

            (int _, List<MeshVertex> vertices, List<ushort> indices, MeshDrawCall _) = groups[slot];
            int start = vertices.Count;
            RenderColor tint = part.Template.Tint;
            Matrix4x4 world = part.Template.World;
            foreach (MeshVertex vertex in part.Vertices)
            {
                MeshVertex placed = vertex;
                placed.Position = Vector3.Transform(vertex.Position, world);
                placed.Color = new Vector4(vertex.Color.X * tint.R, vertex.Color.Y * tint.G, vertex.Color.Z * tint.B, vertex.Color.W);
                vertices.Add(placed);
            }

            foreach (ushort index in part.Indices) indices.Add((ushort)(start + index));
            triangles += part.Indices.Length / 3;
        }

        var result = new Part[groups.Count];
        for (int i = 0; i < result.Length; i++)
        {
            MeshDrawCall template = groups[i].Template;
            template.World = Matrix4x4.Identity;
            template.Tint = RenderColor.White;
            template.Flags |= MeshDrawFlags.NoShadow;
            template.SkinPalette = SkinPaletteHandle.Invalid;
            result[i] = new Part { Template = template, Vertices = groups[i].Vertices.ToArray(), Indices = groups[i].Indices.ToArray() };
        }

        return new ProxySource { Groups = result, Triangles = triangles };
    }

    /// <returns>False while the cell's merged mesh for this tier is still on its way.</returns>
    private bool DrawMerged(LayerState state, Cell cell, int tier, in Matrix4x4 placement, MeshDrawCall[] buffer, ref int count,
        double now, ref int uploadBudget, ref int draws, ref int triangles)
    {
        if (state.Sources == null) return state.SourcesFailed;
        bool ready = cell.Proxies[tier] != null;
        if (!ready)
        {
            Task<BuiltProxy[]> build = cell.Building[tier];
            if (build == null)
            {
                if (_building < 6)
                {
                    ProxySource source = state.Sources[tier];
                    Generated content = cell.Content;
                    Interlocked.Increment(ref _building);
                    cell.Building[tier] = Task.Run(() =>
                    {
                        try { return Merge(source, content); }
                        finally { Interlocked.Decrement(ref _building); }
                    });
                }
            }
            else if (build.IsCompleted && uploadBudget > 0)
            {
                cell.Building[tier] = null;
                var meshes = new List<ProxyMesh>();
                if (build.IsCompletedSuccessfully)
                {
                    foreach (BuiltProxy built in build.Result)
                    {
                        MeshHandle handle = _renderer.RegisterMesh(
                            built.Vertices.AsSpan(0, built.VertexCount), built.Indices.AsSpan(0, built.IndexCount));
                        ArrayPool<MeshVertex>.Shared.Return(built.Vertices);
                        ArrayPool<ushort>.Shared.Return(built.Indices);
                        if (!handle.IsValid) continue;
                        int bytes = built.VertexCount * 48 + built.IndexCount * 2;
                        MeshDrawCall template = state.Sources[tier].Groups[built.Group].Template;
                        template.Mesh = handle;
                        meshes.Add(new ProxyMesh { Handle = handle, Template = template, Triangles = built.IndexCount / 3, Bytes = bytes });
                        uploadBudget -= bytes;
                        _mergedBytes += bytes;
                    }
                }

                cell.Proxies[tier] = meshes;
                ready = true;
            }
        }

        // Until the wanted tier arrives, the other one is better than a bare hillside.
        List<ProxyMesh> drawn = cell.Proxies[tier] ?? cell.Proxies[1 - tier];
        if (drawn != null)
        {
            cell.LastUsed[cell.Proxies[tier] != null ? tier : 1 - tier] = now;
            foreach (ProxyMesh mesh in drawn)
            {
                if (count >= buffer.Length) break;
                MeshDrawCall call = mesh.Template;
                call.World = placement;
                buffer[count++] = call;
                draws++;
                triangles += mesh.Triangles;
            }
        }

        return ready;
    }

    private static BuiltProxy[] Merge(ProxySource source, Generated content)
    {
        var built = new List<BuiltProxy>();
        for (int g = 0; g < source.Groups.Length; g++)
        {
            Part part = source.Groups[g];
            int perMesh = Math.Max(1, ushort.MaxValue / Math.Max(1, part.Vertices.Length));
            for (int first = 0; first < content.Instances.Length; first += perMesh)
            {
                int copies = Math.Min(perMesh, content.Instances.Length - first);
                int vertexCount = copies * part.Vertices.Length, indexCount = copies * part.Indices.Length;
                MeshVertex[] vertices = ArrayPool<MeshVertex>.Shared.Rent(vertexCount);
                ushort[] indices = ArrayPool<ushort>.Shared.Rent(indexCount);
                for (int c = 0; c < copies; c++)
                {
                    ref readonly Matrix4x4 matrix = ref content.Matrices[first + c];
                    float tone = content.Instances[first + c].Tone;
                    Vector4 shade = new(1f + tone * 0.07f, 1f - MathF.Abs(tone) * 0.04f, 1f - tone * 0.09f, 1f);
                    int vertexBase = c * part.Vertices.Length, indexBase = c * part.Indices.Length;
                    for (int v = 0; v < part.Vertices.Length; v++)
                    {
                        MeshVertex vertex = part.Vertices[v];
                        vertex.Position = Vector3.Transform(vertex.Position, matrix);
                        Vector3 normal = Vector3.TransformNormal(vertex.Normal, matrix);
                        vertex.Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
                        vertex.Color *= shade;
                        vertices[vertexBase + v] = vertex;
                    }

                    for (int i = 0; i < part.Indices.Length; i++)
                        indices[indexBase + i] = (ushort)(vertexBase + part.Indices[i]);
                }

                built.Add(new BuiltProxy { Group = g, Vertices = vertices, VertexCount = vertexCount, Indices = indices, IndexCount = indexCount });
            }
        }

        return built.ToArray();
    }

    private void ReleaseIdle(double now)
    {
        foreach (LayerState state in _layers)
        {
            foreach (Cell cell in state.Cells.Values)
            {
                for (int tier = 0; tier < 2; tier++)
                {
                    if (cell.Proxies[tier] == null || now - cell.LastUsed[tier] < IdleSeconds) continue;
                    ReleaseTier(cell, tier);
                }
            }
        }
    }

    private void ReleaseTier(Cell cell, int tier)
    {
        if (cell.Proxies[tier] == null) return;
        foreach (ProxyMesh mesh in cell.Proxies[tier])
        {
            if (mesh.Handle.IsValid) _renderer?.ReleaseMesh(mesh.Handle);
            _mergedBytes -= mesh.Bytes;
        }

        cell.Proxies[tier] = null;
    }

    private void ReleaseLayerMeshes(LayerState state)
    {
        foreach (Cell cell in state.Cells.Values)
        {
            ReleaseTier(cell, Mid);
            ReleaseTier(cell, Far);
        }
    }

    private void ReleaseMeshes()
    {
        foreach (LayerState state in _layers) ReleaseLayerMeshes(state);
    }

    public void Dispose()
    {
        ReleaseMeshes();
        _layers.Clear();
        _renderer = null;
    }
}
