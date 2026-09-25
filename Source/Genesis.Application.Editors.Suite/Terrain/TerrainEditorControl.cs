using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Assets;
using Genesis.World;
using Genesis.World.Foliage;
using Genesis.World.Navigation;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Rendering;
using Genesis.World.Terrain;
using Genesis.World.Water;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Terrain Editor (Plan Phase 5): heightmap sculpting (raise/lower/smooth/flatten) and
/// four-channel splat painting over a streamed-style chunk mesh, previewed with the full
/// editor lighting rig — sun shadows, hemisphere ambient, wrapped diffuse, specular, rim,
/// and aerial haze. Saves <c>.terrain.json</c> settings plus a binary <c>.gterrain</c>
/// sidecar the runtime's <c>RoomTerrainSubsystem</c> loads directly.
/// </summary>
public sealed partial class TerrainEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    public enum TerrainBrush
    {
        Raise,
        Lower,
        Smooth,
        Flatten,
        Paint,
        Erase,
        InFill,
    }

    public enum TerrainEditorMode
    {
        Generate,
        Sculpt,
        Paint,
        Paths,
        Foliage,
        Water,
        Environment,
        Entities,
        Select,
    }

    private sealed class TerrainSettingsDocument
    {
        public int SchemaVersion { get; set; } = 2;
        public int[] Resolution { get; set; } = [129, 129];
        public float CellSize { get; set; } = 1f;
        public float MinHeight { get; set; } = -24f;
        public float MaxHeight { get; set; } = 72f;
        public string SeedProfile { get; set; } = "RollingHills";
        public string Preset { get; set; } = "RollingHills";
        public int Seed { get; set; } = 1337;
        public int ErosionIterations { get; set; }
        public float ErosionStrength { get; set; } = 0.35f;
        public float TerraceStrength { get; set; }
        public int TerraceSteps { get; set; } = 12;
        public int RiverCount { get; set; }
        public float RiverDepth { get; set; } = 8f;
        public bool FogEnabled { get; set; }
        public FaceCullingOverride Culling { get; set; } = FaceCullingOverride.Default;
        public FrontFaceWindingOverride WindingOrder { get; set; } = FrontFaceWindingOverride.Default;
        public List<TerrainLayerDocument> Layers { get; set; } = [];
        public List<string> Entities { get; set; } = [];
        public Dictionary<string, string> ComponentShaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class TerrainLayerDocument
    {
        public string Name { get; set; } = "Layer";
        public float[] Color { get; set; } = [0.4f, 0.5f, 0.3f];
        public string Image { get; set; } = "";
        public float Tiling { get; set; } = 8f;
        public string Addressing { get; set; } = "Repeat";
        public int Resolution { get; set; } = 2048;
    }

    private readonly TerrainSettingsDocument _settings;
    private TerrainAsset _terrain;
    private ushort[] _eraseBaseline = [];
    private TerrainNatureDocument _nature;
    private TerrainPathNetwork _pathNetwork;
    private FoliageField _foliage;
    private Dictionary<string, HeldFoliageInstance[]> _foliageHeldByWater = new(StringComparer.OrdinalIgnoreCase);
    private readonly EditorViewport3D _viewport;
    private readonly ListBox _layerListBox;
    private readonly Label _statusLabel;
    private readonly TrackBar _radiusSlider;
    private readonly TrackBar _strengthSlider;
    private readonly TrackBar _foliageRadiusSlider;
    private readonly TrackBar _foliageDensitySlider;
    private readonly ThemedComboBox _foliageSpeciesCombo;
    private readonly Dictionary<TerrainBrush, Button> _brushButtons = [];
    private readonly Dictionary<FoliageBrushMode, Button> _foliageToolButtons = [];
    private readonly Dictionary<TerrainEditorMode, ToolStripButton> _modeButtons = [];
    private readonly EditorModeHost _modeHost;
    private readonly Label _contextHeader;
    private readonly Label _generateSummary;
    private readonly Label _pathsSummary;
    private readonly Label _foliageSummary;
    private readonly Label _waterSummary;
    private readonly Label _environmentSummary;
    private readonly ToolStripButton _wireframeButton;
    private readonly ToolStripButton _fogButton;
    private TerrainEntityListPanel _entityListPanel = null!;

    private readonly Editor3DSession _viewportSession = new();
    private readonly TerrainSelectionInspector _selectionInspector = new();
    private readonly Dictionary<IRenderController, AuthoredTerrainGround> _terrainGrounds = [];
    private readonly Dictionary<IRenderController, TextureHandle> _terrainAlbedos = [];
    private readonly Dictionary<IRenderController, int> _terrainGroundRevision = [];
    private MeshDrawCall[] _terrainGroundDraws = new MeshDrawCall[32];
    private string? _terrainAlbedoRasterPath;
    private bool _terrainAlbedoResolved;
    private int _groundRevision = 1;
    private bool _meshDirty = true;
    private bool _natureMeshDirty = true;
    private IRenderController? _natureRenderer;
    private readonly List<MeshHandle> _pathMeshes = [];
    private readonly Dictionary<(FoliageSpecies Species, bool Near), MeshHandle> _foliageMeshes = [];
    private FoliageStreamingPlanner? _foliagePlanner;
    private MeshInstanceData[] _foliageInstanceBuffer = [];
    private WaterMeshCache? _waterMeshCache;
    private bool _wireframe;
    private bool _fogEnabled;
    private bool _stroking;
    private bool _foliageStroking;
    private ushort[]? _strokeHeightsBefore;
    private byte[]? _strokeSplatBefore;
    private FoliageField? _strokeFoliageBefore;
    private int _strokeFoliageAdded;
    private int _strokeFoliageRemoved;
    private readonly FoliageBrushSettings _foliageBrush = new();
    private float _flattenTarget;
    private Vector3 _cursorWorld;
    private bool _cursorValid;

    public TerrainEditorControl(string resourcePath, string projectRoot)
        : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        _settings = LoadJsonOrDefault(CreateDefaultSettings);
        if (_settings.Layers.Count == 0)
        {
            _settings.Layers = CreateDefaultSettings().Layers;
        }

        _settings.ComponentShaders = new Dictionary<string, string>(
            _settings.ComponentShaders ?? [],
            StringComparer.OrdinalIgnoreCase);
        _settings.Entities ??= [];

        _fogEnabled = _settings.FogEnabled;
        _terrain = LoadTerrainData();
        _eraseBaseline = (ushort[])_terrain.HeightsData.Clone();
        _nature = LoadNatureData();
        _pathNetwork = new TerrainPathNetwork(_nature.Paths);
        _foliage = LoadFoliageData();
        _foliagePlanner = new FoliageStreamingPlanner(_foliage, _nature.FoliageSettings.StreamingCellSize);

        // ── Responsive command bar ───────────────────────────────────────────────
        EditorCommandBar toolbar = EditorChrome.MakeToolbar();
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildFileMenu(this,
        [
            EditorDocumentMenuChrome.Item("New…", "Create terrain from a preset", OpenCreationWizard),
            EditorDocumentMenuChrome.Item("Export World Map…", "Bake the current world map", ExportWorldMap),
        ]));
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildEditMenu(this));
        ToolStripMenuItem process = new("Process")
        {
            ToolTipText = "Apply deterministic geological processes",
        };
        process.DropDownItems.Add("Erode", null, (_, _) => ApplyErosion(8, 0.35f));
        process.DropDownItems.Add("Terrace", null, (_, _) => ApplyTerracing(0.6f, 12));
        process.DropDownItems.Add("Carve River", null, (_, _) => CarveRivers(1, 8f));
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildToolsMenu(
            "Regenerate the heightfield or apply geological processes",
            [
                EditorDocumentMenuChrome.Item("Regenerate", "Regenerate from the current preset and seed", RegenerateFromSeed),
                EditorDocumentMenuChrome.Item(
                    "Drop into Water",
                    "Drop a playable capsule into the selected or first water body using the live collider",
                    () => DropPlayableIntoWater()),
                new ToolStripSeparator(),
                process,
            ]));
        toolbar.Items.Add(new ToolStripSeparator());
        WireResponsiveChrome(toolbar);

        _wireframeButton = EditorChrome.ToolButton("Wireframe", "Toggle wireframe preview", () =>
        {
            _wireframe = !_wireframe;
            _wireframeButton!.Checked = _wireframe;
        }, toggle: true);
        _wireframeButton.Visible = false;
        _fogButton = EditorChrome.ToolButton("Fog", "Aerial haze at distance (off by default)", () =>
        {
            FogEnabled = !_fogEnabled;
        }, toggle: true);
        _fogButton.Checked = _fogEnabled;
        _fogButton.Visible = false;

        WireComponentsPanel();
        WireEmbeddedWizards();
        _componentsPanel.Dock = DockStyle.Fill;
        _componentsPanel.Width = EditorChrome.LeftPanelWidth;
        _radiusSlider = MakeSlider(2, 40, 10);
        _strengthSlider = MakeSlider(1, 100, 45);
        _foliageRadiusSlider = MakeSlider(1, 128, 12);
        _foliageDensitySlider = MakeSlider(0, 100, 70);
        _foliageSpeciesCombo = new ThemedComboBox
        {
            AccessibleDescription = "Foliage species painted or placed by the regional brush",
            AccessibleName = "Foliage Species",
            Name = "TerrainFoliageSpeciesPicker",
            Width = 244,
        };
        EditorChrome.StyleField(_foliageSpeciesCombo);
        foreach (FoliageSpecies species in Enum.GetValues<FoliageSpecies>())
            _foliageSpeciesCombo.Items.Add(species);
        _foliageSpeciesCombo.SelectedItem = _foliageBrush.Species;
        _foliageSpeciesCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_foliageSpeciesCombo.SelectedItem is FoliageSpecies species)
                _foliageBrush.Species = species;
            UpdateStatus();
        };
        _foliageRadiusSlider.ValueChanged += (_, _) =>
        {
            _foliageBrush.Radius = _foliageRadiusSlider.Value;
            UpdateStatus();
        };
        _foliageDensitySlider.ValueChanged += (_, _) =>
        {
            _foliageBrush.Density = _foliageDensitySlider.Value / 100f;
            UpdateStatus();
        };
        ToolStrip modeRail = EditorChrome.MakeModeRail(72);
        foreach (TerrainEditorMode mode in Enum.GetValues<TerrainEditorMode>().OrderBy(mode => mode == TerrainEditorMode.Select ? -1 : (int)mode))
        {
            TerrainEditorMode captured = mode;
            ToolStripButton button = EditorChrome.ModeButton(
                ModeCaption(mode),
                $"Switch to {ModeCaption(mode)} terrain authoring",
                () => SetMode(captured));
            button.AutoSize = false;
            button.Font = EditorChrome.SmallFont;
            button.Height = 52;
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Width = 68;
            button.Visible = mode is TerrainEditorMode.Select or TerrainEditorMode.Sculpt
                or TerrainEditorMode.Paint or TerrainEditorMode.Generate or TerrainEditorMode.Entities;
            _modeButtons.Add(mode, button);
            modeRail.Items.Add(button);
        }

        Panel right = EditorChrome.SidePanel(320, DockStyle.Right);
        _contextHeader = EditorChrome.SectionLabel("Tool");
        _contextHeader.Visible = true;
        _modeHost = new EditorModeHost();
        _selectionInspector.Tag = ProjectRoot;
        _selectionInspector.EditIdentityRequested += (_, _) =>
        {
            if (_componentsPanel.CurrentSelection is { } selection)
            {
                OnComponentEditRequested(selection);
            }
        };
        _selectionInspector.OpenAssetRequested += (_, path) => RequestOpenLinkedResource(path);
        _selectionInspector.FieldsChanged += (_, fields) => ApplySelectionInspectorFields(fields);
        BuildTerrainShellPanels(right);

        FlowLayoutPanel selectPage = MakeContextPage();
        selectPage.Controls.Add(MakeContextCaption("Terrain objects"));
        selectPage.Controls.Add(MakeContextAction("Add object…", "Create a terrain-owned object and assign resources",
            () => OnEntityCreateRequested(this, TerrainEntityType.Foliage)));
        selectPage.Controls.Add(MakeContextAction("Frame terrain", "Frame the terrain in the viewport", () => FrameTerrain()));
        Label selectHint = MakeContextSummary();
        selectHint.Height = 110;
        selectHint.Text = "Select an object in the viewport or the Terrain Wizard. Use its Inspector to move, resize or assign resources.";
        selectPage.Controls.Add(selectHint);
        _modeHost.AddMode(nameof(TerrainEditorMode.Select), selectPage);

        _generateSummary = MakeContextSummary();
        _pathsSummary = MakeContextSummary();
        _foliageSummary = MakeContextSummary();

        FlowLayoutPanel generatePage = BuildGeneratePage();
        _modeHost.AddMode(nameof(TerrainEditorMode.Generate), generatePage);

        FlowLayoutPanel sculptPage = MakeContextPage();
        sculptPage.Controls.Add(MakeContextCaption("Brush"));
        FlowLayoutPanel brushRow = new()
        {
            AutoSize = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 6),
            WrapContents = true,
            Width = 248,
            Height = 84,
        };
        foreach (TerrainBrush brush in Enum.GetValues<TerrainBrush>().Where(value => value != TerrainBrush.Paint))
        {
            TerrainBrush captured = brush;
            Button button = MakeContextAction(
                brush.ToString(),
                $"Use the {brush.ToString().ToLowerInvariant()} terrain brush",
                () => { ActiveBrush = captured; SyncToolbar(); });
            button.Width = 116;
            button.Height = 34;
            button.Margin = new Padding(0, 0, 6, 4);
            _brushButtons.Add(brush, button);
            brushRow.Controls.Add(button);
        }

        sculptPage.Controls.Add(brushRow);
        sculptPage.Controls.Add(MakeSliderPanel("Radius", _radiusSlider));
        sculptPage.Controls.Add(MakeSliderPanel("Strength", _strengthSlider));
        _modeHost.AddMode(nameof(TerrainEditorMode.Sculpt), sculptPage);

        _layerListBox = new ListBox
        {
            Name = "TerrainPaintLayerList",
            BackColor = EditorChrome.Surface,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Top,
            DrawMode = DrawMode.OwnerDrawFixed,
            Font = EditorChrome.BaseFont,
            ForeColor = EditorChrome.Text,
            ItemHeight = DpiLayout.Scale(this, 86),
            Height = 180,
            MultiColumn = true,
            ColumnWidth = 128,
        };
        _layerListBox.DrawItem += DrawLayerItem;
        foreach (TerrainLayerDocument layer in _settings.Layers)
        {
            _layerListBox.Items.Add(layer.Name);
        }

        if (_layerListBox.Items.Count > 0)
        {
            _layerListBox.SelectedIndex = 0;
        }

        Label hint = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Dock = DockStyle.Bottom,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 54,
            Padding = new Padding(10, 4, 8, 4),
            Text = "Paint targets the selected surface.\nChange navigation in Camera → Control method.",
        };
        Panel paintPage = new() { BackColor = EditorChrome.Surface };
        FlowLayoutPanel paintBrush = MakeContextPage();
        paintBrush.Dock = DockStyle.Top;
        paintBrush.Height = 170;
        Button paintButton = MakeContextAction("Paint Surface", "Paint the selected terrain layer", () =>
        {
            ActiveBrush = TerrainBrush.Paint;
            SyncToolbar();
        });
        _brushButtons.Add(TerrainBrush.Paint, paintButton);
        paintBrush.Controls.Add(paintButton);
        TrackBar paintRadius = MakeSlider(2, 40, _radiusSlider.Value);
        TrackBar paintStrength = MakeSlider(1, 100, _strengthSlider.Value);
        paintRadius.ValueChanged += (_, _) => _radiusSlider.Value = paintRadius.Value;
        paintStrength.ValueChanged += (_, _) => _strengthSlider.Value = paintStrength.Value;
        _radiusSlider.ValueChanged += (_, _) => paintRadius.Value = _radiusSlider.Value;
        _strengthSlider.ValueChanged += (_, _) => paintStrength.Value = _strengthSlider.Value;
        paintBrush.Controls.Add(MakeSliderPanel("Radius", paintRadius));
        paintBrush.Controls.Add(MakeSliderPanel("Strength", paintStrength));
        paintPage.Controls.Add(_layerListBox);
        paintPage.Controls.Add(hint);
        paintPage.Controls.Add(paintBrush);
        _modeHost.AddMode(nameof(TerrainEditorMode.Paint), paintPage);

        FlowLayoutPanel pathsPage = BuildPathsPage();
        _modeHost.AddMode(nameof(TerrainEditorMode.Paths), pathsPage);

        FlowLayoutPanel foliagePage = BuildFoliagePage();
        _modeHost.AddMode(nameof(TerrainEditorMode.Foliage), foliagePage);

        _waterSummary = MakeContextSummary();
        FlowLayoutPanel waterPage = BuildWaterAuthoringPage();
        _modeHost.AddMode(nameof(TerrainEditorMode.Water), waterPage);

        FlowLayoutPanel environmentPage = MakeContextPage();
        _environmentSummary = MakeContextSummary();
        environmentPage.Controls.Add(_environmentSummary);
        environmentPage.Controls.Add(MakeContextAction("Toggle Wireframe", "Inspect terrain mesh topology", () => _wireframeButton.PerformClick()));
        environmentPage.Controls.Add(MakeContextAction("Toggle Fog", "Preview authored aerial haze", () => _fogButton.PerformClick()));
        environmentPage.Controls.Add(MakeContextAction("Export World Map…", "Bake the current world map", ExportWorldMap));
        _modeHost.AddMode(nameof(TerrainEditorMode.Environment), environmentPage);

        WireEntitiesMode(projectRoot);

        _modeHost.AddMode(nameof(TerrainEditorMode.Entities), BuildObjectToolsPage());

        // ── Viewport ─────────────────────────────────────────────────────────────
        _viewport = new EditorViewport3D
        {
            SceneStateFactory = BuildSceneState,
            FarPlane = 1200f,
            ControlMethod = EditorCameraControlMethod.Free,
            MiddleButtonPans = true,
            WheelInputHandler = MoveObjectWithWheel,
        };
        float extent = _terrain.ResolutionX * _terrain.CellSize;
        _viewport.Camera.Target = new Vector3(0f, (_terrain.MinHeight + _terrain.MaxHeight) * 0.18f, 0f);
        // A sculpting tool's default view needs to read relief and paint colour clearly —
        // a satellite-height framing (the terrain's full extent away) washes everything into
        // a near-uniform haze. Frame roughly a third of the terrain instead.
        _viewport.Camera.Distance = Math.Clamp(extent * 0.34f, 24f, 220f);
        _viewport.Camera.Pitch = -0.5f;
        _viewport.DrawScene += DrawTerrainScene;
        _viewport.DrawOverlay += DrawOverlay;
        _viewport.Host.MouseDown += (_, e) => EditorPointerDown(e.Location, e.Button);
        _viewport.Host.MouseMove += (_, e) => EditorPointerMove(e.Location, e.Button);
        _viewport.Host.MouseUp += (_, e) => EditorPointerUp(e.Location, e.Button);
        EnableObjectResourceDrop();
        _viewport.SelectionWorldPoint = SelectedComponentWorldPoint;
        _viewportSession.GridCellSize = MathF.Max(0.5f, _terrain.CellSize);
        _viewportSession.ShowGrid = false;
        _viewportSession.ShowSunVisual = true;
        _viewportSession.FloorStyle = EditorFloorStyle.None;
        _viewportSession.CaptureSunRest();
        _viewport.FloorStyle = _viewportSession.FloorStyle;
        _viewportSession.Changed += (_, _) =>
        {
            _viewport.FloorStyle = _viewportSession.FloorStyle;
            _viewport.Invalidate();
            NotifyInspectorStateChanged();
        };
        EditorViewportChrome.Attach(
            toolbar,
            new EditorViewportChrome.Options
            {
                Viewport = _viewport,
                GetIs2D = () => _viewport.Mode2D,
                SetIs2D = is2D =>
                {
                    _viewport.Mode2D = is2D;
                    _viewport.Invalidate();
                    UpdateStatus();
                },
                ViewTooltip = "Grid, lighting, sky, and diagnostic overlays for the terrain viewport",
                CameraExtraItems = [BuildNavigationMenu()],
                Session = _viewportSession,
                FloorStyle = new EditorViewMenuChrome.FloorStyleBinding
                {
                    Read = () => FloorStyle,
                    Write = SetFloorStyle,
                    Invalidate = () => _viewport.Invalidate(),
                },
                Wireframe = new EditorViewMenuChrome.ToggleBinding
                {
                    Read = () => _wireframe,
                    Write = value =>
                    {
                        _wireframe = value;
                        _wireframeButton.Checked = value;
                    },
                    Invalidate = () => _viewport.Host.Invalidate(),
                },
                Fog = new EditorViewMenuChrome.ToggleBinding
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
                IsolateSelection = new EditorViewMenuChrome.ToggleBinding
                {
                    Read = () => _isolateSelectedComponent,
                    Write = value => _isolateSelectedComponent = value,
                    Invalidate = () => _viewport.Invalidate(true),
                },
                ViewExtraItems =
                [
                    EditorCameraMenuChrome.BuildSecondCameraViewMenu(_viewport),
                    ColliderOverlayMenuItem(),
                    WaterVolumeOverlayMenuItem(),
                ],
                GizmoTooltip = "Move, rotate or resize the selected terrain object",
                ReadGizmoMode = () => _viewportSession.GizmoMode,
                WriteGizmoMode = mode => _viewportSession.GizmoMode = mode,
                ReadGizmoSpace = () => _viewportSession.GizmoSpace,
                WriteGizmoSpace = space => _viewportSession.GizmoSpace = space,
                Invalidate = () =>
                {
                    _viewport.Invalidate();
                    UpdateStatus();
                },
            });
        toolbar.Items.Add(BuildWizardMenu());
        AddTerrainPlayback(toolbar, () =>
        {
            if (!_viewportSession.Clock.Playing && _viewportSession.Clock.Time == 0f)
            {
                _viewport.Host.Renderer.ResetShaderPreviewTime();
                _viewportSession.ResetSun();
            }

            UpdateStatus();
            _viewport.Invalidate();
        });

        _statusLabel = EditorChrome.MakeStatusBar();

        InstallReferenceLayout(toolbar, modeRail, right);

        RegisterResponsivePanels(right, modeRail);
        Controls.Add(_viewport);
        Controls.Add(right);
        Controls.Add(_toolPanel);
        Controls.Add(modeRail);
        Controls.Add(toolbar);
        Controls.Add(_statusLabel);
        _viewport.BringToFront();
        WireInspectorNotifications();
        SetMode(TerrainEditorMode.Select);
        RefreshComponentsPanel();
        SyncToolbar();
        UpdateStatus();
    }

    public TerrainBrush ActiveBrush { get; private set; } = TerrainBrush.Raise;

    public TerrainEditorMode ActiveMode { get; private set; } = TerrainEditorMode.Sculpt;

    public TerrainAffects Affects => _componentsPanel.Affects.Value;

    public IReadOnlyList<string> AuthoringModes => _modeHost.ModeNames;

    public void SetMode(TerrainEditorMode mode)
    {
        if (_drawingSection is not null) CancelSectionDrawing();
        ActiveMode = mode;
        if (mode == TerrainEditorMode.Paint)
        {
            ActiveBrush = TerrainBrush.Paint;
        }
        else if (mode == TerrainEditorMode.Sculpt && ActiveBrush == TerrainBrush.Paint)
        {
            ActiveBrush = TerrainBrush.Raise;
        }

        _modeHost.ShowMode(mode.ToString());
        _contextHeader.Text = ModeCaption(mode).ToUpperInvariant();
        _componentsPanel.Affects.Value = TerrainAffectsStrip.ForMode(mode);
        foreach ((TerrainEditorMode candidate, ToolStripButton button) in _modeButtons)
        {
            button.Checked = candidate == mode;
        }

        SyncToolbar();
        UpdateStatus();
        RebuildSelectionInspector();
        NotifyInspectorStateChanged();
        SyncReferencePalette();
    }

    public TerrainAsset Terrain => _terrain;

    public TerrainNatureDocument Nature => _nature;

    public FoliageField Foliage => _foliage;

    public FoliagePerformanceSnapshot LastFoliagePerformance { get; private set; }

    public EditorViewport3D Viewport => _viewport;

    /// <summary>Aerial haze toggle — off by default; see the toolbar Fog button.</summary>
    public bool FogEnabled
    {
        get => _fogEnabled;
        set
        {
            if (_fogEnabled == value) return;
            _fogEnabled = value;
            _fogButton.Checked = value;
            MarkDirty();
            NotifyInspectorStateChanged();
        }
    }

    public int SelectedLayer => Math.Max(0, _layerListBox.SelectedIndex);

    /// <summary>Programmatically selects the active paint layer (toolbar picker for tests).</summary>
    public void SelectPaintLayer(int index)
    {
        if (index >= 0 && index < _layerListBox.Items.Count)
        {
            _layerListBox.SelectedIndex = index;
        }
    }

    /// <summary>Shader Editor target id for the selected terrain component, such as <c>water:…</c>.</summary>
    public string? SelectedShaderTargetId =>
        _selectedComponentKind is { } kind && !string.IsNullOrWhiteSpace(_selectedComponentId)
            ? TerrainShaderTargetCatalog.ForComponent(kind, _selectedComponentId)
            : null;

    public IReadOnlyList<string> ShaderTargetIds =>
        CollectLiveShaderTargets().Select(target => target.Id).ToArray();

    public void SelectTerrainComponent(TerrainComponentsPanel.ComponentKind kind, string id)
    {
        RefreshComponentsPanel();
        _componentsPanel.Select(kind, id);
    }

    public string GetComponentShader(string targetId)
    {
        string id = TerrainShaderTargetCatalog.Normalize(targetId);
        return _settings.ComponentShaders.TryGetValue(id, out string? path) ? path ?? string.Empty : string.Empty;
    }

    public void SetComponentShader(string targetId, string? relativeShaderPath)
    {
        string id = TerrainShaderTargetCatalog.Normalize(targetId);
        if (string.IsNullOrWhiteSpace(id)
            || string.Equals(id, TerrainShaderTargetCatalog.None, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, TerrainShaderTargetCatalog.All, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string next = (relativeShaderPath ?? string.Empty).Replace('\\', '/').Trim();
        string previous = GetComponentShader(id);
        if (string.Equals(previous, next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PushEdit(
            string.IsNullOrWhiteSpace(next) ? "Clear component shader" : "Assign component shader",
            () => ApplyComponentShader(id, next),
            () => ApplyComponentShader(id, previous));
        ApplyComponentShader(id, next);
    }

    private void ApplyComponentShader(string targetId, string relativeShaderPath)
    {
        if (string.IsNullOrWhiteSpace(relativeShaderPath))
        {
            _settings.ComponentShaders.Remove(targetId);
        }
        else
        {
            _settings.ComponentShaders[targetId] = relativeShaderPath;
        }

        NotifyInspectorStateChanged();
        UpdateStatus();
        _viewport.Invalidate(true);
    }

    private IReadOnlyList<TerrainShaderTarget> CollectLiveShaderTargets()
    {
        List<(int Index, string Name)> layers = [];
        for (int i = 0; i < _settings.Layers.Count; i++)
        {
            layers.Add((i, _settings.Layers[i].Name));
        }

        return TerrainShaderTargetCatalog.FromLive(
            layers,
            _settings.Entities,
            _nature,
            _foliage.Instances.Count);
    }

    private void PruneComponentShaders()
    {
        HashSet<string> live = new(ShaderTargetIds, StringComparer.OrdinalIgnoreCase);
        foreach (string key in _settings.ComponentShaders.Keys.ToArray())
        {
            if (!live.Contains(key))
            {
                _settings.ComponentShaders.Remove(key);
            }
        }
    }

    public float BrushRadius => _radiusSlider.Value;

    public float BrushStrength => _strengthSlider.Value / 100f;

    public FoliageBrushMode ActiveFoliageTool => _foliageBrush.Mode;

    public FoliageSpecies SelectedFoliageSpecies => _foliageBrush.Species;

    public float FoliageBrushRadius => _foliageBrush.Radius;

    public float FoliageBrushDensity => _foliageBrush.Density;

    /// <summary>Configures the visible regional foliage controls (also used by headless acceptance).</summary>
    public void ConfigureFoliageBrush(
        FoliageBrushMode mode,
        FoliageSpecies species,
        float radius,
        float density)
    {
        _foliageBrush.Mode = mode;
        _foliageBrush.Species = species;
        _foliageBrush.Radius = radius;
        _foliageBrush.Density = density;
        _foliageBrush.Normalize();
        _foliageRadiusSlider.Value = Math.Clamp((int)MathF.Round(_foliageBrush.Radius), _foliageRadiusSlider.Minimum, _foliageRadiusSlider.Maximum);
        _foliageDensitySlider.Value = Math.Clamp((int)MathF.Round(_foliageBrush.Density * 100f), _foliageDensitySlider.Minimum, _foliageDensitySlider.Maximum);
        _foliageSpeciesCombo.SelectedItem = _foliageBrush.Species;
        SyncToolbar();
        UpdateStatus();
    }

    public override void Save()
    {
        _settings.Resolution = [_terrain.ResolutionX, _terrain.ResolutionZ];
        _settings.CellSize = _terrain.CellSize;
        _settings.MinHeight = _terrain.MinHeight;
        _settings.MaxHeight = _terrain.MaxHeight;
        _settings.FogEnabled = _fogEnabled;
        PruneComponentShaders();
        SaveJson(_settings);
        _terrain.Save(ResourcePath + ".gterrain");
        SaveNatureData();
        AcceptSave();
    }

    /// <summary>Applies one brush dab at a world position (mouse path and headless tests).</summary>
    public void ApplyBrushAt(float worldX, float worldZ, TerrainBrush? brushOverride = null)
    {
        ApplyConfiguredBrush(worldX, worldZ, brushOverride ?? ActiveBrush);
    }

    /// <summary>Begins an undoable stroke (tests call Begin/Apply/End like the mouse path).</summary>
    public void BeginStroke(float worldX, float worldZ)
    {
        _stroking = true;
        _strokeHeightsBefore = (ushort[])_terrain.HeightsData.Clone();
        _strokeSplatBefore = (byte[])_terrain.SplatmapData.Clone();
        _flattenTarget = _terrain.SampleHeight(worldX, worldZ);
    }

    public void EndStroke()
    {
        if (!_stroking)
        {
            return;
        }

        _stroking = false;
        if (_strokeHeightsBefore is null || _strokeSplatBefore is null)
        {
            return;
        }

        ushort[] before = _strokeHeightsBefore;
        byte[] beforeSplat = _strokeSplatBefore;
        ushort[] after = (ushort[])_terrain.HeightsData.Clone();
        byte[] afterSplat = (byte[])_terrain.SplatmapData.Clone();
        _strokeHeightsBefore = null;
        _strokeSplatBefore = null;
        if (before.AsSpan().SequenceEqual(after) && beforeSplat.AsSpan().SequenceEqual(afterSplat))
        {
            return;
        }

        bool geometryChanged = !before.AsSpan().SequenceEqual(after);
        bool paintChanged = !beforeSplat.AsSpan().SequenceEqual(afterSplat);
        void Restore(ushort[] heights, byte[] splats)
        {
            _terrain.RestoreState(heights, splats);
            if (geometryChanged) _meshDirty = true;
            if (paintChanged) InvalidatePaint();
        }
        // The live stroke is already applied. The journal only needs restoration for undo/redo.
        PushEdit(
            $"{ActiveBrush} stroke",
            () => Restore(after, afterSplat),
            () => Restore(before, beforeSplat));
    }

    /// <summary>Begins one undoable regional foliage stroke.</summary>
    public void BeginFoliageStroke()
    {
        if (_foliageStroking) return;
        _foliageStroking = true;
        _strokeFoliageBefore = _foliage;
        _strokeFoliageAdded = 0;
        _strokeFoliageRemoved = 0;
    }

    /// <summary>Applies one live foliage dab using the same operation as viewport input.</summary>
    public FoliageBrushResult ApplyFoliageBrushAt(float worldX, float worldZ)
    {
        FoliageBrushResult result = Genesis.World.Foliage.FoliageBrush.Apply(
            _terrain,
            _pathNetwork,
            _foliage,
            _nature.FoliageSettings,
            new Vector2(worldX, worldZ),
            _foliageBrush,
            _nature.WaterBodies);
        if (!result.Changed) return result;

        _foliage = result.Field;
        _strokeFoliageAdded += result.AddedInstances;
        _strokeFoliageRemoved += result.RemovedInstances;
        ResetFoliagePreview();
        UpdateStatus();
        _viewport.Invalidate(true);
        return result;
    }

    /// <summary>Commits the complete drag as one undo/redo journal entry.</summary>
    public void EndFoliageStroke()
    {
        if (!_foliageStroking) return;
        _foliageStroking = false;
        FoliageField? before = _strokeFoliageBefore;
        FoliageField after = _foliage;
        _strokeFoliageBefore = null;
        if (before is null || ReferenceEquals(before, after)) return;

        int added = _strokeFoliageAdded;
        int removed = _strokeFoliageRemoved;
        void Restore(FoliageField field)
        {
            _foliage = field;
            ResetFoliagePreview();
            UpdateStatus();
            _viewport.Invalidate(true);
        }

        PushEdit(
            $"{_foliageBrush.Mode} foliage stroke (+{added:N0}/-{removed:N0})",
            () => Restore(after),
            () => Restore(before));
    }

    /// <summary>Raycasts the heightfield under a viewport client point.</summary>
    public bool PickTerrain(Point client, out Vector3 world)
    {
        (Vector3 origin, Vector3 direction) = _viewport.PickRay(client);
        world = default;

        float extent = _terrain.ResolutionX * _terrain.CellSize;
        float maxDistance = MathF.Max(200f, extent * 3f);
        float step = MathF.Max(0.25f, _terrain.CellSize * 0.5f);
        Vector3 previousPoint = origin;
        for (float t = 0f; t < maxDistance; t += step)
        {
            Vector3 point = origin + direction * t;
            float height = _terrain.SampleHeight(point.X, point.Z);
            if (point.Y - height <= 0f)
            {
                // Crossed the surface — bisect between the previous and current points.
                Vector3 low = previousPoint;
                Vector3 high = point;
                for (int i = 0; i < 10; i++)
                {
                    Vector3 mid = (low + high) * 0.5f;
                    float midDelta = mid.Y - _terrain.SampleHeight(mid.X, mid.Z);
                    if (midDelta > 0f)
                    {
                        low = mid;
                    }
                    else
                    {
                        high = mid;
                    }
                }

                world = (low + high) * 0.5f;
                world.Y = _terrain.SampleHeight(world.X, world.Z);
                return InTerrainBounds(world);
            }

            previousPoint = point;
        }

        return false;
    }

    // ── Input ────────────────────────────────────────────────────────────────────

    private void EditorPointerDown(Point client, MouseButtons button)
    {
        if (button != MouseButtons.Left) return;
        if (ActiveMode == TerrainEditorMode.Foliage && !_foliageBrushArmed) return;
        if (PlacePendingTerrain(client)) return;
        if (SectionPointerDown(client)) return;

        if ((_placementEntityPath is not null || (ModifierKeys & (Keys.Control | Keys.Shift)) != 0) && BeginObjectBodyDrag(client)) return;

        if (TryBeginComponentGizmoDrag(client))
        {
            return;
        }

        if (BeginObjectBodyDrag(client)) return;
        if (ActiveMode == TerrainEditorMode.Select) { SelectSceneAt(client); return; }

        if (ActiveMode == TerrainEditorMode.Water)
        {
            HandleWaterPointerDown(client);
            return;
        }

        if (ActiveMode == TerrainEditorMode.Paths && _pathTool == PathAuthoringTool.PaintPath)
        {
            HandlePathPointerDown(client);
            return;
        }

        if (ActiveMode is not (TerrainEditorMode.Sculpt or TerrainEditorMode.Paint or TerrainEditorMode.Foliage))
        {
            return;
        }

        if (PickTerrain(client, out Vector3 world))
        {
            _cursorWorld = world;
            _cursorValid = true;
            _viewport.NavigationEnabled = false;
            if (ActiveMode == TerrainEditorMode.Foliage)
            {
                BeginFoliageStroke();
                ApplyFoliageBrushAt(world.X, world.Z);
            }
            else
            {
                BeginStroke(world.X, world.Z);
                ApplyBrushAt(world.X, world.Z);
            }
        }
    }

    private void EditorPointerMove(Point client, MouseButtons button)
    {
        // Camera drags must not ray-march terrain or rebuild authoring controls on every mouse event.
        if (button is MouseButtons.Right or MouseButtons.Middle) return;
        if (SectionPointerMove(client)) return;
        if (button == MouseButtons.Left && MoveObjectBody(client)) return;
        if (_componentGizmoDragging && button == MouseButtons.Left)
        {
            ApplyComponentGizmoDrag(client);
            return;
        }

        if (ActiveMode == TerrainEditorMode.Water)
        {
            HandleWaterPointerMove(client);
            return;
        }

        if (ActiveMode == TerrainEditorMode.Paths)
        {
            HandlePathPointerMove(client, button);
            UpdateStatus();
            return;
        }

        _cursorValid = PickTerrain(client, out _cursorWorld);
        if (_foliageStroking && button == MouseButtons.Left && _cursorValid)
        {
            ApplyFoliageBrushAt(_cursorWorld.X, _cursorWorld.Z);
        }
        else if (_stroking && button == MouseButtons.Left && _cursorValid)
        {
            ApplyBrushAt(_cursorWorld.X, _cursorWorld.Z);
        }

        UpdateStatus();
    }

    private void EditorPointerUp(Point client, MouseButtons button)
    {
        if (button == MouseButtons.Left && SectionPointerUp(client)) return;
        if (button == MouseButtons.Left && EndObjectBodyDrag()) return;
        if (button == MouseButtons.Left && _componentGizmoDragging)
        {
            EndComponentGizmoDrag();
            return;
        }

        if (button == MouseButtons.Left && ActiveMode == TerrainEditorMode.Water && IsWaterGestureActive)
        {
            HandleWaterPointerUp();
            return;
        }

        if (button == MouseButtons.Left && ActiveMode == TerrainEditorMode.Paths && _pathStroking)
        {
            HandlePathPointerUp();
            return;
        }

        if (button == MouseButtons.Left && (_stroking || _foliageStroking))
        {
            if (_foliageStroking) EndFoliageStroke();
            else EndStroke();
            _viewport.NavigationEnabled = true;
        }
    }

    // ── Data ─────────────────────────────────────────────────────────────────────

    private static TerrainSettingsDocument CreateDefaultSettings() => new()
    {
        Layers =
        [
            new TerrainLayerDocument { Name = "Grass", Color = [0.33f, 0.52f, 0.26f] },
            new TerrainLayerDocument { Name = "Dirt", Color = [0.44f, 0.34f, 0.23f] },
            new TerrainLayerDocument { Name = "Rock", Color = [0.46f, 0.46f, 0.50f] },
            new TerrainLayerDocument { Name = "Snow", Color = [0.90f, 0.92f, 0.96f] },
        ],
    };

    private TerrainAsset LoadTerrainData()
    {
        string binary = ResourcePath + ".gterrain";
        if (File.Exists(binary))
        {
            try
            {
                return TerrainAsset.Load(binary);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                LoadWarning = exception.Message;
            }
        }

        return GenerateFromSeed();
    }

    private TerrainNatureDocument LoadNatureData()
    {
        try
        {
            TerrainNatureDocument document = TerrainNatureSerializer.LoadOrDefault(ResourcePath);
            ConformStandingWater(document.WaterBodies);
            return document;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            LoadWarning = string.IsNullOrWhiteSpace(LoadWarning) ? exception.Message : LoadWarning + " · " + exception.Message;
            return new TerrainNatureDocument();
        }
    }

    private FoliageField LoadFoliageData()
    {
        string? cache = TerrainNatureSerializer.ResolveFoliageCache(ResourcePath, _nature);
        if (!string.IsNullOrWhiteSpace(cache) && File.Exists(cache))
        {
            try { return FoliageFieldCache.Load(cache, _nature.FoliageCacheSha256); }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                LoadWarning = string.IsNullOrWhiteSpace(LoadWarning) ? exception.Message : LoadWarning + " · " + exception.Message;
            }
        }
        return new FoliageField
        {
            Seed = _nature.FoliageSettings.Seed,
            Preset = _nature.FoliageSettings.Preset,
            Instances = Array.Empty<FoliageInstance>(),
            Minimum = new Vector2(_terrain.OriginX, _terrain.OriginZ),
            Maximum = new Vector2(
                _terrain.OriginX + (_terrain.ResolutionX - 1) * _terrain.CellSize,
                _terrain.OriginZ + (_terrain.ResolutionZ - 1) * _terrain.CellSize),
        };
    }

    private void ConformStandingWater(IEnumerable<TerrainWaterDefinition> waters)
    {
        foreach (TerrainWaterDefinition water in waters)
            TerrainRiverSystem.ConformStandingWater(_terrain, water);
    }

    private void SaveNatureData()
    {
        if (_foliage.Instances.Count > 0)
        {
            string cacheName = ResourceDisplayName.Format(ResourcePath) + ".gfoliage";
            string cachePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ResourcePath))!, cacheName);
            _nature.FoliageCacheFile = cacheName;
            _nature.FoliageCacheSha256 = FoliageFieldCache.Save(cachePath, _foliage);
            _nature.FoliageInstanceCount = _foliage.Instances.Count;
        }
        else
        {
            _nature.FoliageCacheFile = string.Empty;
            _nature.FoliageCacheSha256 = string.Empty;
            _nature.FoliageInstanceCount = 0;
        }
        TerrainNatureSerializer.Save(ResourcePath, _nature);
    }

    private void OpenPathDialog()
    {
        using TerrainPathDialog dialog = new(_nature.PathSettings);
        if (dialog.ShowDialog(this) == DialogResult.OK) GeneratePaths(dialog.Settings);
    }

    private void OpenFoliageDialog()
    {
        using FoliageScatterDialog dialog = new(_nature.FoliageSettings);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ScatterFoliage(dialog.Settings);
            RefreshComponentsPanel();
        }
    }

    /// <summary>Generates connected routes, grades their beds and repaints the authored splat layer.</summary>
    public void GeneratePaths(TerrainPathSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TerrainPathSettings oldSettings = Clone(settings: _nature.PathSettings);
        List<TerrainPathDefinition> oldPaths = Clone(_nature.Paths);
        FoliageField oldFoliage = _foliage;
        ushort[] oldHeights = (ushort[])_terrain.HeightsData.Clone();
        byte[] oldSplat = (byte[])_terrain.SplatmapData.Clone();
        TerrainPathSettings nextSettings = Clone(settings);
        TerrainPathNetwork generated = TerrainPathNetwork.Generate(_terrain, nextSettings);
        generated.ApplyTo(_terrain, nextSettings.GradeStrength, nextSettings.SplatChannel);
        List<TerrainPathDefinition> nextPaths = Clone(generated.Paths);
        Dictionary<string, HeldFoliageInstance[]> holdBefore = CloneHold(_foliageHeldByWater);
        FoliageField nextFoliage = oldFoliage.Instances.Count > 0
            ? FoliageScatter.Generate(_terrain, generated, _nature.FoliageSettings, _nature.WaterBodies)
            : oldFoliage;
        Dictionary<string, HeldFoliageInstance[]> holdAfter = oldFoliage.Instances.Count > 0
            ? new Dictionary<string, HeldFoliageInstance[]>(StringComparer.OrdinalIgnoreCase)
            : CloneHold(holdBefore);
        ushort[] nextHeights = (ushort[])_terrain.HeightsData.Clone();
        byte[] nextSplat = (byte[])_terrain.SplatmapData.Clone();
        ApplyNaturalState(nextSettings, nextPaths, nextFoliage, nextHeights, nextSplat, holdAfter);
        PushEdit("Generate connected terrain paths",
            () => ApplyNaturalState(nextSettings, nextPaths, nextFoliage, nextHeights, nextSplat, holdAfter),
            () => ApplyNaturalState(oldSettings, oldPaths, oldFoliage, oldHeights, oldSplat, holdBefore));
        RefreshComponentsPanel();
        UpdateStatus();
    }

    /// <summary>Builds deterministic biome-aware foliage and records the result as one undo step.</summary>
    public void ScatterFoliage(FoliageScatterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        FoliageScatterSettings oldSettings = Clone(_nature.FoliageSettings);
        FoliageField oldField = _foliage;
        Dictionary<string, HeldFoliageInstance[]> holdBefore = CloneHold(_foliageHeldByWater);
        FoliageScatterSettings nextSettings = Clone(settings);
        FoliageField nextField = FoliageScatter.Generate(_terrain, _pathNetwork, nextSettings, _nature.WaterBodies);
        Dictionary<string, HeldFoliageInstance[]> holdAfter = new(StringComparer.OrdinalIgnoreCase);
        _nature.FoliageSettings = nextSettings;
        _foliage = nextField;
        _foliageHeldByWater = CloneHold(holdAfter);
        ResetFoliagePreview();
        PushEdit("Scatter ecological foliage",
            () =>
            {
                _nature.FoliageSettings = nextSettings;
                _foliage = nextField;
                _foliageHeldByWater = CloneHold(holdAfter);
                ResetFoliagePreview();
                RefreshComponentsPanel();
            },
            () =>
            {
                _nature.FoliageSettings = oldSettings;
                _foliage = oldField;
                _foliageHeldByWater = CloneHold(holdBefore);
                ResetFoliagePreview();
                RefreshComponentsPanel();
            });
        RefreshComponentsPanel();
        UpdateStatus();
    }

    public void SetWaterBodies(IEnumerable<TerrainWaterDefinition> waterBodies)
    {
        List<TerrainWaterDefinition> before = Clone(_nature.WaterBodies);
        List<TerrainWaterDefinition> after = Clone(waterBodies ?? Array.Empty<TerrainWaterDefinition>());
        ConformStandingWater(after);
        FoliageField foliageBefore = _foliage;
        Dictionary<string, HeldFoliageInstance[]> holdBefore = CloneHold(_foliageHeldByWater);
        Dictionary<string, HeldFoliageInstance[]> holdAfter = CloneHold(_foliageHeldByWater);
        FoliageField foliageAfter = FoliageScatter.ReconcileWithWater(foliageBefore, before, after, holdAfter);
        _nature.WaterBodies = Clone(after);
        _foliage = foliageAfter;
        _foliageHeldByWater = CloneHold(holdAfter);
        _natureMeshDirty = true;
        ResetFoliagePreview();
        PushEdit("Edit terrain water bodies",
            () =>
            {
                _nature.WaterBodies = Clone(after);
                _foliage = foliageAfter;
                _foliageHeldByWater = CloneHold(holdAfter);
                _natureMeshDirty = true;
                ResetFoliagePreview();
                RefreshComponentsPanel();
            },
            () =>
            {
                _nature.WaterBodies = Clone(before);
                _foliage = foliageBefore;
                _foliageHeldByWater = CloneHold(holdBefore);
                _natureMeshDirty = true;
                ResetFoliagePreview();
                RefreshComponentsPanel();
            });
        RefreshComponentsPanel();
        UpdateStatus();
    }

    public void AddWaterBody(TerrainWaterDefinition water)
    {
        ArgumentNullException.ThrowIfNull(water);
        List<TerrainWaterDefinition> bodies = Clone(_nature.WaterBodies);
        bodies.Add(water.Clone());
        SetWaterBodies(bodies);
    }

    public WorldMapImage BakeWorldMap(int size = 512)
    {
        WorldManifest manifest = WorldManifest.Build(_terrain, _nature);
        return WorldMapBaker.Bake(new WorldQuery(_terrain, manifest), size, size);
    }

    private void ExportWorldMap()
    {
        using SaveFileDialog dialog = new()
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = ResourceDisplayName.Format(ResourcePath) + "-world-map.png",
            InitialDirectory = Path.GetDirectoryName(ResourcePath),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        WorldMapImage map = BakeWorldMap();
        using Bitmap bitmap = new(map.Width, map.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < map.Height; y++)
        for (int x = 0; x < map.Width; x++)
        {
            int p = (y * map.Width + x) * 4;
            bitmap.SetPixel(x, y, Color.FromArgb(map.Pixels[p + 3], map.Pixels[p], map.Pixels[p + 1], map.Pixels[p + 2]));
        }
        bitmap.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
        _statusLabel.Text = $"World map exported · {dialog.FileName}";
    }

    private void ApplyNaturalState(TerrainPathSettings settings, List<TerrainPathDefinition> paths,
        FoliageField foliage, ushort[] heights, byte[] splat, Dictionary<string, HeldFoliageInstance[]>? heldByWater = null)
    {
        _terrain.RestoreState(heights, splat);
        _nature.PathSettings = Clone(settings);
        _nature.Paths = Clone(paths);
        _pathNetwork = new TerrainPathNetwork(_nature.Paths);
        _foliage = foliage;
        if (heldByWater != null)
            _foliageHeldByWater = CloneHold(heldByWater);
        _meshDirty = true;
        ResetFoliagePreview();
    }

    private static Dictionary<string, HeldFoliageInstance[]> CloneHold(Dictionary<string, HeldFoliageInstance[]> source)
    {
        var copy = new Dictionary<string, HeldFoliageInstance[]>(StringComparer.OrdinalIgnoreCase);
        if (source == null) return copy;
        foreach (KeyValuePair<string, HeldFoliageInstance[]> pair in source)
            copy[pair.Key] = pair.Value is { Length: > 0 } ? (HeldFoliageInstance[])pair.Value.Clone() : [];
        return copy;
    }

    private static TerrainPathSettings Clone(TerrainPathSettings settings) => new()
    {
        Seed = settings.Seed, PathCount = settings.PathCount, Width = settings.Width,
        GradeStrength = settings.GradeStrength, SplatChannel = settings.SplatChannel, Kind = settings.Kind,
    };

    private static FoliageScatterSettings Clone(FoliageScatterSettings settings) => new()
    {
        Seed = settings.Seed, Preset = settings.Preset, MaximumInstances = settings.MaximumInstances,
        Density = settings.Density, MinimumSpacing = settings.MinimumSpacing, PathExclusion = settings.PathExclusion,
        MaximumSlopeDegrees = settings.MaximumSlopeDegrees, NearDistance = settings.NearDistance, FarDistance = settings.FarDistance,
        StreamingCellSize = settings.StreamingCellSize,
        VisibleInstanceBudget = settings.VisibleInstanceBudget,
        TriangleBudget = settings.TriangleBudget,
        ResidentMemoryBudgetMegabytes = settings.ResidentMemoryBudgetMegabytes,
        GpuUploadBudgetMegabytes = settings.GpuUploadBudgetMegabytes,
        TargetGpuMilliseconds = settings.TargetGpuMilliseconds,
    };

    private static List<TerrainPathDefinition> Clone(IEnumerable<TerrainPathDefinition> paths) => paths.Select(path => new TerrainPathDefinition
    {
        Id = path.Id, Name = path.Name, Kind = path.Kind, Width = path.Width, Points = new List<Vector3>(path.Points),
    }).ToList();

    private static List<TerrainWaterDefinition> Clone(IEnumerable<TerrainWaterDefinition> waters) => waters.Select(water => water.Clone()).ToList();

    private TerrainGenParams CurrentGenParams()
    {
        int resX = _settings.Resolution is { Length: >= 2 } ? _settings.Resolution[0] : 129;
        int resZ = _settings.Resolution is { Length: >= 2 } ? _settings.Resolution[1] : 129;
        Enum.TryParse(_settings.Preset, out TerrainPreset preset);
        return new TerrainGenParams
        {
            ResolutionX = resX,
            ResolutionZ = resZ,
            CellSize = _settings.CellSize,
            MinHeight = _settings.MinHeight,
            MaxHeight = _settings.MaxHeight,
            Preset = preset,
            Seed = _settings.Seed,
            ErosionIterations = _settings.ErosionIterations,
            ErosionStrength = _settings.ErosionStrength,
            TerraceStrength = _settings.TerraceStrength,
            TerraceSteps = _settings.TerraceSteps,
            RiverCount = _settings.RiverCount,
            RiverDepth = _settings.RiverDepth,
        };
    }

    private TerrainAsset GenerateFromSeed() => TerrainGenerator.Generate(CurrentGenParams());

    private void RegenerateFromSeed()
    {
        ushort[] before = (ushort[])_terrain.HeightsData.Clone();
        byte[] beforeSplat = (byte[])_terrain.SplatmapData.Clone();
        TerrainAsset generated = GenerateFromSeed();
        _terrain.RestoreState(generated.HeightsData, generated.SplatmapData);
        ushort[] after = (ushort[])_terrain.HeightsData.Clone();
        byte[] afterSplat = (byte[])_terrain.SplatmapData.Clone();
        _meshDirty = true;
        PushEdit(
            "Regenerate terrain",
            () => { _terrain.RestoreState(after, afterSplat); _meshDirty = true; },
            () => { _terrain.RestoreState(before, beforeSplat); _meshDirty = true; });
    }

    /// <summary>Opens the New-Terrain wizard and, on Create, replaces the terrain with the
    /// generated result (undoable). Used by the toolbar "New…" button.</summary>
    private void OpenCreationWizard()
    {
        using TerrainCreationWizardDialog wizard = new(ResourceDisplayName.Format(ResourcePath));
        Enum.TryParse(_settings.Preset, out TerrainPreset current);
        wizard.SetPreset(current);
        wizard.SetSeed(_settings.Seed);
        wizard.SetProcesses(_settings.ErosionIterations, _settings.ErosionStrength,
            _settings.TerraceStrength, _settings.RiverCount, _settings.RiverDepth);
        if (wizard.ShowDialog(this) == DialogResult.OK && wizard.PreviewResult is { } result)
        {
            if (wizard.PlaceManually) ArmTerrainPlacement(result, true);
            else { ApplyHeightfield(result, wizard.PlacementPosition); FrameSection(result, wizard.PlacementPosition); }
        }
    }

    /// <summary>Replaces the terrain (and its settings) with a freshly generated one, supporting a
    /// resolution change by swapping the asset. Undoable. Public so headless tests drive the wizard→apply flow.</summary>
    public void ApplyGeneration(TerrainGenParams p)
    {
        TerrainAsset before = _terrain;
        int[] beforeRes = _settings.Resolution;
        float beforeCell = _settings.CellSize, beforeMin = _settings.MinHeight, beforeMax = _settings.MaxHeight;
        string beforePreset = _settings.Preset;
        int beforeSeed = _settings.Seed;
        int beforeErosionIterations = _settings.ErosionIterations;
        float beforeErosionStrength = _settings.ErosionStrength;
        float beforeTerraceStrength = _settings.TerraceStrength;
        int beforeTerraceSteps = _settings.TerraceSteps;
        int beforeRiverCount = _settings.RiverCount;
        float beforeRiverDepth = _settings.RiverDepth;

        TerrainAsset after = TerrainGenerator.Generate(p);
        void ApplySettings(TerrainGenParams gp)
        {
            _settings.Resolution = [gp.ResolutionX, gp.ResolutionZ];
            _settings.CellSize = gp.CellSize;
            _settings.MinHeight = gp.MinHeight;
            _settings.MaxHeight = gp.MaxHeight;
            _settings.Preset = gp.Preset.ToString();
            _settings.Seed = gp.Seed;
            _settings.ErosionIterations = gp.ErosionIterations;
            _settings.ErosionStrength = gp.ErosionStrength;
            _settings.TerraceStrength = gp.TerraceStrength;
            _settings.TerraceSteps = gp.TerraceSteps;
            _settings.RiverCount = gp.RiverCount;
            _settings.RiverDepth = gp.RiverDepth;
            _settings.SchemaVersion = 2;
        }

        void SwapTo(TerrainAsset asset, TerrainGenParams gp)
        {
            _terrain = asset;
            _eraseBaseline = (ushort[])asset.HeightsData.Clone();
            ApplySettings(gp);
            ReleaseTerrainMeshes();
            _meshDirty = true;
        }

        TerrainGenParams beforeParams = new()
        {
            ResolutionX = beforeRes is { Length: >= 2 } ? beforeRes[0] : 129,
            ResolutionZ = beforeRes is { Length: >= 2 } ? beforeRes[1] : 129,
            CellSize = beforeCell, MinHeight = beforeMin, MaxHeight = beforeMax,
            Preset = Enum.TryParse(beforePreset, out TerrainPreset bp) ? bp : TerrainPreset.RollingHills,
            Seed = beforeSeed,
            ErosionIterations = beforeErosionIterations,
            ErosionStrength = beforeErosionStrength,
            TerraceStrength = beforeTerraceStrength,
            TerraceSteps = beforeTerraceSteps,
            RiverCount = beforeRiverCount,
            RiverDepth = beforeRiverDepth,
        };

        SwapTo(after, p);
        MarkDirty();
        PushEdit(
            "New terrain",
            () => SwapTo(after, p),
            () => SwapTo(before, beforeParams));
    }

    /// <summary>Applies talus-based erosion to the current terrain and records an undo snapshot.</summary>
    public void ApplyErosion(int iterations, float strength)
    {
        ApplyTerrainProcess("Erode terrain", parameters =>
        {
            parameters.ErosionIterations = Math.Clamp(iterations, 0, 100);
            parameters.ErosionStrength = Math.Clamp(strength, 0f, 1f);
        }, parameters => TerrainGenerator.ApplyThermalErosion(
            _terrain, parameters.ErosionIterations, parameters.ErosionStrength));
    }

    /// <summary>Adds stepped geological strata to the current terrain, with undo.</summary>
    public void ApplyTerracing(float strength, int steps)
    {
        ApplyTerrainProcess("Terrace terrain", parameters =>
        {
            parameters.TerraceStrength = Math.Clamp(strength, 0f, 1f);
            parameters.TerraceSteps = Math.Clamp(steps, 2, 128);
        }, parameters => TerrainGenerator.ApplyTerracing(
            _terrain, parameters.TerraceStrength, parameters.TerraceSteps));
    }

    /// <summary>Carves seeded meandering rivers into the current terrain, with undo.</summary>
    public void CarveRivers(int count, float depth)
    {
        ApplyTerrainProcess("Carve terrain rivers", parameters =>
        {
            parameters.RiverCount = Math.Clamp(count, 0, 8);
            parameters.RiverDepth = Math.Clamp(depth, 0.1f, 128f);
        }, parameters => TerrainGenerator.CarveRivers(
            _terrain, parameters.Seed, parameters.RiverCount, parameters.RiverDepth));
    }

    private void ApplyTerrainProcess(
        string label,
        Action<TerrainGenParams> configure,
        Action<TerrainGenParams> process)
    {
        ushort[] before = (ushort[])_terrain.HeightsData.Clone();
        byte[] beforeSplat = (byte[])_terrain.SplatmapData.Clone();
        TerrainGenParams beforeParameters = CurrentGenParams();
        TerrainGenParams afterParameters = beforeParameters.Clone();
        configure(afterParameters);
        process(afterParameters);
        TerrainGenerator.SeedSplatFromSlope(_terrain, _terrain.CellSize);
        StoreProcessSettings(afterParameters);
        ushort[] after = (ushort[])_terrain.HeightsData.Clone();
        byte[] afterSplat = (byte[])_terrain.SplatmapData.Clone();
        _meshDirty = true;

        void Restore(ushort[] heights, byte[] splat, TerrainGenParams parameters)
        {
            _terrain.RestoreState(heights, splat);
            StoreProcessSettings(parameters);
            _meshDirty = true;
        }

        PushEdit(label,
            () => Restore(after, afterSplat, afterParameters),
            () => Restore(before, beforeSplat, beforeParameters));
    }

    private void StoreProcessSettings(TerrainGenParams parameters)
    {
        _settings.ErosionIterations = parameters.ErosionIterations;
        _settings.ErosionStrength = parameters.ErosionStrength;
        _settings.TerraceStrength = parameters.TerraceStrength;
        _settings.TerraceSteps = parameters.TerraceSteps;
        _settings.RiverCount = parameters.RiverCount;
        _settings.RiverDepth = parameters.RiverDepth;
        _settings.SchemaVersion = 2;
    }

    // ── Terrain Entity library ───────────────────────────────────────────────────

    private void OnEntityCreateRequested(object? sender, TerrainEntityType type)
    {
        string parent = ResourcePath + ".parts";
        Directory.CreateDirectory(parent);
        string path = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".terrainpart.json");
        File.WriteAllText(path, "{}");
        ShowEntityWizard(path, type, isNew: true);
    }

    private void OnEntityEditRequested(object? sender, string path) =>
        ShowEntityWizard(path, presetType: null, isNew: false);

    private bool InTerrainBounds(Vector3 world)
    {
        float minX = _terrain.OriginX;
        float minZ = _terrain.OriginZ;
        float maxX = minX + (_terrain.ResolutionX - 1) * _terrain.CellSize;
        float maxZ = minZ + (_terrain.ResolutionZ - 1) * _terrain.CellSize;
        return world.X >= minX && world.X <= maxX && world.Z >= minZ && world.Z <= maxZ;
    }

    // ── Mesh build / render ──────────────────────────────────────────────────────

    private Mesh3DState BuildSceneState()
    {
        Mesh3DState state = EditorSceneLighting.Create(
            showFloor: _viewportSession.FloorStyle.DrawsPlate(), shadows: true, farPlane: 1200f);
        state = _viewportSession.Apply(state);
        float extent = _terrain.ResolutionX * _terrain.CellSize;
        // Aerial haze is opt-in (default off) — it previously ran unconditionally and, even
        // pushed well past the terrain's own extent, could wash the subject toward the fog
        // colour. Authors who want atmospheric distance haze can switch it on explicitly.
        if (_fogEnabled)
        {
            state = EditorSceneLighting.WithAerialHaze(state, extent * 2.2f, extent * 6f);
        }
        else
        {
            // Terrain already has an authored Fog toggle, so it explicitly overrides the global
            // engine default. Other 3D editors without a local fog control inherit Preferences.
            state.FogEnabled = false;
            state.FogScreenSpace = false;
        }

        state.Wireframe = _wireframe;
        state.ShadowOrthoSize = MathF.Max(90f, extent * 0.75f);
        return state;
    }

    private void DrawTerrainScene(IRenderController renderer)
    {
        TickPreviewIfPlaying();
        AuthoredTerrainGround ground = EnsureTerrainGround(renderer);
        if (_terrainGroundDraws.Length < ground.MeshCount + 4)
        {
            Array.Resize(ref _terrainGroundDraws, ground.MeshCount + 16);
        }

        int count = 0;
        ground.AppendDrawCalls(
            _terrainGroundDraws,
            ref count,
            MeshRasterDefaults.ApplyOverride(
                MeshDrawFlags.None,
                _settings.Culling,
                _settings.WindingOrder));
        for (int i = 0; i < count; i++)
        {
            renderer.DrawMesh(_terrainGroundDraws[i]);
        }

        DrawNatureScene(renderer);
        DrawPlacedEntities(renderer);
        DrawTerrainPlacement(renderer);
    }

    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        base.OnAssetDependenciesChanged(changes);
        if (!IsDirty && changes.ChangedPaths.Any(IsTerrainSidecar))
        {
            _terrain = LoadTerrainData();
            _eraseBaseline = (ushort[])_terrain.HeightsData.Clone();
            _nature = LoadNatureData();
            _pathNetwork = new TerrainPathNetwork(_nature.Paths);
            _foliage = LoadFoliageData();
            _foliagePlanner = new FoliageStreamingPlanner(_foliage, _nature.FoliageSettings.StreamingCellSize);
        }
        ReleaseNatureMeshes();
        ReleaseTerrainMeshes();
        _meshDirty = true;
        _natureMeshDirty = true;
        _viewport.Invalidate(true);
        _statusLabel.Text = $"Terrain dependencies rebound · generation {changes.Generation}";

        bool IsTerrainSidecar(string path) =>
            path.StartsWith(ResourcePath, StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith(".gterrain", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".nature.json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".foliage", StringComparison.OrdinalIgnoreCase));
    }

    private void DrawNatureScene(IRenderController renderer)
    {
        if (_natureMeshDirty || !ReferenceEquals(_natureRenderer, renderer))
            RebuildNatureMeshes(renderer);

        for (int i = 0; i < _pathMeshes.Count && i < _nature.Paths.Count; i++)
        {
            TerrainPathDefinition path = _nature.Paths[i];
            if (!ShouldDrawNatureComponent(TerrainComponentsPanel.ComponentKind.Path, path.Id)) continue;

            MeshHandle mesh = _pathMeshes[i];
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = mesh,
                World = Matrix4x4.Identity,
                Tint = new RenderColor(0.83f, 0.69f, 0.46f, 1f),
                Flags = MeshDrawFlags.NoShadow,
            });
        }

        if (_waterMeshCache != null)
        {
            foreach (TerrainWaterDefinition definition in _nature.WaterBodies)
            {
                if (!ShouldDrawNatureComponent(TerrainComponentsPanel.ComponentKind.Water, definition.Id)) continue;
                List<MeshDrawCall> waterCalls = [];
                WaterDrawSystem.BuildDrawCalls(definition.ToWaterBody(), _waterMeshCache, _viewport.Camera.Eye, waterCalls);
                string shaderPath = GetComponentShader("water:" + definition.Id);
                foreach (MeshDrawCall call in waterCalls)
                {
                    MeshDrawCall bound = call;
                    if (TryBindAuthoredWaterShader(renderer, shaderPath, ref bound))
                    {
                        bound.Flags = MeshDrawFlags.Transparent
                            | MeshDrawFlags.NoCull
                            | MeshDrawFlags.NoShadow
                            | MeshDrawFlags.NoDepthWrite;
                    }

                    renderer.DrawMesh(bound);
                }
            }
        }

        if (ShouldDrawNatureComponent(TerrainComponentsPanel.ComponentKind.Foliage, "foliage"))
        {
            _foliagePlanner ??= new FoliageStreamingPlanner(_foliage, _nature.FoliageSettings.StreamingCellSize);
            FoliageFramePlan plan = _foliagePlanner.Build(
                _viewport.Camera.Eye,
                _viewport.ViewMatrix * _viewport.ProjectionMatrix,
                Matrix4x4.Identity,
                _nature.FoliageSettings,
                renderer.LastGpuMilliseconds);
            LastFoliagePerformance = plan.Snapshot;

            foreach (FoliageRenderBatch batch in plan.Batches)
            {
                if (!_foliageMeshes.TryGetValue((batch.Key.Species, batch.Key.NearLod), out MeshHandle mesh) || !mesh.IsValid) continue;
                if (_foliageInstanceBuffer.Length < batch.Instances.Count)
                    Array.Resize(ref _foliageInstanceBuffer, NextPowerOfTwo(batch.Instances.Count));
                for (int i = 0; i < batch.Instances.Count; i++)
                {
                    FoliageInstance instance = batch.Instances[i];
                    float hue = instance.HueVariation * 0.045f;
                    Matrix4x4 world = Matrix4x4.CreateScale(instance.Scale)
                        * Matrix4x4.CreateRotationY(instance.Rotation)
                        * Matrix4x4.CreateTranslation(instance.Position + Vector3.UnitY * 0.06f);
                    _foliageInstanceBuffer[i] = new MeshInstanceData(
                        world,
                        new RenderColor(1f + hue, 1f, 1f - hue, 1f));
                }

                MeshDrawCall template = new()
                {
                    Mesh = mesh,
                    Tint = RenderColor.White,
                    Alpha = 1f,
                    Flags = MeshDrawFlags.Foliage | MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow,
                };
                renderer.DrawMeshInstances(template, _foliageInstanceBuffer.AsSpan(0, batch.Instances.Count));
            }

            UpdateFoliageSummary(LastFoliagePerformance);
        }
    }

    private void RebuildNatureMeshes(IRenderController renderer)
    {
        ReleaseNatureMeshes();
        _natureRenderer = renderer;
        foreach (TerrainPathDefinition path in _nature.Paths)
        {
            MeshData geometry = TerrainPathGeometry.BuildRibbon(path);
            if (geometry.Vertices is { Length: > 0 }) _pathMeshes.Add(renderer.RegisterMesh(geometry.Vertices, geometry.Indices));
        }
        foreach (FoliageSpecies species in _foliage.Instances.Select(instance => instance.Species).Distinct())
        {
            foreach (bool nearLod in new[] { true, false })
            {
                MeshData geometry = FoliageGeometry.Build(species, nearLod);
                if (geometry.Vertices is { Length: > 0 })
                    _foliageMeshes[(species, nearLod)] = renderer.RegisterMesh(geometry.Vertices, geometry.Indices);
            }
        }
        _waterMeshCache = new WaterMeshCache(renderer);
        _natureMeshDirty = false;
    }

    private void ReleaseNatureMeshes()
    {
        if (_natureRenderer != null)
        {
            foreach (MeshHandle handle in _pathMeshes) if (handle.IsValid) _natureRenderer.ReleaseMesh(handle);
            foreach (MeshHandle handle in _foliageMeshes.Values) if (handle.IsValid) _natureRenderer.ReleaseMesh(handle);
        }
        _pathMeshes.Clear(); _foliageMeshes.Clear();
        _waterMeshCache?.Dispose(); _waterMeshCache = null;
    }

    private bool TryBindAuthoredWaterShader(IRenderController renderer, string shaderPath, ref MeshDrawCall call)
    {
        if (string.IsNullOrWhiteSpace(shaderPath)) return false;
        return ObjectDrawPass.TryApplyAuthoredWaterShader(renderer, ProjectRoot, shaderPath, ref call);
    }

    private void ResetFoliagePreview()
    {
        _foliagePlanner = new FoliageStreamingPlanner(_foliage, _nature.FoliageSettings.StreamingCellSize);
        LastFoliagePerformance = default;
        _natureMeshDirty = true;
    }

    private void UpdateFoliageSummary(FoliagePerformanceSnapshot snapshot)
    {
        _foliageSummary.Text =
            $"{snapshot.AuthoredInstances:N0} authored · {snapshot.SubmittedInstances:N0} GPU-visible\n" +
            $"{snapshot.VisibleCells:N0}/{snapshot.TotalCells:N0} cells · {snapshot.Batches:N0} instanced batch(es)\n" +
            $"{snapshot.SubmittedTriangles:N0} triangles · {snapshot.UploadBytes / 1048576d:0.00} MB upload · {snapshot.PlanningMilliseconds:0.00} ms plan";
    }

    private static int NextPowerOfTwo(int value)
    {
        int capacity = 128;
        while (capacity < value && capacity < 32768) capacity <<= 1;
        return Math.Max(value, capacity);
    }

    /// <summary>
    /// Chunked <see cref="MeshDrawFlags.TerrainGround"/> meshes submitted this preview.
    /// 513×513 cannot be one 16-bit-index mesh; Play already chunks, and the editor must too.
    /// </summary>
    public int TerrainPreviewChunkCount
    {
        get
        {
            int count = 0;
            foreach (AuthoredTerrainGround ground in _terrainGrounds.Values)
            {
                count = Math.Max(count, ground.MeshCount);
            }

            return count;
        }
    }

    private AuthoredTerrainGround EnsureTerrainGround(IRenderController renderer)
    {
        UpdateMaterialPreview();
        var surfaceMaterial = EnsureSurfaceMaterial(renderer);
        if (_meshDirty)
        {
            _groundRevision++;
            _meshDirty = false;
        }

        if (!_terrainGrounds.TryGetValue(renderer, out AuthoredTerrainGround? ground)
            || !ReferenceEquals(ground.Asset, _terrain))
        {
            ground?.Dispose();
            ground = new AuthoredTerrainGround(_terrain);
            _terrainGrounds[renderer] = ground;
        }

        if (!_terrainGroundRevision.TryGetValue(renderer, out int bound)
            || bound != _groundRevision
            || ground.MeshCount == 0)
        {
            ground.SurfaceMaterial = surfaceMaterial;
            ground.Bind(renderer, ground.SurfaceMaterial?.Texture ?? EnsureTerrainAlbedo(renderer), uvScale: ground.SurfaceMaterial.HasValue ? 1f : 8f);
            _terrainGroundRevision[renderer] = _groundRevision;
        }

        return ground;
    }

    private TextureHandle EnsureTerrainAlbedo(IRenderController renderer)
    {
        if (!_terrainAlbedoResolved)
        {
            _terrainAlbedoRasterPath = ResolveTerrainAlbedoRaster();
            _terrainAlbedoResolved = true;
        }

        if (string.IsNullOrWhiteSpace(_terrainAlbedoRasterPath))
        {
            return TextureHandle.Invalid;
        }

        if (_terrainAlbedos.TryGetValue(renderer, out TextureHandle handle) && handle.IsValid)
        {
            return handle;
        }

        handle = renderer.LoadTexture(_terrainAlbedoRasterPath);
        _terrainAlbedos[renderer] = handle;
        return handle;
    }

    private string? ResolveTerrainAlbedoRaster()
    {
        foreach (TerrainLayerDocument layer in _settings.Layers)
        {
            string? raster = ResolveNamedImageRaster(layer.Name);
            if (!string.IsNullOrWhiteSpace(raster))
            {
                return raster;
            }
        }

        return ResolveNamedImageRaster("ForestGrass")
            ?? ResolveNamedImageRaster("Terrain_ForestGround");
    }

    private string? ResolveNamedImageRaster(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ProjectRoot)) return null;
        return ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, name);
    }

    private void ReleaseTerrainMeshes()
    {
        ReleaseEntityMaterials();
        ReleaseSurfaceMaterials();
        foreach (AuthoredTerrainGround ground in _terrainGrounds.Values)
        {
            ground.Dispose();
        }

        _terrainGrounds.Clear();
        _terrainGroundRevision.Clear();

        foreach ((IRenderController renderer, TextureHandle handle) in _terrainAlbedos)
        {
            if (handle.IsValid)
            {
                renderer.ReleaseTexture(handle);
            }
        }

        _terrainAlbedos.Clear();
        _terrainAlbedoResolved = false;
        _terrainAlbedoRasterPath = null;
        _meshDirty = true;
    }

    private void DrawOverlay(IRenderController renderer)
    {
        float cell = MathF.Max(0.5f, _terrain.CellSize);
        _viewportSession.DrawGrid(
            _viewport,
            renderer,
            horizontalExtent: _terrain.ResolutionX * cell * 0.5f,
            verticalExtent: MathF.Max(8f, (_terrain.MaxHeight - _terrain.MinHeight) * 0.15f));

        DrawSelectionBounds(renderer);
        DrawTerrainObjectMarkers(renderer);
        DrawWaterPreview(renderer);
        DrawSectionPreview(renderer);
        DrawPointsOfInterestOverlay(renderer);
        DrawPathPaintPreview(renderer);
        DrawComponentGizmo(renderer);
        DrawPhysicsOverlays(renderer);
        EditorViewportHudOverlay.Draw(_viewport, renderer, _viewportSession);

        if (!_cursorValid
            || ActiveMode is not (TerrainEditorMode.Sculpt or TerrainEditorMode.Paint or TerrainEditorMode.Foliage or TerrainEditorMode.Paths))
        {
            return;
        }

        // Brush ring projected onto the heightfield.
        RenderColor ring = ActiveMode == TerrainEditorMode.Foliage
            ? _foliageBrush.Mode switch
            {
                FoliageBrushMode.Erase => new RenderColor(0.96f, 0.27f, 0.24f, 0.95f),
                FoliageBrushMode.Place => new RenderColor(1f, 0.72f, 0.16f, 0.95f),
                _ => new RenderColor(0.27f, 0.88f, 0.43f, 0.95f),
            }
            : new RenderColor(EditorChrome.Accent.R / 255f, EditorChrome.Accent.G / 255f, EditorChrome.Accent.B / 255f, 0.9f);
        float radius = ActiveMode == TerrainEditorMode.Foliage ? FoliageBrushRadius : BrushRadius;
        float height = MathF.Max(1.2f, radius * 0.55f);
        const int segments = 28;
        Vector3 previousGround = default;
        Vector3 previousTop = default;
        Vector3 previousInner = default;
        bool hasPrevious = false;
        for (int i = 0; i <= segments; i++)
        {
            float angle = i / (float)segments * MathF.Tau;
            float wx = _cursorWorld.X + MathF.Cos(angle) * radius;
            float wz = _cursorWorld.Z + MathF.Sin(angle) * radius;
            float groundY = _terrain.SampleHeight(wx, wz);
            Vector3 ground = _viewport.WorldToSurface(new Vector3(wx, groundY + 0.08f, wz));
            Vector3 top = _viewport.WorldToSurface(new Vector3(wx, groundY + height, wz));
            float innerWx = _cursorWorld.X + MathF.Cos(angle) * radius * 0.55f;
            float innerWz = _cursorWorld.Z + MathF.Sin(angle) * radius * 0.55f;
            Vector3 inner = _viewport.WorldToSurface(new Vector3(
                innerWx,
                _terrain.SampleHeight(innerWx, innerWz) + 0.1f,
                innerWz));
            if (hasPrevious && ground.Z is > 0f and < 1f && previousGround.Z is > 0f and < 1f)
            {
                renderer.DrawLine(previousGround.X, previousGround.Y, ground.X, ground.Y, ring, 2f, depth: -9000);
                renderer.DrawLine(previousTop.X, previousTop.Y, top.X, top.Y, ring, 1.4f, depth: -8999);
                renderer.DrawLine(previousInner.X, previousInner.Y, inner.X, inner.Y, new RenderColor(ring.R, ring.G, ring.B, 0.45f), 1.2f, depth: -8998);
            }

            if (i % 4 == 0 && ground.Z is > 0f and < 1f && top.Z is > 0f and < 1f)
            {
                renderer.DrawLine(ground.X, ground.Y, top.X, top.Y, new RenderColor(ring.R, ring.G, ring.B, 0.55f), 1.2f, depth: -8997);
            }

            previousGround = ground;
            previousTop = top;
            previousInner = inner;
            hasPrevious = true;
        }
    }

    // ── UI helpers ───────────────────────────────────────────────────────────────

    private static TrackBar MakeSlider(int min, int max, int value) => new()
    {
        AutoSize = false,
        BackColor = EditorChrome.Surface,
        Height = 26,
        Maximum = max,
        Minimum = min,
        TickStyle = TickStyle.None,
        Value = value,
        Width = 108,
    };

    private static string ModeCaption(TerrainEditorMode mode) => mode switch
    {
        TerrainEditorMode.Generate => "Create",
        TerrainEditorMode.Select => "Select",
        TerrainEditorMode.Sculpt => "Sculpt",
        TerrainEditorMode.Paint => "Paint",
        TerrainEditorMode.Paths => "Paths",
        TerrainEditorMode.Foliage => "Foliage",
        TerrainEditorMode.Water => "Water",
        TerrainEditorMode.Environment => "Environment",
        _ => "Objects",
    };

    private static FlowLayoutPanel MakeContextPage() => new()
    {
        AutoScroll = true,
        BackColor = EditorChrome.Surface,
        FlowDirection = FlowDirection.TopDown,
        Padding = EditorChrome.PanelInsets,
        WrapContents = false,
    };

    private static Label MakeContextSummary() => new()
    {
        AutoSize = false,
        BackColor = EditorChrome.Raised,
        Font = EditorChrome.SmallFont,
        ForeColor = EditorChrome.Text,
        Height = 70,
        Margin = new Padding(0, 0, 0, EditorChrome.SectionGap),
        Padding = new Padding(EditorChrome.CompactPadding),
        Width = 244,
    };

    private static Label MakeContextCaption(string text) => new()
    {
        AutoSize = false,
        BackColor = Color.Transparent,
        Font = EditorChrome.HeadingFont,
        ForeColor = EditorChrome.Muted,
        Height = EditorChrome.SectionHeaderHeight,
        Margin = new Padding(0, 0, 0, 4),
        Text = text.ToUpperInvariant(),
        TextAlign = ContentAlignment.MiddleLeft,
        Width = 244,
    };

    private static Button MakeContextAction(string text, string description, Action action)
    {
        Button button = new()
        {
            AccessibleDescription = description,
            AccessibleName = text.Replace("…", string.Empty, StringComparison.Ordinal),
            Height = 34,
            Margin = new Padding(0, 0, 0, 4),
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            Width = 248,
        };
        EditorChrome.StyleField(button);
        button.Click += (_, _) => action();
        return button;
    }

    private static Panel MakeSliderPanel(string caption, TrackBar slider)
    {
        Panel panel = new()
        {
            BackColor = Color.Transparent,
            Height = 52,
            Margin = new Padding(0, 0, 0, 5),
            Width = 244,
        };
        Label label = new()
        {
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 20,
            Text = caption,
        };
        slider.Dock = DockStyle.Fill;
        panel.Controls.Add(slider);
        panel.Controls.Add(label);
        return panel;
    }

    private static Panel MakeComboPanel(string caption, ComboBox combo)
    {
        Panel panel = new()
        {
            BackColor = Color.Transparent,
            Height = 56,
            Margin = new Padding(0, 0, 0, 5),
            Width = 244,
        };
        Label label = new()
        {
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 20,
            Text = caption,
        };
        combo.Dock = DockStyle.Bottom;
        panel.Controls.Add(combo);
        panel.Controls.Add(label);
        return panel;
    }

    private void DrawLayerItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _settings.Layers.Count)
        {
            return;
        }

        TerrainLayerDocument layer = _settings.Layers[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using SolidBrush back = new(selected ? EditorChrome.Hover : EditorChrome.Surface);
        e.Graphics.FillRectangle(back, e.Bounds);
        Color swatch = Color.FromArgb(
            (int)(Math.Clamp(layer.Color[0], 0f, 1f) * 255f),
            (int)(Math.Clamp(layer.Color[1], 0f, 1f) * 255f),
            (int)(Math.Clamp(layer.Color[2], 0f, 1f) * 255f));
        using SolidBrush swatchBrush = new(swatch);
        e.Graphics.FillRectangle(swatchBrush, e.Bounds.X + 12, e.Bounds.Y + 8, 96, 48);
        using Pen border = new(EditorChrome.Border);
        e.Graphics.DrawRectangle(border, e.Bounds.X + 12, e.Bounds.Y + 8, 96, 48);
        using SolidBrush text = new(EditorChrome.Text);
        e.Graphics.DrawString(layer.Name, EditorChrome.SmallFont, text, e.Bounds.X + 12, e.Bounds.Y + 61);
    }

    private void SyncToolbar()
    {
        foreach ((TerrainBrush brush, Button button) in _brushButtons)
        {
            bool active = brush == ActiveBrush
                && ((brush == TerrainBrush.Paint && ActiveMode == TerrainEditorMode.Paint)
                    || (brush != TerrainBrush.Paint && ActiveMode == TerrainEditorMode.Sculpt));
            button.BackColor = active ? EditorChrome.Accent : EditorChrome.Raised;
            button.ForeColor = active ? Color.White : EditorChrome.Text;
            button.FlatAppearance.BorderColor = active ? EditorChrome.Accent : EditorChrome.Border;
        }

        foreach ((FoliageBrushMode mode, Button button) in _foliageToolButtons)
        {
            bool active = ActiveMode == TerrainEditorMode.Foliage && mode == _foliageBrush.Mode;
            button.BackColor = active ? EditorChrome.Accent : EditorChrome.Raised;
            button.ForeColor = active ? Color.White : EditorChrome.Text;
            button.FlatAppearance.BorderColor = active ? EditorChrome.Accent : EditorChrome.Border;
        }

        foreach ((PathAuthoringTool tool, Button button) in _pathToolButtons)
        {
            bool active = ActiveMode == TerrainEditorMode.Paths && tool == _pathTool;
            button.BackColor = active ? EditorChrome.Accent : EditorChrome.Raised;
            button.ForeColor = active ? Color.White : EditorChrome.Text;
            button.FlatAppearance.BorderColor = active ? EditorChrome.Accent : EditorChrome.Border;
        }

        foreach ((WaterAuthoringTool tool, Button button) in _waterToolButtons)
        {
            bool active = ActiveMode == TerrainEditorMode.Water && tool == _waterTool;
            button.BackColor = active ? EditorChrome.Accent : EditorChrome.Raised;
            button.ForeColor = active ? Color.White : EditorChrome.Text;
            button.FlatAppearance.BorderColor = active ? EditorChrome.Accent : EditorChrome.Border;
        }
    }

    private void UpdateStatus()
    {
        string cursor = _cursorValid
            ? $"x {_cursorWorld.X:0.#}  z {_cursorWorld.Z:0.#}  h {_cursorWorld.Y:0.##}"
            : "—";
        string activeTool = ActiveMode switch
        {
            TerrainEditorMode.Select => "Select and move objects",
            TerrainEditorMode.Entities => "Add or edit terrain objects",
            TerrainEditorMode.Generate => "Landscape creation",
            TerrainEditorMode.Water => _waterTool == WaterAuthoringTool.River
                ? $"River · {_riverDraft.Count} points · width {_riverWidthBox.Value:0.##} · depth {_riverDepthBox.Value:0.##} · carve {(_riverCarveCheck.Checked ? "on" : "off")}"
                : $"{_waterTool} · {_pendingWaterKind} · level {(_waterLevelSet ? _waterLevel.ToString("0.##") : "—")} · carve {(_carveBasinCheck.Checked ? "on" : "off")}",
            TerrainEditorMode.Foliage => _foliageBrushArmed ? $"{_foliageBrush.Mode} {_foliageBrush.Species} · radius {FoliageBrushRadius:0} · density {FoliageBrushDensity:P0}" : "Choose a plant or tree to place",
            TerrainEditorMode.Paths => $"{_pathTool} · width {_nature.PathSettings.Width:0.#} · {_nature.PathSettings.Kind}",
            _ => $"{ActiveBrush} · radius {BrushRadius:0} · strength {BrushStrength:P0}",
        };
        string preview = _viewportSession.Clock.Playing
            ? $"  ·  60 FPS preview {_viewportSession.Clock.Time:0.00}s"
            : _viewportSession.Clock.Time > 0f
                ? $"  ·  paused {_viewportSession.Clock.Time:0.00}s"
                : string.Empty;
        _statusLabel.Text =
            $"{ModeCaption(ActiveMode)} · {activeTool}  ·  Affects {_componentsPanel.Affects.StatusLabel}  ·  {cursor}  ·  " +
            $"{_nature.Paths.Count} paths · {_foliage.Instances.Count:N0} foliage · {_nature.WaterBodies.Count} water · " +
            $"{_nature.PlacedEntities.Count} objects · " +
            $"{_terrain.ResolutionX}×{_terrain.ResolutionZ} @ {_terrain.CellSize:0.##}u" +
            preview + (_viewport.ControlMethod == EditorCameraControlMethod.Free ? " · RMB look · MMB pan · wheel travel" : " · RMB orbit · MMB pan · wheel zoom") +
            (SelectedComponentGizmoOrigin() is not null
                ? $"  ·  {EditorTransformGizmo.StatusHint(_viewportSession.GizmoMode, _viewportSession.GizmoSpace, _viewportSession.SnapToGrid)}"
                : string.Empty);
        _generateSummary.Text =
            $"{_settings.Preset}\nSeed {_settings.Seed} · {_terrain.ResolutionX}×{_terrain.ResolutionZ}\n" +
            $"Erosion {_settings.ErosionIterations} · terraces {_settings.TerraceStrength:P0} · rivers {_settings.RiverCount}";
        _pathsSummary.Text =
            $"{_nature.Paths.Count} connected path(s)\nWidth {_nature.PathSettings.Width:0.#} · seed {_nature.PathSettings.Seed}\n" +
            "Paths grade terrain and exclude foliage.";
        _foliageSummary.Text =
            $"{_foliage.Instances.Count:N0} resident instance(s)\n{_nature.FoliageSettings.Preset} · seed {_nature.FoliageSettings.Seed}\n" +
            $"{_foliageBrush.Mode} {_foliageBrush.Species} · {_foliageBrush.Density:P0} density.";
        _waterSummary.Text =
            $"{_nature.WaterBodies.Count} water body/bodies\n{_waterTool} · {_pendingWaterKind} · masks, spline rivers, waterfalls, and contour basins\n" +
            (PlayableIsActive
                ? $"Playable {PlayableMotorState} · submerged {PlayableSubmergedFraction:P0} · y {PlayablePosition.Y:0.00}"
                : "Drop into Water previews buoyancy on the live collider.");
        _environmentSummary.Text =
            $"Fog {(_fogEnabled ? "on" : "off")} · wireframe {(_wireframe ? "on" : "off")}\n" +
            "World-map export includes terrain, paths, foliage, water, and discovery data.";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CloseEntityWizard(trashPending: true);
            _entitiesSplit?.Dispose();
            _creationPanel?.Dispose();
            _referenceTips.Dispose();
            _foliageScatterPage?.Dispose();
            DisposePhysicsPreview();
            _placedModelPreview.InvalidateAssets();
            ReleaseNatureMeshes();
            ReleaseTerrainMeshes();
        }

        base.Dispose(disposing);
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged();
        _statusLabel.BackColor = EditorChrome.Surface;
        _statusLabel.ForeColor = EditorChrome.Muted;
        _layerListBox.BackColor = EditorChrome.Surface;
        _layerListBox.Invalidate();
    }
}
