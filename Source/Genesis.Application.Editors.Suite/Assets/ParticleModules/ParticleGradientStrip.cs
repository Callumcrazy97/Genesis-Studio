using System.Numerics;
using Genesis.Runtime.Particles;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// The colour and opacity gradient of Colour over life, as a strip with its keys under it. Drag a
/// key along the strip; double-click the strip to add a key or a key to recolour it; right-click a
/// key to remove it (two stay); the mouse wheel over a key changes its opacity.
/// </summary>
internal sealed class ParticleGradientStrip : Control
{
    private const int MarkerHeight = 10;
    private readonly Func<List<ParticleGradientStop>> _stops;
    private readonly Action _changed;
    private readonly Action _beginGesture;
    private readonly Action _endGesture;
    private readonly Action _cancelGesture;
    private ParticleGradientStop? _dragging;

    public ParticleGradientStrip(Func<List<ParticleGradientStop>> stops, Action changed,
        Action beginGesture, Action endGesture, Action cancelGesture)
    {
        _stops = stops;
        _changed = changed;
        _beginGesture = beginGesture; _endGesture = endGesture; _cancelGesture = cancelGesture;
        DoubleBuffered = true;
        TabStop = true;
        Cursor = Cursors.Hand;
        BackColor = EditorChrome.Raised;
        AccessibleName = "Colour and opacity over the particle's life";
        AccessibleRole = AccessibleRole.Graphic;
    }

    /// <summary>Asks for a colour (a dialog in Studio); null keeps the old one.</summary>
    public Func<ParticleColor, ParticleColor?> PickColour { get; set; } = _ => null;

    private Rectangle Strip => new(6, 2, Math.Max(10, Width - 12), Math.Max(8, Height - MarkerHeight - 6));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle strip = Strip;
        List<ParticleGradientStop> stops = _stops();
        using (SolidBrush checkerA = new(EditorChrome.Canvas))
        using (SolidBrush checkerB = new(EditorChrome.Hover))
            for (int y = 0; y < strip.Height; y += 6)
                for (int x = 0; x < strip.Width; x += 6)
                    e.Graphics.FillRectangle(((x / 6 + y / 6) & 1) == 0 ? checkerA : checkerB,
                        strip.Left + x, strip.Top + y, Math.Min(6, strip.Width - x), Math.Min(6, strip.Height - y));
        using (Pen pen = new(Color.White))
            for (int x = 0; x < strip.Width; x++)
            {
                Vector4 colour = Sample(stops, x / (float)Math.Max(1, strip.Width - 1));
                pen.Color = Color.FromArgb((int)(Math.Clamp(colour.W, 0f, 1f) * 255), (int)(Math.Clamp(colour.X, 0f, 1f) * 255),
                    (int)(Math.Clamp(colour.Y, 0f, 1f) * 255), (int)(Math.Clamp(colour.Z, 0f, 1f) * 255));
                e.Graphics.DrawLine(pen, strip.Left + x, strip.Top, strip.Left + x, strip.Bottom);
            }
        using (Pen border = new(EditorChrome.Border)) e.Graphics.DrawRectangle(border, strip);
        foreach (ParticleGradientStop stop in stops)
        {
            if (stop?.Color is null) continue;
            float x = strip.Left + (float)Math.Clamp(stop.Position, 0, 1) * strip.Width;
            PointF[] marker = [new(x, strip.Bottom + 1), new(x - 5, strip.Bottom + MarkerHeight), new(x + 5, strip.Bottom + MarkerHeight)];
            using SolidBrush brush = new(Color.FromArgb(255, (int)(Math.Clamp(stop.Color.R, 0f, 1f) * 255),
                (int)(Math.Clamp(stop.Color.G, 0f, 1f) * 255), (int)(Math.Clamp(stop.Color.B, 0f, 1f) * 255)));
            e.Graphics.FillPolygon(brush, marker);
            using Pen outline = new(ReferenceEquals(stop, _dragging) ? EditorChrome.Accent : EditorChrome.Text);
            e.Graphics.DrawPolygon(outline, marker);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left || !Enabled) return;
        _dragging = Hit(e.X);
        if (_dragging is null) return;
        _beginGesture();
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging is null) return;
        Rectangle strip = Strip;
        _dragging.Position = Math.Round(Math.Clamp((e.X - strip.Left) / (double)Math.Max(1, strip.Width), 0, 1), 4);
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
        List<ParticleGradientStop> stops = _stops();
        ParticleGradientStop? existing = Hit(e.X);
        Rectangle strip = Strip;
        double position = Math.Clamp((e.X - strip.Left) / (double)Math.Max(1, strip.Width), 0, 1);
        if (existing is null && stops.Count >= 128) return;
        Vector4 sampled = Sample(stops, (float)position);
        ParticleColor current = existing?.Color ?? new ParticleColor(sampled.X, sampled.Y, sampled.Z, sampled.W);
        ParticleColor? picked = PickColour(current);
        if (picked is null) return;
        if (existing is not null) existing.Color = new ParticleColor(picked.R, picked.G, picked.B, current.A);
        else stops.Add(new ParticleGradientStop { Position = Math.Round(position, 4), Color = new ParticleColor(picked.R, picked.G, picked.B, current.A) });
        _changed();
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Right || !Enabled) return;
        List<ParticleGradientStop> stops = _stops();
        if (stops.Count <= 2 || Hit(e.X) is not { } stop) return;
        stops.Remove(stop);
        _changed();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Enabled || Hit(e.X) is not { } stop) return;
        stop.Color.A = Math.Clamp(stop.Color.A + Math.Sign(e.Delta) * 0.05f, 0f, 1f);
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

    private ParticleGradientStop? Hit(int x)
    {
        Rectangle strip = Strip;
        ParticleGradientStop? best = null;
        float nearest = 8f;
        foreach (ParticleGradientStop stop in _stops())
        {
            if (stop?.Color is null) continue;
            float distance = Math.Abs(strip.Left + (float)stop.Position * strip.Width - x);
            if (distance < nearest) { nearest = distance; best = stop; }
        }
        return best;
    }

    private static Vector4 Sample(List<ParticleGradientStop> stops, float t)
    {
        ParticleGradientStop? before = null, after = null;
        foreach (ParticleGradientStop stop in stops)
        {
            if (stop?.Color is null) continue;
            if (stop.Position <= t && (before is null || stop.Position >= before.Position)) before = stop;
            if (stop.Position >= t && (after is null || stop.Position < after.Position)) after = stop;
        }
        before ??= after;
        after ??= before;
        if (before is null || after is null) return Vector4.One;
        float range = (float)(after.Position - before.Position);
        float amount = range <= 1e-4f ? 0f : Math.Clamp((t - (float)before.Position) / range, 0f, 1f);
        return Vector4.Lerp(new Vector4(before.Color.R, before.Color.G, before.Color.B, before.Color.A),
            new Vector4(after.Color.R, after.Color.G, after.Color.B, after.Color.A), amount);
    }
}
