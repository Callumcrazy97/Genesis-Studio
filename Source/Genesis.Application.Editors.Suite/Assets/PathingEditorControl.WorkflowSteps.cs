using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class PathingEditorControl
{
    private WorkflowBar? _pathingWorkflow;

    /// <summary>The guided steps: Route › Preview › Use in game.</summary>
    public WorkflowBar? PathingWorkflow => _pathingWorkflow;

    private void BuildPathingWorkflowBar(Control toolbar)
    {
        _pathingWorkflow = EditorWorkflow.AttachBelow(toolbar, "PathingWorkflow",
        [
            new("Route", "Route", "Add the points of the route: click in the view, or edit the list on the left.",
                () => { SelectPathingSurface("Quick setup"); _pathingWorkflow?.SetCurrent("Route"); }),
            new("Preview", "Preview", "Press Play to watch something travel along the route.",
                () => { SelectPathingSurface("Quick setup"); Play(); _pathingWorkflow?.SetCurrent("Preview"); }),
            new("UseInGame", "Use in game", "Save, then create an Object that follows this route.",
                ShowPathingGameGuide),
        ]);
    }
}
