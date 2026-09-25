using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Theme bridge for editor assemblies. Studio pushes its live <c>ThemePalette</c> values in
/// here on every theme change, so editor controls stay theme-aware without referencing the
/// shell assembly. Defaults are the GenesisStudioNext dark tokens.
/// </summary>
public static class EditorChrome
{
    public static Color Canvas { get; private set; } = FromHex("#14161D");
    public static Color Surface { get; private set; } = FromHex("#1C1F28");
    public static Color Raised { get; private set; } = FromHex("#252934");
    public static Color Hover { get; private set; } = FromHex("#2E3340");
    public static Color Border { get; private set; } = FromHex("#373D4C");
    public static Color Text { get; private set; } = FromHex("#EEF1F8");
    public static Color Muted { get; private set; } = FromHex("#9DA6BB");
    public static Color Accent { get; private set; } = FromHex("#6C8CFF");
    public static Color Success { get; private set; } = FromHex("#5BCA9A");
    public static Color Warning { get; private set; } = FromHex("#F5B551");
    public static Color Error { get; private set; } = FromHex("#F4626F");
    public static bool IsDark { get; private set; } = true;

    public static Font BaseFont { get; private set; } =
        new("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

    public static Font SmallFont { get; private set; } =
        new("Segoe UI Variable Text", 8f, FontStyle.Regular, GraphicsUnit.Point);

    public static Font HeadingFont { get; private set; } =
        new("Segoe UI Variable Text", 8f, FontStyle.Bold, GraphicsUnit.Point);

    public static Font CodeFont { get; private set; } =
        new("Cascadia Code", 10f, FontStyle.Regular, GraphicsUnit.Point);

    public const int CommandBarHeight = 44;
    public const int ModeRailWidth = 96;
    public const int ModeBarHeight = 40;
    public const int LeftPanelWidth = 280;
    public const int RightPanelWidth = 300;
    public const int CompactSidePanelWidth = 232;
    public const int BottomTimelineHeight = 132;
    public const int NarrowWidthBreakpoint = 1100;
    public const int MinimumWorkspaceWidth = 480;
    public const int SectionHeaderHeight = 27;
    public const int StatusBarHeight = 22;
    public const int FieldHeight = 28;
    public const int SplitterWidth = 3;
    public const int PanelPadding = 12;
    public const int CompactPadding = 8;
    public const int SectionGap = 10;
    public const int FieldGap = 6;

    public static readonly Padding CommandBarPadding = new(8, 6, 8, 6);
    public static readonly Padding StatusBarPadding = new(10, 3, 8, 0);
    public static readonly Padding SectionHeaderPadding = new(8, 6, 4, 0);
    public static readonly Padding ModeRailPadding = new(6, 8, 6, 8);
    public static readonly Padding PanelInsets = new(PanelPadding);
    public static readonly Padding CompactInsets = new(CompactPadding);

    /// <summary>Raised after <see cref="Update"/> so open editors re-theme live.</summary>
    public static event EventHandler? Changed;

    public static void Update(
        Color canvas,
        Color surface,
        Color raised,
        Color hover,
        Color border,
        Color text,
        Color muted,
        Color accent,
        Color success,
        Color warning,
        Color error,
        bool isDark,
        Font baseFont,
        Font codeFont)
    {
        Canvas = canvas;
        Surface = surface;
        Raised = raised;
        Hover = hover;
        Border = border;
        Text = text;
        Muted = muted;
        Accent = accent;
        Success = success;
        Warning = warning;
        Error = error;
        IsDark = isDark;
        BaseFont = baseFont;
        CodeFont = codeFont;
        SmallFont = new Font(baseFont.FontFamily, MathF.Max(7f, baseFont.SizeInPoints - 1.5f), FontStyle.Regular, GraphicsUnit.Point);
        HeadingFont = new Font(baseFont.FontFamily, MathF.Max(7f, baseFont.SizeInPoints - 1.5f), FontStyle.Bold, GraphicsUnit.Point);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    // ── Small control factory helpers shared by every editor surface ──────────────

    public static EditorCommandBar MakeToolbar()
    {
        EditorCommandBar bar = new()
        {
            BackColor = Surface,
            Font = BaseFont,
            Renderer = new ToolStripProfessionalRenderer(new ChromeColorTable()),
        };
        return bar;
    }

    public static ToolStripButton ToolButton(string text, string tooltip, Action onClick, bool toggle = false)
    {
        ToolStripButton button = new(text)
        {
            CheckOnClick = toggle,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ForeColor = Text,
            ToolTipText = tooltip,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Creates a compact vertical rail for persistent editor modes.</summary>
    public static ToolStrip MakeModeRail(int width = ModeRailWidth)
    {
        ToolStrip rail = new()
        {
            AutoSize = false,
            BackColor = Surface,
            CanOverflow = false,
            Dock = DockStyle.Left,
            Font = BaseFont,
            GripStyle = ToolStripGripStyle.Hidden,
            LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow,
            Padding = ModeRailPadding,
            Renderer = new ToolStripProfessionalRenderer(new ChromeColorTable()),
            Width = width,
        };
        return rail;
    }

    /// <summary>Creates a compact horizontal rail for persistent editor modes.</summary>
    public static ToolStrip MakeModeBar()
    {
        ToolStrip bar = new()
        {
            AutoSize = false,
            BackColor = Surface,
            CanOverflow = false,
            Dock = DockStyle.Top,
            Font = BaseFont,
            GripStyle = ToolStripGripStyle.Hidden,
            Height = ModeBarHeight,
            LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow,
            Padding = CommandBarPadding,
            Renderer = new ToolStripProfessionalRenderer(new ChromeColorTable()),
        };
        return bar;
    }

    public static Panel MakePanel(DockStyle dock, int width = 0, int height = 0, Padding? padding = null)
    {
        Panel panel = new()
        {
            BackColor = Surface,
            Dock = dock,
            Padding = padding ?? PanelInsets,
        };
        if (width > 0)
        {
            panel.Width = width;
            panel.MinimumSize = new Size(Math.Min(width, CompactSidePanelWidth), 0);
        }

        if (height > 0)
        {
            panel.Height = height;
            panel.MinimumSize = new Size(panel.MinimumSize.Width, Math.Min(height, 88));
        }

        return panel;
    }

    public static Panel MakeWorkspacePanel() => new()
    {
        BackColor = Canvas,
        Dock = DockStyle.Fill,
        MinimumSize = new Size(MinimumWorkspaceWidth, 320),
        Padding = Padding.Empty,
    };

    public static FlowLayoutPanel MakeSectionStack(DockStyle dock = DockStyle.Fill) => new()
    {
        AutoScroll = true,
        BackColor = Surface,
        Dock = dock,
        FlowDirection = FlowDirection.TopDown,
        Padding = PanelInsets,
        WrapContents = false,
    };

    public static Label MakeStatusBar() => new()
    {
        AutoSize = false,
        BackColor = Surface,
        Dock = DockStyle.Bottom,
        Font = SmallFont,
        ForeColor = Muted,
        Height = StatusBarHeight,
        Padding = StatusBarPadding,
    };

    public static Label DividerLabel(string text) => new()
    {
        AutoSize = false,
        BackColor = Raised,
        Font = HeadingFont,
        ForeColor = Text,
        Height = SectionHeaderHeight,
        Margin = new Padding(0, SectionGap, 0, FieldGap),
        Padding = SectionHeaderPadding,
        Text = $"-- {text} --",
        TextAlign = ContentAlignment.MiddleLeft,
        Width = 240,
    };

    public static ToolStripButton ModeButton(string text, string tooltip, Action onClick)
    {
        ToolStripButton button = ToolButton(text, tooltip, onClick, toggle: true);
        button.AutoSize = false;
        button.Height = FieldHeight + 10;
        button.TextAlign = ContentAlignment.MiddleLeft;
        button.Width = 84;
        button.AccessibleName = text + " mode";
        return button;
    }

    public static Label SectionLabel(string text) => new()
    {
        AutoSize = false,
        BackColor = Color.Transparent,
        Dock = DockStyle.Top,
        Font = HeadingFont,
        ForeColor = Muted,
        Height = SectionHeaderHeight,
        Padding = SectionHeaderPadding,
        Text = text.ToUpperInvariant(),
    };

    /// <summary>
    /// A caption with a hairline rule beneath it, for dividing a panel into named regions.
    /// </summary>
    /// <remarks>
    /// <see cref="SectionLabel"/> is a bare caption floating in the panel background, which reads as
    /// a stray piece of text rather than the start of a region. The rule is what makes it a heading:
    /// it terminates whatever came before and visually owns everything under it.
    /// </remarks>
    public static Control SectionHeader(string text)
    {
        Panel host = new() { BackColor = Surface, Dock = DockStyle.Top, Height = SectionHeaderHeight + 1 };
        host.Paint += (_, e) =>
        {
            using SolidBrush caption = new(Muted);
            using Font font = new(SmallFont, FontStyle.Bold);
            e.Graphics.DrawString(text.ToUpperInvariant(), font, caption, 12f, 9f);
            using Pen rule = new(Color.FromArgb(80, Border));
            e.Graphics.DrawLine(rule, 0, host.Height - 1, host.Width, host.Height - 1);
        };
        return host;
    }

    public static Panel SidePanel(int width, DockStyle dock)
    {
        return new Panel
        {
            BackColor = Surface,
            Dock = dock,
            Width = width,
        };
    }

    /// <summary>Applies the editor theme to an ordinary WinForms input control.</summary>
    /// <remarks>
    /// Text and numeric fields use <see cref="BorderStyle.None"/>, not <c>FixedSingle</c>. WinForms
    /// draws a <c>FixedSingle</c> border with a system colour that ignores the palette entirely, so
    /// against the dark editor surfaces every field was ringed in bright white — visible in the Room
    /// Editor's grid box and both side panels. The field is distinguished from the panel by its
    /// <see cref="Raised"/> fill instead, which is how the rest of the chrome already separates
    /// layers and needs no border to read as an input.
    /// </remarks>
    public static void StyleField(Control control)
    {
        control.BackColor = Raised;
        control.ForeColor = Text;
        control.Font = BaseFont;
        switch (control)
        {
            case TextBoxBase box:
                box.BorderStyle = BorderStyle.None;
                break;
            case NumericUpDown numeric:
                numeric.BorderStyle = BorderStyle.None;
                break;
            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                break;
            case ListView list:
                list.BorderStyle = BorderStyle.None;
                break;
            case ListBox listBox:
                listBox.BorderStyle = BorderStyle.None;
                break;
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = Hover;
                break;
        }
    }

    public static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(
            255,
            Convert.ToInt32(hex.Substring(0, 2), 16),
            Convert.ToInt32(hex.Substring(2, 2), 16),
            Convert.ToInt32(hex.Substring(4, 2), 16));
    }

    private sealed class ChromeColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Surface;
        public override Color MenuStripGradientEnd => Surface;
        public override Color ToolStripGradientBegin => Surface;
        public override Color ToolStripGradientMiddle => Surface;
        public override Color ToolStripGradientEnd => Surface;
        public override Color ToolStripBorder => Border;
        public override Color MenuItemSelected => Hover;
        public override Color ButtonSelectedBorder => Accent;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonPressedGradientBegin => Accent;
        public override Color ButtonPressedGradientEnd => Accent;
        public override Color ButtonCheckedGradientBegin => Accent;
        public override Color ButtonCheckedGradientEnd => Accent;
        public override Color ButtonCheckedHighlight => Accent;
        public override Color ToolStripDropDownBackground => Raised;
        public override Color ImageMarginGradientBegin => Raised;
        public override Color ImageMarginGradientMiddle => Raised;
        public override Color ImageMarginGradientEnd => Raised;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
