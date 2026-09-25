using System.Numerics;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private readonly RuntimeModelRenderSystem _placedModelPreview = new();
    private readonly Dictionary<string, double> _previewVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Wind"] = 0,
    };

    public TerrainComponentsPanel.ComponentKind? SelectedComponentKind => _selectedComponentKind;
    public string? SelectedComponentId => _selectedComponentId;
    public int PaintLayerCount => _settings.Layers.Count;
    public int PlacedEntityCount => _nature.PlacedEntities.Count;
    public bool IsPreviewPlaying => _viewportSession.Clock.Playing;
    public float PreviewTimeSeconds => _viewportSession.Clock.Time;
    public TerrainEntityWizardPanel? ActiveEntityWizard => _entityWizard;
    public bool HasComponentGizmo => SelectedComponentGizmoOrigin() is not null;
    public IReadOnlyDictionary<string, double> PreviewVariables => _previewVariables;

    public void SetPreviewVariable(string name, double value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _previewVariables[name.Trim()] = value;
        NotifyInspectorStateChanged();
        _viewport.Invalidate();
    }

    public void AddPaintLayer(string? name = null)
    {
        List<TerrainLayerDocument> before = CloneLayers(_settings.Layers);
        List<TerrainLayerDocument> after = CloneLayers(before);
        after.Add(new TerrainLayerDocument
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Layer {after.Count + 1}" : name.Trim(),
            Color = [0.55f, 0.48f, 0.40f],
        });
        ApplyLayers(before, after, $"Add paint layer '{after[^1].Name}'");
        SetMode(TerrainEditorMode.Paint);
        _layerListBox.SelectedIndex = _layerListBox.Items.Count - 1;
        _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Layer, (after.Count - 1).ToString());
    }

    public void DuplicateSelectedComponent()
    {
        if (_componentsPanel.CurrentSelection is { } selection)
        {
            DuplicateComponent(selection);
        }
    }

    public void DeleteSelectedComponent()
    {
        if (_componentsPanel.CurrentSelection is { } selection)
        {
            DeleteComponent(selection);
        }
    }

    public void PlayPreview()
    {
        _viewportSession.Clock.Play();
        UpdateStatus();
        _viewport.Invalidate();
    }

    public void PausePreview()
    {
        _viewportSession.Clock.Pause();
        UpdateStatus();
    }

    public void StopPreview()
    {
        _viewportSession.Clock.Stop();
        _viewport.Host.Renderer.ResetShaderPreviewTime();
        _viewportSession.ResetSun();
        UpdateStatus();
        _viewport.Invalidate();
    }

    public void StepPreview(float dt, int steps = 1)
    {
        _viewportSession.Clock.Step(dt, steps);
        float total = dt * Math.Max(1, steps);
        if (total > 0f)
        {
            _viewport.Host.AdvanceSceneTime(total);
        }

        _viewport.Invalidate();
        UpdateStatus();
    }

    public string CreateTerrainEntity(TerrainEntityType type)
    {
        OnEntityCreateRequested(this, type);
        return _pendingEntityPath ?? _entityWizard?.ResourcePath ?? string.Empty;
    }

    public string PlaceTerrainEntity(string entityPath, float worldX, float worldZ)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityPath);
        string relative = ResourceNames.Name(ProjectRoot, entityPath);
        if (!_settings.Entities.Contains(relative, StringComparer.OrdinalIgnoreCase))
        {
            _settings.Entities.Add(relative);
        }

        TerrainEntityDocument? document = TryLoadEntityDocument(entityPath);
        string clip = document?.Components
            .FirstOrDefault(component => component.Type == TerrainEntityComponentKinds.Model)
            ?.Get("AnimationClip") ?? string.Empty;
        TerrainEntityComponent? rule = document?.Components
            .FirstOrDefault(component => component.Type == TerrainEntityComponentKinds.Condition);

        List<TerrainPlacedEntity> before = ClonePlaced(_nature.PlacedEntities);
        TerrainPlacedEntity placed = new()
        {
            Entity = relative,
            Position = new Vector3(worldX, _terrain.SampleHeight(worldX, worldZ), worldZ),
            AnimationClip = clip,
            IfExpression = rule?.Get("If") ?? string.Empty,
            ThenClip = rule?.Get("ThenClip") ?? string.Empty,
            ElseClip = rule?.Get("ElseClip") ?? string.Empty,
            ThenSource = rule?.Get("ThenSource") ?? string.Empty,
            ElseSource = rule?.Get("ElseSource") ?? string.Empty,
        };
        placed.Normalize();
        List<TerrainPlacedEntity> after = ClonePlaced(before);
        after.Add(placed);
        ApplyPlaced(before, after, $"Place '{ResourceDisplayName.Format(entityPath)}'");
        _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Entity, placed.Id);
        return placed.Id;
    }

    private void DuplicateComponent(TerrainComponentsPanel.ComponentSelection selection)
    {
        switch (selection.Kind)
        {
            case TerrainComponentsPanel.ComponentKind.Layer:
                if (int.TryParse(selection.Id, out int layer) && layer >= 0 && layer < _settings.Layers.Count)
                {
                    List<TerrainLayerDocument> before = CloneLayers(_settings.Layers);
                    List<TerrainLayerDocument> after = CloneLayers(before);
                    TerrainLayerDocument copy = after[layer];
                    after.Insert(layer + 1, new TerrainLayerDocument { Name = copy.Name + " copy", Color = [.. copy.Color] });
                    ApplyLayers(before, after, $"Duplicate layer '{copy.Name}'");
                    _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Layer, (layer + 1).ToString());
                }
                break;
            case TerrainComponentsPanel.ComponentKind.Path:
                List<TerrainPathDefinition> pathsBefore = Clone(_nature.Paths);
                List<TerrainPathDefinition> pathsAfter = Clone(pathsBefore);
                TerrainPathDefinition? path = pathsAfter.FirstOrDefault(item =>
                    string.Equals(item.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
                if (path is null) return;
                TerrainPathDefinition pathCopy = new()
                {
                    Name = path.Name + " copy",
                    Kind = path.Kind,
                    Width = path.Width,
                    Points = path.Points.Select(point => point + new Vector3(2f, 0f, 2f)).ToList(),
                };
                pathsAfter.Add(pathCopy);
                ApplyPaths(pathsBefore, pathsAfter, $"Duplicate path '{path.Name}'");
                _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Path, pathCopy.Id);
                break;
            case TerrainComponentsPanel.ComponentKind.Water:
                List<TerrainWaterDefinition> waterBefore = Clone(_nature.WaterBodies);
                List<TerrainWaterDefinition> waterAfter = Clone(waterBefore);
                TerrainWaterDefinition? water = waterAfter.FirstOrDefault(item =>
                    string.Equals(item.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
                if (water is null) return;
                TerrainWaterDefinition waterCopy = water.Clone();
                waterCopy.Id = Guid.NewGuid().ToString("N");
                waterCopy.Name = water.Name + " copy";
                waterCopy.Center = water.Center + new Vector3(4f, 0f, 0f);
                waterCopy.TranslateFootprint(4f, 0f);
                waterAfter.Add(waterCopy);
                SetWaterBodies(waterAfter);
                _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Water, waterCopy.Id);
                break;
            case TerrainComponentsPanel.ComponentKind.PointOfInterest:
                TerrainPointOfInterest? point = _nature.PointsOfInterest.FirstOrDefault(item =>
                    string.Equals(item.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
                if (point is null) return;
                List<TerrainPointOfInterest> pointsBefore = ClonePoints(_nature.PointsOfInterest);
                List<TerrainPointOfInterest> pointsAfter = ClonePoints(pointsBefore);
                TerrainPointOfInterest pointCopy = new()
                {
                    Name = point.Name + " copy",
                    Category = point.Category,
                    Position = point.Position + new Vector3(2f, 0f, 2f),
                    DiscoveryRadius = point.DiscoveryRadius,
                };
                pointsAfter.Add(pointCopy);
                ApplyPointsOfInterest(pointsBefore, pointsAfter, $"Duplicate point '{point.Name}'");
                _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.PointOfInterest, pointCopy.Id);
                break;
            case TerrainComponentsPanel.ComponentKind.Entity:
                TerrainPlacedEntity? placed = _nature.PlacedEntities.FirstOrDefault(item =>
                    string.Equals(item.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
                if (placed is null) return;
                List<TerrainPlacedEntity> placedBefore = ClonePlaced(_nature.PlacedEntities);
                TerrainPlacedEntity placedCopy = ClonePlacedItem(placed);
                placedCopy.Id = Guid.NewGuid().ToString("N");
                placedCopy.Position += new Vector3(3f, 0f, 0f);
                placedCopy.Normalize();
                List<TerrainPlacedEntity> placedAfter = ClonePlaced(placedBefore);
                placedAfter.Add(placedCopy);
                ApplyPlaced(placedBefore, placedAfter, "Duplicate placed entity");
                _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Entity, placedCopy.Id);
                break;
        }
    }

    private void DeleteComponent(TerrainComponentsPanel.ComponentSelection selection)
    {
        switch (selection.Kind)
        {
            case TerrainComponentsPanel.ComponentKind.Layer:
                if (_settings.Layers.Count <= 1) return;
                if (int.TryParse(selection.Id, out int layer) && layer >= 0 && layer < _settings.Layers.Count)
                {
                    List<TerrainLayerDocument> before = CloneLayers(_settings.Layers);
                    List<TerrainLayerDocument> after = CloneLayers(before);
                    string name = after[layer].Name;
                    after.RemoveAt(layer);
                    ApplyLayers(before, after, $"Delete layer '{name}'");
                }
                break;
            case TerrainComponentsPanel.ComponentKind.Path:
                List<TerrainPathDefinition> pathsBefore = Clone(_nature.Paths);
                List<TerrainPathDefinition> pathsAfter = Clone(pathsBefore);
                pathsAfter.RemoveAll(item => string.Equals(item.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
                ApplyPaths(pathsBefore, pathsAfter, "Delete path");
                break;
            case TerrainComponentsPanel.ComponentKind.Water:
                List<TerrainWaterDefinition> waterAfter = Clone(_nature.WaterBodies);
                waterAfter.RemoveAll(item => string.Equals(item.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
                SetWaterBodies(waterAfter);
                break;
            case TerrainComponentsPanel.ComponentKind.Foliage:
                ScatterFoliage(new FoliageScatterSettings
                {
                    Seed = _nature.FoliageSettings.Seed,
                    Preset = _nature.FoliageSettings.Preset,
                    MaximumInstances = 0,
                    Density = 0f,
                });
                break;
            case TerrainComponentsPanel.ComponentKind.PointOfInterest:
                List<TerrainPointOfInterest> pointsBefore = ClonePoints(_nature.PointsOfInterest);
                List<TerrainPointOfInterest> pointsAfter = ClonePoints(pointsBefore);
                pointsAfter.RemoveAll(item => string.Equals(item.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
                ApplyPointsOfInterest(pointsBefore, pointsAfter, "Delete point of interest");
                break;
            case TerrainComponentsPanel.ComponentKind.Entity:
                if (SelectedPlacedEntity() is null && File.Exists(ResolveEntityFullPath(selection.Id)))
                {
                    DeleteObjectDefinition(selection.Id);
                    break;
                }
                List<TerrainPlacedEntity> placedBefore = ClonePlaced(_nature.PlacedEntities);
                List<TerrainPlacedEntity> placedAfter = ClonePlaced(placedBefore);
                placedAfter.RemoveAll(item => string.Equals(item.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
                ApplyPlaced(placedBefore, placedAfter, "Delete placed entity");
                break;
        }

        _selectedComponentId = null;
        _selectedComponentKind = null;
        RefreshComponentsPanel();
    }

    private void ApplyLayers(List<TerrainLayerDocument> before, List<TerrainLayerDocument> after, string label)
    {
        _settings.Layers = CloneLayers(after);
        RebuildLayerList();
        PushEdit(
            label,
            () => { _settings.Layers = CloneLayers(after); RebuildLayerList(); RefreshComponentsPanel(); },
            () => { _settings.Layers = CloneLayers(before); RebuildLayerList(); RefreshComponentsPanel(); });
        RefreshComponentsPanel();
        UpdateStatus();
    }

    private void ApplyPaths(List<TerrainPathDefinition> before, List<TerrainPathDefinition> after, string label)
    {
        _nature.Paths = Clone(after);
        _pathNetwork = new TerrainPathNetwork(_nature.Paths);
        _natureMeshDirty = true;
        PushEdit(
            label,
            () =>
            {
                _nature.Paths = Clone(after);
                _pathNetwork = new TerrainPathNetwork(_nature.Paths);
                _natureMeshDirty = true;
                RefreshComponentsPanel();
            },
            () =>
            {
                _nature.Paths = Clone(before);
                _pathNetwork = new TerrainPathNetwork(_nature.Paths);
                _natureMeshDirty = true;
                RefreshComponentsPanel();
            });
        RefreshComponentsPanel();
        UpdateStatus();
        _viewport.Invalidate();
    }

    private void ApplyPlaced(List<TerrainPlacedEntity> before, List<TerrainPlacedEntity> after, string label)
    {
        _nature.PlacedEntities = ClonePlaced(after);
        PushEdit(
            label,
            () => { _nature.PlacedEntities = ClonePlaced(after); RefreshComponentsPanel(); },
            () => { _nature.PlacedEntities = ClonePlaced(before); RefreshComponentsPanel(); });
        RefreshComponentsPanel();
        UpdateStatus();
        _viewport.Invalidate();
    }

    private void RebuildLayerList()
    {
        _layerListBox.Items.Clear();
        foreach (TerrainLayerDocument layer in _settings.Layers)
        {
            _layerListBox.Items.Add(layer.Name);
        }
    }

    private void TickPreviewIfPlaying()
    {
        if (_viewportSession.AdvancePlayingFrame())
        {
            _viewport.Host.AdvanceSceneTime(EditorPreviewClock.FrameDelta);
            NotifyInspectorStateChanged();
            UpdateStatus();
        }
    }

    private void DrawPlacedEntities(IRenderController renderer)
    {
        IEnumerable<TerrainPlacedEntity> placements = _nature.PlacedEntities;
        if (_placementEntityPath is { } previewPath && _cursorValid)
            placements = placements.Append(new TerrainPlacedEntity { Id = "__placement_preview", Entity = previewPath, Position = _cursorWorld, Scale = 1 });
        foreach (TerrainPlacedEntity placed in placements)
        {
            if (placed.Id != "__placement_preview" && !ShouldDrawNatureComponent(TerrainComponentsPanel.ComponentKind.Entity, placed.Id))
            {
                continue;
            }

            string fullPath = ResolveEntityFullPath(placed.Entity);
            TerrainEntityDocument? document = TryLoadEntityDocument(fullPath);
            TerrainEntityComponent? model = document?.Components
                .FirstOrDefault(component => component.Enabled && component.Type == TerrainEntityComponentKinds.Model);
            string? modelAsset = model?.Get("Model");
            if (modelAsset?.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase) == true)
                modelAsset = ResolveEntityFullPath(modelAsset);
            Vector3 position = placed.Position;
            Matrix4x4 world = Matrix4x4.CreateScale(placed.Scale)
                * Matrix4x4.CreateFromYawPitchRoll(
                    placed.Yaw * MathF.PI / 180f,
                    placed.Pitch * MathF.PI / 180f,
                    placed.Roll * MathF.PI / 180f)
                * Matrix4x4.CreateTranslation(position);

            if (!string.IsNullOrWhiteSpace(modelAsset) && model is not null)
            {
                bool conditionMet = EvaluatePreviewCondition(placed.IfExpression, _previewVariables);
                string clip = ResolvePlacedPreviewClip(placed, conditionMet);
                if (string.IsNullOrWhiteSpace(clip))
                {
                    clip = model.Get("AnimationClip");
                }

                float fps = placed.AnimationFps;
                if (fps <= 0f && float.TryParse(model.Get("AnimationFps", "60"), out float parsed))
                {
                    fps = parsed;
                }

                _entityDrawList.Clear();
                float componentScale = float.TryParse(model.Get("Scale", "1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float scale) ? Math.Clamp(scale, .001f, 1000) : 1;
                _placedModelPreview.Enqueue(
                    _entityDrawList,
                    ProjectRoot,
                    modelAsset,
                    string.Empty,
                    Matrix4x4.CreateScale(componentScale) * world,
                    new Draw3DComponent
                    {
                        Visible = true,
                        CastShadows = placed.CastShadows
                            && !string.Equals(model.Get("CastShadows", "true"), "false", StringComparison.OrdinalIgnoreCase),
                        ReceiveShadows = placed.ReceiveShadows
                            && !string.Equals(model.Get("ReceiveShadows", "true"), "false", StringComparison.OrdinalIgnoreCase),
                    },
                    new ModelRendererComponent
                    {
                        ScaleX = 1f,
                        ScaleY = 1f,
                        ScaleZ = 1f,
                        CastShadows = placed.CastShadows
                            && !string.Equals(model.Get("CastShadows", "true"), "false", StringComparison.OrdinalIgnoreCase),
                        ReceiveShadows = placed.ReceiveShadows
                            && !string.Equals(model.Get("ReceiveShadows", "true"), "false", StringComparison.OrdinalIgnoreCase),
                        Culling = MeshRasterDefaults.Resolve(
                            placed.Culling,
                            document!.Culling),
                        WindingOrder = MeshRasterDefaults.Resolve(
                            placed.WindingOrder,
                            document!.WindingOrder),
                    },
                    new RuntimeModelAnimationState(
                        clip,
                        placed.AnimationPlaying || _viewportSession.Clock.Playing
                            ? _viewportSession.Clock.Time
                            : 0f,
                        fps,
                        placed.AnimationLoop),
                    renderer);
                foreach (var draw in _entityDrawList.Items) SubmitEntityDraw(renderer, document!, draw);
            }
            else if (document is not null) DrawEntitySprite(renderer, document, world);
        }
    }

    public static bool EvaluatePreviewCondition(
        string expression,
        IReadOnlyDictionary<string, double>? variables = null) =>
        TerrainVisualCondition.EvaluateIf(expression, variables);

    public static string ResolvePlacedPreviewClip(TerrainPlacedEntity placed, bool conditionMet)
    {
        if (!string.IsNullOrWhiteSpace(placed.IfExpression))
        {
            return conditionMet
                ? FirstClip(placed.ThenSource, placed.ThenClip, placed.AnimationClip)
                : FirstClip(placed.ElseSource, placed.ElseClip, placed.AnimationClip);
        }

        return FirstClip(placed.ThenSource, placed.AnimationClip);
    }

    private static string FirstClip(string source, string clip, string fallback = "")
    {
        string fromActions = TerrainVisualCondition.ClipFromActions(source);
        if (!string.IsNullOrWhiteSpace(fromActions)) return fromActions;
        if (!string.IsNullOrWhiteSpace(clip)) return clip;
        return fallback ?? "";
    }

    private string ResolveEntityFullPath(string relative)
    {
        return ResourceNames.Resolve(ProjectRoot, relative);
    }

    private TerrainEntityDocument? TryLoadEntityDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            if (path.EndsWith(".object.json", StringComparison.OrdinalIgnoreCase)) return TerrainObjectResourceBridge.Load(ProjectRoot, path);
            return JsonSerializer.Deserialize<TerrainEntityDocument>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                });
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static List<TerrainLayerDocument> CloneLayers(IEnumerable<TerrainLayerDocument> layers) =>
        layers.Select(layer => new TerrainLayerDocument
        {
            Name = layer.Name,
            Image = layer.Image,
            Tiling = layer.Tiling,
            Addressing = layer.Addressing,
            Resolution = layer.Resolution,
            Color = layer.Color is { Length: >= 3 } color ? [color[0], color[1], color[2]] : [0.4f, 0.5f, 0.3f],
        }).ToList();

    private static List<TerrainPlacedEntity> ClonePlaced(IEnumerable<TerrainPlacedEntity> items) =>
        items.Select(ClonePlacedItem).ToList();

    private static TerrainPlacedEntity ClonePlacedItem(TerrainPlacedEntity item)
    {
        TerrainPlacedEntity copy = new()
        {
            Id = item.Id,
            Entity = item.Entity,
            Position = item.Position,
            Yaw = item.Yaw,
            Pitch = item.Pitch,
            Roll = item.Roll,
            Scale = item.Scale,
            AnimationClip = item.AnimationClip,
            AnimationPlaying = item.AnimationPlaying,
            AnimationLoop = item.AnimationLoop,
            AnimationFps = item.AnimationFps,
            IfExpression = item.IfExpression,
            ThenClip = item.ThenClip,
            ElseClip = item.ElseClip,
            ThenSource = item.ThenSource,
            ElseSource = item.ElseSource,
            Culling = item.Culling,
            WindingOrder = item.WindingOrder,
            CastShadows = item.CastShadows,
            ReceiveShadows = item.ReceiveShadows,
        };
        copy.Normalize();
        return copy;
    }
}
