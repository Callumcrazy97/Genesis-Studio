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
/// moving body) are registered, and tiles that every focus has left are removed. Triangle data
/// is prepared on a worker thread; the physics registration itself happens on the caller's thread,
/// one tile per update.
/// </remarks>
public readonly record struct TerrainColliderFocus(Vector3 Position, float Radius);

public sealed class TerrainColliderTiles : IDisposable
{
    /// <summary>Cells along one side of a collision tile.</summary>
    public const int TileCells = 64;

    private sealed class Tile
    {
        public int RegistrationId;
        public bool Building;
        public int LastWantedTick;
    }

    private readonly struct Prepared
    {
        public readonly long Key; public readonly Vector3[] Vertices; public readonly int[] Indices; public readonly int Generation;
        public Prepared(long key, Vector3[] vertices, int[] indices, int generation)
        { Key = key; Vertices = vertices; Indices = indices; Generation = generation; }
    }

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
    /// Registers the nearest missing tile and removes tiles nothing is near any more.
    /// </summary>
    /// <param name="focusPoints">World positions that need ground within their radius.</param>
    public void Update(PhysicsWorld physics, ReadOnlySpan<TerrainColliderFocus> focusPoints)
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
            if (underX >= 0 && underX < _tilesX && underZ >= 0 && underZ < _tilesZ)
            {
                long underKey = ((long)underZ << 32) | (uint)underX;
                if (!_tiles.TryGetValue(underKey, out Tile under))
                {
                    under = new Tile();
                    _tiles[underKey] = under;
                }

                if (under.RegistrationId == 0)
                {
                    TerrainColliderMesh.BuildRegion(_terrain, underX * TileCells, underZ * TileCells, TileCells, TileCells,
                        out Vector3[] underVertices, out int[] underIndices);
                    if (underIndices.Length >= 3)
                        under.RegistrationId = physics.RegisterStaticTriangleMesh(underVertices, underIndices,
                            _scale, _position, _orientation, _label);
                }

                under.LastWantedTick = _tick;
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

        // Prepare the nearest missing tiles off-thread (at most two at a time).
        _missing.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        int building = 0;
        foreach (Tile tile in _tiles.Values) if (tile.Building) building++;
        foreach ((long key, _) in _missing)
        {
            if (building >= 2) break;
            Tile tile = _tiles[key];
            tile.Building = true;
            building++;
            int x = (int)(uint)key, z = (int)(key >> 32), generation = _generation;
            Task.Run(() =>
            {
                TerrainColliderMesh.BuildRegion(_terrain, x * TileCells, z * TileCells, TileCells, TileCells,
                    out Vector3[] vertices, out int[] indices);
                _prepared.Enqueue(new Prepared(key, vertices, indices, generation));
            });
        }

        // Registering builds the physics engine's tree for the tile, so do one per update.
        if (_prepared.TryDequeue(out Prepared prepared) && _tiles.TryGetValue(prepared.Key, out Tile ready))
        {
            ready.Building = false;
            if (prepared.Generation == _generation && prepared.Indices.Length >= 3 && ready.RegistrationId == 0)
            {
                ready.RegistrationId = physics.RegisterStaticTriangleMesh(prepared.Vertices, prepared.Indices,
                    _scale, _position, _orientation, _label);
                ready.LastWantedTick = Math.Max(ready.LastWantedTick, _tick);
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
            if (tile.RegistrationId != 0) physics?.UnregisterStaticSurface(tile.RegistrationId);
        _tiles.Clear();
        while (_prepared.TryDequeue(out _)) { }
        ResidentTiles = 0;
    }

    public void Dispose() => _disposed = true;

    private bool AnyBuilding()
    {
        foreach (Tile tile in _tiles.Values) if (tile.Building) return true;
        return false;
    }
}
