using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Top-right second-camera chrome: a labelled frame around a live <see cref="EditorViewport3D"/>.
/// Matches the Camera POC feed (anchored inset, title bar, theme colours) without ECS placement.
/// </summary>
internal sealed class EditorSecondaryViewportHost : Panel
{
    private const int DefaultWidth = 360;
    private const int DefaultHeight = 220;
    private const int InsetMargin = 12;

    private readonly Label _caption;
    private readonly Button _close;

    public EditorSecondaryViewportHost()
    {
        Size = new Size(DefaultWidth, DefaultHeight);
        MinimumSize = new Size(240, 150);
        BackColor = EditorChrome.Border;
        Padding = new Padding(1);
        Anchor = AnchorStyles.Top | AnchorStyles.Right;

        Panel title = new()
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Top,
            Height = 24,
        };
        _close = new Button
        {
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            ForeColor = EditorChrome.Muted,
            TabStop = false,
            Text = "✕",
            Width = 24,
        };
        _close.FlatAppearance.BorderSize = 0;
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        _caption = new Label
        {
            Dock = DockStyle.Fill,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Text,
            Padding = new Padding(8, 4, 4, 0),
            Text = "Second camera",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        title.Controls.Add(_caption);
        title.Controls.Add(_close);

        Viewport = new EditorViewport3D
        {
            Dock = DockStyle.Fill,
            NavigationEnabled = false,
        };

        Controls.Add(Viewport);
        Controls.Add(title);
        EditorChrome.Changed += OnChromeChanged;
        Disposed += (_, _) => EditorChrome.Changed -= OnChromeChanged;
    }

    public EditorViewport3D Viewport { get; }

    public event EventHandler? CloseRequested;

    public void SetCaption(string text) =>
        _caption.Text = string.IsNullOrWhiteSpace(text) ? "Second camera" : text.Trim();

    public void LayoutIn(Control host)
    {
        ArgumentNullException.ThrowIfNull(host);
        int width = Math.Min(Width, Math.Max(MinimumSize.Width, host.ClientSize.Width - InsetMargin * 2));
        int height = Math.Min(Height, Math.Max(MinimumSize.Height, host.ClientSize.Height - InsetMargin * 2));
        Size = new Size(width, height);
        Location = new Point(Math.Max(InsetMargin, host.ClientSize.Width - Width - InsetMargin), InsetMargin);
        BringToFront();
    }

    private void OnChromeChanged(object? sender, EventArgs e)
    {
        BackColor = EditorChrome.Border;
        _caption.BackColor = EditorChrome.Surface;
        _caption.ForeColor = EditorChrome.Text;
        _caption.Font = EditorChrome.SmallFont;
        _close.ForeColor = EditorChrome.Muted;
        _close.BackColor = EditorChrome.Surface;
    }
}
