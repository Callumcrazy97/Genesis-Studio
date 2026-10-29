using System.Numerics;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.World.Foliage;

namespace Genesis.Application.Core.Terrain;

/// <summary>
/// The ground the built-in grass comes with. A new terrain's Grass layer is a light green that
/// suits bare ground; under the built-in meadow grass the ground between the tufts is a darker
/// green (<see cref="TerrainGrassRuleSettings.DefaultGroundColor"/>), as in the Nature Walk picture
/// Cal chose. Only a layer still holding the stock colour, with no Image, is changed.
/// </summary>
public static class TerrainGrassGround
{
    /// <summary>A new terrain's Grass layer colour (the Terrain resource's template).</summary>
    public static readonly Vector3 StockGrassColor = new(0.33f, 0.52f, 0.26f);

    /// <summary>True for a layer the grass may recolour: the stock Grass colour and no Image.</summary>
    public static bool IsStock(float[]? colour, string? image) =>
        string.IsNullOrWhiteSpace(image) && colour is { Length: >= 3 }
        && MathF.Abs(colour[0] - StockGrassColor.X) < 0.006f
        && MathF.Abs(colour[1] - StockGrassColor.Y) < 0.006f
        && MathF.Abs(colour[2] - StockGrassColor.Z) < 0.006f;

    /// <summary>The ground colour under the built-in grass, as a layer stores it.</summary>
    public static float[] GroundColor =>
        [TerrainGrassRuleSettings.DefaultGroundColor.X, TerrainGrassRuleSettings.DefaultGroundColor.Y, TerrainGrassRuleSettings.DefaultGroundColor.Z];

    /// <summary>
    /// Recolours a stock Grass layer in a Terrain resource's settings file (the layer named Grass,
    /// else the first), backing the file up first. False when there was none to change.
    /// </summary>
    public static bool SuitTerrainFile(string terrainPath)
    {
        if (!File.Exists(terrainPath) || JsonNode.Parse(File.ReadAllText(terrainPath)) is not JsonObject settings) return false;
        if (settings["layers"] is not JsonArray layers || layers.Count == 0) return false;
        JsonObject? grass = layers.OfType<JsonObject>().FirstOrDefault(layer =>
            (layer["name"]?.GetValue<string>() ?? "").Contains("grass", StringComparison.OrdinalIgnoreCase))
            ?? layers[0] as JsonObject;
        if (grass is null) return false;
        float[]? colour = grass["color"] is JsonArray parts && parts.Count >= 3
            ? parts.Take(3).Select(part => part is JsonValue value && value.TryGetValue(out double number) ? (float)number : float.NaN).ToArray()
            : null;
        string? image = grass["image"] is JsonValue imageValue && imageValue.TryGetValue(out string? text) ? text : null;
        if (!IsStock(colour, image)) return false;
        grass["color"] = new JsonArray(GroundColor.Select(part => (JsonNode)JsonValue.Create(Math.Round(part, 3))!).ToArray());
        ResourceBackupService.BackupBeforeOverwrite(terrainPath);
        File.WriteAllText(terrainPath, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return true;
    }
}
