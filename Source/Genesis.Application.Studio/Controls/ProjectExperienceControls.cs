using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Controls;

/// <summary>Theme-aware rounded surface shared by the Project Hub and in-project Start page.</summary>
internal class RoundedSurfacePanel : Panel
{
    private bool _hovered;

    public RoundedSurfacePanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        BackColor = Color.Transparent;
        Tag = "transparent";
    }

    [DefaultValue(false)]
    public bool Interactive { get; set; }

    [DefaultValue(false)]
    public bool Raised { get; set; }

    [DefaultValue(false)]
    public bool Selected { get; set; }

    [DefaultValue(12)]
    public int CornerRadius { get; set; } = 12;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        if (Interactive) Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
        if (Interactive) Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        ThemePalette palette = ThemeService.Palette;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? palette.Canvas);

        Rectangle bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        if (bounds.Width <= 2 || bounds.Height <= 2) return;

        Color fill = Interactive && _hovered
            ? palette.SurfaceHover
            : Raised ? palette.SurfaceRaised : palette.Surface;
        using GraphicsPath path = UiDrawing.RoundedRectangle(bounds, CornerRadius);
        using SolidBrush brush = new(fill);
        e.Graphics.FillPath(brush, path);
        using Pen border = new(Selected ? palette.Accent : palette.Border, Selected ? 2f : 1f);
        e.Graphics.DrawPath(border, path);
    }
}

