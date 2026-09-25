using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Dialogs;

public sealed class ImageRotateDialog : DpiAwareForm
{
    private readonly NumericUpDown _angle = ImageCanvasToolUi.Number("Rotation degrees",-359,359,90);
    private readonly CheckBox _expand = new() { Text = "Expand canvas to fit", Checked = true, AutoSize = true };
    public double Degrees => (double)_angle.Value;
    public bool ExpandCanvas => _expand.Checked;

    public ImageRotateDialog(byte[] pixels, int width, int height)
    {
        Text = "Rotate canvas"; ClientSize = new Size(820,560); MinimumSize = new Size(640,460); StartPosition = FormStartPosition.CenterParent;
        BackColor = ImageEditorChrome.Surface; ForeColor = ImageEditorChrome.Text;
        var preview = new CompositeFramePreviewControl { ShowCaption=false, ShowCheckerboard=true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,244));
        var fields = ImageCanvasToolUi.Fields(244);
        var presets = new FlowLayoutPanel { Width = 212, Height = 82 };
        presets.Controls.AddRange([ImageCanvasToolUi.Button("90° CW",() => _angle.Value=90),ImageCanvasToolUi.Button("90° CCW",() => _angle.Value=-90),ImageCanvasToolUi.Button("180°",() => _angle.Value=180)]);
        var size = ImageCanvasToolUi.Label("",44);
        var apply = ImageCanvasToolUi.Button("Apply rotation"); apply.DialogResult = DialogResult.OK;
        var cancel = ImageCanvasToolUi.Button("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        fields.Controls.AddRange([ImageCanvasToolUi.Label("Angle (clockwise degrees)"),_angle,presets,_expand,size,
            ImageCanvasToolUi.Label("Rotates the target artwork around the canvas centre. Transparent pixels fill the corners.",72),
            ImageCanvasToolUi.Label("Nearest-neighbour pixels. Turn off Expand to keep the canvas size and clip the result.",66),
            ImageCanvasToolUi.Label("9-slice guides follow quarter turns. Other angles reset the guides.",60)]);
        void UpdatePreview()
        {
            Size target = ImageCanvasTransforms.RotatedSize(width,height,Degrees,ExpandCanvas);
            apply.Enabled = target.Width <= 8192 && target.Height <= 8192;
            size.Text = apply.Enabled ? $"Result: {target.Width} × {target.Height} pixels" : "Result exceeds the 8192-pixel canvas limit.";
            if (apply.Enabled)
            {
                var frame = ImageCanvasTransforms.RotatePreview(pixels,width,height,Degrees,ExpandCanvas);
                preview.SetFrame(frame.Pixels,frame.Size.Width,frame.Size.Height);
            }
        }
        _angle.ValueChanged += (_,_) => UpdatePreview(); _expand.CheckedChanged += (_,_) => UpdatePreview();
        layout.Controls.Add(ImageCanvasToolUi.Preview("ROTATION PREVIEW",preview),0,0); layout.Controls.Add(fields,1,0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(12,0,0,0) };
        buttons.Controls.AddRange([apply,cancel]);
        Controls.Add(layout); Controls.Add(buttons); AcceptButton=apply; CancelButton=cancel; ThemeMessageBox.ApplyTheme?.Invoke(this); UpdatePreview();
    }
}
