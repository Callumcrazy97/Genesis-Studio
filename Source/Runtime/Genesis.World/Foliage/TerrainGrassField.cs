using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;
using Genesis.World.Terrain;

namespace Genesis.World.Foliage;

/// <summary>What grass around the camera held and did in the last update and draw.</summary>
public readonly record struct TerrainGrassStatistics(
    int ResidentCells,
    int PooledCells,
    int PendingCells,
    int CellsGeneratedLastUpdate,
    int TuftsResident,
    int TuftsDrawn,
    int NearTuftsDrawn,
    int VisibleCells,
    double GenerationMilliseconds,
    long CellsAllocated,
    long CellsReused,
    long CellsRecycled);

/// <summary>
/// Grass grown from a <see cref="TerrainGrassRuleSettings"/> rule in square cells around the camera.
/// </summary>
/// <remarks>
/// <para>
/// Each cell is a lattice of candidate spots, one every <see cref="TerrainGrassRuleSettings.Spacing"/>
/// metres. A spot grows a tuft with the probability the rule gives the painted layers under it, so a
/// meadow at density 1 is full and a dirt road at 0 is bare. Jitter, size, turn and tone come from a
/// hash of the spot's lattice coordinates and the seed, so the same ground always grows the same
/// grass however the camera arrived there.
/// </para>
/// <para>
/// Cells are grown nearest first and at most <see cref="TerrainGrassRuleSettings.CellsPerFrame"/> a
/// frame. A cell the camera has left behind goes back to a pool with its instance array, and the
/// next cell needed takes it, so walking about allocates nothing once the pool has filled.
/// </para>
/// <para>
/// Drawing culls whole cells against the view, draws the nearest first up to a tuft budget, and thins
/// each cell towards the edge of the radius by drawing only the first part of its tufts, which are
/// stored in a random order. It reuses the scattered grass's tuft meshes and foliage shading.
/// </para>
/// </remarks>
public sealed class TerrainGrassField : IDisposable
{
    /// <summary>One grown cell: its tufts as ready-to-draw instances in a random order.</summary>
    private sealed class Cell
    {
        public int X, Z;
        public MeshInstanceData[] Instances;
        public int Count;
        public Vector3 Min, Max;
        /// <summary>The <see cref="_growth"/> it was grown in; older ones are regrown by <see cref="Refresh"/>.</summary>
        public int Growth;
    }

    private readonly TerrainAsset _terrain;
    private readonly TerrainGrassRuleSettings _settings;
    private readonly TerrainGrassRuleSettings _source;
    private readonly Dictionary<long, Cell> _cells = new();
    private readonly Stack<Cell> _pool = new();
    private readonly List<long> _leaving = new();
    private readonly List<(int X, int Z, float Distance)> _wanted = new();
    private readonly List<(Cell Cell, float Distance)> _visible = new();
    private readonly float[] _densities = new float[TerrainGrassLayerWeights.LayerCount];
    private readonly int _cellsX, _cellsZ, _perSide;
    private readonly float _step, _width, _depth;
    private Matrix4x4 _placement = Matrix4x4.Identity;
    private Matrix4x4 _inverse = Matrix4x4.Identity;
    private bool _placed;
    private int _lastCameraCellX = int.MinValue, _lastCameraCellZ = int.MinValue;
    private bool _pending = true;
    private int _growth;
    private MeshInstanceData[] _nearScratch = Array.Empty<MeshInstanceData>();
    private MeshInstanceData[] _farScratch = Array.Empty<MeshInstanceData>();
    private IRenderController _renderer;
    private MeshHandle _nearMesh, _farMesh;
    private int _generatedLastUpdate, _tuftsResident, _tuftsDrawn, _nearDrawn, _visibleCells;
    private double _generationMilliseconds;
    private long _allocated, _reused, _recycled;

    public TerrainGrassField(TerrainAsset terrain, TerrainGrassRuleSettings settings)
    {
        _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        _source = (settings ?? new TerrainGrassRuleSettings()).Clone();
        _settings = _source.Clone();
        _settings.Normalize();
        for (int layer = 0; layer < _densities.Length; layer++) _densities[layer] = _settings.Density(layer);
        _width = (terrain.ResolutionX - 1) * terrain.CellSize;
        _depth = (terrain.ResolutionZ - 1) * terrain.CellSize;
        _cellsX = Math.Max(1, (int)MathF.Ceiling(_width / _settings.CellSize));
        _cellsZ = Math.Max(1, (int)MathF.Ceiling(_depth / _settings.CellSize));
        _perSide = _settings.TuftsPerCellSide;
        _step = _settings.CellSize / _perSide;
    }

