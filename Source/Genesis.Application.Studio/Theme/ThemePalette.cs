using System.Drawing;

namespace Genesis.Application.Studio.Theme;

public sealed record ThemePalette(
    Color Canvas,
    Color Surface,
    Color SurfaceRaised,
    Color SurfaceHover,
    Color Border,
    Color BorderStrong,
    Color Text,
    Color TextMuted,
    Color Accent,
    Color AccentHover,
    Color Success,
    Color Warning,
    Color Error)
{
    public bool IsDark
    {
        get
        {
            double luminance = (0.299 * Canvas.R) + (0.587 * Canvas.G) + (0.114 * Canvas.B);
            return luminance < 140;
        }
    }

    public static ThemePalette Dark => ThemeCatalog.Get("Dark");

    public static ThemePalette Light => ThemeCatalog.Get("Light");

    public static ThemePalette FromName(string? name) => ThemeCatalog.Get(name);
}
