using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using Genesis.Application.Core.Shaders;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets.ShaderNodes;

/// <summary>
/// The Shader editor's node canvas: nodes with their ports, and the wires between them. It draws
/// the graph it is given and turns the mouse and keyboard into graph edits, which it hands to
/// <see cref="Edit"/> so the editor records each as one undo step and writes the shader's code
/// again. Pan by dragging the background (or with the middle button), zoom with the wheel; drag
/// from a port to wire it; right-click, double-click or Space to add a node.
/// </summary>
public sealed class ShaderGraphView : Control
{
    private const float NodeWidth = 176f;
    private const float HeaderHeight = 26f;
    private const float RowHeight = 22f;
    private const float PortRadius = 5.5f;
    private const float PortHitRadius = 10f;

    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PointF> _moveStart = new(StringComparer.Ordinal);
    private readonly Dictionary<float, Font> _fonts = [];
    private ShaderGraphDocument? _graph;
    private PointF _pan = new(40, 40);
    private float _zoom = 1f;
    private DragMode _drag;
    private Point _dragOrigin;
    private PointF _dragGraphOrigin;
    private PointF _wireEnd;
    private PortRef? _wireFrom;
    private PortRef? _detached;
    private bool _dragMoved;

    private enum DragMode { None, Pan, Move, Wire }

    /// <summary>One port of one node; <see cref="Output"/> says which side.</summary>
    public readonly record struct PortRef(string Node, string Port, bool Output);

