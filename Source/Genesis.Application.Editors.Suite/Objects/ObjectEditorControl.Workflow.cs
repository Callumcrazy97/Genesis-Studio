using Genesis.Application.Core.Resources;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Objects;

public sealed partial class ObjectEditorControl
{
    private sealed record ObjectDraft(string Document, Dictionary<string, string> Events, string? SelectedEvent, int Caret)
    {
        public string Key => Document + JsonConvert.SerializeObject(Events.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    private ObjectDraft? _lastObjectDraft;
    private string _savedObjectDraft = string.Empty;
    private bool _recordingObjectHistory;
    private bool _showObjectGameGuide;
    private bool _syncingObjectPicker;
    private Panel _objectWorkspaceHost = null!;
    private Panel _objectAuthoringPanel = null!;
    private Label _objectStartingSteps = null!;
    private TableLayoutPanel _objectEventPickerRow = null!;
    private ComboBox _objectEventPicker = null!;
    private FlowLayoutPanel? _objectGameGuide;
    private ToolStripButton? _objectBuilderCommand;
    private ToolStripButton? _objectCodeCommand;
    private ToolStripButton? _objectAddActionCommand;
    private float _objectGraphScale = 1;
    private bool ShouldShowObjectProperties => _propertiesPanelChoice ??
        (LogicalClientWidth >= 560 && (!(_previewPanelChoice ?? LogicalClientWidth >= 1000)
            || ClientSize.Width - (_objectPreviewPanel?.Width ?? 326) - (_objectPropertiesPanel?.Width ?? 262) >= 350 * EditorChrome.BaseFont.SizeInPoints / 9.5f));

    private ObjectDraft CaptureObjectDraft() => new(_document.ToString(Formatting.None),
        new Dictionary<string, string>(_events, StringComparer.OrdinalIgnoreCase), _activeEvent, _code.TextBox.SelectionStart);

    private void InitializeObjectWorkflow()
    {
        _lastObjectDraft = CaptureObjectDraft();
        _savedObjectDraft = _lastObjectDraft.Key;
        _code.DocumentUndoRequested = Undo;
        _code.DocumentRedoRequested = Redo;
        _visualActions.UseDocumentHistory(this);
        _visualActions.EditGroupCompleted += RecordObjectEdit;
        DirtyChanged += (_, _) =>
        {
            if (_recordingObjectHistory || IsDisposed || Disposing) return;
            if (!IsDirty) { _lastObjectDraft = CaptureObjectDraft(); _savedObjectDraft = _lastObjectDraft.Key; }
            else RecordObjectEdit();
        };
        RefreshObjectWorkflow();
    }

    private void RecordObjectEdit()
    {
        if (_recordingObjectHistory || _lastObjectDraft is null || _visualActions.IsGroupingEdit) return;
        ObjectDraft after = CaptureObjectDraft(), before = _lastObjectDraft;
        _lastObjectDraft = after;
        if (after.Key == before.Key) return;
        _recordingObjectHistory = true;
        try { PushEdit("Edit Object", () => RestoreObjectDraft(after), () => RestoreObjectDraft(before), 100); }
        finally { _recordingObjectHistory = false; }
    }

    private void RestoreObjectDraft(ObjectDraft draft)
    {
        _recordingObjectHistory = true;
        try
        {
            _document.RemoveAll();
            foreach (JProperty property in JObject.Parse(draft.Document).Properties()) _document.Add(property.Name, property.Value.DeepClone());
            _events.Clear(); foreach ((string id, string source) in draft.Events) _events[id] = source;
            _activeEvent = null; PopulateAssetCombos(); LoadInheritedEvents(); SyncFromDocument(); RebuildEventTree();
            if (draft.SelectedEvent is { } selected && (_events.ContainsKey(selected) || _inheritedEvents.ContainsKey(selected))) SelectEvent(selected);
            else if (_events.Count > 0 || _inheritedEvents.Count > 0) SelectFirstEventWithCode();
            else { _syncing = true; LoadEventCode(string.Empty); _syncing = false; _visualActions.LoadSource(string.Empty, groupName: "Event actions"); }
            _code.MoveCaret(Math.Min(draft.Caret, _code.CodeText.Length));
            _thumbnailAsset = null; RefreshSpritePreview(); RefreshCompositionPreviews(); ValidateActiveEvent();
            _lastObjectDraft = CaptureObjectDraft(); RefreshObjectWorkflow(); InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _recordingObjectHistory = false; }
    }

    private void RefreshObjectDirtyState()
    {
        _recordingObjectHistory = true;
        try { if (CaptureObjectDraft().Key == _savedObjectDraft) AcceptSave(); else MarkDirty(); }
        finally { _recordingObjectHistory = false; }
    }

    public override void Undo() { base.Undo(); RefreshObjectDirtyState(); }
    public override void Redo() { base.Redo(); RefreshObjectDirtyState(); }
    protected override void OnJournalChanged() { base.OnJournalChanged(); _visualActions?.RefreshDocumentHistory(); }

    private ToolStrip BuildObjectWorkflowToolbar()
    {
        ToolStrip toolbar = EditorChrome.MakeToolbar();
        _objectBuilderCommand = EditorChrome.ToolButton("Builder", "Edit the selected event with typed action blocks", ShowVisualActions, toggle: true);
        _objectCodeCommand = EditorChrome.ToolButton("Code", "Edit the same event's PGSL with hints and argument help", () => ShowCodeEditor(), toggle: true);
        toolbar.Items.AddRange([_objectBuilderCommand, _objectCodeCommand]);
        toolbar.Items.Add(EditorChrome.ToolButton("Add event…", "Choose when this Object acts: Create, Step, collisions and more", () => AddEventViaWizard()));
        _objectAddActionCommand = EditorChrome.ToolButton("Add action…", "Choose a typed action for the current event", () =>
        {
            if (_activeEvent is null) { UpdateStatus("Add an event first, then add an action to it."); return; }
            _visualActions.OpenWizard(FindForm());
        });
        toolbar.Items.Add(_objectAddActionCommand);
        toolbar.Items.Add(EditorChrome.ToolButton("Use in game", "Place this saved Object in a Room and use other editors' saved resources", ShowObjectGameGuide));
        ToolStripDropDownButton options = new("Options") { ForeColor = EditorChrome.Text };
        options.DropDownItems.Add("Components…", null, (_, _) => ShowComposition());
        options.DropDownItems.Add("Split Builder and Code", null, (_, _) => SetWorkspaceMode(ObjectWorkspaceMode.Split));
        options.DropDownItems.Add("Properties and events", null, (_, _) => { _showObjectGameGuide = false; _propertiesPanelChoice = !(_objectPropertiesPanel?.Visible ?? false); ApplyObjectLayout(); RefreshObjectWorkflow(); });
        options.DropDownItems.Add("Preview and live variables", null, (_, _) => { _showObjectGameGuide = false; _previewPanelChoice = !(_objectPreviewPanel?.Visible ?? false); ApplyObjectLayout(); RefreshObjectWorkflow(); });
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add("Check event", null, (_, _) => TestEvent());
        options.DropDownItems.Add("Check all events", null, (_, _) => TestObject());
        options.DropDownItems.Add("Debug current event", null, (_, _) => DebugActiveEvent());
        options.DropDownItems.Add("Stop debug", null, (_, _) => StopDebugging());
        toolbar.Items.Add(options); return toolbar;
    }

    private void BuildObjectWorkflowHint(Panel centre)
    {
        _objectStartingSteps = new Label { Name = "ObjectStartingSteps", Dock = DockStyle.Top, ForeColor = EditorChrome.Muted,
            BackColor = EditorChrome.Surface, Padding = new Padding(10, 6, 10, 6),
            Text = "1. Choose a Sprite Image on the left.  2. Add event: Create runs once, Step runs every frame. Use Builder actions or Code.  3. Save, then Use in game to open a Room and place this Object. F5 tests this Object in its own preview." };
        _objectEventPicker = new UiKit.ThemedComboBox { Name = "ObjectEventPicker", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        EditorChrome.StyleField(_objectEventPicker);
        _objectEventPicker.SelectedIndexChanged += (_, _) => { if (!_syncingObjectPicker && _objectEventPicker.SelectedItem is string id) SelectEvent(id); };
        _objectEventPickerRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Padding = new Padding(8, 4, 8, 4), BackColor = EditorChrome.Surface };
        _objectEventPickerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); _objectEventPickerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _objectEventPickerRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _objectEventPickerRow.Controls.Add(new Label { Text = "Event", AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = EditorChrome.Muted }, 0, 0);
        _objectEventPickerRow.Controls.Add(_objectEventPicker, 1, 0);
        centre.Controls.Add(_objectStartingSteps); centre.Controls.Add(_objectEventPickerRow);
    }

