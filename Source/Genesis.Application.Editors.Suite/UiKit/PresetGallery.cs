using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.UiKit;

/// <summary>One selectable preset card for <see cref="PresetGallery"/>.</summary>
public sealed class PresetCard : Control
{
    private bool _hovered;

    public PresetCard()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        Size = new Size(168, 132);
        Cursor = Cursors.Hand;
        Font = EditorChrome.BaseFont;
        EditorChrome.Changed += (_, _) => Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = string.Empty;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Description { get; set; } = string.Empty;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public System.Drawing.Image? Thumbnail { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentPreview { get; set; } = EditorChrome.Accent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get; set; }

    public event EventHandler? SelectedChanged;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        Selected = true;
        SelectedChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle bounds = ClientRectangle;
        bounds.Inflate(-1, -1);
        Color fill = _hovered || Selected ? EditorChrome.Hover : EditorChrome.Surface;
        KitDraw.FillRound(g, bounds, fill);
        KitDraw.StrokeRound(g, bounds, Selected ? EditorChrome.Accent : EditorChrome.Border);

        Rectangle thumb = new(bounds.X + 8, bounds.Y + 8, bounds.Width - 16, 64);
        KitDraw.FillRound(g, thumb, AccentPreview, 4);
        if (Thumbnail is not null)
        {
            using var clip = KitDraw.RoundedRect(thumb, 4);
            g.SetClip(clip);
            g.DrawImage(Thumbnail, thumb);
            g.ResetClip();
        }

        TextRenderer.DrawText(
            g,
            Title,
            EditorChrome.HeadingFont,
            new Rectangle(bounds.X + 8, thumb.Bottom + 6, bounds.Width - 16, 18),
            EditorChrome.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.Left);

        TextRenderer.DrawText(
            g,
            Description,
            EditorChrome.SmallFont,
            new Rectangle(bounds.X + 8, thumb.Bottom + 24, bounds.Width - 16, 28),
            EditorChrome.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
    }
}

/// <summary>Card grid of presets (Hub templates, Physics scenes, etc.).</summary>
public sealed class PresetGallery : FlowLayoutPanel
{
    public PresetGallery()
    {
        AutoScroll = false;
        WrapContents = true;
        FlowDirection = FlowDirection.LeftToRight;
        BackColor = EditorChrome.Canvas;
        Padding = new Padding(4);
        EditorChrome.Changed += OnChromeChanged;
        Disposed += (_, _) => EditorChrome.Changed -= OnChromeChanged;
    }

    public event EventHandler<PresetCard>? SelectionChanged;

    public PresetCard? SelectedCard { get; private set; }

    public void SetPresets(IEnumerable<(string Title, string Description, Color Accent)> presets)
    {
        SuspendLayout();
        Controls.Clear();
        SelectedCard = null;
        foreach ((string title, string description, Color accent) in presets)
        {
            PresetCard card = new()
            {
                Title = title,
                Description = description,
                AccentPreview = accent,
                Margin = new Padding(6),
            };
            card.SelectedChanged += (_, _) => Select(card);
            Controls.Add(card);
        }

        ResumeLayout(true);
    }

    public void Select(PresetCard card)
    {
        foreach (Control child in Controls)
        {
            if (child is PresetCard other)
            {
                other.Selected = ReferenceEquals(other, card);
                other.Invalidate();
            }
        }

        SelectedCard = card;
        SelectionChanged?.Invoke(this, card);
    }

    private void OnChromeChanged(object? sender, EventArgs e)
    {
        BackColor = EditorChrome.Canvas;
        Invalidate(true);
    }
}
