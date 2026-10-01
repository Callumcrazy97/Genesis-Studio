using System.Drawing;

namespace Genesis.Application.Core.UI;

/// <summary>
/// The "Clear" design tokens shared by Studio, the editor suites and the Image editor.
/// </summary>
/// <remarks>
/// Studio pushes its live palette here (with <c>EditorChrome</c> and <c>ImageEditorChrome</c>) on
/// every theme change, so components that live below the editor assemblies — the workflow bar,
/// starter galleries and the pill toolbar renderer — follow the theme without referencing Studio.
/// Defaults are the Genesis Dark values, which is what headless hosts that never load Studio see.
/// </remarks>
public static class UiTokens
{
    public static Color Canvas { get; private set; } = FromHex("#14161D");
    public static Color Surface { get; private set; } = FromHex("#1C1F28");
    public static Color Raised { get; private set; } = FromHex("#252934");
    public static Color Hover { get; private set; } = FromHex("#2E3340");
    public static Color Border { get; private set; } = FromHex("#373D4C");
    public static Color Text { get; private set; } = FromHex("#EEF1F8");
    public static Color Muted { get; private set; } = FromHex("#9DA6BB");
    public static Color Accent { get; private set; } = FromHex("#6C8CFF");
    public static Color Success { get; private set; } = FromHex("#5BCA9A");
    public static Color Warning { get; private set; } = FromHex("#F5B551");
    public static Color Error { get; private set; } = FromHex("#F4626F");
    public static bool IsDark { get; private set; } = true;

    /// <summary>Accent mixed into the surface: the fill of a current step or a checked mode.</summary>
    public static Color AccentSoft => Blend(Accent, Surface, 0.26f);

    /// <summary>Text drawn on a solid accent fill.</summary>
    public static Color OnAccent => Luminance(Accent) > 0.6f ? FromHex("#10131A") : Color.White;

    public static Font BaseFont { get; private set; } = new("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>Secondary text. One point smaller than body text, never the old 8 pt.</summary>
    public static Font SmallFont { get; private set; } = new("Segoe UI Variable Text", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

    public static Font StrongFont { get; private set; } = new("Segoe UI Variable Text", 9.5f, FontStyle.Bold, GraphicsUnit.Point);

    /// <summary>Page and gallery titles.</summary>
    public static Font TitleFont { get; private set; } = new("Segoe UI Variable Display", 12.5f, FontStyle.Bold, GraphicsUnit.Point);

    /// <summary>Raised after <see cref="Update"/>; live controls re-theme from it.</summary>
    public static event EventHandler? Changed;

    public static void Update(
        Color canvas,
        Color surface,
        Color raised,
        Color hover,
        Color border,
        Color text,
        Color muted,
        Color accent,
        Color success,
        Color warning,
        Color error,
        bool isDark,
        Font baseFont)
    {
        Canvas = canvas;
        Surface = surface;
        Raised = raised;
        Hover = hover;
        Border = border;
        Text = text;
        Muted = muted;
        Accent = accent;
        Success = success;
        Warning = warning;
        Error = error;
        IsDark = isDark;
        float size = baseFont.SizeInPoints;
        BaseFont = baseFont;
        SmallFont = new Font(baseFont.FontFamily, MathF.Max(7.5f, size - 1f), FontStyle.Regular, GraphicsUnit.Point);
        StrongFont = new Font(baseFont.FontFamily, size, FontStyle.Bold, GraphicsUnit.Point);
        TitleFont = new Font("Segoe UI Variable Display", size + 3f, FontStyle.Bold, GraphicsUnit.Point);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static Color Blend(Color top, Color bottom, float amount)
    {
        float a = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            255,
            (int)Math.Round(top.R * a + bottom.R * (1 - a)),
            (int)Math.Round(top.G * a + bottom.G * (1 - a)),
            (int)Math.Round(top.B * a + bottom.B * (1 - a)));
    }

    public static float Luminance(Color color) =>
        (0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B) / 255f;

    public static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(
            255,
            Convert.ToInt32(hex.Substring(0, 2), 16),
            Convert.ToInt32(hex.Substring(2, 2), 16),
            Convert.ToInt32(hex.Substring(4, 2), 16));
    }

    /// <summary>
    /// Converts an ALL-CAPS caption to sentence case ("MATERIAL PRESETS" → "Material presets"),
    /// keeping acronyms. Mixed-case captions are returned unchanged.
    /// </summary>
    public static string DisplayHeading(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsLower))
        {
            return text;
        }

        string[] words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            string bare = word.Trim('(', ')', ',', ':', '·', '/', '—', '-');
            if (bare.Length == 0 || KeepsCapitals(bare))
            {
                continue;
            }

            string lower = word.ToLowerInvariant();
            words[i] = i == 0 || words[i - 1].EndsWith('.') || words[i - 1] == "/"
                ? char.ToUpperInvariant(lower[0]) + lower[1..]
                : lower;
        }

        string result = string.Join(' ', words);
        return result.Length > 0 && char.IsLower(result[0])
            ? char.ToUpperInvariant(result[0]) + result[1..]
            : result;
    }

    private static bool KeepsCapitals(string word) =>
        word.Any(char.IsDigit) || Acronyms.Contains(word);

    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "HLSL", "PGSL", "GLSL", "PBR", "GPU", "CPU", "FPS", "RGB", "RGBA", "HDR", "XYZ", "UV", "UVS",
        "UI", "HUD", "LOD", "API", "ID", "GUID", "IK", "FX", "SFX", "AI", "PNG", "JPG", "WAV", "GLB", "FBX",
    };
}
