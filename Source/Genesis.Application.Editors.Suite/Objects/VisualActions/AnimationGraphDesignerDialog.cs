using System.Drawing.Drawing2D;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

/// <summary>
/// Optional visual state-machine authoring for one Object event. It edits graph metadata and lets
/// <see cref="AnimationGraphSyntax"/> compile that graph back to ordinary PGSL.
/// </summary>
public sealed class AnimationGraphDesignerDialog : DpiAwareForm
{
    private readonly ListBox _states = new();
    private readonly ListBox _transitions = new();
    private readonly TextBox _graphName = new();
    private readonly TextBox _modelAsset = new();
    private readonly TextBox _stateName = new();
    private readonly TextBox _clip = new();
    private readonly NumericUpDown _speed = new();
    private readonly CheckBox _loop = new();
    private readonly CheckBox _default = new();
    private readonly CheckBox _keepTransform = new();
    private readonly ComboBox _from = new();
    private readonly ComboBox _to = new();
    private readonly TextBox _condition = new();
    private readonly CheckBox _onEnd = new();
    private readonly NumericUpDown _blend = new();
    private readonly AnimationStateGraphCanvas _canvas = new();
    private bool _syncing;
    private readonly string _projectRoot;

    public AnimationGraphDesignerDialog(AnimationGraphDefinition? definition = null, string? projectRoot = null)
    {
        _projectRoot = projectRoot ?? string.Empty;
        Definition = AnimationGraphSyntax.Normalize(AnimationGraphSyntax.Clone(
            definition ?? new AnimationGraphDefinition()));
        Text = "Visual Animation Graph";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(960, 620);
        Size = new Size(1240, 760);
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        ShowInTaskbar = false;

        Controls.Add(BuildButtons());
        Controls.Add(BuildBody());
        LoadDefinition();
    }

    public AnimationGraphDefinition Definition { get; private set; }
    public AnimationGraphDefinition? ResultDefinition { get; private set; }

    public AnimationGraphState AddState(string name = "New State", string clip = "Animation")
    {
        AnimationGraphState state = new() { Name = name, Clip = clip };
        Definition.States.Add(state);
        Definition = AnimationGraphSyntax.Normalize(Definition);
        RefreshStateList(state.Id);
        return Definition.States.First(candidate => candidate.Id == state.Id);
    }

    public AnimationGraphTransition? AddTransition(
        string fromStateId,
        string toStateId,
        string condition = "true",
        bool onAnimationEnd = false,
        double blendSeconds = 0.15)
    {
        if (!Definition.States.Any(state => state.Id == fromStateId)
            || !Definition.States.Any(state => state.Id == toStateId)) return null;
        AnimationGraphTransition transition = new()
        {
            FromStateId = fromStateId,
            ToStateId = toStateId,
            Condition = condition,
            OnAnimationEnd = onAnimationEnd,
            BlendSeconds = blendSeconds,
        };
        Definition.Transitions.Add(transition);
        Definition = AnimationGraphSyntax.Normalize(Definition);
        RefreshTransitionList(Definition.Transitions.FindIndex(candidate => candidate.Id == transition.Id));
        return transition;
    }

    public bool AcceptDefinition()
    {
        CommitGraph();
        if (Definition.States.Count == 0) return false;
        ResultDefinition = AnimationGraphSyntax.Normalize(AnimationGraphSyntax.Clone(Definition));
        return true;
    }

