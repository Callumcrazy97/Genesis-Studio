using System;
using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Extended 2D drawing, plus the 3D drawing surface.
//
// IPgslDrawSurface is deliberately small — points, lines, rectangles, circles, text, sprites, and a
// 3D cube, sphere, and model queues. Richer shapes are composed here so every backend shares the
// same command semantics.
//
// Two honesty rules apply throughout:
//   • Where a shape is approximated (an ellipse from scanlines, a 3D line from a chain of cubes)
//     the description says so. A designer should not have to discover it by looking.
//   • Every advertised command has a concrete surface effect; inactive 3D passes reject 3D calls
//     explicitly through Is3DActive rather than pretending they reached a 2D frame.
public static partial class PgslCommands
{
    #region Drawing 2D extended

    /// <summary>Segment count for a curve of a given pixel radius — enough to look round, cheap enough to spam.</summary>
    private static int CurveSegments(double radius) => (int)Math.Clamp(radius * 0.8, 12, 64);

    private static Color CurrentColor()
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return Color.White;
        int alpha = (int)Math.Clamp(ctx.DrawAlpha * 255, 0, 255);
        return Color.FromArgb(alpha, ctx.DrawColor);
    }

    private static Color RgbColor(double r, double g, double b)
    {
        PgslContext ctx = GetContext();
        int alpha = ctx is null ? 255 : (int)Math.Clamp(ctx.DrawAlpha * 255, 0, 255);
        return Color.FromArgb(
            alpha,
            (int)Math.Clamp(r, 0, 255),
            (int)Math.Clamp(g, 0, 255),
            (int)Math.Clamp(b, 0, 255));
    }

    [PgslCommand("DrawTriangle", "DrawTriangle(x1, y1, x2, y2, x3, y3, outline)", "Triangle; filled by horizontal scanlines", "Drawing 2D")]
    public static void DrawTriangle(double x1, double y1, double x2, double y2, double x3, double y3, bool outline)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null) return;

        Color color = CurrentColor();
        if (outline)
        {
            surface.DrawLine((float)x1, (float)y1, (float)x2, (float)y2, color);
            surface.DrawLine((float)x2, (float)y2, (float)x3, (float)y3, color);
            surface.DrawLine((float)x3, (float)y3, (float)x1, (float)y1, color);
            return;
        }

        // Scanline fill: the surface has no triangle primitive, so walk Y and draw the span between
        // the two edges the scanline crosses.
        double top = Math.Min(y1, Math.Min(y2, y3));
        double bottom = Math.Max(y1, Math.Max(y2, y3));
        int rows = (int)Math.Clamp(bottom - top, 0, 4096);
        for (int row = 0; row <= rows; row++)
        {
            double y = top + row;
            double left = double.MaxValue;
            double right = double.MinValue;
            SpanEdge(x1, y1, x2, y2, y, ref left, ref right);
            SpanEdge(x2, y2, x3, y3, y, ref left, ref right);
            SpanEdge(x3, y3, x1, y1, y, ref left, ref right);
            if (left <= right)
            {
                surface.FillRectangle(color, new RectangleF((float)left, (float)y, (float)(right - left + 1), 1f));
            }
        }
    }

    private static void SpanEdge(double ax, double ay, double bx, double by, double y, ref double left, ref double right)
    {
        if (Math.Abs(by - ay) < 1e-9) return;                       // horizontal edge contributes nothing
        if (y < Math.Min(ay, by) || y > Math.Max(ay, by)) return;   // scanline misses this edge

        double t = (y - ay) / (by - ay);
        double x = ax + ((bx - ax) * t);
        left = Math.Min(left, x);
        right = Math.Max(right, x);
    }

    [PgslCommand("DrawEllipse", "DrawEllipse(x1, y1, x2, y2, outline)", "Ellipse in a bounding box; filled by scanlines", "Drawing 2D")]
    public static void DrawEllipse(double x1, double y1, double x2, double y2, bool outline)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null) return;

        double centreX = (x1 + x2) * 0.5;
        double centreY = (y1 + y2) * 0.5;
        double radiusX = Math.Abs(x2 - x1) * 0.5;
        double radiusY = Math.Abs(y2 - y1) * 0.5;
        if (radiusX < 0.5 || radiusY < 0.5) return;

        Color color = CurrentColor();
        if (outline)
        {
            int segments = CurveSegments(Math.Max(radiusX, radiusY));
            double previousX = centreX + radiusX;
            double previousY = centreY;
            for (int step = 1; step <= segments; step++)
            {
                double angle = step / (double)segments * Math.PI * 2;
                double nextX = centreX + (Math.Cos(angle) * radiusX);
                double nextY = centreY + (Math.Sin(angle) * radiusY);
                surface.DrawLine((float)previousX, (float)previousY, (float)nextX, (float)nextY, color);
                previousX = nextX;
                previousY = nextY;
            }

            return;
        }

        int rows = (int)Math.Clamp(radiusY * 2, 0, 4096);
        for (int row = 0; row <= rows; row++)
        {
            double y = centreY - radiusY + row;
            double normalised = (y - centreY) / radiusY;
            double inside = 1 - (normalised * normalised);
            if (inside <= 0) continue;

            double halfWidth = radiusX * Math.Sqrt(inside);
            surface.FillRectangle(
                color,
                new RectangleF((float)(centreX - halfWidth), (float)y, (float)(halfWidth * 2), 1f));
        }
    }

    [PgslCommand("DrawRoundRect", "DrawRoundRect(x1, y1, x2, y2, radius, outline)", "Rectangle with rounded corners", "Drawing 2D")]
    public static void DrawRoundRect(double x1, double y1, double x2, double y2, double radius, bool outline)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null) return;

        double left = Math.Min(x1, x2);
        double top = Math.Min(y1, y2);
        double width = Math.Abs(x2 - x1);
        double height = Math.Abs(y2 - y1);
        double r = Math.Clamp(radius, 0, Math.Min(width, height) * 0.5);
        Color color = CurrentColor();

        if (!outline)
        {
            // Centre band plus the two side bands, then the corners as quarter discs.
            surface.FillRectangle(color, new RectangleF((float)left, (float)(top + r), (float)width, (float)(height - (r * 2))));
            surface.FillRectangle(color, new RectangleF((float)(left + r), (float)top, (float)(width - (r * 2)), (float)r));
            surface.FillRectangle(color, new RectangleF((float)(left + r), (float)(top + height - r), (float)(width - (r * 2)), (float)r));
            if (r >= 0.5)
            {
                surface.FillCircle(color, (float)(left + r), (float)(top + r), (float)r);
                surface.FillCircle(color, (float)(left + width - r), (float)(top + r), (float)r);
                surface.FillCircle(color, (float)(left + r), (float)(top + height - r), (float)r);
                surface.FillCircle(color, (float)(left + width - r), (float)(top + height - r), (float)r);
            }

            return;
        }

        surface.DrawLine((float)(left + r), (float)top, (float)(left + width - r), (float)top, color);
        surface.DrawLine((float)(left + r), (float)(top + height), (float)(left + width - r), (float)(top + height), color);
        surface.DrawLine((float)left, (float)(top + r), (float)left, (float)(top + height - r), color);
        surface.DrawLine((float)(left + width), (float)(top + r), (float)(left + width), (float)(top + height - r), color);
    }

    [PgslCommand("DrawArrow", "DrawArrow(x1, y1, x2, y2, headSize)", "Line with an arrowhead at the far end", "Drawing 2D")]
    public static void DrawArrow(double x1, double y1, double x2, double y2, double headSize)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null) return;

        Color color = CurrentColor();
        surface.DrawLine((float)x1, (float)y1, (float)x2, (float)y2, color);

        double dx = x2 - x1;
        double dy = y2 - y1;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-6) return;

        double head = Math.Clamp(headSize, 1, length);
        double ux = dx / length;
        double uy = dy / length;
        double baseX = x2 - (ux * head);
        double baseY = y2 - (uy * head);
        double halfWidth = head * 0.5;
        surface.DrawLine((float)x2, (float)y2, (float)(baseX - (uy * halfWidth)), (float)(baseY + (ux * halfWidth)), color);
        surface.DrawLine((float)x2, (float)y2, (float)(baseX + (uy * halfWidth)), (float)(baseY - (ux * halfWidth)), color);
    }

    [PgslCommand("DrawHealthBar", "DrawHealthBar(x1, y1, x2, y2, amount, backR, backG, backB, barR, barG, barB)", "Two-tone progress bar; amount is 0-100", "Drawing 2D")]
    public static void DrawHealthBar(
        double x1, double y1, double x2, double y2, double amount,
        double backR, double backG, double backB, double barR, double barG, double barB)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null) return;

        double left = Math.Min(x1, x2);
        double top = Math.Min(y1, y2);
        double width = Math.Abs(x2 - x1);
        double height = Math.Abs(y2 - y1);
        surface.FillRectangle(RgbColor(backR, backG, backB), new RectangleF((float)left, (float)top, (float)width, (float)height));

        double filled = width * Math.Clamp(amount, 0, 100) / 100.0;
        if (filled > 0)
        {
            surface.FillRectangle(RgbColor(barR, barG, barB), new RectangleF((float)left, (float)top, (float)filled, (float)height));
        }
    }

    [PgslCommand("DrawGrid", "DrawGrid(x, y, cellWidth, cellHeight, columns, rows)", "Lattice of lines from a top-left origin", "Drawing 2D")]
    public static void DrawGrid(double x, double y, double cellWidth, double cellHeight, double columns, double rows)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || cellWidth <= 0 || cellHeight <= 0) return;

        Color color = CurrentColor();
        int cols = (int)Math.Clamp(columns, 0, 512);
        int lines = (int)Math.Clamp(rows, 0, 512);
        double right = x + (cols * cellWidth);
        double bottom = y + (lines * cellHeight);
        for (int column = 0; column <= cols; column++)
        {
            float lineX = (float)(x + (column * cellWidth));
            surface.DrawLine(lineX, (float)y, lineX, (float)bottom, color);
        }

        for (int row = 0; row <= lines; row++)
        {
            float lineY = (float)(y + (row * cellHeight));
            surface.DrawLine((float)x, lineY, (float)right, lineY, color);
        }
    }

    [PgslCommand("DrawCross", "DrawCross(x, y, size)", "Crosshair centred on a point", "Drawing 2D")]
    public static void DrawCross(double x, double y, double size)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null) return;

        Color color = CurrentColor();
        float half = (float)Math.Abs(size) * 0.5f;
        surface.DrawLine((float)x - half, (float)y, (float)x + half, (float)y, color);
        surface.DrawLine((float)x, (float)y - half, (float)x, (float)y + half, color);
    }

    [PgslCommand("DrawTextColoured", "DrawTextColoured(x, y, text, r, g, b)", "Text in an explicit colour", "Drawing 2D")]
    public static void DrawTextColoured(double x, double y, string text, double r, double g, double b)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || string.IsNullOrEmpty(text)) return;

        PgslContext ctx = GetContext();
        surface.DrawText(
            text,
            ctx?.DrawFont ?? "Arial",
            (float)(ctx?.DrawFontSize ?? 12d),
            RgbColor(r, g, b),
            new Rectangle((int)x, (int)y, 4096, 4096));
    }

    [PgslCommand("DrawTextScaled", "DrawTextScaled(x, y, text, size)", "Text at an explicit point size", "Drawing 2D")]
    public static void DrawTextScaled(double x, double y, string text, double size)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || string.IsNullOrEmpty(text)) return;

        PgslContext ctx = GetContext();
        surface.DrawText(
            text,
            ctx?.DrawFont ?? "Arial",
            (float)Math.Clamp(size, 1, 512),
            CurrentColor(),
            new Rectangle((int)x, (int)y, 4096, 4096));
    }

    [PgslCommand("DrawSetFont", "DrawSetFont(name)", "Font family for later text", "Drawing 2D")]
    public static void DrawSetFont(string name)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null && !string.IsNullOrWhiteSpace(name)) ctx.DrawFont = name;
    }

    [PgslCommand("DrawSetFontSize", "DrawSetFontSize(size)", "Point size for later text", "Drawing 2D")]
    public static void DrawSetFontSize(double size)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null) ctx.DrawFontSize = Math.Clamp(size, 1, 512);
    }

    [PgslCommand("DrawSetColorRgb", "DrawSetColorRgb(r, g, b)", "Set the draw colour from 0-255 channels", "Drawing 2D")]
    public static void DrawSetColorRgb(double r, double g, double b)
    {
        // The original DrawSetColor takes a System.Drawing.Color, which script cannot construct —
        // so from PGSL this is the only way to choose a colour.
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        ctx.DrawColor = Color.FromArgb(
            (int)Math.Clamp(r, 0, 255),
            (int)Math.Clamp(g, 0, 255),
            (int)Math.Clamp(b, 0, 255));
    }

    [PgslCommand("DrawGetColorR", "DrawGetColorR() -> number", "Red channel of the current draw colour, 0-255", "Drawing 2D")]
    public static double DrawGetColorR() => GetContext()?.DrawColor.R ?? 255;

    [PgslCommand("DrawGetColorG", "DrawGetColorG() -> number", "Green channel of the current draw colour, 0-255", "Drawing 2D")]
    public static double DrawGetColorG() => GetContext()?.DrawColor.G ?? 255;

    [PgslCommand("DrawGetColorB", "DrawGetColorB() -> number", "Blue channel of the current draw colour, 0-255", "Drawing 2D")]
    public static double DrawGetColorB() => GetContext()?.DrawColor.B ?? 255;

    [PgslCommand("DrawGetAlpha", "DrawGetAlpha() -> number", "Current draw alpha, 0-1", "Drawing 2D")]
    public static double DrawGetAlpha() => GetContext()?.DrawAlpha ?? 1d;

    [PgslCommand("DrawClearScreen", "DrawClearScreen(r, g, b)", "Fill the whole surface with a colour", "Drawing 2D")]
    public static void DrawClearScreen(double r, double g, double b) => Draw?.Clear(RgbColor(r, g, b));

    [PgslCommand("DrawSpriteScaled", "DrawSpriteScaled(name, x, y, frame, xscale, yscale, angle, alpha)", "Sprite with explicit transform", "Drawing 2D")]
    public static void DrawSpriteScaled(
        string name, double x, double y, double frame, double xscale, double yscale, double angle, double alpha)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || string.IsNullOrEmpty(name)) return;

        PgslContext ctx = GetContext();
        surface.DrawSprite(
            name,
            (float)x,
            (float)y,
            (int)Math.Max(0, frame),
            (float)xscale,
            (float)yscale,
            (float)angle,
            ctx?.ImageBlend ?? Color.White,
            (float)Math.Clamp(alpha, 0, 1));
    }

    #endregion

    #region Drawing 3D

    [PgslCommand("Draw3DIsActive", "Draw3DIsActive() -> bool", "True when the surface is in a 3D pass", "Drawing 3D")]
    public static bool Draw3DIsActive() => Draw?.Is3DActive ?? false;

    [PgslCommand("DrawText3D", "DrawText3D(x, y, z, text, size)", "World-space label projected through the active camera", "Drawing 3D")]
    public static void DrawText3D(double x, double y, double z, string text, double size)
    {
        PgslContext context = GetContext();
        IPgslDrawSurface surface = Draw;
        if (surface is null || !surface.Is3DActive || context?.View3D is not Matrix4x4 view
            || context.Proj3D is not Matrix4x4 projection || string.IsNullOrEmpty(text)) return;
        surface.DrawText3D(
            text,
            (float)x, (float)y, (float)z,
            context.DrawFont,
            (float)Math.Clamp(size, 1d, 512d),
            CurrentColor(),
            view,
            projection);
    }

    private static void QueueBox(double x, double y, double z, double sx, double sy, double sz, Color color)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !surface.Is3DActive) return;

        PgslContext ctx = GetContext();
        surface.QueueCube3D(
            (float)x, (float)y, (float)z,
            (float)sx, (float)sy, (float)sz,
            color,
            (float)Math.Clamp(ctx?.DrawAlpha ?? 1d, 0, 1));
    }

    [PgslCommand("DrawCube3D", "DrawCube3D(x, y, z, size)", "Axis-aligned cube at a point", "Drawing 3D")]
    public static void DrawCube3D(double x, double y, double z, double size) =>
        QueueBox(x, y, z, size, size, size, CurrentColor());

    [PgslCommand("DrawBox3D", "DrawBox3D(x, y, z, sx, sy, sz)", "Axis-aligned box with independent extents", "Drawing 3D")]
    public static void DrawBox3D(double x, double y, double z, double sx, double sy, double sz) =>
        QueueBox(x, y, z, sx, sy, sz, CurrentColor());

    [PgslCommand("DrawCubeColoured3D", "DrawCubeColoured3D(x, y, z, size, r, g, b)", "Cube in an explicit colour", "Drawing 3D")]
    public static void DrawCubeColoured3D(double x, double y, double z, double size, double r, double g, double b) =>
        QueueBox(x, y, z, size, size, size, RgbColor(r, g, b));

    [PgslCommand("DrawFloor3D", "DrawFloor3D(x, y, z, width, depth)", "Flat slab — a box with negligible height", "Drawing 3D")]
    public static void DrawFloor3D(double x, double y, double z, double width, double depth) =>
        QueueBox(x, y, z, width, 0.05, depth, CurrentColor());

    [PgslCommand("DrawPillar3D", "DrawPillar3D(x, y, z, radius, height)", "Square pillar; the cube queue has no cylinder", "Drawing 3D")]
    public static void DrawPillar3D(double x, double y, double z, double radius, double height) =>
        QueueBox(x, y + (height * 0.5), z, radius * 2, height, radius * 2, CurrentColor());

    [PgslCommand("DrawPoint3D", "DrawPoint3D(x, y, z, size)", "Small cube marking a point", "Drawing 3D")]
    public static void DrawPoint3D(double x, double y, double z, double size) =>
        QueueBox(x, y, z, Math.Max(0.01, size), Math.Max(0.01, size), Math.Max(0.01, size), CurrentColor());

    [PgslCommand("DrawWall3D", "DrawWall3D(x1, y1, z1, x2, y2, z2, height, thickness)", "Axis-aligned wall between two points; the dominant axis wins", "Drawing 3D")]
    public static void DrawWall3D(
        double x1, double y1, double z1, double x2, double y2, double z2, double height, double thickness)
    {
        // Honest limitation: QueueCube3D takes no rotation, so a wall can only be axis-aligned.
        // The longer horizontal span decides which axis it runs along.
        double spanX = Math.Abs(x2 - x1);
        double spanZ = Math.Abs(z2 - z1);
        double centreX = (x1 + x2) * 0.5;
        double centreZ = (z1 + z2) * 0.5;
        double baseY = Math.Min(y1, y2);
        double thick = Math.Max(0.01, thickness);

        if (spanX >= spanZ)
        {
            QueueBox(centreX, baseY + (height * 0.5), centreZ, Math.Max(0.01, spanX), height, thick, CurrentColor());
        }
        else
        {
            QueueBox(centreX, baseY + (height * 0.5), centreZ, thick, height, Math.Max(0.01, spanZ), CurrentColor());
        }
    }

    [PgslCommand("DrawLine3D", "DrawLine3D(x1, y1, z1, x2, y2, z2, thickness)", "Chain of small cubes — the surface has no 3D line primitive", "Drawing 3D")]
    public static void DrawLine3D(double x1, double y1, double z1, double x2, double y2, double z2, double thickness)
    {
        double length = DistanceBetweenPoints3D(x1, y1, z1, x2, y2, z2);
        double step = Math.Max(0.05, Math.Abs(thickness));
        int segments = (int)Math.Clamp(length / step, 1, 512);
        Color color = CurrentColor();
        for (int index = 0; index <= segments; index++)
        {
            double t = index / (double)segments;
            QueueBox(
                x1 + ((x2 - x1) * t),
                y1 + ((y2 - y1) * t),
                z1 + ((z2 - z1) * t),
                step, step, step,
                color);
        }
    }

    [PgslCommand("DrawGrid3D", "DrawGrid3D(x, y, z, cellSize, columns, rows)", "Lattice of thin boxes on the XZ plane", "Drawing 3D")]
    public static void DrawGrid3D(double x, double y, double z, double cellSize, double columns, double rows)
    {
        if (cellSize <= 0) return;

        int cols = (int)Math.Clamp(columns, 0, 64);
        int lines = (int)Math.Clamp(rows, 0, 64);
        double width = cols * cellSize;
        double depth = lines * cellSize;
        Color color = CurrentColor();
        double thin = Math.Max(0.02, cellSize * 0.04);

        for (int column = 0; column <= cols; column++)
        {
            QueueBox(x + (column * cellSize), y, z + (depth * 0.5), thin, thin, Math.Max(thin, depth), color);
        }

        for (int row = 0; row <= lines; row++)
        {
            QueueBox(x + (width * 0.5), y, z + (row * cellSize), Math.Max(thin, width), thin, thin, color);
        }
    }

    [PgslCommand("DrawModel3D", "DrawModel3D(modelName, x, y, z, scale)", "Draw a model asset", "Drawing 3D", Namespace = "Engine.Rendering")]
    public static void DrawModel3D(string modelName, double x, double y, double z, double scale)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !surface.Is3DActive || string.IsNullOrWhiteSpace(modelName)) return;
        PgslContext ctx = GetContext();
        surface.QueueModel3D(
            modelName,
            (float)x,
            (float)y,
            (float)z,
            (float)Math.Max(0.001, Math.Abs(scale)),
            CurrentColor(),
            (float)Math.Clamp(ctx?.DrawAlpha ?? 1d, 0d, 1d));
    }

    [PgslCommand(
        "DrawModelTransform3D",
        "DrawModelTransform3D(modelName, x, y, z, sx, sy, sz, xrot, yrot, zrot)",
        "Draw a model resource with independent scale and rotation",
        "Drawing 3D",
        Namespace = "Engine.Rendering")]
    public static void DrawModelTransform3D(
        string modelName,
        double x, double y, double z,
        double sx, double sy, double sz,
        double xrot, double yrot, double zrot)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !surface.Is3DActive || string.IsNullOrWhiteSpace(modelName)) return;
        PgslContext ctx = GetContext();
        surface.QueueModelTransform3D(
            modelName,
            shaderName: string.Empty,
            (float)x, (float)y, (float)z,
            (float)sx, (float)sy, (float)sz,
            (float)xrot, (float)yrot, (float)zrot,
            CurrentColor(),
            (float)Math.Clamp(ctx?.DrawAlpha ?? 1d, 0d, 1d));
    }

    [PgslCommand(
        "DrawModelShader3D",
        "DrawModelShader3D(modelName, shaderName, x, y, z, sx, sy, sz, xrot, yrot, zrot)",
        "Draw a real model resource through an authored mesh shader",
        "Drawing 3D",
        Namespace = "Engine.Rendering")]
    public static void DrawModelShader3D(
        string modelName,
        string shaderName,
        double x, double y, double z,
        double sx, double sy, double sz,
        double xrot, double yrot, double zrot)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !surface.Is3DActive || string.IsNullOrWhiteSpace(modelName)) return;
        PgslContext ctx = GetContext();
        surface.QueueModelTransform3D(
            modelName,
            shaderName ?? string.Empty,
            (float)x, (float)y, (float)z,
            (float)sx, (float)sy, (float)sz,
            (float)xrot, (float)yrot, (float)zrot,
            CurrentColor(),
            (float)Math.Clamp(ctx?.DrawAlpha ?? 1d, 0d, 1d));
    }

    [PgslCommand(
        "DrawPointLight3D",
        "DrawPointLight3D(x, y, z, radius, intensity, r, g, b)",
        "Add an omnidirectional light during the current 3D draw pass",
        "Drawing 3D",
        Namespace = "Engine.Rendering")]
    public static void DrawPointLight3D(
        double x, double y, double z,
        double radius,
        double intensity,
        double r, double g, double b)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !surface.Is3DActive) return;
        surface.QueuePointLight3D(
            (float)x, (float)y, (float)z,
            (float)radius,
            (float)intensity,
            RgbColor(r, g, b));
    }

    [PgslCommand(
        "DrawPointLightFalloff3D",
        "DrawPointLightFalloff3D(x, y, z, radius, intensity, falloff, r, g, b)",
        "Add an omnidirectional light with an explicit falloff exponent during the current 3D draw pass",
        "Drawing 3D",
        Namespace = "Engine.Rendering")]
    public static void DrawPointLightFalloff3D(
        double x, double y, double z,
        double radius,
        double intensity,
        double falloff,
        double r, double g, double b)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !surface.Is3DActive) return;
        surface.QueuePointLight3D(
            (float)x, (float)y, (float)z,
            (float)radius,
            (float)intensity,
            RgbColor(r, g, b),
            (float)falloff);
    }

    [PgslCommand("DrawSphere3D", "DrawSphere3D(x, y, z, radius)", "Draw a sphere", "Drawing 3D")]
    public static void DrawSphere3D(double x, double y, double z, double radius)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !surface.Is3DActive || radius == 0d) return;
        PgslContext ctx = GetContext();
        surface.QueueSphere3D(
            (float)x,
            (float)y,
            (float)z,
            (float)Math.Abs(radius),
            CurrentColor(),
            (float)Math.Clamp(ctx?.DrawAlpha ?? 1d, 0d, 1d));
    }

    #endregion
}
