using System;
using System.IO;
using System.Numerics;

namespace Genesis.World.Terrain;

/// <summary>
/// Persistent terrain asset storing heightmap and splatmap data.
/// Divided into chunks for rendering and physics.
///
/// <para>
/// Not currently instantiated anywhere live (the active sandbox/physics demos use the
/// single-mesh <see cref="Genesis.World.SandboxTerrain.SandboxTerrainGround"/> instead — see
/// its perimeter-skirt fix for the rationale below). If this asset/chunk system is revived for
/// a real streamed world, two things need attention to avoid classic chunk-seam artifacts:
/// </para>
/// <para>
/// 1. <b>Chunk overlap, not chunk adjacency.</b> <see cref="TerrainChunk"/> samples heights
/// directly from this shared asset by global index, so neighboring chunks are seam-free only
/// if each chunk is allocated with <c>gridPointsX/Z = desiredChunkSize + 1</c> and adjacent
/// chunks' <c>startX</c>/<c>startZ</c> are offset by exactly <c>desiredChunkSize</c> (not by
/// <c>gridPointsX</c>) — i.e. each chunk's last column/row of vertices must be the same global
/// indices as its neighbor's first column/row. Offsetting by the full grid size instead of the
/// chunk size skips a column and leaves a one-cell gap: the "see-through, duvet lifted up"
/// look reported against the current single-mesh terrain, but worse, since it's a true hole
/// rather than just a missing edge skirt.
/// </para>
/// <para>
/// 2. <b><see cref="GetHeight"/> returns 0 (not the clamped edge height) outside
/// 0..ResolutionX/Z-1.</b> <see cref="TerrainChunk.ComputeNormal"/> calls this at x-1/x+1 and
/// z-1/z+1, so it's fine for any interior chunk boundary (those indices stay in range), but at
/// the asset's outermost edge it silently substitutes 0 instead of clamping to the nearest
/// valid sample — producing a wrong, sometimes very steep, normal right at the world boundary.
/// Clamp the coordinates instead of returning a sentinel, and add the same kind of perimeter
/// skirt <see cref="Genesis.World.SandboxTerrain.SandboxTerrainGround"/> now drops below its
/// own edge — but only at the asset's outermost boundary, since interior chunk-to-chunk edges
/// are already covered by their neighbor once rule 1 above holds.
/// </para>
/// </summary>
public class TerrainAsset
{
    public int ResolutionX { get; private set; }
    public int ResolutionZ { get; private set; }
    public float MinHeight { get; private set; }
    public float MaxHeight { get; private set; }
    public float CellSize { get; private set; }
    public float OriginX { get; private set; }
    public float OriginZ { get; private set; }

    // 16-bit height values
    private readonly ushort[] _heights;
    // 4-channel splatmap
    private readonly byte[] _splatmap;

    public TerrainAsset(int resX, int resZ, float cellSize, float originX, float originZ, float minHeight, float maxHeight)
        : this(resX, resZ, cellSize, originX, originZ, minHeight, maxHeight, initialise: true)
    {
    }

    private TerrainAsset(int resX, int resZ, float cellSize, float originX, float originZ, float minHeight, float maxHeight, bool initialise)
    {
        ResolutionX = resX;
        ResolutionZ = resZ;
        CellSize = cellSize;
        OriginX = originX;
        OriginZ = originZ;
        MinHeight = minHeight;
        MaxHeight = maxHeight;

        _heights = new ushort[resX * resZ];
        _splatmap = new byte[resX * resZ * 4]; // RGBA
        if (!initialise) return; // Load overwrites both planes.

        // Initialize to middle height
        ushort mid = (ushort)(ushort.MaxValue / 2);
        Array.Fill(_heights, mid);

        // Initialize splatmap to red channel (texture 1)
        FillDefaultSplat();
    }

    /// <summary>Raw height samples (editor undo snapshots / bulk restore).</summary>
    public ushort[] HeightsData => _heights;

    /// <summary>Raw RGBA splat weights, one 4-byte pixel per height sample (editor paint).</summary>
    public byte[] SplatmapData => _splatmap;

    public float GetHeight(int x, int z)
    {
        if (x < 0 || x >= ResolutionX || z < 0 || z >= ResolutionZ) return 0f;
        ushort h = _heights[z * ResolutionX + x];
        float t = h / (float)ushort.MaxValue;
        return MinHeight + t * (MaxHeight - MinHeight);
    }

    public (byte R, byte G, byte B, byte A) GetSplat(int x, int z)
    {
        if (x < 0 || x >= ResolutionX || z < 0 || z >= ResolutionZ) return (255, 0, 0, 0);
        int i = (z * ResolutionX + x) * 4;
        return (_splatmap[i], _splatmap[i + 1], _splatmap[i + 2], _splatmap[i + 3]);
    }

