using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

public sealed partial class VisualActionGraphCanvas
{
    public sealed record NodePosition(float X, float Y);
    public sealed record CommentFrame(string Title, float X, float Y, float Width, float Height, string[]? Members = null);
    public sealed record BlueprintLayout(Dictionary<string, NodePosition> Nodes, List<CommentFrame> Comments);
    private BlueprintLayout _layout = new([], []);
    private string _eventCaption = "Create";
    private bool _routineEntry;
    private string _routineReturnType = "Void";
    private readonly List<(string Name, BlueprintValueType Type)> _routineParameters = [];
    private readonly Dictionary<(string Id, string Name), RectangleF> _fieldHits = [];
    private readonly Dictionary<(string Id, string Name), PointF> _inputPins = [];
    private readonly Dictionary<string, PointF> _outputPins = [];
    private TextBox? _inlineEditor;
    private string? _movingNode, _wireSource;
    private int _movingComment = -1;
    private Dictionary<string, NodePosition>? _commentChildren;
    private PointF _commentStart;
    private bool _executionWire;
    private PointF _pointerGraph, _moveOffset;
    private bool _panningGraph;
    private Point _panStart, _panScroll;
    private RectangleF _structuredEventBounds;
    public event Action<string, string, string>? ParameterEdited;
    public event Action<string, string>? BodyEdited;
    public event Action<string, string>? ConditionEdited;
    public event Action<string, string>? AssetPickRequested;
    public event Action<string, string, string>? DataConnectionRequested;
    public event Action<string, string>? ExecutionConnectionRequested;
    public event Action<string>? LayoutChanged;
    public IReadOnlyDictionary<string, RectangleF> NodeBounds => _nodeBounds;

    public void SetBlueprintSource(string source, string eventCaption)
    {
        _eventCaption = eventCaption;
        var match = Regex.Match(source, @"(?m)^// @blueprint (.*)$");
        try { _layout = match.Success ? JsonSerializer.Deserialize<BlueprintLayout>(match.Groups[1].Value) ?? new([], []) : new([], []); }
        catch (JsonException) { _layout = new([], []); }
        // SetDocument runs immediately before this method. Preserve the structured-flow extent
        // when the source contains managed conditions; the freeform blueprint extent only knows
        // about ordinary node positions and would otherwise collapse a deep flow back to 500 px.
        UpdateExtent(); Invalidate();
    }

    internal void SetRoutineHeader(string name, IEnumerable<(string Name, BlueprintValueType Type)> parameters, string returnType)
    {
        _routineEntry = true;
        _eventCaption = name;
        _routineReturnType = string.IsNullOrWhiteSpace(returnType) ? "Void" : returnType;
        _routineParameters.Clear();
        _routineParameters.AddRange(parameters);
        UpdateExtent();
        Invalidate();
    }

    public void ClearRoutineHeader()
    {
        _routineEntry = false;
        _routineParameters.Clear();
        Invalidate();
    }

    public void AddComment(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var bounds = _nodeBounds.Values.Append(EventBounds).Aggregate(RectangleF.Union);
        _layout.Comments.Add(new(title, Math.Max(0, bounds.X - 22), Math.Max(0, bounds.Y - 48), bounds.Width + 44, bounds.Height + 74, _blocks.Select(block => block.Id).Append("$event").ToArray()));
        PersistLayout();
    }

    public void SetNodePosition(string id, PointF position)
    {
        _layout.Nodes[id] = new(Math.Max(16, position.X), Math.Max(48, position.Y)); PersistLayout();
    }

