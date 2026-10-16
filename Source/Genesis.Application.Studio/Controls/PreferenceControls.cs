using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Controls;

/// <summary>
/// An on/off switch. Still a <see cref="CheckBox"/> (Checked, CheckedChanged, keyboard, tests), but
/// drawn as a track and knob that slides; its label is the setting row's title, not its own text.
/// </summary>
internal sealed class PreferenceSwitch : CheckBox
{
    private float _knob;

    public PreferenceSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = false;
        Size = new Size(46, 26);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        Text = string.Empty;
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        float from = _knob, to = Checked ? 1f : 0f;
        UiMotion.Animate(this, "knob", 160, t => { _knob = UiMotion.Lerp(from, to, t); Invalidate(); });
        base.OnCheckedChanged(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        _knob = Checked ? 1f : 0f;
        base.OnHandleCreated(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeService.Palette;
        Graphics g = e.Graphics;
        g.Clear(UiDrawing.EffectiveBackColor(Parent));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int height = Math.Min(Height - 4, DpiLayout.Scale(this, 22));
        int width = Math.Min(Width - 2, height * 2);
        Rectangle track = new(1, (Height - height) / 2, width, height);
        Color off = UiMotion.Mix(palette.SurfaceRaised, palette.BorderStrong, 0.5f);
        Color fill = UiMotion.Mix(off, palette.Accent, _knob);
        if (!Enabled) fill = UiMotion.Mix(fill, palette.Surface, 0.55f);
        using (GraphicsPath path = UiDrawing.RoundedRectangle(track, height / 2))
        using (SolidBrush brush = new(fill))
        using (Pen edge = new(UiMotion.Mix(palette.Border, palette.Accent, _knob), 1f))
        {
            g.FillPath(brush, path);
            g.DrawPath(edge, path);
        }
        int knob = height - 6;
        float x = track.Left + 3 + (track.Width - knob - 6) * _knob;
        using SolidBrush knobBrush = new(Enabled ? Color.White : UiMotion.Mix(Color.White, palette.Surface, 0.5f));
        g.FillEllipse(knobBrush, x, track.Top + 3, knob, knob);
        if (Focused && ShowFocusCues)
        {
            using Pen focus = new(palette.AccentHover, 1f) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }
}

/// <summary>
/// One setting: a title and a plain-language line under it on the left, the control on the right.
/// A wide row gives its control the whole width instead (galleries, lists, button rows).
/// </summary>
internal sealed class SettingRow : Panel
{
    private readonly Label? _title;
    private readonly Label? _description;
    private readonly Control _editor;
    private readonly bool _wide;
    private readonly int _editorWidth;

    public SettingRow(string? title, string? description, Control editor, bool wide, int editorWidth, string searchText)
    {
        _editor = editor;
        _wide = wide;
        _editorWidth = editorWidth;
        BackColor = Color.Transparent;
        Tag = "transparent";
        SearchText = $"{title} {description} {searchText}";
        if (!string.IsNullOrEmpty(title))
        {
            _title = new Label
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = ThemeService.Palette.Text,
                Text = title,
                UseMnemonic = false,
            };
            Controls.Add(_title);
            if (string.IsNullOrEmpty(editor.AccessibleName)) editor.AccessibleName = title;
        }
        if (!string.IsNullOrEmpty(description))
        {
            _description = new Label
            {
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = ThemeService.Palette.TextMuted,
                Text = description,
                UseMnemonic = false,
            };
            Controls.Add(_description);
        }
        Controls.Add(editor);
    }

    public string SearchText { get; }

    private static Font? _titleFont;

    /// <summary>The bold title font, made again only when the interface font changes.</summary>
    internal static Font TitleFont
    {
        get
        {
            Font basis = ThemeService.InterfaceFont;
            if (_titleFont is null || _titleFont.Size != basis.Size || _titleFont.FontFamily.Name != basis.FontFamily.Name)
                _titleFont = new Font(basis, FontStyle.Bold);
            return _titleFont;
        }
    }

    /// <summary>Lays the row out for <paramref name="width"/> and returns its height.</summary>
    public int Arrange(int width)
    {
        int S(int value) => DpiLayout.Scale(this, value);
        Width = width;
        if (_title is not null && !ReferenceEquals(_title.Font, TitleFont)) _title.Font = TitleFont;
        int y = 0;
        if (_wide)
        {
            if (_title is not null)
            {
                _title.Bounds = new Rectangle(0, 0, width, Forms.ShellDialogLayout.TextHeight(_title, width) + 2);
                y = _title.Bottom + S(2);
            }
            if (_description is not null)
            {
                _description.Bounds = new Rectangle(0, y, width, Forms.ShellDialogLayout.TextHeight(_description, width) + 2);
                y = _description.Bottom + S(6);
            }
            int editorHeight = _editor is SettingsList list ? list.Height : _editor.Height;
            _editor.Bounds = new Rectangle(0, y, width, Math.Max(editorHeight, S(8)));
            if (_editor is FlowLayoutPanel flow)
            {
                flow.MaximumSize = new Size(width, 0);
                _editor.Height = flow.GetPreferredSize(new Size(width, 0)).Height;
            }
            Height = _editor.Bottom;
            return Height;
        }

        int editorWidth = _editor is CheckBox or KitToggleHost ? _editor.Width : Math.Min(S(_editorWidth), Math.Max(S(120), width / 2));
        if (_editor is FlowLayoutPanel row) editorWidth = Math.Min(width / 2 + S(60), row.GetPreferredSize(Size.Empty).Width);
        int textWidth = Math.Max(S(80), width - editorWidth - S(24));
        if (_title is not null)
        {
            _title.Bounds = new Rectangle(0, 0, textWidth, Forms.ShellDialogLayout.TextHeight(_title, textWidth) + 2);
            y = _title.Bottom;
        }
        if (_description is not null)
        {
            _description.Bounds = new Rectangle(0, y + S(1), textWidth, Forms.ShellDialogLayout.TextHeight(_description, textWidth) + 2);
            y = _description.Bottom;
        }
        if (_editor is not CheckBox and not KitToggleHost) _editor.Width = editorWidth;
        int editorHeightRow = _editor is FlowLayoutPanel flowRow ? flowRow.GetPreferredSize(new Size(editorWidth, 0)).Height : _editor.Height;
        if (_editor is FlowLayoutPanel) _editor.Height = editorHeightRow;
        int rowHeight = Math.Max(y, editorHeightRow);
        _editor.Location = new Point(width - editorWidth, Math.Max(0, (rowHeight - editorHeightRow) / 2));
        if (_title is not null && _description is null && rowHeight > _title.Height)
            _title.Top = (rowHeight - _title.Height) / 2;
        Height = rowHeight;
        return Height;
    }
}

/// <summary>A text box in a rounded field that lights up with the accent while you type in it.</summary>
internal sealed class TextFieldHost : Panel
{
    private readonly TextBox _box;

