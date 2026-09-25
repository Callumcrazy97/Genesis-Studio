using System.Windows.Forms;
using Genesis.Application.Core.Editing;

namespace Genesis.Application.Studio.Editing;

/// <summary>Explicit menu/palette actions on text. Keyboard editing stays with Windows.</summary>
internal static class TextCommandSupport
{
    public static bool CanExecute(Control? control, EditCommand command)
    {
        if (control is TextBoxBase text)
            return command switch
            {
                EditCommand.Copy => text.SelectionLength > 0,
                EditCommand.SelectAll => text.TextLength > 0,
                EditCommand.Cut => !text.ReadOnly && text.SelectionLength > 0,
                EditCommand.Paste => !text.ReadOnly,
                EditCommand.Delete => !text.ReadOnly && (text.SelectionLength > 0 || text.SelectionStart < text.TextLength),
                EditCommand.Undo => !text.ReadOnly && text.CanUndo,
                EditCommand.Redo => !text.ReadOnly && text is RichTextBox { CanRedo: true },
                _ => false,
            };
        if (control is ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList } combo)
            return command switch
            {
                EditCommand.Copy or EditCommand.Cut => combo.SelectionLength > 0,
                EditCommand.SelectAll => combo.Text.Length > 0,
                EditCommand.Paste => true,
                EditCommand.Delete => combo.SelectionLength > 0 || combo.SelectionStart < combo.Text.Length,
                _ => false,
            };
        return false;
    }

    public static void Execute(Control? control, EditCommand command)
    {
        if (!CanExecute(control, command)) throw new InvalidOperationException("This text edit is no longer available.");
        if (control is TextBoxBase text)
        {
            switch (command)
            {
                case EditCommand.Undo: text.Undo(); break;
                case EditCommand.Redo: ((RichTextBox)text).Redo(); break;
                case EditCommand.Copy: text.Copy(); break;
                case EditCommand.Cut: text.Cut(); break;
                case EditCommand.Paste: text.Paste(); break;
                case EditCommand.SelectAll: text.SelectAll(); break;
                case EditCommand.Delete:
                    if (text.SelectionLength == 0) text.SelectionLength = 1;
                    text.SelectedText = string.Empty;
                    break;
            }
        }
        else if (control is ComboBox combo)
        {
            switch (command)
            {
                case EditCommand.Copy: Clipboard.SetText(combo.SelectedText); break;
                case EditCommand.Cut: Clipboard.SetText(combo.SelectedText); combo.SelectedText = string.Empty; break;
                case EditCommand.Paste: if (Clipboard.ContainsText()) combo.SelectedText = Clipboard.GetText(); break;
                case EditCommand.SelectAll: combo.SelectAll(); break;
                case EditCommand.Delete:
                    if (combo.SelectionLength == 0) combo.SelectionLength = 1;
                    combo.SelectedText = string.Empty;
                    break;
            }
        }
    }
}
