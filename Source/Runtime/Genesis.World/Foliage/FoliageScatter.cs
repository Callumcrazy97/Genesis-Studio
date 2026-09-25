using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Genesis.World.Terrain;

namespace Genesis.World.Foliage;

public enum FoliageSpecies
{
    MeadowGrass,
    TallGrass,
    Fern,
    Shrub,
    Sapling,
    Wildflower,
    Reed,
}

public enum FoliagePreset
{
    Meadow,
    TemperateForest,
    Alpine,
    Wetland,
    SparseScrub,
}

public sealed class FoliageScatterSettings
{
    public const int EstimatedResidentBytesPerInstance = 40;

    public int Seed { get; set; } = 731947;
    public FoliagePreset Preset { get; set; } = FoliagePreset.TemperateForest;
    public int MaximumInstances { get; set; } = 6000;
    public float Density { get; set; } = 0.62f;
    public float MinimumSpacing { get; set; } = 0.8f;
    public float PathExclusion { get; set; } = 1.35f;
    public float MaximumSlopeDegrees { get; set; } = 38f;
    public float NearDistance { get; set; } = 42f;
    public float FarDistance { get; set; } = 110f;
    public float StreamingCellSize { get; set; } = 24f;
    public int VisibleInstanceBudget { get; set; } = 20000;
    public int TriangleBudget { get; set; } = 1_000_000;
    public float ResidentMemoryBudgetMegabytes { get; set; } = 16f;
    public float GpuUploadBudgetMegabytes { get; set; } = 4f;
    public float TargetGpuMilliseconds { get; set; } = 4f;

    public void Normalize()
    {
        MaximumInstances = Math.Clamp(MaximumInstances, 0, 250000);
        Density = Math.Clamp(Density, 0f, 1f);
        MinimumSpacing = Math.Clamp(MinimumSpacing, 0.15f, 32f);
        PathExclusion = Math.Clamp(PathExclusion, 0f, 8f);
        MaximumSlopeDegrees = Math.Clamp(MaximumSlopeDegrees, 0f, 89f);
        NearDistance = Math.Clamp(NearDistance, 1f, 10000f);
        FarDistance = Math.Clamp(FarDistance, NearDistance + 1f, 20000f);
        StreamingCellSize = Math.Clamp(StreamingCellSize, 4f, 512f);
        VisibleInstanceBudget = Math.Clamp(VisibleInstanceBudget, 128, 32768);
        TriangleBudget = Math.Clamp(TriangleBudget, 1000, 50_000_000);
        ResidentMemoryBudgetMegabytes = Math.Clamp(ResidentMemoryBudgetMegabytes, 0.25f, 1024f);
        GpuUploadBudgetMegabytes = Math.Clamp(GpuUploadBudgetMegabytes, 0.25f, 64f);
        TargetGpuMilliseconds = Math.Clamp(TargetGpuMilliseconds, 0.25f, 33.3f);

        int memoryLimitedInstances = (int)Math.Min(
            250000,
            ResidentMemoryBudgetMegabytes * 1024f * 1024f / EstimatedResidentBytesPerInstance);
        MaximumInstances = Math.Min(MaximumInstances, Math.Max(0, memoryLimitedInstances));
    }
}

public readonly record struct FoliageInstance(
    Vector3 Position,
    float Scale,
    float Rotation,
    FoliageSpecies Species,
    float WindExposure,
    float HueVariation);

public sealed class FoliageField
{
    public int Seed { get; init; }
    public FoliagePreset Preset { get; init; }
    public IReadOnlyList<FoliageInstance> Instances { get; init; } = Array.Empty<FoliageInstance>();
    public Vector2 Minimum { get; init; }
    public Vector2 Maximum { get; init; }
}

/// <summary>
/// Deterministic, path-aware ecological scatter. Placement is a jittered lattice, so lowering a
/// quality budget retains representative coverage instead of chopping a random cloud in half.
/// </summary>
public static class FoliageScatter
{
    public static FoliageField Generate(
        TerrainAsset terrain,
        TerrainPathNetwork paths,
        FoliageScatterSettings settings,
        IReadOnlyList<TerrainWaterDefinition> waters = null)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        settings ??= new FoliageScatterSettings();
        settings.Normalize();
        paths ??= new TerrainPathNetwork(Array.Empty<TerrainPathDefinition>());
        waters ??= Array.Empty<TerrainWaterDefinition>();

