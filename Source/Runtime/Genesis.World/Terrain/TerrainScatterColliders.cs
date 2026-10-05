using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Physics;

namespace Genesis.World.Terrain;

/// <summary>
/// Gives scattered copies something to bump into: an upright box for each trunk or rock near
/// anything that could touch it.
/// </summary>
/// <remarks>
/// A forest is a rule, not a list of trees, so there are no bodies to load. Colliders are made
/// for the copies within a few metres of the camera and of every moving body, from the same rule
/// the renderer draws from, and removed again when nothing is near. A world with a quarter of a
/// million trees keeps a few hundred boxes.
/// </remarks>
public sealed class TerrainScatterColliders
{
    private readonly TerrainAsset _terrain;
    private readonly List<TerrainScatterLayer> _layers = new();
    private readonly Matrix4x4 _placement;
    private readonly float _placementScale;
    private readonly string _label;
    /// <summary>
    /// One cell's copies, sorted into a 16 by 16 grid of buckets so a focus looks only at the
    /// copies in the few buckets around it instead of at every copy in the cell.
    /// </summary>
    private sealed class CellCopies
    {
        public TerrainScatterInstance[] Instances;
        /// <summary>Indices into <see cref="Instances"/>, grouped by bucket.</summary>
        public int[] Order;
        /// <summary>Where each bucket's run begins in <see cref="Order"/>; one more entry than there are buckets.</summary>
        public int[] Start;
        public float MinX, MinZ;
    }

    private const int BucketsPerSide = 16;
    private const float BucketSize = TerrainScatterPlacement.CellSize / BucketsPerSide;

    private readonly Dictionary<(int Layer, int CellX, int CellZ), CellCopies> _cells = new();
    /// <summary>Cells whose copies are being worked out on a worker thread.</summary>
    private readonly Dictionary<(int Layer, int CellX, int CellZ), System.Threading.Tasks.Task<CellCopies>> _making = new();
    private readonly HashSet<(int Layer, int CellX, int CellZ)> _cellsInUse = new();
    private readonly List<(int Layer, int CellX, int CellZ)> _cellsToDrop = new();
    private bool _primed, _waiting;
    private readonly Dictionary<(int Layer, int CellX, int CellZ, int Index), int> _registered = new();
    private readonly HashSet<(int Layer, int CellX, int CellZ, int Index)> _wanted = new();
    private readonly List<(int Layer, int CellX, int CellZ, int Index)> _stale = new();
    private readonly List<Vector3> _lastFocus = new();
    private int _ticksSinceRefresh;

    public TerrainScatterColliders(TerrainAsset terrain, IEnumerable<TerrainScatterLayer> layers, Matrix4x4 placement, string label)
    {
        _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        _placement = placement;
        _placementScale = MathF.Max(1e-4f, new Vector3(placement.M11, placement.M12, placement.M13).Length());
        _label = label ?? "Scatter";
        foreach (TerrainScatterLayer layer in layers ?? Array.Empty<TerrainScatterLayer>())
            if (layer is { Enabled: true, CollisionRadius: > 0f, CollisionHeight: > 0f, DensityPerHectare: > 0f })
                _layers.Add(layer);
    }

    /// <summary>True when at least one layer asks for collision.</summary>
    public bool HasLayers => _layers.Count > 0;

    /// <summary>Colliders currently in the physics world.</summary>
    public int ResidentColliders => _registered.Count;

    /// <summary>How far past a focus's radius a collider is kept, so walking a boundary does not churn.</summary>
    public float KeepMargin { get; set; } = 6f;

    /// <summary>Most colliders added in one update; the rest follow on later updates, nearest cells first.</summary>
    public int RegistrationsPerUpdate { get; set; } = 96;

    /// <summary>True when every copy the last update wanted solid is solid, and no cell is still being worked out.</summary>
    public bool Settled => !_waiting && _wanted.Count == _registered.Count;

