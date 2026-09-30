using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Streaming;

namespace Genesis.World.Navigation;

/// <summary>
/// Spatially indexed manifest lifecycle planner. Notifications alone do not page terrain or physics;
/// the owning provider is responsible for applying actual resource residency.
/// </summary>
public sealed class WorldManifestStreamingProvider : IStreamingProvider
{
    private readonly float _cellSize;
    private readonly WorldBounds _bounds;
    private readonly int _maximumX, _maximumZ;
    private readonly Dictionary<WorldChunkCoordinate, WorldChunkRecord> _chunks = new();
    private readonly Dictionary<WorldChunkCoordinate, List<WorldChunkRecord>> _spatial = new();
    private readonly HashSet<WorldChunkCoordinate> _resident = new(), _wanted = new(), _visited = new();
    private readonly PriorityQueue<WorldChunkCoordinate, (float Distance, int Z, int X)> _loads = new();
    private readonly List<WorldChunkCoordinate> _retire = new();
    private Vector2 _lastFocus;
    private float _lastLoadDistance;
    private bool _refresh = true;

    public WorldManifestStreamingProvider(WorldManifest manifest)
    {
        WorldManifestSerializer.Validate(manifest);
        _bounds = manifest.Bounds; _cellSize = manifest.ChunkSize;
        _maximumX = checked((int)Math.Ceiling((double)_bounds.Size.X / _cellSize)) - 1;
        _maximumZ = checked((int)Math.Ceiling((double)_bounds.Size.Y / _cellSize)) - 1;
        foreach (WorldChunkRecord chunk in manifest.Chunks)
        {
            _chunks.Add(chunk.Coordinate, chunk);
            // Index actual bounds, retaining compatibility with sparse/nonuniform version-1 records.
            int minX = Index(chunk.Bounds.Minimum.X, _bounds.Minimum.X, _maximumX);
            int minZ = Index(chunk.Bounds.Minimum.Y, _bounds.Minimum.Y, _maximumZ);
            int maxX = EndIndex(chunk.Bounds.Maximum.X, _bounds.Minimum.X, _maximumX);
            int maxZ = EndIndex(chunk.Bounds.Maximum.Y, _bounds.Minimum.Y, _maximumZ);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                var key = new WorldChunkCoordinate(x, z);
                if (!_spatial.TryGetValue(key, out List<WorldChunkRecord> records)) _spatial.Add(key, records = new());
                records.Add(chunk);
            }
        }
    }

    public string Name => "World manifest";
    public StreamingStats Stats { get; } = new();
    public IReadOnlyCollection<WorldChunkCoordinate> Resident => _resident;
    public int SpatialCandidatesLastRefresh { get; private set; }
    public int QueueRefreshes { get; private set; }
    public event Action<WorldChunkRecord> ChunkEntered;
    public event Action<WorldChunkRecord> ChunkRetired;
    public void RequestStreamingRefresh() => _refresh = true;

    public void Tick(in StreamingContext context)
    {
        float loadDistance = MathF.Max(_cellSize, context.Settings.LoadDistance(context.CameraFarPlane));
        float unloadDistance = MathF.Max(loadDistance + _cellSize, context.Settings.UnloadDistance(context.CameraFarPlane));
        Vector2 focus = new(context.FocusPosition.X, context.FocusPosition.Z);
        if (!float.IsFinite(focus.X) || !float.IsFinite(focus.Y)
            || !float.IsFinite(loadDistance) || !float.IsFinite(unloadDistance))
            throw new ArgumentException("Streaming focus and distances must be finite.");
        if (_refresh || focus != _lastFocus || loadDistance != _lastLoadDistance) Refresh(focus, loadDistance);

        int loads = 0;
        while (loads < Math.Max(0, context.Settings.MaxGenerationStartsPerFrame) && _loads.TryDequeue(out var coordinate, out _))
        {
            if (!_resident.Add(coordinate)) continue;
            loads++; ChunkEntered?.Invoke(_chunks[coordinate]);
        }
        _retire.Clear();
        int budget = Math.Max(0, context.Settings.MaxUnloadsPerFrame);
        foreach (WorldChunkCoordinate coordinate in _resident)
        {
            if (_retire.Count >= budget) break;
            if (!_wanted.Contains(coordinate) && DistanceSquared(focus, _chunks[coordinate].Bounds) > unloadDistance * unloadDistance)
                _retire.Add(coordinate);
        }
        foreach (WorldChunkCoordinate coordinate in _retire)
        { _resident.Remove(coordinate); ChunkRetired?.Invoke(_chunks[coordinate]); }
        Stats.CellsLoaded = _resident.Count;
        Stats.CellsVisible = _wanted.Count;
        Stats.CellsPending = _loads.Count;
        Stats.LoadsStartedLastFrame = loads;
        Stats.UnloadsLastFrame = _retire.Count;
    }

    private void Refresh(Vector2 focus, float distance)
    {
        _lastFocus = focus; _lastLoadDistance = distance; _refresh = false; QueueRefreshes++;
        _wanted.Clear(); _visited.Clear(); _loads.Clear(); SpatialCandidatesLastRefresh = 0;
        if (focus.X + distance < _bounds.Minimum.X || focus.X - distance > _bounds.Maximum.X
            || focus.Y + distance < _bounds.Minimum.Y || focus.Y - distance > _bounds.Maximum.Y) return;
        // Include the cell ending exactly on the search boundary (bounds are inclusive).
        int minX = EndIndex(focus.X - distance, _bounds.Minimum.X, _maximumX);
        int minZ = EndIndex(focus.Y - distance, _bounds.Minimum.Y, _maximumZ);
        int maxX = Index(focus.X + distance, _bounds.Minimum.X, _maximumX);
        int maxZ = Index(focus.Y + distance, _bounds.Minimum.Y, _maximumZ);
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            if (!_spatial.TryGetValue(new(x, z), out List<WorldChunkRecord> records)) continue;
            foreach (WorldChunkRecord chunk in records)
            {
                if (!_visited.Add(chunk.Coordinate)) continue;
                SpatialCandidatesLastRefresh++;
                float squared = DistanceSquared(focus, chunk.Bounds);
                if (squared > distance * distance) continue;
                _wanted.Add(chunk.Coordinate);
                if (!_resident.Contains(chunk.Coordinate)) _loads.Enqueue(chunk.Coordinate, (squared, chunk.Coordinate.Z, chunk.Coordinate.X));
            }
        }
    }

    private int Index(float value, float origin, int maximum) =>
        (int)Math.Clamp(Math.Floor(((double)value - origin) / _cellSize), 0, maximum);
    private int EndIndex(float value, float origin, int maximum) =>
        (int)Math.Clamp(Math.Ceiling(((double)value - origin) / _cellSize) - 1, 0, maximum);
    private static float DistanceSquared(Vector2 point, WorldBounds bounds)
    {
        float x = MathF.Max(bounds.Minimum.X - point.X, MathF.Max(0f, point.X - bounds.Maximum.X));
        float z = MathF.Max(bounds.Minimum.Y - point.Y, MathF.Max(0f, point.Y - bounds.Maximum.Y));
        return x * x + z * z;
    }
}
