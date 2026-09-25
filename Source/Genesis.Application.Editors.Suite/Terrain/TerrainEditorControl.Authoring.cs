using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;
using Genesis.World.Water;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    public enum WaterAuthoringTool
    {
        Select,
        Spray,
        Region,
        Level,
        River,
        Basin,
    }

    private readonly TerrainComponentsPanel _componentsPanel = new();
    private readonly Dictionary<WaterAuthoringTool, Button> _waterToolButtons = [];
    private readonly CheckBox _carveBasinCheck = new()
    {
        AutoSize = true,
        Checked = false,
        Text = "Carve basin into terrain",
    };
    private readonly CheckBox _waterSquareRegionCheck = new()
    {
        AutoSize = true,
        Text = "Square region",
    };
    private readonly ThemedComboBox _waterKindCombo = new()
    {
        Name = "TerrainWaterKindPicker",
        Width = 244,
    };
    private readonly TrackBar _waterSprayRadiusSlider = MakeSlider(1, 40, 6);
    private readonly NumericUpDown _waterLevelBox = TerrainPathDialog.DecimalNumber(-10000m, 10000m, 0m);

    private WaterAuthoringTool _waterTool = WaterAuthoringTool.Spray;
    private bool _waterDragging;
    private bool _waterSpraying;
    private Vector3 _waterDragStart;
    private Vector3 _waterDragEnd;
    private float _waterPreviewLevel;
    private bool[]? _waterMask;
    private bool _waterRegionActive;
    private int _waterRegionMinX;
    private int _waterRegionMaxX;
    private int _waterRegionMinZ;
    private int _waterRegionMaxZ;
    private float _waterLevel;
    private bool _waterLevelSet;
    private bool _syncingWaterLevelBox;
    private TerrainWaterKind _pendingWaterKind = TerrainWaterKind.Water;
    private string? _selectedComponentId;
    private TerrainComponentsPanel.ComponentKind? _selectedComponentKind;
    private bool _isolateSelectedComponent;
    public bool ShowGrid => _viewportSession.ShowGrid;

    public bool IsolateSelectedComponent => _isolateSelectedComponent;

    public EditorFloorStyle FloorStyle => _viewportSession.FloorStyle;

    public Editor3DSession ViewportSession => _viewportSession;

    private Vector3? SelectedComponentWorldPoint()
    {
        if (_selectedComponentKind is TerrainComponentsPanel.ComponentKind.Water
            && !string.IsNullOrWhiteSpace(_selectedComponentId)
            && _nature.WaterBodies.FirstOrDefault(body =>
                string.Equals(body.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase))
                is { } water)
        {
            return water.Center;
        }

        if (_selectedComponentKind is TerrainComponentsPanel.ComponentKind.Path
            && !string.IsNullOrWhiteSpace(_selectedComponentId)
            && _nature.Paths.FirstOrDefault(path =>
                string.Equals(path.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase))
                is { Points.Count: > 0 } path)
        {
            return path.Points[path.Points.Count / 2];
        }

        if (_selectedComponentKind is TerrainComponentsPanel.ComponentKind.PointOfInterest
            && !string.IsNullOrWhiteSpace(_selectedComponentId)
            && _nature.PointsOfInterest.FirstOrDefault(point =>
                string.Equals(point.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase))
                is { } landmark)
        {
            return landmark.Position;
        }

        if (_selectedComponentKind is TerrainComponentsPanel.ComponentKind.Entity
            && !string.IsNullOrWhiteSpace(_selectedComponentId)
            && _nature.PlacedEntities.FirstOrDefault(placed =>
                string.Equals(placed.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase))
                is { } entity)
        {
            return entity.Position;
        }

        return _cursorValid ? _cursorWorld : null;
    }

    private void WireComponentsPanel()
    {
        _componentsPanel.AddRequested += (_, kind) => OnComponentAddRequested(kind);
        _componentsPanel.ObjectTypeRequested += (_, type) => OnEntityCreateRequested(this, type);
        _componentsPanel.AssetActivated += (_, selection) => ArmLibraryAsset(selection.Id);
        _componentsPanel.EditRequested += (_, selection) => OnComponentEditRequested(selection);
        _componentsPanel.DuplicateRequested += (_, selection) => DuplicateComponent(selection);
        _componentsPanel.DeleteRequested += (_, selection) => DeleteComponent(selection);
        _componentsPanel.AffectsChanged += (_, _) => UpdateStatus();
        _componentsPanel.CategorySelected += (_, category) => { if (category == TerrainAffects.Paint) { _selectedComponentKind = null; _selectedComponentId = null; RebuildSelectionInspector(); } SetMode(category switch
        {
            TerrainAffects.Paint => TerrainEditorMode.Select,
            TerrainAffects.Paths => TerrainEditorMode.Paths,
            TerrainAffects.Water => TerrainEditorMode.Water,
            TerrainAffects.Foliage => TerrainEditorMode.Foliage,
            TerrainAffects.POI => TerrainEditorMode.Environment,
            _ => TerrainEditorMode.Entities,
        }); };
        _componentsPanel.SelectionChanged += (_, selection) =>
        {
            _selectedComponentKind = selection.Kind;
            _selectedComponentId = selection.Id;
            TerrainAffects lockTo = TerrainAffectsStrip.ForKind(selection.Kind);
            if (selection.Kind == TerrainComponentsPanel.ComponentKind.Entity
                && SelectedPlacedEntity() is { } placed)
            {
                TerrainEntityDocument? document = TryLoadEntityDocument(ResolveEntityFullPath(placed.Entity));
                if (document is not null)
                {
                    lockTo = TerrainAffectsStrip.ForEntityType(document.Type);
                }
            }

            _componentsPanel.Affects.Value = lockTo;
            RefreshActiveToolCard();
            RebuildSelectionInspector();
            NotifyInspectorStateChanged();
            UpdateStatus();
            _viewport.Invalidate();
        };
    }

    private bool ShouldDrawNatureComponent(TerrainComponentsPanel.ComponentKind kind, string? id = null)
    {
        if (!_isolateSelectedComponent || _selectedComponentKind is null)
        {
            return true;
        }

        if (_selectedComponentKind != kind)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_selectedComponentId))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(id)
            || string.Equals(id, _selectedComponentId, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshComponentsPanel()
    {
        List<(string Id, string Label)> layers = [];
        for (int i = 0; i < _settings.Layers.Count; i++)
        {
            TerrainLayerDocument layer = _settings.Layers[i];
            layers.Add((i.ToString(), layer.Name));
        }

        List<(string Id, string Label)> paths = _nature.Paths
            .Select(path => (path.Id, $"{path.Name}  ·  {path.Kind}"))
            .ToList();

        List<(string Id, string Label)> water = _nature.WaterBodies
            .Select(body => (body.Id, $"{body.Name}  ·  {body.Kind}"))
            .ToList();

        string foliageSummary = _foliage.Instances.Count == 0
            ? "No foliage scattered"
            : $"{_foliage.Instances.Count:N0} instances";

        List<(string Id, string Label)> points = _nature.PointsOfInterest
            .Select(point => (point.Id, point.Name))
            .ToList();

        List<(string Id, string Label, TerrainEntityType Type)> entities = _nature.PlacedEntities
            .Select(placed =>
            {
                string name = ResourceDisplayName.Format(placed.Entity);
                if (string.IsNullOrWhiteSpace(name)) name = "Entity";
                TerrainEntityDocument? document = TryLoadEntityDocument(ResolveEntityFullPath(placed.Entity));
                if (!string.IsNullOrWhiteSpace(document?.Name)) name = document.Name;
                TerrainEntityType type = document?.Type ?? TerrainEntityType.Object;
                return (placed.Id, name, type);
            })
            .ToList();

        Dictionary<string, string> icons = new();
        foreach (string definition in _settings.Entities.Select(ResolveEntityFullPath)
            .Concat(Directory.Exists(ResourcePath + ".parts") ? Directory.EnumerateFiles(ResourcePath + ".parts", "*.terrainpart.json") : [])
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TerrainEntityDocument? document = TryLoadEntityDocument(definition);
            if (document is null || definition == _pendingEntityPath) continue;
            string relative = ResourceNames.Name(ProjectRoot, definition);
            entities.Add((relative, document.Name, document.Type));
        }
        foreach (var entry in entities)
        {
            string reference = _nature.PlacedEntities.FirstOrDefault(item => item.Id == entry.Id)?.Entity ?? entry.Id;
            string? icon = TryLoadEntityDocument(ResolveEntityFullPath(reference))?.Icon;
            if (string.IsNullOrWhiteSpace(icon)) continue;
            try
            {
                string raster = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(ProjectRoot, icon, 0);
                if (File.Exists(raster)) icons[entry.Id] = raster;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException) { }
        }
        _componentsPanel.SetIcons(icons);
        _componentsPanel.Rebuild(new TerrainComponentInventory(
            layers,
            paths,
            water,
            foliageSummary,
            points,
            entities));
        RefreshFoliageAssets();
        UpdateSectionCameraRange();
        RebuildSelectionInspector();
    }

    private void OnComponentAddRequested(TerrainComponentsPanel.ComponentKind kind)
    {
        switch (kind)
        {
            case TerrainComponentsPanel.ComponentKind.Layer:
                AddPaintLayer();
                break;
            case TerrainComponentsPanel.ComponentKind.Path:
                SetMode(TerrainEditorMode.Paths);
                OpenPathDialog();
                break;
            case TerrainComponentsPanel.ComponentKind.Water:
                SetMode(TerrainEditorMode.Water);
                _waterTool = WaterAuthoringTool.Spray;
                SyncToolbar();
                break;
            case TerrainComponentsPanel.ComponentKind.Foliage:
                SetMode(TerrainEditorMode.Foliage);
                OpenFoliageDialog();
                break;
            case TerrainComponentsPanel.ComponentKind.PointOfInterest:
                AddPointOfInterestAtCursor();
                break;
            case TerrainComponentsPanel.ComponentKind.Entity:
                SetMode(TerrainEditorMode.Entities);
                OnEntityCreateRequested(this, TerrainEntityType.Object);
                break;
        }

        RefreshComponentsPanel();
    }

    private void OnComponentEditRequested(TerrainComponentsPanel.ComponentSelection selection)
    {
        switch (selection.Kind)
        {
            case TerrainComponentsPanel.ComponentKind.Layer:
                if (int.TryParse(selection.Id, out int layerIndex)
                    && layerIndex >= 0
                    && layerIndex < _layerListBox.Items.Count)
                {
                    SetMode(TerrainEditorMode.Paint);
                    _layerListBox.SelectedIndex = layerIndex;
                    OpenLayerMaterial(layerIndex);
                }
                break;
            case TerrainComponentsPanel.ComponentKind.Path:
                SetMode(TerrainEditorMode.Paths);
                OpenPathDialog();
                break;
            case TerrainComponentsPanel.ComponentKind.Water:
                SetMode(TerrainEditorMode.Water);
                OpenWaterDialog(selection.Id);
                break;
            case TerrainComponentsPanel.ComponentKind.Foliage:
                SetMode(TerrainEditorMode.Foliage);
                OpenFoliageDialog();
                break;
            case TerrainComponentsPanel.ComponentKind.PointOfInterest:
                EditPointOfInterest(selection.Id);
                break;
            case TerrainComponentsPanel.ComponentKind.Entity:
                SetMode(TerrainEditorMode.Entities);
                TerrainPlacedEntity? placed = _nature.PlacedEntities.FirstOrDefault(item =>
                    string.Equals(item.Id, selection.Id, StringComparison.OrdinalIgnoreCase));
                string? entityPath = placed is null
                    ? ResolveEntityFullPath(selection.Id)
                    : ResolveEntityFullPath(placed.Entity);
                if (!string.IsNullOrWhiteSpace(entityPath))
                {
                    OnEntityEditRequested(this, entityPath);
                }
                break;
        }
    }

    private void AddPointOfInterestAtCursor()
    {
        float centerX = _terrain.OriginX + (_terrain.ResolutionX - 1) * _terrain.CellSize * 0.5f;
        float centerZ = _terrain.OriginZ + (_terrain.ResolutionZ - 1) * _terrain.CellSize * 0.5f;
        Vector3 position = _cursorValid
            ? _cursorWorld
            : new(centerX, _terrain.SampleHeight(centerX, centerZ), centerZ);
        TerrainPointOfInterest point = new()
        {
            Name = $"Landmark {_nature.PointsOfInterest.Count + 1}",
            Position = position,
        };
        List<TerrainPointOfInterest> before = ClonePoints(_nature.PointsOfInterest);
        List<TerrainPointOfInterest> after = ClonePoints(before);
        after.Add(point);
        ApplyPointsOfInterest(before, after, $"Add point '{point.Name}'");
        _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.PointOfInterest, point.Id);
    }

    private void EditPointOfInterest(string id)
    {
        TerrainPointOfInterest? point = _nature.PointsOfInterest.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (point is null) return;

        using Form dialog = new()
        {
            BackColor = EditorChrome.Surface,
            ClientSize = new Size(360, 170),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            Text = "Point of Interest",
        };
        TextBox name = new() { Location = new Point(96, 16), Width = 240 };
        name.Text = point.Name;
        EditorChrome.StyleField(name);
        NumericUpDown radius = TerrainPathDialog.DecimalNumber(1m, 1000m, (decimal)point.DiscoveryRadius);
        radius.Location = new Point(96, 52);
        radius.Width = 120;
        dialog.Controls.Add(new Label { AutoSize = true, ForeColor = EditorChrome.Muted, Location = new Point(16, 20), Text = "Name" });
        dialog.Controls.Add(new Label { AutoSize = true, ForeColor = EditorChrome.Muted, Location = new Point(16, 56), Text = "Discovery" });
        dialog.Controls.Add(name);
        dialog.Controls.Add(radius);
        Button ok = new() { DialogResult = DialogResult.OK, Location = new Point(256, 112), Text = "Apply" };
        Button cancel = new() { DialogResult = DialogResult.Cancel, Location = new Point(170, 112), Text = "Cancel" };
        EditorChrome.StyleField(ok);
        EditorChrome.StyleField(cancel);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        List<TerrainPointOfInterest> before = ClonePoints(_nature.PointsOfInterest);
        List<TerrainPointOfInterest> after = ClonePoints(before);
        TerrainPointOfInterest edited = after.First(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        edited.Name = string.IsNullOrWhiteSpace(name.Text) ? edited.Name : name.Text.Trim();
        edited.DiscoveryRadius = (float)radius.Value;
        ApplyPointsOfInterest(before, after, $"Edit point '{edited.Name}'");
    }

    private void ApplyPointsOfInterest(
        List<TerrainPointOfInterest> before,
        List<TerrainPointOfInterest> after,
        string label)
    {
        _nature.PointsOfInterest = ClonePoints(after);
        PushEdit(
            label,
            () => { _nature.PointsOfInterest = ClonePoints(after); RefreshComponentsPanel(); },
            () => { _nature.PointsOfInterest = ClonePoints(before); RefreshComponentsPanel(); });
        RefreshComponentsPanel();
        UpdateStatus();
    }

    private static List<TerrainPointOfInterest> ClonePoints(IEnumerable<TerrainPointOfInterest> points) => points
        .Select(point => new TerrainPointOfInterest
        {
            Id = point.Id,
            Name = point.Name,
            Category = point.Category,
            Position = point.Position,
            DiscoveryRadius = point.DiscoveryRadius,
        })
        .ToList();

    private FlowLayoutPanel BuildWaterAuthoringPage()
    {
        if (_waterKindCombo.Items.Count == 0)
        {
            _waterKindCombo.Items.Add(nameof(TerrainWaterKind.Water));
            _waterKindCombo.Items.Add(nameof(TerrainWaterKind.River));
            _waterKindCombo.Items.Add(nameof(TerrainWaterKind.Waterfall));
            _waterKindCombo.SelectedIndex = 0;
            EditorChrome.StyleField(_waterKindCombo);
            _waterKindCombo.SelectedIndexChanged += (_, _) =>
            {
                _pendingWaterKind = _waterKindCombo.SelectedItem?.ToString() switch
                {
                    nameof(TerrainWaterKind.Waterfall) => TerrainWaterKind.Waterfall,
                    nameof(TerrainWaterKind.River) => TerrainWaterKind.River,
                    _ => TerrainWaterKind.Water,
                };
                UpdateStatus();
            };
            _waterLevelBox.Width = 244;
            EditorChrome.StyleField(_waterLevelBox);
            _waterLevelBox.ValueChanged += (_, _) =>
            {
                if (_syncingWaterLevelBox) return;
                _waterLevel = (float)_waterLevelBox.Value;
                _waterLevelSet = true;
                UpdateStatus();
                _viewport.Invalidate();
            };
        }

        FlowLayoutPanel page = MakeContextPage();
        page.Controls.Add(_waterSummary);
        page.Controls.Add(MakeContextCaption("Kind"));
        page.Controls.Add(_waterKindCombo);
        page.Controls.Add(MakeContextCaption("Mask Tools"));
        foreach (WaterAuthoringTool tool in Enum.GetValues<WaterAuthoringTool>())
        {
            WaterAuthoringTool captured = tool;
            string label = tool switch
            {
                WaterAuthoringTool.Select => "Select",
                WaterAuthoringTool.Spray => "Spray",
                WaterAuthoringTool.Region => "Region",
                WaterAuthoringTool.Level => "Level",
                WaterAuthoringTool.River => "River Tool",
                _ => "Fill Basin",
            };
            string description = tool switch
            {
                WaterAuthoringTool.Select => "Select an existing water body from the component list or viewport",
                WaterAuthoringTool.Spray => "Click-drag circular dabs onto free terrain cells",
                WaterAuthoringTool.Region => "Drag a rectangle (or square) of free cells into the mask",
                WaterAuthoringTool.Level => "Click terrain to set the shared water level for Fill",
                WaterAuthoringTool.River => "Click terrain to add spline points; drag point, width, and depth handles",
                _ => "Click inside a closed valley or crater to trace its shoreline and create a lake",
            };
            Button button = MakeContextAction(label, description, () =>
            {
                _waterTool = captured;
                if (captured == WaterAuthoringTool.River)
                {
                    _pendingWaterKind = TerrainWaterKind.River;
                    _waterKindCombo.SelectedItem = nameof(TerrainWaterKind.River);
                }
                else if (captured == WaterAuthoringTool.Basin)
                {
                    _pendingWaterKind = TerrainWaterKind.Water;
                    _waterKindCombo.SelectedItem = nameof(TerrainWaterKind.Water);
                }
                SyncToolbar();
                UpdateStatus();
            });
            _waterToolButtons.Add(tool, button);
            page.Controls.Add(button);
        }

        page.Controls.Add(MakeSliderPanel("Spray radius", _waterSprayRadiusSlider));
        page.Controls.Add(_waterSquareRegionCheck);
        page.Controls.Add(MakeContextCaption("Level"));
        page.Controls.Add(_waterLevelBox);
        page.Controls.Add(MakeContextAction(
            "Invert",
            "Flip the mask inside the current region, or the painted footprint if no region is set",
            InvertWaterSelection));
        page.Controls.Add(MakeContextAction(
            "Fill",
            "Commit selected free cells as Water or a Waterfall at the current level",
            () => FillWaterSelection(_carveBasinCheck.Checked)));
        page.Controls.Add(MakeContextAction(
            "Clear Mask",
            "Discard the in-progress spray and region selection",
            ClearWaterMask));
        page.Controls.Add(_carveBasinCheck);
        AddRiverAuthoringControls(page);
        page.Controls.Add(MakeContextAction(
            "Configure Selected…",
            "Open the full water-body property sheet for the selected body",
            () => OpenWaterDialog(_selectedComponentId)));
        page.Controls.Add(MakeContextAction(
            "Legacy Water Dialog…",
            "Edit all water bodies in one dialog",
            () => OpenWaterDialog(null)));
        page.Controls.Add(MakeContextCaption("Playable Preview"));
        page.Controls.Add(MakeContextAction(
            "Drop into Water",
            "Drop a capsule into the selected or first water body on the live collider",
            () => DropPlayableIntoWater()));
        page.Controls.Add(MakeContextAction(
            "Pause Preview",
            "Pause the playable capsule simulation",
            () => _physicsPlaying = false));
        page.Controls.Add(MakeContextAction(
            "Reset Preview",
            "Remove the playable capsule from the viewport",
            ResetPlayablePreview));
        return page;
    }

    private ToolStripDropDownButton BuildWizardMenu()
    {
        ToolStripDropDownButton menu = new("Wizard")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Create terrain or a Terrain Entity",
        };
        menu.DropDownItems.Add(EditorDocumentMenuChrome.Item(
            "New Terrain…",
            "Create terrain from a preset",
            OpenCreationWizard));
        menu.DropDownItems.Add(EditorDocumentMenuChrome.Item(
            "New Terrain Entity…",
            "Create a reusable terrain dressing definition",
            () => OnEntityCreateRequested(this, TerrainEntityType.Object)));
        return menu;
    }

    private ToolStripDropDownButton BuildViewMenu() =>
        EditorViewMenuChrome.BuildViewMenu(
            "Grid, lighting, sky, and diagnostic overlays for the terrain viewport",
            session: _viewportSession,
            floorStyle: new EditorViewMenuChrome.FloorStyleBinding
            {
                Read = () => FloorStyle,
                Write = SetFloorStyle,
                Invalidate = () => _viewport.Host.Invalidate(),
            },
            wireframe: new EditorViewMenuChrome.ToggleBinding
            {
                Read = () => _wireframe,
                Write = value =>
                {
                    _wireframe = value;
                    _wireframeButton.Checked = value;
                },
                Invalidate = () => _viewport.Host.Invalidate(),
            },
            fog: new EditorViewMenuChrome.ToggleBinding
            {
                Read = () => _fogEnabled,
                Write = value =>
                {
                    _fogEnabled = value;
                    _fogButton.Checked = value;
                    MarkDirty();
                },
                Invalidate = () => _viewport.Host.Invalidate(),
            },
            isolateSelection: new EditorViewMenuChrome.ToggleBinding
            {
                Read = () => _isolateSelectedComponent,
                Write = value => _isolateSelectedComponent = value,
                Invalidate = () => _viewport.Invalidate(true),
            },
            extraItems:
            [
                EditorCameraMenuChrome.BuildSecondCameraViewMenu(_viewport),
                ColliderOverlayMenuItem(),
                WaterVolumeOverlayMenuItem(),
            ]);

    private ToolStripMenuItem ColliderOverlayMenuItem()
    {
        ToolStripMenuItem item = new("Collider overlay")
        {
            CheckOnClick = true,
            Checked = ShowColliderOverlay,
            ToolTipText = "Draw the live heightfield collider used by physics",
        };
        item.CheckedChanged += (_, _) =>
        {
            ShowColliderOverlay = item.Checked;
            _viewport.Invalidate();
        };
        return item;
    }

    private ToolStripMenuItem WaterVolumeOverlayMenuItem()
    {
        ToolStripMenuItem item = new("Water volume overlay")
        {
            CheckOnClick = true,
            Checked = ShowWaterVolumeOverlay,
            ToolTipText = "Draw physics water volume bounds and surface",
        };
        item.CheckedChanged += (_, _) =>
        {
            ShowWaterVolumeOverlay = item.Checked;
            _viewport.Invalidate();
        };
        return item;
    }

    public void SetFloorStyle(EditorFloorStyle style)
    {
        _viewportSession.FloorStyle = style;
        if (style == EditorFloorStyle.GridOnly)
        {
            _viewportSession.ShowGrid = true;
        }

        _viewport.FloorStyle = style;
        _viewport.Host.Invalidate();
    }

    private void DrawPointsOfInterestOverlay(IRenderController renderer)
    {
        foreach (TerrainPointOfInterest point in _nature.PointsOfInterest)
        {
            if (!ShouldDrawNatureComponent(TerrainComponentsPanel.ComponentKind.PointOfInterest, point.Id)) continue;

            Vector3 world = point.Position with { Y = _terrain.SampleHeight(point.Position.X, point.Position.Z) + 0.35f };
            Vector3 screen = _viewport.WorldToSurface(world);
            if (screen.Z is < 0f or > 1f) continue;

            bool selected = _selectedComponentKind == TerrainComponentsPanel.ComponentKind.PointOfInterest
                && string.Equals(_selectedComponentId, point.Id, StringComparison.OrdinalIgnoreCase);
            RenderColor color = selected
                ? new RenderColor(0.95f, 0.82f, 0.2f, 0.95f)
                : new RenderColor(0.55f, 0.85f, 1f, 0.85f);
            renderer.DrawLine(screen.X - 6f, screen.Y, screen.X + 6f, screen.Y, color, 2f, depth: -8997);
            renderer.DrawLine(screen.X, screen.Y - 6f, screen.X, screen.Y + 6f, color, 2f, depth: -8997);

            float radius = MathF.Max(8f, point.DiscoveryRadius);
            const int segments = 24;
            Vector3 previous = default;
            bool hasPrevious = false;
            for (int i = 0; i <= segments; i++)
            {
                float angle = i / (float)segments * MathF.Tau;
                Vector3 ring = new(
                    point.Position.X + MathF.Cos(angle) * radius,
                    _terrain.SampleHeight(point.Position.X + MathF.Cos(angle) * radius, point.Position.Z + MathF.Sin(angle) * radius) + 0.12f,
                    point.Position.Z + MathF.Sin(angle) * radius);
                Vector3 ringScreen = _viewport.WorldToSurface(ring);
                if (hasPrevious && previous.Z is >= 0f and <= 1f && ringScreen.Z is >= 0f and <= 1f)
                {
                    renderer.DrawLine(previous.X, previous.Y, ringScreen.X, ringScreen.Y, new RenderColor(color.R, color.G, color.B, 0.35f), 1f, depth: -8998);
                }

                previous = ringScreen;
                hasPrevious = true;
            }
        }
    }

    private void HandleWaterPointerDown(Point client)
    {
        if (!PickTerrain(client, out Vector3 world)) return;
        _cursorWorld = world;
        _cursorValid = true;
        if (_waterTool == WaterAuthoringTool.Select)
        {
            TryPickWaterAt(world.X, world.Z);
            return;
        }

        if (_waterTool == WaterAuthoringTool.Level)
        {
            SetWaterLevel(world.Y);
            return;
        }

        if (_waterTool == WaterAuthoringTool.River)
        {
            RiverPointerDown(client, world);
            return;
        }

        if (_waterTool == WaterAuthoringTool.Basin)
        {
            FillConformingBasin(world);
            return;
        }

        if (_waterTool == WaterAuthoringTool.Spray)
        {
            _waterSpraying = true;
            _viewport.NavigationEnabled = false;
            SprayWaterAt(world.X, world.Z);
            return;
        }

        _waterDragging = true;
        _waterDragStart = world;
        _waterDragEnd = world;
        _waterPreviewLevel = world.Y;
        _viewport.NavigationEnabled = false;
    }

    private void HandleWaterPointerMove(Point client)
    {
        _cursorValid = PickTerrain(client, out _cursorWorld);
        if (_waterTool == WaterAuthoringTool.River)
        {
            RiverPointerMove(client, _cursorValid ? _cursorWorld : null);
            UpdateStatus();
            _viewport.Invalidate();
            return;
        }
        if (_waterSpraying && _cursorValid)
        {
            SprayWaterAt(_cursorWorld.X, _cursorWorld.Z);
        }
        else if (_waterDragging && _cursorValid)
        {
            _waterDragEnd = _cursorWorld;
        }

        UpdateStatus();
        _viewport.Invalidate();
    }

    private void HandleWaterPointerUp()
    {
        if (_riverDraggingPoint >= 0)
        {
            EndRiverPointerDrag();
            return;
        }
        if (_waterSpraying)
        {
            _waterSpraying = false;
            _viewport.NavigationEnabled = true;
            UpdateStatus();
            _viewport.Invalidate();
            return;
        }

        if (!_waterDragging) return;
        _waterDragging = false;
        _viewport.NavigationEnabled = true;
        if (!_cursorValid) return;

        (Vector3 min, Vector3 max, float level) = NormalizedWaterRegion(_waterDragStart, _waterDragEnd, _waterPreviewLevel);
        PaintWaterRegion(min.X, min.Z, max.X, max.Z, setLevel: !_waterLevelSet, level);
        UpdateStatus();
        _viewport.Invalidate();
    }

    public void SprayWaterAt(float worldX, float worldZ)
    {
        EnsureWaterMask();
        SampleWaterLevelFrom(worldX, worldZ);
        float radius = MathF.Max(1f, _waterSprayRadiusSlider.Value);
        float radiusSq = radius * radius;
        int cellRadius = Math.Max(1, (int)MathF.Ceiling(radius / _terrain.CellSize));
        int cx = WorldToCellX(worldX);
        int cz = WorldToCellZ(worldZ);
        for (int z = cz - cellRadius; z <= cz + cellRadius; z++)
        for (int x = cx - cellRadius; x <= cx + cellRadius; x++)
        {
            if (!InTerrainCell(x, z)) continue;
            float wx = CellWorldX(x);
            float wz = CellWorldZ(z);
            float dx = wx - worldX;
            float dz = wz - worldZ;
            if (dx * dx + dz * dz > radiusSq) continue;
            TryMarkWaterCell(x, z, true);
        }
    }

    public void PaintWaterRegion(float minX, float minZ, float maxX, float maxZ, bool setLevel = false, float level = 0f)
    {
        EnsureWaterMask();
        float left = MathF.Min(minX, maxX);
        float right = MathF.Max(minX, maxX);
        float near = MathF.Min(minZ, maxZ);
        float far = MathF.Max(minZ, maxZ);
        if (_waterSquareRegionCheck.Checked)
        {
            float size = MathF.Max(right - left, far - near);
            right = left + size;
            far = near + size;
        }

        int x0 = WorldToCellX(left);
        int x1 = WorldToCellX(right);
        int z0 = WorldToCellZ(near);
        int z1 = WorldToCellZ(far);
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (z1 < z0) (z0, z1) = (z1, z0);
        if (x1 - x0 < 1 && z1 - z0 < 1)
        {
            return;
        }

        _waterRegionActive = true;
        _waterRegionMinX = x0;
        _waterRegionMaxX = x1;
        _waterRegionMinZ = z0;
        _waterRegionMaxZ = z1;
        if (setLevel)
        {
            SetWaterLevel(level);
        }
        else
        {
            SampleWaterLevelFrom((left + right) * 0.5f, (near + far) * 0.5f);
        }

        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
            TryMarkWaterCell(x, z, true);
    }

    public void InvertWaterSelection()
    {
        EnsureWaterMask();
        if (!TryGetInvertBounds(out int x0, out int x1, out int z0, out int z1))
        {
            _statusLabel.Text = "Invert needs a region or a sprayed footprint.";
            return;
        }

        _waterRegionActive = true;
        _waterRegionMinX = x0;
        _waterRegionMaxX = x1;
        _waterRegionMinZ = z0;
        _waterRegionMaxZ = z1;
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            int i = z * _terrain.ResolutionX + x;
            TryMarkWaterCell(x, z, !_waterMask![i]);
        }

        UpdateStatus();
        _viewport.Invalidate();
    }

    public void SetWaterLevel(float level)
    {
        _waterLevel = level;
        _waterLevelSet = true;
        _syncingWaterLevelBox = true;
        _waterLevelBox.Value = Math.Clamp((decimal)level, _waterLevelBox.Minimum, _waterLevelBox.Maximum);
        _syncingWaterLevelBox = false;
        UpdateStatus();
        _viewport.Invalidate();
    }

    public void ClearWaterMask()
    {
        if (_waterMask is { Length: > 0 })
            Array.Clear(_waterMask);
        _waterRegionActive = false;
        _waterSpraying = false;
        _waterDragging = false;
        UpdateStatus();
        _viewport.Invalidate();
    }

    /// <summary>
    /// Fill-Basin helper used by the walkthrough: sculpt a bowl, paint the ellipse mask, then Fill.
    /// Carve stays opt-in in the UI; this helper still sculpts so the test has a basin.
    /// </summary>
    public TerrainWaterDefinition FillBasinPond(float worldX, float worldZ, float sizeX, float sizeZ, float physicsDepth = 4f)
    {
        SetMode(TerrainEditorMode.Water);
        _pendingWaterKind = TerrainWaterKind.Water;
        if (_waterKindCombo.Items.Count > 0)
            _waterKindCombo.SelectedItem = nameof(TerrainWaterKind.Water);
        EnsureWaterMask();
        ClearWaterMask();
        float level = _terrain.SampleHeight(worldX, worldZ);
        SetWaterLevel(level);
        PaintEllipseMask(worldX, worldZ, sizeX, sizeZ);
        return FillWaterSelection(carveBasin: true, physicsDepth)
            ?? throw new InvalidOperationException("Fill Basin could not commit a water body.");
    }

    public TerrainWaterDefinition? FillWaterSelection(bool carveBasin, float physicsDepth = 0f)
    {
        EnsureWaterMask();
        int width = _terrain.ResolutionX;
        int height = _terrain.ResolutionZ;
        var accepted = new bool[width * height];
        int count = 0;
        float minGround = float.MaxValue;
        float maxGround = float.MinValue;
        int minX = width;
        int minZ = height;
        int maxX = -1;
        int maxZ = -1;
        bool waterfall = _pendingWaterKind == TerrainWaterKind.Waterfall;
        float level = _waterLevelSet ? _waterLevel : _cursorWorld.Y;
        for (int z = 0; z < height; z++)
        for (int x = 0; x < width; x++)
        {
            if (!_waterMask![z * width + x]) continue;
            float wx = CellWorldX(x);
            float wz = CellWorldZ(z);
            if (WaterCellOccupied(wx, wz)) continue;
            float ground = _terrain.GetHeight(x, z);
            if (!waterfall && ground > level + 0.15f) continue;
            accepted[z * width + x] = true;
            count++;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
            if (ground < minGround) minGround = ground;
            if (ground > maxGround) maxGround = ground;
        }

        if (count == 0)
        {
            _statusLabel.Text = "Fill needs free selected cells.";
            return null;
        }

        float sizeX = MathF.Max(_terrain.CellSize * 2f, (maxX - minX + 1) * _terrain.CellSize);
        float sizeZ = MathF.Max(_terrain.CellSize * 2f, (maxZ - minZ + 1) * _terrain.CellSize);
        float centerX = (CellWorldX(minX) + CellWorldX(maxX)) * 0.5f;
        float centerZ = (CellWorldZ(minZ) + CellWorldZ(maxZ)) * 0.5f;
        if (!waterfall)
            level = _waterLevelSet ? _waterLevel : maxGround;
        float depth = physicsDepth > 0.05f
            ? physicsDepth
            : MathF.Max(0.5f, (waterfall ? maxGround : level) - minGround);
        TerrainWaterDefinition water = new()
        {
            Name = waterfall
                ? $"Waterfall {_nature.WaterBodies.Count + 1}"
                : _waterTool == WaterAuthoringTool.Basin
                    ? $"Basin {_nature.WaterBodies.Count + 1}"
                    : $"Water {_nature.WaterBodies.Count + 1}",
            Kind = waterfall ? TerrainWaterKind.Waterfall : TerrainWaterKind.Water,
            Center = new Vector3(centerX, waterfall ? maxGround : level, centerZ),
            SizeX = sizeX,
            SizeZ = sizeZ,
            SurfaceHeight = waterfall ? maxGround : level,
            PhysicsDepth = Math.Clamp(depth, 0.5f, 10000f),
            SimulationEnabled = !waterfall,
            PhysicsMode = WaterPhysicsMode.None,
            FluidDensity = 1000f,
            BuoyancyStrength = 1.1f,
            LinearDrag = 2.8f,
            AngularDrag = 1.2f,
        };
        water.CaptureFootprint(_terrain.OriginX, _terrain.OriginZ, _terrain.CellSize, width, height, accepted);
        if (waterfall)
            water.Waterfall = BuildWaterfallFromBounds(centerX, centerZ, sizeX, sizeZ, maxGround, minGround);

        CommitAuthoredWater(water, carveBasin);
        ClearWaterMask();
        return _nature.WaterBodies.First(body =>
            string.Equals(body.Id, water.Id, StringComparison.OrdinalIgnoreCase));
    }

    private void CommitAuthoredWater(TerrainWaterDefinition water, bool carveBasin)
    {
        ArgumentNullException.ThrowIfNull(water);
        water.Normalize();
        ushort[] heightsBefore = (ushort[])_terrain.HeightsData.Clone();
        byte[] splatBefore = (byte[])_terrain.SplatmapData.Clone();
        List<TerrainWaterDefinition> waterBefore = Clone(_nature.WaterBodies);
        FoliageField foliageBefore = _foliage;
        Dictionary<string, HeldFoliageInstance[]> holdBefore = CloneHold(_foliageHeldByWater);
        if (carveBasin && water.Kind == TerrainWaterKind.River && water.RiverPoints.Count >= 2)
        {
            TerrainRiverSystem.CarveRiverbed(_terrain, water.RiverPoints);
            _meshDirty = true;
        }
        else if (carveBasin)
        {
            TerrainBasinStamp stamp = new(
                new Vector2(water.Center.X, water.Center.Z),
                water.SurfaceHeight,
                MathF.Max(MathF.Min(water.SizeX, water.SizeZ) * 0.5f, _terrain.CellSize * 2f),
                MathF.Max(0.5f, water.PhysicsDepth),
                MathF.Max(_terrain.CellSize * 3f, MathF.Min(water.SizeX, water.SizeZ) * 0.18f));
            stamp.ApplyTo(_terrain);
            _meshDirty = true;
        }

        List<TerrainWaterDefinition> waterAfter = Clone(waterBefore);
        waterAfter.Add(water.Clone());
        ushort[] heightsAfter = (ushort[])_terrain.HeightsData.Clone();
        byte[] splatAfter = (byte[])_terrain.SplatmapData.Clone();
        _nature.WaterBodies = Clone(waterAfter);
        Dictionary<string, HeldFoliageInstance[]> holdAfter = CloneHold(_foliageHeldByWater);
        FoliageField foliageAfter = FoliageScatter.ReconcileWithWater(foliageBefore, waterBefore, waterAfter, holdAfter);
        _foliage = foliageAfter;
        _foliageHeldByWater = CloneHold(holdAfter);
        _natureMeshDirty = true;
        ResetFoliagePreview();
        PushEdit(
            $"Add water '{water.Name}'",
            () =>
            {
                _terrain.RestoreState(heightsAfter, splatAfter);
                _nature.WaterBodies = Clone(waterAfter);
                _foliage = foliageAfter;
                _foliageHeldByWater = CloneHold(holdAfter);
                _meshDirty = true;
                _natureMeshDirty = true;
                ResetFoliagePreview();
                RefreshComponentsPanel();
                UpdateStatus();
                _viewport.Invalidate(true);
            },
            () =>
            {
                _terrain.RestoreState(heightsBefore, splatBefore);
                _nature.WaterBodies = Clone(waterBefore);
                _foliage = foliageBefore;
                _foliageHeldByWater = CloneHold(holdBefore);
                _meshDirty = true;
                _natureMeshDirty = true;
                ResetFoliagePreview();
                RefreshComponentsPanel();
                UpdateStatus();
                _viewport.Invalidate(true);
            });
        RefreshComponentsPanel();
        _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Water, water.Id);
        UpdateStatus();
        _viewport.Invalidate();
    }

    private (Vector3 Min, Vector3 Max, float Level) NormalizedWaterRegion(Vector3 a, Vector3 b, float level)
    {
        float minX = MathF.Min(a.X, b.X);
        float maxX = MathF.Max(a.X, b.X);
        float minZ = MathF.Min(a.Z, b.Z);
        float maxZ = MathF.Max(a.Z, b.Z);
        float surface = _waterLevelSet ? _waterLevel : MathF.Min(a.Y, b.Y);
        if (_waterTool == WaterAuthoringTool.Region)
        {
            surface = level;
        }

        return (new Vector3(minX, surface, minZ), new Vector3(maxX, surface, maxZ), surface);
    }

    private void DrawWaterPreview(IRenderController renderer)
    {
        if (ActiveMode != TerrainEditorMode.Water && !_waterDragging && !_waterSpraying && _riverDraft.Count == 0) return;

        DrawRiverPreview(renderer);

        if (_waterDragging)
        {
            (Vector3 min, Vector3 max, float level) = NormalizedWaterRegion(_waterDragStart, _waterDragEnd, _waterPreviewLevel);
            if (_waterSquareRegionCheck.Checked)
            {
                float size = MathF.Max(max.X - min.X, max.Z - min.Z);
                max = new Vector3(min.X + size, max.Y, min.Z + size);
            }

            DrawRegionOutline(renderer, min, max, new RenderColor(0.2f, 0.65f, 1f, 0.95f), 2.5f);
            if (_pendingWaterKind != TerrainWaterKind.Waterfall)
            {
                DrawLevelPlane(renderer, min, max, _waterLevelSet ? _waterLevel : level, new RenderColor(0.2f, 0.65f, 1f, 0.22f));
            }
        }

        DrawWaterMaskPreview(renderer);

        if (_selectedComponentKind == TerrainComponentsPanel.ComponentKind.Water
            && !string.IsNullOrWhiteSpace(_selectedComponentId))
        {
            TerrainWaterDefinition? selected = _nature.WaterBodies.FirstOrDefault(body =>
                string.Equals(body.Id, _selectedComponentId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                Vector3 min = new(
                    selected.Center.X - selected.SizeX * 0.5f,
                    selected.SurfaceHeight,
                    selected.Center.Z - selected.SizeZ * 0.5f);
                Vector3 max = new(
                    selected.Center.X + selected.SizeX * 0.5f,
                    selected.SurfaceHeight,
                    selected.Center.Z + selected.SizeZ * 0.5f);
                DrawRegionOutline(renderer, min, max, new RenderColor(0.95f, 0.82f, 0.2f, 0.9f), 1.75f);
            }
        }
    }

    private void DrawWaterMaskPreview(IRenderController renderer)
    {
        if (_waterMask is null || _waterMask.Length != _terrain.ResolutionX * _terrain.ResolutionZ)
            return;

        float y = (_waterLevelSet ? _waterLevel : _cursorWorld.Y) + 0.08f;
        int width = _terrain.ResolutionX;
        int drawn = 0;
        RenderColor fill = new(0.25f, 0.72f, 1f, 0.85f);
        for (int z = 0; z < _terrain.ResolutionZ; z++)
        for (int x = 0; x < width; x++)
        {
            if (!_waterMask[z * width + x]) continue;
            if (drawn++ > 2200) return;
            Vector3 world = new(CellWorldX(x), y, CellWorldZ(z));
            Vector3 screen = _viewport.WorldToSurface(world);
            if (screen.Z is < 0f or > 1f) continue;
            renderer.DrawLine(screen.X - 2f, screen.Y, screen.X + 2f, screen.Y, fill, 1.5f, depth: -8997);
            renderer.DrawLine(screen.X, screen.Y - 2f, screen.X, screen.Y + 2f, fill, 1.5f, depth: -8997);
        }
    }

    private void DrawRegionOutline(IRenderController renderer, Vector3 min, Vector3 max, RenderColor color, float thickness)
    {
        Span<Vector3> corners =
        [
            new(min.X, _terrain.SampleHeight(min.X, min.Z) + 0.12f, min.Z),
            new(max.X, _terrain.SampleHeight(max.X, min.Z) + 0.12f, min.Z),
            new(max.X, _terrain.SampleHeight(max.X, max.Z) + 0.12f, max.Z),
            new(min.X, _terrain.SampleHeight(min.X, max.Z) + 0.12f, max.Z),
        ];
        for (int i = 0; i < 4; i++)
        {
            Vector3 a = _viewport.WorldToSurface(corners[i]);
            Vector3 b = _viewport.WorldToSurface(corners[(i + 1) % 4]);
            if (a.Z is > 0f and < 1f && b.Z is > 0f and < 1f)
            {
                renderer.DrawLine(a.X, a.Y, b.X, b.Y, color, thickness, depth: -8998);
            }
        }
    }

    private void DrawLevelPlane(IRenderController renderer, Vector3 min, Vector3 max, float level, RenderColor fill)
    {
        const int segments = 6;
        float stepX = (max.X - min.X) / segments;
        float stepZ = (max.Z - min.Z) / segments;
        for (int z = 0; z < segments; z++)
        for (int x = 0; x < segments; x++)
        {
            float x0 = min.X + x * stepX;
            float x1 = min.X + (x + 1) * stepX;
            float z0 = min.Z + z * stepZ;
            float z1 = min.Z + (z + 1) * stepZ;
            Vector3 a = _viewport.WorldToSurface(new Vector3(x0, level + 0.08f, z0));
            Vector3 b = _viewport.WorldToSurface(new Vector3(x1, level + 0.08f, z0));
            Vector3 c = _viewport.WorldToSurface(new Vector3(x1, level + 0.08f, z1));
            Vector3 d = _viewport.WorldToSurface(new Vector3(x0, level + 0.08f, z1));
            if (a.Z is > 0f and < 1f && c.Z is > 0f and < 1f)
            {
                renderer.DrawLine(a.X, a.Y, c.X, c.Y, fill, 1f, depth: -8999);
            }

            if (b.Z is > 0f and < 1f && d.Z is > 0f and < 1f)
            {
                renderer.DrawLine(b.X, b.Y, d.X, d.Y, fill, 1f, depth: -8999);
            }
        }
    }

    private void EnsureWaterMask()
    {
        int length = _terrain.ResolutionX * _terrain.ResolutionZ;
        if (_waterMask is { Length: var existing } && existing == length)
            return;
        _waterMask = new bool[length];
        _waterRegionActive = false;
    }

    private void PaintEllipseMask(float worldX, float worldZ, float sizeX, float sizeZ)
    {
        EnsureWaterMask();
        float hx = MathF.Max(sizeX * 0.5f, _terrain.CellSize);
        float hz = MathF.Max(sizeZ * 0.5f, _terrain.CellSize);
        int x0 = WorldToCellX(worldX - hx);
        int x1 = WorldToCellX(worldX + hx);
        int z0 = WorldToCellZ(worldZ - hz);
        int z1 = WorldToCellZ(worldZ + hz);
        for (int z = z0; z <= z1; z++)
        for (int x = x0; x <= x1; x++)
        {
            float dx = (CellWorldX(x) - worldX) / hx;
            float dz = (CellWorldZ(z) - worldZ) / hz;
            if (dx * dx + dz * dz <= 1f)
                TryMarkWaterCell(x, z, true, respectOccupancy: false);
        }
    }

    private bool TryMarkWaterCell(int x, int z, bool value, bool respectOccupancy = true)
    {
        if (!InTerrainCell(x, z) || _waterMask is null) return false;
        int i = z * _terrain.ResolutionX + x;
        if (_waterMask[i] == value) return false;
        if (value && respectOccupancy && WaterCellOccupied(CellWorldX(x), CellWorldZ(z)))
            return false;
        _waterMask[i] = value;
        return true;
    }

    private bool WaterCellOccupied(float worldX, float worldZ)
    {
        if (TerrainWaterDefinition.ContainsAny(_nature.WaterBodies, worldX, worldZ))
            return true;
        foreach (TerrainPlacedEntity placed in _nature.PlacedEntities)
        {
            float radius = MathF.Max(0.8f, placed.Scale * 1.2f);
            float dx = worldX - placed.Position.X;
            float dz = worldZ - placed.Position.Z;
            if (dx * dx + dz * dz <= radius * radius)
                return true;
        }

        return false;
    }

    private void TryPickWaterAt(float worldX, float worldZ)
    {
        TerrainWaterDefinition? hit = _nature.WaterBodies.FirstOrDefault(body =>
            body.ContainsHorizontal(worldX, worldZ));
        if (hit is null) return;
        _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Water, hit.Id);
    }

    private void SampleWaterLevelFrom(float worldX, float worldZ)
    {
        if (_waterLevelSet) return;
        SetWaterLevel(_terrain.SampleHeight(worldX, worldZ));
    }

    private bool TryGetInvertBounds(out int x0, out int x1, out int z0, out int z1)
    {
        if (_waterRegionActive)
        {
            x0 = _waterRegionMinX;
            x1 = _waterRegionMaxX;
            z0 = _waterRegionMinZ;
            z1 = _waterRegionMaxZ;
            return true;
        }

        x0 = _terrain.ResolutionX;
        x1 = -1;
        z0 = _terrain.ResolutionZ;
        z1 = -1;
        if (_waterMask is null) return false;
        int width = _terrain.ResolutionX;
        for (int z = 0; z < _terrain.ResolutionZ; z++)
        for (int x = 0; x < width; x++)
        {
            if (!_waterMask[z * width + x]) continue;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (z < z0) z0 = z;
            if (z > z1) z1 = z;
        }

        return x1 >= x0;
    }

    private bool InTerrainCell(int x, int z) =>
        (uint)x < (uint)_terrain.ResolutionX && (uint)z < (uint)_terrain.ResolutionZ;

    private int WorldToCellX(float worldX) =>
        Math.Clamp((int)MathF.Floor((worldX - _terrain.OriginX) / _terrain.CellSize), 0, _terrain.ResolutionX - 1);

    private int WorldToCellZ(float worldZ) =>
        Math.Clamp((int)MathF.Floor((worldZ - _terrain.OriginZ) / _terrain.CellSize), 0, _terrain.ResolutionZ - 1);

    private float CellWorldX(int x) => _terrain.OriginX + x * _terrain.CellSize;
    private float CellWorldZ(int z) => _terrain.OriginZ + z * _terrain.CellSize;

    private static WaterfallParams BuildWaterfallFromBounds(
        float centerX,
        float centerZ,
        float sizeX,
        float sizeZ,
        float topY,
        float bottomY)
    {
        float height = MathF.Max(0.5f, topY - bottomY);
        float width = MathF.Max(0.5f, MathF.Min(sizeX, sizeZ));
        float yaw = sizeX >= sizeZ ? 0f : MathF.PI * 0.5f;
        float c = MathF.Cos(yaw);
        float s = MathF.Sin(yaw);
        var right = new Vector3(c, 0f, -s);
        float half = width * 0.5f;
        var topCenter = new Vector3(centerX, topY, centerZ);
        var topL = topCenter - right * half;
        var topR = topCenter + right * half;
        return new WaterfallParams
        {
            TopLeft = topL,
            TopRight = topR,
            BottomLeft = topL - Vector3.UnitY * height,
            BottomRight = topR - Vector3.UnitY * height,
            VerticalSegments = 12,
        };
    }

    private void OpenWaterDialog(string? selectedId)
    {
        float centerX = _terrain.OriginX + (_terrain.ResolutionX - 1) * _terrain.CellSize * 0.5f;
        float centerZ = _terrain.OriginZ + (_terrain.ResolutionZ - 1) * _terrain.CellSize * 0.5f;
        Vector3 center = _cursorValid
            ? _cursorWorld
            : new(centerX, _terrain.SampleHeight(centerX, centerZ) + 0.35f, centerZ);
        using TerrainWaterDialog dialog = new(_nature.WaterBodies, center);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetWaterBodies(dialog.WaterBodies);
        RefreshComponentsPanel();
        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            _componentsPanel.Select(TerrainComponentsPanel.ComponentKind.Water, selectedId);
        }
    }
}
