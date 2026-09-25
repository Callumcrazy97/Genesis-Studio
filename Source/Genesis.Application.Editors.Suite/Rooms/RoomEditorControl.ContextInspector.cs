using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Supplies the Room Editor's own contextual Inspector. Values stay owned by the room document;
/// the panel is only a typed view over the existing undo, dirty and save boundaries.
/// </summary>
public sealed partial class RoomEditorControl
{
    private const string ContextRoomPrefix = "Context.Room.";
    private const string ContextSelectionPrefix = "Context.Selection.";
    private readonly HashSet<string> _uniformScaleInspectorNodes = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<ResourceInspectorLiveValue> GetContextInspectorValues(RoomNode? node)
    {
        if (node is not null && !CanInspectNodeInActiveContext(node)) node = null;
        List<ResourceInspectorLiveValue> values = [];
        if (node is null)
        {
            AddContextRoomValues(values);
        }
        else if (TileSelectionValid && ReferenceEquals(node, _selectedTileLayer))
        {
            AddSelectedTileContextValues(values);
        }
        else
        {
            AddContextSelectionValues(values, node);
        }

        return values;
    }

    internal bool TryApplyContextInspectorValue(RoomNode? node, string propertyPath, object? value)
    {
        if (string.IsNullOrWhiteSpace(propertyPath)) return false;
        if (node is not null && (!ReferenceEquals(node, _selected) || !CanInspectNodeInActiveContext(node))) return false;
        try
        {
            if (propertyPath.StartsWith(ContextPlacementPrefix, StringComparison.OrdinalIgnoreCase))
                return TryApplyContextPlacementValue(propertyPath, value);
            if (TryApplySelectedTileLiveValue(propertyPath, value)) return true;
            return node is null
                ? TryApplyContextRoomValue(propertyPath, value)
                : TryApplyContextSelectionValue(node, propertyPath, value);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException
                                           or OverflowException or ArgumentException or JsonException)
        {
            return false;
        }
    }

    private void AddContextRoomValues(List<ResourceInspectorLiveValue> values)
    {
        RoomSettings settings = _room.Settings;
        bool threeD = ViewMode3D;
        string unit = threeD ? "m" : "px";

        values.Add(new("Room", ContextRoomPrefix + "Persistent", "Persistent", settings.Persistent,
            Description: "Keep this room loaded when another room starts."));
        values.Add(SimpleNumber("Room", ContextRoomPrefix + "TargetFps", "Frame rate", settings.TargetFps, 1, 0));
        values.Add(SimpleNumber("Room", ContextRoomPrefix + "FixedFps", "Physics rate", settings.FixedFps, 1, 0));

        values.Add(SimpleNumber("Dimensions & bounds", ContextRoomPrefix + "Width", $"Width ({unit})", settings.Width, 1, 0));
        values.Add(SimpleNumber("Dimensions & bounds", ContextRoomPrefix + "Height", $"Height ({unit})", settings.Height, 1, 0));
        if (threeD)
            values.Add(SimpleNumber("Dimensions & bounds", ContextRoomPrefix + "Depth", "Depth (m)", settings.Depth, 1, 0));
        values.Add(new("Dimensions & bounds", ContextRoomPrefix + "ShowBounds", "Show bounds", ShowRoomBounds,
            Description: "Show the authored room boundary in the editor viewport."));

        values.Add(new("Display", ContextRoomPrefix + "ClearColor", "Clear color", ContextColor(_room.Environment.BackgroundColor)));
        values.Add(new("Display", ContextRoomPrefix + "VSync", "VSync", settings.VSync));

        values.Add(new("Grid & snapping", ContextRoomPrefix + "ShowGrid", "Show grid", ShowGrid));
        values.Add(new("Grid & snapping", ContextRoomPrefix + "SnapEnabled", "Snap to grid", settings.SnapEnabled));
        values.Add(SimpleNumber("Grid & snapping", ContextRoomPrefix + (threeD ? "MetricGridSize" : "GridSize"),
            threeD ? "Grid step (m)" : "Grid step (px)", threeD ? MetricGridSize : settings.GridSize,
            threeD ? 0.1m : 1m, threeD ? 2 : 0));
        if (threeD)
        {
            values.Add(new("Grid & snapping", ContextRoomPrefix + "SnapToTerrain", "Snap to terrain", SnapToTerrain));
            values.Add(new("Grid & snapping", ContextRoomPrefix + "AlignToTerrainNormal", "Align to terrain", AlignToTerrainNormal,
                Description: "Orient newly placed and surface-dragged objects to the terrain normal."));
            AddContextPlacementPreferences(values, includeSurface: false);

            float[] gravity = ContextVector3(_room.Environment.Gravity, 0f, -9.81f, 0f);
            values.Add(SimpleNumber("Physics", ContextRoomPrefix + "GravityX", "Gravity X (m/s²)", gravity[0], 0.1m, 2));
            values.Add(SimpleNumber("Physics", ContextRoomPrefix + "GravityY", "Gravity Y (m/s²)", gravity[1], 0.1m, 2));
            values.Add(SimpleNumber("Physics", ContextRoomPrefix + "GravityZ", "Gravity Z (m/s²)", gravity[2], 0.1m, 2));
        }
    }

