using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Genesis.Application.Core.UI;

/// <summary>
/// Windows icon-font glyphs (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10).
/// </summary>
/// <remarks>
/// Each code point was checked against a rendered sheet of Segoe Fluent Icons. When neither font is
/// installed <see cref="Available"/> is false and callers fall back to their text captions, so an
/// icon is never the only label.
/// </remarks>
public static class UiGlyphs
{
    // Commands
    public const string Add = "";
    public const string Close = "";
    public const string More = "";
    public const string Settings = "";
    public const string Stop = "";
    public const string Search = "";
    public const string Next = "";
    public const string Back = "";
    public const string Check = "";
    public const string Save = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Redo = "";
    public const string Undo = "";
    public const string Refresh = "";
    public const string Edit = "";
    public const string Upload = "";
    public const string Download = "";
    public const string Link = "";
    public const string View = "";
    public const string Move = "";
    public const string Sliders = "";
    public const string Bug = "";
    public const string Pin = "";
    public const string Flag = "";
    public const string Target = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Timer = "";
    public const string Keyboard = "";
    public const string List = "";
    public const string Tiles = "";

    // Places and documents
    public const string Home = "";
    public const string Folder = "";
    public const string Document = "";
    public const string Library = "";
    public const string Package = "";

    // Resource kinds
    public const string Picture = "";
    public const string Brush = "";
    public const string Pen = "";
    public const string Code = "";
    public const string Color = "";
    public const string Audio = "";
    public const string Layers = "";
    public const string Grid = "";
    public const string Floor = "";
    public const string Map = "";
    public const string Monitor = "";
    public const string Font = "";
    public const string Cube = "";
    public const string Puzzle = "";
    public const string Game = "";
    public const string Camera = "";
    public const string Video = "";
    public const string Mountain = "";
    public const string Bones = "";
    public const string Flask = "";
    public const string Sparkle = "";
    public const string Lightning = "";
    public const string Pulse = "";

    // People
    public const string People = "";
    public const string Person = "";
    public const string Walk = "";
    public const string Location = "";

    // Nature and weather
    public const string World = "";
    public const string Globe = "";
    public const string Sun = "";
    public const string Moon = "";
    public const string Cloud = "";
    public const string Sunrise = "";
    public const string Sunset = "";
    public const string Snow = "";
    public const string Drop = "";
    public const string Waves = "";
    public const string Fire = "";
    public const string Leaf = "";
    public const string Star = "";
    public const string Heart = "";
    public const string Shield = "";

    private static readonly Lazy<FontFamily?> Family = new(FindFamily);

    public static bool Available => Family.Value is not null;

    /// <summary>A glyph font at <paramref name="points"/>, or null when no icon font exists.</summary>
    public static System.Drawing.Font? CreateFont(float points) =>
        Family.Value is { } family ? new System.Drawing.Font(family, points, FontStyle.Regular, GraphicsUnit.Point) : null;

    public static void Draw(Graphics graphics, string glyph, Rectangle bounds, System.Drawing.Color color, float points)
    {
        using System.Drawing.Font? font = CreateFont(points);
        if (font is null)
        {
            return;
        }

        TextRenderer.DrawText(
            graphics,
            glyph,
            font,
            bounds,
            color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static FontFamily? FindFamily()
    {
        try
        {
            using InstalledFontCollection installed = new();
            foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            {
                if (installed.Families.Any(family => string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return new FontFamily(name);
                }
            }
        }
        catch (ArgumentException)
        {
            // Font enumeration can fail in locked-down hosts; captions carry the meaning.
        }

        return null;
    }
}