/// <summary>A compact illustrative preview for each real project template.</summary>
internal sealed class ProjectTemplateArtworkControl : Control
{
    public ProjectTemplateArtworkControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ProjectTemplateArtwork Artwork { get; set; } = ProjectTemplateArtwork.World;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 20 || Height < 20) return;

        ThemePalette palette = ThemeService.Palette;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        using GraphicsPath clip = UiDrawing.RoundedRectangle(bounds, 10);
        GraphicsState state = e.Graphics.Save();
        e.Graphics.SetClip(clip);

        Color skyTop = UiDrawing.Blend(palette.Accent, palette.Canvas, palette.IsDark ? 0.36f : 0.16f);
        Color skyBottom = UiDrawing.Blend(palette.AccentHover, palette.Surface, palette.IsDark ? 0.18f : 0.09f);
        using LinearGradientBrush sky = new(bounds, skyTop, skyBottom, LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(sky, bounds);

        switch (Artwork)
        {
            case ProjectTemplateArtwork.Blank:
                DrawBlank(e.Graphics, bounds, palette);
                break;
            case ProjectTemplateArtwork.Platformer:
                DrawPlatformer(e.Graphics, bounds, palette);
                break;
            case ProjectTemplateArtwork.DungeonCrawler:
                DrawDungeonCrawler(e.Graphics, bounds, palette);
                break;
            case ProjectTemplateArtwork.NatureWalk:
                DrawNature(e.Graphics, bounds, palette);
                break;
            case ProjectTemplateArtwork.Voxel:
                DrawVoxel(e.Graphics, bounds, palette);
                break;
            default:
                DrawWorld(e.Graphics, bounds, palette);
                break;
        }

        e.Graphics.Restore(state);
        using Pen border = new(UiDrawing.WithAlpha(palette.Text, 42), 1f);
        e.Graphics.DrawPath(border, clip);
    }

    private static void DrawBlank(Graphics graphics, Rectangle b, ThemePalette p)
    {
        int size = Math.Max(40, Math.Min(b.Width, b.Height) / 2);
        Rectangle page = new(
            b.Left + (b.Width - size) / 2,
            b.Top + (b.Height - size) / 2,
            size,
            size);
        using SolidBrush pageFill = new(UiDrawing.WithAlpha(p.SurfaceRaised, 225));
        using Pen pageBorder = new(UiDrawing.WithAlpha(p.Text, 55), 1.5f);
        graphics.FillRectangle(pageFill, page);
        graphics.DrawRectangle(pageBorder, page);
        using Pen plus = new(p.Accent, 3f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        int cx = page.Left + page.Width / 2;
        int cy = page.Top + page.Height / 2;
        graphics.DrawLine(plus, cx - 11, cy, cx + 11, cy);
        graphics.DrawLine(plus, cx, cy - 11, cx, cy + 11);
    }

    private static void DrawPlatformer(Graphics graphics, Rectangle b, ThemePalette p)
    {
        int ground = b.Bottom - Math.Max(24, b.Height / 4);
        using SolidBrush grass = new(UiDrawing.Blend(p.Success, p.Accent, 0.28f));
        using SolidBrush soil = new(UiDrawing.Blend(p.Warning, p.Canvas, 0.46f));
        graphics.FillRectangle(grass, b.Left, ground, b.Width, 8);
        graphics.FillRectangle(soil, b.Left, ground + 8, b.Width, b.Bottom - ground - 8);

        int tile = Math.Max(18, b.Width / 12);
        using Pen seam = new(UiDrawing.WithAlpha(p.Canvas, 90));
        for (int x = b.Left; x < b.Right; x += tile)
        {
            graphics.DrawLine(seam, x, ground + 8, x, b.Bottom);
        }

        int playerX = b.Left + b.Width / 4;
        using SolidBrush player = new(p.Accent);
        using SolidBrush playerDark = new(UiDrawing.Blend(p.Accent, p.Canvas, 0.5f));
        graphics.FillRectangle(player, playerX, ground - 28, 18, 19);
        graphics.FillRectangle(playerDark, playerX + 2, ground - 9, 6, 9);
        graphics.FillRectangle(playerDark, playerX + 11, ground - 9, 6, 9);

        using SolidBrush coin = new(p.Warning);
        for (int i = 0; i < 3; i++)
        {
            int x = b.Left + (b.Width * (55 + i * 13) / 100);
            graphics.FillEllipse(coin, x, ground - 40 - (i % 2) * 12, 12, 12);
        }

        using SolidBrush cloud = new(UiDrawing.WithAlpha(Color.White, 120));
        graphics.FillEllipse(cloud, b.Left + 20, b.Top + 18, 36, 12);
        graphics.FillEllipse(cloud, b.Left + 42, b.Top + 13, 28, 17);
    }

    private static void DrawNature(Graphics graphics, Rectangle b, ThemePalette p)
    {
        Point[] far =
        [
            new(b.Left, b.Bottom - 25),
            new(b.Left + b.Width / 4, b.Top + b.Height / 3),
            new(b.Left + b.Width / 2, b.Bottom - 36),
            new(b.Left + b.Width * 3 / 4, b.Top + b.Height / 4),
            new(b.Right, b.Bottom - 25),
        ];
        using SolidBrush mountain = new(UiDrawing.Blend(p.TextMuted, p.Canvas, 0.46f));
        graphics.FillPolygon(mountain, far);

        using SolidBrush lake = new(UiDrawing.WithAlpha(p.Accent, 165));
        graphics.FillEllipse(lake, b.Left + b.Width / 3, b.Bottom - 34, b.Width / 2, 30);
        using Pen trail = new(UiDrawing.Blend(p.Warning, p.Text, 0.42f), 6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawBezier(
            trail,
            b.Left + 28, b.Bottom,
            b.Left + b.Width / 3, b.Bottom - 30,
            b.Left + b.Width / 2, b.Bottom - 18,
            b.Left + b.Width * 2 / 3, b.Bottom - 52);

        using SolidBrush trunk = new(UiDrawing.Blend(p.Warning, p.Canvas, 0.55f));
        using SolidBrush foliage = new(p.Success);
        for (int i = 0; i < 6; i++)
        {
            int x = b.Left + 16 + i * Math.Max(24, (b.Width - 42) / 6);
            int y = b.Bottom - 25 - (i % 2) * 13;
            graphics.FillRectangle(trunk, x + 5, y - 18, 4, 18);
            graphics.FillPolygon(foliage,
            [
                new Point(x + 7, y - 45),
                new Point(x - 2, y - 17),
                new Point(x + 16, y - 17),
            ]);
        }
    }

    private static void DrawDungeonCrawler(Graphics graphics, Rectangle b, ThemePalette p)
    {
        using SolidBrush dark = new(UiDrawing.Blend(p.Canvas, Color.Black, 0.32f));
        graphics.FillRectangle(dark, b);

        int tile = Math.Max(14, Math.Min(b.Width / 13, b.Height / 7));
        int left = b.Left + (b.Width - tile * 11) / 2;
        int top = b.Top + (b.Height - tile * 6) / 2;
        using Pen mortar = new(UiDrawing.WithAlpha(p.Text, 22), 1f);
        using SolidBrush floor = new(UiDrawing.Blend(p.SurfaceRaised, p.Accent, 0.14f));
        for (int y = 0; y < 6; y++)
        for (int x = 0; x < 11; x++)
        {
            Rectangle cell = new(left + x * tile, top + y * tile, tile, tile);
            graphics.FillRectangle(floor, cell);
            graphics.DrawRectangle(mortar, cell);
        }

        using SolidBrush wall = new(UiDrawing.Blend(p.TextMuted, p.Canvas, 0.48f));
        graphics.FillRectangle(wall, left, top, tile * 11, Math.Max(5, tile / 3));
        graphics.FillRectangle(wall, left, top + tile * 6 - Math.Max(5, tile / 3), tile * 11, Math.Max(5, tile / 3));
        graphics.FillRectangle(wall, left, top, Math.Max(5, tile / 3), tile * 6);
        graphics.FillRectangle(wall, left + tile * 11 - Math.Max(5, tile / 3), top, Math.Max(5, tile / 3), tile * 6);

        using SolidBrush player = new(UiDrawing.Blend(p.Accent, p.Success, 0.38f));
        graphics.FillEllipse(player, left + tile * 3, top + tile * 3, tile, tile);
        using SolidBrush eye = new(UiDrawing.Blend(Color.MediumPurple, p.Accent, 0.28f));
        graphics.FillEllipse(eye, left + tile * 7, top + tile * 2, tile * 2, tile * 2);
        using SolidBrush pupil = new(UiDrawing.Blend(p.Canvas, Color.Black, 0.5f));
        graphics.FillEllipse(pupil, left + tile * 7 + tile / 2, top + tile * 2 + tile / 2, tile, tile);
        using SolidBrush coin = new(p.Warning);
        graphics.FillEllipse(coin, left + tile * 5, top + tile * 4, Math.Max(7, tile / 2), Math.Max(7, tile / 2));
    }

    private static void DrawWorld(Graphics graphics, Rectangle b, ThemePalette p)
    {
        int horizon = b.Top + b.Height / 2;
        using SolidBrush ground = new(UiDrawing.Blend(p.Success, p.Canvas, 0.42f));
        graphics.FillRectangle(ground, b.Left, horizon, b.Width, b.Bottom - horizon);
        using Pen grid = new(UiDrawing.WithAlpha(p.Text, 30));
        for (int i = 0; i < 8; i++)
        {
            int x = b.Left + i * b.Width / 7;
            graphics.DrawLine(grid, b.Left + b.Width / 2, horizon, x, b.Bottom);
        }
        for (int i = 1; i < 5; i++)
        {
            int y = horizon + i * (b.Bottom - horizon) / 5;
            graphics.DrawLine(grid, b.Left, y, b.Right, y);
        }

        using SolidBrush cubeFront = new(p.Accent);
        using SolidBrush cubeSide = new(UiDrawing.Blend(p.Accent, p.Canvas, 0.42f));
        Rectangle cube = new(b.Left + b.Width / 2 - 18, horizon - 24, 36, 36);
        graphics.FillRectangle(cubeFront, cube);
        graphics.FillPolygon(cubeSide,
        [
            new Point(cube.Right, cube.Top),
            new Point(cube.Right + 13, cube.Top - 8),
            new Point(cube.Right + 13, cube.Bottom - 8),
            new Point(cube.Right, cube.Bottom),
        ]);
        using SolidBrush fire = new(p.Warning);
        graphics.FillEllipse(fire, b.Left + b.Width * 3 / 4, horizon + 8, 14, 20);
    }

    private static void DrawVoxel(Graphics graphics, Rectangle b, ThemePalette p)
    {
        int size = Math.Max(14, b.Width / 12);
        using SolidBrush a = new(p.Accent);
        using SolidBrush c = new(p.Success);
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 7; column++)
        {
            int x = b.Left + 18 + column * size;
            int y = b.Bottom - 18 - row * size + ((column % 2) * 4);
            graphics.FillRectangle((row + column) % 3 == 0 ? a : c, x, y - size, size - 2, size - 2);
        }
    }
}

