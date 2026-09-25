using Genesis.World;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>The generation presets the New-Terrain wizard offers. Each is a distinct heightfield
/// recipe (not just a different seed) so the choice visibly changes the landscape shape.</summary>
public enum TerrainPreset
{
    Flatlands,
    RollingHills,
    Hills,
    Mountains,
    ErodedMountains,
    RidgeValleys,
    Badlands,
    Volcanic,
    Islands,
    Canyon,
}

/// <summary>Parameters that fully describe a terrain to generate — everything the wizard collects.</summary>
public sealed class TerrainGenParams
{
    public int ResolutionX { get; set; } = 129;
    public int ResolutionZ { get; set; } = 129;
    public float CellSize { get; set; } = 1f;
    public float MinHeight { get; set; } = -24f;
    public float MaxHeight { get; set; } = 72f;
    public TerrainPreset Preset { get; set; } = TerrainPreset.RollingHills;
    public int Seed { get; set; } = 1337;
    public int ErosionIterations { get; set; }
    public float ErosionStrength { get; set; } = 0.35f;
    public float TerraceStrength { get; set; }
    public int TerraceSteps { get; set; } = 12;
    public int RiverCount { get; set; }
    public float RiverDepth { get; set; } = 8f;

    public TerrainGenParams Clone() => new()
    {
        ResolutionX = ResolutionX,
        ResolutionZ = ResolutionZ,
        CellSize = CellSize,
        MinHeight = MinHeight,
        MaxHeight = MaxHeight,
        Preset = Preset,
        Seed = Seed,
        ErosionIterations = ErosionIterations,
        ErosionStrength = ErosionStrength,
        TerraceStrength = TerraceStrength,
        TerraceSteps = TerraceSteps,
        RiverCount = RiverCount,
        RiverDepth = RiverDepth,
    };
}

/// <summary>
/// Shared, parameterised heightfield + splat generator used by both the New-Terrain wizard
/// (create) and the Terrain Editor's Regenerate button (re-roll). Presets select the noise
/// recipe; splat is auto-seeded from slope/height so paint layers start plausible.
/// </summary>
public static class TerrainGenerator
{
    public static string Describe(TerrainPreset preset) => preset switch
    {
        TerrainPreset.Flatlands => "Near-flat plain with gentle undulation — good for towns and 2.5D levels.",
        TerrainPreset.RollingHills => "Soft rolling hills with mild ridges — the default gentle landscape.",
        TerrainPreset.Hills => "Taller, busier hills with more relief.",
        TerrainPreset.Mountains => "Sharp ridged mountains and deep valleys.",
        TerrainPreset.ErodedMountains => "Weathered mountain chains with talus-softened slopes and gullies.",
        TerrainPreset.RidgeValleys => "Long folded ridges and traversable valleys for large landscapes.",
        TerrainPreset.Badlands => "Dry stratified mesas, terraces, and sharply cut drainage channels.",
        TerrainPreset.Volcanic => "A dominant volcanic cone, broken lava foothills, and a summit caldera.",
        TerrainPreset.Islands => "Land in the centre falling to water at the edges — an island/archipelago.",
        TerrainPreset.Canyon => "A flat plateau carved by a winding canyon.",
        _ => string.Empty,
    };

