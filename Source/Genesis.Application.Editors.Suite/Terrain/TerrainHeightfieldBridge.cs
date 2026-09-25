using System.Numerics;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public static class TerrainHeightfieldBridge
{
    public static TerrainAsset ToHeightfield(TerrainCreationResult result, Vector3 position)
    {
        var heights = result.Heights ?? throw new InvalidOperationException("Volume surfaces cannot replace a heightfield.");
        float width = result.Recipe.Width, length = result.Recipe.Length;
        float cell = Math.Max(width / (heights.GetLength(0) - 1), length / (heights.GetLength(1) - 1));
        int nx = Math.Max(2, (int)Math.Round(width / cell) + 1), nz = Math.Max(2, (int)Math.Round(length / cell) + 1);
        float low = Math.Min(result.Recipe.MinHeight, heights.Cast<float>().Min() - 10) + position.Y;
        float high = Math.Max(result.Recipe.MaxHeight, heights.Cast<float>().Max() + 10) + position.Y;
        var terrain = new TerrainAsset(nx, nz, cell, position.X - (nx - 1) * cell / 2, position.Z - (nz - 1) * cell / 2, low, high);
        for (int z = 0; z < nz; z++) for (int x = 0; x < nx; x++)
        {
            float sx = x / (float)(nx - 1) * (heights.GetLength(0) - 1), sz = z / (float)(nz - 1) * (heights.GetLength(1) - 1);
            int ax = (int)sx, az = (int)sz, bx = Math.Min(ax + 1, heights.GetLength(0) - 1), bz = Math.Min(az + 1, heights.GetLength(1) - 1);
            terrain.SetHeight(x, z, float.Lerp(float.Lerp(heights[ax, az], heights[bx, az], sx - ax), float.Lerp(heights[ax, bz], heights[bx, bz], sx - ax), sz - az) + position.Y);
        }
        return terrain;
    }

    public static TerrainCreationResult FromHeightfield(TerrainAsset asset, string name)
    {
        float width = (asset.ResolutionX - 1) * asset.CellSize, length = (asset.ResolutionZ - 1) * asset.CellSize;
        var recipe = new TerrainCreationRecipe { Name = name, Source = TerrainCreationSource.Heightmap, Width = width, Length = length, Spacing = asset.CellSize, MinHeight = asset.MinHeight, MaxHeight = asset.MaxHeight };
        var heights = new float[asset.ResolutionX, asset.ResolutionZ];
        for (int z = 0; z < asset.ResolutionZ; z++) for (int x = 0; x < asset.ResolutionX; x++) heights[x, z] = (asset.GetHeight(x, z) - asset.MinHeight) / Math.Max(.01f, asset.MaxHeight - asset.MinHeight);
        return TerrainSectionGenerator.Generate(recipe, heightmap: heights);
    }
}
