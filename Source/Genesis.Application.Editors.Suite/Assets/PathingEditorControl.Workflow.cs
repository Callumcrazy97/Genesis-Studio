using System.Text;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class PathingEditorControl
{
    private EditorCommandBar? _commandBar;
    private Panel? _workspace;
    private FlowLayoutPanel? _gameGuide;
    private ThemedComboBox? _planeChoice;
    private ThemedComboBox? _routeChoice;
    private ThemedComboBox? _pointChoice;
    private readonly NumericUpDown _pointX = Number(-1_000_000, 1_000_000, 0, 1, 2);
    private readonly NumericUpDown _pointY = Number(-1_000_000, 1_000_000, 0, 1, 2);
    private readonly NumericUpDown _pointZ = Number(-1_000_000, 1_000_000, 0, 1, 2);
    private readonly NumericUpDown _pointWait = Number(0, 86_400, 0, .1m, 2);
    private readonly CheckBox _pointCurve = new() { Text = "Smooth curve", AccessibleDescription = "Bend the route smoothly through this point" };
    private readonly TextBox _pointName = new();
    private readonly List<Control> _advancedFields = [];
    private readonly List<Control> _pointFields = [];
    private bool _advanced;
    private bool _showGameGuide;
    private ToolStripButton? _quickButton;
    private ToolStripButton? _codeButton;
    private ToolStripButton? _gameButton;
    private string _savedPathingDocument = string.Empty;
    private Control? _speedField;
    private Control? _stoppingField;
    private Control? _wanderField;
    private Control? _followOffsetField;
    private Control? _followTargetField;

    public EditorCommandBar CommandBar => _commandBar!;

    private Control BuildPathingToolbar()
    {
        EditorCommandBar bar = _commandBar = EditorChrome.MakeToolbar();
        _quickButton = EditorChrome.ToolButton("Quick setup", "Edit the route and its points", () => SelectPathingSurface("Quick setup"), toggle: true);
        _codeButton = EditorChrome.ToolButton("Code", "Edit the same saved route as a typed definition", () => SelectPathingSurface(_showCode ? "Quick setup" : "Code"), toggle: true);
        _gameButton = EditorChrome.ToolButton("Use in game", "Create a saved moving Object and learn the gameplay calls", ShowPathingGameGuide, toggle: true);
        bar.Items.AddRange([_quickButton, _codeButton, _gameButton]);
        ToolStripDropDownButton options = new("Options") { ForeColor = EditorChrome.Text };
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Advanced route settings", "Other route modes and optional animation/crowd settings", () =>
        {
            if (!ApplyCode()) return;
            _advanced = !_advanced; SelectPathingSurface("Quick setup"); RefreshPathingQuickFields();
        }));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Agent telemetry", "Inspect preview agents and crowd neighbours", () =>
        {
            if (!ApplyCode()) return;
            _telemetryPreference = !(_telemetryDock?.Visible ?? false); SelectPathingSurface("Quick setup");
        }));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Speed timeline", "Scrub time and edit the speed multiplier curve", () =>
        {
            if (!ApplyCode()) return;
            _timelinePreference = _timelineSplit?.Panel2Collapsed ?? true; SelectPathingSurface("Quick setup");
        }));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Frame route", "Fit the authored points in the preview", _viewport.FrameContent));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Faster preview", "Toggle 4× preview speed", () => { _fastForward = !_fastForward; RefreshTransport(); }));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Test in Room", "Save and run the selected Room with navigation statistics (F3)", LaunchPathingDebug));
        bar.Items.Add(options);
        bar.Items.Add(EditorChrome.ToolButton("Save", "Save this route (Ctrl+S)", Save));
        bar.Items[^1].Alignment = ToolStripItemAlignment.Right;
        return bar;
    }

    private ToolStrip BuildPathingPreviewToolbar()
    {
        ToolStrip bar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Text, Name = "PathingPreviewToolbar" };
        _playButton = EditorChrome.ToolButton("Play", "Run or pause the route preview", () => { if (_playing) Pause(); else Play(); });
        bar.Items.Add(_playButton);
        bar.Items.Add(EditorChrome.ToolButton("Step", "Pause and advance one preview frame", () =>
        {
            _playing = true; _paused = true; _frameTimer.Stop(); AdvanceSimulation(1f / 60f, force: true); RefreshTransport();
        }));
        bar.Items.Add(EditorChrome.ToolButton("Restart", "Reset the preview to the start of the route", Stop));
        _timeScaleValue = Caption("1×"); bar.Items.Add(_timeScaleValue);
        return bar;
    }

    private Panel BuildPathingQuickDock()
    {
        Panel host = EditorChrome.SidePanel(300, DockStyle.Left);
        Panel scroll = new EditorScrollHost(EditorChrome.Surface) { Dock = DockStyle.Fill, Padding = new Padding(12), Name = "PathingQuickScroll" };
        FlowLayoutPanel flow = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, BackColor = EditorChrome.Surface, Name = "PathingQuickSetup", Padding = new Padding(0, 0, 0, 32) };
        _toolsFields = flow;
        flow.Controls.Add(WorkflowText("Quick setup", true));
        flow.Controls.Add(WorkflowText("Move an Object along a route. Choose a preview Object, edit or drag the points, then Play. Use in game creates an Object you can place in a Room."));
        _planeChoice = new ThemedComboBox { Name = "PathingPlaneChoice", DropDownStyle = ComboBoxStyle.DropDownList };
        _planeChoice.Items.AddRange(["2D sprites (XY)", "3D objects (XZ)"]);
        _planeChoice.SelectedIndexChanged += (_, _) => { if (!_syncing) SetDimension(_planeChoice.SelectedIndex == 0 ? PathingDimension.TwoD : PathingDimension.ThreeD); };
        flow.Controls.Add(Field("Dimension", _planeChoice));
        _objectPicker = Picker(OnObjectSelected); _objectPicker.Name = "PathingObjectChoice";
        _roomPicker = Picker(OnRoomSelected); _roomPicker.Name = "PathingRoomChoice";
        flow.Controls.Add(Field("Preview Object", _objectPicker));
        flow.Controls.Add(Field("Room context (optional)", _roomPicker));
        flow.Controls.Add(Field("Repeat", _loopMode));
        _speed.Name = "PathingMovementSpeed";
        flow.Controls.Add(_speedField = Field("Speed (pixels per second)", _speed));
        _pointChoice = new ThemedComboBox { Name = "PathingPointChoice", DropDownStyle = ComboBoxStyle.DropDownList };
        _pointChoice.SelectedIndexChanged += (_, _) => { if (!_syncing) SelectPathingPoint(_pointChoice.SelectedIndex); };
        flow.Controls.Add(Heading("ROUTE POINTS"));
        flow.Controls.Add(WorkflowText("Click a pin or select a point below. Drag pins in the preview; use these fields for exact placement. In 2D, positive Y goes down and Z is retained depth."));
        flow.Controls.Add(Field("Selected point", _pointChoice));
        flow.Controls.Add(ActionRow(("Add point", AddWaypoint), ("Delete point", DeleteWaypoint)));
        _pointName.Name = "PathingPointName"; _pointX.Name = "PathingPointX"; _pointY.Name = "PathingPointY";
        _pointZ.Name = "PathingPointZ"; _pointWait.Name = "PathingPointWait";
        foreach (Control field in new[] { Field("Point name", _pointName), Field("X", _pointX), Field("Y", _pointY),
            Field("Z / depth", _pointZ), Field("Wait at this point (seconds)", _pointWait) }) { flow.Controls.Add(field); _pointFields.Add(field); }
        EditorChrome.StyleField(_pointCurve); flow.Controls.Add(_pointCurve); _pointFields.Add(_pointCurve);
        _pointName.TextChanged += (_, _) => ChangeSelectedPoint(point => point.Name = _pointName.Text);
        _pointX.ValueChanged += (_, _) => ChangeSelectedPoint(point => point.X = (float)_pointX.Value);
        _pointY.ValueChanged += (_, _) => ChangeSelectedPoint(point => point.Y = (float)_pointY.Value);
        _pointZ.ValueChanged += (_, _) => ChangeSelectedPoint(point => point.Z = (float)_pointZ.Value);
        _pointWait.ValueChanged += (_, _) => ChangeSelectedPoint(point => point.WaitSeconds = (float)_pointWait.Value);
        _pointCurve.CheckedChanged += (_, _) => ChangeSelectedPoint(point => point.Curve = _pointCurve.Checked);
        AddAdvanced(Heading("ADVANCED ROUTE SETTINGS"));
        AddAdvanced(WorkflowText("Patrol follows authored points. Navigation search avoids a baked Room mesh; Wander and Follow also use it when available. These routes are movement routines, so keep competing movement code off the same Object."));
        _routeChoice = new ThemedComboBox { Name = "PathingRouteChoice", DropDownStyle = ComboBoxStyle.DropDownList };
        _routeChoice.Items.AddRange(Enum.GetValues<PathingRouteMode>().Cast<object>().ToArray());
        _routeChoice.Format += (_, args) => args.Value = args.ListItem switch { PathingRouteMode.WaypointPatrol => "Patrol along points", PathingRouteMode.NavMeshSearch => "Find a route between points", PathingRouteMode.WanderRadius => "Wander around a point", PathingRouteMode.FollowLeader => "Follow an instance", _ => args.ListItem };
        _routeChoice.FormattingEnabled = true;
        _routeChoice.SelectedIndexChanged += (_, _) => { if (!_syncing && _routeChoice.SelectedItem is PathingRouteMode mode) { _asset.Route.Mode = mode; CommitDocumentChange(); } };
        AddAdvanced(Field("Movement routine", _routeChoice));
        AddAdvanced(_stoppingField = Field("Arrival distance (pixels)", _stopping));
        AddAdvanced(Field("Minimum wait at every point (seconds)", _wait));
        AddAdvanced(_wanderField = Field("Wander radius", _wanderRadius));
        AddAdvanced(_followOffsetField = Field("Follow distance", _followOffset));
        AddAdvanced(_followTargetField = Field("Named instance to follow", _followTarget));
        AddAdvanced(Field("Image / Model animation state", _animationState));
        AddAdvanced(Field("Preview agent count", _previewAgents));
        AddAdvanced(ActionRow(("Insert curve point", InsertCurvePoint), ("Snap point", SnapWaypoint)));
        WireParameterEvents(); scroll.Controls.Add(flow); host.Controls.Add(scroll);
        scroll.SizeChanged += (_, _) => QueuePathingLayout();
        Load += (_, _) => ResetPathingQuickScroll();
        return host;

        void AddAdvanced(Control control) { flow.Controls.Add(control); _advancedFields.Add(control); }
    }

    private static Label WorkflowText(string text, bool heading = false) => new()
    {
        Text = text, ForeColor = heading ? EditorChrome.Text : EditorChrome.Muted,
        Font = heading ? EditorChrome.HeadingFont : EditorChrome.SmallFont,
        Tag = heading ? "WorkflowHeading" : "WorkflowText", Margin = new Padding(0, 0, 0, 8),
    };

    private void SelectPathingPoint(int index)
    {
        _selectedWaypoint = index;
        _journalState = _journalState with { Point = index };
        RefreshInspector(); RefreshPathingQuickFields();
    }

    private void ChangeSelectedPoint(Action<PathingWaypoint> change)
    {
        if (_syncing || _selectedWaypoint < 0 || _selectedWaypoint >= _asset.Route.Waypoints.Count) return;
        change(_asset.Route.Waypoints[_selectedWaypoint]); CommitDocumentChange();
    }

    private void RefreshPathingQuickFields()
    {
        if (_pointChoice is null) return;
        bool wasSyncing = _syncing; _syncing = true;
        try
        {
            _planeChoice!.SelectedIndex = _asset.Dimension == PathingDimension.TwoD ? 0 : 1;
            _routeChoice!.SelectedItem = _asset.Route.Mode;
            _pointChoice.Items.Clear();
            _pointChoice.Items.AddRange(_asset.Route.Waypoints.Select((point, index) => $"{index + 1}. {point.Name}").Cast<object>().ToArray());
            _selectedWaypoint = Math.Clamp(_selectedWaypoint, -1, _asset.Route.Waypoints.Count - 1);
            _pointChoice.SelectedIndex = _selectedWaypoint;
            bool selected = _selectedWaypoint >= 0;
            foreach (Control field in _pointFields) field.Enabled = selected;
            if (selected)
            {
                PathingWaypoint point = _asset.Route.Waypoints[_selectedWaypoint];
                _pointName.Text = point.Name; SetValue(_pointX, point.X); SetValue(_pointY, point.Y); SetValue(_pointZ, point.Z);
                SetValue(_pointWait, point.WaitSeconds); _pointCurve.Checked = point.Curve;
            }
            foreach (Control field in _advancedFields) field.Visible = _advanced;
            if (_wanderField is not null) _wanderField.Visible = _advanced && _asset.Route.Mode == PathingRouteMode.WanderRadius;
            if (_followOffsetField is not null) _followOffsetField.Visible = _advanced && _asset.Route.Mode == PathingRouteMode.FollowLeader;
            if (_followTargetField is not null) _followTargetField.Visible = _advanced && _asset.Route.Mode == PathingRouteMode.FollowLeader;
            bool twoD = _asset.Dimension == PathingDimension.TwoD;
            _speedField!.Controls.OfType<Label>().Single().Text = twoD ? "Speed (pixels per second)" : "Speed (metres per second)";
            _stoppingField!.Controls.OfType<Label>().Single().Text = twoD ? "Arrival distance (pixels)" : "Arrival distance (metres)";
            if (_quickButton is not null) _quickButton.Checked = !_showCode && !_showGameGuide;
            if (_codeButton is not null) _codeButton.Checked = _showCode;
            if (_gameButton is not null) _gameButton.Checked = _showGameGuide;
        }
        finally { _syncing = wasSyncing; }
        QueuePathingLayout();
    }

    private void SelectPathingSurface(string surface)
    {
        if (!ApplyCode()) return;
        _showCode = surface == "Code"; _showGameGuide = surface == "Use in game";
        if (surface == "Use in game") _pathingWorkflow?.SetCurrent("UseInGame");
        else if (surface == "Quick setup" && _pathingWorkflow?.CurrentStepId == "UseInGame") _pathingWorkflow.SetCurrent("Route");
        ApplyResponsiveLayout(); RefreshPathingQuickFields();
        if (surface == "Quick setup") ResetPathingQuickScroll();
    }

    private void ResetPathingQuickScroll()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() => { if (!IsDisposed && _toolsFields?.Parent is ScrollableControl scroll && scroll.Visible) scroll.AutoScrollPosition = Point.Empty; });
    }

    private void LaunchPathingDebug()
    {
        if (string.IsNullOrWhiteSpace(_asset.TargetRoom)) { _status.Text = "Choose a Room context in Quick setup before testing in Room."; return; }
        Save(); DebugRequested?.Invoke(this, new PathingDebugRequestedEventArgs(_asset.TargetRoom, ResourcePath));
        _status.Text = "Launching Room with navigation statistics…";
    }

    private void ShowPathingGameGuide()
    {
        if (!ApplyCode() || _workspace is null) return;
        if (_gameGuide is null)
        {
            _gameGuide = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, BackColor = EditorChrome.Surface, Padding = new Padding(12), Name = "PathingUseInGame" };
            _workspace.Controls.Add(_gameGuide);
        }
        foreach (Control control in _gameGuide.Controls.Cast<Control>().ToArray()) control.Dispose();
        Button back = new() { Text = "Back to Quick setup" }; EditorChrome.StyleField(back); back.Click += (_, _) => SelectPathingSurface("Quick setup");
        _gameGuide.Controls.Add(back);
        bool twoD = _asset.Dimension == PathingDimension.TwoD;
        _gameGuide.Controls.Add(WorkflowText(twoD ? "Use this route in a 2D game" : "Use this route in a 3D game", true));
        _gameGuide.Controls.Add(WorkflowText(twoD
            ? "1. Choose a sprite preview Object in Quick setup. Edit the points and speed, then Play. Points use Room coordinates in pixels, with positive Y down. The placed Object's depth is preserved."
            : "1. Choose a Model preview Object in Quick setup. Edit the points and speed, then Play. Points use Room coordinates in metres: X and Z move across the ground; Y sets height. Choose a Room context to see its saved terrain and props."));
        _gameGuide.Controls.Add(WorkflowText("Select an animation state under Options to play a saved Image clip or Model animation while moving."));
        _gameGuide.Controls.Add(WorkflowText("2. Create a moving Object", true));
        TextBox name = new() { Text = ResourceDisplayName.Format(ResourcePath) + " patrol", Name = "PathingObjectName" }; EditorChrome.StyleField(name);
        _gameGuide.Controls.Add(Field("Object name", name));
        Button create = new() { Text = "Create patrol Object", Name = "PathingCreateObject" }; EditorChrome.StyleField(create);
        Label result = WorkflowText(string.Empty); result.Name = "PathingCreateResult";
        create.Click += (_, _) =>
        {
            try { result.Text = "Created " + ResourceDisplayName.Format(CreatePathingObject(name.Text)) + ". Place it in a Room and Run."; }
            catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException) { result.Text = error.Message; }
            LayoutPathingFields(_gameGuide);
        };
        _gameGuide.Controls.Add(create); _gameGuide.Controls.Add(result);
        _gameGuide.Controls.Add(WorkflowText("3. Drag the new Object from Assets into a Room and Run", true));
        _gameGuide.Controls.Add(WorkflowText((twoD
            ? "The new Object reuses the preview Object's saved sprite. "
            : "The new Object reuses the preview Object's saved Model, scale, materials and animation settings. ")
            + "Its Create event starts this route. Patrol follows points directly and does not need a baked mesh. Navigation search uses a baked Room mesh; NavMeshBake() can create one. Follow targets name a Room instance. Save route edits to update an active routine; Image and Model saves update linked visuals."));
        _gameGuide.Controls.Add(WorkflowText("For an existing Object's Create event", true));
        TextBox example = new() { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Horizontal, WordWrap = false,
            Text = $"PathFollow({PgslString(ResourceNames.Name(ProjectRoot, ResourcePath))}, \"\");\r\n// Empty loop mode uses Repeat from the route.\r\n// PathStop(); stops this instance.", Name = "PathingGameplayExample" };
        EditorChrome.StyleField(example); _gameGuide.Controls.Add(example);
        _gameGuide.Controls.Add(WorkflowText("Preview Object and Room context are authoring choices. They do not attach the routine to an existing Object or place it in a Room. The Create button writes the actual gameplay link."));
        SelectPathingSurface("Use in game"); LayoutPathingFields(_gameGuide);
    }

    private static string PgslString(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    public string CreatePathingObject(string name)
    {
        string validName = ResourceNames.ValidateName(name);
        bool twoD = _asset.Dimension == PathingDimension.TwoD;
        string source = ResolveReference(_asset.TargetObject, ResourceKind.GameObject);
        if (!File.Exists(source)) throw new InvalidOperationException(twoD
            ? "Choose a preview Object with a saved sprite in Quick setup."
            : "Choose a preview Object with a saved Model in Quick setup.");
        JObject preview = JObject.Parse(File.ReadAllText(source));
        ObjectCompositionModel previewComposition = new(preview);
        string visualType = twoD ? "SpriteComponent" : "ModelRendererComponent";
        string visualProperty = twoD ? "Sprite" : "ModelAsset";
        string visual = (string?)previewComposition.Find(visualType)?["props"]?[visualProperty] ?? string.Empty;
        string visualPath = ProjectAssetIndex.ResolveReference(ProjectRoot, visual, twoD ? ResourceKind.Image : ResourceKind.Model);
        if (!File.Exists(visualPath)) throw new InvalidOperationException(twoD
            ? "The preview Object needs a saved sprite Image." : "The preview Object needs a saved Model.");
        if (_asset.Route.Waypoints.Count == 0) throw new InvalidOperationException("Add at least one route point before creating the patrol Object.");
        Save();
        JObject document = JObject.Parse(ResourceDefinitions.Get(ResourceKind.GameObject).DefaultContent);
        document["schemaVersion"] = 3; document["dimension"] = twoD ? "TwoD" : "ThreeD"; document["solid"] = false;
        ObjectCompositionModel composition = new(document);
        foreach (string type in twoD ? new[] { visualType } : new[] { visualType, "ModelAnimatorComponent", "ModelMorphComponent", "MaterialComponent", "ShaderComponent" })
        {
            if (previewComposition.Find(type) is not JObject component) continue;
            JObject copy = composition.Ensure(type);
            copy["props"] = component["props"]?.DeepClone() ?? new JObject();
            copy["enabled"] = component["enabled"]?.DeepClone() ?? true;
        }
        composition.SetAsset(visualType, ResourceNames.Name(ProjectRoot, visualPath));
        composition.SetProperty("ScriptComponent", "ScriptClass", validName);
        document["events"] = new JArray("Create");
        string invocation = $"PathFollow({PgslString(ResourceNames.Name(ProjectRoot, ResourcePath))}, \"\");";
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.GameObject), ResourceKind.GameObject, validName);
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        File.WriteAllText(path, ResourceReferenceRewriter.Normalize(ProjectRoot, path, document.ToString(Newtonsoft.Json.Formatting.Indented)), new UTF8Encoding(false));
        ObjectEventStore.Save(path, new Dictionary<string, string> { ["Create"] = invocation });
        if (!ObjectEventStore.Load(path).TryGetValue("Create", out string? saved) || saved != invocation)
            throw new IOException("The patrol Create event could not be saved: " + path);
        ResourceNames.Invalidate(ProjectRoot); RequestOpenLinkedResource(path); return path;
    }

    private static void LayoutPathingFields(FlowLayoutPanel flow)
    {
        flow.Parent?.PerformLayout();
        int width = Math.Max(100, flow.ClientSize.Width - flow.Padding.Horizontal - (flow.AutoScroll ? 22 : 4));
        foreach (Control field in flow.Controls)
        {
            field.Width = width;
            if (field is Label label)
            {
                label.Font = label.Tag as string == "WorkflowHeading" ? EditorChrome.HeadingFont : EditorChrome.SmallFont;
                label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 8;
            }
            else if (field is Button or CheckBox) { field.Font = EditorChrome.BaseFont; field.Height = field.Font.Height + 18; }
            else if (field is TableLayoutPanel row) { row.Font = EditorChrome.BaseFont; row.Height = row.Font.Height + 18; }
            else if (field is TextBox { Multiline: true }) { field.Font = EditorChrome.CodeFont; field.Height = field.Font.Height * 4 + 16; }
            else if (field.Tag as string == "PathingField")
            {
                Label caption = field.Controls.OfType<Label>().Single();
                Control input = field.Controls.Cast<Control>().Single(control => control is not Label);
                caption.Font = EditorChrome.SmallFont;
                caption.Size = new Size(width, TextRenderer.MeasureText(caption.Text, caption.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 4);
                input.Font = EditorChrome.BaseFont;
                int height = input is ComboBox combo ? Math.Max(combo.PreferredHeight, combo is ThemedComboBox themed ? themed.ItemHeight + 8 : 0) : input.Font.Height + 10;
                input.Bounds = new Rectangle(0, caption.Height + 3, width, height);
                field.Height = input.Bottom + 4;
            }
        }
        if (flow.AutoScroll)
            flow.AutoScrollMinSize = new Size(0, flow.Controls.Cast<Control>().Where(control => control.Visible).Select(control => control.Bottom - flow.AutoScrollPosition.Y + control.Margin.Bottom + flow.Padding.Bottom + 16).DefaultIfEmpty(0).Max());
    }
}
