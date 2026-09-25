using System.Windows.Forms;

namespace Genesis.Application.Studio.Editing;

/// <summary>
/// Gives the Studio palette its reserved shortcut before a child/native editor consumes it.
/// All other keys retain the existing local-first command routing. Modal dialogs and windows
/// belonging to another project are deliberately outside this filter's scope.
/// </summary>
internal sealed class StudioShortcutFilter : IMessageFilter, IDisposable
{
    private const int KeyDown = 0x0100;
    private const int SystemKeyDown = 0x0104;
    private readonly Func<Control, bool> _ownsControl;
    private readonly Action<Control> _showPalette;
    private readonly Func<Keys> _modifiers;
    private bool _disposed;

    internal StudioShortcutFilter(Func<Control, bool> ownsControl, Action<Control> showPalette,
        Func<Keys>? modifiers = null)
    {
        _ownsControl = ownsControl;
        _showPalette = showPalette;
        _modifiers = modifiers ?? (() => Control.ModifierKeys);
        System.Windows.Forms.Application.AddMessageFilter(this);
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (_disposed || message.Msg is not (KeyDown or SystemKeyDown)) return false;
        Keys key = (Keys)(message.WParam.ToInt64() & (long)Keys.KeyCode) | _modifiers();
        if (key != (Keys.Control | Keys.Shift | Keys.P)) return false;
        Control? target = Control.FromChildHandle(message.HWnd);
        if (target is null || target.IsDisposed || target.FindForm()?.Modal == true
            || Form.ActiveForm?.Modal == true || !_ownsControl(target)) return false;
        // Swallow auto-repeat for this reserved command, but never queue several dialogs.
        if ((message.LParam.ToInt64() & (1L << 30)) == 0) _showPalette(target);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        System.Windows.Forms.Application.RemoveMessageFilter(this);
    }
}
