using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>A read-only terrain preview of an Object. Placements retain the original Object reference.</summary>
public static class TerrainObjectResourceBridge
{
    private static readonly Dictionary<string, (DateTime Stamp, DateTime Read, TerrainEntityDocument Document)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static TerrainEntityDocument Load(string projectRoot, string path)
    {
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp && DateTime.UtcNow - cached.Read < TimeSpan.FromSeconds(1)) return cached.Document;
        JObject prefab = ObjectDefinitionResolver.PreviewPrefab(ObjectDefinitionResolver.Load(projectRoot, path));
        var result = new TerrainEntityDocument { Name = ResourceNames.Name(ResourceNames.FindProjectRoot(path), path, ResourceType.Object), Type = TerrainEntityType.Object, Icon = (string?)prefab["sprite"] ?? "" };
        JObject? Component(string name) => (prefab["components"] as JArray)?.OfType<JObject>().FirstOrDefault(component => (string?)component["type"] == name && (bool?)component["enabled"] != false)?["props"] as JObject;
        void Add(string type, string key, string? resource)
        {
            if (string.IsNullOrWhiteSpace(resource)) return;
            var component = new TerrainEntityComponent { Type = type }; component.Set(key, resource); result.Components.Add(component);
        }
        var model = Component("ModelRendererComponent");
        Add(TerrainEntityComponentKinds.Model, "Model", (string?)model?["ModelAsset"] ?? (string?)prefab["model"]);
        if (result.Components.FirstOrDefault(component => component.Type == TerrainEntityComponentKinds.Model) is { } mesh)
        {
            mesh.Set("Scale", model?["ScaleX"]?.ToString() ?? "1");
            mesh.Set("AnimationClip", (string?)Component("ModelAnimatorComponent")?["ClipName"] ?? "");
        }
        Add(TerrainEntityComponentKinds.Texture, "Texture", (string?)Component("MaterialComponent")?["Asset"]
            ?? (string?)prefab["material"] ?? (result.Components.Count == 0 ? (string?)prefab["sprite"] : null));
        Add(TerrainEntityComponentKinds.Shader, "Shader", (string?)Component("ShaderComponent")?["Asset"] ?? (string?)prefab["shader"]);
        Cache[path] = (stamp, DateTime.UtcNow, result); return result;
    }
}
