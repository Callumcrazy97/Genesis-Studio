using System.Drawing.Drawing2D;
using Genesis.Application.Core.Resources;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

// The UI Editor's picture of one element: the same corners, borders, gradients, alignment,
// spacing, state looks, sliders, toggles and image fits the game draws, in GDI+.
internal sealed partial class UiDesignCanvas
{
    private void PaintElement(Graphics g, UiElement element, RectangleF rect, float scale, string state)
    {
        UiLook look = UiLook.Resolve(element, state);
        float radius = Math.Max(0, element.CornerRadius * scale);
        using Font font = new(string.IsNullOrWhiteSpace(element.Font) ? "Segoe UI" : element.Font,
            Math.Max(1f, element.FontSize * scale), GraphicsUnit.Pixel);
        switch (element.Type)
        {
            case UiElementType.Panel:
                FillBox(g, rect, radius, look.Background, look.GradientEnd, element.Fill);
                DrawBorder(g, element, rect, radius, scale, look.Border, legacyWidth: 0);
                break;
            case UiElementType.Text:
                DrawLabel(g, element, element.Text, font, rect, scale, look.Foreground, centredByDefault: false);
                break;
            case UiElementType.Image:
                PaintImage(g, element, rect, look);
                break;
            case UiElementType.Button:
                FillBox(g, rect, radius, look.Background, look.GradientEnd, element.Fill);
                DrawBorder(g, element, rect, radius, scale, look.Border, legacyWidth: 1.5f);
                DrawLabel(g, element, element.Text, font, rect, scale, look.Foreground, centredByDefault: true);
                break;
            case UiElementType.ProgressBar:
            {
                FillBox(g, rect, radius, look.Background, look.GradientEnd, element.Fill);
                float amount = Math.Clamp(element.Value / Math.Max(0.001f, element.Maximum), 0f, 1f);
                RectangleF fill = new(rect.X, rect.Y, rect.Width * amount, rect.Height);
                FillBox(g, fill, Math.Min(radius, fill.Width / 2), look.Accent, look.Accent, UiFill.Solid);
                DrawBorder(g, element, rect, radius, scale, look.Border, legacyWidth: 1);
                break;
            }
            case UiElementType.Slider:
            {
                float knob = rect.Height / 2;
                float range = Math.Max(0.0001f, element.Maximum - element.Minimum);
                float amount = Math.Clamp((element.Value - element.Minimum) / range, 0f, 1f);
                float centreX = rect.X + knob + amount * Math.Max(0, rect.Width - knob * 2);
                float trackHeight = Math.Max(2f, rect.Height / 3f);
                RectangleF track = new(rect.X, rect.Y + (rect.Height - trackHeight) / 2, rect.Width, trackHeight);
                float trackRadius = Math.Min(radius, trackHeight / 2);
                FillBox(g, track, trackRadius, look.Background, look.GradientEnd, element.Fill);
                RectangleF filled = new(track.X, track.Y, Math.Max(0, centreX - track.X), track.Height);
                FillBox(g, filled, Math.Min(trackRadius, filled.Width / 2), look.Accent, look.Accent, UiFill.Solid);
                DrawBorder(g, element, track, trackRadius, scale, look.Border, legacyWidth: 0);
                using (SolidBrush brush = new(look.Foreground)) g.FillEllipse(brush, centreX - knob, rect.Y, knob * 2, knob * 2);
                break;
            }
            case UiElementType.Toggle:
            {
                bool on = element.Value >= 0.5f;
                float switchWidth = Math.Min(rect.Width, rect.Height * 1.8f);
                RectangleF track = new(rect.X, rect.Y, switchWidth, rect.Height);
                float trackRadius = element.CornerRadius > 0 ? Math.Min(radius, rect.Height / 2) : rect.Height / 2;
                if (on) FillBox(g, track, trackRadius, look.Accent, look.Accent, UiFill.Solid);
                else FillBox(g, track, trackRadius, look.Background, look.GradientEnd, element.Fill);
                DrawBorder(g, element, track, trackRadius, scale, look.Border, legacyWidth: 0);
                float inset = Math.Max(2f, rect.Height * .12f);
                float knob = Math.Max(1f, rect.Height / 2 - inset);
                float knobX = on ? track.Right - inset - knob : track.X + inset + knob;
                using (SolidBrush brush = new(look.Foreground)) g.FillEllipse(brush, knobX - knob, rect.Y + rect.Height / 2 - knob, knob * 2, knob * 2);
                float gap = 10 * scale;
                RectangleF label = new(track.Right + gap, rect.Y, Math.Max(1, rect.Width - switchWidth - gap), rect.Height);
                if (label.Width > 1) DrawLabel(g, element, element.Text, font, label, scale, look.Foreground, centredByDefault: false);
                break;
            }
        }
    }

