using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.UiKit;

/// <summary>Collapsible inspector section header + body host.</summary>
public sealed class InspectorSection : Panel
{
    private readonly Label _header = new();
    private readonly Panel _body = new();
    private bool _expanded = true;
    private string _title = "Section";

    public InspectorSection()
    {
        DoubleBuffered = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = EditorChrome.Surface;
        _header.AutoSize = false;
        _header.Dock = DockStyle.Top;
        _header.Height = EditorChrome.SectionHeaderHeight;
        _header.Padding = EditorChrome.SectionHeaderPadding;
        _header.Font = EditorChrome.HeadingFont;
        _header.Cursor = Cursors.Hand;
        _header.Click += (_, _) => Expanded = !Expanded;
        _body.Dock = DockStyle.Top;
        _body.AutoSize = true;
        _body.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _body.Padding = new Padding(8, 4, 8, 8);
        Controls.Add(_body);
        Controls.Add(_header);
        ApplyChrome();
        EditorChrome.Changed += OnChromeChanged;
        RefreshHeader();
    }

    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;
            RefreshHeader();
        }
    }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
            {
                return;
            }

            _expanded = value;
            _body.Visible = _expanded;
            PerformLayout();
            Parent?.PerformLayout();
            RefreshHeader();
            ExpandedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Panel Body => _body;

    public event EventHandler? ExpandedChanged;

    private void OnChromeChanged(object? sender, EventArgs e) => ApplyChrome();

    protected override void Dispose(bool disposing)
    {
        if (disposing) EditorChrome.Changed -= OnChromeChanged;
        base.Dispose(disposing);
    }

    private void RefreshHeader()
    {
        _header.Text = $"{(_expanded ? "▾" : "▸")}  {_title}";
        Invalidate();
    }

    private void ApplyChrome()
    {
        BackColor = EditorChrome.Surface;
        _header.BackColor = EditorChrome.Raised;
        _header.ForeColor = EditorChrome.Muted;
        _header.Font = EditorChrome.HeadingFont;
        _body.BackColor = EditorChrome.Surface;
    }
}