/// <summary>One responsive, fully described project-template card.</summary>
internal sealed class ProjectTemplateCard : RoundedSurfacePanel
{
    private readonly ProjectTemplateArtworkControl _artwork;
    private readonly Label _dimension;
    private readonly Label _name;
    private readonly Label _tagline;
    private readonly Label[] _features;
    private readonly ModernButton _create;

    public ProjectTemplateCard(ProjectTemplate template)
    {
        Template = template ?? throw new ArgumentNullException(nameof(template));
        Interactive = true;
        Raised = true;
        CornerRadius = 14;
        Cursor = Cursors.Hand;
        Height = 410;
        Margin = new Padding(0, 0, 16, 16);

        _artwork = new ProjectTemplateArtworkControl { Artwork = template.Artwork };
        Controls.Add(_artwork);

        _dimension = new Label
        {
            AutoSize = true,
            BackColor = ThemeService.Palette.SurfaceHover,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 7.5f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Accent,
            Padding = new Padding(8, 4, 8, 4),
            Text = template.Dimension == ProjectTemplateDimension.TwoD ? "2D GAME" : "3D GAME",
            UseMnemonic = false,
        };
        Controls.Add(_dimension);

        _name = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Variable Display", 14f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Text = template.Name,
            UseMnemonic = false,
        };
        Controls.Add(_name);