    private void AddContextSelectionValues(List<ResourceInspectorLiveValue> values, RoomNode node)
    {
        bool locked = IsNodeLocked(node);
        bool layerLocked = LayerFor(node)?.Locked == true;
        values.Add(new("Instance", ContextSelectionPrefix + "Name", "Name", node.Name, ReadOnly: locked));
        values.Add(new("Instance", ContextSelectionPrefix + "Visible", "Visible", node.Enabled, ReadOnly: locked));
        values.Add(new("Instance", ContextSelectionPrefix + "Locked", "Locked", locked, ReadOnly: layerLocked,
            Description: layerLocked
                ? "This instance is locked by its layer. Unlock the layer in the outliner first."
                : "Prevent accidental selection and transform edits in the viewport."));
        values.Add(new("Instance", ContextSelectionPrefix + "Layer", "Layer", LayerFor(node)?.Name ?? string.Empty,
            ReadOnly: locked, Choices: _room.Layers.Select(layer => layer.Name).ToArray()));

        string unit = ViewMode3D ? "m" : "px";
        values.Add(SimpleNumber("Transform", ContextSelectionPrefix + "Transform.Position.X", $"Position X ({unit})",
            node.Transform.X, ViewMode3D ? 0.1m : 1m, ViewMode3D ? 3 : 2, locked));
        values.Add(SimpleNumber("Transform", ContextSelectionPrefix + "Transform.Position.Y", $"Position Y ({unit})",
            node.Transform.Y, ViewMode3D ? 0.1m : 1m, ViewMode3D ? 3 : 2, locked));
        if (ViewMode3D)
        {
            values.Add(SimpleNumber("Transform", ContextSelectionPrefix + "Transform.Position.Z", "Position Z (m)",
                node.Transform.Z, 0.1m, 3, locked));
            values.Add(SimpleNumber("Transform", ContextSelectionPrefix + "Transform.Rotation.X", "Rotation X (°)",
                node.Transform.RotationX, 1m, 2, locked));
            values.Add(SimpleNumber("Transform", ContextSelectionPrefix + "Transform.Rotation.Y", "Rotation Y (°)",
                node.Transform.RotationY, 1m, 2, locked));
        }
        values.Add(SimpleNumber("Transform", ContextSelectionPrefix + "Transform.Rotation.Z",
            ViewMode3D ? "Rotation Z (°)" : "Rotation (°)", node.Transform.RotationZ, 1m, 2, locked));
        values.Add(SimpleNumber("Transform", ContextSelectionPrefix + "Transform.Scale.X", "Scale X",
            node.Transform.ScaleX, 0.05m, 3, locked));
        values.Add(SimpleNumber("Transform", ContextSelectionPrefix + "Transform.Scale.Y", "Scale Y",
            node.Transform.ScaleY, 0.05m, 3, locked));
        if (ViewMode3D)
            values.Add(SimpleNumber("Transform", ContextSelectionPrefix + "Transform.Scale.Z", "Scale Z",
                node.Transform.ScaleZ, 0.05m, 3, locked));
        values.Add(new("Transform", ContextSelectionPrefix + "Transform.UniformScale", "Lock proportions",
            _uniformScaleInspectorNodes.Contains(node.Id), ReadOnly: locked,
            Description: "Preserve proportions and mirrored axes when editing scale."));

        AddContextKindValues(values, node, locked);
        AddInheritedComponentValues(values, node, locked);
        AddContextSurfaceValues(values, node, locked);
    }

