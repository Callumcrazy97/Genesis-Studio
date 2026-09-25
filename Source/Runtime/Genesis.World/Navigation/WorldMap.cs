using System;
using System.Numerics;
using Genesis.World.Terrain;

namespace Genesis.World.Navigation;

public sealed class WorldMapImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Pixels { get; init; } = Array.Empty<byte>();
    public WorldBounds Bounds { get; init; }
    public Vector2 ToMapUv(float worldX, float worldZ) => new(
        (worldX - Bounds.Minimum.X) / MathF.Max(Bounds.Size.X, 0.001f),
        (worldZ - Bounds.Minimum.Y) / MathF.Max(Bounds.Size.Y, 0.001f));
}

/// <summary>Renderer-independent cartographic bake sourced exclusively from the live world query.</summary>
public static class WorldMapBaker
{
    private static readonly Vector2 ReliefLight = Vector2.Normalize(new Vector2(-0.72f, -0.69f));

    public static WorldMapImage Bake(IWorldQuery world, int width = 512, int height = 512)
    {
        ArgumentNullException.ThrowIfNull(world);
        width = Math.Clamp(width, 64, 4096);
        height = Math.Clamp(height, 64, 4096);
        var samples = new TerrainSurfaceSample[width * height];
        float minHeight = float.MaxValue, maxHeight = float.MinValue;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            Vector2 position = PixelToWorld(x, y, width, height, world.Manifest.Bounds);
            TerrainSurfaceSample sample = world.SampleTerrain(position.X, position.Y);
            samples[y * width + x] = sample;
            minHeight = MathF.Min(minHeight, sample.Height);
            maxHeight = MathF.Max(maxHeight, sample.Height);
        }

        float range = MathF.Max(maxHeight - minHeight, 0.001f);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            TerrainSurfaceSample sample = samples[y * width + x];
            float elevation = Math.Clamp((sample.Height - minHeight) / range, 0f, 1f);
            Vector3 low = new(0.28f, 0.43f, 0.22f), high = new(0.62f, 0.57f, 0.49f);
            Vector3 color = Vector3.Lerp(low, high, elevation);
            color = Vector3.Lerp(color, new Vector3(0.48f, 0.47f, 0.45f), sample.RockExposure * 0.55f);
            color *= Relief(samples, x, y, width, height);
            Vector2 position = PixelToWorld(x, y, width, height, world.Manifest.Bounds);
            TerrainWaterDefinition water = world.WaterAt(position.X, position.Y);
            if (water != null)
            {
                float depth = world.WaterDepthAt(position.X, position.Y);
                float t = Math.Clamp(depth / MathF.Max(water.SimulationDepth, 0.25f), 0f, 1f);
                color = Vector3.Lerp(new Vector3(0.36f, 0.58f, 0.68f), new Vector3(0.12f, 0.28f, 0.43f), t);
            }
            if (sample.PathMask > 0.02f)
                color = Vector3.Lerp(color, new Vector3(0.73f, 0.61f, 0.42f), Math.Clamp(sample.PathMask * 1.3f, 0f, 1f));
            int index = (y * width + x) * 4;
            pixels[index] = Byte(color.X); pixels[index + 1] = Byte(color.Y); pixels[index + 2] = Byte(color.Z); pixels[index + 3] = 255;
        }
        return new WorldMapImage { Width = width, Height = height, Pixels = pixels, Bounds = world.Manifest.Bounds };
    }

    private static Vector2 PixelToWorld(int x, int y, int width, int height, WorldBounds bounds) => new(
        bounds.Minimum.X + (x + 0.5f) / width * bounds.Size.X,
        bounds.Minimum.Y + (y + 0.5f) / height * bounds.Size.Y);

    private static float Relief(TerrainSurfaceSample[] samples, int x, int y, int width, int height)
    {
        float left = samples[y * width + Math.Max(0, x - 1)].Height;
        float right = samples[y * width + Math.Min(width - 1, x + 1)].Height;
        float up = samples[Math.Max(0, y - 1) * width + x].Height;
        float down = samples[Math.Min(height - 1, y + 1) * width + x].Height;
        float light = -((right - left) * ReliefLight.X + (down - up) * ReliefLight.Y) * 0.22f;
        return Math.Clamp(1f + light, 0.45f, 1.55f);
    }
    private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}

/// <summary>Persistent coarse exploration mask, separate from the expensive static map bake.</summary>
public sealed class MapDiscovery
{
    private readonly byte[] _cells;
    public MapDiscovery(WorldBounds bounds, int resolution = 128)
    {
        if (resolution < 8 || resolution > 2048) throw new ArgumentOutOfRangeException(nameof(resolution));
        Bounds = bounds; Resolution = resolution; _cells = new byte[resolution * resolution];
    }
    public WorldBounds Bounds { get; }
    public int Resolution { get; }
    public ReadOnlySpan<byte> Cells => _cells;
    public float ExploredFraction
    {
        get { int seen = 0; foreach (byte cell in _cells) if (cell > 24) seen++; return seen / (float)_cells.Length; }
    }
    public bool Reveal(Vector3 position, float sightRadius)
    {
        float cellX = Bounds.Size.X / Resolution, cellZ = Bounds.Size.Y / Resolution;
        float cx = (position.X - Bounds.Minimum.X) / MathF.Max(cellX, 0.001f);
        float cz = (position.Z - Bounds.Minimum.Y) / MathF.Max(cellZ, 0.001f);
        float radius = MathF.Max(1f, sightRadius / MathF.Max(0.001f, MathF.Min(cellX, cellZ)));
        int minX = Math.Max(0, (int)MathF.Floor(cx - radius)), maxX = Math.Min(Resolution - 1, (int)MathF.Ceiling(cx + radius));
        int minZ = Math.Max(0, (int)MathF.Floor(cz - radius)), maxZ = Math.Min(Resolution - 1, (int)MathF.Ceiling(cz + radius));
        bool changed = false;
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            float dx = x + 0.5f - cx, dz = z + 0.5f - cz;
            float distance = MathF.Sqrt(dx * dx + dz * dz) / radius;
            if (distance > 1f) continue;
            byte value = (byte)Math.Clamp((1f - distance * distance) * 255f, 0f, 255f);
            int index = z * Resolution + x;
            if (value <= _cells[index]) continue;
            _cells[index] = value; changed = true;
        }
        return changed;
    }
    public byte[] Capture() => (byte[])_cells.Clone();
    public bool Restore(ReadOnlySpan<byte> cells)
    {
        if (cells.Length != _cells.Length) return false;
        cells.CopyTo(_cells); return true;
    }
    public void RevealAll() => Array.Fill(_cells, (byte)255);
}
