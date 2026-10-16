using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Settings;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Controls;

/// <summary>What a project looks like in the Hub: its newest game screenshot and whether it is 2D or 3D.</summary>
internal static partial class ProjectPreview
{
    /// <summary>
    /// The newest picture the game saved (F12, <c>ScreenshotSave</c>, autoshots) scaled to
    /// <paramref name="maxWidth"/>, and "2D", "3D" or "" from the start room. Never throws: a
    /// project without pictures gets a generated cover instead.
    /// </summary>
    public static (Bitmap? Image, string Dimension) Load(string projectFile, int maxWidth)
    {
        string root = Path.GetDirectoryName(projectFile) ?? string.Empty;
        return (LoadScreenshot(root, maxWidth), ReadDimension(projectFile, root));
    }

    private static Bitmap? LoadScreenshot(string root, int maxWidth)
    {
        try
        {
            FileInfo? newest = null;
            foreach (string folder in new[] { Path.Combine(root, "Debug", "Images"), Path.Combine(root, "Screenshots") })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*.png"))
                    if (newest is null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = file;
            }
            if (newest is null) return null;
            using FileStream stream = newest.OpenRead();
            using Image source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            float scale = Math.Min(1f, maxWidth / (float)source.Width);
            Bitmap thumbnail = new(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            using Graphics g = Graphics.FromImage(thumbnail);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, thumbnail.Width, thumbnail.Height);
            return thumbnail;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException or ExternalException)
        {
            return null;
        }
    }

    private static string ReadDimension(string projectFile, string root)
    {
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(projectFile));
            string? startRoom = null;
            foreach (JsonProperty property in manifest.RootElement.EnumerateObject())
                if (string.Equals(property.Name, "startRoom", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    startRoom = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(startRoom)) return string.Empty;
            // Projects name their start room ("Tumble Isles"); older ones give its path.
            string room = Path.Combine(root, startRoom.Replace('/', Path.DirectorySeparatorChar));
            string rooms = Path.Combine(root, "Assets", "Rooms");
            if (!File.Exists(room) && Directory.Exists(rooms))
                room = Directory.EnumerateFiles(rooms, startRoom + ".room.json", SearchOption.AllDirectories).FirstOrDefault() ?? room;
            if (!File.Exists(room)) return string.Empty;
            // The room's dimension sits near the top of its file; the rest can be megabytes of nodes.
            char[] head = new char[4096];
            int read;
            using (StreamReader reader = new(room)) read = reader.ReadBlock(head, 0, head.Length);
            Match match = DimensionField().Match(new string(head, 0, read));
            if (!match.Success) return "2D";
            string value = match.Groups[1].Value.Trim('"');
            return value is "1" or "ThreeD" ? "3D" : "2D";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex("\"dimension\"\\s*:\\s*(\"?\\w+\"?)", RegexOptions.IgnoreCase)]
    private static partial Regex DimensionField();

    /// <summary>"just now", "5 min ago", "2 h ago", "yesterday", "3 days ago", or the date.</summary>
    public static string Ago(DateTime utc)
    {
        TimeSpan age = DateTime.UtcNow - utc;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
        if (age.TotalDays < 2) return "yesterday";
        if (age.TotalDays < 7) return $"{(int)age.TotalDays} days ago";
        return utc.ToLocalTime().ToString("d MMM yyyy");
    }

