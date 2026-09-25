using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Image;

/// <summary>
/// Layout and theme tokens for image editor surfaces. Mirrors <c>EditorChrome</c> in the Suite
/// assembly so this project stays independent of the circular Suite reference. Studio pushes the
/// live palette through <see cref="Update"/> on every theme change (same path as Suite editors).
/// </summary>
public static class ImageEditorChrome
{
    public static Color Canvas { get; private set; } = FromHex("#14161D");
    public static Color Surface { get; private set; } = FromHex("#1C1F28");
    public static Color Raised { get; private set; } = FromHex("#252934");
    public static Color Hover { get; private set; } = FromHex("#2E3340");
    public static Color Border { get; private set; } = FromHex("#373D4C");
    public static Color Text { get; private set; } = FromHex("#EEF1F8");
    public static Color Muted { get; private set; } = FromHex("#9DA6BB");
    public static Color Accent { get; private set; } = FromHex("#6C8CFF");
    public static Color SectionTitle { get; private set; } = FromHex("#7DB7FF");
    public static Color Success { get; private set; } = FromHex("#5BCA9A");
    public static Color Warning { get; private set; } = FromHex("#F5B551");
    public static Color Error { get; private set; } = FromHex("#F4626F");
    public static bool IsDark { get; private set; } = true;

    public static Font BaseFont { get; private set; } =
        new("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

    public static Font HeadingFont { get; private set; } =
        new("Segoe UI Variable Text", 8f, FontStyle.Bold, GraphicsUnit.Point);

    public static Font CodeFont { get; private set; } =
        new("Cascadia Code", 10f, FontStyle.Regular, GraphicsUnit.Point);

    public const int CommandBarHeight = 44;
    public const int MenuBarHeight = 28;
    public const int SectionHeaderHeight = 27;
    public const int PanelInset = 12;
    public const int SectionGap = 8;
    public const int SplitterWidth = 3;
    public const int ResponsiveBreakpoint = 1100;
    public const int LeftPanelWidth = 304;
    public const int RightPanelWidth = 312;
    public const int CompactSidePanelWidth = 248;
    public const int SideSectionWidth = 276;
    public const int BottomTimelineHeight = 172;
    public const int MinimumCanvasWidth = 480;

    /// <summary>Raised after <see cref="Update"/> so open Image Viewer/Editor surfaces re-theme.</summary>
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
        // Section titles stay a touch brighter than Accent so headers read as labels, not buttons.
        SectionTitle = Blend(accent, text, 0.35f);
        BaseFont = baseFont;
        CodeFont = codeFont;
        HeadingFont = new Font(
            baseFont.FontFamily,
            MathF.Max(7f, baseFont.SizeInPoints - 1.5f),
            FontStyle.Bold,
            GraphicsUnit.Point);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static Panel MakePanel() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = Surface,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
    };

    public static Label MakeSectionTitle(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        Height = SectionHeaderHeight,
        Padding = new Padding(8, 6, 4, 0),
        UseMnemonic = false,
        BackColor = Raised,
        ForeColor = SectionTitle,
        Font = HeadingFont,
    };

    public static void StyleButton(Button button, bool accent = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = accent ? Accent : Border;
        button.BackColor = accent ? Color.FromArgb(45, Accent.R, Accent.G, Accent.B) : Hover;
        button.ForeColor = Text;
        button.Font = BaseFont;
        button.Height = 28;
        button.Margin = new Padding(0, 0, 6, 0);
    }

    public static ToolStrip MakeCommandStrip()
    {
        ToolStrip strip = new()
        {
            GripStyle = ToolStripGripStyle.Hidden,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = CommandBarHeight,
            BackColor = Raised,
            ForeColor = Text,
            Padding = new Padding(8, 6, 8, 6),
            RenderMode = ToolStripRenderMode.System,
            ImageScalingSize = new Size(16, 16),
        };
        strip.Renderer = new ToolStripProfessionalRenderer(new DarkToolStripColorTable());
        return strip;
    }

    public static ToolStripButton MakeToggle(string text, bool initialChecked = true)
    {
        ToolStripButton button = new(text)
        {
            CheckOnClick = true,
            Checked = initialChecked,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 4, 0),
        };
        return button;
    }

    public static ToolStripButton MakeButton(string text, EventHandler? click = null)
    {
        ToolStripButton button = new(text)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 4, 0),
        };
        if (click != null) button.Click += click;
        return button;
    }

    public static SplitContainer MakeSplit(Orientation orientation, bool fixedSecondPanel = false)
    {
        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            Orientation = orientation,
            SplitterWidth = SplitterWidth,
            BackColor = Canvas,
            Panel1MinSize = 0,
            Panel2MinSize = 0,
            FixedPanel = fixedSecondPanel ? FixedPanel.Panel2 : FixedPanel.None,
        };
        split.Panel1.BackColor = Surface;
        split.Panel2.BackColor = Surface;
        return split;
    }

    public static void PaintEdgeBorder(object? sender, PaintEventArgs e, bool leftEdge)
    {
        if (sender is not Control panel) return;
        using Pen pen = new(Border);
        if (leftEdge)
            e.Graphics.DrawLine(pen, 0, 0, 0, panel.Height);
        else
            e.Graphics.DrawLine(pen, panel.Width - 1, 0, panel.Width - 1, panel.Height);
    }

    private static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.R + ((b.R - a.R) * t)),
            (int)(a.G + ((b.G - a.G) * t)),
            (int)(a.B + ((b.B - a.B) * t)));
    }

    private static Color FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Color.FromArgb(
            Convert.ToInt32(hex[..2], 16),
            Convert.ToInt32(hex.Substring(2, 2), 16),
            Convert.ToInt32(hex.Substring(4, 2), 16));
    }

    private sealed class DarkToolStripColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Raised;
        public override Color ToolStripGradientMiddle => Raised;
        public override Color ToolStripGradientEnd => Raised;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuStripGradientBegin => Raised;
        public override Color MenuStripGradientEnd => Raised;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
    }
}
