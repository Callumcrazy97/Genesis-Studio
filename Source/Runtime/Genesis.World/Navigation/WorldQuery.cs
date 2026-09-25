using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.World.Terrain;

namespace Genesis.World.Navigation;

public readonly record struct TerrainSurfaceSample(
    float Height,
    Vector3 Normal,
    float Slope,
    Vector4 SplatWeights,
    float PathMask,
    float Moisture,
    float RockExposure);

public readonly record struct ShelterSample(
    float RainOcclusion,
    float WindOcclusion,
    float CanopyDensity,
    bool HasSolidCover,
    bool IsInsideCave,
    string DominantShelterId)
{
    public static ShelterSample Exposed => new(0f, 0f, 0f, false, false, null);
}

public interface IWorldQuery
{
    WorldManifest Manifest { get; }
    TerrainSurfaceSample SampleTerrain(float worldX, float worldZ);
    TerrainWaterDefinition WaterAt(float worldX, float worldZ);
    float? WaterSurfaceHeightAt(float worldX, float worldZ);
    float WaterDepthAt(float worldX, float worldZ);
    TerrainPathSample SamplePath(float worldX, float worldZ);
    ShelterSample SampleShelter(Vector3 position, Vector3 windDirection);
    WorldChunkCoordinate ChunkAt(float worldX, float worldZ);
    IReadOnlyList<WorldPointOfInterest> PointsNear(Vector3 position, float radius);
}

/// <summary>Authoritative queries over one TerrainAsset and its persisted world manifest.</summary>
public sealed class WorldQuery : IWorldQuery
{
    private readonly TerrainAsset _terrain;
    private readonly TerrainPathNetwork _paths;
    private readonly Dictionary<string, float> _waterOffsets = new(StringComparer.OrdinalIgnoreCase);

    public WorldQuery(TerrainAsset terrain, WorldManifest manifest)
    {
        _terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _paths = new TerrainPathNetwork(manifest.Routes.Select(route => new TerrainPathDefinition
        {
            Id = route.Id, Name = route.DisplayName, Kind = route.Kind, Width = route.Width,
            Points = new List<Vector3>(route.CenterLine),
        }));
    }

    public WorldManifest Manifest { get; }

    public TerrainSurfaceSample SampleTerrain(float worldX, float worldZ)
    {
        float d = MathF.Max(_terrain.CellSize, 0.05f);
        float left = _terrain.SampleHeight(worldX - d, worldZ);
        float right = _terrain.SampleHeight(worldX + d, worldZ);
        float back = _terrain.SampleHeight(worldX, worldZ - d);
        float front = _terrain.SampleHeight(worldX, worldZ + d);
        Vector3 normal = Vector3.Normalize(new Vector3(left - right, d * 2f, back - front));
        float slope = MathF.Acos(Math.Clamp(normal.Y, -1f, 1f)) / (MathF.PI * 0.5f);
        int x = Math.Clamp((int)MathF.Round((worldX - _terrain.OriginX) / _terrain.CellSize), 0, _terrain.ResolutionX - 1);
        int z = Math.Clamp((int)MathF.Round((worldZ - _terrain.OriginZ) / _terrain.CellSize), 0, _terrain.ResolutionZ - 1);
        (byte r, byte g, byte b, byte a) = _terrain.GetSplat(x, z);
        Vector4 splat = new(r / 255f, g / 255f, b / 255f, a / 255f);
        TerrainPathSample path = _paths.Sample(worldX, worldZ);
        float height = _terrain.SampleHeight(worldX, worldZ);
        float elevation = Math.Clamp((height - _terrain.MinHeight) / MathF.Max(_terrain.MaxHeight - _terrain.MinHeight, 0.001f), 0f, 1f);
        float moistureNoise = 0.5f + MathF.Sin(worldX * 0.021f + MathF.Cos(worldZ * 0.017f) * 2f) * 0.5f;
        float moisture = Math.Clamp(moistureNoise * (1f - elevation * 0.6f), 0f, 1f);
        return new TerrainSurfaceSample(height, normal, slope, splat, path.Mask(), moisture, Math.Clamp(slope * 1.4f + splat.Z * 0.5f, 0f, 1f));
    }

    public TerrainWaterDefinition WaterAt(float worldX, float worldZ) => Manifest.WaterBodies
        .Where(water => MathF.Abs(worldX - water.Center.X) <= water.SizeX * 0.5f && MathF.Abs(worldZ - water.Center.Z) <= water.SizeZ * 0.5f)
        .OrderBy(water => Vector2.DistanceSquared(new Vector2(worldX, worldZ), new Vector2(water.Center.X, water.Center.Z)))
        .FirstOrDefault();