    /// <summary>The terrain this grass grows on.</summary>
    public TerrainAsset Terrain => _terrain;

    /// <summary>A copy of the rule this field grows, normalised; changing it changes nothing.</summary>
    public TerrainGrassRuleSettings Settings => _settings;

    /// <summary>True when this field was made from a rule that grows the same grass as <paramref name="rule"/>.</summary>
    public bool GrowsFrom(TerrainGrassRuleSettings rule) => _source.SameAs(rule);

    /// <summary>Most tufts one cell can hold.</summary>
    public int CellCapacity => _perSide * _perSide;

    public int ResidentCellCount => _cells.Count;
    public int PooledCellCount => _pool.Count;

    /// <summary>True while cells in reach are still waiting to be grown.</summary>
    public bool Pending => _pending;

    public TerrainGrassStatistics Statistics => new(
        _cells.Count, _pool.Count, _wanted.Count, _generatedLastUpdate, _tuftsResident, _tuftsDrawn, _nearDrawn,
        _visibleCells, _generationMilliseconds, _allocated, _reused, _recycled);

    /// <summary>Tufts in a grown cell, or -1 when that cell is not resident.</summary>
    public int ResidentTufts(int cellX, int cellZ) =>
        _cells.TryGetValue(Key(cellX, cellZ), out Cell cell) ? cell.Count : -1;

    /// <summary>The cell holding a point in the terrain's own space.</summary>
    public (int X, int Z) CellAt(float localX, float localZ) =>
        ((int)MathF.Floor((localX - _terrain.OriginX) / _settings.CellSize),
         (int)MathF.Floor((localZ - _terrain.OriginZ) / _settings.CellSize));

    /// <summary>
    /// Grows the tufts of one cell into <paramref name="destination"/> (at least
    /// <see cref="CellCapacity"/> long) in terrain space, without touching what is resident.
    /// Returns how many grew. For tests and tools; drawing uses the same code.
    /// </summary>
    public int GenerateCell(int cellX, int cellZ, Span<MeshInstanceData> destination) =>
        Generate(cellX, cellZ, Matrix4x4.Identity, destination, out _, out _);

    /// <summary>
    /// Recycles cells the camera has left and grows, nearest first, up to the per-frame budget of
    /// the cells now in reach. <paramref name="placement"/> places the terrain in the world.
    /// </summary>
    public void Update(Vector3 camera, in Matrix4x4 placement)
    {
        long started = Stopwatch.GetTimestamp();
        _generatedLastUpdate = 0;
        if (!_placed || !placement.Equals(_placement))
        {
            // Grown cells carry the placement in their matrices: a moved terrain starts again.
            RecycleAll();
            _placement = placement;
            _inverse = Matrix4x4.Invert(placement, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
            _placed = true;
            _pending = true;
        }

        Vector3 local = Vector3.Transform(camera, _inverse);
        float originX = local.X - _terrain.OriginX, originZ = local.Z - _terrain.OriginZ;
        float size = _settings.CellSize;
        int cameraX = (int)MathF.Floor(originX / size), cameraZ = (int)MathF.Floor(originZ / size);
        if (cameraX == _lastCameraCellX && cameraZ == _lastCameraCellZ && !_pending)
        {
            _generationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }

        _lastCameraCellX = cameraX;
        _lastCameraCellZ = cameraZ;
        float radius = _settings.Radius;
        float keep = radius + size;

        // Cells a little beyond the radius stay, so stepping back and forth over a border does not
        // grow the same row again and again.
        _leaving.Clear();
        foreach (KeyValuePair<long, Cell> pair in _cells)
            if (DistanceToCell(pair.Value.X, pair.Value.Z, originX, originZ) > keep) _leaving.Add(pair.Key);
        foreach (long key in _leaving)
        {
            Cell cell = _cells[key];
            _cells.Remove(key);
            _tuftsResident -= cell.Count;
            cell.Count = 0;
            _pool.Push(cell);
            _recycled++;
        }

        _wanted.Clear();
        int x0 = Math.Max(0, (int)MathF.Floor((originX - radius) / size));
        int x1 = Math.Min(_cellsX - 1, (int)MathF.Floor((originX + radius) / size));
        int z0 = Math.Max(0, (int)MathF.Floor((originZ - radius) / size));
        int z1 = Math.Min(_cellsZ - 1, (int)MathF.Floor((originZ + radius) / size));
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float distance = DistanceToCell(x, z, originX, originZ);
                if (distance > radius || (_cells.TryGetValue(Key(x, z), out Cell grownCell) && grownCell.Growth == _growth)) continue;
                _wanted.Add((x, z, distance));
            }
        }