    private void AddContextKindValues(List<ResourceInspectorLiveValue> values, RoomNode node, bool readOnly)
    {
        if (node.GameObject is { } gameObject)
        {
            values.Add(new("Object reference", "Selection.GameObject.Prefab", "Object", gameObject.Prefab,
                ReadOnly: readOnly, Description: "Source Object used by this placed instance.",
                AssetKind: ResourceKind.GameObject));
            return;
        }

        if (node.Background is { } background)
        {
            values.Add(new("Visual", "Selection.Background.Asset", "Image", background.Asset,
                ReadOnly: readOnly, AssetKind: ResourceKind.Image));
            values.Add(new("Visual", "Selection.Background.Mode", "Mode", background.Mode.ToString(),
                ReadOnly: readOnly, Choices: Enum.GetNames<RoomBackgroundMode>()));
            values.Add(new("Visual", "Selection.Background.Layout", "Layout", background.Layout.ToString(),
                ReadOnly: readOnly, Choices: Enum.GetNames<RoomBackgroundLayout>()));
            values.Add(SimpleNumber("Visual", "Selection.Background.Opacity", "Opacity", background.Opacity, 0.05m, 2, readOnly));
            return;
        }

        if (node.TileLayer is { } tiles)
        {
            values.Add(new("Tile layer", "Selection.TileLayer.Tileset", "Image", tiles.Tileset,
                ReadOnly: readOnly, AssetKind: ResourceKind.Image));
            values.Add(SimpleNumber("Tile layer", "Selection.TileLayer.CellWidth", "Cell width (px)", tiles.CellWidth, 1, 0, readOnly));
            values.Add(SimpleNumber("Tile layer", "Selection.TileLayer.CellHeight", "Cell height (px)", tiles.CellHeight, 1, 0, readOnly));
            values.Add(new("Tile layer", "Selection.TileLayer.CollisionEnabled", "Collision", tiles.CollisionEnabled, ReadOnly: readOnly));
            return;
        }

        if (node.Terrain is { } terrain)
        {
            values.Add(new("Terrain", "Selection.Terrain.Asset", "Terrain", terrain.Asset,
                ReadOnly: readOnly, AssetKind: ResourceKind.Terrain));
            values.Add(new("Terrain", "Selection.Terrain.Material", "Material image", terrain.Material,
                ReadOnly: readOnly, AssetKind: ResourceKind.Image));
            values.Add(new("Terrain", "Selection.Terrain.Albedo", "Albedo", terrain.Albedo,
                ReadOnly: readOnly, AssetKind: ResourceKind.Image));
            values.Add(SimpleNumber("Terrain", "Selection.Terrain.UvScale", "UV scale", terrain.UvScale, 0.05m, 3, readOnly));
        }
    }

    private void AddInheritedComponentValues(List<ResourceInspectorLiveValue> values, RoomNode node, bool readOnly)
    {
        if (!TryReadContextComponents(node, out IReadOnlyList<ContextComponent>? components)
            || components is null) return;
        foreach (ContextComponent component in components)
        {
            if (component.Type.Equals("TransformComponent", StringComparison.OrdinalIgnoreCase)) continue;

            string group = ContextComponentGroup(component.Type, component.DisplayName);
            string root = $"{ContextSelectionPrefix}Components[{component.Index}]";
            string enabledLabel = group.Equals("Model & visual", StringComparison.OrdinalIgnoreCase)
                ? component.DisplayName + " enabled"
                : "Enabled";
            values.Add(new(group, root + ".Enabled", enabledLabel, component.EffectiveEnabled,
                ReadOnly: readOnly, Description: ContextInheritanceDescription(component.EnabledOverridden, component.DisplayName)));

            foreach (JProperty effectiveProperty in component.Properties.Properties())
            {
                string property = effectiveProperty.Name;
                JToken effectiveToken = effectiveProperty.Value;
                JToken? hint = component.Definition?.Defaults[property];
                object? effective = ContextScalarValue(effectiveToken, hint);
                if (effective is null || !Inspectable(effective)) continue;

                bool field = property.StartsWith("field:", StringComparison.OrdinalIgnoreCase);
                bool primaryModelReference = component.Type.Equals("ModelRendererComponent", StringComparison.OrdinalIgnoreCase)
                    && property.Equals("ModelAsset", StringComparison.OrdinalIgnoreCase);
                string valueGroup = field
                    ? "Instance variables"
                    : primaryModelReference ? "Object reference" : group;
                string label = field
                    ? Humanize(property["field:".Length..])
                    : ContextComponentLabel(component.Type, component.DisplayName, property, group);
                bool overridden = component.Override?.Properties.ContainsKey(property) == true;
                values.Add(new(valueGroup, root + ".Properties." + property, label, effective,
                    ReadOnly: readOnly,
                    Description: ContextInheritanceDescription(overridden, component.DisplayName),
                    AssetKind: ContextAssetKind(component.Definition, property)));
            }
        }
    }

