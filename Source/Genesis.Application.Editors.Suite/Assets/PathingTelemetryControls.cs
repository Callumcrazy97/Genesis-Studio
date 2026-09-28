using System.Drawing.Drawing2D;
using System.Numerics;
using Genesis.Runtime.Navigation;

namespace Genesis.Application.Editors.Suite.Assets;

internal sealed class PathingTimelineControl : Control
{
    private PathingAsset _asset = new();
    private float _time;
    private readonly List<(float Time, float Speed)> _samples = [];
    private bool _collisionWarning;
    private Rectangle _graph;
    private PathingSpeedKey? _dragSpeedKey;
    private float CurveDuration => MathF.Max(12f, MathF.Max(_time,
        _asset.Route.SpeedCurve.Count == 0 ? 0 : _asset.Route.SpeedCurve.Max(key => key.Time)));
    private float CurveMaximum => MathF.Max(2.5f,
        _asset.Route.SpeedCurve.Count == 0 ? 0 : _asset.Route.SpeedCurve.Max(key => key.Multiplier));

    public PathingTimelineControl()
    {
        Dock = DockStyle.Fill;
        BackColor = EditorChrome.Surface;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        MouseDown += BeginPointerEdit;
        MouseMove += ContinuePointerEdit;
        MouseUp += EndPointerEdit;
    }

