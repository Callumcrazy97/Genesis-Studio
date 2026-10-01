using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class PhysicsEditorControl
{
    private WorkflowBar? _physicsWorkflow;

    /// <summary>The guided steps: What is it? › Set up › Tune › Test › Use in game.</summary>
    public WorkflowBar? PhysicsWorkflow => _physicsWorkflow;

    private void BuildPhysicsWorkflowBar(EditorCommandBar toolbar)
    {
        _physicsWorkflow?.Dispose();
        _physicsWorkflow = EditorWorkflow.AttachBelow(toolbar, "PhysicsWorkflow",
        [
            new("Kind", "What is it?", "Pick what this behaves like: a bouncy ball, a heavy crate, slippery ice, a trampoline…",
                () => SelectPhysicsWorkspaceMode("Presets", _physicsRail!)),
            new("Setup", "Set up", "Choose 2D or 3D, how the body moves, and the Image or Model it belongs to.",
                () => SelectPhysicsWorkspaceMode("Preview", _physicsRail!)),
            new("Tune", "Tune", "Adjust grip, bounciness and weight.",
                () => SelectPhysicsWorkspaceMode("Properties", _physicsRail!)),
            new("Test", "Test", "Watch it fall, slide and bounce in the preview. Drag things in the view to push them.",
                () => { SelectPhysicsWorkspaceMode("Preview", _physicsRail!); SetPhysicsPaused(false); _physicsWorkflow?.SetCurrent("Test"); }),
            new("UseInGame", "Use in game", "Save, then create an Object that uses this physics material.",
                ShowPhysicsGameGuide),
        ]);

        // The editor opens on Quick setup; the bar shows the step that is actually on screen.
        SyncPhysicsWorkflowStep(_physicsWorkspaceMode);
    }

    private void SyncPhysicsWorkflowStep(string? mode)
    {
        string? step = mode switch
        {
            "Presets" => "Kind",
            "Preview" => "Setup",
            "Properties" => "Tune",
            "Use in game" => "UseInGame",
            _ => null,
        };
        if (step is not null) _physicsWorkflow?.SetCurrent(step);
    }
}
