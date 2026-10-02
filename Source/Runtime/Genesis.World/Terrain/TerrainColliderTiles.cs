using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Genesis.Physics;

namespace Genesis.World.Terrain;

/// <summary>
/// Keeps terrain collision resident only around the points that can touch it.
/// </summary>
/// <remarks>
/// A terrain's collider used to be one triangle mesh covering every cell. For a multi-kilometre
/// terrain that is tens of millions of triangles built in a single frame. Here the terrain is cut
/// into square tiles; tiles within <see cref="Radius"/> of a focus point (the camera and every
/// moving body) are registered, and tiles that every focus has left are removed. A tile's
/// triangles and the tree the physics engine searches them with are prepared on a worker thread;
/// the caller's thread only hands the finished tile to the physics world, which costs it next to
/// nothing. Where a focus stands on a tile that is not ready, a small patch of ground is made
/// solid beneath it at once, so nothing falls through the world, and is taken away when the tile
/// arrives. The patch is a few hundred triangles; a whole tile is eight thousand.
/// </remarks>
public readonly record struct TerrainColliderFocus(Vector3 Position, float Radius);

public sealed class TerrainColliderTiles : IDisposable
{
    /// <summary>Cells along one side of a collision tile.</summary>
    public const int TileCells = 64;

    /// <summary>Cells along one side of the patch made solid beneath a focus whose tile is not ready.</summary>
    public const int PatchCells = 12;

    private sealed class Tile
    {
        public int RegistrationId;
        public bool Building;
        public int LastWantedTick;
        /// <summary>The patch standing in for this tile beneath a focus, or 0.</summary>
        public int PatchId;
        /// <summary>The patch's first cell along each axis.</summary>
        public int PatchX, PatchZ;
    }

    private readonly struct Prepared
    {
        public readonly long Key; public readonly PhysicsWorld.PreparedStaticMesh Mesh; public readonly int Generation;
        public Prepared(long key, PhysicsWorld.PreparedStaticMesh mesh, int generation)
        { Key = key; Mesh = mesh; Generation = generation; }
    }

    /// <summary>Most tiles being prepared on worker threads at one time.</summary>
    private static readonly int MaximumBuilding = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    private readonly TerrainAsset _terrain;
    private readonly Vector3 _scale, _position;
    private readonly Quaternion _orientation;
    private readonly Matrix4x4 _worldToLocal;
    private readonly string _label;
    private readonly int _tilesX, _tilesZ;
    private readonly Dictionary<long, Tile> _tiles = new();
    private readonly ConcurrentQueue<Prepared> _prepared = new();
    private readonly List<(long Key, float Distance)> _missing = new();
    private readonly List<long> _stale = new();
    private int _tick;
    private int _generation;
    private bool _disposed;

