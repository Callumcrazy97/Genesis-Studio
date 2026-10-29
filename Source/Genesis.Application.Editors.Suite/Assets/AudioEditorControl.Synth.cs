using Genesis.Application.Core.Audio;
using Genesis.Application.Core.Resources;
using Genesis.Audio;
using Genesis.Shared.Audio;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Make a sound here: a gallery of the synthesiser's ready-made sounds (the ones automation's
/// <c>audio.synth</c> offers, rendered by the same <see cref="SynthSounds"/>), with length, pitch and
/// volume, Preview, and Use, which puts the WAV in Assets/Audio and selects it as this clip's source
/// exactly as <see cref="ImportWave"/> does.
/// </summary>
public sealed partial class AudioEditorControl
{
    private FlowLayoutPanel? _audioSynth;
    private bool _showAudioSynth;
    private ListBox? _synthPresets;
    private NumericUpDown? _synthLength, _synthPitch, _synthVolume;
    private CheckBox? _synthLoop;
    private Label? _synthDescription, _synthResult;

    private sealed record SynthChoice(string Preset, double Length, double Pitch, double Volume, bool Loop);

    /// <summary>The ready-made sounds "Make a sound" lists, in the order it lists them.</summary>
    public static IReadOnlyList<string> SynthPresets => SynthSounds.GalleryPresets();

    /// <summary>
    /// Makes a ready-made sound with the synthesiser, puts its WAV in Assets/Audio as <see cref="ImportWave"/>
    /// does and selects it as this clip's source. A <paramref name="length"/> of 0 keeps the length the sound
    /// is made at; <paramref name="pitch"/> 1 keeps its pitch; <paramref name="volume"/> (0 to 1) is left alone
    /// when null; <paramref name="loop"/> follows the preset (ambience and music repeat) when null. The clip is
    /// not saved. Returns the project-relative path of the new WAV.
    /// </summary>
    public string UseSynthSound(string preset, double length = 0, double pitch = 1, double? volume = null, bool? loop = null)
    {
        SynthSound sound = SynthSounds.Render(new SynthRecipe { Preset = preset, Length = length, Pitch = pitch });
        string relative = SynthSounds.WithWavFile(sound, SynthSounds.FileStem(preset), wav => ImportWave(wav));
        bool repeat = loop ?? SynthSounds.Info(preset).Loops;
        Commit(() => _document.Loop = repeat);
        if (volume is double level) SetVolume((float)Math.Clamp(level, 0, 1));
        return relative;
    }

    /// <summary>Plays a ready-made sound through the runtime mixer without adding it to the project.</summary>
    public void PreviewSynthSound(string preset, double length = 0, double pitch = 1, double volume = 1, bool loop = false)
    {
        SynthSound sound = SynthSounds.Render(new SynthRecipe { Preset = preset, Length = length, Pitch = pitch });
        Stop();
        _audio?.Dispose(); _audio = null;
        XAudioSystem audio;
        try { audio = new XAudioSystem(string.Empty); }
        catch (Exception exception) when (exception is DllNotFoundException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            // No audio device (headless test host, RDP session without redirection).
            _statusLabel.Text = "No audio output device — the sound can still be used and saved.";
            return;
        }
        _audio = audio;
        // The scratch WAV is read into memory by LoadSound, so it can go as soon as that returns.
        AudioAssetSettings settings = new() { Volume = (float)Math.Clamp(volume, 0, 1), Loop = loop };
        int soundId = SynthSounds.WithWavFile(sound, SynthSounds.FileStem(preset), wav => audio.LoadSound(wav, settings));
        if (soundId == 0)
        {
            _statusLabel.Text = "Could not play " + preset + ".";
            return;
        }
        _channel = audio.Play(soundId);
        _statusLabel.Text = _channel.IsValid
            ? $"Previewing {preset} · {sound.Seconds:0.##} s" + (loop ? " · repeating" : string.Empty)
            : "Mixer refused the sound (no free voice).";
    }

    private void ShowAudioSynth()
    {
        FlowLayoutPanel panel = _audioSynth ??= BuildAudioSynth();
        _showAudioGameGuide = false;
        if (_audioGameGuide is not null) _audioGameGuide.Visible = false;
        _showAudioMixer = false;
        if (_audioMixer is not null) _audioMixer.Visible = false;
        _showAudioSynth = true; panel.Visible = true; panel.BringToFront();
        _audioWorkflow?.SetCurrent("Sound");
        panel.AutoScrollPosition = Point.Empty;
        ApplyAudioLayout(); RefreshAudioWorkflow(); LayoutAudioGameGuide();
        _synthPresets?.Select();
    }

    private FlowLayoutPanel BuildAudioSynth()
    {
        FlowLayoutPanel panel = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, BackColor = EditorChrome.Surface, Padding = new Padding(16, 12, 16, 32), Name = "AudioSynth", Visible = false };
        _audioWorkspace.Controls.Add(panel); panel.SizeChanged += (_, _) => LayoutAudioGameGuide();

