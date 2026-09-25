using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Controls;

/// <summary>The emblem and two-line Genesis name used by the Project Hub navigation rail.</summary>
/// <remarks>
/// Keeping the lock-up in one layout owner is important. The old hub placed the two labels at
/// unrelated pixel coordinates: their left edges differed, and the second line began only one or
/// two pixels below the first label's measured bounds. Font fallback, interface scaling or DPI
/// rounding could therefore make the subtitle look joined to (or clipped by) the title.
/// </remarks>
internal sealed class NavigationBrandControl : Control
{
    private const int LogicalLogoSize = 50;
    private readonly GenesisLogoControl _logo;
    private readonly Label _title;
    private readonly Label _edition;
    private readonly string _density;
    private Font? _ownedTitleFont;
    private bool _arranging;

    public NavigationBrandControl()
        : this(ThemeService.Density)
    {
    }

    /// <summary>Explicit density seam used by the headless layout regression.</summary>
    internal NavigationBrandControl(string density)
    {
        _density = string.IsNullOrWhiteSpace(density) ? "Comfortable" : density;
        SetStyle(ControlStyles.ContainerControl | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;

        _logo = new GenesisLogoControl
        {
            Size = new Size(LogicalLogoSize, LogicalLogoSize),
        };
        _ownedTitleFont = new Font(
            "Segoe UI Variable Display",
            12.5f * ThemeService.InterfaceScale,
            FontStyle.Bold,
            GraphicsUnit.Point);
        _title = new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Font = _ownedTitleFont,
            ForeColor = ThemeService.Palette.Text,
            Text = "Genesis Studio",
            UseMnemonic = false,
        };
        _edition = new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Font = ThemeService.InterfaceFont,
            ForeColor = ThemeService.Palette.TextMuted,
            Text = FormatEditionLine(),
            UseMnemonic = false,
        };

        Controls.Add(_logo);
        Controls.Add(_title);
        Controls.Add(_edition);

        // PreferredSize is already expressed in device pixels because these point-sized fonts know
        // the current monitor DPI even before the control is parented. Convert that measurement
        // back to the 96-DPI design coordinate system; the containing DpiAwareForm will scale the
        // complete lock-up once. Keeping the device-pixel value here would scale it a second time
        // and make the brand overlap the navigation items below it on a 200% display.
        NavigationBrandMetrics metrics = NavigationBrandMetrics.For(_density, DeviceDpi);
        int textHeight = _title.PreferredSize.Height + metrics.TextLineGap + _edition.PreferredSize.Height;
        int logicalTextHeight = (int)Math.Ceiling(textHeight * (96f / Math.Max(96, DeviceDpi)));
        int logicalTextWidth = (int)Math.Ceiling(
            Math.Max(_title.PreferredSize.Width, _edition.PreferredSize.Width) * (96f / Math.Max(96, DeviceDpi)));
        Size = new Size(
            Math.Max(182, LogicalLogoSize + metrics.LogoTextGap + logicalTextWidth),
            Math.Max(LogicalLogoSize, logicalTextHeight));
    }

    private static string FormatEditionLine()
    {
        Version? version = typeof(NavigationBrandControl).Assembly.GetName().Version;
        return version is null
            ? "Studio"
            : $"Version {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    internal GenesisLogoControl Logo => _logo;

    internal Label Title => _title;

    internal Label Edition => _edition;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_arranging || IsDisposed)
        {
            return;
        }

        _arranging = true;
        try
        {
            NavigationBrandMetrics metrics = NavigationBrandMetrics.For(_density, DeviceDpi);
            int textLeft = _logo.Right + metrics.LogoTextGap;
            int textHeight = _title.Height + metrics.TextLineGap + _edition.Height;
            int textTop = Math.Max(0, (ClientSize.Height - textHeight) / 2);

            _logo.Location = new Point(0, Math.Max(0, (ClientSize.Height - _logo.Height) / 2));
            _title.Location = new Point(textLeft, textTop);
            _edition.Location = new Point(textLeft, _title.Bottom + metrics.TextLineGap);
        }
        finally
        {
            _arranging = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // This font belongs only to this lock-up; the edition uses ThemeService's shared font.
            _ownedTitleFont?.Dispose();
            _ownedTitleFont = null;
        }

        base.Dispose(disposing);
    }
}

/// <summary>DPI-scaled whitespace for each interface density.</summary>
internal readonly record struct NavigationBrandMetrics(int LogoTextGap, int TextLineGap)
{
    public static NavigationBrandMetrics For(string density, int dpi)
    {
        (int logoGap, int lineGap) = density switch
        {
            "Compact" => (8, 3),
            "Spacious" => (12, 7),
            _ => (10, 5),
        };

        int safeDpi = dpi > 0 ? dpi : 96;
        return new NavigationBrandMetrics(
            Scale(logoGap, safeDpi),
            Scale(lineGap, safeDpi));
    }

    private static int Scale(int logicalPixels, int dpi) =>
        Math.Max(1, (int)Math.Round(logicalPixels * (dpi / 96f)));
}