    public ShaderGraphView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleName = "Shader node graph";
        AccessibleRole = AccessibleRole.Diagram;
        Name = "ShaderNodeGraph";
    }

    /// <summary>Records one graph edit (a label for the undo history, and the change). False when it was refused.</summary>
    public Func<string, Action<ShaderGraphDocument>, bool>? Edit { get; set; }

    /// <summary>Asks for the node menu at a point of the canvas, with the wire being dragged when there is one.</summary>
    public Action<Point, PortRef?>? AddNodeRequested { get; set; }

    public event EventHandler? SelectionChanged;

    /// <summary>A short message for the editor's status bar (why a wire was refused, what a key does).</summary>
    public event EventHandler<string>? StatusMessage;

    public ShaderGraphDocument? Graph
    {
        get => _graph;
        set
        {
            _graph = value;
            int before = _selected.Count;
            _selected.RemoveWhere(id => _graph?.FindNode(id) is null);
            CancelDrag();
            if (before != _selected.Count) SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public float Zoom => _zoom;
    public PointF Pan => _pan;
    public IReadOnlyCollection<string> SelectedNodeIds => _selected;
    public string? SelectedNodeId => _selected.Count == 1 ? _selected.First() : null;

    public void SelectNode(string? id, bool add = false)
    {
        if (!add) _selected.Clear();
        if (id is not null && _graph?.FindNode(id) is not null) _selected.Add(id);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>The graph point under a point of the control.</summary>
    public PointF ToGraph(Point client) => new(client.X / _zoom - _pan.X, client.Y / _zoom - _pan.Y);

    public Point ToClient(PointF graph) => Point.Round(new PointF((graph.X + _pan.X) * _zoom, (graph.Y + _pan.Y) * _zoom));

    /// <summary>Fits every node in view.</summary>
    public void FrameAll()
    {
        if (_graph is null || _graph.Nodes.Count == 0 || ClientSize.Width < 40 || ClientSize.Height < 40)
        {
            _pan = new PointF(40, 40);
            _zoom = 1f;
            Invalidate();
            return;
        }
        RectangleF bounds = NodeBounds(_graph.Nodes[0]);
        foreach (ShaderGraphNode node in _graph.Nodes) bounds = RectangleF.Union(bounds, NodeBounds(node));
        bounds.Inflate(40, 40);
        _zoom = Math.Clamp(Math.Min(ClientSize.Width / bounds.Width, ClientSize.Height / bounds.Height), 0.35f, 1.25f);
        _pan = new PointF(
            ClientSize.Width / _zoom / 2f - (bounds.X + bounds.Width / 2f),
            ClientSize.Height / _zoom / 2f - (bounds.Y + bounds.Height / 2f));
        Invalidate();
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private static ShaderGraphNodeDefinition? Definition(ShaderGraphNode node) => ShaderGraphCatalog.Find(node.Type);

    public static RectangleF NodeBounds(ShaderGraphNode node)
    {
        ShaderGraphNodeDefinition? definition = Definition(node);
        int rows = Math.Max(1, Math.Max(definition?.Inputs.Count ?? 0, definition?.Outputs.Count ?? 0));
        // A parameter has a line more, for its kind under its ports.
        float caption = definition?.Role == ShaderGraphNodeRole.Parameter ? 14f : 0f;
        return new RectangleF(node.X, node.Y, NodeWidth, HeaderHeight + rows * RowHeight + 8f + caption);
    }

    /// <summary>Where a port's circle is, in graph units.</summary>
    public static PointF PortPosition(ShaderGraphNode node, string port, bool output)
    {
        ShaderGraphNodeDefinition? definition = Definition(node);
        IReadOnlyList<ShaderGraphPort> ports = output ? definition?.Outputs ?? [] : definition?.Inputs ?? [];
        int index = Math.Max(0, ports.ToList().FindIndex(candidate => string.Equals(candidate.Name, port, StringComparison.OrdinalIgnoreCase)));
        return new PointF(output ? node.X + NodeWidth : node.X, node.Y + HeaderHeight + 4f + index * RowHeight + RowHeight / 2f);
    }

    private PortRef? HitPort(PointF graph)
    {
        if (_graph is null) return null;
        float radius = PortHitRadius / Math.Min(1f, _zoom);
        foreach (ShaderGraphNode node in DrawOrder().Reverse())
        {
            ShaderGraphNodeDefinition? definition = Definition(node);
            if (definition is null) continue;
            foreach (ShaderGraphPort port in definition.Outputs)
                if (Distance(PortPosition(node, port.Name, true), graph) <= radius) return new PortRef(node.Id, port.Name, true);
            foreach (ShaderGraphPort port in definition.Inputs)
                if (Distance(PortPosition(node, port.Name, false), graph) <= radius) return new PortRef(node.Id, port.Name, false);
        }
        return null;
    }

    /// <summary>The nodes from the bottom up: selected ones are drawn last, on top (the document's order is left alone).</summary>
    private IEnumerable<ShaderGraphNode> DrawOrder() => _graph is null
        ? []
        : _graph.Nodes.Where(node => !_selected.Contains(node.Id)).Concat(_graph.Nodes.Where(node => _selected.Contains(node.Id)));

    private ShaderGraphNode? HitNode(PointF graph) =>
        DrawOrder().Reverse().FirstOrDefault(node => NodeBounds(node).Contains(graph));

    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ── Mouse and keys ───────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        _dragOrigin = e.Location;
        _dragGraphOrigin = ToGraph(e.Location);
        _dragMoved = false;
        if (_graph is null) return;
        if (e.Button == MouseButtons.Middle)
        {
            _drag = DragMode.Pan;
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        PointF graph = _dragGraphOrigin;
        if (HitPort(graph) is { } port)
        {
            if (port.Output)
            {
                _wireFrom = port;
            }
            else if (_graph.FindInput(port.Node, port.Port) is { } wire)
            {
                // Picking up a wired input carries its wire: drop it on another input or let it go.
                _wireFrom = new PortRef(wire.FromNode, wire.FromPort, true);
                _detached = port;
            }
            else
            {
                _wireFrom = port;
            }
            _wireEnd = graph;
            _drag = DragMode.Wire;
            Invalidate();
            return;
        }

        if (HitNode(graph) is { } node)
        {
            bool add = (ModifierKeys & (Keys.Control | Keys.Shift)) != 0;
            if (add && _selected.Contains(node.Id)) _selected.Remove(node.Id);
            else
            {
                if (!add && !_selected.Contains(node.Id)) _selected.Clear();
                _selected.Add(node.Id);
            }
            _moveStart.Clear();
            foreach (string id in _selected)
                if (_graph.FindNode(id) is { } selected) _moveStart[id] = new PointF(selected.X, selected.Y);
            _drag = DragMode.Move;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            return;
        }

        if ((ModifierKeys & (Keys.Control | Keys.Shift)) == 0 && _selected.Count > 0)
        {
            _selected.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        _drag = DragMode.Pan;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Math.Abs(e.X - _dragOrigin.X) + Math.Abs(e.Y - _dragOrigin.Y) > 3) _dragMoved = true;
        switch (_drag)
        {
            case DragMode.Pan:
                _pan = new PointF(_pan.X + (e.X - _dragOrigin.X) / _zoom, _pan.Y + (e.Y - _dragOrigin.Y) / _zoom);
                _dragOrigin = e.Location;
                Invalidate();
                break;
            case DragMode.Move when _graph is not null:
                PointF now = ToGraph(e.Location);
                foreach ((string id, PointF start) in _moveStart)
                {
                    if (_graph.FindNode(id) is not { } node) continue;
                    node.X = start.X + now.X - _dragGraphOrigin.X;
                    node.Y = start.Y + now.Y - _dragGraphOrigin.Y;
                }
                Invalidate();
                break;
            case DragMode.Wire:
                _wireEnd = ToGraph(e.Location);
                Invalidate();
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        DragMode drag = _drag;
        _drag = DragMode.None;
        if (_graph is null) return;
        if (e.Button == MouseButtons.Right && !_dragMoved)
        {
            ShowContextMenu(e.Location);
            return;
        }

        if (drag == DragMode.Move)
        {
            FinishMove();
        }
        else if (drag == DragMode.Wire && _wireFrom is { } from)
        {
            PortRef? detached = _detached;
            _wireFrom = null;
            _detached = null;
            FinishWire(from, detached, ToGraph(e.Location), e.Location);
        }
        Invalidate();
    }

    private void FinishMove()
    {
        if (_graph is null || !_dragMoved || _moveStart.Count == 0) return;
        Dictionary<string, PointF> end = [];
        foreach (string id in _moveStart.Keys)
        {
            if (_graph.FindNode(id) is not { } node) continue;
            end[id] = new PointF(node.X, node.Y);
            // Back where it was, so the edit records the move as one step.
            node.X = _moveStart[id].X;
            node.Y = _moveStart[id].Y;
        }
        Dictionary<string, PointF> start = new(_moveStart);
        _moveStart.Clear();
        Edit?.Invoke(end.Count == 1 ? "Move shader node" : "Move shader nodes", graph =>
        {
            foreach ((string id, PointF point) in end)
            {
                if (graph.FindNode(id) is not { } node) continue;
                node.X = MathF.Round(point.X);
                node.Y = MathF.Round(point.Y);
            }
        });
    }

    private void FinishWire(PortRef from, PortRef? detached, PointF graphPoint, Point client)
    {
        if (_graph is null) return;
        PortRef? target = HitPort(graphPoint);
        if (target is { } port && port.Output != from.Output)
        {
            PortRef output = from.Output ? from : port;
            PortRef input = from.Output ? port : from;
            if (detached is { } old && old == input) return;
            string? problem = ShaderGraphDocuments.ConnectionProblem(_graph, output.Node, output.Port, input.Node, input.Port);
            if (problem is not null)
            {
                StatusMessage?.Invoke(this, problem);
                return;
            }
            Edit?.Invoke("Connect shader nodes", graph =>
            {
                if (detached is { } moved) ShaderGraphDocuments.Disconnect(graph, moved.Node, moved.Port);
                ShaderGraphDocuments.Connect(graph, output.Node, output.Port, input.Node, input.Port);
            });
            return;
        }

        if (detached is { } released)
        {
            Edit?.Invoke("Disconnect shader nodes", graph => ShaderGraphDocuments.Disconnect(graph, released.Node, released.Port));
            return;
        }
        if (_dragMoved && target is null && HitNode(graphPoint) is null)
            AddNodeRequested?.Invoke(client, from);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_graph is not null && e.Button == MouseButtons.Left && HitNode(ToGraph(e.Location)) is null)
            AddNodeRequested?.Invoke(e.Location, null);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        PointF anchor = ToGraph(e.Location);
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.12f : 1f / 1.12f), 0.3f, 2.2f);
        _pan = new PointF(e.X / _zoom - anchor.X, e.Y / _zoom - anchor.Y);
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Delete or Keys.Back or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Delete or Keys.Back:
                DeleteSelection();
                e.Handled = true;
                break;
            case Keys.Space:
                Point cursor = PointToClient(Cursor.Position);
                AddNodeRequested?.Invoke(ClientRectangle.Contains(cursor) ? cursor : new Point(ClientSize.Width / 2, ClientSize.Height / 2), null);
                e.Handled = true;
                break;
            case Keys.F:
                FrameAll();
                e.Handled = true;
                break;
            case Keys.Escape:
                CancelDrag();
                break;
        }
    }

    /// <summary>Deletes the selected nodes and their wires (the Output node stays).</summary>
    public bool DeleteSelection()
    {
        if (_graph is null || _selected.Count == 0) return false;
        string[] ids = _selected.ToArray();
        if (ids.All(id => string.Equals(_graph.FindNode(id)?.Type, ShaderGraphCatalog.OutputType, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage?.Invoke(this, "The Output node stays: it is what the shader gives the engine.");
            return false;
        }
        bool done = Edit?.Invoke(ids.Length == 1 ? "Delete shader node" : "Delete shader nodes",
            graph => ShaderGraphDocuments.RemoveNodes(graph, ids)) == true;
        if (done)
        {
            _selected.RemoveWhere(id => _graph?.FindNode(id) is null);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        return done;
    }

    private void CancelDrag()
    {
        if (_drag == DragMode.Move && _graph is not null)
        {
            foreach ((string id, PointF start) in _moveStart)
            {
                if (_graph.FindNode(id) is { } node) { node.X = start.X; node.Y = start.Y; }
            }
        }
        _drag = DragMode.None;
        _moveStart.Clear();
        _wireFrom = null;
        _detached = null;
        Invalidate();
    }

    private void ShowContextMenu(Point client)
    {
        if (_graph is null) return;
        PointF graph = ToGraph(client);
        ContextMenuStrip menu = new();
        if (HitPort(graph) is { Output: false } input && _graph.FindInput(input.Node, input.Port) is not null)
        {
            menu.Items.Add("Disconnect", null, (_, _) =>
                Edit?.Invoke("Disconnect shader nodes", document => ShaderGraphDocuments.Disconnect(document, input.Node, input.Port)));
        }
        if (HitNode(graph) is { } node)
        {
            if (!_selected.Contains(node.Id)) SelectNode(node.Id);
            menu.Items.Add("Delete", null, (_, _) => DeleteSelection()).Enabled =
                !string.Equals(node.Type, ShaderGraphCatalog.OutputType, StringComparison.OrdinalIgnoreCase);
        }
        menu.Items.Add("Add node…", null, (_, _) => AddNodeRequested?.Invoke(client, null));
        menu.Items.Add("Frame all", null, (_, _) => FrameAll());
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, client);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    public static Color PortColour(ShaderGraphValueType type) => type switch
    {
        ShaderGraphValueType.Float => Color.FromArgb(160, 168, 186),
        ShaderGraphValueType.Float2 => Color.FromArgb(120, 214, 140),
        ShaderGraphValueType.Float3 => Color.FromArgb(240, 206, 96),
        ShaderGraphValueType.Float4 => Color.FromArgb(214, 140, 236),
        ShaderGraphValueType.Texture => Color.FromArgb(240, 120, 110),
        _ => Color.FromArgb(120, 186, 246),
    };

    private static Color CategoryColour(ShaderGraphNodeRole role) => role switch
    {
        ShaderGraphNodeRole.Input => Color.FromArgb(52, 112, 158),
        ShaderGraphNodeRole.Constant => Color.FromArgb(92, 98, 116),
        ShaderGraphNodeRole.Parameter => Color.FromArgb(46, 132, 102),
        ShaderGraphNodeRole.Texture => Color.FromArgb(160, 82, 70),
        ShaderGraphNodeRole.Output => Color.FromArgb(150, 92, 190),
        _ => Color.FromArgb(70, 82, 120),
    };

    private Font ScaledFont(float scale)
    {
        float size = MathF.Round(Math.Max(5f, EditorChrome.SmallFont.SizeInPoints * _zoom * scale) * 4f) / 4f;
        if (!_fonts.TryGetValue(size, out Font? font))
        {
            font = new Font(EditorChrome.SmallFont.FontFamily, size, scale > 1f ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            _fonts[size] = font;
        }
        return font;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(EditorChrome.Canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawGrid(g);
        if (_graph is null) return;

        foreach (ShaderGraphConnection wire in _graph.Connections)
        {
            ShaderGraphNode? from = _graph.FindNode(wire.FromNode);
            ShaderGraphNode? to = _graph.FindNode(wire.ToNode);
            if (from is null || to is null) continue;
            if (_detached is { } lifted && lifted.Node == wire.ToNode && string.Equals(lifted.Port, wire.ToPort, StringComparison.OrdinalIgnoreCase)) continue;
            ShaderGraphValueType type = Definition(from)?.FindOutput(wire.FromPort)?.Type ?? ShaderGraphValueType.Float;
            bool lit = _selected.Contains(from.Id) || _selected.Contains(to.Id);
            DrawWire(g, PortPosition(from, wire.FromPort, true), PortPosition(to, wire.ToPort, false), PortColour(type), lit);
        }

        foreach (ShaderGraphNode node in DrawOrder()) DrawNode(g, node);

        if (_drag == DragMode.Wire && _wireFrom is { } dragged && _graph.FindNode(dragged.Node) is { } origin)
        {
            PointF start = PortPosition(origin, dragged.Port, dragged.Output);
            ShaderGraphPort? port = dragged.Output ? Definition(origin)?.FindOutput(dragged.Port) : Definition(origin)?.FindInput(dragged.Port);
            Color colour = PortColour(port?.Type ?? ShaderGraphValueType.Float);
            if (dragged.Output) DrawWire(g, start, _wireEnd, colour, true);
            else DrawWire(g, _wireEnd, start, colour, true);
        }
    }

    private void DrawGrid(Graphics g)
    {
        float step = 24f * _zoom;
        if (step < 6f) return;
        using Pen minor = new(Color.FromArgb(EditorChrome.IsDark ? 18 : 26, EditorChrome.Text));
        using Pen major = new(Color.FromArgb(EditorChrome.IsDark ? 34 : 44, EditorChrome.Text));
        float offsetX = (_pan.X * _zoom) % step, offsetY = (_pan.Y * _zoom) % step;
        int column = (int)MathF.Floor(-_pan.X / 24f);
        for (float x = offsetX; x < ClientSize.Width; x += step, column++)
            g.DrawLine(column % 5 == 0 ? major : minor, x, 0, x, ClientSize.Height);
        int row = (int)MathF.Floor(-_pan.Y / 24f);
        for (float y = offsetY; y < ClientSize.Height; y += step, row++)
            g.DrawLine(row % 5 == 0 ? major : minor, 0, y, ClientSize.Width, y);
    }

    private void DrawWire(Graphics g, PointF fromGraph, PointF toGraph, Color colour, bool lit)
    {
        PointF a = ToClient(fromGraph), b = ToClient(toGraph);
        float reach = Math.Max(30f * _zoom, Math.Abs(b.X - a.X) * 0.5f);
        using Pen pen = new(lit ? colour : Color.FromArgb(190, colour), (lit ? 2.6f : 1.8f) * Math.Max(0.6f, _zoom));
        g.DrawBezier(pen, a, new PointF(a.X + reach, a.Y), new PointF(b.X - reach, b.Y), b);
    }

    private void DrawNode(Graphics g, ShaderGraphNode node)
    {
        ShaderGraphNodeDefinition? definition = Definition(node);
        RectangleF graphBounds = NodeBounds(node);
        Point topLeft = ToClient(graphBounds.Location);
        RectangleF bounds = new(topLeft.X, topLeft.Y, graphBounds.Width * _zoom, graphBounds.Height * _zoom);
        if (!RectangleF.Inflate(bounds, 40, 40).IntersectsWith(ClientRectangle)) return;
        float header = HeaderHeight * _zoom;
        bool selected = _selected.Contains(node.Id);

        using GraphicsPath shape = Rounded(bounds, 6f * _zoom);
        using (SolidBrush body = new(EditorChrome.Raised)) g.FillPath(body, shape);
        RectangleF headerRect = new(bounds.X, bounds.Y, bounds.Width, header);
        using (GraphicsPath top = Rounded(headerRect, 6f * _zoom, bottom: false))
        using (SolidBrush fill = new(definition is null ? EditorChrome.Error : CategoryColour(definition.Role)))
            g.FillPath(fill, top);
        using (Pen border = new(selected ? EditorChrome.Accent : EditorChrome.Border, selected ? 2.2f : 1f))
            g.DrawPath(border, shape);

        string title = definition?.Title ?? node.Type + " (unknown)";
        if (definition?.Role == ShaderGraphNodeRole.Parameter && node.Name.Length > 0) title = node.Name;
        else if (definition?.Type == "SampleTexture" && node.Mode is { Length: > 0 } mode && mode != "Colour") title += " · " + mode;
        else if (definition?.Role == ShaderGraphNodeRole.Constant) title += " " + ValueText(node.Value);
        TextRenderer.DrawText(g, title, ScaledFont(1.08f), Rectangle.Round(new RectangleF(bounds.X + 8 * _zoom, bounds.Y, bounds.Width - 16 * _zoom, header)),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (definition is null) return;

        if (definition.Role == ShaderGraphNodeRole.Parameter)
        {
            // A parameter's kind in small letters under its name, as the Inspector will show it.
            TextRenderer.DrawText(g, definition.Title, ScaledFont(0.8f), Rectangle.Round(new RectangleF(bounds.X, bounds.Bottom - 15 * _zoom, bounds.Width - 8 * _zoom, 14 * _zoom)),
                EditorChrome.Muted, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        }

        Font font = ScaledFont(1f);
        foreach (ShaderGraphPort port in definition.Inputs)
        {
            PointF centre = ToClient(PortPosition(node, port.Name, false));
            bool wired = _graph!.FindInput(node.Id, port.Name) is not null;
            DrawPort(g, centre, port.Type, wired);
            string label = port.DisplayName;
            if (!wired && port.Type != ShaderGraphValueType.Texture && node.Inputs.TryGetValue(port.Name, out float[]? own)) label += "  " + ValueText(own);
            else if (!wired && port.Source != ShaderGraphDefaultSource.Constant) label += port.Source switch
            {
                ShaderGraphDefaultSource.Uv => "  (mesh UV)",
                ShaderGraphDefaultSource.Normal => "  (mesh normal)",
                ShaderGraphDefaultSource.ViewDirection => "  (to camera)",
                _ => "  (main picture)",
            };
            TextRenderer.DrawText(g, label, font, Rectangle.Round(new RectangleF(centre.X + 9 * _zoom, centre.Y - RowHeight * _zoom / 2f, bounds.Width * 0.62f, RowHeight * _zoom)),
                wired ? EditorChrome.Text : EditorChrome.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        foreach (ShaderGraphPort port in definition.Outputs)
        {
            PointF centre = ToClient(PortPosition(node, port.Name, true));
            DrawPort(g, centre, port.Type, _graph!.Connections.Any(wire => wire.FromNode == node.Id && string.Equals(wire.FromPort, port.Name, StringComparison.OrdinalIgnoreCase)));
            TextRenderer.DrawText(g, port.DisplayName, font, Rectangle.Round(new RectangleF(bounds.X + bounds.Width * 0.3f, centre.Y - RowHeight * _zoom / 2f, bounds.Width * 0.7f - 9 * _zoom, RowHeight * _zoom)),
                EditorChrome.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private void DrawPort(Graphics g, PointF centre, ShaderGraphValueType type, bool wired)
    {
        float radius = PortRadius * Math.Max(0.7f, _zoom);
        RectangleF circle = new(centre.X - radius, centre.Y - radius, radius * 2, radius * 2);
        Color colour = PortColour(type);
        if (wired)
        {
            using SolidBrush fill = new(colour);
            g.FillEllipse(fill, circle);
        }
        else
        {
            using SolidBrush fill = new(EditorChrome.Surface);
            g.FillEllipse(fill, circle);
        }
        using Pen ring = new(colour, 1.6f);
        g.DrawEllipse(ring, circle);
    }

    private static string ValueText(float[]? values)
    {
        if (values is not { Length: > 0 }) return string.Empty;
        return values.Length == 1
            ? values[0].ToString("0.###", CultureInfo.InvariantCulture)
            : "(" + string.Join(", ", values.Select(value => value.ToString("0.##", CultureInfo.InvariantCulture))) + ")";
    }

    private static GraphicsPath Rounded(RectangleF bounds, float radius, bool bottom = true)
    {
        GraphicsPath path = new();
        float diameter = Math.Max(1f, radius * 2f);
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        if (bottom)
        {
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        }
        else
        {
            path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        }
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Font font in _fonts.Values) font.Dispose();
            _fonts.Clear();
        }
        base.Dispose(disposing);
    }
}
