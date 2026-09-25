using System.Text.RegularExpressions;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

public sealed class VisualActionSourceChangedEventArgs(string source, int caret) : EventArgs
{
    public string Source { get; } = source;
    public int Caret { get; } = caret;
}

/// <summary>
/// Integrated Universal Builder for Object events. The graph, property Inspector and PGSL preview
/// all edit the same marker-backed event source, so changing between Builder and Code never creates
/// a second document or loses hand-written code.
/// </summary>
public sealed partial class VisualActionBuilderControl : UserControl
{
    private readonly string _projectRoot;
    private readonly VisualActionPresetStore _presetStore;
    private readonly TextBox _search = new();
    private readonly TreeView _palette = new();
    private readonly VisualActionGraphCanvas _graph = new();
    private readonly RichTextBox _preview = new();
    private readonly Label _summary = new();
    private readonly ToolStripButton _undo = new("Undo");
    private readonly ToolStripButton _redo = new("Redo");
    private readonly ToolStripButton _cut = new("Cut");
    private readonly ToolStripButton _copy = new("Copy");
    private readonly ToolStripButton _paste = new("Paste");
    private readonly ToolStripButton _delete = new("Delete");
    private Panel? _paletteHost;
    private SplitContainer? _workspaceSplit;
    private readonly Stack<string> _undoSources = new();
    private readonly Stack<string> _redoSources = new();
    private string _source = string.Empty;
    private string _groupName = "Event actions";
    private string? _loadedGroup;
    private int _caret;
    private VisualActionTemplate? _clipboard;
    private bool _applyingResponsiveLayout;
    private bool _blueprintWorkspace;
    private bool _routineWorkspace;
    private string _routineReturnType = "Void";
    private readonly Dictionary<string, BlueprintValueType> _routineParameters = new(StringComparer.OrdinalIgnoreCase);
    private bool _groupingEdit, _groupUndoPushed;

    public void UseBlueprintWorkspace()
    {
        _blueprintWorkspace = true;
        if (_workspaceSplit is not null) _workspaceSplit.Panel2Collapsed = true;
        if (_paletteHost is not null) _paletteHost.Visible = false;
        _summary.Visible = false;
    }

    internal void ConfigureRoutineHeader(string name, IEnumerable<(string Name, BlueprintValueType Type)> parameters, string returnType)
    {
        _routineWorkspace = true;
        _routineReturnType = string.IsNullOrWhiteSpace(returnType) ? "Void" : returnType;
        _routineParameters.Clear();
        foreach ((string parameterName, BlueprintValueType type) in parameters)
            _routineParameters[parameterName] = type;
        _groupName = name;
        _graph.SetRoutineHeader(name, _routineParameters.Select(pair => (pair.Key, pair.Value)), _routineReturnType);
    }

    public Control TakeActionToolbox()
    {
        var panel = _paletteHost!; Controls.Remove(panel); panel.Dock = DockStyle.Fill; panel.Visible = true; return panel;
    }

    public VisualActionBuilderControl(string projectRoot)
    {
        _projectRoot = projectRoot;
        _presetStore = new VisualActionPresetStore(projectRoot);
        Dock = DockStyle.Fill;
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        EditorChrome.Changed += OnChromeChanged;
        Disposed += (_, _) => EditorChrome.Changed -= OnChromeChanged;
        Controls.Add(BuildWorkspace());
        Controls.Add(BuildPalette());
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        RefreshPalette();
        RefreshSurface();
    }

    public event EventHandler<VisualActionSourceChangedEventArgs>? SourceChanged;
    public event Action<int>? EditCodeRequested;
    public event EventHandler? SelectionChanged;

    public string Source => _source;
    public IReadOnlyList<VisualActionBlock> Blocks => VisualActionSyntax.Parse(_source);
    public IReadOnlyList<VisualActionFlowGroup> Flows => VisualActionSyntax.ParseFlows(_source);
    public IReadOnlyList<VisualActionPreset> Presets => _presetStore.All;
    public string GroupName => _groupName;
    public VisualActionGraphCanvas Graph => _graph;
    public string? SelectedBlockId => _graph.SelectedBlockId;
    public VisualActionBlock? SelectedBlock => Blocks.FirstOrDefault(block => block.Id == SelectedBlockId);
    public bool CanUndo => _undoSources.Count > 0;
    public bool CanRedo => _redoSources.Count > 0;
    public bool CanPaste => _clipboard is not null;