        float width = (terrain.ResolutionX - 1) * terrain.CellSize;
        float depth = (terrain.ResolutionZ - 1) * terrain.CellSize;
        float spacing = settings.MinimumSpacing / MathF.Max(0.35f, MathF.Sqrt(MathF.Max(settings.Density, 0.02f)));
        int cellsX = Math.Max(1, (int)MathF.Ceiling(width / spacing));
        int cellsZ = Math.Max(1, (int)MathF.Ceiling(depth / spacing));
        var candidates = new List<(uint Hash, int X, int Z)>(cellsX * cellsZ);
        for (int z = 0; z < cellsZ; z++)
        for (int x = 0; x < cellsX; x++)
            candidates.Add((Hash(unchecked((uint)(settings.Seed ^ x * 73856093 ^ z * 19349663))), x, z));
        candidates.Sort(static (a, b) => a.Hash.CompareTo(b.Hash));

        float maxSlope = settings.MaximumSlopeDegrees * MathF.PI / 180f;
        var result = new List<FoliageInstance>(Math.Min(settings.MaximumInstances, candidates.Count));
        var occupied = new Dictionary<(int X, int Z), List<Vector2>>();
        foreach ((uint hash, int cellX, int cellZ) in candidates)
        {
            if (result.Count >= settings.MaximumInstances) break;
            float jx = Random01(hash + 1u);
            float jz = Random01(hash + 2u);
            float worldX = terrain.OriginX + MathF.Min(width, (cellX + 0.12f + jx * 0.76f) * spacing);
            float worldZ = terrain.OriginZ + MathF.Min(depth, (cellZ + 0.12f + jz * 0.76f) * spacing);
            float chance = settings.Density * HabitatProbability(terrain, settings.Preset, worldX, worldZ);
            if (Random01(hash + 3u) > chance) continue;

            Vector3 normal = SampleNormal(terrain, worldX, worldZ);
            float slope = MathF.Acos(Math.Clamp(normal.Y, -1f, 1f));
            if (slope > maxSlope) continue;
            TerrainPathSample path = paths.Sample(worldX, worldZ);
            if (path.IsValid && path.Distance < path.Width * settings.PathExclusion) continue;
            if (TerrainWaterDefinition.ContainsAny(waters, worldX, worldZ, TerrainWaterDefinition.FoliageExclusionPadding)) continue;

            Vector2 horizontal = new(worldX, worldZ);
            if (!HasSpacing(horizontal, settings.MinimumSpacing, occupied)) continue;
            AddOccupied(horizontal, settings.MinimumSpacing, occupied);

            FoliageSpecies species = SelectSpecies(settings.Preset, hash);
            float scale = SpeciesScale(species) * (0.78f + Random01(hash + 4u) * 0.46f);
            float shelter = EstimateShelter(terrain, worldX, worldZ, normal);
            result.Add(new FoliageInstance(
                new Vector3(worldX, terrain.SampleHeight(worldX, worldZ), worldZ),
                scale,
                Random01(hash + 5u) * MathF.Tau,
                species,
                1f - shelter,
                Random01(hash + 6u) * 2f - 1f));
        }

