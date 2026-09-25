using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Core.UI;

/// <summary>
/// A code-built WinForms window whose coordinates are authored at the standard 96-DPI baseline.
/// </summary>
/// <remarks>
/// WinForms designer files persist <c>AutoScaleDimensions</c>, but Genesis builds its windows in
/// code. Without an explicit design baseline, WinForms records the monitor's current DPI as both
/// the design and runtime DPI. Point-sized text then grows while every pixel-sized row, rail and
/// inspector remains unchanged. Applying the baseline at load time (after derived constructors
/// have populated the complete control tree) lets WinForms scale all of that geometry together.
/// </remarks>
public class DpiAwareForm : Form
{
    private bool _initialDpiScaleApplied;

    public DpiAwareForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    protected override void OnLoad(EventArgs e)
    {
        ApplyInitialDpiScale();
        DpiLayout.ConstrainInitialSize(this);
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        DpiLayout.ConstrainToWorkingArea(this);
    }

    protected override void OnShown(EventArgs e)
    {
        // Constraining an oversized preferred window during OnLoad changes its final client area.
        // Force docking/table layout to consume that final area before page-specific Shown
        // handlers calculate responsive columns. Without this pass, a 200%-DPI Project Hub could
        // retain its pre-constraint content width and draw the third action card off-window.
        PerformLayout();
        base.OnShown(e);
        PerformLayout();
    }

    private void ApplyInitialDpiScale()
    {
        if (_initialDpiScaleApplied)
        {
            return;
        }

        _initialDpiScaleApplied = true;
        DpiLayout.ApplyDesignBaseline(this);
    }
}

/// <summary>Shared DPI and working-area measurements for code-built application UI.</summary>
public static class DpiLayout
{
    public const int DesignDpi = 96;
    private const int LogicalScreenMargin = 24;

    public static SizeF DesignDimensions { get; } = new(DesignDpi, DesignDpi);

    public static int Scale(Control control, int logicalPixels) =>
        Scale(logicalPixels, control.DeviceDpi);

    public static int Scale(int logicalPixels, int dpi)
    {
        int safeDpi = dpi > 0 ? dpi : DesignDpi;
        return (int)Math.Round(logicalPixels * (safeDpi / (float)DesignDpi));
    }

    /// <summary>Scales a completed code-built control tree from its authored 96-DPI geometry.</summary>
    public static void ApplyDesignBaseline(ContainerControl root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.SuspendLayout();
        root.AutoScaleMode = AutoScaleMode.Dpi;
        root.AutoScaleDimensions = DesignDimensions;
        root.ResumeLayout(performLayout: true);
    }

    /// <summary>
    /// Keeps a preferred window inside the current display without imposing a permanent maximum;
    /// the user can still maximize it and every docked/anchored surface receives the extra space.
    /// </summary>
    public static void ConstrainInitialSize(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        Rectangle safeArea = SafeWorkingArea(form);
        ConstrainSize(form, safeArea);

        if (form.StartPosition is not (FormStartPosition.CenterScreen or FormStartPosition.CenterParent))
        {
            return;
        }

        Rectangle target = form.StartPosition == FormStartPosition.CenterParent && form.Owner is not null
            ? form.Owner.Bounds
            : safeArea;
        int left = target.Left + (target.Width - form.Width) / 2;
        int top = target.Top + (target.Height - form.Height) / 2;
        left = Math.Clamp(left, safeArea.Left, Math.Max(safeArea.Left, safeArea.Right - form.Width));
        top = Math.Clamp(top, safeArea.Top, Math.Max(safeArea.Top, safeArea.Bottom - form.Height));

        // Windows chooses the centred location from the pre-autoscale Size before OnLoad. Once the
        // 96-DPI design tree has expanded, retaining that old location pushes the right/bottom of
        // the window off-screen. Commit the corrected bounds as a manual start position.
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(left, top);
    }

    /// <summary>Keeps a window usable after it crosses to a smaller or differently scaled display.</summary>
    public static void ConstrainToWorkingArea(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.WindowState != FormWindowState.Normal)
        {
            return;
        }

        Rectangle safeArea = SafeWorkingArea(form);
        ConstrainSize(form, safeArea);

        int left = Math.Clamp(form.Left, safeArea.Left, Math.Max(safeArea.Left, safeArea.Right - form.Width));
        int top = Math.Clamp(form.Top, safeArea.Top, Math.Max(safeArea.Top, safeArea.Bottom - form.Height));
        form.Location = new Point(left, top);
    }

    private static void ConstrainSize(Form form, Rectangle? knownSafeArea = null)
    {
        if (form.WindowState != FormWindowState.Normal)
        {
            return;
        }

        Rectangle safeArea = knownSafeArea ?? SafeWorkingArea(form);
        if (safeArea.Width <= 0 || safeArea.Height <= 0)
        {
            return;
        }

        Size minimum = form.MinimumSize;
        form.MinimumSize = new Size(
            Math.Min(minimum.Width, safeArea.Width),
            Math.Min(minimum.Height, safeArea.Height));
        form.Size = new Size(
            Math.Min(form.Width, safeArea.Width),
            Math.Min(form.Height, safeArea.Height));
    }

    private static Rectangle SafeWorkingArea(Form form)
    {
        Rectangle workingArea = Screen.FromControl(form).WorkingArea;
        int margin = Scale(LogicalScreenMargin, form.DeviceDpi);
        if (workingArea.Width <= margin * 2 || workingArea.Height <= margin * 2)
        {
            return workingArea;
        }

        return Rectangle.Inflate(workingArea, -margin, -margin);
    }
}
