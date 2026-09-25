using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Dialogs;

public sealed class ImageNineSliceDialog : DpiAwareForm
{
    private readonly int _width, _height;
    private readonly byte[] _pixels;
    private readonly ImageViewportControl _source = new();
    private readonly CompositeFramePreviewControl _preview = new() { ShowCaption=false, ShowCheckerboard=true };
    private readonly NumericUpDown[] _margins;
    private readonly NumericUpDown _outputWidth, _outputHeight;
    private readonly CheckBox _enabled = new() { Text = "Enable saved guides", Checked = true, AutoSize = true };
    private readonly ImageThemedComboBox[] _modes = [new(),new(),new()];
    private bool _syncing;
    private int _dragGuide = -1;
    public bool ResizePixels { get; private set; }
    public Size OutputSize => new((int)_outputWidth.Value,(int)_outputHeight.Value);
    public ImageViewportControl SourceCanvas => _source;
    public ImageNineSlice Settings => new() { Enabled = _enabled.Checked, Left = (int)_margins[0].Value, Top = (int)_margins[1].Value,
        Right = (int)_margins[2].Value, Bottom = (int)_margins[3].Value,
        HorizontalEdges = (ImageSliceMode)_modes[0].SelectedIndex, VerticalEdges = (ImageSliceMode)_modes[1].SelectedIndex, Centre = (ImageSliceMode)_modes[2].SelectedIndex };

