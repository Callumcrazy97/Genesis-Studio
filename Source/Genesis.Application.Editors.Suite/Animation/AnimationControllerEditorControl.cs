using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.Json;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Animation;

/// <summary>
/// The Animation Controller editor: a character's states (a clip each, or a blend of clips by a
/// parameter) drawn as boxes, the transitions between them as arrows, the parameters scripts set
/// in a table, and the selected state or transition's settings beside the graph.
/// </summary>
/// <remarks>
/// Drag a state to move it. Ctrl+drag from one state to another (or from "Any state") makes a
/// transition. Double-click empty space for a new state. Delete removes the selection. Every
/// change can be undone. A game uses the controller with AnimationControllerUse("name").
/// </remarks>
public sealed class AnimationControllerEditorControl : EditorSurfaceControl
{
    private static readonly JsonSerializerOptions SnapshotJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private AnimationControllerAsset _asset;
    private readonly DataGridView _parameters = new() { Name = "AnimationControllerParameters" };
    private readonly GraphCanvas _graph;
    private readonly PropertyGrid _properties = new() { Name = "AnimationControllerProperties", HelpVisible = true, ToolbarVisible = false };
    private readonly Label _hint = new() { Name = "AnimationControllerHint", AutoSize = false, Dock = DockStyle.Top, Height = 28 };
    private bool _loading;

    public AnimationControllerEditorControl(string resourcePath, string projectRoot) : base(resourcePath, projectRoot)
    {
        Name = "AnimationControllerEditor";
        _asset = ReadAsset(resourcePath);
        _graph = new GraphCanvas(this) { Name = "AnimationControllerGraph", Dock = DockStyle.Fill };

        _hint.Text = "Drag a state to move it · Ctrl+drag from one state to another for a transition · Double-click for a new state · Delete removes";
        _hint.ForeColor = EditorChrome.Muted;
        _hint.Padding = new Padding(8, 6, 8, 0);

        EditorCommandBar toolbar = EditorChrome.MakeToolbar();
        toolbar.Name = "AnimationControllerToolbar";
        toolbar.Dock = DockStyle.Top;
        toolbar.Items.Add(new ToolStripButton("New state", null, (_, _) => AddState(new PointF(60 + 24 * _asset.States.Count, 60 + 24 * _asset.States.Count))) { Name = "AnimationControllerAddState" });
        toolbar.Items.Add(new ToolStripButton("Make default", null, (_, _) => MakeSelectedDefault()) { Name = "AnimationControllerMakeDefault" });
        toolbar.Items.Add(new ToolStripButton("Delete", null, (_, _) => DeleteSelection()) { Name = "AnimationControllerDelete" });
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripButton("New parameter", null, (_, _) => AddParameter()) { Name = "AnimationControllerAddParameter" });

        _parameters.Dock = DockStyle.Fill;
        _parameters.BackgroundColor = EditorChrome.Canvas;
        _parameters.GridColor = EditorChrome.Border;
        _parameters.BorderStyle = BorderStyle.None;
        _parameters.EnableHeadersVisualStyles = false;
        _parameters.ColumnHeadersDefaultCellStyle.BackColor = EditorChrome.Surface;
        _parameters.ColumnHeadersDefaultCellStyle.ForeColor = EditorChrome.Muted;
        _parameters.DefaultCellStyle.BackColor = EditorChrome.Raised;
        _parameters.DefaultCellStyle.ForeColor = EditorChrome.Text;
        _parameters.DefaultCellStyle.SelectionBackColor = EditorChrome.AccentSoft;
        _parameters.DefaultCellStyle.SelectionForeColor = EditorChrome.Text;
        _parameters.AllowUserToAddRows = false;
        _parameters.RowHeadersVisible = false;
        _parameters.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _parameters.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Parameter" });
        var kind = new DataGridViewComboBoxColumn { Name = "Kind", HeaderText = "Kind", FlatStyle = FlatStyle.Flat };
        kind.Items.AddRange("float", "bool", "trigger");
        _parameters.Columns.Add(kind);
        _parameters.Columns.Add(new DataGridViewTextBoxColumn { Name = "Default", HeaderText = "Default" });
        _parameters.CellValueChanged += (_, _) => ParametersEdited();
        _parameters.CurrentCellDirtyStateChanged += (_, _) => { if (_parameters.IsCurrentCellDirty) _parameters.CommitEdit(DataGridViewDataErrorContexts.Commit); };

