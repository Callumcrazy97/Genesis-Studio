using System.Text;
using System.Text.Json;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Scripts;

public sealed partial class PgslScriptEditorControl
{
    private string _lastScriptText = string.Empty;
    private string _savedScriptText = string.Empty;
    private bool _restoringScript;
    private bool _showScriptGuide;
    private bool _refreshingRoutinePicker;
    private Label? _scriptWorkflowHint;
    private TableLayoutPanel? _routinePickerPanel;
    private ComboBox? _routinePicker;
    private Panel? _scriptGameGuide;
    private FlowLayoutPanel? _scriptGameSteps;
    private Label? _scriptOutlineHint;
    private float _scriptGraphScale = 1;

    private void RecordScriptEdit(string label)
    {
        string after = _code.CodeText, before = _lastScriptText;
        if (!_restoringScript && _builder.IsGroupingEdit) { MarkDirty(); return; }
        _lastScriptText = after;
        if (_restoringScript || string.Equals(before, after, StringComparison.Ordinal)) return;
        string? routine = _selectedRoutineName;
        int caret = _code.TextBox.SelectionStart;
        PushEdit(label, () => RestoreScript(after, routine, caret), () => RestoreScript(before, routine, caret), 100);
        RefreshScriptDirtyState();
    }

    private void RestoreScript(string source, string? routine, int caret)
    {
        _restoringScript = _syncingBuilder = true;
        try
        {
            _code.CodeText = _lastScriptText = source;
            _selectedRoutineName = routine;
            RefreshOutline();
            _code.MoveCaret(Math.Min(caret, source.Length));
        }
        finally { _restoringScript = _syncingBuilder = false; }
        ValidateNow();
    }

    private void RefreshScriptDirtyState()
    {
        if (_code.CodeText == _savedScriptText) AcceptSave();
        else MarkDirty();
    }

    public override void Undo() { base.Undo(); RefreshScriptDirtyState(); }
    public override void Redo() { base.Redo(); RefreshScriptDirtyState(); }
    protected override void OnJournalChanged() { base.OnJournalChanged(); _builder?.RefreshDocumentHistory(); }

