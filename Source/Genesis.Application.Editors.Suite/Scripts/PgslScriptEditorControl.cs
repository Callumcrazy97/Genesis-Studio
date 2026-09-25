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
        _code.SetRules(BuildRules());
        _code.CodeText = LoadText();
        _code.IntelligenceRequested += OnIntelligenceRequested;

        _builder = new VisualActionBuilderControl(ProjectRoot) { Dock = DockStyle.Fill };
        _builder.UseBlueprintWorkspace();
        _builder.SourceChanged += BuilderSourceChanged;
        _builder.EditCodeRequested += caret =>
        {
            SetAuthoringMode(PgslScriptAuthoringMode.Code);
            if (FindRoutine(_selectedRoutineName) is { } routine)
                _code.MoveCaret(Math.Clamp(routine.BodyStart + caret, routine.BodyStart, routine.BodyEnd));
        };

        ToolStrip toolbar = EditorChrome.MakeToolbar();
        EditorViewportChrome.AttachDocumentMenus(toolbar, this);
        toolbar.Items.Add(new ToolStripLabel(ResourceDisplayName.Format(ResourcePath))
        {
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            ToolTipText = ResourcePath,
        });
        ToolStripButton save = EditorChrome.ToolButton("Save", "Save callable PGSL library (Ctrl+S)", Save);
        save.BackColor = EditorChrome.Accent;
        toolbar.Items.Add(save);
        toolbar.Items.Add(new ToolStripSeparator());
        _builderModeButton = EditorChrome.ToolButton("Builder", "Author the selected function with typed visual blocks", () => SetAuthoringMode(PgslScriptAuthoringMode.Builder), toggle: true);
        _codeModeButton = EditorChrome.ToolButton("</> Code", "Author the complete PGSL library as text", () => SetAuthoringMode(PgslScriptAuthoringMode.Code), toggle: true);
        toolbar.Items.Add(_builderModeButton);
        toolbar.Items.Add(_codeModeButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(EditorChrome.ToolButton("+ Function", "Add a callable function with typed inputs and return metadata", AddFunction));
        toolbar.Items.Add(EditorChrome.ToolButton("If / Else", "Add a branch to the selected function", () => _builder.InsertCondition("condition")));
        toolbar.Items.Add(EditorChrome.ToolButton("Return", "Add a return-value node to the selected function", () => _builder.InsertBlueprintAction("Return Value")));
        toolbar.Items.Add(EditorChrome.ToolButton("Validate", "Parse and semantically check this library (Ctrl+Shift+V)", ValidateNow));

        Panel left = EditorChrome.SidePanel(EditorChrome.LeftPanelWidth, DockStyle.Left);
        _outline.BackColor = EditorChrome.Surface;
        _outline.BorderStyle = BorderStyle.None;
        _outline.Dock = DockStyle.Fill;
        _outline.Font = EditorChrome.BaseFont;
        _outline.ForeColor = EditorChrome.Text;
        _outline.IntegralHeight = false;
        _outline.SelectedIndexChanged += (_, _) => NavigateToSelectedOutlineItem(moveCaret: false);
        _outline.DoubleClick += (_, _) => NavigateToSelectedOutlineItem(moveCaret: true);
        FlowLayoutPanel routineTools = new()
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            BackColor = EditorChrome.Surface,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(6, 4, 4, 4),
        };
        routineTools.Controls.Add(SmallButton("+ Function", AddFunction, 88));
        routineTools.Controls.Add(SmallButton("Signature", EditSelectedFunction, 78));
        routineTools.Controls.Add(SmallButton("Delete", DeleteSelectedFunction, 64));
        left.Controls.Add(_outline);
        left.Controls.Add(routineTools);
        left.Controls.Add(EditorChrome.SectionLabel("FUNCTIONS & LIBRARY VALUES"));

        Panel right = EditorChrome.SidePanel(EditorChrome.RightPanelWidth, DockStyle.Right);
        Control actionToolbox = _builder.TakeActionToolbox();
        Panel commandReference = new() { Dock = DockStyle.Bottom, Height = 230, BackColor = EditorChrome.Surface };
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
        commandReference.Controls.Add(EditorChrome.SectionLabel("COMMAND SIGNATURES"));
        right.Controls.Add(actionToolbox);
        right.Controls.Add(commandReference);

        Panel bottom = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Bottom, Height = 132 };
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
        _statusLabel = EditorChrome.MakeStatusBar();

        Controls.Add(_authoringHost);
        Controls.Add(bottom);
        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(toolbar);
        Controls.Add(_statusLabel);
        _authoringHost.BringToFront();

        _validateTimer = new System.Windows.Forms.Timer { Interval = 650 };
        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); ValidateNow(); };
        _code.TextChangedByUser += (_, _) =>
        {
            MarkDirty();
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
            MarkDirty();
            RefreshOutline();
            ValidateNow();
        }
    }

    public void SetAuthoringMode(PgslScriptAuthoringMode mode)
    {
        _authoringMode = mode;
        bool builder = mode == PgslScriptAuthoringMode.Builder;
        _builder.Visible = builder;
        _code.Visible = !builder;
        _builderModeButton.Checked = builder;
        _codeModeButton.Checked = !builder;
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
        AcceptSave();
    }

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues()
    {
        List<ResourceInspectorLiveValue> values = [];
        string scriptName = ResourceDisplayName.Format(ResourcePath);
        foreach (PgslInspectableVariables.Variable variable in PgslInspectableVariables.Reflect(_code.CodeText))
            values.Add(new(scriptName, "Variables." + variable.Name, Humanize(variable.Name), variable.Value,
                Description: $"Authored library value in {scriptName}; saved with this PGSL script."));
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
        MarkDirty();
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
        UpdateStatusForMode(report.Summary);
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshOutline()
    {
        string? selected = _selectedRoutineName;
        _outline.BeginUpdate();
        _outline.Items.Clear();
        foreach (ScriptRoutine routine in ParseRoutines(_code.CodeText))
            _outline.Items.Add(new ScriptOutlineItem("Function", routine.Name, routine.DeclarationStart, routine));
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
        if (_authoringMode == PgslScriptAuthoringMode.Builder) LoadSelectedRoutine();
    }

    private void NavigateToSelectedOutlineItem(bool moveCaret)
    {
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
                _builder.ConfigureRoutineHeader("NewFunction", [], "Void");
                _builder.LoadSource(string.Empty, groupName: "NewFunction");
                return;
            }
            _selectedRoutineName = routine.Name;
            _builder.ConfigureRoutineHeader(routine.Name, routine.Parameters.Select(parameter => (parameter.Name, parameter.Type)), routine.ReturnType);
            _builder.LoadSource(_code.CodeText[routine.BodyStart..routine.BodyEnd].Trim('\r', '\n'), groupName: routine.Name);
        }
        finally { _syncingBuilder = false; }
    }

    private void BuilderSourceChanged(object? sender, VisualActionSourceChangedEventArgs args)
    {
        if (_syncingBuilder || FindRoutine(_selectedRoutineName) is not { } routine) return;
        string body = args.Source.Trim('\r', '\n');
        string replacement = Environment.NewLine + body + Environment.NewLine;
        _code.CodeText = _code.CodeText[..routine.BodyStart] + replacement + _code.CodeText[routine.BodyEnd..];
        MarkDirty();
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
        MarkDirty();
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
        MarkDirty();
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
        MarkDirty();
        RefreshOutline();
        ValidateNow();
    }

    private bool ShowFunctionDialog(ScriptRoutine? existing, out FunctionSignature signature)
    {
        using DpiAwareForm dialog = new() { Text = existing is null ? "Add Function" : "Edit Function Signature", ClientSize = new Size(480, 232), StartPosition = FormStartPosition.CenterParent };
        TextBox name = new() { Width = 430, Text = existing?.Name ?? "NewFunction" };
        TextBox parameters = new() { Width = 430, Text = existing is null ? string.Empty : string.Join(", ", existing.Parameters.Select(parameter => $"{parameter.Name}: {parameter.Type}")) };
        ThemedComboBox returns = new() { Width = 430, DropDownStyle = ComboBoxStyle.DropDownList };
        returns.Items.AddRange(["Void", .. Enum.GetNames<BlueprintValueType>()]);
        returns.SelectedItem = existing?.ReturnType ?? "Void";
        EditorChrome.StyleField(name); EditorChrome.StyleField(parameters); EditorChrome.StyleField(returns);
        FlowLayoutPanel fields = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14), BackColor = EditorChrome.Surface };
        fields.Controls.Add(DialogLabel("FUNCTION NAME")); fields.Controls.Add(name);
        fields.Controls.Add(DialogLabel("INPUTS  ·  name: Type, name: Type")); fields.Controls.Add(parameters);
        fields.Controls.Add(DialogLabel("RETURN TYPE")); fields.Controls.Add(returns);
        Button create = new() { Dock = DockStyle.Bottom, Height = 36, Text = existing is null ? "Create Function" : "Update Signature", DialogResult = DialogResult.OK };
        dialog.Controls.Add(fields); dialog.Controls.Add(create); dialog.AcceptButton = create;
        signature = default!;
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return false;
        string functionName = name.Text.Trim();
        if (!Regex.IsMatch(functionName, @"^[A-Za-z_]\w*$")) return false;
        List<RoutineParameter> parsed = [];
        foreach (string token in parameters.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(':', 2, StringSplitOptions.TrimEntries);
            if (!Regex.IsMatch(parts[0], @"^[A-Za-z_]\w*$")) return false;
            BlueprintValueType type = parts.Length == 2 && Enum.TryParse(parts[1], true, out BlueprintValueType value) ? value : BlueprintValueType.Float;
            parsed.Add(new RoutineParameter(parts[0], type));
        }
        signature = new FunctionSignature(functionName, parsed, returns.SelectedItem?.ToString() ?? "Void");
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
    }

    private void OnIntelligenceRequested(object? sender, CodeIntelligenceRequestEventArgs request) => PgslCodeIntelligenceProvider.ApplyRequest(_code, ProjectRoot, _code.CodeText, request);
    private void UpdateStatusForMode(string? validation = null) => _statusLabel.Text = $"{_authoringMode} · {(_selectedRoutineName ?? "No function selected")}" + (string.IsNullOrWhiteSpace(validation) ? string.Empty : " · " + validation);
    private static Button SmallButton(string text, Action action, int width) { Button button = new() { Text = text, Width = width, Height = 30, Margin = new Padding(2) }; EditorChrome.StyleField(button); button.Click += (_, _) => action(); return button; }
    private static Label DialogLabel(string text) => new() { AutoSize = false, Width = 430, Height = 20, ForeColor = EditorChrome.Muted, Text = text };
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
