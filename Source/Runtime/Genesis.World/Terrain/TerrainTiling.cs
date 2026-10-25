using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.World.Terrain;

/// <summary>One tile of a terrain split by <see cref="TerrainTiling.Split"/>: where it sits in the grid of tiles, its heights and paint, and its share of the nature.</summary>
public sealed record TerrainTile(int Column, int Row, TerrainAsset Terrain, TerrainNatureDocument Nature);

/// <summary>
/// A large terrain made into tiles a room reads as the camera nears each (its terrain distance):
/// every tile keeps the original's coordinates, cell size and height range, and neighbouring
/// tiles share their edge samples, so they meet exactly where the original was one ground. The
/// nature's rules (grass, scatter layers, path settings) go to every tile; what is placed (water,
/// paths, points of interest, terrain entities) goes with the tile its centre is in.
/// </summary>
public static class TerrainTiling
{
    /// <summary>
    /// Splits <paramref name="source"/> into <paramref name="columns"/> by <paramref name="rows"/>
    /// tiles, in row order. Each tile has at most the original's cells divided by the count, plus
    /// the shared edge.
    /// </summary>
    public static IReadOnlyList<TerrainTile> Split(TerrainAsset source, TerrainNatureDocument nature, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(source);
        int cellsX = source.ResolutionX - 1, cellsZ = source.ResolutionZ - 1;
        if (columns < 1 || rows < 1 || columns > cellsX || rows > cellsZ)
            throw new ArgumentOutOfRangeException(nameof(columns), $"A terrain of {cellsX} by {cellsZ} cells cannot be made into {columns} by {rows} tiles.");
        int stepX = (cellsX + columns - 1) / columns, stepZ = (cellsZ + rows - 1) / rows;
        nature ??= new TerrainNatureDocument();
        var tiles = new List<TerrainTile>(columns * rows);
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < columns; column++)
        {
            int startX = Math.Min(column * stepX, cellsX - 1), startZ = Math.Min(row * stepZ, cellsZ - 1);
            int samplesX = Math.Min(stepX, cellsX - startX) + 1, samplesZ = Math.Min(stepZ, cellsZ - startZ) + 1;
            TerrainAsset tile = Section(source, startX, startZ, samplesX, samplesZ);
            // A thing on a shared edge goes to the first tile it touches (the last row and column keep their far edge).
            float minX = tile.OriginX, minZ = tile.OriginZ;
            float maxX = minX + (samplesX - 1) * source.CellSize, maxZ = minZ + (samplesZ - 1) * source.CellSize;
            bool lastColumn = column == columns - 1, lastRow = row == rows - 1;
            bool Holds(Vector3 point) =>
                (point.X >= minX || column == 0) && (point.X < maxX || lastColumn)
                && (point.Z >= minZ || row == 0) && (point.Z < maxZ || lastRow);
            tiles.Add(new TerrainTile(column, row, tile, NatureFor(nature, Holds)));
        }
        return tiles;
    }

    /// <summary>The samples from (<paramref name="startX"/>, <paramref name="startZ"/>) of <paramref name="source"/> as a terrain of their own, where they were.</summary>
    public static TerrainAsset Section(TerrainAsset source, int startX, int startZ, int samplesX, int samplesZ)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (startX < 0 || startZ < 0 || samplesX < 2 || samplesZ < 2 || startX + samplesX > source.ResolutionX || startZ + samplesZ > source.ResolutionZ)
            throw new ArgumentOutOfRangeException(nameof(startX), "The section lies outside the terrain.");
        var tile = new TerrainAsset(samplesX, samplesZ, source.CellSize,
            source.OriginX + startX * source.CellSize, source.OriginZ + startZ * source.CellSize, source.MinHeight, source.MaxHeight);
        ushort[] heights = source.HeightsData, tileHeights = tile.HeightsData;
        byte[] splat = source.SplatmapData, tileSplat = tile.SplatmapData;
        byte[] extended = source.ExtendedSplatmapData, tileExtended = extended != null ? tile.EnsureExtendedSplatmap() : null;
        for (int z = 0; z < samplesZ; z++)
        {
            int from = (startZ + z) * source.ResolutionX + startX, to = z * samplesX;
            Array.Copy(heights, from, tileHeights, to, samplesX);
            Array.Copy(splat, from * 4, tileSplat, to * 4, samplesX * 4);
            if (tileExtended != null) Array.Copy(extended, from * 4, tileExtended, to * 4, samplesX * 4);
        }
        return tile;
    }

    // The nature's rules, and the placed things whose centre the tile holds.
    private static TerrainNatureDocument NatureFor(TerrainNatureDocument nature, Func<Vector3, bool> holds)
    {
        TerrainNatureDocument tile = TerrainNatureSerializer.Clone(nature);
        tile.Normalize();
        tile.WaterBodies.RemoveAll(water => !holds(water.Center));
        tile.Paths.RemoveAll(path => path.Points.Count == 0 || !holds(Middle(path.Points)));
        tile.PointsOfInterest.RemoveAll(point => !holds(point.Position));
        tile.PlacedEntities.RemoveAll(placed => !holds(placed.Position));
        // The baked foliage was for the whole terrain; each tile grows its own from the rules.
        tile.FoliageCacheFile = "";
        tile.FoliageCacheSha256 = "";
        tile.FoliageInstanceCount = 0;
        return tile;
    }

    private static Vector3 Middle(List<Vector3> points)
    {
        Vector3 sum = Vector3.Zero;
        foreach (Vector3 point in points) sum += point;
        return sum / points.Count;
    }
}