        if (_wanted.Count > 0)
        {
            _wanted.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            int budget = Math.Min(_settings.CellsPerFrame, _wanted.Count);
            double timeBudget = _settings.GenerationBudgetMilliseconds;
            int grown = 0;
            for (int i = 0; i < budget; i++)
            {
                // The nearest cell always grows, so a slow machine still fills in, nearest first.
                if (i > 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= timeBudget) break;
                (int x, int z, float _) = _wanted[i];
                // A cell grown before a refresh is regrown in place; it is drawn as it was until then.
                if (_cells.TryGetValue(Key(x, z), out Cell cell)) _tuftsResident -= cell.Count;
                else cell = Rent();
                cell.X = x;
                cell.Z = z;
                cell.Count = Generate(x, z, _placement, cell.Instances, out cell.Min, out cell.Max);
                cell.Growth = _growth;
                _cells[Key(x, z)] = cell;
                _tuftsResident += cell.Count;
                _generatedLastUpdate++;
                grown++;
            }

            _wanted.RemoveRange(0, grown);
        }

        _pending = _wanted.Count > 0;
        _generationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>
    /// Draws the grown grass in view: nearest cells first, thinned towards the radius, at most
    /// <see cref="TerrainGrassRuleSettings.MaximumDrawnTufts"/> tufts.
    /// </summary>
    public void Draw(IRenderController renderer, Vector3 camera, in Matrix4x4 viewProjection)
    {
        _tuftsDrawn = _nearDrawn = _visibleCells = 0;
        if (renderer == null || _cells.Count == 0) return;
        EnsureMeshes(renderer);
        if (!_nearMesh.IsValid || !_farMesh.IsValid) return;

        var frustum = new CameraFrustum(viewProjection);
        _visible.Clear();
        foreach (Cell cell in _cells.Values)
        {
            if (cell.Count == 0 || !frustum.IntersectsAabb(cell.Min, cell.Max)) continue;
            _visible.Add((cell, DistanceToBox(camera, cell.Min, cell.Max)));
        }

        _visible.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        _visibleCells = _visible.Count;
        int budget = _settings.MaximumDrawnTufts;
        int near = 0, far = 0;
        float nearDistance = _settings.NearDistance;
        foreach ((Cell cell, float distance) in _visible)
        {
            if (budget <= 0) break;
            int count = (int)(cell.Count * Keep(distance) + 0.5f);
            count = Math.Min(count, budget);
            if (count <= 0) continue;
            budget -= count;
            if (distance < nearDistance)
            {
                Grow(ref _nearScratch, near + count);
                cell.Instances.AsSpan(0, count).CopyTo(_nearScratch.AsSpan(near));
                near += count;
            }
            else
            {
                Grow(ref _farScratch, far + count);
                cell.Instances.AsSpan(0, count).CopyTo(_farScratch.AsSpan(far));
                far += count;
            }
        }

        if (near > 0) renderer.DrawMeshInstances(Template(_nearMesh), _nearScratch.AsSpan(0, near));
        if (far > 0) renderer.DrawMeshInstances(Template(_farMesh), _farScratch.AsSpan(0, far));
        _nearDrawn = near;
        _tuftsDrawn = near + far;
    }

    /// <summary>
    /// Regrows every resident cell from the terrain as it is now (after a sculpt or paint stroke),
    /// nearest first and within the per-frame budget. Each cell keeps drawing its old tufts until
    /// its turn comes, so the grass changes without going bare.
    /// </summary>
    public void Refresh()
    {
        _growth++;
        _pending = true;
    }

    /// <summary>Hands every grown cell back to the pool; the next update grows them again.</summary>
    public void RecycleAll()
    {
        foreach (Cell cell in _cells.Values)
        {
            cell.Count = 0;
            _pool.Push(cell);
            _recycled++;
        }

        _cells.Clear();
        _tuftsResident = 0;
        _pending = true;
        _lastCameraCellX = _lastCameraCellZ = int.MinValue;
    }

    public void Dispose()
    {
        ReleaseMeshes();
        _cells.Clear();
        _pool.Clear();
    }

