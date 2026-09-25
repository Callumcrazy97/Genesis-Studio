namespace Genesis.Application.Editors.Image.Imaging;

public enum ImageToolKind
{
    Pencil,
    Brush,
    Eraser,
    ColorPicker,
    Fill,
    Gradient,
    Line,
    Rectangle,
    Ellipse,
    Polygon,
    Bezier,
    Move,
    Transform,
    Crop,
    Text,
    TileStamp,
    RectSelect,
    EllipseSelect,
    LassoSelect,
    MagicWand,
    Bone,
    WeightPaint,
}

public sealed class ImageBrushSettings
{
    public bool Square { get; set; }
    public int Size { get; set; } = 1;
    public float Hardness { get; set; } = 1f;
    public float Opacity { get; set; } = 1f;
    public float Flow { get; set; } = 1f;
    public float Spacing { get; set; } = 0.15f;
    public float AngleDegrees { get; set; }
    public bool PixelPerfect { get; set; } = true;
    public bool Stabilizer { get; set; }
    public bool SymmetryX { get; set; }
    public bool SymmetryY { get; set; }
}

public static class RasterOperations
{
    /// <summary>
    /// Resamples a buffer to a new size, averaging over the source area each target pixel covers.
    /// </summary>
    /// <remarks>
    /// Area averaging rather than nearest-neighbour: this exists for fitting an oversized *external*
    /// image — a screenshot, a photo, art from another tool — into a smaller canvas, where dropping
    /// three pixels in four produces visible aliasing. Alpha is weighted so a transparent border
    /// does not darken the edge it is averaged into.
    /// </remarks>
    public static byte[] Resample(byte[] rgba, int width, int height, int targetWidth, int targetHeight)
    {
        Validate(rgba, width, height);
        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentException("Resample target must be at least one pixel in each axis.");
        if (targetWidth == width && targetHeight == height)
            return (byte[])rgba.Clone();

        byte[] result = new byte[targetWidth * targetHeight * 4];
        float scaleX = width / (float)targetWidth;
        float scaleY = height / (float)targetHeight;

        for (int y = 0; y < targetHeight; y++)
        {
            int sourceTop = (int)(y * scaleY);
            int sourceBottom = Math.Max(sourceTop + 1, (int)MathF.Ceiling((y + 1) * scaleY));
            sourceBottom = Math.Min(sourceBottom, height);

            for (int x = 0; x < targetWidth; x++)
            {
                int sourceLeft = (int)(x * scaleX);
                int sourceRight = Math.Max(sourceLeft + 1, (int)MathF.Ceiling((x + 1) * scaleX));
                sourceRight = Math.Min(sourceRight, width);

                double red = 0, green = 0, blue = 0, alpha = 0, weight = 0;
                for (int sy = sourceTop; sy < sourceBottom; sy++)
                {
                    for (int sx = sourceLeft; sx < sourceRight; sx++)
                    {
                        int index = ((sy * width) + sx) * 4;
                        double a = rgba[index + 3] / 255.0;
                        red += rgba[index] * a;
                        green += rgba[index + 1] * a;
                        blue += rgba[index + 2] * a;
                        alpha += rgba[index + 3];
                        weight += a;
                    }
                }

                int samples = Math.Max(1, (sourceBottom - sourceTop) * (sourceRight - sourceLeft));
                int destination = ((y * targetWidth) + x) * 4;
                if (weight > 0)
                {
                    result[destination] = (byte)Math.Clamp((int)Math.Round(red / weight), 0, 255);
                    result[destination + 1] = (byte)Math.Clamp((int)Math.Round(green / weight), 0, 255);
                    result[destination + 2] = (byte)Math.Clamp((int)Math.Round(blue / weight), 0, 255);
                }

                result[destination + 3] = (byte)Math.Clamp((int)Math.Round(alpha / samples), 0, 255);
            }
        }

        return result;
    }

