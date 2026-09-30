using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Runtime.Scene;

/// <summary>Resolves parent defaults and event overrides identically for authoring and play.</summary>
public static class ObjectDefinitionResolver
{
    public sealed record Definition(JObject Prefab, Dictionary<string, string> Events);

    private sealed class SpawnEntry
    {
        public Definition Definition;
        public JObject Prefab;
        public long StampTicks;
        public long Generation;
        public long NextCheckMilliseconds;
    }

    private static readonly Dictionary<(string Project, string Path), SpawnEntry> SpawnCache = new();
    private static readonly object SpawnGate = new();

    /// <summary>
    /// Resolves an Object for runtime spawning. Each spawn used to stat and parse the object file
    /// (and its parents), enumerate and read every event script, and parse the Create event for a
    /// model preview; bullet/particle spawners paid all of that per instance. The resolved result is
    /// now shared until an explicit invalidation or the bounded fallback check sees a change; every
    /// caller still receives its own prefab copy.
    /// </summary>
    public static bool TryLoadForSpawn(string projectPath, string path, out Definition definition, out JObject prefab)
    {
        definition = null;
        prefab = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var key = (projectPath ?? string.Empty, path);
        long now = Environment.TickCount64;
        long generation = RuntimeAssetPolicy.Generation;
        SpawnEntry entry;
        lock (SpawnGate)
        {
            if (SpawnCache.TryGetValue(key, out entry)
                && entry.Generation == generation && now < entry.NextCheckMilliseconds)
            {
                definition = entry.Definition;
                prefab = (JObject)entry.Prefab.DeepClone();
                return true;
            }
        }

        AssetIoCounters.Check(2);
        if (!File.Exists(path)) return false;
        long stamp = File.GetLastWriteTimeUtc(path).Ticks;
        long next = RuntimeAssetPolicy.NextCheck(now, RuntimeAssetPolicy.FramePathIntervalMilliseconds,
            StringComparer.OrdinalIgnoreCase.GetHashCode(path));
        lock (SpawnGate)
        {
            if (entry != null && entry.StampTicks == stamp && entry.Generation == generation)
            {
                entry.NextCheckMilliseconds = next;
                definition = entry.Definition;
                prefab = (JObject)entry.Prefab.DeepClone();
                return true;
            }
        }

        AssetIoCounters.Read();
        Definition loaded = Load(projectPath, path);
        JObject preview = PreviewPrefab(loaded);
        lock (SpawnGate)
        {
            SpawnCache[key] = new SpawnEntry
            {
                Definition = loaded,
                Prefab = preview,
                StampTicks = stamp,
                Generation = generation,
                NextCheckMilliseconds = next,
            };
        }
        definition = loaded;
        prefab = (JObject)preview.DeepClone();
        return true;
    }

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