    private void RefreshObjectWorkflow()
    {
        if (_objectBuilderCommand is null || _objectCodeCommand is null || _objectAddActionCommand is null || _objectEventPicker is null) return;
        _objectBuilderCommand.Checked = !_showObjectGameGuide && WorkspaceMode != ObjectWorkspaceMode.Code;
        _objectCodeCommand.Checked = !_showObjectGameGuide && WorkspaceMode != ObjectWorkspaceMode.Graph;
        _objectAddActionCommand.Available = !_showObjectGameGuide && WorkspaceMode != ObjectWorkspaceMode.Code;
        _objectAddActionCommand.Enabled = _activeEvent is not null;
        if (Controls.OfType<EditorCommandBar>().FirstOrDefault()?.HistoryCommand is { } history)
            history.Available = !_showObjectGameGuide && WorkspaceMode == ObjectWorkspaceMode.Code;
        _syncingObjectPicker = true;
        try
        {
            _objectEventPicker.Items.Clear();
            foreach (string id in _events.Keys.Union(_inheritedEvents.Keys).OrderBy(OrderOf)) _objectEventPicker.Items.Add(id);
            _objectEventPicker.SelectedItem = _activeEvent;
        }
        finally { _syncingObjectPicker = false; }
        LayoutObjectWorkflow();
    }

