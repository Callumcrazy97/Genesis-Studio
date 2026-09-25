using Newtonsoft.Json.Linq;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>One designer-facing capability that can be attached to an Object.</summary>
public sealed record ObjectComponentDefinition(
    string Type,
    string DisplayName,
    string AssetProperty,
    ResourceKind? AssetKind,
    JObject Defaults,
    bool Removable = true);

/// <summary>
/// Canonical Object component stack. It owns ordering, stable ids, enabled state and component
/// properties while mirroring the legacy root bindings needed by older rooms and importers.
/// </summary>
public sealed class ObjectCompositionModel
{
    public static IReadOnlyList<ObjectComponentDefinition> Definitions { get; } =
    [
        Def("SpriteComponent", "Image / Animated Sprite", "Sprite", ResourceKind.Image,
            new JObject { ["Sprite"] = "", ["ImageSpeed"] = 1f, ["Alpha"] = 1f, ["Depth"] = 0 }),
        Def("ModelRendererComponent", "3D Model", "ModelAsset", ResourceKind.Model,
            new JObject { ["ModelAsset"] = "", ["ScaleX"] = 1f, ["ScaleY"] = 1f, ["ScaleZ"] = 1f,
                ["CastShadows"] = true, ["ReceiveShadows"] = true,
                ["KeepPreviousTransform"] = false }),
        Def("ModelAnimatorComponent", "Model Animation", "", null,
            new JObject { ["ClipName"] = "", ["ClipFps"] = 60f, ["Playing"] = true, ["Loop"] = true,
                ["PlaybackSpeed"] = 1f, ["BlendTime"] = 0.15f }),
        Def("ModelMorphComponent", "Model Morph Targets", "", null,
            new JObject { ["Enabled"] = true, ["InitialWeights"] = "" }),
        Def("ThirdPersonCameraComponent", "Third-Person Camera", "", null,
            new JObject { ["Distance"] = 7.5f, ["Pitch"] = 15f, ["Yaw"] = 0f,
                ["ShoulderX"] = 1.2f, ["Height"] = 2.4f, ["ShoulderZ"] = 0f,
                ["CollisionEnabled"] = true, ["MouseLook"] = true }),
        Def("NavMeshAgentComponent", "Navigation Agent", "", null,
            new JObject { ["Speed"] = 3.8f, ["StoppingDistance"] = .1f }),
        Def("CrowdAgentComponent", "Local Avoidance", "", null,
            new JObject { ["Enabled"] = true, ["Radius"] = .35f, ["NeighborDistance"] = 3f,
                ["AvoidanceStrength"] = .75f, ["MaxNeighbors"] = 12,
                ["NearUpdateHz"] = 30f, ["FarUpdateHz"] = 6f,
                ["FarDistance"] = 35f, ["AvoidanceDistance"] = 250f }),
        Def("MaterialComponent", "Material / Texture", "Asset", ResourceKind.Image,
            new JObject { ["Asset"] = "" }),
        Def("ShaderComponent", "Shader", "Asset", ResourceKind.Shader,
            new JObject { ["Asset"] = "" }),
        Def("ParticleComponent", "Particle Effect", "Asset", ResourceKind.Particle,
            new JObject { ["Asset"] = "", ["Emitting"] = true, ["FollowEntity"] = true,
                ["RateScale"] = 1f, ["EmitRate"] = 0f }),
        Def("AudioComponent", "Audio", "Asset", ResourceKind.Audio,
            new JObject { ["Asset"] = "", ["AutoPlay"] = false, ["Spatial"] = true,
                ["Loop"] = false, ["Volume"] = 1f, ["Pitch"] = 1f }),
        // Keep the serialized type for backwards compatibility; the designer-facing capability
        // is a complete Light Emitter rather than the original fixed point-light record.
        Def("PointLightComponent", "Light Emitter", "", null,
            new JObject { ["Enabled"] = true, ["Color"] = new JArray(1f, 0.72f, 0.35f),
                ["SecondaryColor"] = new JArray(1f, 0.25f, 0.08f),
                ["TertiaryColor"] = new JArray(0.35f, 0.55f, 1f), ["ColorCount"] = 1,
                ["Offset"] = new JArray(0f, 0f, 0f), ["Radius"] = 8f, ["Intensity"] = 2f,
                ["Falloff"] = 2f, ["Action"] = "Steady", ["ActionSpeed"] = 1f,
                ["ActionAmount"] = 0.25f, ["Phase"] = 0f }),
        Def("PhysicsComponent", "Physics", "Preset", null,
            new JObject { ["Preset"] = "Dynamic", ["Gravity"] = 0f, ["GravityDirection"] = 270f,
                ["Friction"] = 0f, ["Solid"] = true }),
        Def("ScriptComponent", "PGSL Events", "", null,
            new JObject(), removable: false),
    ];