        _properties.BackColor = EditorChrome.Surface;
        _properties.ViewBackColor = EditorChrome.Raised;
        _properties.ViewForeColor = EditorChrome.Text;
        _properties.ViewBorderColor = EditorChrome.Border;
        _properties.LineColor = EditorChrome.Surface;
        _properties.CategoryForeColor = EditorChrome.Muted;
        _properties.CategorySplitterColor = EditorChrome.Border;
        _properties.HelpBackColor = EditorChrome.Surface;
        _properties.HelpForeColor = EditorChrome.Muted;
        _properties.HelpBorderColor = EditorChrome.Border;
        _properties.PropertyValueChanged += (_, _) => Changed("Edit " + (_graph.SelectedState?.Name ?? "transition"), () => { });

        var left = new Panel { Dock = DockStyle.Left, Width = 230, Padding = new Padding(6) };
        left.Controls.Add(_parameters);
        left.Controls.Add(new Label { Text = "Parameters (set by scripts)", Dock = DockStyle.Top, Height = 22, ForeColor = EditorChrome.Muted });
        var right = new Panel { Dock = DockStyle.Right, Width = 300, Padding = new Padding(6) };
        right.Controls.Add(_properties);
        Controls.Add(_graph);
        Controls.Add(left);
        Controls.Add(right);
        Controls.Add(_hint);
        Controls.Add(toolbar);
        ShowParameters();
        ShowSelection();
    }

    /// <summary>The controller as it is being edited.</summary>
    public AnimationControllerAsset Asset => _asset;

    private static AnimationControllerAsset ReadAsset(string path)
    {
        try { return File.Exists(path) ? AnimationControllerAsset.Load(path) : new AnimationControllerAsset(); }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            return new AnimationControllerAsset();
        }
    }

    public override void Save()
    {
        _asset.Save(ResourcePath);
        AcceptSave();
    }

    // ── Changes, each one undoable ──────────────────────────────────────────

    /// <summary>Makes a change: what the controller was before goes on the undo stack.</summary>
    private void Changed(string label, Action change)
    {
        if (_loading) return;
        string before = JsonSerializer.Serialize(_asset, SnapshotJson);
        change();
        _asset.Normalize();
        string after = JsonSerializer.Serialize(_asset, SnapshotJson);
        if (before == after) { _graph.Invalidate(); return; }
        PushEdit(label, () => Restore(after), () => Restore(before));
        MarkDirty();
        _graph.Invalidate();
        ShowParameters();
    }

    private void Restore(string snapshot)
    {
        _asset = JsonSerializer.Deserialize<AnimationControllerAsset>(snapshot, SnapshotJson) ?? new AnimationControllerAsset();
        _graph.Select(null, null);
        ShowParameters();
        ShowSelection();
        _graph.Invalidate();
    }

    /// <summary>A new state at a point of the graph; its name is free.</summary>
    public AnimationControllerState AddState(PointF at)
    {
        string name = "State";
        for (int n = 2; _asset.States.Exists(state => string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = "State " + n;
        var state = new AnimationControllerState { Name = name, Clip = "Idle", X = at.X, Y = at.Y };
        Changed("Add state", () => _asset.States.Add(state));
        _graph.Select(state, null);
        ShowSelection();
        return state;
    }

    /// <summary>A transition from one state ("*": any state) to another.</summary>
    public AnimationControllerTransition? AddTransition(string from, string to)
    {
        if (string.IsNullOrWhiteSpace(to) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return null;
        var transition = new AnimationControllerTransition { From = from, To = to, Condition = "true", BlendSeconds = 0.15 };
        Changed("Add transition", () => _asset.Transitions.Add(transition));
        _graph.Select(null, transition);
        ShowSelection();
        return transition;
    }

    public void DeleteSelection()
    {
        if (_graph.SelectedState is { } state)
            Changed("Delete state", () =>
            {
                _asset.States.Remove(state);
                _asset.Transitions.RemoveAll(t => string.Equals(t.From, state.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.To, state.Name, StringComparison.OrdinalIgnoreCase));
            });
        else if (_graph.SelectedTransition is { } transition)
            Changed("Delete transition", () => _asset.Transitions.Remove(transition));
        _graph.Select(null, null);
        ShowSelection();
    }

    private void MakeSelectedDefault()
    {
        if (_graph.SelectedState is { } state) Changed("Make default", () => _asset.DefaultState = state.Name);
    }

    private void AddParameter()
    {
        string name = "parameter";
        for (int n = 2; _asset.Parameters.Exists(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = "parameter" + n;
        Changed("Add parameter", () => _asset.Parameters.Add(new AnimationControllerParameter { Name = name }));
    }

    private void ShowParameters()
    {
        _loading = true;
        try
        {
            _parameters.Rows.Clear();
            foreach (AnimationControllerParameter parameter in _asset.Parameters)
                _parameters.Rows.Add(parameter.Name, parameter.Kind is "bool" or "trigger" ? parameter.Kind : "float",
                    parameter.Default.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { _loading = false; }
    }

    private void ParametersEdited()
    {
        if (_loading) return;
        Changed("Edit parameters", () =>
        {
            _asset.Parameters.Clear();
            foreach (DataGridViewRow row in _parameters.Rows)
            {
                string name = Convert.ToString(row.Cells[0].Value)?.Trim() ?? string.Empty;
                if (name.Length == 0) continue;
                double.TryParse(Convert.ToString(row.Cells[2].Value), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double value);
                _asset.Parameters.Add(new AnimationControllerParameter { Name = name, Kind = Convert.ToString(row.Cells[1].Value) ?? "float", Default = value });
            }
        });
    }

    private void ShowSelection()
    {
        _properties.SelectedObject = _graph.SelectedState is { } state ? new StateView(state, this)
            : _graph.SelectedTransition is { } transition ? new TransitionView(transition) : null;
    }

    internal void OnGraphSelection() => ShowSelection();

    internal void OnStateMoved(AnimationControllerState state, PointF from, PointF to) =>
        PushMove(state, from, to);

    private void PushMove(AnimationControllerState state, PointF from, PointF to)
    {
        PushEdit("Move " + state.Name, () => { state.X = to.X; state.Y = to.Y; _graph.Invalidate(); },
            () => { state.X = from.X; state.Y = from.Y; _graph.Invalidate(); });
        MarkDirty();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Delete && _graph.Focused) { DeleteSelection(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── What the property panel shows ──────────────────────────────────────

    private sealed class StateView(AnimationControllerState state, AnimationControllerEditorControl editor)
    {
        [Category("State"), Description("Its name: transitions and AnimationStateCurrent use it.")]
        public string Name
        {
            get => state.Name;
            set
            {
                string old = state.Name, name = value?.Trim() ?? string.Empty;
                if (name.Length == 0 || editor._asset.States.Exists(s => s != state && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))) return;
                state.Name = name;
                foreach (AnimationControllerTransition t in editor._asset.Transitions)
                {
                    if (string.Equals(t.From, old, StringComparison.OrdinalIgnoreCase)) t.From = name;
                    if (string.Equals(t.To, old, StringComparison.OrdinalIgnoreCase)) t.To = name;
                }
                if (string.Equals(editor._asset.DefaultState, old, StringComparison.OrdinalIgnoreCase)) editor._asset.DefaultState = name;
            }
        }

        [Category("State"), Description("The clip it plays (by its name in the model), when it is not a blend.")]
        public string Clip { get => state.Clip ?? string.Empty; set => state.Clip = value; }

        [Category("State"), Description("Play the clip again and again; off for an attack or a jump that ends.")]
        public bool Loop { get => state.Loop; set => state.Loop = value; }

        [Category("Blend"), Description("A 1D blend: the parameter that chooses between the clips (empty: play Clip).")]
        public string BlendParameter { get => state.BlendParameter ?? string.Empty; set => state.BlendParameter = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }

        [Category("Blend"), Description("The blend's clips and the parameter value each one plays at, as clip=value pairs: Walk=1.5, Run=5")]
        public string BlendClips
        {
            get => state.Blend is { Count: > 0 } points
                ? string.Join(", ", points.Select(p => p.Clip + "=" + p.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                : string.Empty;
            set
            {
                var points = new List<AnimationControllerBlendPoint>();
                foreach (string part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string[] pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (pair[0].Length == 0) continue;
                    double threshold = 0;
                    if (pair.Length == 2) double.TryParse(pair[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out threshold);
                    points.Add(new AnimationControllerBlendPoint { Clip = pair[0], Threshold = threshold });
                }
                state.Blend = points.Count > 0 ? points : null;
            }
        }

        [Category("Blend"), Description("Keep the blend's clips in step: a walk and a run put their feet down together.")]
        public bool Synced { get => state.Synced; set => state.Synced = value; }

        [Category("State"), Description("The state the controller starts in.")]
        public bool IsDefault => string.Equals(editor._asset.DefaultState, state.Name, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TransitionView(AnimationControllerTransition transition)
    {
        [Category("Transition"), Description("The state it leaves (* for any state).")]
        public string From => transition.From;

        [Category("Transition"), Description("The state it goes to.")]
        public string To => transition.To;

        [Category("Transition"), Description("When it happens: a bool or trigger parameter (jump), a comparison (speed > 0.5), AnimationFinished, or true.")]
        public string Condition { get => transition.Condition; set => transition.Condition = string.IsNullOrWhiteSpace(value) ? "true" : value.Trim(); }

        [Category("Transition"), Description("Seconds the two states are blended for.")]
        public double BlendSeconds { get => transition.BlendSeconds; set => transition.BlendSeconds = Math.Clamp(value, 0, 10); }
    }

    // ── The graph ───────────────────────────────────────────────────────────

    /// <summary>States as boxes, transitions as arrows; "Any state" is a box of its own at the top left.</summary>
    internal sealed class GraphCanvas : Control
    {
        private const float BoxWidth = 140, BoxHeight = 44;
        private static readonly PointF AnyState = new(16, 16);
        private readonly AnimationControllerEditorControl _editor;
        private AnimationControllerState? _dragging;
        private PointF _dragOffset, _dragStart;
        private string? _linkFrom;
        private Point _mouse;

        public GraphCanvas(AnimationControllerEditorControl editor)
        {
            _editor = editor;
            DoubleBuffered = true;
            BackColor = EditorChrome.Canvas;
            ForeColor = EditorChrome.Text;
            TabStop = true;
        }

        public AnimationControllerState? SelectedState { get; private set; }
        public AnimationControllerTransition? SelectedTransition { get; private set; }

        public void Select(AnimationControllerState? state, AnimationControllerTransition? transition)
        {
            SelectedState = state;
            SelectedTransition = transition;
            Invalidate();
        }

        private static RectangleF Box(AnimationControllerState state) => new(state.X, state.Y, BoxWidth, BoxHeight);
        private static RectangleF AnyBox => new(AnyState.X, AnyState.Y, 100, 30);

        private RectangleF? BoxOf(string name) =>
            name == "*" ? AnyBox
            : _editor._asset.States.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } state ? Box(state) : null;

        private AnimationControllerState? StateAt(Point p) =>
            _editor._asset.States.LastOrDefault(state => Box(state).Contains(p));

        // An arrow's ends: box edge to box edge, nudged aside when two states have arrows both ways.
        private (PointF Start, PointF End)? Arrow(AnimationControllerTransition transition)
        {
            if (BoxOf(transition.From) is not { } from || BoxOf(transition.To) is not { } to) return null;
            PointF a = new(from.X + from.Width / 2, from.Y + from.Height / 2), b = new(to.X + to.Width / 2, to.Y + to.Height / 2);
            float dx = b.X - a.X, dy = b.Y - a.Y, length = MathF.Max(1, MathF.Sqrt(dx * dx + dy * dy));
            PointF side = new(-dy / length * 6, dx / length * 6);
            return (Edge(from, a, b, side), Edge(to, b, a, side));
        }

        private static PointF Edge(RectangleF box, PointF centre, PointF toward, PointF side)
        {
            float dx = toward.X - centre.X, dy = toward.Y - centre.Y;
            float scale = MathF.Min(dx == 0 ? float.MaxValue : box.Width / 2 / MathF.Abs(dx), dy == 0 ? float.MaxValue : box.Height / 2 / MathF.Abs(dy));
            return new PointF(centre.X + dx * scale + side.X, centre.Y + dy * scale + side.Y);
        }

        private AnimationControllerTransition? TransitionAt(Point p)
        {
            foreach (AnimationControllerTransition transition in _editor._asset.Transitions)
            {
                if (Arrow(transition) is not { } arrow) continue;
                float dx = arrow.End.X - arrow.Start.X, dy = arrow.End.Y - arrow.Start.Y;
                float t = Math.Clamp(((p.X - arrow.Start.X) * dx + (p.Y - arrow.Start.Y) * dy) / MathF.Max(1, dx * dx + dy * dy), 0, 1);
                float x = arrow.Start.X + dx * t - p.X, y = arrow.Start.Y + dy * t - p.Y;
                if (x * x + y * y < 36) return transition;
            }
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            AnimationControllerState? state = StateAt(e.Location);
            bool fromAny = AnyBox.Contains(e.Location);
            if ((ModifierKeys & Keys.Control) != 0 && (state != null || fromAny))
            {
                _linkFrom = fromAny ? "*" : state!.Name;
                return;
            }
            if (state != null)
            {
                Select(state, null);
                _dragging = state;
                _dragStart = new PointF(state.X, state.Y);
                _dragOffset = new PointF(e.X - state.X, e.Y - state.Y);
            }
            else Select(null, TransitionAt(e.Location));
            _editor.OnGraphSelection();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.Location;
            if (_dragging != null)
            {
                _dragging.X = MathF.Max(0, e.X - _dragOffset.X);
                _dragging.Y = MathF.Max(0, e.Y - _dragOffset.Y);
                Invalidate();
            }
            else if (_linkFrom != null) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging != null)
            {
                var to = new PointF(_dragging.X, _dragging.Y);
                if (to != _dragStart) _editor.OnStateMoved(_dragging, _dragStart, to);
                _dragging = null;
            }
            if (_linkFrom != null)
            {
                if (StateAt(e.Location) is { } target) _editor.AddTransition(_linkFrom, target.Name);
                _linkFrom = null;
                Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (StateAt(e.Location) == null && !AnyBox.Contains(e.Location))
                _editor.AddState(new PointF(e.X - BoxWidth / 2, e.Y - BoxHeight / 2));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var font = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
            using var small = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1f));
            using var line = new Pen(EditorChrome.Muted, 1.6f) { CustomEndCap = new AdjustableArrowCap(4, 5) };
            using var chosenLine = new Pen(EditorChrome.Accent, 2.4f) { CustomEndCap = new AdjustableArrowCap(4, 5) };
            using var text = new SolidBrush(EditorChrome.Text);
            using var muted = new SolidBrush(EditorChrome.Muted);

            foreach (AnimationControllerTransition transition in _editor._asset.Transitions)
            {
                if (Arrow(transition) is not { } arrow) continue;
                bool chosen = transition == SelectedTransition;
                g.DrawLine(chosen ? chosenLine : line, arrow.Start, arrow.End);
                // Beside its own arrow (to the arrow's right), so two arrows' labels do not meet.
                float ax = arrow.End.X - arrow.Start.X, ay = arrow.End.Y - arrow.Start.Y, al = MathF.Max(1, MathF.Sqrt(ax * ax + ay * ay));
                SizeF size = g.MeasureString(transition.Condition, small);
                var middle = new PointF((arrow.Start.X + arrow.End.X) / 2 - ay / al * 12 - size.Width / 2, (arrow.Start.Y + arrow.End.Y) / 2 + ax / al * 12 - size.Height / 2);
                g.DrawString(transition.Condition, small, chosen ? text : muted, middle);
            }
            if (_linkFrom != null && BoxOf(_linkFrom) is { } source)
                g.DrawLine(chosenLine, new PointF(source.X + source.Width / 2, source.Y + source.Height / 2), _mouse);

            DrawBox(g, AnyBox, "Any state", null, false, false, font, small, text, muted);
            foreach (AnimationControllerState state in _editor._asset.States)
            {
                string detail = !string.IsNullOrWhiteSpace(state.BlendParameter) && state.Blend is { Count: > 0 }
                    ? "blend by " + state.BlendParameter : state.Clip ?? string.Empty;
                bool isDefault = string.Equals(_editor._asset.DefaultState, state.Name, StringComparison.OrdinalIgnoreCase);
                DrawBox(g, Box(state), state.Name, detail, state == SelectedState, isDefault, font, small, text, muted);
            }
        }

        private static void DrawBox(Graphics g, RectangleF box, string title, string? detail, bool chosen, bool isDefault,
            Font font, Font small, Brush text, Brush muted)
        {
            using GraphicsPath path = Rounded(box, 8);
            using var fill = new SolidBrush(isDefault ? EditorChrome.AccentSoft : EditorChrome.Raised);
            using var border = new Pen(chosen ? EditorChrome.Accent : EditorChrome.Border, chosen ? 2.4f : 1.2f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(title, font, text, box.X + 10, box.Y + 6);
            if (!string.IsNullOrEmpty(detail)) g.DrawString(detail, small, muted, box.X + 10, box.Y + 24);
        }

        private static GraphicsPath Rounded(RectangleF box, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(box.X, box.Y, d, d, 180, 90);
            path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
            path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
            path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
