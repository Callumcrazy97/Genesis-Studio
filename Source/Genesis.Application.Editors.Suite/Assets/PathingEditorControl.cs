using System.Drawing.Drawing2D;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed class PathingDebugRequestedEventArgs(string targetRoom, string pathingAsset) : EventArgs
{
    public string TargetRoom { get; } = targetRoom;
    public string PathingAsset { get; } = pathingAsset;
}

/// <summary>
/// Visual and declarative authoring surface for reusable navigation routes. The editor previews
/// project Rooms and Objects but does not encode NPC roles or game-specific behavior.
/// </summary>
public sealed class PathingEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private sealed record AssetChoice(string Name, string Path)
    {
        public override string ToString() => Name;
    }

    private PathingAsset _asset;
    private RoomAsset? _room;
    private NavMeshData? _navMesh;
    private readonly PathingPreviewSimulation _simulation = new();
    private readonly PathingViewportControl _viewport;
    private readonly PathingTimelineControl _timeline = new();
    private readonly CrowdGraphControl _crowdGraph = new();
    private readonly CodeEditor _code = new();
    private readonly DataGridView _roster = new();
    private readonly Label _status = EditorChrome.MakeStatusBar();
    private readonly Label _codeStatus = new();
    private readonly Label _inspectorSummary = new();
    private readonly Dictionary<PathingRouteMode, Button> _modeButtons = [];
    private readonly ThemedComboBox _loopMode = new();
    private readonly ThemedComboBox _animationState = new();
    private readonly NumericUpDown _speed = Number(0, 10_000, 3.8m, .1m, 2);
    private readonly NumericUpDown _stopping = Number(0, 10_000, .15m, .05m, 2);
    private readonly NumericUpDown _wait = Number(0, 86_400, 0m, .1m, 2);
    private readonly NumericUpDown _wanderRadius = Number(0, 100_000, 8m, .5m, 1);
    private readonly NumericUpDown _followOffset = Number(0, 100_000, 2m, .25m, 2);
    private readonly NumericUpDown _previewAgents = Number(1, 128, 3m, 1m, 0);
    private readonly TextBox _followTarget = new();
    private readonly System.Windows.Forms.Timer _frameTimer;
    private readonly System.Windows.Forms.Timer _codeTimer;
    private ToolStripComboBox? _roomPicker;
    private ToolStripComboBox? _objectPicker;
    private ToolStripButton? _playButton;
    private ToolStripButton? _pauseButton;
    private ToolStripButton? _stopButton;
    private ToolStripButton? _fastForwardButton;
    private ToolStripLabel? _documentTitle;
    private ToolStripLabel? _timeScaleValue;
    private TrackBar? _timeScale;
    private int _selectedWaypoint = -1;
    private bool _syncing;
    private bool _playing;
    private bool _paused;
    private bool _fastForward;
    private float _time;

    public PathingEditorControl(string resourcePath, string projectRoot) : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        _viewport = new PathingViewportControl(projectRoot);
        _asset = LoadAsset();
        Controls.Add(BuildWorkspace());
        Controls.Add(BuildToolbar());
        Controls.Add(_status);

        _codeTimer = new System.Windows.Forms.Timer { Interval = 450 };
        _codeTimer.Tick += (_, _) => { _codeTimer.Stop(); ApplyCode(); };
        _code.SetRules(BuildRules());
        _code.TextChangedByUser += (_, _) =>
        {
            if (_syncing) return;
            _codeTimer.Stop(); _codeTimer.Start();
            _codeStatus.Text = "Editing…";
            _codeStatus.ForeColor = EditorChrome.Warning;
        };
        _frameTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _frameTimer.Tick += (_, _) => AdvanceSimulation(1f / 60f);
        _viewport.WaypointSelected += index => { _selectedWaypoint = index; RefreshInspector(); };
        _viewport.WaypointMoved += MoveWaypoint;
        _viewport.WaypointDeleteRequested += index => { _selectedWaypoint = index; DeleteWaypoint(); };
        _timeline.TimeScrubbed += ScrubTo;
        _timeline.SpeedCurveChanged += () => CommitDocumentChange(resetSimulation: false);
        DirtyChanged += (_, _) => RefreshDocumentTitle();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Disposed += (_, _) => { _frameTimer.Dispose(); _codeTimer.Dispose(); };

        PopulateTargets();
        SyncUiFromAsset(resetSimulation: true);
        RefreshDocumentTitle();
        ApplyResponsiveLayout();
    }

    public event EventHandler? InspectorStateChanged;
    public event EventHandler<PathingDebugRequestedEventArgs>? DebugRequested;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F3)
        {
            Save();
            DebugRequested?.Invoke(this, new PathingDebugRequestedEventArgs(_asset.TargetRoom, ResourcePath));
            _status.Text = "Launching room with F6 debugger focused on AI & Navigation…";
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public override void Save()
    {
        ApplyCode();
        PathingAssetSerializer.Save(ResourcePath, _asset);
        AcceptSave();
        _status.Text = $"Saved {ResourceDisplayName.Format(ResourcePath)}";
    }

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues() =>
    [
        new("Pathing", "Pathing.Mode", "Route mode", _asset.Route.Mode.ToString(), Choices: Enum.GetNames<PathingRouteMode>()),
        new("Pathing", "Pathing.Loop", "Loop mode", _asset.Route.LoopMode.ToString(), Choices: Enum.GetNames<PathingLoopMode>()),
        new("Pathing", "Pathing.Speed", "Movement speed", _asset.Route.Speed, Minimum: 0, Maximum: 10000, Increment: .1m, DecimalPlaces: 2),
        new("Pathing", "Pathing.StoppingDistance", "Stopping distance", _asset.Route.StoppingDistance, Minimum: 0, Maximum: 10000, Increment: .05m, DecimalPlaces: 2),
        new("Pathing", "Pathing.Wait", "Default wait", _asset.Route.DefaultWaitSeconds, Minimum: 0, Maximum: 86400, Increment: .1m, DecimalPlaces: 2),
        new("Bindings", "Pathing.Room", "Target room", _asset.TargetRoom, AssetKind: ResourceKind.Room),
        new("Bindings", "Pathing.Object", "Preview object", _asset.TargetObject, AssetKind: ResourceKind.GameObject),
        new("Simulation", "Runtime.AgentCount", "Agents", _simulation.Agents.Count, ReadOnly: true),
        new("Simulation", "Runtime.Time", "Time", _time, ReadOnly: true),
        new("Simulation", "Runtime.CollisionWarning", "Path warning", _simulation.CollisionWarning, ReadOnly: true),
    ];

    public bool TryApplyInspectorValue(string propertyPath, object? value) => TryApplyLiveInspectorValue(propertyPath, value);

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        switch (propertyPath)
        {
            case "Pathing.Mode" when Enum.TryParse(text, true, out PathingRouteMode mode): _asset.Route.Mode = mode; break;
            case "Pathing.Loop" when Enum.TryParse(text, true, out PathingLoopMode loop): _asset.Route.LoopMode = loop; break;
            case "Pathing.Speed" when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed): _asset.Route.Speed = speed; break;
            case "Pathing.StoppingDistance" when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float stopping): _asset.Route.StoppingDistance = stopping; break;
            case "Pathing.Wait" when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float wait): _asset.Route.DefaultWaitSeconds = wait; break;
            case "Pathing.Room": _asset.TargetRoom = text; break;
            case "Pathing.Object": _asset.TargetObject = text; break;
            default: return false;
        }
        CommitDocumentChange(contextChanged: propertyPath is "Pathing.Room" or "Pathing.Object");
        return true;
    }

    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        if (!string.IsNullOrWhiteSpace(_asset.TargetRoom) || !string.IsNullOrWhiteSpace(_asset.TargetObject))
        {
            LoadContext();
            ResetSimulation();
        }
        base.OnAssetDependenciesChanged(changes);
    }

    private Control BuildToolbar()
    {
        EditorCommandBar toolbar = EditorChrome.MakeToolbar();
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildFileMenu(this));
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildEditMenu(this));
        toolbar.Items.Add(new ToolStripSeparator());
        _documentTitle = new ToolStripLabel(ResourceDisplayName.Format(ResourcePath)) { ForeColor = EditorChrome.Text, Font = EditorChrome.HeadingFont, ToolTipText = ResourcePath };
        toolbar.Items.Add(_documentTitle);
        ToolStripButton save = EditorChrome.ToolButton("Save", "Save pathing resource (Ctrl+S)", Save); save.BackColor = EditorChrome.Accent; toolbar.Items.Add(save);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(Caption("Target Room"));
        _roomPicker = Picker(190, OnRoomSelected); toolbar.Items.Add(_roomPicker);
        _roomPicker.Enabled = false;
        toolbar.Items.Add(EditorChrome.ToolButton("Browse…", "Choose target Room", () => PickBinding(ResourceKind.Room)));
        toolbar.Items.Add(Caption("Target Object"));
        _objectPicker = Picker(180, OnObjectSelected); toolbar.Items.Add(_objectPicker);
        _objectPicker.Enabled = false;
        toolbar.Items.Add(EditorChrome.ToolButton("Browse…", "Choose preview Object", () => PickBinding(ResourceKind.GameObject)));
        toolbar.Items.Add(new ToolStripSeparator());
        _playButton = EditorChrome.ToolButton("▶", "Play simulation", Play); _playButton.BackColor = EditorChrome.Accent; toolbar.Items.Add(_playButton);
        _pauseButton = EditorChrome.ToolButton("Ⅱ", "Pause simulation", Pause); toolbar.Items.Add(_pauseButton);
        _stopButton = EditorChrome.ToolButton("■", "Stop and reset", Stop); toolbar.Items.Add(_stopButton);
        toolbar.Items.Add(EditorChrome.ToolButton("Step", "Advance one 60 Hz frame", () => { if (!_playing) { _playing = true; _paused = true; } AdvanceSimulation(1f / 60f, force: true); }));
        _fastForwardButton = EditorChrome.ToolButton("»", "Toggle 4× fast-forward", () => { _fastForward = !_fastForward; RefreshTransport(); }, toggle: true);
        toolbar.Items.Add(_fastForwardButton);
        toolbar.Items.Add(Caption("Speed"));
        _timeScale = new TrackBar { Minimum = 1, Maximum = 40, Value = 10, TickStyle = TickStyle.None, Width = 105, Height = 24 };
        _timeScale.ValueChanged += (_, _) => RefreshTransport();
        toolbar.Items.Add(new ToolStripControlHost(_timeScale) { AutoSize = false, Size = new Size(112, 28), Margin = new Padding(0, 4, 4, 0) });
        _timeScaleValue = Caption("1.0×"); toolbar.Items.Add(_timeScaleValue);
        return toolbar;
    }

    private void PickBinding(ResourceKind kind)
    {
        string current = kind == ResourceKind.Room ? _asset.TargetRoom : _asset.TargetObject;
        ProjectAssetEntry? selected = AssetPickerService.PickAsset(
            new AssetPickerRequest(ProjectRoot, kind, current,
                kind == ResourceKind.Room ? "Choose Target Room" : "Choose Preview Object",
                AllowNone: kind == ResourceKind.GameObject), FindForm());
        if (selected is null) return;
        if (kind == ResourceKind.Room) _asset.TargetRoom = selected.Reference;
        else _asset.TargetObject = selected.Reference;
        PopulateTargets();
        CommitDocumentChange(contextChanged: true);
    }

    private Control BuildWorkspace()
    {
        Panel workspace = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas, Padding = new Padding(1) };
        Panel left = BuildLeftDock();
        Panel right = BuildRightDock();
        SplitContainer vertical = new()
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 4,
            BackColor = EditorChrome.Border, Panel2MinSize = 120,
        };
        vertical.HandleCreated += (_, _) => vertical.SplitterDistance = Math.Max(240, vertical.Height - 170);
        vertical.SizeChanged += (_, _) =>
        {
            if (vertical.Height > 300) vertical.SplitterDistance = Math.Clamp(vertical.Height - 170, 180, vertical.Height - 100);
        };
        SplitContainer center = new()
        {
            Dock = DockStyle.Fill, SplitterWidth = 4, BackColor = EditorChrome.Border,
            Panel1MinSize = 280, Panel2MinSize = 260,
        };
        center.HandleCreated += (_, _) => center.SplitterDistance = Math.Max(280, (int)(center.Width * .58f));
        center.Panel1.Controls.Add(ChromePanel("3D NAVMESH VIEWPORT", _viewport));
        center.Panel2.Controls.Add(ChromePanel("DECLARATIVE PGSL PATHING", BuildCodeSurface()));
        vertical.Panel1.Controls.Add(center);
        vertical.Panel2.Controls.Add(_timeline);
        workspace.Controls.Add(vertical);
        workspace.Controls.Add(right);
        workspace.Controls.Add(left);
        return workspace;
    }

    private Panel BuildLeftDock()
    {
        Panel host = EditorChrome.SidePanel(220, DockStyle.Left);
        Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10), BackColor = EditorChrome.Surface };
        FlowLayoutPanel flow = new()
        {
            Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, BackColor = EditorChrome.Surface, Padding = Padding.Empty,
        };
        flow.Controls.Add(Heading("ROUTE MODE"));
        AddMode(flow, PathingRouteMode.WaypointPatrol, "⌁", "Waypoint Patrol", "Fixed authored route with loop, ping-pong or once playback.");
        AddMode(flow, PathingRouteMode.NavMeshSearch, "⌕", "NavMesh A* Search", "Dynamic point-to-point travel constrained by the selected room NavMesh.");
        AddMode(flow, PathingRouteMode.WanderRadius, "◉", "Wander Radius", "Roam inside an authored radius using deterministic preview targets.");
        AddMode(flow, PathingRouteMode.FollowLeader, "⚑", "Follow Leader", "Trail another project entity using a configurable stopping offset.");
        flow.Controls.Add(Heading("WAYPOINT TOOLS"));
        flow.Controls.Add(ActionRow(("＋ Waypoint", AddWaypoint), ("⌁ Bezier", InsertCurvePoint)));
        flow.Controls.Add(ActionRow(("Delete", DeleteWaypoint), ("Snap Floor", SnapWaypoint)));
        flow.Controls.Add(Heading("PATH PARAMETERS"));
        flow.Controls.Add(Field("Loop", _loopMode));
        flow.Controls.Add(Field("Movement speed (m/s)", _speed));
        flow.Controls.Add(Field("Stopping distance", _stopping));
        flow.Controls.Add(Field("Wait at waypoints (s)", _wait));
        flow.Controls.Add(Field("Wander radius", _wanderRadius));
        flow.Controls.Add(Field("Follow offset", _followOffset));
        flow.Controls.Add(Field("Follow target", _followTarget));
        flow.Controls.Add(Field("Animation state", _animationState));
        flow.Controls.Add(Field("Preview agents", _previewAgents));
        WireParameterEvents();
        scroll.Controls.Add(flow);
        host.Controls.Add(scroll);
        host.Controls.Add(EditorChrome.SectionHeader("Pathing Tool Palette"));
        return host;
    }

    private Panel BuildRightDock()
    {
        Panel host = EditorChrome.SidePanel(244, DockStyle.Right);
        TabControl tabs = new() { Dock = DockStyle.Fill };
        TabPage active = new("Active") { BackColor = EditorChrome.Surface, Padding = new Padding(8) };
        TabPage inspector = new("Inspector") { BackColor = EditorChrome.Surface, Padding = new Padding(10) };
        _roster.Dock = DockStyle.Top; _roster.Height = 235; _roster.ReadOnly = true; _roster.AllowUserToAddRows = false;
        _roster.AllowUserToDeleteRows = false; _roster.AllowUserToResizeRows = false; _roster.RowHeadersVisible = false;
        _roster.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _roster.BackgroundColor = EditorChrome.Surface;
        _roster.BorderStyle = BorderStyle.None; _roster.GridColor = EditorChrome.Border; _roster.ForeColor = EditorChrome.Text;
        _roster.DefaultCellStyle.BackColor = EditorChrome.Surface; _roster.DefaultCellStyle.SelectionBackColor = EditorChrome.Accent;
        _roster.ColumnHeadersDefaultCellStyle.BackColor = EditorChrome.Raised; _roster.ColumnHeadersDefaultCellStyle.ForeColor = EditorChrome.Text;
        _roster.EnableHeadersVisualStyles = false; _roster.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _roster.Columns.Add("Agent", "Agent");
        _roster.Columns.Add("Position", "Position");
        _roster.Columns.Add("Destination", "Destination");
        _roster.Columns.Add("Waypoint", "WP");
        _roster.Columns.Add("Distance", "Remaining");
        _roster.Columns.Add("State", "State");
        Label graphTitle = Heading("CROWD SPATIAL HASH NEIGHBORS"); graphTitle.Dock = DockStyle.Top;
        Panel graphCard = new() { Dock = DockStyle.Fill, Padding = new Padding(5), BackColor = EditorChrome.Canvas };
        graphCard.Controls.Add(_crowdGraph);
        active.Controls.Add(graphCard); active.Controls.Add(graphTitle); active.Controls.Add(_roster);

        _inspectorSummary.Dock = DockStyle.Fill; _inspectorSummary.ForeColor = EditorChrome.Text; _inspectorSummary.BackColor = EditorChrome.Surface;
        _inspectorSummary.Font = EditorChrome.BaseFont; _inspectorSummary.Padding = new Padding(6); _inspectorSummary.TextAlign = ContentAlignment.TopLeft;
        inspector.Controls.Add(_inspectorSummary);
        tabs.TabPages.Add(active); tabs.TabPages.Add(inspector);
        host.Controls.Add(tabs);
        host.Controls.Add(EditorChrome.SectionHeader("Agent Telemetry & Inspector"));
        return host;
    }

    private Control BuildCodeSurface()
    {
        Panel panel = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        _codeStatus.Dock = DockStyle.Bottom; _codeStatus.Height = 24; _codeStatus.Padding = new Padding(8, 4, 8, 0);
        _codeStatus.BackColor = EditorChrome.Surface; _codeStatus.ForeColor = EditorChrome.Success; _codeStatus.Text = "Visual route and code are synchronized";
        panel.Controls.Add(_code); panel.Controls.Add(_codeStatus);
        return panel;
    }

    private void WireParameterEvents()
    {
        foreach (PathingLoopMode value in Enum.GetValues<PathingLoopMode>()) _loopMode.Items.Add(value);
        _loopMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _animationState.DropDownStyle = ComboBoxStyle.DropDown;
        EditorChrome.StyleField(_loopMode); EditorChrome.StyleField(_animationState);
        EditorChrome.StyleField(_followTarget);
        _loopMode.SelectedIndexChanged += (_, _) => { if (!_syncing && _loopMode.SelectedItem is PathingLoopMode mode) { _asset.Route.LoopMode = mode; CommitDocumentChange(); } };
        _speed.ValueChanged += (_, _) => SetNumber(_speed, value => _asset.Route.Speed = value);
        _stopping.ValueChanged += (_, _) => SetNumber(_stopping, value => _asset.Route.StoppingDistance = value);
        _wait.ValueChanged += (_, _) => SetNumber(_wait, value => _asset.Route.DefaultWaitSeconds = value);
        _wanderRadius.ValueChanged += (_, _) => SetNumber(_wanderRadius, value => _asset.Route.WanderRadius = value);
        _followOffset.ValueChanged += (_, _) => SetNumber(_followOffset, value => _asset.Route.FollowOffset = value);
        _previewAgents.ValueChanged += (_, _) => { if (!_syncing) { _asset.PreviewAgentCount = (int)_previewAgents.Value; CommitDocumentChange(); } };
        _followTarget.TextChanged += (_, _) => { if (!_syncing) { _asset.Route.FollowTarget = _followTarget.Text.Trim(); CommitDocumentChange(resetSimulation: false); } };
        _animationState.TextChanged += (_, _) => { if (!_syncing) { _asset.Route.AnimationState = _animationState.Text.Trim(); CommitDocumentChange(resetSimulation: false); } };
    }

    private void PopulateTargets()
    {
        if (_roomPicker is null || _objectPicker is null) return;
        _syncing = true;
        try
        {
            _roomPicker.Items.Clear(); _objectPicker.Items.Clear();
            _roomPicker.Items.Add(new AssetChoice("(select room)", string.Empty));
            _objectPicker.Items.Add(new AssetChoice("(default capsule)", string.Empty));
            foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.Room))
                _roomPicker.Items.Add(new AssetChoice(entry.DisplayName, entry.Reference));
            foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.GameObject))
                _objectPicker.Items.Add(new AssetChoice(entry.DisplayName, entry.Reference));
            SelectChoice(_roomPicker, _asset.TargetRoom); SelectChoice(_objectPicker, _asset.TargetObject);
        }
        finally { _syncing = false; }
    }

    private void OnRoomSelected()
    {
        if (_syncing || _roomPicker?.SelectedItem is not AssetChoice choice) return;
        _asset.TargetRoom = choice.Path; CommitDocumentChange(contextChanged: true);
    }

    private void OnObjectSelected()
    {
        if (_syncing || _objectPicker?.SelectedItem is not AssetChoice choice) return;
        _asset.TargetObject = choice.Path; CommitDocumentChange(contextChanged: true);
    }

    private void LoadContext()
    {
        _room = null; _navMesh = null;
        string roomPath = ResolveReference(_asset.TargetRoom, ResourceKind.Room);
        if (File.Exists(roomPath))
        {
            try
            {
                _room = RoomAssetLoader.Parse(roomPath);
                string nav = roomPath + ".navmesh";
                if (File.Exists(nav)) _navMesh = NavMeshData.Load(nav);
                _status.Text = _navMesh is null
                    ? $"{_room.Name}: no baked NavMesh sidecar; route editing remains available"
                    : $"{_room.Name}: {_navMesh.Walkable.Count(walkable => walkable):N0} walkable cells";
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or Newtonsoft.Json.JsonException)
            {
                _status.Text = "Room/NavMesh preview could not load: " + exception.Message;
            }
        }
        RefreshAnimationStates();
    }

    private void RefreshAnimationStates()
    {
        string selected = _asset.Route.AnimationState;
        _syncing = true;
        try
        {
            _animationState.Items.Clear();
            string objectPath = ResolveReference(_asset.TargetObject, ResourceKind.GameObject);
            if (File.Exists(objectPath))
            {
                JObject root = JObject.Parse(File.ReadAllText(objectPath));
                string model = (string?)root["model"] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(model) && root["components"] is JArray components)
                {
                    JObject? component = components.OfType<JObject>().FirstOrDefault(item =>
                        ((string?)item["type"] ?? string.Empty).Contains("Model", StringComparison.OrdinalIgnoreCase));
                    JObject? props = component?["props"] as JObject;
                    model = (string?)props?["Model"] ?? (string?)props?["Asset"] ?? (string?)props?["ModelAsset"] ?? string.Empty;
                }
                string modelPath = StudioModelResourceLoader.Resolve(ProjectRoot, model);
                if (File.Exists(modelPath))
                    foreach (string name in StudioModelResourceLoader.LoadReadOnly(modelPath).Animations.Select(animation => animation.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                        _animationState.Items.Add(name);
            }
        }
        catch (Exception) { /* Optional preview metadata never blocks route editing. */ }
        finally { _animationState.Text = selected; _syncing = false; }
    }

    private void SyncUiFromAsset(bool resetSimulation)
    {
        _syncing = true;
        try
        {
            foreach ((PathingRouteMode mode, Button button) in _modeButtons)
            {
                bool active = mode == _asset.Route.Mode;
                button.BackColor = active ? Color.FromArgb(51, 74, 85) : EditorChrome.Raised;
                button.FlatAppearance.BorderColor = active ? EditorChrome.Accent : EditorChrome.Border;
            }
            _loopMode.SelectedItem = _asset.Route.LoopMode;
            SetValue(_speed, _asset.Route.Speed); SetValue(_stopping, _asset.Route.StoppingDistance);
            SetValue(_wait, _asset.Route.DefaultWaitSeconds); SetValue(_wanderRadius, _asset.Route.WanderRadius);
            SetValue(_followOffset, _asset.Route.FollowOffset); SetValue(_previewAgents, _asset.PreviewAgentCount);
            _followTarget.Text = _asset.Route.FollowTarget; _animationState.Text = _asset.Route.AnimationState;
            _syncing = true; _code.CodeText = PathingCodeCodec.Encode(_asset);
            _codeStatus.Text = "Visual route and code are synchronized"; _codeStatus.ForeColor = EditorChrome.Success;
        }
        finally { _syncing = false; }
        LoadContext();
        _viewport.Bind(_asset, _room, _navMesh, _simulation);
        _timeline.Bind(_asset);
        if (resetSimulation) ResetSimulation();
        RefreshInspector();
    }

    private void CommitDocumentChange(bool contextChanged = false, bool resetSimulation = true)
    {
        if (_syncing) return;
        MarkDirty();
        if (contextChanged) { PopulateTargets(); LoadContext(); }
        SyncCodeFromVisual();
        _viewport.Bind(_asset, _room, _navMesh, _simulation);
        _timeline.Bind(_asset);
        if (resetSimulation) ResetSimulation();
        RefreshInspector();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncCodeFromVisual()
    {
        _syncing = true;
        try { _code.CodeText = PathingCodeCodec.Encode(_asset); }
        finally { _syncing = false; }
        _codeStatus.Text = "Visual route and code are synchronized"; _codeStatus.ForeColor = EditorChrome.Success;
    }

    private void ApplyCode()
    {
        if (_syncing) return;
        if (!PathingCodeCodec.TryDecode(_code.CodeText, _asset, out PathingAsset parsed, out string error))
        {
            _codeStatus.Text = error; _codeStatus.ForeColor = EditorChrome.Error; return;
        }
        _asset = parsed; MarkDirty(); PopulateTargets(); SyncUiFromAsset(resetSimulation: true);
    }

    private void AddMode(FlowLayoutPanel flow, PathingRouteMode mode, string glyph, string name, string tip)
    {
        Button button = new()
        {
            Text = $"{glyph}   {name}", TextAlign = ContentAlignment.MiddleLeft, Width = 192, Height = 38,
            BackColor = EditorChrome.Raised, ForeColor = EditorChrome.Text, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 0, 5), Cursor = Cursors.Hand, AccessibleDescription = tip,
        };
        button.FlatAppearance.BorderColor = EditorChrome.Border;
        button.Click += (_, _) => { if (!_syncing) { _asset.Route.Mode = mode; CommitDocumentChange(); } };
        ToolTip tooltip = new(); tooltip.SetToolTip(button, tip);
        _modeButtons[mode] = button; flow.Controls.Add(button);
    }

    private void AddWaypoint()
    {
        Vector3 position = _asset.Route.Waypoints.Count > 0 ? _asset.Route.Waypoints[^1].Position + new Vector3(2, 0, 2) : Vector3.Zero;
        position = Snap(position);
        _asset.Route.Waypoints.Add(new PathingWaypoint { Name = $"WP{_asset.Route.Waypoints.Count + 1}", Position = position });
        _selectedWaypoint = _asset.Route.Waypoints.Count - 1; CommitDocumentChange();
    }

    private void InsertCurvePoint()
    {
        int after = Math.Clamp(_selectedWaypoint, -1, _asset.Route.Waypoints.Count - 1);
        Vector3 position = after >= 0 ? _asset.Route.Waypoints[after].Position + new Vector3(1, 0, 1) : Vector3.Zero;
        PathingWaypoint point = new() { Name = "Curve", Position = Snap(position), Curve = true };
        _asset.Route.Waypoints.Insert(after + 1, point); RenameWaypoints(); _selectedWaypoint = after + 1; CommitDocumentChange();
    }

    private void DeleteWaypoint()
    {
        if (_selectedWaypoint < 0 || _selectedWaypoint >= _asset.Route.Waypoints.Count) return;
        _asset.Route.Waypoints.RemoveAt(_selectedWaypoint); RenameWaypoints();
        _selectedWaypoint = Math.Min(_selectedWaypoint, _asset.Route.Waypoints.Count - 1); CommitDocumentChange();
    }

    private void SnapWaypoint()
    {
        if (_selectedWaypoint < 0 || _selectedWaypoint >= _asset.Route.Waypoints.Count) return;
        _asset.Route.Waypoints[_selectedWaypoint].Position = Snap(_asset.Route.Waypoints[_selectedWaypoint].Position); CommitDocumentChange();
    }

    private void MoveWaypoint(int index, Vector3 position)
    {
        if (index < 0 || index >= _asset.Route.Waypoints.Count) return;
        _asset.Route.Waypoints[index].Position = position;
        MarkDirty(); SyncCodeFromVisual(); ResetSimulation(); RefreshInspector(); InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private Vector3 Snap(Vector3 position)
    {
        if (_navMesh is null) return new Vector3(MathF.Round(position.X), position.Y, MathF.Round(position.Z));
        int cell = _navMesh.Cell(position);
        if (cell >= 0) return _navMesh.Center(cell);
        float x = Math.Clamp(position.X, _navMesh.OriginX, _navMesh.OriginX + (_navMesh.Width - .5f) * _navMesh.CellSize);
        float z = Math.Clamp(position.Z, _navMesh.OriginZ, _navMesh.OriginZ + (_navMesh.Depth - .5f) * _navMesh.CellSize);
        cell = _navMesh.Cell(new Vector3(x, 0, z));
        return cell >= 0 ? _navMesh.Center(cell) : position;
    }

    private void RenameWaypoints()
    {
        for (int index = 0; index < _asset.Route.Waypoints.Count; index++)
            if (_asset.Route.Waypoints[index].Name.StartsWith("WP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(_asset.Route.Waypoints[index].Name, "Curve", StringComparison.OrdinalIgnoreCase))
                _asset.Route.Waypoints[index].Name = $"WP{index + 1}";
    }

    private void Play() { _playing = true; _paused = false; _frameTimer.Start(); RefreshTransport(); }
    private void Pause() { if (!_playing) return; _paused = !_paused; if (_paused) _frameTimer.Stop(); else _frameTimer.Start(); RefreshTransport(); }
    private void Stop() { _playing = false; _paused = false; _frameTimer.Stop(); ResetSimulation(); RefreshTransport(); }
    private void AdvanceSimulation(float dt, bool force = false)
    {
        if (!force && (!_playing || _paused)) return;
        float scale = (_timeScale?.Value ?? 10) / 10f * (_fastForward ? 4f : 1f);
        dt *= scale; _time += dt;
        _simulation.Step(_asset, _navMesh, dt);
        _viewport.SetTime(_time);
        _timeline.SetFrame(_time, AveragePreviewSpeed(), _simulation.CollisionWarning);
        RefreshRoster(); InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetSimulation()
    {
        _time = 0f; _simulation.Reset(_asset, _navMesh); _timeline.Reset(); RefreshRoster(); _viewport.SetTime(0f);
    }

    private void RefreshTransport()
    {
        if (_timeScaleValue is not null) _timeScaleValue.Text = $"{((_timeScale?.Value ?? 10) / 10f):0.0}×";
        if (_playButton is not null) _playButton.BackColor = _playing && !_paused ? EditorChrome.Success : EditorChrome.Accent;
        if (_pauseButton is not null) _pauseButton.BackColor = _paused ? EditorChrome.Warning : EditorChrome.Surface;
        if (_fastForwardButton is not null)
        {
            _fastForwardButton.Checked = _fastForward;
            _fastForwardButton.BackColor = _fastForward ? EditorChrome.Warning : EditorChrome.Surface;
        }
        _status.Text = _playing ? (_paused ? "Simulation paused" : $"Simulation running at {((_timeScale?.Value ?? 10) / 10f * (_fastForward ? 4 : 1)):0.0}×") : "Simulation stopped";
    }

    private void RefreshRoster()
    {
        _roster.Rows.Clear();
        foreach (PathingPreviewAgent agent in _simulation.Agents.Take(128))
            _roster.Rows.Add(
                agent.Name,
                Coordinates(agent.Position),
                Coordinates(agent.Destination),
                agent.Waypoint + 1,
                agent.DistanceRemaining.ToString("0.00", CultureInfo.InvariantCulture),
                agent.State);
        _crowdGraph.SetAgents(_simulation.Agents);
    }

    private void ScrubTo(float time)
    {
        bool resume = _playing && !_paused;
        _frameTimer.Stop();
        _simulation.Reset(_asset, _navMesh);
        _timeline.Reset();
        _time = 0f;
        int frames = Math.Clamp((int)MathF.Round(MathF.Max(0f, time) * 60f), 0, 60 * 60 * 10);
        for (int frame = 0; frame < frames; frame++)
        {
            _simulation.Step(_asset, _navMesh, 1f / 60f);
            _time += 1f / 60f;
            if ((frame & 3) == 0)
                _timeline.SetFrame(_time, AveragePreviewSpeed(), _simulation.CollisionWarning);
        }
        _timeline.SetFrame(_time, AveragePreviewSpeed(), _simulation.CollisionWarning);
        RefreshRoster(); RefreshInspector(); _viewport.SetTime(_time);
        if (resume) _frameTimer.Start();
    }

    private float AveragePreviewSpeed() => _simulation.Agents.Count == 0
        ? 0f : _simulation.Agents.Average(agent => agent.Velocity.Length());

    private static string Coordinates(Vector3 value) => string.Create(CultureInfo.InvariantCulture,
        $"{value.X:0.0}, {value.Y:0.0}, {value.Z:0.0}");

    private void RefreshDocumentTitle()
    {
        if (_documentTitle is not null)
            _documentTitle.Text = ResourceDisplayName.Format(ResourcePath) + (IsDirty ? "  •" : string.Empty);
    }

    private void RefreshInspector()
    {
        string point = _selectedWaypoint >= 0 && _selectedWaypoint < _asset.Route.Waypoints.Count
            ? $"Selected: {_asset.Route.Waypoints[_selectedWaypoint].Name}\nPosition: ({_asset.Route.Waypoints[_selectedWaypoint].X:0.##}, {_asset.Route.Waypoints[_selectedWaypoint].Y:0.##}, {_asset.Route.Waypoints[_selectedWaypoint].Z:0.##})\nWait: {_asset.Route.Waypoints[_selectedWaypoint].WaitSeconds:0.##}s\nCurve: {_asset.Route.Waypoints[_selectedWaypoint].Curve}"
            : "Select a waypoint pin to inspect it.";
        _inspectorSummary.Text = $"ROUTE\n{_asset.Name}\n\nMode: {_asset.Route.Mode}\nLoop: {_asset.Route.LoopMode}\nSpeed: {_asset.Route.Speed:0.##} m/s\nStopping: {_asset.Route.StoppingDistance:0.##} m\nAnimation: {(string.IsNullOrWhiteSpace(_asset.Route.AnimationState) ? "(unchanged)" : _asset.Route.AnimationState)}\n\nWAYPOINT\n{point}\n\nROOM CONTEXT\n{(_room?.Name ?? "No room selected")}\nNavMesh: {(_navMesh is null ? "Not baked" : $"{_navMesh.Count:N0} cells")}";
    }

    private void ApplyResponsiveLayout()
    {
        // Side docks stay readable; WinForms hides them only at genuinely narrow embedded widths.
        foreach (Control control in Controls)
            if (control.Dock is DockStyle.Left or DockStyle.Right) control.Visible = Width >= 850;
    }

    private PathingAsset LoadAsset()
    {
        try { if (File.Exists(ResourcePath)) return PathingAssetSerializer.Load(ResourcePath); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        { LoadWarning = exception.Message; }
        return new PathingAsset { Name = DisplayName(ResourcePath), Route = new PathingRoute { Waypoints =
        [
            new() { Name = "WP1", X = -4, Z = -3, Curve = true }, new() { Name = "WP2", X = 4, Z = -3, Curve = true },
            new() { Name = "WP3", X = 4, Z = 3, Curve = true }, new() { Name = "WP4", X = -4, Z = 3, Curve = true },
        ] } };
    }

    private string ResolveReference(string reference, ResourceKind kind)
    {
        return ProjectAssetIndex.ResolveReference(ProjectRoot, reference, kind);
    }

    private static string DisplayName(string path)
    {
        return ResourceDisplayName.Format(path);
    }

    private static Panel ChromePanel(string title, Control content)
    {
        Panel panel = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        panel.Controls.Add(content); panel.Controls.Add(EditorChrome.SectionHeader(title));
        return panel;
    }

    private static Label Heading(string text) => new()
    {
        Text = text, Width = 192, Height = 28, ForeColor = EditorChrome.Muted,
        Font = EditorChrome.SmallFont, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 0, 3),
    };

    private static Panel Field(string label, Control control)
    {
        Panel panel = new() { Width = 192, Height = 53, BackColor = EditorChrome.Surface, Margin = new Padding(0, 0, 0, 4) };
        Label caption = new() { Text = label, ForeColor = EditorChrome.Muted, Location = new Point(0, 0), Size = new Size(192, 20) };
        control.Location = new Point(0, 21); control.Size = new Size(192, 28); control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        panel.Controls.Add(control); panel.Controls.Add(caption); return panel;
    }

    private static Panel ActionRow(params (string Text, Action Click)[] actions)
    {
        Panel row = new() { Width = 192, Height = 36, Margin = new Padding(0, 0, 0, 4) };
        int width = (192 - (actions.Length - 1) * 5) / actions.Length;
        for (int i = 0; i < actions.Length; i++)
        {
            (string text, Action click) = actions[i];
            Button button = new() { Text = text, Location = new Point(i * (width + 5), 0), Size = new Size(width, 34), BackColor = EditorChrome.Raised, ForeColor = EditorChrome.Text, FlatStyle = FlatStyle.Flat };
            button.FlatAppearance.BorderColor = EditorChrome.Border; button.Click += (_, _) => click(); row.Controls.Add(button);
        }
        return row;
    }

    private static ToolStripLabel Caption(string text) => new(text) { ForeColor = EditorChrome.Muted, Margin = new Padding(4, 0, 3, 0) };
    private static ToolStripComboBox Picker(int width, Action changed)
    {
        ToolStripComboBox combo = new() { AutoSize = false, Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.SelectedIndexChanged += (_, _) => changed(); return combo;
    }
    private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal increment, int decimals) => new()
    {
        Minimum = min, Maximum = max, Value = value, Increment = increment, DecimalPlaces = decimals,
        BorderStyle = BorderStyle.FixedSingle, BackColor = EditorChrome.Canvas, ForeColor = EditorChrome.Text,
    };
    private static void SetValue(NumericUpDown input, float value)
    {
        decimal converted = float.IsFinite(value) ? (decimal)value : input.Minimum;
        input.Value = Math.Clamp(converted, input.Minimum, input.Maximum);
    }
    private void SetNumber(NumericUpDown input, Action<float> apply) { if (!_syncing) { apply((float)input.Value); CommitDocumentChange(); } }
    private static void SelectChoice(ToolStripComboBox combo, string path)
    {
        for (int index = 0; index < combo.Items.Count; index++)
            if (combo.Items[index] is AssetChoice choice && string.Equals(choice.Path, path, StringComparison.OrdinalIgnoreCase)) { combo.SelectedIndex = index; return; }
        combo.SelectedIndex = 0;
    }

    private static List<HighlightRule> BuildRules() =>
    [
        new(new Regex("\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled), Color.FromArgb(210, 180, 118)),
        new(new Regex(@"\b-?\d+(?:\.\d+)?\b", RegexOptions.Compiled), Color.FromArgb(181, 206, 168)),
        new(new Regex(@"\b(pathing|WaypointPatrol|NavMeshSearch|WanderRadius|FollowLeader|Loop|PingPong|Once|waypoint|wait|curve|true|false)\b", RegexOptions.Compiled), Color.FromArgb(86, 156, 214), true),
        new(new Regex(@"^[ \t]*(room|object|preview_agents|mode|loop|speed|stopping_distance|wait|wander_radius|follow_offset|follow_target|animation)(?=\s*:)", RegexOptions.Compiled | RegexOptions.Multiline), Color.FromArgb(78, 201, 176)),
        new(new Regex(@"//[^\r\n]*", RegexOptions.Compiled), Color.FromArgb(98, 151, 85)),
    ];
}