    public void LoadSource(string source, int caret = 0, string? groupName = null)
    {
        string normalizedSource = source ?? string.Empty;
        string formatted = FormatGroupName(groupName);
        bool changedDocument = !string.Equals(_loadedGroup, formatted, StringComparison.OrdinalIgnoreCase)
                               || !string.Equals(_source, normalizedSource, StringComparison.Ordinal);
        _source = normalizedSource;
        _caret = Math.Clamp(caret, 0, _source.Length);
        _groupName = formatted;
        _loadedGroup = formatted;
        if (changedDocument)
        {
            _undoSources.Clear();
            _redoSources.Clear();
            _graph.SelectBlock(null);
        }
        RefreshSurface();
    }

    public bool OpenWizard(IWin32Window? owner = null, string? preselectCommand = null)
    {
        using VisualActionWizardDialog wizard = new(_projectRoot, _presetStore, preselectCommand);
        if (wizard.ShowDialog(owner ?? FindForm()) != DialogResult.OK || wizard.SelectedTemplate is null) return false;
        ApplyMutation(VisualActionSyntax.Insert(_source, wizard.SelectedTemplate, wizard.Placement, _caret), selectNewest: true);
        RefreshPalette();
        return true;
    }

    public bool InsertPreset(string presetId, int actionIndex = int.MaxValue)
    {
        VisualActionPreset? preset = _presetStore.All.FirstOrDefault(candidate => candidate.Id == presetId);
        return preset is not null && InsertTemplate(preset.ToTemplate(), actionIndex);
    }

    public bool InsertCommand(string commandName, int actionIndex = int.MaxValue)
    {
        VisualActionCommand? command = VisualActionCatalog.Find(commandName);
        return command is not null && InsertTemplate(ToTemplate(command), actionIndex);
    }

    public bool InsertCondition(string condition, bool includeElse = true, int actionIndex = int.MaxValue)
    {
        int target = actionIndex == int.MaxValue ? Blocks.Count : Math.Clamp(actionIndex, 0, Blocks.Count);
        return ApplyMutation(VisualActionSyntax.InsertConditionAtActionIndex(
            _source, condition, target, includeElse), selectNewest: false);
    }

    public bool InsertAnimationGraph(AnimationGraphDefinition definition, int actionIndex = int.MaxValue)
    {
        int target = actionIndex == int.MaxValue ? Blocks.Count : Math.Clamp(actionIndex, 0, Blocks.Count);
        return ApplyMutation(VisualActionSyntax.InsertAtActionIndex(
            _source, AnimationGraphSyntax.CreateTemplate(definition), target), selectNewest: true);
    }

    public bool UpdateAnimationGraph(string blockId, AnimationGraphDefinition definition)
    {
        VisualActionBlock? block = Blocks.FirstOrDefault(candidate => candidate.Id == blockId);
        if (block is null || !AnimationGraphSyntax.TryRead(block.Body, out _)) return false;
        return ApplyMutation(VisualActionSyntax.Replace(
            _source, block, AnimationGraphSyntax.CreateTemplate(definition)), blockId);
    }

    public bool InsertCommandIntoCondition(string flowId, string branch, string commandName)
    {
        VisualActionCommand? command = VisualActionCatalog.Find(commandName);
        return command is not null && InsertTemplateIntoCondition(flowId, branch, ToTemplate(command));
    }

    public bool InsertConditionIntoCondition(
        string flowId,
        string branch,
        string condition,
        bool includeElse = true)
    {
        VisualActionFlowGroup? parent = Flows.FirstOrDefault(flow => flow.Id == flowId);
        if (parent is null) return false;
        return ApplyMutation(VisualActionSyntax.InsertConditionIntoBranch(
            _source, flowId, branch, condition, includeElse), selectNewest: false);
    }

    public bool SetCondition(string flowId, string condition) =>
        ApplyMutation(VisualActionSyntax.SetCondition(_source, flowId, condition));

