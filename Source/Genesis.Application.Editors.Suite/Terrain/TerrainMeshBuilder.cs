using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.World;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Builds the lit, splat-coloured preview mesh for a <see cref="TerrainAsset"/> heightfield.
/// Shared by the Terrain Editor (its own working copy) and the Room Editor (a placed Terrain
/// node's read-only preview) so both editors render an identical, correctly-wound surface
/// instead of two independently-maintained copies of this math drifting apart.
/// </summary>
public static class TerrainMeshBuilder
{
    /// <summary>Builds the (shared, resolution-only-dependent) triangle index buffer.</summary>
    public static ushort[] BuildIndices(int resolutionX, int resolutionZ)
    {
        ushort[] indices = new ushort[(resolutionX - 1) * (resolutionZ - 1) * 6];
        int index = 0;
        for (int z = 0; z < resolutionZ - 1; z++)
        {
            for (int x = 0; x < resolutionX - 1; x++)
            {
                ushort i0 = (ushort)(z * resolutionX + x);
                ushort i1 = (ushort)(z * resolutionX + x + 1);
                ushort i2 = (ushort)((z + 1) * resolutionX + x + 1);
                ushort i3 = (ushort)((z + 1) * resolutionX + x);
                // Counter-clockwise from above: cross products point +Y, as do the normals.
                indices[index++] = i0;
                indices[index++] = i2;
                indices[index++] = i1;
                indices[index++] = i0;
                indices[index++] = i3;
                indices[index++] = i2;
            }
        }

        return indices;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> (length resolutionX*resolutionZ) with positions,
    /// normals, splat-blended vertex colour, and tiling UVs for <paramref name="terrain"/>.
    /// <paramref name="layerColors"/> must have exactly 4 entries (grass/dirt/rock/snow, RGBA).
    /// </summary>
    public static void BuildVertices(TerrainAsset terrain, ReadOnlySpan<Vector4> layerColors, MeshVertex[] destination)
    {
        int resX = terrain.ResolutionX;
        int resZ = terrain.ResolutionZ;
        for (int z = 0; z < resZ; z++)
        {
            for (int x = 0; x < resX; x++)
            {
                float wx = terrain.OriginX + x * terrain.CellSize;
                float wz = terrain.OriginZ + z * terrain.CellSize;
                float h = terrain.GetHeight(x, z);

                float hl = terrain.GetHeight(Math.Max(x - 1, 0), z);
                float hr = terrain.GetHeight(Math.Min(x + 1, resX - 1), z);
                float hd = terrain.GetHeight(x, Math.Max(z - 1, 0));
                float hu = terrain.GetHeight(x, Math.Min(z + 1, resZ - 1));
                Vector3 normal = Vector3.Normalize(new Vector3(hl - hr, 2f * terrain.CellSize, hd - hu));

                (byte r, byte g, byte b, byte a) = terrain.GetSplat(x, z);
                float wGrass = r / 255f, wDirt = g / 255f, wRock = b / 255f, wSnow = a / 255f;
                float weightSum = MathF.Max(0.001f, wGrass + wDirt + wRock + wSnow);
                Vector4 color =
                    (layerColors[0] * wGrass + layerColors[1] * wDirt + layerColors[2] * wRock + layerColors[3] * wSnow)
                    / weightSum;

                // Micro-variation so flat fields don't read as one untextured sheet.
                float variation = Noise.Fbm2(911, wx * 0.16f, wz * 0.16f, 2) * 0.07f;
                float slopeShade = 0.92f + normal.Y * 0.08f;
                color = new Vector4(
                    Math.Clamp((color.X + variation) * slopeShade, 0f, 1f),
                    Math.Clamp((color.Y + variation) * slopeShade, 0f, 1f),
                    Math.Clamp((color.Z + variation * 0.7f) * slopeShade, 0f, 1f),
                    1f);

                destination[z * resX + x] = new MeshVertex
                {
                    Position = new Vector3(wx, h, wz),
                    Normal = normal,
                    Color = color,
                    UV = new Vector2(x / (float)(resX - 1) * 12f, z / (float)(resZ - 1) * 12f),
                };
            }
        }
    }

    /// <summary>Default grass/dirt/rock/snow palette used when a terrain's own layer colours
    /// can't be read (e.g. a Room Editor preview of a terrain whose .terrain.json is missing).</summary>
    public static Vector4[] DefaultLayerColors { get; } =
    [
        new(0.33f, 0.52f, 0.26f, 1f),
        new(0.44f, 0.34f, 0.23f, 1f),
        new(0.46f, 0.46f, 0.50f, 1f),
        new(0.90f, 0.92f, 0.96f, 1f),
    ];
}
