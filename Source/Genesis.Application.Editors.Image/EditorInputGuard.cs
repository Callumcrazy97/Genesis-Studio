using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Editors;

/// <summary>
/// Answers the one question every editor's key handling has to ask first: is the person typing?
/// </summary>
/// <remarks>
/// An editor surface overriding <see cref="Control.ProcessCmdKey"/> sees keys *before* the focused
/// control does, and before the shell does — <c>ProcessCmdKey</c> bubbles up from the focused
/// control, so the innermost editor wins. That is right for its own shortcuts and catastrophic for
/// everything else: the Room Editor bound bare Q/W/E/R/G/F to tools and Delete to "delete the
/// selected instances", so renaming a node in its outliner, or typing a grid size, switched tools
/// on every letter and deleted the selection on Delete.
///
/// Every editor consults this before consuming a key. The rule is simple and the same everywhere:
/// <b>while a text field has the focus, the text field owns the keyboard.</b> Shortcuts that need
/// to work even while typing (Ctrl+S, F7 compile) are free to check this and proceed anyway.
///
/// It lives here, in the lowest editor assembly, because the Suite references the Image editors and
/// the two have no other shared WinForms home — Core is deliberately WinForms-free.
/// </remarks>
public static class EditorInputGuard
{
    [ThreadStatic] private static CommandFocusScope? _commandFocus;

    /// <summary>
    /// Evaluate an explicit menu/palette command against its original editing control instead of
    /// the search box/menu that temporarily owns native focus. Use only for synchronous command
    /// validation/execution; never retain the scope across await or a modal message loop.
    /// Ordinary keyboard routing never creates a scope and continues to query Windows.
    /// </summary>
    public static IDisposable UseCommandFocus(Control? control) => new CommandFocusScope(control);

    private sealed class CommandFocusScope : IDisposable
    {
        private readonly CommandFocusScope? _previous;
        private bool _disposed;
        public CommandFocusScope(Control? control)
        {
            Control = control;
            _previous = _commandFocus;
            _commandFocus = this;
        }
        public Control? Control { get; }
        public void Dispose()
        {
            if (_disposed) return;
            if (!ReferenceEquals(_commandFocus, this))
                throw new InvalidOperationException("Command focus scopes must be disposed in reverse order on their UI thread.");
            _commandFocus = _previous;
            _disposed = true;
        }
    }

    /// <summary>True when the keyboard focus is in something that edits text.</summary>
    public static bool IsTextEntryFocused() => IsTextEntry(FocusedControl());

    /// <summary>The control that actually holds the keyboard focus, or null.</summary>
    /// <remarks>
    /// Asked of the window manager rather than walked through <c>ActiveControl</c>: that chain
    /// breaks at the first plain <see cref="Panel"/>, and editors nest their inputs several panels
    /// deep inside toolbars, docks and split containers.
    /// </remarks>
    public static Control? FocusedControl()
    {
        if (_commandFocus is { } command)
            return command.Control is { IsDisposed: false } control ? control : null;
        IntPtr handle = GetFocus();
        if (handle == IntPtr.Zero) return null;
        return Control.FromHandle(handle) ?? Control.FromChildHandle(handle);
    }

    /// <summary>True when the control edits text by itself.</summary>
    public static bool IsTextEntry(Control? control) =>
        control switch
        {
            null => false,
            TextBoxBase => true,
            ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList } => true,
            UpDownBase => true,
            // A NumericUpDown's editable part is an inner TextBox whose parent is the spinner.
            _ => control.Parent is UpDownBase,
        };

    /// <summary>
    /// True while a tree or list is renaming in place, where Delete and letters are the label's.
    /// </summary>
    public static bool IsLabelEditing(Control? control) =>
        control is TreeView { LabelEdit: true } or ListView { LabelEdit: true };

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
}
