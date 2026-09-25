using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Primitives;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Preset/code shader authoring with target-aware live preview and timeline control.</summary>
public sealed partial class ShaderEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private readonly List<ShaderPresetDefinition> _presets;

    private ShaderAssetDocument _document;
    private readonly CodeEditor _code;
    private readonly TextBox _entryBox;
    private readonly ThemedComboBox _profileCombo;
    private readonly ThemedComboBox _targetTypeCombo;
    private readonly ThemedComboBox _targetAssetCombo;
    private readonly ThemedComboBox _terrainComponentCombo;
    private readonly Label _targetAssetLabel;
    private readonly Label _terrainComponentLabel;
    private readonly ToolStripButton _authorToggle;
    private readonly ToolStripButton _previewToggle;
    private readonly ToolStripButton _diagnosticsToggle;
    private readonly ToolStripLabel _compileIndicator;
    private readonly EditorCommandBar _toolbar;
    private readonly ListView _output;
    private readonly Label _statusLabel;
    private readonly FlowLayoutPanel _parameters;
    private readonly Label _presetDescription;
    private readonly ListBox _presetList;
    private readonly TextBox _presetSearch;
    private readonly Button _applyPresetButton;
    private readonly Button _renamePresetButton;
    private readonly Button _updatePresetButton;
    private readonly Button _deletePresetButton;
    private readonly Panel _presetSurface;
    private readonly Panel _codeSurface;
    private readonly SplitContainer _main;
    private readonly SplitContainer _workspace;
    private readonly Panel _diagnosticsPanel;
    private readonly EditorViewport3D _viewport;
    private readonly Label _previewContext;
    private readonly FlowLayoutPanel _previewSetup;
    private readonly ToolStrip _modeRail;
    private Button _playPauseButton = null!;
    private Label _frameStatus = null!;
    private TrackBar _speedSlider = null!;
    private readonly Dictionary<ShaderAuthoringMode, ToolStripButton> _modeButtons = [];
    private readonly System.Windows.Forms.Timer _liveCompileTimer;
    private readonly System.Windows.Forms.Timer _playbackTimer;
    private readonly List<string> _targetAssets = [];
    private readonly List<string> _imageAssets = [];
    private readonly Dictionary<string, TextureHandle> _resourceTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _terrainComponentIds = [];
    private readonly List<ShaderPresetDefinition> _visiblePresets = [];
    private readonly RuntimeModelRenderSystem _modelPreviewRenderer = new();

    private string? _loadedTerrainComponent;
    private TextureHandle _previewTexture;
    private IRenderController? _previewRenderer;
    private string? _loadedPreviewPath;
    private string? _loadedTerrainResource;
    private bool _pendingPreviewApply;
    private bool _previewApplied;
    private bool _assetRefreshPending;
    private bool _updatingUi = true;
    private bool _playing;
    private bool _resetPreviewTime;
    private bool _narrowLayout;
    private bool _widePreviewVisible = true;
    private bool _narrowPreviewActive;
    private bool _diagnosticsUserChoice;
    private bool _recordingDocumentEdit;
    private string? _pendingSourceSnapshot;
    private EditorFloorStyle _floorStyle = EditorFloorStyle.GridOnly;
    private bool _showGrid = true;
    private int _previewFrame;

    public ShaderEditorControl(string resourcePath, string projectRoot) : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        _document = File.Exists(ResourcePath)
            ? ShaderAssetDocument.ParseFlexible(File.ReadAllText(ResourcePath))
            : new ShaderAssetDocument();
        _presets = ShaderPresetLibrary.Load(ProjectRoot).ToList();
        bool newDocument = string.IsNullOrWhiteSpace(_document.Source);
        bool legacyDocument = _document.SchemaVersion < 3;
        if (newDocument)
        {
            _document.Source = _presets.First(preset => preset.TargetType == ShaderTargetType.Image).Source;
            _document.AuthoringMode = ShaderAuthoringMode.Preset;
            _document.TargetType = ShaderTargetType.Image;
            _document.Preset = "Image Rainbow";
        }
        else if (legacyDocument)
        {
            _document.AuthoringMode = ShaderAuthoringMode.Code;
            _document.TargetType = TargetForPipeline(_document.Pipeline);
            _document.Preset = string.Empty;
        }

        EnsureShaderPasses();
        _document.SchemaVersion = 6;
        if (string.IsNullOrWhiteSpace(_document.Profile)) _document.Profile = "ps_5_0";
        _document.Pipeline = ResolveTargetPipeline();
        if (!TrySynchronizeParameters(showDiagnostics: false))
            LoadWarning ??= "Shader parameters could not be synchronized with the current source.";

        _toolbar = EditorChrome.MakeToolbar();
        ToolStrip menuBar = new()
        {
            Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, AutoSize = false,
            Height = 30, BackColor = EditorChrome.Surface, Font = EditorChrome.BaseFont,
            Renderer = _toolbar.Renderer, Padding = new Padding(6, 0, 6, 0),
        };
        menuBar.Name = "ShaderMenus";
        menuBar.Items.Add(EditorDocumentMenuChrome.BuildFileMenu(this,
        [
            EditorDocumentMenuChrome.Item("Compile", "F7", "Compile this shader now", CompileNow),
        ]));
        menuBar.Items.Add(EditorDocumentMenuChrome.BuildEditMenu(this));
        _toolbar.Items.Add(new ToolStripLabel("SHADER EDITOR") { ForeColor = EditorChrome.Muted });
        ToolStripButton compileButton = EditorChrome.ToolButton(
            "Compile",
            "Compile this shader now (F7)",
            CompileNow);
        compileButton.Overflow = ToolStripItemOverflow.Never;
        _toolbar.Items.Add(compileButton);
        _autoCompile = EditorChrome.ToolButton("Auto-compile", "Compile source changes automatically", () => { if (_autoCompile?.Checked == true) QueueLiveCompile(); }, toggle: true);
        _autoCompile.Checked = true;
        _toolbar.Items.Add(_autoCompile);

        _authorToggle = EditorChrome.ToolButton(
            "Author",
            "Show preset or code authoring (Ctrl+1)",
            ShowNarrowAuthoring,
            toggle: true);
        _authorToggle.AccessibleName = "Show shader authoring";
        _authorToggle.Checked = true;
        _authorToggle.Visible = false;
        _toolbar.Items.Add(_authorToggle);

        _previewToggle = EditorChrome.ToolButton(
            "Preview",
            "Return to the visual shader workspace",
            TogglePreview,
            toggle: true);
        _previewToggle.Checked = true;
        _toolbar.Items.Add(_previewToggle);

        _diagnosticsToggle = EditorChrome.ToolButton(
            "Diagnostics",
            "Show or hide compiler diagnostics",
            ToggleDiagnostics,
            toggle: true);
        _diagnosticsToggle.Checked = false;
        _toolbar.Items.Add(_diagnosticsToggle);
        _toolbar.Items.Add(new ToolStripSeparator());

        _compileIndicator = new ToolStripLabel("Preparing…")
        {
            ForeColor = EditorChrome.Muted,
            ToolTipText = "Shader compile status",
            AccessibleName = "Shader compile status",
        };
        _toolbar.Items.Add(_compileIndicator);

        _targetTypeCombo = new ThemedComboBox { Name = "ShaderTargetTypePicker", Width = 118 };
        _targetTypeCombo.Items.AddRange(ShaderTargetTypesForUi());
        EditorChrome.StyleField(_targetTypeCombo);

        _targetAssetLabel = SetupLabel("Target");
        _targetAssetCombo = new ThemedComboBox { Name = "ShaderTargetAssetPicker", Width = 210 };
        EditorChrome.StyleField(_targetAssetCombo);

        _terrainComponentLabel = SetupLabel("Component");
        _terrainComponentCombo = new ThemedComboBox { Name = "ShaderTerrainComponentPicker", Width = 176 };
        EditorChrome.StyleField(_terrainComponentCombo);

        _entryBox = new TextBox { Text = _document.Entry, Width = 96 };
        EditorChrome.StyleField(_entryBox);
        _profileCombo = new ThemedComboBox
        {
            Name = "ShaderProfilePicker",
            Width = 100,
        };
        _profileCombo.Items.Add("ps_5_0");
        if (!_profileCombo.Items.Contains(_document.Profile))
            _profileCombo.Items.Add(_document.Profile);
        _profileCombo.SelectedItem = _document.Profile;
        EditorChrome.StyleField(_profileCombo);

        _code = new CodeEditor { Dock = DockStyle.Fill };
        _code.SetRules(BuildHlslRules());
        _code.CodeText = _document.Source;
        _codeSurface = new Panel { Name = "ShaderCodeSurface", BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill };
        _codeSurface.Controls.Add(_code);
        FlowLayoutPanel codeSettings = new()
        {
            AutoSize = false,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 45,
            Name = "ShaderCodeSettings",
            Padding = EditorChrome.PanelInsets,
            WrapContents = false,
        };
        codeSettings.Controls.Add(SetupLabel("Entry"));
        codeSettings.Controls.Add(_entryBox);
        codeSettings.Controls.Add(SetupLabel("Profile"));
        codeSettings.Controls.Add(_profileCombo);
        _codeSurface.Controls.Add(codeSettings);
        _code.BringToFront();

        _presetList = new ListBox
        {
            Name = "ShaderPresetList",
            BackColor = EditorChrome.Raised,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ForeColor = EditorChrome.Text,
            IntegralHeight = false,
            ItemHeight = 24,
        };
        EditorChrome.StyleField(_presetList);
        _presetList.DrawItem += DrawPresetListItem;
        _presetSearch = new TextBox
        {
            Dock = DockStyle.Top,
            Height = 30,
            PlaceholderText = "Filter presets",
        };
        EditorChrome.StyleField(_presetSearch);
        _applyPresetButton = PresetButton("Apply", ApplySelectedPreset);
        Button savePresetButton = PresetButton("Save As", SavePresetFromDialog);
        _renamePresetButton = PresetButton("Rename", RenameSelectedPresetFromDialog);
        _updatePresetButton = PresetButton("Update", () => UpdateSelectedPreset());
        _deletePresetButton = PresetButton("Delete", () => DeleteSelectedPreset());
        FlowLayoutPanel presetActions = new()
        {
            BackColor = EditorChrome.Surface, Dock = DockStyle.Bottom, Height = 40,
            Padding = new Padding(0, 4, 0, 0), WrapContents = false,
        };
        _applyPresetButton.Text = "Use preset";
        presetActions.Controls.Add(_applyPresetButton);
        Button presetMore = PresetButton("More…", () => ShowPresetActions(savePresetButton));
        presetActions.Controls.Add(presetMore);
        // Project actions remain available through a single menu rather than five permanent buttons.
        _presetLibrary = new Panel { Dock = DockStyle.Top, Height = 230, Padding = new Padding(12, 0, 12, 8), BackColor = EditorChrome.Surface };
        _presetLibrary.Controls.Add(_presetList);
        _presetLibrary.Controls.Add(presetActions);
        _presetLibrary.Controls.Add(_presetSearch);
        _presetList.ItemHeight = 28;
        _presetList.BringToFront();

        _parameters = new FlowLayoutPanel
        {
            AutoScroll = true, BackColor = EditorChrome.Surface, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown, Padding = new Padding(12, 8, 12, 8), WrapContents = false,
            Name = "ShaderControls",
        };
        _presetDescription = new Label
        {
            AutoEllipsis = true, Dock = DockStyle.Bottom, ForeColor = EditorChrome.Muted,
            Height = 44, Padding = new Padding(12, 6, 12, 4),
        };
        RefreshPresetList(_document.Preset);
        _propertiesPanel = new Panel { BackColor = EditorChrome.Surface, Dock = DockStyle.Fill, Name = "ShaderProperties" };
        _propertiesPanel.Controls.Add(_parameters);
        _propertiesPanel.Controls.Add(_presetLibrary);
        _propertiesPanel.Controls.Add(_presetDescription);
        _presetHeader = new Button { Name = "ShaderPresetsToggle", Dock = DockStyle.Top, Height = 36, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat };
        EditorChrome.StyleField(_presetHeader);
        _presetHeader.Click += (_, _) => { _presetsExpanded = !_presetsExpanded; ApplyResponsiveLayout(); };
        _propertiesPanel.Controls.Add(_presetHeader);
        _propertiesPanel.Controls.Add(EditorChrome.SectionLabel("Shader controls"));
        _parameters.BringToFront();

        _presetSurface = new Panel { Dock = DockStyle.Fill };
        Panel authoringHost = new() { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill };
        authoringHost.Controls.Add(_codeSurface);
        _modeRail = EditorChrome.MakeModeRail(EditorChrome.ModeRailWidth);
        _modeRail.Visible = false;
        foreach (ShaderAuthoringMode mode in Enum.GetValues<ShaderAuthoringMode>())
        {
            ShaderAuthoringMode captured = mode;
            ToolStripButton button = EditorChrome.ToolButton(mode == ShaderAuthoringMode.Preset ? "Visual" : "Edit code", "Switch shader workspace", () => SetAuthoringMode(captured), toggle: true);
            _modeButtons.Add(mode, button);
            _toolbar.Items.Insert(1 + (int)mode, button);
        }

        _viewport = new EditorViewport3D { Mode2D = _document.Pipeline != ShaderAssetPipeline.Mesh };
        _viewport.Camera.Distance = 6f;
        _viewport.Camera.Target = new Vector3(0f, 0.5f, 0f);
        _viewport.Camera.Yaw = MathF.PI + .45f;
        _viewport.Camera.Pitch = -.3f;
        _viewport.FloorStyle = _floorStyle;
        _viewport.SceneStateFactory = CreateShaderSceneState;
        _viewport.DrawScene2D += DrawPreview2D;
        _viewport.DrawScene += DrawPreview3D;
        _viewport.DrawOverlay += DrawShaderViewportOverlay;
        _viewport.SelectionWorldPoint = () =>
            _document.Pipeline == ShaderAssetPipeline.Mesh ? new Vector3(0f, 0.5f, 0f) : null;
        EditorCommandBar viewportToolbar = EditorChrome.MakeToolbar();
        viewportToolbar.Name = "ShaderViewportToolbar";
        _dimensionLabel = new ToolStripLabel { ForeColor = EditorChrome.Muted };
        viewportToolbar.Items.Add(_dimensionLabel);
        viewportToolbar.Items.Add(EditorChrome.ToolButton("Frame", "Fit the current target in the preview", FramePreviewTarget));
        EditorViewportChrome.Attach(
            viewportToolbar,
            new EditorViewportChrome.Options
            {
                Viewport = _viewport,
                ViewTooltip = "Grid and reference-floor options for the shader preview",
                Grid = new EditorViewMenuChrome.GridBinding
                {
                    Read = () => _showGrid,
                    Write = value => _showGrid = value,
                    Invalidate = () => _viewport.Host.Invalidate(),
                },
                FloorStyle = new EditorViewMenuChrome.FloorStyleBinding
                {
                    Read = () => _floorStyle,
                    Write = SetFloorStyle,
                    Invalidate = () => _viewport.Host.Invalidate(),
                },
                GizmoTooltip = "Transform gizmo for the shader preview target",
                ReadGizmoMode = () => EditorGizmoMode.Move,
                WriteGizmoMode = _ => { },
                ReadGizmoSpace = () => EditorGizmoSpace.World,
                WriteGizmoSpace = _ => { },
                Invalidate = () => _viewport.Invalidate(true),
                IncludeGizmo = false,
                IncludeRotateGizmo = false,
                IncludeScaleGizmo = false,
            });

        _previewContext = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Text,
            Height = 28,
            Margin = new Padding(0, 2, 18, 0),
            Text = "Preview",
            TextAlign = ContentAlignment.MiddleLeft,
            Width = 196,
        };
        _previewSetup = new FlowLayoutPanel
        {
            AutoSize = false,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 45,
            Name = "ShaderPreviewSetup",
            Padding = EditorChrome.PanelInsets,
            WrapContents = true,
        };
        _previewSetup.AutoSize = true;
        _previewSetup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _previewSetup.MinimumSize = new Size(0, 48);
        _previewSetup.Padding = new Padding(12, 7, 12, 7);
        _targetTypeCombo.Width = 136;
        _targetAssetCombo.Width = 248;
        _terrainComponentCombo.Width = 240;
        _targetAssetCombo.DropDownWidth = 360;
        _terrainComponentCombo.DropDownWidth = 360;
        _previewSetup.Controls.Add(TargetField(SetupLabel("Type"), _targetTypeCombo));
        _targetAssetGroup = TargetField(_targetAssetLabel, _targetAssetCombo);
        _componentGroup = TargetField(_terrainComponentLabel, _terrainComponentCombo);
        _previewSetup.Controls.Add(_targetAssetGroup);
        _previewSetup.Controls.Add(_componentGroup);
        Control frameBar = BuildFrameBar();
        Panel previewPanel = new() { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill };
        previewPanel.Controls.Add(_viewport);
        previewPanel.Controls.Add(frameBar);
        previewPanel.Controls.Add(viewportToolbar);
        _viewport.BringToFront();

        _previewAndControls = new SplitContainer
        {
            BackColor = EditorChrome.Border, Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2,
            Size = new Size(1200, 560), Panel1MinSize = 0, Panel2MinSize = 0,
            SplitterDistance = 868, SplitterWidth = EditorChrome.SplitterWidth,
        };
        _previewAndControls.Panel1.Controls.Add(previewPanel);
        _previewAndControls.Panel2.Controls.Add(_propertiesPanel);
        _main = new SplitContainer
        {
            BackColor = EditorChrome.Border, Dock = DockStyle.Fill, Size = new Size(1200, 560),
            Panel1MinSize = 0, Panel2MinSize = 0, SplitterDistance = 450,
            SplitterWidth = EditorChrome.SplitterWidth,
        };
        _main.Panel1.Controls.Add(authoringHost);
        _main.Panel2.Controls.Add(_previewAndControls);
        _controlsToggle = EditorChrome.ToolButton("Controls", "Show shader parameters and presets", () =>
        {
            if (_document.AuthoringMode == ShaderAuthoringMode.Code) _codeControlsVisible = _controlsToggle?.Checked == true;
            else _controlsVisible = _controlsToggle?.Checked == true;
            ApplyResponsiveLayout();
        }, toggle: true);
        _controlsToggle.Checked = true;
        _toolbar.Items.Add(_controlsToggle);

        _diagnosticsPanel = new Panel
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 70),
            Name = "ShaderDiagnosticsPanel",
        };
        _output = new ListView
        {
            BackColor = EditorChrome.Surface,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Text,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            View = View.Details,
        };
        _output.Columns.Add("Output", 1100);
        _output.ClientSizeChanged += (_, _) =>
        {
            ResizeDiagnosticsColumn();
        };
        _output.ItemActivate += (_, _) => NavigateToSelectedDiagnostic();
        _diagnosticsPanel.Controls.Add(_output);
        _diagnosticsPanel.Controls.Add(EditorChrome.SectionLabel("Compiler Output"));
        _workspace = new SplitContainer
        {
            BackColor = EditorChrome.Border,
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            Orientation = Orientation.Horizontal,
            // A collapsed diagnostics panel must not impose its minimum height on the
            // workspace. WinForms otherwise leaves both panels at zero size in short docks.
            Panel1MinSize = 0,
            Panel2MinSize = 0,
            Panel2Collapsed = true,
            Size = new Size(1200, 650),
            SplitterDistance = 540,
            SplitterWidth = EditorChrome.SplitterWidth,
        };
        _workspace.Panel1.Controls.Add(_main);
        _workspace.Panel2.Controls.Add(_diagnosticsPanel);
        _statusLabel = EditorChrome.MakeStatusBar();
        _statusLabel.Name = "ShaderStatus";

        Controls.Add(_workspace);
        Controls.Add(_previewSetup);
        Controls.Add(_toolbar);
        Controls.Add(menuBar);
        Controls.Add(_statusLabel);
        _workspace.BringToFront();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        HandleCreated += (_, _) => BeginInvoke(ApplyResponsiveLayout);

        _liveCompileTimer = new System.Windows.Forms.Timer { Interval = 450 };
        _liveCompileTimer.Tick += (_, _) =>
        {
            _liveCompileTimer.Stop();
            CompileNow();
        };
        _playbackTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _playbackTimer.Tick += (_, _) => AdvancePlayback();

        _code.TextChangedByUser += (_, _) =>
        {
            if (_updatingUi) return;
            _pendingSourceSnapshot ??= CaptureDocument();
            _document.Source = _code.CodeText;
            SyncActiveShaderPass();
            _document.Preset = string.Empty;
            MarkDirty();
            QueueLiveCompile();
        };
        _entryBox.TextChanged += (_, _) =>
        {
            if (_updatingUi) return;
            RecordDocumentEdit("Change shader entry point", () =>
            {
                _document.Entry = _entryBox.Text;
                SyncActiveShaderPass();
                MarkDirty();
                QueueLiveCompile();
            });
        };
        _profileCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi) return;
            RecordDocumentEdit("Change shader profile", () =>
            {
                _document.Profile = _profileCombo.SelectedItem?.ToString() ?? "ps_5_0";
                MarkDirty();
                QueueLiveCompile();
            });
        };
        _targetTypeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi
                || !Enum.TryParse(_targetTypeCombo.SelectedItem?.ToString(), out ShaderTargetType target))
            {
                return;
            }

            RecordDocumentEdit(
                $"Target shader at {target}",
                () => ChangeTargetType(target));
        };
        _targetAssetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi) return;
            RecordDocumentEdit("Change shader preview asset", () =>
            {
                int index = _targetAssetCombo.SelectedIndex - 1;
                _document.PreviewAsset = index >= 0 && index < _targetAssets.Count
                    ? _targetAssets[index]
                    : string.Empty;
                InvalidatePreviewTarget();
                PopulateTerrainComponents();
                UpdatePreviewContext();
                MarkDirty();
            });
        };
        _terrainComponentCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi) return;
            RecordDocumentEdit("Change shader target component", () =>
            {
                int index = _terrainComponentCombo.SelectedIndex;
                _document.TargetComponent = index >= 0 && index < _terrainComponentIds.Count
                    ? _terrainComponentIds[index]
                    : string.Empty;
                InvalidatePreviewTarget();
                UpdatePreviewContext();
                MarkDirty();
            });
        };
        _presetList.SelectedIndexChanged += (_, _) =>
        {
            UpdatePresetSelection();
        };
        _presetSearch.TextChanged += (_, _) =>
        {
            if (_updatingUi) return;
            RefreshPresetList();
        };
        _parameters.ClientSizeChanged += (_, _) => ResizeParameterCards();

        _targetTypeCombo.SelectedItem = _document.TargetType.ToString();
        PopulateTargetAssets();
        PopulateTerrainComponents();
        SelectPresetInList(_document.Preset);
        SetAuthoringMode(_document.AuthoringMode, markDirty: false);
        UpdatePreviewContext();
        BuildReferenceShaderWorkspace();
        _updatingUi = false;
        _playbackTimer.Start();
        CompileNow();
        ApplyResponsiveLayout();
    }

    public bool LastCompileSucceeded { get; private set; }

    public event EventHandler? InspectorStateChanged;

    public bool LivePreviewApplied => _previewApplied;

    public int ReflectedParameterCount => _document.Parameters.Count;

    public int ReflectedResourceCount => _document.Resources.Count;

    public string ActiveVariant => _document.ActiveVariant;

    public IReadOnlyList<string> VariantNames =>
        _document.Variants.Select(variant => variant.Name).ToArray();

    public ShaderAssetPipeline Pipeline => _document.Pipeline;

    public ShaderAuthoringMode AuthoringMode => _document.AuthoringMode;

    public ShaderTargetType TargetType => _document.TargetType;

    public string TargetComponent => _document.TargetComponent;

    public string PreviewAsset => _document.PreviewAsset;

    public string SourceText => _code.CodeText;

    public string EntryPoint => _document.Entry;

    public string CompilerProfile => _document.Profile;

    public IReadOnlyList<float> ParameterValue(string name) =>
        _document.Parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))?.Value.ToArray()
        ?? [];

    public bool PreviewVisible => !_main.Panel2Collapsed;

    public bool DiagnosticsVisible => _referenceShaderLayout
        ? _referenceDiagnosticsHost?.Visible == true
        : !_workspace.Panel2Collapsed;

    public bool IsNarrowLayout => _narrowLayout;

    public bool IsNarrowPreviewActive => _narrowLayout && _narrowPreviewActive;

    public bool IsPreviewPlaying => _playing;

    public bool SupportsAuthoredShaderPreview => _previewRenderer is not null
        && !string.Equals(_previewRenderer.BackendName, "Software", StringComparison.Ordinal);

    public int PreviewSpeed => _speedSlider.Value;

    public IReadOnlyList<string> PresetNames => _presets.Select(preset => preset.Name).ToArray();

    public IReadOnlyList<string> TerrainComponents => _terrainComponentIds;

    public EditorViewport3D Viewport => _viewport;

    public EditorCommandBar CommandBar => _toolbar;

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues()
    {
        List<ResourceInspectorLiveValue> values =
        [
            new(
                "Authoring",
                "AuthoringMode",
                "Authoring mode",
                _document.AuthoringMode.ToString(),
                Description: "Preset keeps exposed controls visible; Code opens the HLSL source.",
                Choices: Enum.GetNames<ShaderAuthoringMode>()),
            new(
                "Preview target",
                "TargetType",
                "Target type",
                _document.TargetType.ToString(),
                Description: "Selects the renderer pipeline and compatible preview assets.",
                Choices: Enum.GetNames<ShaderTargetType>()),
            new(
                "Preview target",
                "Pipeline",
                "Pipeline",
                _document.Pipeline.ToString(),
                ReadOnly: true,
                Description: "Derived from the selected target type."),
            new(
                "Authoring",
                "Entry",
                "Entry point",
                _document.Entry,
                Description: "Pixel shader function compiled by Genesis."),
            new(
                "Authoring",
                "Profile",
                "Compiler profile",
                _document.Profile,
                Choices: ["ps_5_0"]),
            new(
                "Authoring",
                "Preset",
                "Applied preset",
                _document.Preset,
                Description: "Applies a built-in or project shader preset.",
                Choices: _presets.Select(preset => preset.Name).ToArray()),
            new(
                "Compile status",
                "Compile.Succeeded",
                "Last compile succeeded",
                LastCompileSucceeded,
                ReadOnly: true),
            new(
                "Compile status",
                "Compile.PreviewState",
                "Preview state",
                LastCompileSucceeded && _previewApplied
                    ? "Current"
                    : _previewApplied
                        ? "Last successful (stale)"
                        : "Not applied",
                ReadOnly: true),
            new(
                "Compile status",
                "Compile.Diagnostics",
                "Diagnostics",
                _output.Items.Cast<ListViewItem>().Count(item =>
                    item.Text.StartsWith("✕", StringComparison.Ordinal)),
                ReadOnly: true),
        ];

        if (_document.TargetType != ShaderTargetType.Fullscreen)
        {
            values.Add(new ResourceInspectorLiveValue(
                "Resources",
                "PreviewAsset",
                "Preview asset",
                _document.PreviewAsset,
                Description: "Project asset used by the embedded live preview.",
                Choices: [string.Empty, .. _targetAssets],
                AssetKind: ResourceKindForTarget(_document.TargetType)));
        }

        if (_document.TargetType == ShaderTargetType.Terrain)
        {
            values.Add(new ResourceInspectorLiveValue(
                "Preview target",
                "TargetComponent",
                "Terrain component",
                _document.TargetComponent,
                Description: "Terrain component id shared with the Terrain Editor (layer:, water:, path:, point:, entity:, nature:vegetation).",
                Choices: _terrainComponentIds.ToArray()));
        }

        foreach (ShaderParameterValue parameter in _document.Parameters)
        {
            ShaderParameterDescriptor descriptor = ShaderParameterMetadata.Describe(
                parameter.Name,
                parameter.Type);
            string[] componentNames = ["X", "Y", "Z", "W"];
            for (int index = 0; index < parameter.Value.Length; index++)
            {
                string suffix = parameter.Value.Length == 1 ? string.Empty : "." + componentNames[index];
                string label = parameter.Value.Length == 1
                    ? HumanizeParameter(parameter.Name)
                    : $"{HumanizeParameter(parameter.Name)} {componentNames[index]}";
                values.Add(new ResourceInspectorLiveValue(
                    "Parameters",
                    "Parameters." + parameter.Name + suffix,
                    label,
                    InspectorParameterValue(parameter, index),
                    Description: $"{parameter.Type} from GenesisParameters : register(b5).",
                    Minimum: (decimal)descriptor.Minimum,
                    Maximum: (decimal)descriptor.Maximum,
                    Increment: (decimal)descriptor.Increment,
                    DecimalPlaces: descriptor.DecimalPlaces));
            }
        }

        values.Add(new ResourceInspectorLiveValue(
            "Variants",
            "ActiveVariant",
            "Active variant",
            _document.ActiveVariant,
            Description: "Named compile permutation. Empty uses the base source without extra keywords.",
            Choices: ["", .. _document.Variants.Select(variant => variant.Name)]));

        foreach (ShaderResourceBinding resource in _document.ResolveResources())
        {
            bool pipelineOwned = ShaderResourceReflection.IsPipelineOwned(_document.Pipeline, resource);
            if (resource.Kind == ShaderResourceKind.SamplerState)
            {
                values.Add(new ResourceInspectorLiveValue(
                    "Resources",
                    "Resources." + resource.Name,
                    HumanizeParameter(resource.Name),
                    string.IsNullOrWhiteSpace(resource.Binding) ? "Linear" : resource.Binding,
                    ReadOnly: pipelineOwned,
                    Description: $"SamplerState : register(s{resource.Slot}).",
                    Choices: ["Linear", "Point"]));
                continue;
            }

            values.Add(new ResourceInspectorLiveValue(
                "Resources",
                "Resources." + resource.Name,
                HumanizeParameter(resource.Name),
                resource.Binding,
                ReadOnly: pipelineOwned,
                Description: pipelineOwned
                    ? $"{resource.Kind} : register(t{resource.Slot}) is bound by the preview/object Image."
                    : $"{resource.Kind} : register(t{resource.Slot}).",
                Choices: pipelineOwned ? null : ["", .. _imageAssets],
                AssetKind: pipelineOwned ? null : ResourceKind.Image));
        }

        return values;
    }

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) =>
        TryApplyInspectorValue(propertyPath, value);

    public void SetSource(string source)
    {
        source ??= string.Empty;
        if (string.Equals(_code.CodeText, source, StringComparison.Ordinal)) return;
        RecordDocumentEdit("Edit shader source", () =>
        {
            bool wasUpdating = _updatingUi;
            _updatingUi = true;
            _code.CodeText = source;
            _document.Source = _code.CodeText;
            _document.Preset = string.Empty;
            _updatingUi = wasUpdating;
            MarkDirty();
            QueueLiveCompile();
        });
    }

    public void SetPipeline(ShaderAssetPipeline pipeline)
    {
        if (_document.Pipeline == pipeline) return;
        ShaderTargetType target = pipeline switch
        {
            ShaderAssetPipeline.Mesh when _document.TargetType == ShaderTargetType.Terrain => ShaderTargetType.Terrain,
            ShaderAssetPipeline.Mesh => ShaderTargetType.Model,
            ShaderAssetPipeline.Fullscreen => ShaderTargetType.Fullscreen,
            ShaderAssetPipeline.Sprite when _document.TargetType == ShaderTargetType.Particle => ShaderTargetType.Particle,
            _ => ShaderTargetType.Image,
        };
        RecordDocumentEdit(
            $"Change shader pipeline to {pipeline}",
            () => ChangeTargetType(target));
    }

    public void SetTargetType(ShaderTargetType targetType)
    {
        if (_document.TargetType == targetType) return;
        RecordDocumentEdit(
            $"Target shader at {targetType}",
            () => ChangeTargetType(targetType));
    }

    public void SetAuthoringMode(ShaderAuthoringMode mode)
    {
        if (_document.AuthoringMode == mode) return;
        RecordDocumentEdit(
            $"Switch to {mode} authoring",
            () => SetAuthoringMode(mode, markDirty: true));
    }

    public bool SelectPreset(string name)
    {
        ShaderPresetDefinition? preset = _presets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return false;
        SelectPresetInList(preset.Name);
        RecordDocumentEdit($"Apply shader preset '{preset.Name}'", () => ApplyPreset(preset));
        return true;
    }

    public string SelectedPresetName => SelectedPreset()?.Name ?? string.Empty;

    public bool SelectedPresetIsBuiltIn => SelectedPreset()?.IsBuiltIn == true;

    public bool SavePresetAs(string name, string? description = null)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0
            || _presets.Any(preset =>
                string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _statusLabel.Text = name.Length == 0
                ? "Preset name is required."
                : $"A preset named '{name}' already exists.";
            return false;
        }

        ShaderPresetDefinition saved = ShaderPresetLibrary.Save(
            ProjectRoot,
            name,
            description ?? $"Project preset for {_document.TargetType}.",
            _document.TargetType,
            _code.CodeText,
            entryPoint: _document.Entry,
            profile: _document.Profile,
            parameters: _document.Parameters);
        _presets.Add(saved);
        RecordDocumentEdit($"Associate shader preset '{saved.Name}'", () =>
        {
            _document.Preset = saved.Name;
            _document.AuthoringMode = ShaderAuthoringMode.Preset;
            SetAuthoringMode(ShaderAuthoringMode.Preset, markDirty: true);
        });
        RefreshPresetList(saved.Name);
        _statusLabel.Text = $"Saved project preset · {saved.Name}";
        return true;
    }

    public bool UpdateSelectedPreset()
    {
        ShaderPresetDefinition? preset = SelectedPreset();
        if (preset is null || preset.IsBuiltIn)
        {
            _statusLabel.Text = "Built-in presets are read-only. Use Save As to create a project preset.";
            return false;
        }

        ShaderPresetDefinition updated = ShaderPresetLibrary.Save(
            ProjectRoot,
            preset.Name,
            preset.Description,
            _document.TargetType,
            _code.CodeText,
            existingPath: preset.FilePath,
            entryPoint: _document.Entry,
            profile: _document.Profile,
            parameters: _document.Parameters);
        int index = _presets.IndexOf(preset);
        _presets[index] = updated;
        RefreshPresetList(updated.Name);
        _statusLabel.Text = $"Updated project preset · {updated.Name}";
        return true;
    }

    public bool RenameSelectedPreset(string name)
    {
        ShaderPresetDefinition? preset = SelectedPreset();
        name = name?.Trim() ?? string.Empty;
        if (preset is null || preset.IsBuiltIn || name.Length == 0)
        {
            _statusLabel.Text = "Select a project preset and enter a new name.";
            return false;
        }
        if (_presets.Any(candidate =>
                !ReferenceEquals(candidate, preset)
                && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _statusLabel.Text = $"A preset named '{name}' already exists.";
            return false;
        }
        if (string.Equals(preset.Name, name, StringComparison.Ordinal)) return true;

        try
        {
            ShaderPresetDefinition renamed = ShaderPresetLibrary.Rename(ProjectRoot, preset, name);
            int index = _presets.IndexOf(preset);
            _presets[index] = renamed;
            if (string.Equals(_document.Preset, preset.Name, StringComparison.OrdinalIgnoreCase))
            {
                _document.Preset = renamed.Name;
                MarkDirty();
            }
            RefreshPresetList(renamed.Name);
            _statusLabel.Text = $"Renamed project preset · {renamed.Name}";
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _statusLabel.Text = exception.Message;
            return false;
        }
    }

    public bool DeleteSelectedPreset()
    {
        ShaderPresetDefinition? preset = SelectedPreset();
        if (preset is null || preset.IsBuiltIn)
        {
            _statusLabel.Text = "Built-in presets cannot be deleted.";
            return false;
        }

        bool wasAssociated = string.Equals(_document.Preset, preset.Name, StringComparison.OrdinalIgnoreCase);
        string documentBefore = CaptureDocument();

        void ApplyDelete()
        {
            if (!ShaderPresetLibrary.Delete(ProjectRoot, preset))
                throw new InvalidOperationException("The selected project shader preset could not be deleted.");
            _presets.Remove(preset);
            if (wasAssociated)
            {
                _document.Preset = string.Empty;
                SetAuthoringMode(ShaderAuthoringMode.Code, markDirty: true);
            }

            RefreshPresetList();
            _statusLabel.Text = $"Deleted project preset · {preset.Name}";
        }

        try
        {
            ApplyDelete();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _statusLabel.Text = exception.Message;
            return false;
        }

        PushEdit(
            $"Delete shader preset '{preset.Name}'",
            apply: ApplyDelete,
            revert: () =>
            {
                ShaderPresetLibrary.Restore(ProjectRoot, preset);
                if (!_presets.Any(candidate =>
                        string.Equals(candidate.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _presets.Add(preset);
                }

                RestoreDocument(documentBefore);
                RefreshPresetList(_document.Preset);
                _statusLabel.Text = $"Restored project preset · {preset.Name}";
            });
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SetPreviewVisible(bool visible)
    {
        _widePreviewVisible = visible;
        if (!visible)
            SetAuthoringMode(ShaderAuthoringMode.Code, markDirty: false);
        ApplyResponsiveLayout();
    }

    public bool SetParameterValue(string name, params float[] values)
    {
        ShaderParameterValue? parameter = _document.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (parameter is null) return false;
        float[] next = values
            .Take(parameter.Value.Length)
            .Concat(Enumerable.Repeat(0f, parameter.Value.Length))
            .Take(parameter.Value.Length)
            .ToArray();
        if (parameter.Value.SequenceEqual(next)) return true;
        RecordDocumentEdit($"Set shader parameter '{parameter.Name}'", () =>
        {
            parameter.Value = next;
            RebuildParameterControls();
            _viewport.Invalidate(true);
            MarkDirty();
        });
        return true;
    }

    public bool AddVariant(string name, params string[] keywords)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length == 0
            || _document.Variants.Any(variant =>
                string.Equals(variant.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        RecordDocumentEdit($"Add shader variant '{name}'", () =>
        {
            _document.Variants.Add(new ShaderVariant
            {
                Name = name,
                Keywords = keywords?.Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                    .Select(keyword => keyword.Trim())
                    .ToList() ?? [],
            });
            _document.ActiveVariant = name;
            MarkDirty();
            QueueLiveCompile();
        });
        return true;
    }

    public bool SelectVariant(string? name)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length > 0 && _document.FindVariant(name) is null) return false;
        if (string.Equals(_document.ActiveVariant, name, StringComparison.OrdinalIgnoreCase)) return true;
        RecordDocumentEdit(
            name.Length == 0 ? "Clear shader variant" : $"Select shader variant '{name}'",
            () =>
            {
                _document.ActiveVariant = name;
                MarkDirty();
                QueueLiveCompile();
            });
        return true;
    }

    public bool RemoveVariant(string name)
    {
        ShaderVariant? variant = _document.FindVariant(name);
        if (variant is null) return false;
        RecordDocumentEdit($"Remove shader variant '{variant.Name}'", () =>
        {
            _document.Variants.Remove(variant);
            if (string.Equals(_document.ActiveVariant, variant.Name, StringComparison.OrdinalIgnoreCase))
                _document.ActiveVariant = string.Empty;
            MarkDirty();
            QueueLiveCompile();
        });
        return true;
    }

    public bool SetResourceBinding(string name, string? binding)
    {
        ShaderResourceBinding? resource = _document.Resources.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (resource is null) return false;
        if (ShaderResourceReflection.IsPipelineOwned(_document.Pipeline, resource)) return false;
        string next = (binding ?? string.Empty).Replace('\\', '/').Trim();
        if (resource.Kind == ShaderResourceKind.SamplerState)
        {
            if (next.Length == 0) next = "Linear";
            if (!next.Equals("Linear", StringComparison.OrdinalIgnoreCase)
                && !next.Equals("Point", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (next.Length > 0
            && !_imageAssets.Contains(next, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(resource.Binding, next, StringComparison.OrdinalIgnoreCase)) return true;
        RecordDocumentEdit($"Bind shader resource '{resource.Name}'", () =>
        {
            resource.Binding = next;
            bool wasUpdating = _updatingUi;
            _updatingUi = true;
            try
            {
                foreach (Control card in _parameters.Controls)
                foreach (ThemedComboBox picker in card.Controls.OfType<ThemedComboBox>())
                    if (string.Equals(picker.Tag as string, resource.Name, StringComparison.OrdinalIgnoreCase))
                        picker.SelectedIndex = _imageAssets.FindIndex(asset =>
                            string.Equals(asset, next, StringComparison.OrdinalIgnoreCase)) + 1;
            }
            finally { _updatingUi = wasUpdating; }
            InvalidatePreviewTarget();
            MarkDirty();
        });
        return true;
    }

    public string ResourceBinding(string name) =>
        _document.Resources.FirstOrDefault(resource =>
            string.Equals(resource.Name, name, StringComparison.OrdinalIgnoreCase))?.Binding
        ?? string.Empty;

    private void SetEntryPoint(string entry)
    {
        entry = string.IsNullOrWhiteSpace(entry) ? "MainPS" : entry.Trim();
        if (string.Equals(_document.Entry, entry, StringComparison.Ordinal)) return;
        RecordDocumentEdit("Change shader entry point", () =>
        {
            bool wasUpdating = _updatingUi;
            _updatingUi = true;
            _entryBox.Text = entry;
            _document.Entry = entry;
            _updatingUi = wasUpdating;
            MarkDirty();
            QueueLiveCompile();
        });
    }

    private void SetCompilerProfile(string profile)
    {
        profile = string.IsNullOrWhiteSpace(profile) ? "ps_5_0" : profile.Trim();
        if (string.Equals(_document.Profile, profile, StringComparison.OrdinalIgnoreCase)) return;
        RecordDocumentEdit("Change shader profile", () =>
        {
            bool wasUpdating = _updatingUi;
            _updatingUi = true;
            if (!_profileCombo.Items.Contains(profile)) _profileCombo.Items.Add(profile);
            _profileCombo.SelectedItem = profile;
            _document.Profile = profile;
            _updatingUi = wasUpdating;
            MarkDirty();
            QueueLiveCompile();
        });
    }

    private bool SetPreviewAsset(string value)
    {
        string relative = value.Replace('\\', '/').Trim();
        if (Path.IsPathFullyQualified(value))
        {
            string full = Path.GetFullPath(value);
            string root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            relative = Path.GetRelativePath(ProjectRoot, full).Replace('\\', '/');
        }

        if (relative.Length > 0) relative = ResourceNames.Name(ProjectRoot, relative);
        if (relative.Length > 0
            && !_targetAssets.Contains(relative, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.Equals(_document.PreviewAsset, relative, StringComparison.OrdinalIgnoreCase)) return true;

        RecordDocumentEdit("Change shader preview asset", () =>
        {
            bool wasUpdating = _updatingUi;
            _updatingUi = true;
            _document.PreviewAsset = relative;
            int index = _targetAssets.FindIndex(candidate =>
                string.Equals(candidate, relative, StringComparison.OrdinalIgnoreCase));
            _targetAssetCombo.SelectedIndex = index + 1;
            PopulateTerrainComponents();
            _updatingUi = wasUpdating;
            InvalidatePreviewTarget();
            UpdatePreviewContext();
            MarkDirty();
        });
        return true;
    }

    private bool SetTargetComponent(string component)
    {
        component = TerrainShaderTargetCatalog.Normalize(component);
        int index = _terrainComponentIds.FindIndex(candidate =>
            string.Equals(candidate, component, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        if (string.Equals(_document.TargetComponent, component, StringComparison.OrdinalIgnoreCase)) return true;
        RecordDocumentEdit("Change shader target component", () =>
        {
            bool wasUpdating = _updatingUi;
            _updatingUi = true;
            _document.TargetComponent = _terrainComponentIds[index];
            _terrainComponentCombo.SelectedIndex = index;
            _updatingUi = wasUpdating;
            InvalidatePreviewTarget();
            UpdatePreviewContext();
            MarkDirty();
        });
        return true;
    }

    private static object InspectorParameterValue(ShaderParameterValue parameter, int index)
    {
        float value = parameter.Value[index];
        if (parameter.Type.Equals("bool", StringComparison.OrdinalIgnoreCase)) return value != 0f;
        if (parameter.Type.StartsWith("int", StringComparison.OrdinalIgnoreCase)
            || parameter.Type.StartsWith("uint", StringComparison.OrdinalIgnoreCase))
        {
            return (int)MathF.Round(value);
        }

        return value;
    }

    private static string HumanizeParameter(string name) =>
        Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        const string prefix = "Parameters.";
        if (!propertyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (propertyPath.Equals("AuthoringMode", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(text, ignoreCase: true, out ShaderAuthoringMode mode))
            {
                SetAuthoringMode(mode);
                return true;
            }
            if (propertyPath.Equals("TargetType", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(text, ignoreCase: true, out ShaderTargetType target))
            {
                SetTargetType(target);
                return true;
            }
            if (propertyPath.Equals("Pipeline", StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse(text, ignoreCase: true, out ShaderAssetPipeline pipeline))
            {
                SetPipeline(pipeline);
                return true;
            }
            if (propertyPath.Equals("Entry", StringComparison.OrdinalIgnoreCase))
            {
                SetEntryPoint(text);
                return true;
            }
            if (propertyPath.Equals("Profile", StringComparison.OrdinalIgnoreCase))
            {
                SetCompilerProfile(text);
                return true;
            }
            if (propertyPath.Equals("Preset", StringComparison.OrdinalIgnoreCase))
                return SelectPreset(text);
            if (propertyPath.Equals("PreviewAsset", StringComparison.OrdinalIgnoreCase))
                return SetPreviewAsset(text);
            if (propertyPath.Equals("TargetComponent", StringComparison.OrdinalIgnoreCase))
                return SetTargetComponent(text);
            if (propertyPath.Equals("ActiveVariant", StringComparison.OrdinalIgnoreCase))
                return SelectVariant(text);
            if (propertyPath.StartsWith("Resources.", StringComparison.OrdinalIgnoreCase))
                return SetResourceBinding(propertyPath["Resources.".Length..], text);
            return false;
        }
        string[] parts = propertyPath[prefix.Length..]
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2) return false;

        ShaderParameterValue? parameter = _document.Parameters.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, parts[0], StringComparison.OrdinalIgnoreCase));
        if (parameter is null) return false;
        int component = parts.Length == 1 ? 0 : parts[1].ToUpperInvariant() switch
        {
            "X" or "R" => 0,
            "Y" or "G" => 1,
            "Z" or "B" => 2,
            "W" or "A" => 3,
            _ => -1,
        };
        if (component < 0 || component >= parameter.Value.Length) return false;

        try
        {
            float[] values = parameter.Value.ToArray();
            values[component] = Convert.ToSingle(value, CultureInfo.InvariantCulture);
            return SetParameterValue(parameter.Name, values);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException
                                           or OverflowException)
        {
            return false;
        }
    }

    public override void Undo()
    {
        CommitPendingSourceEdit();
        base.Undo();
    }

    public override void Redo()
    {
        CommitPendingSourceEdit();
        base.Redo();
    }

    public override void Save()
    {
        _document.SchemaVersion = 6;
        _document.Source = _code.CodeText;
        _document.Entry = _entryBox.Text;
        _document.VertexEntry = _vertexEntryBox.Text.Trim();
        SyncActiveShaderPass();
        _document.Profile = _profileCombo.SelectedItem?.ToString() ?? "ps_5_0";
        if (!TrySynchronizeParameters(showDiagnostics: true)) return;
        SaveJson(_document);
        AcceptSave();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CompileNow()
    {
        _liveCompileTimer?.Stop();
        _document.Source = _code.CodeText;
        _document.Entry = string.IsNullOrWhiteSpace(_entryBox.Text) ? "MainPS" : _entryBox.Text.Trim();
        _document.VertexEntry = _vertexEntryBox.Text.Trim();
        SyncActiveShaderPass();
        _document.Profile = _profileCombo.SelectedItem?.ToString() ?? "ps_5_0";
        if (!TrySynchronizeParameters(showDiagnostics: true))
        {
            CommitPendingSourceEdit();
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        RebuildParameterControls();
        _output.BeginUpdate();
        _output.Items.Clear();
        try
        {
            string source = _document.ResolveCompiledSource();
            IReadOnlyList<string> includeRoots = ShaderCompiler.BuildDefaultIncludeSearchPaths(ResourcePath, ProjectRoot);
            byte[] bytecode = ShaderCompiler.Compile(
                source, _document.Entry, _document.Profile, ResourcePath, includeRoots);
            byte[] spirv = ShaderCompiler.CompileForBackend(
                source,
                _document.Entry,
                GpuShaderStage.Pixel,
                GpuShaderBinaryFormat.SpirV,
                ResourcePath,
                includeRoots).Blob;
            int vertexBytes = 0;
            int vertexSpirvBytes = 0;
            if (!string.IsNullOrWhiteSpace(_document.VertexEntry))
            {
                vertexBytes = ShaderCompiler.CompileForBackend(
                    source,
                    _document.VertexEntry,
                    GpuShaderStage.Vertex,
                    GpuShaderBinaryFormat.Dxbc,
                    ResourcePath,
                    includeRoots).Blob.Length;
                vertexSpirvBytes = ShaderCompiler.CompileForBackend(
                    source,
                    _document.VertexEntry,
                    GpuShaderStage.Vertex,
                    GpuShaderBinaryFormat.SpirV,
                    ResourcePath,
                    includeRoots).Blob.Length;
            }
            LastCompileSucceeded = bytecode.Length > 0;
            _pendingPreviewApply = LastCompileSucceeded;
            _resetPreviewTime = LastCompileSucceeded;
            _previewFrame = 0;
            _output.Items.Add(new ListViewItem(
                $"✓ Compiled {(_document.VertexEntry.Length == 0 ? "engine VS" : _document.VertexEntry)} + {_document.Entry} · {_document.TargetType} · DXBC {bytecode.Length + vertexBytes} B · SPIR-V {spirv.Length + vertexSpirvBytes} B · {_document.Parameters.Count} control(s).")
            {
                ForeColor = EditorChrome.Success,
            });
            _compileIndicator.Text = "● Applying…";
            _compileIndicator.ForeColor = EditorChrome.Success;
            _statusLabel.Text = "Compile OK · live preview queued.";
            if (!_diagnosticsUserChoice)
                SetDiagnosticsVisible(false, userChoice: false);
        }
        catch (Exception exception)
        {
            LastCompileSucceeded = false;
            _pendingPreviewApply = false;
            foreach (string line in exception.Message.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _output.Items.Add(new ListViewItem("✕ " + line.Trim())
                    {
                        ForeColor = EditorChrome.Error,
                    });
                }
            }

            _compileIndicator.Text = "● Error";
            _compileIndicator.ForeColor = EditorChrome.Error;
            _statusLabel.Text = _previewApplied
                ? "Compile failed · showing last successful preview."
                : "Compile failed.";
            SetDiagnosticsVisible(true, userChoice: false);
        }
        finally
        {
            UpdateFrameStatus();
            RefreshShaderBufferCards();
            _output.EndUpdate();
            ResizeDiagnosticsColumn();
            CommitPendingSourceEdit();
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F7)
        {
            CompileNow();
            return true;
        }
        if (keyData == (Keys.Control | Keys.D1) && _narrowLayout)
        {
            ShowNarrowAuthoring();
            return true;
        }
        if (keyData == (Keys.Control | Keys.D2) && _narrowLayout)
        {
            TogglePreview();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _liveCompileTimer?.Dispose();
            _playbackTimer?.Dispose();
            _speedSlider?.Dispose();
            _previewContext?.Dispose();
            _modeRail?.Dispose();
            _presetSurface?.Dispose();
            _renamePresetButton?.Dispose();
            _updatePresetButton?.Dispose();
            _deletePresetButton?.Dispose();
            ReleasePreviewResources();
        }

        base.Dispose(disposing);
    }

    private Control BuildFrameBar()
    {
        Panel bar = new() { Name = "ShaderTransport", BackColor = EditorChrome.Surface, Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(10, 8, 10, 8) };
        _playPauseButton = FrameButton("Play", TogglePlayback, 76);
        _playPauseButton.Name = "ShaderPlayPause";
        _playPauseButton.Dock = DockStyle.Left;
        Button stop = FrameButton("Stop", StopPreview, 70);
        stop.Name = "ShaderStop";
        stop.Dock = DockStyle.Left;
        _speedSlider = new TrackBar { Minimum = 0, Maximum = 60, Value = 60, Visible = false };
        _speedSlider.ValueChanged += (_, _) => { _playbackTimer.Interval = Math.Max(16, 1000 / Math.Max(1, _speedSlider.Value)); UpdateFrameStatus(); };
        _frameStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = EditorChrome.Muted, AutoEllipsis = true };
        bar.Controls.Add(_frameStatus);
        bar.Controls.Add(stop);
        bar.Controls.Add(_playPauseButton);
        UpdateFrameStatus();
        return bar;
    }

    private static Button FrameButton(string text, Action click, int width)
    {
        Button button = new()
        {
            Height = 28,
            Margin = new Padding(0, 2, 5, 0),
            Text = text,
            Width = width,
        };
        EditorChrome.StyleField(button);
        button.Click += (_, _) => click();
        return button;
    }

    private static Label SetupLabel(string text) => new()
    {
        AutoSize = false,
        ForeColor = EditorChrome.Muted,
        Height = 26,
        Margin = new Padding(5, 1, 4, 0),
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        Width = TextRenderer.MeasureText(text, EditorChrome.BaseFont).Width + 8,
    };

    private void TogglePreview()
    {
        _narrowPreviewActive = true;
        _widePreviewVisible = true;
        SetAuthoringMode(ShaderAuthoringMode.Preset, markDirty: false);
        ApplyResponsiveLayout();
    }

    private void ShowNarrowAuthoring()
    {
        _narrowPreviewActive = false;
        SetAuthoringMode(ShaderAuthoringMode.Code, markDirty: false);
        ApplyResponsiveLayout();
    }

    private void ToggleDiagnostics() =>
        SetDiagnosticsVisible(_diagnosticsToggle.Checked, userChoice: true);

    private void SetDiagnosticsVisible(bool visible, bool userChoice)
    {
        if (userChoice) _diagnosticsUserChoice = true;
        _diagnosticsToggle.Checked = visible;
        if (_referenceShaderLayout)
        {
            if (_referenceDiagnosticsHost is not null) _referenceDiagnosticsHost.Visible = visible;
            return;
        }
        _workspace.Panel2Collapsed = !visible;
        if (visible) SetWorkspaceSplitter();
    }

    private void ApplyResponsiveLayout()
    {
        if (_referenceShaderLayout)
        {
            ResizeParameterCards();
            return;
        }
        if (IsDisposed || _main is null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        _narrowLayout = ClientSize.Width < 1050;
        bool code = _document.AuthoringMode == ShaderAuthoringMode.Code;
        _authorToggle.Visible = false;
        _previewToggle.Visible = false;
        _controlsToggle.Visible = true;
        _main.SuspendLayout();
        _previewAndControls.SuspendLayout();
        try
        {
            _main.Panel1MinSize = _main.Panel2MinSize = 0;
            _main.Panel1Collapsed = false;
            _main.Panel2Collapsed = false;
            _main.Orientation = code && _narrowLayout ? Orientation.Horizontal : Orientation.Vertical;
            _main.Panel1Collapsed = !code;
            _main.Panel2Collapsed = code && !_widePreviewVisible;
            _main.IsSplitterFixed = !code;
            _narrowPreviewActive = !code;
            if (code && !_main.Panel2Collapsed)
            {
                if (_narrowLayout)
                {
                    int usable = Math.Max(0, _main.ClientSize.Height - _main.SplitterWidth);
                    // Keep source and its live result visible together, with the full width
                    // available to each. Short docks keep a proportional, draggable split.
                    _main.SplitterDistance = (int)(usable * .43f);
                }
                else
                    _main.SplitterDistance = Math.Clamp((int)(_main.Width * .39f), 360, Math.Max(360, _main.Width - 500));
            }
            _controlsToggle.Checked = code ? !_narrowLayout && _codeControlsVisible : _controlsVisible;
            _controlsToggle.Enabled = !code || !_narrowLayout;
            _previewAndControls.Panel2Collapsed = !_controlsToggle.Checked;
            if (!_previewAndControls.Panel2Collapsed)
            {
                int width = _previewAndControls.ClientSize.Width;
                int controls = width < 820 ? 280 : 330;
                _previewAndControls.SplitterDistance = Math.Max(100, width - controls - _previewAndControls.SplitterWidth);
            }
            _presetLibrary.Visible = !code && _presetsExpanded;
            _presetHeader.Text = (_presetsExpanded ? "▾  " : "▸  ") + "Presets · " + (string.IsNullOrWhiteSpace(_document.Preset) ? "Custom shader" : _document.Preset);
            _presetHeader.Visible = !code;
            _presetDescription.Visible = !code;
        }
        catch (InvalidOperationException) { /* Retry after the parent settles its bounds. */ }
        finally
        {
            _main.ResumeLayout(true);
            _previewAndControls.ResumeLayout(true);
        }
        if (!_diagnosticsUserChoice) SetDiagnosticsVisible(!LastCompileSucceeded, userChoice: false);
        else if (!_workspace.Panel2Collapsed) SetWorkspaceSplitter();
        ResizeParameterCards();
    }

    private void SetWorkspaceSplitter()
    {
        if (_workspace.Panel2Collapsed || _workspace.ClientSize.Height <= 0) return;
        try
        {
            int usable = _workspace.ClientSize.Height - _workspace.SplitterWidth;
            int diagnosticsHeight = Math.Clamp(96, 72, Math.Max(72, usable / 4));
            if (usable > _workspace.Panel1MinSize + _workspace.Panel2MinSize)
            {
                _workspace.SplitterDistance = Math.Clamp(
                    usable - diagnosticsHeight,
                    _workspace.Panel1MinSize,
                    usable - _workspace.Panel2MinSize);
            }
        }
        catch (InvalidOperationException)
        {
            // See ApplyResponsiveLayout.
        }
    }

    private void NavigateToSelectedDiagnostic()
    {
        if (_output.SelectedItems.Count == 0) return;
        string text = _output.SelectedItems[0].Text;
        Match match = Regex.Match(
            text,
            @"(?:\(|\bline\s+)(?<line>\d+)(?:,\d+)?(?:\)|\b)",
            RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["line"].Value, out int line)) return;
        if (_narrowLayout) ShowNarrowAuthoring();
        SetAuthoringMode(ShaderAuthoringMode.Code, markDirty: false);
        int position = _code.TextBox.GetFirstCharIndexFromLine(Math.Max(0, line - 1));
        if (position >= 0)
        {
            _code.MoveCaret(position);
            _code.Focus();
        }
    }

    private void TogglePlayback()
    {
        _playing = !_playing;
        _playPauseButton.Text = _playing ? "Pause" : "Play";
        UpdateFrameStatus();
    }

    private void StopPreview()
    {
        _playing = false;
        _playPauseButton.Text = "Play";
        _previewFrame = 0;
        _resetPreviewTime = true;
        UpdateFrameStatus();
        _viewport.Invalidate(true);
    }

    private void StepPreview(int direction)
    {
        int fps = Math.Max(1, _speedSlider.Value);
        _viewport.Host.AdvanceSceneTime(direction / (float)fps);
        _previewFrame = Math.Max(0, _previewFrame + direction);
        UpdateFrameStatus();
        _viewport.Invalidate(true);
    }

    private void AdvancePlayback()
    {
        if (!_playing || _speedSlider.Value <= 0 || !PreviewVisible) return;
        _viewport.Host.AdvanceSceneTime(1f / _speedSlider.Value);
        _previewFrame++;
        UpdateFrameStatus();
    }

    private void UpdateFrameStatus()
    {
        if (_frameStatus is null || _speedSlider is null) return;
        string state = _playing && _speedSlider.Value > 0 ? "Playing" : "Paused";
        _frameStatus.Text = $"{state} · {_speedSlider.Value} fps · frame {_previewFrame}";
    }

    private void SetAuthoringMode(ShaderAuthoringMode mode, bool markDirty)
    {
        _document.AuthoringMode = mode;
        foreach ((ShaderAuthoringMode key, ToolStripButton button) in _modeButtons)
        {
            button.Checked = key == mode;
        }

        _presetSurface.Visible = false;
        _codeSurface.Visible = mode == ShaderAuthoringMode.Code;
        if (mode == ShaderAuthoringMode.Preset)
        {
            _presetSurface.BringToFront();
            if (_presetList.SelectedIndex < 0)
            {
                ShaderPresetDefinition? preset = _presets.FirstOrDefault(candidate => candidate.TargetType == _document.TargetType);
                if (preset is not null) SelectPresetInList(preset.Name);
            }
        }
        else
        {
            _codeSurface.BringToFront();
        }

        if (markDirty && !_updatingUi) MarkDirty();
        ApplyResponsiveLayout();
    }

    private void ChangeTargetType(ShaderTargetType targetType)
    {
        _document.TargetType = targetType;
        if (targetType != ShaderTargetType.Object) _document.Pipeline = ResolveTargetPipeline();
        _document.TargetComponent = targetType == ShaderTargetType.Terrain
            ? _document.TargetComponent
            : string.Empty;
        _viewport.Mode2D = _document.Pipeline != ShaderAssetPipeline.Mesh;

        bool previousUpdating = _updatingUi;
        _updatingUi = true;
        _targetTypeCombo.SelectedItem = targetType.ToString();
        PopulateTargetAssets();
        PopulateTerrainComponents();
        _updatingUi = previousUpdating;
        InvalidatePreviewTarget();

        ShaderPresetDefinition? appliedPreset = _presets.FirstOrDefault(preset =>
            string.Equals(preset.Name, _document.Preset, StringComparison.OrdinalIgnoreCase));
        if (appliedPreset is not null && appliedPreset.TargetType != targetType)
        {
            _document.Preset = string.Empty;
            ShaderPresetDefinition? suggested = _presets.FirstOrDefault(preset => preset.TargetType == targetType);
            if (suggested is not null) SelectPresetInList(suggested.Name);
        }

        _pendingPreviewApply = LastCompileSucceeded;
        QueueLiveCompile();

        UpdatePreviewContext();
        if (!_updatingUi) MarkDirty();
    }

    private void ApplyPreset(ShaderPresetDefinition preset)
    {
        bool previousUpdating = _updatingUi;
        _updatingUi = true;
        _document.Preset = preset.Name;
        _document.AuthoringMode = ShaderAuthoringMode.Preset;
        bool keepObject = _document.TargetType == ShaderTargetType.Object
            && preset.TargetType is ShaderTargetType.Image or ShaderTargetType.Model
            && PipelineForTarget(preset.TargetType) == ResolveTargetPipeline();
        _document.TargetType = keepObject ? ShaderTargetType.Object : preset.TargetType;
        _document.Pipeline = ResolveTargetPipeline();
        _targetTypeCombo.SelectedItem = _document.TargetType.ToString();
        _viewport.Mode2D = _document.Pipeline != ShaderAssetPipeline.Mesh;
        PopulateTargetAssets();
        PopulateTerrainComponents();
        _code.CodeText = preset.Source;
        _document.Source = preset.Source;
        _document.Entry = string.IsNullOrWhiteSpace(preset.Entry) ? "MainPS" : preset.Entry;
        _entryBox.Text = _document.Entry;
        _document.Profile = string.IsNullOrWhiteSpace(preset.Profile) ? "ps_5_0" : preset.Profile;
        ResetActiveShaderPass(preset.Name, preset.Source, _document.Entry);
        if (!_profileCombo.Items.Contains(_document.Profile)) _profileCombo.Items.Add(_document.Profile);
        _profileCombo.SelectedItem = _document.Profile;
        _document.Parameters = preset.Parameters?.Select(parameter => new ShaderParameterValue
        {
            Name = parameter.Name,
            Type = parameter.Type,
            Value = parameter.Value?.ToArray() ?? [],
        }).ToList() ?? [];
        SetAuthoringMode(ShaderAuthoringMode.Preset, markDirty: false);
        ShaderParameterReflection.Synchronize(_document);
        _presetDescription.Text = preset.Description;
        RebuildParameterControls();
        _updatingUi = previousUpdating;
        InvalidatePreviewTarget();
        UpdatePreviewContext();
        MarkDirty();
        CompileNow();
    }

    private void SelectPresetInList(string name)
    {
        int index = _visiblePresets.FindIndex(preset =>
            string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            ShaderPresetDefinition? requested = _presets.FirstOrDefault(preset =>
                string.Equals(preset.Name, name, StringComparison.OrdinalIgnoreCase));
            if (requested is not null && _presetSearch.TextLength > 0)
            {
                bool wasUpdating = _updatingUi;
                _updatingUi = true;
                _presetSearch.Clear();
                _updatingUi = wasUpdating;
                RefreshPresetList(name);
                return;
            }

            index = _visiblePresets.FindIndex(preset => preset.TargetType == _document.TargetType);
        }

        _presetList.SelectedIndex = _visiblePresets.Count == 0 ? -1 : Math.Max(0, index);
        UpdatePresetSelection();
    }

    private void RefreshPresetList(string? selectedName = null)
    {
        selectedName ??= SelectedPreset()?.Name;
        string filter = _presetSearch.Text.Trim();
        bool wasUpdating = _updatingUi;
        _updatingUi = true;
        _visiblePresets.Clear();
        _visiblePresets.AddRange(_presets.Where(preset =>
            filter.Length == 0
            || preset.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || preset.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        _presetList.BeginUpdate();
        _presetList.Items.Clear();
        foreach (ShaderPresetDefinition preset in _visiblePresets)
        {
            _presetList.Items.Add(preset.Name + (preset.IsBuiltIn ? string.Empty : "  · Project"));
        }

        _presetList.EndUpdate();
        int selected = selectedName is null
            ? -1
            : _visiblePresets.FindIndex(preset =>
                string.Equals(preset.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        _presetList.SelectedIndex = selected >= 0
            ? selected
            : (_visiblePresets.Count > 0 ? 0 : -1);
        _updatingUi = wasUpdating;
        UpdatePresetSelection();
    }

    private ShaderPresetDefinition? SelectedPreset()
    {
        int index = _presetList.SelectedIndex;
        return index >= 0 && index < _visiblePresets.Count ? _visiblePresets[index] : null;
    }

    private void UpdatePresetSelection()
    {
        ShaderPresetDefinition? preset = SelectedPreset();
        _presetDescription.Text = preset is null
            ? "No presets match this filter."
            : preset.Description + (preset.IsBuiltIn ? " · Built-in" : " · Project preset");
        _applyPresetButton.Enabled = preset is not null;
        _renamePresetButton.Enabled = preset is { IsBuiltIn: false };
        _updatePresetButton.Enabled = preset is { IsBuiltIn: false };
        _deletePresetButton.Enabled = preset is { IsBuiltIn: false };
    }

    private void ApplySelectedPreset()
    {
        ShaderPresetDefinition? preset = SelectedPreset();
        if (preset is null) return;
        RecordDocumentEdit($"Apply shader preset '{preset.Name}'", () => ApplyPreset(preset));
    }

    private void SavePresetFromDialog()
    {
        string suggested = string.IsNullOrWhiteSpace(_document.Preset)
            ? $"{_document.TargetType} Preset"
            : _document.Preset + " Copy";
        string? name = PromptForPresetName(FindForm(), suggested);
        if (name is not null) SavePresetAs(name);
    }

    private void RenameSelectedPresetFromDialog()
    {
        ShaderPresetDefinition? preset = SelectedPreset();
        if (preset is null || preset.IsBuiltIn) return;
        string? name = PromptForPresetName(FindForm(), preset.Name, "Rename Shader Preset");
        if (name is not null) RenameSelectedPreset(name);
    }

    private static Button PresetButton(string text, Action click)
    {
        Button button = new()
        {
            Height = 30,
            Margin = new Padding(0, 0, 5, 5),
            Text = text,
            Width = 96,
        };
        EditorChrome.StyleField(button);
        button.Click += (_, _) => click();
        return button;
    }

    private static string? PromptForPresetName(
        IWin32Window? owner,
        string suggested,
        string title = "Save Shader Preset")
    {
        using Form dialog = new()
        {
            AcceptButton = null,
            BackColor = EditorChrome.Surface,
            ClientSize = new Size(390, 132),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            Text = title,
        };
        Label label = new()
        {
            AutoSize = true,
            ForeColor = EditorChrome.Text,
            Location = new Point(14, 14),
            Text = "Preset name",
        };
        TextBox input = new()
        {
            Location = new Point(17, 40),
            Text = suggested,
            Width = 356,
        };
        EditorChrome.StyleField(input);
        Button save = PresetButton("Save", () => dialog.DialogResult = DialogResult.OK);
        save.Location = new Point(173, 87);
        Button cancel = PresetButton("Cancel", () => dialog.DialogResult = DialogResult.Cancel);
        cancel.Location = new Point(277, 87);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(label);
        dialog.Controls.Add(input);
        dialog.Controls.Add(save);
        dialog.Controls.Add(cancel);
        dialog.Shown += (_, _) =>
        {
            input.SelectAll();
            input.Focus();
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
    }

    private void QueueLiveCompile()
    {
        _liveCompileTimer.Stop();
        if (_autoCompile.Checked) _liveCompileTimer.Start();
    }

    private void RecordDocumentEdit(string label, Action mutation)
    {
        if (_recordingDocumentEdit)
        {
            mutation();
            return;
        }

        CommitPendingSourceEdit();
        string before = CaptureDocument();
        _recordingDocumentEdit = true;
        try
        {
            mutation();
        }
        finally
        {
            _recordingDocumentEdit = false;
        }

        string after = CaptureDocument();
        if (string.Equals(before, after, StringComparison.Ordinal)) return;
        PushEdit(
            label,
            () => RestoreDocument(after),
            () => RestoreDocument(before));
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CommitPendingSourceEdit()
    {
        if (_pendingSourceSnapshot is null || _recordingDocumentEdit) return;
        string before = _pendingSourceSnapshot;
        _pendingSourceSnapshot = null;
        string after = CaptureDocument();
        if (string.Equals(before, after, StringComparison.Ordinal)) return;
        PushEdit(
            "Edit shader source",
            () => RestoreDocument(after),
            () => RestoreDocument(before));
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private string CaptureDocument() => JsonSerializer.Serialize(_document);

    private void RestoreDocument(string snapshot)
    {
        ShaderAssetDocument restored = JsonSerializer.Deserialize<ShaderAssetDocument>(snapshot)
            ?? throw new InvalidDataException("Shader history snapshot is empty.");
        _pendingSourceSnapshot = null;
        _liveCompileTimer.Stop();
        _updatingUi = true;
        try
        {
            _document = restored;
            EnsureShaderPasses();
            _code.CodeText = _document.Source;
            _entryBox.Text = _document.Entry;
            _vertexEntryBox.Text = _document.VertexEntry;
            if (!_profileCombo.Items.Contains(_document.Profile))
                _profileCombo.Items.Add(_document.Profile);
            _profileCombo.SelectedItem = _document.Profile;
            _targetTypeCombo.SelectedItem = _document.TargetType.ToString();
            _viewport.Mode2D = _document.Pipeline != ShaderAssetPipeline.Mesh;
            PopulateTargetAssets();
            PopulateTerrainComponents();
            SelectPresetInList(_document.Preset);
            SetAuthoringMode(_document.AuthoringMode, markDirty: false);
            RebuildParameterControls();
        }
        finally
        {
            _updatingUi = false;
        }

        InvalidatePreviewTarget();
        UpdatePreviewContext();
        CompileNow();
        ApplyResponsiveLayout();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPreviewState(IRenderController renderer)
    {
        _previewRenderer = renderer;
        ShaderParameterReflection.Pack(
            _document,
            null!,
            out Vector4 row0,
            out Vector4 row1,
            out Vector4 row2,
            out Vector4 row3);
        renderer.SetShaderPreviewParameters(row0, row1, row2, row3);
        if (_resetPreviewTime)
        {
            renderer.ResetShaderPreviewTime();
            _resetPreviewTime = false;
        }

        if (!_pendingPreviewApply) return;
        if (!SupportsAuthoredShaderPreview)
        {
            // The software rasterizer handles engine materials but does not execute arbitrary
            // shader bytecode. A successful syntax check must not imply a working effect.
            const string explanation = "Software previews geometry only. Choose DX11, DX12, Vulkan or OpenGL to preview shader effects.";
            renderer.ClearPreviewShaderOverride();
            _pendingPreviewApply = false;
            _previewApplied = false;
            _compileIndicator.Text = "● Geometry only";
            _compileIndicator.ForeColor = EditorChrome.Muted;
            _compileIndicator.ToolTipText = explanation;
            _statusLabel.Text = explanation;
            _output.Items.Add(new ListViewItem(explanation) { ForeColor = EditorChrome.Muted });
            ResizeDiagnosticsColumn();
            return;
        }
        ShaderPreviewProfile profile = _document.Pipeline switch
        {
            ShaderAssetPipeline.Mesh => ShaderPreviewProfile.MeshPipeline,
            ShaderAssetPipeline.Fullscreen => ShaderPreviewProfile.FullscreenEffect,
            _ => ShaderPreviewProfile.SpritePipeline,
        };
        try
        {
            if (string.IsNullOrWhiteSpace(_document.VertexEntry))
            {
                renderer.SetPreviewShaderOverride(
                    _document.ResolveCompiledSource(),
                    ResourcePath,
                    profile,
                    ProjectRoot,
                    _document.Entry,
                    _document.Profile);
            }
            else
            {
                renderer.SetPreviewShaderProgramOverride(
                    _document.ResolveCompiledSource(),
                    _document.VertexEntry,
                    _document.Entry,
                    ResourcePath,
                    profile,
                    ProjectRoot);
            }
            renderer.SetShaderPreviewTextures(BuildPreviewAuthoredTextures(renderer));
            renderer.ResetShaderPreviewTime();
            _pendingPreviewApply = false;
            _previewApplied = true;
            _compileIndicator.Text = "● Ready";
            _compileIndicator.ForeColor = EditorChrome.Success;
            _statusLabel.Text = _previewContext.Text;
        }
        catch (Exception exception)
        {
            _pendingPreviewApply = false;
            LastCompileSucceeded = false;
            _compileIndicator.Text = "● Preview error";
            _compileIndicator.ForeColor = EditorChrome.Error;
            _statusLabel.Text = "Preview failed · see Diagnostics for the complete error.";
            foreach (string line in exception.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                _output.Items.Add(new ListViewItem(line) { ForeColor = EditorChrome.Error });
            SetDiagnosticsVisible(true, userChoice: false);
        }
    }

    private void DrawPreview2D(IRenderController renderer)
    {
        ApplyPreviewState(renderer);
        if (_document.Pipeline == ShaderAssetPipeline.Fullscreen)
        {
            renderer.DrawShaderPreviewFullscreen(0, 0, renderer.PixelWidth, renderer.PixelHeight);
            return;
        }

        EnsurePreviewTexture(renderer);
        if (!_previewTexture.IsValid || (IsObjectPreview && _objectImage.Length == 0)) return;
        if (_document.TargetType == ShaderTargetType.Particle)
        {
            DrawParticlePreview(renderer);
            return;
        }

        const float pad = 0.94f;
        float viewW = renderer.PixelWidth * pad;
        float viewH = renderer.PixelHeight * pad;
        (float texW, float texH) = ResolvePreviewImageDimensions();
        float scale = MathF.Min(viewW / texW, viewH / texH);
        float drawW = texW * scale;
        float drawH = texH * scale;
        renderer.DrawSprite(new SpriteDrawCall
        {
            Texture = _previewTexture,
            X = 0f,
            Y = 0f,
            Width = drawW,
            Height = drawH,
            OriginX = drawW * 0.5f,
            OriginY = drawH * 0.5f,
            ScaleX = 1f,
            ScaleY = 1f,
            Alpha = 1f,
            Tint = RenderColor.White,
            AuthoredTextures = BuildPreviewAuthoredTextures(renderer),
        });
    }

    private (float Width, float Height) ResolvePreviewImageDimensions()
    {
        string? target = IsObjectPreview && _objectImage.Length > 0
            ? ResourceNames.Resolve(ProjectRoot, _objectImage, ResourceType.Image) : ResolveTargetResourcePath();
        if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(target));
                if (json.RootElement.TryGetProperty("width", out JsonElement width)
                    && json.RootElement.TryGetProperty("height", out JsonElement height)
                    && width.TryGetInt32(out int w)
                    && height.TryGetInt32(out int h)
                    && w > 0
                    && h > 0)
                {
                    return (w, h);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
            }
        }

        return (256f, 256f);
    }

    private void DrawParticlePreview(IRenderController renderer)
    {
        float scale = MathF.Min(renderer.PixelWidth, renderer.PixelHeight) / 720f;
        for (int index = 0; index < 9; index++)
        {
            float angle = index / 9f * MathF.PI * 2f;
            float radius = (55f + (index % 3) * 34f) * scale;
            float size = (50f - (index % 3) * 8f) * scale;
            renderer.DrawSprite(new SpriteDrawCall
            {
                Texture = _previewTexture,
                X = renderer.PixelWidth * 0.5f + MathF.Cos(angle) * radius,
                Y = renderer.PixelHeight * 0.5f + MathF.Sin(angle) * radius,
                Width = size,
                Height = size,
                OriginX = size * 0.5f,
                OriginY = size * 0.5f,
                ScaleX = 1f,
                ScaleY = 1f,
                Alpha = 0.85f,
                Tint = RenderColor.White,
            });
        }
    }

    private void DrawPreview3D(IRenderController renderer)
    {
        ApplyPreviewState(renderer);
        if (_assetRefreshPending)
        {
            _modelPreviewRenderer.InvalidateAssets(renderer);
            _assetRefreshPending = false;
        }
        if (_document.TargetType == ShaderTargetType.Terrain && !IsObjectPreview)
        {
            DrawTerrainPreview(renderer);
            return;
        }

        string modelAsset = PreviewModelAsset();
        FramePreviewModel(modelAsset);
        if (!string.IsNullOrWhiteSpace(modelAsset)
            && _modelPreviewRenderer.DrawModel(
                ProjectRoot, modelAsset, null, _previewModelWorld,
                new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true },
                new ModelRendererComponent { ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f, CastShadows = true, ReceiveShadows = true },
                default, renderer)) return;

        if (IsObjectPreview) return;
        renderer.DrawMesh(new MeshDrawCall
        {
            Mesh = renderer.GetBuiltinMesh(BuiltinMeshKind.Sphere),
            World = _previewModelWorld, Tint = RenderColor.White, Alpha = 1f,
            AuthoredTextures = BuildPreviewAuthoredTextures(renderer),
        });
    }

    private Vector4[] ReadTerrainLayerColours(string terrainResource, string componentFilter)
    {
        Vector4[] result = TerrainMeshBuilder.DefaultLayerColors.ToArray();
        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(terrainResource));
            if (json.RootElement.TryGetProperty("layers", out JsonElement layers)
                && layers.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement layer in layers.EnumerateArray())
                {
                    if (index >= result.Length) break;
                    if (componentFilter.StartsWith("layer:", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(componentFilter["layer:".Length..], out int selected)
                        && selected != index)
                    {
                        index++;
                        continue;
                    }

                    if (layer.TryGetProperty("color", out JsonElement colour)
                        && colour.ValueKind == JsonValueKind.Array)
                    {
                        float[] values = colour.EnumerateArray().Select(value => value.GetSingle()).ToArray();
                        if (values.Length >= 3) result[index] = new Vector4(values[0], values[1], values[2], 1f);
                    }

                    index++;
                }

                if (componentFilter.StartsWith("layer:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(componentFilter["layer:".Length..], out int onlyLayer)
                    && onlyLayer >= 0
                    && onlyLayer < result.Length)
                {
                    Vector4 selected = result[onlyLayer];
                    for (int i = 0; i < result.Length; i++)
                    {
                        result[i] = i == onlyLayer
                            ? selected
                            : new Vector4(selected.X * 0.18f, selected.Y * 0.18f, selected.Z * 0.18f, 1f);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return result;
        }

        return result;
    }

    private void EnsurePreviewTexture(IRenderController renderer)
    {
        _previewRenderer = renderer;
        if (_assetRefreshPending)
        {
            if (_previewTexture.IsValid) renderer.ReleaseTexture(_previewTexture);
            foreach (TextureHandle handle in _resourceTextures.Values)
            {
                if (handle.IsValid) renderer.ReleaseTexture(handle);
            }

            _resourceTextures.Clear();
            _previewTexture = TextureHandle.Invalid;
            _loadedPreviewPath = null;
            _modelPreviewRenderer.InvalidateAssets(renderer);
            _assetRefreshPending = false;
        }

        string? path = ResolvePreviewImagePath();
        if (string.Equals(path, _loadedPreviewPath, StringComparison.OrdinalIgnoreCase)
            && _previewTexture.IsValid)
        {
            return;
        }

        if (_previewTexture.IsValid) renderer.ReleaseTexture(_previewTexture);
        _previewTexture = TextureHandle.Invalid;
        _loadedPreviewPath = path;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            _previewTexture = renderer.LoadTexture(path);
        }

        if (!_previewTexture.IsValid)
        {
            byte[] pixels =
            [
                255, 255, 255, 255, 42, 53, 84, 255,
                42, 53, 84, 255, 255, 255, 255, 255,
            ];
            _previewTexture = renderer.CreateTexture(2, 2, pixels);
        }
    }

    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        base.OnAssetDependenciesChanged(changes);
        _assetRefreshPending = true;
        InvalidatePreviewTarget();
        bool wasUpdating = _updatingUi;
        _updatingUi = true;
        PopulateTargetAssets();
        PopulateTerrainComponents();
        _updatingUi = wasUpdating;
        _pendingPreviewApply = LastCompileSucceeded;
        _viewport.Invalidate(true);
        _statusLabel.Text = $"Live preview rebound · generation {changes.Generation}";
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private string? ResolvePreviewImagePath()
    {
        string? target = ResolveTargetResourcePath();
        if (IsObjectPreview)
        {
            ResolveObjectPreview();
            target = _objectImage.Length == 0 ? null : ResourceNames.Resolve(ProjectRoot, _objectImage, ResourceType.Image);
        }
        if (target is null) return null;
        if (_document.TargetType == ShaderTargetType.Particle)
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(target));
                if (json.RootElement.TryGetProperty("texturePath", out JsonElement texture)
                    && texture.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(texture.GetString()))
                {
                    string relative = texture.GetString()!;
                    string resource = ResourceNames.Resolve(ProjectRoot, relative, ResourceType.Image);
                    string root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    if (!resource.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                    return ResourceAssociates.FindPrimaryImage(resource);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                return null;
            }

            return null;
        }

        return ResourceAssociates.FindPrimaryImage(target);
    }

    private string? ResolveTargetResourcePath()
    {
        string path = ResourceNames.Resolve(ProjectRoot, _document.PreviewAsset);
        return path.Length == 0 ? null : path;
    }

        private readonly List<string> _imageAssetDisplayNames = [];

    private void PopulateImageAssets()
    {
        _imageAssets.Clear();
        _imageAssetDisplayNames.Clear();
        foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.Image))
        {
            _imageAssets.Add(ResourceNames.Name(ProjectRoot, entry.FullPath));
            _imageAssetDisplayNames.Add(entry.DisplayName);
        }

    }

    private AuthoredShaderTextures BuildPreviewAuthoredTextures(IRenderController renderer)
    {
        var textures = new AuthoredShaderTextures();
        foreach (ShaderResourceBinding resource in _document.ResolveResources())
        {
            if (!ShaderResourceReflection.IsTextureKind(resource.Kind)) continue;
            if (ShaderResourceReflection.IsPipelineOwned(_document.Pipeline, resource)) continue;
            if (string.IsNullOrWhiteSpace(resource.Binding)) continue;
            TextureHandle handle = EnsureResourceTexture(renderer, resource.Binding);
            if (handle.IsValid)
                textures.Add(resource.Slot, handle);
        }

        renderer.SetShaderPreviewTextures(textures);
        return textures;
    }

    private TextureHandle EnsureResourceTexture(IRenderController renderer, string relative)
    {
        if (_resourceTextures.TryGetValue(relative, out TextureHandle existing) && existing.IsValid)
            return existing;

        string full = ResourceNames.Resolve(ProjectRoot, relative, ResourceType.Image);
        string? pixels = ResourceAssociates.FindPrimaryImage(full);
        TextureHandle handle = !string.IsNullOrWhiteSpace(pixels) && File.Exists(pixels)
            ? renderer.LoadTexture(pixels)
            : TextureHandle.Invalid;
        if (handle.IsValid)
            _resourceTextures[relative] = handle;
        return handle;
    }

    private void PopulateTargetAssets()
    {
        PopulateImageAssets();
        string previous = _document.PreviewAsset;
        _targetAssets.Clear();
        _targetAssetCombo.Items.Clear();
        ResourceKind? kind = ResourceKindForTarget(_document.TargetType);
        if (kind is null)
        {
            _targetAssetCombo.Items.Add("Full frame");
            _targetAssetCombo.SelectedIndex = 0;
            _document.PreviewAsset = string.Empty;
            SetTargetControlVisibility(showTarget: false, showComponent: false);
            return;
        }

        if (_document.TargetType != ShaderTargetType.Terrain)
        {
            _targetAssetCombo.Items.Add(BuiltInTargetLabel(_document.TargetType));
        }
        else
        {
            _targetAssetCombo.Items.Add("(select terrain)");
        }
        foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, kind.Value))
        {
            string relative = ResourceNames.Name(ProjectRoot, entry.FullPath);
            _targetAssets.Add(relative);
            _targetAssetCombo.Items.Add(entry.DisplayName);
        }

        int selected = _targetAssets.FindIndex(path =>
            string.Equals(path, previous, StringComparison.OrdinalIgnoreCase));
        if (selected < 0 && previous.Length > 0 && _targetAssets.Count > 0)
        {
            selected = _targetAssets.FindIndex(path => path.Contains("Genesis", StringComparison.OrdinalIgnoreCase));
            if (selected < 0) selected = 0;
        }

        _document.PreviewAsset = selected >= 0 ? _targetAssets[selected] : string.Empty;
        _targetAssetCombo.SelectedIndex = selected >= 0 ? selected + 1 : 0;
        SetTargetControlVisibility(
            showTarget: true,
            showComponent: _document.TargetType == ShaderTargetType.Terrain);
    }

    private void PopulateTerrainComponents()
    {
        string previous = _document.TargetComponent;
        _terrainComponentCombo.Items.Clear();
        _terrainComponentIds.Clear();
        if (_document.TargetType != ShaderTargetType.Terrain)
        {
            _document.TargetComponent = string.Empty;
            SetTargetControlVisibility(
                showTarget: _document.TargetType != ShaderTargetType.Fullscreen,
                showComponent: false);
            return;
        }

        AddTerrainComponent(TerrainShaderTargetCatalog.None, "None");
        AddTerrainComponent(TerrainShaderTargetCatalog.All, "All");
        string? resource = ResolveTargetResourcePath();
        if (!string.IsNullOrWhiteSpace(resource) && File.Exists(resource))
        {
            foreach (TerrainShaderTarget target in TerrainShaderTargetCatalog.FromResource(resource))
            {
                AddTerrainComponent(target.Id, target.Label);
            }
        }

        int selected = _terrainComponentIds.FindIndex(id =>
            string.Equals(id, TerrainShaderTargetCatalog.Normalize(previous), StringComparison.OrdinalIgnoreCase));
        if (selected < 0)
        {
            selected = _terrainComponentIds.FindIndex(id =>
                string.Equals(id, TerrainShaderTargetCatalog.All, StringComparison.OrdinalIgnoreCase));
        }

        _terrainComponentCombo.SelectedIndex = selected >= 0 ? selected : 0;
        _document.TargetComponent = _terrainComponentIds[_terrainComponentCombo.SelectedIndex];
        SetTargetControlVisibility(showTarget: true, showComponent: true);
    }

    private void AddTerrainComponent(string id, string label)
    {
        _terrainComponentIds.Add(id);
        _terrainComponentCombo.Items.Add(label);
    }

    private void SetTargetControlVisibility(bool showTarget, bool showComponent)
    {
        _targetAssetLabel.Visible = showTarget;
        _targetAssetCombo.Visible = showTarget;
        _targetAssetGroup.Visible = showTarget;
        _terrainComponentLabel.Visible = showComponent;
        _terrainComponentCombo.Visible = showComponent;
        _componentGroup.Visible = showComponent;
        _previewSetup.PerformLayout();
    }

    private void UpdatePreviewContext()
    {
        string target = _targetAssetCombo.SelectedItem?.ToString() ?? BuiltInTargetLabel(_document.TargetType);
        string component = _document.TargetType == ShaderTargetType.Terrain
            ? " · " + (_terrainComponentCombo.SelectedItem?.ToString() ?? "All")
            : string.Empty;
        _previewContext.Text = $"{_document.TargetType} · {target}{component}";
        _dimensionLabel.Text = _document.Pipeline == ShaderAssetPipeline.Mesh ? "3D Preview" : "2D Preview";
        if (LastCompileSucceeded && _previewApplied)
            _statusLabel.Text = _previewContext.Text;
    }

    private void InvalidatePreviewTarget()
    {
        _assetRefreshPending = true;
        _framedModel = null;
        _resolvedObject = null;
        RefreshTargetDimension();
        _loadedPreviewPath = null;
        _loadedTerrainResource = null;
        _loadedTerrainComponent = null;
        _viewport.Invalidate(true);
    }

    private void ReleasePreviewResources()
    {
        if (_previewRenderer is null) return;
        if (_previewTexture.IsValid) _previewRenderer.ReleaseTexture(_previewTexture);
        foreach (TextureHandle handle in _resourceTextures.Values)
        {
            if (handle.IsValid) _previewRenderer.ReleaseTexture(handle);
        }

        _resourceTextures.Clear();
        ReleaseTerrainPreview(_previewRenderer);
        _modelPreviewRenderer.InvalidateAssets(_previewRenderer);
        _previewTexture = TextureHandle.Invalid;
    }

    private bool TrySynchronizeParameters(bool showDiagnostics)
    {
        if (ShaderParameterReflection.TrySynchronize(_document, out string? error)) return true;
        if (!showDiagnostics) return false;

        LastCompileSucceeded = false;
        _pendingPreviewApply = false;
        _output.BeginUpdate();
        _output.Items.Clear();
        _output.Items.Add(new ListViewItem("✕ " + error)
        {
            ForeColor = EditorChrome.Error,
        });
        _output.EndUpdate();
        ResizeDiagnosticsColumn();
        _compileIndicator.Text = "● Error";
        _compileIndicator.ForeColor = EditorChrome.Error;
        _statusLabel.Text = "Parameter ABI error · see diagnostics.";
        SetDiagnosticsVisible(true, userChoice: false);
        return false;
    }

    private void RebuildParameterControls()
    {
        if (_parameters is null) return;
        _parameters.SuspendLayout();
        while (_parameters.Controls.Count > 0) _parameters.Controls[0].Dispose();
        foreach (var group in _document.Parameters.GroupBy(ParameterSection).OrderBy(g => g.Key == "Appearance" ? 0 : 1))
        {
            _parameters.Controls.Add(ParameterSectionHeading(group.Key));
            foreach (ShaderParameterValue parameter in group)
                _parameters.Controls.Add(BuildParameterCard(parameter));
        }

        bool textureHeading = false;
        foreach (ShaderResourceBinding resource in _document.Resources)
        {
            if (!ShaderResourceReflection.IsTextureKind(resource.Kind)) continue;
            if (ShaderResourceReflection.IsPipelineOwned(_document.Pipeline, resource)) continue;
            if (!textureHeading) { _parameters.Controls.Add(ParameterSectionHeading("Textures")); textureHeading = true; }
            _parameters.Controls.Add(BuildResourceCard(resource));
        }

        if (_document.Parameters.Count == 0 && !_document.Resources.Any(resource =>
                ShaderResourceReflection.IsTextureKind(resource.Kind)
                && !ShaderResourceReflection.IsPipelineOwned(_document.Pipeline, resource)))
        {
            _parameters.Controls.Add(new Label
            {
                AutoSize = false,
                ForeColor = EditorChrome.Muted,
                Height = 76,
                Padding = new Padding(4, 8, 4, 4),
                Text = "No editable values.\n\nAdd parameters in Edit code to show controls here.",
                Width = Math.Max(220, _parameters.ClientSize.Width - 28),
            });
        }

        _parameters.ResumeLayout();
        ResizeParameterCards();
    }

    private Control BuildResourceCard(ShaderResourceBinding resource)
    {
        Panel card = new()
        {
            BackColor = EditorChrome.Raised,
            Height = 82,
            Margin = new Padding(0, 0, 0, EditorChrome.SectionGap),
            Name = "ShaderResourceCard",
            Padding = EditorChrome.CompactInsets,
            Width = Math.Max(220, _parameters.ClientSize.Width - 30),
        };
        card.Controls.Add(new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Top,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Height = 22,
            Text = FriendlyParameterName(resource.Name),
        });
        TableLayoutPanel picker = new()
        {
            Name = "ShaderResourcePicker_" + resource.Name,
            Tag = resource.Name,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 28, 0, 0),
            ColumnCount = 2,
            RowCount = 1,
        };
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        TextBox value = new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Text = string.IsNullOrWhiteSpace(resource.Binding)
                ? "(none)"
                : ResourceDisplayName.Format(resource.Binding),
        };
        Button browse = new() { Dock = DockStyle.Fill, Text = "…" };
        EditorChrome.StyleField(value);
        EditorChrome.StyleField(browse);
        browse.Click += (_, _) =>
        {
            ProjectAssetEntry? selected = Genesis.Application.Editors.Suite.Inspector.AssetPickerService.PickAsset(
                new Genesis.Application.Editors.Suite.Inspector.AssetPickerRequest(ProjectRoot, ResourceKind.Image, resource.Binding,
                    "Choose Shader Texture", AllowNone: true), FindForm());
            if (selected is null) return;
            SetResourceBinding(resource.Name, selected.Reference);
        };
        picker.Controls.Add(value, 0, 0);
        picker.Controls.Add(browse, 1, 0);
        card.Controls.Add(picker);
        picker.BringToFront();
        return card;
    }

    private Control BuildParameterCard(ShaderParameterValue parameter)
    {
        int componentCount = Math.Max(1, parameter.Value.Length);
        Panel card = new()
        {
            BackColor = EditorChrome.Surface,
            Height = componentCount == 1 ? 82 : 44 + componentCount * 34,
            Margin = new Padding(0, 0, 0, EditorChrome.SectionGap),
            Name = "ShaderParameterCard",
            Padding = EditorChrome.CompactInsets,
            Width = Math.Max(220, _parameters.ClientSize.Width - 30),
        };
        card.Controls.Add(new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Top,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Height = 22,
            Text = FriendlyParameterName(parameter.Name),
        });

        if (componentCount == 1)
        {
            AddScalarControl(card, parameter);
        }
        else
        {
            AddVectorControls(card, parameter);
        }

        return card;
    }

    private void AddScalarControl(Panel card, ShaderParameterValue parameter)
    {
        ShaderParameterDescriptor descriptor = ShaderParameterMetadata.Describe(
            parameter.Name,
            parameter.Type);
        if (descriptor.Kind == ShaderParameterScalarKind.Boolean)
        {
            Panel booleanRow = new()
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 28, 0, 0),
            };
            CheckBox toggle = new()
            {
                AutoSize = true,
                Checked = parameter.Value[0] != 0f,
                Dock = DockStyle.Left,
                ForeColor = EditorChrome.Text,
                Text = parameter.Value[0] != 0f ? "Enabled" : "Disabled",
            };
            toggle.CheckedChanged += (_, _) => RecordDocumentEdit(
                $"Set shader parameter '{parameter.Name}'",
                () =>
                {
                    parameter.Value[0] = toggle.Checked ? 1f : 0f;
                    toggle.Text = toggle.Checked ? "Enabled" : "Disabled";
                    ParameterChanged();
                });
            booleanRow.Controls.Add(toggle);
            card.Controls.Add(booleanRow);
            booleanRow.BringToFront();
            return;
        }

        if (descriptor.Kind is ShaderParameterScalarKind.SignedInteger
            or ShaderParameterScalarKind.UnsignedInteger)
        {
            Panel integerRow = new()
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 26, 0, 0),
            };
            NumericUpDown integer = ParameterNumeric(parameter.Value[0], descriptor);
            integer.Dock = DockStyle.Fill;
            integer.ValueChanged += (_, _) => RecordDocumentEdit(
                $"Set shader parameter '{parameter.Name}'",
                () =>
                {
                    parameter.Value[0] = (float)integer.Value;
                    ParameterChanged();
                });
            integerRow.Controls.Add(integer);
            card.Controls.Add(integerRow);
            integerRow.BringToFront();
            return;
        }

        TableLayoutPanel row = new()
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(0, 24, 0, 0),
            RowCount = 1,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        TrackBar slider = new()
        {
            Dock = DockStyle.Fill,
            LargeChange = 100,
            Maximum = 1000,
            Minimum = 0,
            TickStyle = TickStyle.None,
            Value = SliderValue(parameter.Value[0], descriptor),
        };
        NumericUpDown numeric = ParameterNumeric(parameter.Value[0], descriptor);
        numeric.Dock = DockStyle.Fill;
        bool synchronizing = false;
        slider.ValueChanged += (_, _) =>
        {
            if (synchronizing) return;
            RecordDocumentEdit($"Set shader parameter '{parameter.Name}'", () =>
            {
                synchronizing = true;
                float value = SliderParameter(slider.Value, descriptor);
                numeric.Value = (decimal)Math.Clamp(value, (float)numeric.Minimum, (float)numeric.Maximum);
                parameter.Value[0] = value;
                synchronizing = false;
                ParameterChanged();
            });
        };
        numeric.ValueChanged += (_, _) =>
        {
            if (synchronizing) return;
            RecordDocumentEdit($"Set shader parameter '{parameter.Name}'", () =>
            {
                synchronizing = true;
                parameter.Value[0] = (float)numeric.Value;
                slider.Value = SliderValue(parameter.Value[0], descriptor);
                synchronizing = false;
                ParameterChanged();
            });
        };
        row.Controls.Add(slider, 0, 0);
        row.Controls.Add(numeric, 1, 0);
        card.Controls.Add(row);
        row.BringToFront();
    }

    private void AddVectorControls(Panel card, ShaderParameterValue parameter)
    {
        ShaderParameterDescriptor descriptor = ShaderParameterMetadata.Describe(
            parameter.Name,
            parameter.Type);
        TableLayoutPanel rows = new()
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 2, 0, 0),
            RowCount = parameter.Value.Length,
        };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        string[] componentNames = ["X", "Y", "Z", "W"];
        for (int component = 0; component < parameter.Value.Length; component++)
        {
            int captured = component;
            rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            rows.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = EditorChrome.Muted,
                Text = componentNames[Math.Min(component, componentNames.Length - 1)],
                TextAlign = ContentAlignment.MiddleLeft,
            }, 0, component);
            NumericUpDown numeric = ParameterNumeric(parameter.Value[component], descriptor);
            numeric.Dock = DockStyle.Fill;
            numeric.ValueChanged += (_, _) => RecordDocumentEdit(
                $"Set shader parameter '{parameter.Name}.{componentNames[captured]}'",
                () =>
                {
                    parameter.Value[captured] = (float)numeric.Value;
                    ParameterChanged();
                });
            rows.Controls.Add(numeric, 1, component);
        }

        card.Controls.Add(rows);
        rows.BringToFront();
    }

    private static NumericUpDown ParameterNumeric(
        float value,
        ShaderParameterDescriptor descriptor)
    {
        NumericUpDown input = new()
        {
            DecimalPlaces = descriptor.DecimalPlaces,
            Increment = (decimal)descriptor.Increment,
            Maximum = (decimal)descriptor.Maximum,
            Minimum = (decimal)descriptor.Minimum,
            Value = (decimal)Math.Clamp(value, descriptor.Minimum, descriptor.Maximum),
        };
        EditorChrome.StyleField(input);
        return input;
    }

    private void ParameterChanged()
    {
        _viewport.Invalidate(true);
        MarkDirty();
    }

    private void ResizeParameterCards()
    {
        int width = Math.Max(220, _parameters.ClientSize.Width - _parameters.Padding.Horizontal - 20);
        foreach (Control control in _parameters.Controls)
        {
            control.Width = width;
        }
    }

    private static int SliderValue(float value, ShaderParameterDescriptor descriptor) =>
        (int)Math.Round(Math.Clamp(
            (value - descriptor.Minimum) / (descriptor.Maximum - descriptor.Minimum),
            0f,
            1f) * 1000f);

    private static float SliderParameter(int value, ShaderParameterDescriptor descriptor) =>
        descriptor.Minimum + (descriptor.Maximum - descriptor.Minimum) * (value / 1000f);

    private static ShaderAssetPipeline PipelineForTarget(ShaderTargetType target) => target switch
    {
        ShaderTargetType.Model or ShaderTargetType.Terrain => ShaderAssetPipeline.Mesh,
        ShaderTargetType.Fullscreen => ShaderAssetPipeline.Fullscreen,
        _ => ShaderAssetPipeline.Sprite,
    };

    private static ShaderTargetType TargetForPipeline(ShaderAssetPipeline pipeline) => pipeline switch
    {
        ShaderAssetPipeline.Mesh => ShaderTargetType.Model,
        ShaderAssetPipeline.Fullscreen => ShaderTargetType.Fullscreen,
        _ => ShaderTargetType.Image,
    };

    private static string[] ShaderTargetTypesForUi() =>
        Enum.GetNames<ShaderTargetType>()
            .ToArray();

    private static ResourceKind? ResourceKindForTarget(ShaderTargetType target) => target switch
    {
        ShaderTargetType.Object => ResourceKind.GameObject,
        ShaderTargetType.Image => ResourceKind.Image,
        ShaderTargetType.Model => ResourceKind.Model,
        ShaderTargetType.Particle => ResourceKind.Particle,
        ShaderTargetType.Terrain => ResourceKind.Terrain,
        _ => null,
    };

    private static string BuiltInTargetLabel(ShaderTargetType target) => target switch
    {
        ShaderTargetType.Object => "(select object)",
        ShaderTargetType.Image => "Built-in checker",
        ShaderTargetType.Model => "Built-in sphere",
        ShaderTargetType.Particle => "Built-in particles",
        ShaderTargetType.Terrain => "Built-in terrain",
        _ => "Full frame",
    };

    private void DrawPresetListItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _presetList.Items.Count)
            return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color fill = selected
            ? Color.FromArgb(55, EditorChrome.Accent)
            : EditorChrome.Raised;
        using SolidBrush brush = new(fill);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (selected)
        {
            using SolidBrush accent = new(EditorChrome.Accent);
            e.Graphics.FillRectangle(
                accent,
                new Rectangle(e.Bounds.X, e.Bounds.Y + 3, 3, Math.Max(6, e.Bounds.Height - 6)));
        }

        TextRenderer.DrawText(
            e.Graphics,
            _presetList.Items[e.Index]?.ToString() ?? string.Empty,
            EditorChrome.BaseFont,
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
            selected ? EditorChrome.Text : EditorChrome.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private static List<HighlightRule> BuildHlslRules()
    {
        Color keyword = EditorChrome.FromHex("#6C8CFF");
        Color type = EditorChrome.FromHex("#4EC9B0");
        Color semantic = EditorChrome.FromHex("#D882C6");
        Color number = EditorChrome.FromHex("#B5CEA8");
        Color comment = EditorChrome.FromHex("#6A9955");
        return
        [
            new HighlightRule(new Regex(@"\b\d+(?:\.\d+)?f?\b", RegexOptions.Compiled), number),
            new HighlightRule(new Regex(@"\b(cbuffer|struct|register|return|if|else|for|while|static|const|row_major)\b", RegexOptions.Compiled), keyword),
            new HighlightRule(new Regex(@"\b(float[1-4]?(?:x[1-4])?|int[1-4]?|uint[1-4]?|bool|void|Texture2D|Texture3D|TextureCube|SamplerState|StructuredBuffer)\b", RegexOptions.Compiled), type),
            new HighlightRule(new Regex(@":\s*([A-Z_][A-Za-z0-9_]*)", RegexOptions.Compiled), semantic),
            new HighlightRule(new Regex(@"//[^\r\n]*", RegexOptions.Compiled), comment),
            new HighlightRule(new Regex(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline), comment),
        ];
    }
}


