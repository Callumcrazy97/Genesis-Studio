using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ParticleEditorControl
{
    private WorkflowBar? _particleWorkflow;
    private Action? _showParticlePresets;

    /// <summary>The guided steps: Effect › Tune › Use in game.</summary>
    public WorkflowBar? ParticleWorkflow => _particleWorkflow;

    /// <summary>
    /// Step 1 opens the effect tiles (they used to be two menus deep, under Options › Emitters /
    /// Presets); choosing a tile moves the bar on to Tune.
    /// </summary>
    private void BuildParticleWorkflowBar(EditorCommandBar toolbar)
    {
        _particleWorkflow?.Dispose();
        _particleWorkflow = EditorWorkflow.AttachBelow(toolbar, "ParticleWorkflow",
        [
            new("Effect", "Effect", "Choose an effect to start from: fire, smoke, sparks, rain, magic and more.",
                () => _showParticlePresets?.Invoke()),
            new("Tune", "Tune", "Adjust amount, size, colour and speed. The preview updates as you go.",
                ShowParticleQuickSetup),
            new("UseInGame", "Use in game", "Save, then create an Object that plays this effect.",
                ShowParticleGameGuide),
        ]);
    }
}
