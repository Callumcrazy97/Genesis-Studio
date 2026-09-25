using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Genesis.Application.Studio.Theme;

/// <summary>
/// Draws the active theme's picture behind the shell.
/// </summary>
/// <remarks>
/// Every surface samples one shared bitmap sized to its window, and draws the slice that falls
/// under it. That is what makes the menu bar, the dock and the empty document area read as a single
/// picture with the interface floating on top, rather than as four controls that each happen to
/// have a background. Draw each control its own copy of the image and the seams are immediate.
///
/// A scrim goes over the picture in every case. The image is decoration; the panels and text on top
/// of it are the product, and unveiled artwork behind a tree view makes the tree unreadable. The
/// caller chooses how heavy the veil is — light where nothing sits on top, heavy under text.
/// </remarks>
public static class ThemeBackdrop
{
    private static readonly object Gate = new();
    private static Image? _source;
    private static Bitmap? _scaled;
    private static Size _scaledFor;

    /// <summary>The image theme in force, or null when a plain colour theme is selected.</summary>
    public static ThemeImage? Current { get; private set; }

    /// <summary>True when there is a picture to draw.</summary>
    public static bool IsActive => _source is not null;

    /// <summary>Veil for surfaces with nothing on top: the dock's empty space.</summary>
    public const double OpenScrim = 0.55;

    /// <summary>Veil for the menu and tool bands, where text sits directly on the picture.</summary>
    public const double TextScrim = 0.80;

    /// <summary>Veil for the splash and hub, which are mostly picture by design.</summary>
    public const double ShowcaseScrim = 0.42;

    /// <summary>Switches to a theme's picture, or to none.</summary>
    public static void Use(ThemeImage? theme)
    {
        lock (Gate)
        {
            Current = theme;
            _source = theme?.Picture;
            _scaled?.Dispose();
            _scaled = null;
            _scaledFor = Size.Empty;
        }
    }

    /// <summary>
    /// Fills a control's client area with its slice of the backdrop. Returns false if there is none,
    /// leaving the caller to paint its usual colour.
    /// </summary>
    public static bool Paint(Graphics graphics, Control control, double scrim)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(control);

        Form? window = control.FindForm();
        if (window is null || _source is null)
        {
            return false;
        }

        Size windowSize = window.ClientSize;
        if (windowSize.Width <= 0 || windowSize.Height <= 0)
        {
            return false;
        }

        Bitmap? backdrop = ScaledTo(windowSize);
        if (backdrop is null)
        {
            return false;
        }

        // Where this control sits inside its window decides which part of the picture it shows.
        Point origin = control.PointToScreen(Point.Empty);
        Point windowOrigin = window.PointToScreen(Point.Empty);
        int offsetX = origin.X - windowOrigin.X;
        int offsetY = origin.Y - windowOrigin.Y;

        graphics.DrawImage(backdrop, new Rectangle(-offsetX, -offsetY, backdrop.Width, backdrop.Height));

        int alpha = (int)Math.Round(Math.Clamp(scrim, 0d, 1d) * 255d);
        if (alpha > 0)
        {
            using SolidBrush veil = new(Color.FromArgb(alpha, ThemeService.Palette.Canvas));
            graphics.FillRectangle(veil, control.ClientRectangle);
        }

        return true;
    }

    /// <summary>Draws the picture into an arbitrary rectangle, cropped to fill it.</summary>
    /// <remarks>
    /// For the splash and the hub, which are one picture in their own right rather than a slice of a
    /// larger window.
    /// </remarks>
    public static bool PaintFill(Graphics graphics, Rectangle bounds, double scrim)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        if (_source is null || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        Rectangle source = CoverSource(_source.Width, _source.Height, bounds.Width, bounds.Height);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(_source, bounds, source, GraphicsUnit.Pixel);

        int alpha = (int)Math.Round(Math.Clamp(scrim, 0d, 1d) * 255d);
        if (alpha > 0)
        {
            using SolidBrush veil = new(Color.FromArgb(alpha, ThemeService.Palette.Canvas));
            graphics.FillRectangle(veil, bounds);
        }

        return true;
    }

    /// <summary>Discards the cached bitmap. Call when the window it was sized for has gone.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _scaled?.Dispose();
            _scaled = null;
            _scaledFor = Size.Empty;
        }
    }

    /// <summary>The backdrop rendered once at window size, cropped to fill rather than squashed.</summary>
    /// <remarks>
    /// Rescaling a multi-megapixel photograph is far too slow to do inside a paint handler, and the
    /// dock repaints constantly while a panel is being dragged. One bitmap per window size, rebuilt
    /// only when that size changes.
    /// </remarks>
    private static Bitmap? ScaledTo(Size size)
    {
        lock (Gate)
        {
            if (_source is null)
            {
                return null;
            }

            if (_scaled is not null && _scaledFor == size)
            {
                return _scaled;
            }

            Bitmap next = new(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(next))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Rectangle source = CoverSource(_source.Width, _source.Height, size.Width, size.Height);
                graphics.DrawImage(
                    _source, new Rectangle(0, 0, size.Width, size.Height), source, GraphicsUnit.Pixel);
            }

            _scaled?.Dispose();
            _scaled = next;
            _scaledFor = size;
            return _scaled;
        }
    }

    /// <summary>
    /// The centred region of the source with the target's aspect ratio, so filling the target crops
    /// the picture instead of distorting it.
    /// </summary>
    private static Rectangle CoverSource(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        double sourceAspect = (double)sourceWidth / sourceHeight;
        double targetAspect = (double)targetWidth / targetHeight;

        if (sourceAspect > targetAspect)
        {
            // Source is the wider shape: keep its full height and trim the sides.
            int width = (int)Math.Round(sourceHeight * targetAspect);
            return new Rectangle((sourceWidth - width) / 2, 0, width, sourceHeight);
        }

        int height = (int)Math.Round(sourceWidth / targetAspect);
        return new Rectangle(0, (sourceHeight - height) / 2, sourceWidth, height);
    }
}
