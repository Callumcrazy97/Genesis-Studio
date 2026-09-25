using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private byte[]? _pendingPixels;
    private Point _previewEnd;
    private bool _shapeFilled;
    private readonly List<Point> _curvePoints = [];
    private readonly NumericUpDown _threshold = Number(0, 255, 16);
    private readonly CheckBox _filledShape = new() { Text = "Filled shape", AutoSize = true };

    private static bool IsRasterGesture(ImageToolKind tool) => tool is ImageToolKind.Pencil or ImageToolKind.Brush
        or ImageToolKind.Eraser or ImageToolKind.Line or ImageToolKind.Rectangle or ImageToolKind.Ellipse or ImageToolKind.Gradient;

    // The displayed buffer is the buffer committed on release. No screen-space approximation.
    private void PreviewRaster(Point end)
    {
        var layer = _workspace.CurrentLayer;
        if (layer == null) return;
        _previewEnd = end;
        _pendingPixels = (byte[])layer.Pixels.Clone();
        var pixels = _pendingPixels;
        var brush = ActiveBrushSettings();
        var selection = _workspace.Selection;
        int w = _workspace.Width, h = _workspace.Height;
        var points = new List<Point>(_strokePoints);
        if (points.Count == 0 || points[^1] != end) points.Add(end);
        switch (_activeTool)
        {
            case ImageToolKind.Pencil:
                RasterOperations.DrawPixelPerfectStroke(pixels, w, h, points, ActivePaintColor(), brush, false, layer.AlphaLocked, selection);
                break;
            case ImageToolKind.Brush:
            case ImageToolKind.Eraser:
                if (points.Count == 1) points.Add(points[0]);
                for (int i = 1; i < points.Count; i++)
                    RasterOperations.DrawLine(pixels, w, h, points[i-1], points[i], ActivePaintColor(), brush, _activeTool == ImageToolKind.Eraser, layer.AlphaLocked, selection);
                break;
            case ImageToolKind.Line:
                RasterOperations.DrawLine(pixels, w, h, _strokeStart, end, ActivePaintColor(), brush, selection: selection); break;
            case ImageToolKind.Rectangle:
                RasterOperations.DrawRectangle(pixels, w, h, Normalize(_strokeStart, end), ActivePaintColor(), brush, _shapeFilled, selection); break;
            case ImageToolKind.Ellipse:
                RasterOperations.DrawEllipse(pixels, w, h, Normalize(_strokeStart, end), ActivePaintColor(), brush, _shapeFilled, selection); break;
            case ImageToolKind.Gradient:
                if (end != _strokeStart)
                    RasterOperations.Gradient(pixels, w, h, _strokeStart, end, ActivePaintColor(), _paintWithBackground ? _foreground.Colour : _background.Colour, selection);
                break;
        }
        _canvas.Overlay.ShowShapePreview = false;
        RefreshCanvas();
    }

    private void CommitRasterPreview(Point end)
    {
        PreviewRaster(end);
        var layer = _workspace.CurrentLayer;
        if (layer == null || _pendingPixels == null) return;
        PixelStrokeRecorder recorder = new(_workspace, layer);
        recorder.Capture(new Rectangle(0, 0, _workspace.Width, _workspace.Height));
        Buffer.BlockCopy(_pendingPixels, 0, layer.Pixels, 0, layer.Pixels.Length);
        _pendingPixels = null;
        _strokeRecorder = null;
        _strokePoints.Clear();
        _previewStroke = false;
        Commit(recorder.Complete($"Draw {_activeTool}"));
        RefreshCanvas();
    }

    private void PreviewCurve(Point cursor, bool commit = false)
    {
        var layer = _workspace.CurrentLayer;
        if (layer == null || _curvePoints.Count == 0) return;
        var points = _curvePoints.Append(cursor).Take(4).ToArray();
        var pixels = (byte[])layer.Pixels.Clone();
        if (points.Length >= 2)
        {
            // First two clicks are endpoints, then two control handles.
            Point a = points[0], b = points[1];
            Point c = points.Length > 2 ? points[2] : a, d = points.Length > 3 ? points[3] : b;
            ImageToolOperations.DrawBezier(pixels, _workspace.Width, _workspace.Height, a, c, d, b, ActivePaintColor(), ActiveBrushSettings(), _workspace.Selection);
        }
        _pendingPixels = pixels;
        if (commit)
        {
            PixelStrokeRecorder recorder = new(_workspace, layer);
            recorder.Capture(new Rectangle(0, 0, _workspace.Width, _workspace.Height));
            Buffer.BlockCopy(pixels, 0, layer.Pixels, 0, pixels.Length);
            _pendingPixels = null; _curvePoints.Clear();
            Commit(recorder.Complete("Draw Bezier curve"));
        }
        RefreshCanvas();
    }
}
