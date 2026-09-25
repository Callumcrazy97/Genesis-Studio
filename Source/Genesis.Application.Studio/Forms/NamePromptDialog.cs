using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

public sealed class NamePromptDialog : DpiAwareForm
{
    private readonly TextBox _nameBox;
    private readonly Label _error;

    public NamePromptDialog(string title, string prompt, string initialValue = "")
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 220);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = title;
        BackColor = ThemeService.Palette.Canvas;

        Label promptLabel = new()
        {
            AutoSize = false,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(24, 24),
            Size = new Size(400, 28),
            Text = prompt,
        };
        Controls.Add(promptLabel);

        _nameBox = new TextBox
        {
            Location = new Point(24, 62),
            Size = new Size(410, 32),
            Text = initialValue,
        };
        _nameBox.SelectAll();
        Controls.Add(_nameBox);

        _error = new Label
        {
            AutoSize = false,
            ForeColor = ThemeService.Palette.Error,
            Location = new Point(24, 101),
            Size = new Size(410, 26),
        };
        Controls.Add(_error);

        ModernButton cancel = new()
        {
            DialogResult = DialogResult.Cancel,
            Location = new Point(218, 152),
            Size = new Size(102, 38),
            Text = "Cancel",
        };
        Controls.Add(cancel);

        ModernButton confirm = new()
        {
            Accent = true,
            Location = new Point(330, 152),
            Size = new Size(104, 38),
            Text = "Create",
        };
        confirm.Click += (_, _) => Confirm();
        Controls.Add(confirm);

        AcceptButton = confirm;
        CancelButton = cancel;
        Shown += (_, _) => _nameBox.Focus();
        ThemeService.Apply(this);
    }

    public string Value => _nameBox.Text.Trim();

    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            _error.Text = "Enter a name.";
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
