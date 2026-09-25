using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genesis.Runtime.Scripting;
using Newtonsoft.Json.Linq;

namespace Genesis.Runtime.Scene;

/// <summary>Resolves parent defaults and event overrides identically for authoring and play.</summary>
public static class ObjectDefinitionResolver
{
    public sealed record Definition(JObject Prefab, Dictionary<string, string> Events);

    public static Definition Load(string projectPath, string path, JObject workingCopy = null,
        IReadOnlyDictionary<string, string> workingEvents = null)
        => LoadCore(projectPath, Path.GetFullPath(path), workingCopy, workingEvents, new(StringComparer.OrdinalIgnoreCase));

    /// <summary>Resolve literal Create-event model assignments for editor previews without running gameplay code.</summary>
    public static JObject PreviewPrefab(Definition definition)
    {
        var result = (JObject)definition.Prefab.DeepClone();
        if (!definition.Events.TryGetValue("Create", out string source)) return result;
        if (!TryPreviewModel(source, out string model)) return result;
        result["model"] = model;
        var components = result["components"] as JArray;
        if (components is null)
        {
            components = new JArray();
            result["components"] = components;
        }
        var component = components.OfType<JObject>().FirstOrDefault(item => (string)item["type"] == "ModelRendererComponent");
        if (component is null) { component = new JObject { ["type"] = "ModelRendererComponent", ["props"] = new JObject() }; components.Add(component); }
        component["enabled"] = true;
        var props = component["props"] as JObject;
        if (props is null)
        {
            props = new JObject();
            component["props"] = props;
        }
        props["ModelAsset"] = model;
        props.Remove("Model");
        return result;
    }

    public static bool TryPreviewModel(string source, out string model)
    {
        model = null;
        try
        {
            foreach (var statement in PgslAstBuilder.Parse(source).Body)
                if (statement is Genesis.Runtime.Scripting.Ast.ExprStmt { Expression: Genesis.Runtime.Scripting.Ast.CallExpr call }
                    && call.Name == "ModelSet" && call.Arguments.Count == 1
                    && call.Arguments[0] is Genesis.Runtime.Scripting.Ast.StringExpr text) model = text.Value;
        }
        catch (Exception) { /* Incomplete code is reported by the code editor; previews retain the authored default. */ }
        return model is not null;
    }

    private static Definition LoadCore(string project, string path, JObject workingCopy,
        IReadOnlyDictionary<string, string> workingEvents, HashSet<string> ancestors)
    {
        if (!ancestors.Add(path) || ancestors.Count > 64) throw new InvalidDataException("Object parents form an inheritance cycle.");
        var own = workingCopy ?? JObject.Parse(File.ReadAllText(path));
        var result = new JObject();
        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if ((string)own["parent"] is { Length: > 0 } parent)
        {
            string parentPath = RoomSceneBuilder.ResolvePrefabPath(project, parent);
            if (parentPath == null) throw new FileNotFoundException("Parent Object is missing: " + parent);
            var inherited = LoadCore(project, Path.GetFullPath(parentPath), null, null, ancestors);
            result = (JObject)inherited.Prefab.DeepClone(); events = inherited.Events;
        }
        var components = result["components"] as JArray ?? new JArray();
        foreach (var property in own.Properties())
            if (property.Name != "components") result[property.Name] = property.Value.DeepClone();
        foreach (var component in (own["components"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var existing = components.OfType<JObject>().FirstOrDefault(candidate =>
                string.Equals((string)candidate["type"], (string)component["type"], StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Replace(component.DeepClone()); else components.Add(component.DeepClone());
        }
        result["components"] = components;
        foreach (var pair in workingEvents ?? ObjectEventStore.Load(path)) events[pair.Key] = pair.Value;
        return new(result, events);
    }
}
