using System;
using System.Collections.Generic;
using System.Drawing;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Pictures a script paints at run time (a map, a chart, a sign): pixels set from lists, drawn like
// an image. A pixel is red, green and blue from 0 to 255 and alpha from 0 to 1, as DrawSetColorRgb,
// DrawSetAlpha and MeshAddQuadColors take a colour. Changed pixels reach the GPU once, when the
// texture is next drawn, so a map painted once costs one sprite a frame to draw.
public static partial class PgslCommands
{
    private const int PixelNumbers = 4;

    [PgslCommand("TextureCreate", "TextureCreate(width, height) -> texture",
        "A picture a script paints, width x height pixels (1 to 4096 each), transparent at first; 0 when the size is not allowed. Paint it with TextureSetPixels, TextureSetRegion or TextureFillRectanglesFromList and draw it with DrawTexture",
        "Textures")]
    public static double TextureCreate(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && width >= 1 && height >= 1
            && width <= ScriptTextures.MaxSize && height <= ScriptTextures.MaxSize
            ? ScriptTextures.Create((int)width, (int)height)
            : 0;

    [PgslCommand("TextureSetPixels", "TextureSetPixels(texture, list) -> pixels set",
        "Set a texture's pixels from a list holding 4 numbers for each, row by row from the top-left corner: red, green, blue (0-255) and alpha (0-1). A shorter list sets the pixels it holds",
        "Textures")]
    public static double TextureSetPixels(double texture, double list) =>
        TextureSetRegion(texture, 0, 0, ScriptTextures.Width(ValidTexture(texture)), ScriptTextures.Height(ValidTexture(texture)), list);

    [PgslCommand("TextureSetRegion", "TextureSetRegion(texture, x, y, width, height, list) -> pixels set",
        "Set the pixels of a rectangle of a texture (x, y from its top-left corner) from a list holding 4 numbers for each, row by row: red, green, blue (0-255) and alpha (0-1). Pixels falling outside the texture are left out; a shorter list sets the pixels it holds",
        "Textures")]
    public static double TextureSetRegion(double texture, double x, double y, double width, double height, double list)
    {
        List<object> entries = Resolve<List<object>>("list", list);
        int id = ValidTexture(texture);
        if (entries is null || id == 0 || !Finite3(x, y, width) || !double.IsFinite(height) || width < 1 || height < 1) return 0;
        int left = (int)Math.Floor(x), top = (int)Math.Floor(y);
        long regionWidth = (long)Math.Min(width, ScriptTextures.MaxSize), regionHeight = (long)Math.Min(height, ScriptTextures.MaxSize);
        long available = Math.Min(regionWidth * regionHeight, entries.Count / PixelNumbers);
        if (available <= 0) return 0;
        // Only the rows the list reaches are changed.
        int rows = (int)((available + regionWidth - 1) / regionWidth);
        byte[] pixels = ScriptTextures.BeginWrite(id, left, top, (int)Math.Min(left + regionWidth, int.MaxValue),
            (int)Math.Min(top + (long)rows, int.MaxValue), out int textureWidth, out int textureHeight);
        if (pixels is null) return 0;
        int set = 0;
        for (long i = 0; i < available; i++)
        {
            long px = left + (i % regionWidth), py = top + (i / regionWidth);
            if (px < 0 || py < 0 || px >= textureWidth || py >= textureHeight) continue;
            int at = (int)i * PixelNumbers;
            WritePixel(pixels, (int)(((py * textureWidth) + px) * 4),
                AsNumber(entries[at]), AsNumber(entries[at + 1]), AsNumber(entries[at + 2]), AsNumber(entries[at + 3]));
            set++;
        }
        return set;
    }

    [PgslCommand("TextureFillRectanglesFromList", "TextureFillRectanglesFromList(texture, list) -> rectangles painted",
        "Paint rectangles into a texture from a list laid out as DrawRectanglesFromList's: x, y, width, height (pixels of the texture), red, green, blue (0-255), alpha (0-1). Each replaces the pixels it covers, alpha included, and is cut to the texture: a map's runs painted once, then drawn every frame with DrawTexture",
        "Textures")]
    public static double TextureFillRectanglesFromList(double texture, double list)
    {
        List<object> entries = Resolve<List<object>>("list", list);
        int id = ValidTexture(texture);
        if (entries is null || id == 0) return 0;
        int textureWidth = ScriptTextures.Width(id), textureHeight = ScriptTextures.Height(id);
        int painted = 0;
        for (int at = 0; at + RectangleNumbers <= entries.Count; at += RectangleNumbers)
        {
            double x = AsNumber(entries[at]), y = AsNumber(entries[at + 1]);
            double w = AsNumber(entries[at + 2]), h = AsNumber(entries[at + 3]);
            if (!Finite3(x, y, w) || !double.IsFinite(h) || !(w > 0) || !(h > 0)) continue;
            // Whole pixels: the ones whose centres the rectangle covers.
            int left = (int)Math.Clamp(Math.Round(x), 0, textureWidth), right = (int)Math.Clamp(Math.Round(x + w), 0, textureWidth);
            int top = (int)Math.Clamp(Math.Round(y), 0, textureHeight), bottom = (int)Math.Clamp(Math.Round(y + h), 0, textureHeight);
            byte[] pixels = ScriptTextures.BeginWrite(id, left, top, right, bottom, out _, out _);
            if (pixels is null) continue;
            int first = ((top * textureWidth) + left) * 4;
            WritePixel(pixels, first, AsNumber(entries[at + 4]), AsNumber(entries[at + 5]), AsNumber(entries[at + 6]), AsNumber(entries[at + 7]));
            // One pixel written, then copied along the row, and the row down the rectangle.
            Span<byte> colour = pixels.AsSpan(first, 4);
            int rowBytes = (right - left) * 4;
            for (int column = 1; column < right - left; column++) colour.CopyTo(pixels.AsSpan(first + (column * 4), 4));
            for (int row = top + 1; row < bottom; row++)
                pixels.AsSpan(first, rowBytes).CopyTo(pixels.AsSpan(((row * textureWidth) + left) * 4, rowBytes));
            painted++;
        }
        return painted;
    }

    [PgslCommand("TextureSetSmooth", "TextureSetSmooth(texture, smooth)",
        "Whether a texture is smoothed when drawn bigger or smaller than its pixels. Off at first: its pixels stay sharp, as a map's should",
        "Textures")]
    public static void TextureSetSmooth(double texture, bool smooth) => ScriptTextures.SetSmooth(ValidTexture(texture), smooth);

    [PgslCommand("TextureWidth", "TextureWidth(texture) -> number", "A texture's width in pixels; 0 when there is no such texture", "Textures")]
    public static double TextureWidth(double texture) => ScriptTextures.Width(ValidTexture(texture));

    [PgslCommand("TextureHeight", "TextureHeight(texture) -> number", "A texture's height in pixels; 0 when there is no such texture", "Textures")]
    public static double TextureHeight(double texture) => ScriptTextures.Height(ValidTexture(texture));

    [PgslCommand("TextureExists", "TextureExists(texture) -> bool", "Whether a texture exists (made by TextureCreate and not destroyed)", "Textures")]
    public static bool TextureExists(double texture) => ValidTexture(texture) != 0 && ScriptTextures.Exists(ValidTexture(texture));

    [PgslCommand("TextureDestroy", "TextureDestroy(texture)",
        "Free a texture and its copy on the graphics card. Every texture is freed when the game ends", "Textures")]
    public static void TextureDestroy(double texture) => ScriptTextures.Destroy(ValidTexture(texture));

    [PgslCommand("DrawTexture", "DrawTexture(texture, x, y, xscale, yscale, angle, alpha)",
        "Draw a texture like an image, its top-left corner at x, y, scaled and turned (degrees) about that corner and tinted by the image blend; in Draw GUI (ordered with the GUI's other drawing) or a 2D Draw event. Changed pixels go to the graphics card once, when it is next drawn",
        "Drawing 2D")]
    public static void DrawTexture(double texture, double x, double y, double xscale, double yscale, double angle, double alpha)
    {
        IPgslDrawSurface surface = Draw;
        int id = ValidTexture(texture);
        if (surface is null || id == 0 || !Finite3(x, y, xscale) || !double.IsFinite(yscale) || !double.IsFinite(angle)
            || xscale == 0 || yscale == 0) return;
        float width = (float)(ScriptTextures.Width(id) * xscale), height = (float)(ScriptTextures.Height(id) * yscale);
        surface.DrawScriptTexture(id, new RectangleF(0f, 0f, 1f, 1f), new RectangleF((float)x, (float)y, width, height),
            (float)angle, GetContext()?.ImageBlend ?? Color.White, (float)Unit01(alpha));
    }

    [PgslCommand("DrawTexturePart", "DrawTexturePart(texture, u0, v0, u1, v1, x, y, width, height, alpha)",
        "Part of a texture into a rectangle, as DrawSpritePart: u0, v0 to u1, v1 are fractions of the texture (0 to 1), so a window onto a big map, or a zoom; tinted by the image blend",
        "Drawing 2D")]
    public static void DrawTexturePart(double texture, double u0, double v0, double u1, double v1,
        double x, double y, double width, double height, double alpha)
    {
        IPgslDrawSurface surface = Draw;
        int id = ValidTexture(texture);
        if (surface is null || id == 0 || !Finite3(x, y, width) || !double.IsFinite(height) || !(width > 0) || !(height > 0)) return;
        float left = (float)Unit01(u0), right = (float)Unit01(u1), top = (float)Unit01(v0), bottom = (float)Unit01(v1);
        if (right <= left || bottom <= top) return;
        surface.DrawScriptTexture(id, RectangleF.FromLTRB(left, top, right, bottom),
            new RectangleF((float)x, (float)y, (float)width, (float)height), 0f,
            GetContext()?.ImageBlend ?? Color.White, (float)Unit01(alpha));
    }

    /// <summary>A texture id the registry could hold, or 0.</summary>
    private static int ValidTexture(double texture) =>
        double.IsFinite(texture) && texture >= 1 && texture <= int.MaxValue ? (int)texture : 0;

    /// <summary>One pixel as RGBA bytes, rounded as DrawSetColorRgb and DrawSetAlpha round a colour.</summary>
    private static void WritePixel(byte[] pixels, int at, double r, double g, double b, double a)
    {
        pixels[at] = (byte)ChannelOf(r);
        pixels[at + 1] = (byte)ChannelOf(g);
        pixels[at + 2] = (byte)ChannelOf(b);
        pixels[at + 3] = (byte)(int)(Unit01(a) * 255);
    }
}
