using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Shared.Assets;
using Genesis.Shared.Audio;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// The project's mixer, from the Audio editor: each bus's volume and effects (filter, EQ, reverb,
/// compressor; all off until turned on) and named snapshots of them, saved as the project's
/// <c>AudioMixer.json</c>, which a game starts with and blends between
/// (<c>Engine.Audio.Snapshot.Blend("Combat", 2)</c>). Play here plays this sound through its bus as
/// the panel has it, saved or not.
/// </summary>
public sealed partial class AudioEditorControl
{
    private FlowLayoutPanel? _audioMixer;
    private bool _showAudioMixer;
    private AudioMixerSettings? _mixer;
    private ThemedComboBox? _mixerBus, _mixerFilterType, _mixerReverbPlace;
    private NumericUpDown? _mixerVolume, _mixerFilterFrequency, _mixerFilterQ, _mixerEqLow, _mixerEqMid, _mixerEqHigh, _mixerReverbMix,
        _mixerThreshold, _mixerRatio, _mixerAttack, _mixerRelease;
    private CheckBox? _mixerEffects, _mixerFilter, _mixerEq, _mixerReverb, _mixerCompressor;
    private ListBox? _mixerSnapshots;
    private TextBox? _mixerSnapshotName;
    private Label? _mixerResult;
    private bool _mixerSyncing;

    /// <summary>The project's mixer as the Mixer page has it (loaded from AudioMixer.json when first asked for).</summary>
    public AudioMixerSettings Mixer => _mixer ??= AudioMixerSettings.Load(ProjectRoot);

    /// <summary>Sets one bus's volume and effects in the mixer (not saved until <see cref="SaveMixer"/>); a playing audition hears it at once.</summary>
    public void SetMixerBus(string bus, AudioBusMix mix)
    {
        string name = MixerBusName(bus);
        Mixer.Buses[name] = new AudioBusMix { Volume = Math.Clamp(float.IsFinite(mix.Volume) ? mix.Volume : 1f, 0f, 1f), Effects = mix.Effects.Normalized() };
        if (_audio is not null)
        {
            _audio.SetBusVolume(name, Mixer.Buses[name].Volume);
            _audio.SetBusEffects(name, Mixer.Buses[name].Effects);
        }
    }

    /// <summary>Keeps every bus the mixer has as the snapshot <paramref name="name"/> (replacing one of that name).</summary>
    public void SaveMixerSnapshot(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A snapshot needs a name.");
        Mixer.Snapshots[name.Trim()] = Mixer.Buses.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Writes the mixer as the project's AudioMixer.json.</summary>
    public void SaveMixer()
    {
        string path = AudioMixerSettings.PathFor(ProjectRoot);
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        Mixer.Save(ProjectRoot);
    }

    private static string MixerBusName(string bus)
    {
        string name = (bus ?? string.Empty).Trim().ToLowerInvariant();
        return name.Length == 0 ? "sfx" : name;
    }

    private void ShowAudioMixer()
    {
        FlowLayoutPanel panel = _audioMixer ??= BuildAudioMixer();
        _showAudioGameGuide = _showAudioSynth = false;
        if (_audioGameGuide is not null) _audioGameGuide.Visible = false;
        if (_audioSynth is not null) _audioSynth.Visible = false;
        _showAudioMixer = true; panel.Visible = true; panel.BringToFront();
        panel.AutoScrollPosition = Point.Empty;
        ShowMixerBus();
        ApplyAudioLayout(); RefreshAudioWorkflow(); LayoutAudioGameGuide();
    }

    private FlowLayoutPanel BuildAudioMixer()
    {
        FlowLayoutPanel panel = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, BackColor = EditorChrome.Surface, Padding = new Padding(16, 12, 16, 32), Name = "AudioMixer", Visible = false };
        _audioWorkspace.Controls.Add(panel); panel.SizeChanged += (_, _) => LayoutAudioGameGuide();

        Button back = new() { Text = "Back to Quick setup", Name = "AudioMixerBack" }; EditorChrome.StyleField(back);
        back.Click += (_, _) => ShowAudioQuickSetup();
        panel.Controls.Add(back);
        panel.Controls.Add(AudioWorkflowText("Mixer: effects on each bus, and snapshots", true));
        panel.Controls.Add(AudioWorkflowText("Every sound plays on a bus (sfx, music, or the project's own). Give a bus effects here: they are off until ticked, and a bus without any plays as it always did. Save the mixer and the game starts with it. A snapshot keeps every bus's volume and effects under a name; a game moves to one over a time with Engine.Audio.Snapshot.Blend(\"Combat\", 2)."));

        _mixerBus = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Name = "AudioMixerBus" };
        EditorChrome.StyleField(_mixerBus);
        _mixerBus.Items.AddRange([.. Buses]);
        panel.Controls.Add(LabelledRow("Bus", _mixerBus));
        _mixerVolume = MakeNumeric(0m, 100m, 5m, 0, 100m); _mixerVolume.Name = "AudioMixerVolume";
        panel.Controls.Add(LabelledRow("Volume (%)", _mixerVolume));
        _mixerEffects = MixerCheck("This bus's effects are on", "AudioMixerEffects");
        panel.Controls.Add(_mixerEffects);

