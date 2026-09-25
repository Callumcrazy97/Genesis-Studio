using System.Globalization;
using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    public event EventHandler? InspectorStateChanged;

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues()
    {
        TerrainPathSettings paths = _nature.PathSettings;
        List<ResourceInspectorLiveValue> values =
        [
            new(
                "Terrain resource",
                "Terrain.ResolutionX",
                "Resolution X",
                _terrain.ResolutionX,
                ReadOnly: true),
            new(
                "Terrain resource",
                "Terrain.ResolutionZ",
                "Resolution Z",
                _terrain.ResolutionZ,
                ReadOnly: true),
            new(
                "Terrain resource",
                "Terrain.CellSize",
                "Cell size",
                _terrain.CellSize,
                ReadOnly: true),
            new(
                "Terrain resource",
                "Terrain.MinHeight",
                "Minimum height",
                _terrain.MinHeight,
                ReadOnly: true),
            new(
                "Terrain resource",
                "Terrain.MaxHeight",
                "Maximum height",
                _terrain.MaxHeight,
                ReadOnly: true),
            new(
                "Terrain resource",
                "Terrain.LayerCount",
                "Paint layers",
                _settings.Layers.Count,
                ReadOnly: true),
            new(
                "Terrain resource",
                "Terrain.EntityCount",
                "Entity resources",
                _settings.Entities.Count,
                ReadOnly: true),
            new(
                "Rendering",
                "culling",
                "Culling",
                _settings.Culling.ToString(),
                Choices: Enum.GetNames<FaceCullingOverride>(),
                Description: "Default inherits Preferences - Rendering."),
            new(
                "Rendering",
                "windingOrder",
                "Winding order",
                _settings.WindingOrder.ToString(),
                Choices: Enum.GetNames<FrontFaceWindingOverride>(),
                Description: "Default inherits Preferences - Rendering."),
            new(
                "Generation",
                "Generation.Preset",
                "Preset",
                _settings.Preset,
                Choices: ["Flat", "RollingHills", "Mountains", "Valley", "Plateau"],
                Description: "Preset used by the next explicit Regenerate command."),
            new(
                "Generation",
                "Generation.Seed",
                "Seed",
                _settings.Seed,
                Minimum: 0,
                Maximum: int.MaxValue,
                Increment: 1,
                DecimalPlaces: 0),
            new(
                "Generation",
                "Generation.ErosionIterations",
                "Erosion iterations",
                _settings.ErosionIterations,
                Minimum: 0,
                Maximum: 10_000,
                Increment: 1,
                DecimalPlaces: 0),
            new(
                "Generation",
                "Generation.ErosionStrength",
                "Erosion strength",
                _settings.ErosionStrength,
                Minimum: 0,
                Maximum: 1,
                Increment: 0.01m,
                DecimalPlaces: 2),
            new(
                "Generation",
                "Generation.TerraceStrength",
                "Terrace strength",
                _settings.TerraceStrength,
                Minimum: 0,
                Maximum: 1,
                Increment: 0.01m,
                DecimalPlaces: 2),
            new(
                "Generation",
                "Generation.TerraceSteps",
                "Terrace steps",
                _settings.TerraceSteps,
                Minimum: 2,
                Maximum: 256,
                Increment: 1,
                DecimalPlaces: 0),
            new(
                "Generation",
                "Generation.RiverCount",
                "River count",
                _settings.RiverCount,
                Minimum: 0,
                Maximum: 128,
                Increment: 1,
                DecimalPlaces: 0),
            new(
                "Generation",
                "Generation.RiverDepth",
                "River depth",
                _settings.RiverDepth,
                Minimum: 0,
                Maximum: 1024,
                Increment: 0.25m,
                DecimalPlaces: 2),
            new(
                "Terrain editor",
                "Editor.ActiveMode",
                "Active mode",
                ActiveMode.ToString(),
                ReadOnly: true,
                Description: "The authoring mode selected in the terrain editor."),
            new(
                "Preview",
                "Preview.Wind",
                "Wind",
                _previewVariables.GetValueOrDefault("Wind"),
                Minimum: 0,
                Maximum: 2,
                Increment: 0.05m,
                DecimalPlaces: 2,
                Description: "Preview variable used by Terrain Entity If expressions such as Wind > 0.2."),
            new(
                "Preview",
                "Preview.Playing",
                "Playing",
                _viewportSession.Clock.Playing,
                ReadOnly: true,
                Description: "Whether the Terrain Play clock is running."),
            new(
                "Preview",
                "Preview.Time",
                "Time (seconds)",
                _viewportSession.Clock.Time,
                ReadOnly: true,
                DecimalPlaces: 2,
                Description: "Elapsed terrain preview clock used by shaders and placed-model clips."),
            new(
                "Terrain shape",
                "fogEnabled",
                "Fog",
                _fogEnabled,
                Description: "Aerial haze in the terrain preview."),
            new(
                "Brush",
                "Editor.BrushRadius",
                "Brush radius",
                BrushRadius,
                Minimum: _radiusSlider.Minimum,
                Maximum: _radiusSlider.Maximum,
                Increment: 1,
                DecimalPlaces: 0,
                Description: "Sculpt and paint brush radius in world units."),
            new(
                "Brush",
                "Editor.BrushStrength",
                "Brush strength",
                BrushStrength,
                Minimum: 0,
                Maximum: 1,
                Increment: 0.01m,
                DecimalPlaces: 2,
                Description: "Sculpt and paint brush strength."),
            new(
                "Path authoring",
                "PathSettings.Width",
                "Path width",
                paths.Width,
                Minimum: 0.25m,
                Maximum: 64m,
                Increment: 0.25m,
                DecimalPlaces: 2),
            new(
                "Path authoring",
                "PathSettings.Kind",
                "Path kind",
                paths.Kind.ToString(),
                Choices: Enum.GetNames<TerrainPathKind>()),
            new(
                "Path authoring",
                "PathSettings.GradeStrength",
                "Grade strength",
                paths.GradeStrength,
                Minimum: 0,
                Maximum: 1,
                Increment: 0.01m,
                DecimalPlaces: 2),
            new(
                "Path authoring",
                "PathSettings.Seed",
                "Path seed",
                paths.Seed,
                Minimum: 0,
                Maximum: int.MaxValue,
                Increment: 1,
                DecimalPlaces: 0),
            new(
                "Path authoring",
                "PathSettings.PathCount",
                "Path count",
                paths.PathCount,
                Minimum: 1,
                Maximum: 8,
                Increment: 1,
                DecimalPlaces: 0),
        ];

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
            && SelectedPlacedEntity() is { } selectedPlaced)
        {
            values.Add(new ResourceInspectorLiveValue(
                "Selected terrain object · Rendering",
                "Component.Culling",
                "Culling",
                selectedPlaced.Culling.ToString(),
                Choices: Enum.GetNames<FaceCullingOverride>(),
                Description: "Default inherits the Terrain Entity resource, then Preferences - Rendering."));
            values.Add(new ResourceInspectorLiveValue(
                "Selected terrain object · Rendering",
                "Component.WindingOrder",
                "Winding order",
                selectedPlaced.WindingOrder.ToString(),
                Choices: Enum.GetNames<FrontFaceWindingOverride>(),
                Description: "Default inherits the Terrain Entity resource, then Preferences - Rendering."));
            values.Add(new ResourceInspectorLiveValue(
                "Selected terrain object · Shadows",
                "Component.CastShadows",
                "Cast shadows",
                selectedPlaced.CastShadows,
                Description: "Disabled objects are omitted from the shared shadow-caster batches."));
            values.Add(new ResourceInspectorLiveValue(
                "Selected terrain object · Shadows",
                "Component.ReceiveShadows",
                "Receive shadows",
                selectedPlaced.ReceiveShadows,
                Description: "Disabled objects render normally without sampling scene shadow maps."));
        }

        if (_selectedComponentKind is { } kind
            && !string.IsNullOrWhiteSpace(_selectedComponentId)
            && SelectedShaderTargetId is { Length: > 0 } targetId)
        {
            values.Add(new ResourceInspectorLiveValue(
                "Selected component",
                "Component.Kind",
                "Kind",
                kind.ToString(),
                ReadOnly: true,
                Description: "The selected terrain component kind."));
            values.Add(new ResourceInspectorLiveValue(
                "Selected component",
                "Component.TargetId",
                "Shader target",
                targetId,
                ReadOnly: true,
                Description: "The same id the Shader Editor terrain component picker uses."));
            values.Add(new ResourceInspectorLiveValue(
                "Selected component",
                "Component.Shader",
                "Shader",
                GetComponentShader(targetId),
                Description: "Shader resource applied to this terrain component.",
                AssetKind: ResourceKind.Shader));
        }

        return values;
    }

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) =>
        TryApplyInspectorValue(propertyPath, value);

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        try
        {
            if (propertyPath.Equals("culling", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(text, true, out FaceCullingOverride culling))
            {
                _settings.Culling = culling;
                MarkDirty();
                _viewport.Invalidate();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("windingOrder", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(text, true, out FrontFaceWindingOverride winding))
            {
                _settings.WindingOrder = winding;
                MarkDirty();
                _viewport.Invalidate();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("Component.Culling", StringComparison.OrdinalIgnoreCase)
                && SelectedPlacedEntity() is { } placedForCull
                && Enum.TryParse(text, true, out FaceCullingOverride placedCulling))
            {
                placedForCull.Culling = placedCulling;
                MarkDirty();
                _viewport.Invalidate();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("Component.WindingOrder", StringComparison.OrdinalIgnoreCase)
                && SelectedPlacedEntity() is { } placedForWinding
                && Enum.TryParse(text, true, out FrontFaceWindingOverride placedWinding))
            {
                placedForWinding.WindingOrder = placedWinding;
                MarkDirty();
                _viewport.Invalidate();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("Component.CastShadows", StringComparison.OrdinalIgnoreCase)
                && SelectedPlacedEntity() is { } placedCast)
            {
                placedCast.CastShadows = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                MarkDirty();
                _viewport.Invalidate();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("Component.ReceiveShadows", StringComparison.OrdinalIgnoreCase)
                && SelectedPlacedEntity() is { } placedReceive)
            {
                placedReceive.ReceiveShadows = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                MarkDirty();
                _viewport.Invalidate();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("Preview.Wind", StringComparison.OrdinalIgnoreCase))
            {
                SetPreviewVariable("Wind", Convert.ToDouble(value, CultureInfo.InvariantCulture));
                return true;
            }

            if (propertyPath.Equals("fogEnabled", StringComparison.OrdinalIgnoreCase))
            {
                FogEnabled = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                return true;
            }

            if (propertyPath.StartsWith("Generation.", StringComparison.OrdinalIgnoreCase))
            {
                switch (propertyPath["Generation.".Length..].ToLowerInvariant())
                {
                    case "preset":
                        _settings.Preset = text;
                        _settings.SeedProfile = text;
                        break;
                    case "seed": _settings.Seed = Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
                    case "erosioniterations": _settings.ErosionIterations = Math.Max(0, Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
                    case "erosionstrength": _settings.ErosionStrength = Math.Clamp(Convert.ToSingle(value, CultureInfo.InvariantCulture), 0f, 1f); break;
                    case "terracestrength": _settings.TerraceStrength = Math.Clamp(Convert.ToSingle(value, CultureInfo.InvariantCulture), 0f, 1f); break;
                    case "terracesteps": _settings.TerraceSteps = Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 2, 256); break;
                    case "rivercount": _settings.RiverCount = Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 0, 128); break;
                    case "riverdepth": _settings.RiverDepth = Math.Clamp(Convert.ToSingle(value, CultureInfo.InvariantCulture), 0f, 1024f); break;
                    default: return false;
                }
                MarkDirty();
                UpdateStatus();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("Editor.BrushRadius", StringComparison.OrdinalIgnoreCase))
            {
                _radiusSlider.Value = Math.Clamp(
                    Convert.ToInt32(value, CultureInfo.InvariantCulture),
                    _radiusSlider.Minimum,
                    _radiusSlider.Maximum);
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("Editor.BrushStrength", StringComparison.OrdinalIgnoreCase))
            {
                float strength = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                _strengthSlider.Value = Math.Clamp(
                    (int)MathF.Round(strength * 100f),
                    _strengthSlider.Minimum,
                    _strengthSlider.Maximum);
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("PathSettings.Width", StringComparison.OrdinalIgnoreCase))
            {
                _nature.PathSettings.Width = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                _nature.PathSettings.Normalize();
                MarkDirty();
                UpdateStatus();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("PathSettings.GradeStrength", StringComparison.OrdinalIgnoreCase))
            {
                _nature.PathSettings.GradeStrength = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                _nature.PathSettings.Normalize();
                MarkDirty();
                UpdateStatus();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("PathSettings.Seed", StringComparison.OrdinalIgnoreCase))
            {
                _nature.PathSettings.Seed = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                _nature.PathSettings.Normalize();
                MarkDirty();
                UpdateStatus();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("PathSettings.PathCount", StringComparison.OrdinalIgnoreCase))
            {
                _nature.PathSettings.PathCount = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                _nature.PathSettings.Normalize();
                MarkDirty();
                UpdateStatus();
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("Component.Shader", StringComparison.OrdinalIgnoreCase)
                && SelectedShaderTargetId is { Length: > 0 } targetId)
            {
                SetComponentShader(targetId, text);
                NotifyInspectorStateChanged();
                return true;
            }

            if (propertyPath.Equals("PathSettings.Kind", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(text, ignoreCase: true, out TerrainPathKind kind))
            {
                _nature.PathSettings.Kind = kind;
                _nature.PathSettings.Normalize();
                MarkDirty();
                UpdateStatus();
                NotifyInspectorStateChanged();
                return true;
            }
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException
                                           or OverflowException)
        {
            return false;
        }

        return false;
    }

    private void RebuildSelectionInspector()
    {
        if (ActiveMode == TerrainEditorMode.Foliage && !_foliageBrushArmed)
        {
            _selectionInspector.ShowEmpty("Foliage");
            return;
        }
        if (_selectedComponentKind is null || string.IsNullOrWhiteSpace(_selectedComponentId))
        {
            _selectionInspector.ShowTerrainMaterials(_settings.Layers.Take(4).Select((layer, index) => (layer.Name, layer.Image, (Action)(() => OpenLayerMaterial(index)))));
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
            && SelectedPlacedEntity() is { } placed)
        {
            TerrainEntityDocument? document = TryLoadEntityDocument(ResolveEntityFullPath(placed.Entity));
            string title = document?.Name
                ?? ResourceDisplayName.Format(placed.Entity);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Entity";
            }

            TerrainEntityType type = document?.Type ?? TerrainEntityType.Object;
            string kindLabel = type == TerrainEntityType.Fluid ? "Water" : type.ToString();
            TerrainEntityComponent? model = document?.Components
                .FirstOrDefault(component => component.Enabled && component.Type == TerrainEntityComponentKinds.Model);
            string shader = SelectedShaderTargetId is { Length: > 0 } target
                ? GetComponentShader(target)
                : "";
            _selectionInspector.ShowSelection(new TerrainSelectionFields(
                title,
                kindLabel,
                CanEditIdentity: true,
                HasTransform: true,
                placed.Position,
                new Vector3(placed.Pitch, placed.Yaw, placed.Roll),
                placed.Scale,
                HasShader: true,
                shader,
                HasModel: true,
                model?.Get("Model") ?? "",
                placed.AnimationClip,
                HasCondition: true,
                placed.IfExpression,
                placed.ThenClip,
                placed.ElseClip,
                placed.Culling.ToString(),
                placed.WindingOrder.ToString(),
                HasRasterSettings: true,
                CastShadows: placed.CastShadows,
                ReceiveShadows: placed.ReceiveShadows));
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Water
            && SelectedWater() is { } water)
        {
            _selectionInspector.ShowSelection(new TerrainSelectionFields(
                water.Name,
                water.Kind.ToString(),
                CanEditIdentity: true,
                HasTransform: true,
                new Vector3(water.Center.X, water.SurfaceHeight, water.Center.Z),
                Vector3.Zero,
                1f,
                HasShader: true,
                SelectedShaderTargetId is { Length: > 0 } waterTarget ? GetComponentShader(waterTarget) : "",
                HasModel: false,
                "",
                "",
                HasCondition: false,
                "",
                "",
                ""));
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.PointOfInterest
            && SelectedPoint() is { } point)
        {
            _selectionInspector.ShowSelection(new TerrainSelectionFields(
                point.Name,
                point.Category,
                CanEditIdentity: true,
                HasTransform: true,
                point.Position,
                Vector3.Zero,
                point.DiscoveryRadius,
                HasShader: false,
                "",
                HasModel: false,
                "",
                "",
                HasCondition: false,
                "",
                "",
                ""));
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Path
            && SelectedPath() is { } path)
        {
            _selectionInspector.ShowSelection(new TerrainSelectionFields(
                path.Name,
                path.Kind.ToString(),
                CanEditIdentity: true,
                HasTransform: false,
                Vector3.Zero,
                Vector3.Zero,
                1f,
                HasShader: true,
                SelectedShaderTargetId is { Length: > 0 } pathTarget ? GetComponentShader(pathTarget) : "",
                HasModel: false,
                "",
                "",
                HasCondition: false,
                "",
                "",
                ""));
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Layer
            && int.TryParse(_selectedComponentId, out int layer)
            && layer >= 0 && layer < _settings.Layers.Count)
        {
            TerrainLayerDocument document = _settings.Layers[layer];
            _selectionInspector.ShowSelection(new TerrainSelectionFields(
                document.Name,
                "Paint layer",
                CanEditIdentity: false,
                HasTransform: false,
                Vector3.Zero,
                Vector3.Zero,
                1f,
                HasShader: true,
                SelectedShaderTargetId is { Length: > 0 } layerTarget ? GetComponentShader(layerTarget) : "",
                HasModel: false,
                "",
                "",
                HasCondition: false,
                "",
                "",
                ""));
            _selectionInspector.AddMaterialAction(document.Image, TerrainImageMaterial.Describe(ProjectRoot, document.Image), () => OpenLayerMaterial(layer));
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Foliage)
        {
            _selectionInspector.ShowSelection(new TerrainSelectionFields(
                $"{_foliage.Instances.Count:N0} instances",
                "Foliage field",
                CanEditIdentity: true,
                HasTransform: false,
                Vector3.Zero,
                Vector3.Zero,
                1f,
                HasShader: true,
                SelectedShaderTargetId is { Length: > 0 } foliageTarget ? GetComponentShader(foliageTarget) : "",
                HasModel: false,
                "",
                "",
                HasCondition: false,
                "",
                "",
                ""));
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
            && TryLoadEntityDocument(ResolveEntityFullPath(_selectedComponentId)) is { } definition)
        {
            _selectionInspector.ShowDefinition(definition.Name, definition.Type.ToString());
            return;
        }
        _selectedComponentKind = null; _selectedComponentId = null;
        _selectionInspector.ShowTerrainMaterials(_settings.Layers.Take(4).Select((layer, index) => (layer.Name, layer.Image, (Action)(() => OpenLayerMaterial(index)))));
    }

    private void ApplySelectionInspectorFields(TerrainSelectionFields fields)
    {
        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Entity
            && SelectedPlacedEntity() is { } placed)
        {
            placed.Position = fields.Position;
            placed.Pitch = fields.Rotation.X;
            placed.Yaw = fields.Rotation.Y;
            placed.Roll = fields.Rotation.Z;
            placed.Scale = fields.Scale;
            placed.AnimationClip = fields.AnimationClip;
            placed.IfExpression = fields.IfExpression;
            if (Enum.TryParse(fields.Culling, true, out FaceCullingOverride culling))
                placed.Culling = culling;
            if (Enum.TryParse(fields.WindingOrder, true, out FrontFaceWindingOverride winding))
                placed.WindingOrder = winding;
            placed.CastShadows = fields.CastShadows;
            placed.ReceiveShadows = fields.ReceiveShadows;
            placed.Normalize();
            if (SelectedShaderTargetId is { Length: > 0 } entityShader)
            {
                SetComponentShader(entityShader, fields.ShaderPath);
            }

            MarkDirty();
            _viewport.Invalidate();
            UpdateStatus();
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Water
            && SelectedWater() is { } water)
        {
            Vector3 previous = new(water.Center.X, water.SurfaceHeight, water.Center.Z);
            Vector3 delta = fields.Position - previous;
            water.Center += delta;
            water.SurfaceHeight = fields.Position.Y;
            water.TranslateGeometry(delta);
            if (SelectedShaderTargetId is { Length: > 0 } waterShader)
            {
                SetComponentShader(waterShader, fields.ShaderPath);
            }

            _natureMeshDirty = true;
            MarkDirty();
            _viewport.Invalidate();
            UpdateStatus();
            return;
        }

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.PointOfInterest
            && SelectedPoint() is { } point)
        {
            point.Position = fields.Position;
            point.DiscoveryRadius = Math.Clamp(fields.Scale, 0.5f, 10000f);
            MarkDirty();
            _viewport.Invalidate();
            UpdateStatus();
            return;
        }

        if (SelectedShaderTargetId is { Length: > 0 } shaderTarget
            && !string.IsNullOrWhiteSpace(fields.ShaderPath))
        {
            SetComponentShader(shaderTarget, fields.ShaderPath);
            NotifyInspectorStateChanged();
        }
    }

    private void WireInspectorNotifications()
    {
        _radiusSlider.ValueChanged += (_, _) => NotifyInspectorStateChanged();
        _strengthSlider.ValueChanged += (_, _) => NotifyInspectorStateChanged();
    }

    private void NotifyInspectorStateChanged() =>
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
}
