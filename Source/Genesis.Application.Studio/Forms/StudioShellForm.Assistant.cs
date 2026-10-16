using Genesis.Application.Studio.Docking;

namespace Genesis.Application.Studio.Forms;

public sealed partial class StudioShellForm
{
    private AssistantDock? _assistantDock;

    /// <summary>Opens the AI assistant, made on first use so a project that never asks for it pays nothing.</summary>
    private void ShowAssistant()
    {
        if (_assistantDock is null || _assistantDock.IsDisposed) _assistantDock = new AssistantDock(_project);
        ShowToolWindow(_assistantDock);
    }
}
