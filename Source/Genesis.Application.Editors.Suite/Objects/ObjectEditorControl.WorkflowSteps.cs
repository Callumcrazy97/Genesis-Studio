using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Objects;

public sealed partial class ObjectEditorControl
{
    private WorkflowBar? _objectWorkflow;

    /// <summary>The guided steps: Look › Behaviour › Test › Use in game.</summary>
    public WorkflowBar? ObjectWorkflow => _objectWorkflow;

    private void BuildObjectWorkflowBar(EditorCommandBar toolbar)
    {
        _objectWorkflow = EditorWorkflow.AttachBelow(toolbar, "ObjectWorkflow",
        [
            new("Look", "Look", "Choose the Image or Model this Object shows, on the left.", ShowObjectLookStep),
            new("Behaviour", "Behaviour", "Start from a recipe, or add events (Create runs once, Step every frame) and actions.",
                ShowBehaviourStep),
            new("Test", "Test", "Try this Object on its own in the live preview (F5). Check all events is under Options.",
                RunLiveSandbox),
            new("UseInGame", "Use in game", "Save, then open a Room and place this Object in it.",
                ShowObjectGameGuide),
        ]);
    }

    private void ShowObjectLookStep()
    {
        _showObjectGameGuide = false;
        _propertiesPanelChoice = true;
        ApplyObjectLayout();
        RefreshObjectWorkflow();
        _objectWorkflow?.SetCurrent("Look");
        if (_spriteCombo.CanFocus) _spriteCombo.Focus();
    }
}