    private readonly JObject _document;

    public ObjectCompositionModel(JObject document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        if (_document["components"] is not JArray)
            _document["components"] = new JArray();
        NormalizeExisting();
        ImportLegacyBindings();
    }

    public JObject Document => _document;
    public JArray Components => (JArray)_document["components"]!;
    public int Count => Components.OfType<JObject>().Count();

    public IReadOnlyList<JObject> Snapshot() =>
        [.. Components.OfType<JObject>().Select(component => (JObject)component.DeepClone())];

    public JObject? Find(string type) => Components.OfType<JObject>()
        .FirstOrDefault(component => SameType(component, type));

    public JObject Ensure(string type)
    {
        ObjectComponentDefinition definition = Definition(type);
        JObject? existing = Find(type);
        if (existing != null) return existing;
        JObject component = NewComponent(definition);
        Components.Add(component);
        SynchronizeLegacyBindings();
        return component;
    }

    public JObject Add(string type)
    {
        // A prefab has one slot for each authored capability. Re-adding selects/returns that slot.
        return Ensure(type);
    }

    public bool Remove(string idOrType)
    {
        JObject? component = FindByIdOrType(idOrType);
        if (component == null) return false;
        ObjectComponentDefinition definition = Definition((string?)component["type"] ?? string.Empty);
        if (!definition.Removable) return false;
        component.Remove();
        SynchronizeLegacyBindings();
        return true;
    }

    public bool Move(string idOrType, int delta)
    {
        JObject? component = FindByIdOrType(idOrType);
        if (component == null || delta == 0) return false;
        int oldIndex = Components.IndexOf(component);
        int newIndex = Math.Clamp(oldIndex + delta, 0, Components.Count - 1);
        if (newIndex == oldIndex) return false;
        component.Remove();
        Components.Insert(newIndex, component);
        return true;
    }

    public bool SetEnabled(string idOrType, bool enabled)
    {
        JObject? component = FindByIdOrType(idOrType);
        if (component == null) return false;
        component["enabled"] = enabled;
        return true;
    }

    public JObject SetAsset(string type, string? projectRelativePath)
    {
        ObjectComponentDefinition definition = Definition(type);
        if (string.IsNullOrWhiteSpace(definition.AssetProperty))
            throw new InvalidOperationException($"{definition.DisplayName} does not use an asset.");
        JObject component = Ensure(type);
        Props(component)[definition.AssetProperty] = NormalPath(projectRelativePath);
        SynchronizeLegacyBindings();
        return component;
    }

    public JObject SetProperty(string type, string property, JToken? value)
    {
        JObject component = Ensure(type);
        Props(component)[property] = value?.DeepClone() ?? JValue.CreateNull();
        SynchronizeLegacyBindings();
        return component;
    }

    public JObject SetPropertyById(string id, string property, JToken? value)
    {
        JObject component = FindByIdOrType(id)
            ?? throw new KeyNotFoundException($"Object component '{id}' no longer exists.");
        Props(component)[property] = value?.DeepClone() ?? JValue.CreateNull();
        SynchronizeLegacyBindings();
        return component;
    }

