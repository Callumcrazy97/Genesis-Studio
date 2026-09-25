using System.Drawing.Drawing2D;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

/// <summary>Scalable, code-drawn tool symbols with persistent text labels.</summary>
internal sealed class ImageToolButton(ImageToolKind tool) : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        using var fill = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(fill, ClientRectangle);
        using var border = new Pen(Focused ? ImageEditorChrome.Accent : FlatAppearance.BorderColor);
        e.Graphics.DrawRectangle(border, 0, 0, Width-1, Height-1);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(2, Height/2, Width-4, Height/2-2), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var g = e.Graphics;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(Width / 2f - 9, 7);
        using Pen pen = new(ForeColor, 1.7f);
        switch (tool)
        {
            case ImageToolKind.RectSelect:
            case ImageToolKind.Crop:
                pen.DashStyle = DashStyle.Dash; g.DrawRectangle(pen, 1, 1, 16, 14); break;
            case ImageToolKind.Rectangle: g.DrawRectangle(pen, 1, 1, 16, 14); break;
            case ImageToolKind.EllipseSelect: pen.DashStyle = DashStyle.Dash; g.DrawEllipse(pen, 1, 1, 16, 14); break;
            case ImageToolKind.Ellipse: g.DrawEllipse(pen, 1, 1, 16, 14); break;
            case ImageToolKind.Move:
            case ImageToolKind.Transform:
                g.DrawLine(pen, 9, 0, 9, 18); g.DrawLine(pen, 0, 9, 18, 9);
                g.DrawLines(pen, [new(5, 4), new(9, 0), new(13, 4)]);
                g.DrawLines(pen, [new(14, 5), new(18, 9), new(14, 13)]); break;
            case ImageToolKind.Text:
                g.DrawLine(pen, 1, 1, 17, 1); g.DrawLine(pen, 9, 1, 9, 17); g.DrawLine(pen, 5, 17, 13, 17); break;
            case ImageToolKind.Fill:
                g.DrawPolygon(pen, [new(8, 0), new(17, 9), new(9, 17), new(0, 8)]); g.DrawLine(pen, 2, 10, 15, 10); break;
            case ImageToolKind.MagicWand:
                g.DrawLine(pen, 0, 18, 13, 5); g.DrawLine(pen, 13, 0, 13, 4); g.DrawLine(pen, 15, 5, 19, 5); break;
            case ImageToolKind.Line: g.DrawLine(pen, 1, 17, 17, 1); break;
            case ImageToolKind.Polygon: g.DrawPolygon(pen, [new(9, 0), new(18, 6), new(14, 17), new(2, 17), new(0, 6)]); break;
            case ImageToolKind.Bezier:
            case ImageToolKind.LassoSelect: g.DrawBezier(pen, new(0, 16), new(22, -12), new(-5, 22), new(18, 2)); break;
            case ImageToolKind.Gradient:
                for (int x = 0; x < 18; x += 3) { using var shade = new Pen(Color.FromArgb(40 + x * 12, ForeColor), 3); g.DrawLine(shade, x, 1, x, 16); }
                break;
            case ImageToolKind.TileStamp:
                g.DrawRectangle(pen, 0, 0, 17, 17); g.DrawLine(pen, 8, 0, 8, 17); g.DrawLine(pen, 0, 8, 17, 8); break;
            case ImageToolKind.Eraser:
                g.DrawPolygon(pen, [new(1, 11), new(11, 1), new(18, 8), new(8, 18)]); g.DrawLine(pen, 4, 8, 11, 15); break;
            default:
                g.DrawPolygon(pen, [new(1, 17), new(4, 10), new(14, 0), new(18, 4), new(8, 14)]);
                g.DrawLine(pen, 4, 10, 8, 14); break;
        }
        g.Restore(state);
    }
}
