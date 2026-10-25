using System.Text.Json.Nodes;
using Genesis.Application.Core.Resources;
using Genesis.World.Terrain;

namespace Genesis.Application.Core.Terrain;

/// <summary>
/// One Terrain resource made into tiles beside it ("Vale 1-1", "Vale 2-1" ...), each a Terrain
/// of its own with the original's look (its settings copied, the size made the tile's), its
/// share of the heights and paint, and its share of the nature (<see cref="TerrainTiling"/>).
/// A room placing the tiles where it placed the original reads each as the camera nears it, so
/// a world too large to load at once streams in by tile. The original resource is left as it was.
/// </summary>
public static class TerrainTileSplit
{
    /// <summary>A tile made: where it sits in the grid of tiles and its resource file.</summary>
    public sealed record Tile(int Column, int Row, string ResourcePath);

    /// <summary>
    /// Makes <paramref name="columns"/> by <paramref name="rows"/> tile resources of the Terrain
    /// resource at <paramref name="terrainResourcePath"/> (its .terrain.json), in the same folder.
    /// </summary>
    public static IReadOnlyList<Tile> Split(ResourceService resources, string terrainResourcePath, int columns, int rows)
    {
        ArgumentNullException.ThrowIfNull(resources);
        string source = Path.GetFullPath(terrainResourcePath);
        if (!File.Exists(source) || !source.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The terrain to split is not a Terrain resource.", terrainResourcePath);
        JsonObject settings = JsonNode.Parse(File.ReadAllText(source)) as JsonObject
            ?? throw new InvalidDataException("The terrain's settings are not a JSON object.");
        string binary = source + ".gterrain";
        // A terrain never sculpted has no heights saved yet; its landform is made as the editor makes it.
        TerrainAsset terrain = File.Exists(binary) ? TerrainAsset.Load(binary) : TerrainGenerator.Generate(Automation.TerrainShapeCommands.Params(settings));
        IReadOnlyList<TerrainTile> tiles = TerrainTiling.Split(terrain, TerrainNatureSerializer.LoadOrDefault(source), columns, rows);

        string folder = Path.GetDirectoryName(source)!;
        string stem = Path.GetFileName(source)[..^".terrain.json".Length];
        var made = new List<Tile>(tiles.Count);
        foreach (TerrainTile tile in tiles)
        {
            string path = resources.CreateResource(folder, ResourceKind.Terrain, $"{stem} {tile.Column + 1}-{tile.Row + 1}");
            JsonObject tileSettings = (JsonObject)settings.DeepClone();
            tileSettings["resolution"] = new JsonArray(tile.Terrain.ResolutionX, tile.Terrain.ResolutionZ);
            File.WriteAllText(path, tileSettings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            tile.Terrain.Save(path + ".gterrain");
            TerrainNatureSerializer.Save(path, tile.Nature);
            made.Add(new Tile(tile.Column, tile.Row, path));
        }
        return made;
    }
}
