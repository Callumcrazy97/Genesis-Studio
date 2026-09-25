using System.Drawing.Drawing2D;

namespace Genesis.Application.Editors.Suite.Objects.VisualActions;

/// <summary>
/// Theme-aware, zoomable event-flow canvas. Ordinary actions follow source order; managed
/// condition regions are shown as brace-like Then/Else packages which compile to ordinary PGSL.
/// </summary>
public sealed partial class VisualActionGraphCanvas : ScrollableControl
{
    private const int NodeWidth = 220;
    private int NodeHeight => 86 + Math.Max(1, _blocks.Select(block => block.Parameters.Count).DefaultIfEmpty(1).Max()) * 29;
    private const int TerminalHeight = 48;
    private const int Gap = 42;

    private readonly Dictionary<string, RectangleF> _nodeBounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RectangleF> _branchBounds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<VisualActionBlock> _blocks = [];
    private IReadOnlyList<VisualActionFlowGroup> _flows = [];
    private string? _selectedId;
    private string? _dragId;
    private Point _dragOrigin;
    private float _zoom = 1f;

    public VisualActionGraphCanvas()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        AllowDrop = true;
        BackColor = EditorChrome.Canvas;
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(this);
    }

    public event Action<string?>? SelectionChanged;
    public event Action<string, int>? MoveRequested;
    public event Action<string>? DeleteRequested;
    public event Action<string>? EditRequested;
    public event Action<object, int>? ItemDropped;
    public event Action<object, string, string>? BranchItemDropped;
    public event Action? EditStarted;
    public event Action? EditCompleted;

    public IReadOnlyList<VisualActionBlock> Blocks => _blocks;
    public int NodeCount => _blocks.Count;
    public bool HasStartNode => true;
    public bool HasEndNode => true;
    public string? SelectedBlockId => _selectedId;
    public float Zoom => _zoom;

    public void SetBlocks(IReadOnlyList<VisualActionBlock> blocks, string? selectedId = null)
        => SetDocument(blocks, [], selectedId);

    public void SetDocument(
        IReadOnlyList<VisualActionBlock> blocks,
        IReadOnlyList<VisualActionFlowGroup> flows,
        string? selectedId = null)
    {
        _blocks = blocks ?? [];
        _flows = flows ?? [];
        if (selectedId is not null) _selectedId = selectedId;
        if (_selectedId is not null && !_blocks.Any(block => block.Id == _selectedId))
            _selectedId = null;
        UpdateExtent();
        Invalidate();
    }

    public bool SelectBlock(string? blockId)
    {
        if (blockId is not null && !_blocks.Any(block => block.Id == blockId)) return false;
        if (string.Equals(_selectedId, blockId, StringComparison.OrdinalIgnoreCase)) return true;
        _selectedId = blockId;
        Invalidate();
        SelectionChanged?.Invoke(blockId);
        return true;
    }

    public void SetZoom(float zoom)
    {
        _zoom = Math.Clamp(zoom, .25f, 1.6f);
        UpdateExtent();
        Invalidate();
    }

    public void FitGraph()
    {
        if (_flows.Count == 0)
        {
            float right = 230, bottom = 250;
            for (int i = 0; i < _blocks.Count; i++) { var bounds = BlueprintBounds(_blocks[i], i); right = Math.Max(right, bounds.Right + 35); bottom = Math.Max(bottom, bounds.Bottom + 35); }
            SetZoom(Math.Min(1.15f, Math.Min((ClientSize.Width - 20f) / right, (ClientSize.Height - 20f) / bottom)));
            AutoScrollPosition = Point.Empty; return;
        }
        int contentHeight = BaseContentHeight();
        float fit = ClientSize.Height <= 0 ? 1f : (ClientSize.Height - 36f) / contentHeight;
        SetZoom(Math.Clamp(fit, .65f, 1.15f));
        AutoScrollPosition = Point.Empty;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            SetZoom(_zoom + (e.Delta > 0 ? .1f : -.1f));
            return;
        }
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (BeginBlueprintInput(e)) return;
        Focus();
        string? hit = HitTest(e.Location);
        SelectBlock(hit);
        if (e.Button == MouseButtons.Left && hit is not null)
        {
            _dragId = hit;
            _dragOrigin = e.Location;
            Capture = true;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (EndBlueprintInput(e)) return;
        base.OnMouseUp(e);
        if (_dragId is not null && Distance(_dragOrigin, e.Location) > 8)
            MoveRequested?.Invoke(_dragId, DropIndex(e.Location));
        _dragId = null;
        Capture = false;
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (_selectedId is not null) EditRequested?.Invoke(_selectedId);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && CancelConnection()) { e.Handled = true; return; }
        if (e.KeyCode == Keys.Delete && DeleteSelectedConnection()) { e.Handled = true; return; }
        if (e.KeyCode == Keys.Delete && _selectedId is not null)
        {
            DeleteRequested?.Invoke(_selectedId);
            e.Handled = true;
        }
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        drgevent.Effect = drgevent.Data?.GetDataPresent(typeof(VisualActionPaletteItem)) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        if (drgevent.Data?.GetData(typeof(VisualActionPaletteItem)) is not { } item) return;
        Point client = PointToClient(new Point(drgevent.X, drgevent.Y));
        EditStarted?.Invoke();
        try
        {
        if (HitBranch(client) is { } branch)
        {
            BranchItemDropped?.Invoke(item, branch.FlowId, branch.Branch);
            return;
        }
        var previousIds = _blocks.Select(block => block.Id).ToHashSet();
        var position = GraphPoint(client);
        ItemDropped?.Invoke(item, _flows.Count == 0 ? _blocks.Count : DropIndex(client));
        if (_flows.Count == 0 && _blocks.FirstOrDefault(block => !previousIds.Contains(block.Id)) is { } added)
            SetNodePosition(added.Id, position);
        }
        finally { EditCompleted?.Invoke(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _connections.Clear(); _fieldHits.Clear(); _inputPins.Clear(); _outputPins.Clear();
        if (_flows.Count == 0) { DrawBlueprint(e.Graphics); return; }
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(18, 29, 43));
        DrawGrid(g);

        GraphicsState state = g.Save();
        g.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
        g.ScaleTransform(_zoom, _zoom);
        _nodeBounds.Clear();
        _branchBounds.Clear();
        var measuring = g.Save(); g.SetClip(Rectangle.Empty);
        DrawStructuredGraph(g); g.Restore(measuring);
        FitCommentFrames(); DrawCommentFrames(g);
        _connections.Clear(); _fieldHits.Clear(); _inputPins.Clear(); _outputPins.Clear();
        _nodeBounds.Clear(); _branchBounds.Clear();
        DrawStructuredGraph(g);
        g.Restore(state);
    }

    private void DrawStructuredGraph(Graphics g)
    {
        float x = Math.Max(32, ((ClientSize.Width / _zoom) - NodeWidth) / 2f);
        float y = 28;
        RectangleF start = new(x + 52, y, NodeWidth - 104, TerminalHeight);
        _structuredEventBounds = start;
        DrawTerminal(g, start, _routineEntry ? "Function " + _eventCaption + " → " + _routineReturnType : "On " + _eventCaption, Color.IndianRed);
        y += TerminalHeight + Gap;

        RectangleF previous = start;
        List<(int Source, VisualActionBlock? Block, VisualActionFlowGroup? Flow)> items = [];
        items.AddRange(_blocks.Where(block => block.FlowId is null)
            .Select(block => (block.SourceStart, (VisualActionBlock?)block, (VisualActionFlowGroup?)null)));
        items.AddRange(_flows.Where(flow => flow.ParentFlowId is null)
            .Select(flow => (flow.SourceStart, (VisualActionBlock?)null, (VisualActionFlowGroup?)flow)));
        foreach ((int _, VisualActionBlock? block, VisualActionFlowGroup? flow) in items.OrderBy(item => item.Source))
        {
            if (flow is not null)
            {
                previous = DrawCondition(g, flow, x, ref y, previous);
                continue;
            }
            if (block is not null)
            {
                RectangleF node = new(x, y, NodeWidth, NodeHeight);
                DrawStructuredConnection(g, previous, node, block, "$event");
                DrawAction(g, node, block, string.Equals(block.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
                _nodeBounds[block.Id] = node;
                if (!IsPureData(block)) previous = node;
                y += NodeHeight + Gap;
            }
        }

        RectangleF end = new(x + 52, y, NodeWidth - 104, TerminalHeight);
        DrawConnector(g, previous, end);
        DrawTerminal(g, end, "END", EditorChrome.Error);
        DrawDataConnections(g);
    }

    private RectangleF DrawCondition(
        Graphics g,
        VisualActionFlowGroup flow,
        float centreX,
        ref float y,
        RectangleF previous)
    {
        const float BranchGap = 34f;
        const float BranchInset = 28f;
        float branchWidth = NodeWidth - BranchInset;
        IReadOnlyList<VisualActionBlock> thenBlocks = _blocks
            .Where(block => block.FlowId == flow.Id && block.FlowBranch == "then").ToArray();
        IReadOnlyList<VisualActionBlock> elseBlocks = _blocks
            .Where(block => block.FlowId == flow.Id && block.FlowBranch == "else").ToArray();
        bool split = flow.HasElse;
        float conditionWidth = 250f;
        RectangleF condition = new(centreX + (NodeWidth - conditionWidth) / 2f, y, conditionWidth, 58);
        DrawConnector(g, previous, condition);
        DrawConditionHeader(g, condition, flow);
        y += 58 + 30;

        float thenX = split ? centreX - branchWidth / 2f - BranchGap / 2f : centreX + BranchInset / 2f;
        float elseX = centreX + NodeWidth / 2f + BranchGap / 2f;
        float thenAdvance = MeasureBranchAdvance(flow.Id, "then");
        float elseAdvance = split ? MeasureBranchAdvance(flow.Id, "else") : 0;
        float branchAdvance = Math.Max(thenAdvance, elseAdvance);
        float branchTop = y;
        RectangleF thenLast = DrawBranch(g, condition, thenX, branchTop, branchWidth, thenAdvance,
            flow.Id, "then", "THEN  {", thenBlocks);
        RectangleF elseLast = split
            ? DrawBranch(g, condition, elseX, branchTop, branchWidth, elseAdvance,
                flow.Id, "else", "ELSE  {", elseBlocks)
            : thenLast;

        y = branchTop + branchAdvance + 6;
        RectangleF merge = new(centreX + NodeWidth / 2f - 34f, y, 68, 30);
        DrawBranchMerge(g, thenLast, merge);
        if (split) DrawBranchMerge(g, elseLast, merge);
        using Font font = new(EditorChrome.CodeFont, FontStyle.Bold);
        TextRenderer.DrawText(g, "}", font, Rectangle.Round(merge), EditorChrome.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        y += merge.Height + Gap;
        return merge;
    }

    private RectangleF DrawBranch(
        Graphics g,
        RectangleF condition,
        float x,
        float y,
        float width,
        float branchAdvance,
        string flowId,
        string branch,
        string label,
        IReadOnlyList<VisualActionBlock> blocks)
    {
        RectangleF package = new(x - 14, y - 25, width + 28,
            Math.Max(44, branchAdvance - Gap) + 42);
        _branchBounds[flowId + "|" + branch] = package;
        using Pen packagePen = new(Color.FromArgb(130, EditorChrome.Border), 1.4f) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(packagePen, Rectangle.Round(package));
        TextRenderer.DrawText(g, label, EditorChrome.CodeFont,
            Rectangle.Round(new RectangleF(package.X + 10, package.Y + 2, package.Width - 20, 22)),
            EditorChrome.Accent, TextFormatFlags.VerticalCenter);

        RectangleF previous = condition;
        List<(int Source, VisualActionBlock? Block, VisualActionFlowGroup? Flow)> items = [];
        items.AddRange(blocks.Select(block =>
            (block.SourceStart, (VisualActionBlock?)block, (VisualActionFlowGroup?)null)));
        items.AddRange(_flows.Where(flow => flow.ParentFlowId == flowId
                                           && flow.ParentBranch == branch)
            .Select(flow =>
                (flow.SourceStart, (VisualActionBlock?)null, (VisualActionFlowGroup?)flow)));
        if (items.Count == 0)
        {
            RectangleF empty = new(x, y, width, 44);
            DrawConnector(g, condition, empty);
            using Pen border = new(EditorChrome.Border, 1f) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(border, Rectangle.Round(empty));
            TextRenderer.DrawText(g, "Drop actions here", EditorChrome.SmallFont, Rectangle.Round(empty),
                EditorChrome.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return empty;
        }

        foreach ((int _, VisualActionBlock? block, VisualActionFlowGroup? nested) in
                 items.OrderBy(item => item.Source))
        {
            if (nested is not null)
            {
                previous = DrawNestedCondition(g, nested, x, ref y, width, previous);
                continue;
            }
            if (block is null) continue;
            RectangleF node = new(x, y, width, NodeHeight);
            DrawStructuredConnection(g, previous, node, block, "$branch:" + flowId + ":" + branch);
            DrawAction(g, node, block, string.Equals(block.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
            _nodeBounds[block.Id] = node;
            if (!IsPureData(block)) previous = node;
            y += NodeHeight + Gap;
        }
        return previous;
    }

    private RectangleF DrawNestedCondition(
        Graphics g,
        VisualActionFlowGroup flow,
        float x,
        ref float y,
        float width,
        RectangleF previous)
    {
        RectangleF header = new(x + 10, y, width - 20, 58);
        DrawConnector(g, previous, header);
        DrawConditionHeader(g, header, flow);
        y += header.Height + 22;

        RectangleF thenLast = DrawNestedBranch(g, header, flow, "then", "THEN  {", x + 12, ref y, width - 24);
        RectangleF elseLast = thenLast;
        if (flow.HasElse)
            elseLast = DrawNestedBranch(g, header, flow, "else", "ELSE  {", x + 12, ref y, width - 24);

        RectangleF merge = new(x + width / 2f - 26, y, 52, 28);
        DrawBranchMerge(g, thenLast, merge);
        if (flow.HasElse) DrawBranchMerge(g, elseLast, merge);
        using Font font = new(EditorChrome.CodeFont, FontStyle.Bold);
        TextRenderer.DrawText(g, "}", font, Rectangle.Round(merge), EditorChrome.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        y += merge.Height + 22;
        return merge;
    }

    private RectangleF DrawNestedBranch(
        Graphics g,
        RectangleF previous,
        VisualActionFlowGroup flow,
        string branch,
        string label,
        float x,
        ref float y,
        float width)
    {
        float startY = y;
        List<(int Source, VisualActionBlock? Block, VisualActionFlowGroup? Flow)> items = [];
        items.AddRange(_blocks.Where(block => block.FlowId == flow.Id && block.FlowBranch == branch)
            .Select(block => (block.SourceStart, (VisualActionBlock?)block, (VisualActionFlowGroup?)null)));
        items.AddRange(_flows.Where(nested => nested.ParentFlowId == flow.Id
                                              && nested.ParentBranch == branch)
            .Select(nested => (nested.SourceStart, (VisualActionBlock?)null, (VisualActionFlowGroup?)nested)));

        RectangleF last = previous;
        if (items.Count == 0)
        {
            RectangleF empty = new(x, y, width, 44);
            DrawConnector(g, previous, empty);
            using Pen border = new(EditorChrome.Border, 1f) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(border, Rectangle.Round(empty));
            TextRenderer.DrawText(g, "Drop actions here", EditorChrome.SmallFont, Rectangle.Round(empty),
                EditorChrome.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            y += empty.Height + Gap;
            last = empty;
        }
        else
        {
            foreach ((int _, VisualActionBlock? block, VisualActionFlowGroup? nested) in
                     items.OrderBy(item => item.Source))
            {
                if (nested is not null)
                {
                    last = DrawNestedCondition(g, nested, x, ref y, width, last);
                    continue;
                }
                if (block is null) continue;
                RectangleF node = new(x, y, width, NodeHeight);
                DrawStructuredConnection(g, last, node, block, "$branch:" + flow.Id + ":" + branch);
                DrawAction(g, node, block, string.Equals(block.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
                _nodeBounds[block.Id] = node;
                if (!IsPureData(block)) last = node;
                y += NodeHeight + Gap;
            }
        }

        RectangleF package = new(x - 8, startY - 24, width + 16,
            Math.Max(44, y - startY - Gap) + 38);
        _branchBounds[flow.Id + "|" + branch] = package;
        using Pen packagePen = new(Color.FromArgb(150, EditorChrome.Border), 1.2f) { DashStyle = DashStyle.Dash };
        g.DrawRectangle(packagePen, Rectangle.Round(package));
        TextRenderer.DrawText(g, label, EditorChrome.CodeFont,
            Rectangle.Round(new RectangleF(package.X + 8, package.Y + 1, package.Width - 16, 22)),
            EditorChrome.Accent, TextFormatFlags.VerticalCenter);
        return last;
    }

    private float MeasureBranchAdvance(string flowId, string branch)
    {
        List<(int Source, VisualActionBlock? Block, VisualActionFlowGroup? Flow)> items = [];
        items.AddRange(_blocks.Where(block => block.FlowId == flowId && block.FlowBranch == branch)
            .Select(block => (block.SourceStart, (VisualActionBlock?)block, (VisualActionFlowGroup?)null)));
        items.AddRange(_flows.Where(flow => flow.ParentFlowId == flowId && flow.ParentBranch == branch)
            .Select(flow => (flow.SourceStart, (VisualActionBlock?)null, (VisualActionFlowGroup?)flow)));
        if (items.Count == 0) return 44 + Gap;

        float advance = 0;
        foreach ((int _, VisualActionBlock? block, VisualActionFlowGroup? nested) in
                 items.OrderBy(item => item.Source))
        {
            if (block is not null) advance += NodeHeight + Gap;
            else if (nested is not null) advance += MeasureNestedConditionAdvance(nested);
        }
        return advance;
    }

    private float MeasureNestedConditionAdvance(VisualActionFlowGroup flow) =>
        58 + 22
        + MeasureBranchAdvance(flow.Id, "then")
        + (flow.HasElse ? MeasureBranchAdvance(flow.Id, "else") : 0)
        + 28 + 22;

    private void DrawConditionHeader(Graphics g, RectangleF bounds, VisualActionFlowGroup flow)
    {
        _fieldHits[(flow.Id, "$condition")] = new(bounds.X + 10, bounds.Y + 26, bounds.Width - 20, 27);
        using GraphicsPath path = Rounded(bounds, 8);
        using SolidBrush fill = new(EditorChrome.Raised);
        using Pen border = new(EditorChrome.Accent, 1.8f);
        using Font titleFont = new(EditorChrome.BaseFont, FontStyle.Bold);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        DrawText(g, "If Condition", new(bounds.X + 12, bounds.Y + 4, bounds.Width - 24, 23), EditorChrome.Accent, true);
        var field = _fieldHits[(flow.Id, "$condition")];
        using var fieldFill = new SolidBrush(Color.FromArgb(34, 42, 49)); g.FillRectangle(fieldFill, field);
        DrawText(g, flow.Condition, RectangleF.Inflate(field, -4, -2), EditorChrome.Text);
    }

    private static void DrawBranchMerge(Graphics g, RectangleF from, RectangleF merge) => DrawConnector(g, from, merge);

    private void DrawGrid(Graphics g)
    {
        int spacing = Math.Max(12, (int)Math.Round(20 * _zoom));
        int offsetX = AutoScrollPosition.X % spacing;
        int offsetY = AutoScrollPosition.Y % spacing;
        using Pen minor = new(Color.FromArgb(35, 65, 89));
        for (int y = offsetY; y < ClientSize.Height; y += spacing) g.DrawLine(minor, 0, y, ClientSize.Width, y);
        for (int x = offsetX; x < ClientSize.Width; x += spacing) g.DrawLine(minor, x, 0, x, ClientSize.Height);
    }

    private static void DrawConnector(Graphics g, RectangleF from, RectangleF to)
    {
        PointF a = new(from.Left + from.Width / 2, from.Bottom);
        PointF b = new(to.Left + to.Width / 2, to.Top);
        float mid = (a.Y + b.Y) / 2;
        using GraphicsPath path = new();
        path.AddBezier(a, new PointF(a.X, mid), new PointF(b.X, mid), b);
        using Pen pen = new(EditorChrome.Border, 2.2f) { EndCap = LineCap.RoundAnchor };
        g.DrawPath(pen, path);
    }

    private static void DrawTerminal(Graphics g, RectangleF bounds, string text, Color colour)
    {
        using GraphicsPath path = Rounded(bounds, 18);
        using SolidBrush fill = new(EditorChrome.Surface);
        using Pen border = new(colour, 1.6f);
        using Font font = new(EditorChrome.BaseFont, FontStyle.Bold);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        using var textBrush = new SolidBrush(EditorChrome.Text);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, textBrush, bounds, format);
    }

    private void DrawAction(Graphics g, RectangleF bounds, VisualActionBlock block, bool selected) => DrawBlueprintAction(g, bounds, block, selected);

    private static void DrawLegacyAction(Graphics g, RectangleF bounds, VisualActionBlock block, bool selected)
    {
        Color accent = CategoryColour(block.Category);
        using GraphicsPath path = Rounded(bounds, 8);
        using GraphicsPath shadowPath = Rounded(new RectangleF(bounds.X + 3, bounds.Y + 4, bounds.Width, bounds.Height), 8);
        using SolidBrush shadow = new(Color.FromArgb(38, Color.Black));
        using SolidBrush fill = new(EditorChrome.Surface);
        using Pen border = new(selected ? EditorChrome.Accent : EditorChrome.Border, selected ? 2.4f : 1f);
        using Font titleFont = new(EditorChrome.BaseFont, FontStyle.Bold);
        g.FillPath(shadow, shadowPath);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        using SolidBrush strip = new(accent);
        g.FillRectangle(strip, bounds.X, bounds.Y, 5, bounds.Height);

        Rectangle title = Rectangle.Round(new RectangleF(bounds.X + 18, bounds.Y + 10, bounds.Width - 34, 24));
        TextRenderer.DrawText(g, block.Name, titleFont, title,
            EditorChrome.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        string details = AnimationGraphSyntax.TryRead(block.Body, out AnimationGraphDefinition graph)
            ? $"{graph.States.Count} states  ·  {graph.Transitions.Count} transitions"
            : block.Body.Length > 0
            ? "Custom PGSL block"
            : string.Join("  ·  ", block.Parameters.Take(3).Select(parameter => $"{parameter.Name}: {parameter.Value}"));
        if (string.IsNullOrWhiteSpace(details)) details = block.CommandName;
        Rectangle subtitle = Rectangle.Round(new RectangleF(bounds.X + 18, bounds.Y + 40, bounds.Width - 34, 24));
        TextRenderer.DrawText(g, details, EditorChrome.SmallFont, subtitle, EditorChrome.Muted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    private string? HitTest(Point client)
    {
        PointF graph = new(
            (client.X - AutoScrollPosition.X) / _zoom,
            (client.Y - AutoScrollPosition.Y) / _zoom);
        return _nodeBounds.FirstOrDefault(pair => pair.Value.Contains(graph)).Key;
    }

    private (string FlowId, string Branch)? HitBranch(Point client)
    {
        PointF graph = new(
            (client.X - AutoScrollPosition.X) / _zoom,
            (client.Y - AutoScrollPosition.Y) / _zoom);
        foreach ((string key, RectangleF bounds) in _branchBounds
                     .Where(pair => pair.Value.Contains(graph))
                     .OrderBy(pair => pair.Value.Width * pair.Value.Height))
        {
            int separator = key.IndexOf('|');
            if (separator > 0) return (key[..separator], key[(separator + 1)..]);
        }
        return null;
    }

    private int DropIndex(Point client)
    {
        float graphY = (client.Y - AutoScrollPosition.Y) / _zoom;
        int index = 0;
        foreach (VisualActionBlock block in _blocks)
        {
            if (_nodeBounds.TryGetValue(block.Id, out RectangleF bounds) && graphY < bounds.Top + bounds.Height / 2)
                return index;
            index++;
        }
        return index;
    }

    private void UpdateExtent()
    {
        if (_flows.Count == 0) { UpdateBlueprintExtent(); return; }
        AutoScrollMinSize = new Size(
            (int)Math.Ceiling(((_flows.Count > 0 ? NodeWidth * 2 + 220 : NodeWidth + 100)) * _zoom),
            (int)Math.Ceiling(BaseContentHeight() * _zoom));
    }

    private int BaseContentHeight()
    {
        int topLevelBlocks = _blocks.Count(block => block.FlowId is null);
        int height = 90 + TerminalHeight * 2 + topLevelBlocks * NodeHeight
            + (topLevelBlocks + 1) * Gap;
        foreach (VisualActionFlowGroup flow in _flows.Where(flow => flow.ParentFlowId is null))
        {
            float branchAdvance = Math.Max(MeasureBranchAdvance(flow.Id, "then"),
                flow.HasElse ? MeasureBranchAdvance(flow.Id, "else") : 0);
            height += (int)Math.Ceiling(150 + branchAdvance);
        }
        return height;
    }

    private static double Distance(Point first, Point second) =>
        Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));

    private static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float diameter = radius * 2;
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color CategoryColour(string category)
    {
        int hash = 17;
        foreach (char character in category ?? string.Empty)
            hash = unchecked((hash * 31) + char.ToUpperInvariant(character));
        Color[] colours =
        [
            Color.FromArgb(0, 145, 220), Color.FromArgb(151, 104, 226),
            Color.FromArgb(42, 181, 121), Color.FromArgb(225, 144, 45),
            Color.FromArgb(218, 91, 120), Color.FromArgb(54, 169, 181),
        ];
        return colours[(hash & int.MaxValue) % colours.Length];
    }
}

/// <summary>Drag payload shared by the palette and graph without exposing storage internals.</summary>
public sealed record VisualActionPaletteItem(
    string Id,
    string Name,
    string Category,
    string Description,
    VisualActionTemplate Template,
    bool IsUserPreset)
{
    public override string ToString() => Name;
}