    public void Bind(PathingAsset asset) { _asset = asset; Invalidate(); }
    public event Action<float>? TimeScrubbed;
    public event Action? SpeedCurveChanged;
    public void Reset() { _time = 0f; _samples.Clear(); _collisionWarning = false; Invalidate(); }
    public void SetFrame(float time, float speed, bool collisionWarning)
    {
        _time = MathF.Max(0, time);
        _collisionWarning = collisionWarning;
        if (_samples.Count == 0 || _time - _samples[^1].Time >= 1f / 20f)
        {
            _samples.Add((_time, MathF.Max(0f, speed)));
            while (_samples.Count > 720) _samples.RemoveAt(0);
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush bg = new(EditorChrome.Surface);
        g.FillRectangle(bg, ClientRectangle);
        Font title = EditorChrome.HeadingFont;
        Font small = EditorChrome.SmallFont;
        using SolidBrush text = new(EditorChrome.Text);
        using SolidBrush muted = new(EditorChrome.Muted);
        string stats = $"{_time:0.00}s · {_asset.Route.SpeedAt(_time):0.##} {(_asset.Dimension == PathingDimension.TwoD ? "px/s" : "m/s")}";
        float statsWidth = g.MeasureString(stats, small).Width;
        bool stackHeading = g.MeasureString("PATH SIMULATION", title).Width + statsWidth + 48 > Width;
        g.DrawString("PATH SIMULATION", title, text, 12, 6);
        g.DrawString(stats, small, muted, Math.Max(12, Width - statsWidth - 12), stackHeading ? title.Height + 8 : 8);
        int top = title.Height + small.Height + 20 + (stackHeading ? small.Height + 2 : 0);
        int footer = small.Height * 3 + 16;
        Rectangle graph = _graph = new(30, top, Math.Max(20, Width - 60), Math.Max(10, Height - top - footer));
        using Pen grid = new(Color.FromArgb(65, 83, 101, 116), 1f);
        int divisions = Math.Clamp(graph.Width / Math.Max(50, small.Height * 3), 2, 12);
        for (int i = 0; i <= divisions; i++)
        {
            float x = graph.Left + graph.Width * i / (float)divisions;
            g.DrawLine(grid, x, graph.Top, x, graph.Bottom);
            string tick = (CurveDuration * i / divisions).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            g.DrawString(tick, small, muted, x - g.MeasureString(tick, small).Width / 2, graph.Top - small.Height - 2);
        }
        for (int i = 0; i <= 3; i++)
        {
            float y = graph.Top + graph.Height * i / 3f;
            g.DrawLine(grid, graph.Left, y, graph.Right, y);
        }

        if (_samples.Count >= 2)
        {
            float maxSpeed = MathF.Max(.001f, _asset.Route.Speed * CurveMaximum);
            PointF[] curve = _samples
                .Select(sample => new PointF(
                    graph.Left + graph.Width * Math.Clamp(sample.Time / CurveDuration, 0, 1),
                    graph.Bottom - graph.Height * Math.Clamp(sample.Speed / maxSpeed, 0f, 1f))).ToArray();
            if (curve.Length >= 2)
            {
                using Pen glow = new(Color.FromArgb(60, 48, 220, 240), 7f);
                using Pen line = new(Color.FromArgb(240, 45, 213, 236), 2.2f);
                g.DrawLines(glow, curve); g.DrawLines(line, curve);
            }
        }

        DrawAuthoredSpeedCurve(g, graph, small, muted);

        float playhead = graph.Left + graph.Width * Math.Clamp(_time / CurveDuration, 0, 1);
        using Pen head = new(Color.FromArgb(245, 82, 205, 245), 2f);
        g.DrawLine(head, playhead, graph.Top - 6, playhead, graph.Bottom);
        if (_collisionWarning)
        {
            using SolidBrush warning = new(Color.FromArgb(225, 244, 78, 89));
            string message = "UN-WALKABLE PATH";
            SizeF extent = g.MeasureString(message, small);
            g.FillRectangle(warning, new RectangleF(graph.Right - extent.Width - 12, graph.Top + 4, extent.Width + 8, small.Height + 4));
            g.DrawString(message, small, Brushes.White, graph.Right - extent.Width - 8, graph.Top + 6);
        }
    }

    private void DrawAuthoredSpeedCurve(Graphics graphics, Rectangle graph, Font small, Brush muted)
    {
        IReadOnlyList<PathingSpeedKey> keys = _asset.Route.SpeedCurve;
        if (keys.Count == 0)
        {
            using Pen implicitSpeed = new(Color.FromArgb(150, 246, 183, 76), 1.4f) { DashStyle = DashStyle.Dash };
            float y = graph.Bottom - graph.Height / CurveMaximum;
            graphics.DrawLine(implicitSpeed, graph.Left, y, graph.Right, y);
        }
        else
        {
            PointF[] points = keys.Select(KeyPoint).ToArray();
            if (points.Length > 1)
            {
                using Pen authored = new(Color.FromArgb(245, 246, 183, 76), 2f);
                graphics.DrawLines(authored, points);
            }
            using SolidBrush key = new(Color.FromArgb(255, 246, 183, 76));
            using Pen outline = new(Color.FromArgb(255, 24, 30, 38), 1.5f);
            foreach (PointF point in points)
            {
                graphics.FillEllipse(key, point.X - 5, point.Y - 5, 10, 10);
                graphics.DrawEllipse(outline, point.X - 5, point.Y - 5, 10, 10);
            }
        }
        graphics.DrawString("Amber: speed × · cyan: actual speed", small, muted, 12, graph.Bottom + 4);
        graphics.DrawString("Drag: move key · click: add", small, muted, 12, graph.Bottom + small.Height + 4);
        graphics.DrawString("Right click: remove · Ctrl-drag: scrub time", small, muted, 12, graph.Bottom + small.Height * 2 + 4);

        PointF KeyPoint(PathingSpeedKey key) => new(
            graph.Left + graph.Width * Math.Clamp(key.Time / CurveDuration, 0f, 1f),
            graph.Bottom - graph.Height * Math.Clamp(key.Multiplier / CurveMaximum, 0f, 1f));
    }

    private void BeginPointerEdit(object? sender, MouseEventArgs args)
    {
        if (_graph.Width <= 0 || !_graph.Contains(args.Location)) return;
        if (args.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) != 0)
        {
            Scrub(args.X, args.Y);
            return;
        }
        PathingSpeedKey? hit = _asset.Route.SpeedCurve
            .OrderBy(key => DistanceSquared(KeyPoint(key), args.Location))
            .FirstOrDefault(key => DistanceSquared(KeyPoint(key), args.Location) <= 144f);
        if (args.Button == MouseButtons.Right)
        {
            if (hit is not null)
            {
                _asset.Route.SpeedCurve.Remove(hit);
                SpeedCurveChanged?.Invoke();
                Invalidate();
            }
            return;
        }
        if (args.Button != MouseButtons.Left) return;
        _dragSpeedKey = hit ?? new PathingSpeedKey();
        if (hit is null) _asset.Route.SpeedCurve.Add(_dragSpeedKey);
        SetKeyFromPointer(_dragSpeedKey, args.Location);
        Invalidate();
    }

