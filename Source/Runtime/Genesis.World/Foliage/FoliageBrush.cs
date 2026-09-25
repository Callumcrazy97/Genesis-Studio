using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.World.Terrain;

namespace Genesis.World.Foliage;

/// <summary>The regional authoring operation applied by the Terrain Editor foliage brush.</summary>
public enum FoliageBrushMode
{
    Paint,
    Place,
    Erase,
}

/// <summary>
/// Serializable-free brush state. The authored result is stored in the canonical foliage cache;
/// these values only describe the current editor tool.
/// </summary>
public sealed class FoliageBrushSettings
{
    public FoliageBrushMode Mode { get; set; } = FoliageBrushMode.Paint;
    public FoliageSpecies Species { get; set; } = FoliageSpecies.MeadowGrass;
    public float Radius { get; set; } = 12f;
    public float Density { get; set; } = 0.7f;

    public void Normalize()
    {
        if (!Enum.IsDefined(Mode)) Mode = FoliageBrushMode.Paint;
        if (!Enum.IsDefined(Species)) Species = FoliageSpecies.MeadowGrass;
        Radius = Math.Clamp(Radius, 0.5f, 256f);
        Density = Math.Clamp(Density, 0f, 1f);
    }
}

/// <summary>One immutable regional edit and its useful authoring diagnostics.</summary>
public readonly record struct FoliageBrushResult(
    FoliageField Field,
    int AddedInstances,
    int RemovedInstances)
{
    public bool Changed => AddedInstances != 0 || RemovedInstances != 0;
}

/// <summary>
/// Deterministic regional foliage authoring shared by the editor and runtime acceptance tests.
/// Paint replaces the circular region with the selected species at the requested density, erase
/// clears it, and place adds the nearest legal individual. All operations preserve instances
/// outside their region and enforce the scatter asset's slope, path, spacing, and capacity rules.
/// </summary>
public static class FoliageBrush
{
    private const int PlaceCandidateCount = 64;

    public static FoliageBrushResult Apply(
        TerrainAsset terrain,
        TerrainPathNetwork paths,
        FoliageField source,
        FoliageScatterSettings scatterSettings,
        Vector2 center,
        FoliageBrushSettings brushSettings,
        IReadOnlyList<TerrainWaterDefinition> waters = null)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        source ??= EmptyField(terrain, scatterSettings);
        paths ??= new TerrainPathNetwork(Array.Empty<TerrainPathDefinition>());
        scatterSettings ??= new FoliageScatterSettings();
        brushSettings ??= new FoliageBrushSettings();
        scatterSettings.Normalize();
        brushSettings.Normalize();
        waters ??= Array.Empty<TerrainWaterDefinition>();

        Vector2 minimum = new(terrain.OriginX, terrain.OriginZ);
        Vector2 maximum = new(
            terrain.OriginX + (terrain.ResolutionX - 1) * terrain.CellSize,
            terrain.OriginZ + (terrain.ResolutionZ - 1) * terrain.CellSize);
        center = Vector2.Clamp(center, minimum, maximum);