    public static void Clear(byte[] rgba, int width, int height, Color color)
    {
        Validate(rgba, width, height);
        for (int i = 0; i < width * height * 4; i += 4)
        {
            rgba[i] = color.R;
            rgba[i + 1] = color.G;
            rgba[i + 2] = color.B;
            rgba[i + 3] = color.A;
        }
    }

    public static Color GetPixel(byte[] rgba, int width, int height, int x, int y)
    {
        Validate(rgba, width, height);
        if ((uint)x >= (uint)width || (uint)y >= (uint)height) return Color.Transparent;
        int i = (y * width + x) * 4;
        return Color.FromArgb(rgba[i + 3], rgba[i], rgba[i + 1], rgba[i + 2]);
    }

    public static void SetPixel(
        byte[] rgba,
        int width,
        int height,
        int x,
        int y,
        Color color,
        float opacity = 1f,
        bool erase = false,
        bool alphaLock = false,
        ImageSelectionMask? selection = null)
    {
        if ((uint)x >= (uint)width || (uint)y >= (uint)height || selection?.Contains(x, y) == false)
            return;
        int i = (y * width + x) * 4;
        if (alphaLock && rgba[i + 3] == 0) return;

        float strength = Math.Clamp(opacity, 0f, 1f);
        if (erase)
        {
            rgba[i + 3] = (byte)Math.Clamp((int)MathF.Round(rgba[i + 3] * (1f - strength)), 0, 255);
            return;
        }

        float sa = color.A / 255f * strength;
        float da = rgba[i + 3] / 255f;
        float oa = sa + da * (1f - sa);
        if (oa <= 0f)
        {
            rgba[i] = rgba[i + 1] = rgba[i + 2] = rgba[i + 3] = 0;
            return;
        }

        rgba[i] = Composite(color.R, rgba[i], sa, da, oa);
        rgba[i + 1] = Composite(color.G, rgba[i + 1], sa, da, oa);
        rgba[i + 2] = Composite(color.B, rgba[i + 2], sa, da, oa);
        rgba[i + 3] = (byte)Math.Clamp((int)MathF.Round(oa * 255f), 0, 255);
    }

