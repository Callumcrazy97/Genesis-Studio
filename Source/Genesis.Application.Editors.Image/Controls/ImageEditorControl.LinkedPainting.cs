using Genesis.Application.Core.Images;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private bool _linkedMaterialPainting;
    public Color ForegroundColor => _foreground.Colour;
    public int BrushSize => _brush.Size;

    /// <summary>Use the Image workspace and its normal history inside a material painting window.</summary>
    public void ConfigureMaterialPainting()
    {
        _linkedMaterialPainting = true;
        _leftPanelVisible = _rightPanelVisible = _timelinePanelVisible = false;
        foreach (ToolStripItem item in _imageCommandBar.Items)
            if (item.Text is "Draw" or "Animate" or "Rig" or "Use in game" or "Save") item.Visible = false;
        HideImageWorkflowBar();
        ApplyResponsiveLayout();
    }

    /// <summary>Commit a projected Model stroke to the same undo history as ordinary Image strokes.</summary>
    public void CommitPixelEdit(IImageDocumentCommand command) => Commit(command);
}