    /// <summary>
    /// Brings colliders in line with what could touch them. Positions are in world space; each
    /// focus is a point and the distance around it that needs collision.
    /// </summary>
    /// <param name="firstCellsAtOnce">
    /// Work the first update's cells out on this thread, so what stands beside the player when a
    /// room opens is solid from the first step. False while a room is being prepared behind a
    /// loading screen, where the cells can be worked out on worker threads and waited for.
    /// </param>
    public void Update(PhysicsWorld physics, ReadOnlySpan<TerrainColliderFocus> focuses, bool firstCellsAtOnce = true)
    {
        if (physics == null || _layers.Count == 0) return;

        // Nothing moved far enough to matter: checking every copy every tick would be waste.
        bool moved = focuses.Length != _lastFocus.Count || ++_ticksSinceRefresh >= 30;
        for (int i = 0; !moved && i < focuses.Length; i++)
            moved = Vector3.DistanceSquared(focuses[i].Position, _lastFocus[i]) > 1f;
        if (!moved && !_waiting && _wanted.Count == _registered.Count) return;
        _ticksSinceRefresh = 0;
        _waiting = false;
        _cellsInUse.Clear();
        // The first update works its cells out at once, so what stands beside the player when a
        // room opens is solid from the first step. After that a new cell is worked out on a
        // worker thread: placing a few hundred copies is several milliseconds, which crossing a
        // cell boundary should not cost the frame.
        bool immediate = !_primed && firstCellsAtOnce;
        _primed = true;
        _lastFocus.Clear();
        foreach (TerrainColliderFocus focus in focuses) _lastFocus.Add(focus.Position);

        if (!Matrix4x4.Invert(_placement, out Matrix4x4 inverse)) inverse = Matrix4x4.Identity;
        _wanted.Clear();
        int budget = Math.Max(1, RegistrationsPerUpdate);
        (int cellsX, int cellsZ) = TerrainScatterPlacement.CellCount(_terrain);

        foreach (TerrainColliderFocus focus in focuses)
        {
            Vector3 local = Vector3.Transform(focus.Position, inverse);
            float radius = MathF.Max(1f, focus.Radius) / _placementScale;
            float keep = radius + KeepMargin / _placementScale;
            int x1 = Math.Min(cellsX - 1, (int)MathF.Floor((local.X + keep - _terrain.OriginX) / TerrainScatterPlacement.CellSize));
            int z1 = Math.Min(cellsZ - 1, (int)MathF.Floor((local.Z + keep - _terrain.OriginZ) / TerrainScatterPlacement.CellSize));

            for (int layerIndex = 0; layerIndex < _layers.Count; layerIndex++)
            {
                TerrainScatterLayer layer = _layers[layerIndex];
                // A cell owns the copies whose grid square starts in it, so a copy can stand up to
                // one pitch past the cell's far edge: look that much further back for its cell.
                float overhang = TerrainScatterPlacement.Pitch(layer);
                int x0 = Math.Max(0, (int)MathF.Floor((local.X - keep - overhang - _terrain.OriginX) / TerrainScatterPlacement.CellSize));
                int z0 = Math.Max(0, (int)MathF.Floor((local.Z - keep - overhang - _terrain.OriginZ) / TerrainScatterPlacement.CellSize));
                for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    var cellKey = (layerIndex, cx, cz);
                    _cellsInUse.Add(cellKey);
                    if (!_cells.TryGetValue(cellKey, out CellCopies cell))
                    {
                        if (immediate)
                        {
                            using var timed = Genesis.Shared.Assets.LoadClock.Measure(Genesis.Shared.Assets.LoadWork.ScatterCell);
                            _cells[cellKey] = cell = Bucket(TerrainScatterPlacement.Generate(_terrain, layer, cx, cz), cx, cz);
                        }
                        else if (_making.TryGetValue(cellKey, out System.Threading.Tasks.Task<CellCopies> making))
                        {
                            if (!making.IsCompleted) { _waiting = true; continue; }
                            _making.Remove(cellKey);
                            if (!making.IsCompletedSuccessfully) { _waiting = true; continue; }
                            _cells[cellKey] = cell = making.Result;
                        }
                        else
                        {
                            int cellX = cx, cellZ = cz;
                            _making[cellKey] = System.Threading.Tasks.Task.Run(() =>
                            {
                                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("scatter collision cell made on a worker"))
                                    return Bucket(TerrainScatterPlacement.Generate(_terrain, layer, cellX, cellZ), cellX, cellZ);
                            });
                            _waiting = true;
                            continue;
                        }
                    }

                    TerrainScatterInstance[] instances = cell.Instances;
                    if (instances.Length == 0) continue;

                    int bx0 = Math.Clamp((int)MathF.Floor((local.X - keep - cell.MinX) / BucketSize), 0, BucketsPerSide - 1);
                    int bx1 = Math.Clamp((int)MathF.Floor((local.X + keep - cell.MinX) / BucketSize), 0, BucketsPerSide - 1);
                    int bz0 = Math.Clamp((int)MathF.Floor((local.Z - keep - cell.MinZ) / BucketSize), 0, BucketsPerSide - 1);
                    int bz1 = Math.Clamp((int)MathF.Floor((local.Z + keep - cell.MinZ) / BucketSize), 0, BucketsPerSide - 1);
                    for (int bz = bz0; bz <= bz1; bz++)
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        int bucket = bz * BucketsPerSide + bx;
                        for (int slot = cell.Start[bucket]; slot < cell.Start[bucket + 1]; slot++)
                        {
                            int i = cell.Order[slot];
                            Vector3 position = instances[i].Position;
                            float dx = position.X - local.X, dz = position.Z - local.Z;
                            float flat = dx * dx + dz * dz;
                            if (flat > keep * keep) continue;
                            var key = (layerIndex, cx, cz, i);
                            bool resident = _registered.ContainsKey(key);
                            // Inside the radius a collider is needed; in the margin one is only kept.
                            if (flat > radius * radius && !resident) continue;
                            _wanted.Add(key);
                            if (resident || budget <= 0) continue;
                            budget--;
                            _registered[key] = Register(physics, layer, instances[i]);
                        }
                    }
                }
            }
        }

        _stale.Clear();
        foreach ((int Layer, int CellX, int CellZ, int Index) key in _registered.Keys)
            if (!_wanted.Contains(key)) _stale.Add(key);
        foreach ((int Layer, int CellX, int CellZ, int Index) key in _stale)
        {
            physics.UnregisterStaticSurface(_registered[key]);
            _registered.Remove(key);
        }

        // Keep only a bounded number of copy lists, and never drop one that is in use: its
        // colliders would be taken for stale and removed from under whatever stands there.
        if (_cells.Count > 256)
        {
            _cellsToDrop.Clear();
            foreach ((int Layer, int CellX, int CellZ) key in _cells.Keys)
                if (!_cellsInUse.Contains(key)) _cellsToDrop.Add(key);
            foreach ((int Layer, int CellX, int CellZ) key in _cellsToDrop) _cells.Remove(key);
        }

        if (_making.Count > 64)
        {
            // Cells asked for and never come back to: let finished ones go.
            _cellsToDrop.Clear();
            foreach (KeyValuePair<(int Layer, int CellX, int CellZ), System.Threading.Tasks.Task<CellCopies>> pair in _making)
                if (pair.Value.IsCompleted && !_cellsInUse.Contains(pair.Key)) _cellsToDrop.Add(pair.Key);
            foreach ((int Layer, int CellX, int CellZ) key in _cellsToDrop) _making.Remove(key);
        }
    }

    /// <summary>Sorts a cell's copies into buckets by where they stand. An instance keeps its index.</summary>
    private CellCopies Bucket(TerrainScatterInstance[] instances, int cellX, int cellZ)
    {
        var cell = new CellCopies
        {
            Instances = instances,
            Order = new int[instances.Length],
            Start = new int[BucketsPerSide * BucketsPerSide + 1],
            MinX = _terrain.OriginX + cellX * TerrainScatterPlacement.CellSize,
            MinZ = _terrain.OriginZ + cellZ * TerrainScatterPlacement.CellSize,
        };
        int BucketOf(in TerrainScatterInstance instance) =>
            Math.Clamp((int)MathF.Floor((instance.Position.Z - cell.MinZ) / BucketSize), 0, BucketsPerSide - 1) * BucketsPerSide
            + Math.Clamp((int)MathF.Floor((instance.Position.X - cell.MinX) / BucketSize), 0, BucketsPerSide - 1);

        // Counting sort: count each bucket, turn the counts into starts, then place.
        for (int i = 0; i < instances.Length; i++) cell.Start[BucketOf(instances[i]) + 1]++;
        for (int bucket = 0; bucket < BucketsPerSide * BucketsPerSide; bucket++) cell.Start[bucket + 1] += cell.Start[bucket];
        int[] next = new int[BucketsPerSide * BucketsPerSide];
        for (int i = 0; i < instances.Length; i++)
        {
            int bucket = BucketOf(instances[i]);
            cell.Order[cell.Start[bucket] + next[bucket]++] = i;
        }

        return cell;
    }

    private int Register(PhysicsWorld physics, TerrainScatterLayer layer, in TerrainScatterInstance instance)
    {
        float scale = instance.Scale * _placementScale;
        float radius = MathF.Max(0.02f, layer.CollisionRadius * scale);
        float height = MathF.Max(0.05f, layer.CollisionHeight * scale);
        // The copy was sunk into the ground by the layer's Sink; the box starts at the copy's base.
        Vector3 centre = Vector3.Transform(instance.Position, _placement) + new Vector3(0f, height * 0.5f, 0f);
        return physics.RegisterStaticBox(centre, new Vector3(radius, height * 0.5f, radius), _label);
    }

    /// <summary>Removes every collider, for a room change or a terrain reload.</summary>
    public void Clear(PhysicsWorld physics)
    {
        if (physics != null)
            foreach (int id in _registered.Values) physics.UnregisterStaticSurface(id);
        _registered.Clear();
        _wanted.Clear();
        _cells.Clear();
        _making.Clear();
        _cellsInUse.Clear();
        _primed = false;
        _waiting = false;
        _lastFocus.Clear();
    }
}