        return new FoliageField
        {
            Seed = settings.Seed,
            Preset = settings.Preset,
            Instances = result,
            Minimum = new Vector2(terrain.OriginX, terrain.OriginZ),
            Maximum = new Vector2(terrain.OriginX + width, terrain.OriginZ + depth),
        };
    }

    /// <summary>Drops instances whose XZ falls inside any authored water ellipse. Returns <paramref name="source"/> when nothing changes.</summary>
    public static FoliageField ExcludeWater(FoliageField source, IReadOnlyList<TerrainWaterDefinition> waters)
    {
        if (source?.Instances == null || source.Instances.Count == 0) return source;
        if (waters == null || waters.Count == 0) return source;

        var kept = new List<FoliageInstance>(source.Instances.Count);
        foreach (FoliageInstance instance in source.Instances)
        {
            if (!TerrainWaterDefinition.ContainsAny(waters, instance.Position.X, instance.Position.Z, TerrainWaterDefinition.FoliageExclusionPadding))
                kept.Add(instance);
        }

        if (kept.Count == source.Instances.Count) return source;
        return new FoliageField
        {
            Seed = source.Seed,
            Preset = source.Preset,
            Instances = kept,
            Minimum = source.Minimum,
            Maximum = source.Maximum,
        };
    }

    /// <summary>
    /// Strips instances that enter newly added water ellipses into <paramref name="held"/>, and
    /// restores held instances when those bodies are removed so duplicate/delete stays foliage-neutral.
    /// </summary>
    public static FoliageField ReconcileWithWater(
        FoliageField source,
        IReadOnlyList<TerrainWaterDefinition> before,
        IReadOnlyList<TerrainWaterDefinition> after,
        Dictionary<string, HeldFoliageInstance[]> held)
    {
        source ??= new FoliageField { Instances = Array.Empty<FoliageInstance>() };
        held ??= new Dictionary<string, HeldFoliageInstance[]>(StringComparer.OrdinalIgnoreCase);
        before ??= Array.Empty<TerrainWaterDefinition>();
        after ??= Array.Empty<TerrainWaterDefinition>();

        var beforeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TerrainWaterDefinition water in before)
        {
            if (water != null && !string.IsNullOrWhiteSpace(water.Id))
                beforeIds.Add(water.Id);
        }

        var afterBodies = new List<TerrainWaterDefinition>();
        var afterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TerrainWaterDefinition water in after)
        {
            if (water == null || string.IsNullOrWhiteSpace(water.Id)) continue;
            afterBodies.Add(water);
            afterIds.Add(water.Id);
        }

        var instances = new List<FoliageInstance>(source.Instances ?? Array.Empty<FoliageInstance>());
        foreach (string id in beforeIds)
        {
            if (afterIds.Contains(id)) continue;
            if (!held.TryGetValue(id, out HeldFoliageInstance[] stored) || stored == null)
            {
                held.Remove(id);
                continue;
            }

            Array.Sort(stored, static (a, b) => a.Index.CompareTo(b.Index));
            foreach (HeldFoliageInstance heldInstance in stored)
            {
                if (TerrainWaterDefinition.ContainsAny(
                    afterBodies,
                    heldInstance.Instance.Position.X,
                    heldInstance.Instance.Position.Z,
                    TerrainWaterDefinition.FoliageExclusionPadding))
                {
                    continue;
                }

                int insert = Math.Clamp(heldInstance.Index, 0, instances.Count);
                instances.Insert(insert, heldInstance.Instance);
            }
            held.Remove(id);
        }

        foreach (TerrainWaterDefinition water in afterBodies)
        {
            if (beforeIds.Contains(water.Id)) continue;
            var captured = new List<HeldFoliageInstance>();
            var kept = new List<FoliageInstance>(instances.Count);
            for (int i = 0; i < instances.Count; i++)
            {
                FoliageInstance instance = instances[i];
                if (water.ContainsHorizontal(instance.Position.X, instance.Position.Z, TerrainWaterDefinition.FoliageExclusionPadding))
                    captured.Add(new HeldFoliageInstance(i, instance));
                else
                    kept.Add(instance);
            }

            if (captured.Count > 0)
                held[water.Id] = captured.ToArray();
            instances = kept;
        }

        return new FoliageField
        {
            Seed = source.Seed,
            Preset = source.Preset,
            Instances = instances,
            Minimum = source.Minimum,
            Maximum = source.Maximum,
        };
    }

    private static float HabitatProbability(TerrainAsset terrain, FoliagePreset preset, float x, float z)
    {
        float h = terrain.SampleHeight(x, z);
        float heightT = Math.Clamp((h - terrain.MinHeight) / MathF.Max(terrain.MaxHeight - terrain.MinHeight, 0.001f), 0f, 1f);
        int gx = Math.Clamp((int)MathF.Round((x - terrain.OriginX) / terrain.CellSize), 0, terrain.ResolutionX - 1);
        int gz = Math.Clamp((int)MathF.Round((z - terrain.OriginZ) / terrain.CellSize), 0, terrain.ResolutionZ - 1);
        (byte grass, byte dirt, byte rock, byte snow) = terrain.GetSplat(gx, gz);
        float green = grass / 255f;
        float wetNoise = 0.5f + 0.5f * MathF.Sin(x * 0.061f + MathF.Cos(z * 0.043f) * 1.7f);
        return preset switch
        {
            FoliagePreset.Meadow => green * (0.65f + wetNoise * 0.35f),
            FoliagePreset.TemperateForest => green * (0.35f + wetNoise * 0.65f) * (1f - heightT * 0.45f),
            FoliagePreset.Alpine => (rock / 255f * 0.35f + green * 0.65f) * Smooth(0.35f, 0.82f, heightT),
            FoliagePreset.Wetland => green * Smooth(0.38f, 0.76f, wetNoise) * (1f - heightT * 0.7f),
            _ => Math.Clamp((dirt + grass) / 510f, 0.1f, 0.8f) * 0.55f,
        };
    }

    private static FoliageSpecies SelectSpecies(FoliagePreset preset, uint hash)
    {
        float value = Random01(hash + 17u);
        return preset switch
        {
            FoliagePreset.Meadow => value < 0.58f ? FoliageSpecies.MeadowGrass : value < 0.82f ? FoliageSpecies.Wildflower : FoliageSpecies.TallGrass,
            FoliagePreset.TemperateForest => value < 0.36f ? FoliageSpecies.Fern : value < 0.63f ? FoliageSpecies.Shrub : value < 0.82f ? FoliageSpecies.Sapling : FoliageSpecies.TallGrass,
            FoliagePreset.Alpine => value < 0.52f ? FoliageSpecies.Shrub : value < 0.78f ? FoliageSpecies.Wildflower : FoliageSpecies.MeadowGrass,
            FoliagePreset.Wetland => value < 0.62f ? FoliageSpecies.Reed : value < 0.82f ? FoliageSpecies.TallGrass : FoliageSpecies.Wildflower,
            _ => value < 0.58f ? FoliageSpecies.Shrub : FoliageSpecies.MeadowGrass,
        };
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

    private static float Smooth(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / MathF.Max(edge1 - edge0, 0.001f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static int FastFloor(float value) { int i = (int)value; return value < i ? i - 1 : i; }
    private static float Random01(uint value) => Hash(value) * (1f / uint.MaxValue);
    private static uint Hash(uint value)
    {
        value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15; value *= 0x846ca68bu;
        return value ^ (value >> 16);
    }
}

public readonly struct HeldFoliageInstance
{
    public HeldFoliageInstance(int index, FoliageInstance instance)
    {
        Index = index;
        Instance = instance;
    }

    public int Index { get; }
    public FoliageInstance Instance { get; }
}

public static class FoliageFieldCache
{
    private const int Magic = 0x4C4F4647; // GFOL
    private const int Version = 1;

    public static string Save(string path, FoliageField field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(field);
        string absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        string temporary = absolute + ".tmp";
        using (FileStream stream = File.Create(temporary))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic); writer.Write(Version); writer.Write(field.Seed); writer.Write((int)field.Preset);
            writer.Write(field.Minimum.X); writer.Write(field.Minimum.Y); writer.Write(field.Maximum.X); writer.Write(field.Maximum.Y);
            writer.Write(field.Instances.Count);
            foreach (FoliageInstance instance in field.Instances)
            {
                writer.Write(instance.Position.X); writer.Write(instance.Position.Y); writer.Write(instance.Position.Z);
                writer.Write(instance.Scale); writer.Write(instance.Rotation); writer.Write((byte)instance.Species);
                writer.Write(instance.WindExposure); writer.Write(instance.HueVariation);
            }
        }
        File.Move(temporary, absolute, true);
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolute)));
    }

    public static FoliageField Load(string path, string expectedSha256 = null)
    {
        string absolute = Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolute)));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Foliage cache digest does not match its manifest.");
        }
        using FileStream stream = File.OpenRead(absolute);
        using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
            throw new InvalidDataException("Unsupported Genesis foliage cache.");
        int seed = reader.ReadInt32();
        FoliagePreset preset = (FoliagePreset)reader.ReadInt32();
        Vector2 minimum = new(reader.ReadSingle(), reader.ReadSingle());
        Vector2 maximum = new(reader.ReadSingle(), reader.ReadSingle());
        int count = reader.ReadInt32();
        if (count is < 0 or > 250000) throw new InvalidDataException("Foliage instance count is invalid.");
        var instances = new FoliageInstance[count];
        for (int i = 0; i < count; i++)
            instances[i] = new FoliageInstance(
                new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                reader.ReadSingle(), reader.ReadSingle(), (FoliageSpecies)reader.ReadByte(),
                reader.ReadSingle(), reader.ReadSingle());
        if (stream.Position != stream.Length) throw new InvalidDataException("Foliage cache contains trailing data.");
        return new FoliageField { Seed = seed, Preset = preset, Minimum = minimum, Maximum = maximum, Instances = instances };
    }
}
