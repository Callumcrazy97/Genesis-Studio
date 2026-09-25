using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Scrollable host that keeps editor chrome colours and requests the dark scrollbar theme on
/// Windows so note/markdown previews do not flash a bright system scrollbar in split view.
/// </summary>
internal sealed class EditorScrollHost : Panel
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string pszSubAppName, string? pszSubIdList);

    public EditorScrollHost(Color backColor)
    {
        AutoScroll = true;
        BackColor = backColor;
        HandleCreated += (_, _) => ApplyDarkScrollTheme();
    }

    public void ApplyDarkScrollTheme() => ApplyDarkScrollTheme(this);

    internal static void ApplyDarkScrollTheme(Control control)
    {
        if (!control.IsHandleCreated || !EditorChrome.IsDark) return;
        try
        {
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch (DllNotFoundException)
        {
            // Non-Windows hosts ignore scroll theming.
        }
    }
}