    /// <summary>A cover for projects with no screenshot: two theme-tinted colours from the name, and its initials.</summary>
    public static void PaintCover(Graphics g, Rectangle area, string name)
    {
        ThemePalette palette = ThemeService.Palette;
        int hash = 17;
        foreach (char c in name) hash = hash * 31 + c;
        float hue = Math.Abs(hash % 360);
        Color tint = FromHue(hue);
        Color first = UiDrawing.Blend(tint, palette.Accent, 0.45f);
        Color second = UiDrawing.Blend(FromHue((hue + 50) % 360), palette.Canvas, 0.55f);
        using (LinearGradientBrush wash = new(area, first, second, 35f)) g.FillRectangle(wash, area);
        using (Pen lines = new(Color.FromArgb(22, Color.White), 1f))
            for (int x = area.Left - area.Height; x < area.Right; x += 14)
                g.DrawLine(lines, x, area.Bottom, x + area.Height, area.Top);
        string initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => char.ToUpperInvariant(word[0])));
        float size = Math.Max(10f, area.Height * 0.32f / (g.DpiY / 72f));
        using Font font = new("Segoe UI Variable Display", size, FontStyle.Bold);
        TextRenderer.DrawText(g, initials, font, area, Color.FromArgb(220, Color.White),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static Color FromHue(float hue)
    {
        float h = hue / 60f, x = 1 - Math.Abs(h % 2 - 1);
        (float r, float g, float b) = (int)h switch
        {
            0 => (1f, x, 0f), 1 => (x, 1f, 0f), 2 => (0f, 1f, x), 3 => (0f, x, 1f), 4 => (x, 0f, 1f), _ => (1f, 0f, x),
        };
        return Color.FromArgb((int)(60 + r * 150), (int)(60 + g * 150), (int)(60 + b * 150));
    }

    /// <summary>
    /// Draws <paramref name="image"/> to fill <paramref name="area"/> (cropping, not stretching),
    /// zoomed by <paramref name="zoom"/> and faded to <paramref name="alpha"/>.
    /// </summary>
    public static void PaintFill(Graphics g, Image image, Rectangle area, float zoom, float alpha)
    {
        float scale = Math.Max(area.Width / (float)image.Width, area.Height / (float)image.Height) * zoom;
        float w = image.Width * scale, h = image.Height * scale;
        RectangleF target = new(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
        if (alpha >= 0.999f)
        {
            g.DrawImage(image, target);
            return;
        }
        using ImageAttributes fade = new();
        fade.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0f, 1f) });
        g.DrawImage(image, Rectangle.Round(target), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, fade);
    }

    /// <summary>A small rounded label drawn over a picture, such as "3D".</summary>
    public static void PaintChip(Graphics g, string text, Point at, Font font)
    {
        Size size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding);
        Rectangle chip = new(at.X, at.Y, size.Width + 14, size.Height + 6);
        using GraphicsPath path = UiDrawing.RoundedRectangle(chip, chip.Height / 2);
        using SolidBrush back = new(Color.FromArgb(150, 10, 12, 20));
        g.FillPath(back, path);
        TextRenderer.DrawText(g, text, font, chip, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// Shared by the recent tiles and the Continue card: loads the preview on a worker thread, fades it
/// in when it arrives and zooms it slightly while the surface is hovered.
/// </summary>
internal abstract class PreviewSurfacePanel(string projectName) : RoundedSurfacePanel
{
    private readonly string _projectName = projectName;
    private Bitmap? _preview;
    private float _previewFade;
    private string _dimension = string.Empty;

    protected string Dimension => _dimension;

    protected void BeginLoadPreview(string projectFile, int maxWidth)
    {
        SynchronizationContext? context = SynchronizationContext.Current;
        if (context is null)
        {
            Apply(ProjectPreview.Load(projectFile, maxWidth), instant: true);
            return;
        }
        Task.Run(() => ProjectPreview.Load(projectFile, maxWidth)).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) context.Post(_ => Apply(task.Result, instant: false), null);
        }, TaskScheduler.Default);
    }

    private void Apply((Bitmap? Image, string Dimension) loaded, bool instant)
    {
        if (IsDisposed)
        {
            loaded.Image?.Dispose();
            return;
        }
        _preview = loaded.Image;
        _dimension = loaded.Dimension;
        OnPreviewLoaded();
        if (instant) { _previewFade = 1f; Invalidate(); }
        else UiMotion.Animate(this, "preview", 360, t => { _previewFade = t; Invalidate(); });
    }

    protected virtual void OnPreviewLoaded() { }

    protected override void OnHoverChanged() => Invalidate();

    /// <summary>Paints the picture (or the generated cover) into <paramref name="area"/>, clipped to <paramref name="clip"/>.</summary>
    protected void PaintPreview(Graphics g, Rectangle area, GraphicsPath clip)
    {
        GraphicsState state = g.Save();
        g.SetClip(clip, CombineMode.Intersect);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float zoom = 1f + 0.05f * HoverAmount;
        if (_preview is null || _previewFade < 1f) ProjectPreview.PaintCover(g, area, _projectName);
        if (_preview is not null) ProjectPreview.PaintFill(g, _preview, area, zoom, _previewFade * RevealAmount);
        // A scrim at the foot of the picture keeps chips and overlaid text readable.
        Rectangle foot = new(area.Left, area.Top + area.Height * 3 / 5, area.Width, area.Height * 2 / 5 + 1);
        using (LinearGradientBrush scrim = new(foot, Color.FromArgb(0, Color.Black), Color.FromArgb(90, Color.Black), LinearGradientMode.Vertical))
            g.FillRectangle(scrim, foot);
        g.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preview?.Dispose();
            _preview = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>One recent project: its picture, 2D/3D chip, name and when it was last opened.</summary>
internal sealed class RecentProjectTile : PreviewSurfacePanel
{
    private readonly RecentProject _recent;
    private readonly bool _exists;
    private int _thumbnailHeight = 140;

    public RecentProjectTile(RecentProject recent, bool exists) : base(recent.Name)
    {
        _recent = recent;
        _exists = exists;
        CornerRadius = 12;
        Raised = true;
        Interactive = exists;
        Cursor = exists ? Cursors.Hand : Cursors.Default;
        Tag = recent;
        Width = 280;
        Height = 230;
        Margin = new Padding(0, 0, 16, 16);
        if (exists) BeginLoadPreview(recent.ProjectFile, 640);
    }

    internal int ThumbnailHeight => _thumbnailHeight;

    internal void SetThumbnailHeight(int height)
    {
        _thumbnailHeight = Math.Max(40, height);
        foreach (AnimatedIconPlayer icon in Controls.OfType<AnimatedIconPlayer>()) icon.Height = _thumbnailHeight;
        Invalidate();
    }

    protected override void OnPreviewLoaded()
    {
        // The chip says what kind of game it is; it is drawn over the picture.
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle body = BodyBounds;
        Rectangle picture = new(body.Left + 1, body.Top + 1, body.Width - 2, Math.Min(_thumbnailHeight, body.Height - 2));
        using GraphicsPath clip = UiDrawing.RoundedRectangle(new Rectangle(body.Left + 1, body.Top + 1, body.Width - 2, body.Height - 2), CornerRadius - 1);
        if (!Controls.OfType<AnimatedIconPlayer>().Any()) PaintPreview(g, picture, clip);
        using Font chipFont = new(ThemeService.InterfaceFont.FontFamily, 7.5f, FontStyle.Bold);
        int pad = DpiLayout.Scale(this, 10);
        if (Dimension.Length > 0) ProjectPreview.PaintChip(g, Dimension, new Point(picture.Left + pad, picture.Top + pad), chipFont);
        if (_recent.IsPinned)
        {
            int chipWidth = TextRenderer.MeasureText(g, "★ Pinned", chipFont, Size.Empty, TextFormatFlags.NoPadding).Width + 14;
            ProjectPreview.PaintChip(g, "★ Pinned", new Point(picture.Right - pad - chipWidth, picture.Top + pad), chipFont);
        }
        if (!_exists)
        {
            using SolidBrush veil = new(Color.FromArgb(150, ThemeService.Palette.Canvas));
            g.FillRectangle(veil, picture);
            TextRenderer.DrawText(g, "⊘  Project not found", Font, picture, ThemeService.Palette.Error,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

/// <summary>A compact "start something" row: a glyph in a tinted circle, a title and a line under it.</summary>
internal sealed class LauncherAction : RoundedSurfacePanel
{
    private readonly string _glyph;
    private readonly string _title;
    private readonly string _detail;

    public LauncherAction(string glyph, string title, string detail, Action onClick)
    {
        _glyph = glyph;
        _title = title;
        _detail = detail;
        Interactive = true;
        CornerRadius = 10;
        Cursor = Cursors.Hand;
        AccessibleName = title;
        AccessibleRole = AccessibleRole.PushButton;
        Click += (_, _) => onClick();
    }

    protected override void OnHoverChanged() => Invalidate();

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ThemePalette palette = ThemeService.Palette;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle body = BodyBounds;
        int circle = Math.Max(20, body.Height - DpiLayout.Scale(this, 16));
        Rectangle dot = new(body.Left + DpiLayout.Scale(this, 8), body.Top + (body.Height - circle) / 2, circle, circle);
        using (SolidBrush tint = new(UiMotion.Mix(UiDrawing.Blend(palette.Accent, palette.SurfaceRaised, 0.7f), palette.Accent, HoverAmount * 0.6f)))
            g.FillEllipse(tint, dot);
        TextRenderer.DrawText(g, _glyph, Font, dot, palette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        int textLeft = dot.Right + DpiLayout.Scale(this, 10);
        Rectangle text = new(textLeft, body.Top, Math.Max(1, body.Right - textLeft - DpiLayout.Scale(this, 8)), body.Height);
        using Font bold = new(Font, FontStyle.Bold);
        int line = Font.Height;
        int top = body.Top + (body.Height - line * 2) / 2;
        TextRenderer.DrawText(g, _title, bold, new Rectangle(text.Left, top, text.Width, line), palette.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _detail, Font, new Rectangle(text.Left, top + line, text.Width, line), palette.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// The Hub's first card: the project you worked on last, large, with Open and Show folder, beside
/// shortcuts to start something new.
/// </summary>
internal sealed class ContinueProjectCard : PreviewSurfacePanel
{
    private readonly Label _eyebrow;
    private readonly Label _name;
    private readonly Label _meta;
    private readonly ModernButton _open;
    private readonly ModernButton _folder;
    private readonly Label _startHeading;
    private readonly List<LauncherAction> _actions = [];
    private readonly RecentProject _recent;
    private Rectangle _picture;

    public ContinueProjectCard(RecentProject recent, Action open, IEnumerable<LauncherAction> actions) : base(recent.Name)
    {
        _recent = recent;
        Raised = true;
        Interactive = true;
        CornerRadius = 16;
        Tag = "continue";
        Margin = new Padding(0, 0, 0, 22);
        ThemePalette palette = ThemeService.Palette;

        _eyebrow = new Label
        {
            AutoSize = false, BackColor = Color.Transparent, ForeColor = palette.Accent,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 8f, FontStyle.Bold), Text = "CONTINUE WHERE YOU LEFT OFF",
        };
        _name = new Label
        {
            AutoSize = false, AutoEllipsis = true, BackColor = Color.Transparent, ForeColor = palette.Text,
            Font = new Font("Segoe UI Variable Display", 22f, FontStyle.Bold), Text = recent.Name, UseMnemonic = false,
        };
        _meta = new Label
        {
            AutoSize = false, AutoEllipsis = true, BackColor = Color.Transparent, ForeColor = palette.TextMuted,
            Text = "Opened " + ProjectPreview.Ago(recent.LastOpenedUtc), UseMnemonic = false,
        };
        _open = new ModernButton { Accent = true, Glyph = "▶", Text = "Open project" };
        _open.Click += (_, _) => open();
        _folder = new ModernButton { Glyph = "↗", Text = "Show folder" };
        _folder.Click += (_, _) => ShowFolder();
        _startHeading = new Label
        {
            AutoSize = false, BackColor = Color.Transparent, ForeColor = palette.Text,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold), Text = "Start something new",
        };
        Controls.AddRange([_eyebrow, _name, _meta, _open, _folder, _startHeading]);
        foreach (LauncherAction action in actions)
        {
            _actions.Add(action);
            Controls.Add(action);
        }
        ToolTip tip = new();
        tip.SetToolTip(this, recent.ProjectFile);
        Disposed += (_, _) => tip.Dispose();
        Click += (_, _) => open();
        BeginLoadPreview(recent.ProjectFile, 1280);
    }

    protected override void OnPreviewLoaded()
    {
        if (Dimension.Length > 0)
            _meta.Text = $"{Dimension} game  ·  Opened {ProjectPreview.Ago(_recent.LastOpenedUtc)}";
    }

    private void ShowFolder()
    {
        string? folder = Path.GetDirectoryName(_recent.ProjectFile);
        if (folder is null || !Directory.Exists(folder)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Explorer missing or blocked: nothing to show, and nothing else depends on it.
        }
    }

    /// <summary>Lays the card out for <paramref name="width"/>: picture beside the details, or above them when narrow.</summary>
    internal void ApplyLayout(int width)
    {
        int S(int value) => DpiLayout.Scale(this, value);
        Width = width;
        int pad = S(18);
        bool narrow = width < S(780);
        int pictureWidth = narrow ? width - pad * 2 : (int)(width * 0.5f);
        int pictureHeight = Math.Min(narrow ? S(240) : int.MaxValue, pictureWidth * 9 / 16);
        _picture = new Rectangle(pad, pad, pictureWidth, pictureHeight);
        int x = narrow ? pad : pad + pictureWidth + S(24);
        int y = narrow ? pad + pictureHeight + S(18) : pad + S(2);
        int infoWidth = Math.Max(S(160), width - x - pad);

        _eyebrow.Bounds = new Rectangle(x, y, infoWidth, _eyebrow.Font.Height + 2);
        y = _eyebrow.Bottom + S(4);
        _name.Bounds = new Rectangle(x, y, infoWidth, _name.Font.Height + 6);
        y = _name.Bottom + S(2);
        _meta.Bounds = new Rectangle(x, y, infoWidth, _meta.Font.Height + 4);
        y = _meta.Bottom + S(14);
        Forms.ShellDialogLayout.FitButton(_open);
        Forms.ShellDialogLayout.FitButton(_folder);
        _open.Location = new Point(x, y);
        _folder.Location = new Point(_open.Right + S(8), y);
        y = _open.Bottom + S(22);
        _startHeading.Bounds = new Rectangle(x, y, infoWidth, _startHeading.Font.Height + 4);
        y = _startHeading.Bottom + S(8);
        int columns = infoWidth >= S(460) ? 2 : 1;
        int gap = S(8);
        int actionWidth = (infoWidth - gap * (columns - 1)) / columns;
        int actionHeight = Math.Max(S(48), Font.Height * 2 + S(16));
        for (int i = 0; i < _actions.Count; i++)
        {
            int column = i % columns, row = i / columns;
            _actions[i].Bounds = new Rectangle(x + column * (actionWidth + gap), y + row * (actionHeight + gap), actionWidth, actionHeight);
        }
        int infoBottom = _actions.Count == 0 ? _startHeading.Bottom : _actions.Max(action => action.Bottom);
        Height = Math.Max(_picture.Bottom, infoBottom) + pad + S(3);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_picture.Width <= 0) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath clip = UiDrawing.RoundedRectangle(_picture, 12);
        PaintPreview(g, _picture, clip);
        if (Dimension.Length > 0)
        {
            using Font chipFont = new(ThemeService.InterfaceFont.FontFamily, 8f, FontStyle.Bold);
            int pad = DpiLayout.Scale(this, 12);
            ProjectPreview.PaintChip(g, Dimension, new Point(_picture.Left + pad, _picture.Top + pad), chipFont);
        }
    }
}

/// <summary>
/// The Start page's project banner: the project's newest screenshot on the right, fading into the
/// card behind the project's name.
/// </summary>
internal sealed class ProjectBannerPanel : PreviewSurfacePanel
{
    public ProjectBannerPanel(string projectName, string projectFile) : base(projectName)
    {
        Raised = true;
        CornerRadius = 14;
        BeginLoadPreview(projectFile, 960);
    }

    /// <summary>How wide the picture is drawn; text beside it stops short of it.</summary>
    internal int PictureWidth => Math.Max(0, Math.Min(Width * 45 / 100, (Height - 4) * 16 / 9));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int width = PictureWidth;
        if (width < 60) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle body = BodyBounds;
        Rectangle picture = new(body.Right - width, body.Top + 1, width - 1, body.Height - 2);
        using GraphicsPath clip = UiDrawing.RoundedRectangle(new Rectangle(body.Left + 1, body.Top + 1, body.Width - 2, body.Height - 2), CornerRadius - 1);
        PaintPreview(g, picture, clip);
        Color card = UiMotion.Mix(ThemeService.Palette.Canvas, ThemeService.Palette.SurfaceRaised, RevealAmount);
        Rectangle fade = new(picture.Left, picture.Top, picture.Width * 2 / 5, picture.Height);
        using (LinearGradientBrush mask = new(new Rectangle(fade.Left - 1, fade.Top, fade.Width + 2, fade.Height), card, Color.FromArgb(0, card), LinearGradientMode.Horizontal))
            g.FillRectangle(mask, fade);
        if (Dimension.Length > 0)
        {
            using Font chipFont = new(ThemeService.InterfaceFont.FontFamily, 8f, FontStyle.Bold);
            int pad = DpiLayout.Scale(this, 12);
            int chipWidth = TextRenderer.MeasureText(g, Dimension, chipFont, Size.Empty, TextFormatFlags.NoPadding).Width + 14;
            ProjectPreview.PaintChip(g, Dimension, new Point(picture.Right - pad - chipWidth, picture.Top + pad), chipFont);
        }
    }
}
