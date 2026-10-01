using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.World.Terrain;

/// <summary>One copy of a scattered model, in the terrain's own space.</summary>
public readonly struct TerrainScatterInstance
{
    public readonly Vector3 Position;
    /// <summary>The ground's normal when the layer tilts to the ground, otherwise straight up.</summary>
    public readonly Vector3 Up;
    public readonly float Yaw;
    public readonly float Scale;
    /// <summary>A stable value from -1 to 1 for varying each copy's colour a little.</summary>
    public readonly float Tone;

    public TerrainScatterInstance(Vector3 position, Vector3 up, float yaw, float scale, float tone)
    {
        Position = position;
        Up = up;
        Yaw = yaw;
        Scale = scale;
        Tone = tone;
    }

    /// <summary>Scale, turn, tilt, then move into place.</summary>
    public Matrix4x4 ToMatrix()
    {
        Matrix4x4 matrix = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationY(Yaw);
        if (Up.Y < 0.9995f)
        {
            Vector3 axis = Vector3.Cross(Vector3.UnitY, Up);
            float length = axis.Length();
            if (length > 1e-5f)
                matrix *= Matrix4x4.CreateFromAxisAngle(axis / length, MathF.Acos(Math.Clamp(Up.Y, -1f, 1f)));
        }

        matrix.Translation = Position;
        return matrix;
    }
}

/// <summary>
/// Works out where a scatter layer's copies stand, one square cell of the terrain at a time.
/// </summary>
/// <remarks>
/// Nothing is stored: a forest of any size is a rule plus a seed. Every copy comes from a fixed
/// world-wide grid of candidate spots, so a cell always produces the same copies whichever cells
/// were made before it, and neighbouring cells meet without a seam or a doubled tree.
/// </remarks>
public static class TerrainScatterPlacement
{
    /// <summary>Side of one cell in metres.</summary>
    public const float CellSize = 256f;

    /// <summary>Most copies one cell may hold, whatever the density asks for.</summary>
    public const int MaximumPerCell = 60000;

    /// <summary>Number of cells along each side of a terrain.</summary>
    public static (int X, int Z) CellCount(TerrainAsset terrain)
    {
        float width = (terrain.ResolutionX - 1) * terrain.CellSize;
        float length = (terrain.ResolutionZ - 1) * terrain.CellSize;
        return (Math.Max(1, (int)MathF.Ceiling(width / CellSize)), Math.Max(1, (int)MathF.Ceiling(length / CellSize)));
    }

    /// <summary>The corner of a cell nearest the terrain's origin, in the terrain's own space.</summary>
    public static Vector2 CellOrigin(TerrainAsset terrain, int cellX, int cellZ) =>
        new(terrain.OriginX + cellX * CellSize, terrain.OriginZ + cellZ * CellSize);

    /// <summary>Distance between candidate spots: the layer's density, limited by its spacing.</summary>
    public static float Pitch(TerrainScatterLayer layer)
    {
        float density = MathF.Max(0.0001f, layer.DensityPerHectare);
        return Math.Clamp(MathF.Max(layer.MinimumSpacing, 100f / MathF.Sqrt(density)), 0.25f, CellSize);
    }