    public static void StampBrush(
        byte[] rgba,
        int width,
        int height,
        int centerX,
        int centerY,
        Color color,
        ImageBrushSettings settings,
        bool erase,
        bool alphaLock,
        ImageSelectionMask? selection)
    {
        int size = Math.Clamp(settings.Size, 1, 1024);
        if (size <= 1)
        {
            SetPixel(rgba, width, height, centerX, centerY, color, settings.Opacity, erase, alphaLock, selection);
            if (settings.SymmetryX)
                SetPixel(rgba, width, height, width - 1 - centerX, centerY, color, settings.Opacity, erase, alphaLock, selection);
            if (settings.SymmetryY)
                SetPixel(rgba, width, height, centerX, height - 1 - centerY, color, settings.Opacity, erase, alphaLock, selection);
            if (settings.SymmetryX && settings.SymmetryY)
                SetPixel(rgba, width, height, width - 1 - centerX, height - 1 - centerY, color, settings.Opacity, erase, alphaLock, selection);
            return;
        }

        float radius = Math.Max(0.5f, size * 0.5f);
        int minX = (int)MathF.Floor(centerX - radius);
        int maxX = (int)MathF.Ceiling(centerX + radius);
        int minY = (int)MathF.Floor(centerY - radius);
        int maxY = (int)MathF.Ceiling(centerY + radius);
        float hardness = Math.Clamp(settings.Hardness, 0f, 1f);
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = (x + 0.5f - centerX) / radius;
                float dy = (y + 0.5f - centerY) / radius;
                float distance = settings.Square ? Math.Max(Math.Abs(dx), Math.Abs(dy)) : MathF.Sqrt(dx * dx + dy * dy);
                if (distance > 1f) continue;
                float edge = hardness >= 0.999f
                    ? 1f
                    : Math.Clamp((1f - distance) / Math.Max(0.001f, 1f - hardness), 0f, 1f);
                SetPixel(
                    rgba, width, height, x, y, color,
                    settings.Opacity * settings.Flow * edge,
                    erase, alphaLock, selection);
            }
        }

        if (settings.SymmetryX)
            StampMirrored(rgba, width, height, width - 1 - centerX, centerY, color, settings, erase, alphaLock, selection);
        if (settings.SymmetryY)
            StampMirrored(rgba, width, height, centerX, height - 1 - centerY, color, settings, erase, alphaLock, selection);
        if (settings.SymmetryX && settings.SymmetryY)
            StampMirrored(rgba, width, height, width - 1 - centerX, height - 1 - centerY, color, settings, erase, alphaLock, selection);
    }

    public static void DrawLine(
        byte[] rgba,
        int width,
        int height,
        Point from,
        Point to,
        Color color,
        ImageBrushSettings settings,
        bool erase = false,
        bool alphaLock = false,
        ImageSelectionMask? selection = null)
    {
        if (settings.PixelPerfect && settings.Size <= 1 && !erase)
        {
            DrawPixelPerfectSegment(rgba, width, height, from, to, color, settings, alphaLock, selection);
            return;
        }

        int x0 = from.X;
        int y0 = from.Y;
        int x1 = to.X;
        int y1 = to.Y;
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        while (true)
        {
            StampBrush(rgba, width, height, x0, y0, color, settings, erase, alphaLock, selection);
            if (x0 == x1 && y0 == y1) break;
            int twice = 2 * error;
            if (twice >= dy) { error += dy; x0 += sx; }
            if (twice <= dx) { error += dx; y0 += sy; }
        }
    }

    public static void DrawPixelPerfectStroke(
        byte[] rgba,
        int width,
        int height,
        IReadOnlyList<Point> points,
        Color color,
        ImageBrushSettings settings,
        bool erase = false,
        bool alphaLock = false,
        ImageSelectionMask? selection = null)
    {
        if (points.Count == 0) return;
        if (points.Count == 1)
        {
            StampBrush(rgba, width, height, points[0].X, points[0].Y, color, settings, erase, alphaLock, selection);
            return;
        }

        if (!settings.PixelPerfect || settings.Size > 1 || erase)
        {
            for (int index = 1; index < points.Count; index++)
                DrawLine(rgba, width, height, points[index - 1], points[index], color, settings, erase, alphaLock, selection);
            return;
        }

        Point? previous = null;
        Point? last = null;
        foreach (Point waypoint in points)
        {
            if (last == null)
            {
                StampBrush(rgba, width, height, waypoint.X, waypoint.Y, color, settings, false, alphaLock, selection);
                last = waypoint;
                continue;
            }

            foreach (Point step in BresenhamPixels(last.Value, waypoint))
            {
                if (step == last) continue;
                if (previous != null && last != null && IsPixelPerfectCorner(previous.Value, last.Value, step))
                {
                    SetPixel(rgba, width, height, last.Value.X, last.Value.Y, Color.Transparent, 1f, alphaLock: alphaLock, selection: selection);
                    previous = null;
                }
                else
                {
                    previous = last;
                }

                StampBrush(rgba, width, height, step.X, step.Y, color, settings, false, alphaLock, selection);
                last = step;
            }
        }
    }

    private static void DrawPixelPerfectSegment(
        byte[] rgba,
        int width,
        int height,
        Point from,
        Point to,
        Color color,
        ImageBrushSettings settings,
        bool alphaLock,
        ImageSelectionMask? selection)
    {
        Point? previous = null;
        Point? last = from;
        StampBrush(rgba, width, height, from.X, from.Y, color, settings, false, alphaLock, selection);
        foreach (Point step in BresenhamPixels(from, to))
        {
            if (step == last) continue;
            if (previous != null && last != null && IsPixelPerfectCorner(previous.Value, last.Value, step))
            {
                SetPixel(rgba, width, height, last.Value.X, last.Value.Y, Color.Transparent, 1f, alphaLock: alphaLock, selection: selection);
                previous = null;
            }
            else
            {
                previous = last;
            }

            StampBrush(rgba, width, height, step.X, step.Y, color, settings, false, alphaLock, selection);
            last = step;
        }
    }

    private static bool IsPixelPerfectCorner(Point a, Point b, Point c) =>
        (a.X != b.X && b.X == c.X && a.Y == b.Y && b.Y != c.Y)
        || (a.Y != b.Y && b.Y == c.Y && a.X == b.X && b.X != c.X);

    private static IEnumerable<Point> BresenhamPixels(Point from, Point to)
    {
        int x0 = from.X;
        int y0 = from.Y;
        int x1 = to.X;
        int y1 = to.Y;
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        while (true)
        {
            yield return new Point(x0, y0);
            if (x0 == x1 && y0 == y1) break;
            int twice = 2 * error;
            if (twice >= dy) { error += dy; x0 += sx; }
            if (twice <= dx) { error += dx; y0 += sy; }
        }
    }

    public static void DrawRectangle(
        byte[] rgba,
        int width,
        int height,
        Rectangle rectangle,
        Color color,
        ImageBrushSettings settings,
        bool filled,
        ImageSelectionMask? selection = null)
    {
        Rectangle rect = Normalize(rectangle);
        if (filled)
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
                for (int x = rect.Left; x < rect.Right; x++)
                    SetPixel(rgba, width, height, x, y, color, settings.Opacity, selection: selection);
            return;
        }
        DrawLine(rgba, width, height, new Point(rect.Left, rect.Top), new Point(rect.Right - 1, rect.Top), color, settings, selection: selection);
        DrawLine(rgba, width, height, new Point(rect.Right - 1, rect.Top), new Point(rect.Right - 1, rect.Bottom - 1), color, settings, selection: selection);
        DrawLine(rgba, width, height, new Point(rect.Right - 1, rect.Bottom - 1), new Point(rect.Left, rect.Bottom - 1), color, settings, selection: selection);
        DrawLine(rgba, width, height, new Point(rect.Left, rect.Bottom - 1), new Point(rect.Left, rect.Top), color, settings, selection: selection);
    }

    public static void DrawEllipse(
        byte[] rgba,
        int width,
        int height,
        Rectangle rectangle,
        Color color,
        ImageBrushSettings settings,
        bool filled,
        ImageSelectionMask? selection = null)
    {
        Rectangle rect = Normalize(rectangle);
        float rx = Math.Max(0.5f, rect.Width * 0.5f);
        float ry = Math.Max(0.5f, rect.Height * 0.5f);
        float cx = rect.Left + rx;
        float cy = rect.Top + ry;
        float threshold = Math.Max(1f / rx, 1f / ry) * Math.Max(1, settings.Size);
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            for (int x = rect.Left; x < rect.Right; x++)
            {
                float nx = (x + 0.5f - cx) / rx;
                float ny = (y + 0.5f - cy) / ry;
                float d = nx * nx + ny * ny;
                if ((filled && d <= 1f) || (!filled && MathF.Abs(d - 1f) <= threshold))
                    SetPixel(rgba, width, height, x, y, color, settings.Opacity, selection: selection);
            }
        }
    }

    public static void DrawPolygon(
        byte[] rgba,
        int width,
        int height,
        IReadOnlyList<Point> vertices,
        Color color,
        ImageBrushSettings settings,
        bool filled,
        ImageSelectionMask? selection = null)
    {
        if (vertices.Count < 2) return;
        if (!filled)
        {
            for (int index = 0; index < vertices.Count; index++)
            {
                Point from = vertices[index];
                Point to = vertices[(index + 1) % vertices.Count];
                DrawLine(rgba, width, height, from, to, color, settings, selection: selection);
            }
            return;
        }

        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        foreach (Point vertex in vertices)
        {
            minX = Math.Min(minX, vertex.X);
            minY = Math.Min(minY, vertex.Y);
            maxX = Math.Max(maxX, vertex.X);
            maxY = Math.Max(maxY, vertex.Y);
        }
        Rectangle bounds = Rectangle.Intersect(
            Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1),
            new Rectangle(0, 0, width, height));
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (!PointInPolygon(x + 0.5f, y + 0.5f, vertices)) continue;
                SetPixel(rgba, width, height, x, y, color, settings.Opacity, selection: selection);
            }
        }
    }

    public static void FloodFill(
        byte[] rgba,
        int width,
        int height,
        int startX,
        int startY,
        Color replacement,
        int tolerance,
        ImageSelectionMask? selection = null)
    {
        Validate(rgba, width, height);
        if ((uint)startX >= (uint)width || (uint)startY >= (uint)height) return;
        Color target = GetPixel(rgba, width, height, startX, startY);
        if (Distance(target, replacement) == 0) return;
        bool[] visited = new bool[width * height];
        Queue<Point> queue = new();
        queue.Enqueue(new Point(startX, startY));
        while (queue.Count > 0)
        {
            Point p = queue.Dequeue();
            if ((uint)p.X >= (uint)width || (uint)p.Y >= (uint)height) continue;
            int index = p.Y * width + p.X;
            if (visited[index]) continue;
            visited[index] = true;
            if (selection?.Contains(p.X, p.Y) == false) continue;
            if (Distance(GetPixel(rgba, width, height, p.X, p.Y), target) > tolerance) continue;
            SetPixel(rgba, width, height, p.X, p.Y, replacement);
            queue.Enqueue(new Point(p.X - 1, p.Y));
            queue.Enqueue(new Point(p.X + 1, p.Y));
            queue.Enqueue(new Point(p.X, p.Y - 1));
            queue.Enqueue(new Point(p.X, p.Y + 1));
        }
    }

    public static void Gradient(
        byte[] rgba,
        int width,
        int height,
        Point from,
        Point to,
        Color start,
        Color end,
        ImageSelectionMask? selection = null)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float lengthSq = Math.Max(0.0001f, dx * dx + dy * dy);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (selection?.Contains(x, y) == false) continue;
                float t = Math.Clamp(((x - from.X) * dx + (y - from.Y) * dy) / lengthSq, 0f, 1f);
                SetPixel(rgba, width, height, x, y, Lerp(start, end, t));
            }
        }
    }

    private static void StampMirrored(
        byte[] rgba,
        int width,
        int height,
        int centerX,
        int centerY,
        Color color,
        ImageBrushSettings settings,
        bool erase,
        bool alphaLock,
        ImageSelectionMask? selection)
    {
        bool sx = settings.SymmetryX;
        bool sy = settings.SymmetryY;
        settings.SymmetryX = false;
        settings.SymmetryY = false;
        StampBrush(rgba, width, height, centerX, centerY, color, settings, erase, alphaLock, selection);
        settings.SymmetryX = sx;
        settings.SymmetryY = sy;
    }

    private static byte Composite(byte source, byte destination, float sa, float da, float outAlpha)
    {
        float value = (source / 255f * sa + destination / 255f * da * (1f - sa)) / outAlpha;
        return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }

    private static int Distance(Color a, Color b) => ColorDistance(a, b);

    internal static int ColorDistance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) + Math.Abs(a.A - b.A);

    private static Color Lerp(Color a, Color b, float t) =>
        Color.FromArgb(
            (int)MathF.Round(a.A + (b.A - a.A) * t),
            (int)MathF.Round(a.R + (b.R - a.R) * t),
            (int)MathF.Round(a.G + (b.G - a.G) * t),
            (int)MathF.Round(a.B + (b.B - a.B) * t));

    private static Rectangle Normalize(Rectangle rectangle)
    {
        int left = Math.Min(rectangle.Left, rectangle.Right);
        int top = Math.Min(rectangle.Top, rectangle.Bottom);
        return new Rectangle(left, top, Math.Abs(rectangle.Width), Math.Abs(rectangle.Height));
    }

    private static bool PointInPolygon(float x, float y, IReadOnlyList<Point> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Point a = polygon[i];
            Point b = polygon[j];
            bool intersects = (a.Y > y) != (b.Y > y)
                && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y + float.Epsilon) + a.X;
            if (intersects) inside = !inside;
        }
        return inside;
    }

    private static void Validate(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("RGBA surface dimensions do not match its byte buffer.", nameof(rgba));
    }
}
