using System.Drawing.Drawing2D;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// A small lifetime-curve editor drawn as points (<see cref="ParticleBezierCurve.Keys"/>), shared by
/// every curve field of the module stack. Drag a point; double-click empty space to add one, or a
/// point to switch it between a straight and a smooth join; right-click a point to remove it.
/// Escape cancels a drag. The line drawn is what the runtime samples.
/// </summary>
internal sealed class ParticleKeyCurveEditor : Control
{
    private const int Inset = 8;
    private readonly Func<ParticleBezierCurve?> _curve;
    private readonly Func<float, float> _evaluate;
    private readonly Func<List<ParticleCurveKey>> _ensureKeys;
    private readonly Action _changed;
    private readonly Action _beginGesture;
    private readonly Action _endGesture;
    private readonly Action _cancelGesture;
    private ParticleCurveKey? _dragging;

    public ParticleKeyCurveEditor(ParticleCurveChannel channel, Func<ParticleBezierCurve?> curve, Func<float, float> evaluate,
        Func<List<ParticleCurveKey>> ensureKeys, Action changed, Action beginGesture, Action endGesture, Action cancelGesture)
    {
        _curve = curve;
        _evaluate = evaluate;
        _ensureKeys = ensureKeys;
        _changed = changed;
        _beginGesture = beginGesture; _endGesture = endGesture; _cancelGesture = cancelGesture;
        (Minimum, Maximum) = ParticleModuleCurves.ValueRange(channel);
        DoubleBuffered = true;
        TabStop = true;
        Cursor = Cursors.Cross;
        BackColor = EditorChrome.Canvas;
        AccessibleName = channel + " curve over the particle's life";
        AccessibleRole = AccessibleRole.Graphic;
    }

    public double Minimum { get; }
    public double Maximum { get; }

    /// <summary>The points being shown, or null while the curve is not made of points.</summary>
    public List<ParticleCurveKey>? Points => _curve()?.Keys;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle graph = Graph;
        using (Pen grid = new(Color.FromArgb(60, EditorChrome.Border)))
        {
            for (int i = 0; i <= 4; i++)
            {
                int x = graph.Left + graph.Width * i / 4;
                graphics.DrawLine(grid, x, graph.Top, x, graph.Bottom);
            }
            for (int i = 0; i <= 2; i++)
            {
                int y = graph.Top + graph.Height * i / 2;
                graphics.DrawLine(grid, graph.Left, y, graph.Right, y);
            }
        }
        using (SolidBrush caption = new(EditorChrome.Muted))
        {
            graphics.DrawString(Maximum.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), EditorChrome.SmallFont, caption, graph.Left + 2, graph.Top);
            graphics.DrawString(Minimum.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), EditorChrome.SmallFont, caption,
                graph.Left + 2, graph.Bottom - EditorChrome.SmallFont.Height);
        }
        using (Pen line = new(Enabled ? EditorChrome.Accent : EditorChrome.Muted, 2f))
        {
            PointF previous = ToScreen(0, _evaluate(0));
            for (int x = 1; x <= graph.Width; x++)
            {
                float t = x / (float)Math.Max(1, graph.Width);
                PointF next = ToScreen(t, _evaluate(t));
                graphics.DrawLine(line, previous, next);
                previous = next;
            }
        }
        List<ParticleCurveKey>? keys = Points;
        if (keys is null) return;
        using SolidBrush fill = new(Enabled ? EditorChrome.Text : EditorChrome.Muted);
        using Pen ring = new(EditorChrome.Accent, 2f);
        foreach (ParticleCurveKey key in keys)
        {
            PointF at = ToScreen(key.Time, key.Value);
            RectangleF mark = new(at.X - 4, at.Y - 4, 8, 8);
            if (key.Smooth) graphics.FillEllipse(fill, mark);
            else graphics.FillRectangle(fill, mark);
            if (ReferenceEquals(key, _dragging)) graphics.DrawEllipse(ring, mark.X - 2, mark.Y - 2, 12, 12);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left || !Enabled) return;
        ParticleCurveKey? hit = Hit(e.Location);
        if (hit is null) return;
        _dragging = hit;
        _beginGesture();
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is null || Points is not { } keys) return;
        int index = keys.IndexOf(_dragging);
        if (index < 0) { _dragging = null; return; }
        (double time, double value) = FromScreen(e.Location);
        double low = index > 0 ? keys[index - 1].Time : 0;
        double high = index < keys.Count - 1 ? keys[index + 1].Time : 1;
        _dragging.Time = Math.Round(Math.Clamp(time, low, high), 4);
        _dragging.Value = Math.Round(Math.Clamp(value, Minimum, Maximum), 4);
        _changed();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        EndDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) EndDrag();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape || _dragging is null) return;
        _dragging = null;
        Capture = false;
        _cancelGesture();
        Invalidate();
        e.Handled = true;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        ParticleCurveKey? hit = Hit(e.Location);
        if (hit is not null)
        {
            hit.Smooth = !hit.Smooth;
            _changed();
            Invalidate();
            return;
        }
        List<ParticleCurveKey> keys = _ensureKeys();
        if (keys.Count >= ParticleModuleCurves.MaximumKeys) return;
        (double time, double value) = FromScreen(e.Location);
        int at = keys.FindIndex(key => key.Time > time);
        if (at < 0) at = keys.Count;
        bool smooth = at > 0 && keys[at - 1].Smooth;
        keys.Insert(at, new ParticleCurveKey
        {
            Time = Math.Round(Math.Clamp(time, 0, 1), 4),
            Value = Math.Round(Math.Clamp(value, Minimum, Maximum), 4),
            Smooth = smooth,
        });
        _changed();
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Right || !Enabled || Points is not { Count: > 2 } keys) return;
        ParticleCurveKey? hit = Hit(e.Location);
        if (hit is null) return;
        keys.Remove(hit);
        _changed();
        Invalidate();
    }

    private void EndDrag()
    {
        if (_dragging is null) return;
        _dragging = null;
        Capture = false;
        _endGesture();
        Invalidate();
    }

    private ParticleCurveKey? Hit(Point point)
    {
        // A curve that is not points yet (a Bezier) has none to drag; a double-click converts it.
        if (Points is not { } keys) return null;
        ParticleCurveKey? best = null;
        float nearest = 9f;
        foreach (ParticleCurveKey key in keys)
        {
            PointF at = ToScreen(key.Time, key.Value);
            float distance = MathF.Sqrt((at.X - point.X) * (at.X - point.X) + (at.Y - point.Y) * (at.Y - point.Y));
            if (distance < nearest) { nearest = distance; best = key; }
        }
        return best;
    }

    private Rectangle Graph => new(Inset, Inset / 2, Math.Max(10, Width - Inset * 2), Math.Max(10, Height - Inset));

    private PointF ToScreen(double time, double value)
    {
        Rectangle graph = Graph;
        double span = Math.Max(1e-6, Maximum - Minimum);
        double y = Math.Clamp((value - Minimum) / span, -0.05, 1.05);
        return new PointF(graph.Left + (float)(Math.Clamp(time, 0, 1) * graph.Width), graph.Bottom - (float)(y * graph.Height));
    }

    private (double Time, double Value) FromScreen(Point point)
    {
        Rectangle graph = Graph;
        double time = (point.X - graph.Left) / (double)Math.Max(1, graph.Width);
        double value = Minimum + (graph.Bottom - point.Y) / (double)Math.Max(1, graph.Height) * (Maximum - Minimum);
        return (Math.Clamp(time, 0, 1), value);
    }
}