    /// <summary>The share of a cell's tufts drawn at a distance: all of them inside the full-density radius, none at the edge.</summary>
    private float Keep(float distance)
    {
        float radius = _settings.Radius;
        float full = radius * _settings.FullDensityFraction;
        if (distance <= full) return 1f;
        if (distance >= radius) return 0f;
        float t = (distance - full) / MathF.Max(radius - full, 0.001f);
        float remaining = 1f - t;
        return remaining * remaining;
    }

    private int Generate(int cellX, int cellZ, in Matrix4x4 placement, Span<MeshInstanceData> destination,
        out Vector3 min, out Vector3 max)
    {
        TerrainAsset terrain = _terrain;
        TerrainGrassRuleSettings settings = _settings;
        int perSide = _perSide;
        float step = _step;
        float jitter = settings.Jitter;
        float minScale = settings.MinimumScale, scaleRange = settings.MaximumScale - settings.MinimumScale;
        bool slopeLimited = settings.MaximumSlopeDegrees < 89.9f;
        float minimumUp = MathF.Cos(settings.MaximumSlopeDegrees * MathF.PI / 180f);
        float normalStep = MathF.Max(terrain.CellSize, 0.1f);
        uint seed = unchecked((uint)settings.Seed * 0x9E3779B9u);
        Vector3 localMin = new(float.MaxValue), localMax = new(float.MinValue);
        int count = 0;
        Span<float> weights = stackalloc float[TerrainGrassLayerWeights.LayerCount];
        for (int j = 0; j < perSide; j++)
        {
            int latticeZ = cellZ * perSide + j;
            for (int i = 0; i < perSide; i++)
            {
                int latticeX = cellX * perSide + i;
                uint hash = Hash(unchecked((uint)latticeX * 73856093u ^ (uint)latticeZ * 19349663u ^ seed));
                float offsetX = (latticeX + 0.5f + (Random01(hash + 1u) - 0.5f) * jitter) * step;
                float offsetZ = (latticeZ + 0.5f + (Random01(hash + 2u) - 0.5f) * jitter) * step;
                if (offsetX < 0f || offsetZ < 0f || offsetX > _width || offsetZ > _depth) continue;

                float density = DensityAt(terrain, offsetX, offsetZ, weights);
                if (density <= 0f || Random01(hash + 3u) >= density) continue;

                float x = terrain.OriginX + offsetX, z = terrain.OriginZ + offsetZ;
                if (slopeLimited)
                {
                    float left = terrain.SampleHeight(x - normalStep, z), right = terrain.SampleHeight(x + normalStep, z);
                    float back = terrain.SampleHeight(x, z - normalStep), front = terrain.SampleHeight(x, z + normalStep);
                    Vector3 normal = Vector3.Normalize(new Vector3(left - right, normalStep * 2f, back - front));
                    if (normal.Y < minimumUp) continue;
                }

                var position = new Vector3(x, terrain.SampleHeight(x, z), z);
                float scale = minScale + Random01(hash + 4u) * scaleRange;
                float turn = Random01(hash + 5u) * MathF.Tau;
                float hue = Random01(hash + 6u) * 2f - 1f;
                // The same tone spread as the scattered grass.
                var tint = new RenderColor(
                    Math.Clamp(1f + hue * 0.06f, 0.72f, 1.2f),
                    Math.Clamp(1f - MathF.Abs(hue) * 0.03f, 0.72f, 1.1f),
                    Math.Clamp(1f - hue * 0.08f, 0.72f, 1.2f), 1f);
                Matrix4x4 world = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(turn)
                    * Matrix4x4.CreateTranslation(position) * placement;
                destination[count++] = new MeshInstanceData(world, tint);
                localMin = Vector3.Min(localMin, position);
                localMax = Vector3.Max(localMax, position);
            }
        }

        // A random order, the same every time, so drawing the first part of a cell thins it evenly.
        uint shuffle = Hash(unchecked((uint)cellX * 2654435761u ^ (uint)cellZ * 40503u ^ seed ^ 0xA511E9B3u));
        for (int i = count - 1; i > 0; i--)
        {
            shuffle = Hash(shuffle + (uint)i);
            int other = (int)(shuffle % (uint)(i + 1));
            (destination[i], destination[other]) = (destination[other], destination[i]);
        }

        if (count == 0)
        {
            min = max = Vector3.Zero;
            return 0;
        }

        // Room for the tallest tuft and its blades leaning out.
        float reach = 0.35f * settings.MaximumScale;
        localMin -= new Vector3(reach, 0.1f, reach);
        localMax += new Vector3(reach, 1.6f * settings.MaximumScale, reach);
        TransformBounds(localMin, localMax, placement, out min, out max);
        return count;
    }