        Button back = new() { Text = "Back to Quick setup", Name = "AudioSynthBack" }; EditorChrome.StyleField(back);
        back.Click += (_, _) => ShowAudioQuickSetup();
        panel.Controls.Add(back);
        panel.Controls.Add(AudioWorkflowText("Make a sound here", true));
        panel.Controls.Add(AudioWorkflowText("Pick a ready-made sound, change how long it lasts and how high it is, and press Preview. Use this sound adds it to Assets/Audio and saves it as this clip's source, as importing a WAV would; trim and fade it in Quick setup afterwards."));
        panel.Controls.Add(AudioWorkflowText("Sound", true));
        _synthPresets = new ListBox { Name = "AudioSynthPresets", IntegralHeight = false };
        EditorChrome.StyleField(_synthPresets);
        foreach (string name in SynthSounds.GalleryPresets()) _synthPresets.Items.Add(name);
        panel.Controls.Add(_synthPresets);
        _synthDescription = AudioWorkflowText(string.Empty); _synthDescription.Name = "AudioSynthDescription";
        panel.Controls.Add(_synthDescription);

        _synthLength = MakeNumeric(0.05m, 30m, 0.1m, 2, 1m); _synthLength.Name = "AudioSynthLength";
        _synthPitch = MakeNumeric(0.5m, 2m, 0.05m, 2, 1m); _synthPitch.Name = "AudioSynthPitch";
        _synthVolume = MakeNumeric(0m, 100m, 5m, 0, 100m); _synthVolume.Name = "AudioSynthVolume";
        panel.Controls.Add(LabelledRow("Length (s)", _synthLength));
        panel.Controls.Add(LabelledRow("Pitch (1 = as made)", _synthPitch));
        panel.Controls.Add(LabelledRow("Volume (%)", _synthVolume));
        _synthLoop = new CheckBox { AutoSize = true, ForeColor = EditorChrome.Text, Text = "Repeat (ambience and music)", Name = "AudioSynthLoop" };
        panel.Controls.Add(_synthLoop);

        Button preview = new() { Text = "Preview", Name = "AudioSynthPreview" }; EditorChrome.StyleField(preview);
        preview.Click += (_, _) => PreviewSynthChoice();
        Button stop = new() { Text = "Stop", Name = "AudioSynthStop" }; EditorChrome.StyleField(stop);
        stop.Click += (_, _) => Stop();
        Button use = new() { Text = "Use this sound", Name = "AudioSynthUse" }; EditorChrome.StyleField(use);
        use.Click += (_, _) => UseSynthChoice();
        panel.Controls.Add(preview); panel.Controls.Add(stop); panel.Controls.Add(use);
        _synthResult = AudioWorkflowText(string.Empty); _synthResult.Name = "AudioSynthResult";
        panel.Controls.Add(_synthResult);

        _synthPresets.SelectedIndexChanged += (_, _) => SynthPresetChosen();
        if (_synthPresets.Items.Count > 0) _synthPresets.SelectedIndex = 0;
        return panel;
    }

    // Laying the page out resizes the list, and a list that is resized refreshes its items and
    // reports its choice again: without this the two called each other until the stack ran out.
    private bool _synthChoosing;

    // A chosen sound starts at the length it is made at, its own pitch and its own repeat setting.
    private void SynthPresetChosen()
    {
        if (_synthChoosing || _synthPresets?.SelectedItem is not string name || _synthLength is null || _synthPitch is null
            || _synthLoop is null || _synthDescription is null) return;
        _synthChoosing = true;
        try
        {
            SynthPresetInfo info = SynthSounds.Info(name);
            double seconds = SynthSounds.PresetSeconds(name);
            _synthLength.Value = Math.Clamp((decimal)Math.Round(seconds, 2), _synthLength.Minimum, _synthLength.Maximum);
            _synthPitch.Value = 1m;
            _synthLoop.Checked = info.Loops;
            _synthDescription.Text = (string.IsNullOrEmpty(info.Description) ? info.Category : info.Category + ": " + info.Description)
                + $" Made at {seconds:0.##} s.";
            LayoutAudioGameGuide();
        }
        finally { _synthChoosing = false; }
    }

    private SynthChoice? ReadSynthChoice()
    {
        if (_synthPresets?.SelectedItem is not string preset || _synthLength is null || _synthPitch is null
            || _synthVolume is null || _synthLoop is null) return null;
        double length = (double)_synthLength.Value;
        // The length it is made at goes through as 0, so a sound left alone is exactly the one audio.synth makes.
        if (Math.Abs(length - Math.Round(SynthSounds.PresetSeconds(preset), 2)) < 1e-9) length = 0;
        return new SynthChoice(preset, length, (double)_synthPitch.Value, (double)_synthVolume.Value / 100, _synthLoop.Checked);
    }

    private void SetSynthResult(string text)
    {
        if (_synthResult is null) return;
        _synthResult.Text = text;
        LayoutAudioGameGuide();
    }

    private void PreviewSynthChoice()
    {
        if (ReadSynthChoice() is not { } choice) { SetSynthResult("Pick a sound from the list first."); return; }
        try
        {
            PreviewSynthSound(choice.Preset, choice.Length, choice.Pitch, choice.Volume, choice.Loop);
            SetSynthResult(_statusLabel.Text);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or UnauthorizedAccessException)
        { SetSynthResult(exception.Message); }
    }

    private void UseSynthChoice()
    {
        if (ReadSynthChoice() is not { } choice) { SetSynthResult("Pick a sound from the list first."); return; }
        try
        {
            Stop();
            string relative = UseSynthSound(choice.Preset, choice.Length, choice.Pitch, choice.Volume, choice.Loop);
            Save();
            ShowAudioQuickSetup();
            _audioWorkflow?.SetCurrent("Tune");
            _statusLabel.Text = "Made " + ResourceDisplayName.Format(relative) + " in Assets/Audio and saved it as this clip's sound.";
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or UnauthorizedAccessException
                                           or InvalidOperationException or InvalidDataException)
        { SetSynthResult(exception.Message); }
    }
}