        _mixerFilter = MixerCheck("Filter", "AudioMixerFilter");
        _mixerFilterType = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Name = "AudioMixerFilterType" };
        EditorChrome.StyleField(_mixerFilterType); _mixerFilterType.Items.AddRange([.. AudioBusEffects.FilterTypes]);
        _mixerFilterFrequency = MakeNumeric(20m, 20000m, 50m, 0, 1000m); _mixerFilterFrequency.Name = "AudioMixerFilterFrequency";
        _mixerFilterQ = MakeNumeric(0.5m, 10m, 0.05m, 2, 0.71m); _mixerFilterQ.Name = "AudioMixerFilterQ";
        panel.Controls.Add(_mixerFilter);
        panel.Controls.Add(LabelledRow("Type", _mixerFilterType));
        panel.Controls.Add(LabelledRow("Frequency (Hz)", _mixerFilterFrequency));
        panel.Controls.Add(LabelledRow("Q", _mixerFilterQ));

        _mixerEq = MixerCheck("EQ (three bands, dB)", "AudioMixerEq");
        _mixerEqLow = MakeNumeric(-18m, 18m, 0.5m, 1, 0m); _mixerEqLow.Name = "AudioMixerEqLow";
        _mixerEqMid = MakeNumeric(-18m, 18m, 0.5m, 1, 0m); _mixerEqMid.Name = "AudioMixerEqMid";
        _mixerEqHigh = MakeNumeric(-18m, 18m, 0.5m, 1, 0m); _mixerEqHigh.Name = "AudioMixerEqHigh";
        panel.Controls.Add(_mixerEq);
        panel.Controls.Add(LabelledRow("Low, 150 Hz", _mixerEqLow));
        panel.Controls.Add(LabelledRow("Mid, 1 kHz", _mixerEqMid));
        panel.Controls.Add(LabelledRow("High, 6 kHz", _mixerEqHigh));

        _mixerReverb = MixerCheck("Reverb", "AudioMixerReverb");
        _mixerReverbPlace = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Name = "AudioMixerReverbPlace" };
        EditorChrome.StyleField(_mixerReverbPlace); _mixerReverbPlace.Items.AddRange([.. AudioBusEffects.ReverbPlaces]);
        _mixerReverbMix = MakeNumeric(0m, 100m, 5m, 0, 30m); _mixerReverbMix.Name = "AudioMixerReverbMix";
        panel.Controls.Add(_mixerReverb);
        panel.Controls.Add(LabelledRow("Place", _mixerReverbPlace));
        panel.Controls.Add(LabelledRow("Mix (%)", _mixerReverbMix));

        _mixerCompressor = MixerCheck("Compressor", "AudioMixerCompressor");
        _mixerThreshold = MakeNumeric(-60m, 0m, 1m, 1, -12m); _mixerThreshold.Name = "AudioMixerThreshold";
        _mixerRatio = MakeNumeric(1m, 20m, 0.5m, 1, 4m); _mixerRatio.Name = "AudioMixerRatio";
        _mixerAttack = MakeNumeric(0m, 5m, 0.01m, 3, 0.01m); _mixerAttack.Name = "AudioMixerAttack";
        _mixerRelease = MakeNumeric(0m, 10m, 0.05m, 2, 0.25m); _mixerRelease.Name = "AudioMixerRelease";
        panel.Controls.Add(_mixerCompressor);
        panel.Controls.Add(LabelledRow("Threshold (dB)", _mixerThreshold));
        panel.Controls.Add(LabelledRow("Ratio", _mixerRatio));
        panel.Controls.Add(LabelledRow("Attack (s)", _mixerAttack));
        panel.Controls.Add(LabelledRow("Release (s)", _mixerRelease));

        Button play = new() { Text = "Play this sound through its bus", Name = "AudioMixerPlay" }; EditorChrome.StyleField(play);
        play.Click += (_, _) => Play();
        Button stop = new() { Text = "Stop", Name = "AudioMixerStop" }; EditorChrome.StyleField(stop);
        stop.Click += (_, _) => Stop();
        Button save = new() { Text = "Save the mixer", Name = "AudioMixerSave" }; EditorChrome.StyleField(save);
        save.Click += (_, _) => RunMixerAction(() => { SaveMixer(); return "Saved the mixer as AudioMixer.json beside the project file."; });
        panel.Controls.Add(play); panel.Controls.Add(stop); panel.Controls.Add(save);

        panel.Controls.Add(AudioWorkflowText("Snapshots", true));
        _mixerSnapshots = new ListBox { Name = "AudioMixerSnapshots", IntegralHeight = false };
        EditorChrome.StyleField(_mixerSnapshots);
        panel.Controls.Add(_mixerSnapshots);
        _mixerSnapshotName = new TextBox { Name = "AudioMixerSnapshotName", Text = "Combat" };
        EditorChrome.StyleField(_mixerSnapshotName);
        panel.Controls.Add(LabelledRow("Name", _mixerSnapshotName));
        Button keep = new() { Text = "Keep every bus as this snapshot", Name = "AudioMixerKeepSnapshot" }; EditorChrome.StyleField(keep);
        keep.Click += (_, _) => RunMixerAction(() =>
        {
            SaveMixerSnapshot(_mixerSnapshotName!.Text);
            ShowMixerSnapshots();
            return $"Kept the buses as the snapshot '{_mixerSnapshotName.Text.Trim()}'. Save the mixer to keep it in the project.";
        });
        Button remove = new() { Text = "Delete the chosen snapshot", Name = "AudioMixerDeleteSnapshot" }; EditorChrome.StyleField(remove);
        remove.Click += (_, _) => RunMixerAction(() =>
        {
            if (_mixerSnapshots!.SelectedItem is not string chosen) return "Choose a snapshot first.";
            Mixer.Snapshots.Remove(chosen);
            ShowMixerSnapshots();
            return $"Deleted '{chosen}'. Save the mixer to keep the change.";
        });
        panel.Controls.Add(keep); panel.Controls.Add(remove);
        _mixerResult = AudioWorkflowText(string.Empty); _mixerResult.Name = "AudioMixerResult";
        panel.Controls.Add(_mixerResult);

        _mixerBus.SelectedIndexChanged += (_, _) => ShowMixerBus();
        foreach (Control field in new Control[] { _mixerVolume, _mixerFilterFrequency, _mixerFilterQ, _mixerEqLow, _mixerEqMid, _mixerEqHigh,
                     _mixerReverbMix, _mixerThreshold, _mixerRatio, _mixerAttack, _mixerRelease })
            ((NumericUpDown)field).ValueChanged += (_, _) => MixerFieldsChanged();
        foreach (CheckBox check in new[] { _mixerEffects, _mixerFilter, _mixerEq, _mixerReverb, _mixerCompressor })
            check.CheckedChanged += (_, _) => MixerFieldsChanged();
        _mixerFilterType.SelectedIndexChanged += (_, _) => MixerFieldsChanged();
        _mixerReverbPlace.SelectedIndexChanged += (_, _) => MixerFieldsChanged();
        int own = Array.IndexOf(Buses, _document.Bus);
        _mixerBus.SelectedIndex = Math.Max(0, own);
        ShowMixerSnapshots();
        return panel;
    }

    private static CheckBox MixerCheck(string text, string name) =>
        new() { AutoSize = true, ForeColor = EditorChrome.Text, Text = text, Name = name };

    private void RunMixerAction(Func<string> action)
    {
        string text;
        try { text = action(); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { text = exception.Message; }
        if (_mixerResult is not null) _mixerResult.Text = text;
        LayoutAudioGameGuide();
    }

    private string SelectedMixerBus => _mixerBus?.SelectedItem as string ?? "sfx";

    // The chosen bus's volume and effects into the fields.
    private void ShowMixerBus()
    {
        if (_mixerBus is null || _mixerVolume is null) return;
        AudioBusMix mix = Mixer.Buses.TryGetValue(SelectedMixerBus, out AudioBusMix? found) ? found : new AudioBusMix();
        AudioBusEffects e = mix.Effects.Normalized();
        _mixerSyncing = true;
        try
        {
            _mixerVolume.Value = (decimal)Math.Round(Math.Clamp(mix.Volume, 0f, 1f) * 100f);
            _mixerEffects!.Checked = e.Enabled;
            _mixerFilter!.Checked = e.FilterEnabled; _mixerFilterType!.SelectedItem = e.FilterType;
            _mixerFilterFrequency!.Value = (decimal)e.FilterFrequency; _mixerFilterQ!.Value = Math.Clamp((decimal)e.FilterQ, _mixerFilterQ.Minimum, _mixerFilterQ.Maximum);
            _mixerEq!.Checked = e.EqEnabled; _mixerEqLow!.Value = (decimal)e.EqLow; _mixerEqMid!.Value = (decimal)e.EqMid; _mixerEqHigh!.Value = (decimal)e.EqHigh;
            _mixerReverb!.Checked = e.ReverbEnabled; _mixerReverbPlace!.SelectedItem = e.ReverbPlace; _mixerReverbMix!.Value = (decimal)Math.Round(e.ReverbMix * 100f);
            _mixerCompressor!.Checked = e.CompressorEnabled; _mixerThreshold!.Value = (decimal)e.CompressorThreshold; _mixerRatio!.Value = (decimal)e.CompressorRatio;
            _mixerAttack!.Value = (decimal)e.CompressorAttack; _mixerRelease!.Value = (decimal)e.CompressorRelease;
        }
        finally { _mixerSyncing = false; }
    }

    // The fields into the chosen bus.
    private void MixerFieldsChanged()
    {
        if (_mixerSyncing || _mixerVolume is null) return;
        AudioBusMix current = Mixer.Buses.TryGetValue(SelectedMixerBus, out AudioBusMix? found) ? found : new AudioBusMix();
        AudioBusEffects e = current.Effects.Copy();
        e.Enabled = _mixerEffects!.Checked;
        e.FilterEnabled = _mixerFilter!.Checked; e.FilterType = _mixerFilterType!.SelectedItem as string ?? "LowPass";
        e.FilterFrequency = (float)_mixerFilterFrequency!.Value; e.FilterQ = (float)_mixerFilterQ!.Value;
        e.EqEnabled = _mixerEq!.Checked; e.EqLow = (float)_mixerEqLow!.Value; e.EqMid = (float)_mixerEqMid!.Value; e.EqHigh = (float)_mixerEqHigh!.Value;
        e.ReverbEnabled = _mixerReverb!.Checked; e.ReverbPlace = _mixerReverbPlace!.SelectedItem as string ?? "room"; e.ReverbMix = (float)_mixerReverbMix!.Value / 100f;
        e.CompressorEnabled = _mixerCompressor!.Checked; e.CompressorThreshold = (float)_mixerThreshold!.Value; e.CompressorRatio = (float)_mixerRatio!.Value;
        e.CompressorAttack = (float)_mixerAttack!.Value; e.CompressorRelease = (float)_mixerRelease!.Value;
        SetMixerBus(SelectedMixerBus, new AudioBusMix { Volume = (float)_mixerVolume.Value / 100f, Effects = e });
    }

    private void ShowMixerSnapshots()
    {
        if (_mixerSnapshots is null) return;
        _mixerSnapshots.Items.Clear();
        foreach (string name in Mixer.Snapshots.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) _mixerSnapshots.Items.Add(name);
    }
}