    public float? WaterSurfaceHeightAt(float worldX, float worldZ)
    {
        TerrainWaterDefinition water = WaterAt(worldX, worldZ);
        if (water == null) return null;
        _waterOffsets.TryGetValue(water.Id, out float offset);
        return water.SurfaceHeight + offset;
    }

    public float WaterDepthAt(float worldX, float worldZ)
    {
        float? surface = WaterSurfaceHeightAt(worldX, worldZ);
        return surface.HasValue ? MathF.Max(0f, surface.Value - _terrain.SampleHeight(worldX, worldZ)) : 0f;
    }

    public void SetWaterLevelOffset(string waterBodyId, float offset)
    {
        if (Manifest.WaterBodies.TrueForAll(water => !string.Equals(water.Id, waterBodyId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Unknown water body '{waterBodyId}'.", nameof(waterBodyId));
        _waterOffsets[waterBodyId] = Math.Clamp(offset, -100f, 100f);
    }

    public TerrainPathSample SamplePath(float worldX, float worldZ) => _paths.Sample(worldX, worldZ);

    public ShelterSample SampleShelter(Vector3 position, Vector3 windDirection)
    {
        Vector2 wind = new(windDirection.X, windDirection.Z);
        if (wind.LengthSquared() > 1e-6f) wind = Vector2.Normalize(wind);
        float rain = 0f, windBlock = 0f, canopy = 0f;
        bool solid = false, cave = false;
        string dominant = null;
        foreach (WorldShelter shelter in Manifest.Shelters)
        {
            Vector2 delta = new(position.X - shelter.Center.X, position.Z - shelter.Center.Z);
            bool covered = MathF.Abs(delta.X) <= shelter.HalfExtents.X && MathF.Abs(delta.Y) <= shelter.HalfExtents.Z &&
                           position.Y <= shelter.Center.Y + shelter.HalfExtents.Y;
            Vector2 upwind = new Vector2(position.X, position.Z) - wind * MathF.Max(shelter.HalfExtents.X, shelter.HalfExtents.Z);
            bool blocked = covered || (MathF.Abs(upwind.X - shelter.Center.X) <= shelter.HalfExtents.X && MathF.Abs(upwind.Y - shelter.Center.Z) <= shelter.HalfExtents.Z);
            if (!covered && !blocked) continue;
            bool isSolid = shelter.Kind is ShelterKind.Cave or ShelterKind.Building or ShelterKind.RockOverhang;
            float nextRain = covered ? (isSolid ? 1f : 1f - MathF.Exp(-2.2f * shelter.CanopyDensity)) : 0f;
            float nextWind = blocked ? 1f - Math.Clamp(shelter.WindPermeability, 0f, 1f) : 0f;
            if (nextRain > rain || nextWind > windBlock) dominant = shelter.Id;
            rain = MathF.Max(rain, nextRain); windBlock = MathF.Max(windBlock, nextWind);
            canopy = MathF.Max(canopy, shelter.CanopyDensity);
            solid |= covered && isSolid; cave |= covered && shelter.Kind == ShelterKind.Cave;
        }
        return new ShelterSample(rain, windBlock, canopy, solid, cave, dominant);
    }

    public WorldChunkCoordinate ChunkAt(float worldX, float worldZ)
    {
        int x = Math.Clamp((int)MathF.Floor((worldX - Manifest.Bounds.Minimum.X) / Manifest.ChunkSize), 0,
            Math.Max(0, (int)MathF.Ceiling(Manifest.Bounds.Size.X / Manifest.ChunkSize) - 1));
        int z = Math.Clamp((int)MathF.Floor((worldZ - Manifest.Bounds.Minimum.Y) / Manifest.ChunkSize), 0,
            Math.Max(0, (int)MathF.Ceiling(Manifest.Bounds.Size.Y / Manifest.ChunkSize) - 1));
        return new WorldChunkCoordinate(x, z);
    }

    public IReadOnlyList<WorldPointOfInterest> PointsNear(Vector3 position, float radius)
    {
        float squared = MathF.Max(0f, radius) * MathF.Max(0f, radius);
        return Manifest.PointsOfInterest.Where(poi => Vector3.DistanceSquared(position, poi.Position) <= squared)
            .OrderBy(poi => Vector3.DistanceSquared(position, poi.Position)).ToArray();
    }
}
