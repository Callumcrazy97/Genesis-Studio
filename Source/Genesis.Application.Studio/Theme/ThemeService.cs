using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Settings;
using WeifenLuo.WinFormsUI.Docking;
using WinFormsApplication = System.Windows.Forms.Application;

namespace Genesis.Application.Studio.Theme;

public static class ThemeService
{
    static ThemeService() => Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.ApplyTheme = Apply;
    internal const string BorderlessCanvasTextTag = "borderless-canvas-text";
    internal const string DenseToolStripTag = "dense-tool-strip";

    private static Font _interfaceFont =
        new("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);

    private static Font _headingFont =
        new("Segoe UI Variable Display", 16f, FontStyle.Bold, GraphicsUnit.Point);

    private static Font _codeFont =
        new("Cascadia Code", 10f, FontStyle.Regular, GraphicsUnit.Point);

    public static ThemePalette Palette { get; private set; } = ThemePalette.Dark;

    public static Font InterfaceFont => _interfaceFont;

    public static Font HeadingFont => _headingFont;

    public static Font CodeFont => _codeFont;

    public static float InterfaceScale { get; private set; } = 1f;

    public static string Density { get; private set; } = "Comfortable";

    public static bool UseAnimations { get; private set; } = true;

    public static event EventHandler? ThemeChanged;

    public static void SetPalette(ThemePalette palette)
    {
        Palette = palette ?? throw new ArgumentNullException(nameof(palette));
    }

    /// <summary>Applies appearance settings that can take effect without restarting Studio.</summary>
    public static void ApplySettings(GenesisSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppearanceSettings appearance = settings.Appearance;

        // The image, if this theme has one, is what supplies the palette — so it is resolved first
        // and its derived colours become the palette rather than a lookup in the colour catalogue.
        string mode = ThemeCatalog.ResolveMode(appearance.ThemeMode, appearance.Theme);
        ThemeImage? backdrop = string.Equals(
            mode, AppearanceThemeModes.Image, StringComparison.Ordinal)
            ? ThemeCatalog.FindSelectableImage(appearance.Theme)
            : null;
        SetPalette(backdrop?.Palette ?? ThemeCatalog.GetColour(appearance.Theme));
        ThemeBackdrop.Use(backdrop);

        InterfaceScale = Math.Clamp(appearance.InterfaceScale, 0.75f, 2f);
        Density = string.IsNullOrWhiteSpace(appearance.Density) ? "Comfortable" : appearance.Density;
        UseAnimations = appearance.UseAnimations;

        float interfaceSize = Math.Clamp(9.5f * InterfaceScale, 8f, 18f);
        float headingSize = Math.Clamp(16f * InterfaceScale, 12f, 28f);
        int codeSize = Math.Clamp(appearance.CodeFontSize, 8, 24);

        ReplaceFont(ref _interfaceFont, new Font("Segoe UI Variable Text", interfaceSize, FontStyle.Regular, GraphicsUnit.Point));
        ReplaceFont(ref _headingFont, new Font("Segoe UI Variable Display", headingSize, FontStyle.Bold, GraphicsUnit.Point));
        ReplaceFont(ref _codeFont, new Font("Cascadia Code", codeSize, FontStyle.Regular, GraphicsUnit.Point));

        try
        {
            WinFormsApplication.SetDefaultFont(InterfaceFont);
        }
        catch (InvalidOperationException)
        {
            // Default font can only be set before the first window in some hosts.
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static ThemeBase CreateDockTheme() =>
        GenesisDockTheme.Create(Palette);

    public static void ApplyToOpenForms()
    {
        foreach (Form form in WinFormsApplication.OpenForms.Cast<Form>().ToArray())
        {
            if (form.IsDisposed)
            {
                continue;
            }

            Apply(form);
            if (form.MainMenuStrip is not null)
            {
                form.MainMenuStrip.Renderer = CreateToolStripRenderer();
            }

            foreach (Control child in form.Controls)
            {
                if (child is ToolStrip strip)
                {
                    strip.Renderer = CreateToolStripRenderer();
                }
            }
        }
    }

    public static void Apply(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        ApplyControl(root);

        // DockPanel owns a deep internal control tree; walking it during theme apply
        // can re-enter layout and hang the STA thread.
        if (root is DockPanel)
        {
            return;
        }

        foreach (Control child in root.Controls)
        {
            Apply(child);
        }
    }

    public static GenesisToolStripRenderer CreateToolStripRenderer() =>
        new(Palette);

    public static int TreeItemHeight => Density switch
    {
        "Compact" => 22,
        "Spacious" => 32,
        _ => 27,
    };

    private static void ReplaceFont(ref Font target, Font next)
    {
        // Keep prior Font instances alive — live controls may still reference them.
        target = next;
    }

    private static void ApplyControl(Control control)
    {
        switch (control)
        {
            case Form:
                control.BackColor = Palette.Canvas;
                control.ForeColor = Palette.Text;
                control.Font = InterfaceFont;
                break;
            case MenuStrip or ToolStrip or StatusStrip:
                control.BackColor = Palette.Surface;
                control.Font = InterfaceFont;
                if (control is ToolStrip
                    && string.Equals(
                        control.Tag as string,
                        DenseToolStripTag,
                        StringComparison.OrdinalIgnoreCase))
                {
                    control.Padding = Density switch
                    {
                        "Compact" => new Padding(6, 1, 6, 1),
                        "Spacious" => new Padding(8, 4, 8, 4),
                        _ => new Padding(6, 2, 6, 2),
                    };
                }
                break;
            case TextBoxBase textBox:
                bool borderlessCanvas = string.Equals(
                    textBox.Tag as string,
                    BorderlessCanvasTextTag,
                    StringComparison.OrdinalIgnoreCase);
                textBox.BackColor = borderlessCanvas ? Palette.Canvas : Palette.SurfaceRaised;
                textBox.ForeColor = Palette.Text;
                // Never FixedSingle: WinForms paints that border with a system colour that ignores
                // the palette, so every field read as a bright white box against the dark chrome.
                // The SurfaceRaised fill is what separates a field from its panel.
                textBox.BorderStyle = BorderStyle.None;
                textBox.Font = textBox is RichTextBox ? CodeFont : InterfaceFont;
                break;
            case TreeView tree:
                tree.BackColor = Palette.Surface;
                tree.ForeColor = Palette.Text;
                tree.BorderStyle = BorderStyle.None;
                tree.LineColor = Palette.BorderStrong;
                tree.ItemHeight = DpiLayout.Scale(tree, TreeItemHeight);
                tree.Font = InterfaceFont;
                break;
            case ListBox list:
                list.BackColor = Palette.Surface;
                list.ForeColor = Palette.Text;
                list.BorderStyle = BorderStyle.None;
                list.Font = InterfaceFont;
                break;
            case ListView listView:
                listView.BackColor = Palette.Surface;
                listView.ForeColor = Palette.Text;
                listView.BorderStyle = BorderStyle.None;
                listView.Font = InterfaceFont;
                break;
            case ComboBox combo:
                combo.BackColor = Palette.SurfaceRaised;
                combo.ForeColor = Palette.Text;
                combo.FlatStyle = FlatStyle.Flat;
                combo.Font = InterfaceFont;
                break;
            case NumericUpDown numeric:
                numeric.BackColor = Palette.SurfaceRaised;
                numeric.ForeColor = Palette.Text;
                numeric.BorderStyle = BorderStyle.None;
                numeric.Font = InterfaceFont;
                break;
            case CheckBox:
            case RadioButton:
                control.BackColor = Color.Transparent;
                control.ForeColor = Palette.Text;
                control.Font = InterfaceFont;
                break;
            case Label:
                control.ForeColor = Palette.Text;
                break;
            case Button button:
                button.BackColor = Palette.SurfaceRaised;
                button.ForeColor = Palette.Text;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Palette.Border;
                button.FlatAppearance.MouseOverBackColor = Palette.SurfaceHover;
                button.FlatAppearance.MouseDownBackColor = Palette.Accent;
                button.Font = InterfaceFont;
                break;
            case SplitContainer split:
                split.BackColor = Palette.Border;
                split.Panel1.BackColor = Palette.Canvas;
                split.Panel2.BackColor = Palette.Canvas;
                break;
            case DockPanel dock:
                dock.BackColor = Palette.Canvas;
                break;
            case Panel or FlowLayoutPanel or TableLayoutPanel or UserControl or TabPage:
                ApplyPanelSurface(control);
                break;
            default:
                break;
        }
    }

    private static void ApplyPanelSurface(Control control)
    {
        string? tag = control.Tag as string;
        if (string.Equals(tag, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            control.BackColor = Color.Transparent;
            return;
        }

        if (string.Equals(tag, "surface", StringComparison.OrdinalIgnoreCase))
        {
            control.BackColor = Palette.Surface;
        }
        else if (string.Equals(tag, "canvas", StringComparison.OrdinalIgnoreCase) ||
                 control.Parent is Form or DockContent)
        {
            control.BackColor = Palette.Canvas;
        }
        else
        {
            control.BackColor = Palette.Surface;
        }

        control.ForeColor = Palette.Text;
    }
}

public sealed class GenesisColorTable(ThemePalette palette) : ProfessionalColorTable
{
    public override Color MenuStripGradientBegin => palette.Surface;
    public override Color MenuStripGradientEnd => palette.Surface;
    public override Color ToolStripGradientBegin => palette.Surface;
    public override Color ToolStripGradientMiddle => palette.Surface;
    public override Color ToolStripGradientEnd => palette.Surface;
    public override Color ToolStripBorder => palette.Border;
    public override Color MenuItemSelected => palette.SurfaceHover;
    public override Color MenuItemSelectedGradientBegin => palette.SurfaceHover;
    public override Color MenuItemSelectedGradientEnd => palette.SurfaceHover;
    public override Color MenuItemPressedGradientBegin => palette.SurfaceRaised;
    public override Color MenuItemPressedGradientEnd => palette.SurfaceRaised;
    public override Color ToolStripDropDownBackground => palette.SurfaceRaised;
    public override Color ImageMarginGradientBegin => palette.SurfaceRaised;
    public override Color ImageMarginGradientMiddle => palette.SurfaceRaised;
    public override Color ImageMarginGradientEnd => palette.SurfaceRaised;
    public override Color SeparatorDark => palette.Border;
    public override Color SeparatorLight => palette.BorderStrong;
    public override Color ButtonSelectedBorder => palette.Accent;
    public override Color ButtonSelectedGradientBegin => palette.SurfaceHover;
    public override Color ButtonSelectedGradientEnd => palette.SurfaceHover;
    public override Color ButtonPressedGradientBegin => palette.Accent;
    public override Color ButtonPressedGradientEnd => palette.Accent;
}

public sealed class GenesisToolStripRenderer(ThemePalette palette)
    : ToolStripProfessionalRenderer(new GenesisColorTable(palette))
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? palette.Text : palette.TextMuted;
        base.OnRenderItemText(e);
    }

    /// <summary>
    /// Continues the theme picture across the menu and tool bands instead of capping the window
    /// with a flat grey slab.
    /// </summary>
    /// <remarks>
    /// Behind a heavy scrim: menu text sits directly on this, and a menu you have to squint at is a
    /// bad trade for a prettier title area. Drop-downs are excluded outright — they open over
    /// arbitrary content and need to read as solid, opaque surfaces to be legible at all.
    /// </remarks>
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown
            || !ThemeBackdrop.Paint(e.Graphics, e.ToolStrip, ThemeBackdrop.TextScrim))
        {
            base.OnRenderToolStripBackground(e);
        }
    }
}
