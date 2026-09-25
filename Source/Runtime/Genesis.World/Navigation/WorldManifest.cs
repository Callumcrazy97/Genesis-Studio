using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Genesis.Streaming;
using Genesis.World.Terrain;

namespace Genesis.World.Navigation;

public readonly record struct WorldBounds(Vector2 Minimum, Vector2 Maximum)
{
    public Vector2 Size => Maximum - Minimum;
    public Vector2 Center => (Minimum + Maximum) * 0.5f;
    public bool Contains(Vector2 point) => point.X >= Minimum.X && point.X <= Maximum.X && point.Y >= Minimum.Y && point.Y <= Maximum.Y;
}

public readonly record struct WorldChunkCoordinate(int X, int Z)
{
    public override string ToString() => $"{X},{Z}";
}

public enum ShelterKind { TreeCanopy, Cave, Building, RockOverhang }

public sealed class WorldShelter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ShelterKind Kind { get; set; }
    public Vector3 Center { get; set; }
    public Vector3 HalfExtents { get; set; } = new(2f);
    public float CanopyDensity { get; set; }
    public float WindPermeability { get; set; } = 0.5f;
}

public sealed class WorldPointOfInterest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Landmark";
    public string Category { get; set; } = "Landmark";
    public Vector3 Position { get; set; }
    public float DiscoveryRadius { get; set; } = 20f;
}

public sealed class WorldRoute
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Trail";
    public TerrainPathKind Kind { get; set; }
    public float Width { get; set; } = 4f;
    public List<Vector3> CenterLine { get; set; } = new();
    public float Length => Enumerable.Range(1, Math.Max(0, CenterLine.Count - 1)).Sum(i => Vector3.Distance(CenterLine[i - 1], CenterLine[i]));
}

public sealed class WorldChunkRecord
{
    public WorldChunkCoordinate Coordinate { get; set; }
    public WorldBounds Bounds { get; set; }
    public List<string> PointsOfInterest { get; set; } = new();
    public List<string> Routes { get; set; } = new();
    public List<string> WaterBodies { get; set; } = new();
}

public sealed class WorldManifest
{
    public const string SchemaName = "genesis.world-manifest";
    public const int CurrentVersion = 1;
    public string Schema { get; set; } = SchemaName;
    public int Version { get; set; } = CurrentVersion;
    public int Seed { get; set; }
    public float ChunkSize { get; set; } = 128f;
    public WorldBounds Bounds { get; set; }
    public List<WorldPointOfInterest> PointsOfInterest { get; set; } = new();
    public List<WorldRoute> Routes { get; set; } = new();
    public List<TerrainWaterDefinition> WaterBodies { get; set; } = new();
    public List<WorldShelter> Shelters { get; set; } = new();
    public List<WorldChunkRecord> Chunks { get; set; } = new();

    public static WorldManifest Build(TerrainAsset terrain, TerrainNatureDocument nature, float chunkSize = 128f)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        nature ??= new TerrainNatureDocument();
        nature.Normalize();
        var manifest = new WorldManifest
        {
            Seed = nature.PathSettings.Seed,
            ChunkSize = Math.Clamp(chunkSize, terrain.CellSize * 4f, 4096f),
            Bounds = new WorldBounds(
                new Vector2(terrain.OriginX, terrain.OriginZ),
                new Vector2(terrain.OriginX + (terrain.ResolutionX - 1) * terrain.CellSize,
                            terrain.OriginZ + (terrain.ResolutionZ - 1) * terrain.CellSize)),
            PointsOfInterest = nature.PointsOfInterest.Select(p => new WorldPointOfInterest
            {
                Id = p.Id, DisplayName = p.Name, Category = p.Category,
                Position = p.Position, DiscoveryRadius = p.DiscoveryRadius,
            }).ToList(),
            Routes = nature.Paths.Select(p => new WorldRoute
            {
                Id = p.Id, DisplayName = p.Name, Kind = p.Kind, Width = p.Width, CenterLine = new List<Vector3>(p.Points),
            }).ToList(),
            WaterBodies = new List<TerrainWaterDefinition>(nature.WaterBodies),
        };
        BuildChunks(manifest);
        return manifest;
    }

    private static void BuildChunks(WorldManifest manifest)
    {
        int countX = Math.Max(1, (int)MathF.Ceiling(manifest.Bounds.Size.X / manifest.ChunkSize));
        int countZ = Math.Max(1, (int)MathF.Ceiling(manifest.Bounds.Size.Y / manifest.ChunkSize));
        for (int z = 0; z < countZ; z++)
        for (int x = 0; x < countX; x++)
        {
            Vector2 min = manifest.Bounds.Minimum + new Vector2(x, z) * manifest.ChunkSize;
            Vector2 max = Vector2.Min(min + new Vector2(manifest.ChunkSize), manifest.Bounds.Maximum);
            var record = new WorldChunkRecord { Coordinate = new WorldChunkCoordinate(x, z), Bounds = new WorldBounds(min, max) };
            foreach (WorldPointOfInterest poi in manifest.PointsOfInterest)
                if (record.Bounds.Contains(new Vector2(poi.Position.X, poi.Position.Z))) record.PointsOfInterest.Add(poi.Id);
            foreach (WorldRoute route in manifest.Routes)
                if (route.CenterLine.Any(point => record.Bounds.Contains(new Vector2(point.X, point.Z)))) record.Routes.Add(route.Id);
            foreach (TerrainWaterDefinition water in manifest.WaterBodies)
                if (Intersects(record.Bounds, new Vector2(water.Center.X, water.Center.Z), new Vector2(water.SizeX, water.SizeZ) * 0.5f)) record.WaterBodies.Add(water.Id);
            manifest.Chunks.Add(record);
        }
    }

    private static bool Intersects(WorldBounds bounds, Vector2 center, Vector2 half) =>
        center.X + half.X >= bounds.Minimum.X && center.X - half.X <= bounds.Maximum.X &&
        center.Y + half.Y >= bounds.Minimum.Y && center.Y - half.Y <= bounds.Maximum.Y;
}

