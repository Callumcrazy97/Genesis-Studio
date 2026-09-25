using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Controls;

public sealed class ModernButton : Button
{
    private bool _hovered;
    private bool _pressed;

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
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeService.Palette;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        bounds.Inflate(-1, -1);

        Color fill = Accent
            ? (_pressed ? palette.AccentHover : palette.Accent)
            : (_pressed
                ? palette.BorderStrong
                : _hovered ? palette.SurfaceHover : palette.SurfaceRaised);

        using GraphicsPath path = RoundedRectangle(bounds, 7);
        using SolidBrush background = new(fill);
        e.Graphics.FillPath(background, path);

        if (!Accent)
        {
            using Pen border = new(palette.Border, 1);
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
