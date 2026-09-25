using System.Text.RegularExpressions;
using Genesis.Application.Editors.Suite.Inspector;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

public sealed partial class VisualActionBuilderControl
{
    private void WireBlueprintEditing(ToolStrip tools)
    {
        _graph.EditStarted += BeginGroupedEdit;
        _graph.EditCompleted += () => _groupingEdit = false;
        _graph.ParameterEdited += (id, parameter, value) => SetArgument(id, parameter, value);
        _graph.BodyEdited += (id, body) => SetCustomBody(id, body);
        _graph.ConditionEdited += (id, condition) => SetCondition(id, condition);
        _graph.AssetPickRequested += (id, parameter) =>
        {
            var field = Blocks.FirstOrDefault(block => block.Id == id)?.Parameters.FirstOrDefault(field => field.Name == parameter);
            if (field?.AssetKind is not { } kind) return;
            var asset = AssetPickerService.PickAsset(new AssetPickerRequest(_projectRoot, kind, field.Value), FindForm());
            if (asset is not null) SetArgument(id, parameter, asset.Reference);
        };
        _graph.LayoutChanged += json =>
        {
            string source = Regex.Replace(_source, @"(?m)^// @blueprint [^\r\n]*(?:\r?\n|$)", "");
            ApplyMutation(source.TrimEnd('\r', '\n') + Environment.NewLine + "// @blueprint " + json + Environment.NewLine, _graph.SelectedBlockId);
        };
        _graph.DataConnectionRequested += (from, to, parameter) => ConnectData(from, to, parameter);
        _graph.DataConnectionMoved += (old, source, target, parameter) =>
        {
            BeginGroupedEdit();
            try { if (ConnectData(source, target, parameter)) DisconnectData(old.Target, old.Parameter); }
            finally { _groupingEdit = false; }
        };
        _graph.ExecutionConnectionRequested += (from, to) => ConnectExecution(from, to);
        _graph.ConnectionDeleteRequested += connection =>
        {
            if (connection.Parameter == "$exec") DisconnectExecution(connection.Target);
            else DisconnectData(connection.Target, connection.Parameter);
        };
        tools.Items.Add(EditorChrome.ToolButton("Comment", "Group nodes in a labelled comment frame", () =>
        {
            using var dialog = new DpiAwareForm { Text = "Comment frame", ClientSize = new Size(370, 110), StartPosition = FormStartPosition.CenterParent };
            var text = new TextBox { Text = "Initialize " + _groupName, Dock = DockStyle.Top }; EditorChrome.StyleField(text);
            var add = new Button { Text = "Add frame", Dock = DockStyle.Bottom, Height = 34, DialogResult = DialogResult.OK }; dialog.Controls.Add(text); dialog.Controls.Add(add); dialog.AcceptButton = add;
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) _graph.AddComment(text.Text);
        }));
    }
    private void BeginGroupedEdit() { _groupingEdit = true; _groupUndoPushed = false; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_graph.ContainsFocus && !_graph.Controls.OfType<TextBox>().Any(box => box.Focused))
        {
            if (keyData == Keys.Delete && _graph.DeleteSelectedConnection()) return true;
            if (keyData == Keys.Escape && _graph.CancelConnection()) return true;
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y)) { Redo(); return true; }
            if (keyData == (Keys.Control | Keys.C)) return CopySelected();
            if (keyData == (Keys.Control | Keys.X)) return CutSelected();
            if (keyData == (Keys.Control | Keys.V)) return Paste();
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    public bool SetCustomBody(string blockId, string body)
    {
        var block = Blocks.FirstOrDefault(block => block.Id == blockId); if (block is null) return false;
        return ApplyMutation(VisualActionSyntax.Replace(_source, block, block.ToTemplate() with { Body = body, Parameters = [], ResultVariable = "" }), blockId);
    }
    public bool InsertBlueprintAction(string name, int index = int.MaxValue)
    {
        var template = BlueprintActions.Templates.FirstOrDefault(template => template.Name == name);
        return template is not null && InsertTemplate(template, index);
    }
    public bool ConnectData(string sourceId, string targetId, string parameterName)
    {
        var blocks = Blocks.ToList(); var source = blocks.FirstOrDefault(block => block.Id == sourceId); var target = blocks.FirstOrDefault(block => block.Id == targetId);
        var parameter = target?.Parameters.FirstOrDefault(parameter => parameter.Name == parameterName);
        if (sourceId.StartsWith("$param:", StringComparison.OrdinalIgnoreCase))
        {
            string routineName = sourceId[7..];
            if (target is null || parameter is null || !_routineParameters.TryGetValue(routineName, out BlueprintValueType outputType)) return false;
            BlueprintValueType inputType = BlueprintActions.TypeOf(parameter);
            if (outputType != inputType && !(outputType == BlueprintValueType.Int && inputType == BlueprintValueType.Float)) return false;
            VisualActionTemplate replacement = target.ToTemplate() with
            {
                Parameters = target.Parameters.Select(field => field.Name == parameterName
                    ? field with { Value = routineName, UnlinkedValue = field.UnlinkedValue ?? field.Value }
                    : field).ToArray(),
            };
            return ApplyMutation(VisualActionSyntax.Replace(_source, target, replacement), targetId);
        }
        if (source is null || target is null || parameter is null || source.ResultVariable.Length == 0 || source.Id == target.Id
            || source.FlowId != target.FlowId || source.FlowBranch != target.FlowBranch
            || source.DetachedChain.Length > 0 && source.DetachedChain != target.DetachedChain) return false;
        var output = BlueprintActions.OutputType(source); var input = BlueprintActions.TypeOf(parameter);
        if (output != input && !(output == BlueprintValueType.Int && input == BlueprintValueType.Float)) return false;
        bool alreadyGrouped = _groupingEdit;
        if (!alreadyGrouped) BeginGroupedEdit();
        try
        {
            if (blocks.IndexOf(source) > blocks.IndexOf(target))
            {
                // A value must be evaluated before its consumer. Keep existing dependencies in order;
                // cycles and cross-branch links are rejected instead of generating invalid PGSL.
                if (source.Parameters.Any(field => blocks.Skip(blocks.IndexOf(target)).Any(block =>
                    block.ResultVariable.Length > 0 && field.Value == block.ResultVariable))) return false;
                if (!MoveAction(sourceId, blocks.IndexOf(target))) return false;
            }
            var current = Blocks.First(block => block.Id == targetId);
            var replacement = current.ToTemplate() with
            {
                Parameters = current.Parameters.Select(field => field.Name == parameterName
                    ? field with { Value = source.ResultVariable, UnlinkedValue = field.UnlinkedValue
                        ?? (_graph.Blocks.Any(block => block.ResultVariable.Length > 0 && block.ResultVariable == field.Value) ? "0" : field.Value) }
                    : field).ToArray(),
            };
            return ApplyMutation(VisualActionSyntax.Replace(_source, current, replacement), targetId);
        }
        finally { _groupingEdit = alreadyGrouped; }
    }
    public bool ConnectExecution(string sourceId, string targetId)
    {
        var blocks = Blocks.ToList(); int from = blocks.FindIndex(block => block.Id == sourceId); int to = blocks.FindIndex(block => block.Id == targetId);
        string[]? branchEntry = sourceId.StartsWith("$branch:", StringComparison.Ordinal) ? sourceId.Split(':') : null;
        if (to < 0 || sourceId == targetId || sourceId != "$event" && branchEntry is null && from < 0) return false;
        var target = blocks[to]; var upstream = from < 0 ? null : blocks[from];
        string? flowId = branchEntry is { Length: 3 } ? branchEntry[1] : upstream?.FlowId;
        string? branch = branchEntry is { Length: 3 } ? branchEntry[2] : upstream?.FlowBranch;
        if (target.FlowId != flowId || target.FlowBranch != branch) return false;
        var tail = target.DetachedChain.Length == 0 ? new[] { target } : blocks.Where(block => block.DetachedChain == target.DetachedChain).ToArray();
        if (tail.Any(block => block.Id == sourceId)) return false;
        string source = _source;
        foreach (var block in tail.Reverse())
            source = VisualActionSyntax.Replace(source, block, block.ToTemplate() with { DetachedChain = upstream?.DetachedChain ?? "" });
        string previous = sourceId;
        foreach (var original in tail)
        {
            var current = VisualActionSyntax.Parse(source).ToList();
            if (flowId is not null) current = current.Where(block => block.FlowId == flowId && block.FlowBranch == branch).ToList();
            int oldIndex = current.FindIndex(block => block.Id == original.Id);
            int index = previous == "$event" || previous == sourceId && branchEntry is not null ? 0 : current.FindIndex(block => block.Id == previous) + 1;
            if (index > oldIndex) index--;
            source = flowId is null ? VisualActionSyntax.Move(source, original.Id, index) : VisualActionSyntax.MoveWithinBranch(source, original.Id, index);
            previous = original.Id;
        }
        // Pure values may precede the visible execution chain. Keep them before their consumers;
        // reject a move which would require reordering an action with side effects.
        for (int pass = 0; pass <= blocks.Count; pass++)
        {
            var current = VisualActionSyntax.Parse(source).ToList(); bool moved = false;
            for (int consumer = 0; consumer < current.Count && !moved; consumer++)
            foreach (var field in current[consumer].Parameters)
            {
                int producer = current.FindIndex(block => block.ResultVariable.Length > 0 && block.ResultVariable == field.Value);
                if (producer < consumer) continue;
                if (producer == consumer || current[producer].DetachedChain.Length > 0 || BlueprintActions.Category(current[producer].Category) != "Math") return false;
                source = VisualActionSyntax.Move(source, current[producer].Id, consumer); moved = true; break;
            }
            if (!moved)
            {
                // Never move an action through a structured condition boundary implicitly.
                if (current.Any(block => blocks.FirstOrDefault(original => original.Id == block.Id) is { } original
                    && (original.FlowId != block.FlowId || original.FlowBranch != block.FlowBranch))) return false;
                return ApplyMutation(source, targetId);
            }
        }
        return false;
    }
    public bool DisconnectData(string targetId, string parameterName)
    {
        var target = Blocks.FirstOrDefault(block => block.Id == targetId);
        var field = target?.Parameters.FirstOrDefault(parameter => parameter.Name == parameterName);
        if (target is null || field is null) return false;
        string value = field.UnlinkedValue ?? (field.Kind == VisualActionValueKind.Boolean ? "false" : field.Kind == VisualActionValueKind.Asset ? "" : "0");
        return ApplyMutation(VisualActionSyntax.Replace(_source, target, target.ToTemplate() with
        {
            Parameters = target.Parameters.Select(parameter => parameter.Name == parameterName ? parameter with { Value = value, UnlinkedValue = null } : parameter).ToArray(),
        }), targetId);
    }

    public bool DisconnectExecution(string targetId)
    {
        var blocks = Blocks.ToList(); int index = blocks.FindIndex(block => block.Id == targetId);
        if (index < 0) return false;
        var target = blocks[index]; string chain = "chain_" + Guid.NewGuid().ToString("N")[..10];
        string source = _source;
        foreach (var block in blocks.Skip(index).Where(block => block.DetachedChain == target.DetachedChain
            && block.FlowId == target.FlowId && block.FlowBranch == target.FlowBranch
            && !(BlueprintActions.Category(block.Category) == "Math" && block.ResultVariable.Length > 0)).Reverse())
            source = VisualActionSyntax.Replace(source, block, block.ToTemplate() with { DetachedChain = chain });
        return ApplyMutation(source, targetId);
    }
}