    public ImageNineSliceDialog(byte[] pixels, int width, int height, ImageNineSlice settings)
    {
        _pixels = pixels; _width = width; _height = height;
        Text = "9-slice editor"; ClientSize = new Size(1060,690); MinimumSize = new Size(860,590); StartPosition = FormStartPosition.CenterParent;
        BackColor = ImageEditorChrome.Surface; ForeColor = ImageEditorChrome.Text;
        var initial = settings.Clone();
        _enabled.Checked = settings.Enabled || settings.Left+settings.Top+settings.Right+settings.Bottom == 0;
        if (!initial.Enabled && initial.Left+initial.Top+initial.Right+initial.Bottom == 0)
        { initial.Left = initial.Right = Math.Min(8,width/3); initial.Top = initial.Bottom = Math.Min(8,height/3); }
        _margins = [ImageCanvasToolUi.Number("Left margin",0,width-1,initial.Left),ImageCanvasToolUi.Number("Top margin",0,height-1,initial.Top),
            ImageCanvasToolUi.Number("Right margin",0,width-1,initial.Right),ImageCanvasToolUi.Number("Bottom margin",0,height-1,initial.Bottom)];
        _outputWidth = ImageCanvasToolUi.Number("Output width",1,8192,Math.Min(8192,width*2));
        _outputHeight = ImageCanvasToolUi.Number("Output height",1,8192,Math.Min(8192,height*2));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,246));
        var fields = ImageCanvasToolUi.Fields(246);
        fields.Controls.Add(ImageCanvasToolUi.Label("Drag the four blue guides. Corners keep their original pixels.",48));
        fields.Controls.Add(_enabled);
        for (int i = 0; i < 4; i++)
        {
            int index = i; fields.Controls.Add(ImageCanvasToolUi.Label(_margins[i].Name)); fields.Controls.Add(_margins[i]);
            _margins[i].ValueChanged += (_,_) => ChangeMargin(index);
        }
        fields.Controls.Add(ImageCanvasToolUi.Label("Output size (pixels)"));
        var dimensions = new FlowLayoutPanel { Width = 212, Height = 32 };
        _outputWidth.Width = _outputHeight.Width = 96; dimensions.Controls.AddRange([_outputWidth,_outputHeight]); fields.Controls.Add(dimensions);
        string[] names = ["Top / bottom edges","Left / right edges","Centre"];
        ImageSliceMode[] modes = [initial.HorizontalEdges,initial.VerticalEdges,initial.Centre];
        for (int i = 0; i < 3; i++)
        {
            _modes[i].Name = names[i]; _modes[i].AccessibleName = names[i]; _modes[i].Width = 200;
            _modes[i].Items.AddRange(Enum.GetNames<ImageSliceMode>()); _modes[i].SelectedIndex = (int)modes[i];
            _modes[i].SelectedIndexChanged += (_,_) => UpdatePreview(); fields.Controls.Add(ImageCanvasToolUi.Label(names[i])); fields.Controls.Add(_modes[i]);
        }
        var save = ImageCanvasToolUi.Button("Save guides",() => ResizePixels=false); save.DialogResult=DialogResult.OK;
        var resize = ImageCanvasToolUi.Button("Apply resized pixels",() => ResizePixels=true); resize.DialogResult=DialogResult.OK;
        var cancel = ImageCanvasToolUi.Button("Cancel"); cancel.DialogResult=DialogResult.Cancel;
        var actions = new Panel { Dock = DockStyle.Bottom, Height = 76, Padding = new Padding(12,0,12,4) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42 }; buttons.Controls.AddRange([save,resize,cancel]);
        actions.Controls.Add(buttons);
        actions.Controls.Add(new Label { Text = "Apply resizes the target artwork. Save guides keeps the canvas size.", Dock = DockStyle.Bottom, Height = 26 });
        _outputWidth.ValueChanged += (_,_) => UpdatePreview(); _outputHeight.ValueChanged += (_,_) => UpdatePreview(); _enabled.CheckedChanged += (_,_) => UpdatePreview();
        _source.SetSurface(pixels,width,height,1); _source.Overlay.ShowOrigin=false; _source.Overlay.ShowGrid=false; _source.Overlay.ShowPixelGrid=false;
        _source.CanvasPointerDown += (_,e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var s = Settings; float[] distances = [Math.Abs(e.ImagePoint.X-s.Left),Math.Abs(e.ImagePoint.Y-s.Top),Math.Abs(e.ImagePoint.X-(_width-s.Right)),Math.Abs(e.ImagePoint.Y-(_height-s.Bottom))];
            int nearest = Array.IndexOf(distances,distances.Min()); _dragGuide = distances[nearest] <= Math.Max(1,9/_source.Zoom) ? nearest : -1;
        };
        _source.CanvasPointerMove += (_,e) => Drag(e.ImagePoint);
        _source.CanvasPointerUp += (_,e) => { Drag(e.ImagePoint); _dragGuide=-1; };
        layout.Controls.Add(ImageCanvasToolUi.Preview("SOURCE · DRAG GUIDES",_source),0,0);
        layout.Controls.Add(ImageCanvasToolUi.Preview("RESIZED PREVIEW",_preview),1,0); layout.Controls.Add(fields,2,0);
        Controls.Add(layout); Controls.Add(actions); AcceptButton=save; CancelButton=cancel;
        Shown += (_,_) => _source.FitToView(); ThemeMessageBox.ApplyTheme?.Invoke(this); ChangeMargin(0);
    }

    private void Drag(PointF point)
    {
        if (_dragGuide < 0) return;
        double value = _dragGuide switch { 0 => point.X, 1 => point.Y, 2 => _width-point.X, _ => _height-point.Y };
        _margins[_dragGuide].Value = Math.Clamp((int)Math.Round(value),(int)_margins[_dragGuide].Minimum,(int)_margins[_dragGuide].Maximum);
    }

    private void ChangeMargin(int index)
    {
        if (_syncing) return; _syncing=true;
        int other = (index+2)%4, length = index%2 == 0 ? _width : _height;
        _margins[other].Value = Math.Min(_margins[other].Value,length-1-_margins[index].Value);
        // Normalize the other axis too, including previously saved malformed settings.
        _margins[2].Value = Math.Min(_margins[2].Value,_width-1-_margins[0].Value);
        _margins[3].Value = Math.Min(_margins[3].Value,_height-1-_margins[1].Value);
        _outputWidth.Minimum = _margins[0].Value+_margins[2].Value+1;
        _outputHeight.Minimum = _margins[1].Value+_margins[3].Value+1;
        _syncing=false; UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_syncing || _modes.Any(m => m.SelectedIndex < 0)) return;
        var settings = Settings;
        _source.Overlay.ShowNineSlice = _enabled.Checked;
        _source.Overlay.NineSlice = new Padding(settings.Left,settings.Top,settings.Right,settings.Bottom);
        _source.Invalidate();
        var frame = ImageCanvasTransforms.NineSlicePreview(_pixels,_width,_height,OutputSize,settings);
        _preview.SetFrame(frame.Pixels,frame.Size.Width,frame.Size.Height);
    }
}
