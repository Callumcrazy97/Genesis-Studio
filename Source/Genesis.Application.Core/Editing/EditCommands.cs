namespace Genesis.Application.Core.Editing;

/// <summary>The standard editing verbs every surface competes for.</summary>
public enum EditCommand
{
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    Delete,
    SelectAll,
}

/// <summary>
/// Implemented by a control that wants Ctrl+C/V/X/Z/Y or Delete when it has the focus.
/// </summary>
/// <remarks>
/// Studio routes these keys to whatever the user is actually looking at, and this is how a surface
/// says "that is mine". Before this existed the shell's Edit menu owned the shortcuts outright:
/// menu accelerators are processed before the focused control ever sees the key, so Ctrl+C copied
/// the selected *resource* while the caret sat in a code editor, and Delete moved a file to the
/// trash while an image selection was live.
///
/// A control that does the standard thing already — every text box, and anything derived from
/// <c>TextBoxBase</c> — needs none of this. The router leaves those keys alone so Windows' own
/// editing behaviour applies. This interface is for surfaces whose editing is their own: an image
/// canvas, a room's instance selection, a timeline.
/// </remarks>
public interface IEditCommandTarget
{
    /// <summary>Whether this surface would do something useful with the command right now.</summary>
    bool CanEdit(EditCommand command);

    /// <summary>Runs the command. Returns false when it turned out not to apply after all.</summary>
    bool TryEdit(EditCommand command);
}
