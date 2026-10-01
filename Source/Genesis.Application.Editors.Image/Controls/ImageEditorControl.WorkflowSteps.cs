using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private WorkflowBar? _imageWorkflow;

    /// <summary>The guided steps: Draw › Animate › Rig › Use in game.</summary>
    public WorkflowBar? ImageWorkflow => _imageWorkflow;

    /// <summary>Docks the workflow bar directly under the command header.</summary>
    /// <remarks>
    /// Top-docked siblings stack in reverse z-order, so taking the header's slot in the collection
    /// places the bar immediately beneath it. It replaces the "1. 2. 3." text that used to sit at the
    /// top of the drawing tools, where it was easy to miss and could not be clicked.
    /// </remarks>
    private void BuildImageWorkflowBar()
    {
        _imageWorkflow = new WorkflowBar("ImageWorkflow",
        [
            new("Draw", "Draw", "Draw on the canvas with the tools on the left, or import a picture from Options › File.",
                () => { ShowImageEditing(); _imageWorkflow?.SetCurrent("Draw"); }),
            new("Animate", "Animate", "Add frames in the timeline below the canvas, then press Play to preview them.",
                ShowAnimationStep),
            new("Rig", "Rig", "Draw bones over the sprite, then pose and animate them in the rig studio.",
                () => OpenRigStudio(0)),
            new("UseInGame", "Use in game", "Save, then create an Object that shows this image or animation.",
                ShowImageGameGuide),
        ])
        {
            AutoHideBelowHeight = 560,
        };
        _imageRoot.SuspendLayout();
        _imageRoot.Controls.Add(_imageWorkflow);
        _imageRoot.Controls.SetChildIndex(_imageWorkflow, _imageRoot.Controls.GetChildIndex(_imageHeader));
        _imageRoot.ResumeLayout(performLayout: true);
    }

    private void ShowAnimationStep()
    {
        ShowImageEditing();
        _timelinePanelVisible = true;
        ApplyResponsiveLayout();
        _timeline.Focus();
        _imageWorkflow?.SetCurrent("Animate");
    }

    /// <summary>Material painting is not an image workflow: the bar goes with Draw/Animate/Rig.</summary>
    private void HideImageWorkflowBar()
    {
        if (_imageWorkflow is null)
        {
            return;
        }

        _imageWorkflow.AutoHideBelowHeight = 0;
        _imageWorkflow.Visible = false;
    }
}
