using System.Drawing;

namespace Genesis.Application.Studio.Theme;

/// <summary>
/// The Genesis marks, loaded once from the files that ship beside the executable.
/// </summary>
/// <remarks>
/// Kept out of resources and read from disk so the artwork can be replaced without a rebuild, which
/// is how the logo was iterated on in the first place. Every accessor tolerates the file being
/// absent: branding is not worth failing a launch over, and callers fall back to drawing the mark
/// themselves.
/// </remarks>
public static class Branding
{
    private static readonly object Gate = new();
    private static Image? _logo;
    private static Image? _emblem;
    private static Icon? _icon;
    private static bool _logoTried;
    private static bool _emblemTried;
    private static bool _iconTried;

    /// <summary>The full lock-up: glyph, wordmark and tagline. Square.</summary>
    public static Image? Logo => Cached(ref _logo, ref _logoTried, "Genesis_Studio_Logo.png");

    /// <summary>The glyph alone, for places too small for the wordmark to survive.</summary>
    public static Image? Emblem => Cached(ref _emblem, ref _emblemTried, "Genesis_Studio_Emblem.png");

    /// <summary>The window icon.</summary>
    /// <remarks>
    /// Read from the same .ico the executable is stamped with, so a window's icon and its taskbar
    /// button cannot drift apart.
    /// </remarks>
    public static Icon? WindowIcon
    {
        get
        {
            lock (Gate)
            {
                if (_iconTried)
                {
                    return _icon;
                }

                _iconTried = true;
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Genesis.ico");
                try
                {
                    if (File.Exists(path))
                    {
                        _icon = new Icon(path);
                    }
                }
                catch (IOException)
                {
                    // Branding is optional; callers retain the form's embedded/default icon.
                }
                catch (ArgumentException)
                {
                    // A malformed replacement ICO must not prevent Studio from opening.
                }

                return _icon;
            }
        }
    }

    private static Image? Cached(ref Image? slot, ref bool tried, string fileName)
    {
        lock (Gate)
        {
            if (tried)
            {
                return slot;
            }

            tried = true;
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            try
            {
                if (File.Exists(path))
                {
                    // Through memory rather than Image.FromFile, which holds the file open for the
                    // life of the image and would block anyone swapping the artwork.
                    slot = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
                }
            }
            catch (IOException)
            {
                // Branding is optional; callers draw their established fallback mark.
            }
            catch (ArgumentException)
            {
                // A malformed replacement image must not prevent Studio from opening.
            }

            return slot;
        }
    }
}