    private bool TryApplyContextRoomValue(string path, object? value)
    {
        if (!path.StartsWith(ContextRoomPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string name = path[ContextRoomPrefix.Length..];
        switch (name.ToLowerInvariant())
        {
            case "persistent": return SetRoomSettingsValue("persistent", value);
            case "targetfps": return SetRoomSettingsValue("targetFps", value);
            case "fixedfps": return SetRoomSettingsValue("fixedFps", value);
            case "width": return SetRoomSettingsValue("width", value);
            case "height": return SetRoomSettingsValue("height", value);
            case "depth": return SetRoomSettingsValue("depth", value);
            case "vsync": return SetRoomSettingsValue("vsync", value);
            case "showbounds": SetRoomBoundsVisible(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); return true;
            case "showgrid": SetGridVisible(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); return true;
            case "snapenabled": SetSnapEnabled(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); return true;
            case "gridsize": SetGridSize(Convert.ToSingle(value, CultureInfo.InvariantCulture)); return true;
            case "metricgridsize": SetMetricGridSize(Convert.ToSingle(value, CultureInfo.InvariantCulture)); return true;
            case "snaptoterrain": SetSnapToTerrain(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); return true;
            case "aligntoterrainnormal": SetAlignToTerrainNormal(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); return true;
            case "clearcolor": return SetContextClearColor(value);
            case "gravityx": return SetContextGravity(0, value);
            case "gravityy": return SetContextGravity(1, value);
            case "gravityz": return SetContextGravity(2, value);
            default: return false;
        }
    }

    private bool TryApplyContextSelectionValue(RoomNode node, string path, object? value)
    {
        if (path.Equals(ContextSelectionPrefix + "Locked", StringComparison.OrdinalIgnoreCase))
            return SetNodeLocked(node, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
        if (!CanEditNodeInActiveContext(node)) return false;
        if (path.Equals(ContextSelectionPrefix + "Name", StringComparison.OrdinalIgnoreCase))
            return SetNodeName(node, InspectorText(value));
        if (path.Equals(ContextSelectionPrefix + "Visible", StringComparison.OrdinalIgnoreCase))
            return SetNodeEnabled(node, Convert.ToBoolean(value, CultureInfo.InvariantCulture));
        if (path.Equals(ContextSelectionPrefix + "Layer", StringComparison.OrdinalIgnoreCase))
        {
            RoomLayer? layer = _room.Layers.FirstOrDefault(candidate =>
                candidate.Name.Equals(InspectorText(value), StringComparison.OrdinalIgnoreCase));
            return layer is not null && SetSelectionLayer(layer);
        }
        if (path.Equals(ContextSelectionPrefix + "Transform.UniformScale", StringComparison.OrdinalIgnoreCase))
        {
            if (Convert.ToBoolean(value, CultureInfo.InvariantCulture)) _uniformScaleInspectorNodes.Add(node.Id);
            else _uniformScaleInspectorNodes.Remove(node.Id);
            return true;
        }
        if (TryApplyContextTransform(node, path, value)) return true;
        if (path.StartsWith("Selection.", StringComparison.OrdinalIgnoreCase))
        {
            bool changed = TryApplyKindSpecific(node, path, value);
            if (changed)
            {
                _visualCache.Clear();
                _viewport?.Host?.Invalidate();
            }
            return changed;
        }
        return TryApplyContextComponentValue(node, path, value);
    }

    private bool TryApplyContextTransform(RoomNode node, string path, object? value)
    {
        const string prefix = ContextSelectionPrefix + "Transform.";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string tail = path[prefix.Length..];
        if (tail.Equals("UniformScale", StringComparison.OrdinalIgnoreCase)) return false;
        if (!tail.StartsWith("Scale.", StringComparison.OrdinalIgnoreCase)
            || !_uniformScaleInspectorNodes.Contains(node.Id))
        {
            return TryApplyTransform(node, "Selection.Transform." + tail, value);
        }

        float scale = Convert.ToSingle(value, CultureInfo.InvariantCulture);
        (float X, float Y, float Z) before = (node.Transform.ScaleX, node.Transform.ScaleY, node.Transform.ScaleZ);
        float anchor = tail.ToUpperInvariant() switch
        { "SCALE.X" => before.X, "SCALE.Y" => before.Y, "SCALE.Z" when ViewMode3D => before.Z, _ => float.NaN };
        if (!float.IsFinite(scale) || !float.IsFinite(anchor) || Math.Abs(anchor) < 0.000001f)
        {
            UpdateStatus("Unlock proportions to edit a zero scale axis.");
            return false;
        }
        float factor = scale / anchor;
        (float X, float Y, float Z) after = (before.X * factor, before.Y * factor, ViewMode3D ? before.Z * factor : before.Z);
        if (!float.IsFinite(after.X) || !float.IsFinite(after.Y) || !float.IsFinite(after.Z)) return false;
        if (before == after) return true;
        void Write((float X, float Y, float Z) next)
        {
            node.Transform.ScaleX = next.X;
            node.Transform.ScaleY = next.Y;
            node.Transform.ScaleZ = next.Z;
        }
        Write(after);
        PushEdit($"Scale proportions '{node.Name}'",
            () => { Write(after); RefreshPhase4Ui(); },
            () => { Write(before); RefreshPhase4Ui(); });
        return true;
    }

    private bool TryApplyContextComponentValue(RoomNode node, string path, object? value)
    {
        Match match = Regex.Match(path,
            "^Context\\.Selection\\.Components\\[(?<index>\\d+)\\]\\.(?<tail>.+)$",
            RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["index"].Value, out int index)
            || !TryReadContextComponent(node, index, out ContextComponent? component)
            || component is null) return false;

        string tail = match.Groups["tail"].Value;
        if (tail.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            bool enabled = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            return SetContextComponentState(node, component, null, new JValue(enabled));
        }
        const string propertyPrefix = "Properties.";
        if (!tail.StartsWith(propertyPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        string property = tail[propertyPrefix.Length..];
        if (!component.InheritedProperties.TryGetValue(property, out JToken? inherited)
            && component.Override?.Properties.TryGetValue(property, out inherited) != true) return false;
        JToken? hint = component.Definition?.Defaults[property] ?? inherited;
        JToken next = ContextTokenForValue(value, hint);
        return SetContextComponentState(node, component, property, next);
    }

    private bool SetContextComponentState(RoomNode node, ContextComponent component, string? property, JToken value)
    {
        if (node.GameObject is not { } gameObject || IsNodeLocked(node)) return false;
        RoomComponentOverride? existing = gameObject.ComponentOverrides.FirstOrDefault(candidate =>
            candidate.ComponentId.Equals(component.Id, StringComparison.OrdinalIgnoreCase));
        RoomComponentOverride? before = existing is null ? null : CloneContextOverride(existing);
        RoomComponentOverride working = existing is null
            ? new RoomComponentOverride { ComponentId = component.Id }
            : CloneContextOverride(existing);

        if (property is null)
        {
            bool next = value.Value<bool>();
            working.Enabled = next == component.InheritedEnabled ? null : next;
        }
        else
        {
            JToken? inherited = component.InheritedProperties[property];
            JToken? hint = component.Definition?.Defaults[property] ?? inherited;
            JToken? normalizedInherited = inherited is null ? null
                : ContextTokenForValue(ContextScalarValue(inherited, hint), hint);
            if (normalizedInherited is not null && JToken.DeepEquals(normalizedInherited, value))
                working.Properties.Remove(property);
            else
                working.Properties[property] = value.DeepClone();
        }

        RoomComponentOverride? after = working.Enabled is null && working.Properties.Count == 0
            ? null
            : working;
        if (ContextOverridesEqual(before, after)) return true;
        int insertionIndex = existing is null ? gameObject.ComponentOverrides.Count
            : gameObject.ComponentOverrides.IndexOf(existing);
        void Apply(RoomComponentOverride? state) => ApplyContextOverrideSnapshot(
            gameObject, component.Id, state, insertionIndex);

        Apply(after);
        string label = property is null ? $"Override {component.DisplayName} state" : $"Override {Humanize(property)}";
        PushEdit(label, () => Apply(after), () => Apply(before));
        return true;
    }

    private bool SetContextClearColor(object? value)
    {
        if (value is not Color color) return false;
        float alpha = _room.Environment.BackgroundColor is { Length: >= 4 }
            ? _room.Environment.BackgroundColor[3]
            : 1f;
        SetRoomBackgroundColor([color.R / 255f, color.G / 255f, color.B / 255f, alpha]);
        return true;
    }

    private bool SetContextGravity(int axis, object? value)
    {
        float[] before = ContextVector3(_room.Environment.Gravity, 0f, -9.81f, 0f);
        float[] after = (float[])before.Clone();
        after[axis] = Convert.ToSingle(value, CultureInfo.InvariantCulture);
        if (before.SequenceEqual(after)) return true;
        void Apply(float[] gravity)
        {
            _room.Environment.Gravity = (float[])gravity.Clone();
            _viewport?.Host?.Invalidate();
        }
        Apply(after);
        PushEdit("Room gravity", () => Apply(after), () => Apply(before));
        return true;
    }

    private bool TryReadContextComponents(RoomNode node, out IReadOnlyList<ContextComponent>? components)
    {
        components = null;
        if (node.GameObject is not { } gameObject) return false;
        string? path = RoomSceneBuilder.ResolvePrefabPath(ProjectRoot, gameObject.Prefab);
        if (path is null || !File.Exists(path)) return false;
        try
        {
            if (!_roomPrefabInspectorSources.TryGetValue(path, out JObject? document))
            {
                document = JObject.Parse(File.ReadAllText(path));
                _roomPrefabInspectorSources[path] = document;
                RoomMetadataReadCount++;
            }
            if (document["components"] is not JArray source) return false;
            IReadOnlyDictionary<string, object> pgslFields = ReadContextPgslFields(path);
            List<ContextComponent> result = [];
            for (int index = 0; index < source.Count; index++)
            {
                if (source[index] is not JObject json) continue;
                string type = (string?)json["type"] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(type)) continue;
                ObjectComponentDefinition? definition = ObjectCompositionModel.Definitions.FirstOrDefault(candidate =>
                    candidate.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
                string id = (string?)json["id"] ?? type;
                bool inheritedEnabled = (bool?)json["enabled"] ?? true;
                RoomComponentOverride? instanceOverride = gameObject.ComponentOverrides.FirstOrDefault(candidate =>
                    candidate.ComponentId.Equals(id, StringComparison.OrdinalIgnoreCase));
                JObject inheritedProperties = definition is null
                    ? new JObject()
                    : (JObject)definition.Defaults.DeepClone();
                if (json["props"] is JObject authored)
                {
                    foreach (JProperty authoredProperty in authored.Properties())
                        inheritedProperties[authoredProperty.Name] = authoredProperty.Value.DeepClone();
                }
                if (type.Equals("ScriptComponent", StringComparison.OrdinalIgnoreCase))
                {
                    foreach ((string name, object value) in pgslFields)
                    {
                        string property = ScriptHostSystem.FieldPrefix + name;
                        inheritedProperties[property] ??= JToken.FromObject(value);
                    }
                }
                JObject effectiveProperties = (JObject)inheritedProperties.DeepClone();
                if (instanceOverride is not null)
                {
                    foreach ((string property, JToken token) in instanceOverride.Properties)
                        effectiveProperties[property] = token.DeepClone();
                }
                result.Add(new ContextComponent(
                    index,
                    id,
                    type,
                    definition?.DisplayName ?? Humanize(type.Replace("Component", string.Empty, StringComparison.OrdinalIgnoreCase)),
                    inheritedEnabled,
                    instanceOverride?.Enabled ?? inheritedEnabled,
                    instanceOverride?.Enabled is not null,
                    inheritedProperties,
                    effectiveProperties,
                    instanceOverride,
                    definition));
            }
            components = result;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private IReadOnlyDictionary<string, object> ReadContextPgslFields(string objectPath)
    {
        if (_roomPgslInspectorFields.TryGetValue(objectPath, out IReadOnlyDictionary<string, object>? cached)) return cached;
        Dictionary<string, object> fields = new(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> events = ObjectEventStore.Load(objectPath);
        foreach (ObjectEventDefinition definition in ObjectEventCatalog.All)
        {
            if (!events.TryGetValue(definition.Id, out string? source) || source is null) continue;
            foreach (PgslExposedVariables.Variable variable in PgslExposedVariables.Reflect(source))
                fields.TryAdd(variable.Name, variable.Value);
        }
        _roomPgslInspectorFields[objectPath] = fields;
        return fields;
    }

    private bool TryReadContextComponent(RoomNode node, int index, out ContextComponent? component)
    {
        component = null;
        if (!TryReadContextComponents(node, out IReadOnlyList<ContextComponent>? components)
            || components is null) return false;
        component = components.FirstOrDefault(candidate => candidate.Index == index);
        return component is not null;
    }

    private void ApplyContextOverrideSnapshot(
        RoomGameObjectData gameObject,
        string componentId,
        RoomComponentOverride? state,
        int insertionIndex)
    {
        int existingIndex = gameObject.ComponentOverrides.FindIndex(candidate =>
            candidate.ComponentId.Equals(componentId, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0) gameObject.ComponentOverrides.RemoveAt(existingIndex);
        if (state is not null)
        {
            int index = Math.Clamp(insertionIndex, 0, gameObject.ComponentOverrides.Count);
            gameObject.ComponentOverrides.Insert(index, CloneContextOverride(state));
        }
        _contextVisualKeys.Remove(gameObject);
        _visualCache.Clear();
        _viewport?.Host?.Invalidate();
    }

    private static JObject ApplyContextVisualOverrides(JObject prefab, RoomGameObjectData gameObject) =>
        RoomSceneBuilder.ApplyOverrides(prefab, gameObject.ComponentOverrides);

    private readonly Dictionary<RoomGameObjectData, string> _contextVisualKeys = new(ReferenceEqualityComparer.Instance);

    private string ContextVisualCacheKey(RoomNode node)
    {
        RoomGameObjectData? gameObject = node.GameObject;
        if (gameObject is null) return node.Id + "|" + node.Kind;
        if (gameObject.ComponentOverrides.Count == 0) return gameObject.Prefab;
        if (_contextVisualKeys.TryGetValue(gameObject, out string? cachedKey)
            && cachedKey.StartsWith(gameObject.Prefab + "|", StringComparison.Ordinal)) return cachedKey;
        StringBuilder key = new(gameObject.Prefab);
        // Visuals depend on the prefab and component overrides, not the instance ID.
        // Share resolved assets between repeated props and stable placement previews.
        foreach (RoomComponentOverride component in gameObject.ComponentOverrides)
        {
            key.Append('|').Append(component.ComponentId).Append(':').Append(component.Enabled?.ToString() ?? "inherit");
            foreach ((string property, JToken value) in component.Properties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                key.Append('|').Append(property).Append('=').Append(value.ToString(Formatting.None));
        }
        string result = key.ToString();
        _contextVisualKeys[gameObject] = result;
        return result;
    }

    private static RoomComponentOverride CloneContextOverride(RoomComponentOverride source) => new()
    {
        ComponentId = source.ComponentId,
        Enabled = source.Enabled,
        Properties = source.Properties.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.DeepClone(),
            StringComparer.Ordinal),
    };

    private static bool ContextOverridesEqual(RoomComponentOverride? left, RoomComponentOverride? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (!left.ComponentId.Equals(right.ComponentId, StringComparison.OrdinalIgnoreCase)
            || left.Enabled != right.Enabled || left.Properties.Count != right.Properties.Count) return false;
        return left.Properties.All(pair => right.Properties.TryGetValue(pair.Key, out JToken? other)
                                           && JToken.DeepEquals(pair.Value, other));
    }

    private static object? ContextScalarValue(JToken? token, JToken? hint)
    {
        if (token is not JValue scalar || scalar.Value is null) return null;
        object value = scalar.Value;
        if (value is not string text || hint is not JValue hintValue) return value;
        return hintValue.Type switch
        {
            JTokenType.Boolean when bool.TryParse(text, out bool parsed) => parsed,
            JTokenType.Integer when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            JTokenType.Float when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => value,
        };
    }

    private static JToken ContextTokenForValue(object? value, JToken? hint)
    {
        JTokenType type = hint?.Type ?? value switch
        {
            bool => JTokenType.Boolean,
            byte or sbyte or short or ushort or int or uint or long or ulong => JTokenType.Integer,
            float or double or decimal => JTokenType.Float,
            _ => JTokenType.String,
        };
        return ConvertToken(value, type);
    }

    private static ResourceKind? ContextAssetKind(ObjectComponentDefinition? definition, string property)
    {
        if (definition?.AssetKind is { } kind
            && property.Equals(definition.AssetProperty, StringComparison.OrdinalIgnoreCase)) return kind;
        string semantic = property.StartsWith("field:", StringComparison.OrdinalIgnoreCase)
            ? property["field:".Length..]
            : property;
        string lower = semantic.ToLowerInvariant();
        if (lower.Contains("sprite") || lower.Contains("image") || lower.Contains("texture")
            || lower.Contains("albedo") || lower.Contains("normalmap")) return ResourceKind.Image;
        if (lower.Contains("model")) return ResourceKind.Model;
        if (lower.Contains("shader")) return ResourceKind.Shader;
        if (lower.Contains("audio") || lower.Contains("sound")) return ResourceKind.Audio;
        if (lower.Contains("particle")) return ResourceKind.Particle;
        if (lower.Contains("terrain")) return ResourceKind.Terrain;
        if (lower.Contains("object") || lower.Contains("prefab")) return ResourceKind.GameObject;
        return null;
    }

    private static string ContextComponentGroup(string type, string displayName) => type switch
    {
        "SpriteComponent" or "ModelRendererComponent" or "MaterialComponent" or "ShaderComponent" => "Model & visual",
        _ => displayName,
    };

    private static string ContextComponentLabel(string type, string displayName, string property, string group)
    {
        if (type.Equals("ModelRendererComponent", StringComparison.OrdinalIgnoreCase)
            && property.Equals("ModelAsset", StringComparison.OrdinalIgnoreCase)) return "Model";
        if (type.Equals("SpriteComponent", StringComparison.OrdinalIgnoreCase)
            && property.Equals("Sprite", StringComparison.OrdinalIgnoreCase)) return "Image";
        if (type.Equals("MaterialComponent", StringComparison.OrdinalIgnoreCase)
            && property.Equals("Asset", StringComparison.OrdinalIgnoreCase)) return "Material image";
        if (type.Equals("ShaderComponent", StringComparison.OrdinalIgnoreCase)
            && property.Equals("Asset", StringComparison.OrdinalIgnoreCase)) return "Shader";
        string label = Humanize(property);
        return group.Equals("Model & visual", StringComparison.OrdinalIgnoreCase)
               && !type.Equals("ModelRendererComponent", StringComparison.OrdinalIgnoreCase)
            ? displayName + " · " + label
            : label;
    }

    private static string ContextInheritanceDescription(bool overridden, string source) => overridden
        ? $"Instance override. Set this back to the {source} value to resume inheritance."
        : $"Inherited from {source}. Editing creates an override for this instance.";

    private static ResourceInspectorLiveValue SimpleNumber(
        string group,
        string path,
        string label,
        object value,
        decimal increment,
        int decimalPlaces,
        bool readOnly = false) =>
        new(group, path, label, value, ReadOnly: readOnly, Increment: increment, DecimalPlaces: decimalPlaces);

    private static Color ContextColor(float[]? values)
    {
        float[] source = values is { Length: >= 3 } ? values : [0.07f, 0.09f, 0.14f, 1f];
        return Color.FromArgb(255,
            (int)MathF.Round(Math.Clamp(source[0], 0f, 1f) * 255f),
            (int)MathF.Round(Math.Clamp(source[1], 0f, 1f) * 255f),
            (int)MathF.Round(Math.Clamp(source[2], 0f, 1f) * 255f));
    }

    private static float[] ContextVector3(float[]? source, float x, float y, float z) =>
        source is { Length: >= 3 } ? [source[0], source[1], source[2]] : [x, y, z];

    private static float ContextFloat(JObject properties, string name, float fallback) =>
        float.TryParse(properties[name]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;

    private static bool ContextBool(JObject properties, string name, bool fallback) =>
        bool.TryParse(properties[name]?.ToString(), out bool value) ? value : fallback;

    private sealed record ContextComponent(
        int Index,
        string Id,
        string Type,
        string DisplayName,
        bool InheritedEnabled,
        bool EffectiveEnabled,
        bool EnabledOverridden,
        JObject InheritedProperties,
        JObject Properties,
        RoomComponentOverride? Override,
        ObjectComponentDefinition? Definition);
}
