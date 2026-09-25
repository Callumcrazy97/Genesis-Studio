using Genesis.Application.Editors.Image.Controls;

namespace Genesis.Application.Editors.Image.Dialogs;

internal static class ImageCanvasToolUi
{
    public static FlowLayoutPanel Fields(int width) => new() { Dock = DockStyle.Fill, Width = width, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12), BackColor = ImageEditorChrome.Surface };
    public static Label Label(string text, int height = 24) => new() { Text = text, Width = 212, Height = height, ForeColor = ImageEditorChrome.Text, Margin = new Padding(0,6,0,2) };
    public static NumericUpDown Number(string name, int minimum, int maximum, int value) => new() { Name = name, AccessibleName = name, Minimum = minimum, Maximum = maximum, Value = Math.Clamp(value,minimum,maximum), Width = 200, Height = 28, BackColor = ImageEditorChrome.Raised, ForeColor = ImageEditorChrome.Text };
    public static Button Button(string text, Action? click = null)
    {
        var button = new Button { Text = text, AccessibleName = text, AutoSize = true, MinimumSize = new Size(90,30), FlatStyle = FlatStyle.Flat, BackColor = ImageEditorChrome.Raised, ForeColor = ImageEditorChrome.Text, Margin = new Padding(0,6,8,2) };
        if (click != null) button.Click += (_,_) => click(); return button;
    }
    public static Control Preview(string title, Control content)
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = ImageEditorChrome.Canvas };
        content.Dock = DockStyle.Fill;
        panel.Controls.Add(content);
        panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 30, ForeColor = ImageEditorChrome.Text, Font = new Font(SystemFonts.MessageBoxFont!,FontStyle.Bold) });
        return panel;
    }
}
