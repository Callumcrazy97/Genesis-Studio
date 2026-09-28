using Genesis.Application.Studio.Controls;

namespace Genesis.Application.Studio.Forms;

internal static class ShellDialogLayout
{
    internal static int TextHeight(Control control, int width) => Math.Max(control.Font.Height,
        TextRenderer.MeasureText(control.Text.Length == 0 ? "Ag" : control.Text, control.Font,
            new Size(Math.Max(1, width - control.Padding.Horizontal), int.MaxValue), TextFormatFlags.WordBreak).Height) + control.Padding.Vertical;

    internal static void FitButton(Button button)
    {
        string text = button is ModernButton modern && modern.Glyph.Length > 0 ? modern.Glyph + "  " + button.Text : button.Text;
        button.Size = new Size(Math.Max(90, TextRenderer.MeasureText(text, button.Font).Width + 28),
            Math.Max(34, button.Font.Height + 18));
    }

    internal static void StackLabels(Control panel, int inset, int top, int gap)
    {
        foreach (Label label in panel.Controls.OfType<Label>().OrderBy(label => label.Top).ToArray())
        {
            label.AutoSize = false;
            label.Bounds = new Rectangle(inset, top, Math.Max(1, panel.ClientSize.Width - inset * 2),
                TextHeight(label, Math.Max(1, panel.ClientSize.Width - inset * 2)));
            top = label.Bottom + gap;
        }
    }
}