    public static TerrainAsset Generate(TerrainGenParams p)
    {
        int resX = Math.Clamp(p.ResolutionX, 17, 513);
        int resZ = Math.Clamp(p.ResolutionZ, 17, 513);
        float cell = Math.Clamp(p.CellSize, 0.1f, 32f);
        float originX = -resX * cell * 0.5f;
        float originZ = -resZ * cell * 0.5f;
        var asset = new TerrainAsset(resX, resZ, cell, originX, originZ, p.MinHeight, p.MaxHeight);

        int seed = p.Seed & 0x7FFF;
        float halfSpanX = resX * cell * 0.5f;
        float halfSpanZ = resZ * cell * 0.5f;
        float maxRadius = MathF.Sqrt(halfSpanX * halfSpanX + halfSpanZ * halfSpanZ);

        for (int z = 0; z < resZ; z++)
        {
            for (int x = 0; x < resX; x++)
            {
                float wx = originX + x * cell;
                float wz = originZ + z * cell;
                float h = SampleHeight(p.Preset, seed, wx, wz, maxRadius);
                asset.SetHeight(x, z, h);
            }
        }

        float terrace = Math.Clamp(p.TerraceStrength, 0f, 1f);
        if (p.Preset == TerrainPreset.Badlands && terrace <= 0f) terrace = 0.58f;
        if (terrace > 0f) ApplyTerracing(asset, terrace, p.TerraceSteps);

        int erosionIterations = Math.Clamp(p.ErosionIterations, 0, 100);
        if (p.Preset == TerrainPreset.ErodedMountains && erosionIterations == 0) erosionIterations = 12;
        if (erosionIterations > 0)
            ApplyThermalErosion(asset, erosionIterations, p.ErosionStrength);

        if (p.RiverCount > 0)
            CarveRivers(asset, p.Seed, p.RiverCount, p.RiverDepth);

        SeedSplatFromSlope(asset, cell);
        return asset;
    }

    private static float SampleHeight(TerrainPreset preset, int seed, float wx, float wz, float maxRadius)
    {
        switch (preset)
        {
            case TerrainPreset.Flatlands:
            {
                float h = 1.5f;
                h += Noise.Fbm2(seed, wx * 0.02f, wz * 0.02f, 3) * 1.4f;
                return h;
            }

            case TerrainPreset.RollingHills:
            {
                float h = 2.5f;
                h += Noise.Fbm2(seed, wx * 0.021f, wz * 0.021f, 4) * 9f;
                h += MathF.Sin(wx * 0.05f) * MathF.Cos(wz * 0.041f) * 1.7f;
                float ridge = MathF.Abs(Noise.Fbm2(seed + 77, wx * 0.012f, wz * 0.012f, 3));
                h += ridge * ridge * 6f;
                return h;
            }

            case TerrainPreset.Hills:
            {
                float h = 3f;
                h += Noise.Fbm2(seed, wx * 0.018f, wz * 0.018f, 5) * 16f;
                h += MathF.Sin(wx * 0.03f) * MathF.Cos(wz * 0.028f) * 4f;
                return h;
            }

            case TerrainPreset.Mountains:
            {
                float h = 4f;
                h += Noise.Fbm2(seed, wx * 0.015f, wz * 0.015f, 5) * 10f;
                float ridge = 1f - MathF.Abs(Noise.Fbm2(seed + 131, wx * 0.009f, wz * 0.009f, 4));
                h += ridge * ridge * ridge * 46f;
                return h;
            }

            case TerrainPreset.ErodedMountains:
            {
                float baseNoise = Noise.Fbm2(seed, wx * 0.013f, wz * 0.013f, 5) * 9f;
                float ridge = 1f - MathF.Abs(Noise.Fbm2(seed + 313, wx * 0.008f, wz * 0.008f, 5));
                float gullies = MathF.Abs(Noise.Fbm2(seed + 719, wx * 0.045f, wz * 0.045f, 3));
                return 2f + baseNoise + ridge * ridge * 40f - gullies * ridge * 7f;
            }

            case TerrainPreset.RidgeValleys:
            {
                float warp = Noise.Fbm2(seed + 101, wx * 0.006f, wz * 0.006f, 3) * 24f;
                float folds = MathF.Abs(MathF.Sin((wx + warp) * 0.045f));
                float broken = Noise.Fbm2(seed + 202, wx * 0.018f, wz * 0.018f, 4) * 6f;
                return 1f + MathF.Pow(folds, 2.2f) * 25f + broken;
            }

            case TerrainPreset.Badlands:
            {
                float ridge = 1f - MathF.Abs(Noise.Fbm2(seed, wx * 0.018f, wz * 0.018f, 5));
                float drainage = MathF.Abs(Noise.Fbm2(seed + 409, wx * 0.05f, wz * 0.05f, 3));
                return 2f + ridge * ridge * 29f - drainage * 7f;
            }

            case TerrainPreset.Volcanic:
            {
                float distance = MathF.Sqrt(wx * wx + wz * wz) / MathF.Max(1f, maxRadius);
                float cone = MathF.Max(0f, 1f - distance * 1.9f);
                float crater = MathF.Exp(-(distance * distance) / (2f * 0.075f * 0.075f));
                float lava = Noise.Fbm2(seed, wx * 0.022f, wz * 0.022f, 5) * (5f + cone * 8f);
                return -2f + cone * 58f - crater * 24f + lava;
            }

            case TerrainPreset.Islands:
            {
                float dist = MathF.Sqrt(wx * wx + wz * wz) / MathF.Max(1f, maxRadius);
                float falloff = MathF.Max(0f, 1f - dist * dist * 1.35f);
                float land = Noise.Fbm2(seed, wx * 0.02f, wz * 0.02f, 4) * 0.5f + 0.5f;
                float h = (land * falloff) * 34f - 8f; // negative near the edge → water basin
                return h;
            }

            case TerrainPreset.Canyon:
            {
                float plateau = 12f + Noise.Fbm2(seed, wx * 0.02f, wz * 0.02f, 3) * 3f;
                float channel = wx + MathF.Sin(wz * 0.03f) * 22f; // winding centre line
                float carve = MathF.Exp(-(channel * channel) / (2f * 26f * 26f));
                float h = plateau - carve * 30f;
                return h;
            }

            default:
                return 0f;
        }
    }

