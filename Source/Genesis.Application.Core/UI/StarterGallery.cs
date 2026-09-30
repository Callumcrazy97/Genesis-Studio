using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Genesis.Application.Core.UI;

/// <summary>One ready-made starting point shown as a card in a <see cref="StarterGallery"/>.</summary>
/// <param name="Id">Stable identifier; the card is named <c>Starter_&lt;Id&gt;</c>.</param>
/// <param name="Title">What the user gets ("Rolling hills", "Campfire smoke").</param>
/// <param name="Description">One short sentence; the card shows two lines.</param>
public sealed record StarterItem(string Id, string Title, string Description)
{
    /// <summary>Optional group heading; cards are grouped in first-seen category order.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Optional <see cref="UiGlyphs"/> icon drawn on the swatch.</summary>
    public string Glyph { get; init; } = string.Empty;

    /// <summary>Swatch colour when there is no thumbnail. Empty uses the accent.</summary>
    public Color Swatch { get; init; } = Color.Empty;

    /// <summary>Optional picture drawn over the swatch (cropped to fill).</summary>
    public Image? Thumbnail { get; init; }

    /// <summary>Optional short label such as "Recommended", "Done" or "Preview only".</summary>
    public string Badge { get; init; } = string.Empty;

    /// <summary>Colour of the badge: accent, success (done) or warning (preview only).</summary>
    public StarterBadgeTone BadgeTone { get; init; }
}

public enum StarterBadgeTone
{
    Accent,
    Success,
    Warning,
}

/// <summary>
/// A responsive grid of <see cref="StarterItem"/> cards, grouped under category headings. One
/// click chooses a card; there is no separate Apply or OK step.
/// </summary>
/// <remarks>
/// Every editor's first workflow step starts from something ready-made instead of a blank
/// document or a modal dialog. The cards are focusable buttons (Tab, Enter, Space) with the
/// description as their accessible description.
/// </remarks>
public sealed class StarterGallery : Panel
{
    private const int LogicalCardWidth = 188;
    private const int LogicalSwatchHeight = 60;
    private const int CompactCardWidth = 128;
    private const int CompactSwatchHeight = 34;
    private const int LogicalGap = 10;
    private const int LogicalPadding = 12;

    private readonly List<StarterCard> _cards = [];
    private readonly List<Label> _headings = [];
    private string _heading = string.Empty;
    private string _subheading = string.Empty;
    private string? _selectedId;
    private Font? _glyphFont;
    private bool _laying;
    private bool _fitsContent;
    private bool _compact;
    private readonly ToolTip _tips = new() { ShowAlways = true };

    public StarterGallery(string name)
    {
        Name = name;
        AutoScroll = true;
        DoubleBuffered = true;
        Tag = "surface";
        BackColor = UiTokens.Surface;
        AccessibleRole = AccessibleRole.List;
        UiTokens.Changed += OnTokensChanged;
        Disposed += (_, _) =>
        {
            UiTokens.Changed -= OnTokensChanged;
            _glyphFont?.Dispose();
            _tips.Dispose();
        };
    }

    /// <summary>Raised when the user (or <see cref="Choose"/>) picks a card.</summary>
    public event EventHandler<StarterItem>? ItemChosen;

