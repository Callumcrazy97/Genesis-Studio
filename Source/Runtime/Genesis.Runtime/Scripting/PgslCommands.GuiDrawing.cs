using System;
using System.Drawing;
using System.Numerics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// What a game's menus and HUD need beyond plain shapes and text: measured, spaced and aligned
// text, smooth shapes with borders, gradients, part of an image, a clip rectangle, and the
// controller, keyboard and pointer as a menu reads them.
public static partial class PgslCommands
{
    #region Text layout

    /// <summary>Text at x, y in the current alignment and letter spacing.</summary>
    private static void DrawTextAt(double x, double y, string text, string font, double size, Color color)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || string.IsNullOrEmpty(text)) return;

        PgslContext ctx = GetContext();
        float tracking = (float)(ctx?.TextTracking ?? 0d);
        float drawX = (float)x;
        if (ctx is { TextAlign: 1 or 2 })
        {
            float width = surface.MeasureText(text, font, (float)size, tracking).X;
            drawX -= ctx.TextAlign == 1 ? width * 0.5f : width;
        }

        // Whole pixels, as text has always been placed.
        surface.DrawTextRun(text, font, (float)size, color, (int)drawX, (int)y, tracking);
    }

    private static Vector2 MeasureTextNow(string text, double size)
    {
        PgslContext ctx = GetContext();
        string font = ctx?.DrawFont ?? "Arial";
        float tracking = (float)(ctx?.TextTracking ?? 0d);
        float pixels = (float)Math.Clamp(size <= 0 ? ctx?.DrawFontSize ?? 12d : size, 1, 512);
        if (Draw is IPgslDrawSurface surface) return surface.MeasureText(text ?? string.Empty, font, pixels, tracking);
        // Outside a Draw event there is no surface; the project font is looked up the same way.
        return Genesis.Shared.Overlay.GlyphAtlas.Measure(text ?? string.Empty,
            PgslRenderDrawSurface.ResolveProjectFont(ProjectPath, font), pixels, bold: false, tracking);
    }

    [PgslCommand("DrawTextWidth", "DrawTextWidth(text, size) -> number",
        "Width in pixels that text takes in the current font and letter spacing; size 0 uses the current font size", "Drawing 2D")]
    public static double DrawTextWidth(string text, double size) => MeasureTextNow(text, size).X;

    [PgslCommand("DrawTextHeight", "DrawTextHeight(size) -> number",
        "Line height in pixels of the current font at a size; size 0 uses the current font size", "Drawing 2D")]
    public static double DrawTextHeight(double size) => MeasureTextNow("Ag", size).Y;

    [PgslCommand("DrawSetTextTracking", "DrawSetTextTracking(pixels)",
        "Letter spacing for later text: pixels added between letters, on top of the font's own spacing and kerning (0 is none)", "Drawing 2D")]
    public static void DrawSetTextTracking(double pixels)
    {
        PgslContext ctx = GetContext();
        if (ctx is not null) ctx.TextTracking = double.IsFinite(pixels) ? Math.Clamp(pixels, -64, 256) : 0;
    }

    [PgslCommand("DrawSetTextAlign", "DrawSetTextAlign(align)",
        "Where later text sits on its x: \"left\" starts there, \"center\" is centred on it, \"right\" ends there", "Drawing 2D")]
    public static void DrawSetTextAlign(string align)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        ctx.TextAlign = (align ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "center" or "centre" or "middle" => 1,
            "right" or "end" => 2,
            _ => 0,
        };
    }

    #endregion

    #region Shapes

    [PgslCommand("DrawSetClip", "DrawSetClip(x, y, width, height)",
        "Limit later GUI drawing (shapes, text and images) to a rectangle until DrawResetClip or the end of the event", "Drawing 2D")]
    public static void DrawSetClip(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height)) return;
        // A zero-size clip hides everything rather than clearing the clip.
        Draw?.SetClip(new RectangleF((float)x, (float)y, (float)Math.Max(width, 0.001), (float)Math.Max(height, 0.001)));
    }

    [PgslCommand("DrawResetClip", "DrawResetClip()", "Draw everywhere again after DrawSetClip", "Drawing 2D")]
    public static void DrawResetClip() => Draw?.SetClip(RectangleF.Empty);

    [PgslCommand("DrawLineWidth", "DrawLineWidth(x1, y1, x2, y2, width)", "Line of a given width in pixels", "Drawing 2D")]
    public static void DrawLineWidth(double x1, double y1, double x2, double y2, double width) =>
        Draw?.DrawLine((float)x1, (float)y1, (float)x2, (float)y2, CurrentColor(), (float)Math.Clamp(width, 0.1, 512));

    [PgslCommand("DrawRoundRectEx", "DrawRoundRectEx(x1, y1, x2, y2, radius, border)",
        "Smooth-edged rounded rectangle: filled when border is 0, otherwise an outline that many pixels wide", "Drawing 2D")]
    public static void DrawRoundRectEx(double x1, double y1, double x2, double y2, double radius, double border)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null) return;
        double left = Math.Min(x1, x2), top = Math.Min(y1, y2);
        double width = Math.Abs(x2 - x1), height = Math.Abs(y2 - y1);
        if (width <= 0 || height <= 0) return;
        double r = Math.Clamp(radius, 0, Math.Min(width, height) * 0.5);
        double b = border > 0 ? Math.Min(border, Math.Min(width, height) * 0.5) : 0;

        RoundRectSpan outer = new(left, top, width, height, r);
        RoundRectSpan inner = new(left + b, top + b, width - (b * 2), height - (b * 2), Math.Max(0, r - b));
        FillCoverage(surface, CurrentColor(), top, top + height, (double y, Span<double> spans) =>
            b > 0 ? Subtract(outer, inner, y, spans) : outer.At(y, spans));
    }

    [PgslCommand("DrawCircleEx", "DrawCircleEx(x, y, radius, border)",
        "Smooth-edged circle: filled when border is 0, otherwise a ring that many pixels wide", "Drawing 2D")]
    public static void DrawCircleEx(double x, double y, double radius, double border)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !(radius > 0)) return;
        double b = border > 0 ? Math.Min(border, radius) : 0;
        RoundRectSpan outer = new(x - radius, y - radius, radius * 2, radius * 2, radius);
        double innerRadius = radius - b;
        RoundRectSpan inner = new(x - innerRadius, y - innerRadius, innerRadius * 2, innerRadius * 2, innerRadius);
        FillCoverage(surface, CurrentColor(), y - radius, y + radius, (double row, Span<double> spans) =>
            b > 0 ? Subtract(outer, inner, row, spans) : outer.At(row, spans));
    }

    [PgslCommand("DrawRectangleGradient", "DrawRectangleGradient(x1, y1, x2, y2, r1, g1, b1, a1, r2, g2, b2, a2, vertical)",
        "Rectangle shading from the first colour to the second (channels 0-255, alpha 0-1): top to bottom when vertical, else left to right", "Drawing 2D")]
    public static void DrawRectangleGradient(
        double x1, double y1, double x2, double y2,
        double r1, double g1, double b1, double a1,
        double r2, double g2, double b2, double a2,
        bool vertical)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null) return;
        float left = (float)Math.Min(x1, x2), top = (float)Math.Min(y1, y2);
        float width = (float)Math.Abs(x2 - x1), height = (float)Math.Abs(y2 - y1);
        if (width <= 0 || height <= 0) return;

        // One band per pixel along the gradient: on screen that is the gradient itself.
        float length = vertical ? height : width;
        int bands = (int)Math.Clamp(Math.Ceiling(length), 1, 2048);
        float step = length / bands;
        for (int i = 0; i < bands; i++)
        {
            double t = bands == 1 ? 0.5 : i / (double)(bands - 1);
            Color color = Color.FromArgb(
                Channel(Lerp(a1, a2, t) * 255),
                Channel(Lerp(r1, r2, t)), Channel(Lerp(g1, g2, t)), Channel(Lerp(b1, b2, t)));
            if (color.A == 0) continue;
            RectangleF band = vertical
                ? new RectangleF(left, top + (i * step), width, step)
                : new RectangleF(left + (i * step), top, step, height);
            surface.FillRectangle(color, band);
        }
    }

    private static int Channel(double value) => double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value), 0, 255) : 0;

    /// <summary>A rounded rectangle's horizontal extent at a height (a circle is one with r = half its size).</summary>
    private readonly struct RoundRectSpan
    {
        private readonly double _left, _top, _width, _height, _radius;

        public RoundRectSpan(double left, double top, double width, double height, double radius)
        {
            _left = left; _top = top; _width = width; _height = height; _radius = radius;
        }

        public bool Exists => _width > 0 && _height > 0;

        public bool Extent(double y, out double from, out double to)
        {
            from = to = 0;
            if (!Exists || y < _top || y >= _top + _height) return false;
            double inset = 0;
            double corner = y < _top + _radius ? _top + _radius - y
                : y > _top + _height - _radius ? y - (_top + _height - _radius) : 0;
            if (corner > 0) inset = _radius - Math.Sqrt(Math.Max(0, (_radius * _radius) - (corner * corner)));
            from = _left + inset;
            to = _left + _width - inset;
            return to > from;
        }

        public int At(double y, Span<double> spans)
        {
            if (!Extent(y, out double from, out double to)) return 0;
            spans[0] = from;
            spans[1] = to;
            return 1;
        }
    }

    private static int Subtract(RoundRectSpan outer, RoundRectSpan inner, double y, Span<double> spans)
    {
        if (!outer.Extent(y, out double from, out double to)) return 0;
        if (!inner.Extent(y, out double holeFrom, out double holeTo))
        {
            spans[0] = from; spans[1] = to;
            return 1;
        }
        spans[0] = from; spans[1] = holeFrom;
        spans[2] = holeTo; spans[3] = to;
        return 2;
    }

    private delegate int SpanSource(double y, Span<double> spans);

    private const int CoverageSamples = 4;

    /// <summary>
    /// Fills a shape given as up to two horizontal spans per height, one pixel row at a time, so
    /// no pixel is drawn twice (a translucent shape has no darker seams). Each row is sampled at
    /// four heights: pixels the edge passes through get the share of the pixel the shape covers
    /// as their alpha, which is what makes the edges smooth.
    /// </summary>
    private static void FillCoverage(IPgslDrawSurface surface, Color color, double top, double bottom, SpanSource source)
    {
        if (color.A == 0 || !double.IsFinite(top) || !double.IsFinite(bottom)) return;
        int firstRow = (int)Math.Floor(top);
        int lastRow = Math.Min((int)Math.Ceiling(bottom), firstRow + 8192);
        Span<double> sampleSpans = stackalloc double[CoverageSamples * 4];
        Span<int> sampleCounts = stackalloc int[CoverageSamples];
        Span<double> scratch = stackalloc double[4];
        Span<int> edges = stackalloc int[CoverageSamples * 4];

        for (int row = firstRow; row < lastRow; row++)
        {
            int edgeCount = 0;
            for (int s = 0; s < CoverageSamples; s++)
            {
                int count = source(row + ((s + 0.5) / CoverageSamples), scratch);
                sampleCounts[s] = count;
                for (int i = 0; i < count * 2; i++)
                {
                    sampleSpans[(s * 4) + i] = scratch[i];
                    edges[edgeCount++] = (int)Math.Floor(scratch[i]);
                }
            }
            if (edgeCount == 0) continue;

            edges[..edgeCount].Sort();
            int previous = int.MinValue;
            for (int e = 0; e < edgeCount; e++)
            {
                int column = edges[e];
                if (column == previous) continue;
                // Columns between two edge pixels are covered the same all the way across.
                if (previous != int.MinValue && column > previous + 1)
                {
                    double between = Coverage(sampleSpans, sampleCounts, previous + 1, column);
                    Emit(surface, color, previous + 1, row, column - previous - 1, between);
                }
                Emit(surface, color, column, row, 1, Coverage(sampleSpans, sampleCounts, column, column + 1));
                previous = column;
            }
        }
    }

    /// <summary>Average share of the pixels from <paramref name="from"/> to <paramref name="to"/> the spans cover.</summary>
    private static double Coverage(ReadOnlySpan<double> spans, ReadOnlySpan<int> counts, double from, double to)
    {
        double covered = 0;
        for (int s = 0; s < CoverageSamples; s++)
        {
            for (int i = 0; i < counts[s]; i++)
            {
                double l = Math.Max(from, spans[(s * 4) + (i * 2)]);
                double r = Math.Min(to, spans[(s * 4) + (i * 2) + 1]);
                if (r > l) covered += r - l;
            }
        }
        return covered / ((to - from) * CoverageSamples);
    }

    private static void Emit(IPgslDrawSurface surface, Color color, int x, int y, int width, double coverage)
    {
        if (coverage <= 0.002 || width <= 0) return;
        Color shade = coverage >= 0.998 ? color : Color.FromArgb((int)Math.Round(color.A * Math.Min(1, coverage)), color);
        if (shade.A == 0) return;
        surface.FillRectangle(shade, new RectangleF(x, y, width, 1));
    }

    #endregion

    #region Images

    [PgslCommand("DrawSpritePart", "DrawSpritePart(name, frame, u0, v0, u1, v1, x, y, width, height, alpha)",
        "Part of an image frame into a rectangle: u0, v0 to u1, v1 are fractions of the frame (0 to 1), so a crop or a zoom; tinted by the image blend", "Drawing 2D")]
    public static void DrawSpritePart(string name, double frame, double u0, double v0, double u1, double v1,
        double x, double y, double width, double height, double alpha)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || string.IsNullOrWhiteSpace(name) || !(width > 0) || !(height > 0)) return;
        PgslContext ctx = GetContext();
        surface.DrawSpritePart(name, (int)Math.Max(0, frame),
            RectangleF.FromLTRB((float)u0, (float)v0, (float)u1, (float)v1),
            new RectangleF((float)x, (float)y, (float)width, (float)height),
            ctx?.ImageBlend ?? Color.White, (float)Math.Clamp(alpha, 0, 1));
    }

    [PgslCommand("SpriteWidth", "SpriteWidth(name) -> number", "Width in pixels of an Image's frame; 0 when there is no such Image", "Sprites")]
    public static double SpriteWidth(string name) =>
        ObjectDrawPass.TryGetImageFrameSize(ProjectPath, name, out int width, out _) ? width : 0;

    [PgslCommand("SpriteHeight", "SpriteHeight(name) -> number", "Height in pixels of an Image's frame; 0 when there is no such Image", "Sprites")]
    public static double SpriteHeight(string name) =>
        ObjectDrawPass.TryGetImageFrameSize(ProjectPath, name, out _, out int height) ? height : 0;

    #endregion

    #region Input for menus

    [PgslCommand("WindowSetCursorVisible", "WindowSetCursorVisible(visible)",
        "Show or hide the system pointer over the game window, for a menu that draws its own", "Display")]
    public static void WindowSetCursorVisible(bool visible) =>
        ActiveGameContext?.SetCursorMode(visible ? CursorMode.Normal : CursorMode.Hidden);

    [PgslCommand("GamepadAxisRaw", "GamepadAxisRaw(axis) -> number",
        "A stick or trigger as the controller reports it, with no dead zone; the same names and directions as GamepadAxis", "Input")]
    public static double GamepadAxisRaw(string axis)
    {
        InputState input = ActiveGameContext?.Input;
        if (input == null || string.IsNullOrWhiteSpace(axis)) return 0;
        return axis.Trim().ToLowerInvariant() switch
        {
            "leftx" => input.LeftStickRaw.X,
            "lefty" => input.LeftStickRaw.Y,
            "rightx" => input.RightStickRaw.X,
            "righty" => -input.RightStickRaw.Y,
            "lefttrigger" or "lt" => input.LeftTriggerRaw,
            "righttrigger" or "rt" => input.RightTriggerRaw,
            _ => 0,
        };
    }

    [PgslCommand("GamepadSetDeadZone", "GamepadSetDeadZone(stick, amount)",
        "How far \"Left\", \"Right\" (the sticks), \"Sticks\" (both) or \"Triggers\" must move, 0 to 0.95, before GamepadAxis reads them; the sticks start at 0.18 and the triggers at 0", "Input")]
    public static void GamepadSetDeadZone(string stick, double amount)
    {
        InputState input = ActiveGameContext?.Input;
        if (input == null || !double.IsFinite(amount)) return;
        float zone = (float)Math.Clamp(amount, 0, 0.95);
        switch ((stick ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "left": input.LeftStickDeadZone = zone; break;
            case "right": input.RightStickDeadZone = zone; break;
            case "sticks": input.LeftStickDeadZone = input.RightStickDeadZone = zone; break;
            case "triggers" or "trigger": input.TriggerDeadZone = zone; break;
        }
    }

    [PgslCommand("GamepadGetDeadZone", "GamepadGetDeadZone(stick) -> number", "The dead zone of \"Left\", \"Right\" or \"Triggers\"", "Input")]
    public static double GamepadGetDeadZone(string stick)
    {
        InputState input = ActiveGameContext?.Input;
        if (input == null) return 0;
        return (stick ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "left" or "sticks" => input.LeftStickDeadZone,
            "right" => input.RightStickDeadZone,
            "triggers" or "trigger" => input.TriggerDeadZone,
            _ => 0,
        };
    }

    [PgslCommand("KeyboardTextTake", "KeyboardTextTake() -> string",
        "The characters typed since the last call, in order, as the keyboard layout and shift make them; for name boxes", "Input")]
    public static string KeyboardTextTake() => ActiveGameContext?.Input?.TakeTypedText() ?? string.Empty;

    #endregion

    #region Lighting

    [PgslCommand("EnvironmentSetReflection", "EnvironmentSetReflection(strength)",
        "How strongly models reflect the sky and ground: 0 off, 1 physically balanced, up to 4. Gives metals their colour away from lights", "Lighting")]
    public static void EnvironmentSetReflection(double strength)
    {
        var environment = ActiveGameContext?.Scene?.Environment;
        if (environment != null && double.IsFinite(strength)) environment.EnvironmentReflection = (float)Math.Clamp(strength, 0, 4);
    }

    [PgslCommand("EnvironmentGetReflection", "EnvironmentGetReflection() -> number", "The room's sky and ground reflection strength", "Lighting")]
    public static double EnvironmentGetReflection() => ActiveGameContext?.Scene?.Environment.EnvironmentReflection ?? 0;

    #endregion

    #region Inverse trigonometry

    [PgslCommand("ArcSin", "ArcSin(x) -> number", "The angle in degrees whose sine is x (-1 to 1)", "Math")]
    public static double ArcSin(double x) => Math.Asin(Math.Clamp(x, -1, 1)) * 180.0 / Math.PI;

    [PgslCommand("ArcCos", "ArcCos(x) -> number", "The angle in degrees whose cosine is x (-1 to 1)", "Math")]
    public static double ArcCos(double x) => Math.Acos(Math.Clamp(x, -1, 1)) * 180.0 / Math.PI;

    [PgslCommand("ArcTan2", "ArcTan2(y, x) -> number", "The angle in degrees of the direction (x, y), from -180 to 180", "Math")]
    public static double ArcTan2(double y, double x) => Math.Atan2(y, x) * 180.0 / Math.PI;

    #endregion
}
