using System.Globalization;
using System.Numerics;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Inspector;

public sealed record ResourceInspectorEditRequest(
    ResourceItem Resource,
    string PropertyPath,
    object? Value);

public sealed class ResourceInspectorEditedEventArgs(
    ResourceItem resource,
    string propertyPath,
    object? value,
    bool routedToOpenEditor) : EventArgs
{
    public ResourceItem Resource { get; } = resource;
    public string PropertyPath { get; } = propertyPath;
    public object? Value { get; } = value;
    public bool RoutedToOpenEditor { get; } = routedToOpenEditor;
    public bool Persisted => !RoutedToOpenEditor;
}

/// <summary>
/// Typed, collapsible editing surface for selected resources. JSON resources expose their scalar
/// leaves while shader and PGSL code use reflection, keeping source bodies out of the Inspector.
/// </summary>
public sealed class ResourceInspectorPropertySurface : Panel
{
    private const int MaxFields = 240;
    private static readonly JsonSerializerOptions WriteOptions =
        new(JsonSerializerOptions.Default) { WriteIndented = true };
    private static readonly string[] Components = ["X", "Y", "Z", "W"];
    private static readonly IReadOnlyDictionary<string, string[]> Choices =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["dimension"] = ["TwoD", "ThreeD"],
            ["authoringMode"] = Enum.GetNames<ShaderAuthoringMode>(),
            ["targetType"] = Enum.GetNames<ShaderTargetType>(),
            ["pipeline"] = Enum.GetNames<ShaderAssetPipeline>(),
            ["profile"] = ["ps_5_0"],
            ["blendMode"] = ["Alpha", "Additive", "Multiply", "Premultiplied"],
            ["shape"] = ["Point", "Circle", "Box", "Cone", "Sphere", "Hemisphere", "Mesh"],
            ["type"] = ["Foliage", "Water", "Path", "Point", "Structure"],
            ["seedProfile"] = ["Flat", "RollingHills", "Mountains", "Valley", "Plateau"],
            ["culling"] = ["Default", "Back", "Front", "None"],
            ["windingOrder"] = ["Default", "Clockwise", "CounterClockwise"],
            ["action"] = ["Steady", "Flicker", "Pulse", "Glow", "ColourCycle"],
        };

    private readonly TableLayoutPanel _groups;
    private readonly Dictionary<string, Action<object?>> _setters =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _livePropertyPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _groupNames = [];
    private readonly List<GroupView> _groupViews = [];
    private readonly Dictionary<string, bool> _groupExpansion = new(StringComparer.OrdinalIgnoreCase);
    private ResourceItem? _resource;
    private JsonObject? _json;
    private string? _projectRoot;
    private int _fieldCount;
    private string _filterText = string.Empty;
    private string _liveSchema = string.Empty;
    private bool _includeResourceDocument;
    private bool _refreshingValues;
    private readonly Dictionary<string, object?> _lastLiveValues = new(StringComparer.OrdinalIgnoreCase);
    private (long Ticks, long Length) _documentStamp;
    private bool _documentSchemaDirty;
    private readonly Dictionary<string, (Control Control, object Initial)> _drawers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Diagnostics used by regression tests: scalar edits must not increase this counter.</summary>
    public int LayoutBuildCount { get; private set; }
    public int ValueRefreshCount { get; private set; }

    public ResourceInspectorPropertySurface()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Name = "InspectorPropertySurface";
        Tag = "canvas";
        _groups = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Margin = Padding.Empty,
            Name = "InspectorPropertyGroups",
            Padding = Padding.Empty,
            RowCount = 0,
            Tag = "canvas",
        };
        _groups.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_groups);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<ResourceInspectorEditRequest, bool>? EditRouter { get; set; }

    public event EventHandler<ResourceInspectorEditedEventArgs>? ResourceEdited;

    public IReadOnlyList<string> EditablePropertyPaths => _setters.Keys.ToArray();

    public IReadOnlyList<string> GroupNames => _groupNames.ToArray();

    public bool HasContent => _groups.RowCount > 0;

    public bool ShowIdentityGroup { get; set; } = true;

    /// <summary>Filters logical groups without discarding their expansion state or editors.</summary>
    public void SetFilter(string? text)
    {
        string next = text?.Trim() ?? string.Empty;
        if (_filterText == next) return;
        _filterText = next;
        ApplyFilter();
    }

    public void Inspect(
        ResourceItem resource,
        IReadOnlyList<ResourceInspectorLiveValue>? liveValues = null)
        => InspectCore(resource, liveValues, includeResourceDocument: true);

    /// <summary>Uses the same card and drawer stack for an editor-owned live context.</summary>
    public void InspectLive(
        ResourceItem resource,
        IReadOnlyList<ResourceInspectorLiveValue> liveValues)
        => InspectCore(resource, liveValues, includeResourceDocument: false);

    private void InspectCore(
        ResourceItem resource,
        IReadOnlyList<ResourceInspectorLiveValue>? liveValues,
        bool includeResourceDocument)
    {
        string schema = LiveSchema(liveValues ?? []);
        bool liveOnly = !includeResourceDocument
            || (liveValues is { Count: > 0 } && resource.Kind != ResourceKind.GameObject);
        bool sameDocument = liveOnly || (_json is not null && !_documentSchemaDirty
            && DocumentStamp(resource.FullPath) == _documentStamp);
        if (sameDocument && _resource is not null && _includeResourceDocument == includeResourceDocument
            && string.Equals(_resource.FullPath, resource.FullPath, StringComparison.OrdinalIgnoreCase)
            && _liveSchema == schema && _drawers.Count > 0)
        {
            _resource = resource;
            RefreshLiveValues(liveValues ?? []);
            return;
        }
        LayoutBuildCount++;
        _documentSchemaDirty = false;
        _liveSchema = schema;
        _includeResourceDocument = includeResourceDocument;
        _drawers.Clear();
        _lastLiveValues.Clear();
        foreach (ResourceInspectorLiveValue live in liveValues ?? []) _lastLiveValues[live.PropertyPath] = live.Value;
        _resource = resource;
        _json = null;
        _projectRoot = ResolveProjectRoot(resource.FullPath);
        _fieldCount = 0;
        _setters.Clear();
        _livePropertyPaths.Clear();
        _groupNames.Clear();
        _groups.SuspendLayout();
        ClearGroups();

        try
        {
            foreach (ResourceInspectorLiveValue value in liveValues ?? [])
                _livePropertyPaths.Add(value.PropertyPath);
            // Play-mode values stay at the top, like Unity's live component state, so a large
            // authored Object does not bury the running instance below hundreds of JSON fields.
            BuildLiveValues(liveValues ?? []);
            if (includeResourceDocument && !resource.IsFolder && File.Exists(resource.FullPath))
            {
                bool hasLiveEditor = _livePropertyPaths.Count > 0;
                if (resource.Kind == ResourceKind.PgslScript && !hasLiveEditor)
                {
                    BuildPgsl(File.ReadAllText(resource.FullPath));
                }
                else if (resource.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         && (!hasLiveEditor || resource.Kind == ResourceKind.GameObject))
                {
                    BuildJson(File.ReadAllText(resource.FullPath), resource.Kind);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or JsonException or InvalidDataException)
        {
            AddMessage("Properties unavailable", exception.Message);
        }
        finally
        {
            _documentStamp = _json is not null ? DocumentStamp(resource.FullPath) : default;
            _groups.ResumeLayout(performLayout: true);
            ApplyTheme();
            ApplyFilter();
        }
    }

    /// <summary>Detach a hidden/removed browser selection before focus-loss callbacks can commit it.</summary>
    public void ClearSelection()
    {
        _refreshingValues = true;
        _resource = null; _json = null; _projectRoot = null;
        _setters.Clear(); _livePropertyPaths.Clear(); _drawers.Clear(); _lastLiveValues.Clear();
        _groupNames.Clear(); _liveSchema = string.Empty; _documentStamp = default;
        _documentSchemaDirty = false; _fieldCount = 0;
        _groups.SuspendLayout();
        try { ClearGroups(); }
        finally { _groups.ResumeLayout(true); _refreshingValues = false; }
    }

    public bool SetValue(string propertyPath, object? value)
    {
        if (!_setters.TryGetValue(propertyPath, out Action<object?>? setter)) return false;
        setter(value);
        return true;
    }

    public void ApplyTheme()
    {
        BackColor = EditorChrome.Canvas;
        _groups.BackColor = EditorChrome.Canvas;
        foreach (Control control in Descendants(this))
        {
            switch (control)
            {
                case Button button when button.Name.StartsWith("InspectorGroup", StringComparison.Ordinal):
                    button.BackColor = EditorChrome.Raised;
                    button.ForeColor = EditorChrome.Text;
                    button.FlatAppearance.BorderColor = EditorChrome.Border;
                    break;
                case Button button when !Equals(button.Tag, "property-color"):
                    button.BackColor = EditorChrome.Raised;
                    button.ForeColor = EditorChrome.Text;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = EditorChrome.Border;
                    break;
                case TextBox text:
                    text.BackColor = EditorChrome.Raised;
                    text.ForeColor = EditorChrome.Text;
                    text.BorderStyle = BorderStyle.None;
                    break;
                case ComboBox combo:
                    combo.BackColor = EditorChrome.Raised;
                    combo.ForeColor = EditorChrome.Text;
                    break;
                case NumericUpDown number:
                    number.BackColor = EditorChrome.Raised;
                    number.ForeColor = EditorChrome.Text;
                    break;
                case CheckBox check:
                    check.BackColor = EditorChrome.Surface;
                    check.ForeColor = EditorChrome.Text;
                    break;
                case TrackBar track:
                    track.BackColor = EditorChrome.Surface;
                    break;
                case Label label:
                    label.BackColor = EditorChrome.Surface;
                    label.ForeColor = label.Name.EndsWith("Hint", StringComparison.Ordinal)
                        ? EditorChrome.Muted
                        : EditorChrome.Text;
                    break;
                case TableLayoutPanel table:
                    table.BackColor = table == _groups ? EditorChrome.Canvas : EditorChrome.Surface;
                    break;
                case Panel panel:
                    panel.BackColor = EditorChrome.Surface;
                    break;
            }
        }
    }

    private void BuildJson(string text, ResourceKind kind)
    {
        _json = JsonNode.Parse(text) as JsonObject
                ?? throw new JsonException("The resource root is not a JSON object.");

        EnsureIdentityFields(kind, _json);
        EnsureRasterOverrides(kind, _json);

        Dictionary<string, List<FieldSpec>> groups = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, JsonNode? node) in _json)
        {
            // The browser's transactional rename owns global identity; a document title must not silently create another name.
            if (name.Equals("name", StringComparison.OrdinalIgnoreCase) || IsMetadata(name)
                || kind == ResourceKind.Shader && ShouldSkipShaderJsonField(name, _json)
                || ShouldSkipStructuredField(kind, name, _livePropertyPaths.Count > 0))
            {
                continue;
            }

            if (kind == ResourceKind.GameObject)
            {
                if (name.Equals("shaderParameters", StringComparison.OrdinalIgnoreCase)
                    && node is JsonObject shaderOverrides
                    && CollectObjectShaderParameters(groups, shaderOverrides))
                {
                    continue;
                }
                if (name.Equals("components", StringComparison.OrdinalIgnoreCase)
                    && node is JsonArray components)
                {
                    CollectObjectComponents(groups, components);
                    continue;
                }

                // These are compatibility mirrors or editor bookkeeping. Their canonical values
                // live in the component stack/event files and exposing both copies lets them drift.
                if (IsObjectCompatibilityField(name)) continue;
            }

            if (kind == ResourceKind.Image
                && name.Equals("usage", StringComparison.OrdinalIgnoreCase)
                && node is JsonObject usage)
            {
                CollectImageUsage(groups, usage);
                continue;
            }

            string group = TopLevelGroupName(kind, name, node);
            Action<JsonNode?> assign = value => _json[name] = value;
            if (kind == ResourceKind.Shader
                && name.Equals("targetType", StringComparison.OrdinalIgnoreCase))
            {
                assign = value =>
                {
                    _json[name] = value;
                    if (value is JsonValue targetValue
                        && targetValue.TryGetValue<string>(out string? targetText)
                        && Enum.TryParse(targetText, ignoreCase: true, out ShaderTargetType target))
                    {
                        _json["pipeline"] = PipelineForShaderTarget(target).ToString();
                    }
                };
            }

            CollectJsonFields(groups, group, name, Humanize(name), node, assign, depth: 0);
        }

        if (kind == ResourceKind.Shader) CollectShaderParameters(groups);
        if (kind == ResourceKind.Shader) CollectShaderResources(groups);
        int groupIndex = 0;
        foreach ((string name, List<FieldSpec> fields) in groups
                     .OrderBy(pair => InspectorGroupOrder(pair.Key))
                     .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (fields.Count > 0 && (ShowIdentityGroup || !name.Equals("Identity", StringComparison.OrdinalIgnoreCase)))
            {
                AddGroup(name, fields, expanded: groupIndex == 0 || name == "Parameters");
                groupIndex++;
            }
        }

        if (kind == ResourceKind.GameObject) AddComponentButton();

        if (_fieldCount >= MaxFields)
        {
            AddMessage("More properties", $"Showing the first {MaxFields} editable values.");
        }
        else if (_fieldCount == 0)
        {
            AddMessage("Properties", "This resource has no scalar values to edit here.");
        }
    }

    private static int InspectorGroupOrder(string name)
    {
        string value = name.ToLowerInvariant();
        if (value == "identity") return 0;
        if (value.Contains("transform")) return 10;
        if (value.Contains("mesh") || value.Contains("model")) return 20;
        if (value.Contains("shader") || value.Contains("material")) return 30;
        if (value.Contains("light")) return 40;
        if (value.Contains("audio")) return 50;
        if (value.Contains("script") || value.Contains("variable")) return 60;
        return 100;
    }

    private void AddComponentButton()
    {
        Button add = new()
        {
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            Height = 38,
            Margin = new Padding(12, 2, 12, 12),
            Name = "InspectorAddComponent",
            Text = "+ Add Component",
        };
        add.FlatAppearance.BorderColor = EditorChrome.Border;
        ContextMenuStrip menu = new();
        foreach (ObjectComponentDefinition definition in ObjectCompositionModel.Definitions)
        {
            ToolStripMenuItem item = new(definition.DisplayName) { Tag = definition };
            item.Click += (_, _) =>
            {
                if (_json is null || item.Tag is not ObjectComponentDefinition selected) return;
                JsonArray components = _json["components"] as JsonArray ?? [];
                _json["components"] = components;
                if (components.OfType<JsonObject>().Any(component =>
                        string.Equals(component["type"]?.GetValue<string>(), selected.Type,
                            StringComparison.OrdinalIgnoreCase))) return;
                JsonObject component = new()
                {
                    ["id"] = $"cmp-{Guid.NewGuid():N}",
                    ["type"] = selected.Type,
                    ["enabled"] = true,
                    ["props"] = JsonNode.Parse(selected.Defaults.ToString(Newtonsoft.Json.Formatting.None)),
                };
                components.Add(component);
                _documentSchemaDirty = true;
                Commit("components", components);
                if (_resource is not null) Inspect(_resource, []);
            };
            menu.Items.Add(item);
        }
        add.Click += (_, _) => menu.Show(add, new Point(0, add.Height));
        add.Disposed += (_, _) => menu.Dispose();
        AddGroupControl(add);
    }

    private void CollectImageUsage(
        Dictionary<string, List<FieldSpec>> groups,
        JsonObject usage)
    {
        foreach ((string name, JsonNode? node) in usage)
        {
            if (IsMetadata(name)) continue;
            if (name.Equals("allowed", StringComparison.OrdinalIgnoreCase)
                && _livePropertyPaths.Any(path =>
                    path.StartsWith("Usage.", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string path = "usage." + name;
            string group = name.ToLowerInvariant() switch
            {
                "allowed" => "Image usage",
                "sprite" => "Sprite rendering",
                "tileset" => "Tile set",
                "background" => "Background",
                "material" => "Material surface",
                _ => "Image usage",
            };
            CollectJsonFields(
                groups,
                group,
                path,
                Humanize(name),
                node,
                value => usage[name] = value,
                depth: 0);
        }
    }

    private void CollectObjectComponents(
        Dictionary<string, List<FieldSpec>> groups,
        JsonArray components)
    {
        for (int index = 0; index < components.Count && _fieldCount < MaxFields; index++)
        {
            if (components[index] is not JsonObject component) continue;

            string type = component["type"]?.GetValue<string>() ?? $"Component {index + 1}";
            ObjectComponentDefinition? definition = ObjectCompositionModel.Definitions.FirstOrDefault(candidate =>
                string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));
            string group = definition?.DisplayName ?? Humanize(RemoveComponentSuffix(type));
            string componentPath = $"components[{index}]";

            if (component["enabled"] is JsonValue enabledNode
                && TryScalar(enabledNode, out object? enabled)
                && enabled is bool)
            {
                AddSpec(groups, group, new FieldSpec("Enabled", componentPath + ".enabled", enabled, newValue =>
                {
                    bool converted = Convert.ToBoolean(newValue, CultureInfo.InvariantCulture);
                    component["enabled"] = converted;
                    Commit(componentPath + ".enabled", converted);
                }, null));
            }

            if (component["props"] is not JsonObject properties) continue;
            foreach ((string propertyName, JsonNode? value) in properties)
            {
                // ScriptClass is the generated runtime binding for this Object, not an authored
                // variable. Event/script variables are presented by their code-aware surfaces.
                if (type.Equals("ScriptComponent", StringComparison.OrdinalIgnoreCase)
                    && propertyName.Equals("ScriptClass", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string propertyPath = componentPath + ".props." + propertyName;
                CollectJsonFields(
                    groups,
                    group,
                    propertyPath,
                    Humanize(propertyName),
                    value,
                    replacement => properties[propertyName] = replacement,
                    depth: 0);
            }
        }
    }

    private bool CollectObjectShaderParameters(
        Dictionary<string, List<FieldSpec>> groups,
        JsonObject overrides)
    {
        if (_json is null || _projectRoot is null) return false;
        string relative = _json["shader"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(relative)) return false;

        string shaderPath = ResourceNames.Resolve(_projectRoot, relative, ResourceType.Shader);
        if (!File.Exists(shaderPath)) return false;

        ShaderAssetDocument shader;
        try
        {
            shader = ShaderAssetDocument.Load(shaderPath);
            ShaderParameterReflection.Synchronize(shader);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException
            or Newtonsoft.Json.JsonException or InvalidDataException)
        {
            return false;
        }

        foreach (ShaderParameterValue parameter in shader.Parameters)
        {
            ShaderParameterDescriptor descriptor = ShaderParameterMetadata.Describe(
                parameter.Name,
                parameter.Type);
            JsonArray components = overrides[parameter.Name] as JsonArray ?? [];
            if (components.Count == 0
                && overrides[parameter.Name] is JsonValue scalar
                && TryScalar(scalar, out object? scalarValue)
                && scalarValue is not null)
            {
                components.Add(Convert.ToSingle(scalarValue, CultureInfo.InvariantCulture));
            }
            while (components.Count < parameter.Value.Length)
                components.Add(parameter.Value[components.Count]);
            while (components.Count > parameter.Value.Length)
                components.RemoveAt(components.Count - 1);
            overrides[parameter.Name] = components;

            for (int index = 0; index < components.Count; index++)
            {
                int captured = index;
                float raw = components[index]?.GetValue<float>() ?? 0f;
                object initial = descriptor.Kind switch
                {
                    ShaderParameterScalarKind.Boolean => raw != 0f,
                    ShaderParameterScalarKind.SignedInteger or ShaderParameterScalarKind.UnsignedInteger =>
                        (int)MathF.Round(raw),
                    _ => raw,
                };
                string suffix = components.Count == 1 ? string.Empty : " " + Components[index];
                string path = $"shaderParameters.{parameter.Name}[{index}]";
                AddSpec(groups, "Shader parameters", new FieldSpec(
                    Humanize(parameter.Name) + suffix,
                    path,
                    initial,
                    newValue =>
                    {
                        float converted = newValue is bool enabled
                            ? enabled ? 1f : 0f
                            : Convert.ToSingle(newValue, CultureInfo.InvariantCulture);
                        components[captured] = converted;
                        Commit(path, newValue);
                    },
                    null,
                    Description: $"{parameter.Type} override reflected from {ResourceDisplayName.Format(shaderPath)}.",
                    Range: new NumericRange(
                        (decimal)descriptor.Minimum,
                        (decimal)descriptor.Maximum,
                        (decimal)descriptor.Increment,
                        descriptor.DecimalPlaces)));
            }
        }

        return shader.Parameters.Count > 0;
    }

    private void CollectJsonFields(
        Dictionary<string, List<FieldSpec>> groups,
        string group,
        string path,
        string label,
        JsonNode? node,
        Action<JsonNode?> assign,
        int depth)
    {
        if (_fieldCount >= MaxFields || depth > 5) return;
        if (_livePropertyPaths.Contains(path)) return;
        if (node is JsonObject obj)
        {
            foreach ((string name, JsonNode? child) in obj)
            {
                if (IsMetadata(name)) continue;
                string childPath = path + "." + name;
                string childLabel = depth == 0 ? Humanize(name) : label + " · " + Humanize(name);
                CollectJsonFields(groups, group, childPath, childLabel, child,
                    value => obj[name] = value, depth + 1);
            }
            return;
        }
        if (node is JsonArray array)
        {
            if (TryCollectVector(groups, group, path, label, array, assign)) return;
            for (int index = 0; index < array.Count && _fieldCount < MaxFields; index++)
            {
                int captured = index;
                JsonNode? child = array[index];
                string childPath = $"{path}[{index}]";
                string childLabel = child is JsonObject
                    ? $"{label} {index + 1}"
                    : $"{label} {Components.ElementAtOrDefault(index) ?? (index + 1).ToString(CultureInfo.InvariantCulture)}";
                CollectJsonFields(groups, group, childPath, childLabel, child,
                    value => array[captured] = value, depth + 1);
            }
            return;
        }

        if (node is JsonValue valueNode && TryScalar(valueNode, out object? initial))
        {
            if (initial is null) return;
            if (initial is string longText && longText.Length > 512) return;
            if (initial is string vectorText && TryParseVector(vectorText, out object? vector))
            {
                AddSpec(groups, group, new FieldSpec(label, path, vector!, newValue =>
                {
                    string converted = newValue switch
                    {
                        Vector2 value => FormattableString.Invariant($"{value.X:0.###},{value.Y:0.###}"),
                        Vector3 value => FormattableString.Invariant($"{value.X:0.###},{value.Y:0.###},{value.Z:0.###}"),
                        _ => vectorText,
                    };
                    assign(JsonValue.Create(converted));
                    Commit(path, converted);
                }, null));
                return;
            }
            AddSpec(groups, group, new FieldSpec(label, path, initial, newValue =>
            {
                object? converted = ConvertLike(initial, newValue);
                assign(JsonValue.Create(converted));
                Commit(path, converted);
            }, ChoicesFor(path)));
        }
    }

    private static bool TryParseVector(string text, out object? vector)
    {
        vector = null;
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (2 or 3)) return false;
        float[] values = new float[parts.Length];
        for (int index = 0; index < parts.Length; index++)
            if (!float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
                return false;
        vector = values.Length == 2
            ? new Vector2(values[0], values[1])
            : new Vector3(values[0], values[1], values[2]);
        return true;
    }

    private bool TryCollectVector(
        Dictionary<string, List<FieldSpec>> groups,
        string group,
        string path,
        string label,
        JsonArray array,
        Action<JsonNode?> assign)
    {
        if (array.Count is not (2 or 3)) return false;
        float[] values = new float[array.Count];
        for (int index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonValue value)
                return false;
            if (value.TryGetValue<float>(out float single)) values[index] = single;
            else if (value.TryGetValue<double>(out double number)) values[index] = (float)number;
            else if (value.TryGetValue<long>(out long integer)) values[index] = integer;
            else return false;
        }

        object vector = values.Length == 2
            ? new Vector2(values[0], values[1])
            : new Vector3(values[0], values[1], values[2]);
        AddSpec(groups, group, new FieldSpec(label, path, vector, newValue =>
        {
            JsonArray replacement = newValue switch
            {
                Vector2 value => [value.X, value.Y],
                Vector3 value => [value.X, value.Y, value.Z],
                _ => new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            };
            assign(replacement);
            Commit(path, newValue);
        }, null));
        return true;
    }

    private void CollectShaderParameters(Dictionary<string, List<FieldSpec>> groups)
    {
        if (_json is null) return;
        string source = _json["source"]?.GetValue<string>() ?? string.Empty;
        IReadOnlyList<ReflectedShaderParameter> reflected = ShaderParameterReflection.Reflect(source);
        JsonArray values = _json["parameters"] as JsonArray ?? new JsonArray();
        _json["parameters"] = values;
        Dictionary<string, JsonObject> existing = values.OfType<JsonObject>()
            .Where(item => item["name"] is JsonValue)
            .ToDictionary(item => item["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);

        JsonArray synchronized = [];
        foreach (ReflectedShaderParameter field in reflected)
        {
            JsonObject parameter = existing.GetValueOrDefault(field.Name)?.DeepClone().AsObject()
                                   ?? new JsonObject
            {
                ["name"] = field.Name,
                ["type"] = field.Type,
                ["value"] = new JsonArray(),
            };
            JsonArray components = parameter["value"] as JsonArray ?? new JsonArray();
            parameter["value"] = components;
            while (components.Count < field.ComponentCount) components.Add(0f);
            while (components.Count > field.ComponentCount) components.RemoveAt(components.Count - 1);
            synchronized.Add(parameter);

            for (int index = 0; index < field.ComponentCount; index++)
            {
                int captured = index;
                float initial = components[index]?.GetValue<float>() ?? 0f;
                string suffix = field.ComponentCount == 1 ? string.Empty : "." + Components[index];
                string path = "Parameters." + field.Name + suffix;
                if (_livePropertyPaths.Contains(path)) continue;
                string label = field.ComponentCount == 1
                    ? Humanize(field.Name)
                    : Humanize(field.Name) + " " + Components[index];
                AddSpec(groups, "Parameters", new FieldSpec(label, path, initial, newValue =>
                {
                    float converted = Convert.ToSingle(newValue, CultureInfo.InvariantCulture);
                    components[captured] = converted;
                    Commit(path, converted);
                }, null));
            }
        }
        _json["parameters"] = synchronized;
    }

    private void CollectShaderResources(Dictionary<string, List<FieldSpec>> groups)
    {
        if (_json is null) return;
        string source = _json["source"]?.GetValue<string>() ?? string.Empty;
        IReadOnlyList<ReflectedShaderResource> reflected = ShaderResourceReflection.Reflect(source);
        JsonArray values = _json["resources"] as JsonArray ?? new JsonArray();
        _json["resources"] = values;
        Dictionary<string, JsonObject> existing = values.OfType<JsonObject>()
            .Where(item => item["name"] is JsonValue)
            .ToDictionary(item => item["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);

        ShaderAssetPipeline pipeline = ReadShaderPipeline(_json);
        JsonArray synchronized = [];
        foreach (ReflectedShaderResource field in reflected)
        {
            JsonObject resource = existing.GetValueOrDefault(field.Name)?.DeepClone().AsObject()
                                   ?? new JsonObject
            {
                ["name"] = field.Name,
                ["kind"] = field.Kind.ToString(),
                ["slot"] = field.Slot,
                ["binding"] = string.Empty,
            };
            resource["kind"] = field.Kind.ToString();
            resource["slot"] = field.Slot;
            synchronized.Add(resource);
            if (ShaderResourceReflection.IsPipelineOwned(pipeline, field.Kind, field.Slot)) continue;
            string path = "Resources." + field.Name;
            if (_livePropertyPaths.Contains(path)) continue;
            string initial = resource["binding"]?.GetValue<string>() ?? string.Empty;
            AddSpec(groups, "Resources", new FieldSpec(Humanize(field.Name), path, initial, newValue =>
            {
                string converted = Convert.ToString(newValue, CultureInfo.InvariantCulture) ?? string.Empty;
                resource["binding"] = converted;
                Commit(path, converted);
            }, field.Kind == ShaderResourceKind.SamplerState ? ["Linear", "Point"] : null));
        }

        _json["resources"] = synchronized;
    }

    private static ShaderAssetPipeline ReadShaderPipeline(JsonObject json)
    {
        if (json["pipeline"] is JsonValue value
            && value.TryGetValue<string>(out string? text)
            && Enum.TryParse(text, ignoreCase: true, out ShaderAssetPipeline pipeline))
        {
            return pipeline;
        }

        return PipelineForShaderTarget(ReadShaderTargetType(json));
    }

    private void BuildPgsl(string source)
    {
        List<FieldSpec> fields = [];
        foreach (PgslInspectableVariables.Variable variable in PgslInspectableVariables.Reflect(source))
        {
            string path = "Variables." + variable.Name;
            fields.Add(new FieldSpec(Humanize(variable.Name), path, variable.Value, newValue =>
            {
                if (_resource is null) return;
                string current = File.Exists(_resource.FullPath)
                    ? File.ReadAllText(_resource.FullPath)
                    : source;
                if (!PgslInspectableVariables.TrySetValue(current, variable.Name, newValue,
                        out string updated))
                {
                    return;
                }
                CommitText(path, newValue, updated);
            }, null));
            _setters[path] = fields[^1].Set;
            _fieldCount++;
        }

        if (fields.Count == 0)
        {
            AddMessage(ScriptGroupName(),
                "Add a file-scope ‘var name = value;’ declaration to expose it here. Variables inside events stay private.");
        }
        else
        {
            AddGroup(ScriptGroupName(), fields, expanded: true, register: false);
        }
    }

    private void BuildLiveValues(IReadOnlyList<ResourceInspectorLiveValue> liveValues)
    {
        int groupIndex = 0;
        foreach (IGrouping<string, ResourceInspectorLiveValue> group in liveValues
                     .Where(value => value.Value is not null && IsInspectableLiveValue(value.Value))
                     .GroupBy(value => value.Group, StringComparer.OrdinalIgnoreCase))
        {
            List<FieldSpec> fields = [];
            List<ResourceInspectorLiveValue> groupValues = group.ToList();
            HashSet<string> consumed = new(StringComparer.OrdinalIgnoreCase);
            foreach (ResourceInspectorLiveValue value in groupValues)
            {
                if (consumed.Contains(value.PropertyPath)) continue;
                if (TryCreateLiveVector(groupValues, value, consumed, out FieldSpec? vectorField))
                {
                    fields.Add(vectorField!);
                    continue;
                }
                object initial = value.Value;
                fields.Add(new FieldSpec(
                    value.Label,
                    value.PropertyPath,
                    initial,
                    newValue => CommitLive(value.PropertyPath, ConvertLike(initial, newValue)),
                    value.Choices?.ToArray() ?? ChoicesFor(value.PropertyPath),
                    value.ReadOnly,
                    value.Description,
                    value.AssetKind,
                    LiveRange(value)));
            }

            if (fields.Count > 0)
            {
                bool importantRuntimeGroup = group.Key.Equals("INSTANCE FIELDS", StringComparison.OrdinalIgnoreCase)
                                             || group.Key.EndsWith(" EVENT", StringComparison.OrdinalIgnoreCase);
                AddGroup(group.Key, fields, expanded: groupIndex == 0 || importantRuntimeGroup);
                groupIndex++;
            }
        }
    }

    private bool TryCreateLiveVector(
        IReadOnlyList<ResourceInspectorLiveValue> values,
        ResourceInspectorLiveValue first,
        HashSet<string> consumed,
        out FieldSpec? field)
    {
        field = null;
        if (!first.PropertyPath.EndsWith(".X", StringComparison.OrdinalIgnoreCase)
            || !TryNumber(first.Value, out float x)) return false;
        string prefix = first.PropertyPath[..^2];
        ResourceInspectorLiveValue? yValue = values.FirstOrDefault(candidate =>
            candidate.PropertyPath.Equals(prefix + ".Y", StringComparison.OrdinalIgnoreCase));
        ResourceInspectorLiveValue? zValue = values.FirstOrDefault(candidate =>
            candidate.PropertyPath.Equals(prefix + ".Z", StringComparison.OrdinalIgnoreCase));
        if (yValue is null || !TryNumber(yValue.Value, out float y)) return false;
        string label = Regex.Replace(first.Label, @"\s+X$", string.Empty, RegexOptions.IgnoreCase);
        if (zValue is not null && TryNumber(zValue.Value, out float z))
        {
            consumed.UnionWith([first.PropertyPath, yValue.PropertyPath, zValue.PropertyPath]);
            field = new FieldSpec(label, prefix, new Vector3(x, y, z), newValue =>
            {
                if (newValue is not Vector3 vector) return;
                CommitLive(first.PropertyPath, ConvertLike(first.Value, vector.X));
                CommitLive(yValue.PropertyPath, ConvertLike(yValue.Value, vector.Y));
                CommitLive(zValue.PropertyPath, ConvertLike(zValue.Value, vector.Z));
            }, null, first.ReadOnly && yValue.ReadOnly && zValue.ReadOnly, first.Description);
            return true;
        }
        consumed.UnionWith([first.PropertyPath, yValue.PropertyPath]);
        field = new FieldSpec(label, prefix, new Vector2(x, y), newValue =>
        {
            if (newValue is not Vector2 vector) return;
            CommitLive(first.PropertyPath, ConvertLike(first.Value, vector.X));
            CommitLive(yValue.PropertyPath, ConvertLike(yValue.Value, vector.Y));
        }, null, first.ReadOnly && yValue.ReadOnly, first.Description);
        return true;
    }

    private static bool TryNumber(object value, out float number)
    {
        try
        {
            number = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            return float.IsFinite(number);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            number = 0f;
            return false;
        }
    }

    private void AddSpec(
        Dictionary<string, List<FieldSpec>> groups,
        string group,
        FieldSpec field)
    {
        if (!groups.TryGetValue(group, out List<FieldSpec>? fields))
        {
            fields = [];
            groups[group] = fields;
        }
        fields.Add(field);
        _setters[field.Path] = field.Set;
        _fieldCount++;
    }

    private void AddGroup(string title, List<FieldSpec> fields, bool expanded, bool register = true)
    {
        if (register)
        {
            foreach (FieldSpec field in fields) _setters[field.Path] = field.Set;
        }
        _groupNames.Add(title);
        string caption = FormatGroupCaption(title);
        FieldSpec? enabledField = fields.FirstOrDefault(field =>
            field.Label.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
            && field.Initial is bool);
        List<FieldSpec> bodyFields = enabledField is null
            ? fields
            : fields.Where(field => !ReferenceEquals(field, enabledField)).ToList();
        TableLayoutPanel group = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 4, 0, 12),
            Name = "InspectorGroup" + Regex.Replace(title, @"\W", string.Empty),
            RowCount = 2,
            Tag = "surface",
        };
        group.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        group.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Panel headerHost = new()
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackColor = EditorChrome.Raised,
        };
        Button header = new()
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
            Margin = Padding.Empty,
            Name = group.Name + "Header",
            Padding = new Padding(12, 0, 8, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };
        header.FlatAppearance.BorderSize = 1;
        Button menu = new()
        {
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            Text = "⋮",
            Width = 32,
        };
        menu.FlatAppearance.BorderSize = 0;
        menu.ForeColor = EditorChrome.Muted;
        ToolTip toolTip = new();
        toolTip.SetToolTip(menu, "Component options");
        ContextMenuStrip options = BuildGroupMenu(fields);
        menu.Click += (_, _) => options.Show(menu, new Point(0, menu.Height));
        menu.Disposed += (_, _) =>
        {
            options.Dispose();
            toolTip.Dispose();
        };
        if (enabledField is not null)
        {
            CheckBox active = new()
            {
                AccessibleName = $"{title} active",
                Checked = (bool)enabledField.Initial,
                Dock = DockStyle.Right,
                Text = string.Empty,
                Width = 28,
            };
            active.CheckedChanged += (_, _) => { if (!_refreshingValues) enabledField.Set(active.Checked); };
            _drawers[enabledField.Path] = (active, enabledField.Initial);
            headerHost.Controls.Add(active);
        }
        headerHost.Controls.Add(menu);
        headerHost.Controls.Add(header);
        header.BringToFront();
        TableLayoutPanel body = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = new Padding(14, 10, 14, 14),
            RowCount = bodyFields.Count,
            Tag = "surface",
            Visible = expanded,
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        for (int row = 0; row < bodyFields.Count; row++)
        {
            FieldSpec field = bodyFields[row];
            bool compactVector = field.Initial is Vector2 or Vector3;
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, compactVector ? 36 : 42));
            Label label = new()
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 10, 4),
                Text = field.Label,
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
            };
            Control editor = MakeEditor(field);
            editor.Enabled = !field.ReadOnly;
            editor.Dock = DockStyle.Fill;
            editor.Margin = new Padding(0, 3, 0, 3);
            body.Controls.Add(label, 0, row);
            body.Controls.Add(editor, 1, row);
        }
        string expansionKey = (_resource?.FullPath ?? string.Empty) + "|" + title;
        if (_groupExpansion.TryGetValue(expansionKey, out bool remembered)) expanded = remembered;
        GroupView? view = null;
        void SetExpanded(bool visible)
        {
            _groupExpansion[expansionKey] = visible;
            if (view is not null) view.Expanded = visible;
            body.Visible = visible || _filterText.Length > 0;
            header.Text = (visible ? "▾  " : "▸  ") + caption;
            group.PerformLayout();
            _groups.PerformLayout();
        }
        group.Controls.Add(headerHost, 0, 0);
        group.Controls.Add(body, 0, 1);
        view = new GroupView(group, header, body, title, fields, expanded);
        _groupViews.Add(view);
        header.Click += (_, _) => SetExpanded(!view.Expanded);
        SetExpanded(expanded);
        AddGroupControl(group);
    }

    private ContextMenuStrip BuildGroupMenu(IReadOnlyList<FieldSpec> fields)
    {
        ContextMenuStrip menu = new();
        ToolStripMenuItem reset = new("Reset visible values");
        reset.Click += (_, _) =>
        {
            foreach (FieldSpec field in fields) field.Set(field.Initial);
        };
        menu.Items.Add(reset);

        Match component = fields.Select(field => Regex.Match(field.Path, @"^components\[(?<index>\d+)\]",
                RegexOptions.IgnoreCase))
            .FirstOrDefault(match => match.Success) ?? Match.Empty;
        if (component.Success && int.TryParse(component.Groups["index"].Value, out int index))
        {
            ToolStripMenuItem remove = new("Remove component");
            remove.Click += (_, _) =>
            {
                if (_json?["components"] is not JsonArray components || index < 0 || index >= components.Count)
                    return;
                string type = components[index]?["type"]?.GetValue<string>() ?? string.Empty;
                ObjectComponentDefinition? definition = ObjectCompositionModel.Definitions.FirstOrDefault(candidate =>
                    candidate.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
                if (definition is { Removable: false }) return;
                components.RemoveAt(index);
                _documentSchemaDirty = true;
                Commit("components", components);
                if (_resource is not null) Inspect(_resource, []);
            };
            menu.Items.Add(remove);
        }
        return menu;
    }

    private Control MakeEditor(FieldSpec field)
    {
        NumericRange? range = field.Range ?? RangeFor(field.Path);
        Control control = PropertyDrawerRegistry.CreateControl(new PropertyDrawerContext(
            field.Label,
            field.Initial.GetType(),
            field.Initial,
            field.Set,
            field.Choices,
            range?.Minimum,
            range?.Maximum,
            range?.Increment,
            range?.DecimalPlaces,
            field.ReadOnly,
            field.AssetKind ?? AssetKindFor(field.Path),
            _projectRoot,
            FindForm(),
            "InspectorProperty_" + SafeName(field.Path),
            field.Description));
        _drawers[field.Path] = (control, field.Initial);
        return control;
    }

    private static string LiveSchema(IReadOnlyList<ResourceInspectorLiveValue> values)
    {
        StringBuilder schema = new();
        foreach (ResourceInspectorLiveValue value in values)
        {
            schema.Append(value.Group).Append('|').Append(value.PropertyPath).Append('|')
                .Append(value.Label).Append('|').Append(value.Value?.GetType().FullName).Append('|')
                .Append(value.ReadOnly).Append('|').Append(value.AssetKind).Append('|')
                .Append(value.Minimum).Append('|').Append(value.Maximum).Append('|')
                .Append(value.Increment).Append('|').Append(value.DecimalPlaces).Append('|')
                .Append(value.Description).Append('|').AppendJoin('\t', value.Choices ?? []).AppendLine();
        }
        return schema.ToString();
    }

    private void RefreshLiveValues(IReadOnlyList<ResourceInspectorLiveValue> values)
    {
        ValueRefreshCount++;
        Dictionary<string, object?> current = new(StringComparer.OrdinalIgnoreCase);
        foreach (ResourceInspectorLiveValue value in values)
        { current[value.PropertyPath] = value.Value; _lastLiveValues[value.PropertyPath] = value.Value; }
        _refreshingValues = true;
        try
        {
            foreach (var entry in _drawers)
            {
                object? value;
                if (entry.Value.Initial is Vector2 or Vector3
                    && !current.TryGetValue(entry.Key, out value))
                {
                    if (!current.TryGetValue(entry.Key + ".X", out object? x)
                        || !current.TryGetValue(entry.Key + ".Y", out object? y)) continue;
                    float vx = Convert.ToSingle(x, CultureInfo.InvariantCulture), vy = Convert.ToSingle(y, CultureInfo.InvariantCulture);
                    value = entry.Value.Initial is Vector3 && current.TryGetValue(entry.Key + ".Z", out object? z)
                        ? new Vector3(vx, vy, Convert.ToSingle(z, CultureInfo.InvariantCulture))
                        : new Vector2(vx, vy);
                }
                else if (!current.TryGetValue(entry.Key, out value)) continue;
                PropertyDrawerRegistry.RefreshValue(entry.Value.Control, value);
            }
        }
        finally { _refreshingValues = false; }
    }

    private static (long Ticks, long Length) DocumentStamp(string path)
    {
        try { FileInfo file = new(path); return file.Exists ? (file.LastWriteTimeUtc.Ticks, file.Length) : default; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return default; }
    }

    private void AddMessage(string title, string text)
    {
        _groupNames.Add(title);
        Label message = new()
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            MaximumSize = new Size(420, 0),
            Name = "InspectorPropertiesHint",
            Padding = new Padding(10),
            Tag = "surface",
            Text = text,
            UseMnemonic = false,
        };
        AddGroupControl(message);
    }

    private void ClearGroups()
    {
        foreach (Control control in _groups.Controls.Cast<Control>().ToArray())
            control.Dispose();
        _groups.Controls.Clear();
        _groups.RowStyles.Clear();
        _groups.RowCount = 0;
        _groupViews.Clear();
    }

    private void ApplyFilter()
    {
        string query = _filterText;
        foreach (GroupView view in _groupViews)
        {
            bool matches = query.Length == 0
                           || view.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                           || view.Fields.Any(field =>
                               field.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || field.Path.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || (field.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
            view.Container.Visible = matches;
            view.Body.Visible = matches && (query.Length > 0 || view.Expanded);
            view.Header.Text = ((query.Length > 0 || view.Expanded) ? "▾  " : "▸  ")
                               + FormatGroupCaption(view.Title);
        }
        _groups.PerformLayout();
    }

    private void AddGroupControl(Control control)
    {
        int row = _groups.RowCount++;
        _groups.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _groups.Controls.Add(control, 0, row);
    }

    private void Commit(string propertyPath, object? value)
    {
        if (_refreshingValues || _resource is null || _json is null) return;
        bool routed = EditRouter?.Invoke(new ResourceInspectorEditRequest(_resource, propertyPath, value)) == true;
        if (!routed)
        {
            WriteText(_resource.FullPath, _json.ToJsonString(WriteOptions));
        }
        _documentStamp = DocumentStamp(_resource.FullPath);
        // These fields change which groups/asset-specific reflected parameters are available.
        if (propertyPath is "components" or "shader" or "targetType" or "pipeline" or "type") _documentSchemaDirty = true;
        ResourceEdited?.Invoke(this,
            new ResourceInspectorEditedEventArgs(_resource, propertyPath, value, routed));
    }

    private void CommitText(string propertyPath, object? value, string updated)
    {
        if (_resource is null) return;
        bool routed = EditRouter?.Invoke(new ResourceInspectorEditRequest(_resource, propertyPath, value)) == true;
        if (!routed) WriteText(_resource.FullPath, updated);
        ResourceEdited?.Invoke(this,
            new ResourceInspectorEditedEventArgs(_resource, propertyPath, value, routed));
    }

    private void CommitLive(string propertyPath, object? value)
    {
        if (_refreshingValues || _resource is null) return;
        if (_lastLiveValues.TryGetValue(propertyPath, out object? previous) && Equals(previous, value)) return;
        bool routed = EditRouter?.Invoke(new ResourceInspectorEditRequest(_resource, propertyPath, value)) == true;
        if (!routed) return;
        _lastLiveValues[propertyPath] = value;
        ResourceEdited?.Invoke(this,
            new ResourceInspectorEditedEventArgs(_resource, propertyPath, value, routedToOpenEditor: true));
    }

    private static void WriteText(string path, string content)
    {
        string root = ResourceNames.FindProjectRoot(path);
        content = Genesis.Shared.Assets.ResourceReferenceRewriter.Normalize(root, path, content);
        ResourceBackupService.BackupBeforeOverwrite(path);
        // Inspector writes intentionally are not tagged as the current document's local save: an
        // independently open editor must observe this edit and refresh its document/preview.
        File.WriteAllText(path, content, new UTF8Encoding(false));
        ResourceNames.Invalidate(root);
    }

    private static bool ShouldSkipShaderJsonField(string name, JsonObject json)
    {
        if (name is "source" or "parameters" or "pipeline" or "variants" or "resources" or "activeVariant") return true;
        ShaderTargetType target = ReadShaderTargetType(json);
        if (name.Equals("previewAsset", StringComparison.OrdinalIgnoreCase)
            && target == ShaderTargetType.Fullscreen)
        {
            return true;
        }

        if (name.Equals("targetComponent", StringComparison.OrdinalIgnoreCase)
            && target != ShaderTargetType.Terrain)
        {
            return true;
        }

        return false;
    }

    private static ShaderTargetType ReadShaderTargetType(JsonObject json)
    {
        if (json["targetType"] is JsonValue value
            && value.TryGetValue<string>(out string? text)
            && Enum.TryParse(text, ignoreCase: true, out ShaderTargetType target))
        {
            return target;
        }

        if (json["pipeline"] is JsonValue pipeline
            && pipeline.TryGetValue<string>(out string? pipelineText)
            && Enum.TryParse(pipelineText, ignoreCase: true, out ShaderAssetPipeline assetPipeline))
        {
            return assetPipeline switch
            {
                ShaderAssetPipeline.Mesh => ShaderTargetType.Model,
                ShaderAssetPipeline.Fullscreen => ShaderTargetType.Fullscreen,
                _ => ShaderTargetType.Image,
            };
        }

        return ShaderTargetType.Image;
    }

    private static ShaderAssetPipeline PipelineForShaderTarget(ShaderTargetType target) => target switch
    {
        ShaderTargetType.Model or ShaderTargetType.Terrain => ShaderAssetPipeline.Mesh,
        ShaderTargetType.Fullscreen => ShaderAssetPipeline.Fullscreen,
        _ => ShaderAssetPipeline.Sprite,
    };

    private static bool TryScalar(JsonValue node, out object? value)
    {
        if (node.TryGetValue<bool>(out bool flag)) { value = flag; return true; }
        if (node.TryGetValue<long>(out long integer)) { value = integer; return true; }
        if (node.TryGetValue<double>(out double number)) { value = number; return true; }
        if (node.TryGetValue<string>(out string? text)) { value = text ?? string.Empty; return true; }
        value = null;
        return false;
    }

    private static object? ConvertLike(object? initial, object? value)
    {
        if (initial is Color && value is Color color) return color;
        if (initial is Vector2 && value is Vector2 vector2) return vector2;
        if (initial is Vector3 && value is Vector3 vector3) return vector3;
        if (initial is bool) return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        if (initial is byte) return Convert.ToByte(value, CultureInfo.InvariantCulture);
        if (initial is sbyte) return Convert.ToSByte(value, CultureInfo.InvariantCulture);
        if (initial is short) return Convert.ToInt16(value, CultureInfo.InvariantCulture);
        if (initial is ushort) return Convert.ToUInt16(value, CultureInfo.InvariantCulture);
        if (initial is int) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        if (initial is uint) return Convert.ToUInt32(value, CultureInfo.InvariantCulture);
        if (initial is long) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (initial is ulong) return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        if (initial is float) return Convert.ToSingle(value, CultureInfo.InvariantCulture);
        if (initial is double) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (initial is decimal) return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool IsInspectableLiveValue(object value) =>
        value is bool or string or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or Vector2 or Vector3 or Color;

    private static bool IsMetadata(string name) => name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)
                                                    || name.Equals("version", StringComparison.OrdinalIgnoreCase)
                                                    || name.Equals("$schema", StringComparison.OrdinalIgnoreCase);

    private static bool IsObjectCompatibilityField(string name) => name.ToLowerInvariant() is
        "sprite" or "model" or "material" or "shader" or "physics" or "events";

    private static bool ShouldSkipStructuredField(ResourceKind kind, string name, bool hasLiveContext)
    {
        string key = name.ToLowerInvariant();
        return kind switch
        {
            // Dedicated editors own these ordered structures. Exposing raw array indices and ids
            // here is noisy and becomes unsafe as soon as an item is inserted or reordered.
            ResourceKind.Image => key is "attachments" or "frames" or "tags" or "events"
                or "collisionshapes" or "layers" or "materialchannels" or "armature"
                or "deformmeshes" or "constraints" or "tracks",
            ResourceKind.Model => key is "materials" or "lods" or "rig" or "animations",
            ResourceKind.Terrain when hasLiveContext => key is "layers" or "entities",
            ResourceKind.Room when hasLiveContext => key is "layers" or "nodes" or "viewports",
            _ => false,
        };
    }

    private static string TopLevelGroupName(ResourceKind kind, string name, JsonNode? node)
    {
        if (name.Equals("name", StringComparison.OrdinalIgnoreCase)
            || name.Equals("active", StringComparison.OrdinalIgnoreCase)
            || name.Equals("static", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tag", StringComparison.OrdinalIgnoreCase)
            || name.Equals("layer", StringComparison.OrdinalIgnoreCase))
        {
            return "Identity";
        }
        if (name.Equals("culling", StringComparison.OrdinalIgnoreCase)
            || name.Equals("windingOrder", StringComparison.OrdinalIgnoreCase))
        {
            return "Rendering";
        }

        if (kind == ResourceKind.Image)
        {
            return name.ToLowerInvariant() switch
            {
                "canvas" => "Canvas",
                "import" => "Source & import",
                "texturegroup" => "Texture group",
                "origin" => "Origin & pivot",
                _ => PrimaryGroupName(kind, name),
            };
        }

        if (kind == ResourceKind.Room)
        {
            return name.ToLowerInvariant() switch
            {
                "settings" => "Room settings",
                "environment" => "Environment",
                "layers" => "Layers",
                "nodes" => "Scene objects",
                "viewports" => "Viewports",
                _ => PrimaryGroupName(kind, name),
            };
        }

        return node is JsonObject or JsonArray
            ? Humanize(name)
            : PrimaryGroupName(kind, name);
    }

    private static string PathLeaf(string path)
    {
        int dot = path.LastIndexOf('.');
        string leaf = dot >= 0 ? path[(dot + 1)..] : path;
        int bracket = leaf.IndexOf('[');
        return bracket >= 0 ? leaf[..bracket] : leaf;
    }

    private string ScriptGroupName()
    {
        string? name = _resource?.Name;
        if (string.IsNullOrWhiteSpace(name)) name = "SCRIPT";
        name = ResourceDisplayName.Format(name);
        return $"{name.ToUpperInvariant()} · SCRIPT VARIABLES";
    }

    private static string PrimaryGroupName(ResourceKind kind, string propertyName)
    {
        if (kind == ResourceKind.Particle)
        {
            return propertyName.ToLowerInvariant() switch
            {
                "maxparticles" or "emitrate" or "burstcount" or "loop" => "Emission",
                "shape" or "spreaddegrees" or "emitradius" => "Emitter shape",
                "speed" or "speedvariance" or "gravity" or "drag" => "Motion",
                "lifetime" or "lifetimevariance" => "Lifetime",
                "startsize" or "endsize" or "emissive" or "blendmode" => "Appearance",
                _ => "Particle settings",
            };
        }

        return kind switch
        {
            ResourceKind.Image => "Image settings",
            ResourceKind.Audio => propertyName.Equals("spatial", StringComparison.OrdinalIgnoreCase)
                ? "Spatial audio"
                : "Playback",
            ResourceKind.Shader => propertyName.StartsWith("preview", StringComparison.OrdinalIgnoreCase)
                ? "Preview"
                : "Shader target",
            ResourceKind.GameObject => "Object settings",
            ResourceKind.Room => "Room settings",
            ResourceKind.Model => "Model import",
            ResourceKind.Physics => "Physics material",
            ResourceKind.Terrain => "Terrain shape",
            ResourceKind.TerrainEntity => "Terrain object",
            _ => "Resource settings",
        };
    }

    private static void EnsureRasterOverrides(ResourceKind kind, JsonObject json)
    {
        if (kind is not (ResourceKind.GameObject or ResourceKind.Model
            or ResourceKind.Terrain or ResourceKind.TerrainEntity))
        {
            return;
        }

        json["culling"] ??= "Default";
        json["windingOrder"] ??= "Default";
    }

    private void EnsureIdentityFields(ResourceKind kind, JsonObject json)
    {
        if (kind is not (ResourceKind.GameObject or ResourceKind.Room or ResourceKind.Terrain
            or ResourceKind.Model or ResourceKind.TerrainEntity)) return;
        json["name"] ??= ResourceDisplayName.Format(_resource?.Name ?? "Resource");
        json["active"] ??= true;
        json["static"] ??= false;
        json["tag"] ??= "Untagged";
        json["layer"] ??= "Default";
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Value";
        string spaced = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2")
            .Replace('_', ' ');
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private static string SafeName(string value) => Regex.Replace(value, @"\W", "_");

    private static string FormatGroupCaption(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "◆  [Properties]";
        }

        string trimmed = title.Trim();
        if (trimmed.StartsWith("--", StringComparison.Ordinal)
            && trimmed.EndsWith("--", StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (trimmed.EndsWith(" EVENT", StringComparison.OrdinalIgnoreCase))
        {
            string eventName = trimmed[..^" EVENT".Length].Trim();
            return $"⚡  [{Humanize(eventName)} Event]";
        }

        if (trimmed.Contains("SCRIPT VARIABLES", StringComparison.OrdinalIgnoreCase))
        {
            string scriptName = trimmed.Split('·')[0].Trim();
            return $"#  [Script: {Humanize(scriptName)}]";
        }

        if (trimmed.Equals("INSTANCE FIELDS", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("OBJECT VARIABLES", StringComparison.OrdinalIgnoreCase))
        {
            return $"◆  [{Humanize(trimmed)}]";
        }

        return $"{GroupIcon(title)}  [{title}]";
    }

    private static string GroupIcon(string title)
    {
        string value = title.ToLowerInvariant();
        if (value.Contains("transform") || value.Contains("position")) return "⌖";
        if (value.Contains("mesh") || value.Contains("model")) return "◇";
        if (value.Contains("shader") || value.Contains("material")) return "◉";
        if (value.Contains("light")) return "☀";
        if (value.Contains("audio") || value.Contains("sound")) return "♪";
        if (value.Contains("script") || value.Contains("variable")) return "#";
        if (value.Contains("physics")) return "⬡";
        return "◆";
    }

    private ResourceKind? AssetKindFor(string path)
    {
        string leaf = PathLeaf(path).ToLowerInvariant();
        ResourceKind? direct = leaf switch
        {
            "sprite" or "image" or "imageasset" or "icon" or "normalmap" or "albedo" => ResourceKind.Image,
            "model" or "modelasset" => ResourceKind.Model,
            "shader" or "shaderasset" => ResourceKind.Shader,
            "audio" or "sound" or "audioasset" => ResourceKind.Audio,
            "particle" or "particleasset" => ResourceKind.Particle,
            "object" or "prefab" or "objectasset" or "parent" => ResourceKind.GameObject,
            "terrain" or "terrainasset" => ResourceKind.Terrain,
            "physicsmaterial" => ResourceKind.Physics,
            _ => null,
        };
        if (direct.HasValue) return direct;
        if (!leaf.Equals("asset", StringComparison.OrdinalIgnoreCase) || _json is null) return null;

        Match component = Regex.Match(path, @"^components\[(?<index>\d+)\]", RegexOptions.IgnoreCase);
        if (!component.Success
            || _json["components"] is not JsonArray components
            || !int.TryParse(component.Groups["index"].Value, out int index)
            || index < 0 || index >= components.Count
            || components[index] is not JsonObject item)
        {
            return null;
        }

        string type = item["type"]?.GetValue<string>() ?? string.Empty;
        ObjectComponentDefinition? definition = ObjectCompositionModel.Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));
        return definition is not null
               && definition.AssetKind.HasValue
               && leaf.Equals(definition.AssetProperty, StringComparison.OrdinalIgnoreCase)
            ? definition.AssetKind
            : null;
    }

    private string[]? ChoicesFor(string path)
    {
        string normalized = path.ToLowerInvariant();
        string[]? pathChoices = normalized switch
        {
            "canvas.pixelformat" => Enum.GetNames<Genesis.Application.Core.Images.ImagePixelFormat>(),
            "canvas.colorspace" => Enum.GetNames<Genesis.Application.Core.Images.ImageColorSpace>(),
            "import.mode" => Enum.GetNames<Genesis.Application.Core.Images.ImageImportMode>(),
            "usage.allowed" => Enum.GetNames<Genesis.Application.Core.Images.ImageUsage>(),
            "usage.sprite.filter" => Enum.GetNames<Genesis.Application.Core.Images.ImageFilterMode>(),
            "usage.sprite.wrap" => Enum.GetNames<Genesis.Application.Core.Images.ImageWrapMode>(),
            "usage.background.mode" => Enum.GetNames<Genesis.Application.Core.Images.ImageBackgroundMode>(),
            "origin.space" => Enum.GetNames<Genesis.Application.Core.Images.ImageCoordinateSpace>(),
            "environment.weather" => ["Clear", "Cloudy", "Rain", "Storm", "Snow", "Fog"],
            "environment.atmospherepreset" => ["Natural", "Cinematic", "Stylized", "Alien"],
            _ => null,
        };
        if (pathChoices is not null) return pathChoices;

        string leaf = PathLeaf(path);
        if (Choices.TryGetValue(leaf, out string[]? declared)) return declared;
        if (!leaf.Equals("Preset", StringComparison.OrdinalIgnoreCase) || _json is null) return null;

        Match component = Regex.Match(path, @"^components\[(?<index>\d+)\]", RegexOptions.IgnoreCase);
        if (!component.Success
            || _json["components"] is not JsonArray components
            || !int.TryParse(component.Groups["index"].Value, out int index)
            || index < 0 || index >= components.Count
            || components[index] is not JsonObject item)
        {
            return null;
        }

        string type = item["type"]?.GetValue<string>() ?? string.Empty;
        if (type.Equals("PhysicsComponent", StringComparison.OrdinalIgnoreCase))
        {
            return [.. ObjectPhysicsDialog.Presets.Select(preset => preset.Id)];
        }
        return null;
    }

    private static string RemoveComponentSuffix(string type) =>
        type.EndsWith("Component", StringComparison.OrdinalIgnoreCase)
            ? type[..^"Component".Length]
            : type;

    private static NumericRange? RangeFor(string path)
    {
        string leaf = PathLeaf(path).ToLowerInvariant();
        return leaf switch
        {
            "alpha" or "imagealpha" or "opacity" or "volume" or "roughness" or "metallic"
                or "restitution" or "damping" or "drag" or "probability" or "chance" =>
                new NumericRange(0m, 1m, 0.01m, 3),
            "friction" => new NumericRange(0m, 4m, 0.05m, 3),
            "angle" or "imageangle" or "direction" or "gravitydirection" or "spreaddegrees" =>
                new NumericRange(-360m, 360m, 1m, 1),
            "intensity" or "emissive" => new NumericRange(0m, 32m, 0.1m, 2),
            "radius" => new NumericRange(0.01m, 100000m, 0.1m, 2),
            "falloff" => new NumericRange(0.05m, 16m, 0.05m, 2),
            "speed" or "actionspeed" or "pulsespeed" or "windspeed" =>
                new NumericRange(0m, 60m, 0.05m, 3),
            "colorcount" => new NumericRange(1m, 3m, 1m, 0),
            "actionamount" => new NumericRange(0m, 1m, 0.01m, 3),
            "saturation" or "tintstrength" or "strength" or "windstrength" =>
                new NumericRange(0m, 1m, 0.01m, 3),
            _ => null,
        };
    }

    private static NumericRange? LiveRange(ResourceInspectorLiveValue value) =>
        value.Minimum.HasValue
        && value.Maximum.HasValue
        && value.Increment.HasValue
        && value.DecimalPlaces.HasValue
            ? new NumericRange(
                value.Minimum.Value,
                value.Maximum.Value,
                value.Increment.Value,
                value.DecimalPlaces.Value)
            : null;

    private static string? ResolveProjectRoot(string resourcePath)
    {
        DirectoryInfo? current = new FileInfo(resourcePath).Directory;
        while (current is not null)
        {
            if (current.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return current.Parent?.FullName;
            }
            current = current.Parent;
        }
        return null;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private sealed record FieldSpec(
        string Label,
        string Path,
        object Initial,
        Action<object?> Set,
        string[]? Choices,
        bool ReadOnly = false,
        string? Description = null,
        ResourceKind? AssetKind = null,
        NumericRange? Range = null);

    private sealed record NumericRange(
        decimal Minimum,
        decimal Maximum,
        decimal Increment,
        int DecimalPlaces);

    private sealed class GroupView(
        Control container,
        Button header,
        Control body,
        string title,
        IReadOnlyList<FieldSpec> fields,
        bool expanded)
    {
        public Control Container { get; } = container;
        public Button Header { get; } = header;
        public Control Body { get; } = body;
        public string Title { get; } = title;
        public IReadOnlyList<FieldSpec> Fields { get; } = fields;
        public bool Expanded { get; set; } = expanded;
    }
}