    private void PersistLayout() { LayoutChanged?.Invoke(JsonSerializer.Serialize(_layout)); UpdateBlueprintExtent(); Invalidate(); }
    private PointF GraphPoint(Point point) => new((point.X - AutoScrollPosition.X) / _zoom, (point.Y - AutoScrollPosition.Y) / _zoom);
    private Rectangle ClientBounds(RectangleF bounds) => Rectangle.Round(new(bounds.X * _zoom + AutoScrollPosition.X, bounds.Y * _zoom + AutoScrollPosition.Y, bounds.Width * _zoom, bounds.Height * _zoom));
    private RectangleF EventBounds => _flows.Count > 0 && !_structuredEventBounds.IsEmpty ? _structuredEventBounds
        : _layout.Nodes.TryGetValue("$event", out var point)
            ? new(point.X, point.Y, _routineEntry ? 280 : 148, _routineEntry ? 66 + _routineParameters.Count * 24 : 68)
            : new(36, 150, _routineEntry ? 280 : 148, _routineEntry ? 66 + _routineParameters.Count * 24 : 68);
    public IReadOnlyList<CommentFrame> CommentFrames => _layout.Comments;
    private RectangleF BlueprintBounds(VisualActionBlock block, int index)
    {
        var position = _layout.Nodes.GetValueOrDefault(block.Id) ?? new NodePosition(230 + index % 2 * 270, 150 + index / 2 * (NodeHeight + 80));
        var bounds = new RectangleF(position.X, position.Y, NodeWidth, 86 + Math.Max(1, block.Parameters.Count) * 29 + (block.Body.Length > 0 ? 64 : 0));
        if (!_layout.Nodes.ContainsKey(block.Id))
        {
            for (int attempt = 0; attempt < _blocks.Count; attempt++)
            {
                if (!_blocks.Where(other => other.Id != block.Id && _layout.Nodes.ContainsKey(other.Id)).Any(other =>
                    RectangleF.Inflate(new RectangleF(_layout.Nodes[other.Id].X, _layout.Nodes[other.Id].Y, NodeWidth,
                        86 + Math.Max(1, other.Parameters.Count) * 29 + (other.Body.Length > 0 ? 64 : 0)), 15, 15).IntersectsWith(bounds))) break;
                bounds.Y += NodeHeight + 80;
            }
        }
        return bounds;
    }
    private void UpdateBlueprintExtent()
    {
        FitCommentFrames();
        float right = 760, bottom = 500;
        for (int i = 0; i < _blocks.Count; i++) { var bounds = BlueprintBounds(_blocks[i], i); right = Math.Max(right, bounds.Right + 48); bottom = Math.Max(bottom, bounds.Bottom + 56); }
        foreach (var frame in _layout.Comments) { right = Math.Max(right, frame.X + frame.Width + 20); bottom = Math.Max(bottom, frame.Y + frame.Height + 20); }
        AutoScrollMinSize = new((int)(right * _zoom), (int)(bottom * _zoom));
    }

    private void FitCommentFrames()
    {
        if (_layout.Comments.Count == 0) return;
        var bounds = _flows.Count > 0 ? new Dictionary<string, RectangleF>(_nodeBounds)
            : _blocks.Select((block, index) => (block.Id, Bounds: BlueprintBounds(block, index))).ToDictionary(pair => pair.Id, pair => pair.Bounds);
        if (_flows.Count > 0)
            foreach (var pair in _branchBounds) bounds["$branch:" + pair.Key] = pair.Value;
        bounds["$event"] = EventBounds;
        var owned = _layout.Comments.Where(frame => frame.Members is not null).SelectMany(frame => frame.Members!).ToHashSet();
        for (int index = 0; index < _layout.Comments.Count; index++)
        {
            var frame = _layout.Comments[index];
            var members = (frame.Members ?? bounds.Keys.ToArray()).Where(bounds.ContainsKey).ToHashSet();
            if (index == _layout.Comments.Count - 1) members.UnionWith(bounds.Keys.Where(id => !owned.Contains(id)));
            if (members.Count == 0) continue;
            var area = members.Select(id => bounds[id]).Aggregate(RectangleF.Union);
            float x = Math.Max(0, area.X - 22), y = Math.Max(0, area.Y - 48);
            _layout.Comments[index] = frame with { X = x, Y = y, Width = area.Right - x + 22, Height = area.Bottom - y + 26, Members = members.ToArray() };
        }
    }

