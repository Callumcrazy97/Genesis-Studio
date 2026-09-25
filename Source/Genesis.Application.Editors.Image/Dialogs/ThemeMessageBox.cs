using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Image.Dialogs;

/// <summary>Shared modal dialog for all editors. Studio supplies its active ThemePalette through ApplyTheme.</summary>
public static class ThemeMessageBox
{
    public static Action<Control>? ApplyTheme { get; set; }

    public static DialogResult Show(string text, string caption = "Genesis Studio", MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1) =>
        Show(Form.ActiveForm, text, caption, buttons, icon, defaultButton);

    public static DialogResult Show(IWin32Window? owner, string text, string caption = "Genesis Studio",
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        using Form dialog = Create(text, caption, buttons, icon, defaultButton);
        return owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    /// <summary>Create without showing so layout, keyboard behaviour and theming can be verified.</summary>
    public static Form Create(string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        DialogResult[] results = buttons switch
        {
            MessageBoxButtons.OK => [DialogResult.OK],
            MessageBoxButtons.OKCancel => [DialogResult.OK, DialogResult.Cancel],
            MessageBoxButtons.YesNo => [DialogResult.Yes, DialogResult.No],
            MessageBoxButtons.YesNoCancel => [DialogResult.Yes, DialogResult.No, DialogResult.Cancel],
            MessageBoxButtons.RetryCancel => [DialogResult.Retry, DialogResult.Cancel],
            MessageBoxButtons.AbortRetryIgnore => [DialogResult.Abort, DialogResult.Retry, DialogResult.Ignore],
            MessageBoxButtons.CancelTryContinue => [DialogResult.Cancel, DialogResult.TryAgain, DialogResult.Continue],
            _ => throw new ArgumentOutOfRangeException(nameof(buttons)),
        };
        Form dialog = new DialogForm()
        {
            Text = caption, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi,
            ControlBox = results.Contains(DialogResult.Cancel) || results.Length == 1,
            ClientSize = new Size(520, 180), Padding = new Padding(18), KeyPreview = true,
        };
        Label glyph = new()
        {
            Text = icon switch { MessageBoxIcon.Error => "×", MessageBoxIcon.Warning => "!", MessageBoxIcon.Question => "?", MessageBoxIcon.Information => "i", _ => "" },
            Dock = DockStyle.Left, Width = icon == MessageBoxIcon.None ? 0 : 44, TextAlign = ContentAlignment.TopCenter,
            Font = new Font(dialog.Font.FontFamily, 22, FontStyle.Bold), AccessibleName = icon.ToString(),
        };
        var glyphFont = glyph.Font;
        dialog.Disposed += (_, _) => glyphFont.Dispose();
        TextBox message = new()
        {
            Text = text.ReplaceLineEndings(), Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill, ScrollBars = ScrollBars.None, TabStop = false, AccessibleName = "Message",
        };
        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = new Padding(0, 8, 0, 0),
        };
        var controls = new List<Button>();
        foreach (DialogResult result in results)
            controls.Add(new DialogButton { Text = "&" + (result == DialogResult.TryAgain ? "Try again" : result.ToString()),
                DialogResult = result, MinimumSize = new Size(88, 30), AutoSize = true, TabIndex = controls.Count,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false });
        for (int index = controls.Count - 1; index >= 0; index--) actions.Controls.Add(controls[index]);
        int selected = Math.Clamp((int)defaultButton / 256, 0, controls.Count - 1);
        dialog.AcceptButton = controls[selected];
        dialog.CancelButton = controls.FirstOrDefault(b => b.DialogResult == DialogResult.Cancel)
            ?? (results.Length == 1 ? controls[0] : null);
        dialog.Controls.Add(message); dialog.Controls.Add(glyph); dialog.Controls.Add(actions);
        ApplyTheme?.Invoke(dialog);
        // A read-only TextBox defaults to the disabled Windows surface; keep it on the dialog canvas.
        message.BackColor = dialog.BackColor; message.ForeColor = dialog.ForeColor;
        Size measured = TextRenderer.MeasureText(text, dialog.Font, new Size(440, 500), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        dialog.ClientSize = new Size(520, Math.Clamp(measured.Height + 100, 160, 600));
        if (measured.Height > 490) message.ScrollBars = ScrollBars.Vertical;
        dialog.Shown += (_, _) => controls[selected].Focus();
        return dialog;
    }

    private sealed class DialogForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int dark = BackColor.GetBrightness() < .5f ? 1 : 0;
            _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }
    }
    private sealed class DialogButton : Button
    {
        private bool _hover, _pressed;
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new(0, 0, Width - 1, Height - 1);
            float d = Math.Max(4, 8f * DeviceDpi / 96);
            using GraphicsPath path = new();
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90); path.CloseFigure();
            using SolidBrush fill = new(_pressed ? FlatAppearance.MouseDownBackColor : _hover ? FlatAppearance.MouseOverBackColor : BackColor);
            using Pen edge = new(FlatAppearance.BorderColor);
            e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, rect, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rect, -5, -5), ForeColor, BackColor);
        }
    }
}
