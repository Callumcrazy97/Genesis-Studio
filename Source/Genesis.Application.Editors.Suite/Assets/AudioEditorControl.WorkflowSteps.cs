using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class AudioEditorControl
{
    private WorkflowBar? _audioWorkflow;

    /// <summary>The guided steps: Sound › Tune › Listen › Use in game.</summary>
    public WorkflowBar? AudioWorkflow => _audioWorkflow;

    private void BuildAudioWorkflowBar(Control toolbar)
    {
        _audioWorkflow = EditorWorkflow.AttachBelow(toolbar, "AudioWorkflow",
        [
            new("Sound", "Sound", "Choose or import the WAV this sound plays.",
                () => { ShowAudioQuickSetup(); if (_sourceCombo.CanFocus) _sourceCombo.Focus(); _audioWorkflow?.SetCurrent("Sound"); }),
            new("Tune", "Tune", "Set the volume and repeat; trim and fade the part you want to hear.",
                () => { ShowAudioQuickSetup(); if (_volumeSlider.CanFocus) _volumeSlider.Focus(); _audioWorkflow?.SetCurrent("Tune"); }),
            new("Listen", "Listen", "Press Play to hear it through the game's own mixer.",
                () => { ShowAudioQuickSetup(); Play(); _audioWorkflow?.SetCurrent("Listen"); }),
            new("UseInGame", "Use in game", "Save, then create an Object that plays this sound.",
                ShowAudioGameGuide),
        ]);
    }
}
