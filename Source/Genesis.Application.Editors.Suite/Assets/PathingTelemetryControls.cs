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
    private const float CurveDuration = 12f;
    private const float CurveMaximum = 2.5f;

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
        using Font title = new(Font.FontFamily, 9f, FontStyle.Bold);
        using Font small = new(Font.FontFamily, 8f);
        using SolidBrush text = new(EditorChrome.Text);
        using SolidBrush muted = new(EditorChrome.Muted);
        g.DrawString("PATH SIMULATION", title, text, 12, 9);
        g.DrawString($"{_time:0.00}s · {_asset.Route.Speed:0.##} m/s", small, muted, Math.Max(150, Width - 150), 10);
        Rectangle graph = _graph = new(18, 38, Math.Max(20, Width - 36), Math.Max(28, Height - 54));
        using Pen grid = new(Color.FromArgb(65, 83, 101, 116), 1f);
        for (int i = 0; i <= 12; i++)
        {
            float x = graph.Left + graph.Width * i / 12f;
            g.DrawLine(grid, x, graph.Top, x, graph.Bottom);
            g.DrawString((i * 10).ToString(System.Globalization.CultureInfo.InvariantCulture), small, muted, x - 5, graph.Top - 14);
        }
        for (int i = 0; i <= 3; i++)
        {
            float y = graph.Top + graph.Height * i / 3f;
            g.DrawLine(grid, graph.Left, y, graph.Right, y);
        }

        if (_samples.Count >= 2)
        {
            float visibleEnd = MathF.Max(12f, _time);
            float visibleStart = MathF.Max(0f, visibleEnd - 12f);
            float maxSpeed = MathF.Max(1f, MathF.Max(_asset.Route.Speed, _samples.Max(sample => sample.Speed)));
            PointF[] curve = _samples.Where(sample => sample.Time >= visibleStart)
                .Select(sample => new PointF(
                    graph.Left + graph.Width * (sample.Time - visibleStart) / MathF.Max(.001f, visibleEnd - visibleStart),
                    graph.Bottom - graph.Height * Math.Clamp(sample.Speed / maxSpeed, 0f, 1f))).ToArray();
            if (curve.Length >= 2)
            {
                using Pen glow = new(Color.FromArgb(60, 48, 220, 240), 7f);
                using Pen line = new(Color.FromArgb(240, 45, 213, 236), 2.2f);
                g.DrawLines(glow, curve); g.DrawLines(line, curve);
            }
        }

        DrawAuthoredSpeedCurve(g, graph, small, muted);

        int count = Math.Max(1, _asset.Route.Waypoints.Count);
        for (int index = 0; index < count; index++)
        {
            float x = graph.Left + graph.Width * index / Math.Max(1f, count - 1f);
            float y = graph.Bottom - graph.Height * .84f;
            using SolidBrush key = new(Color.FromArgb(245, 246, 183, 76));
            g.FillEllipse(key, x - 4, y - 4, 8, 8);
        }
        float playhead = graph.Left + graph.Width * ((_time % 12f) / 12f);
        using Pen head = new(Color.FromArgb(245, 82, 205, 245), 2f);
        g.DrawLine(head, playhead, graph.Top - 6, playhead, graph.Bottom);
        if (_collisionWarning)
        {
            using SolidBrush warning = new(Color.FromArgb(225, 244, 78, 89));
            g.FillRectangle(warning, new RectangleF(graph.Right - 176, 5, 168, 24));
            g.DrawString("UN-WALKABLE PATH", small, Brushes.White, graph.Right - 164, 10);
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
        graphics.DrawString("Drag amber speed keys · click to add · right-click to remove · Ctrl-drag to scrub", small, muted, graph.Left + 6, graph.Bottom - 17);

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
        float time = Math.Clamp((x - _graph.Left) / (float)_graph.Width, 0f, 1f) * 12f;
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
