using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Controls;

/// <summary>A horizontally scrolling picker whose choices are the theme pictures themselves.</summary>
internal sealed class ThemeImageGallery : FlowLayoutPanel
{
    private readonly List<ThemeImageCard> _cards = [];

    internal ThemeImageGallery()
    {
        AutoScroll = true;
        BackColor = ThemeService.Palette.Canvas;
        FlowDirection = FlowDirection.LeftToRight;
        Padding = new Padding(4);
        WrapContents = false;
    }

    internal ThemeImage? SelectedTheme { get; private set; }

    internal IReadOnlyList<ThemeImage> Images => [.. _cards.Select(card => card.Theme)];

    internal event EventHandler? SelectedThemeChanged;

    internal void SetImages(IEnumerable<ThemeImage> images, string? selectedName = null)
    {
        ArgumentNullException.ThrowIfNull(images);

        SuspendLayout();
        foreach (ThemeImageCard card in _cards)
        {
            card.Dispose();
        }

        _cards.Clear();
        Controls.Clear();
        SelectedTheme = null;

        foreach (ThemeImage image in images)
        {
            ThemeImageCard card = new(image);
            card.CheckedChanged += CardCheckedChanged;
            card.DeleteRequested += CardDeleteRequested;
            _cards.Add(card);
            Controls.Add(card);
        }

        ResumeLayout(performLayout: true);
        SelectTheme(selectedName);
    }

    internal bool SelectTheme(string? name)
    {
        ThemeImageCard? match = _cards.FirstOrDefault(card =>
            string.Equals(card.Theme.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        match.Checked = true;
        ScrollControlIntoView(match);
        return true;
    }

    private void CardCheckedChanged(object? sender, EventArgs e)
    {
        if (sender is not ThemeImageCard { Checked: true } card)
        {
            return;
        }

        SelectedTheme = card.Theme;
        SelectedThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CardDeleteRequested(object? sender, EventArgs e)
    {
        if (sender is not ThemeImageCard card || card.Theme.IsBuiltIn)
        {
            return;
        }

        if (MessageBox.Show(
            this,
            $"Remove the custom theme '{card.Theme.Name}'?",
            "Remove Theme Image",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            if (File.Exists(card.Theme.Path))
            {
                File.Delete(card.Theme.Path);
            }

            ThemeCatalog.RefreshImages();
            string? fallback = SelectedTheme?.Name == card.Theme.Name
                ? ThemeCatalog.Images.FirstOrDefault()?.Name
                : SelectedTheme?.Name;

            SetImages(ThemeCatalog.Images, fallback);
            SelectedThemeChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not remove theme image: {ex.Message}",
                "Remove Theme Image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}

/// <summary>One cached-thumbnail choice in <see cref="ThemeImageGallery"/>.</summary>
internal sealed class ThemeImageCard : RadioButton
{
    private static readonly Size ThumbnailSize = new(154, 82);
    private bool _deleteHovered;
    private bool _deletePressed;

    internal ThemeImageCard(ThemeImage theme)
    {
        Theme = theme ?? throw new ArgumentNullException(nameof(theme));
        AccessibleName = $"Image theme {theme.Name}";
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 8, 0);
        Size = new Size(164, 122);
        TabStop = true;
        Text = theme.Name;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
    }

    internal ThemeImage Theme { get; }

    internal event EventHandler? DeleteRequested;

    private Rectangle DeleteButtonBounds => new(Width - 27, 7, 20, 20);

    protected override void OnMouseMove(MouseEventArgs mevent)
    {
        base.OnMouseMove(mevent);
        if (!Theme.IsBuiltIn)
        {
            bool hovered = DeleteButtonBounds.Contains(mevent.Location);
            if (hovered != _deleteHovered)
            {
                _deleteHovered = hovered;
                Invalidate(DeleteButtonBounds);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        base.OnMouseLeave(eventargs);
        if (_deleteHovered)
        {
            _deleteHovered = false;
            Invalidate(DeleteButtonBounds);
        }
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (!Theme.IsBuiltIn && mevent.Button == MouseButtons.Left && DeleteButtonBounds.Contains(mevent.Location))
        {
            _deletePressed = true;
            Invalidate(DeleteButtonBounds);
            return;
        }

        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        if (_deletePressed)
        {
            _deletePressed = false;
            Invalidate(DeleteButtonBounds);
            if (DeleteButtonBounds.Contains(mevent.Location))
            {
                DeleteRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        ThemePalette palette = ThemeService.Palette;
        Rectangle bounds = ClientRectangle;
        using (SolidBrush background = new(Checked ? palette.SurfaceHover : palette.SurfaceRaised))
        {
            pevent.Graphics.FillRectangle(background, bounds);
        }

        Rectangle pictureBounds = new(5, 5, ThumbnailSize.Width, ThumbnailSize.Height);
        Bitmap? thumbnail = ThemeThumbnailCache.Get(Theme, ThumbnailSize);
        if (thumbnail is not null)
        {
            pevent.Graphics.DrawImageUnscaled(thumbnail, pictureBounds.Location);
        }
        else
        {
            using SolidBrush failed = new(palette.Canvas);
            pevent.Graphics.FillRectangle(failed, pictureBounds);
            TextRenderer.DrawText(
                pevent.Graphics,
                "Image unavailable",
                ThemeService.InterfaceFont,
                pictureBounds,
                palette.Error,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        Rectangle textBounds = new(7, 92, bounds.Width - 14, 24);
        TextRenderer.DrawText(
            pevent.Graphics,
            Theme.Name,
            ThemeService.InterfaceFont,
            textBounds,
            palette.Text,
            TextFormatFlags.EndEllipsis
            | TextFormatFlags.HorizontalCenter
            | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine);

        Color borderColour = Checked ? palette.Accent : palette.Border;
        int inset = Checked ? 1 : 0;
        using Pen border = new(borderColour, Checked ? 2f : 1f);
        pevent.Graphics.DrawRectangle(
            border,
            inset,
            inset,
            bounds.Width - (inset * 2) - 1,
            bounds.Height - (inset * 2) - 1);

        // Render X button badge on user-added (non-built-in) custom themes
        if (!Theme.IsBuiltIn)
        {
            Rectangle btn = DeleteButtonBounds;
            pevent.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color btnBg = _deleteHovered
                ? palette.Error
                : Color.FromArgb(175, 20, 20, 24);
            using (SolidBrush bgBrush = new(btnBg))
            {
                pevent.Graphics.FillEllipse(bgBrush, btn);
            }

            using (Pen btnPen = new(Color.FromArgb(220, 255, 255, 255), 1.8f))
            {
                int pad = 5;
                pevent.Graphics.DrawLine(btnPen, btn.Left + pad, btn.Top + pad, btn.Right - pad, btn.Bottom - pad);
                pevent.Graphics.DrawLine(btnPen, btn.Right - pad, btn.Top + pad, btn.Left + pad, btn.Bottom - pad);
            }

            pevent.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
        }

        if (Focused)
        {
            ControlPaint.DrawFocusRectangle(
                pevent.Graphics,
                Rectangle.Inflate(bounds, -4, -4),
                palette.Text,
                palette.SurfaceRaised);
        }
    }
}

