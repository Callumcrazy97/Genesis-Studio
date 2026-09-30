using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Core.UI;

/// <summary>Matches the Windows-drawn title bar to the application theme.</summary>
/// <remarks>
/// Windows draws every caption bar light unless a window asks otherwise, so each dark Genesis
/// window and dialog carried a bright white strip across its top. Windows 10 20H1 and later
/// honour <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> (20); earlier Windows 10 builds used 19. Older
/// systems reject both and keep the default caption, which is harmless.
/// </remarks>
public static class WindowChrome
{
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeBefore20H1 = 19;

    /// <summary>True when newly created windows should use a dark caption.</summary>
    public static bool DarkTitleBars { get; private set; } = true;

    /// <summary>Applies the current caption mode to a window with a handle.</summary>
    public static void Apply(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsHandleCreated && !form.IsDisposed)
        {
            SetDark(form.Handle, DarkTitleBars);
        }
    }

    /// <summary>Changes the caption mode and updates every open window.</summary>
    public static void SetDarkTitleBars(bool dark)
    {
        DarkTitleBars = dark;
        foreach (Form form in System.Windows.Forms.Application.OpenForms.Cast<Form>().ToArray())
        {
            Apply(form);
        }
    }

    private static void SetDark(IntPtr handle, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        int value = dark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(handle, ImmersiveDarkModeBefore20H1, ref value, sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // Desktop composition is unavailable (for example some server cores); keep the default.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
