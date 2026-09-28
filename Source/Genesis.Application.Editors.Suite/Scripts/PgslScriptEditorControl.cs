using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Commands;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Editors.Suite.Scripts;

public enum PgslScriptAuthoringMode
{
    Builder,
    Code,
}

/// <summary>
/// Callable PGSL library editor. Builder and Code edit the same function bodies; the builder
/// stores only ordinary PGSL plus marker comments, so scripts have no editor-only runtime.
/// </summary>
public sealed partial class PgslScriptEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private readonly CodeEditor _code;
    private readonly VisualActionBuilderControl _builder;
    private readonly Panel _authoringHost;
    private readonly ListBox _outline = new();
    private readonly ListView _problems;
    private readonly ListView _commands;
    private readonly Label _statusLabel;
    private readonly System.Windows.Forms.Timer _validateTimer;
    private readonly ToolStripButton _builderModeButton;
    private readonly ToolStripButton _codeModeButton;
    private readonly ToolStripMenuItem _functionsButton;
    private readonly ToolStripMenuItem _commandsButton;
    private readonly ToolStripButton _addActionButton;
    private readonly Panel _functionsPanel;
    private readonly Panel _referencePanel;
    private readonly Panel _commandReference;
    private readonly Panel _diagnosticsPanel;
    private readonly Control _actionToolbox;
    private readonly ToolStripMenuItem _signatureButton;
    private readonly ToolStripMenuItem _deleteRoutineButton;
    private bool _showFunctions;
    private bool _showCommands;
    private int _errorCount;
    private int _warningCount;
    private bool _syncingBuilder;
    private string? _selectedRoutineName;
    private PgslScriptAuthoringMode _authoringMode = PgslScriptAuthoringMode.Builder;

    public PgslScriptEditorControl(string resourcePath, string projectRoot)
        : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;

        _code = new CodeEditor { Dock = DockStyle.Fill };
        _code.SetLanguage("PGSL");
        _code.SetRules(BuildRules());
        _code.CodeText = LoadText();
        _lastScriptText = _savedScriptText = _code.CodeText;
        _code.DocumentUndoRequested = Undo;
        _code.DocumentRedoRequested = Redo;
        _code.IntelligenceRequested += OnIntelligenceRequested;

        _builder = new VisualActionBuilderControl(ProjectRoot) { Dock = DockStyle.Fill };
        _builder.UseDocumentHistory(this);
        _builder.EditGroupCompleted += () => RecordScriptEdit("Edit script actions");
        _builder.UseBlueprintWorkspace();
        _builder.SourceChanged += BuilderSourceChanged;
        _builder.EditCodeRequested += caret =>
        {
            SetAuthoringMode(PgslScriptAuthoringMode.Code);
            if (FindRoutine(_selectedRoutineName) is { } routine)
                _code.MoveCaret(Math.Clamp(routine.BodyStart + caret, routine.BodyStart, routine.BodyEnd));
            else _code.MoveCaret(caret);
        };

        ToolStrip toolbar = EditorChrome.MakeToolbar();
        ToolStripButton save = EditorChrome.ToolButton("Save", "Save callable PGSL library (Ctrl+S)", Save);
        save.BackColor = EditorChrome.Accent;
        toolbar.Items.Add(save);
        _builderModeButton = EditorChrome.ToolButton("Builder", "Author the selected function with typed visual blocks", () => SetAuthoringMode(PgslScriptAuthoringMode.Builder), toggle: true);
        _codeModeButton = EditorChrome.ToolButton("</> Code", "Author the complete PGSL library as text", () => SetAuthoringMode(PgslScriptAuthoringMode.Code), toggle: true);
        toolbar.Items.Add(_builderModeButton);
        toolbar.Items.Add(_codeModeButton);
        toolbar.Items.Add(new ToolStripSeparator());
        _addActionButton = EditorChrome.ToolButton("Add action…", "Choose a typed action; it is added to the selected function or script entry", () => _builder.OpenWizard(FindForm()));
        toolbar.Items.Add(_addActionButton);
        toolbar.Items.Add(EditorChrome.ToolButton("Use in game", "Create an Object caller or see the exact gameplay code for this Script", ShowScriptGameGuide));
        ToolStripDropDownButton options = new("Options") { ForeColor = EditorChrome.Text };
        options.DropDownItems.Add("Add function…", null, (_, _) => AddFunction());
        _signatureButton = new ToolStripMenuItem("Edit selected function…", null, (_, _) => EditSelectedFunction());
        _deleteRoutineButton = new ToolStripMenuItem("Remove selected function", null, (_, _) => DeleteSelectedFunction());
        options.DropDownItems.AddRange([_signatureButton, _deleteRoutineButton, new ToolStripSeparator()]);
        options.DropDownItems.Add("Validate", null, (_, _) => ValidateNow()).ToolTipText = "Check PGSL syntax and commands (Ctrl+Shift+V)";
        _functionsButton = new ToolStripMenuItem("Function outline") { CheckOnClick = true };
        _functionsButton.CheckedChanged += (_, _) =>
        {
            _showFunctions = _functionsButton.Checked;
            if (_showFunctions && _commandsButton is not null) _commandsButton.Checked = false;
            LayoutAuthoringPanels();
        };
        _commandsButton = new ToolStripMenuItem("Command reference") { CheckOnClick = true };
        _commandsButton.CheckedChanged += (_, _) =>
        {
            _showCommands = _commandsButton.Checked;
            if (_showCommands) _functionsButton.Checked = false;
            LayoutAuthoringPanels();
        };
        options.DropDownItems.AddRange([_functionsButton, _commandsButton]);
        toolbar.Items.Add(options);

        Panel left = _functionsPanel = EditorChrome.SidePanel(240, DockStyle.Left);
        _outline.BackColor = EditorChrome.Surface;
        _outline.BorderStyle = BorderStyle.None;
        _outline.Dock = DockStyle.Fill;
        _outline.Font = EditorChrome.BaseFont;
        _outline.ForeColor = EditorChrome.Text;
        _outline.IntegralHeight = false;
        _outline.SelectedIndexChanged += (_, _) => NavigateToSelectedOutlineItem(moveCaret: false);
        _outline.DoubleClick += (_, _) => NavigateToSelectedOutlineItem(moveCaret: true);
        left.Controls.Add(_outline);
        _scriptOutlineHint = new Label { Dock = DockStyle.Top, ForeColor = EditorChrome.Muted,
            Text = "Select a function; double-click a value to edit its code.", Padding = new Padding(8, 4, 8, 4) };
        left.Controls.Add(_scriptOutlineHint);
        left.Controls.Add(EditorChrome.SectionLabel("SCRIPT OUTLINE"));

        Panel right = _referencePanel = EditorChrome.SidePanel(260, DockStyle.Right);
        Control actionToolbox = _actionToolbox = _builder.TakeActionToolbox();
        Panel commandReference = _commandReference = new() { Dock = DockStyle.Bottom, Height = 180, BackColor = EditorChrome.Surface };
        TextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Filter callable commands…" };
        EditorChrome.StyleField(search);
        _commands = new ListView
        {
            BackColor = EditorChrome.Surface,
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Text,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            View = View.Details,
            ShowItemToolTips = true,
        };
        _commands.Columns.Add("Command", 280);
        _commands.DoubleClick += (_, _) => InsertSelectedCommand();
        search.TextChanged += (_, _) => PopulateCommands(search.Text);
        commandReference.Controls.Add(_commands);
        commandReference.Controls.Add(search);
        commandReference.Controls.Add(EditorChrome.SectionLabel("COMMANDS"));
        right.Controls.Add(actionToolbox);
        right.Controls.Add(commandReference);

        Panel bottom = _diagnosticsPanel = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Bottom, Height = 100 };
        _problems = new ListView
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
        _problems.Columns.Add("Problem", 900);
        bottom.Controls.Add(_problems);
        bottom.Controls.Add(EditorChrome.SectionLabel("DIAGNOSTICS"));

        _authoringHost = new Panel { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        _authoringHost.Controls.Add(_code);
        _authoringHost.Controls.Add(_builder);
        BuildScriptWorkflow();
        _statusLabel = EditorChrome.MakeStatusBar();

        Controls.Add(_authoringHost);
        Controls.Add(bottom);
        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(toolbar);
        Controls.Add(_statusLabel);
        if (toolbar is EditorCommandBar commandBar && commandBar.HistoryCommand is { } history) history.Visible = false;
        _authoringHost.BringToFront();

        _validateTimer = new System.Windows.Forms.Timer { Interval = 650 };
        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); ValidateNow(); };
        _code.TextChangedByUser += (_, _) =>
        {
            if (_syncingBuilder) return;
            RecordScriptEdit("Edit script code");
            _validateTimer.Stop();
            _validateTimer.Start();
            RefreshOutline();
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        };
        Disposed += (_, _) => _validateTimer.Dispose();

        PopulateCommands(string.Empty);
        RefreshOutline();
        SetAuthoringMode(PgslScriptAuthoringMode.Builder);
        ValidateNow();
    }

    public CodeEditor Code => _code;
    public VisualActionBuilderControl Builder => _builder;
    public PgslScriptAuthoringMode AuthoringMode => _authoringMode;
    public int ErrorCount => _errorCount;
    public int WarningCount => _warningCount;
    public int VisibleCommandCount => _commands.Items.Count;
    public event EventHandler? InspectorStateChanged;

    public string ScriptText
    {
        get => _code.CodeText;
        set
        {
            _code.CodeText = value ?? string.Empty;
            RecordScriptEdit("Edit script");
            RefreshOutline();
            ValidateNow();
        }
    }

    public void SetAuthoringMode(PgslScriptAuthoringMode mode)
    {
        _authoringMode = mode;
        _showScriptGuide = false;
        bool builder = mode == PgslScriptAuthoringMode.Builder;
        _builder.Visible = builder;
        _code.Visible = !builder;
        _builderModeButton.Checked = builder;
        _codeModeButton.Checked = !builder;
        _addActionButton.Visible = builder;
        LayoutAuthoringPanels();
        if (builder)
        {
            LoadSelectedRoutine();
            _builder.BringToFront();
            _builder.Focus();
        }
        else
        {
            _code.BringToFront();
            _code.Focus();
        }
        UpdateStatusForMode();
    }

    public override void Save()
    {
        WriteResourceText(_code.CodeText);
        _savedScriptText = _code.CodeText;
        ScriptAssetRegistry.ClearCache();
        AcceptSave();
    }

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues()
    {
        List<ResourceInspectorLiveValue> values = [];
        string scriptName = ResourceDisplayName.Format(ResourcePath);
        foreach (PgslInspectableVariables.Variable variable in PgslInspectableVariables.Reflect(_code.CodeText, ProjectRoot))
            values.Add(new(scriptName, "Variables." + variable.Name, variable.Name, variable.Value,
                Description: $"Authored library value in {scriptName}; saved with this PGSL script.", AssetKind: variable.AssetKind));
        values.Add(new("Diagnostics", "Diagnostics.Errors", "Errors", _errorCount, ReadOnly: true));
        values.Add(new("Diagnostics", "Diagnostics.Warnings", "Warnings", _warningCount, ReadOnly: true));
        return values;
    }

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) => TryApplyInspectorValue(propertyPath, value);

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        const string prefix = "Variables.";
        if (!propertyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string name = propertyPath[prefix.Length..];
        if (!PgslInspectableVariables.TrySetValue(_code.CodeText, name, value, out string updated)) return false;
        _code.CodeText = updated;
        RecordScriptEdit("Edit script value");
        RefreshOutline();
        ValidateNow();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CommandBrowserContains(string name, bool implemented)
    {
        foreach (ListViewItem item in _commands.Items)
        {
            if (item.Tag is PgslCommandInfo pgsl && string.Equals(pgsl.Name, name, StringComparison.OrdinalIgnoreCase) && pgsl.IsImplemented == implemented) return true;
            if (item.Tag is EngineCommandInfo engine && string.Equals(engine.Name, name, StringComparison.OrdinalIgnoreCase) && engine.IsImplemented == implemented) return true;
        }
        return false;
    }

    public void ValidateNow()
    {
        PgslValidationReport report = PgslScriptValidator.ValidateSource(_code.CodeText, ResourceDisplayName.Format(ResourcePath));
        _errorCount = report.Errors.Count;
        _warningCount = report.Warnings.Count;
        _problems.BeginUpdate();
        _problems.Items.Clear();
        foreach (string error in report.Errors) _problems.Items.Add(new ListViewItem("✕  " + error) { ForeColor = EditorChrome.Error });
        foreach (string warning in report.Warnings) _problems.Items.Add(new ListViewItem("△  " + warning) { ForeColor = EditorChrome.Warning });
        if (_errorCount == 0 && _warningCount == 0) _problems.Items.Add(new ListViewItem("✓  No problems.") { ForeColor = EditorChrome.Success });
        _problems.EndUpdate();
        LayoutAuthoringPanels();
        UpdateStatusForMode(report.Summary);
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_functionsPanel is not null) LayoutAuthoringPanels();
    }

    private void LayoutAuthoringPanels()
    {
        bool builder = _authoringMode == PgslScriptAuthoringMode.Builder;
        float scale = EditorChrome.BaseFont.SizeInPoints / 9.5f * DeviceDpi / 96f;
        _functionsPanel.Width = Math.Min((int)(240 * scale), Math.Max(160, ClientSize.Width / 3));
        _referencePanel.Width = Math.Min((int)(260 * scale), Math.Max(180, ClientSize.Width / 3));
        _functionsPanel.Visible = !_showScriptGuide && _showFunctions && LogicalClientWidth >= 400;
        _referencePanel.Visible = !_showScriptGuide && (_showCommands || builder && LogicalClientWidth >= 900)
            && LogicalClientWidth >= 400 && (!_showFunctions || LogicalClientWidth >= 1100);
        _actionToolbox.Visible = builder && !_showCommands;
        _commandReference.Visible = _showCommands;
        _commandReference.Dock = DockStyle.Fill;
        _diagnosticsPanel.Visible = _errorCount + _warningCount > 0;
        _diagnosticsPanel.Height = Math.Min((int)(100 * scale), Math.Max(72, ClientSize.Height / 3));
        LayoutScriptWorkflow();
    }

    private void RefreshOutline(bool loadBuilder = true)
    {
        string? selected = _selectedRoutineName;
        _outline.BeginUpdate();
        _outline.Items.Clear();
        foreach (ScriptRoutine routine in ParseRoutines(_code.CodeText))
            _outline.Items.Add(new ScriptOutlineItem("Function", routine.Name, routine.DeclarationStart, routine));
        if (ParseRoutines(_code.CodeText).Count == 0)
            _outline.Items.Add(new ScriptOutlineItem("Script", "Entry", 0, null));
        foreach (PgslInspectableVariables.Variable variable in PgslInspectableVariables.Reflect(_code.CodeText))
            _outline.Items.Add(new ScriptOutlineItem("Library value", variable.Name,
                Math.Max(0, _code.CodeText.IndexOf(variable.Name, StringComparison.OrdinalIgnoreCase)), null));
        if (_outline.Items.Count == 0)
            _outline.Items.Add(new ScriptOutlineItem("Hint", "Add a callable function to begin", 0, null));
        int restore = -1;
        for (int index = 0; index < _outline.Items.Count; index++)
            if (_outline.Items[index] is ScriptOutlineItem { Routine: { } routine } && string.Equals(routine.Name, selected, StringComparison.OrdinalIgnoreCase)) { restore = index; break; }
        if (restore < 0)
        {
            for (int index = 0; index < _outline.Items.Count; index++)
                if (_outline.Items[index] is ScriptOutlineItem { Routine: not null }) { restore = index; break; }
        }
        _outline.SelectedIndex = restore >= 0 ? restore : (_outline.Items.Count > 0 ? 0 : -1);
        _outline.EndUpdate();
        RefreshScriptRoutinePicker();
        if (loadBuilder && _authoringMode == PgslScriptAuthoringMode.Builder) LoadSelectedRoutine();
    }

    private void NavigateToSelectedOutlineItem(bool moveCaret)
    {
        if (_syncingBuilder) return;
        if (_outline.SelectedItem is not ScriptOutlineItem item || item.Kind == "Hint") return;
        if (item.Routine is { } routine)
        {
            _selectedRoutineName = routine.Name;
            if (_authoringMode == PgslScriptAuthoringMode.Builder) LoadSelectedRoutine();
        }
        if (moveCaret)
        {
            SetAuthoringMode(PgslScriptAuthoringMode.Code);
            _code.MoveCaret(item.Offset);
        }
        UpdateStatusForMode();
    }

    private void LoadSelectedRoutine()
    {
        ScriptRoutine? routine = FindRoutine(_selectedRoutineName) ?? ParseRoutines(_code.CodeText).FirstOrDefault();
        _syncingBuilder = true;
        try
        {
            if (routine is null)
            {
                _selectedRoutineName = null;
                _signatureButton.Visible = _deleteRoutineButton.Visible = false;
                string name = ResourceDisplayName.Format(ResourcePath);
                _builder.ConfigureRoutineHeader(name, [], "Dynamic", scriptEntry: true);
                _builder.LoadSource(_code.CodeText, groupName: name);
                _builder.Graph.FocusStartNode();
                return;
            }
            _signatureButton.Visible = _deleteRoutineButton.Visible = true;
            _selectedRoutineName = routine.Name;
            _builder.ConfigureRoutineHeader(routine.Name, routine.Parameters.Select(parameter => (parameter.Name, parameter.Type)), routine.ReturnType);
            _builder.LoadSource(_code.CodeText[routine.BodyStart..routine.BodyEnd].Trim('\r', '\n'), groupName: routine.Name);
            _builder.Graph.FocusStartNode();
        }
        finally { _syncingBuilder = false; }
    }

    private void BuilderSourceChanged(object? sender, VisualActionSourceChangedEventArgs args)
    {
        if (_syncingBuilder) return;
        _syncingBuilder = true;
        try
        {
            if (FindRoutine(_selectedRoutineName) is { } routine)
            {
                string body = args.Source.Trim('\r', '\n');
                string replacement = Environment.NewLine + body + Environment.NewLine;
                _code.CodeText = _code.CodeText[..routine.BodyStart] + replacement + _code.CodeText[routine.BodyEnd..];
            }
            else if (ParseRoutines(_code.CodeText).Count == 0) _code.CodeText = args.Source;
            else return;
            RefreshOutline(loadBuilder: false);
        }
        finally { _syncingBuilder = false; }
        RecordScriptEdit("Edit script actions");
        _validateTimer.Stop();
        _validateTimer.Start();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddFunction()
    {
        if (!ShowFunctionDialog(null, out FunctionSignature signature)) return;
        string separator = _code.CodeText.Length == 0 || _code.CodeText.EndsWith('\n') ? string.Empty : Environment.NewLine;
        _code.CodeText += separator + BuildFunctionText(signature, string.Empty) + Environment.NewLine;
        _selectedRoutineName = signature.Name;
        RecordScriptEdit("Add function");
        RefreshOutline();
        ValidateNow();
    }

    private void EditSelectedFunction()
    {
        if (FindRoutine(_selectedRoutineName) is not { } routine) return;
        if (!ShowFunctionDialog(routine, out FunctionSignature signature)) return;
        string body = _code.CodeText[routine.BodyStart..routine.BodyEnd].Trim('\r', '\n');
        string replacement = BuildFunctionText(signature, body);
        _code.CodeText = _code.CodeText[..routine.DeclarationStart] + replacement + _code.CodeText[(routine.CloseBrace + 1)..];
        _selectedRoutineName = signature.Name;
        RecordScriptEdit("Edit function signature");
        RefreshOutline();
        ValidateNow();
    }

    private void DeleteSelectedFunction()
    {
        if (FindRoutine(_selectedRoutineName) is not { } routine) return;
        int end = routine.CloseBrace + 1;
        while (end < _code.CodeText.Length && (_code.CodeText[end] == '\r' || _code.CodeText[end] == '\n')) end++;
        _code.CodeText = _code.CodeText.Remove(routine.DeclarationStart, end - routine.DeclarationStart);
        _selectedRoutineName = null;
        RecordScriptEdit("Remove function");
        RefreshOutline();
        ValidateNow();
    }

    private bool ShowFunctionDialog(ScriptRoutine? existing, out FunctionSignature signature)
    {
        using DpiAwareForm dialog = CreateFunctionDialog(existing);
        signature = default!;
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return false;
        signature = (FunctionSignature)dialog.Tag!;
        return true;
    }

    private static string BuildFunctionText(FunctionSignature signature, string body)
    {
        string inputMetadata = string.Join(',', signature.Parameters.Select(parameter => $"{parameter.Name}:{parameter.Type}"));
        string arguments = string.Join(", ", signature.Parameters.Select(parameter => parameter.Name));
        string content = string.IsNullOrWhiteSpace(body) ? string.Empty : Environment.NewLine + body.Trim('\r', '\n') + Environment.NewLine;
        return $"// @function {signature.Name} inputs={inputMetadata} return={signature.ReturnType}{Environment.NewLine}function {signature.Name}({arguments}) {{{content}}}";
    }

    private static IReadOnlyList<ScriptRoutine> ParseRoutines(string source)
    {
        List<ScriptRoutine> routines = [];
        foreach (Match match in FunctionDeclaration().Matches(source))
        {
            int open = source.IndexOf('{', match.Index + match.Length - 1);
            int close = FindClosingBrace(source, open);
            if (open < 0 || close < 0) continue;
            string name = match.Groups["name"].Value;
            string[] argumentNames = match.Groups["parameters"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            int declarationStart = match.Index;
            string returnType = "Void";
            Dictionary<string, BlueprintValueType> typed = new(StringComparer.OrdinalIgnoreCase);
            int lineStart = source.LastIndexOf('\n', Math.Max(0, match.Index - 1));
            if (lineStart >= 0)
            {
                int previousStart = source.LastIndexOf('\n', Math.Max(0, lineStart - 1)) + 1;
                string previous = source[previousStart..lineStart].Trim();
                Match metadata = FunctionMetadata().Match(previous);
                if (metadata.Success && string.Equals(metadata.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
                {
                    declarationStart = previousStart;
                    returnType = metadata.Groups["return"].Value;
                    foreach (string token in metadata.Groups["inputs"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] parts = token.Split(':', 2, StringSplitOptions.TrimEntries);
                        if (parts.Length == 2 && Enum.TryParse(parts[1], true, out BlueprintValueType type)) typed[parts[0]] = type;
                    }
                }
            }
            RoutineParameter[] parameters = argumentNames.Select(argument => new RoutineParameter(argument, typed.GetValueOrDefault(argument, BlueprintValueType.Float))).ToArray();
            routines.Add(new ScriptRoutine(name, declarationStart, open + 1, close, close, parameters, returnType));
        }
        return routines;
    }

    private ScriptRoutine? FindRoutine(string? name) => string.IsNullOrWhiteSpace(name) ? null : ParseRoutines(_code.CodeText).FirstOrDefault(routine => string.Equals(routine.Name, name, StringComparison.OrdinalIgnoreCase));

    private static int FindClosingBrace(string source, int openingBrace)
    {
        if (openingBrace < 0) return -1;
        int depth = 0; bool quoted = false; bool escaped = false; bool lineComment = false; bool blockComment = false;
        for (int index = openingBrace; index < source.Length; index++)
        {
            char current = source[index], next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (lineComment) { if (current == '\n') lineComment = false; continue; }
            if (blockComment) { if (current == '*' && next == '/') { blockComment = false; index++; } continue; }
            if (!quoted && current == '/' && next == '/') { lineComment = true; index++; continue; }
            if (!quoted && current == '/' && next == '*') { blockComment = true; index++; continue; }
            if (current == '"' && !escaped) quoted = !quoted;
            escaped = current == '\\' && !escaped;
            if (quoted) continue;
            escaped = false;
            if (current == '{') depth++;
            else if (current == '}' && --depth == 0) return index;
        }
        return -1;
    }

    private string LoadText()
    {
        try { return File.Exists(ResourcePath) ? File.ReadAllText(ResourcePath) : string.Empty; }
        catch (IOException exception) { LoadWarning = exception.Message; return string.Empty; }
    }

    private void PopulateCommands(string filter)
    {
        PgslCommands.WarmRegistry();
        _commands.BeginUpdate();
        _commands.Items.Clear();
        try
        {
            foreach (PgslCommandInfo info in PgslCommandRegistry.GetFullCatalog())
            {
                if (!MatchesCommandFilter(info.Name, info.Category, filter)) continue;
                _commands.Items.Add(new ListViewItem(info.Signature ?? info.Name) { ForeColor = info.IsImplemented ? EditorChrome.Text : EditorChrome.Muted, Tag = info, ToolTipText = info.Description });
            }
            foreach (EngineCommandInfo info in EngineCommandRegistry.GetCatalog().OrderBy(command => command.Category).ThenBy(command => command.Name))
            {
                if (!MatchesCommandFilter(info.Name, info.Category, filter)) continue;
                _commands.Items.Add(new ListViewItem(info.Signature ?? info.Name) { ForeColor = info.IsImplemented ? EditorChrome.Text : EditorChrome.Muted, Tag = info, ToolTipText = info.IsImplemented ? info.Description : "Roadmap — not implemented: " + info.Description });
            }
        }
        finally { _commands.EndUpdate(); }
    }

    private static bool MatchesCommandFilter(string name, string? category, string filter) => string.IsNullOrWhiteSpace(filter) || name.Contains(filter, StringComparison.OrdinalIgnoreCase) || (category?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private void InsertSelectedCommand()
    {
        if (_commands.SelectedItems.Count != 1) return;
        if (_commands.SelectedItems[0].Tag is PgslCommandInfo pgsl && pgsl.IsImplemented)
        {
            if (_authoringMode == PgslScriptAuthoringMode.Builder) _builder.InsertCommand(pgsl.QualifiedName);
            else _code.InsertAtCaret(pgsl.Signature ?? pgsl.Name);
        }
        else if (_commands.SelectedItems[0].Tag is EngineCommandInfo engine && engine.IsImplemented)
        {
            if (_authoringMode == PgslScriptAuthoringMode.Builder) _builder.InsertCommand(engine.Name);
            else _code.InsertAtCaret(engine.Signature ?? engine.Name);
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.V)) { ValidateNow(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public static List<HighlightRule> BuildRules()
    {
        Color keyword = EditorChrome.FromHex("#6C8CFF"), command = EditorChrome.FromHex("#5BCAC0"), str = EditorChrome.FromHex("#E8B15C"), number = EditorChrome.FromHex("#B5CEA8"), comment = EditorChrome.FromHex("#6A9955"), function = EditorChrome.FromHex("#D882C6");
        return
        [
            new(new Regex(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled), number),
            new(new Regex(@"\b(" + string.Join('|', PgslCodeIntelligenceProvider.Keywords) + @")\b", RegexOptions.Compiled), keyword),
            new(new Regex(@"\bfunction\s+([A-Za-z_]\w*)", RegexOptions.Compiled), function),
            new(new Regex(@"\b(?:Engine|Game|Debug|Audio|Net)\.[A-Za-z_]\w*", RegexOptions.Compiled), command),
            new(new Regex("\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled), str),
            new(new Regex(@"//[^\r\n]*", RegexOptions.Compiled), comment),
            new(new Regex(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline), comment),
        ];
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged();
        _statusLabel.BackColor = EditorChrome.Surface; _statusLabel.ForeColor = EditorChrome.Muted;
        _outline.BackColor = EditorChrome.Surface; _outline.ForeColor = EditorChrome.Text;
        _problems.BackColor = EditorChrome.Surface; _commands.BackColor = EditorChrome.Surface;
        LayoutAuthoringPanels();
    }

    private void OnIntelligenceRequested(object? sender, CodeIntelligenceRequestEventArgs request) => PgslCodeIntelligenceProvider.ApplyRequest(_code, ProjectRoot, _code.CodeText, request);
    private void UpdateStatusForMode(string? validation = null) => _statusLabel.Text = $"{_authoringMode} · {(_selectedRoutineName ?? "Script entry")}" + (string.IsNullOrWhiteSpace(validation) ? string.Empty : " · " + validation);
    private static string Humanize(string value) { if (string.IsNullOrWhiteSpace(value)) return "Value"; string spaced = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' '); return char.ToUpperInvariant(spaced[0]) + spaced[1..]; }

    private sealed record RoutineParameter(string Name, BlueprintValueType Type);
    private sealed record FunctionSignature(string Name, IReadOnlyList<RoutineParameter> Parameters, string ReturnType);
    private sealed record ScriptRoutine(string Name, int DeclarationStart, int BodyStart, int BodyEnd, int CloseBrace, IReadOnlyList<RoutineParameter> Parameters, string ReturnType);
    private sealed record ScriptOutlineItem(string Kind, string Name, int Offset, ScriptRoutine? Routine) { public override string ToString() => $"{Kind}: {Name}"; }

    [GeneratedRegex(@"\bfunction\s+(?<name>[A-Za-z_]\w*)\s*\((?<parameters>[^)]*)\)\s*\{")]
    private static partial Regex FunctionDeclaration();
    [GeneratedRegex(@"^//\s*@function\s+(?<name>[A-Za-z_]\w*)\s+inputs=(?<inputs>.*?)\s+return=(?<return>[A-Za-z_]\w*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FunctionMetadata();
}
