using System.Globalization;
using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Runtime.Scene;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Objects;

public enum ObjectWorkspaceMode { Graph, Code, Split }

public sealed partial class ObjectEditorControl
{
    private SplitContainer _authoringSplit = null!;
    private readonly Button _splitModeButton = new();
    private ObjectCompositionPreviewControl _runtimePreview = null!;
    private readonly DataGridView _variableWatch = new();
    private readonly Label _runtimeStatus = new();
    private readonly System.Windows.Forms.Timer _watchTimer = new() { Interval = 100 };
    private DateTime _previewChange;
    private bool _previewPending;
    private bool _liveStarted;
    private bool _sandboxNeedsReload;
    private string? _runtimeAuthoringFingerprint;
    private readonly Dictionary<string, CheckBox> _identityFlags = [];
    private string? _thumbnailAsset;
    private readonly Label _identityModelLabel = new() { Dock = DockStyle.Fill, ForeColor = EditorChrome.Text, TextAlign = ContentAlignment.MiddleLeft };
    private IReadOnlyDictionary<string, object> _watchedValues = new Dictionary<string, object>();
    private Panel? _objectPropertiesPanel;
    private SplitContainer? _objectPreviewPanel;
    private SplitContainer? _objectPreviewDetails;
    private Panel? _identityScroll;
    private TableLayoutPanel? _identityStack;
    private bool? _propertiesPanelChoice;
    private bool? _previewPanelChoice;
    private bool _applyingObjectLayout;
    private enum SandboxInspectorSource { Authored, Diagnostic, Embedded }
    private SandboxInspectorSource _sandboxInspectorSource;
    public ObjectWorkspaceMode WorkspaceMode { get; private set; }
    public ObjectCompositionPreviewControl RuntimePreview => _runtimePreview;

    public void SetWorkspaceMode(ObjectWorkspaceMode mode)
    {
        _showObjectGameGuide = false;
        WorkspaceMode = mode;
        _authoringSplit.SuspendLayout();
        _authoringSplit.Panel1Collapsed = false; _authoringSplit.Panel2Collapsed = false;
        _visualActions.Visible = mode != ObjectWorkspaceMode.Code; _code.Visible = mode != ObjectWorkspaceMode.Graph;
        _authoringSplit.Panel1Collapsed = mode == ObjectWorkspaceMode.Code;
        _authoringSplit.Panel2Collapsed = mode == ObjectWorkspaceMode.Graph;
        if (mode == ObjectWorkspaceMode.Split && _authoringSplit.Height > 300)
            _authoringSplit.SplitterDistance = (int)(_authoringSplit.Height * .58);
        _authoringSplit.ResumeLayout(); SyncAuthoringModeButtons();
    }