    /// <summary>Mirror canonical bindings for readers that still consume schema-v2 root fields.</summary>
    public void SynchronizeLegacyBindings()
    {
        Mirror("SpriteComponent", "Sprite", "sprite");
        Mirror("ModelRendererComponent", "ModelAsset", "model");
        Mirror("MaterialComponent", "Asset", "material");
        Mirror("ShaderComponent", "Asset", "shader");
        Mirror("PhysicsComponent", "Preset", "physics");
    }

    public static ObjectComponentDefinition Definition(string type) => Definitions.FirstOrDefault(
        definition => string.Equals(definition.Type, type, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unsupported Object component type '{type}'.", nameof(type));

    public static JObject Props(JObject component)
    {
        if (component["props"] is JObject props) return props;
        props = new JObject();
        component["props"] = props;
        return props;
    }

    private void NormalizeExisting()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (JObject component in Components.OfType<JObject>().ToList())
        {
            string type = (string?)component["type"] ?? string.Empty;
            if (string.Equals(type, "ModelComponent", StringComparison.OrdinalIgnoreCase))
            {
                type = "ModelRendererComponent";
                component["type"] = type;
            }
            if (!Definitions.Any(definition => string.Equals(definition.Type, type, StringComparison.OrdinalIgnoreCase)))
                continue; // Preserve extension/plugin components unchanged.
            if (!seen.Add(type))
            {
                component.Remove();
                continue;
            }
            component["id"] ??= $"cmp-{Guid.NewGuid():N}";
            component["enabled"] ??= true;
            JObject props = Props(component);
            JObject defaults = Definition(type).Defaults;
            foreach (JProperty property in defaults.Properties())
                props[property.Name] ??= property.Value.DeepClone();
        }
    }

    private void ImportLegacyBindings()
    {
        Import("sprite", "SpriteComponent", "Sprite");
        Import("model", "ModelRendererComponent", "ModelAsset");
        Import("material", "MaterialComponent", "Asset");
        Import("shader", "ShaderComponent", "Asset");
        Import("physics", "PhysicsComponent", "Preset");
        SynchronizeLegacyBindings();
    }

    private void Import(string rootProperty, string type, string componentProperty)
    {
        string root = NormalPath((string?)_document[rootProperty]);
        JObject? existing = Find(type);
        if (existing == null && string.IsNullOrWhiteSpace(root)) return;
        JObject component = existing ?? EnsureWithoutSync(type);
        JObject props = Props(component);
        if (string.IsNullOrWhiteSpace((string?)props[componentProperty]) && !string.IsNullOrWhiteSpace(root))
            props[componentProperty] = root;
    }

    private JObject EnsureWithoutSync(string type)
    {
        JObject component = NewComponent(Definition(type));
        Components.Add(component);
        return component;
    }

    private static JObject NewComponent(ObjectComponentDefinition definition) => new()
    {
        ["id"] = $"cmp-{Guid.NewGuid():N}",
        ["type"] = definition.Type,
        ["enabled"] = true,
        ["props"] = definition.Defaults.DeepClone(),
    };

    private JObject? FindByIdOrType(string idOrType) => Components.OfType<JObject>().FirstOrDefault(component =>
        string.Equals((string?)component["id"], idOrType, StringComparison.OrdinalIgnoreCase)
        || string.Equals((string?)component["type"], idOrType, StringComparison.OrdinalIgnoreCase));

    private static bool SameType(JObject component, string type) =>
        string.Equals((string?)component["type"], type, StringComparison.OrdinalIgnoreCase);

    private void Mirror(string type, string property, string rootProperty)
    {
        JObject? component = Find(type);
        _document[rootProperty] = component == null ? string.Empty : NormalPath((string?)Props(component)[property]);
    }

    private static string NormalPath(string? path) => (path ?? string.Empty).Trim().Replace('\\', '/');

    private static ObjectComponentDefinition Def(
        string type, string name, string assetProperty, ResourceKind? assetKind, JObject defaults, bool removable = true) =>
        new(type, name, assetProperty, assetKind, defaults, removable);
}
