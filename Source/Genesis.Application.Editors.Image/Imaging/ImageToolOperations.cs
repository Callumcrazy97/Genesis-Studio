namespace Genesis.Application.Editors.Image.Imaging;

public static class ImageToolOperations
{
    public static void DrawBezier(
        byte[] rgba,
        int width,
        int height,
        Point start,
        Point end,
        Color color,
        ImageBrushSettings settings,
        ImageSelectionMask? selection = null)
    {
        Point control1 = new(
            start.X + (end.X - start.X) / 3,
            start.Y + (end.Y - start.Y) / 3 - Math.Max(4, Math.Abs(end.X - start.X) / 6));
        Point control2 = new(
            start.X + (end.X - start.X) * 2 / 3,
            start.Y + (end.Y - start.Y) * 2 / 3 + Math.Max(4, Math.Abs(end.Y - start.Y) / 6));
        DrawBezier(rgba, width, height, start, control1, control2, end, color, settings, selection);
    }

    public static void DrawBezier(byte[] rgba, int width, int height, Point start, Point control1, Point control2, Point end,
        Color color, ImageBrushSettings settings, ImageSelectionMask? selection = null)
    {
        int steps = Math.Clamp((Math.Abs(control1.X-start.X) + Math.Abs(control1.Y-start.Y) + Math.Abs(control2.X-control1.X)
            + Math.Abs(control2.Y-control1.Y) + Math.Abs(end.X-control2.X) + Math.Abs(end.Y-control2.Y)) * 2, 12, 4096);
        Point previous = start;
        for (int step = 1; step <= steps; step++)
        {
            float t = step / (float)steps;
            Point current = CubicBezier(start, control1, control2, end, t);
            RasterOperations.DrawLine(rgba, width, height, previous, current, color, settings, selection: selection);
            previous = current;
        }
    }

    public static void StampTile(
        byte[] rgba,
        int width,
        int height,
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int tileWidth,
        int tileHeight,
        int tileIndex,
        Point anchor,
        ImageSelectionMask? selection = null)
    {
        if (tileWidth <= 0 || tileHeight <= 0) return;
        int columns = Math.Max(1, sourceWidth / tileWidth);
        int tileX = tileIndex % columns;
        int tileY = tileIndex / columns;
        int sourceLeft = tileX * tileWidth;
        int sourceTop = tileY * tileHeight;
        for (int y = 0; y < tileHeight; y++)
        {
            for (int x = 0; x < tileWidth; x++)
            {
                int canvasX = anchor.X + x;
                int canvasY = anchor.Y + y;
                if ((uint)canvasX >= (uint)width || (uint)canvasY >= (uint)height) continue;
                int sourcePixelX = sourceLeft + x;
                int sourcePixelY = sourceTop + y;
                if ((uint)sourcePixelX >= (uint)sourceWidth || (uint)sourcePixelY >= (uint)sourceHeight) continue;
                int sourceIndex = (sourcePixelY * sourceWidth + sourcePixelX) * 4;
                if (source[sourceIndex + 3] == 0) continue;
                Color pixel = Color.FromArgb(
                    source[sourceIndex + 3],
                    source[sourceIndex],
                    source[sourceIndex + 1],
                    source[sourceIndex + 2]);
                RasterOperations.SetPixel(rgba, width, height, canvasX, canvasY, pixel, 1f, selection: selection);
            }
        }
    }

    public static void DrawText(
        byte[] rgba,
        int width,
        int height,
        Point anchor,
        string text,
        Color color,
        int fontSize,
        ImageSelectionMask? selection = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        using Bitmap bitmap = new(Math.Max(8, text.Length * fontSize), Math.Max(fontSize + 4, fontSize * 2));
        bitmap.SetResolution(96, 96);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using Font font = new("Segoe UI", Math.Clamp(fontSize, 6, 96), FontStyle.Regular, GraphicsUnit.Pixel);
        using SolidBrush brush = new(Color.FromArgb(color.A, color.R, color.G, color.B));
        graphics.DrawString(text, font, brush, 0, 0);
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 0) continue;
                int canvasX = anchor.X + x;
                int canvasY = anchor.Y + y;
                if ((uint)canvasX >= (uint)width || (uint)canvasY >= (uint)height) continue;
                RasterOperations.SetPixel(rgba, width, height, canvasX, canvasY, pixel, 1f, selection: selection);
            }
        }
    }

    public static byte[] ScaleSelection(
        byte[] rgba,
        int width,
        int height,
        ImageSelectionMask selection,
        Rectangle bounds,
        float scaleX,
        float scaleY)
    {
        byte[] copy = (byte[])rgba.Clone();
        int newWidth = Math.Max(1, (int)MathF.Round(bounds.Width * scaleX));
        int newHeight = Math.Max(1, (int)MathF.Round(bounds.Height * scaleY));
        float centerX = bounds.Left + bounds.Width * 0.5f;
        float centerY = bounds.Top + bounds.Height * 0.5f;

        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (!selection.Contains(x, y)) continue;
                RasterOperations.SetPixel(copy, width, height, x, y, Color.Transparent, 1f, erase: true);
            }
        }

        for (int y = 0; y < newHeight; y++)
        {
            for (int x = 0; x < newWidth; x++)
            {
                float sourceX = centerX + (x - newWidth * 0.5f) / Math.Max(0.001f, scaleX);
                float sourceY = centerY + (y - newHeight * 0.5f) / Math.Max(0.001f, scaleY);
                int sampleX = (int)MathF.Round(sourceX);
                int sampleY = (int)MathF.Round(sourceY);
                if (!selection.Contains(sampleX, sampleY)) continue;
                int destX = (int)MathF.Round(centerX - newWidth * 0.5f + x);
                int destY = (int)MathF.Round(centerY - newHeight * 0.5f + y);
                if ((uint)destX >= (uint)width || (uint)destY >= (uint)height) continue;
                Color pixel = RasterOperations.GetPixel(rgba, width, height, sampleX, sampleY);
                if (pixel.A == 0) continue;
                RasterOperations.SetPixel(copy, width, height, destX, destY, pixel, 1f);
            }
        }

        return copy;
    }

    private static Point CubicBezier(Point p0, Point p1, Point p2, Point p3, float t)
    {
        float u = 1f - t;
        float tt = t * t;
        float uu = u * u;
        float uuu = uu * u;
        float ttt = tt * t;
        int x = (int)MathF.Round(uuu * p0.X + 3f * uu * t * p1.X + 3f * u * tt * p2.X + ttt * p3.X);
        int y = (int)MathF.Round(uuu * p0.Y + 3f * uu * t * p1.Y + 3f * u * tt * p2.Y + ttt * p3.Y);
        return new Point(x, y);
    }
}
