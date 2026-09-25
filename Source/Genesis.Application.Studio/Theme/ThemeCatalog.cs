using System.Drawing;
using Genesis.Application.Core.Settings;

namespace Genesis.Application.Studio.Theme;

/// <summary>
/// Named Studio themes aligned with the legacy Genesis ThemeRegistry set.
/// Each theme owns its full palette including accent — no separate hex accent field.
/// </summary>
public static class ThemeCatalog
{
    private static readonly Dictionary<string, ThemePalette> Themes =
        new(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<ThemeImage>? _images;

    /// <summary>
    /// Colour themes offered in Preferences. <c>Dark</c> is the product <b>Genesis Dark</b>
    /// palette (Application Design Images); the prefs key stays <c>Dark</c> so saved settings
    /// keep working. Retinting Dark must never remove other colour names or the image-theme
    /// discovery path — built-in JPGs under repo <c>Themes/</c> ship via Content copy.
    /// </summary>
    public static IReadOnlyList<string> ColourNames { get; } =
    [
        "Dark", "Light", "Blue", "Green", "Default", "Legacy",
        "Midnight", "Aurora", "Slate", "Neon", "Twilight", "Dawn",
        "Luna", "Cyberpunk", "Nord", "Monokai", "Forest", "Coffee",
    ];

    /// <summary>Friendly label for a colour theme key (prefs still store <c>Dark</c>/<c>Light</c>).</summary>
    public static string ColourDisplayName(string? name) => name?.Trim() switch
    {
        "Dark" or "Graphite" or "Genesis Dark" => "Genesis Dark",
        "Light" or "Genesis Light" => "Genesis Light",
        _ => string.IsNullOrWhiteSpace(name) ? "Genesis Dark" : name.Trim(),
    };

    /// <summary>
    /// Built-in image theme display names that must ship beside the executable.
    /// Gate: <c>Theme.Images.ShipAndAreDiscovered</c>.
    /// </summary>
    public static IReadOnlyList<string> RequiredBuiltInImageThemes { get; } =
    [
        "Cosmic Nebula",
        "Cyberpunk Grid",
        "Ocean Paradise",
        "Rainforest Canopy",
        "Snowy Mountains",
        "Synthwave Horizon",
        "Underground Luminous",
        "Urban Metropolis",
    ];

    /// <summary>Every theme that can be chosen, image themes first.</summary>
    /// <remarks>
    /// Image themes lead because they are the ones with a picture behind them and the ones a person
    /// is most likely to be looking for. The colour themes remain, unchanged and still selectable —
    /// they are what someone reaches for when they want the chrome to disappear.
    /// </remarks>
    public static IReadOnlyList<string> Names =>
        [.. Images.Select(image => image.Name), .. ColourNames];

    /// <summary>The image themes found on disk, discovered once per session.</summary>
    /// <remarks>
    /// Cached rather than re-scanned: the list is read on every Preferences repaint, and hitting the
    /// file system from a paint handler is how an interface starts to feel sticky. <see
    /// cref="RefreshImages"/> exists for the one moment the cache is genuinely stale — a user has
    /// just added a file.
    /// </remarks>
    public static IReadOnlyList<ThemeImage> Images => _images ??= ThemeImage.Discover();

    /// <summary>Rescans the themes folders. Call after adding or removing an image.</summary>
    public static void RefreshImages()
    {
        ThemeThumbnailCache.Clear();
        _images = ThemeImage.Discover();
    }

    /// <summary>Resolves an explicit mode, or infers the mode used by pre-v2 preferences.</summary>
    public static string ResolveMode(string? mode, string? themeName)
    {
        if (string.Equals(mode, AppearanceThemeModes.Image, StringComparison.OrdinalIgnoreCase))
        {
            return AppearanceThemeModes.Image;
        }

        if (string.Equals(mode, AppearanceThemeModes.Colour, StringComparison.OrdinalIgnoreCase))
        {
            return AppearanceThemeModes.Colour;
        }

        return FindImage(themeName) is null
            ? AppearanceThemeModes.Colour
            : AppearanceThemeModes.Image;
    }

    /// <summary>The image theme of that name, or null when the name is not one.</summary>
    /// <remarks>
    /// A built-in colour theme always wins the name, matching <see cref="Get"/>. Without that the
    /// two disagree: a file called <c>Monokai.jpg</c> in the user's themes folder would paint its
    /// backdrop while the palette still came from the built-in Monokai.
    /// </remarks>
    public static ThemeImage? FindImage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();
        if (Themes.ContainsKey(trimmed))
        {
            return null;
        }

        return FindSelectableImage(trimmed);
    }