    /// <summary>The rule's density at a point, blended between the four nearest height samples.</summary>
    private float DensityAt(TerrainAsset terrain, float offsetX, float offsetZ, Span<float> weights)
    {
        float gx = offsetX / terrain.CellSize, gz = offsetZ / terrain.CellSize;
        int x0 = (int)MathF.Floor(gx), z0 = (int)MathF.Floor(gz);
        float tx = gx - x0, tz = gz - z0;
        float d00 = CornerDensity(terrain, x0, z0, weights);
        float d10 = CornerDensity(terrain, x0 + 1, z0, weights);
        float d01 = CornerDensity(terrain, x0, z0 + 1, weights);
        float d11 = CornerDensity(terrain, x0 + 1, z0 + 1, weights);
        float near = d00 + (d10 - d00) * tx;
        float far = d01 + (d11 - d01) * tx;
        return near + (far - near) * tz;
    }

    private float CornerDensity(TerrainAsset terrain, int x, int z, Span<float> weights)
    {
        TerrainGrassLayerWeights.Sample(terrain, x, z, weights);
        float density = 0f;
        for (int layer = 0; layer < _densities.Length; layer++) density += weights[layer] * _densities[layer];
        return density;
    }

    private Cell Rent()
    {
        int capacity = CellCapacity;
        while (_pool.Count > 0)
        {
            Cell pooled = _pool.Pop();
            if (pooled.Instances != null && pooled.Instances.Length >= capacity)
            {
                _reused++;
                return pooled;
            }
        }

        _allocated++;
        return new Cell { Instances = new MeshInstanceData[capacity] };
    }

    private void EnsureMeshes(IRenderController renderer)
    {
        if (ReferenceEquals(renderer, _renderer) && _nearMesh.IsValid && _farMesh.IsValid) return;
        ReleaseMeshes();
        _renderer = renderer;
        MeshData near = FoliageGeometry.Build(_settings.Species, nearLod: true);
        MeshData far = FoliageGeometry.Build(_settings.Species, nearLod: false);
        _nearMesh = renderer.RegisterMesh(near.Vertices, near.Indices);
        _farMesh = renderer.RegisterMesh(far.Vertices, far.Indices);
    }

    private void ReleaseMeshes()
    {
        if (_renderer != null)
        {
            if (_nearMesh.IsValid) _renderer.ReleaseMesh(_nearMesh);
            if (_farMesh.IsValid) _renderer.ReleaseMesh(_farMesh);
        }

        _nearMesh = default;
        _farMesh = default;
        _renderer = null;
    }

    /// <summary>The same template the scattered grass draws with, so the two look alike.</summary>
    private static MeshDrawCall Template(MeshHandle mesh) => new()
    {
        Mesh = mesh,
        Tint = RenderColor.White,
        Alpha = 1f,
        Flags = MeshDrawFlags.Foliage | MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow,
    };

    private static void Grow(ref MeshInstanceData[] buffer, int needed)
    {
        if (buffer.Length >= needed) return;
        int capacity = Math.Max(1024, buffer.Length);
        while (capacity < needed) capacity *= 2;
        Array.Resize(ref buffer, capacity);
    }

    private float DistanceToCell(int cellX, int cellZ, float originX, float originZ)
    {
        float size = _settings.CellSize;
        float minX = cellX * size, minZ = cellZ * size;
        float dx = MathF.Max(0f, MathF.Max(minX - originX, originX - (minX + size)));
        float dz = MathF.Max(0f, MathF.Max(minZ - originZ, originZ - (minZ + size)));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static float DistanceToBox(Vector3 point, Vector3 min, Vector3 max)
    {
        float x = MathF.Max(min.X - point.X, MathF.Max(0f, point.X - max.X));
        float y = MathF.Max(min.Y - point.Y, MathF.Max(0f, point.Y - max.Y));
        float z = MathF.Max(min.Z - point.Z, MathF.Max(0f, point.Z - max.Z));
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    private static void TransformBounds(Vector3 localMin, Vector3 localMax, in Matrix4x4 placement,
        out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) == 0 ? localMin.X : localMax.X,
                (corner & 2) == 0 ? localMin.Y : localMax.Y,
                (corner & 4) == 0 ? localMin.Z : localMax.Z);
            Vector3 world = Vector3.Transform(point, placement);
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }
    }

    private static long Key(int x, int z) => ((long)z << 32) | (uint)x;
    private static float Random01(uint value) => (Hash(value) >> 8) * (1f / 16777216f);

    private static uint Hash(uint value)
    {
        value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15; value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }
}