    public static TerrainScatterInstance[] Generate(TerrainAsset terrain, TerrainScatterLayer layer, int cellX, int cellZ)
    {
        if (terrain == null || layer == null || !layer.Enabled || layer.DensityPerHectare <= 0f)
            return Array.Empty<TerrainScatterInstance>();

        float pitch = Pitch(layer);
        Vector2 origin = CellOrigin(terrain, cellX, cellZ);
        float endX = MathF.Min(origin.X + CellSize, terrain.OriginX + (terrain.ResolutionX - 1) * terrain.CellSize);
        float endZ = MathF.Min(origin.Y + CellSize, terrain.OriginZ + (terrain.ResolutionZ - 1) * terrain.CellSize);
        // Candidates are numbered across the whole terrain, so a cell owns exactly those whose
        // grid square starts inside it.
        int firstX = (int)MathF.Ceiling((origin.X - terrain.OriginX) / pitch - 1e-4f);
        int firstZ = (int)MathF.Ceiling((origin.Y - terrain.OriginZ) / pitch - 1e-4f);
        uint seed = unchecked((uint)layer.Seed * 0x9E3779B1u + 0x7F4A7C15u);
        float slopeLimit = MathF.Tan(layer.MaximumSlopeDegrees * MathF.PI / 180f);
        float step = terrain.CellSize;
        var instances = new List<TerrainScatterInstance>();

        for (int gz = firstZ; terrain.OriginZ + gz * pitch < endZ - 1e-4f; gz++)
        {
            for (int gx = firstX; terrain.OriginX + gx * pitch < endX - 1e-4f; gx++)
            {
                uint hash = Hash((uint)gx, (uint)gz, seed);
                float x = terrain.OriginX + (gx + 0.5f + (Unit(hash) - 0.5f) * 0.8f) * pitch;
                float z = terrain.OriginZ + (gz + 0.5f + (Unit(hash = Next(hash)) - 0.5f) * 0.8f) * pitch;
                float chance = Unit(hash = Next(hash));
                float yaw = Unit(hash = Next(hash)) * MathF.Tau;
                float size = Unit(hash = Next(hash));
                float tone = Unit(Next(hash)) * 2f - 1f;

                if (layer.ClumpSize > 0f && layer.ClumpStrength > 0f)
                {
                    float clump = ValueNoise(x / layer.ClumpSize, z / layer.ClumpSize, seed ^ 0x51ED270Bu);
                    float open = SmoothStep(0.38f, 0.62f, clump);
                    if (chance > 1f - layer.ClumpStrength * (1f - open)) continue;
                }

                float height = terrain.SampleHeight(x, z);
                if (height < layer.MinimumHeight || height > layer.MaximumHeight) continue;

                float dx = terrain.SampleHeight(x + step, z) - terrain.SampleHeight(x - step, z);
                float dz = terrain.SampleHeight(x, z + step) - terrain.SampleHeight(x, z - step);
                float slope = MathF.Sqrt(dx * dx + dz * dz) / (2f * step);
                if (slope > slopeLimit) continue;

                if (layer.PaintLayerMask != 15 && PaintWeight(terrain, x, z, layer.PaintLayerMask) < layer.MinimumPaintWeight)
                    continue;

                Vector3 up = Vector3.UnitY;
                if (layer.AlignToGround)
                    up = Vector3.Normalize(new Vector3(-dx / (2f * step), 1f, -dz / (2f * step)));
                float scale = layer.MinimumScale + (layer.MaximumScale - layer.MinimumScale) * size;
                instances.Add(new TerrainScatterInstance(new Vector3(x, height - layer.Sink * scale, z), up, yaw, scale, tone));
                if (instances.Count >= MaximumPerCell) return instances.ToArray();
            }
        }

        return instances.ToArray();
    }

    /// <summary>Share of the paint at a spot that belongs to the layers in a mask, 0 to 1.</summary>
    public static float PaintWeight(TerrainAsset terrain, float x, float z, int mask)
    {
        int sx = Math.Clamp((int)MathF.Round((x - terrain.OriginX) / terrain.CellSize), 0, terrain.ResolutionX - 1);
        int sz = Math.Clamp((int)MathF.Round((z - terrain.OriginZ) / terrain.CellSize), 0, terrain.ResolutionZ - 1);
        (byte r, byte g, byte b, byte a) = terrain.GetSplat(sx, sz);
        int total = r + g + b + a;
        if (total == 0) return (mask & 1) != 0 ? 1f : 0f;
        int allowed = ((mask & 1) != 0 ? r : 0) + ((mask & 2) != 0 ? g : 0) + ((mask & 4) != 0 ? b : 0) + ((mask & 8) != 0 ? a : 0);
        return allowed / (float)total;
    }

    private static uint Hash(uint x, uint z, uint seed)
    {
        uint h = seed ^ (x * 0x85EBCA6Bu) ^ (z * 0xC2B2AE35u);
        return Next(h ^ (h >> 15));
    }

    private static uint Next(uint h)
    {
        h ^= h >> 16; h *= 0x7FEB352Du;
        h ^= h >> 15; h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    private static float Unit(uint h) => (h >> 8) * (1f / 16777216f);

    private static float ValueNoise(float x, float z, uint seed)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float fx = x - ix, fz = z - iz;
        fx = fx * fx * (3f - 2f * fx);
        fz = fz * fz * (3f - 2f * fz);
        float a = Unit(Hash((uint)ix, (uint)iz, seed)), b = Unit(Hash((uint)(ix + 1), (uint)iz, seed));
        float c = Unit(Hash((uint)ix, (uint)(iz + 1), seed)), d = Unit(Hash((uint)(ix + 1), (uint)(iz + 1), seed));
        return a + (b - a) * fx + (c - a) * fz + (a - b - c + d) * fx * fz;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