        _tagline = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Text = template.Tagline,
            UseMnemonic = false,
        };
        Controls.Add(_tagline);

        _features = template.Contents.Take(4).Select(line => new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 8.4f),
            ForeColor = ThemeService.Palette.TextMuted,
            Text = "✓  " + line,
            UseMnemonic = false,
        }).ToArray();
        Controls.AddRange(_features);

        _create = new ModernButton
        {
            Accent = true,
            Glyph = "＋",
            Text = "Use this template",
        };
        _create.Click += (_, _) => CreateRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_create);

        void CreateFromCard(object? _, EventArgs __) => CreateRequested?.Invoke(this, EventArgs.Empty);
        Click += CreateFromCard;
        foreach (Control control in Controls.Cast<Control>().Where(control => control != _create))
        {
            control.Cursor = Cursors.Hand;
            control.Click += CreateFromCard;
        }

        Resize += (_, _) => Arrange();
        Arrange();
    }

    public ProjectTemplate Template { get; }

    public event EventHandler? CreateRequested;

    private void Arrange()
    {
        int S(int logical) => DpiLayout.Scale(this, logical);
        int contentWidth = Math.Max(S(120), ClientSize.Width - S(28));
        _artwork.SetBounds(S(14), S(14), contentWidth, S(116));
        _dimension.Location = new Point(S(16), S(143));
        _name.SetBounds(S(16), S(176), contentWidth, S(28));
        _tagline.SetBounds(S(16), S(207), contentWidth, S(38));

        int y = S(253);
        foreach (Label feature in _features)
        {
            feature.SetBounds(S(17), y, contentWidth - S(2), S(22));
            y += S(24);
        }

        _create.SetBounds(S(16), ClientSize.Height - S(52), contentWidth - S(4), S(38));
    }
}

internal static class UiDrawing
{
    public static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        int safeRadius = Math.Max(1, Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2));
        int diameter = safeRadius * 2;
        GraphicsPath path = new();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Blend(Color foreground, Color background, float amount) =>
        Color.FromArgb(
            255,
            (int)(foreground.R * (1f - amount) + background.R * amount),
            (int)(foreground.G * (1f - amount) + background.G * amount),
            (int)(foreground.B * (1f - amount) + background.B * amount));

    public static Color WithAlpha(Color color, int alpha) =>
        Color.FromArgb(Math.Clamp(alpha, 0, 255), color);
}