        return brushSettings.Mode switch
        {
            FoliageBrushMode.Erase => Erase(source, center, brushSettings.Radius, minimum, maximum),
            FoliageBrushMode.Place => Place(
                terrain, paths, source, scatterSettings, brushSettings, center, minimum, maximum, waters),
            _ => Paint(
                terrain, paths, source, scatterSettings, brushSettings, center, minimum, maximum, waters),
        };
    }

    private static FoliageBrushResult Erase(
        FoliageField source,
        Vector2 center,
        float radius,
        Vector2 minimum,
        Vector2 maximum)
    {
        float radiusSquared = radius * radius;
        var survivors = new List<FoliageInstance>(source.Instances.Count);
        foreach (FoliageInstance instance in source.Instances)
        {
            Vector2 horizontal = new(instance.Position.X, instance.Position.Z);
            if (Vector2.DistanceSquared(horizontal, center) > radiusSquared)
                survivors.Add(instance);
        }

        int removed = source.Instances.Count - survivors.Count;
        if (removed == 0) return new FoliageBrushResult(source, 0, 0);
        return new FoliageBrushResult(CloneField(source, survivors, minimum, maximum), 0, removed);
    }

    private static FoliageBrushResult Paint(
        TerrainAsset terrain,
        TerrainPathNetwork paths,
        FoliageField source,
        FoliageScatterSettings scatter,
        FoliageBrushSettings brush,
        Vector2 center,
        Vector2 minimum,
        Vector2 maximum,
        IReadOnlyList<TerrainWaterDefinition> waters)
    {
        float radiusSquared = brush.Radius * brush.Radius;
        float occupancyRadius = brush.Radius + scatter.MinimumSpacing;
        float occupancyRadiusSquared = occupancyRadius * occupancyRadius;
        var result = new List<FoliageInstance>(source.Instances.Count + 128);
        var occupied = new Dictionary<(int X, int Z), List<Vector2>>();
        int removed = 0;

        // Only nearby survivors need indexing for candidate spacing. This keeps regional editing
        // responsive even when the canonical field contains hundreds of thousands of instances.
        foreach (FoliageInstance instance in source.Instances)
        {
            Vector2 horizontal = new(instance.Position.X, instance.Position.Z);
            float distanceSquared = Vector2.DistanceSquared(horizontal, center);
            if (distanceSquared <= radiusSquared)
            {
                removed++;
                continue;
            }

            result.Add(instance);
            if (distanceSquared <= occupancyRadiusSquared)
                AddOccupied(horizontal, scatter.MinimumSpacing, occupied);
        }

        if (brush.Density <= 0f || result.Count >= scatter.MaximumInstances)
        {
            if (removed == 0) return new FoliageBrushResult(source, 0, 0);
            return new FoliageBrushResult(CloneField(source, result, minimum, maximum), 0, removed);
        }

        float spacing = scatter.MinimumSpacing;
        int minCellX = FastFloor((center.X - brush.Radius - terrain.OriginX) / spacing) - 1;
        int maxCellX = FastFloor((center.X + brush.Radius - terrain.OriginX) / spacing) + 1;
        int minCellZ = FastFloor((center.Y - brush.Radius - terrain.OriginZ) / spacing) - 1;
        int maxCellZ = FastFloor((center.Y + brush.Radius - terrain.OriginZ) / spacing) + 1;
        var candidates = new List<(uint Hash, int X, int Z)>((maxCellX - minCellX + 1) * (maxCellZ - minCellZ + 1));
        uint speciesSalt = unchecked((uint)((int)brush.Species + 1) * 0x9e3779b9);
        for (int z = minCellZ; z <= maxCellZ; z++)
        for (int x = minCellX; x <= maxCellX; x++)
        {
            uint hash = Hash(unchecked((uint)(source.Seed ^ x * 73856093 ^ z * 19349663)) ^ speciesSalt);
            candidates.Add((hash, x, z));
        }
        candidates.Sort(static (a, b) => a.Hash.CompareTo(b.Hash));

        int added = 0;
        foreach ((uint hash, int cellX, int cellZ) in candidates)
        {
            if (result.Count >= scatter.MaximumInstances) break;
            if (Random01(hash + 3u) > brush.Density) continue;
            float worldX = terrain.OriginX + (cellX + 0.12f + Random01(hash + 1u) * 0.76f) * spacing;
            float worldZ = terrain.OriginZ + (cellZ + 0.12f + Random01(hash + 2u) * 0.76f) * spacing;
            var horizontal = new Vector2(worldX, worldZ);
            if (Vector2.DistanceSquared(horizontal, center) > radiusSquared) continue;
            if (!IsLegal(terrain, paths, scatter, waters, horizontal, minimum, maximum, out Vector3 normal)) continue;
            if (!HasSpacing(horizontal, spacing, occupied)) continue;

            AddOccupied(horizontal, spacing, occupied);
            result.Add(CreateInstance(terrain, brush.Species, horizontal, normal, hash));
            added++;
        }

        if (added == 0 && removed == 0) return new FoliageBrushResult(source, 0, 0);
        return new FoliageBrushResult(CloneField(source, result, minimum, maximum), added, removed);
    }

    private static FoliageBrushResult Place(
        TerrainAsset terrain,
        TerrainPathNetwork paths,
        FoliageField source,
        FoliageScatterSettings scatter,
        FoliageBrushSettings brush,
        Vector2 center,
        Vector2 minimum,
        Vector2 maximum,
        IReadOnlyList<TerrainWaterDefinition> waters)
    {
        if (source.Instances.Count >= scatter.MaximumInstances)
            return new FoliageBrushResult(source, 0, 0);

        uint centerHash = Hash(unchecked((uint)(source.Seed
            ^ FastFloor(center.X * 100f) * 73856093
            ^ FastFloor(center.Y * 100f) * 19349663
            ^ ((int)brush.Species + 1) * 83492791)));
        for (int i = 0; i < PlaceCandidateCount; i++)
        {
            // Candidate zero is exactly under the cursor. Remaining candidates form a stable
            // sunflower spiral so a click near a path or neighbour finds the closest legal point.
            float t = i == 0 ? 0f : MathF.Sqrt(i / (float)(PlaceCandidateCount - 1));
            float angle = i * 2.39996323f + Random01(centerHash) * MathF.Tau;
            Vector2 horizontal = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (brush.Radius * t);
            if (!IsLegal(terrain, paths, scatter, waters, horizontal, minimum, maximum, out Vector3 normal)) continue;
            bool spaced = true;
            float spacingSquared = scatter.MinimumSpacing * scatter.MinimumSpacing;
            foreach (FoliageInstance instance in source.Instances)
            {
                float dx = instance.Position.X - horizontal.X;
                float dz = instance.Position.Z - horizontal.Y;
                if (dx * dx + dz * dz < spacingSquared)
                {
                    spaced = false;
                    break;
                }
            }
            if (!spaced) continue;

            var instances = new List<FoliageInstance>(source.Instances.Count + 1);
            instances.AddRange(source.Instances);
            instances.Add(CreateInstance(terrain, brush.Species, horizontal, normal, Hash(centerHash + (uint)i + 1u)));
            return new FoliageBrushResult(CloneField(source, instances, minimum, maximum), 1, 0);
        }

        return new FoliageBrushResult(source, 0, 0);
    }

    private static bool IsLegal(
        TerrainAsset terrain,
        TerrainPathNetwork paths,
        FoliageScatterSettings scatter,
        IReadOnlyList<TerrainWaterDefinition> waters,
        Vector2 horizontal,
        Vector2 minimum,
        Vector2 maximum,
        out Vector3 normal)
    {
        normal = Vector3.UnitY;
        if (horizontal.X < minimum.X || horizontal.X > maximum.X
            || horizontal.Y < minimum.Y || horizontal.Y > maximum.Y)
            return false;
        if (TerrainWaterDefinition.ContainsAny(waters, horizontal.X, horizontal.Y, TerrainWaterDefinition.FoliageExclusionPadding)) return false;

        normal = SampleNormal(terrain, horizontal.X, horizontal.Y);
        float maximumSlope = scatter.MaximumSlopeDegrees * MathF.PI / 180f;
        if (MathF.Acos(Math.Clamp(normal.Y, -1f, 1f)) > maximumSlope) return false;
        TerrainPathSample path = paths.Sample(horizontal.X, horizontal.Y);
        return !path.IsValid || path.Distance >= path.Width * scatter.PathExclusion;
    }

    private static FoliageInstance CreateInstance(
        TerrainAsset terrain,
        FoliageSpecies species,
        Vector2 horizontal,
        Vector3 normal,
        uint hash)
    {
        float scale = SpeciesScale(species) * (0.78f + Random01(hash + 4u) * 0.46f);
        float shelter = EstimateShelter(terrain, horizontal.X, horizontal.Y, normal);
        return new FoliageInstance(
            new Vector3(horizontal.X, terrain.SampleHeight(horizontal.X, horizontal.Y), horizontal.Y),
            scale,
            Random01(hash + 5u) * MathF.Tau,
            species,
            1f - shelter,
            Random01(hash + 6u) * 2f - 1f);
    }

    private static FoliageField CloneField(
        FoliageField source,
        IReadOnlyList<FoliageInstance> instances,
        Vector2 minimum,
        Vector2 maximum) => new()
    {
        Seed = source.Seed,
        Preset = source.Preset,
        Instances = instances,
        Minimum = minimum,
        Maximum = maximum,
    };

    private static FoliageField EmptyField(TerrainAsset terrain, FoliageScatterSettings settings)
    {
        settings ??= new FoliageScatterSettings();
        return new FoliageField
        {
            Seed = settings.Seed,
            Preset = settings.Preset,
            Instances = Array.Empty<FoliageInstance>(),
            Minimum = new Vector2(terrain.OriginX, terrain.OriginZ),
            Maximum = new Vector2(
                terrain.OriginX + (terrain.ResolutionX - 1) * terrain.CellSize,
                terrain.OriginZ + (terrain.ResolutionZ - 1) * terrain.CellSize),
        };
    }

    private static Vector3 SampleNormal(TerrainAsset terrain, float x, float z)
    {
        float d = MathF.Max(terrain.CellSize, 0.1f);
        float left = terrain.SampleHeight(x - d, z);
        float right = terrain.SampleHeight(x + d, z);
        float back = terrain.SampleHeight(x, z - d);
        float front = terrain.SampleHeight(x, z + d);
        return Vector3.Normalize(new Vector3(left - right, d * 2f, back - front));
    }

    private static float EstimateShelter(TerrainAsset terrain, float x, float z, Vector3 normal)
    {
        float d = terrain.CellSize * 5f;
        float upwind = terrain.SampleHeight(x - d, z + d * 0.3f) - terrain.SampleHeight(x, z);
        return Math.Clamp(upwind / MathF.Max(d, 0.001f) * 2.5f + (1f - normal.Y), 0f, 1f);
    }

    private static float SpeciesScale(FoliageSpecies species) => species switch
    {
        FoliageSpecies.MeadowGrass => 0.55f,
        FoliageSpecies.TallGrass => 0.95f,
        FoliageSpecies.Fern => 0.85f,
        FoliageSpecies.Shrub => 1.35f,
        FoliageSpecies.Sapling => 2.6f,
        FoliageSpecies.Wildflower => 0.62f,
        FoliageSpecies.Reed => 1.2f,
        _ => 1f,
    };

    private static bool HasSpacing(Vector2 point, float spacing, Dictionary<(int X, int Z), List<Vector2>> occupied)
    {
        int cx = FastFloor(point.X / spacing);
        int cz = FastFloor(point.Y / spacing);
        float squared = spacing * spacing;
        for (int z = cz - 1; z <= cz + 1; z++)
        for (int x = cx - 1; x <= cx + 1; x++)
            if (occupied.TryGetValue((x, z), out List<Vector2> bucket))
                foreach (Vector2 other in bucket)
                    if (Vector2.DistanceSquared(point, other) < squared) return false;
        return true;
    }

    private static void AddOccupied(Vector2 point, float spacing, Dictionary<(int X, int Z), List<Vector2>> occupied)
    {
        var key = (FastFloor(point.X / spacing), FastFloor(point.Y / spacing));
        if (!occupied.TryGetValue(key, out List<Vector2> bucket)) occupied[key] = bucket = new List<Vector2>();
        bucket.Add(point);
    }

    private static int FastFloor(float value) { int i = (int)value; return value < i ? i - 1 : i; }
    private static float Random01(uint value) => Hash(value) * (1f / uint.MaxValue);
    private static uint Hash(uint value)
    {
        value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15; value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }
}