    private void DrawBlueprint(Graphics g)
    {
        FitCommentFrames(); _connections.Clear(); _inputPins.Clear(); _outputPins.Clear(); _fieldHits.Clear();
        g.Clear(Color.FromArgb(18, 29, 43)); g.SmoothingMode = SmoothingMode.AntiAlias; DrawGrid(g);
        var state = g.Save(); g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y); g.ScaleTransform(_zoom, _zoom);
        _nodeBounds.Clear(); _branchBounds.Clear();
        DrawCommentFrames(g);
        var start = EventBounds;
        using (var path = Rounded(start, 7)) { using var fill = new SolidBrush(Color.FromArgb(89, 35, 40)); g.FillPath(fill, path); }
        DrawText(g, _routineEntry ? "Function  " + _eventCaption : "On " + _eventCaption,
            new(start.X + 12, start.Y + 9, start.Width - 20, 28), Color.White, true);
        float executionY = start.Y + 48;
        DrawExecutionPin(g, new(start.Right, executionY));
        _outputPins["$event"] = new(start.Right, executionY);
        if (_routineEntry)
        {
            DrawText(g, "Returns " + _routineReturnType, new(start.X + 12, start.Y + 34, start.Width - 30, 22), Color.LightSteelBlue);
            float parameterY = start.Y + 68;
            foreach ((string name, BlueprintValueType type) in _routineParameters)
            {
                PointF pin = new(start.Right - 12, parameterY + 8);
                using SolidBrush brush = new(BlueprintActions.Colour(type));
                g.FillEllipse(brush, pin.X - 5, pin.Y - 5, 10, 10);
                _outputPins["$param:" + name + ":data"] = pin;
                DrawText(g, $"{name} : {type}", new(start.X + 16, parameterY - 3, start.Width - 42, 22), Color.Silver);
                parameterY += 24;
            }
        }
        var previous = new Dictionary<string, (string Id, PointF Pin)> { [""] = ("$event", _outputPins["$event"]) };
        for (int i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i]; var bounds = BlueprintBounds(block, i); _nodeBounds[block.Id] = bounds;
            if (IsPureData(block)) continue;
            if (previous.TryGetValue(block.DetachedChain, out var upstream))
                DrawConnection(g, new(upstream.Id, block.Id, "$exec", upstream.Pin, new(bounds.X, bounds.Y + 48), block.DetachedChain.Length > 0 ? Color.Gray : Color.WhiteSmoke));
            previous[block.DetachedChain] = (block.Id, new(bounds.Right, bounds.Y + 48));
        }
        for (int i = 0; i < _blocks.Count; i++) DrawBlueprintAction(g, _nodeBounds[_blocks[i].Id], _blocks[i], _selectedId == _blocks[i].Id);
        DrawDataConnections(g);
        g.Restore(state);
    }

    private void DrawCommentFrames(Graphics g)
    {
        foreach (var frame in _layout.Comments)
        {
            var bounds = new RectangleF(frame.X, frame.Y, frame.Width, frame.Height);
            using var path = Rounded(bounds, 8); using var fill = new SolidBrush(Color.FromArgb(65, 107, 143, 164)); using var border = new Pen(Color.FromArgb(92, 141, 162));
            g.FillPath(fill, path); g.DrawPath(border, path);
            DrawText(g, frame.Title, new(frame.X + 10, frame.Y + 7, frame.Width - 20, 30), Color.WhiteSmoke, true);
        }
    }

    private void DrawBlueprintAction(Graphics g, RectangleF bounds, VisualActionBlock block, bool selected)
    {
        string category = BlueprintActions.Category(block.Category);
        Color accent = category == "Math" ? Color.FromArgb(39, 142, 157) : category == "Movement" ? Color.FromArgb(46, 100, 146) : category is "Particles" or "Instances" ? Color.FromArgb(60, 126, 82) : Color.FromArgb(94, 78, 128);
        using var path = Rounded(bounds, 7); using var fill = new SolidBrush(Color.FromArgb(18, 25, 30)); using var border = new Pen(selected ? Color.DeepSkyBlue : Color.FromArgb(74, 92, 105), selected ? 2 : 1);
        g.FillPath(fill, path); g.DrawPath(border, path);
        var clip = g.Save(); g.SetClip(path); using (var header = new SolidBrush(accent)) g.FillRectangle(header, bounds.X, bounds.Y, bounds.Width, 34); g.Restore(clip);
        DrawText(g, block.Name + (block.DetachedChain.Length > 0 ? " · Disconnected" : ""), new(bounds.X + 24, bounds.Y + 7, bounds.Width - 30, 25), Color.White, true);
        using (var badge = new SolidBrush(Color.FromArgb(155, Color.White))) g.FillEllipse(badge, bounds.X + 10, bounds.Y + 12, 7, 7);
        if (!IsPureData(block))
        {
            DrawExecutionPin(g, new(bounds.X, bounds.Y + 48)); DrawExecutionPin(g, new(bounds.Right, bounds.Y + 48));
            _inputPins[(block.Id, "$exec")] = new(bounds.X, bounds.Y + 48); _outputPins[block.Id] = new(bounds.Right, bounds.Y + 48);
        }
        float y = bounds.Y + 65;
        foreach (var parameter in block.Parameters)
        {
            var type = BlueprintActions.TypeOf(parameter); var pin = new PointF(bounds.X + 12, y + 11);
            using var brush = new SolidBrush(BlueprintActions.Colour(type)); g.FillEllipse(brush, pin.X - 4, pin.Y - 4, 8, 8);
            _inputPins[(block.Id, parameter.Name)] = pin;
            DrawText(g, parameter.Name, new(bounds.X + 24, y, 76, 24), Color.Silver);
            var field = new RectangleF(bounds.X + 103, y, bounds.Width - 113, 24);
            using var fieldFill = new SolidBrush(Color.FromArgb(34, 42, 49)); g.FillRectangle(fieldFill, field);
            DrawText(g, parameter.Kind == VisualActionValueKind.Asset ? ResourceDisplayName.Format(parameter.Value) : parameter.Value, RectangleF.Inflate(field, -4, -2), Color.WhiteSmoke);
            _fieldHits[(block.Id, parameter.Name)] = field; y += 29;
        }
        if (block.Body.Length > 0)
        {
            var field = new RectangleF(bounds.X + 12, y, bounds.Width - 24, Math.Max(50, bounds.Bottom - y - 12));
            DrawText(g, block.Body, field, Color.LightSteelBlue); _fieldHits[(block.Id, "$body")] = field;
        }
        if (block.ResultVariable.Length > 0)
        {
            var pin = new PointF(bounds.Right - 12, bounds.Bottom - 15);
            using var brush = new SolidBrush(BlueprintActions.Colour(BlueprintActions.OutputType(block))); g.FillEllipse(brush, pin.X - 5, pin.Y - 5, 10, 10);
            _outputPins[block.Id + ":data"] = pin;
            DrawText(g, block.ResultVariable, new(bounds.X + 12, bounds.Bottom - 26, bounds.Width - 38, 23), Color.Silver);
        }
    }
    private static bool IsPureData(VisualActionBlock block) => BlueprintActions.Category(block.Category) == "Math" && block.ResultVariable.Length > 0;

    private static void DrawText(Graphics graphics, string text, RectangleF bounds, Color color, bool bold = false)
    {
        using var brush = new SolidBrush(color); using var font = new Font("Segoe UI", bold ? 10 : 9, bold ? FontStyle.Bold : FontStyle.Regular);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
        graphics.DrawString(text, font, brush, bounds, format);
    }
    private static void DrawExecutionPin(Graphics graphics, PointF point)
    {
        using var brush = new SolidBrush(Color.WhiteSmoke);
        graphics.FillPolygon(brush, new PointF[] { new(point.X - 4, point.Y - 6), new(point.X + 5, point.Y), new(point.X - 4, point.Y + 6) });
    }
    private static void DrawWire(Graphics graphics, PointF from, PointF to, Color colour)
    {
        float bend = Math.Max(45, Math.Abs(to.X - from.X) * .5f);
        using var pen = new Pen(colour, 2); graphics.DrawBezier(pen, from, new(from.X + bend, from.Y), new(to.X - bend, to.Y), to);
    }

    private bool BeginBlueprintInput(MouseEventArgs e)
    {
        var point = GraphPoint(e.Location); _pointerGraph = point;
        if (e.Button is MouseButtons.Middle or MouseButtons.Right)
        {
            _panningGraph = true; _panStart = e.Location; _panScroll = AutoScrollPosition; Capture = true; return true;
        }
        if (e.Button != MouseButtons.Left) return false;
        Focus();
        if (BeginConnectionInput(e, point)) return true;
        if (_flows.Count > 0) return false;
        for (int i = _layout.Comments.Count - 1; i >= 0; i--)
        {
            var frame = _layout.Comments[i];
            if (!new RectangleF(frame.X, frame.Y, frame.Width, 32).Contains(point)) continue;
            if (e.Clicks > 1) { EditComment(i); return true; }
            _movingComment = i; _commentStart = point; _moveOffset = new(point.X - frame.X, point.Y - frame.Y);
            var area = new RectangleF(frame.X, frame.Y, frame.Width, frame.Height);
            _commentChildren = _nodeBounds.Where(pair => area.Contains(pair.Value)).ToDictionary(pair => pair.Key, pair => new NodePosition(pair.Value.X, pair.Value.Y));
            if (frame.Members?.Contains("$event") == true) _commentChildren["$event"] = new(EventBounds.X, EventBounds.Y);
            Capture = true; return true;
        }
        string? hit = HitTest(e.Location); SelectBlock(hit);
        if (hit is null && EventBounds.Contains(point)) { _movingNode = "$event"; _moveOffset = new(point.X - EventBounds.X, point.Y - EventBounds.Y); Capture = true; return true; }
        if (hit is not null) { _movingNode = hit; var bounds = _nodeBounds[hit]; _moveOffset = new(point.X - bounds.X, point.Y - bounds.Y); Capture = true; }
        return true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); _pointerGraph = GraphPoint(e.Location);
        MoveConnectionInput(e);
        if (_panningGraph) AutoScrollPosition = new(-_panScroll.X - e.X + _panStart.X, -_panScroll.Y - e.Y + _panStart.Y);
        if (_movingNode is { } id) _layout.Nodes[id] = new(Math.Max(16, _pointerGraph.X - _moveOffset.X), Math.Max(48, _pointerGraph.Y - _moveOffset.Y));
        if (_movingComment >= 0)
        {
            var frame = _layout.Comments[_movingComment];
            _layout.Comments[_movingComment] = frame with { X = Math.Max(0, _pointerGraph.X - _moveOffset.X), Y = Math.Max(0, _pointerGraph.Y - _moveOffset.Y) };
            foreach (var (node, start) in _commentChildren!) _layout.Nodes[node] = new(Math.Max(16, start.X + _pointerGraph.X - _commentStart.X), Math.Max(48, start.Y + _pointerGraph.Y - _commentStart.Y));
        }
        if (_panningGraph || _movingNode is not null || _wireSource is not null || _wireInput is not null || _movingComment >= 0) { FitCommentFrames(); Invalidate(); }
    }
    private bool EndBlueprintInput(MouseEventArgs e)
    {
        if (EndConnectionInput(e)) return true;
        if (_movingComment >= 0) { _movingComment = -1; _commentChildren = null; Capture = false; PersistLayout(); return true; }
        if (_movingNode is not null) { _movingNode = null; Capture = false; PersistLayout(); return true; }
        if (_panningGraph) { _panningGraph = false; Capture = false; return true; }
        return false;
    }

    private void EditComment(int index)
    {
        SelectBlock(null);
        using var dialog = new DpiAwareForm { Text = "Comment frame", ClientSize = new Size(360, 110), StartPosition = FormStartPosition.CenterParent };
        var title = new TextBox { Dock = DockStyle.Top, Text = _layout.Comments[index].Title }; EditorChrome.StyleField(title);
        var save = new Button { Text = "Save", Dock = DockStyle.Bottom, Height = 32, DialogResult = DialogResult.OK };
        var remove = new Button { Text = "Remove frame", Dock = DockStyle.Bottom, Height = 32, DialogResult = DialogResult.No };
        dialog.Controls.Add(title); dialog.Controls.Add(save); dialog.Controls.Add(remove); dialog.AcceptButton = save;
        var result = dialog.ShowDialog(FindForm());
        if (result == DialogResult.No) _layout.Comments.RemoveAt(index);
        else if (result == DialogResult.OK) _layout.Comments[index] = _layout.Comments[index] with { Title = title.Text };
        else return;
        PersistLayout();
    }

    private void BeginFieldEdit(string id, string name, RectangleF bounds)
    {
        var block = _blocks.FirstOrDefault(candidate => candidate.Id == id);
        var parameter = block?.Parameters.FirstOrDefault(candidate => candidate.Name == name);
        if (parameter?.Kind == VisualActionValueKind.Asset) { AssetPickRequested?.Invoke(id, name); return; }
        if (parameter?.Kind == VisualActionValueKind.Boolean) { ParameterEdited?.Invoke(id, name, parameter.Value == "true" ? "false" : "true"); return; }
        _inlineEditor?.Dispose();
        var box = new TextBox { Bounds = ClientBounds(bounds), Text = name == "$condition" ? _flows.First(flow => flow.Id == id).Condition : name == "$body" ? block?.Body : parameter?.Value ?? "", Multiline = name == "$body" || name == "Body",
            BackColor = Color.FromArgb(32, 44, 56), ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f * _zoom) };
        _inlineEditor = box; Controls.Add(box); box.BringToFront(); box.Focus(); box.SelectAll();
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return; done = true; string value = box.Text ?? ""; _inlineEditor = null; Controls.Remove(box); box.Dispose();
            if (commit) { if (name == "$condition") ConditionEdited?.Invoke(id, value); else if (name == "$body") BodyEdited?.Invoke(id, value); else ParameterEdited?.Invoke(id, name, value); }
            Invalidate();
        }
        box.LostFocus += (_, _) => Finish(true);
        box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Finish(false); }
            else if (e.KeyCode == Keys.Enter && (!box.Multiline || e.Control)) { e.SuppressKeyPress = true; Finish(true); } };
    }
}