    public void SetSplat(int x, int z, byte r, byte g, byte b, byte a)
    {
        if (x < 0 || x >= ResolutionX || z < 0 || z >= ResolutionZ) return;
        int i = (z * ResolutionX + x) * 4;
        _splatmap[i] = r;
        _splatmap[i + 1] = g;
        _splatmap[i + 2] = b;
        _splatmap[i + 3] = a;
    }

    /// <summary>Paint one splat channel (0..3) with smooth falloff; other channels renormalise.</summary>
    public void ApplyPaintBrush(float worldX, float worldZ, float radius, float strength, int channel)
    {
        channel = Math.Clamp(channel, 0, 3);
        float cx = (worldX - OriginX) / CellSize;
        float cz = (worldZ - OriginZ) / CellSize;
        float cellRadius = radius / CellSize;

        int minX = Math.Max(0, (int)Math.Floor(cx - cellRadius));
        int maxX = Math.Min(ResolutionX - 1, (int)Math.Ceiling(cx + cellRadius));
        int minZ = Math.Max(0, (int)Math.Floor(cz - cellRadius));
        int maxZ = Math.Min(ResolutionZ - 1, (int)Math.Ceiling(cz + cellRadius));

        Span<float> weights = stackalloc float[4];
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x - cx;
                float dz = z - cz;
                float distSq = dx * dx + dz * dz;
                if (distSq > cellRadius * cellRadius) continue;

                float dist = MathF.Sqrt(distSq);
                float t = 1f - Math.Clamp(dist / MathF.Max(0.001f, cellRadius), 0f, 1f);
                float falloff = t * t * (3f - 2f * t);
                float blend = Math.Clamp(strength * falloff, 0f, 1f);

                int i = (z * ResolutionX + x) * 4;
                for (int c = 0; c < 4; c++) weights[c] = _splatmap[i + c] / 255f;
                for (int c = 0; c < 4; c++)
                    weights[c] = c == channel
                        ? weights[c] + (1f - weights[c]) * blend
                        : weights[c] * (1f - blend);

                float sum = weights[0] + weights[1] + weights[2] + weights[3];
                if (sum < 0.001f) { weights[channel] = 1f; sum = 1f; }
                for (int c = 0; c < 4; c++)
                    _splatmap[i + c] = (byte)Math.Clamp((int)MathF.Round(weights[c] / sum * 255f), 0, 255);
            }
        }
    }

    /// <summary>Moves heights toward their local average (smooth) with brush falloff.</summary>
    public void ApplySmoothBrush(float worldX, float worldZ, float radius, float strength)
    {
        float cx = (worldX - OriginX) / CellSize;
        float cz = (worldZ - OriginZ) / CellSize;
        float cellRadius = radius / CellSize;

        int minX = Math.Max(0, (int)Math.Floor(cx - cellRadius));
        int maxX = Math.Min(ResolutionX - 1, (int)Math.Ceiling(cx + cellRadius));
        int minZ = Math.Max(0, (int)Math.Floor(cz - cellRadius));
        int maxZ = Math.Min(ResolutionZ - 1, (int)Math.Ceiling(cz + cellRadius));

        int width = maxX - minX + 1;
        int depth = maxZ - minZ + 1;
        if (width <= 0 || depth <= 0) return;

        float[] smoothed = new float[width * depth];
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float sum = 0f;
                int count = 0;
                for (int oz = -1; oz <= 1; oz++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int sx = Math.Clamp(x + ox, 0, ResolutionX - 1);
                        int sz = Math.Clamp(z + oz, 0, ResolutionZ - 1);
                        sum += GetHeight(sx, sz);
                        count++;
                    }
                }

                smoothed[(z - minZ) * width + (x - minX)] = sum / count;
            }
        }

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x - cx;
                float dz = z - cz;
                float distSq = dx * dx + dz * dz;
                if (distSq > cellRadius * cellRadius) continue;

                float dist = MathF.Sqrt(distSq);
                float t = 1f - Math.Clamp(dist / MathF.Max(0.001f, cellRadius), 0f, 1f);
                float falloff = t * t * (3f - 2f * t);
                float current = GetHeight(x, z);
                float target = smoothed[(z - minZ) * width + (x - minX)];
                SetHeight(x, z, current + (target - current) * Math.Clamp(strength * falloff, 0f, 1f));
            }
        }
    }

    /// <summary>Moves heights toward a fixed target height with brush falloff.</summary>
    public void ApplyFlattenBrush(float worldX, float worldZ, float radius, float strength, float targetHeight)
    {
        float cx = (worldX - OriginX) / CellSize;
        float cz = (worldZ - OriginZ) / CellSize;
        float cellRadius = radius / CellSize;

        int minX = Math.Max(0, (int)Math.Floor(cx - cellRadius));
        int maxX = Math.Min(ResolutionX - 1, (int)Math.Ceiling(cx + cellRadius));
        int minZ = Math.Max(0, (int)Math.Floor(cz - cellRadius));
        int maxZ = Math.Min(ResolutionZ - 1, (int)Math.Ceiling(cz + cellRadius));

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x - cx;
                float dz = z - cz;
                float distSq = dx * dx + dz * dz;
                if (distSq > cellRadius * cellRadius) continue;

                float dist = MathF.Sqrt(distSq);
                float t = 1f - Math.Clamp(dist / MathF.Max(0.001f, cellRadius), 0f, 1f);
                float falloff = t * t * (3f - 2f * t);
                float current = GetHeight(x, z);
                SetHeight(x, z, current + (targetHeight - current) * Math.Clamp(strength * falloff, 0f, 1f));
            }
        }
    }

    /// <summary>Carves a deterministic Aetherforge-style lake or pond basin into this heightmap.</summary>
    public void ApplyBasinStamp(TerrainBasinStamp stamp)
    {
        if (stamp == null) throw new ArgumentNullException(nameof(stamp));
        stamp.ApplyTo(this);
    }

    /// <summary>Grades a buildable platform while preserving the existing ground material.</summary>
    public void ApplyPlateauStamp(TerrainPlateauStamp stamp)
    {
        if (stamp == null) throw new ArgumentNullException(nameof(stamp));
        stamp.ApplyTo(this);
    }

    /// <summary>Bulk restore for editor undo (arrays must match this asset's resolution).</summary>
    public void RestoreState(ushort[] heights, byte[] splat)
    {
        if (heights != null && heights.Length == _heights.Length)
            Array.Copy(heights, _heights, _heights.Length);
        if (splat != null && splat.Length == _splatmap.Length)
            Array.Copy(splat, _splatmap, _splatmap.Length);
    }

    public void SetHeight(int x, int z, float height)
    {
        if (x < 0 || x >= ResolutionX || z < 0 || z >= ResolutionZ) return;
        float t = Math.Clamp((height - MinHeight) / (MaxHeight - MinHeight), 0f, 1f);
        _heights[z * ResolutionX + x] = (ushort)(t * ushort.MaxValue);
    }

    public ushort GetRawHeight(int x, int z)
    {
        if (x < 0 || x >= ResolutionX || z < 0 || z >= ResolutionZ) return 0;
        return _heights[z * ResolutionX + x];
    }

    public void ApplySculptBrush(float worldX, float worldZ, float radius, float strength, bool raise)
    {
        float cx = (worldX - OriginX) / CellSize;
        float cz = (worldZ - OriginZ) / CellSize;
        float cellRadius = radius / CellSize;

        int minX = Math.Max(0, (int)Math.Floor(cx - cellRadius));
        int maxX = Math.Min(ResolutionX - 1, (int)Math.Ceiling(cx + cellRadius));
        int minZ = Math.Max(0, (int)Math.Floor(cz - cellRadius));
        int maxZ = Math.Min(ResolutionZ - 1, (int)Math.Ceiling(cz + cellRadius));

        float dir = raise ? 1f : -1f;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x - cx;
                float dz = z - cz;
                float distSq = dx * dx + dz * dz;

                if (distSq <= cellRadius * cellRadius)
                {
                    float dist = MathF.Sqrt(distSq);
                    float t = 1f - Math.Clamp(dist / cellRadius, 0f, 1f);
                    float falloff = t * t * (3f - 2f * t);

                    float currentH = GetHeight(x, z);
                    SetHeight(x, z, currentH + dir * strength * falloff);
                }
            }
        }
    }

    public float SampleHeight(float worldX, float worldZ)
    {
        float gx = (worldX - OriginX) / CellSize;
        float gz = (worldZ - OriginZ) / CellSize;
        int x0 = (int)MathF.Floor(gx);
        int z0 = (int)MathF.Floor(gz);
        int x1 = Math.Min(x0 + 1, ResolutionX - 1);
        int z1 = Math.Min(z0 + 1, ResolutionZ - 1);
        float tx = gx - x0;
        float tz = gz - z0;
        float h00 = GetHeight(Math.Clamp(x0, 0, ResolutionX - 1), Math.Clamp(z0, 0, ResolutionZ - 1));
        float h10 = GetHeight(Math.Clamp(x1, 0, ResolutionX - 1), Math.Clamp(z0, 0, ResolutionZ - 1));
        float h01 = GetHeight(Math.Clamp(x0, 0, ResolutionX - 1), Math.Clamp(z1, 0, ResolutionZ - 1));
        float h11 = GetHeight(Math.Clamp(x1, 0, ResolutionX - 1), Math.Clamp(z1, 0, ResolutionZ - 1));
        float hx0 = h00 + (h10 - h00) * tx;
        float hx1 = h01 + (h11 - h01) * tx;
        return hx0 + (hx1 - hx0) * tz;
    }

    public void Save(string path)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(0x4E525447); // 'GTRN'
        bw.Write(1);
        bw.Write(ResolutionX);
        bw.Write(ResolutionZ);
        bw.Write(CellSize);
        bw.Write(OriginX);
        bw.Write(OriginZ);
        bw.Write(MinHeight);
        bw.Write(MaxHeight);
        // One block write: a sample-at-a-time loop took seconds on a multi-kilometre terrain.
        bw.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_heights.AsSpan()));
        bw.Write(_splatmap);
    }

    public static TerrainAsset Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (br.ReadInt32() != 0x4E525447)
            throw new InvalidDataException("Not a Genesis terrain file (.gterrain).");
        int version = br.ReadInt32();
        if (version != 1)
            throw new InvalidDataException($"Unsupported terrain version {version}.");
        int resX = br.ReadInt32();
        int resZ = br.ReadInt32();
        float cell = br.ReadSingle();
        float ox = br.ReadSingle();
        float oz = br.ReadSingle();
        float minH = br.ReadSingle();
        float maxH = br.ReadSingle();
        if (resX < 1 || resZ < 1 || (long)resX * resZ > MaximumSamples)
            throw new InvalidDataException($"Terrain resolution {resX} x {resZ} is outside the supported range.");
        var asset = new TerrainAsset(resX, resZ, cell, ox, oz, minH, maxH, initialise: false);
        // Read both planes straight into their arrays. The file is little-endian, as is every
        // platform Genesis runs on.
        Span<byte> heights = System.Runtime.InteropServices.MemoryMarshal.AsBytes(asset._heights.AsSpan());
        if (ReadFully(fs, heights) != heights.Length)
            throw new InvalidDataException("Terrain file ends inside its height data.");
        // An absent or short splat plane keeps the default (all first layer), as it always has.
        if (ReadFully(fs, asset._splatmap) != asset._splatmap.Length)
            asset.FillDefaultSplat();
        return asset;
    }

    /// <summary>Largest sample count a terrain may hold (16385 x 16385).</summary>
    public const long MaximumSamples = 16385L * 16385L;

    private static int ReadFully(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read <= 0) break;
            total += read;
        }

        return total;
    }

    private void FillDefaultSplat()
    {
        for (int i = 0; i < _splatmap.Length; i += 4)
        {
            _splatmap[i] = 255;
            _splatmap[i + 1] = 0;
            _splatmap[i + 2] = 0;
            _splatmap[i + 3] = 0;
        }
    }

    public static TerrainAsset CreateDefaultForestGlade()
    {
        const int res = 129;
        const float cell = 1f;
        const float origin = -64f;
        var asset = new TerrainAsset(res, res, cell, origin, origin, -2f, 18f);
        for (int z = 0; z < res; z++)
        {
            for (int x = 0; x < res; x++)
            {
                float wx = origin + x * cell;
                float wz = origin + z * cell;
                float h = 2f;
                h += MathF.Sin(wx * 0.045f) * MathF.Cos(wz * 0.038f) * 1.2f;
                h += Noise.Fbm2(8803, wx * 0.028f, wz * 0.028f, 3) * 1.4f;
                float pathCenterX = MathF.Sin(wz * 0.08f) * 4f;
                float pathDist = MathF.Abs(wx - pathCenterX);
                float pathMask = Smooth(3.8f, 1.2f, pathDist) * Smooth(-6f, 8f, wz) * Smooth(52f, 38f, wz);
                h += (2.05f - h) * pathMask * 0.92f;
                float ldx = wx - 2f;
                float ldz = wz - 34f;
                float ldist = MathF.Sqrt(ldx * ldx + ldz * ldz);
                if (ldist < 21f)
                {
                    float t = 1f - ldist / 21f;
                    h -= 3.6f * t * t * (3f - 2f * t);
                }
                float cliffMask = Smooth(18f, 4f, MathF.Max(0f, wz - 50f)) * Smooth(22f, 6f, MathF.Abs(wx - 2f));
                h += cliffMask * 11f;
                asset.SetHeight(x, z, h);
            }
        }
        return asset;
    }

    private static float Smooth(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge1) / (edge0 - edge1), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
