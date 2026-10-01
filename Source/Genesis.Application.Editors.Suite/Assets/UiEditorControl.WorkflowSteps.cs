using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class UiEditorControl
{
    private WorkflowBar? _uiWorkflow;

    /// <summary>The guided steps: Start › Design › Use in game.</summary>
    public WorkflowBar? UiWorkflow => _uiWorkflow;

    private void BuildUiWorkflowBar(Control toolbar)
    {
        _uiWorkflow = EditorWorkflow.AttachBelow(toolbar, "UiWorkflow",
        [
            new("Start", "Start", "Start from a ready-made HUD or menu, or add elements one at a time.",
                ShowUiStartingGuide),
            new("Design", "Design", "Drag elements on the canvas; change their text, colours and anchors in the Inspector.",
                ShowUiDesign),
            new("UseInGame", "Use in game", "Save, then create an Object that draws this interface.",
                ShowUiGameGuide),
        ]);
        _uiWorkflow.SetCurrent("Design");
    }
}
