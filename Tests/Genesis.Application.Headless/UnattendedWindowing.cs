using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Headless;

/// <summary>
/// Keeps WinForms surfaces used by the build gate off-screen and out of the foreground so
/// unattended verification does not interrupt gaming or other desktop work.
/// </summary>
internal static class UnattendedWindowing
{
    private const int SwShownoactivate = 4;
    private static readonly Point OffScreenOrigin = new(-12_000, -12_000);

    /// <summary>When set, test hosts may appear on-screen for local visual debugging.</summary>
    public static bool PreferForeground =>
        string.Equals(
            Environment.GetEnvironmentVariable("GENESIS_TESTS_FOREGROUND"),
            "1",
            StringComparison.Ordinal);

    public static HeadlessHostForm NewHost(int width = 1360, int height = 860) =>
        new(width, height);

    public static void Configure(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.StartPosition = FormStartPosition.Manual;
        form.ShowInTaskbar = false;
        form.Location = PreferForeground ? new Point(40, 40) : OffScreenOrigin;
    }

    public static void ShowWithoutFocus(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        Configure(form);

        if (PreferForeground)
        {
            form.Show();
            return;
        }

        if (form is HeadlessHostForm headlessHost)
        {
            headlessHost.Show();
            return;
        }

        if (!form.IsHandleCreated)
        {
            form.CreateControl();
        }

        ShowWindow(form.Handle, SwShownoactivate);
        form.Visible = true;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

/// <summary>Default host for editor and shell regression surfaces.</summary>
internal sealed class HeadlessHostForm : Form
{
    public HeadlessHostForm(int width, int height)
    {
        ClientSize = new Size(width, height);
        BackColor = Color.FromArgb(20, 22, 28);
        UnattendedWindowing.Configure(this);
    }

    protected override bool ShowWithoutActivation => !UnattendedWindowing.PreferForeground;
}