    public TerrainColliderTiles(TerrainAsset terrain, Vector3 scale, Vector3 position, Quaternion orientation, string label)
    {
        _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        _scale = scale;
        _position = position;
        _orientation = orientation;
        _label = label ?? "Terrain";
        Matrix4x4 localToWorld = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(orientation)
            * Matrix4x4.CreateTranslation(position);
        _worldToLocal = Matrix4x4.Invert(localToWorld, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
        _tilesX = Math.Max(1, (terrain.ResolutionX - 1 + TileCells - 1) / TileCells);
        _tilesZ = Math.Max(1, (terrain.ResolutionZ - 1 + TileCells - 1) / TileCells);
    }

    /// <summary>Tiles currently registered with the physics world.</summary>
    public int ResidentTiles { get; private set; }

    /// <summary>Tiles wanted by the last update that are not registered yet.</summary>
    public int PendingTiles { get; private set; }

    /// <summary>
    /// True when every tile the last update wanted is solid: nothing is missing, being prepared
    /// or waiting to be handed over.
    /// </summary>
    public bool Settled => PendingTiles == 0 && _prepared.IsEmpty && !AnyBuilding();

    /// <summary>
    /// Registers the tiles that are ready, starts preparing the nearest missing ones and removes
    /// tiles nothing is near any more.
    /// </summary>
    /// <param name="focusPoints">World positions that need ground within their radius.</param>
    /// <param name="groundBeneathAtOnce">
    /// Make the tile directly beneath each focus on this thread if it is missing. False while a
    /// room is being prepared behind a loading screen: nothing moves yet, so nothing can fall.
    /// </param>
    public void Update(PhysicsWorld physics, ReadOnlySpan<TerrainColliderFocus> focusPoints, bool groundBeneathAtOnce = true)
    {
        if (_disposed || physics == null) return;
        _tick++;
        float tileSize = TileCells * _terrain.CellSize;
        _missing.Clear();
        foreach (TerrainColliderFocus point in focusPoints)
        {
            Vector3 focus = Vector3.Transform(point.Position, _worldToLocal);
            float radius = MathF.Max(1f, point.Radius);

            // The tile directly beneath a focus cannot wait: a body would fall through the world.
            int underX = (int)MathF.Floor((focus.X - _terrain.OriginX) / tileSize);
            int underZ = (int)MathF.Floor((focus.Z - _terrain.OriginZ) / tileSize);
            if (groundBeneathAtOnce && underX >= 0 && underX < _tilesX && underZ >= 0 && underZ < _tilesZ)
            {
                long underKey = ((long)underZ << 32) | (uint)underX;
                if (!_tiles.TryGetValue(underKey, out Tile under))
                {
                    under = new Tile();
                    _tiles[underKey] = under;
                }

                if (under.RegistrationId == 0)
                {
                    // Keep the focus a couple of cells inside the patch; make a new one when it nears the edge.
                    int cellX = (int)MathF.Floor((focus.X - _terrain.OriginX) / _terrain.CellSize);
                    int cellZ = (int)MathF.Floor((focus.Z - _terrain.OriginZ) / _terrain.CellSize);
                    bool covered = under.PatchId != 0
                        && cellX >= under.PatchX + 2 && cellX < under.PatchX + PatchCells - 2
                        && cellZ >= under.PatchZ + 2 && cellZ < under.PatchZ + PatchCells - 2;
                    if (!covered)
                    {
                        using var timed = Genesis.Shared.Assets.LoadClock.Measure(Genesis.Shared.Assets.LoadWork.TerrainCollider);
                        if (under.PatchId != 0) physics.UnregisterStaticSurface(under.PatchId);
                        under.PatchX = Math.Clamp(cellX - PatchCells / 2, 0, Math.Max(0, _terrain.ResolutionX - 1 - PatchCells));
                        under.PatchZ = Math.Clamp(cellZ - PatchCells / 2, 0, Math.Max(0, _terrain.ResolutionZ - 1 - PatchCells));
                        TerrainColliderMesh.BuildRegion(_terrain, under.PatchX, under.PatchZ, PatchCells, PatchCells,
                            out Vector3[] patchVertices, out int[] patchIndices);
                        under.PatchId = patchIndices.Length >= 3
                            ? physics.RegisterStaticTriangleMesh(patchVertices, patchIndices, _scale, _position, _orientation, _label)
                            : 0;
                    }
                }
            }

            int minX = (int)MathF.Floor((focus.X - radius - _terrain.OriginX) / tileSize);
            int maxX = (int)MathF.Floor((focus.X + radius - _terrain.OriginX) / tileSize);
            int minZ = (int)MathF.Floor((focus.Z - radius - _terrain.OriginZ) / tileSize);
            int maxZ = (int)MathF.Floor((focus.Z + radius - _terrain.OriginZ) / tileSize);
            for (int z = Math.Max(0, minZ); z <= Math.Min(_tilesZ - 1, maxZ); z++)
            for (int x = Math.Max(0, minX); x <= Math.Min(_tilesX - 1, maxX); x++)
            {
                long key = ((long)z << 32) | (uint)x;
                if (!_tiles.TryGetValue(key, out Tile tile))
                {
                    tile = new Tile();
                    _tiles[key] = tile;
                }

                bool listed = tile.LastWantedTick == _tick;
                tile.LastWantedTick = _tick;
                if (listed || tile.RegistrationId != 0 || tile.Building) continue;
                float centreX = _terrain.OriginX + (x + 0.5f) * tileSize, centreZ = _terrain.OriginZ + (z + 0.5f) * tileSize;
                _missing.Add((key, MathF.Abs(centreX - focus.X) + MathF.Abs(centreZ - focus.Z)));
            }
        }

        // Prepare the nearest missing tiles off-thread, a few at a time.
        _missing.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        int building = 0;
        foreach (Tile tile in _tiles.Values) if (tile.Building) building++;
        foreach ((long key, _) in _missing)
        {
            if (building >= MaximumBuilding) break;
            Tile tile = _tiles[key];
            tile.Building = true;
            building++;
            int x = (int)(uint)key, z = (int)(key >> 32), generation = _generation;
            Vector3 scale = _scale;
            Task.Run(() =>
            {
                PhysicsWorld.PreparedStaticMesh mesh = null;
                try
                {
                    TerrainColliderMesh.BuildRegion(_terrain, x * TileCells, z * TileCells, TileCells, TileCells,
                        out Vector3[] vertices, out int[] indices);
                    // The tree is built here, on the worker: it is most of what a tile costs.
                    if (indices.Length >= 3) mesh = PhysicsWorld.PrepareStaticTriangleMesh(vertices, indices, scale);
                }
                finally
                {
                    // Always answer, so the tile is not left marked as being built for ever.
                    _prepared.Enqueue(new Prepared(key, mesh, generation));
                }
            });
        }

        // Handing a prepared tile to the physics world is cheap, so take every one that is ready.
        while (_prepared.TryDequeue(out Prepared prepared))
        {
            if (prepared.Generation != _generation || !_tiles.TryGetValue(prepared.Key, out Tile ready))
            {
                prepared.Mesh?.Dispose();
                continue;
            }

            ready.Building = false;
            if (prepared.Mesh == null) continue;
            if (ready.RegistrationId != 0)
            {
                // Made on this thread in the meantime, because something stood on it.
                prepared.Mesh.Dispose();
                continue;
            }

            using var timed = Genesis.Shared.Assets.LoadClock.Measure(Genesis.Shared.Assets.LoadWork.TerrainCollider);
            ready.RegistrationId = physics.RegisterStaticTriangleMesh(prepared.Mesh, _position, _orientation, _label);
            ready.LastWantedTick = Math.Max(ready.LastWantedTick, _tick);
            // The tile is solid before its stand-in goes, so what rests there is held throughout.
            if (ready.PatchId != 0)
            {
                physics.UnregisterStaticSurface(ready.PatchId);
                ready.PatchId = 0;
            }
        }

        // Keep a tile for a while after everything leaves, so pacing at a border does not thrash.
        _stale.Clear();
        int resident = 0;
        foreach ((long key, Tile tile) in _tiles)
        {
            if (_tick - tile.LastWantedTick > 240 && !tile.Building) _stale.Add(key);
            else if (tile.RegistrationId != 0) resident++;
        }

        foreach (long key in _stale)
        {
            Tile tile = _tiles[key];
            if (tile.RegistrationId != 0) physics.UnregisterStaticSurface(tile.RegistrationId);
            if (tile.PatchId != 0) physics.UnregisterStaticSurface(tile.PatchId);
            _tiles.Remove(key);
        }

        ResidentTiles = resident;
        PendingTiles = _missing.Count;
    }

    /// <summary>Registers every tile near the focus points now (first frame, tests, teleports).</summary>
    public void RegisterImmediately(PhysicsWorld physics, ReadOnlySpan<TerrainColliderFocus> focusPoints)
    {
        long deadline = Environment.TickCount64 + 20000;
        do
        {
            Update(physics, focusPoints);
            if (PendingTiles > 0 || !_prepared.IsEmpty) Thread.Sleep(1);
        }
        while ((PendingTiles > 0 || !_prepared.IsEmpty || AnyBuilding()) && Environment.TickCount64 < deadline);
    }

    /// <summary>Drops every tile, for example after the heights were edited.</summary>
    public void Clear(PhysicsWorld physics)
    {
        _generation++;
        foreach (Tile tile in _tiles.Values)
        {
            if (tile.RegistrationId != 0) physics?.UnregisterStaticSurface(tile.RegistrationId);
            if (tile.PatchId != 0) physics?.UnregisterStaticSurface(tile.PatchId);
        }

        _tiles.Clear();
        while (_prepared.TryDequeue(out Prepared prepared)) prepared.Mesh?.Dispose();
        ResidentTiles = 0;
    }

    public void Dispose()
    {
        _disposed = true;
        while (_prepared.TryDequeue(out Prepared prepared)) prepared.Mesh?.Dispose();
    }

    private bool AnyBuilding()
    {
        foreach (Tile tile in _tiles.Values) if (tile.Building) return true;
        return false;
    }
}
