using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Scripts;

public sealed partial class PgslScriptEditorControl
{
    private WorkflowBar? _scriptWorkflow;

    /// <summary>The guided steps: Write › Check › Use in game.</summary>
    public WorkflowBar? ScriptWorkflow => _scriptWorkflow;

    private void BuildScriptWorkflowBar(Control toolbar)
    {
        _scriptWorkflow = EditorWorkflow.AttachBelow(toolbar, "ScriptWorkflow",
        [
            new("Write", "Write", "Add actions in the Builder, or switch to Code and type PGSL. Both edit the same script.",
                () => { SetAuthoringMode(PgslScriptAuthoringMode.Builder); _scriptWorkflow?.SetCurrent("Write"); }),
            new("Check", "Check", "Problems are listed under the code as you type. Fix them before you save.",
                () => { ValidateNow(); _scriptWorkflow?.SetCurrent("Check"); }),
            new("UseInGame", "Use in game", "Save, then call these functions from an Object's events.",
                ShowScriptGameGuide),
        ]);
    }
}