    /// <summary>Quantises height bands while retaining a controllable amount of the source shape.</summary>
    public static void ApplyTerracing(TerrainAsset asset, float strength, int steps)
    {
        float blend = Math.Clamp(strength, 0f, 1f);
        int bands = Math.Clamp(steps, 2, 128);
        float span = MathF.Max(0.001f, asset.MaxHeight - asset.MinHeight);
        float step = span / bands;
        for (int z = 0; z < asset.ResolutionZ; z++)
        {
            for (int x = 0; x < asset.ResolutionX; x++)
            {
                float height = asset.GetHeight(x, z);
                float terraced = asset.MinHeight + MathF.Round((height - asset.MinHeight) / step) * step;
                asset.SetHeight(x, z, height + (terraced - height) * blend);
            }
        }
    }

    /// <summary>
    /// Deterministic thermal erosion. Material above the talus angle moves toward the lowest
    /// neighbour, softening impossible spikes while preserving broad ridges.
    /// </summary>
    public static void ApplyThermalErosion(TerrainAsset asset, int iterations, float strength)
    {
        int width = asset.ResolutionX;
        int depth = asset.ResolutionZ;
        int passes = Math.Clamp(iterations, 0, 100);
        float amount = Math.Clamp(strength, 0f, 1f);
        // Heights are stored in world units, so a 0.65 * cell-size threshold ignored nearly every
        // authored one-metre terrain slope (the mountain fixture averages only ~0.16u per edge).
        // Keep a small scale-aware repose threshold, then move material symmetrically across each
        // edge. Processing every source toward only its single lowest neighbour let several cells
        // pile material into one target and could increase total variation instead of eroding it.
        float talus = MathF.Max(0.01f, asset.CellSize * 0.08f);
        float[] heights = new float[width * depth];
        float[] next = new float[heights.Length];
        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
                heights[z * width + x] = asset.GetHeight(x, z);

        for (int pass = 0; pass < passes; pass++)
        {
            Array.Copy(heights, next, heights.Length);
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int here = z * width + x;
                    if (x + 1 < width)
                        TransferThermalMaterial(heights, next, here, here + 1, talus, amount);
                    if (z + 1 < depth)
                        TransferThermalMaterial(heights, next, here, here + width, talus, amount);
                }
            }

