using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Controls;

/// <summary>Draws the Genesis mark at whatever size it is given.</summary>
/// <remarks>
/// Prefers the real artwork and falls back to drawing the mark in GDI+. The fallback is not
/// decoration: the artwork is loaded from a file beside the executable, and a splash screen with a
/// blank hole where the logo should be is a worse first impression than a simple drawn glyph.
/// </remarks>
public sealed class GenesisLogoControl : Control
{
    public GenesisLogoControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(76, 76);
    }

    /// <summary>Whether to include the wordmark, or draw the glyph alone.</summary>
    /// <remarks>
    /// Off by default because most placements are small, and the tagline in the full lock-up is
    /// unreadable below roughly 200px — at which point it is just noise around the glyph.
    /// </remarks>
    [System.ComponentModel.DefaultValue(false)]
    public bool ShowWordmark { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        Image? artwork = ShowWordmark ? Branding.Logo : Branding.Emblem;
        if (artwork is not null)
        {
            // Square and centred: the source art is square, so fitting to the shorter side keeps it
            // from stretching in a control that is not.
            int side = Math.Min(Width, Height);
            e.Graphics.DrawImage(artwork, new Rectangle((Width - side) / 2, (Height - side) / 2, side, side));
            return;
        }

        DrawFallbackMark(e.Graphics);
    }

    /// <summary>The mark drawn from scratch, for when the artwork file is missing.</summary>
    private void DrawFallbackMark(Graphics graphics)
    {
        ThemePalette palette = ThemeService.Palette;
        RectangleF bounds = new(4, 4, Width - 8, Height - 8);
        PointF center = new(bounds.Left + (bounds.Width / 2f), bounds.Top + (bounds.Height / 2f));

        PointF[] outer =
        [
            new(center.X, bounds.Top),
            new(bounds.Right, bounds.Top + (bounds.Height * 0.25f)),
            new(bounds.Right, bounds.Bottom - (bounds.Height * 0.25f)),
            new(center.X, bounds.Bottom),
            new(bounds.Left, bounds.Bottom - (bounds.Height * 0.25f)),
            new(bounds.Left, bounds.Top + (bounds.Height * 0.25f)),
        ];

        using SolidBrush outerBrush = new(palette.Accent);
        graphics.FillPolygon(outerBrush, outer);

        float inset = bounds.Width * 0.22f;
        RectangleF innerBounds = RectangleF.Inflate(bounds, -inset, -inset);
        PointF[] inner =
        [
            new(center.X, innerBounds.Top),
            new(innerBounds.Right, innerBounds.Top + (innerBounds.Height * 0.25f)),
            new(innerBounds.Right, innerBounds.Bottom - (innerBounds.Height * 0.25f)),
            new(center.X, innerBounds.Bottom),
            new(innerBounds.Left, innerBounds.Bottom - (innerBounds.Height * 0.25f)),
            new(innerBounds.Left, innerBounds.Top + (innerBounds.Height * 0.25f)),
        ];

        using SolidBrush cutout = new(Parent?.BackColor ?? palette.Canvas);
        graphics.FillPolygon(cutout, inner);

        using Pen slash = new(palette.AccentHover, Math.Max(3f, Width * 0.07f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawLine(
            slash,
            bounds.Left + (bounds.Width * 0.24f),
            bounds.Bottom - (bounds.Height * 0.26f),
            bounds.Right - (bounds.Width * 0.2f),
            bounds.Top + (bounds.Height * 0.2f));
    }
}
