using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ShaderEditorControl
{
    private WorkflowBar? _shaderWorkflow;

    /// <summary>The guided steps: Look › Preview on › Tune › Use in game.</summary>
    public WorkflowBar? ShaderWorkflow => _shaderWorkflow;

    /// <summary>
    /// Code and Buffers stay on the command bar and in Options: they are for people who already
    /// know what they want. The steps are the path for everyone else.
    /// </summary>
    private void BuildShaderWorkflowBar()
    {
        _shaderWorkflow?.Dispose();
        _shaderWorkflow = EditorWorkflow.AttachBelow(_toolbar, "ShaderWorkflow",
        [
            new("Look", "Look", "Choose a look to start from: water, glow, outline, dissolve, pixelate and more.",
                () => SelectShaderWorkspaceMode("Presets")),
            new("Preview", "Preview on", "Choose what the shader applies to and preview it on one of your images or models.",
                () => SelectShaderWorkspaceMode("Preview")),
            new("Tune", "Tune", "Adjust colours, speed and strength until it looks right.",
                () => SelectShaderWorkspaceMode("Parameters")),
            new("UseInGame", "Use in game", "Save, then create an Object that uses this shader.",
                ShowShaderGameGuide),
        ]);
    }
}