    private Control BuildBody()
    {
        SplitContainer outer = new()
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 246,
            SplitterWidth = EditorChrome.SplitterWidth,
            BackColor = EditorChrome.Border,
        };
        outer.Panel1.Controls.Add(BuildStateRail());
        SplitContainer workspace = new()
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 610,
            SplitterWidth = EditorChrome.SplitterWidth,
            BackColor = EditorChrome.Border,
        };
        workspace.Panel1.Controls.Add(_canvas);
        workspace.Panel2.Controls.Add(BuildInspector());
        outer.Panel2.Controls.Add(workspace);
        return outer;
    }

    private Control BuildStateRail()
    {
        Panel panel = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Padding = new Padding(10) };
        Label title = Section("ANIMATION STATES");
        _states.Dock = DockStyle.Fill;
        _states.BackColor = EditorChrome.Canvas;
        _states.ForeColor = EditorChrome.Text;
        _states.BorderStyle = BorderStyle.FixedSingle;
        _states.IntegralHeight = false;
        _states.SelectedIndexChanged += (_, _) => SelectState();
        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0),
        };
        buttons.Controls.Add(Button("＋ State", () => AddState(), 94));
        buttons.Controls.Add(Button("Remove", RemoveState, 94));
        panel.Controls.Add(_states);
        panel.Controls.Add(buttons);
        panel.Controls.Add(title);
        return panel;
    }

    private Control BuildInspector()
    {
        Panel panel = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = EditorChrome.Surface,
            Padding = new Padding(12) };
        FlowLayoutPanel stack = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        _graphName.Width = 310;
        _graphName.TextChanged += (_, _) => CommitGraph();
        EditorChrome.StyleField(_graphName);
        _modelAsset.Width = 268;
        _modelAsset.TextChanged += (_, _) => CommitGraph();
        EditorChrome.StyleField(_modelAsset);
        Panel modelRow = new() { Width = 310, Height = 30 };
        modelRow.Controls.Add(_modelAsset);
        Button browseModel = Button("…", BrowseModel, 36);
        browseModel.Location = new Point(274, 0);
        modelRow.Controls.Add(browseModel);
        _keepTransform.AutoSize = true;
        _keepTransform.Text = "Keep previous position, rotation and proportional scale";
        _keepTransform.CheckedChanged += (_, _) => CommitGraph();
        stack.Controls.Add(Section("GRAPH"));
        stack.Controls.Add(Field("Name", _graphName));
        stack.Controls.Add(Field("Model asset (optional; blank uses the Object binding)", modelRow));
        stack.Controls.Add(_keepTransform);

        _stateName.Width = _clip.Width = 310;
        _stateName.TextChanged += (_, _) => CommitState();
        _clip.TextChanged += (_, _) => CommitState();
        EditorChrome.StyleField(_stateName);
        EditorChrome.StyleField(_clip);
        ConfigureDecimal(_speed, 0, 60, 0.05M, 1);
        _speed.ValueChanged += (_, _) => CommitState();
        _loop.Text = "Loop";
        _default.Text = "Default entry state";
        _loop.AutoSize = _default.AutoSize = true;
        _loop.CheckedChanged += (_, _) => CommitState();
        _default.CheckedChanged += (_, _) => CommitState();
        stack.Controls.Add(Section("SELECTED STATE"));
        stack.Controls.Add(Field("Display name", _stateName));
        stack.Controls.Add(Field("Animation / clip", _clip));
        stack.Controls.Add(Field("Playback speed", _speed));
        stack.Controls.Add(_loop);
        stack.Controls.Add(_default);

        _transitions.Width = 310;
        _transitions.Height = 116;
        _transitions.BackColor = EditorChrome.Canvas;
        _transitions.ForeColor = EditorChrome.Text;
        _transitions.BorderStyle = BorderStyle.FixedSingle;
        _transitions.SelectedIndexChanged += (_, _) => SelectTransition();
        FlowLayoutPanel transitionButtons = new() { Width = 310, Height = 38, WrapContents = false };
        transitionButtons.Controls.Add(Button("＋ Transition", AddTransitionFromSelected, 126));
        transitionButtons.Controls.Add(Button("Remove", RemoveTransition, 88));
        stack.Controls.Add(Section("TRANSITIONS"));
        stack.Controls.Add(_transitions);
        stack.Controls.Add(transitionButtons);

        _from.Width = _to.Width = 310;
        _from.DropDownStyle = _to.DropDownStyle = ComboBoxStyle.DropDownList;
        _from.SelectedIndexChanged += (_, _) => CommitTransition();
        _to.SelectedIndexChanged += (_, _) => CommitTransition();
        EditorChrome.StyleField(_from);
        EditorChrome.StyleField(_to);
        _condition.Width = 310;
        _condition.PlaceholderText = "Example: vspeed > 0";
        _condition.TextChanged += (_, _) => CommitTransition();
        EditorChrome.StyleField(_condition);
        _onEnd.Text = "Transition when animation finishes";
        _onEnd.AutoSize = true;
        _onEnd.CheckedChanged += (_, _) =>
        {
            _condition.Enabled = !_onEnd.Checked;
            CommitTransition();
        };
        ConfigureDecimal(_blend, 0, 10, 0.05M, 0.15M);
        _blend.ValueChanged += (_, _) => CommitTransition();
        stack.Controls.Add(Field("From", _from));
        stack.Controls.Add(Field("To", _to));
        stack.Controls.Add(_onEnd);
        stack.Controls.Add(Field("Condition", _condition));
        stack.Controls.Add(Field("Blend seconds", _blend));
        panel.Controls.Add(stack);
        return panel;
    }

    private Control BuildButtons()
    {
        Panel panel = new() { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(10),
            BackColor = EditorChrome.Raised };
        Button cancel = Button("Cancel", () => { DialogResult = DialogResult.Cancel; Close(); }, 96);
        cancel.Dock = DockStyle.Right;
        Button apply = Button("Use Graph", () =>
        {
            if (!AcceptDefinition()) return;
            DialogResult = DialogResult.OK;
            Close();
        }, 110);
        apply.Dock = DockStyle.Right;
        apply.BackColor = EditorChrome.Accent;
        apply.ForeColor = Color.White;
        panel.Controls.Add(cancel);
        panel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        panel.Controls.Add(apply);
        AcceptButton = apply;
        CancelButton = cancel;
        return panel;
    }

    private void LoadDefinition()
    {
        _syncing = true;
        _graphName.Text = Definition.Name;
        _modelAsset.Text = Definition.ModelAsset;
        _keepTransform.Checked = Definition.KeepPreviousTransform;
        _syncing = false;
        RefreshStateList(Definition.States.FirstOrDefault(state => state.IsDefault)?.Id);
        RefreshTransitionList();
    }

    private void RefreshStateList(string? selectedId = null)
    {
        selectedId ??= (_states.SelectedItem as AnimationGraphState)?.Id;
        _syncing = true;
        _states.Items.Clear();
        _states.Items.AddRange(Definition.States.Cast<object>().ToArray());
        int selected = Definition.States.FindIndex(state => state.Id == selectedId);
        _states.SelectedIndex = selected >= 0 ? selected : Definition.States.Count > 0 ? 0 : -1;
        RefillStateCombos();
        _syncing = false;
        SelectState();
        _canvas.SetGraph(Definition, (_states.SelectedItem as AnimationGraphState)?.Id);
    }

    private void RefreshTransitionList(int selectedIndex = -1)
    {
        if (selectedIndex < 0) selectedIndex = _transitions.SelectedIndex;
        _syncing = true;
        _transitions.Items.Clear();
        foreach (AnimationGraphTransition transition in Definition.Transitions)
        {
            string from = Definition.States.FirstOrDefault(state => state.Id == transition.FromStateId)?.Name ?? "?";
            string to = Definition.States.FirstOrDefault(state => state.Id == transition.ToStateId)?.Name ?? "?";
            string when = transition.OnAnimationEnd ? "end" : transition.Condition;
            _transitions.Items.Add($"{from}  →  {to}   [{when}]");
        }
        _transitions.SelectedIndex = Definition.Transitions.Count == 0
            ? -1
            : Math.Clamp(selectedIndex < 0 ? 0 : selectedIndex, 0, Definition.Transitions.Count - 1);
        _syncing = false;
        SelectTransition();
        _canvas.SetGraph(Definition, (_states.SelectedItem as AnimationGraphState)?.Id);
    }

    private void RefillStateCombos()
    {
        _from.Items.Clear();
        _to.Items.Clear();
        foreach (AnimationGraphState state in Definition.States)
        {
            _from.Items.Add(state);
            _to.Items.Add(state);
        }
    }

    private void SelectState()
    {
        if (_syncing) return;
        _syncing = true;
        if (_states.SelectedItem is AnimationGraphState state)
        {
            _stateName.Text = state.Name;
            _clip.Text = state.Clip;
            _speed.Value = ClampDecimal(state.Speed, _speed.Minimum, _speed.Maximum);
            _loop.Checked = state.Loop;
            _default.Checked = state.IsDefault;
            _stateName.Enabled = _clip.Enabled = _speed.Enabled = _loop.Enabled = _default.Enabled = true;
        }
        else
        {
            _stateName.Clear();
            _clip.Clear();
            _stateName.Enabled = _clip.Enabled = _speed.Enabled = _loop.Enabled = _default.Enabled = false;
        }
        _syncing = false;
        _canvas.SetGraph(Definition, (_states.SelectedItem as AnimationGraphState)?.Id);
    }

    private void SelectTransition()
    {
        if (_syncing) return;
        _syncing = true;
        int index = _transitions.SelectedIndex;
        bool enabled = index >= 0 && index < Definition.Transitions.Count;
        _from.Enabled = _to.Enabled = _condition.Enabled = _onEnd.Enabled = _blend.Enabled = enabled;
        if (enabled)
        {
            AnimationGraphTransition transition = Definition.Transitions[index];
            _from.SelectedIndex = Definition.States.FindIndex(state => state.Id == transition.FromStateId);
            _to.SelectedIndex = Definition.States.FindIndex(state => state.Id == transition.ToStateId);
            _condition.Text = transition.Condition;
            _onEnd.Checked = transition.OnAnimationEnd;
            _condition.Enabled = !transition.OnAnimationEnd;
            _blend.Value = ClampDecimal(transition.BlendSeconds, _blend.Minimum, _blend.Maximum);
        }
        _syncing = false;
    }

    private void CommitGraph()
    {
        if (_syncing) return;
        Definition.Name = string.IsNullOrWhiteSpace(_graphName.Text) ? "Animation Graph" : _graphName.Text.Trim();
        Definition.ModelAsset = _modelAsset.Text.Trim().Replace('\\', '/');
        Definition.KeepPreviousTransform = _keepTransform.Checked;
    }

    private void BrowseModel()
    {
        if (string.IsNullOrWhiteSpace(_projectRoot)) return;
        ProjectAssetEntry? selected = AssetPickerService.PickAsset(
            new AssetPickerRequest(
                _projectRoot,
                ResourceKind.Model,
                _modelAsset.Text,
                "Select model for animation graph",
                AllowNone: true),
            this);
        if (selected is not null) _modelAsset.Text = selected.Reference;
    }

    private void CommitState()
    {
        if (_syncing || _states.SelectedItem is not AnimationGraphState state) return;
        state.Name = string.IsNullOrWhiteSpace(_stateName.Text) ? "State" : _stateName.Text.Trim();
        state.Clip = string.IsNullOrWhiteSpace(_clip.Text) ? state.Name : _clip.Text.Trim();
        state.Speed = (double)_speed.Value;
        state.Loop = _loop.Checked;
        if (_default.Checked)
            foreach (AnimationGraphState candidate in Definition.States) candidate.IsDefault = candidate.Id == state.Id;
        else if (state.IsDefault)
            _default.Checked = true;
        _states.Refresh();
        _canvas.SetGraph(Definition, state.Id);
    }

    private void CommitTransition()
    {
        if (_syncing || _transitions.SelectedIndex < 0
            || _transitions.SelectedIndex >= Definition.Transitions.Count) return;
        AnimationGraphTransition transition = Definition.Transitions[_transitions.SelectedIndex];
        if (_from.SelectedItem is AnimationGraphState from) transition.FromStateId = from.Id;
        if (_to.SelectedItem is AnimationGraphState to) transition.ToStateId = to.Id;
        transition.Condition = string.IsNullOrWhiteSpace(_condition.Text) ? "true" : _condition.Text.Trim();
        transition.OnAnimationEnd = _onEnd.Checked;
        transition.BlendSeconds = (double)_blend.Value;
        RefreshTransitionList(_transitions.SelectedIndex);
    }

    private void RemoveState()
    {
        if (_states.SelectedItem is not AnimationGraphState state || Definition.States.Count <= 1) return;
        int index = _states.SelectedIndex;
        Definition.States.Remove(state);
        Definition.Transitions.RemoveAll(transition =>
            transition.FromStateId == state.Id || transition.ToStateId == state.Id);
        Definition = AnimationGraphSyntax.Normalize(Definition);
        RefreshStateList(Definition.States[Math.Clamp(index, 0, Definition.States.Count - 1)].Id);
        RefreshTransitionList();
    }

    private void AddTransitionFromSelected()
    {
        if (Definition.States.Count == 0) return;
        AnimationGraphState from = _states.SelectedItem as AnimationGraphState ?? Definition.States[0];
        AnimationGraphState to = Definition.States.FirstOrDefault(state => state.Id != from.Id) ?? from;
        AddTransition(from.Id, to.Id);
    }

    private void RemoveTransition()
    {
        int index = _transitions.SelectedIndex;
        if (index < 0 || index >= Definition.Transitions.Count) return;
        Definition.Transitions.RemoveAt(index);
        RefreshTransitionList(Math.Min(index, Definition.Transitions.Count - 1));
    }

    private static Label Section(string text) => new()
    {
        Text = text,
        Width = 310,
        Height = 31,
        Padding = new Padding(0, 9, 0, 0),
        ForeColor = EditorChrome.Muted,
        Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
    };

    private static Panel Field(string label, Control control)
    {
        Panel panel = new() { Width = 310, Height = 58 };
        control.Location = new Point(0, 25);
        control.Width = 310;
        panel.Controls.Add(control);
        panel.Controls.Add(new Label { Text = label, Dock = DockStyle.Top, Height = 22,
            ForeColor = EditorChrome.Muted });
        return panel;
    }

    private static Button Button(string text, Action action, int width)
    {
        Button button = new() { Text = text, Width = width, Height = 30 };
        EditorChrome.StyleField(button);
        button.Click += (_, _) => action();
        return button;
    }

    private static void ConfigureDecimal(NumericUpDown input, decimal min, decimal max, decimal increment, decimal value)
    {
        input.Minimum = min;
        input.Maximum = max;
        input.DecimalPlaces = 2;
        input.Increment = increment;
        input.Value = value;
        EditorChrome.StyleField(input);
    }

    private static decimal ClampDecimal(double value, decimal min, decimal max) =>
        Math.Clamp((decimal)(double.IsFinite(value) ? value : 0), min, max);
}