    public bool MoveAction(string blockId, int finalIndex)
    {
        IReadOnlyList<VisualActionBlock> blocks = Blocks;
        int originalIndex = blocks.ToList().FindIndex(block => block.Id == blockId);
        if (originalIndex < 0) return false;
        VisualActionBlock moving = blocks[originalIndex];
        if (moving.FlowId is not null && moving.FlowBranch is not null)
        {
            int branchTarget = blocks.Take(Math.Clamp(finalIndex, 0, blocks.Count))
                .Count(block => block.FlowId == moving.FlowId && block.FlowBranch == moving.FlowBranch);
            string movedWithinBranch = VisualActionSyntax.MoveWithinBranch(_source, blockId, branchTarget);
            if (movedWithinBranch == _source) return false;
            ApplyMutation(movedWithinBranch, blockId);
            return true;
        }
        if (finalIndex > originalIndex) finalIndex--;
        string moved = VisualActionSyntax.Move(_source, blockId, finalIndex);
        if (moved == _source) return false;
        ApplyMutation(moved, blockId);
        return true;
    }

    public bool RemoveAction(string blockId)
    {
        VisualActionBlock? block = Blocks.FirstOrDefault(candidate => candidate.Id == blockId);
        if (block is null) return false;
        string source = _source;
        foreach (var candidate in Blocks.Where(candidate => candidate.Id == blockId || block.ResultVariable.Length > 0
            && candidate.Parameters.Any(field => field.Value == block.ResultVariable)).Reverse())
        {
            if (candidate.Id == blockId) source = VisualActionSyntax.Remove(source, candidate);
            else source = VisualActionSyntax.Replace(source, candidate, candidate.ToTemplate() with
            {
                Parameters = candidate.Parameters.Select(field => field.Value == block.ResultVariable
                    ? field with { Value = field.UnlinkedValue ?? "0", UnlinkedValue = null } : field).ToArray(),
            });
        }
        ApplyMutation(source);
        return true;
    }

    public bool SetArgument(string blockId, string parameterName, string value)
    {
        VisualActionBlock? block = Blocks.FirstOrDefault(candidate => candidate.Id == blockId);
        if (block is null || block.Body.Length > 0) return false;
        List<VisualActionParameter> parameters = [.. block.Parameters];
        int index = parameters.FindIndex(parameter => string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        bool linked = Blocks.Any(candidate => candidate.ResultVariable.Length > 0 && candidate.ResultVariable == value);
        parameters[index] = parameters[index] with { Value = value ?? string.Empty,
            UnlinkedValue = linked ? parameters[index].UnlinkedValue ?? parameters[index].Value : null };
        VisualActionTemplate replacement = block.ToTemplate() with { Parameters = parameters };
        ApplyMutation(VisualActionSyntax.Replace(_source, block, replacement), blockId);
        return true;
    }

    public bool SaveActionAsPreset(string blockId, string? name = null)
    {
        VisualActionBlock? block = Blocks.FirstOrDefault(candidate => candidate.Id == blockId);
        if (block is null) return false;
        try
        {
            _presetStore.Save(block.ToTemplate() with { Name = string.IsNullOrWhiteSpace(name) ? block.Name : name.Trim() });
            RefreshPalette();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowError("Preset could not be saved: " + exception.Message);
            return false;
        }
    }

    public bool SelectBlock(string? blockId) => _graph.SelectBlock(blockId);

    public bool CopySelected()
    {
        if (SelectedBlock is not { } block) return false;
        _clipboard = block.ToTemplate();
        RefreshCommands();
        return true;
    }

    public bool CutSelected()
    {
        string? id = SelectedBlockId;
        return id is not null && CopySelected() && RemoveAction(id);
    }

    public bool Paste(int actionIndex = int.MaxValue) => _clipboard is not null && InsertTemplate(_clipboard, actionIndex);

    public void Undo()
    {
        if (_undoSources.Count == 0) return;
        _redoSources.Push(_source);
        _source = _undoSources.Pop();
        RaiseSourceChanged();
        RefreshSurface();
    }

    public void Redo()
    {
        if (_redoSources.Count == 0) return;
        _undoSources.Push(_source);
        _source = _redoSources.Pop();
        RaiseSourceChanged();
        RefreshSurface();
    }

    public IReadOnlyList<ResourceInspectorLiveValue> GetSelectedInspectorValues()
    {
        if (SelectedBlock is not { } block) return [];
        string group = $"{block.Name.ToUpperInvariant()} ACTION";
        List<ResourceInspectorLiveValue> values =
        [
            new(group, $"VisualActions.{block.Id}.Command", "Command", block.CommandName,
                ReadOnly: true, Description: block.Description),
        ];
        foreach (VisualActionParameter parameter in block.Parameters)
        {
            values.Add(new ResourceInspectorLiveValue(
                group, $"VisualActions.{block.Id}.Parameters.{parameter.Name}", Humanize(parameter.Name),
                parameter.Value, Description: $"{block.Name} · {parameter.Name}",
                Choices: parameter.Choices, AssetKind: parameter.AssetKind));
        }
        if (block.Body.Length > 0)
            values.Add(new ResourceInspectorLiveValue(group, $"VisualActions.{block.Id}.Body", "Custom PGSL", block.Body,
                ReadOnly: true, Description: "Open Code mode to edit this custom action body."));
        return values;
    }

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        Match match = Regex.Match(propertyPath ?? string.Empty,
            @"^VisualActions\.(?<id>[^.]+)\.Parameters\.(?<parameter>.+)$", RegexOptions.IgnoreCase);
        return match.Success && SetArgument(match.Groups["id"].Value, match.Groups["parameter"].Value,
            Convert.ToString(value) ?? string.Empty);
    }

