using System.Drawing;
using Genesis.Application.Core;

namespace Genesis.Application.Studio.Theme;

/// <summary>
/// A theme whose colours come from a picture, and which draws that picture behind the shell.
/// </summary>
/// <remarks>
/// The bitmap is loaded once and held for the life of the process. Themes are a handful of files
/// and each one is a few hundred KB decoded; re-reading from disk on every repaint would be
/// unthinkable, and the alternative — a cache that evicts — buys nothing at this scale.
/// </remarks>
public sealed class ThemeImage
{
    private readonly object _gate = new();
    private Image? _picture;
    private ThemePalette? _palette;
    private bool _failed;

    private ThemeImage(string name, string path, bool builtIn)
    {
        Name = name;
        Path = path;
        IsBuiltIn = builtIn;
    }

    /// <summary>Display name, taken from the file name.</summary>
    public string Name { get; }

    /// <summary>Absolute path of the image on disk.</summary>
    public string Path { get; }

    /// <summary>True when this shipped with the application rather than being user-supplied.</summary>
    public bool IsBuiltIn { get; }

    /// <summary>True once the image has failed to load and been given up on.</summary>
    public bool Failed => _failed;

    /// <summary>The palette derived from the picture, or the Dark palette if it will not load.</summary>
    public ThemePalette Palette
    {
        get
        {
            Load();
            return _palette ?? ThemeCatalog.Get("Dark");
        }
    }

    /// <summary>The picture itself, or null if it will not load.</summary>
    public Image? Picture
    {
        get
        {
            Load();
            return _picture;
        }
    }

    /// <summary>Reads a theme from an image file without loading or decoding it yet.</summary>
    public static ThemeImage FromFile(string path, bool builtIn) =>
        new(Describe(path), System.IO.Path.GetFullPath(path), builtIn);

    /// <summary>
    /// Turns a file name into a display name: <c>Theme_Cosmic_Nebula.jpg</c> becomes
    /// <c>Cosmic Nebula</c>.
    /// </summary>
    public static string Describe(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (name.StartsWith("Theme_", StringComparison.OrdinalIgnoreCase))
        {
            name = name["Theme_".Length..];
        }

        return name.Replace('_', ' ').Replace('-', ' ').Trim();
    }

    /// <summary>Every image Studio can offer as a theme, installed ones first.</summary>
    /// <remarks>
    /// A user file wins a name clash, so someone can replace a shipped theme by dropping a file of
    /// the same name into their own folder rather than editing the installation.
    /// </remarks>
    public static IReadOnlyList<ThemeImage> Discover()
    {
        Dictionary<string, ThemeImage> found = new(StringComparer.OrdinalIgnoreCase);

        foreach (ThemeImage theme in Scan(ApplicationPaths.InstalledThemesDirectory, builtIn: true))
        {
            found[theme.Name] = theme;
        }

        foreach (ThemeImage theme in Scan(ApplicationPaths.UserThemesDirectory, builtIn: false))
        {
            found[theme.Name] = theme;
        }

        return [.. found.Values.OrderBy(theme => theme.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<ThemeImage> Scan(string directory, bool builtIn)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            files = Directory.GetFiles(directory);
        }
        catch (IOException)
        {
            // A themes folder that cannot be listed is a cosmetic loss, never a startup failure.
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return files
            .Where(file => IsSupported(System.IO.Path.GetExtension(file)))
            .Select(file => FromFile(file, builtIn));
    }

    private static bool IsSupported(string extension) =>
        extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";

    private void Load()
    {
        if (_palette is not null || _failed)
        {
            return;
        }

        lock (_gate)
        {
            if (_palette is not null || _failed)
            {
                return;
            }

            try
            {
                // Copied into memory first: Image.FromFile keeps a lock on the file for the life of
                // the object, which would stop the user replacing a theme image while Studio runs.
                byte[] bytes = File.ReadAllBytes(Path);
                using MemoryStream stream = new(bytes);
                Image picture = Image.FromStream(stream);

                _palette = PaletteExtractor.FromImage(picture);
                _picture = picture;
            }
            catch (IOException)
            {
                _failed = true;
            }
            catch (UnauthorizedAccessException)
            {
                _failed = true;
            }
            catch (ArgumentException)
            {
                // Image.FromStream reports a file that is not a picture this way.
                _failed = true;
            }
        }
    }
}