    private void LayoutObjectWorkflow()
    {
        if (_objectStartingSteps is null || _objectEventPickerRow is null || _objectAuthoringPanel is null) return;
        _objectStartingSteps.Visible = WorkspaceMode != ObjectWorkspaceMode.Code;
        _objectStartingSteps.Font = EditorChrome.SmallFont;
        _objectStartingSteps.Height = _objectStartingSteps.Padding.Vertical + TextRenderer.MeasureText(_objectStartingSteps.Text,
            _objectStartingSteps.Font, new Size(Math.Max(120, _objectAuthoringPanel.ClientSize.Width - _objectStartingSteps.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height;
        _objectEventPickerRow.Visible = !ShouldShowObjectProperties;
        _objectEventPicker.Font = EditorChrome.BaseFont;
        _objectEventPickerRow.Height = _objectEventPicker.PreferredHeight + _objectEventPickerRow.Padding.Vertical + 8;
        float scale = Math.Max(1, EditorChrome.BaseFont.SizeInPoints / 9.5f);
        if (Math.Abs(scale - _objectGraphScale) > .001f)
        {
            _visualActions.Graph.SetZoom(_visualActions.Graph.Zoom * scale / _objectGraphScale); _objectGraphScale = scale;
            _visualActions.Graph.FocusStartNode();
        }
        _visualActions.ApplyInterfaceLayout();
        _objectAuthoringPanel.Visible = !_showObjectGameGuide;
        if (_objectPropertiesPanel is not null) _objectPropertiesPanel.Visible = !_showObjectGameGuide && ShouldShowObjectProperties;
        if (_objectPreviewPanel is not null) _objectPreviewPanel.Visible = !_showObjectGameGuide && (_previewPanelChoice ?? LogicalClientWidth >= 1000);
        if (_objectGameGuide is { } guide)
        {
            guide.Visible = _showObjectGameGuide;
            int width = Math.Max(120, guide.ClientSize.Width - guide.Padding.Horizontal - 24);
            foreach (Control child in guide.Controls)
            {
                child.Width = width; child.Font = child.Tag as string == "heading" ? EditorChrome.HeadingFont : EditorChrome.BaseFont;
                child.Height = child is Label label ? TextRenderer.MeasureText(label.Text, child.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 10
                    : child is ComboBox combo ? combo.PreferredHeight : child.Font.Height + 16;
            }
            guide.AutoScrollMinSize = new Size(0, guide.Controls.Cast<Control>().Where(child => child.Visible)
                .Select(child => child.Bottom - guide.AutoScrollPosition.Y + child.Margin.Bottom + guide.Padding.Bottom + 24).DefaultIfEmpty(0).Max());
            if (_showObjectGameGuide) guide.BringToFront();
        }
    }

    private void ShowObjectGameGuide()
    {
        if (_objectGameGuide is null)
        {
            _objectGameGuide = new FlowLayoutPanel { Name = "ObjectUseInGame", Dock = DockStyle.Fill, AutoScroll = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(18, 14, 18, 28), BackColor = EditorChrome.Surface };
            _objectGameGuide.SizeChanged += (_, _) => LayoutObjectWorkflow(); _objectWorkspaceHost.Controls.Add(_objectGameGuide);
        }
        foreach (Control child in _objectGameGuide.Controls.Cast<Control>().ToArray()) child.Dispose();
        void Text(string text, bool heading = false) => _objectGameGuide.Controls.Add(new Label { Text = text, Tag = heading ? "heading" : null, ForeColor = heading ? EditorChrome.Text : EditorChrome.Muted });
        Text("Use this Object in a game", true);
        Text("1. Choose a Sprite Image, then add events", true);
        Text("Create runs once; Step runs every frame; Draw adds world graphics; Draw GUI adds screen graphics. Builder and Code edit the same PGSL event files. Adding an event starts with instructions and does not insert gameplay you did not choose. Add action opens the searchable typed action picker, including when the preview toolbox is hidden.");
        Text("2. Save and place the Object in a Room", true);
        Text("Open a Room below. Choose this Object in the Room's Objects list, then click in the room to place an instance. Its position is the Image's saved origin: use a bottom origin for sprites standing on the floor. Room placement and gameplay share that origin; edit it through the Image viewer. For a complete jumping platformer, create a Mushroom Meadow project and edit its Player, enemies and level.");
        ComboBox rooms = new UiKit.ThemedComboBox { Name = "ObjectGameplayRoom", DropDownStyle = ComboBoxStyle.DropDownList };
        string[] paths = ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.Room).Select(asset => asset.FullPath).ToArray();
        rooms.Items.AddRange(paths.Select(ResourceDisplayName.Format).Cast<object>().ToArray()); if (rooms.Items.Count > 0) rooms.SelectedIndex = 0;
        EditorChrome.StyleField(rooms); _objectGameGuide.Controls.Add(rooms);
        Button open = new() { Name = "ObjectOpenRoom", Text = "Save and open selected Room", Enabled = paths.Length > 0 };
        EditorChrome.StyleField(open); open.Click += (_, _) => { if (rooms.SelectedIndex >= 0) { Save(); RequestOpenLinkedResource(paths[rooms.SelectedIndex]); } }; _objectGameGuide.Controls.Add(open);
        if (paths.Length == 0) Text("Create a Room through the Assets panel's New Resource menu, then reopen this guide.");
        Text("3. Run the Room and keep editing saved resources", true);
        Text("F5 here runs the isolated Object preview. Use Studio Run to play the project and its Room, tiles, collisions, cameras and other Objects. The isolated preview cannot reproduce room-dependent gameplay. Options → Preview and live variables shows its Run/Pause/Reset controls; live variable edits affect that retained instance, while Reset restores authored values.");
        Text("Other editors feed this Object", true);
        Text("Image supplies sprite frames, clips and saved rigs; Physics supplies saved 2D bodies; Shader supplies sprite effects; Particle supplies bursts; Audio supplies sound; Pathing supplies movement routes; UI supplies interactive screen layouts; Script supplies reusable functions. Their Use in game pages show exact commands or create ordinary Objects using the same saved resources. Components under Options adds persistent capabilities without copying assets. Save changes in an editor to refresh dependent previews and gameplay.");
        _showObjectGameGuide = true; _objectGameGuide.AutoScrollPosition = Point.Empty; RefreshObjectWorkflow();
    }
}