    private Control BuildPalette()
    {
        Panel palette = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Left, Padding = new Padding(10), Width = 236 };
        _paletteHost = palette;
        Label heading = new() { Dock = DockStyle.Top, Font = EditorChrome.HeadingFont, ForeColor = EditorChrome.Text,
            Height = 30, Text = "Action Toolbox", TextAlign = ContentAlignment.MiddleLeft };
        _search.Dock = DockStyle.Top;
        _search.PlaceholderText = "Search codeblocks & actions…";
        _search.Height = 31;
        _search.TextChanged += (_, _) => RefreshPalette();
        EditorChrome.StyleField(_search);
        _palette.Dock = DockStyle.Fill;
        _palette.BackColor = EditorChrome.Surface;
        _palette.ForeColor = EditorChrome.Text;
        _palette.BorderStyle = BorderStyle.None;
        _palette.HideSelection = false;
        _palette.ShowLines = false;
        _palette.ShowPlusMinus = true;
        _palette.ShowNodeToolTips = true;
        _palette.Indent = 16;
        _palette.ItemHeight = 27;
        _palette.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(_palette);
        _palette.NodeMouseDoubleClick += (_, args) =>
        {
            if (args.Node is not null) InsertPaletteItem(args.Node);
        };
        _palette.ItemDrag += (_, args) =>
        {
            if (args.Item is TreeNode { Tag: VisualActionPaletteItem item }) _palette.DoDragDrop(item, DragDropEffects.Copy);
        };
        FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight,
            Height = 42, Padding = new Padding(0, 7, 0, 0), WrapContents = false };
        actions.Controls.Add(MakeButton("＋ New", () => OpenWizard(), 82));
        actions.Controls.Add(MakeButton("Save preset", SaveSelectedPreset, 104));
        Label hint = new() { Dock = DockStyle.Bottom, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont,
            Height = 26, Padding = new Padding(0, 5, 0, 0), Text = "Drag an action to the graph, or double-click." };
        palette.Controls.Add(_palette);
        palette.Controls.Add(hint);
        palette.Controls.Add(actions);
        palette.Controls.Add(_search);
        palette.Controls.Add(heading);
        _palette.BringToFront();
        return palette;
    }

    private Control BuildWorkspace()
    {
        Panel host = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        ToolStrip tools = EditorChrome.MakeToolbar();
        tools.Dock = DockStyle.Top;
        tools.GripStyle = ToolStripGripStyle.Hidden;
        Configure(_undo, Undo, "Undo the latest block edit");
        Configure(_redo, Redo, "Redo the latest block edit");
        Configure(_cut, () => CutSelected(), "Cut selected action");
        Configure(_copy, () => CopySelected(), "Copy selected action");
        Configure(_paste, () => Paste(), "Paste action at the end");
        Configure(_delete, () => { if (SelectedBlockId is { } id) RemoveAction(id); }, "Delete selected action");
        tools.Items.AddRange([_undo, _redo]);
        tools.Items.Add(new ToolStripSeparator());
        tools.Items.Add(EditorChrome.ToolButton("−", "Zoom out", () => _graph.SetZoom(_graph.Zoom - .1f)));
        tools.Items.Add(EditorChrome.ToolButton("Fit", "Fit the whole event graph", _graph.FitGraph));
        tools.Items.Add(EditorChrome.ToolButton("＋", "Zoom in", () => _graph.SetZoom(_graph.Zoom + .1f)));
        tools.Items.Add(new ToolStripSeparator());
        var more = new ToolStripDropDownButton("More");
        more.DropDownItems.AddRange([_cut, _copy, _paste, _delete, new ToolStripSeparator()]);
        more.DropDownItems.Add("If / Else", null, (_, _) => AddCondition());
        more.DropDownItems.Add("Animation state graph…", null, (_, _) => InsertAnimationGraph(new AnimationGraphDefinition()));
        more.DropDownItems.Add("Edit selected PGSL", null, (_, _) => EditSelectedCode());
        tools.Items.Add(more);
        _summary.Dock = DockStyle.Top;
        _summary.Height = 30;
        _summary.Padding = new Padding(10, 5, 4, 0);
        _summary.BackColor = EditorChrome.Raised;
        _summary.ForeColor = EditorChrome.Muted;
        _preview.Dock = DockStyle.Fill;
        _preview.BackColor = EditorChrome.Canvas;
        _preview.ForeColor = EditorChrome.Text;
        _preview.BorderStyle = BorderStyle.None;
        _preview.Font = EditorChrome.CodeFont;
        _preview.ReadOnly = true;
        _preview.WordWrap = false;
        _preview.DetectUrls = false;
        Panel previewHost = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas, Padding = new Padding(10, 0, 10, 10) };
        previewHost.Controls.Add(_preview);
        previewHost.Controls.Add(new Label { Dock = DockStyle.Top, Height = 30, Text = "PGSL CODE PREVIEW  ·  AUTO-SYNCED",
            TextAlign = ContentAlignment.MiddleLeft, Font = EditorChrome.SmallFont, ForeColor = EditorChrome.Muted });
        SplitContainer split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterDistance = 300, Panel1MinSize = 180, Panel2MinSize = 96,
            SplitterWidth = EditorChrome.SplitterWidth, BackColor = EditorChrome.Border };
        _workspaceSplit = split;
        split.SizeChanged += (_, _) => ApplyResponsiveLayout();
        _graph.Dock = DockStyle.Fill;
        _graph.SelectionChanged += _ => { RefreshCommands(); SelectionChanged?.Invoke(this, EventArgs.Empty); };
        _graph.MoveRequested += (id, index) => MoveAction(id, index);
        _graph.DeleteRequested += id => RemoveAction(id);
        _graph.EditRequested += EditCodeFor;
        _graph.ItemDropped += (payload, index) => { if (payload is VisualActionPaletteItem item) InsertTemplate(item.Template, index); };
        _graph.BranchItemDropped += (payload, flowId, branch) =>
        {
            if (payload is VisualActionPaletteItem item) InsertTemplateIntoCondition(flowId, branch, item.Template);
        };
        WireBlueprintEditing(tools);
        split.Panel1.Controls.Add(_graph);
        split.Panel2.Controls.Add(previewHost);
        host.Controls.Add(split);
        host.Controls.Add(_summary);
        host.Controls.Add(tools);
        return host;
    }

    private void ApplyResponsiveLayout()
    {
        if (_blueprintWorkspace) return;
        if (_applyingResponsiveLayout) return;
        _applyingResponsiveLayout = true;
        try
        {
            if (_paletteHost is not null)
                _paletteHost.Visible = ClientSize.Width >= DpiLayout.Scale(this, 660);
            if (_workspaceSplit is null || _workspaceSplit.ClientSize.Height <= 0) return;
            bool showPreview = _workspaceSplit.ClientSize.Height >= DpiLayout.Scale(this, 430);
            _workspaceSplit.Panel2Collapsed = !showPreview;
            if (!showPreview) return;
            int desired = Math.Max(_workspaceSplit.Panel1MinSize,
                _workspaceSplit.ClientSize.Height - DpiLayout.Scale(this, 155));
            int maximum = _workspaceSplit.ClientSize.Height - _workspaceSplit.Panel2MinSize - _workspaceSplit.SplitterWidth;
            if (maximum >= _workspaceSplit.Panel1MinSize)
                _workspaceSplit.SplitterDistance = Math.Clamp(desired, _workspaceSplit.Panel1MinSize, maximum);
        }
        finally
        {
            _applyingResponsiveLayout = false;
        }
    }

    private void RefreshPalette()
    {
        string query = _search.Text.Trim();
        List<VisualActionPaletteItem> items = [];
        items.AddRange(_presetStore.All.Select(preset => new VisualActionPaletteItem(preset.Id, preset.Name,
            preset.BuiltIn ? "Starter Presets" : "My Presets", preset.Description, preset.ToTemplate(), !preset.BuiltIn)));
        items.InsertRange(0, BlueprintActions.Templates.Select(template => new VisualActionPaletteItem("blueprint:" + template.Name, template.Name, template.Category, template.Description, template, false)));
        items.AddRange(VisualActionCatalog.GetCommands().Select(command => new VisualActionPaletteItem("command:" + command.Name,
            ShortName(command.Name), command.Category, command.Description, ToTemplate(command), false)));
        if (query.Length > 0)
            items = items.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Template.CommandName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _palette.BeginUpdate();
        _palette.Nodes.Clear();
        foreach (IGrouping<string, VisualActionPaletteItem> group in items.GroupBy(item => BlueprintActions.Category(item.Category), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => CategoryOrder(group.Key)).ThenBy(group => group.Key))
        {
            TreeNode category = new(group.Key.ToUpperInvariant()) { ForeColor = EditorChrome.Muted };
            foreach (VisualActionPaletteItem item in group.OrderBy(item => item.Name))
                category.Nodes.Add(new TreeNode(item.Name) { Tag = item, ToolTipText = item.Description });
            _palette.Nodes.Add(category);
            if (query.Length > 0) category.Expand();
        }
        _palette.EndUpdate();
    }

    private void RefreshSurface()
    {
        IReadOnlyList<VisualActionBlock> blocks = Blocks;
        _graph.SetDocument(blocks, Flows, _graph.SelectedBlockId);
        _graph.SetBlueprintSource(_source, _groupName);
        if (_routineWorkspace)
            _graph.SetRoutineHeader(_groupName, _routineParameters.Select(pair => (pair.Key, pair.Value)), _routineReturnType);
        _preview.Text = _source;
        int rawLines = RawCodeLineCount(blocks);
        _summary.ForeColor = EditorChrome.Muted;
        _summary.Text = rawLines > 0
            ? $"— {_groupName.ToUpperInvariant()} —  {blocks.Count} action(s) · {rawLines} hand-written line(s) preserved"
            : $"— {_groupName.ToUpperInvariant()} —  {blocks.Count} action(s) · select a node to edit it in Inspector";
        RefreshCommands();
    }

    private VisualActionTemplate PrepareResult(VisualActionTemplate template)
    {
        if (template.CommandName is "RandomRange" or "ParticleCreate" or "SpawnParticleEmitter" or "CreateInstance" or "PlaySound" && template.ResultVariable.Length == 0)
        {
            string stem = template.CommandName == "RandomRange" ? "randomValue" : template.CommandName == "CreateInstance" ? "instance" : template.CommandName == "PlaySound" ? "soundChannel" : "emitter";
            string variable = stem; int suffix = 2;
            while (Regex.IsMatch(_source, @"\b" + Regex.Escape(variable) + @"\b")) variable = stem + suffix++;
            template = template with { ResultVariable = variable };
        }
        return template;
    }

    private bool InsertTemplate(VisualActionTemplate template, int actionIndex)
    {
        template = PrepareResult(template);
        if (template.CommandName.Equals("Control.IfElse", StringComparison.OrdinalIgnoreCase))
            return InsertCondition("condition", includeElse: true, actionIndex: actionIndex);
        if (template.CommandName.Equals(AnimationGraphSyntax.CommandName, StringComparison.OrdinalIgnoreCase)
            && !AnimationGraphSyntax.TryRead(template.Body, out _))
            template = AnimationGraphSyntax.CreateTemplate(new AnimationGraphDefinition());
        int target = actionIndex == int.MaxValue ? Blocks.Count : Math.Clamp(actionIndex, 0, Blocks.Count);
        return ApplyMutation(VisualActionSyntax.InsertAtActionIndex(_source, template, target), selectNewest: true);
    }

    private bool InsertTemplateIntoCondition(string flowId, string branch, VisualActionTemplate template)
    {
        template = PrepareResult(template);
        if (template.CommandName.Equals("Control.IfElse", StringComparison.OrdinalIgnoreCase))
            return InsertConditionIntoCondition(flowId, branch, "condition", includeElse: true);
        if (template.CommandName.Equals(AnimationGraphSyntax.CommandName, StringComparison.OrdinalIgnoreCase)
            && !AnimationGraphSyntax.TryRead(template.Body, out _))
            template = AnimationGraphSyntax.CreateTemplate(new AnimationGraphDefinition());
        return ApplyMutation(VisualActionSyntax.InsertIntoBranch(_source, flowId, branch, template), selectNewest: true);
    }

    private void AddCondition()
    {
        string? expression = PromptForCondition();
        if (expression is not null) InsertCondition(expression);
    }

    private bool ApplyMutation(string source, string? selectedId = null, bool selectNewest = false)
    {
        if (source == _source) return false;
        if (!_groupingEdit || !_groupUndoPushed) { _undoSources.Push(_source); _groupUndoPushed = true; }
        _redoSources.Clear();
        _source = source;
        RaiseSourceChanged();
        RefreshSurface();
        if (selectedId is not null) _graph.SelectBlock(selectedId);
        else if (selectNewest) _graph.SelectBlock(Blocks.LastOrDefault()?.Id);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RaiseSourceChanged()
    {
        _caret = Math.Clamp(_caret, 0, _source.Length);
        SourceChanged?.Invoke(this, new VisualActionSourceChangedEventArgs(_source, _caret));
    }

    private void EditSelectedCode()
    {
        if (SelectedBlock is { } block) EditCodeFor(block.Id);
        else EditCodeRequested?.Invoke(_caret);
    }

    private void EditCodeFor(string id)
    {
        VisualActionBlock? block = Blocks.FirstOrDefault(candidate => candidate.Id == id);
        if (block is null) return;
        if (AnimationGraphSyntax.TryRead(block.Body, out AnimationGraphDefinition graph))
        {
            using AnimationGraphDesignerDialog dialog = new(graph, _projectRoot);
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK && dialog.ResultDefinition is not null)
                UpdateAnimationGraph(block.Id, dialog.ResultDefinition);
            return;
        }
        EditCodeRequested?.Invoke(block.SourceStart);
    }

    private void SaveSelectedPreset()
    {
        if (SelectedBlock is not { } block) return;
        string? name = PromptForPresetName(block.Name);
        if (name is not null) SaveActionAsPreset(block.Id, name);
    }

    private void InsertPaletteItem(TreeNode node)
    {
        if (node.Tag is VisualActionPaletteItem item) InsertTemplate(item.Template, Blocks.Count);
        else node.Toggle();
    }

    private void RefreshCommands()
    {
        _undo.Enabled = CanUndo;
        _redo.Enabled = CanRedo;
        _cut.Enabled = _copy.Enabled = _delete.Enabled = SelectedBlock is not null;
        _paste.Enabled = CanPaste;
    }

    private void ShowError(string message)
    {
        _summary.ForeColor = EditorChrome.Error;
        _summary.Text = message;
    }

    private int RawCodeLineCount(IReadOnlyList<VisualActionBlock> blocks)
    {
        string unmanaged = _source;
        IReadOnlyList<VisualActionFlowGroup> flows = Flows;
        IEnumerable<(int Start, int Length)> managedRanges =
            flows.Where(flow => flow.ParentFlowId is null)
                .Select(flow => (flow.SourceStart, flow.SourceLength))
                .Concat(blocks.Where(block => block.Managed && block.FlowId is null)
                    .Select(block => (block.SourceStart, block.SourceLength)));
        foreach ((int start, int length) in managedRanges.OrderByDescending(range => range.Start))
            unmanaged = unmanaged.Remove(start, length);
        return unmanaged.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Count(line => !string.IsNullOrWhiteSpace(line));
    }

    private string? PromptForPresetName(string initial)
    {
        using DpiAwareForm dialog = new() { BackColor = EditorChrome.Canvas, ClientSize = new Size(430, 150),
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, Text = "Save Action Preset" };
        TextBox name = new() { Location = new Point(18, 46), Size = new Size(394, 28), Text = initial };
        EditorChrome.StyleField(name);
        dialog.Controls.Add(new Label { AutoSize = true, ForeColor = EditorChrome.Text, Location = new Point(18, 18), Text = "Preset name" });
        dialog.Controls.Add(name);
        Button save = MakeButton("Save", () => { }, 92); save.DialogResult = DialogResult.OK; save.Location = new Point(220, 96);
        Button cancel = MakeButton("Cancel", () => { }, 92); cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(320, 96);
        dialog.Controls.Add(save); dialog.Controls.Add(cancel);
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        return dialog.ShowDialog(FindForm()) == DialogResult.OK && name.Text.Trim().Length > 0 ? name.Text.Trim() : null;
    }

    private string? PromptForCondition()
    {
        using DpiAwareForm dialog = new() { BackColor = EditorChrome.Canvas, ClientSize = new Size(520, 166),
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false, StartPosition = FormStartPosition.CenterParent, Text = "Add Condition" };
        TextBox expression = new() { Location = new Point(18, 50), Size = new Size(484, 28), Text = "speed > 0" };
        EditorChrome.StyleField(expression);
        dialog.Controls.Add(new Label { AutoSize = true, ForeColor = EditorChrome.Text, Location = new Point(18, 18),
            Text = "PGSL condition — actions can be dragged into Then and Else" });
        dialog.Controls.Add(expression);
        Button add = MakeButton("Add", () => { }, 92); add.DialogResult = DialogResult.OK; add.Location = new Point(310, 106);
        Button cancel = MakeButton("Cancel", () => { }, 92); cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(410, 106);
        dialog.Controls.Add(add); dialog.Controls.Add(cancel);
        dialog.AcceptButton = add; dialog.CancelButton = cancel;
        return dialog.ShowDialog(FindForm()) == DialogResult.OK && expression.Text.Trim().Length > 0
            ? expression.Text.Trim()
            : null;
    }

    private static void Configure(ToolStripButton button, Action click, string tooltip)
    {
        button.DisplayStyle = ToolStripItemDisplayStyle.Text;
        button.ToolTipText = tooltip;
        button.Click += (_, _) => click();
    }

    private static Button MakeButton(string text, Action click, int width)
    {
        Button button = new() { FlatStyle = FlatStyle.Flat, Height = 29, Margin = new Padding(2, 0, 2, 0), Text = text, Width = width };
        button.FlatAppearance.BorderColor = EditorChrome.Border;
        button.BackColor = EditorChrome.Raised;
        button.ForeColor = EditorChrome.Text;
        button.Click += (_, _) => click();
        return button;
    }

    private static VisualActionTemplate ToTemplate(VisualActionCommand command) =>
        new(ShortName(command.Name), command.Name, command.Category, command.Description, command.Parameters);

    private static string ShortName(string command) => command.Contains('.') ? command[(command.LastIndexOf('.') + 1)..] : command;

    private static string Humanize(string value)
    {
        string result = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2").Replace('_', ' ');
        return result.Length == 0 ? "Value" : char.ToUpperInvariant(result[0]) + result[1..];
    }

    private string FormatGroupName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return _routineWorkspace ? "New Function" : "Event actions";
        string humanized = Humanize(value.Trim());
        if (_routineWorkspace) return humanized;
        return humanized.EndsWith(" event", StringComparison.OrdinalIgnoreCase) ? humanized : humanized + " Event";
    }

    private static int CategoryOrder(string category) => category switch
    {
        "Movement" => 0, "Instances" => 1, "Flow Control" => 2, "Audio" => 3,
        "Variables" => 4, "Physics / Collision" => 5, "Math" => 6, "Particles" => 7,
        "My Presets" => 8, "Starter Presets" => 9, _ => 10,
    };

    private void OnChromeChanged(object? sender, EventArgs args)
    {
        if (IsDisposed) return;
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        _search.BackColor = _palette.BackColor = _summary.BackColor = EditorChrome.Surface;
        _search.ForeColor = _palette.ForeColor = EditorChrome.Text;
        _summary.ForeColor = EditorChrome.Muted;
        _preview.BackColor = _graph.BackColor = EditorChrome.Canvas;
        _preview.ForeColor = EditorChrome.Text;
        _preview.Font = EditorChrome.CodeFont;
        RefreshPalette();
        Invalidate(true);
    }
}
