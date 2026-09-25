using System.Drawing;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Studio.Theme;

/// <summary>
/// Retints DockPanelSuite's VS2015 skins with the live <see cref="ThemePalette"/> so
/// document tabs, tool-window captions, and auto-hide strips match Genesis Dark / Light.
/// </summary>
internal static class GenesisDockTheme
{
    public static ThemeBase Create(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ThemeBase theme = palette.IsDark ? new VS2015DarkTheme() : new VS2015LightTheme();
        Remap(theme, palette);
        return theme;
    }

    public static void Remap(ThemeBase? theme, ThemePalette palette)
    {
        if (theme?.ColorPalette is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(palette);
        DockPanelColorPalette colors = theme.ColorPalette;

        Color canvas = palette.Canvas;
        Color surface = palette.Surface;
        Color raised = palette.SurfaceRaised;
        Color hover = palette.SurfaceHover;
        Color border = palette.Border;
        Color borderStrong = palette.BorderStrong;
        Color text = palette.Text;
        Color muted = palette.TextMuted;
        Color accent = palette.Accent;
        Color accentHover = palette.AccentHover;
        Color onAccent = ContrastingInk(accent);

        colors.MainWindowActive.Background = canvas;
        colors.MainWindowStatusBarDefault.Background = accent;
        colors.MainWindowStatusBarDefault.Text = onAccent;
        colors.MainWindowStatusBarDefault.HighlightText = onAccent;
        colors.MainWindowStatusBarDefault.ResizeGrip = onAccent;

        colors.ToolWindowBorder = border;
        colors.ToolWindowSeparator = border;

        colors.AutoHideStripDefault.Background = surface;
        colors.AutoHideStripDefault.Border = border;
        colors.AutoHideStripDefault.Text = muted;
        colors.AutoHideStripHovered.Background = surface;
        colors.AutoHideStripHovered.Border = accent;
        colors.AutoHideStripHovered.Text = accentHover;

        colors.TabSelectedActive.Background = accent;
        colors.TabSelectedActive.Text = onAccent;
        colors.TabSelectedActive.Button = onAccent;
        colors.TabSelectedInactive.Background = raised;
        colors.TabSelectedInactive.Text = text;
        colors.TabSelectedInactive.Button = muted;
        colors.TabUnselected.Background = surface;
        colors.TabUnselected.Text = muted;
        colors.TabUnselectedHovered.Background = hover;
        colors.TabUnselectedHovered.Text = text;
        colors.TabUnselectedHovered.Button = text;

        colors.TabButtonSelectedActiveHovered.Background = accentHover;
        colors.TabButtonSelectedActiveHovered.Border = accentHover;
        colors.TabButtonSelectedActiveHovered.Glyph = onAccent;
        colors.TabButtonSelectedActivePressed.Background = accent;
        colors.TabButtonSelectedActivePressed.Border = accent;
        colors.TabButtonSelectedActivePressed.Glyph = onAccent;
        colors.TabButtonSelectedInactiveHovered.Background = hover;
        colors.TabButtonSelectedInactiveHovered.Border = hover;
        colors.TabButtonSelectedInactiveHovered.Glyph = text;
        colors.TabButtonSelectedInactivePressed.Background = raised;
        colors.TabButtonSelectedInactivePressed.Border = raised;
        colors.TabButtonSelectedInactivePressed.Glyph = text;
        colors.TabButtonUnselectedTabHoveredButtonHovered.Background = accentHover;
        colors.TabButtonUnselectedTabHoveredButtonHovered.Border = accentHover;
        colors.TabButtonUnselectedTabHoveredButtonHovered.Glyph = onAccent;
        colors.TabButtonUnselectedTabHoveredButtonPressed.Background = accent;
        colors.TabButtonUnselectedTabHoveredButtonPressed.Border = accent;
        colors.TabButtonUnselectedTabHoveredButtonPressed.Glyph = onAccent;

        colors.ToolWindowCaptionActive.Background = accent;
        colors.ToolWindowCaptionActive.Text = onAccent;
        colors.ToolWindowCaptionActive.Button = onAccent;
        colors.ToolWindowCaptionActive.Grip = Blend(accent, onAccent, 0.35f);
        colors.ToolWindowCaptionInactive.Background = surface;
        colors.ToolWindowCaptionInactive.Text = muted;
        colors.ToolWindowCaptionInactive.Button = muted;
        colors.ToolWindowCaptionInactive.Grip = borderStrong;
        colors.ToolWindowCaptionButtonActiveHovered.Background = accentHover;
        colors.ToolWindowCaptionButtonActiveHovered.Border = accentHover;
        colors.ToolWindowCaptionButtonActiveHovered.Glyph = onAccent;
        colors.ToolWindowCaptionButtonInactiveHovered.Background = hover;
        colors.ToolWindowCaptionButtonInactiveHovered.Border = hover;
        colors.ToolWindowCaptionButtonInactiveHovered.Glyph = text;
        colors.ToolWindowCaptionButtonPressed.Background = accent;
        colors.ToolWindowCaptionButtonPressed.Border = accent;
        colors.ToolWindowCaptionButtonPressed.Glyph = onAccent;

        colors.ToolWindowTabSelectedActive.Background = canvas;
        colors.ToolWindowTabSelectedActive.Text = accent;
        colors.ToolWindowTabSelectedInactive.Background = canvas;
        colors.ToolWindowTabSelectedInactive.Text = accent;
        colors.ToolWindowTabUnselected.Background = surface;
        colors.ToolWindowTabUnselected.Text = muted;
        colors.ToolWindowTabUnselectedHovered.Background = hover;
        colors.ToolWindowTabUnselectedHovered.Text = accentHover;

        colors.DockTarget.Background = surface;
        colors.DockTarget.Border = border;
        colors.DockTarget.ButtonBackground = raised;
        colors.DockTarget.ButtonBorder = raised;
        colors.DockTarget.GlyphBackground = raised;
        colors.DockTarget.GlyphBorder = accent;
        colors.DockTarget.GlyphArrow = text;

        colors.CommandBarToolbarDefault.Background = surface;
        colors.CommandBarToolbarDefault.Border = surface;
        colors.CommandBarToolbarDefault.Tray = surface;
        colors.CommandBarToolbarDefault.Grip = borderStrong;
        colors.CommandBarToolbarDefault.Separator = border;
        colors.CommandBarToolbarDefault.SeparatorAccent = borderStrong;
        colors.CommandBarToolbarDefault.OverflowButtonBackground = surface;
        colors.CommandBarToolbarDefault.OverflowButtonGlyph = muted;
        colors.CommandBarToolbarButtonDefault.Arrow = muted;
        colors.CommandBarToolbarButtonHovered.Arrow = accent;
        colors.CommandBarToolbarButtonHovered.Separator = border;
        colors.CommandBarToolbarButtonPressed.Background = accent;
        colors.CommandBarToolbarButtonPressed.Arrow = onAccent;
        colors.CommandBarToolbarButtonPressed.Text = onAccent;
        colors.CommandBarToolbarButtonChecked.Background = raised;
        colors.CommandBarToolbarButtonChecked.Border = accent;
        colors.CommandBarToolbarButtonChecked.Text = text;
        colors.CommandBarToolbarButtonCheckedHovered.Border = accentHover;
        colors.CommandBarToolbarButtonCheckedHovered.Text = text;
        colors.CommandBarToolbarOverflowHovered.Background = Color.FromArgb(0x72, hover);
        colors.CommandBarToolbarOverflowHovered.Glyph = accent;
        colors.CommandBarToolbarOverflowPressed.Background = accent;
        colors.CommandBarToolbarOverflowPressed.Glyph = onAccent;

        colors.CommandBarMenuDefault.Background = surface;
        colors.CommandBarMenuDefault.Text = text;
        colors.CommandBarMenuPopupDefault.BackgroundTop = raised;
        colors.CommandBarMenuPopupDefault.BackgroundBottom = raised;
        colors.CommandBarMenuPopupDefault.Border = border;
        colors.CommandBarMenuPopupDefault.Separator = border;
        colors.CommandBarMenuPopupDefault.IconBackground = raised;
        colors.CommandBarMenuPopupDefault.Arrow = muted;
        colors.CommandBarMenuPopupDefault.Checkmark = muted;
        colors.CommandBarMenuPopupDefault.CheckmarkBackground = surface;
        colors.CommandBarMenuPopupHovered.ItemBackground = hover;
        colors.CommandBarMenuPopupHovered.Text = text;
        colors.CommandBarMenuPopupHovered.Arrow = accent;
        colors.CommandBarMenuPopupHovered.Checkmark = text;
        colors.CommandBarMenuPopupHovered.CheckmarkBackground = raised;
        colors.CommandBarMenuPopupDisabled.Text = muted;
        colors.CommandBarMenuPopupDisabled.Checkmark = muted;
        colors.CommandBarMenuPopupDisabled.CheckmarkBackground = surface;
        colors.CommandBarMenuTopLevelHeaderHovered.Background = hover;
        colors.CommandBarMenuTopLevelHeaderHovered.Border = surface;
        colors.CommandBarMenuTopLevelHeaderHovered.Text = text;

        colors.OverflowButtonDefault.Glyph = text;
        colors.OverflowButtonHovered.Background = hover;
        colors.OverflowButtonHovered.Border = hover;
        colors.OverflowButtonHovered.Glyph = accent;
        colors.OverflowButtonPressed.Background = accent;
        colors.OverflowButtonPressed.Border = accent;
        colors.OverflowButtonPressed.Glyph = onAccent;
    }

    private static Color ContrastingInk(Color fill)
    {
        double luminance = (0.299 * fill.R) + (0.587 * fill.G) + (0.114 * fill.B);
        return luminance < 150 ? Color.White : Color.FromArgb(20, 22, 29);
    }

    private static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(from.A + ((to.A - from.A) * amount)),
            (int)(from.R + ((to.R - from.R) * amount)),
            (int)(from.G + ((to.G - from.G) * amount)),
            (int)(from.B + ((to.B - from.B) * amount)));
    }
}
