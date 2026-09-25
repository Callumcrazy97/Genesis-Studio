using System.Reflection;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Studio.Theme;

/// <summary>
/// Makes an existing control draw the theme backdrop behind whatever it already shows.
/// </summary>
/// <remarks>
/// The shell's large surfaces come from DockPanelSuite rather than Studio-owned controls. Attaching
/// to their paint pipeline keeps the backdrop independent of the docking implementation while
/// allowing every surface to draw the same window-relative slice.
/// </remarks>
public static class BackdropSurface
{
    /// <summary>Paints the backdrop behind a control for as long as it lives.</summary>
    public static void Attach(Control control, double scrim)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (control is DockPanel dockPanel)
        {
            PrepareDockPanel(dockPanel);
        }

        EnableDoubleBuffering(control);
        control.Paint += (_, e) => ThemeBackdrop.Paint(e.Graphics, control, scrim);

        // The backdrop is positioned against the window, so moving or resizing the control changes
        // which part of the picture belongs to it. Without this the image tears on every drag.
        control.Resize += (_, _) => control.Invalidate();
        control.LocationChanged += (_, _) => control.Invalidate();
    }

    /// <summary>Stops DockPanelSuite covering the backdrop after the normal paint event.</summary>
    /// <remarks>
    /// DockPanelSuite raises <see cref="Control.Paint"/> and then fills its own
    /// <see cref="DockPanel.DockBackColor"/> over that result whenever the two background colours
    /// differ. Theme application changes <see cref="Control.BackColor"/>, creating that mismatch
    /// and leaving the document region flat grey. Keeping the values aligned selects the library's
    /// documented background-image path and lets our earlier paint remain visible.
    /// </remarks>
    private static void PrepareDockPanel(DockPanel dockPanel)
    {
        AlignBackgroundColours();
        dockPanel.BackColorChanged += (_, _) => AlignBackgroundColours();
        dockPanel.Paint += (_, _) => AlignBackgroundColours();

        void AlignBackgroundColours()
        {
            if (dockPanel.DockBackColor != dockPanel.BackColor)
            {
                dockPanel.DockBackColor = dockPanel.BackColor;
            }
        }
    }

    /// <summary>Repaints every attached surface on a form, after a theme change.</summary>
    public static void Refresh(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Invalidate(invalidateChildren: true);
    }

    /// <summary>Turns on double buffering for a control that does not expose the setting.</summary>
    /// <remarks>
    /// <see cref="Control.DoubleBuffered"/> is protected, and painting a full-window image into a
    /// single-buffered surface flickers hard on every resize. Reflection is the standard way to
    /// reach it for third-party and framework controls; a failure here is cosmetic, so it is
    /// swallowed deliberately rather than allowed to take out the shell.
    /// </remarks>
    private static void EnableDoubleBuffering(Control control)
    {
        PropertyInfo? property = typeof(Control).GetProperty(
            "DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);

        try
        {
            property?.SetValue(control, true, null);
        }
        catch (TargetInvocationException)
        {
            // A control that refuses the setting still renders, just less smoothly.
        }
        catch (MethodAccessException)
        {
            // A control that blocks non-public access still renders, just less smoothly.
        }
    }
}