    private void ContinuePointerEdit(object? sender, MouseEventArgs args)
    {
        if (_dragSpeedKey is null && args.Button == MouseButtons.Left && (ModifierKeys & Keys.Control) != 0)
        {
            Scrub(args.X, args.Y);
            return;
        }
        if (_dragSpeedKey is null || args.Button != MouseButtons.Left) return;
        SetKeyFromPointer(_dragSpeedKey, args.Location);
        Invalidate();
    }

    private void EndPointerEdit(object? sender, MouseEventArgs args)
    {
        if (_dragSpeedKey is null) return;
        SetKeyFromPointer(_dragSpeedKey, args.Location);
        _asset.Route.SpeedCurve.Sort((left, right) => left.Time.CompareTo(right.Time));
        _dragSpeedKey = null;
        SpeedCurveChanged?.Invoke();
        Invalidate();
    }

    private void SetKeyFromPointer(PathingSpeedKey key, Point location)
    {
        key.Time = Math.Clamp((location.X - _graph.Left) / (float)Math.Max(1, _graph.Width), 0f, 1f) * CurveDuration;
        key.Multiplier = (1f - Math.Clamp((location.Y - _graph.Top) / (float)Math.Max(1, _graph.Height), 0f, 1f)) * CurveMaximum;
    }

    private PointF KeyPoint(PathingSpeedKey key) => new(
        _graph.Left + _graph.Width * Math.Clamp(key.Time / CurveDuration, 0f, 1f),
        _graph.Bottom - _graph.Height * Math.Clamp(key.Multiplier / CurveMaximum, 0f, 1f));

    private static float DistanceSquared(PointF left, Point right)
    {
        float x = left.X - right.X;
        float y = left.Y - right.Y;
        return x * x + y * y;
    }

    private void Scrub(int x, int y)
    {
        if (_graph.Width <= 0 || !_graph.Contains(x, y)) return;
        float time = Math.Clamp((x - _graph.Left) / (float)_graph.Width, 0f, 1f) * CurveDuration;
        TimeScrubbed?.Invoke(time);
    }
}

internal sealed class CrowdGraphControl : Control
{
    private IReadOnlyList<PathingPreviewAgent> _agents = [];

    public CrowdGraphControl()
    {
        Dock = DockStyle.Fill;
        BackColor = EditorChrome.Canvas;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void SetAgents(IReadOnlyList<PathingPreviewAgent> agents) { _agents = agents; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(EditorChrome.Canvas);
        if (_agents.Count == 0) return;
        float minX = _agents.Min(a => a.Position.X), maxX = _agents.Max(a => a.Position.X);
        float minZ = _agents.Min(a => a.Position.Z), maxZ = _agents.Max(a => a.Position.Z);
        float spanX = MathF.Max(1, maxX - minX), spanZ = MathF.Max(1, maxZ - minZ);
        PointF Map(Vector3 p) => new(14 + (p.X - minX) / spanX * Math.Max(1, Width - 28),
            14 + (p.Z - minZ) / spanZ * Math.Max(1, Height - 28));
        using Pen link = new(Color.FromArgb(100, 241, 179, 72), 1f);
        for (int i = 0; i < _agents.Count; i++)
        for (int j = i + 1; j < _agents.Count; j++)
            if (Vector3.DistanceSquared(_agents[i].Position, _agents[j].Position) <= 9f)
                g.DrawLine(link, Map(_agents[i].Position), Map(_agents[j].Position));
        for (int i = 0; i < _agents.Count; i++)
        {
            PointF p = Map(_agents[i].Position);
            using SolidBrush dot = new(i == 0 ? Color.FromArgb(245, 244, 78, 88) : Color.FromArgb(245, 42, 210, 213));
            g.FillEllipse(dot, p.X - 5, p.Y - 5, 10, 10);
        }
    }
}