    /// <summary>Optional title drawn above the cards ("Start with a landform").</summary>
    [DefaultValue("")]
    public string Heading
    {
        get => _heading;
        set
        {
            _heading = value ?? string.Empty;
            AccessibleName = _heading;
            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>Optional plain sentence under the heading.</summary>
    [DefaultValue("")]
    public string Subheading
    {
        get => _subheading;
        set
        {
            _subheading = value ?? string.Empty;
            PerformLayout();
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<StarterItem> Items => _cards.Select(card => card.Item).ToList();

    /// <summary>The card drawn as current (for example the landform in use), or null.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            _selectedId = value;
            foreach (StarterCard card in _cards)
            {
                card.Invalidate();
            }
        }
    }

    public void SetItems(IEnumerable<StarterItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        List<StarterItem> list = items.ToList();
        if (list.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != list.Count)
        {
            throw new ArgumentException("Starter ids must be unique.", nameof(items));
        }

        SuspendLayout();
        try
        {
            foreach (Control control in _cards.Cast<Control>().Concat(_headings))
            {
                Controls.Remove(control);
                control.Dispose();
            }

            _cards.Clear();
            _headings.Clear();
            foreach (string category in list.Select(item => item.Category).Distinct(StringComparer.Ordinal))
            {
                if (category.Length > 0)
                {
                    Label heading = new()
                    {
                        AutoSize = false,
                        BackColor = Color.Transparent,
                        Name = "StarterCategory_" + category.Replace(' ', '_'),
                        Text = UiTokens.DisplayHeading(category),
                        TextAlign = ContentAlignment.BottomLeft,
                        UseMnemonic = false,
                    };
                    _headings.Add(heading);
                    Controls.Add(heading);
                }

                foreach (StarterItem item in list.Where(item => item.Category == category))
                {
                    StarterCard card = new(this, item);
                    _tips.SetToolTip(card, item.Description);
                    _cards.Add(card);
                    Controls.Add(card);
                }
            }

            ApplyTokens();
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    public Control? Card(string id) => _cards.FirstOrDefault(card => card.Item.Id == id);

    /// <summary>Chooses a card as if it were clicked. Returns false for an unknown id.</summary>
    public bool Choose(string id)
    {
        StarterCard? card = _cards.FirstOrDefault(candidate => candidate.Item.Id == id);
        if (card is null)
        {
            return false;
        }

        SelectedId = id;
        ItemChosen?.Invoke(this, card.Item);
        return true;
    }

    /// <summary>The height the gallery needs to show every card at its current width.</summary>
    public int PreferredContentHeight => LayoutCards(apply: false);

    /// <summary>
    /// Smaller cards (title only, the description becomes a tooltip) for side panels, where two
    /// columns of full cards would not fit.
    /// </summary>
    [DefaultValue(false)]
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            _glyphFont?.Dispose();
            _glyphFont = null;
            PerformLayout();
            Invalidate(true);
        }
    }

    /// <summary>
    /// When true the gallery never scrolls: it sets its own height to fit every card, for use
    /// inside a page that already scrolls (the Home page).
    /// </summary>
    [DefaultValue(false)]
    public bool FitsContent
    {
        get => _fitsContent;
        set
        {
            _fitsContent = value;
            AutoScroll = !value;
            PerformLayout();
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_laying)
        {
            return;
        }

        _laying = true;
        try
        {
            int height = LayoutCards(apply: true);
            if (_fitsContent)
            {
                if (Height != height)
                {
                    Height = height;
                }
            }
            else
            {
                Size minimum = new(0, height);
                if (AutoScrollMinSize != minimum)
                {
                    AutoScrollMinSize = minimum;
                }
            }
        }
        finally
        {
            _laying = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int pad = Scale(LogicalPadding);
        int y = AutoScrollPosition.Y + pad;
        int width = Math.Max(1, ClientSize.Width - pad * 2);
        if (_heading.Length > 0)
        {
            int h = TextRenderer.MeasureText(_heading, UiTokens.TitleFont, new Size(width, 0), TextFormatFlags.WordBreak).Height;
            TextRenderer.DrawText(e.Graphics, _heading, UiTokens.TitleFont, new Rectangle(pad, y, width, h), UiTokens.Text,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            y += h + Scale(2);
        }

        if (_subheading.Length > 0)
        {
            int h = TextRenderer.MeasureText(_subheading, UiTokens.BaseFont, new Size(width, 0), TextFormatFlags.WordBreak).Height;
            TextRenderer.DrawText(e.Graphics, _subheading, UiTokens.BaseFont, new Rectangle(pad, y, width, h), UiTokens.Muted,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _glyphFont?.Dispose();
        _glyphFont = null;
        PerformLayout();
    }

    /// <summary>Scales logical pixels by monitor DPI and by the interface text size preference.</summary>
    internal int Scale(int logical) =>
        (int)Math.Round(logical * DeviceDpi / 96f * Math.Clamp(UiTokens.BaseFont.SizeInPoints / 9.5f, 0.75f, 2f));

    internal Font? GlyphFont => _glyphFont ??= UiGlyphs.CreateFont(_compact ? 13f : 17f);

    internal int SwatchHeight => Scale(_compact ? CompactSwatchHeight : LogicalSwatchHeight);

    internal void OnCardChosen(StarterCard card) => Choose(card.Item.Id);

    internal int CardHeight()
    {
        int title = TextRenderer.MeasureText("Ag", UiTokens.StrongFont, Size.Empty, TextFormatFlags.NoPadding).Height;
        int line = TextRenderer.MeasureText("Ag", UiTokens.SmallFont, Size.Empty, TextFormatFlags.NoPadding).Height;
        return _compact
            ? Scale(6) + SwatchHeight + Scale(6) + title + Scale(8)
            : Scale(8) + SwatchHeight + Scale(8) + title + Scale(3) + line * 2 + Scale(10);
    }

    private int LayoutCards(bool apply)
    {
        int pad = Scale(LogicalPadding);
        int gap = Scale(LogicalGap);
        int available = Math.Max(1, ClientSize.Width - pad * 2);
        int y = pad;
        if (_heading.Length > 0)
        {
            y += TextRenderer.MeasureText(_heading, UiTokens.TitleFont, new Size(available, 0), TextFormatFlags.WordBreak).Height + Scale(2);
        }

        if (_subheading.Length > 0)
        {
            y += TextRenderer.MeasureText(_subheading, UiTokens.BaseFont, new Size(available, 0), TextFormatFlags.WordBreak).Height;
        }

        if (_heading.Length > 0 || _subheading.Length > 0)
        {
            y += Scale(8);
        }

        // Cards stretch to share the row, between their logical width and 1.5x it, so a wide pane
        // shows larger cards instead of a ragged strip of empty space on the right.
        int minimumCard = Scale(_compact ? CompactCardWidth : LogicalCardWidth);
        int columns = Math.Max(1, (available + gap) / (minimumCard + gap));
        int cardWidth = Math.Min((available - gap * (columns - 1)) / columns, minimumCard * 3 / 2);
        cardWidth = Math.Max(Math.Min(minimumCard, available), cardWidth);
        int cardHeight = CardHeight();
        int headingHeight = TextRenderer.MeasureText("Ag", UiTokens.StrongFont, Size.Empty, TextFormatFlags.NoPadding).Height + Scale(10);
        Point scroll = AutoScrollPosition;

        int headingIndex = 0;
        foreach (IGrouping<string, StarterCard> group in _cards.GroupBy(card => card.Item.Category))
        {
            if (group.Key.Length > 0 && headingIndex < _headings.Count)
            {
                if (apply)
                {
                    _headings[headingIndex].SetBounds(pad + scroll.X, y + scroll.Y, available, headingHeight);
                }

                headingIndex++;
                y += headingHeight + Scale(4);
            }

            int column = 0;
            foreach (StarterCard card in group)
            {
                if (apply)
                {
                    card.SetBounds(pad + scroll.X + column * (cardWidth + gap), y + scroll.Y, cardWidth, cardHeight);
                }

                column++;
                if (column == columns)
                {
                    column = 0;
                    y += cardHeight + gap;
                }
            }

            if (column != 0)
            {
                y += cardHeight + gap;
            }

            y += Scale(6);
        }

        return y + pad;
    }

    private void OnTokensChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        ApplyTokens();
        PerformLayout();
        Invalidate(true);
    }

    private void ApplyTokens()
    {
        // Follows the theme service's convention: a "canvas" tag sits on the page background.
        BackColor = string.Equals(Tag as string, "canvas", StringComparison.Ordinal) ? UiTokens.Canvas : UiTokens.Surface;
        foreach (Label heading in _headings)
        {
            heading.Font = UiTokens.StrongFont;
            heading.ForeColor = UiTokens.Text;
        }
    }

    /// <summary>One card: swatch (colour, glyph or picture), badge, title and description.</summary>
    internal sealed class StarterCard : Control
    {
        private readonly StarterGallery _owner;
        private bool _hovered;

        public StarterCard(StarterGallery owner, StarterItem item)
        {
            _owner = owner;
            Item = item;
            Name = "Starter_" + item.Id;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = item.Title;
            AccessibleDescription = item.Badge.Length > 0 ? $"{item.Description} ({item.Badge})" : item.Description;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
        }

        public StarterItem Item { get; }

        [AllowNull]
        public override string Text
        {
            get => Item.Title;
            set { }
        }

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
            base.OnClick(e);
            Focus();
            _owner.OnCardChosen(this);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int radius = _owner.Scale(8);
            bool selected = string.Equals(_owner.SelectedId, Item.Id, StringComparison.Ordinal);
            Rectangle card = new(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color fill = selected ? UiTokens.AccentSoft : _hovered ? UiTokens.Hover : UiTokens.Raised;
            PillToolStripRenderer.FillRound(g, card, fill, radius);
            Color stroke = selected || Focused && ShowFocusCues
                ? UiTokens.Accent
                : UiTokens.Blend(UiTokens.Border, UiTokens.Raised, _hovered ? 1f : 0.6f);
            PillToolStripRenderer.StrokeRound(g, card, stroke, radius);

            int inset = _owner.Scale(_owner.Compact ? 6 : 8);
            Rectangle swatch = new(inset, inset, Math.Max(1, Width - inset * 2), _owner.SwatchHeight);
            PaintSwatch(g, swatch, _owner.Scale(6));

            if (Item.Badge.Length > 0)
            {
                PaintBadge(g, swatch);
            }

            int titleHeight = TextRenderer.MeasureText("Ag", UiTokens.StrongFont, Size.Empty, TextFormatFlags.NoPadding).Height;
            Rectangle title = new(inset + _owner.Scale(2), swatch.Bottom + _owner.Scale(_owner.Compact ? 6 : 8), Math.Max(1, Width - inset * 2 - _owner.Scale(4)), titleHeight);
            TextRenderer.DrawText(g, Item.Title, UiTokens.StrongFont, title, UiTokens.Text,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (_owner.Compact)
            {
                return;
            }

            Rectangle description = new(title.Left, title.Bottom + _owner.Scale(3), title.Width, Math.Max(1, Height - title.Bottom - _owner.Scale(3) - _owner.Scale(8)));
            TextRenderer.DrawText(g, Item.Description, UiTokens.SmallFont, description, UiTokens.Muted,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
        }

        private void PaintSwatch(Graphics g, Rectangle swatch, int radius)
        {
            Color baseColour = Item.Swatch.IsEmpty ? UiTokens.Accent : Item.Swatch;
            using GraphicsPath path = PillToolStripRenderer.RoundedRect(swatch, radius);
            if (Item.Thumbnail is { } picture)
            {
                GraphicsState state = g.Save();
                g.SetClip(path);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(picture, Cover(picture.Size, swatch));
                g.Restore(state);
                return;
            }

            Color top = UiTokens.Blend(baseColour, UiTokens.Raised, 0.85f);
            Color bottom = UiTokens.Blend(baseColour, UiTokens.Canvas, 0.45f);
            using (LinearGradientBrush brush = new(swatch, top, bottom, LinearGradientMode.ForwardDiagonal))
            {
                g.FillPath(brush, path);
            }

            if (Item.Glyph.Length > 0 && _owner.GlyphFont is { } glyphFont)
            {
                Color ink = UiTokens.Luminance(bottom) > 0.55f ? UiTokens.FromHex("#10131A") : Color.White;
                TextRenderer.DrawText(g, Item.Glyph, glyphFont, swatch, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private void PaintBadge(Graphics g, Rectangle swatch)
        {
            Size text = TextRenderer.MeasureText(Item.Badge, UiTokens.SmallFont, Size.Empty, TextFormatFlags.NoPadding);
            int padX = _owner.Scale(7);
            int padY = _owner.Scale(2);
            Rectangle badge = new(
                swatch.Right - text.Width - padX * 2 - _owner.Scale(6),
                swatch.Top + _owner.Scale(6),
                text.Width + padX * 2,
                text.Height + padY * 2);
            Color back = Item.BadgeTone switch
            {
                StarterBadgeTone.Success => UiTokens.Success,
                StarterBadgeTone.Warning => UiTokens.Warning,
                _ => UiTokens.Accent,
            };
            PillToolStripRenderer.FillRound(g, badge, back, badge.Height / 2);
            Color ink = UiTokens.Luminance(back) > 0.6f ? UiTokens.FromHex("#10131A") : Color.White;
            TextRenderer.DrawText(g, Item.Badge, UiTokens.SmallFont, badge, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private static Rectangle Cover(Size source, Rectangle target)
        {
            if (source.Width <= 0 || source.Height <= 0)
            {
                return target;
            }

            float scale = Math.Max(target.Width / (float)source.Width, target.Height / (float)source.Height);
            int width = (int)Math.Ceiling(source.Width * scale);
            int height = (int)Math.Ceiling(source.Height * scale);
            return new Rectangle(target.X + (target.Width - width) / 2, target.Y + (target.Height - height) / 2, width, height);
        }
    }
}
