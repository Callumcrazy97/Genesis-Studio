using System.Windows.Forms;
using Genesis.Application.Core.Editing;
using Genesis.Application.Editors;

namespace Genesis.Application.Studio.Editing;

/// <summary>
/// Decides who gets Ctrl+C/X/V/Z/Y, Delete, Ctrl+A and F2: whatever currently has the focus.
/// </summary>
/// <remarks>
/// Menu accelerators are resolved before the focused control is offered the key, so hanging these
/// shortcuts off the Edit menu gave the resource browser every one of them, everywhere. Copying
/// code copied the selected asset instead; pressing Delete with an image open moved a file to the
/// project trash. The menu still lists the shortcuts — it just no longer claims them — and the
/// order below is the order a person would expect:
/// <list type="number">
/// <item><b>Text entry</b> — hands the key straight back to Windows. A text box's own editing is
/// the correct behaviour and nothing here can improve on it.</item>
/// <item><b>An <see cref="IEditCommandTarget"/> under the focus</b> — a surface that has said these
/// keys are its own, searched from the focused control outwards.</item>
/// <item><b>The active document</b> — for Undo/Redo, which are document-wide by nature.</item>
/// <item><b>The Assets tree</b> — and only when the tree genuinely has the focus, which is what
/// makes Delete safe.</item>
/// </list>
/// </remarks>
public static class EditCommandRouter
{
    /// <summary>What a key combination means, or null when it is not an editing shortcut.</summary>
    public static EditCommand? Map(Keys keyData) => keyData switch
    {
        Keys.Control | Keys.Z => EditCommand.Undo,
        Keys.Control | Keys.Y => EditCommand.Redo,
        Keys.Control | Keys.Shift | Keys.Z => EditCommand.Redo,
        Keys.Control | Keys.X => EditCommand.Cut,
        Keys.Control | Keys.C => EditCommand.Copy,
        Keys.Control | Keys.V => EditCommand.Paste,
        Keys.Control | Keys.A => EditCommand.SelectAll,
        Keys.Delete => EditCommand.Delete,
        _ => null,
    };

    /// <summary>The control that actually has the keyboard focus, or null.</summary>
    /// <remarks>
    /// Deferred to <see cref="EditorInputGuard"/> so the shell and the editors cannot disagree about
    /// whether the user is typing. The editors need the same answer for their own shortcuts and
    /// cannot reference Studio, so the shared copy lives down there and this forwards to it.
    /// </remarks>
    public static Control? FocusedControl() => EditorInputGuard.FocusedControl();

    /// <summary>True when the focus is somewhere that edits text by itself.</summary>
    public static bool IsTextEntry(Control? focused) => EditorInputGuard.IsTextEntry(focused);

    /// <summary>
    /// The nearest surface, from the focused control outwards, that claims the command.
    /// </summary>
    public static IEditCommandTarget? FindTarget(Control? focused, EditCommand command)
    {
        for (Control? control = focused; control is not null; control = control.Parent)
        {
            if (control is IEditCommandTarget target && target.CanEdit(command))
            {
                return target;
            }
        }

        return null;
    }
}