public static class WorldManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public static void Save(string path, WorldManifest manifest)
    {
        Validate(manifest);
        string absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        string temporary = absolute + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Options));
        File.Move(temporary, absolute, true);
    }
    public static WorldManifest Load(string path)
    {
        WorldManifest manifest = JsonSerializer.Deserialize<WorldManifest>(File.ReadAllText(Path.GetFullPath(path)), Options)
            ?? throw new InvalidDataException("World manifest is empty.");
        Validate(manifest);
        return manifest;
    }
    public static void Validate(WorldManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Schema != WorldManifest.SchemaName || manifest.Version != WorldManifest.CurrentVersion)
            throw new InvalidDataException("Unsupported world manifest schema.");
        if (manifest.ChunkSize <= 0f || manifest.Bounds.Size.X <= 0f || manifest.Bounds.Size.Y <= 0f)
            throw new InvalidDataException("World manifest bounds or chunk size are invalid.");
        if (manifest.Chunks.Select(c => c.Coordinate).Distinct().Count() != manifest.Chunks.Count)
            throw new InvalidDataException("World manifest contains duplicate chunks.");
    }
}

/// <summary>Manifest-backed provider using Genesis.Streaming's existing budgets and residency loop.</summary>
public sealed class WorldManifestStreamingProvider : IStreamingProvider
{
    private readonly WorldManifest _manifest;
    private readonly Dictionary<WorldChunkCoordinate, WorldChunkRecord> _chunks;
    private readonly HashSet<WorldChunkCoordinate> _resident = new();

    public WorldManifestStreamingProvider(WorldManifest manifest)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _chunks = manifest.Chunks.ToDictionary(chunk => chunk.Coordinate);
    }
    public string Name => "World manifest";
    public StreamingStats Stats { get; } = new();
    public IReadOnlyCollection<WorldChunkCoordinate> Resident => _resident;
    public event Action<WorldChunkRecord> ChunkEntered;
    public event Action<WorldChunkRecord> ChunkRetired;
    public void RequestStreamingRefresh() { }

    public void Tick(in StreamingContext context)
    {
        float loadDistance = MathF.Max(_manifest.ChunkSize, context.Settings.LoadDistance(context.CameraFarPlane));
        float unloadDistance = MathF.Max(loadDistance + _manifest.ChunkSize, context.Settings.UnloadDistance(context.CameraFarPlane));
        Vector2 focus = new(context.FocusPosition.X, context.FocusPosition.Z);
        var wanted = _chunks.Values
            .Where(chunk => DistanceToBounds(focus, chunk.Bounds) <= loadDistance)
            .OrderBy(chunk => DistanceToBounds(focus, chunk.Bounds))
            .Select(chunk => chunk.Coordinate)
            .ToHashSet();
        int loads = 0;
        foreach (WorldChunkCoordinate coordinate in wanted)
        {
            if (_resident.Contains(coordinate) || loads >= context.Settings.MaxGenerationStartsPerFrame) continue;
            _resident.Add(coordinate); loads++; ChunkEntered?.Invoke(_chunks[coordinate]);
        }
        int unloads = 0;
        foreach (WorldChunkCoordinate coordinate in _resident.ToArray())
        {
            if (wanted.Contains(coordinate) || unloads >= context.Settings.MaxUnloadsPerFrame) continue;
            if (DistanceToBounds(focus, _chunks[coordinate].Bounds) <= unloadDistance) continue;
            _resident.Remove(coordinate); unloads++; ChunkRetired?.Invoke(_chunks[coordinate]);
        }
        Stats.CellsLoaded = _resident.Count;
        Stats.CellsVisible = wanted.Count;
        Stats.CellsPending = Math.Max(0, wanted.Count - _resident.Count);
        Stats.LoadsStartedLastFrame = loads;
        Stats.UnloadsLastFrame = unloads;
    }

    private static float DistanceToBounds(Vector2 point, WorldBounds bounds)
    {
        float x = MathF.Max(bounds.Minimum.X - point.X, MathF.Max(0f, point.X - bounds.Maximum.X));
        float z = MathF.Max(bounds.Minimum.Y - point.Y, MathF.Max(0f, point.Y - bounds.Maximum.Y));
        return MathF.Sqrt(x * x + z * z);
    }
}