    private Panel BuildDesignModeBar()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(8, 7, 4, 7),
            ColumnCount = 3, RowCount = 1, BackColor = EditorChrome.Surface };
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ConfigureModeButton(_actionsModeButton, "Graph", ShowVisualActions);
        ConfigureModeButton(_codeModeButton, "Code", () => ShowCodeEditor());
        ConfigureModeButton(_splitModeButton, "Split", () => SetWorkspaceMode(ObjectWorkspaceMode.Split));
        foreach (var button in new[] { _actionsModeButton, _codeModeButton, _splitModeButton })
        {
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            button.Dock = DockStyle.Fill;
            bar.Controls.Add(button);
        }
        return bar;
    }

    private Panel BuildDesignIdentity()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 414, BackColor = Color.FromArgb(22, 27, 34), Padding = new Padding(12) };
        var stack = _identityStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9 };
        foreach (int height in new[] { 30, 126, 30, 28, 55, 30, 30, 30, 28 }) stack.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var choose = new Button { Text = "Choose Sprite / Model…", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
        EditorChrome.StyleField(choose);
        choose.Click += (_, _) =>
        {
            // Own the menu until its owner is disposed; opening a popup is asynchronous.
            var persistent = new ContextMenuStrip();
            persistent.Items.Add("Sprite / Image…", null, (_, _) => PickIdentityAsset(ResourceKind.Image));
            persistent.Items.Add("3D Model…", null, (_, _) => PickIdentityAsset(ResourceKind.Model));
            choose.ContextMenuStrip?.Dispose(); choose.ContextMenuStrip = persistent;
            persistent.Show(choose, new Point(0, choose.Height));
        };
        _spritePreview.Dock = DockStyle.Fill;
        _spriteCombo.Dock = DockStyle.Fill; _spriteCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _spriteCombo.Enabled = false;
        EditorChrome.StyleField(_spriteCombo);
        _spriteCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing) return;
            int index = _spriteCombo.SelectedIndex;
            UseImageVisual(index > 0 && index <= _spriteChoices.Count ? _spriteChoices[index - 1] : "");
        };
        var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        foreach (var (name, label, fallback) in new[] { ("visible", "Visible", true), ("solid", "Solid", false), ("persistent", "Persistent", false), ("dimension", "3D", false) })
        {
            var field = new CheckBox { Text = label, Width = 106, Height = 24, ForeColor = EditorChrome.Text,
                Checked = name == "dimension" ? (string?)_document[name] == "ThreeD" : (bool?)_document[name]
                    ?? (name == "solid" ? (bool?)_composition.Find("PhysicsComponent")?["props"]?["Solid"] : null) ?? fallback };
            field.CheckedChanged += (_, _) =>
            {
                if (_syncing) return;
                _document[name] = name == "dimension" ? JToken.FromObject(field.Checked ? "ThreeD" : "TwoD") : JToken.FromObject(field.Checked);
                if (name == "solid") _composition.SetProperty("PhysicsComponent", "Solid", field.Checked);
                if (name == "dimension") { _thumbnailAsset = null; SyncIdentityAssetLabel(); RefreshSpritePreview(); }
                MarkDirty();
            };
            _identityFlags[name] = field;
            flags.Controls.Add(field);
        }
        Control Row(string label, Control field)
        {
            var row = new Panel { Dock = DockStyle.Fill };
            row.Controls.Add(field); field.Dock = DockStyle.Fill;
            row.Controls.Add(new Label { Text = label, Dock = DockStyle.Left, AutoSize = true, Padding = new Padding(0, 0, 8, 0), ForeColor = EditorChrome.Muted, TextAlign = ContentAlignment.MiddleLeft });
            EditorChrome.StyleField(field); return row;
        }
        _depthInput.Minimum = -100000; _depthInput.Maximum = 100000;
        _depthInput.ValueChanged += (_, _) => { if (_syncing) return; _document["depth"] = _depthInput.Value; _composition.SetProperty("SpriteComponent", "Depth", _depthInput.Value); MarkDirty(); };
        _parentCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _parentCombo.Enabled = false;
        _parentCombo.SelectedIndexChanged += (_, _) => { if (_syncing) return; int i = _parentCombo.SelectedIndex;
            JToken? previous = _document["parent"]?.DeepClone();
            _document["parent"] = i > 0 && i <= _parentChoices.Count ? _parentChoices[i - 1] : "";
            try { ObjectDefinitionResolver.Load(ProjectRoot, ResourcePath, _document, _events); }
            catch (IOException exception) { _document["parent"] = previous; SyncFromDocument(); UpdateStatus(exception.Message); return; }
            LoadInheritedEvents(); RebuildEventTree(); if (_activeEvent is not null) SelectEvent(_activeEvent); MarkDirty(); };
        SyncIdentityAssetLabel();
        stack.Controls.Add(choose, 0, 0); stack.Controls.Add(_spritePreview, 0, 1); stack.Controls.Add(_spriteCombo, 0, 2);
        stack.Controls.Add(_identityModelLabel, 0, 3); stack.Controls.Add(flags, 0, 4); stack.Controls.Add(BuildVisualScaleRow(), 0, 5);
        stack.Controls.Add(Row("Depth", _depthInput), 0, 6);
        Panel parentRow = (Panel)Row("Parent", _parentCombo);
        Button parentPicker = new() { Dock = DockStyle.Right, Text = "…", Width = 34 };
        EditorChrome.StyleField(parentPicker);
        parentPicker.Click += (_, _) =>
        {
            ProjectAssetEntry? picked = AssetPickerService.PickAsset(
                new AssetPickerRequest(ProjectRoot, ResourceKind.GameObject,
                    Convert.ToString(_document["parent"]), "Choose Parent Object", AllowNone: true), FindForm());
            if (picked is null) return;
            _document["parent"] = picked.Reference;
            LoadInheritedEvents();
            RebuildEventTree();
            MarkDirty();
            SyncFromDocument();
        };
        parentRow.Controls.Add(parentPicker);
        parentPicker.BringToFront();
        stack.Controls.Add(parentRow, 0, 7);
        stack.Controls.Add(new Label { Text = "EVENTS", ForeColor = EditorChrome.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 8);
        panel.Controls.Add(stack); return panel;
    }

    private void SyncIdentityAssetLabel()
    {
        _eventList.IsThreeD = IsThreeD; _eventList.Invalidate();
        _identityModelLabel.Visible = true; _spriteCombo.Visible = true;
        _identityModelLabel.Text = !IsThreeD ? "2D Image · original pixel size" : InitialModelBinding is { Length: > 0 } model
            ? "Model · " + ResourceAssociates.GetStem(model) + " (Create)" : "3D Cube · assigned image";
    }

    private void PickIdentityAsset(ResourceKind kind)
    {
        var picked = AssetPickerService.PickAsset(new AssetPickerRequest(ProjectRoot, kind), FindForm());
        if (picked is null) return;
        if (kind == ResourceKind.Model) { SetVisualDimension(true); SetVisualModel(picked.Reference); }
        else UseImageVisual(picked.Reference);
    }

    private Control BuildDesignRightPanel()
    {
        var right = _objectPreviewPanel = new SplitContainer { Dock = DockStyle.Right, Width = 326, Orientation = Orientation.Horizontal,
            BackColor = EditorChrome.Border, SplitterWidth = 5, FixedPanel = FixedPanel.Panel1 };
        _runtimePreview = new ObjectCompositionPreviewControl(ProjectRoot, compact: true) { Playing = false };
        var sandbox = new Panel { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        var bar = EditorChrome.MakeToolbar(); bar.Items.Add(EditorChrome.ToolButton("Run (F5)", "Start or resume the live Object", RunLiveSandbox));
        bar.Items.Add(EditorChrome.ToolButton("Pause", "Pause simulation", () => _runtimePreview.Playing = false));
        bar.Items.Add(EditorChrome.ToolButton("Reset", "Reset to the authored Object", ResetLiveSandbox));
        bar.Items.Add(EditorChrome.ToolButton("Frame", "Fit the complete asset in the preview", () => _runtimePreview.FrameAsset(VisualPreviewPrefab(_document), force: true)));
        _runtimeStatus.Dock = DockStyle.Bottom; _runtimeStatus.Height = 25; _runtimeStatus.ForeColor = EditorChrome.Muted; _runtimeStatus.Text = "Ready · 60 Hz simulation";
        sandbox.Controls.Add(_runtimePreview); sandbox.Controls.Add(_runtimeStatus); sandbox.Controls.Add(bar);
        sandbox.Controls.Add(DesignHeading("Object preview")); right.Panel1.Controls.Add(sandbox);
        var lower = _objectPreviewDetails = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, BackColor = EditorChrome.Border };
        var watch = new Panel { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        _variableWatch.Dock = DockStyle.Fill; _variableWatch.BackgroundColor = EditorChrome.Surface; _variableWatch.BorderStyle = BorderStyle.None;
        _variableWatch.AllowUserToAddRows = false; _variableWatch.AllowUserToDeleteRows = false; _variableWatch.RowHeadersVisible = false;
        _variableWatch.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; _variableWatch.EnableHeadersVisualStyles = false;
        _variableWatch.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
        _variableWatch.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _variableWatch.ColumnHeadersDefaultCellStyle.BackColor = EditorChrome.Raised; _variableWatch.ColumnHeadersDefaultCellStyle.ForeColor = EditorChrome.Text;
        _variableWatch.DefaultCellStyle.BackColor = EditorChrome.Surface; _variableWatch.DefaultCellStyle.ForeColor = EditorChrome.Text; _variableWatch.GridColor = EditorChrome.Border;
        _variableWatch.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(_variableWatch);
        _variableWatch.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _variableWatch.Columns.Add("Variable", "Variable"); _variableWatch.Columns.Add("Value", "Value"); _variableWatch.Columns.Add("Type", "Type");
        _variableWatch.Columns[0].ReadOnly = true; _variableWatch.Columns[2].ReadOnly = true;
        _variableWatch.CellEndEdit += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            string name = _variableWatch.Rows[e.RowIndex].Cells[0].Value?.ToString() ?? "";
            if (!_watchedValues.TryGetValue(name, out var previous)) return;
            try
            {
                object? input = _variableWatch.Rows[e.RowIndex].Cells[1].Value;
                object value;
                if (previous is Vector3)
                {
                    var parts = (input?.ToString() ?? "").Trim('<', '>').Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length != 3) throw new FormatException();
                    value = new Vector3(float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture), float.Parse(parts[2], CultureInfo.InvariantCulture));
                }
                else value = Convert.ChangeType(input, previous.GetType(), CultureInfo.InvariantCulture) ?? previous;
                _runtimePreview.LiveBehavior?.TrySetLiveValue(name, value);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { _runtimeStatus.Text = "Enter a valid " + previous.GetType().Name; }
            RefreshLiveWatch();
        };
        watch.Controls.Add(_variableWatch); watch.Controls.Add(DesignHeading("Live Variables Watch")); lower.Panel1.Controls.Add(watch);
        lower.Panel2.Controls.Add(_visualActions.TakeActionToolbox()); right.Panel2.Controls.Add(lower);
        right.SizeChanged += (_, _) => { if (right.Height > 540) right.SplitterDistance = (int)(right.Height * .40); };
        lower.SizeChanged += (_, _) => { if (lower.Height > 230) lower.SplitterDistance = (int)(lower.Height * .38); };
        return right;
    }

    private static Label DesignHeading(string text) => new() { Dock = DockStyle.Top, Height = 32, Text = text, Padding = new Padding(10, 7, 2, 0), ForeColor = EditorChrome.Text, BackColor = Color.FromArgb(22, 27, 34) };

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyObjectLayout();
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged();
        ApplyObjectLayout();
    }

    public override void ApplyInterfaceLayout() => ApplyObjectLayout();

    private void ApplyObjectLayout()
    {
        if (_applyingObjectLayout || _objectPropertiesPanel is null || _objectPreviewPanel is null || _identityScroll is null) return;
        _applyingObjectLayout = true;
        try
        {
            float scale = DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f;
            _objectPropertiesPanel.Width = (int)Math.Ceiling(262 * scale);
            foreach (CheckBox flag in _identityFlags.Values)
            {
                flag.Width = (int)Math.Ceiling(106 * scale);
                flag.Height = (int)Math.Ceiling(24 * scale);
            }
            _objectPreviewPanel.Width = Math.Min((int)Math.Ceiling(326 * scale), Math.Max(240, (int)(ClientSize.Width * .40f)));
            _objectPropertiesPanel.Visible = ShouldShowObjectProperties;
            _objectPreviewPanel.Visible = _previewPanelChoice ?? LogicalClientWidth >= 1000;
            bool compactPreview = _objectPreviewPanel.ClientSize.Height / scale < 500;
            if (_objectPreviewDetails is not null) _objectPreviewDetails.Panel2Collapsed = compactPreview;
            if (_objectPreviewPanel.ClientSize.Height > 250)
                _objectPreviewPanel.SplitterDistance = Math.Clamp((int)(_objectPreviewPanel.ClientSize.Height * (compactPreview ? .65f : .50f)), 140,
                    _objectPreviewPanel.ClientSize.Height - _objectPreviewPanel.SplitterWidth - 100);
            int fixedHeight = _objectPropertiesPanel.Controls.Cast<Control>()
                .Where(control => control != _identityScroll && control.Visible && control.Dock is DockStyle.Top or DockStyle.Bottom)
                .Sum(control => control.Height);
            int identityBudget = Math.Max(40, _objectPropertiesPanel.ClientSize.Height - fixedHeight - (int)Math.Ceiling(80 * scale));
            _identityScroll.Height = Math.Min(identityBudget, Math.Min((int)(414 * scale),
                Math.Max(120, (int)(_objectPropertiesPanel.ClientSize.Height * .55f))));
            if (_identityStack is not null)
            {
                int[] rows = [30, 126, 30, 28, 55, 30, 30, 30, 28];
                for (int row = 0; row < rows.Length; row++) _identityStack.RowStyles[row].Height = (int)Math.Ceiling(rows[row] * scale);
                _identityStack.RowStyles[4].Height = Math.Max(_identityStack.RowStyles[4].Height,
                    _identityFlags.Values.Max(flag => flag.Height + flag.Margin.Vertical) * 2);
                _identityStack.RowStyles[1].Height = Math.Max(56 * DeviceDpi / 96f,
                    Math.Min(126 * scale, _identityScroll.Height - 30 * scale - 36 * DeviceDpi / 96f));
                _identityStack.Parent!.Height = (int)Math.Ceiling(_identityStack.RowStyles.Cast<RowStyle>().Sum(row => row.Height)) + _identityStack.Parent.Padding.Vertical;
            }
        }
        finally { _applyingObjectLayout = false; }
        LayoutObjectWorkflow();
    }

    private void InitializeDesignPreview()
    {
        DirtyChanged += (_, _) => { _previewPending = true; _sandboxNeedsReload = true; _previewChange = DateTime.UtcNow; };
        void StartPreview()
        {
            if (IsDisposed || _watchTimer.Enabled) return;
            if (_sandboxInspectorSource == SandboxInspectorSource.Authored && !_liveStarted) ResetLiveSandbox();
            _watchTimer.Start();
        }
        HandleCreated += (_, _) => BeginInvoke(StartPreview);
        // Binding native fields can create our handle before this final initialization runs.
        if (IsHandleCreated) BeginInvoke(StartPreview);
        _watchTimer.Tick += (_, _) =>
        {
            SyncIdentityAssetLabel();
            string visualKey = IsThreeD + ":" + InitialModelBinding + ":" + (string?)_document["sprite"];
            if (_identityVisualKey != visualKey) { _identityVisualKey = visualKey; _thumbnailAsset = null; RefreshSpritePreview(); }
            if (_previewPending && DateTime.UtcNow - _previewChange > TimeSpan.FromMilliseconds(600))
            {
                _previewPending = false;
                bool running = _runtimePreview.Playing;
                if (running) RunLiveSandbox();
                else if (!_liveStarted) ReloadStaticPreview();
            }
            if (IsThreeD && _runtimePreview.IsHandleCreated
                && InitialModelBinding is { Length: > 0 } model && _thumbnailAsset != model)
            {
                using var frame = _runtimePreview.CaptureFrame(1);
                if (frame is not null)
                {
                    _spritePreview.SetFrame(frame, ResourceAssociates.GetStem(model) + " · 3D Model"); _thumbnailAsset = model;
                    if (_runtimePreview.ModelDimensions is { } size)
                        _spritePreview.AccessibleDescription = $"Model dimensions: {size.X:0.##} × {size.Y:0.##} × {size.Z:0.##}";
                }
            }
            RefreshLiveWatch();
        };
        Disposed += (_, _) => _watchTimer.Dispose();
    }

    public void RunLiveSandbox()
    {
        _showObjectGameGuide = false;
        _previewPanelChoice = true;
        ApplyObjectLayout();
        _previewPending = false;
        // Compare the current source on explicit resume as well as on live edit notifications.
        if (!_liveStarted || _sandboxNeedsReload || !_runtimePreview.Playing)
        {
            try
            {
                var definition = ObjectDefinitionResolver.Load(ProjectRoot, ResourcePath, _document, _events);
                string fingerprint = definition.Prefab.ToString(Newtonsoft.Json.Formatting.None) + string.Join("\n", definition.Events.OrderBy(pair => pair.Key)
                    .Select(pair => pair.Key + ":" + new ObjectCodeProjection(pair.Value).Text.TrimEnd()));
                if (!_liveStarted || fingerprint != _runtimeAuthoringFingerprint)
                {
                    _runtimePreview.EventSources = definition.Events;
                    _runtimePreview.Reload(definition.Prefab); _runtimeAuthoringFingerprint = fingerprint;
                }
                _liveStarted = true; _sandboxNeedsReload = false;
            }
            catch (IOException exception) { _runtimeStatus.Text = exception.Message; return; }
        }
        _runtimePreview.Playing = true;
        _sandboxInspectorSource = SandboxInspectorSource.Embedded;
        RefreshLiveWatch();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetLiveSandbox()
    {
        _previewPending = false;
        _liveStarted = false; _runtimePreview.Playing = false; _runtimePreview.EventSources = null; ReloadStaticPreview(); RefreshLiveWatch();
        _sandboxInspectorSource = SandboxInspectorSource.Authored;
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadStaticPreview()
    {
        try { _runtimePreview.Reload(VisualPreviewPrefab(ObjectDefinitionResolver.Load(ProjectRoot, ResourcePath, _document, _events).Prefab)); }
        catch (IOException exception) { _runtimeStatus.Text = exception.Message; }
    }

    private void RefreshLiveWatch()
    {
        if (_variableWatch.IsCurrentCellInEditMode) return;
        var behavior = _runtimePreview.LiveBehavior;
        var values = new Dictionary<string, object>();
        if (behavior is not null)
        {
            values["x"] = behavior.Context.X; values["y"] = behavior.Context.Y; values["z"] = behavior.Context.Z;
            foreach (var pair in behavior.GetVariablesSnapshot())
                if (!pair.Key.StartsWith("__", StringComparison.Ordinal) && pair.Value is string or bool or int or long or float or double or Vector3) values[pair.Key] = pair.Value;
        }
        _watchedValues = values;
        if (_variableWatch.Rows.Count != values.Count) { _variableWatch.Rows.Clear(); foreach (var _ in values) _variableWatch.Rows.Add(); }
        int row = 0;
        foreach (var (name, value) in values)
        {
            _variableWatch.Rows[row].SetValues(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", value is double or float ? "Float" : value is int or long ? "Int" : value is bool ? "Boolean" : value is Vector3 ? "Vector3" : "String"); row++;
        }
        _runtimeStatus.Text = _runtimePreview.ScriptError is { Length: > 0 } error ? error
            : _runtimePreview.Playing ? $"Running · 60 Hz · Frame {_runtimePreview.SimulationFrames}" : _liveStarted ? "Paused" : "Ready · Run Sandbox (F5)";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5) { RunLiveSandbox(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
