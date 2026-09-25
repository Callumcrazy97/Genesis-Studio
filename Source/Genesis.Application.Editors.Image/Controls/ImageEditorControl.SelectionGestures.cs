using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private bool _selectionHoverMove, _selectionDragMove, _selectionAdd, _selectionSubtract;
    public ImageToolKind EffectiveTool => _selectionHoverMove || _selectionDragMove ? ImageToolKind.Move : _activeTool;
    private static bool IsSelectionTool(ImageToolKind tool) => tool is ImageToolKind.RectSelect or ImageToolKind.EllipseSelect or ImageToolKind.LassoSelect or ImageToolKind.MagicWand;

    private void UpdateSelectionHover(Point point, Keys modifiers)
    {
        if (_drawing || _movingFloating) return;
        bool move = IsSelectionTool(_activeTool) && (modifiers & (Keys.Control | Keys.Shift)) == 0
            && _workspace.Selection.HasSelection && _workspace.Selection.Contains(point.X,point.Y);
        if (_selectionHoverMove == move) return;
        _selectionHoverMove = move;
        _canvas.Cursor = move ? Cursors.SizeAll : Cursors.Cross;
        var button = _toolButtons.FirstOrDefault(item => item.Kind == EffectiveTool).Button;
        if (button != null) SelectToolButton(button);
        if (_currentToolSection != null) _currentToolSection.HeaderText = "Current Tool · " + DisplayName(EffectiveTool);
    }

    private void ClearSelectionGesture()
    {
        _drawing = _previewStroke = _selectionDragMove = false;
        _lassoPoints.Clear(); _strokePoints.Clear(); _strokeRecorder = null;
        CancelFloatingSelection();
        _workspace.Selection.Clear(); ClearShapePreview();
        _canvas.Capture = false;
        UpdateSelectionHover(new Point(-1,-1),Keys.None);
        RefreshCanvas();
    }
}
