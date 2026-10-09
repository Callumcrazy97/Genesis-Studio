using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Many rectangles or image parts from a list in one call (a minimap, a world map, a tile layer a
// script lays out): one script call and one GUI command where there were thousands of each. The
// list is read straight into a reused buffer, so a frame's batch allocates nothing.
public static partial class PgslCommands
{
    private const int RectangleNumbers = 8;
    private const int SpritePartNumbers = 10;

    [ThreadStatic] private static GuiRectangle[] _rectangleBatch;
    [ThreadStatic] private static GuiSpritePart[] _spritePartBatch;

    [PgslCommand("DrawRectanglesFromList", "DrawRectanglesFromList(list, x, y, xscale, yscale) -> rectangles drawn",
        "Draw many filled rectangles in one call from a list holding 8 numbers for each: x, y, width, height, then red, green and blue (0-255) and alpha (0-1) as DrawSetColorRgb and DrawSetAlpha take them. Each is placed at x + its x * xscale, y + its y * yscale and sized by the scales (x, y, xscale and yscale may be left out: 0, 0, 1, 1), so a map kept in cells is drawn anywhere at any zoom. Drawn in list order, exactly as DrawRectangle draws each; entries with no width, height or alpha are skipped",
        "Drawing 2D")]
    public static double DrawRectanglesFromList(double list, double x = 0, double y = 0, double xscale = 1, double yscale = 1)
    {
        IPgslDrawSurface surface = Draw;
        List<object> entries = Resolve<List<object>>("list", list);
        if (surface is null || entries is null || !Finite3(x, y, xscale) || !double.IsFinite(yscale)) return 0;
        int capacity = entries.Count / RectangleNumbers;
        if (capacity == 0) return 0;
        GuiRectangle[] batch = BatchBuffer(ref _rectangleBatch, capacity);
        int count = 0;
        for (int at = 0; at + RectangleNumbers <= entries.Count; at += RectangleNumbers)
        {
            double left = x + (AsNumber(entries[at]) * xscale), top = y + (AsNumber(entries[at + 1]) * yscale);
            double width = AsNumber(entries[at + 2]) * xscale, height = AsNumber(entries[at + 3]) * yscale;
            if (!(width > 0) || !(height > 0) || !double.IsFinite(left) || !double.IsFinite(top)
                || !double.IsFinite(width) || !double.IsFinite(height)) continue;
            // The same rounding as DrawSetColorRgb and DrawSetAlpha, so a batch draws the very
            // pixels the single commands would.
            int alpha = (int)(Unit01(AsNumber(entries[at + 7])) * 255);
            if (alpha <= 0) continue;
            batch[count++] = new GuiRectangle((float)left, (float)top, (float)width, (float)height, new Vector4(
                ChannelOf(AsNumber(entries[at + 4])) / 255f, ChannelOf(AsNumber(entries[at + 5])) / 255f,
                ChannelOf(AsNumber(entries[at + 6])) / 255f, alpha / 255f));
        }
        if (count > 0) surface.FillRectangles(batch.AsSpan(0, count));
        return count;
    }

    [PgslCommand("DrawSpritePartsFromList", "DrawSpritePartsFromList(name, list, x, y, xscale, yscale) -> parts drawn",
        "Draw many parts of one Image in one call from a list holding 10 numbers for each, in DrawSpritePart's order: frame, u0, v0, u1, v1 (fractions of the frame), x, y, width, height, alpha. Placed and sized by x, y, xscale and yscale as DrawRectanglesFromList (may be left out: 0, 0, 1, 1) and tinted by the image blend; a tile map or an icon strip in one call and one batch",
        "Drawing 2D")]
    public static double DrawSpritePartsFromList(string name, double list, double x = 0, double y = 0, double xscale = 1, double yscale = 1)
    {
        IPgslDrawSurface surface = Draw;
        List<object> entries = Resolve<List<object>>("list", list);
        if (surface is null || entries is null || string.IsNullOrWhiteSpace(name)
            || !Finite3(x, y, xscale) || !double.IsFinite(yscale)) return 0;
        int capacity = entries.Count / SpritePartNumbers;
        if (capacity == 0) return 0;
        GuiSpritePart[] batch = BatchBuffer(ref _spritePartBatch, capacity);
        int count = 0;
        for (int at = 0; at + SpritePartNumbers <= entries.Count; at += SpritePartNumbers)
        {
            double frame = AsNumber(entries[at]);
            double left = x + (AsNumber(entries[at + 5]) * xscale), top = y + (AsNumber(entries[at + 6]) * yscale);
            double width = AsNumber(entries[at + 7]) * xscale, height = AsNumber(entries[at + 8]) * yscale;
            double alpha = Unit01(AsNumber(entries[at + 9]));
            if (!(width > 0) || !(height > 0) || !(alpha > 0) || !double.IsFinite(left) || !double.IsFinite(top)
                || !double.IsFinite(width) || !double.IsFinite(height)) continue;
            batch[count++] = new GuiSpritePart(
                double.IsFinite(frame) ? (int)Math.Clamp(frame, 0, int.MaxValue) : 0,
                (float)AsNumber(entries[at + 1]), (float)AsNumber(entries[at + 2]),
                (float)AsNumber(entries[at + 3]), (float)AsNumber(entries[at + 4]),
                (float)left, (float)top, (float)width, (float)height, (float)alpha);
        }
        if (count > 0) surface.DrawSpriteParts(name, batch.AsSpan(0, count), GetContext()?.ImageBlend ?? System.Drawing.Color.White);
        return count;
    }

    /// <summary>A reused buffer of at least <paramref name="needed"/> entries (grown, never shrunk).</summary>
    private static T[] BatchBuffer<T>(ref T[] buffer, int needed)
    {
        if (buffer is null || buffer.Length < needed)
            buffer = new T[Math.Max(needed, Math.Max(64, (buffer?.Length ?? 0) * 2))];
        return buffer;
    }

    /// <summary>0 to 1; not a number is 0.</summary>
    private static double Unit01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    /// <summary>A whole colour channel, 0 to 255, as DrawSetColorRgb takes it; not a number is 0.</summary>
    private static int ChannelOf(double value) => double.IsFinite(value) ? (int)Math.Clamp(value, 0, 255) : 0;
}
