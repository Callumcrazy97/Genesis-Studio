namespace Genesis.Application.Editors.Image.Controls;

using System.ComponentModel;

/// <summary>Collapsible inspector section used by the image editor side panels.</summary>
public sealed class CollapsibleSection : Panel
{
    private readonly Label _header;
    private readonly Panel _content;
    private bool _expanded = true;
    private int _contentHeight;
    private int _expandedHeight;
    private int _collapsedHeight = HeaderHeight;
    private const int HeaderHeight = ImageEditorChrome.SectionHeaderHeight;

    public event EventHandler? StateChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string HeaderText
    {
        get => _header.Text.Length > 2 ? _header.Text[2..] : _header.Text;
        set => _header.Text = (_expanded ? "▼ " : "► ") + value;
    }

    public Panel Content => _content;

    public void SetContentHeight(int height)
    {
        _contentHeight = height; _expandedHeight = HeaderHeight + height;
        if (_expanded) Height = _expandedHeight;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded != value)
                Toggle();
        }
    }

    public CollapsibleSection(string headerText, int contentHeight, int width = ImageEditorChrome.SideSectionWidth)
    {
        _contentHeight = contentHeight;
        _expandedHeight = HeaderHeight + contentHeight;
        Size = new Size(width, _expandedHeight);
        BackColor = ImageEditorChrome.Surface;
        Margin = new Padding(0, 0, 0, ImageEditorChrome.SectionGap);

        _header = new Label
        {
            Dock = DockStyle.Top,
            Height = HeaderHeight,
            Font = ImageEditorChrome.HeadingFont,
            ForeColor = ImageEditorChrome.SectionTitle,
            BackColor = ImageEditorChrome.Raised,
            Padding = new Padding(8, 0, 8, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand,
        };
        _header.Click += (_, _) => Toggle();

        _content = new Panel
        {
            Location = new Point(0, HeaderHeight),
            Size = new Size(width, contentHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = ImageEditorChrome.Surface,
        };
        Controls.Add(_content);
        Controls.Add(_header);
        _header.BringToFront();

        HeaderText = headerText;
        ImageEditorChrome.Changed += OnChromeChanged;
        Disposed += (_, _) => ImageEditorChrome.Changed -= OnChromeChanged;
    }

    private void OnChromeChanged(object? sender, EventArgs e)
    {
        BackColor = ImageEditorChrome.Surface;
        _header.BackColor = ImageEditorChrome.Raised;
        _header.ForeColor = ImageEditorChrome.SectionTitle;
        _header.Font = ImageEditorChrome.HeadingFont;
        _content.BackColor = ImageEditorChrome.Surface;
        Invalidate();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (_content == null)
            return;
        _content.Bounds = new Rectangle(
            0,
            HeaderHeight,
            Width,
            Math.Max(0, Height - HeaderHeight));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using Pen pen = new(ImageEditorChrome.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
    }

    private void Toggle()
    {
        _expanded = !_expanded;
        if (_expanded)
        {
            _header.Text = _header.Text.Replace("►", "▼", StringComparison.Ordinal);
            _content.Visible = true;
            Height = _expandedHeight;
        }
        else
        {
            _header.Text = _header.Text.Replace("▼", "►", StringComparison.Ordinal);
            _content.Visible = false;
            Height = _collapsedHeight;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        _contentHeight = Math.Max(1, (int)Math.Round(_contentHeight * factor.Height));
        _expandedHeight = Math.Max(1, (int)Math.Round(_expandedHeight * factor.Height));
        _collapsedHeight = Math.Max(1, (int)Math.Round(_collapsedHeight * factor.Height));
        if (_expanded)
            Height = _expandedHeight;
    }
}
