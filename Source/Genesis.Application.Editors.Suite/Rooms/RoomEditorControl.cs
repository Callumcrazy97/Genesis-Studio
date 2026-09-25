using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Editing;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Rendering.Meshes;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Assets;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Scene-centric Room Editor (Plan Phase 4): 2D/3D projection toggle, object palette
/// placement (Ctrl places repeatedly), click selection with bounding box, and
/// move/rotate/scale gizmos with grid snapping. Documents are the runtime
/// <see cref="RoomAsset"/> format, so saved rooms play with F5 without migration.
/// </summary>
public sealed partial class RoomEditorControl : EditorSurfaceControl, IEditCommandTarget,
    IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    public enum RoomTool
    {
        Select,
        Place,
        Paint,
    }

    public enum GizmoKind
    {
        Move,
        Rotate,
        Scale,
    }

    private enum DragKind
    {
        None,
        Move,
        Rotate,
        ScaleHandle,
        Axis3D,
        Rotate3DY,
        Scale3DAxis,
    }

    /// <summary>Which resource list the left palette shows — Terrain placement is 3D-only,
    /// tile painting is 2D-only.</summary>
    private enum PaletteMode
    {
        Objects,
        Terrain,
        Tiles,
        Backgrounds,
    }

    private sealed class NodeVisual
    {
        public string? ImagePath;
        public int Width = 32;
        public int Height = 32;
        public float OriginX;
        public float OriginY;
        public bool HasModel;
        public bool HasFlow;
        /// <summary>Resolved <c>.model.json</c> path, so the placed object can be drawn with its
        /// real geometry rather than a placeholder box.</summary>
        public string? ModelPath;
        /// <summary>Authored project-relative binding consumed by the same model registry as F5.</summary>
        public string? ModelAsset;
        public string MaterialAsset = string.Empty;
        public float ModelScaleX = 1f;
        public float ModelScaleY = 1f;
        public float ModelScaleZ = 1f;
        public bool CastShadows = true;
        public bool ReceiveShadows = true;
        public float Alpha = 1f;
        public string AnimationClip = string.Empty;
        public float AnimationFps = 60f;
        public bool AnimationPlaying;
        public bool AnimationLoop = true;
        public RenderColor Tint = new(0.55f, 0.65f, 0.95f);
    }

    /// <summary>
    /// A loaded Terrain resource's read-only geometry for placement picks and bounds. Rendering
    /// uses RoomTerrainSubsystem's chunked ground, material and nature composition, just as F5 does.
    /// </summary>
    private sealed class TerrainPreview
    {
        public TerrainAsset? Asset;
        public float HalfExtentX;
        public float HalfExtentZ;
        public float MinHeight;
        public float MaxHeight;
    }


    private readonly RoomAsset _room;
    private readonly EditorViewport3D _viewport;
        private readonly TextBox _paletteSearch;
    private readonly ListView _paletteList;
    private readonly TilePickerPanel _tilePicker;
    private readonly Label _tilePickerLabel;
    private readonly Panel _tilePickerHost;
    private readonly Label _paletteHintLabel;
    private readonly Button _paletteObjectsButton;
    private readonly Button _paletteTerrainButton;
    private readonly Button _paletteTilesButton;
    private readonly Button _paletteBackgroundsButton;
    private readonly CheckedListBox _layerList;
    private readonly Label _placementLabel;
    private readonly Label _statusLabel;
    private readonly EditorDimensionChrome.DimensionToggle _dimensionToggle;
    private readonly ToolStripButton _selectToolButton;
    private readonly ToolStripButton _placeToolButton;
    private readonly ToolStripMenuItem _paintTilesMenuItem;
    private readonly ToolStripButton _cameraToolButton;
    private readonly ToolStripButton _moveGizmoButton;
    private readonly ToolStripButton _rotateGizmoButton;
    private readonly ToolStripButton _scaleGizmoButton;
    private readonly ToolStripButton _localGizmoButton;
    private readonly ToolStripButton _snapButton;
    private readonly ToolStripTextBox _gridSizeBox;

    private readonly Panel _inspectorPanel;
    private readonly TextBox _nameBox;
    private readonly NumericUpDown[] _position = new NumericUpDown[3];
    private readonly NumericUpDown[] _rotation = new NumericUpDown[3];
    private readonly NumericUpDown[] _scale = new NumericUpDown[3];
    private readonly ThemedComboBox _nodeLayerCombo;
    private readonly ThemedComboBox _nodeParentCombo;
    private readonly Button _setGameCameraButton;
    private readonly ToolStripComboBox _sceneViewBox;
    private readonly ToolStripItem _playButton;
    private readonly SplitContainer _mainSplit;
    private readonly SplitContainer _centerSplit;
    private readonly ToolStripButton _paletteToggle;
    private readonly ToolStripButton _inspectorToggle;
    private bool _narrowLayout;
    private bool _palettePanelVisible = true;
    private bool _inspectorPanelVisible = true;

    /// <summary>Node id the Scene dropdown is looking through, or empty for the editor's own camera.</summary>
    private string _sceneViewNodeId = string.Empty;
    private readonly CheckBox _nodeEnabledCheck;
    private readonly NumericUpDown _roomWidth;
    private readonly NumericUpDown _roomHeight;

    private readonly ViewportTextureCache _textures = new();
    private readonly Dictionary<string, NodeVisual> _visualCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TerrainPreview> _terrainPreviewCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Baked geometry for each referenced model resource, so a placed model draws its real shape.
    /// Keyed by resolved path; one entry is shared by every instance of that model. Models with an
    /// animated-UV ("flow") material keep their vertices so the UVs can be re-scrolled per frame.
    /// </summary>
    private sealed class PlacedModelMesh
    {
        public MeshHandle Mesh;
        public MeshVertex[] Vertices = [];
        public ushort[] Indices = [];
        public ModelUvAnimator? Flow;
        public TextureHandle FlowTexture;
    }

    private readonly Dictionary<string, PlacedModelMesh> _modelMeshCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly RuntimeModelRenderSystem _runtimeModelPreview = new();
    private bool _assetRefreshPending;
    private float _roomTime;
    private bool _roomTimePaused = true;
    private readonly List<ProjectAssetEntry> _paletteEntries = [];
    private readonly List<ProjectAssetEntry> _terrainPaletteEntries = [];
    private readonly List<ProjectAssetEntry> _tileSetPaletteEntries = [];
    private readonly List<ProjectAssetEntry> _spritePaletteEntries = [];
    private readonly List<ProjectAssetEntry> _backgroundPaletteEntries = [];

    // ── Tile painting ───────────────────────────────────────────────────────────
    private string? _activeTileSetPath;
    private TileSetInfo? _activeTileSet;
    private RoomNode? _activeTileLayer;
    private int _activeTileIndex;

    private PaletteMode _paletteMode = PaletteMode.Objects;
    private RoomNode? _selected;
    private RoomNode? _hover;
    private string? _pendingPlacementPath;

    /// <summary>
    /// Where the armed object would land if the pointer clicked now, or null when nothing is armed
    /// or the pointer is outside the viewport. Drives the translucent placement preview.
    /// </summary>
    private Vector2? _ghostWorld;
    private RoomNodeKind _pendingPlacementKind = RoomNodeKind.GameObject;
    private DragKind _drag = DragKind.None;
    private int _dragData;                    // handle index / axis index
    private Vector2 _dragStartWorld;
    private Point _dragStartClient;
    private RoomTransform? _dragStartTransform;
    private Vector3 _dragAxisWorld;
    private float _dragAxisWorldPerPixel;
    private Vector2 _dragAxisScreenDir;
    private bool _syncingInspector;
    private int _placeCounter;
    private MeshHandle _unitCube;
    private MeshHandle _unitQuad;

    private readonly RoomPlacementController _placement;
    private readonly RoomEditorToolbar _editorToolbar;
    private readonly RoomEditorNavigation _navigation;
    private readonly RoomInspectorPanel _inspector;
    private readonly RoomViewportOverlay _viewportOverlay;
    private readonly Panel _viewportBottomBar;
    private readonly Label _viewportCoordsLabel;
    private readonly Label _zoomLabel;

    public bool ShowRoomBounds { get; set; } = true;
    public Color RoomBoundsColor { get; set; } = Color.FromArgb(64, 128, 255);
    public RoomPlacementController Placement => _placement;
    public RoomEditorNavigation Navigation => _navigation;
    public RoomInspectorPanel Inspector => _inspector;
    public RoomEditorToolbar EditorToolbar => _editorToolbar;

    public RoomEditorControl(string resourcePath, string projectRoot)
        : base(resourcePath, projectRoot)
    {
        _room = LoadRoom();
        Dock = DockStyle.Fill;

        _placement = new RoomPlacementController(this);
        _editorToolbar = new RoomEditorToolbar();
        _navigation = new RoomEditorNavigation(this);
        _inspector = new RoomInspectorPanel(this);
        _viewportOverlay = new RoomViewportOverlay(this);

        // ── Toolbar ──────────────────────────────────────────────────────────────
        EditorCommandBar toolbar = EditorChrome.MakeToolbar();
        _selectToolButton = EditorChrome.ToolButton("Select", "Select instances; drag their body to move (Q)", () => SetTool(RoomTool.Select), toggle: true);
        _placeToolButton = EditorChrome.ToolButton("Place", "Place the chosen object (click a palette object first)", () => SetTool(RoomTool.Place), toggle: true);
        _cameraToolButton = EditorChrome.ToolButton("Camera", "Toggle the active game-camera preview", ToggleCameraMode, toggle: true);
        _moveGizmoButton = EditorChrome.ToolButton("Move", "Move gizmo (W)", () => SetGizmo(GizmoKind.Move), toggle: true);
        _rotateGizmoButton = EditorChrome.ToolButton("Rotate", "Rotate gizmo (E)", () => SetGizmo(GizmoKind.Rotate), toggle: true);
        _scaleGizmoButton = EditorChrome.ToolButton("Scale", "Scale gizmo (R)", () => SetGizmo(GizmoKind.Scale), toggle: true);
        _localGizmoButton = EditorChrome.ToolButton("Local", "Toggle local versus world gizmo axes (X)", ToggleGizmoSpace, toggle: true);
        _snapButton = EditorChrome.ToolButton("Snap", "Snap placement and movement to the grid (G)", ToggleSnap, toggle: true);
        _snapButton.Checked = _room.Settings.SnapEnabled;
        _gridSizeBox = new ToolStripTextBox { AutoSize = false, Width = 44, Text = _room.Settings.GridSize.ToString("0.#") };
        _gridSizeBox.Leave += (_, _) => ApplyGridSizeText();
        _gridSizeBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                ApplyGridSizeText();
                args.Handled = true;
                args.SuppressKeyPress = true;
            }
        };

        // Toolbar grouping is by intent. File/Edit match the other 3D editors; projection and
        // transform state stay inline; clipboard actions live under Edit; Paint tiles is 2D-only
        // and lives there too so 3D mode stays uncluttered.
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildFileMenu(this));
        ToolStripMenuItem paintTiles = MenuAction(
            "Paint tiles (2D)",
            "T",
            "Open the tile palette and paint a 2D tile layer",
            ActivatePaintMode);
        _paintTilesMenuItem = paintTiles;
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildEditMenu(this,
        [
            MenuAction("Delete", "Del", "Delete every selected, unlocked instance", DeleteSelected),
            MenuAction("Copy", "Ctrl+C", "Copy selected instances", () => CopySelected()),
            MenuAction("Paste", "Ctrl+V", "Paste copied instances", () => PasteCopied()),
            MenuAction("Duplicate", "Ctrl+D", "Duplicate selected instances", DuplicateSelected),
            MenuAction("Snap to floor", "End", "Drop selected 3D instances onto terrain, objects, or the room floor", () => SnapSelectionToFloor()),
            new ToolStripSeparator(),
            MenuAction("Frame contents", "F", "Frame the room contents", FrameContent),
            new ToolStripSeparator(),
            paintTiles,
        ]));

        _editorToolbar.DimensionChanged += is2D => ViewMode3D = !is2D;
        _editorToolbar.ZoomInRequested += () => ZoomViewport(1.25f);
        _editorToolbar.ZoomOutRequested += () => ZoomViewport(0.8f);
        _editorToolbar.ResetViewRequested += ResetViewportView;
        _editorToolbar.GridToggled += SetGridVisible;
        _editorToolbar.SnapToggled += SetSnapEnabled;
        _editorToolbar.SnapSizeChanged += value => { if (ViewMode3D) SetMetricGridSize(value); else SetGridSize(value); };
        _editorToolbar.PlayRequested += () => { if (_roomPlayer?.IsRunning == true) _roomPlayer.Resume(); else PlayRoom(); SyncPlayerToolbar(); };
        _editorToolbar.PauseRequested += () => { _roomPlayer?.Pause(); SyncPlayerToolbar(); };
        _editorToolbar.StopRequested += () => { _roomPlayer?.Dispose(); _roomPlayer = null; SyncPlayerToolbar(); UpdateStatus("Game stopped."); };
        _editorToolbar.PlayExternalRequested += () => PlayRoom();
        Disposed += (_, _) => _roomPlayer?.Dispose();

        _editorToolbar.CameraMenuOpening += () =>
        {
            _editorToolbar.CameraDropdown.DropDownItems.Clear();
            AddCameraControlChoices(_editorToolbar.CameraDropdown);
            ToolStripMenuItem showGameCamera = new("Show game camera in inset")
            {
                ToolTipText = "Keep the editor view and preview the room camera in the top-right inset",
            };
            showGameCamera.Click += (_, _) => ShowGameCameraInInset();
            _editorToolbar.CameraDropdown.DropDownItems.Add(showGameCamera);

            ToolStripMenuItem frameContent = new("Frame room contents (F)");
            frameContent.Click += (_, _) => FrameContent();
            _editorToolbar.CameraDropdown.DropDownItems.Add(frameContent);

            ToolStripMenuItem resetCamera = new("Reset camera view");
            resetCamera.Click += (_, _) => ResetViewportView();
            _editorToolbar.CameraDropdown.DropDownItems.Add(resetCamera);
        };

        _navigation.ObjectsPanel.ObjectArmed += path => BeginPlacement(path);
        _navigation.ObjectsPanel.InstanceSelected += node => Select(node);
        _navigation.TilesetsPanel.TileSelected += (info, index) =>
        {
            RoomNode? target = _navigation.TilesetsPanel.ActiveTileLayer;
            if (target is null || !CanEditNodeInActiveContext(target)) return;
            _activeTileLayer = target;
            _activeTileSet = info;
            _activeTileSetPath = info.ImagePath;
            _activeTileIndex = index;
            ActiveTool = RoomTool.Paint;
            UpdateStatus($"Painting with tile {index}" + (_activeTileSet.IsSolid(index) ? " (solid)" : string.Empty));
        };
        _inspector.CloseRequested += () => SetInspectorVisible(false);
        toolbar.Items.Add(new ToolStripSeparator());
        _dimensionToggle = EditorDimensionChrome.AddTo(
            toolbar,
            () => !ViewMode3D,
            is2D => ViewMode3D = !is2D,
            caption: null);
        // Projection and grid controls live beside the viewport transport.
        _dimensionToggle.Button2D.Available = false;
        _dimensionToggle.Button3D.Available = false;
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(ToolbarCaption("Tools"));
        toolbar.Items.Add(_selectToolButton);
        toolbar.Items.Add(_placeToolButton);
        toolbar.Items.Add(_cameraToolButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(ToolbarCaption("Transform"));
        toolbar.Items.Add(_moveGizmoButton);
        toolbar.Items.Add(_rotateGizmoButton);
        toolbar.Items.Add(_scaleGizmoButton);
        toolbar.Items.Add(_localGizmoButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripSeparator());

        // Scene view: the editor's own free camera by default, or look through a room camera so
        // what you are composing is what the game will show.
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Scene") { ForeColor = EditorChrome.Muted });
        _sceneViewBox = new ToolStripComboBox
        {
            AutoSize = false,
            Width = 150,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _sceneViewBox.SelectedIndexChanged += (_, _) => ApplySceneView();
        toolbar.Items.Add(_sceneViewBox);

        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(BuildViewMenu());

        _paletteToggle = EditorChrome.ToolButton(
            "Palette",
            "Show the palette, layers, and room settings",
            () => { },
            toggle: true);
        _paletteToggle.Checked = true;
        _paletteToggle.Alignment = ToolStripItemAlignment.Right;
        _paletteToggle.CheckedChanged += (_, _) =>
        {
            _palettePanelVisible = _paletteToggle.Checked;
            ApplyResponsiveLayout();
        };
        _inspectorToggle = EditorChrome.ToolButton(
            "Inspector",
            "Show the instance and room inspector",
            () => { },
            toggle: true);
        _inspectorToggle.Checked = true;
        _inspectorToggle.Alignment = ToolStripItemAlignment.Right;
        _inspectorToggle.CheckedChanged += (_, _) =>
        {
            _inspectorPanelVisible = _inspectorToggle.Checked;
            ApplyResponsiveLayout();
        };
        _paletteToggle.Visible = false;
        _inspectorToggle.Visible = false;
        toolbar.Items.Add(_paletteToggle);
        toolbar.Items.Add(_inspectorToggle);
        toolbar.Items.Add(new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right });

        _playButton = EditorChrome.ToolButton(
            "▶  Play",
            "Compile and run this room in the Player (F5), from the room camera if one is set",
            () => PlayRoom());
        _playButton.Alignment = ToolStripItemAlignment.Right;
        _playButton.ForeColor = EditorChrome.Success;
        _playButton.Font = new Font(EditorChrome.BaseFont, FontStyle.Bold);
        _playButton.Padding = new Padding(8, 0, 8, 0);
        _placementLabel = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Dock = DockStyle.Bottom,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 0,
            Padding = new Padding(8, 2, 4, 2),
            Text = string.Empty,
            Visible = false,
        };

        // ── Left: object palette + layers ───────────────────────────────────────
        // Deliberately still 300. Widening this to 330 to fit the layer button run shrank the
        // viewport enough that `Editor.Room`'s render step captured an effectively blank frame
        // ("drew 3 colours") — the side panels are already claiming most of the gate window's width.
        // The truncated labels are fixed by letting that button row *wrap* instead (see
        // RoomEditorControl.Phase4.cs), which costs the viewport nothing.
        Panel left = new()
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(240, 0),
        };
        _layerList = new CheckedListBox
        {
            BackColor = EditorChrome.Surface,
            BorderStyle = BorderStyle.None,
            CheckOnClick = true,
            Dock = DockStyle.Bottom,
            Font = EditorChrome.BaseFont,
            ForeColor = EditorChrome.Text,
            Height = 118,
        };
        _layerList.ItemCheck += OnLayerChecked;
        Button addLayer = new() { Dock = DockStyle.Bottom, Height = 26, Text = "＋ Add Layer" };
        EditorChrome.StyleField(addLayer);
        addLayer.Click += (_, _) => AddLayer();

                _paletteSearch = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "Search palette...",
        };
        EditorChrome.StyleField(_paletteSearch);
        _paletteSearch.TextChanged += delegate { RebuildPaletteListView(); };

        _paletteList = new ListView
        {
            BackColor = EditorChrome.Surface,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Font = EditorChrome.BaseFont,
            ForeColor = EditorChrome.Text,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            MultiSelect = false,
            ShowGroups = false,
            View = View.Details,
        };
        _paletteList.Columns.Add("Object", 274);
        _paletteList.SelectedIndexChanged += OnPaletteSelected;

        _paletteHintLabel = EditorChrome.SectionLabel("Objects — click, then click the room");
        Panel paletteModeRow = new() { Dock = DockStyle.Top, Height = EditorChrome.FieldHeight, Padding = new Padding(EditorChrome.CompactPadding, 2, EditorChrome.CompactPadding, 2) };
        _paletteObjectsButton = new Button { Dock = DockStyle.Left, Width = 66, Text = "Objects", FlatStyle = FlatStyle.Flat };
        _paletteTerrainButton = new Button { Dock = DockStyle.Left, Width = 66, Text = "Terrain", FlatStyle = FlatStyle.Flat };
        _paletteTilesButton = new Button { Dock = DockStyle.Left, Width = 48, Text = "Tiles", FlatStyle = FlatStyle.Flat };
        _paletteBackgroundsButton = new Button { Dock = DockStyle.Left, Width = 44, Text = "BG", FlatStyle = FlatStyle.Flat };
        EditorChrome.StyleField(_paletteObjectsButton);
        EditorChrome.StyleField(_paletteTerrainButton);
        EditorChrome.StyleField(_paletteTilesButton);
        EditorChrome.StyleField(_paletteBackgroundsButton);
        _paletteObjectsButton.Click += (_, _) => SetPaletteMode(PaletteMode.Objects);
        _paletteTerrainButton.Click += (_, _) => SetPaletteMode(PaletteMode.Terrain);
        _paletteTilesButton.Click += (_, _) => SetPaletteMode(PaletteMode.Tiles);
        _paletteBackgroundsButton.Click += (_, _) => SetPaletteMode(PaletteMode.Backgrounds);
        paletteModeRow.Controls.Add(_paletteBackgroundsButton);
        paletteModeRow.Controls.Add(_paletteTilesButton);
        paletteModeRow.Controls.Add(_paletteTerrainButton);
        paletteModeRow.Controls.Add(_paletteObjectsButton);

        // The tile picker sits under the set list, so choosing a set and then a tile reads top to
        // bottom — the order GameMaker uses and the order a designer works in.
        _tilePicker = new TilePickerPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(6),
        };
        _tilePicker.TileSelected += index =>
        {
            _activeTileIndex = index;
            UpdateStatus($"Painting with tile {index}" + (_activeTileSet?.IsSolid(index) == true ? " (solid)" : string.Empty));
        };

        // Caption and grid live in one container rather than as two bottom-docked siblings, so the
        // caption is guaranteed to sit above the grid instead of depending on sibling dock order.
        _tilePickerLabel = EditorChrome.SectionLabel("Tile — click one to paint with it");
        _tilePickerLabel.Dock = DockStyle.Top;
        _tilePickerHost = new Panel
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Bottom,
            Height = 246,
            Visible = false,
        };
        _tilePickerHost.Controls.Add(_tilePicker);
        _tilePickerHost.Controls.Add(_tilePickerLabel);

        BuildRoomPanels();

        Panel outlinerPanel = BuildPhase4OutlinerPanel(addLayer);
        Panel hierarchyPanel = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
                hierarchyPanel.Controls.Add(_paletteList);
        hierarchyPanel.Controls.Add(_paletteSearch);
        hierarchyPanel.Controls.Add(_paletteHintLabel);
        hierarchyPanel.Controls.Add(_tilePickerHost);
        hierarchyPanel.Controls.Add(paletteModeRow);
        hierarchyPanel.Controls.Add(outlinerPanel);

        TabControl leftTabs = new() { Dock = DockStyle.Fill };
        TabPage hierarchyPage = new("Palette") { BackColor = EditorChrome.Surface };
        TabPage viewsPage = new("Views") { BackColor = EditorChrome.Surface };
        TabPage roomSettingsPage = new("Room") { BackColor = EditorChrome.Surface };

        hierarchyPage.Controls.Add(hierarchyPanel);
        viewsPage.Controls.Add(_viewportsPanel);
        roomSettingsPage.Controls.Add(_roomSettingsPanel);

        leftTabs.TabPages.Add(hierarchyPage);
        leftTabs.TabPages.Add(viewsPage);
        leftTabs.TabPages.Add(roomSettingsPage);
        left.Controls.Add(leftTabs);

        // ── Right: inspector ─────────────────────────────────────────────────────
        _inspectorPanel = new Panel
        {
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(240, 0),
        };
        _nameBox = new TextBox { Width = 216 };
        _nodeLayerCombo = new ThemedComboBox { Width = 216 };
        _nodeParentCombo = new ThemedComboBox { Width = 216, DisplayMember = "Name" };
        _setGameCameraButton = new Button { Text = "Set as game camera", Width = 216, Height = 26 };
        _nodeEnabledCheck = new CheckBox { Text = "Enabled", ForeColor = EditorChrome.Text, BackColor = Color.Transparent, AutoSize = true };
        _roomWidth = MakeNumeric(1, 100000, 0);
        _roomHeight = MakeNumeric(1, 100000, 0);
        for (int i = 0; i < 3; i++)
        {
            _position[i] = MakeNumeric(-1000000, 1000000, 2);
            _rotation[i] = MakeNumeric(-36000, 36000, 1);
            _scale[i] = MakeNumeric(-1000, 1000, 3);
        }

        BuildInspectorLayout();

        // ── Center: viewport ─────────────────────────────────────────────────────
        _viewport = new EditorViewport3D
        {
            Mode2D = _room.Dimension == RoomDimension.TwoD,
            Background2D = () =>
            {
                float[]? bg = _room.Environment.BackgroundColor;
                return bg is { Length: >= 3 } ? (bg[0], bg[1], bg[2]) : (0.07f, 0.09f, 0.14f);
            },
            SceneStateFactory = CreateRoomSceneLighting,
        };
        _viewport.Camera2DX = _room.Settings.Width * 0.5f;
        _viewport.Camera2DY = _room.Settings.Height * 0.5f;
        _viewport.Zoom2D = 0.8f;
        _viewport.Camera.Target = new Vector3(0f, 1.5f, 0f);
        _viewport.Camera.Distance = 30f;
        _viewport.DrawScene2D += DrawRoom2D;
        _viewport.DrawScene += DrawRoom3D;
        _viewport.DrawOverlay += DrawOverlay;
        _viewport.FloorStyle = _floorStyle;
        _viewport.Host.MouseDown += (_, e) => EditorPointerDown(e.Location, e.Button, ModifierKeys);
        _viewport.Host.MouseMove += (_, e) => EditorPointerMove(e.Location, e.Button, ModifierKeys);
        _viewport.Host.MouseUp += (_, e) => EditorPointerUp(e.Location, e.Button, ModifierKeys);
        _viewport.ControlMethod = EditorCameraControlMethod.Free;
        _viewport.MiddleButtonPans = true;
        _viewport.WheelInputHandler = HandleSelectionWheel;
        _viewport.SelectionWorldPoint = () => _selected is null ? null : NodePosition3D(_selected);
        ToolStripMenuItem showGameCamera = new("Show game camera in inset")
        {
            ToolTipText = "Keep the editor view and preview the room camera in the top-right inset",
        };
        showGameCamera.Click += (_, _) => ShowGameCameraInInset();

        _statusLabel = EditorChrome.MakeStatusBar();

        _viewportBottomBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            BackColor = EditorChrome.Surface,
            Padding = new Padding(8, 2, 8, 2),
        };
        _viewportCoordsLabel = new Label
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Text = "X: 0.0, Y: 0.0",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _zoomLabel = new Label
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Text = "Zoom: 80%",
            TextAlign = ContentAlignment.MiddleRight,
        };
        _viewportBottomBar.Controls.Add(_viewportCoordsLabel);
        _viewportBottomBar.Controls.Add(_zoomLabel);

        Panel viewportHost = new()
        {
            Dock = DockStyle.Fill,
            BackColor = EditorChrome.Canvas,
        };
        _viewport.Dock = DockStyle.Fill;
        viewportHost.Controls.Add(_viewport);
        viewportHost.Controls.Add(_viewportBottomBar);

        Panel rightDock = new()
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(240, 0),
        };
        rightDock.Controls.Add(_inspector);
        rightDock.Controls.Add(_inspectorPanel);
        _inspectorPanel.Visible = false;

        _centerSplit = new SplitContainer
        {
            BackColor = EditorChrome.Canvas,
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            Orientation = Orientation.Vertical,
            Panel1MinSize = 0,
            Panel2MinSize = 0,
            SplitterWidth = EditorChrome.SplitterWidth,
        };
        _centerSplit.Panel1.Controls.Add(viewportHost);
        _centerSplit.Panel2.Controls.Add(rightDock);

        _mainSplit = new SplitContainer
        {
            BackColor = EditorChrome.Canvas,
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Orientation = Orientation.Vertical,
            Panel1MinSize = 0,
            Panel2MinSize = 0,
            SplitterWidth = EditorChrome.SplitterWidth,
        };
        _mainSplit.Panel1.Controls.Add(_navigation);
        _mainSplit.Panel1.Controls.Add(left);
        left.Visible = false;
        _mainSplit.Panel2.Controls.Add(_centerSplit);

        Controls.Add(_mainSplit);
        Controls.Add(_placementLabel);
        BuildRoomWorkspace(toolbar, viewportHost);
        Controls.Add(toolbar);
        Disposed += (_, _) => _editorToolbar.Dispose();
        Controls.Add(_statusLabel);

        SizeChanged += (_, _) => QueueResponsiveRoomLayout();
        HandleCreated += (_, _) => QueueResponsiveRoomLayout();

        RefreshPalette();
        RefreshLayers();
        RefreshSceneViews();
        SyncToolbar();
        SyncInspector();
        UpdatePaletteModeButtonStyles();
        UpdateStatus(null);
        UpdateZoomStatus();
        _roomUiTopology = RoomUiTopology();
    }

    // ── Public editing surface (used by input handlers, headless tests, shortcuts) ──

    public RoomAsset Room => _room;

    public void SetRoomBoundsVisible(bool visible)
    {
        ShowRoomBounds = visible;
        _viewport?.Host?.Invalidate();
    }

    public void SetRoomBackgroundColor(float[] color)
    {
        float[] before = (float[])_room.Environment.BackgroundColor.Clone();
        float[] after = (float[])color.Clone();
        if (before.SequenceEqual(after)) return;
        void Apply(float[] value) { _room.Environment.BackgroundColor = (float[])value.Clone(); SyncInspector(); _viewport.Host.Invalidate(); }
        Apply(after);
        PushEdit("Room background colour", () => Apply(after), () => Apply(before));
    }

    public void SetSnapEnabled(bool enabled)
    {
        bool before = _room.Settings.SnapEnabled;
        if (before == enabled) return;
        void Apply(bool value)
        {
            _room.Settings.SnapEnabled = value; _placement.SnapToGrid = value;
            _snapButton.Checked = value; SyncToolbar(); SyncInspector(); _viewport.Host.Invalidate();
        }
        Apply(enabled);
        PushEdit("Snap to grid", () => Apply(enabled), () => Apply(before));
    }

    public void SetGridSize(float size)
    {
        float before = _room.Settings.GridSize;
        float after = Math.Clamp(size, 1f, 4096f);
        if (before == after) return;
        void Apply(float value)
        {
            _room.Settings.GridSize = value; _gridSizeBox.Text = value.ToString("0.#");
            SyncToolbar(); SyncInspector(); _viewport.Host.Invalidate();
        }
        Apply(after);
        PushEdit("Grid size", () => Apply(after), () => Apply(before));
    }

    public void SetInspectorVisible(bool visible)
    {
        _inspectorPanelVisible = visible;
        _centerSplit.Panel2Collapsed = !visible;
        if (_inspectorToggle is not null)
        {
            _inspectorToggle.Checked = visible;
        }
    }

    public new void MarkDirty() => base.MarkDirty();

    public void PushEdit(string label, Action apply, Action revert) => base.PushEdit(label, apply, revert);

    public void AdvanceRoomTime(float dt)
    {
        _roomTime += dt;
        foreach (RoomNode node in _room.Nodes)
        {
            if (node.Kind == RoomNodeKind.Background && node.Background is not null)
            {
                node.Background.Scroll ??= [0f, 0f];
            }
        }
    }

    public void RestoreRoomSnapshot(RoomAsset snapshot)
    {
        _room.Nodes.Clear();
        foreach (RoomNode n in snapshot.Nodes)
        {
            _room.Nodes.Add(CloneNode(n));
        }
        _room.Settings.Width = snapshot.Settings.Width;
        _room.Settings.Height = snapshot.Settings.Height;
        _room.Settings.Depth = snapshot.Settings.Depth;
        _room.Settings.TargetFps = snapshot.Settings.TargetFps;
        _room.Settings.FixedFps = snapshot.Settings.FixedFps;
        _room.Settings.Persistent = snapshot.Settings.Persistent;
        _room.Settings.SnapEnabled = snapshot.Settings.SnapEnabled;
        _room.Settings.GridSize = snapshot.Settings.GridSize;
        _room.Settings.MetricGridSize = snapshot.Settings.MetricGridSize;
        _room.Settings.SnapToTerrain = snapshot.Settings.SnapToTerrain;
        _room.Settings.AlignToTerrainNormal = snapshot.Settings.AlignToTerrainNormal;
        _room.Environment.BackgroundColor = (float[]?)snapshot.Environment.BackgroundColor?.Clone() ?? [0.07f, 0.09f, 0.14f];
        _room.Viewports.Clear();
        foreach (RoomViewport vp in snapshot.Viewports)
        {
            _room.Viewports.Add(vp.Clone());
        }
        RefreshPalette();
        RefreshLayers();
        RefreshOutliner();
        _navigation?.ObjectsPanel?.RefreshRoomInstances();
        _inspector?.InspectNode(_selected);
    }

    public void AddGameObjectFromPicker()
    {
        ProjectAssetEntry? entry = AssetPickerService.PickObject(ProjectRoot, this);
        if (entry is not null)
        {
            BeginPlacement(entry.FullPath);
            _navigation?.ObjectsPanel?.SyncSelection();
        }
    }

    public void ZoomViewport(float factor)
    {
        if (_viewport.Mode2D)
        {
            _viewport.Zoom2D = Math.Clamp(_viewport.Zoom2D * factor, 0.05f, 32f);
        }
        else
        {
            if (_viewport.ControlMethod == EditorCameraControlMethod.Free)
                _viewport.Camera.Travel(MathF.Log(factor) / MathF.Log(1.25f));
            else _viewport.Camera.Distance = Math.Clamp(_viewport.Camera.Distance / factor, 1f, 500f);
        }
        UpdateZoomStatus();
        _viewport.Host.Invalidate();
    }

    public void ResetViewportView()
    {
        _viewport.Camera2DX = _room.Settings.Width * 0.5f;
        _viewport.Camera2DY = _room.Settings.Height * 0.5f;
        _viewport.Zoom2D = 0.8f;
        _viewport.Camera.Target = new Vector3(0f, 1.5f, 0f);
        _viewport.Camera.Distance = 30f;
        UpdateZoomStatus();
        _viewport.Host.Invalidate();
    }

    private void UpdateZoomStatus()
    {
        if (_zoomLabel is not null)
        {
            int percent = (int)MathF.Round((_viewport.Mode2D ? _viewport.Zoom2D : 30f / MathF.Max(1f, _viewport.Camera.Distance)) * 100f);
            _zoomLabel.Text = $"Zoom: {percent}%";
        }
    }

    public bool IsNarrowLayout => _narrowLayout;

    public RoomNode? SelectedNode => _selected;

    public RoomTool ActiveTool { get; private set; } = RoomTool.Select;

    public GizmoKind Gizmo { get; private set; } = GizmoKind.Move;

    private bool _transformToolActive;
    public bool TransformGizmoVisible => ActiveTool == RoomTool.Select && _transformToolActive;

    public EditorGizmoSpace GizmoSpace { get; private set; } = EditorGizmoSpace.World;

    public string? PendingPlacementPath => _pendingPlacementPath;

    public EditorViewport3D Viewport => _viewport;

    public bool ViewMode3D
    {
        get => _room.Dimension == RoomDimension.ThreeD;
        set
        {
            RoomDimension next = value ? RoomDimension.ThreeD : RoomDimension.TwoD;
            if (_room.Dimension != next)
            {
                RoomDimension previous = _room.Dimension;
                ApplyRoomDimension(next);
                PushEdit(
                    value ? "Switch room to 3D" : "Switch room to 2D",
                    () => ApplyRoomDimension(next),
                    () => ApplyRoomDimension(previous));
            }
            else ApplyRoomDimension(next);
        }
    }

    private void ApplyRoomDimension(RoomDimension next)
    {
        _room.Dimension = next;
        _viewport.Mode2D = next == RoomDimension.TwoD;
        if (next == RoomDimension.TwoD)
        {
            // Terrain has no 2D projection. Drop its placement and selection before the
            // sprite-based 2D gizmo runs; undo and redo must take this same UI path.
            if (_paletteMode == PaletteMode.Terrain)
            {
                SetPaletteMode(PaletteMode.Objects);
            }

            SetSelection(_selection.Where(node => node.Supports(RoomDimension.TwoD) && node.Kind != RoomNodeKind.Terrain));
        }
        else if (ActiveTool == RoomTool.Paint || _paletteMode == PaletteMode.Tiles)
        {
            _activeTileLayer = null;
            _paletteMode = PaletteMode.Objects;
            ActiveTool = RoomTool.Select;
            RefreshPalette();
        }

        if (IsGameCameraPreview) RefreshCameraPreviewIfNeeded();
        UpdatePaletteModeButtonStyles();
        SyncToolbar();
        UpdateStatus(null);
    }

    public override void Save()
    {
        _room.Name = ResourceDisplayName.Format(ResourcePath);
        RoomAssetLoader.Save(_room, ResourcePath);
        AcceptSave();
    }

    /// <summary>Switches the active gizmo (toolbar, W/E/R keys, headless tests).</summary>
    public void SetGizmo(GizmoKind gizmo)
    {
        SetTool(RoomTool.Select);
        _transformToolActive = true;
        Gizmo = gizmo;
        SyncToolbar();
        _viewport.Host.Invalidate();
        UpdateStatus(null);
    }

    public void SetGizmoSpace(EditorGizmoSpace space)
    {
        GizmoSpace = space;
        SyncToolbar();
        _viewport.Host.Invalidate();
        UpdateStatus(null);
    }

    public void SetTool(RoomTool tool)
    {
        if (tool != ActiveTool) CancelActiveRoomGesture();
        ActiveTool = tool;
        _transformToolActive = false;
        SyncToolbar();
        _viewport.Host.Invalidate();
        UpdateStatus(null);
    }

    private void ActivatePaintMode()
    {
        if (ViewMode3D)
        {
            ViewMode3D = false;
        }

        _navigation.SetSection(RoomNavSection.Tilesets);
        SetPaletteMode(PaletteMode.Tiles);
        _activeTileLayer = _navigation.TilesetsPanel.ActiveTileLayer;
        ActiveTool = RoomTool.Paint;
        SyncToolbar();
        UpdateStatus(_activeTileLayer is null
            ? "Choose a tile set, then drag in the room to paint."
            : "Drag LMB to paint tiles; drag RMB to erase.");
    }

    private void ToggleCameraMode()
    {
        if (IsGameCameraPreview)
        {
            ExitGameCameraPreview();
        }
        else if (!PreviewGameCamera())
        {
            UpdateStatus("Choose a camera instance and set it as the game camera first.");
        }

        SyncToolbar();
    }

    /// <summary>Maps a 2D world position to viewport client coordinates (test driving).</summary>
    public Point ClientFromWorld2D(Vector2 world)
    {
        Vector2 surface = _viewport.World2DToSurface(world);
        PointF scale = _viewport.ControlToSurface(new Point(1, 1));
        return new Point(
            (int)MathF.Round(surface.X / MathF.Max(0.0001f, scale.X)),
            (int)MathF.Round(surface.Y / MathF.Max(0.0001f, scale.Y)));
    }

    /// <summary>Maps a 3D world position to viewport client coordinates (test driving).</summary>
    public Point ClientFromWorld3D(Vector3 world)
    {
        Vector3 surface = _viewport.WorldToSurface(world);
        PointF scale = _viewport.ControlToSurface(new Point(1, 1));
        return new Point(
            (int)MathF.Round(surface.X / MathF.Max(0.0001f, scale.X)),
            (int)MathF.Round(surface.Y / MathF.Max(0.0001f, scale.Y)));
    }

    /// <summary>Arms the Place tool with an object resource (palette click or test hook).</summary>
    public void BeginPlacement(string objectResourceFullPath) => BeginPlacement(objectResourceFullPath, RoomNodeKind.GameObject);

    /// <summary>Arms the Place tool with a Terrain resource. 3D-only — a heightmap has no 2D projection.</summary>
    public void BeginPlacementTerrain(string terrainResourceFullPath)
    {
        if (!ViewMode3D)
        {
            UpdateStatus("Switch to 3D mode to place Terrain.");
            return;
        }

        BeginPlacement(terrainResourceFullPath, RoomNodeKind.Terrain);
    }

    private void BeginPlacement(string resourceFullPath, RoomNodeKind kind)
    {
        if (!CanPlaceInActiveContext(kind))
        {
            UpdateStatus("Select the matching resource tab and an unlocked layer before placing resources.");
            return;
        }
        _lastSurfacePlacement = null;
        ClearSurfacePlacementPreview();
        _pendingPlacementPath = resourceFullPath;
        _pendingPlacementKind = kind;
        _placement.ArmObject(resourceFullPath);

        // Arming a placement *is* leaving tile mode — the mirror of BeginTilePainting arming it.
        // Without this, a click after painting tiles was swallowed by the tile brush (pointer-down
        // checks IsTilePainting first), so the object silently never landed (NEXT-045). Go through
        // SetPaletteMode so the palette buttons and hint text follow, rather than leaving "Tiles"
        // lit above a list of objects.
        if (_paletteMode == PaletteMode.Tiles)
        {
            SetPaletteMode(kind == RoomNodeKind.Terrain ? PaletteMode.Terrain : PaletteMode.Objects);
        }

        ActiveTool = RoomTool.Place;
        SyncToolbar();
        UpdateStatus($"Placing '{ResourceDisplayName.Format(resourceFullPath)}' — click the room (hold Ctrl to place many, Esc to stop)");
    }

    public void CancelPlacement()
    {
        _lastSurfacePlacement = null;
        ClearSurfacePlacementPreview();
        _pendingPlacementPath = null;
        _pendingPlacementKind = RoomNodeKind.GameObject;
        _ghostWorld = null;
        _placement.Disarm();
        ActiveTool = RoomTool.Select;
        SyncToolbar();
        UpdateStatus(null);
    }

    /// <summary>
    /// Where the placement preview currently sits, or null when nothing is armed or the pointer has
    /// not entered the viewport yet.
    /// </summary>
    public Vector2? PlacementGhostWorld => _ghostWorld ?? _placement.GhostWorld;

    /// <summary>
    /// Moves the pointer to a world position without a real mouse, so the preview tracks it.
    /// </summary>
    /// <remarks>
    /// A purpose-built test-driving hook, per the project convention: it goes through
    /// <see cref="ClientFromWorld2D"/> and the real <see cref="EditorPointerMove"/> handler rather
    /// than setting the ghost field directly, so a showcase or headless test exercises the same
    /// path a designer's mouse does — including the snapping — instead of proving only that a
    /// field can be assigned.
    /// </remarks>
    public void MoveVirtualCursorTo(Vector2 world)
    {
        EditorPointerMove(ClientFromWorld2D(world), MouseButtons.None, Keys.None);
    }

    /// <summary>Places the armed object at a world position, as a click there would.</summary>
    public void ClickVirtualCursorAt(Vector2 world)
    {
        Point client = ClientFromWorld2D(world);
        EditorPointerMove(client, MouseButtons.None, Keys.None);
        EditorPointerDown(client, MouseButtons.Left, Keys.None);
        EditorPointerUp(client, MouseButtons.Left, Keys.None);
    }

    /// <summary>Hit tests the topmost instance at a viewport client point.</summary>
    public RoomNode? HitTestNode(Point clientPoint) => HitTestNodes(clientPoint).FirstOrDefault();

    /// <summary>
    /// Every selectable instance under a point, front-to-back. Alt-click uses this list to step
    /// through overlapping objects in dense scenes instead of making the front one inescapable.
    /// </summary>
    public IReadOnlyList<RoomNode> HitTestNodes(Point clientPoint)
    {
        if (_viewport.Mode2D)
        {
            Vector2 world = _viewport.ControlToWorld2D(clientPoint);
            return EnumerateEditableHitNodes()
                .Where(node => HitTestEditableNode2D(node, world))
                .OrderBy(EffectiveNodeDepth)
                .ThenByDescending(node => _room.Nodes.IndexOf(node))
                .ToList();
        }

        (Vector3 origin, Vector3 direction) = _viewport.PickRay(clientPoint);
        List<(RoomNode Node, float Distance)> hits = [];
        foreach (RoomNode node in EnumerateVisibleNodes().Where(CanEditNodeInActiveContext))
        {
            (Vector3 min, Vector3 max) = NodeBounds3D(node);
            if (!IsNodeLocked(node) && RayIntersectsAabb(origin, direction, min, max, out float t))
            {
                hits.Add((node, t));
            }
        }

        if (hits.Count > 0)
        {
            return hits.OrderBy(hit => hit.Distance).Select(hit => hit.Node).ToList();
        }

        foreach (RoomNode node in EnumerateVisibleTerrainNodes().Where(CanEditNodeInActiveContext))
        {
            (Vector3 min, Vector3 max) = NodeBounds3D(node);
            if (!IsNodeLocked(node) && RayIntersectsAabb(origin, direction, min, max, out float t))
            {
                hits.Add((node, t));
            }
        }

        return hits.OrderBy(hit => hit.Distance).Select(hit => hit.Node).ToList();
    }

    public void EditorPointerDown(Point client, MouseButtons button, Keys modifiers)
    {
        FocusRoomViewport();
        _dragStartClient = client;
        _placement.ResetContinuousPlacement();

        // Tile painting owns both buttons while a tile set is armed (LMB paints, RMB erases).
        if (IsTilePainting && _viewport.Mode2D && button is MouseButtons.Left or MouseButtons.Right)
        {
            Vector2 world = _viewport.ControlToWorld2D(client);
            BeginTileStroke(world, erase: button == MouseButtons.Right);
            _viewport.NavigationEnabled = false;
            return;
        }

        if (button == MouseButtons.Left)
        {
            if (ActiveTool == RoomTool.Place && _pendingPlacementPath is not null)
            {
                PlaceAt(client, modifiers);
                return;
            }

            if (ActiveTool == RoomTool.Select && _viewport.Mode2D
                && (modifiers & (Keys.Control | Keys.Shift | Keys.Alt)) == 0)
            {
                if (TileSelectionValid && TryBeginTileGizmoDrag(client))
                {
                    _viewport.NavigationEnabled = false;
                    return;
                }

                // When a tile layer is selected in the outliner it owns clicks on its painted
                // cells, even if a game object overlaps them. This is the explicit "edit this
                // layer" affordance that was missing from the old paint-only workflow.
                if (_navigation.CurrentSection == RoomNavSection.Tilesets
                    && TryHitTileCell(client, out RoomNode? tileLayer, out RoomTileCell? tileCell))
                {
                    SelectTileCell(tileLayer!, tileCell!);
                    BeginTileBodyDrag(client);
                    return;
                }
            }

            if (_selected is not null
                && CanEditNodeInActiveContext(_selected)
                && (modifiers & (Keys.Control | Keys.Shift | Keys.Alt)) == 0
                && TryBeginGizmoDrag(client))
            {
                _viewport.NavigationEnabled = false;
                return;
            }

            // CAM-2: camera markers (viewport frustum / game camera) open the second-camera inset
            // before node picking, so a designer can click the gizmo without selecting the floor.
            if (_navigation.CurrentSection == RoomNavSection.Views
                && (modifiers & (Keys.Control | Keys.Shift | Keys.Alt)) == 0
                && TryHitCameraMarkerCore(client, out int markerViewport, out bool markerGameCamera))
            {
                if (markerGameCamera) ShowGameCameraInInset();
                else ShowViewportInInset(markerViewport);
                _viewport.NavigationEnabled = false;
                return;
            }

            IReadOnlyList<RoomNode> hits = HitTestNodes(client);
            RoomNode? hit = hits.FirstOrDefault();
            if ((modifiers & Keys.Alt) != 0 && hits.Count > 1 && _selected is not null)
            {
                int selectedIndex = hits.ToList().IndexOf(_selected);
                hit = hits[(selectedIndex + 1 + hits.Count) % hits.Count];
            }

            if (hit is null)
            {
                // Fallback tile picking still requires Tilesets and its explicitly selected layer;
                // never discover or switch to another resource type/layer from a viewport hit.
                if (ActiveTool == RoomTool.Select && _viewport.Mode2D
                    && (modifiers & (Keys.Control | Keys.Shift | Keys.Alt)) == 0
                    && TryHitTileCell(client, out RoomNode? tileLayer, out RoomTileCell? tileCell))
                {
                    SelectTileCell(tileLayer!, tileCell!);
                    BeginTileBodyDrag(client);
                    return;
                }
                if ((modifiers & (Keys.Control | Keys.Shift)) == 0) Select(null);
                return;
            }

            if ((modifiers & Keys.Control) != 0)
            {
                if (TryBeginConstrainedBodyDrag(hit, client, modifiers)) return;
                Select(hit, additive: true, toggle: true);
                return;
            }

            if ((modifiers & Keys.Shift) != 0)
            {
                if (TryBeginConstrainedBodyDrag(hit, client, modifiers)) return;
                Select(hit, additive: true);
                return;
            }

            if (_selection.Contains(hit))
            {
                SetSelection(_selection.Where(node => !ReferenceEquals(node, hit)).Append(hit));
            }
            else
            {
                Select(hit);
            }

            if (hit is not null)
            {
                // Body drag moves the whole selection immediately (classic room-editor feel).
                BeginTransformDrag(DragKind.Move, client);
                _viewport.NavigationEnabled = false;
            }
        }
        else if (button == MouseButtons.Right && ActiveTool == RoomTool.Place)
        {
            CancelPlacement();
        }
    }

    public void EditorPointerMove(Point client, MouseButtons button, Keys modifiers)
    {
        if (_viewport.Mode2D)
        {
            Vector2 world = _viewport.ControlToWorld2D(client);
            if (_viewportCoordsLabel is not null)
            {
                _viewportCoordsLabel.Text = $"X: {world.X:0.#}, Y: {world.Y:0.#}";
            }
            _placement.UpdateGhost(world, _room.Settings.GridSize);
        }
        else if (_viewport.RayToGround(client, 0f, out Vector3 ground))
        {
            if (_viewportCoordsLabel is not null)
            {
                _viewportCoordsLabel.Text = $"X: {ground.X:0.##}, Z: {ground.Z:0.##}";
            }
        }
        if (ViewMode3D) UpdateSurfacePlacementPreview(client);

        // Continuous placement with duplicate prevention
        if (button == MouseButtons.Left && (modifiers & Keys.Control) != 0)
        {
            if (ActiveTool == RoomTool.Place && _pendingPlacementPath is not null)
            {
                if (ViewMode3D)
                {
                    ContinueSurfacePlacement(client);
                    _viewport.Host.Invalidate();
                    return;
                }
                Vector2 worldPos = _viewport.Mode2D
                    ? _viewport.ControlToWorld2D(client)
                    : (_viewport.RayToGround(client, 0f, out Vector3 g) ? new Vector2(g.X, g.Z) : Vector2.Zero);
                Vector2 snapped = SnapPoint(worldPos);

                if (_placement.ShouldPlaceObject(snapped, _room.Settings.GridSize, _pendingPlacementPath))
                {
                    Vector3 placePos = _viewport.Mode2D
                        ? new Vector3(snapped.X, snapped.Y, 0f)
                        : new Vector3(snapped.X, 0f, snapped.Y);
                    AddPlacedNode(placePos);
                    _navigation?.ObjectsPanel?.SyncSelection();
                    _viewport?.Host?.Invalidate();
                }
                return;
            }

            if (IsTilePainting && _viewport.Mode2D)
            {
                Vector2 tileWorld = _viewport.ControlToWorld2D(client);
                ContinueTileStroke(tileWorld);
                return;
            }
        }

        if (IsTilePainting && _viewport.Mode2D && button is MouseButtons.Left or MouseButtons.Right)
        {
            Vector2 tileWorld = _viewport.ControlToWorld2D(client);
            ContinueTileStroke(tileWorld);
            return;
        }

        if (_tileDragKind != TileDragKind.None)
        {
            UpdateTileDrag(client, modifiers);
            return;
        }

        if (_drag != DragKind.None && _selected is not null && _dragStartTransform is not null)
        {
            UpdateDrag(client, modifiers);
            SyncInspectorTransformDuringDrag();
            return;
        }

        _hover = ActiveTool == RoomTool.Select ? HitTestNode(client) : null;
        if (_viewport.Mode2D)
        {
            Vector2 world = _viewport.ControlToWorld2D(client);

            // Track the armed object under the cursor so the preview shows what is about to be
            // placed, and where. Without this a designer arms an object and sees nothing at all
            // until after the click has already committed it.
            // SnapPoint, the same call PlaceAt uses, so the preview is where the click lands.
            _ghostWorld = ActiveTool == RoomTool.Place && _pendingPlacementPath is not null
                ? SnapPoint(world)
                : null;

            UpdateStatus($"x {world.X:0.#}  y {world.Y:0.#}");
        }
        else if (_viewport.RayToGround(client, 0f, out Vector3 ground))
        {
            UpdateStatus($"x {ground.X:0.##}  z {ground.Z:0.##}");
        }
    }

    public void EditorPointerUp(Point client, MouseButtons button, Keys modifiers)
    {
        _placement.ResetContinuousPlacement();
        _lastSurfacePlacement = null;

        if (_tileStrokeActive)
        {
            CommitTileStroke();
        }

        if (_tileDragKind != TileDragKind.None)
        {
            CommitTileDrag();
        }

        if (_drag != DragKind.None && _selected is not null && _dragStartTransform is not null)
        {
            CommitDrag();
        }
        FinishConstrainedBodyDrag();

        _drag = DragKind.None;
        _dragStartTransform = null;
        _viewport.NavigationEnabled = !IsGameCameraPreview;
    }

    public void PlaceObjectAtWorld2D(float x, float y, Keys modifiers = Keys.None)
    {
        if (_pendingPlacementPath is null)
        {
            return;
        }

        Vector2 snapped = SnapPoint(new Vector2(x, y));
        AddPlacedNode(new Vector3(snapped.X, snapped.Y, 0f));
        if ((modifiers & Keys.Control) == 0)
        {
            CancelPlacement();
        }
    }

    /// <summary>Scrubs canonical model clips in the room preview. The ordinary editor plays them.</summary>
    public void SetModelPreviewTime(float seconds, bool pause = true)
    {
        _roomTime = MathF.Max(0f, seconds);
        _roomTimePaused = pause;
    }

    public void ResumeModelPreview() => _roomTimePaused = false;
    public bool IsModelPreviewPaused => _roomTimePaused;

    public void FrameContentForTest() => FrameContent();

    /// <summary>
    /// The Room Editor's own shortcuts — but never while the designer is typing.
    /// </summary>
    /// <remarks>
    /// This binds bare letters to tools and Delete to "remove the selected instances", which is
    /// correct over the viewport and wrong everywhere else in the same editor. Renaming a node in
    /// the outliner used to switch tools on every letter of the new name and delete the selection
    /// the moment Delete was pressed; typing in the grid-size box did the same. A text field owns
    /// the keyboard while it has the focus.
    /// </remarks>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (EditorInputGuard.IsTextEntryFocused()
            || EditorInputGuard.IsLabelEditing(EditorInputGuard.FocusedControl()))
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        switch (keyData)
        {
            case Keys.Q:
                SetTool(RoomTool.Select);
                return true;
            case Keys.W:
                SetGizmo(GizmoKind.Move);
                return true;
            case Keys.E:
                SetGizmo(GizmoKind.Rotate);
                return true;
            case Keys.R:
                SetGizmo(GizmoKind.Scale);
                return true;
            case Keys.X:
                ToggleGizmoSpace();
                return true;
            case Keys.G:
                ToggleSnap();
                return true;
            case Keys.F:
                FrameContent();
                return true;
            case Keys.End:
                return SnapSelectionToFloor();
            case Keys.Escape:
                if (CancelTileTransformDrag()) return true;
                if (CancelTransformDrag()) return true;
                if (_inspectorPanelVisible && !_centerSplit.Panel2Collapsed)
                {
                    SetInspectorVisible(false);
                    return true;
                }

                if (ActiveTool == RoomTool.Place || _placement.IsArmed)
                {
                    CancelPlacement();
                    return true;
                }

                Select(null);
                SetTool(RoomTool.Select);
                return true;
            case Keys.Delete:
                DeleteSelected();
                return true;
            case Keys.Control | Keys.A:
                SetSelection(_room.Nodes.Where(node => node.Enabled && node.Supports(_room.Dimension) && !IsNodeLocked(node)));
                return true;
            case Keys.Control | Keys.C:
                CopySelected();
                return true;
            case Keys.Control | Keys.V:
                PasteCopied();
                return true;
            case Keys.Control | Keys.D:
                DuplicateSelected();
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── Standard editing verbs ───────────────────────────────────────────────────

    /// <summary>
    /// Whether the room's instance selection would do something with a standard editing verb.
    /// </summary>
    /// <remarks>
    /// Implementing this is what lets the shell's Edit menu act on the room instead of on the
    /// resource tree behind it — the same routing the keyboard already uses, so Edit ▸ Delete and
    /// the Delete key finally agree about what "the selection" means. A verb is claimed only when
    /// it would actually do something, because claiming one and doing nothing swallows the
    /// keystroke instead of passing it on.
    /// </remarks>
    public bool CanEdit(EditCommand command)
    {
        if (EditorInputGuard.IsTextEntryFocused()) return false;
        return command switch
        {
            EditCommand.Undo => CanUndo,
            EditCommand.Redo => CanRedo,
            EditCommand.Cut or EditCommand.Copy or EditCommand.Delete => _selection.Count > 0,
            EditCommand.Paste => HasRoomClipboardContent(),
            EditCommand.SelectAll => _room.Nodes.Count > 0,
            _ => false,
        };
    }

    /// <inheritdoc />
    public bool TryEdit(EditCommand command)
    {
        if (!CanEdit(command)) return false;
        switch (command)
        {
            case EditCommand.Undo:
                Undo();
                return true;
            case EditCommand.Redo:
                Redo();
                return true;
            case EditCommand.Copy:
                return CopySelected();
            case EditCommand.Cut:
                if (!CopySelected()) return false;
                DeleteSelected();
                return true;
            case EditCommand.Paste:
                return PasteCopied().Count > 0;
            case EditCommand.Delete:
                DeleteSelected();
                return true;
            case EditCommand.SelectAll:
                SetSelection(_room.Nodes.Where(node =>
                    node.Enabled && node.Supports(_room.Dimension) && !IsNodeLocked(node)));
                return true;
            default:
                return false;
        }
    }

    // ── Loading / palette / layers ───────────────────────────────────────────────

    private RoomAsset LoadRoom()
    {
        try
        {
            if (File.Exists(ResourcePath))
            {
                return RoomAssetLoader.Parse(ResourcePath);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or Newtonsoft.Json.JsonException)
        {
            LoadWarning = exception.Message;
        }

        string name = ResourceDisplayName.Format(ResourcePath);

        return RoomAsset.Create(name, RoomDimension.TwoD);
    }

    public void RefreshPalette()
    {
        _paletteEntries.Clear();
        foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.GameObject))
        {
            _paletteEntries.Add(entry);
        }

        _terrainPaletteEntries.Clear();
        foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.Terrain))
        {
            _terrainPaletteEntries.Add(entry);
        }

        // T1: there is no separate Tile Set or Background kind any more — both palettes are the
        // Image list filtered by what each image has been *enabled for*. Filtering here (rather
        // than showing every image and failing on click) is what keeps the flags meaningful.
        _tileSetPaletteEntries.Clear();
        _spritePaletteEntries.Clear();
        _backgroundPaletteEntries.Clear();
        foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.Image))
        {
            if (TileSetInfo.Load(entry.FullPath) is not null)
                _tileSetPaletteEntries.Add(entry);
            if (ProjectAssetIndex.SupportsImageUsage(entry, ImageUsage.Background))
                _backgroundPaletteEntries.Add(entry);
            _spritePaletteEntries.Add(entry);
        }

        RebuildPaletteListView();
        _navigation?.ObjectsPanel?.RefreshObjects(_paletteEntries);
        _navigation?.TilesetsPanel?.RefreshTilesets(_spritePaletteEntries);
    }

    private void RebuildPaletteListView()
    {
        List<ProjectAssetEntry> source = _paletteMode switch
        {
            PaletteMode.Terrain => _terrainPaletteEntries,
            PaletteMode.Tiles => _tileSetPaletteEntries,
            PaletteMode.Backgrounds => _backgroundPaletteEntries,
            _ => _paletteEntries,
        };
        string glyph = _paletteMode switch
        {
            PaletteMode.Terrain => "⛰  ",
            PaletteMode.Tiles => "▦  ",
            PaletteMode.Backgrounds => "▤  ",
            _ => "⬡  ",
        };

        _paletteList.BeginUpdate();
        _paletteList.Items.Clear();
        string query = _paletteSearch.Text.Trim();
        foreach (ProjectAssetEntry entry in source)
        {
            if (query.Length == 0 || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _paletteList.Items.Add(new ListViewItem(glyph + entry.DisplayName) { Tag = entry });
            }
        }

        if (source.Count == 0)
        {
            string empty = _paletteMode switch
            {
                PaletteMode.Terrain => "(no terrains — create one in Assets)",
                PaletteMode.Tiles => "(no tile sets — create one in Assets)",
                PaletteMode.Backgrounds => "(no Background images — enable the Background flag in Image Editor)",
                _ => "(no objects — create one in Assets)",
            };
            _paletteList.Items.Add(new ListViewItem(empty) { ForeColor = EditorChrome.Muted });
        }

        _paletteList.EndUpdate();
    }

    private void RefreshLayers()
    {
        string? selectedId = _layerList.SelectedIndex >= 0 && _layerList.SelectedIndex < _room.Layers.Count
            ? _room.Layers[_layerList.SelectedIndex].Id
            : null;
        _syncingLayers = true;
        try
        {
            _layerList.Items.Clear();
            foreach (RoomLayer layer in _room.Layers)
            {
                int index = _layerList.Items.Add(LayerDisplayText(layer), layer.Enabled);
                if (layer.Id == selectedId) _layerList.SelectedIndex = index;
            }

            if (_layerList.SelectedIndex < 0 && _layerList.Items.Count > 0)
            {
                _layerList.SelectedIndex = 0;
            }
        }
        finally
        {
            _syncingLayers = false;
        }

        _navigation?.ObjectsPanel?.RefreshLayers();
        _navigation?.TilesetsPanel?.RefreshLayers();
    }

    /// <summary>Switches the left palette between Objects and Terrain. Terrain placement is
    /// 3D-only (a heightmap has no meaningful 2D projection), so the switch is refused in 2D.</summary>
    private void SetPaletteMode(PaletteMode mode)
    {
        if (mode == PaletteMode.Terrain && !ViewMode3D)
        {
            UpdateStatus("Switch to 3D mode to place Terrain.");
            return;
        }

        if (mode == PaletteMode.Tiles && ViewMode3D)
        {
            UpdateStatus("Switch to 2D mode to paint tiles.");
            return;
        }

        _paletteMode = mode;
        _paletteHintLabel.Text = mode switch
        {
            PaletteMode.Terrain => "Terrain — click, then click the room",
            PaletteMode.Tiles => "Tiles — pick a set, then paint (RMB erases)",
            PaletteMode.Backgrounds => "Backgrounds — click a sprite to add a layer",
            _ => "Objects — click, then click the room",
        };
        UpdatePaletteModeButtonStyles();
        RebuildPaletteListView();
        UpdateTilePickerVisibility();
    }

    /// <summary>The picker is only meaningful in tile mode with a sheet loaded.</summary>
    private void UpdateTilePickerVisibility()
    {
        _tilePickerHost.Visible = _paletteMode == PaletteMode.Tiles && _tilePicker.HasSheet;
    }

    private void UpdatePaletteModeButtonStyles()
    {
        void Style(Button button, PaletteMode mode, bool enabled)
        {
            bool active = _paletteMode == mode;
            button.BackColor = active ? EditorChrome.Accent : EditorChrome.Raised;
            button.ForeColor = active ? Color.White : EditorChrome.Text;
            button.Enabled = enabled || active;
        }

        Style(_paletteObjectsButton, PaletteMode.Objects, true);
        Style(_paletteTerrainButton, PaletteMode.Terrain, ViewMode3D);   // terrain is 3D-only
        Style(_paletteTilesButton, PaletteMode.Tiles, !ViewMode3D);      // tiles are 2D-only
        Style(_paletteBackgroundsButton, PaletteMode.Backgrounds, true); // backgrounds work in both
    }

    private void OnPaletteSelected(object? sender, EventArgs e)
    {
        if (_paletteList.SelectedItems.Count != 1 || _paletteList.SelectedItems[0].Tag is not ProjectAssetEntry entry)
        {
            return;
        }

        if (_paletteMode == PaletteMode.Terrain)
        {
            BeginPlacementTerrain(entry.FullPath);
        }
        else if (_paletteMode == PaletteMode.Tiles)
        {
            BeginTilePainting(entry.FullPath, createNewLayer: (ModifierKeys & Keys.Control) == Keys.Control);
        }
        else if (_paletteMode == PaletteMode.Backgrounds)
        {
            AddBackground(entry.FullPath);
        }
        else
        {
            BeginPlacement(entry.FullPath);
        }
    }

    // ── Views / hierarchy / per-instance overrides ──────────────────────────────

    /// <summary>
    /// Designates which placed object drives the game camera (the room's "view"). The runtime's
    /// <c>ApplyActiveGameCamera</c> already reads this — it just had no authoring surface.
    /// Pass null to clear.
    /// </summary>
    public void SetActiveGameCamera(RoomNode? node)
    {
        string before = _room.ActiveGameCameraId;
        string after = node?.Id ?? string.Empty;
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _room.ActiveGameCameraId = after;
        MarkDirty();
        PushEdit(
            node is null ? "Clear game camera" : $"Set game camera to '{node.Name}'",
            () => { _room.ActiveGameCameraId = after; RefreshSceneViews(); },
            () => { _room.ActiveGameCameraId = before; RefreshSceneViews(); });
        RefreshSceneViews();
        UpdateStatus(node is null ? "Cleared the active game camera." : $"'{node.Name}' is now the game camera.");
    }

    public RoomNode? ActiveGameCamera =>
        _room.Nodes.FirstOrDefault(n => n.Id == _room.ActiveGameCameraId);

    // ── Scene view (look through a camera) ──────────────────────────────────────

    /// <summary>
    /// Nodes the Scene dropdown can look through, in order. The designated game camera comes first
    /// because that is the one the running game will actually use.
    /// </summary>
    /// <remarks>
    /// The room model designates a single camera (<c>RoomAsset.ActiveGameCameraId</c>) rather than
    /// holding a list of views, so today this yields at most one entry. It enumerates rather than
    /// returning that one node so a future multi-view model needs no change here or in the UI.
    /// </remarks>
    public IEnumerable<RoomNode> CameraNodes()
    {
        RoomNode? active = ActiveGameCamera;
        if (active is not null)
        {
            yield return active;
        }
    }

    /// <summary>Node the viewport is looking through, or null for the editor's own free camera.</summary>
    public RoomNode? SceneViewNode =>
        string.IsNullOrEmpty(_sceneViewNodeId)
            ? null
            : _room.Nodes.FirstOrDefault(n => n.Id == _sceneViewNodeId);

    /// <summary>Rebuilds the Scene dropdown, preserving the current choice when it still exists.</summary>
    private bool _refreshingSceneViews;

    public void RefreshSceneViews()
    {
        if (_sceneViewBox is null) return;

        string previous = _sceneViewNodeId;
        _refreshingSceneViews = true;
        try
        {
            _sceneViewBox.Items.Clear();
            _sceneViewBox.Items.Add(new SceneViewChoice(string.Empty, "Scene"));

            foreach (RoomNode camera in CameraNodes())
            {
                _sceneViewBox.Items.Add(new SceneViewChoice(camera.Id, camera.Name));
            }

            int index = 0;
            for (int i = 0; i < _sceneViewBox.Items.Count; i++)
            {
                if (_sceneViewBox.Items[i] is SceneViewChoice choice
                    && string.Equals(choice.NodeId, previous, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            _sceneViewBox.SelectedIndex = index;
        }
        finally { _refreshingSceneViews = false; }
        SyncWorkspaceSceneCameras();
        if (_sceneViewBox.SelectedItem is SceneViewChoice selected && selected.NodeId != previous)
            ApplySceneView();
    }

    private sealed record SceneViewChoice(string NodeId, string Label)
    {
        // ToolStripComboBox renders items by ToString().
        public override string ToString() => Label;
    }

    /// <summary>Points the viewport at whichever camera the Scene dropdown selects.</summary>
    private void ApplySceneView()
    {
        if (_refreshingSceneViews || _sceneViewBox?.SelectedItem is not SceneViewChoice choice) return;

        _sceneViewNodeId = choice.NodeId;
        RoomNode? node = SceneViewNode;
        ApplySceneCameraView(node);
    }

    // ── Play ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves, compiles designer scripts, and launches this room in the Player — the same two calls
    /// F5 makes from the shell, so the button and the menu cannot diverge.
    /// </summary>
    /// <returns>True when the Player started.</returns>
    /// <remarks>
    /// Play starts from the room's designated camera. When none is set the runtime's
    /// <c>ApplyActiveGameCamera</c> leaves the camera at the room's default origin (0,0, and z=0 in
    /// 3D), which is the documented fallback rather than an error — a room with no camera is still
    /// playable.
    /// </remarks>
    private ProjectRunSession? _roomPlayer;

    private void SyncPlayerToolbar() => _editorToolbar.Sync(!ViewMode3D, ShowGrid, _room.Settings.SnapEnabled,
        ViewMode3D ? MetricGridSize : _room.Settings.GridSize, _roomPlayer?.IsRunning == true, _roomPlayer?.IsPaused == true);

    private void OnRoomPlayerExited(int code, string details)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || Disposing) return;
            SyncPlayerToolbar();
            UpdateStatus(code == 0 ? "Game stopped." : $"Game failed (exit {code}). {details}");
            if (code != 0 && !Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)
                MessageBox.Show(this, $"The game stopped with exit code {code}.\n{details}", "Genesis — game error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }));
    }

    public bool PlayRoom()
    {
        Save();
        _roomPlayer?.Dispose();
        _roomPlayer = null;

        string projectRoot = ProjectRoot;
        Genesis.Runtime.Project.ProjectRunLauncher.CompileOutcome compile =
            Genesis.Runtime.Project.ProjectRunLauncher.CompileScripts(projectRoot);
        if (!compile.Success)
        {
            UpdateStatus("Play cancelled — script errors. " + (compile.ErrorMessage ?? string.Empty));
            return false;
        }

        string roomName = ResourceDisplayName.Format(ResourcePath);

        Genesis.Runtime.Project.ProjectRunLauncher.LaunchOutcome launch =
            Genesis.Runtime.Project.ProjectRunLauncher.Launch(projectRoot, roomName: roomName,
                supervised: true, exited: OnRoomPlayerExited,
                extraEnvironment: new Dictionary<string, string>
                {
                    [Genesis.Rendering.Core.RenderBackendSelection.EnvironmentVariable] = Genesis.Rendering.Core.RenderBackendSelection.ToEnvironmentValue(Genesis.Rendering.Core.RenderBackendSelection.EffectiveBackend),
                });
        if (!launch.Success)
        {
            UpdateStatus("Play cancelled — " + (launch.ErrorMessage ?? "the Player did not start."));
            return false;
        }

        _roomPlayer = launch.Session;
        SyncPlayerToolbar();
        RoomNode? camera = ActiveGameCamera;
        UpdateStatus(camera is null
            ? $"Playing '{roomName}' from the room origin (no camera set)."
            : $"Playing '{roomName}' from camera '{camera.Name}'.");
        return true;
    }

    /// <summary>
    /// Changes the parent while preserving the world pose. Refuses cycles, singular parents
    /// and relative poses requiring shear, which the room's local TRS schema cannot represent.
    /// </summary>
    public bool SetNodeParent(RoomNode child, RoomNode? parent)
    {
        if (!CanEditNodeInActiveContext(child) || parent is not null && !CanEditNodeInActiveContext(parent)) return false;
        if (IsNodeLocked(child))
        {
            UpdateStatus("Unlock the child's layer before changing its parent.");
            return false;
        }

        if (parent is not null && WouldCycle(child, parent))
        {
            UpdateStatus("That would create a parent loop.");
            return false;
        }

        string before = child.ParentId;
        string after = parent?.Id ?? string.Empty;
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        RoomTransform localBefore = CloneTransform(child.Transform);
        RoomTransform parentWorld = parent is null ? new RoomTransform() : GetNodeWorldTransform(parent);
        if (!RoomHierarchyTransforms.TryReparent(GetNodeWorldTransform(child), parentWorld, out RoomTransform localAfter))
        {
            UpdateStatus("Cannot preserve this pose under that parent's scale. Use a non-zero uniform parent scale before reparenting.");
            return false;
        }
        void ApplyParent(string id, RoomTransform local)
        { child.ParentId = id; CopyTransform(local, child.Transform); RefreshPhase4Ui(); }
        ApplyParent(after, localAfter);
        MarkDirty();
        PushEdit(
            parent is null ? $"Unparent '{child.Name}'" : $"Parent '{child.Name}' to '{parent.Name}'",
            () => ApplyParent(after, localAfter),
            () => ApplyParent(before, localBefore));
        RefreshPhase4Ui();
        return true;
    }

    /// <summary>True if parenting <paramref name="child"/> under <paramref name="parent"/> would loop.</summary>
    private bool WouldCycle(RoomNode child, RoomNode parent)
    {
        RoomNode? walker = parent;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        while (walker is not null && seen.Add(walker.Id))
        {
            if (string.Equals(walker.Id, child.Id, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            walker = string.IsNullOrEmpty(walker.ParentId)
                ? null
                : _room.Nodes.FirstOrDefault(n => n.Id == walker.ParentId);
        }

        return false;
    }

    /// <summary>
    /// Overrides one component property for a single placed instance, leaving the shared prefab
    /// untouched — the runtime applies these in <c>ApplyOverrides</c> at spawn time.
    /// </summary>
    public void SetComponentOverride(RoomNode node, string componentId, string property, string value)
    {
        if (node.GameObject is not { } gameObject || IsNodeLocked(node))
        {
            return;
        }

        RoomComponentOverride? existing = gameObject.ComponentOverrides
            .FirstOrDefault(o => string.Equals(o.ComponentId, componentId, StringComparison.OrdinalIgnoreCase));

        RoomComponentOverride target = existing ?? new RoomComponentOverride { ComponentId = componentId };
        bool isNew = existing is null;
        JToken? previous = target.Properties.TryGetValue(property, out JToken? old) ? old : null;

        target.Properties[property] = value;
        if (isNew)
        {
            gameObject.ComponentOverrides.Add(target);
        }

        MarkDirty();
        PushEdit(
            $"Override {componentId}.{property}",
            () =>
            {
                target.Properties[property] = value;
                if (!gameObject.ComponentOverrides.Contains(target)) { gameObject.ComponentOverrides.Add(target); }
            },
            () =>
            {
                if (previous is null) { target.Properties.Remove(property); } else { target.Properties[property] = previous; }
                if (isNew && target.Properties.Count == 0) { gameObject.ComponentOverrides.Remove(target); }
            });
    }

    // ── Backgrounds ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds a background layer from a sprite. Backgrounds are full-room layers rather than
    /// click-placed instances, so selecting one in the palette adds it immediately. Each new layer
    /// sits behind the previous ones (increasing depth), which is what parallax stacking needs.
    /// The runtime already draws these (<c>RoomRenderSubsystem</c>); this is the missing authoring UI.
    /// </summary>
    public RoomNode AddBackground(string spriteFullPath, RoomBackgroundLayout layout = RoomBackgroundLayout.StretchRoom)
    {
        string reference = ResourceNames.Name(ProjectRoot, spriteFullPath);
        int existing = _room.Nodes.Count(n => n.Kind == RoomNodeKind.Background);

        RoomNode node = new()
        {
            Kind = RoomNodeKind.Background,
            Name = ResourceDisplayName.Format(spriteFullPath) + " Background",
            LayerId = PlacementLayerId(),
            Background = new RoomBackgroundData
            {
                Asset = reference,
                Mode = ViewMode3D ? RoomBackgroundMode.Sky : RoomBackgroundMode.TwoD,
                Layout = ViewMode3D ? RoomBackgroundLayout.StretchView : layout,
                // Larger depth draws further back, so each added layer stacks behind the last.
                Depth = 10_000 + existing * 100,
                Opacity = 1f,
            },
        };

        _room.Nodes.Add(node);
        MarkDirty();
        PushEdit(
            $"Add background '{node.Name}'",
            () => { if (!_room.Nodes.Contains(node)) { _room.Nodes.Add(node); } },
            () => { _room.Nodes.Remove(node); if (_selected == node) { Select(null); } });
        Select(node);
        UpdateStatus($"Added background '{ResourceDisplayName.Format(spriteFullPath)}'");
        return node;
    }

    /// <summary>Sets a background layer's parallax scroll rate (units per second).</summary>
    public void SetBackgroundScroll(RoomNode node, float scrollX, float scrollY)
    {
        if (node.Background is not { } background || IsNodeLocked(node))
        {
            return;
        }

        float[] before = background.Scroll is { Length: >= 2 }
            ? [background.Scroll[0], background.Scroll[1]]
            : [0f, 0f];
        float[] after = [scrollX, scrollY];
        if (before[0] == after[0] && before[1] == after[1]) return;
        background.Scroll = after;
        PushEdit(
            "Set background scroll",
            () => { background.Scroll = [after[0], after[1]]; RefreshPhase4Ui(); },
            () => { background.Scroll = [before[0], before[1]]; RefreshPhase4Ui(); });
        RefreshPhase4Ui();
    }

    public IReadOnlyList<RoomNode> Backgrounds =>
        _room.Nodes.Where(n => n.Kind == RoomNodeKind.Background).ToList();

    // ── Tile painting ───────────────────────────────────────────────────────────

    /// <summary>
    /// Arms tile painting with a tile set, creating (or reusing) the room's TileLayer node for it.
    /// 2D-only: the runtime spawns tile cells as flat sprites.
    /// </summary>
    public void BeginTilePainting(string tileSetFullPath, bool createNewLayer = false)
    {
        if (_navigation.CurrentSection != RoomNavSection.Tilesets)
        {
            UpdateStatus("Open the Tiles tab and select a tile layer first.");
            return;
        }
        if (ViewMode3D)
        {
            UpdateStatus("Switch to 2D mode to paint tiles.");
            return;
        }

        _activeTileSet = TileSetInfo.Load(tileSetFullPath);
        if (_activeTileSet is null)
        {
            UpdateStatus("Could not read that tile set.");
            return;
        }

        _activeTileSetPath = tileSetFullPath;
        // Arming a tile set *is* entering tile mode, however it was triggered (palette click or API).
        _paletteMode = PaletteMode.Tiles;
        UpdatePaletteModeButtonStyles();

        // Show the sheet so a tile can be chosen. Without this the palette armed a *set* and left
        // every tilemap stuck on tile 0 (NEXT-061).
        _activeTileIndex = 0;
        _tilePicker.Load(_activeTileSet, ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, _activeTileSet.ImagePath));
        UpdateTilePickerVisibility();
        string reference = ResourceNames.Name(ProjectRoot, tileSetFullPath);
        _activeTileLayer = createNewLayer ? null : _navigation.TilesetsPanel.ActiveTileLayer;
        if (_activeTileLayer is not null && !CanEditNodeInActiveContext(_activeTileLayer)) return;
        if (_activeTileLayer is not null) ConfigureTileLayerTileset(_activeTileLayer, reference, _activeTileSet);

        if (_activeTileLayer is null)
        {
            RoomNode layer = new()
            {
                Kind = RoomNodeKind.TileLayer,
                Name = ResourceDisplayName.Format(tileSetFullPath) + " Tiles",
                LayerId = PlacementLayerId(),
                EnabledIn3D = false,
                TileLayer = new RoomTileLayerData
                {
                    Tileset = reference,
                    CellWidth = _activeTileSet.TileWidth,
                    CellHeight = _activeTileSet.TileHeight,
                    Margin = _activeTileSet.Margin,
                    Separation = _activeTileSet.Separation,
                },
            };
            _room.Nodes.Add(layer);
            _activeTileLayer = layer;
            MarkDirty();
            PushEdit(
                $"Add tile layer '{layer.Name}'",
                () => { if (!_room.Nodes.Contains(layer)) { _room.Nodes.Add(layer); } },
                () => { _room.Nodes.Remove(layer); if (ReferenceEquals(_activeTileLayer, layer)) { _activeTileLayer = null; } });
        }

        _navigation.TilesetsPanel.SelectLayer(_activeTileLayer);
        Select(_activeTileLayer);
        ActiveTool = RoomTool.Paint;
        SyncToolbar();
        UpdateStatus($"Painting tiles from '{ResourceDisplayName.Format(tileSetFullPath)}' — drag LMB to paint, drag RMB to erase; Ctrl-select the set for another layer");
    }

    /// <summary>Chooses which tile of the active set gets painted.</summary>
    /// <summary>
    /// Choose the tile the brush paints. Kept in step with the picker so the API and the UI cannot
    /// disagree about which tile is armed.
    /// </summary>
    public void SelectTileIndex(int index)
    {
        _activeTileIndex = Math.Max(0, index);
        _tilePicker.Select(_activeTileIndex);
    }

    /// <summary>The tile picker, so tests can drive the same path a click takes.</summary>
    public TilePickerPanel TilePicker => _tilePicker;

    public int ActiveTileIndex => _activeTileIndex;

    public RoomNode? ActiveTileLayer => _activeTileLayer;

    public bool IsTilePainting => ActiveTool == RoomTool.Paint && _paletteMode == PaletteMode.Tiles
        && _activeTileLayer is not null && CanEditNodeInActiveContext(_activeTileLayer);

    /// <summary>Paints (or erases) the tile cell containing a world-space 2D point.</summary>
    public bool PaintTileAtWorld(float worldX, float worldY, bool erase = false) =>
        PaintTileAtWorldCore(worldX, worldY, erase, journal: true);

    /// <summary>Columns in the active tile set's source image (falls back to a square guess).</summary>
    private int TileSetColumns()
    {
        if (_activeTileSet is null)
        {
            return 1;
        }

        RoomImageMetadata metadata = GetRoomImageMetadata(_activeTileSet.ImagePath);
        return metadata.ImagePath is null ? 1 : _activeTileSet.ColumnsFor(metadata.Width);
    }

    private void OnLayerChecked(object? sender, ItemCheckEventArgs e)
    {
        if (!_syncingLayers && e.Index >= 0 && e.Index < _room.Layers.Count)
        {
            RoomLayer layer = _room.Layers[e.Index];
            bool next = e.NewValue == CheckState.Checked;
            _handlingLayerCheck = true;
            try
            {
                SetLayerVisibility(layer, next);
            }
            finally
            {
                _handlingLayerCheck = false;
            }
        }
    }

    private void AddLayer()
    {
        AddRoomLayer();
    }

    // ── Placement / node management ─────────────────────────────────────────────

    private void PlaceAt(Point client, Keys modifiers)
    {
        if (!CanPlaceInActiveContext(_pendingPlacementKind)) return;
        if (_pendingPlacementKind == RoomNodeKind.Terrain && _viewport.Mode2D)
        {
            // Guards the edge case where 3D→2D happened after arming Terrain placement
            // but before the placing click — Terrain has no 2D projection to place into.
            CancelPlacement();
            return;
        }

        Vector3 world;
        if (_viewport.Mode2D)
        {
            Vector2 world2D = SnapPoint(_viewport.ControlToWorld2D(client));
            world = new Vector3(world2D.X, world2D.Y, 0f);
        }
        else
        {
            if (!TryPlacementPosition3D(client, out world)) return;
        }

        AddPlacedNode(world);
        if ((modifiers & Keys.Control) == 0)
        {
            CancelPlacement();
        }
    }

    private void AddPlacedNode(Vector3 worldPosition)
    {
        if (!CanPlaceInActiveContext(_pendingPlacementKind)) return;
        if (_pendingPlacementPath is null)
        {
            return;
        }

        RoomLayer? placementLayer = _room.Layers.FirstOrDefault(layer => layer.Id == _placement.TargetLayerId);
        if (placementLayer?.Locked == true)
        {
            UpdateStatus("The placement layer is locked. Choose an unlocked layer.");
            return;
        }

        string reference = ResourceNames.Name(ProjectRoot, _pendingPlacementPath);
        RoomNode node;
        if (_pendingPlacementKind == RoomNodeKind.Terrain)
        {
            string display = ResourceDisplayName.Format(_pendingPlacementPath);

            node = new RoomNode
            {
                Kind = RoomNodeKind.Terrain,
                Name = $"{display} {++_placeCounter}",
                LayerId = PlacementLayerId(),
                EnabledIn2D = false,
                Terrain = new RoomTerrainData { Asset = reference },
            };
        }
        else
        {
            string display = ResourceDisplayName.Format(_pendingPlacementPath);

            node = new RoomNode
            {
                Kind = RoomNodeKind.GameObject,
                Name = $"{display} {++_placeCounter}",
                LayerId = PlacementLayerId(),
                GameObject = new RoomGameObjectData { Prefab = reference },
            };
        }

        node.Transform.X = worldPosition.X;
        node.Transform.Y = worldPosition.Y;
        node.Transform.Z = worldPosition.Z;
        if (placementLayer is not null) node.LayerId = placementLayer.Id;
        ApplyPlacementDefaults(node.Transform);

        if (ViewMode3D && node.Kind == RoomNodeKind.GameObject)
        {
            if (!SnapToTerrain || !SnapNodeToTerrain(node))
                ApplySurfaceContact(node, new RoomSurfaceHit(worldPosition, Vector3.UnitY, 0), false);
            float cell = _room.Settings.SnapEnabled ? MetricGridSize : .01f;
            _lastSurfacePlacement = ((int)MathF.Round(worldPosition.X / cell), (int)MathF.Round(worldPosition.Z / cell), _pendingPlacementPath);
        }

        int randomSequenceBefore = _room.Settings.PlacementRandomSequence;
        int randomSequenceAfter = ViewMode3D && node.Kind == RoomNodeKind.GameObject && RandomYaw
            ? (randomSequenceBefore == int.MaxValue ? 0 : randomSequenceBefore + 1) : randomSequenceBefore;
        void ApplyRandomSequence(int sequence)
        {
            _room.Settings.PlacementRandomSequence = sequence;
            ClearSurfacePlacementPreview();
        }
        _room.Nodes.Add(node);
        ApplyRandomSequence(randomSequenceAfter);
        PushEdit(
            $"Place '{node.Name}'",
            () => { if (!_room.Nodes.Contains(node)) { _room.Nodes.Add(node); } ApplyRandomSequence(randomSequenceAfter); },
            () => { _room.Nodes.Remove(node); ApplyRandomSequence(randomSequenceBefore); if (_selected == node) { Select(null); } });
        Select(node);
    }

    public void DeleteSelected()
    {
        if (DeleteSelectedTileCell()) return;
        DeleteSelectionNodes();
    }

    public void DuplicateSelected()
    {
        DuplicateSelectionNodes();
    }

    /// <summary>
    /// Selects a node, showing its bounding box and filling the inspector — the same call the
    /// pointer path makes on a click. Public so a showcase or headless test can select without
    /// having to hit-test a pixel, per the project's test-hook convention (<c>SelectedNode</c> is
    /// already public; this is the setter that was missing).
    /// </summary>
    public void Select(RoomNode? node)
    {
        SetSelection(node is null ? [] : [node]);
    }

    // ── Gizmo interaction ────────────────────────────────────────────────────────

    private bool TryBeginGizmoDrag(Point client)
    {
        if (!TransformGizmoVisible) return false;
        if (_selected is null)
        {
            return false;
        }

        PointF surface = _viewport.ControlToSurface(client);
        if (_viewport.Mode2D)
        {
            if (_selected.Kind == RoomNodeKind.Terrain)
            {
                // Terrain has no 2D projection/gizmo — reachable only if selection somehow
                // survived a 3D→2D switch (ViewMode3D's setter normally clears it).
                return false;
            }

            Vector2[] corners = NodeCorners2D(_selected);
            Vector2 center = (corners[0] + corners[2]) * 0.5f;

            if (Gizmo == GizmoKind.Rotate)
            {
                Vector2 rotateHandle = RotateHandle2D(_selected);
                if (Vector2.Distance(new Vector2(surface.X, surface.Y), _viewport.World2DToSurface(rotateHandle)) < 12f)
                {
                    BeginTransformDrag(DragKind.Rotate, client);
                    return true;
                }
            }

            if (Gizmo is GizmoKind.Scale or GizmoKind.Move)
            {
                Vector2[] handles = ScaleHandles2D(corners);
                for (int i = 0; i < handles.Length; i++)
                {
                    Vector2 handleSurface = _viewport.World2DToSurface(handles[i]);
                    if (MathF.Abs(handleSurface.X - surface.X) <= 7f && MathF.Abs(handleSurface.Y - surface.Y) <= 7f)
                    {
                        BeginTransformDrag(DragKind.ScaleHandle, client);
                        _dragData = i;
                        return true;
                    }
                }
            }

            // Rotate ring fallback: near the selection centre ring in rotate mode.
            if (Gizmo == GizmoKind.Rotate && Vector2.Distance(new Vector2(surface.X, surface.Y), _viewport.World2DToSurface(center)) < 90f)
            {
                BeginTransformDrag(DragKind.Rotate, client);
                return true;
            }

            return false;
        }

        // 3D: test axis handles in screen space.
        Vector3 origin = NodePosition3D(_selected);
        float axisLength = AxisWorldLength();
        EditorGizmoMode gizmoMode = CurrentGizmoMode();
        Vector3 euler = SelectedEulerDegrees(_selected);
        if (EditorTransformGizmo.HitTest3D(
                _viewport,
                surface,
                origin,
                axisLength,
                gizmoMode,
                GizmoSpace,
                euler,
                includeZ: true) is not EditorGizmoHit hit)
        {
            if (Gizmo == GizmoKind.Rotate)
            {
                Vector3 originSurfaceMiss = _viewport.WorldToSurface(origin);
                float ringRadius = Vector2.Distance(
                    new Vector2(originSurfaceMiss.X, originSurfaceMiss.Y),
                    new Vector2(surface.X, surface.Y));
                if (ringRadius is > 30f and < 110f)
                {
                    _dragData = 1;
                    BeginTransformDrag(DragKind.Rotate3DY, client);
                    return true;
                }
            }

            return false;
        }

        Vector3[] axes = EditorTransformGizmo.Axes(GizmoSpace, euler);
        Vector3 originSurface = _viewport.WorldToSurface(origin);
        Vector3 tip = origin + axes[hit.AxisIndex] * axisLength;
        Vector3 tipSurface = _viewport.WorldToSurface(tip);
        Vector2 screenDir = new(tipSurface.X - originSurface.X, tipSurface.Y - originSurface.Y);
        float screenLen = screenDir.Length();
        if (screenLen < 1f)
        {
            return false;
        }

        _dragAxisWorld = axes[hit.AxisIndex];
        _dragAxisScreenDir = screenDir / screenLen;
        _dragAxisWorldPerPixel = axisLength / screenLen;
        _dragData = hit.AxisIndex;
        BeginTransformDrag(Gizmo switch
        {
            GizmoKind.Scale => DragKind.Scale3DAxis,
            GizmoKind.Rotate => DragKind.Rotate3DY,
            _ => DragKind.Axis3D,
        }, client);
        return true;
    }

    private void BeginTransformDrag(DragKind kind, Point client)
    {
        if (_selected is null || !CanEditNodeInActiveContext(_selected)) return;
        if (_selected.Background?.Layout == RoomBackgroundLayout.StretchView
            || (kind == DragKind.ScaleHandle && _selected.Background?.Layout == RoomBackgroundLayout.StretchRoom))
        {
            UpdateStatus("This background is stretched by its layout. Choose a native/tiled layout to resize it.");
            return;
        }
        _drag = kind;
        _dragStartClient = client;
        _dragStartWorld = _viewport.Mode2D
            ? _viewport.ControlToWorld2D(client)
            : Vector2.Zero;
        _dragStartTransform = GetNodeWorldTransform(_selected);
        BeginSurfaceDrag(client);
        CaptureDragSelectionTransforms();
    }

    private void UpdateDrag(Point client, Keys modifiers)
    {
        if (_selected is null || _dragStartTransform is null)
        {
            return;
        }

        RoomTransform start = _dragStartTransform;
        RoomTransform transform = CloneTransform(start);

        if (_viewport.Mode2D)
        {
            Vector2 world = _viewport.ControlToWorld2D(client);
            Vector2 delta = world - _dragStartWorld;
            switch (_drag)
            {
                case DragKind.Move:
                {
                    Vector2 target = new(start.X + delta.X, start.Y + delta.Y);
                    if ((modifiers & Keys.Shift) == 0) target = SnapPoint(target);
                    transform.X = target.X;
                    transform.Y = target.Y;
                    break;
                }

                case DragKind.Rotate:
                {
                    (float width, float height) = NodeSize2D(_selected);
                    Vector2 center = NodeEditingCenter2D(_selected, start, width, height);
                    float startAngle = MathF.Atan2(_dragStartWorld.Y - center.Y, _dragStartWorld.X - center.X);
                    float nowAngle = MathF.Atan2(world.Y - center.Y, world.X - center.X);
                    float degrees = start.RotationZ + (nowAngle - startAngle) * 180f / MathF.PI;
                    if (_room.Settings.SnapEnabled && (modifiers & Keys.Shift) == 0)
                    {
                        degrees = MathF.Round(degrees / 5f) * 5f;
                    }

                    transform.RotationZ = degrees;
                    break;
                }

                case DragKind.ScaleHandle:
                {
                    NodeVisual visual = VisualFor(_selected);
                    float baseW = MathF.Max(1f, visual.Width);
                    float baseH = MathF.Max(1f, visual.Height);
                    float angle = start.RotationZ * MathF.PI / 180f;
                    Vector2 center = NodeEditingCenter2D(_selected, start,
                        baseW * MathF.Abs(start.ScaleX), baseH * MathF.Abs(start.ScaleY));

                    // Handle index: 0..3 corners (NW NE SE SW), 4..7 edges (N E S W).
                    Vector2 local = RotateVector(world - center, -angle);
                    float halfW = MathF.Abs(local.X);
                    float halfH = MathF.Abs(local.Y);
                    float scaleX = start.ScaleX;
                    float scaleY = start.ScaleY;
                    bool affectsX = _dragData is 0 or 1 or 2 or 3 or 5 or 7;
                    bool affectsY = _dragData is 0 or 1 or 2 or 3 or 4 or 6;
                    if (affectsX)
                    {
                        scaleX = MathF.Max(0.02f, halfW * 2f / baseW) * MathF.Sign(start.ScaleX == 0f ? 1f : start.ScaleX);
                    }

                    if (affectsY)
                    {
                        scaleY = MathF.Max(0.02f, halfH * 2f / baseH) * MathF.Sign(start.ScaleY == 0f ? 1f : start.ScaleY);
                    }

                    if ((modifiers & Keys.Shift) != 0 && affectsX && affectsY)
                    {
                        float uniform = MathF.Max(MathF.Abs(scaleX), MathF.Abs(scaleY));
                        scaleX = uniform * MathF.Sign(scaleX);
                        scaleY = uniform * MathF.Sign(scaleY);
                    }

                    transform.ScaleX = scaleX;
                    transform.ScaleY = scaleY;
                    if (_selected.Kind == RoomNodeKind.Background)
                    {
                        transform.X = center.X - baseW * MathF.Abs(scaleX) * .5f;
                        transform.Y = center.Y - baseH * MathF.Abs(scaleY) * .5f;
                    }
                    break;
                }
            }

            ApplyGroupTransformFromPrimary(start, transform);
            return;
        }

        // 3D drags.
        Point deltaClient = new(client.X - _dragStartClient.X, client.Y - _dragStartClient.Y);
        PointF surfaceScale = _viewport.ControlToSurface(new Point(1, 1));
        Vector2 deltaSurface = new(deltaClient.X * surfaceScale.X, deltaClient.Y * surfaceScale.Y);
        switch (_drag)
        {
            case DragKind.Move:
                if (_constrainedBodyDrag) UpdateConstrainedBodyDrag(client, start, transform);
                else MoveBodyOnSurface(client, start, transform);
                break;
            case DragKind.Axis3D:
            {
                float alongScreen = Vector2.Dot(deltaSurface, _dragAxisScreenDir);
                float worldDelta = alongScreen * _dragAxisWorldPerPixel;
                if (_room.Settings.SnapEnabled && (modifiers & Keys.Shift) == 0)
                {
                    float grid = MathF.Max(0.01f, GridWorldSize3D());
                    worldDelta = MathF.Round(worldDelta / grid) * grid;
                }

                transform.X = start.X + _dragAxisWorld.X * worldDelta;
                transform.Y = start.Y + _dragAxisWorld.Y * worldDelta;
                transform.Z = start.Z + _dragAxisWorld.Z * worldDelta;
                break;
            }

            case DragKind.Rotate3DY:
            {
                float degrees = deltaSurface.X * 0.4f;
                if (_room.Settings.SnapEnabled && (modifiers & Keys.Shift) == 0)
                {
                    degrees = MathF.Round(degrees / 5f) * 5f;
                }

                switch (_dragData)
                {
                    case 0: transform.RotationX = start.RotationX + degrees; break;
                    case 2: transform.RotationZ = start.RotationZ + degrees; break;
                    default: transform.RotationY = start.RotationY + degrees; break;
                }

                break;
            }

            case DragKind.Scale3DAxis:
            {
                float alongScreen = Vector2.Dot(deltaSurface, _dragAxisScreenDir);
                float factor = 1f + alongScreen * 0.01f;
                factor = MathF.Max(0.02f, factor);
                switch (_dragData)
                {
                    case 0: transform.ScaleX = MathF.Max(0.02f, start.ScaleX * factor); break;
                    case 1: transform.ScaleY = MathF.Max(0.02f, start.ScaleY * factor); break;
                    default: transform.ScaleZ = MathF.Max(0.02f, start.ScaleZ * factor); break;
                }

                if ((modifiers & Keys.Shift) != 0)
                {
                    float uniform = _dragData switch
                    {
                        0 => transform.ScaleX,
                        1 => transform.ScaleY,
                        _ => transform.ScaleZ,
                    };
                    transform.ScaleX = transform.ScaleY = transform.ScaleZ = uniform;
                }

                break;
            }
        }

        ApplyGroupTransformFromPrimary(start, transform);
        if (!_constrainedBodyDrag) SnapDraggedSelection();
    }

    private void CommitDrag()
    {
        if (_selected is null || _dragStartTransform is null)
        {
            return;
        }

        RoomNode node = _selected;
        Dictionary<string, RoomTransform> before = new(_dragStartTransforms, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RoomTransform> after = SnapshotSelectionTransforms();
        if (TransformSnapshotsEqual(before, after))
        {
            return;
        }

        string label = _drag switch
        {
            DragKind.Rotate or DragKind.Rotate3DY => $"Rotate '{node.Name}'",
            DragKind.ScaleHandle or DragKind.Scale3DAxis => $"Scale '{node.Name}'",
            _ => $"Move '{node.Name}'",
        };
        if (_selection.Count > 1) label += $" + {_selection.Count - 1} selected";
        PushTransformSnapshotEdit(label, before, after);
    }

    private static void CopyTransform(RoomTransform source, RoomTransform target)
    {
        target.X = source.X;
        target.Y = source.Y;
        target.Z = source.Z;
        target.RotationX = source.RotationX;
        target.RotationY = source.RotationY;
        target.RotationZ = source.RotationZ;
        target.ScaleX = source.ScaleX;
        target.ScaleY = source.ScaleY;
        target.ScaleZ = source.ScaleZ;
    }

    // ── Geometry helpers ─────────────────────────────────────────────────────────

    private IEnumerable<RoomNode> EnumerateVisibleNodes()
    {
        foreach (RoomNode node in _room.Nodes.OrderBy(EffectiveNodeDepth))
        {
            if (node.Kind != RoomNodeKind.GameObject || !node.Enabled || !node.Supports(_room.Dimension))
            {
                continue;
            }

            // Type filters gate this one enumeration, which draw and hit testing both use, so a
            // hidden object cannot be selected by a click either.
            if (!IsKindVisible(node.Kind))
            {
                continue;
            }

            RoomLayer? layer = _room.Layers.FirstOrDefault(l => l.Id == node.LayerId);
            if (layer is { Enabled: false })
            {
                continue;
            }

            yield return node;
        }
    }

    /// <summary>Placed Terrain nodes visible right now — 3D-only, mirroring
    /// <see cref="RoomTerrainSubsystem"/>'s own enabled/layer/dimension filter at runtime.</summary>
    private IEnumerable<RoomNode> EnumerateVisibleTerrainNodes()
    {
        if (_viewport.Mode2D)
        {
            yield break;
        }

        foreach (RoomNode node in _room.Nodes)
        {
            if (node.Kind != RoomNodeKind.Terrain
                || !node.Enabled
                || !node.Supports(_room.Dimension)
                || string.IsNullOrWhiteSpace(node.Terrain?.Asset))
            {
                continue;
            }

            RoomLayer? layer = _room.Layers.FirstOrDefault(l => l.Id == node.LayerId);
            if (layer is { Enabled: false })
            {
                continue;
            }

            yield return node;
        }
    }

    private NodeVisual VisualFor(RoomNode node)
    {
        if (node.Background is { } background)
        {
            RoomImageMetadata image = GetRoomImageMetadata(background.Asset);
            return new NodeVisual { Width = image.Width, Height = image.Height };
        }
        string key = ContextVisualCacheKey(node);
        if (_visualCache.TryGetValue(key, out NodeVisual? cached))
        {
            return cached;
        }

        NodeVisual visual = new();
        try
        {
            string prefabReference = node.GameObject?.Prefab ?? string.Empty;
            string? prefabPath = RoomSceneBuilder.ResolvePrefabPath(ProjectRoot, prefabReference);
            if (prefabPath is not null)
            {
                JObject prefab = ObjectDefinitionResolver.PreviewPrefab(ObjectDefinitionResolver.Load(ProjectRoot, prefabPath));
                if (node.GameObject is { } gameObject)
                    prefab = ApplyContextVisualOverrides(prefab, gameObject);
                JArray? components = prefab["components"] as JArray;

                JObject? spriteComponent = components?.OfType<JObject>().FirstOrDefault(component =>
                    string.Equals((string?)component["type"], "SpriteComponent", StringComparison.OrdinalIgnoreCase));
                bool spriteEnabled = spriteComponent is null || ((bool?)spriteComponent["enabled"] ?? true);
                string? sprite = spriteEnabled
                    ? (string?)(spriteComponent?["props"] as JObject)?["Sprite"]
                      ?? (string?)(spriteComponent?["props"] as JObject)?["Image"]
                    : null;
                if (spriteComponent is null && string.IsNullOrWhiteSpace(sprite))
                    sprite = (string?)prefab["sprite"];
                if (spriteEnabled && spriteComponent?["props"] is JObject spriteProperties)
                {
                    visual.Alpha = Math.Clamp(ContextFloat(spriteProperties, "Alpha", 1f), 0f, 1f);
                }

                JObject? modelComponent = components?.OfType<JObject>().FirstOrDefault(component =>
                    string.Equals((string?)component["type"], "ModelRendererComponent", StringComparison.OrdinalIgnoreCase)
                    || string.Equals((string?)component["type"], "ModelComponent", StringComparison.OrdinalIgnoreCase));
                bool modelEnabled = modelComponent is null || ((bool?)modelComponent["enabled"] ?? true);
                JObject? modelProperties = modelComponent?["props"] as JObject;
                string? model = modelEnabled
                    ? (string?)modelProperties?["ModelAsset"] ?? (string?)modelProperties?["Model"]
                    : null;
                if (modelComponent is null && string.IsNullOrWhiteSpace(model))
                    model = (string?)prefab["model"];
                visual.HasModel = !string.IsNullOrWhiteSpace(model);
                visual.ModelAsset = model;
                visual.ModelPath = ResolveModelPath(model);
                visual.HasFlow = visual.ModelPath is not null && ModelAssetLoader.LoadFlow(visual.ModelPath).Enabled;
                if (modelEnabled && modelProperties is not null)
                {
                    visual.ModelScaleX = ContextFloat(modelProperties, "ScaleX", 1f);
                    visual.ModelScaleY = ContextFloat(modelProperties, "ScaleY", 1f);
                    visual.ModelScaleZ = ContextFloat(modelProperties, "ScaleZ", 1f);
                    visual.CastShadows = ContextBool(modelProperties, "CastShadows", true);
                    visual.ReceiveShadows = ContextBool(modelProperties, "ReceiveShadows", true);
                }
                JObject? materialComponent = components?.OfType<JObject>().FirstOrDefault(component =>
                    ((bool?)component["enabled"] ?? true)
                    && string.Equals((string?)component["type"], "MaterialComponent", StringComparison.OrdinalIgnoreCase));
                visual.MaterialAsset = (string?)(materialComponent?["props"] as JObject)?["Asset"] ?? string.Empty;

                JObject? animator = components?.OfType<JObject>()
                    .FirstOrDefault(component => string.Equals(
                        (string?)component["type"], "ModelAnimatorComponent", StringComparison.OrdinalIgnoreCase));
                if (animator?["props"] is JObject animatorProps)
                {
                    visual.AnimationClip = (string?)animatorProps["ClipName"] ?? string.Empty;
                    visual.AnimationFps = MathF.Max(1f, (float?)animatorProps["ClipFps"] ?? 60f);
                    visual.AnimationPlaying = ((bool?)animator["enabled"] ?? true)
                        && ((bool?)animatorProps["Playing"] ?? true);
                    visual.AnimationLoop = (bool?)animatorProps["Loop"] ?? true;
                }

                string? image = ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, sprite);
                if (image is not null)
                {
                    visual.ImagePath = image;
                    ReadPngSize(image, out visual.Width, out visual.Height);
                    visual.OriginX = visual.Width * 0.5f;
                    visual.OriginY = visual.Height * 0.5f;
                }

                int hash = prefabReference.GetHashCode(StringComparison.OrdinalIgnoreCase);
                visual.Tint = new RenderColor(
                    0.35f + (hash & 0x3F) / 160f,
                    0.35f + ((hash >> 6) & 0x3F) / 160f,
                    0.45f + ((hash >> 12) & 0x3F) / 190f);
            }
        }
        catch (Exception exception) when (exception is IOException or Newtonsoft.Json.JsonReaderException)
        {
            // Missing/broken prefab: keep the placeholder visual.
        }

        _visualCache[key] = visual;
        return visual;
    }

    /// <summary>Resolves a prefab's model reference to a <c>.model.json</c> under the project.</summary>
    private string? ResolveModelPath(string? model)
    {
        string path = ResourceNames.Resolve(ProjectRoot, model ?? string.Empty, ResourceType.Model);
        return path.Length > 0 ? path : null;
    }

    /// <summary>
    /// Baked mesh for a model resource, registered once and shared by all its instances. If the
    /// model carries an animated-UV material, its UVs are re-scrolled and re-uploaded each frame so
    /// an authored waterfall keeps flowing where it is placed, not only in the Model Editor.
    /// </summary>
    private PlacedModelMesh? ModelMeshFor(IRenderController renderer, string modelPath)
    {
        if (!_modelMeshCache.TryGetValue(modelPath, out PlacedModelMesh? entry))
        {
            (MeshVertex[] vertices, ushort[] indices) = ModelAssetLoader.LoadBaked(modelPath);
            entry = new PlacedModelMesh { Vertices = vertices, Indices = indices };
            if (vertices.Length > 0 && indices.Length > 0)
            {
                entry.Mesh = renderer.RegisterMesh(vertices, indices);

                ModelAssetLoader.ModelFlowSettings flow = ModelAssetLoader.LoadFlow(modelPath);
                if (flow.Enabled)
                {
                    entry.Flow = new ModelUvAnimator { Enabled = true, Speed = flow.Speed };
                    entry.Flow.Capture(vertices);
                    entry.FlowTexture = renderer.CreateTexture(128, 128, ModelUvAnimator.CreateWaterTexture(128, 128));
                }
            }

            _modelMeshCache[modelPath] = entry;
        }

        if (entry.Flow is { Enabled: true } && entry.Mesh.IsValid
            && entry.Flow.Apply(entry.Vertices, _roomTime))
        {
            renderer.UpdateMesh(entry.Mesh, entry.Vertices);
        }

        return entry.Mesh.IsValid ? entry : null;
    }

    private (float Width, float Height) NodeSize2D(RoomNode node)
    {
        NodeVisual visual = VisualFor(node);
        RoomTransform transform = GetNodeWorldTransform(node);
        if (node.Background?.Layout == RoomBackgroundLayout.StretchRoom) return (_room.Settings.Width, _room.Settings.Height);
        if (node.Background?.Layout == RoomBackgroundLayout.StretchView)
            return (_viewport.SurfaceWidth / MathF.Max(.0001f, _viewport.Zoom2D),
                _viewport.SurfaceHeight / MathF.Max(.0001f, _viewport.Zoom2D));
        return (
            MathF.Max(2f, visual.Width * MathF.Abs(transform.ScaleX)),
            MathF.Max(2f, visual.Height * MathF.Abs(transform.ScaleY)));
    }

    private Vector2[] NodeCorners2D(RoomNode node)
    {
        (float width, float height) = NodeSize2D(node);
        RoomTransform transform = GetNodeWorldTransform(node);
        float angle = transform.RotationZ * MathF.PI / 180f;
        Vector2 center = NodeEditingCenter2D(node, transform, width, height);
        Vector2 half = new(width * 0.5f, height * 0.5f);
        Vector2[] corners =
        [
            new(-half.X, -half.Y),
            new(half.X, -half.Y),
            new(half.X, half.Y),
            new(-half.X, half.Y),
        ];
        for (int i = 0; i < corners.Length; i++)
        {
            corners[i] = center + RotateVector(corners[i], angle);
        }

        return corners;
    }

    private static Vector2[] ScaleHandles2D(Vector2[] corners)
    {
        // 0..3 corners, 4..7 edge midpoints (N E S W order after corners).
        return
        [
            corners[0], corners[1], corners[2], corners[3],
            (corners[0] + corners[1]) * 0.5f,
            (corners[1] + corners[2]) * 0.5f,
            (corners[2] + corners[3]) * 0.5f,
            (corners[3] + corners[0]) * 0.5f,
        ];
    }

    private Vector2 RotateHandle2D(RoomNode node)
    {
        Vector2[] corners = NodeCorners2D(node);
        Vector2 topMid = (corners[0] + corners[1]) * 0.5f;
        Vector2 center = (corners[0] + corners[2]) * 0.5f;
        Vector2 up = topMid - center;
        float length = up.Length();
        if (length < 0.001f)
        {
            return topMid + new Vector2(0f, -30f / _viewport.Zoom2D);
        }

        return topMid + up / length * (26f / _viewport.Zoom2D);
    }

    private bool HitTest2D(RoomNode node, Vector2 world)
    {
        (float width, float height) = NodeSize2D(node);
        RoomTransform transform = GetNodeWorldTransform(node);
        float angle = transform.RotationZ * MathF.PI / 180f;
        Vector2 local = RotateVector(world - NodeEditingCenter2D(node, transform, width, height), -angle);
        return MathF.Abs(local.X) <= width * 0.5f && MathF.Abs(local.Y) <= height * 0.5f;
    }

    private Vector3 NodePosition3D(RoomNode node) => GetNodeWorldMatrix(node).Translation;

    private Vector3 SelectedEulerDegrees(RoomNode node)
    {
        RoomTransform transform = GetNodeWorldTransform(node);
        return new(transform.RotationX, transform.RotationY, transform.RotationZ);
    }

    private EditorGizmoMode CurrentGizmoMode() => Gizmo switch
    {
        GizmoKind.Scale => EditorGizmoMode.Scale,
        GizmoKind.Rotate => EditorGizmoMode.Rotate,
        _ => EditorGizmoMode.Move,
    };

    private const float PixelsPerUnit3D = 32f;

    private (Vector3 Min, Vector3 Max) NodeBounds3D(RoomNode node)
    {
        if (node.Kind == RoomNodeKind.Terrain) return TerrainNodeBounds3D(node);
        GetPlacementBounds(node, out Vector3 min, out Vector3 max);
        (Vector3 worldMin, Vector3 worldMax) = TransformBounds(min, max, GetNodeWorldMatrix(node));
        return (worldMin - new Vector3(.025f), worldMax + new Vector3(.025f));
    }
    /// <summary>
    /// Loads (and caches, keyed by resolved .gterrain path) authoritative height samples and
    /// extents for placement picks. Rendering uses the shared runtime terrain subsystem.
    /// Unresolved references retain fallback selection bounds.
    /// </summary>
    private TerrainPreview? TerrainPreviewFor(RoomNode node)
    {
        string asset = node.Terrain?.Asset ?? string.Empty;
        if (string.IsNullOrWhiteSpace(asset))
        {
            return null;
        }

        string? path = RoomTerrainSubsystem.ResolveTerrainFile(ProjectRoot, asset);
        if (path is null)
        {
            return null;
        }

        if (_terrainPreviewCache.TryGetValue(path, out TerrainPreview? cached))
        {
            return cached;
        }

        try
        {
            TerrainAsset terrain = TerrainAsset.Load(path);
            float minHeight = float.MaxValue;
            float maxHeight = float.MinValue;
            for (int z = 0; z < terrain.ResolutionZ; z++)
            for (int x = 0; x < terrain.ResolutionX; x++)
            {
                float height = terrain.GetHeight(x, z);
                minHeight = MathF.Min(minHeight, height);
                maxHeight = MathF.Max(maxHeight, height);
            }
            TerrainPreview preview = new()
            {
                Asset = terrain,
                HalfExtentX = (terrain.ResolutionX - 1) * terrain.CellSize * 0.5f,
                HalfExtentZ = (terrain.ResolutionZ - 1) * terrain.CellSize * 0.5f,
                MinHeight = minHeight,
                MaxHeight = maxHeight,
            };
            _terrainPreviewCache[path] = preview;
            return preview;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            ReportTerrainPreviewFailure(exception);
            return null;
        }
    }

    private (Vector3 Min, Vector3 Max) TerrainNodeBounds3D(RoomNode node)
    {
        Vector3 position = NodePosition3D(node);
        TerrainPreview? preview = TerrainPreviewFor(node);
        if (preview is null)
        {
            Vector3 fallbackHalf = new(4f, 1f, 4f);
            return (position - fallbackHalf, position + fallbackHalf);
        }

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        Matrix4x4 placement = GetNodeWorldMatrix(node);
        float originX = preview.Asset!.OriginX, originZ = preview.Asset.OriginZ;
        foreach (float x in new[] { originX, originX + preview.HalfExtentX * 2f })
        foreach (float y in new[] { preview.MinHeight, preview.MaxHeight })
        foreach (float z in new[] { originZ, originZ + preview.HalfExtentZ * 2f })
        {
            Vector3 point = Vector3.Transform(new Vector3(x, y, z), placement);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        return (min - new Vector3(0.01f), max + new Vector3(0.01f));
    }

    private float AxisWorldLength() => MathF.Max(0.6f, _viewport.Camera.Distance * 0.14f);

    private float GridWorldSize3D() => MetricGridSize;

    private Vector2 SnapPoint(Vector2 world)
    {
        if (!_room.Settings.SnapEnabled)
        {
            return world;
        }

        float grid = _viewport.Mode2D ? MathF.Max(1f, _room.Settings.GridSize) : GridWorldSize3D();
        return new Vector2(MathF.Round(world.X / grid) * grid, MathF.Round(world.Y / grid) * grid);
    }

    private static Vector2 RotateVector(Vector2 value, float radians)
    {
        float cos = MathF.Cos(radians);
        float sin = MathF.Sin(radians);
        return new Vector2(value.X * cos - value.Y * sin, value.X * sin + value.Y * cos);
    }

    private static bool RayIntersectsAabb(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float t)
    {
        t = 0f;
        float tMin = 0f;
        float tMax = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float o = i == 0 ? origin.X : i == 1 ? origin.Y : origin.Z;
            float d = i == 0 ? direction.X : i == 1 ? direction.Y : direction.Z;
            float lo = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
            float hi = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
            if (MathF.Abs(d) < 1e-8f)
            {
                if (o < lo || o > hi)
                {
                    return false;
                }

                continue;
            }

            float t1 = (lo - o) / d;
            float t2 = (hi - o) / d;
            if (t1 > t2)
            {
                (t1, t2) = (t2, t1);
            }

            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax)
            {
                return false;
            }
        }

        t = tMin;
        return true;
    }

    private static void ReadPngSize(string path, out int width, out int height)
    {
        width = height = 32;
        try
        {
            byte[] header = new byte[26];
            using FileStream stream = File.OpenRead(path);
            if (stream.Read(header, 0, header.Length) > 24
                && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G')
            {
                width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            }
        }
        catch (IOException)
        {
            width = height = 32;
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────────────

    private void DrawRoom2D(IRenderController renderer)
    {
        ApplyPendingAssetRefresh(renderer);
        _viewportOverlay?.Draw2D(renderer, _viewport);
        float grid = MathF.Max(4f, _room.Settings.GridSize);
        float roomW = _room.Settings.Width;
        float roomH = _room.Settings.Height;

        // Room interior slightly lighter than the void.
        renderer.DrawRect(0f, 0f, roomW, roomH, new RenderColor(1f, 1f, 1f, 0.045f), filled: true, depth: 9000);

        // Grid lines (skip when zoomed far out to avoid line soup).
        if (ShowGrid && grid * _viewport.Zoom2D >= 7f)
        {
            RenderColor gridColor = new(GridColor[0], GridColor[1], GridColor[2], GridColor[3]);
            float halfViewW = _viewport.SurfaceWidth / MathF.Max(.0001f, _viewport.Zoom2D) * .5f;
            float halfViewH = _viewport.SurfaceHeight / MathF.Max(.0001f, _viewport.Zoom2D) * .5f;
            float firstX = MathF.Max(0f, MathF.Floor((_viewport.Camera2DX - halfViewW) / grid) * grid);
            float lastX = MathF.Min(roomW, _viewport.Camera2DX + halfViewW);
            float firstY = MathF.Max(0f, MathF.Floor((_viewport.Camera2DY - halfViewH) / grid) * grid);
            float lastY = MathF.Min(roomH, _viewport.Camera2DY + halfViewH);
            for (float x = firstX; x <= lastX + 0.5f; x += grid)
            {
                renderer.DrawLine(x, 0f, x, roomH, gridColor, 1f / _viewport.Zoom2D, depth: 8900);
            }

            for (float y = firstY; y <= lastY + 0.5f; y += grid)
            {
                renderer.DrawLine(0f, y, roomW, y, gridColor, 1f / _viewport.Zoom2D, depth: 8900);
            }
        }

        // Room border.
        RenderColor border = ToRenderColor(EditorChrome.Accent, 0.85f);
        float thickness = 2f / _viewport.Zoom2D;
        renderer.DrawLine(0f, 0f, roomW, 0f, border, thickness, depth: 100);
        renderer.DrawLine(roomW, 0f, roomW, roomH, border, thickness, depth: 100);
        renderer.DrawLine(roomW, roomH, 0f, roomH, border, thickness, depth: 100);
        renderer.DrawLine(0f, roomH, 0f, 0f, border, thickness, depth: 100);

        if (ShowBackgrounds) DrawBackgrounds2D(renderer);
        if (ShowTileLayers) DrawTileLayers2D(renderer);
        DrawViewportRegions2D(renderer);

        foreach (RoomNode node in EnumerateVisibleNodes())
        {
            NodeVisual visual = VisualFor(node);
            RoomTransform nodeWorld = GetNodeWorldTransform(node);
            (float width, float height) = NodeSize2D(node);
            if (!IsRoomRectVisible(nodeWorld.X, nodeWorld.Y, width, height, nodeWorld.RotationZ)) continue;
            if (visual.ImagePath is not null
                && _textures.TryGet(renderer, visual.ImagePath, out TextureHandle texture, out _, out _))
            {
                renderer.DrawSprite(new SpriteDrawCall
                {
                    Texture = texture,
                    X = nodeWorld.X,
                    Y = nodeWorld.Y,
                    Width = width,
                    Height = height,
                    OriginX = width * 0.5f,
                    OriginY = height * 0.5f,
                    Rotation = nodeWorld.RotationZ,
                    ScaleX = MathF.Sign(nodeWorld.ScaleX == 0f ? 1f : nodeWorld.ScaleX),
                    ScaleY = MathF.Sign(nodeWorld.ScaleY == 0f ? 1f : nodeWorld.ScaleY),
                    Alpha = 1f,
                    Tint = RenderColor.White,
                    Depth = EffectiveNodeDepth(node),
                });
            }
            else
            {
                renderer.DrawRect(
                    nodeWorld.X - width * 0.5f,
                    nodeWorld.Y - height * 0.5f,
                    width,
                    height,
                    new RenderColor(visual.Tint.R, visual.Tint.G, visual.Tint.B, 0.85f),
                    filled: true,
                    depth: EffectiveNodeDepth(node));
            }
        }
    }

    /// <summary>
    /// Scene clock driving animated-UV materials on placed models. Settable so headless captures
    /// can step it deterministically instead of depending on how many frames have been rendered.
    /// </summary>
    public float RoomTime
    {
        get => _roomTime;
        set => _roomTime = MathF.Max(0f, value);
    }

    /// <summary>
    /// Draws background layers behind everything else, mirroring the runtime's own layout rules
    /// (<c>RoomRenderSubsystem.DrawBackgrounds2D</c>): stretch-to-room, or tile/repeat across it.
    /// Deepest layer first, so the editor preview matches play-time stacking.
    /// </summary>
    private void DrawBackgrounds2D(IRenderController renderer)
    {
        foreach (RoomNode node in _room.Nodes
            .Where(n => n.Kind == RoomNodeKind.Background
                && n.Enabled
                && n.Supports(_room.Dimension)
                && n.Background is not null
                && LayerFor(n)?.Enabled != false)
            .OrderByDescending(EffectiveNodeDepth))
        {
            RoomBackgroundData background = node.Background!;
            RoomTransform nodeWorld = GetNodeWorldTransform(node);
            if (background.Mode != RoomBackgroundMode.TwoD)
            {
                continue;   // Sky/Billboard/WorldPlane are 3D-only presentations
            }

            string? image = GetRoomImageMetadata(background.Asset).ImagePath;
            if (image is null || !_textures.TryGet(renderer, image, out TextureHandle texture, out int imageW, out int imageH))
            {
                continue;
            }

            float width = imageW * nodeWorld.ScaleX;
            float height = imageH * nodeWorld.ScaleY;
            float previewTime = CanInspectNodeInActiveContext(node) ? 0 : _roomTime;
            float scrollX = background.Scroll is { Length: > 0 } ? background.Scroll[0] * previewTime : 0f;
            float scrollY = background.Scroll is { Length: > 1 } ? background.Scroll[1] * previewTime : 0f;
            float baseX = nodeWorld.X + scrollX;
            float baseY = nodeWorld.Y + scrollY;
            if (background.Layout == RoomBackgroundLayout.StretchRoom)
            {
                width = _room.Settings.Width;
                height = _room.Settings.Height;
            }
            else if (background.Layout == RoomBackgroundLayout.StretchView)
            {
                width = _viewport.SurfaceWidth / MathF.Max(.0001f, _viewport.Zoom2D);
                height = _viewport.SurfaceHeight / MathF.Max(.0001f, _viewport.Zoom2D);
                baseX = _viewport.Camera2DX - width * .5f;
                baseY = _viewport.Camera2DY - height * .5f;
            }

            width = MathF.Max(1f, width);
            height = MathF.Max(1f, height);
            int repeatX = background.Layout == RoomBackgroundLayout.Tile || background.RepeatX
                ? Math.Max(1, (int)MathF.Ceiling(_room.Settings.Width / width))
                : 1;
            int repeatY = background.Layout == RoomBackgroundLayout.Tile || background.RepeatY
                ? Math.Max(1, (int)MathF.Ceiling(_room.Settings.Height / height))
                : 1;

            float viewHalfW = _viewport.SurfaceWidth / MathF.Max(.0001f, _viewport.Zoom2D) * .5f;
            float viewHalfH = _viewport.SurfaceHeight / MathF.Max(.0001f, _viewport.Zoom2D) * .5f;
            float pad = MathF.Max(width, height); // include rotated edge repeats
            int firstX = Math.Clamp((int)MathF.Floor((_viewport.Camera2DX - viewHalfW - baseX - pad) / width), 0, repeatX - 1);
            int lastX = Math.Clamp((int)MathF.Ceiling((_viewport.Camera2DX + viewHalfW - baseX + pad) / width), 0, repeatX - 1);
            int firstY = Math.Clamp((int)MathF.Floor((_viewport.Camera2DY - viewHalfH - baseY - pad) / height), 0, repeatY - 1);
            int lastY = Math.Clamp((int)MathF.Ceiling((_viewport.Camera2DY + viewHalfH - baseY + pad) / height), 0, repeatY - 1);
            int depth = EffectiveNodeDepth(node);
            for (int y = firstY; y <= lastY; y++)
            {
                for (int x = firstX; x <= lastX; x++)
                {
                    if (!IsRoomRectVisible(baseX + x * width + width * .5f, baseY + y * height + height * .5f,
                        width, height, nodeWorld.RotationZ)) continue;
                    renderer.DrawSprite(new SpriteDrawCall
                    {
                        Texture = texture,
                        X = baseX + x * width + width * 0.5f,
                        Y = baseY + y * height + height * 0.5f,
                        Width = width,
                        Height = height,
                        OriginX = width * 0.5f,
                        OriginY = height * 0.5f,
                        ScaleX = 1f,
                        ScaleY = 1f,
                        Rotation = nodeWorld.RotationZ,
                        Alpha = background.Opacity,
                        Tint = TintToRenderColor(background.TintArgb),
                        Depth = depth,
                    });
                }
            }
        }
    }

    /// <summary>
    /// Draws every painted TileLayer beneath the room's objects. Each cell samples its tile's
    /// sub-rectangle of the tile set image, so what the editor shows matches what
    /// <c>RoomAssetLoader.SpawnTileLayer</c> spawns at play time.
    /// </summary>
    private void DrawTileLayers2D(IRenderController renderer)
    {
        foreach (RoomNode node in _room.Nodes)
        {
            if (node.Kind != RoomNodeKind.TileLayer || !node.Enabled || node.TileLayer is not { } layer)
            {
                continue;
            }

            if (_room.Layers.FirstOrDefault(l => l.Id == node.LayerId) is { Enabled: false })
            {
                continue;
            }

            TileSetInfo? info = GetRoomTilesetMetadata(layer.Tileset);
            string? image = info is not null ? GetRoomImageMetadata(info.ImagePath).ImagePath : null;

            TextureHandle texture = default;
            int texW = 0, texH = 0;
            bool textured = image is not null
                && _textures.TryGet(renderer, image, out texture, out texW, out texH);

            RoomTransform nodeWorld = GetNodeWorldTransform(node);
            Matrix4x4 nodeMatrix = RoomHierarchyTransforms.Matrix(nodeWorld);
            int layerDepth = EffectiveNodeDepth(node);
            foreach (RoomTileCell cell in layer.Cells)
            {
                float cellScaleX = MathF.Max(0.001f, MathF.Abs(cell.ScaleX));
                float cellScaleY = MathF.Max(0.001f, MathF.Abs(cell.ScaleY));
                Vector3 position = Vector3.Transform(new Vector3(
                    (cell.X + .5f) * layer.CellWidth + cell.OffsetX,
                    (cell.Y + .5f) * layer.CellHeight + cell.OffsetY, 0), nodeMatrix);
                float x = position.X;
                float y = position.Y;
                float width = layer.CellWidth * MathF.Abs(nodeWorld.ScaleX) * cellScaleX;
                float height = layer.CellHeight * MathF.Abs(nodeWorld.ScaleY) * cellScaleY;
                if (!IsRoomRectVisible(x, y, width, height, nodeWorld.RotationZ + cell.Rotation)) continue;

                if (textured && info is not null && texW > 0 && texH > 0)
                {
                    int columns = info.ColumnsFor(texW);
                    int index = cell.TileY * Math.Max(1, columns) + cell.TileX;
                    Rectangle tile = info.TileRect(index, texW);
                    (int sx, int sy, int sw, int sh) = (tile.X, tile.Y, tile.Width, tile.Height);

                    // UvRect is (u0, v0, u1, v1) — normalised CORNERS, not position+size. Passing
                    // width/height made the second component fail the shader's `u1 > u0` sanity check
                    // for every tile except index 0, so it silently fell back to the full texture and
                    // painted the entire sheet into each cell (NEXT-059). The runtime's own tile path
                    // in RoomRenderSubsystem always used corners, which is why the game looked right
                    // and only the editor preview was garbled.
                    renderer.DrawSprite(new SpriteDrawCall
                    {
                        Texture = texture,
                        X = x,
                        Y = y,
                        Width = width,
                        Height = height,
                        OriginX = width * .5f,
                        OriginY = height * .5f,
                        ScaleX = (nodeWorld.ScaleX < 0 ? -1f : 1f) * (cell.FlipX ? -1f : 1f),
                        ScaleY = (nodeWorld.ScaleY < 0 ? -1f : 1f) * (cell.FlipY ? -1f : 1f),
                        Rotation = nodeWorld.RotationZ + cell.Rotation,
                        UvRect = new Vector4(
                            sx / (float)texW,
                            sy / (float)texH,
                            (sx + sw) / (float)texW,
                            (sy + sh) / (float)texH),
                        Alpha = 1f,
                        Tint = RenderColor.White,
                        Depth = layerDepth,
                    });
                }
                else
                {
                    // No resolvable image yet — draw a readable placeholder so painting is visible.
                    renderer.DrawRect(x - width * .5f, y - height * .5f, width, height,
                        new RenderColor(0.35f, 0.62f, 0.45f, 0.9f), filled: true, depth: layerDepth);
                }
            }
        }

        DrawPlacementGhost(renderer);
    }

    /// <summary>
    /// Translucent preview of the armed object at the position a click would place it.
    /// </summary>
    /// <remarks>
    /// Drawn above everything (negative depth) and deliberately semi-transparent with a dashed-ish
    /// outline, so it reads as "not yet placed" rather than as an instance that already exists.
    /// Falls back to an outlined box when the object has no resolvable sprite, because "armed but
    /// invisible" is the exact confusion this preview exists to remove.
    /// </remarks>
    private void DrawPlacementGhost(IRenderController renderer)
    {
        if (_ghostWorld is not Vector2 centre || _pendingPlacementPath is null)
        {
            return;
        }

        NodeVisual visual = GhostVisual();
        float w = visual.Width;
        float h = visual.Height;
        float left = centre.X - (w * 0.5f);
        float top = centre.Y - (h * 0.5f);

        if (visual.ImagePath is not null
            && _textures.TryGet(renderer, visual.ImagePath, out TextureHandle texture, out _, out _))
        {
            renderer.DrawSprite(new SpriteDrawCall
            {
                Texture = texture,
                X = centre.X,
                Y = centre.Y,
                Width = w,
                Height = h,
                OriginX = w * 0.5f,
                OriginY = h * 0.5f,
                ScaleX = 1f,
                ScaleY = 1f,
                UvRect = new Vector4(0f, 0f, 1f, 1f),
                Alpha = 0.55f,
                Tint = RenderColor.White,
                Depth = -600,
            });
        }
        else
        {
            renderer.DrawRect(left, top, w, h, new RenderColor(0.55f, 0.75f, 1f, 0.30f),
                filled: true, depth: -600);
        }

        // Outline + crosshair: the outline shows the footprint, the crosshair shows the exact
        // snapped point, which matters when grid snapping is on and the cursor is between cells.
        RenderColor accent = new(0.45f, 0.78f, 1f, 0.95f);
        float thickness = 1.5f / MathF.Max(0.0001f, _viewport.Zoom2D);
        renderer.DrawLine(left, top, left + w, top, accent, thickness, depth: -700);
        renderer.DrawLine(left + w, top, left + w, top + h, accent, thickness, depth: -700);
        renderer.DrawLine(left + w, top + h, left, top + h, accent, thickness, depth: -700);
        renderer.DrawLine(left, top + h, left, top, accent, thickness, depth: -700);

        float arm = MathF.Max(6f, MathF.Min(w, h) * 0.25f);
        renderer.DrawLine(centre.X - arm, centre.Y, centre.X + arm, centre.Y, accent, thickness, depth: -700);
        renderer.DrawLine(centre.X, centre.Y - arm, centre.X, centre.Y + arm, accent, thickness, depth: -700);
    }

    /// <summary>Resolves the armed object's visual, reusing <see cref="VisualFor"/>'s cache.</summary>
    private NodeVisual GhostVisual()
    {
        string reference = ResourceNames.Name(ProjectRoot, _pendingPlacementPath!);
        return VisualFor(new RoomNode
        {
            Kind = RoomNodeKind.GameObject,
            GameObject = new RoomGameObjectData { Prefab = reference },
        });
    }

    /// <summary>Resolves a tile-layer's tile-set reference to a file under the project.</summary>
    private string? ResolveTileSetPath(string? reference)
    {
        string path = ResourceNames.Resolve(ProjectRoot, reference ?? string.Empty, ResourceType.Image);
        return path.Length > 0 ? path : null;
    }

    private void DrawRoom3D(IRenderController renderer)
    {
        _runtimeModelPreview.BeginFrame();
        try { DrawRoom3DFrame(renderer); }
        finally { _runtimeModelPreview.EndFrame(); }
    }

    private void DrawRoom3DFrame(IRenderController renderer)
    {
        ApplyPendingAssetRefresh(renderer);
        EnsureMeshes(renderer);
        if (!_roomTimePaused) _roomTime += 1f / 60f;
        SubmitRoomEnvironment(renderer);
        DrawBackgrounds3D(renderer);

        foreach (RoomNode node in EnumerateVisibleNodes())
        {
            NodeVisual visual = VisualFor(node);
            if (visual.ImagePath is not null
                && !visual.HasModel
                && _textures.TryGet(renderer, visual.ImagePath, out TextureHandle texture, out int texW, out int texH))
            {
                float width = texW / PixelsPerUnit3D;
                float height = texH / PixelsPerUnit3D;
                Matrix4x4 world =
                    Matrix4x4.CreateScale(width, height, 1f)
                    * Matrix4x4.CreateTranslation(0f, height * .5f, 0f)
                    * GetNodeWorldMatrix(node);
                renderer.DrawMesh(new MeshDrawCall
                {
                    Mesh = _unitQuad,
                    Texture = texture,
                    World = world,
                    Tint = RenderColor.White,
                    Alpha = visual.Alpha,
                    Flags = MeshDrawFlags.NoCull,
                });
            }
            else
            {
                Matrix4x4 modelWorld = Matrix4x4.CreateScale(visual.ModelScaleX, visual.ModelScaleY, visual.ModelScaleZ)
                    * GetNodeWorldMatrix(node);

                // Canonical models use the shipping material/skin renderer here, just as they do
                // in Object preview and F5. Legacy animated-UV models retain their specialised
                // CPU-flow path until that material becomes a canonical shader binding.
                if (!visual.HasFlow
                    && !string.IsNullOrWhiteSpace(visual.ModelAsset)
                    && _runtimeModelPreview.DrawModel(
                        ProjectRoot,
                        visual.ModelAsset,
                        visual.MaterialAsset,
                        modelWorld,
                        new Draw3DComponent
                        {
                            Visible = true,
                            CastShadows = visual.CastShadows,
                            ReceiveShadows = visual.ReceiveShadows,
                        },
                        new ModelRendererComponent
                        {
                            ScaleX = visual.ModelScaleX,
                            ScaleY = visual.ModelScaleY,
                            ScaleZ = visual.ModelScaleZ,
                            CastShadows = visual.CastShadows,
                            ReceiveShadows = visual.ReceiveShadows,
                        },
                        new RuntimeModelAnimationState(
                            visual.AnimationClip,
                            visual.AnimationPlaying ? _roomTime : 0f,
                            visual.AnimationFps,
                            visual.AnimationLoop),
                        renderer))
                {
                    continue;
                }

                // A legacy model-backed object draws its real baked geometry; only objects with
                // neither a sprite nor a resolvable model fall back to the placeholder cube.
                PlacedModelMesh? placedModel = visual.ModelPath is not null
                    ? ModelMeshFor(renderer, visual.ModelPath)
                    : null;

                if (placedModel is not null)
                {
                    renderer.DrawMesh(new MeshDrawCall
                    {
                        Mesh = placedModel.Mesh,
                        Texture = placedModel.FlowTexture,
                        World = modelWorld,
                        Tint = RenderColor.White,
                        Alpha = 1f,
                        Flags = MeshDrawFlags.NoCull,
                    });
                    continue;
                }

                Matrix4x4 world = Matrix4x4.CreateTranslation(0f, .5f, 0f) * GetNodeWorldMatrix(node);
                renderer.DrawMesh(new MeshDrawCall
                {
                    Mesh = _unitCube,
                    World = world,
                    Tint = visual.Tint,
                    Alpha = 1f,
                    Flags = MeshDrawFlags.NoCull,
                });
            }
        }

        DrawAuthoredTerrainPreview(renderer);
        DrawPlacementModelPreview(renderer);
    }

    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        base.OnAssetDependenciesChanged(changes);
        _assetRefreshPending = true;
        InvalidateRoomMetadata();
        RefreshPalette();
        RefreshPhase4Ui();
        _viewport.Invalidate(true);
        UpdateStatus($"Live preview rebound · generation {changes.Generation}");
    }

    private void ApplyPendingAssetRefresh(IRenderController renderer)
    {
        if (!_assetRefreshPending) return;
        foreach (PlacedModelMesh model in _modelMeshCache.Values)
        {
            if (model.Mesh.IsValid) renderer.ReleaseMesh(model.Mesh);
            if (model.FlowTexture.IsValid) renderer.ReleaseTexture(model.FlowTexture);
        }
        ReleaseAuthoredTerrainPreviews();
        _runtimeModelPreview.InvalidateAssets(renderer);
        _modelMeshCache.Clear();
        _terrainPreviewCache.Clear();
        _visualCache.Clear();
        _textures.Clear();
        _assetRefreshPending = false;
    }

    private void EnsureMeshes(IRenderController renderer)
    {
        if (!_unitCube.IsValid)
        {
            _unitCube = MeshGeometry.RegisterCube(renderer, RenderColor.White, 1f);
        }

        if (!_unitQuad.IsValid)
        {
            _unitQuad = MeshGeometry.RegisterQuad(renderer, RenderColor.White);
        }
    }

    private void DrawOverlay(IRenderController renderer)
    {
        _viewportOverlay?.DrawOverlay(renderer, _viewport);
        DrawViewportRegions3D(renderer);
        DrawSurfacePlacementPreview(renderer);
        if (!_viewport.Mode2D && ShowGrid)
        {
            DrawRoomMetricGrid(renderer);
        }
        else { LastMetricGridLayout = null; LastMetricGridDrawnLines = 0; }

        // Hover outline.
        if (_hover is not null && !_selection.Contains(_hover))
        {
            DrawNodeOutline(renderer, _hover, ToRenderColor(EditorChrome.Muted, 0.8f), 1.5f);
        }

        // An individually selected tile owns the selection overlay. Drawing the TileLayer node's
        // generic 32x32 placeholder on top would be misleading and would put resize handles in
        // the wrong place.
        if (DrawSelectedTileOverlay(renderer))
        {
            return;
        }

        if (_selected is null)
        {
            return;
        }

        RenderColor accent = ToRenderColor(EditorChrome.Accent, 1f);
        RenderColor secondary = ToRenderColor(EditorChrome.Accent, 0.55f);
        foreach (RoomNode node in _selection)
        {
            if (!ReferenceEquals(node, _selected)) DrawNodeOutline(renderer, node, secondary, 1.5f);
        }
        DrawNodeOutline(renderer, _selected, accent, 2f);
        if (!TransformGizmoVisible) return;

        if (_viewport.Mode2D)
        {
            Vector2[] corners = NodeCorners2D(_selected);
            Vector2[] handles = ScaleHandles2D(corners);
            if (Gizmo is GizmoKind.Scale or GizmoKind.Move)
            {
                foreach (Vector2 handle in handles)
                {
                    Vector2 surface = _viewport.World2DToSurface(handle);
                    renderer.DrawRect(surface.X - 4f, surface.Y - 4f, 8f, 8f, RenderColor.White, filled: true, depth: -9000);
                    renderer.DrawRect(surface.X - 4f, surface.Y - 4f, 8f, 8f, accent, filled: false, depth: -9001);
                }
            }

            if (Gizmo == GizmoKind.Rotate)
            {
                Vector2 center = (corners[0] + corners[2]) * 0.5f;
                Vector2 handleWorld = RotateHandle2D(_selected);
                Vector2 centerSurface = _viewport.World2DToSurface(center);
                Vector2 handleSurface = _viewport.World2DToSurface(handleWorld);
                renderer.DrawLine(centerSurface.X, centerSurface.Y, handleSurface.X, handleSurface.Y, accent, 1.5f, depth: -9000);
                DrawCircle(renderer, handleSurface, 6f, RenderColor.White, accent);
            }

            if (Gizmo == GizmoKind.Move)
            {
                EditorTransformGizmo.Draw2DAxes(
                    _viewport,
                    renderer,
                    new Vector2(GetNodeWorldTransform(_selected).X, GetNodeWorldTransform(_selected).Y),
                    includeZ: false);
            }

            return;
        }

        // 3D gizmo overlay.
        Vector3 origin = NodePosition3D(_selected);
        EditorTransformGizmo.Draw3D(
            _viewport,
            renderer,
            origin,
            AxisWorldLength(),
            CurrentGizmoMode(),
            GizmoSpace,
            SelectedEulerDegrees(_selected),
            includeZ: true);
    }

    private void DrawNodeOutline(IRenderController renderer, RoomNode node, RenderColor color, float thickness)
    {
        if (_viewport.Mode2D)
        {
            Vector2[] corners = NodeCorners2D(node);
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = _viewport.World2DToSurface(corners[i]);
                Vector2 b = _viewport.World2DToSurface(corners[(i + 1) % 4]);
                renderer.DrawLine(a.X, a.Y, b.X, b.Y, color, thickness, depth: -8999);
            }

            return;
        }

        (Vector3 min, Vector3 max) = NodeBounds3D(node);
        Span<Vector3> points =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z),
            new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z),
            new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
            new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        Span<(int A, int B)> edges =
        [
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        ];
        foreach ((int a, int b) in edges)
        {
            Vector3 sa = _viewport.WorldToSurface(points[a]);
            Vector3 sb = _viewport.WorldToSurface(points[b]);
            if (sa.Z is > 0f and < 1f && sb.Z is > 0f and < 1f)
            {
                renderer.DrawLine(sa.X, sa.Y, sb.X, sb.Y, color, thickness, depth: -8999);
            }
        }
    }

    private static void DrawCircle(IRenderController renderer, Vector2 center, float radius, RenderColor fill, RenderColor outline)
    {
        renderer.DrawRect(center.X - radius, center.Y - radius, radius * 2f, radius * 2f, fill, filled: true, depth: -9002);
        DrawCircleOutline(renderer, center, radius, outline, 1.5f);
    }

    private static void DrawCircleOutline(IRenderController renderer, Vector2 center, float radius, RenderColor color, float thickness)
    {
        const int segments = 28;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i / (float)segments * MathF.Tau;
            float a1 = (i + 1) / (float)segments * MathF.Tau;
            renderer.DrawLine(
                center.X + MathF.Cos(a0) * radius,
                center.Y + MathF.Sin(a0) * radius,
                center.X + MathF.Cos(a1) * radius,
                center.Y + MathF.Sin(a1) * radius,
                color,
                thickness,
                depth: -9002);
        }
    }

    private static RenderColor ToRenderColor(Color color, float alpha) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f, alpha);

    private static RenderColor TintToRenderColor(int argb) => new(
        ((argb >> 16) & 255) / 255f,
        ((argb >> 8) & 255) / 255f,
        (argb & 255) / 255f,
        ((argb >> 24) & 255) / 255f);

    // ── Inspector ────────────────────────────────────────────────────────────────

    private static NumericUpDown MakeNumeric(decimal min, decimal max, int decimals)
    {
        NumericUpDown numeric = new()
        {
            DecimalPlaces = decimals,
            Increment = decimals == 0 ? 1 : 0.5m,
            Maximum = max,
            Minimum = min,
            Width = 66,
        };
        EditorChrome.StyleField(numeric);
        return numeric;
    }

    private void BuildInspectorLayout()
    {
        _inspectorPanel.Controls.Clear();
        int y = 8;

        Control Section(string text)
        {
            Label label = new()
            {
                AutoSize = true,
                BackColor = Color.Transparent,
                Font = EditorChrome.HeadingFont,
                ForeColor = EditorChrome.Muted,
                Location = new Point(12, y),
                Text = text.ToUpperInvariant(),
            };
            y += 22;
            return label;
        }

        Control Row(string caption, params Control[] fields)
        {
            Panel row = new()
            {
                BackColor = Color.Transparent,
                Location = new Point(12, y),
                Size = new Size(224, 26),
            };
            Label label = new()
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                Font = EditorChrome.SmallFont,
                ForeColor = EditorChrome.Muted,
                Location = new Point(0, 5),
                Size = new Size(16, 18),
                Text = caption,
            };
            row.Controls.Add(label);
            int x = 18;
            foreach (Control field in fields)
            {
                field.Location = new Point(x, 0);
                row.Controls.Add(field);
                x += field.Width + 3;
            }

            y += 30;
            return row;
        }

        _inspectorPanel.Controls.Add(Section("Instance"));
        _nameBox.Location = new Point(12, y);
        _inspectorPanel.Controls.Add(_nameBox);
        y += 30;
        _nameBox.Validated += (_, _) =>
        {
            if (!_syncingInspector && _selected is not null && _selection.Count == 1)
            {
                SetNodeName(_selected, _nameBox.Text);
            }
        };

        _inspectorPanel.Controls.Add(Section("Position"));
        _inspectorPanel.Controls.Add(Row("X", _position[0]));
        _inspectorPanel.Controls.Add(Row("Y", _position[1]));
        _inspectorPanel.Controls.Add(Row("Z", _position[2]));
        _inspectorPanel.Controls.Add(Section("Rotation"));
        _inspectorPanel.Controls.Add(Row("X", _rotation[0]));
        _inspectorPanel.Controls.Add(Row("Y", _rotation[1]));
        _inspectorPanel.Controls.Add(Row("Z", _rotation[2]));
        _inspectorPanel.Controls.Add(Section("Scale"));
        _inspectorPanel.Controls.Add(Row("X", _scale[0]));
        _inspectorPanel.Controls.Add(Row("Y", _scale[1]));
        _inspectorPanel.Controls.Add(Row("Z", _scale[2]));

        _inspectorPanel.Controls.Add(Section("Layer"));
        _nodeLayerCombo.Location = new Point(12, y);
        _inspectorPanel.Controls.Add(_nodeLayerCombo);
        y += 32;
        EditorChrome.StyleField(_nodeLayerCombo);
        _nodeLayerCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingInspector && _selected is not null && _nodeLayerCombo.SelectedIndex >= 0
                && _nodeLayerCombo.SelectedIndex < _room.Layers.Count)
            {
                SetSelectionLayer(_room.Layers[_nodeLayerCombo.SelectedIndex]);
            }
        };

        _nodeEnabledCheck.Location = new Point(12, y);
        _inspectorPanel.Controls.Add(_nodeEnabledCheck);
        y += 30;

        // Hierarchy: parenting composes this node's transform onto another's at play time.
        _inspectorPanel.Controls.Add(Section("Parent"));
        _nodeParentCombo.Location = new Point(12, y);
        _inspectorPanel.Controls.Add(_nodeParentCombo);
        y += 32;
        EditorChrome.StyleField(_nodeParentCombo);
        _nodeParentCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingInspector || _selected is null)
            {
                return;
            }

            RoomNode? parent = _nodeParentCombo.SelectedIndex > 0
                ? _nodeParentCombo.SelectedItem as RoomNode
                : null;
            if (SetNodeParent(_selected, parent))
            {
                SyncInspector();
            }
        };

        // Views: which placed object the game camera follows.
        _setGameCameraButton.Location = new Point(12, y);
        _inspectorPanel.Controls.Add(_setGameCameraButton);
        y += 32;
        EditorChrome.StyleField(_setGameCameraButton);
        _setGameCameraButton.Click += (_, _) =>
        {
            if (_selected is not null)
            {
                SetActiveGameCamera(ReferenceEquals(ActiveGameCamera, _selected) ? null : _selected);
                SyncInspector();
            }
        };
        _nodeEnabledCheck.CheckedChanged += (_, _) =>
        {
            if (!_syncingInspector && _selected is not null)
            {
                SetSelectionEnabled(_nodeEnabledCheck.Checked);
            }
        };

        _inspectorPanel.Controls.Add(Section("Room"));
        _inspectorPanel.Controls.Add(Row("W", _roomWidth));
        _inspectorPanel.Controls.Add(Row("H", _roomHeight));
        EditorChrome.StyleField(_nameBox);
        _roomWidth.Width = _roomHeight.Width = 90;
        _roomWidth.ValueChanged += (_, _) =>
        {
            if (!_syncingInspector)
            {
                SetRoomDimensions((int)_roomWidth.Value, _room.Settings.Height);
            }
        };
        _roomHeight.ValueChanged += (_, _) =>
        {
            if (!_syncingInspector)
            {
                SetRoomDimensions(_room.Settings.Width, (int)_roomHeight.Value);
            }
        };

        for (int i = 0; i < 3; i++)
        {
            int axis = i;
            _position[i].ValueChanged += (_, _) => ApplyInspectorTransform(t =>
            {
                if (axis == 0) { t.X = (float)_position[0].Value; }
                else if (axis == 1) { t.Y = (float)_position[1].Value; }
                else { t.Z = (float)_position[2].Value; }
            });
            _rotation[i].ValueChanged += (_, _) => ApplyInspectorTransform(t =>
            {
                if (axis == 0) { t.RotationX = (float)_rotation[0].Value; }
                else if (axis == 1) { t.RotationY = (float)_rotation[1].Value; }
                else { t.RotationZ = (float)_rotation[2].Value; }
            });
            _scale[i].ValueChanged += (_, _) => ApplyInspectorTransform(t =>
            {
                if (axis == 0) { t.ScaleX = (float)_scale[0].Value; }
                else if (axis == 1) { t.ScaleY = (float)_scale[1].Value; }
                else { t.ScaleZ = (float)_scale[2].Value; }
            });
        }

        BuildPhase4Inspector(y);
    }

    private void ApplyInspectorTransform(Action<RoomTransform> apply)
    {
        ApplyInspectorSelectionTransform(apply);
    }

    private void SyncInspectorTransformDuringDrag() => QueueRoomUiRefresh();

    private void SyncInspector()
    {
        _syncingInspector = true;
        try
        {
            _nodeLayerCombo.Items.Clear();
            foreach (RoomLayer layer in _room.Layers)
            {
                _nodeLayerCombo.Items.Add(layer.Locked ? layer.Name + " (locked)" : layer.Name);
            }

            bool has = _selected is not null;
            bool editable = has && CanEditNodeInActiveContext(_selected!);
            bool tileCell = TileSelectionValid;
            _nameBox.Enabled = editable && !tileCell && _selection.Count == 1;
            _nodeLayerCombo.Enabled = editable && !tileCell;
            _nodeEnabledCheck.Enabled = editable && !tileCell;
            _nodeParentCombo.Enabled = editable && !tileCell && _selection.Count == 1;
            _setGameCameraButton.Enabled = editable && !tileCell && _selection.Count == 1 && _selected!.Kind == RoomNodeKind.GameObject;

            // Parent picker: "(none)" plus every other node that wouldn't create a loop.
            _nodeParentCombo.Items.Clear();
            _nodeParentCombo.Items.Add("(none)");
            if (_selected is not null)
            {
                foreach (RoomNode candidate in _room.Nodes)
                {
                    if (!ReferenceEquals(candidate, _selected) && !WouldCycle(_selected, candidate))
                    {
                        _nodeParentCombo.Items.Add(candidate);
                    }
                }

                int parentIndex = 0;
                for (int i = 1; i < _nodeParentCombo.Items.Count; i++)
                {
                    if (_nodeParentCombo.Items[i] is RoomNode candidate
                        && string.Equals(candidate.Id, _selected.ParentId, StringComparison.OrdinalIgnoreCase))
                    {
                        parentIndex = i;
                        break;
                    }
                }

                _nodeParentCombo.SelectedIndex = parentIndex;
                _setGameCameraButton.Text = ReferenceEquals(ActiveGameCamera, _selected)
                    ? "✓ Game camera (click to clear)"
                    : "Set as game camera";
            }
            else
            {
                _nodeParentCombo.SelectedIndex = 0;
                _setGameCameraButton.Text = "Set as game camera";
            }
            foreach (NumericUpDown numeric in _position.Concat(_rotation).Concat(_scale))
            {
                numeric.Enabled = editable && !tileCell;
            }

            if (_selected is not null)
            {
                RoomTransform t = _selected.Transform;
                _nameBox.Text = tileCell && _selectedTileCell is not null
                    ? $"Tile {_selectedTileCell.TileX},{_selectedTileCell.TileY} · {_selected.Name}"
                    : _selection.Count == 1 ? _selected.Name : $"{_selection.Count} instances selected";
                _position[0].Value = ClampDecimal(_position[0], (decimal)t.X);
                _position[1].Value = ClampDecimal(_position[1], (decimal)t.Y);
                _position[2].Value = ClampDecimal(_position[2], (decimal)t.Z);
                _rotation[0].Value = ClampDecimal(_rotation[0], (decimal)t.RotationX);
                _rotation[1].Value = ClampDecimal(_rotation[1], (decimal)t.RotationY);
                _rotation[2].Value = ClampDecimal(_rotation[2], (decimal)t.RotationZ);
                _scale[0].Value = ClampDecimal(_scale[0], (decimal)t.ScaleX);
                _scale[1].Value = ClampDecimal(_scale[1], (decimal)t.ScaleY);
                _scale[2].Value = ClampDecimal(_scale[2], (decimal)t.ScaleZ);
                int layerIndex = _room.Layers.FindIndex(l => l.Id == _selected.LayerId);
                _nodeLayerCombo.SelectedIndex = layerIndex >= 0 ? layerIndex : -1;
                _nodeEnabledCheck.Checked = _selected.Enabled;
            }
            else
            {
                _nameBox.Text = string.Empty;
            }

            _roomWidth.Value = ClampDecimal(_roomWidth, _room.Settings.Width);
            SyncPhase4Inspector();
            _inspector?.InspectNode(_selected);
            _navigation?.ObjectsPanel?.SyncSelection();
        }
        finally
        {
            _syncingInspector = false;
        }
    }

    private static decimal ClampDecimal(NumericUpDown numeric, decimal value) =>
        Math.Clamp(value, numeric.Minimum, numeric.Maximum);

    // ── Toolbar/status ───────────────────────────────────────────────────────────

    private void ToggleGizmoSpace() =>
        SetGizmoSpace(GizmoSpace == EditorGizmoSpace.World ? EditorGizmoSpace.Local : EditorGizmoSpace.World);

    private void ToggleSnap()
    {
        _room.Settings.SnapEnabled = !_room.Settings.SnapEnabled;
        _snapButton.Checked = _room.Settings.SnapEnabled;
        MarkDirty();
    }

    /// <summary>
    /// Commits the grid-size box, but only when the number in it is actually different.
    /// </summary>
    /// <remarks>
    /// This runs on the box's Leave event, which fires the moment focus moves anywhere else — so
    /// marking the room dirty unconditionally meant that opening a room and clicking once left it
    /// "edited", and closing then asked whether to save a file nobody had touched. Compare first;
    /// an edit that changes nothing is not an edit.
    /// </remarks>
    private void ApplyGridSizeText()
    {
        if (float.TryParse(_gridSizeBox.Text, out float grid) && grid is >= 1f and <= 4096f)
        {
            if (MathF.Abs(_room.Settings.GridSize - grid) < 0.0001f)
            {
                return;
            }

            _room.Settings.GridSize = grid;
            MarkDirty();
        }
        else
        {
            _gridSizeBox.Text = _room.Settings.GridSize.ToString("0.#");
        }
    }

    private void FrameContent()
    {
        List<RoomNode> nodes = EnumerateVisibleNodes().Concat(EnumerateVisibleTerrainNodes()).ToList();
        if (_viewport.Mode2D)
        {
            _viewport.Camera2DX = _room.Settings.Width * 0.5f;
            _viewport.Camera2DY = _room.Settings.Height * 0.5f;
            float zw = _viewport.SurfaceWidth / MathF.Max(64f, _room.Settings.Width * 1.15f);
            float zh = _viewport.SurfaceHeight / MathF.Max(64f, _room.Settings.Height * 1.15f);
            _viewport.Zoom2D = Math.Clamp(MathF.Min(zw, zh), 0.05f, 4f);
            return;
        }

        if (nodes.Count == 0)
        {
            _viewport.Camera.Target = new Vector3(0f, 1f, 0f);
            _viewport.Camera.Distance = 30f;
            return;
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (RoomNode node in nodes)
        {
            (Vector3 nodeMin, Vector3 nodeMax) = NodeBounds3D(node);
            min = Vector3.Min(min, nodeMin);
            max = Vector3.Max(max, nodeMax);
        }

        Vector3 center = (min + max) * 0.5f;
        float radius = MathF.Max(2f, Vector3.Distance(min, max) * 0.5f);
        _viewport.Camera.Target = center;
        _viewport.Camera.Distance = radius * 2.6f;
        // Framing a large terrain can move the editor camera beyond its normal far plane.
        // Keep the complete framed bounds visible without changing authored game cameras.
        _viewport.FarPlane = MathF.Max(_viewport.FarPlane, _viewport.Camera.Distance + radius * 1.5f);
    }

    private void ApplyResponsiveLayout()
    {
        if (IsDisposed || _arrangingRoomPanels || _movingRoomPalette || _movingRoomInspector
            || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        bool narrow = ClientSize.Width < 1000;
        if (narrow && !_narrowLayout)
        {
            _inspectorPanelVisible = false;
            _inspectorToggle.Checked = false;
        }
        else if (!narrow && _narrowLayout)
        {
            _palettePanelVisible = true;
            _inspectorPanelVisible = true;
        }
        _narrowLayout = narrow;
        _arrangingRoomPanels = true;
        try
        {
            _mainSplit.Panel1MinSize = 0;
            _mainSplit.Panel2MinSize = 0;
            _centerSplit.Panel1MinSize = 0;
            _centerSplit.Panel2MinSize = 0;
            _mainSplit.Panel1Collapsed = !_palettePanelVisible;
            _centerSplit.Panel2Collapsed = !_inspectorPanelVisible;
            _mainSplit.IsSplitterFixed = _mainSplit.Panel1Collapsed;
            _centerSplit.IsSplitterFixed = _centerSplit.Panel2Collapsed;
            _mainSplit.SplitterWidth = 4;
            _centerSplit.SplitterWidth = 4;
            SetWideSplitters();
        }
        catch (InvalidOperationException) { }
        finally
        {
            _arrangingRoomPanels = false;
        }
    }

    private void SetWideSplitters()
    {
        int available = _mainSplit.ClientSize.Width;
        if (!_mainSplit.Panel1Collapsed && available > 400)
        {
            int desired = Math.Min(_preferredRoomPaletteWidth, narrowPaletteWidth());
            _mainSplit.SplitterDistance = Math.Clamp(desired, 280, Math.Max(280, available - 320));
        }
        int center = _centerSplit.ClientSize.Width;
        if (!_centerSplit.Panel2Collapsed && center > 500)
        {
            int desired = Math.Clamp(_preferredRoomInspectorWidth, 264, Math.Max(264, center - 320));
            _centerSplit.SplitterDistance = Math.Max(0, center - desired - _centerSplit.SplitterWidth);
        }
        int narrowPaletteWidth() => _narrowLayout ? 320 : 520;
    }

    private void SyncToolbar()
    {
        _dimensionToggle.Sync(!ViewMode3D);
        _selectToolButton.Checked = ActiveTool == RoomTool.Select && !TransformGizmoVisible;
        _placeToolButton.Checked = ActiveTool == RoomTool.Place;
        bool painting = ActiveTool == RoomTool.Paint || IsTilePainting;
        _paintTilesMenuItem.Checked = painting;
        _paintTilesMenuItem.Enabled = !ViewMode3D;
        _cameraToolButton.Checked = IsGameCameraPreview;
        _moveGizmoButton.Checked = TransformGizmoVisible && Gizmo == GizmoKind.Move;
        _rotateGizmoButton.Checked = TransformGizmoVisible && Gizmo == GizmoKind.Rotate;
        _scaleGizmoButton.Checked = TransformGizmoVisible && Gizmo == GizmoKind.Scale;
        _localGizmoButton.Checked = GizmoSpace == EditorGizmoSpace.Local;
        _snapButton.Checked = _room.Settings.SnapEnabled;

        _editorToolbar?.Sync(
            !ViewMode3D,
            ShowGrid,
            _room.Settings.SnapEnabled,
            ViewMode3D ? MetricGridSize : _room.Settings.GridSize,
            _roomPlayer?.IsRunning == true,
            _roomPlayer?.IsPaused == true);
        SyncRoomWorkspace();
        UpdateZoomStatus();
    }

    private void UpdateStatus(string? message)
    {
        string mode = ViewMode3D ? "3D" : "2D";
        string tool = ActiveTool switch
        {
            RoomTool.Place => "Place",
            RoomTool.Paint => "Paint tiles",
            _ when IsGameCameraPreview => "Camera preview",
            _ => TransformGizmoVisible ? Gizmo.ToString() : "Select",
        };
        string selection = _selection.Count switch
        {
            0 => "nothing selected",
            1 => $"'{_selected!.Name}'",
            _ => $"{_selection.Count} selected · primary '{_selected!.Name}'",
        };
        string hint = TransformGizmoVisible
            ? EditorTransformGizmo.StatusHint(CurrentGizmoMode(), GizmoSpace, _room.Settings.SnapEnabled)
            : "Drag to move · Ctrl/Shift to add to selection";
        _statusLabel.Text = message is null
            ? $"{mode}  ·  {tool}  ·  {selection}  ·  {_room.Nodes.Count} instance(s)  ·  {hint}"
            : $"{mode}  ·  {tool}  ·  {message}";
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged();
        _statusLabel.BackColor = EditorChrome.Surface;
        _statusLabel.ForeColor = EditorChrome.Muted;
        _paletteList.BackColor = EditorChrome.Surface;
        _paletteList.ForeColor = EditorChrome.Text;
        _layerList.BackColor = EditorChrome.Surface;
        _layerList.ForeColor = EditorChrome.Text;
        _outliner.BackColor = EditorChrome.Surface;
        _outliner.ForeColor = EditorChrome.Text;
        _kindInspector.BackColor = EditorChrome.Surface;
        _kindInspector.ViewBackColor = EditorChrome.Surface;
        _kindInspector.ViewForeColor = EditorChrome.Text;
        UpdatePaletteModeButtonStyles();
    }
}
