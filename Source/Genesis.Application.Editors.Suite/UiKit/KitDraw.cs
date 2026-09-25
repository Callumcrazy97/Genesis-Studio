using System.Drawing;
using System.Drawing.Drawing2D;

namespace Genesis.Application.Editors.Suite.UiKit;

/// <summary>Shared drawing helpers for the Phase 1 UI kit (Genesis Dark geometry).</summary>
internal static class KitDraw
{
    public const int CornerRadius = 6;

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius = CornerRadius)
    {
        int d = Math.Max(2, radius * 2);
        GraphicsPath path = new();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Rectangle bounds, Color fill, int radius = CornerRadius)
    {
        using GraphicsPath path = RoundedRect(bounds, radius);
        using SolidBrush brush = new(fill);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
    }

    public static void StrokeRound(Graphics g, Rectangle bounds, Color border, int radius = CornerRadius)
    {
        using GraphicsPath path = RoundedRect(bounds, radius);
        using Pen pen = new(border);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawPath(pen, path);
    }
}