internal sealed class AnimationStateGraphCanvas : Control
{
    private AnimationGraphDefinition _graph = new();
    private string? _selectedId;

    public AnimationStateGraphCanvas()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        BackColor = EditorChrome.Canvas;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetGraph(AnimationGraphDefinition graph, string? selectedId)
    {
        _graph = AnimationGraphSyntax.Clone(graph);
        _selectedId = selectedId;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawGrid(g);
        if (_graph.States.Count == 0) return;
        const float nodeWidth = 180;
        const float nodeHeight = 82;
        int columns = Math.Max(1, (ClientSize.Width - 40) / 230);
        Dictionary<string, RectangleF> nodes = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < _graph.States.Count; index++)
        {
            int row = index / columns;
            int column = index % columns;
            nodes[_graph.States[index].Id] = new RectangleF(34 + column * 230, 54 + row * 150, nodeWidth, nodeHeight);
        }
        foreach (AnimationGraphTransition transition in _graph.Transitions)
        {
            if (!nodes.TryGetValue(transition.FromStateId, out RectangleF from)
                || !nodes.TryGetValue(transition.ToStateId, out RectangleF to)) continue;
            DrawArrow(g, from, to, transition.OnAnimationEnd ? "end" : transition.Condition);
        }
        foreach (AnimationGraphState state in _graph.States)
            DrawNode(g, nodes[state.Id], state, state.Id == _selectedId);
        TextRenderer.DrawText(g, "ENTRY", EditorChrome.SmallFont, new Rectangle(20, 14, 72, 24),
            EditorChrome.Success, TextFormatFlags.VerticalCenter);
    }

    private static void DrawNode(Graphics g, RectangleF bounds, AnimationGraphState state, bool selected)
    {
        using GraphicsPath path = Rounded(bounds, 9);
        using SolidBrush fill = new(EditorChrome.Surface);
        using Pen border = new(selected ? EditorChrome.Accent : state.IsDefault ? EditorChrome.Success : EditorChrome.Border,
            selected ? 2.4f : 1.4f);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        TextRenderer.DrawText(g, state.Name, new Font(EditorChrome.BaseFont, FontStyle.Bold),
            Rectangle.Round(new RectangleF(bounds.X + 12, bounds.Y + 9, bounds.Width - 24, 25)),
            EditorChrome.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, $"{state.Clip}  ·  {state.Speed:0.##}×  ·  {(state.Loop ? "loop" : "once")}",
            EditorChrome.SmallFont, Rectangle.Round(new RectangleF(bounds.X + 12, bounds.Y + 41, bounds.Width - 24, 25)),
            EditorChrome.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    private static void DrawArrow(Graphics g, RectangleF from, RectangleF to, string label)
    {
        PointF start = new(from.Right, from.Top + from.Height / 2);
        PointF end = new(to.Left, to.Top + to.Height / 2);
        if (Math.Abs(start.X - end.X) < 2)
        {
            start = new PointF(from.Left + from.Width / 2, from.Bottom);
            end = new PointF(to.Left + to.Width / 2, to.Top);
        }
        using Pen pen = new(EditorChrome.Accent, 1.6f) { EndCap = LineCap.ArrowAnchor };
        g.DrawLine(pen, start, end);
        Rectangle caption = new((int)((start.X + end.X) / 2) - 58, (int)((start.Y + end.Y) / 2) - 12, 116, 22);
        TextRenderer.DrawText(g, label, EditorChrome.SmallFont, caption, EditorChrome.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawGrid(Graphics g)
    {
        using SolidBrush brush = new(Color.FromArgb(48, EditorChrome.Border));
        for (int y = 12; y < Height; y += 20)
            for (int x = 12; x < Width; x += 20)
                g.FillEllipse(brush, x, y, 2, 2);
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float diameter = radius * 2;
        GraphicsPath path = new();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