    /// <summary>Finds an image selected from the explicit gallery, including colour-name clashes.</summary>
    /// <remarks>
    /// The separated UI makes <c>Monokai</c> colour and <c>Monokai.jpg</c> image unambiguous. Legacy
    /// automatic mode still uses <see cref="FindImage"/>, where the built-in colour must win to
    /// preserve the meaning of old preferences.
    /// </remarks>
    public static ThemeImage? FindSelectableImage(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Trim();
        return Images.FirstOrDefault(image =>
            string.Equals(image.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    static ThemeCatalog()
    {
        // Genesis Dark — SSOT for Application Design Images + EditorChrome / ImageEditorChrome.
        // Canvas #14161D, Surface #1C1F28, Raised #252934, Hover #2E3340, Border #373D4C,
        // Accent #6C8CFF (action blue only).
        Add("Dark", C(20, 22, 29), C(28, 31, 40), C(37, 41, 52), C(46, 51, 64),
            C(55, 61, 76), C(74, 81, 102), C(238, 241, 248), C(157, 166, 187),
            C(108, 140, 255), C(138, 164, 255), C(91, 202, 154), C(245, 181, 81), C(244, 98, 111));

        Add("Default", C(40, 44, 52), C(49, 54, 64), C(55, 60, 70), C(60, 66, 78),
            C(68, 73, 80), C(90, 96, 108), C(229, 229, 229), C(160, 166, 178),
            C(0, 122, 204), C(40, 150, 230), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Legacy", C(35, 39, 46), C(45, 50, 60), C(50, 55, 66), C(55, 60, 72),
            C(70, 78, 90), C(100, 110, 128), C(224, 224, 224), C(150, 156, 168),
            C(0, 191, 255), C(40, 210, 255), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Midnight", C(13, 17, 23), C(22, 27, 34), C(28, 33, 40), C(36, 42, 51),
            C(48, 54, 61), C(70, 78, 88), C(201, 209, 217), C(139, 148, 158),
            C(0, 122, 204), C(40, 150, 230), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Twilight", C(20, 20, 20), C(28, 28, 28), C(34, 34, 34), C(40, 40, 40),
            C(48, 48, 48), C(70, 70, 70), C(176, 176, 176), C(120, 120, 120),
            C(0, 122, 204), C(40, 150, 230), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Neon", C(10, 14, 39), C(17, 22, 51), C(24, 29, 64), C(34, 40, 84),
            C(42, 42, 74), C(70, 70, 110), C(224, 224, 224), C(150, 150, 180),
            C(0, 255, 200), C(80, 255, 220), C(57, 255, 20), C(200, 130, 0), C(200, 40, 40));

        Add("Cyberpunk", C(10, 10, 15), C(18, 18, 30), C(26, 26, 46), C(36, 36, 60),
            C(51, 51, 68), C(80, 80, 100), C(224, 224, 224), C(150, 150, 170),
            C(255, 0, 200), C(255, 80, 220), C(0, 255, 150), C(200, 130, 0), C(200, 40, 40));

        Add("Monokai", C(39, 40, 34), C(50, 51, 44), C(56, 57, 50), C(62, 63, 55),
            C(73, 72, 62), C(100, 98, 85), C(248, 248, 242), C(168, 170, 158),
            C(166, 226, 46), C(190, 240, 80), C(102, 217, 239), C(200, 130, 0), C(200, 40, 40));

        Add("Light", Color.White, C(243, 243, 243), C(250, 250, 250), C(236, 236, 236),
            C(208, 208, 208), C(180, 180, 180), Color.Black, C(110, 110, 110),
            C(0, 120, 215), C(40, 150, 235), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Dawn", C(230, 223, 207), C(214, 207, 191), C(222, 215, 199), C(210, 203, 187),
            C(192, 184, 168), C(170, 160, 140), C(74, 74, 64), C(120, 116, 100),
            C(180, 120, 60), C(200, 140, 80), C(120, 150, 80), C(180, 120, 40), C(160, 60, 60));

        Add("Blue", C(26, 26, 46), C(22, 33, 62), C(28, 40, 72), C(34, 48, 84),
            C(15, 52, 96), C(40, 80, 130), C(234, 234, 234), C(150, 160, 200),
            C(90, 160, 255), C(120, 180, 255), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Green", C(30, 42, 30), C(37, 53, 37), C(44, 62, 44), C(50, 70, 50),
            C(45, 74, 45), C(70, 100, 70), C(234, 234, 234), C(150, 180, 150),
            C(120, 200, 120), C(150, 220, 150), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Aurora", C(10, 22, 40), C(15, 31, 58), C(24, 48, 84), C(30, 56, 94),
            C(30, 58, 95), C(50, 90, 140), C(224, 230, 240), C(150, 170, 200),
            C(120, 220, 200), C(150, 240, 220), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Slate", C(44, 62, 80), C(52, 73, 94), C(58, 82, 106), C(66, 92, 120),
            C(74, 106, 128), C(100, 130, 155), C(236, 240, 241), C(160, 178, 196),
            C(120, 180, 220), C(150, 200, 235), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Luna", C(26, 27, 38), C(31, 35, 53), C(38, 43, 64), C(44, 50, 73),
            C(41, 46, 66), C(70, 78, 110), C(169, 177, 214), C(120, 128, 170),
            C(150, 160, 255), C(180, 190, 255), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Nord", C(46, 52, 64), C(59, 66, 82), C(66, 74, 90), C(72, 80, 98),
            C(76, 86, 106), C(100, 112, 136), C(216, 222, 233), C(160, 170, 186),
            C(136, 192, 208), C(160, 210, 225), C(163, 190, 140), C(235, 203, 139), C(191, 97, 106));

        Add("Forest", C(26, 35, 26), C(36, 47, 36), C(42, 55, 42), C(48, 62, 48),
            C(51, 66, 51), C(75, 95, 75), C(200, 214, 200), C(140, 160, 140),
            C(140, 180, 120), C(170, 210, 150), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        Add("Coffee", C(40, 33, 28), C(54, 45, 38), C(60, 50, 42), C(66, 55, 46),
            C(73, 61, 51), C(100, 85, 70), C(214, 197, 181), C(150, 140, 126),
            C(190, 150, 110), C(210, 170, 130), C(45, 156, 72), C(200, 130, 0), C(200, 40, 40));

        // Legacy aliases from earlier greenfield builds.
        Themes["Obsidian"] = Themes["Midnight"];
        Themes["Graphite"] = Themes["Dark"];
        Themes["Genesis Dark"] = Themes["Dark"];
        Themes["Genesis Light"] = Themes["Light"];
    }

    /// <summary>The palette for a theme name, whether it is a colour theme or an image one.</summary>
    /// <remarks>
    /// Colour themes are checked first so a user cannot shadow a built-in name by dropping a file
    /// called <c>Monokai.jpg</c> into their themes folder and silently changing what Monokai means.
    /// </remarks>
    public static ThemePalette Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Themes["Dark"];
        }

        if (Themes.TryGetValue(name.Trim(), out ThemePalette? palette))
        {
            return palette;
        }

        return FindImage(name)?.Palette ?? Themes["Dark"];
    }

    /// <summary>A flat palette lookup that never turns an image name into a backdrop theme.</summary>
    public static ThemePalette GetColour(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Themes["Dark"];
        }

        return Themes.TryGetValue(name.Trim(), out ThemePalette? palette)
            ? palette
            : Themes["Dark"];
    }

    public static Color[] PreviewSwatches(string? name)
    {
        ThemePalette palette = Get(name);
        return
        [
            palette.Canvas,
            palette.Surface,
            palette.SurfaceRaised,
            palette.Border,
            palette.Text,
            palette.Accent,
            palette.Success,
        ];
    }

    private static void Add(
        string name,
        Color canvas,
        Color surface,
        Color surfaceRaised,
        Color surfaceHover,
        Color border,
        Color borderStrong,
        Color text,
        Color textMuted,
        Color accent,
        Color accentHover,
        Color success,
        Color warning,
        Color error)
    {
        Themes[name] = new ThemePalette(
            canvas,
            surface,
            surfaceRaised,
            surfaceHover,
            border,
            borderStrong,
            text,
            textMuted,
            accent,
            accentHover,
            success,
            warning,
            error);
    }

    private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);
}
