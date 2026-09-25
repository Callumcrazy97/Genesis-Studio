using System.Drawing.Drawing2D;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private readonly ToolTip _toolHelp = new();
    private readonly Dictionary<string, Bitmap> _thumbnails = new();
    private long _thumbnailVersion = -1;
    private readonly CheckBox _layerVisible = new() { Text = "Visible", AutoSize = true };
    private readonly CheckBox _layerLocked = new() { Text = "Locked", AutoSize = true };
    private readonly Label _canvasDimensions = new() { AutoSize = true };
    private void OnSessionChanged(object? sender, EventArgs e) => DirtyChanged?.Invoke(this, EventArgs.Empty);
    private void OnCanvasViewChanged(object? sender, EventArgs e) => RefreshStatus();
    private void RefreshStatus() => _status.Text = $"{_workspace.Width} × {_workspace.Height} px  ·  {DisplayName(_activeTool)}  ·  Zoom {_canvas.Zoom:P0}  ·  Frame {_workspace.SelectedFrameIndex + 1}/{_workspace.Frames.Count}";

    private Control BuildAuthoringTools()
    {
        FlowLayoutPanel container = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(10),
            BackColor = ImageEditorChrome.Surface
        };
        _currentToolSection = new CollapsibleSection("Current Tool · Pencil", 224);
        var current = _currentToolSection.Content;
        _brushSize.SetBounds(170, 8, 90, 26); _brushHardness.SetBounds(170, 40, 90, 26); _brushOpacity.SetBounds(170, 72, 90, 26);
        _threshold.SetBounds(170, 104, 90, 26);
        var shape = new ImageThemedComboBox { Name = "BrushShape", Location = new Point(170,136), Width = 90 };
        shape.Items.AddRange(["Square", "Circle"]); shape.SelectedIndex = 0;
        shape.SelectedIndexChanged += (_, _) => _brush.Square = shape.SelectedIndex == 0;
        _brush.Square = true;
        _filledShape.Location = new Point(10,170);
        current.Controls.AddRange([SectionLabel("Size (diameter px)",10,12), SectionLabel("Hardness (%)",10,44),
            SectionLabel("Opacity (%)",10,76), SectionLabel("Threshold",10,108), SectionLabel("Brush shape",10,140),
            _brushSize,_brushHardness,_brushOpacity,_threshold,shape,_filledShape]);
        _foreground.SetBounds(10, 195, 32, 25); _background.SetBounds(48, 195, 32, 25);
        _foreground.BorderStyle = _background.BorderStyle = BorderStyle.FixedSingle;
        _foreground.Click += (_, _) => PickColor(_foreground); _background.Click += (_, _) => PickColor(_background);
        var swap = SectionActionButton("Swap (X)", 90, 195); swap.Name = "SwapColours"; swap.Click += (_, _) => SwapColours();
        current.Controls.AddRange([_foreground,_background,swap]); container.Controls.Add(_currentToolSection);
        void Group(string title, params ImageToolKind[] kinds)
        {
            int columns = title == "Selections" ? 4 : 3; int stride = title == "Selections" ? 66 : 88;
            var section = new CollapsibleSection(title, ((kinds.Length+columns-1)/columns)*46+8);
            for (int i = 0; i < kinds.Length; i++)
            {
                var kind = kinds[i];
                string label = kind switch { ImageToolKind.RectSelect => "Select", ImageToolKind.EllipseSelect => "Oval", ImageToolKind.LassoSelect => "Lasso", ImageToolKind.MagicWand => "Wand", ImageToolKind.ColorPicker => "Pick colour", _ => DisplayName(kind) };
                var button = new ImageToolButton(kind) { Name = "ImageTool"+kind, Text = label, Location = new Point(6+i%columns*stride,4+i/columns*46),
                    Size = new Size(stride-4,44), FlatStyle = FlatStyle.Flat, BackColor = ImageEditorChrome.Raised, ForeColor = ImageEditorChrome.Text,
                    AccessibleName = DisplayName(kind), TabStop = true };
                button.FlatAppearance.BorderColor = ImageEditorChrome.Border;
                button.Click += (_, _) => SetActiveTool(kind);
                _toolHelp.SetToolTip(button, ToolDescription(kind)); _toolButtons.Add((button,kind)); section.Content.Controls.Add(button);
            }
            container.Controls.Add(section);
        }
        Group("Brushes", ImageToolKind.Pencil, ImageToolKind.Brush, ImageToolKind.Eraser);
        Group("Shapes", ImageToolKind.Line, ImageToolKind.Rectangle, ImageToolKind.Ellipse, ImageToolKind.Polygon, ImageToolKind.Bezier);
        Group("Tools", ImageToolKind.Move, ImageToolKind.Fill, ImageToolKind.Gradient, ImageToolKind.ColorPicker, ImageToolKind.Text, ImageToolKind.Crop);
        Group("Selections", ImageToolKind.RectSelect, ImageToolKind.EllipseSelect, ImageToolKind.LassoSelect, ImageToolKind.MagicWand);
        container.Controls.Add(BuildPalette());
        SelectToolButton(_toolButtons.First(item => item.Kind == ImageToolKind.Pencil).Button);
        LayoutCurrentTool();
        return container;
    }

    private void LayoutCurrentTool()
    {
        if (_currentToolSection == null) return;
        bool paint = _activeTool is ImageToolKind.Pencil or ImageToolKind.Brush or ImageToolKind.Eraser;
        bool shape = _activeTool is ImageToolKind.Line or ImageToolKind.Rectangle or ImageToolKind.Ellipse or ImageToolKind.Polygon or ImageToolKind.Bezier;
        var rows = new (string Label, Control Field, bool Visible)[] {
            ("Size (diameter px)",_brushSize,paint||shape), ("Hardness (%)",_brushHardness,_activeTool is ImageToolKind.Brush or ImageToolKind.Eraser),
            ("Opacity (%)",_brushOpacity,paint||shape), ("Threshold",_threshold,_activeTool is ImageToolKind.Fill or ImageToolKind.MagicWand),
            ("Brush shape",_currentToolSection.Content.Controls["BrushShape"]!,paint||shape) };
        int y=8;
        foreach(var row in rows)
        {
            var label=_currentToolSection.Content.Controls.OfType<Label>().First(l=>l.Text==row.Label);
            label.Visible=row.Field.Visible=row.Visible;
            if(!row.Visible)continue;label.Top=y+4;row.Field.Top=y;y+=30;
        }
        _filledShape.Visible=_activeTool is ImageToolKind.Rectangle or ImageToolKind.Ellipse or ImageToolKind.Polygon;
        if(_filledShape.Visible){_filledShape.Top=y;y+=28;}
        _foreground.Top=_background.Top=y;
        _currentToolSection.Content.Controls["SwapColours"]!.Top=y;
        _currentToolSection.SetContentHeight(y+35);
    }
    private void SwapColours() => (_foreground.Colour, _background.Colour) = (_background.Colour, _foreground.Colour);
    private static string ToolDescription(ImageToolKind kind) => kind switch
    {
        ImageToolKind.Pencil => "Pencil (B) · right button uses background colour",
        ImageToolKind.Eraser => "Eraser (E)",
        ImageToolKind.Fill => "Flood fill (G)",
        ImageToolKind.ColorPicker => "Pick colour (I)",
        ImageToolKind.RectSelect or ImageToolKind.EllipseSelect or ImageToolKind.LassoSelect or ImageToolKind.MagicWand => "Ctrl adds · Shift subtracts · drag inside to move · Escape clears",
        ImageToolKind.Move => "Move selected pixels (V) · Enter applies, Escape cancels",
        ImageToolKind.Rectangle or ImageToolKind.Ellipse => "Drag to draw · Shift fills the shape",
        ImageToolKind.Polygon => "Click vertices, click first vertex or Enter to close · Shift fills",
        ImageToolKind.Bezier => "Click start, end, then two control handles · Escape cancels",
        _ => DisplayName(kind),
    };

    private void DrawThumbnail(Graphics graphics, Rectangle bounds, string key, Func<byte[]> pixels)
    {
        if (_thumbnailVersion != _workspace.Version)
        {
            foreach (var image in _thumbnails.Values) image.Dispose();
            _thumbnails.Clear(); _thumbnailVersion = _workspace.Version;
        }
        if (!_thumbnails.TryGetValue(key, out Bitmap? bitmap))
        {
            bitmap = CompositeFramePreviewControl.RgbaToBitmap(pixels(), _workspace.Width, _workspace.Height);
            _thumbnails[key] = bitmap;
        }
        var state = graphics.Save(); graphics.SetClip(bounds);
        using var light = new SolidBrush(ImageEditorChrome.Raised); using var dark = new SolidBrush(ImageEditorChrome.Canvas);
        graphics.FillRectangle(dark, bounds);
        for (int y = 0; y < bounds.Height; y += 6)
            for (int x = 0; x < bounds.Width; x += 6)
                if ((x / 6 + y / 6) % 2 == 0) graphics.FillRectangle(light, bounds.X + x, bounds.Y + y, 6, 6);
        float scale = Math.Min(bounds.Width / (float)bitmap.Width, bounds.Height / (float)bitmap.Height);
        var target = new RectangleF(bounds.X + (bounds.Width - bitmap.Width * scale) / 2, bounds.Y + (bounds.Height - bitmap.Height * scale) / 2, bitmap.Width * scale, bitmap.Height * scale);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(bitmap, target); graphics.Restore(state);
    }

    private void DrawTimelineFrame(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _visibleTimelineFrames.Count) return;
        int frameIndex = _visibleTimelineFrames[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0 || (_rangeStart >= 0 && frameIndex >= Math.Min(_rangeStart,_rangeEnd) && frameIndex <= Math.Max(_rangeStart,_rangeEnd));
        using var fill = new SolidBrush(selected ? ImageEditorChrome.Hover : ImageEditorChrome.Surface);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var thumbnail = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 5, 60, 60);
        var frame = _workspace.Frames[frameIndex];
        DrawThumbnail(e.Graphics, thumbnail, frame.Id.ToString(), () => _workspace.CompositeCurrentFrameFor(frameIndex).Pixels);
        using Pen border = new(selected ? ImageEditorChrome.Accent : ImageEditorChrome.Border, selected ? 2 : 1);
        e.Graphics.DrawRectangle(border, thumbnail);
        TextRenderer.DrawText(e.Graphics, $"{frameIndex + 1} · {frame.DurationMilliseconds} ms", ImageEditorChrome.BaseFont,
            new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 69, 78, 23), ImageEditorChrome.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawLayerRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _workspace.CurrentFrame == null || e.Index >= _workspace.CurrentFrame.Layers.Count) return;
        var layer = _workspace.CurrentFrame.Layers[_workspace.CurrentFrame.Layers.Count - 1 - e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var fill = new SolidBrush(selected ? ImageEditorChrome.Hover : ImageEditorChrome.Canvas); e.Graphics.FillRectangle(fill, e.Bounds);
        DrawThumbnail(e.Graphics, new Rectangle(e.Bounds.X + 36, e.Bounds.Y + 4, 32, 32), $"{_workspace.CurrentFrame.Id}/{layer.Id}", () => layer.Pixels);
        var eye=new Rectangle(e.Bounds.X+7,e.Bounds.Y+15,20,12);
        using var eyePen=new Pen(layer.Visible ? ImageEditorChrome.Text : ImageEditorChrome.Muted,1.5f);
        e.Graphics.DrawEllipse(eyePen,eye);
        if(layer.Visible) { using var pupil=new SolidBrush(ImageEditorChrome.Text); e.Graphics.FillEllipse(pupil,eye.X+7,eye.Y+3,6,6); }
        else e.Graphics.DrawLine(eyePen,eye.Left,eye.Bottom,eye.Right,eye.Top);
        TextRenderer.DrawText(e.Graphics, layer.Name + (layer.Locked ? "  [locked]" : ""), ImageEditorChrome.BaseFont,
            new Rectangle(e.Bounds.X + 75, e.Bounds.Y, e.Bounds.Width - 78, e.Bounds.Height), ImageEditorChrome.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (selected) { using Pen line = new(ImageEditorChrome.Accent, 2); e.Graphics.DrawLine(line, e.Bounds.X, e.Bounds.Y, e.Bounds.X, e.Bounds.Bottom); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.Changed -= OnSessionChanged;
            _canvas.ViewChanged -= OnCanvasViewChanged;
            _playbackTimer.Dispose(); _toolHelp.Dispose();
            foreach (var thumbnail in _thumbnails.Values) thumbnail.Dispose();
            _thumbnails.Clear();
        }
        base.Dispose(disposing);
    }
}
