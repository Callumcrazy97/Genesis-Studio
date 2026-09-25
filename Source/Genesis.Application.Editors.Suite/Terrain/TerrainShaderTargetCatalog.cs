using System.Text.Json;
using Genesis.Application.Core.Resources;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Stable shader-target IDs for authored terrain sub-resources. Shader Editor's component picker
/// and Terrain Editor's inspector both use this list so a water body, paint layer, or path is the
/// same target in both editors.
/// </summary>
internal readonly record struct TerrainShaderTarget(string Id, string Label);

internal static class TerrainShaderTargetCatalog
{
    public const string None = "none";
    public const string All = "all";
    public const string Vegetation = "nature:vegetation";

    public static string Normalize(string? component)
    {
        if (string.IsNullOrWhiteSpace(component)
            || string.Equals(component, "terrain:surface", StringComparison.OrdinalIgnoreCase))
        {
            return All;
        }

        return component;
    }

    public static string ForComponent(TerrainComponentsPanel.ComponentKind kind, string id) => kind switch
    {
        TerrainComponentsPanel.ComponentKind.Layer => "layer:" + id,
        TerrainComponentsPanel.ComponentKind.Path => "path:" + id,
        TerrainComponentsPanel.ComponentKind.Water => "water:" + id,
        TerrainComponentsPanel.ComponentKind.Foliage => Vegetation,
        TerrainComponentsPanel.ComponentKind.PointOfInterest => "point:" + id,
        TerrainComponentsPanel.ComponentKind.Entity => "entity:" + id,
        _ => string.Empty,
    };

    public static IReadOnlyList<TerrainShaderTarget> FromResource(string? terrainResourcePath)
    {
        if (string.IsNullOrWhiteSpace(terrainResourcePath) || !File.Exists(terrainResourcePath))
        {
            return [];
        }

        List<(int Index, string Name)> layers = [];
        List<string> entities = [];
        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(terrainResourcePath));
            if (json.RootElement.TryGetProperty("layers", out JsonElement layerArray)
                && layerArray.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement layer in layerArray.EnumerateArray())
                {
                    string name = layer.TryGetProperty("name", out JsonElement value)
                        ? value.GetString() ?? $"Layer {index + 1}"
                        : $"Layer {index + 1}";
                    layers.Add((index, name));
                    index++;
                }
            }

            if (json.RootElement.TryGetProperty("entities", out JsonElement entityArray)
                && entityArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entity in entityArray.EnumerateArray())
                {
                    if (entity.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string path = entity.GetString() ?? string.Empty;
                    if (path.Length > 0)
                    {
                        entities.Add(path);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            // A broken terrain is reported by its own editor. Shader targeting still offers None/All.
        }

        TerrainNatureDocument nature;
        try
        {
            nature = TerrainNatureSerializer.LoadOrDefault(terrainResourcePath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            nature = new TerrainNatureDocument();
        }

        return Collect(layers, entities, nature, nature.FoliageInstanceCount);
    }

    public static IReadOnlyList<TerrainShaderTarget> FromLive(
        IReadOnlyList<(int Index, string Name)> layers,
        IReadOnlyList<string> entities,
        TerrainNatureDocument nature,
        int foliageInstanceCount) =>
        Collect(layers, entities, nature, foliageInstanceCount);

    private static IReadOnlyList<TerrainShaderTarget> Collect(
        IReadOnlyList<(int Index, string Name)> layers,
        IReadOnlyList<string> entities,
        TerrainNatureDocument nature,
        int foliageInstanceCount)
    {
        nature ??= new TerrainNatureDocument();
        nature.Normalize();
        List<TerrainShaderTarget> targets = [];
        foreach ((int index, string name) in layers)
        {
            targets.Add(new TerrainShaderTarget($"layer:{index}", "Layer · " + name));
        }

        foreach (string path in entities)
        {
            targets.Add(new TerrainShaderTarget("entity:" + path, "Entity · " + ResourceDisplayName(path)));
        }

        if (foliageInstanceCount > 0 || !string.IsNullOrWhiteSpace(nature.FoliageCacheFile))
        {
            targets.Add(new TerrainShaderTarget(
                Vegetation,
                $"Vegetation · {foliageInstanceCount:N0} instances"));
        }

        foreach (TerrainPathDefinition path in nature.Paths)
        {
            targets.Add(new TerrainShaderTarget("path:" + path.Id, "Path · " + path.Name));
        }

        foreach (TerrainWaterDefinition water in nature.WaterBodies)
        {
            targets.Add(new TerrainShaderTarget("water:" + water.Id, "Water · " + water.Name));
        }

        foreach (TerrainPointOfInterest point in nature.PointsOfInterest)
        {
            targets.Add(new TerrainShaderTarget("point:" + point.Id, "Point · " + point.Name));
        }

        return targets;
    }

    private static string ResourceDisplayName(string path)
    {
        return Genesis.Application.Core.Resources.ResourceDisplayName.Format(path);
    }
}