    public TextFieldHost(TextBox box, int width)
    {
        _box = box;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Tag = "transparent";
        box.BorderStyle = BorderStyle.None;
        Controls.Add(box);
        Size = new Size(width, box.PreferredHeight + 14);
        box.GotFocus += (_, _) => Invalidate();
        box.LostFocus += (_, _) => Invalidate();
        box.FontChanged += (_, _) => { Height = _box.PreferredHeight + DpiLayout.Scale(this, 14); Arrange(); };
        Resize += (_, _) => Arrange();
    }

    private void Arrange()
    {
        int pad = DpiLayout.Scale(this, 10);
        _box.BorderStyle = BorderStyle.None;
        _box.Bounds = new Rectangle(pad, Math.Max(0, (Height - _box.PreferredHeight) / 2), Math.Max(1, Width - pad * 2), _box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeService.Palette;
        e.Graphics.Clear(UiDrawing.EffectiveBackColor(Parent));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle field = new(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = UiDrawing.RoundedRectangle(field, DpiLayout.Scale(this, 8));
        using SolidBrush fill = new(_box.BackColor);
        using Pen edge = new(_box.Focused ? palette.Accent : palette.Border, 1f);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(edge, path);
    }
}

/// <summary>Wraps a control that sizes itself (a toggle) so a row leaves its size alone.</summary>
internal sealed class KitToggleHost : Panel
{
    public KitToggleHost(Control toggle)
    {
        BackColor = Color.Transparent;
        Tag = "transparent";
        Size = toggle.Size;
        toggle.Location = Point.Empty;
        Controls.Add(toggle);
    }
}

/// <summary>A list (such as the shortcuts) that a wide row should give a fixed height.</summary>
internal sealed class SettingsList : Panel
{
    public SettingsList(Control list, int height)
    {
        BackColor = Color.Transparent;
        Tag = "transparent";
        Height = height;
        list.Dock = DockStyle.Fill;
        Controls.Add(list);
    }
}

/// <summary>A card of related settings: a heading, then rows divided by hairlines.</summary>
internal sealed class SettingsSection : RoundedSurfacePanel
{
    private readonly Label _heading;
    private readonly List<SettingRow> _rows = [];
    private readonly List<int> _dividers = [];

    public SettingsSection(string heading)
    {
        CornerRadius = 12;
        Raised = true;
        Margin = Padding.Empty;
        _heading = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.Text,
            Font = HeadingFont,
            Text = heading,
            UseMnemonic = false,
        };
        Controls.Add(_heading);
    }

    public string Heading => _heading.Text;

    private static Font? _headingFont;

    private static Font HeadingFont
    {
        get
        {
            Font basis = ThemeService.InterfaceFont;
            float size = basis.Size + 1.5f;
            if (_headingFont is null || _headingFont.Size != size || _headingFont.FontFamily.Name != basis.FontFamily.Name)
                _headingFont = new Font(basis.FontFamily, size, FontStyle.Bold);
            return _headingFont;
        }
    }

    public SettingRow Add(string title, string? description, Control editor, int editorWidth = 260, string searchText = "")
    {
        SettingRow row = new(title, description, editor, wide: false, editorWidth, searchText);
        _rows.Add(row);
        Controls.Add(row);
        return row;
    }

    public SettingRow AddWide(Control editor, string? title = null, string? description = null, string searchText = "")
    {
        SettingRow row = new(title, description, editor, wide: true, 0, searchText);
        _rows.Add(row);
        Controls.Add(row);
        return row;
    }

    /// <summary>Shows only the rows matching <paramref name="text"/> (all of them when the heading matches); false when none do.</summary>
    public bool ApplyFilter(string text)
    {
        bool headingMatches = text.Length == 0 || Heading.Contains(text, StringComparison.OrdinalIgnoreCase);
        bool any = false;
        _filteredRows.Clear();
        foreach (SettingRow row in _rows)
        {
            bool show = headingMatches || row.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase);
            if (!show) _filteredRows.Add(row);
            any |= show;
        }
        Visible = any;
        return any;
    }