    private void PaintImage(Graphics g, UiElement element, RectangleF rect, UiLook look)
    {
        Bitmap? image = LoadImage(element.Image);
        if (image is null)
        {
            using (SolidBrush brush = new(Color.FromArgb(42, look.Accent))) g.FillRectangle(brush, rect);
            using (Pen pen = new(look.Accent))
            {
                g.DrawRectangle(pen, Rectangle.Round(rect));
                g.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Bottom); g.DrawLine(pen, rect.Right, rect.Top, rect.Left, rect.Bottom);
            }
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(element.Image) ? "Choose Image" : ResourceDisplayName.Format(element.Image), Font,
                Rectangle.Round(rect), look.Foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        using System.Drawing.Imaging.ImageAttributes attributes = new();
        attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = look.Foreground.A / 255f });
        if (element.ImageFit == UiImageFit.Stretch)
        {
            g.DrawImage(image, Rectangle.Round(new RectangleF(rect.X, rect.Y, rect.Width * element.ImageScaleX, rect.Height * element.ImageScaleY)),
                0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            return;
        }
        UiImageLayout.Fit(element, image.Width, image.Height, rect, out RectangleF source, out RectangleF destination);
        g.DrawImage(image, Rectangle.Round(destination), source.X * image.Width, source.Y * image.Height,
            source.Width * image.Width, source.Height * image.Height, GraphicsUnit.Pixel, attributes);
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        GraphicsPath path = new();
        float r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
        if (r <= .5f) { path.AddRectangle(rect); return path; }
        float d = r * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillBox(Graphics g, RectangleF rect, float radius, Color from, Color to, UiFill fill)
    {
        if (rect.Width < .5f || rect.Height < .5f) return;
        using Brush brush = fill == UiFill.Solid || from == to
            ? new SolidBrush(from)
            : new LinearGradientBrush(RectangleF.Inflate(rect, 1, 1), from, to,
                fill == UiFill.VerticalGradient ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal) { WrapMode = WrapMode.TileFlipXY };
        if (radius <= 0) { g.FillRectangle(brush, rect); return; }
        using GraphicsPath path = RoundedPath(rect, radius);
        g.FillPath(brush, path);
    }

    /// <summary>The authored border, or the type's own outline when none is authored.</summary>
    private static void DrawBorder(Graphics g, UiElement element, RectangleF rect, float radius, float scale, Color colour, float legacyWidth)
    {
        float width = element.BorderWidth is float authored ? authored * scale : legacyWidth;
        if (width <= 0 || colour.A == 0 || rect.Width <= width || rect.Height <= width) return;
        using Pen pen = new(colour, width);
        RectangleF inset = RectangleF.Inflate(rect, -width / 2, -width / 2);
        if (radius <= 0 && element.BorderWidth is null) { g.DrawRectangle(pen, Rectangle.Round(rect)); return; }
        using GraphicsPath path = RoundedPath(inset, Math.Max(0, radius - width / 2));
        g.DrawPath(pen, path);
    }

    /// <summary>Text placed as the game places it: authored alignment and letter spacing, or the type's own placement.</summary>
    private static void DrawLabel(Graphics g, UiElement element, string text, Font font, RectangleF rect, float scale, Color colour, bool centredByDefault)
    {
        if (string.IsNullOrEmpty(text)) return;
        UiHorizontalAlign across = element.TextAlign != UiHorizontalAlign.Auto ? element.TextAlign
            : centredByDefault ? UiHorizontalAlign.Center : UiHorizontalAlign.Left;
        UiVerticalAlign down = element.TextVerticalAlign == UiVerticalAlign.Auto ? UiVerticalAlign.Middle : element.TextVerticalAlign;
        float spacing = element.LetterSpacing * scale;
        if (Math.Abs(spacing) < .01f)
        {
            TextFormatFlags flags = TextFormatFlags.EndEllipsis | (across switch
            {
                UiHorizontalAlign.Center => TextFormatFlags.HorizontalCenter,
                UiHorizontalAlign.Right => TextFormatFlags.Right,
                _ => TextFormatFlags.Left,
            }) | (down switch
            {
                UiVerticalAlign.Top => TextFormatFlags.Top,
                UiVerticalAlign.Bottom => TextFormatFlags.Bottom,
                _ => TextFormatFlags.VerticalCenter,
            });
            TextRenderer.DrawText(g, text, font, Rectangle.Round(rect), colour, flags);
            return;
        }
        const TextFormatFlags glyph = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        int[] widths = text.Select(character => TextRenderer.MeasureText(g, character.ToString(), font, Size.Empty, glyph).Width).ToArray();
        float total = widths.Sum() + spacing * Math.Max(0, text.Length - 1);
        float x = across switch
        {
            UiHorizontalAlign.Center => rect.X + (rect.Width - total) / 2,
            UiHorizontalAlign.Right => rect.Right - total,
            _ => rect.X,
        };
        float y = down switch
        {
            UiVerticalAlign.Top => rect.Y,
            UiVerticalAlign.Bottom => rect.Bottom - font.Height,
            _ => rect.Y + (rect.Height - font.Height) / 2,
        };
        for (int i = 0; i < text.Length; i++)
        {
            TextRenderer.DrawText(g, text[i].ToString(), font, new Point((int)Math.Round(x), (int)Math.Round(y)), colour, glyph);
            x += widths[i] + spacing;
        }
    }
}