    private void BuildScriptWorkflow()
    {
        _scriptWorkflowHint = new Label
        {
            Dock = DockStyle.Top, ForeColor = EditorChrome.Muted, BackColor = EditorChrome.Surface,
            Padding = new Padding(10, 6, 10, 6), Name = "ScriptWorkflowHint",
            Text = "Reusable game logic: Add action… or write Code, then Save and Use in game. Actions run in order; they share the same PGSL source.",
        };
        _routinePicker = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            Name = "ScriptRoutinePicker", AccessibleName = "Function to edit" };
        EditorChrome.StyleField(_routinePicker);
        _routinePicker.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingRoutinePicker || _routinePicker.SelectedItem is not ScriptOutlineItem item) return;
            _outline.SelectedItem = item;
        };
        _routinePickerPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(8, 4, 8, 4), BackColor = EditorChrome.Surface,
        };
        _routinePickerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        _routinePickerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _routinePickerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _routinePickerPanel.Controls.Add(new Label { Text = "Editing", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = EditorChrome.Muted }, 0, 0);
        _routinePickerPanel.Controls.Add(_routinePicker, 1, 0);
        _scriptGameSteps = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(12), BackColor = EditorChrome.Surface,
            Name = "ScriptGameSteps",
        };
        Button back = new() { Dock = DockStyle.Top, AutoSize = true, Text = "Back to editing",
            Name = "ScriptBackToEditing", MinimumSize = new Size(0, 34) };
        EditorChrome.StyleField(back);
        back.Click += (_, _) => SetAuthoringMode(_authoringMode);
        _scriptGameGuide = new Panel { Dock = DockStyle.Fill, Visible = false,
            BackColor = EditorChrome.Surface, Name = "ScriptGameGuide" };
        _scriptGameGuide.Controls.Add(_scriptGameSteps); _scriptGameGuide.Controls.Add(back);
        _authoringHost.Controls.Add(_scriptWorkflowHint);
        _authoringHost.Controls.Add(_routinePickerPanel);
        _authoringHost.Controls.Add(_scriptGameGuide);
        _scriptGameSteps.SizeChanged += (_, _) => SizeScriptGameSteps();
    }

    private void RefreshScriptRoutinePicker()
    {
        if (_routinePicker is null) return;
        _refreshingRoutinePicker = true;
        try
        {
            _routinePicker.Items.Clear();
            foreach (ScriptOutlineItem item in _outline.Items.OfType<ScriptOutlineItem>()
                         .Where(item => item.Kind is "Function" or "Script")) _routinePicker.Items.Add(item);
            _routinePicker.SelectedItem = _outline.SelectedItem;
        }
        finally { _refreshingRoutinePicker = false; }
        LayoutScriptWorkflow();
    }

    private void LayoutScriptWorkflow()
    {
        if (_scriptGameGuide is null || _scriptWorkflowHint is null || _routinePickerPanel is null) return;
        bool builder = _authoringMode == PgslScriptAuthoringMode.Builder;
        float scale = Math.Max(1, EditorChrome.BaseFont.SizeInPoints / 9.5f);
        if (Math.Abs(scale - _scriptGraphScale) > .001f)
        {
            _builder.Graph.SetZoom(_builder.Graph.Zoom * scale / _scriptGraphScale);
            _scriptGraphScale = scale;
            _builder.Graph.FocusStartNode();
        }
        if (_scriptOutlineHint is not null)
        {
            _scriptOutlineHint.Font = EditorChrome.SmallFont;
            _scriptOutlineHint.Height = _scriptOutlineHint.Padding.Vertical + TextRenderer.MeasureText(
                _scriptOutlineHint.Text, _scriptOutlineHint.Font,
                new Size(Math.Max(1, _functionsPanel.Width - _scriptOutlineHint.Padding.Horizontal), int.MaxValue),
                TextFormatFlags.WordBreak).Height;
        }
        _scriptGameGuide.Visible = _showScriptGuide;
        _builder.Visible = builder && !_showScriptGuide;
        _code.Visible = !builder && !_showScriptGuide;
        _addActionButton.Visible = builder && !_showScriptGuide;
        _scriptWorkflowHint.Visible = builder && !_showScriptGuide;
        _scriptWorkflowHint.Font = EditorChrome.SmallFont;
        _scriptWorkflowHint.Height = _scriptWorkflowHint.Padding.Vertical + TextRenderer.MeasureText(
            _scriptWorkflowHint.Text, _scriptWorkflowHint.Font,
            new Size(Math.Max(1, _authoringHost.ClientSize.Width - _scriptWorkflowHint.Padding.Horizontal), int.MaxValue),
            TextFormatFlags.WordBreak).Height;
        _routinePickerPanel.Visible = builder && !_showScriptGuide && !_functionsPanel.Visible
            && _routinePicker!.Items.Count > 1;
        Label caption = (Label)_routinePickerPanel.GetControlFromPosition(0, 0)!;
        caption.Font = EditorChrome.BaseFont;
        _routinePicker!.Font = EditorChrome.BaseFont;
        _routinePickerPanel.Height = _routinePickerPanel.Padding.Vertical + Math.Max(
            _routinePicker.PreferredHeight + _routinePicker.Margin.Vertical,
            caption.PreferredSize.Height + caption.Margin.Vertical);
        _routinePickerPanel.ColumnStyles[0].Width = TextRenderer.MeasureText("Editing", EditorChrome.BaseFont).Width + 12;
        SizeScriptGameSteps();
        if (_showScriptGuide) _scriptGameGuide.BringToFront();
    }

    private string ScriptInvocation()
    {
        string name = ResourceNames.Name(ProjectRoot, ResourcePath);
        string source = $"ScriptExecute({JsonSerializer.Serialize(name)});";
        if (FindRoutine(_selectedRoutineName) is { } routine)
        {
            string arguments = string.Join(", ", routine.Parameters.Select(parameter => parameter.Type switch
            {
                BlueprintValueType.String => "\"\"", BlueprintValueType.Boolean => "false",
                BlueprintValueType.Vector3 => "\"0,0,0\"", _ => "0",
            }));
            string result = string.Equals(routine.ReturnType, "Void", StringComparison.OrdinalIgnoreCase)
                ? string.Empty : "var scriptResult = ";
            source += Environment.NewLine + result + routine.Name + "(" + arguments + ");";
        }
        return source + Environment.NewLine;
    }

    private void ShowScriptGameGuide()
    {
        foreach (Control control in _scriptGameSteps!.Controls.Cast<Control>().ToArray()) control.Dispose();
        ScriptGuideText("Use this Script from an Object", heading: true);
        ScriptGuideText("Scripts hold reusable logic. They run when an Object event calls them. Use Create for one-time setup, Step for actions each frame, or Draw / Draw GUI for drawing. Place that Object in a Room, then Run.");
        ScriptGuideText("1. Call the saved Script", heading: true);
        ScriptGuideText(FindRoutine(_selectedRoutineName) is null
            ? "Put this call in your Object event. Script arguments are available as argument0, argument1, and so on."
            : "ScriptExecute loads this resource and its functions. The second line calls the selected function. Replace the example inputs with your game values in the Object editor. Script entry statements run before the function call.");
        TextBox example = new()
        {
            ReadOnly = true, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both,
            Text = ScriptInvocation(), Font = EditorChrome.CodeFont, Height = 90,
            Name = "ScriptGameplayExample", AccessibleName = "PGSL call to use this Script in an Object event",
        };
        EditorChrome.StyleField(example); example.Font = EditorChrome.CodeFont;
        _scriptGameSteps.Controls.Add(example);
        ScriptGuideText("2. Create a caller Object", heading: true);
        ScriptGuideText("This creates an ordinary 2D Object with the example in its Create event and opens it for editing. It has no image yet: choose an Image in that Object if it should be visible. The Object keeps a reference to this Script; edit and save the Script to update subsequent calls.");
        TextBox name = new() { Text = ResourceDisplayName.Format(ResourcePath) + " Caller",
            Name = "ScriptCallerName", AccessibleName = "Name for the caller Object" };
        EditorChrome.StyleField(name); _scriptGameSteps.Controls.Add(name);
        Button create = new() { Text = "Create caller Object", AutoSize = true,
            Name = "ScriptCreateCallerObject", MinimumSize = new Size(0, 36) };
        EditorChrome.StyleField(create);
        create.Click += (_, _) =>
        {
            try { CreateCallerObject(name.Text); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                or IOException or UnauthorizedAccessException)
            {
                ScriptGuideText(exception.Message);
                _statusLabel.Text = exception.Message;
                SizeScriptGameSteps();
            }
        };
        _scriptGameSteps.Controls.Add(create);
        ScriptGuideText("3. Try it in your Room", heading: true);
        ScriptGuideText("Place the caller Object in a Room, then Run. Drawing commands belong in Draw or Draw GUI; move the example there when drawing each frame. For a repeating action, move it to Step. A Script does not run by itself just because it is saved.");
        ScriptGuideText("The Inspector exposes file-scope numbers, booleans and strings. A string naming a saved resource gets a picker for that resource type. For an initially empty reference, use var hero = \"\"; // @resource Image. Object event values support the same hint. Changes use editor Undo and take effect in gameplay after Save.");
        _showScriptGuide = true; LayoutAuthoringPanels();
    }

    private void ScriptGuideText(string text, bool heading = false)
    {
        _scriptGameSteps!.Controls.Add(new Label { AutoSize = true, Text = text,
            ForeColor = heading ? EditorChrome.Text : EditorChrome.Muted,
            Font = heading ? EditorChrome.HeadingFont : EditorChrome.BaseFont,
            Margin = new Padding(3, heading ? 12 : 4, 3, 8) });
    }

    private void SizeScriptGameSteps()
    {
        if (_scriptGameSteps is null) return;
        int width = Math.Max(100, _scriptGameSteps.ClientSize.Width - _scriptGameSteps.Padding.Horizontal - 28);
        float scale = Math.Max(1, EditorChrome.BaseFont.SizeInPoints / 9.5f * DeviceDpi / 96f);
        foreach (Control step in _scriptGameSteps.Controls)
        {
            step.Width = width;
            if (step is Label label) label.MaximumSize = new Size(width, 0);
            else if (step is TextBox { Multiline: true }) step.Height = (int)(90 * scale);
            else if (step is Button button) button.MinimumSize = new Size(width, (int)(36 * scale));
        }
    }

    /// <summary>Creates a normal 2D Object whose Create event calls the saved Script.</summary>
    public string CreateCallerObject(string name)
    {
        string validatedName = ResourceNames.ValidateName(name);
        PgslValidationReport validation = PgslScriptValidator.ValidateSource(_code.CodeText,
            ResourceDisplayName.Format(ResourcePath), strict: true);
        if (!validation.Success) throw new InvalidOperationException("Fix the Script before creating a caller: " + string.Join(" ", validation.Errors));
        string invocation = ScriptInvocation();
        Save();
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.GameObject),
            ResourceKind.GameObject, validatedName);
        JObject document = JObject.Parse(File.ReadAllText(path));
        document["schemaVersion"] = 3; document["events"] = new JArray("Create");
        ((JArray)document["components"]!).Add(new JObject
        {
            ["type"] = "ScriptComponent", ["enabled"] = true,
            ["props"] = new JObject { ["ScriptClass"] = validatedName },
        });
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        File.WriteAllText(path, document.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        ObjectEventStore.Save(path, new Dictionary<string, string> { ["Create"] = invocation });
        if (!ObjectEventStore.Load(path).TryGetValue("Create", out string? saved) || saved != invocation)
            throw new IOException("The caller's Create event could not be saved: " + path);
        ResourceNames.Invalidate(ProjectRoot);
        RequestOpenLinkedResource(path);
        return path;
    }
}
