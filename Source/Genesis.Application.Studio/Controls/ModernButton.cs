using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Controls;

public sealed class ModernButton : Button
{
    // Hover and press as 0..1 amounts, eased by UiMotion, so colours fade rather than snap.
    private float _hover;
    private float _press;

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Height = 38;
        Padding = new Padding(12, 0, 12, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint, true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Accent { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get; set; } = string.Empty;

    protected override void OnMouseEnter(EventArgs e)
    {
        FadeHover(1f, 120);
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        FadeHover(0f, 220);
        FadePress(0f, 120);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        FadePress(1f, 60);
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        FadePress(0f, 160);
        base.OnMouseUp(e);
    }

    private void FadeHover(float target, int milliseconds)
    {
        float from = _hover;
        UiMotion.Animate(this, "hover", milliseconds, t => { _hover = UiMotion.Lerp(from, target, t); Invalidate(); });
    }

    private void FadePress(float target, int milliseconds)
    {
        float from = _press;
        UiMotion.Animate(this, "press", milliseconds, t => { _press = UiMotion.Lerp(from, target, t); Invalidate(); });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeService.Palette;
        // The corners outside the rounded body show what the button sits on, not a square.
        e.Graphics.Clear(UiDrawing.EffectiveBackColor(Parent));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        // Pressing sinks the button a pixel or so into the surface.
        int sink = (int)MathF.Round(_press * 1.5f);
        bounds.Inflate(-sink, -sink);
        if (bounds.Width <= 2 || bounds.Height <= 2) return;

        Color fill = Accent
            ? UiMotion.Mix(UiMotion.Mix(palette.Accent, palette.AccentHover, _hover * 0.55f), palette.AccentHover, _press)
            : UiMotion.Mix(UiMotion.Mix(palette.SurfaceRaised, palette.SurfaceHover, _hover), palette.BorderStrong, _press * 0.7f);

        using GraphicsPath path = RoundedRectangle(bounds, 8);
        using SolidBrush background = new(Enabled ? fill : UiMotion.Mix(fill, palette.Surface, 0.5f));
        e.Graphics.FillPath(background, path);

        if (Accent)
        {
            // A soft sheen on the upper half reads as a lit, raised control.
            Rectangle upper = new(bounds.Left, bounds.Top, bounds.Width, Math.Max(1, bounds.Height / 2));
            using LinearGradientBrush sheen = new(upper, Color.FromArgb(38 + (int)(_hover * 22), Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Vertical);
            GraphicsState state = e.Graphics.Save();
            e.Graphics.SetClip(path, CombineMode.Intersect);
            e.Graphics.FillRectangle(sheen, upper);
            e.Graphics.Restore(state);
        }
        else
        {
            using Pen border = new(UiMotion.Mix(palette.Border, palette.Accent, _hover * 0.75f), 1);
            e.Graphics.DrawPath(border, path);
        }

        TextRenderer.DrawText(
            e.Graphics,
            string.IsNullOrWhiteSpace(Glyph) ? Text : $"{Glyph}  {Text}",
            Font,
            bounds,
            Enabled ? palette.Text : palette.TextMuted,
            TextFormatFlags.HorizontalCenter |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        int diameter = radius * 2;
        GraphicsPath path = new();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(
            rectangle.Right - diameter,
            rectangle.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