            (heights, next) = (next, heights);
        }

        for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
                asset.SetHeight(x, z, heights[z * width + x]);
    }

    private static void TransferThermalMaterial(
        float[] source,
        float[] destination,
        int first,
        int second,
        float talus,
        float strength)
    {
        float difference = source[first] - source[second];
        float excess = MathF.Abs(difference) - talus;
        if (excess <= 0f) return;

        // Each cell participates in at most four edges. An eighth of the excess per edge leaves a
        // generous stability margin even at strength 1 while preserving mass exactly.
        float transfer = excess * strength * 0.125f;
        if (difference > 0f)
        {
            destination[first] -= transfer;
            destination[second] += transfer;
        }
        else
        {
            destination[first] += transfer;
            destination[second] -= transfer;
        }
    }

    /// <summary>Carves deterministic meandering water courses across the generated heightfield.</summary>
    public static void CarveRivers(TerrainAsset asset, int seed, int count, float depth)
    {
        int rivers = Math.Clamp(count, 0, 8);
        float carveDepth = Math.Clamp(depth, 0.1f, 128f);
        float width = MathF.Max(1.5f, Math.Min(asset.ResolutionX, asset.ResolutionZ) * 0.018f);
        for (int river = 0; river < rivers; river++)
        {
            float phase = (seed * 0.017f + river * 2.173f) % MathF.Tau;
            float offset = (river - (rivers - 1) * 0.5f) * asset.ResolutionX / MathF.Max(2f, rivers + 1f);
            for (int z = 0; z < asset.ResolutionZ; z++)
            {
                float normalZ = z / (float)Math.Max(1, asset.ResolutionZ - 1);
                float center = (asset.ResolutionX - 1) * 0.5f + offset
                    + MathF.Sin(normalZ * MathF.Tau * 1.35f + phase) * asset.ResolutionX * 0.11f
                    + MathF.Sin(normalZ * MathF.Tau * 3.1f + phase * 0.7f) * asset.ResolutionX * 0.035f;
                int minX = Math.Max(0, (int)MathF.Floor(center - width * 3f));
                int maxX = Math.Min(asset.ResolutionX - 1, (int)MathF.Ceiling(center + width * 3f));
                for (int x = minX; x <= maxX; x++)
                {
                    float distance = (x - center) / width;
                    float weight = MathF.Exp(-distance * distance * 0.5f);
                    asset.SetHeight(x, z, asset.GetHeight(x, z) - carveDepth * weight);
                }
            }
        }
    }

    /// <summary>Grass base, dirt on mid slopes, rock on steep faces, snow on high peaks.</summary>
    public static void SeedSplatFromSlope(TerrainAsset asset, float cell)
    {
        int resX = asset.ResolutionX;
        int resZ = asset.ResolutionZ;
        for (int z = 0; z < resZ; z++)
        {
            for (int x = 0; x < resX; x++)
            {
                float h = asset.GetHeight(x, z);
                float hx = asset.GetHeight(Math.Min(x + 1, resX - 1), z) - asset.GetHeight(Math.Max(x - 1, 0), z);
                float hz = asset.GetHeight(x, Math.Min(z + 1, resZ - 1)) - asset.GetHeight(x, Math.Max(z - 1, 0));
                float slope = MathF.Sqrt(hx * hx + hz * hz) / (cell * 2f);

                float rock = Math.Clamp((slope - 0.55f) * 2.2f, 0f, 1f);
                float snow = Math.Clamp((h - 14f) * 0.16f, 0f, 1f) * (1f - rock * 0.6f);
                float dirt = Math.Clamp((slope - 0.22f) * 1.8f, 0f, 1f) * (1f - rock) * (1f - snow);
                float grass = MathF.Max(0f, 1f - rock - snow - dirt);
                float sum = MathF.Max(0.0001f, grass + dirt + rock + snow);
                asset.SetSplat(x, z,
                    (byte)(grass / sum * 255f),
                    (byte)(dirt / sum * 255f),
                    (byte)(rock / sum * 255f),
                    (byte)(snow / sum * 255f));
            }
        }
    }
}