    public void Arrange(int width)
    {
        int S(int value) => DpiLayout.Scale(this, value);
        Width = width;
        int pad = S(20), inner = Math.Max(S(120), width - pad * 2);
        if (!ReferenceEquals(_heading.Font, HeadingFont)) _heading.Font = HeadingFont;
        _heading.Bounds = new Rectangle(pad, S(16), inner, _heading.Font.Height + S(4));
        int y = _heading.Bottom + S(10);
        _dividers.Clear();
        bool first = true;
        foreach (SettingRow row in _rows)
        {
            // A row hidden by the search stays out of the layout; a row hidden by its page (a theme
            // mode's field) is skipped the same way.
            bool shown = !_filteredRows.Contains(row) && !_hiddenRows.Contains(row);
            row.Visible = shown;
            if (!shown) continue;
            if (!first)
            {
                _dividers.Add(y + S(8));
                y += S(17);
            }
            first = false;
            row.Location = new Point(pad, y);
            y += row.Arrange(inner);
        }
        Height = y + S(18);
        Invalidate();
    }

    private readonly HashSet<SettingRow> _hiddenRows = [];
    private readonly HashSet<SettingRow> _filteredRows = [];

    /// <summary>Hides or shows a row for a reason other than the search (a theme mode's own fields).</summary>
    public void SetRowShown(SettingRow row, bool shown)
    {
        if (shown) _hiddenRows.Remove(row);
        else _hiddenRows.Add(row);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int pad = DpiLayout.Scale(this, 20);
        using Pen line = new(UiMotion.Mix(ThemeService.Palette.SurfaceRaised, ThemeService.Palette.Border, 0.7f), 1f);
        foreach (int y in _dividers) e.Graphics.DrawLine(line, pad, y, Width - pad, y);
    }
}

/// <summary>A scrolling page: a large title, a line about the page, then its sections.</summary>
internal sealed class PreferencePage : Panel
{
    private readonly Label _title;
    private readonly Label _description;
    private readonly List<SettingsSection> _sections = [];

    public PreferencePage(string name, string title, string description)
    {
        Name = name;
        AutoScroll = true;
        BackColor = ThemeService.Palette.Canvas;
        Tag = "canvas";
        _title = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.Text,
            Text = title,
            UseMnemonic = false,
        };
        _description = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Text = description,
            UseMnemonic = false,
        };
        Controls.Add(_title);
        Controls.Add(_description);
        ClientSizeChanged += (_, _) => Arrange();
    }

    public IReadOnlyList<SettingsSection> Sections => _sections;

    public SettingsSection Section(string heading)
    {
        SettingsSection section = new(heading);
        _sections.Add(section);
        Controls.Add(section);
        return section;
    }

    public bool ApplyFilter(string text)
    {
        bool any = false;
        foreach (SettingsSection section in _sections) any |= section.ApplyFilter(text);
        return any;
    }

    private bool _arranging;

    public void Arrange()
    {
        if (_arranging || ClientSize.Width <= 0) return;
        _arranging = true;
        try
        {
            int S(int value) => DpiLayout.Scale(this, value);
            Point scroll = AutoScrollPosition;
            int pad = S(28);
            int width = Math.Max(S(320), ClientSize.Width - pad * 2);
            float titleSize = 20f * ThemeService.InterfaceScale;
            if (Math.Abs(_title.Font.Size - titleSize) > 0.01f || !_title.Font.Bold)
                _title.Font = new Font("Segoe UI Variable Display", titleSize, FontStyle.Bold);
            _title.Bounds = new Rectangle(pad, scroll.Y + S(22), width, _title.Font.Height + S(6));
            _description.Bounds = new Rectangle(pad, _title.Bottom + S(2), width, Forms.ShellDialogLayout.TextHeight(_description, width) + 2);
            int y = _description.Bottom + S(18);
            foreach (SettingsSection section in _sections)
            {
                if (!section.Visible) continue;
                section.Location = new Point(pad, y);
                section.Arrange(width);
                y = section.Bottom + S(16);
            }
            AutoScrollMinSize = new Size(0, y - scroll.Y + S(12));
        }
        finally { _arranging = false; }
    }
}
