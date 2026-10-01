using System.Drawing.Drawing2D;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Assets;
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
public sealed partial class PathingEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
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
    private ComboBox? _roomPicker;
    private ComboBox? _objectPicker;
    private ToolStripButton? _playButton;
    private ToolStripLabel? _timeScaleValue;
    private int _selectedWaypoint = -1;
    private bool _syncing;
    private bool _playing;
    private bool _paused;
    private bool _fastForward;
    private float _time;
    private Panel? _toolsDock;
    private Panel? _telemetryDock;
    private SplitContainer? _previewSplit;
    private SplitContainer? _timelineSplit;
    private bool _showCode;
    private bool _layingOut;
    private bool _pathingLayoutQueued;
    private bool _refreshingRoster;
    private bool? _telemetryPreference;
    private bool? _timelinePreference;
    private FlowLayoutPanel? _toolsFields;
    private readonly ToolStripDropDownButton _dimensionMenu = new("Plane");
    private PathingEditSnapshot _journalState = new(string.Empty, string.Empty, string.Empty, -1);
    private PathingEditSnapshot? _waypointDragBefore;

    public PathingEditorControl(string resourcePath, string projectRoot) : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        _viewport = new PathingViewportControl(projectRoot);
        _asset = LoadAsset();
        _selectedWaypoint = _asset.Route.Waypoints.Count > 0 ? 0 : -1;
        Controls.Add(BuildWorkspace());
        Controls.Add(BuildToolbar());
        if (_commandBar?.HistoryCommand is { } history) history.Visible = false;
        Controls.Add(_status);
        BuildPathingWorkflowBar(_commandBar!);

        _codeTimer = new System.Windows.Forms.Timer { Interval = 450 };
        _codeTimer.Tick += (_, _) => { _codeTimer.Stop(); ApplyCode(); };
        _code.SetRules(BuildRules());
        AssetCodeIntelligenceProvider.AttachPathing(_code);
        _code.DocumentUndoRequested = Undo; _code.DocumentRedoRequested = Redo;
        _code.TextChangedByUser += (_, _) =>
        {
            if (_syncing) return;
            _codeTimer.Stop(); _codeTimer.Start();
            MarkDirty();
            _codeStatus.Text = "Editing…";
            _codeStatus.ForeColor = EditorChrome.Warning;
        };
        _frameTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _frameTimer.Tick += (_, _) => AdvanceSimulation(1f / 60f);
        _viewport.WaypointSelected += index => SelectPathingPoint(index);
        _viewport.WaypointMoved += MoveWaypoint;
        _viewport.WaypointDragStarted += () => _waypointDragBefore = _journalState;
        _viewport.WaypointDragCompleted += () => { _waypointDragBefore = null; JournalDocumentChange(); };
        _viewport.WaypointDeleteRequested += index => { _selectedWaypoint = index; DeleteWaypoint(); };
        _timeline.TimeScrubbed += ScrubTo;
        _timeline.SpeedCurveChanged += () => CommitDocumentChange(resetSimulation: false);
        DirtyChanged += (_, _) => RefreshDocumentTitle();
        SizeChanged += (_, _) => { ApplyResponsiveLayout(); QueuePathingLayout(); };
        Disposed += (_, _) => { _frameTimer.Dispose(); _codeTimer.Dispose(); };

        PopulateTargets();
        SyncUiFromAsset(resetSimulation: true);
        _journalState = CapturePathingEdit();
        _savedPathingDocument = _journalState.Document;
        RefreshDocumentTitle();
        ApplyResponsiveLayout();
    }

    public event EventHandler? InspectorStateChanged;
    public event EventHandler<PathingDebugRequestedEventArgs>? DebugRequested;

    public CodeEditor Code => _code;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F3)
        {
            LaunchPathingDebug();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public override void Save()
    {
        if (!ApplyCode()) throw new InvalidDataException(_codeStatus.Text);
        Genesis.Application.Core.Projects.ResourceBackupService.BackupBeforeOverwrite(ResourcePath);
        ProjectAssetWriteRegistry.MarkLocalWrite(ResourcePath);
        PathingAssetSerializer.Save(ResourcePath, _asset);
        ResourceNames.Invalidate(ProjectRoot);
        _savedPathingDocument = PathingAssetSerializer.Serialize(_asset);
        AcceptSave();
        _status.Text = $"Saved {ResourceDisplayName.Format(ResourcePath)}";
    }

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues() =>
    [
        new("Pathing", "Pathing.Dimension", "Plane", _asset.Dimension.ToString(), Choices: Enum.GetNames<PathingDimension>()),
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
        if (!ApplyCode()) return false;
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        switch (propertyPath)
        {
            case "Pathing.Dimension" when Enum.TryParse(text, true, out PathingDimension dimension) && Enum.IsDefined(dimension):
                SetDimension(dimension); return _asset.Dimension == dimension;
            case "Pathing.Mode" when Enum.TryParse(text, true, out PathingRouteMode mode) && Enum.IsDefined(mode): _asset.Route.Mode = mode; break;
            case "Pathing.Loop" when Enum.TryParse(text, true, out PathingLoopMode loop) && Enum.IsDefined(loop): _asset.Route.LoopMode = loop; break;
            case "Pathing.Speed" when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) && float.IsFinite(speed) && speed is >= 0 and <= 10000: _asset.Route.Speed = speed; break;
            case "Pathing.StoppingDistance" when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float stopping) && float.IsFinite(stopping) && stopping is >= 0 and <= 10000: _asset.Route.StoppingDistance = stopping; break;
            case "Pathing.Wait" when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float wait) && float.IsFinite(wait) && wait is >= 0 and <= 86400: _asset.Route.DefaultWaitSeconds = wait; break;
            case "Pathing.Room": _asset.TargetRoom = text; break;
            case "Pathing.Object": _asset.TargetObject = text; break;
            default: return false;
        }
        CommitDocumentChange(contextChanged: propertyPath is "Pathing.Room" or "Pathing.Object");
        SyncUiFromAsset(resetSimulation: true);
        return true;
    }

    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        if (!string.IsNullOrWhiteSpace(_asset.TargetRoom) || !string.IsNullOrWhiteSpace(_asset.TargetObject))
        {
            LoadContext();
            _viewport.Bind(_asset, _room, _navMesh, _simulation);
            _viewport.RefreshContext();
            ResetSimulation();
        }
        base.OnAssetDependenciesChanged(changes);
    }

    private Control BuildToolbar() => BuildPathingToolbar();

    public PathingDimension Dimension => _asset.Dimension;

    public void SetDimension(PathingDimension dimension)
    {
        if (_asset.Dimension == dimension || !ApplyCode()) return;
        foreach (PathingWaypoint point in _asset.Route.Waypoints) (point.Y, point.Z) = (point.Z, point.Y);
        _asset.Dimension = dimension;
        CommitDocumentChange(resetSimulation: true);
        _viewport.FrameContent();
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
        Panel workspace = _workspace = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas, Padding = new Padding(1) };
        Panel left = _toolsDock = BuildLeftDock();
        Panel right = _telemetryDock = BuildRightDock();
        SplitContainer vertical = new()
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 4,
            BackColor = EditorChrome.Border, Size = new Size(900, 600), SplitterDistance = 450,
        };
        _timelineSplit = vertical;
        vertical.SizeChanged += (_, _) => LayoutPreview();
        SplitContainer center = new()
        {
            Dock = DockStyle.Fill, SplitterWidth = 4, BackColor = EditorChrome.Border,
            Size = new Size(900, 450), SplitterDistance = 520,
        };
        _previewSplit = center;
        center.SizeChanged += (_, _) => LayoutPreview();
        Panel preview = ChromePanel("PATH PREVIEW", _viewport);
        preview.Controls.Add(BuildPathingPreviewToolbar());
        center.Panel1.Controls.Add(preview);
        center.Panel2.Controls.Add(ChromePanel("PATH DEFINITION", BuildCodeSurface()));
        vertical.Panel1.Controls.Add(center);
        vertical.Panel2.Controls.Add(_timeline);
        workspace.Controls.Add(vertical);
        workspace.Controls.Add(right);
        workspace.Controls.Add(left);
        return workspace;
    }

    private Panel BuildLeftDock() => BuildPathingQuickDock();

    private Panel BuildRightDock()
    {
        Panel host = EditorChrome.SidePanel(244, DockStyle.Right);
        TabControl tabs = new EditorTabControl { Dock = DockStyle.Fill };
        EditorChrome.StyleTabs(tabs);
        TabPage active = new("Active") { BackColor = EditorChrome.Surface, Padding = new Padding(8) };
        TabPage inspector = new("Inspector") { BackColor = EditorChrome.Surface, Padding = new Padding(10), AutoScroll = true };
        _roster.Dock = DockStyle.Top; _roster.Height = 235; _roster.ReadOnly = true; _roster.AllowUserToAddRows = false;
        _roster.AllowUserToDeleteRows = false; _roster.AllowUserToResizeRows = false; _roster.RowHeadersVisible = false;
        _roster.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _roster.BackgroundColor = EditorChrome.Surface;
        _roster.BorderStyle = BorderStyle.None; _roster.GridColor = EditorChrome.Border; _roster.ForeColor = EditorChrome.Text;
        _roster.DefaultCellStyle.BackColor = EditorChrome.Surface; _roster.DefaultCellStyle.SelectionBackColor = EditorChrome.Accent;
        _roster.ColumnHeadersDefaultCellStyle.BackColor = EditorChrome.Raised; _roster.ColumnHeadersDefaultCellStyle.ForeColor = EditorChrome.Text;
        _roster.EnableHeadersVisualStyles = false; _roster.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _roster.Columns.Add("Agent", "Agent");
        _roster.Columns.Add("Position", "Position");
        _roster.Columns.Add("State", "State");
        _roster.SelectionChanged += (_, _) => { if (!_refreshingRoster) RefreshInspector(); };
        Label graphTitle = Heading("NEIGHBOURS"); graphTitle.Dock = DockStyle.Top;
        Panel graphCard = new() { Dock = DockStyle.Fill, Padding = new Padding(5), BackColor = EditorChrome.Canvas };
        graphCard.Controls.Add(_crowdGraph);
        active.Controls.Add(graphCard); active.Controls.Add(graphTitle); active.Controls.Add(_roster);

        _inspectorSummary.Dock = DockStyle.Top; _inspectorSummary.AutoSize = true;
        _inspectorSummary.ForeColor = EditorChrome.Text; _inspectorSummary.BackColor = EditorChrome.Surface;
        _inspectorSummary.Font = EditorChrome.BaseFont; _inspectorSummary.Padding = new Padding(6); _inspectorSummary.TextAlign = ContentAlignment.TopLeft;
        inspector.Controls.Add(_inspectorSummary);
        tabs.TabPages.Add(active); tabs.TabPages.Add(inspector);
        host.Controls.Add(tabs);
        host.Controls.Add(EditorChrome.SectionHeader("Agents"));
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
            _objectPicker.Items.Add(new AssetChoice("(agent marker)", string.Empty));
            foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.Room))
                _roomPicker.Items.Add(new AssetChoice(entry.DisplayName, entry.Reference));
            foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.GameObject))
                _objectPicker.Items.Add(new AssetChoice(entry.DisplayName, entry.Reference));
            SelectChoice(_roomPicker, _asset.TargetRoom, ResourceKind.Room); SelectChoice(_objectPicker, _asset.TargetObject, ResourceKind.GameObject);
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
                string sprite = (string?)root["sprite"] ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sprite))
                    foreach (string name in SpriteAssetLoader.Load(ProjectRoot, sprite).Tags.Select(tag => tag.Name))
                        _animationState.Items.Add(name);
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

    private void SyncUiFromAsset(bool resetSimulation, bool preserveSource = false)
    {
        _dimensionMenu.Text = _asset.Dimension == PathingDimension.TwoD ? "Plane: XY" : "Plane: XZ";
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
            _syncing = true;
            if (!preserveSource)
            {
                string source = _asset.AuthoringSource ?? string.Empty;
                _code.CodeText = PathingCodeCodec.TryDecode(source, _asset, out PathingAsset authored, out _)
                    && PathingCodeCodec.Encode(authored) == PathingCodeCodec.Encode(_asset) ? source : PathingCodeCodec.Encode(_asset);
            }
            _asset.AuthoringSource = _code.CodeText;
            _codeStatus.Text = "Visual route and code are synchronized"; _codeStatus.ForeColor = EditorChrome.Success;
        }
        finally { _syncing = false; }
        LoadContext();
        _viewport.Bind(_asset, _room, _navMesh, _simulation);
        _timeline.Bind(_asset);
        if (resetSimulation) ResetSimulation();
        RefreshInspector();
        RefreshPathingQuickFields();
    }

    private void CommitDocumentChange(bool contextChanged = false, bool resetSimulation = true)
    {
        if (_syncing) return;
        _dimensionMenu.Text = _asset.Dimension == PathingDimension.TwoD ? "Plane: XY" : "Plane: XZ";
        if (contextChanged) { PopulateTargets(); LoadContext(); }
        SyncCodeFromVisual();
        JournalDocumentChange();
        _viewport.Bind(_asset, _room, _navMesh, _simulation);
        _timeline.Bind(_asset);
        if (resetSimulation) ResetSimulation();
        RefreshInspector();
        RefreshPathingQuickFields();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncCodeFromVisual()
    {
        _syncing = true;
        try
        {
            string[] comments = _code.CodeText.Replace("\r", string.Empty).Split('\n').Where(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToArray();
            _code.CodeText = (comments.Length > 0 ? string.Join(Environment.NewLine, comments) + Environment.NewLine : string.Empty) + PathingCodeCodec.Encode(_asset);
            _asset.AuthoringSource = _code.CodeText;
        }
        finally { _syncing = false; }
        _codeStatus.Text = "Visual route and code are synchronized"; _codeStatus.ForeColor = EditorChrome.Success;
    }

    private bool ApplyCode()
    {
        if (_syncing) return true;
        _codeTimer.Stop();
        if (!PathingCodeCodec.TryDecode(_code.CodeText, _asset, out PathingAsset parsed, out string error))
        {
            _codeStatus.Text = "Definition error: " + error; _codeStatus.ForeColor = EditorChrome.Error;
            JournalDocumentChange(); MarkDirty();
            _showCode = true; _showGameGuide = false; ApplyResponsiveLayout(); RefreshPathingQuickFields(); return false;
        }
        parsed.AuthoringSource = _code.CodeText;
        bool changed = PathingAssetSerializer.Serialize(parsed) != PathingAssetSerializer.Serialize(_asset);
        _asset = parsed;
        _codeStatus.Text = "Visual route and code are synchronized"; _codeStatus.ForeColor = EditorChrome.Success;
        if (changed) { PopulateTargets(); SyncUiFromAsset(resetSimulation: true, preserveSource: true); }
        JournalDocumentChange();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private sealed record PathingEditSnapshot(string Document, string Source, string Error, int Point);

    private PathingEditSnapshot CapturePathingEdit() => new(PathingAssetSerializer.Serialize(_asset), _code.CodeText,
        _codeStatus.ForeColor == EditorChrome.Error ? _codeStatus.Text : string.Empty, _selectedWaypoint);

    private void JournalDocumentChange()
    {
        PathingEditSnapshot after = CapturePathingEdit(), before = _journalState;
        if (before.Document == after.Document && before.Source == after.Source && before.Error == after.Error) return;
        if (_waypointDragBefore is not null) { MarkDirty(); return; }
        _journalState = after;
        PushEdit("Edit pathing route", () => RestoreDocument(after), () => RestoreDocument(before), maximumEntries: 100);
        RefreshPathingDirtyState();
    }

    private void RestoreDocument(PathingEditSnapshot snapshot)
    {
        _codeTimer.Stop();
        _asset = System.Text.Json.JsonSerializer.Deserialize<PathingAsset>(snapshot.Document,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }) ?? throw new InvalidDataException("Pathing undo document is empty.");
        _selectedWaypoint = snapshot.Point; _journalState = snapshot;
        PopulateTargets(); SyncUiFromAsset(resetSimulation: true);
        _syncing = true;
        try { _code.CodeText = snapshot.Source; }
        finally { _syncing = false; }
        if (snapshot.Error.Length > 0)
        {
            _codeStatus.Text = snapshot.Error; _codeStatus.ForeColor = EditorChrome.Error;
            _showCode = true; _showGameGuide = false; ApplyResponsiveLayout();
        }
        RefreshPathingDirtyState(); RefreshPathingQuickFields();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshPathingDirtyState()
    {
        if (_savedPathingDocument == PathingAssetSerializer.Serialize(_asset) && _codeStatus.ForeColor != EditorChrome.Error) AcceptSave();
        else MarkDirty();
    }

    public override void Undo()
    {
        if (_codeTimer.Enabled || _code.CodeText != _journalState.Source) ApplyCode();
        base.Undo(); RefreshPathingDirtyState();
    }

    public override void Redo() { _codeTimer.Stop(); base.Redo(); RefreshPathingDirtyState(); }

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
        Vector3 offset = _asset.Dimension == PathingDimension.TwoD ? new Vector3(32, 32, 0) : new Vector3(2, 0, 2);
        Vector3 position = _asset.Route.Waypoints.Count > 0 ? _asset.Route.Waypoints[^1].Position + offset : Vector3.Zero;
        position = Snap(position);
        _asset.Route.Waypoints.Add(new PathingWaypoint { Name = $"WP{_asset.Route.Waypoints.Count + 1}", Position = position });
        _selectedWaypoint = _asset.Route.Waypoints.Count - 1; CommitDocumentChange();
        _viewport.FrameContent();
    }

    private void InsertCurvePoint()
    {
        int after = Math.Clamp(_selectedWaypoint, -1, _asset.Route.Waypoints.Count - 1);
        Vector3 offset = _asset.Dimension == PathingDimension.TwoD ? new Vector3(16, 16, 0) : new Vector3(1, 0, 1);
        Vector3 position = after >= 0 ? _asset.Route.Waypoints[after].Position + offset : Vector3.Zero;
        PathingWaypoint point = new() { Name = "Curve", Position = Snap(position), Curve = true };
        _asset.Route.Waypoints.Insert(after + 1, point); RenameWaypoints(); _selectedWaypoint = after + 1; CommitDocumentChange();
        _viewport.FrameContent();
    }

    private void DeleteWaypoint()
    {
        if (_selectedWaypoint < 0 || _selectedWaypoint >= _asset.Route.Waypoints.Count) return;
        _asset.Route.Waypoints.RemoveAt(_selectedWaypoint); RenameWaypoints();
        _selectedWaypoint = Math.Min(_selectedWaypoint, _asset.Route.Waypoints.Count - 1); CommitDocumentChange();
        _viewport.FrameContent();
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
        CommitDocumentChange();
    }

    private Vector3 Snap(Vector3 position)
    {
        if (_navMesh is null) return _asset.Dimension == PathingDimension.TwoD
            ? new Vector3(MathF.Round(position.X), MathF.Round(position.Y), position.Z)
            : new Vector3(MathF.Round(position.X), position.Y, MathF.Round(position.Z));
        Vector3 authored = position;
        position = _asset.NavigationPosition(position);
        int cell = _navMesh.Cell(position);
        if (cell >= 0) return _asset.WorldPosition(_navMesh.Center(cell), authored.Z);
        float x = Math.Clamp(position.X, _navMesh.OriginX, _navMesh.OriginX + (_navMesh.Width - .5f) * _navMesh.CellSize);
        float z = Math.Clamp(position.Z, _navMesh.OriginZ, _navMesh.OriginZ + (_navMesh.Depth - .5f) * _navMesh.CellSize);
        cell = _navMesh.Cell(new Vector3(x, 0, z));
        return cell >= 0 ? _asset.WorldPosition(_navMesh.Center(cell), authored.Z) : authored;
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
        float scale = _fastForward ? 4f : 1f;
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
        if (_timeScaleValue is not null) _timeScaleValue.Text = _fastForward ? "4×" : "1×";
        if (_playButton is not null) _playButton.Text = _playing && !_paused ? "Pause" : "Play";
        _status.Text = _playing ? (_paused ? "Preview paused" : $"Preview running at {(_fastForward ? 4 : 1)}×") : "Preview stopped";
    }

    private void RefreshRoster()
    {
        _refreshingRoster = true;
        try
        {
            while (_roster.Rows.Count > _simulation.Agents.Count) _roster.Rows.RemoveAt(_roster.Rows.Count - 1);
            for (int index = 0; index < _simulation.Agents.Count; index++)
            {
                if (index >= _roster.Rows.Count) _roster.Rows.Add();
                PathingPreviewAgent agent = _simulation.Agents[index];
                Vector3 world = _asset.WorldPosition(agent.Position);
                _roster.Rows[index].SetValues(agent.Name,
                    _asset.Dimension == PathingDimension.TwoD ? $"{world.X:0.0}, {world.Y:0.0}" : $"{world.X:0.0}, {world.Z:0.0}", agent.State);
            }
            _roster.Columns[1].HeaderText = _asset.Dimension == PathingDimension.TwoD ? "Position XY" : "Position XZ";
        }
        finally { _refreshingRoster = false; }
        _crowdGraph.SetAgents(_simulation.Agents);
        RefreshInspector();
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
        _commandBar?.RefreshDocumentState();
    }

    private void RefreshInspector()
    {
        string point = _selectedWaypoint >= 0 && _selectedWaypoint < _asset.Route.Waypoints.Count
            ? $"Selected: {_asset.Route.Waypoints[_selectedWaypoint].Name}\nPosition: ({_asset.Route.Waypoints[_selectedWaypoint].X:0.##}, {_asset.Route.Waypoints[_selectedWaypoint].Y:0.##}, {_asset.Route.Waypoints[_selectedWaypoint].Z:0.##})\nWait: {_asset.Route.Waypoints[_selectedWaypoint].WaitSeconds:0.##}s\nCurve: {_asset.Route.Waypoints[_selectedWaypoint].Curve}"
            : "Select a waypoint pin to inspect it.";
        string agentText = _roster.CurrentRow is { Index: var index } && index < _simulation.Agents.Count
            ? $"\n\nAGENT\n{_simulation.Agents[index].Name}\nPosition: {Coordinates(_asset.WorldPosition(_simulation.Agents[index].Position))}\nDestination: {Coordinates(_asset.WorldPosition(_simulation.Agents[index].Destination))}\nWaypoint: {_simulation.Agents[index].Waypoint + 1}\nRemaining: {_simulation.Agents[index].DistanceRemaining:0.00} units" : string.Empty;
        _inspectorSummary.Text = $"ROUTE\n{_asset.Name}\n\nPlane: {_asset.Dimension}\nMode: {_asset.Route.Mode}\nLoop: {_asset.Route.LoopMode}\nSpeed: {_asset.Route.SpeedAt(_time):0.##} units/s\nStopping: {_asset.Route.StoppingDistance:0.##} units\nAnimation: {(string.IsNullOrWhiteSpace(_asset.Route.AnimationState) ? "(unchanged)" : _asset.Route.AnimationState)}\n\nWAYPOINT\n{point}\n\nROOM CONTEXT\n{(_room?.Name ?? "No room selected")}\nNavMesh: {(_navMesh is null ? "Not baked" : $"{_navMesh.Count:N0} cells")}{agentText}";
    }

    private void ApplyResponsiveLayout()
    {
        bool visual = !_showCode && !_showGameGuide;
        if (_toolsDock is not null) _toolsDock.Visible = visual;
        if (_telemetryDock is not null) _telemetryDock.Visible = visual && (_telemetryPreference ?? false);
        if (_timelineSplit is not null) _timelineSplit.Panel2Collapsed = !visual || !(_timelinePreference ?? false);
        if (_timelineSplit is not null) _timelineSplit.Visible = !_showGameGuide;
        if (_gameGuide is not null) { _gameGuide.Visible = _showGameGuide; if (_showGameGuide) _gameGuide.BringToFront(); }
        LayoutPreview();
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged(); QueuePathingLayout();
    }

    private void QueuePathingLayout()
    {
        if (_pathingLayoutQueued || !IsHandleCreated || IsDisposed) return;
        _pathingLayoutQueued = true;
        BeginInvoke((Action)(() => { _pathingLayoutQueued = false; if (!IsDisposed) ApplyResponsiveLayout(); }));
    }

    private void LayoutPreview()
    {
        if (_previewSplit is null || _timelineSplit is null || _layingOut) return;
        _layingOut = true;
        try
        {
            float scale = Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
            if (_toolsDock is not null) _toolsDock.Width = (int)Math.Min(300 * scale, ClientSize.Width * .47f);
            if (_telemetryDock is not null) _telemetryDock.Width = (int)Math.Min(300 * scale, ClientSize.Width * .30f);
            _workspace?.PerformLayout();
            _roster.Font = EditorChrome.SmallFont;
            _roster.ColumnHeadersHeight = _roster.Font.Height + 14;
            _roster.RowTemplate.Height = _roster.Font.Height + 10;
            foreach (DataGridViewRow row in _roster.Rows) row.Height = _roster.RowTemplate.Height;
            _roster.Height = Math.Min((int)(235 * scale), Math.Max(_roster.ColumnHeadersHeight + _roster.RowTemplate.Height,
                (_roster.Parent?.ClientSize.Height ?? 500) / 2));
            _inspectorSummary.Font = EditorChrome.SmallFont;
            _inspectorSummary.MaximumSize = new Size(Math.Max(80, (_inspectorSummary.Parent?.ClientSize.Width ?? 300) - 32), 0);
            _codeStatus.Font = EditorChrome.SmallFont;
            _codeStatus.Height = TextRenderer.MeasureText(_codeStatus.Text, _codeStatus.Font, new Size(Math.Max(100, _codeStatus.Width - 16), int.MaxValue), TextFormatFlags.WordBreak).Height + 12;
            if (_toolsFields is not null) LayoutPathingFields(_toolsFields);
            if (_gameGuide is not null) LayoutPathingFields(_gameGuide);
            _previewSplit.Panel1Collapsed = false;
            _previewSplit.Panel2Collapsed = !_showCode;
            if (_showCode) _previewSplit.Panel1Collapsed = true;
            if (!_timelineSplit.Panel2Collapsed && _timelineSplit.Height > _timelineSplit.SplitterWidth)
                _timelineSplit.SplitterDistance = Math.Clamp(_timelineSplit.Height - (int)Math.Min(Math.Max(150 * scale, EditorChrome.SmallFont.Height * 7 + 50), _timelineSplit.Height * .48f),
                    0, _timelineSplit.Height - _timelineSplit.SplitterWidth);
            _timelineSplit.PerformLayout(); _previewSplit.PerformLayout();
        }
        finally { _layingOut = false; }
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
        Text = Genesis.Application.Core.UI.UiTokens.DisplayHeading(text), Width = 192, Height = 28, ForeColor = EditorChrome.Muted,
        Font = EditorChrome.SmallFont, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 6, 0, 3),
    };

    private static Panel Field(string label, Control control)
    {
        Panel panel = new() { Width = 192, Height = 53, BackColor = EditorChrome.Surface, Margin = new Padding(0, 0, 0, 4), Tag = "PathingField" };
        Label caption = new() { Text = label, ForeColor = EditorChrome.Muted, Location = new Point(0, 0), Size = new Size(192, 20) };
        control.Location = new Point(0, 21); control.Size = new Size(192, 28); control.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        panel.Controls.Add(control); panel.Controls.Add(caption); return panel;
    }

    private static Panel ActionRow(params (string Text, Action Click)[] actions)
    {
        TableLayoutPanel row = new() { Width = 192, Height = 36, Margin = new Padding(0, 0, 0, 4), ColumnCount = actions.Length, RowCount = 1 };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < actions.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / actions.Length));
            (string text, Action click) = actions[i];
            Button button = new() { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0, 0, i == actions.Length - 1 ? 0 : 5, 0), BackColor = EditorChrome.Raised, ForeColor = EditorChrome.Text, FlatStyle = FlatStyle.Flat };
            button.FlatAppearance.BorderColor = EditorChrome.Border; button.Click += (_, _) => click(); row.Controls.Add(button, i, 0);
        }
        return row;
    }

    private static ToolStripLabel Caption(string text) => new(text) { ForeColor = EditorChrome.Muted, Margin = new Padding(4, 0, 3, 0) };
    private static ComboBox Picker(Action changed)
    {
        ComboBox combo = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        EditorChrome.StyleField(combo);
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
    private void SelectChoice(ComboBox combo, string path, ResourceKind kind)
    {
        for (int index = 0; index < combo.Items.Count; index++)
            if (combo.Items[index] is AssetChoice choice && (string.Equals(choice.Path, path, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(choice.Path)
                    && string.Equals(ResolveReference(choice.Path, kind), ResolveReference(path, kind), StringComparison.OrdinalIgnoreCase))))
            { combo.SelectedIndex = index; return; }
        if (string.IsNullOrWhiteSpace(path)) combo.SelectedIndex = 0;
        else { combo.Items.Add(new AssetChoice("(missing) " + path, path)); combo.SelectedIndex = combo.Items.Count - 1; }
    }

    private static List<HighlightRule> BuildRules() =>
    [
        new(new Regex("\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled), Color.FromArgb(210, 180, 118)),
        new(new Regex(@"\b-?\d+(?:\.\d+)?\b", RegexOptions.Compiled), Color.FromArgb(181, 206, 168)),
        new(new Regex(@"\b(pathing|TwoD|ThreeD|WaypointPatrol|NavMeshSearch|WanderRadius|FollowLeader|Loop|PingPong|Once|waypoint|wait|curve|true|false)\b", RegexOptions.Compiled), Color.FromArgb(86, 156, 214), true),
        new(new Regex(@"^[ \t]*(dimension|room|object|preview_agents|mode|loop|speed|stopping_distance|wait|wander_radius|follow_offset|follow_target|animation)(?=\s*:)", RegexOptions.Compiled | RegexOptions.Multiline), Color.FromArgb(78, 201, 176)),
        new(new Regex(@"//[^\r\n]*", RegexOptions.Compiled), Color.FromArgb(98, 151, 85)),
    ];
}
