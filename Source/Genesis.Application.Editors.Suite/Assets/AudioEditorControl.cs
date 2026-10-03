using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Audio;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Shared.Audio;
using Genesis.Runtime.Climate;
using Genesis.Shared.Assets;
using Newtonsoft.Json;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Audio Editor: pick a project WAV, see its decoded waveform, audition playback through the
/// real runtime mixer, and edit the playback fields the game will honour (volume, pitch, bus,
/// loop, and the spatial falloff curve). Saves <c>.audio.json</c>.
/// </summary>
/// <remarks>
/// The audition deliberately runs through <see cref="XAudioSystem"/> rather than
/// System.Media.SoundPlayer. SoundPlayer cannot apply gain, pitch or attenuation, so the
/// editor used to play every clip at full volume no matter what the sliders said — the
/// designer heard something the game would never produce.
/// </remarks>
public sealed partial class AudioEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private sealed class AudioDocument
    {
        // v2 added Pitch / Bus / MinDistance / MaxDistance / Falloff. v1 documents load
        // unchanged: the missing members keep their defaults, which reproduce v1 behaviour.
        public int SchemaVersion { get; set; } = 4;
        public string? Source { get; set; }
        public float Volume { get; set; } = 1f;
        public float Pitch { get; set; } = 1f;
        public bool Loop { get; set; }
        public bool Spatial { get; set; }
        public string Bus { get; set; } = "sfx";
        public float MinDistance { get; set; } = 1f;
        public float MaxDistance { get; set; } = 48f;
        public float Falloff { get; set; } = 1f;
        public string EnvironmentRole { get; set; } = "None";
        public float TrimStart { get; set; }
        public float TrimEnd { get; set; }
        public float FadeIn { get; set; }
        public float FadeOut { get; set; }
    }

    private static readonly string[] Buses = ["sfx", "music", "master"];

    private AudioDocument _document;
    private readonly ThemedComboBox _sourceCombo;
    private readonly ThemedComboBox _busCombo;
    private readonly ThemedComboBox _environmentRoleCombo;
    private readonly Panel _waveformPanel;
    private readonly Panel _curvePanel;
    private readonly Panel _inspectorPanel;
    private readonly FlowLayoutPanel _propertyRows;
    private bool _applyingAudioLayout;
    private readonly Label _statusLabel;
    private readonly TrackBar _volumeSlider;
    private readonly CheckBox _loopCheck;
    private readonly CheckBox _spatialCheck;
    private readonly NumericUpDown _pitchInput;
    private readonly NumericUpDown _minDistanceInput;
    private readonly NumericUpDown _maxDistanceInput;
    private readonly NumericUpDown _falloffInput;
    private readonly NumericUpDown _trimStartInput;
    private readonly NumericUpDown _trimEndInput;
    private readonly NumericUpDown _fadeInInput;
    private readonly NumericUpDown _fadeOutInput;
    private PcmAudioClip? _sourceClip;
    private PcmAudioClip? _previewClip;
    private readonly TrackBar _listenerSlider;
    private readonly Label _listenerLabel;
    private readonly List<string> _choices = [];
    private readonly ToolTip _sourceToolTip = new();
    private float[] _waveformMin = [];
    private float[] _waveformMax = [];
    private XAudioSystem? _audio;
    private AudioChannel _channel;
    private string? _loadedInfo;
    private bool _syncing;

    public AudioEditorControl(string resourcePath, string projectRoot)
        : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        _document = LoadJsonOrDefault(() => new AudioDocument());
        if (!ValidDocument(_document))
        {
            LoadWarning = "Audio values must be finite and within the playback controls' supported ranges.";
            _document = new AudioDocument();
        }

        ToolStrip toolbar = BuildAudioWorkflowToolbar();
        _sourceCombo = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        EditorChrome.StyleField(_sourceCombo);
        _volumeSlider = new TrackBar { AutoSize = false, BackColor = EditorChrome.Surface, Height = 30,
            Maximum = 100, Minimum = 0, TickStyle = TickStyle.None,
            Value = (int)Math.Clamp(_document.Volume * 100f, 0f, 100f), Width = 244 };
        _loopCheck = new CheckBox { AutoSize = true, Checked = _document.Loop, ForeColor = EditorChrome.Text, Text = "Repeat the selected region" };
        _spatialCheck = new CheckBox { AutoSize = true, Checked = _document.Spatial, ForeColor = EditorChrome.Text, Text = "Volume changes with distance" };
        // Options on the left (Suite convention) — source summary + mixing/spatial inspector.
        _pitchInput = MakeNumeric(0.10m, 4.00m, 0.05m, 2, (decimal)_document.Pitch);
        _minDistanceInput = MakeNumeric(0m, 4096m, 1m, 1, (decimal)_document.MinDistance);
        _maxDistanceInput = MakeNumeric(0.1m, 8192m, 1m, 1, (decimal)_document.MaxDistance);
        _falloffInput = MakeNumeric(0.10m, 8.00m, 0.10m, 2, (decimal)_document.Falloff);
        _trimStartInput = MakeNumeric(0m, 86400m, 0.01m, 3, (decimal)_document.TrimStart);
        _trimEndInput = MakeNumeric(0m, 86400m, 0.01m, 3, (decimal)_document.TrimEnd);
        _fadeInInput = MakeNumeric(0m, 86400m, 0.01m, 3, (decimal)_document.FadeIn);
        _fadeOutInput = MakeNumeric(0m, 86400m, 0.01m, 3, (decimal)_document.FadeOut);
        _busCombo = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        _busCombo.Items.AddRange([.. Buses]);
        _busCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Buses, _document.Bus));
        EditorChrome.StyleField(_busCombo);
        _environmentRoleCombo = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        _environmentRoleCombo.Items.AddRange(Enum.GetNames<EnvironmentAudioRole>());
        _environmentRoleCombo.SelectedItem = Enum.TryParse(_document.EnvironmentRole, true, out EnvironmentAudioRole authoredRole)
            ? authoredRole.ToString()
            : EnvironmentAudioRole.None.ToString();
        EditorChrome.StyleField(_environmentRoleCombo);

        _listenerSlider = new TrackBar
        {
            AutoSize = false,
            BackColor = EditorChrome.Surface,
            Height = 30,
            Maximum = 200,
            Minimum = 0,
            TickStyle = TickStyle.None,
            Value = 0,
            Width = 244,
        };
        _listenerLabel = new Label
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 18,
            Width = 244,
        };

        FlowLayoutPanel rows = _propertyRows = new()
        {
            Name = "AudioQuickFields",
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(10, 8, 8, 8),
            WrapContents = false,
        };
        rows.Controls.Add(AudioWorkflowText("Choose a clip, then Play", true));
        rows.Controls.Add(AudioWorkflowText("1. Select or import a WAV. 2. Set volume and repeat; trim/fades use seconds. 3. Save and use this sound in a game."));
        rows.Controls.Add(EditorChrome.DividerLabel("WAV source"));
        rows.Controls.Add(_sourceCombo);
        Button import = new() { Text = "Import WAV…", Width = 244, Height = 31, BackColor = EditorChrome.Raised, ForeColor = EditorChrome.Text, FlatStyle = FlatStyle.Flat };
        import.Click += (_, _) => ImportAudio(); rows.Controls.Add(import);
        _quickPreset = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Name = "AudioStartingPreset" };
        _quickPreset.Items.AddRange(["Sound effect", "Music", "Custom / ambience"]); EditorChrome.StyleField(_quickPreset);
        _quickPreset.SelectedIndexChanged += (_, _) => { if (!_syncing && _quickPreset.SelectedIndex < 2) ApplyPlaybackPreset(_quickPreset.SelectedIndex == 1 ? "Music" : "General SFX"); };
        rows.Controls.Add(AudioWorkflowText("Starting preset", true)); rows.Controls.Add(_quickPreset);
        rows.Controls.Add(_volumeCaption = AudioWorkflowText("Volume", true)); rows.Controls.Add(_volumeSlider);
        rows.Controls.Add(_loopCheck);
        rows.Controls.Add(EditorChrome.DividerLabel("Regions"));
        rows.Controls.Add(LabelledRow("Start (s)", _trimStartInput));
        rows.Controls.Add(LabelledRow("End (s)*", _trimEndInput));
        rows.Controls.Add(LabelledRow("Fade in (s)", _fadeInInput));
        rows.Controls.Add(LabelledRow("Fade out (s)", _fadeOutInput));
        rows.Controls.Add(new Label { Text = "* 0 = clip end. Loop repeats this region.", AutoSize = true, ForeColor = EditorChrome.Muted });
        rows.Controls.Add(EditorChrome.SectionLabel("Mixing"));
        rows.Controls.Add(LabelledRow("Bus", _busCombo));
        rows.Controls.Add(LabelledRow("Pitch", _pitchInput));
        _advancedAudioFields.AddRange([EditorChrome.SectionLabel("Adaptive ambience"), LabelledRow("Role", _environmentRoleCombo)]);
        foreach (Control field in _advancedAudioFields) rows.Controls.Add(field);
        rows.Controls.Add(_spatialCheck);
        _spatialAudioFields.AddRange([EditorChrome.SectionLabel("Spatial falloff"), LabelledRow("Min dist", _minDistanceInput),
            LabelledRow("Max dist", _maxDistanceInput), LabelledRow("Curve", _falloffInput), EditorChrome.SectionLabel("Audition listener"), _listenerLabel, _listenerSlider]);
        foreach (Control field in _spatialAudioFields) rows.Controls.Add(field);

        Panel inspector = _inspectorPanel = EditorChrome.SidePanel(EditorChrome.LeftPanelWidth, DockStyle.Left);
        inspector.Controls.Add(rows);
        inspector.Controls.Add(EditorChrome.SectionLabel("Audio Properties"));

        _waveformPanel = new Panel { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill };
        _waveformPanel.Paint += PaintWaveform;
        _waveformPanel.Resize += (_, _) => _waveformPanel.Invalidate();
        _waveformPanel.Controls.Add(BuildAudioPreviewToolbar());

        _curvePanel = new Panel { BackColor = EditorChrome.Canvas, Dock = DockStyle.Bottom, Height = EditorChrome.BottomTimelineHeight };
        _curvePanel.Paint += PaintFalloffCurve;
        _curvePanel.Resize += (_, _) => _curvePanel.Invalidate();
        _curvePanel.Visible = _document.Spatial;

        _statusLabel = EditorChrome.MakeStatusBar();

        _audioWorkspace.Controls.Add(_waveformPanel);
        _audioWorkspace.Controls.Add(_curvePanel);
        _audioWorkspace.Controls.Add(inspector);
        Controls.Add(_audioWorkspace);
        Controls.Add(toolbar);
        Controls.Add(_statusLabel);
        BuildAudioWorkflowBar(toolbar);
        rows.Layout += (_, _) => ApplyAudioLayout();
        rows.SizeChanged += (_, _) => ApplyAudioLayout();
        rows.FontChanged += (_, _) => ApplyAudioLayout();
        SizeChanged += (_, _) => ApplyAudioLayout();

        PopulateSources();
        DecodeWaveform();
        UpdateListenerLabel();

        _sourceCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing)
            {
                return;
            }

            int index = _sourceCombo.SelectedIndex;
            Commit(() =>
            {
                _document.Source = index > 0 && index - 1 < _choices.Count ? _choices[index - 1] : null;
                _document.TrimStart = _document.TrimEnd = _document.FadeIn = _document.FadeOut = 0;
            });
        };
        _volumeSlider.ValueChanged += (_, _) => Commit(() => _document.Volume = _volumeSlider.Value / 100f);
        _loopCheck.CheckedChanged += (_, _) => Commit(() => _document.Loop = _loopCheck.Checked);
        _spatialCheck.CheckedChanged += (_, _) => Commit(() => _document.Spatial = _spatialCheck.Checked);
        _busCombo.SelectedIndexChanged += (_, _) => Commit(() => _document.Bus = Buses[Math.Max(0, _busCombo.SelectedIndex)]);
        _environmentRoleCombo.SelectedIndexChanged += (_, _) => Commit(() =>
            _document.EnvironmentRole = _environmentRoleCombo.SelectedItem?.ToString() ?? EnvironmentAudioRole.None.ToString());
        _pitchInput.ValueChanged += (_, _) => Commit(() => _document.Pitch = (float)_pitchInput.Value);
        _minDistanceInput.ValueChanged += (_, _) => Commit(() => _document.MinDistance = (float)_minDistanceInput.Value);
        _maxDistanceInput.ValueChanged += (_, _) => Commit(() => _document.MaxDistance = (float)_maxDistanceInput.Value);
        _falloffInput.ValueChanged += (_, _) => Commit(() => _document.Falloff = (float)_falloffInput.Value);
        foreach (NumericUpDown input in new[] { _trimStartInput, _trimEndInput, _fadeInInput, _fadeOutInput })
            input.ValueChanged += (_, _) =>
            {
                if (!_syncing && !SetRegion((float)_trimStartInput.Value, (float)_trimEndInput.Value,
                        (float)_fadeInInput.Value, (float)_fadeOutInput.Value))
                {
                    SynchronizePlaybackFields();
                    _statusLabel.Text = "Region unchanged: start must precede end, within the source clip.";
                }
            };
        _listenerSlider.ValueChanged += (_, _) =>
        {
            UpdateListenerLabel();
            ApplyListener();
            _curvePanel.Invalidate();
        };
        InitializeAudioWorkflow();
    }

    private static bool ValidDocument(AudioDocument document)
    {
        static bool Within(float value, float minimum, float maximum) =>
            float.IsFinite(value) && value >= minimum && value <= maximum;
        return Within(document.Volume, 0, 1) && Within(document.Pitch, .1f, 4)
            && Within(document.MinDistance, 0, 4096) && Within(document.MaxDistance, .1f, 8192)
            && Within(document.Falloff, .1f, 8) && Within(document.TrimStart, 0, 86400)
            && Within(document.TrimEnd, 0, 86400) && Within(document.FadeIn, 0, 86400)
            && Within(document.FadeOut, 0, 86400);
    }

    private static Control InfoCard(string title, string body)
    {
        Panel card = new()
        {
            BackColor = EditorChrome.Raised,
            Height = 72,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(10, 8, 10, 8),
            Width = 244,
        };
        card.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = EditorChrome.Muted,
            Text = body,
        });
        card.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Font = new Font(EditorChrome.BaseFont, FontStyle.Bold),
            ForeColor = EditorChrome.Text,
            Height = 22,
            Text = title,
        });
        return card;
    }

    public bool HasWaveform => _waveformMin.Length > 0;

    /// <summary>Distance (world units) the audition listener sits from the source.</summary>
    public float ListenerDistance =>
        _listenerSlider.Value / 200f * MathF.Max(1f, _document.MaxDistance * 1.2f);

    /// <summary>The listener readout as drawn, so tests can catch it going stale.</summary>
    public string ListenerSummary => _listenerLabel.Text;

    /// <summary>True while an audition voice is live on the runtime mixer.</summary>
    public bool IsAuditioning => _audio is not null && _audio.IsPlaying(_channel);

    /// <summary>
    /// The document as the runtime will read it. Tests assert against this so the editor's
    /// knobs and the shipped playback path cannot diverge.
    /// </summary>
    public AudioAssetSettings Settings => new()
    {
        Source = _document.Source,
        Volume = _document.Volume,
        Pitch = _document.Pitch,
        Loop = _document.Loop,
        Spatial = _document.Spatial,
        Bus = _document.Bus,
        MinDistance = _document.MinDistance,
        MaxDistance = _document.MaxDistance,
        Falloff = _document.Falloff,
        EnvironmentRole = _document.EnvironmentRole,
        TrimStart = _document.TrimStart, TrimEnd = _document.TrimEnd,
        FadeIn = _document.FadeIn, FadeOut = _document.FadeOut,
    };

    public PcmAudioClip? PreviewClip => _previewClip;

    public bool SetRegion(float start, float end, float fadeIn, float fadeOut)
    {
        if (_sourceClip is null || !float.IsFinite(start) || !float.IsFinite(end)
            || !float.IsFinite(fadeIn) || !float.IsFinite(fadeOut) || start < 0 || end < 0 || fadeIn < 0 || fadeOut < 0
            || start >= _sourceClip.Duration || (end > 0 && (end <= start || end > _sourceClip.Duration))) return false;
        Commit(() => { _document.TrimStart = start; _document.TrimEnd = end; _document.FadeIn = fadeIn; _document.FadeOut = fadeOut; });
        return true;
    }

    /// <summary>Gain the runtime would apply at <paramref name="distance"/> from the listener.</summary>
    public float GainAtDistance(float distance) => _document.Volume * Settings.AttenuationAt(distance);

    public void SetVolume(float volume) =>
        _volumeSlider.Value = (int)Math.Clamp(volume * 100f, 0f, 100f);

    public void SetSpatial(bool spatial) => _spatialCheck.Checked = spatial;

    public void SetLoop(bool loop) => _loopCheck.Checked = loop;

    public void SetFalloff(float minDistance, float maxDistance, float curve)
    {
        _maxDistanceInput.Value = Math.Clamp((decimal)maxDistance, _maxDistanceInput.Minimum, _maxDistanceInput.Maximum);
        _minDistanceInput.Value = Math.Clamp((decimal)minDistance, _minDistanceInput.Minimum, _minDistanceInput.Maximum);
        _falloffInput.Value = Math.Clamp((decimal)curve, _falloffInput.Minimum, _falloffInput.Maximum);
    }

    public void ApplyEnvironmentPreset(EnvironmentAudioRole role)
    {
        if (role == EnvironmentAudioRole.None)
        {
            ApplyPlaybackPreset("General SFX");
            return;
        }
        Commit(() =>
        {
        _document.EnvironmentRole = role.ToString();
        _document.Bus = "sfx";
        _document.Loop = true;
        _document.Pitch = 1f;
        _document.Spatial = role is EnvironmentAudioRole.Water or EnvironmentAudioRole.Fire or EnvironmentAudioRole.Wildlife;
        (_document.Volume, _document.MinDistance, _document.MaxDistance, _document.Falloff) = role switch
        {
            EnvironmentAudioRole.Wind => (0.62f, 1f, 48f, 1f),
            EnvironmentAudioRole.Rain => (0.78f, 1f, 48f, 1f),
            EnvironmentAudioRole.Water => (0.72f, 3f, 72f, 1.35f),
            EnvironmentAudioRole.Fire => (0.68f, 2f, 42f, 1.55f),
            EnvironmentAudioRole.Wildlife => (0.58f, 4f, 110f, 1.2f),
            EnvironmentAudioRole.Night => (0.52f, 1f, 48f, 1f),
            _ => (1f, 1f, 48f, 1f),
        };
        });
    }

    public void ApplyPlaybackPreset(string preset)
    {
        bool music = string.Equals(preset, "Music", StringComparison.OrdinalIgnoreCase);
        Commit(() =>
        {
        _document.EnvironmentRole = EnvironmentAudioRole.None.ToString();
        _document.Bus = music ? "music" : "sfx";
        _document.Volume = music ? 0.8f : 1f;
        _document.Pitch = 1f;
        _document.Loop = music;
        _document.Spatial = false;
        _document.MinDistance = 1f; _document.MaxDistance = 48f; _document.Falloff = 1f;
        });
    }

    private void SynchronizePlaybackFields()
    {
        _syncing = true;
        try
        {
            _volumeSlider.Value = Math.Clamp((int)MathF.Round(_document.Volume * 100f), _volumeSlider.Minimum, _volumeSlider.Maximum);
            _loopCheck.Checked = _document.Loop; _spatialCheck.Checked = _document.Spatial;
            _busCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Buses, _document.Bus));
            _environmentRoleCombo.SelectedItem = _document.EnvironmentRole;
            _pitchInput.Value = Math.Clamp((decimal)_document.Pitch, _pitchInput.Minimum, _pitchInput.Maximum);
            _minDistanceInput.Value = Math.Clamp((decimal)_document.MinDistance, _minDistanceInput.Minimum, _minDistanceInput.Maximum);
            _maxDistanceInput.Value = Math.Clamp((decimal)_document.MaxDistance, _maxDistanceInput.Minimum, _maxDistanceInput.Maximum);
            _falloffInput.Value = Math.Clamp((decimal)_document.Falloff, _falloffInput.Minimum, _falloffInput.Maximum);
            _trimStartInput.Value = (decimal)_document.TrimStart; _trimEndInput.Value = (decimal)_document.TrimEnd;
            _fadeInInput.Value = (decimal)_document.FadeIn; _fadeOutInput.Value = (decimal)_document.FadeOut;
            if (_quickPreset is not null) _quickPreset.SelectedIndex = _document.EnvironmentRole != "None" ? 2 : _document.Bus == "music" ? 1 : 0;
            if (_volumeCaption is not null) _volumeCaption.Text = $"Volume · {_document.Volume:P0}";
        }
        finally { _syncing = false; }
    }

    public bool SelectSource(string projectRelativePath)
    {
        if (string.IsNullOrWhiteSpace(projectRelativePath))
        {
            _sourceCombo.SelectedIndex = 0;
            return true;
        }
        int index = _choices.IndexOf(projectRelativePath);
        if (index < 0)
        {
            return false;
        }

        _sourceCombo.SelectedIndex = index + 1;
        return true;
    }

    public event EventHandler? InspectorStateChanged;

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues() =>
    [
        new("Playback", "volume", "Volume", _document.Volume, Minimum: 0, Maximum: 1, Increment: 0.01m, DecimalPlaces: 2),
        new("Playback", "pitch", "Pitch", _document.Pitch, Minimum: 0.25m, Maximum: 4m, Increment: 0.05m, DecimalPlaces: 2),
        new("Playback", "loop", "Loop", _document.Loop),
        new("Spatial audio", "spatial", "Spatial", _document.Spatial),
        new("Playback", "bus", "Bus", _document.Bus, Choices: Buses),
        new("Spatial audio", "minDistance", "Min distance", _document.MinDistance, Minimum: 0.1m, Maximum: 256m, Increment: 0.5m, DecimalPlaces: 1),
        new("Spatial audio", "maxDistance", "Max distance", _document.MaxDistance, Minimum: 1m, Maximum: 512m, Increment: 1m, DecimalPlaces: 1),
        new("Spatial audio", "falloff", "Falloff", _document.Falloff, Minimum: 0.1m, Maximum: 8m, Increment: 0.05m, DecimalPlaces: 2),
        new("Playback", "environmentRole", "Environment role", _document.EnvironmentRole,
            Choices: Enum.GetNames<EnvironmentAudioRole>()),
        new("Region", "trimStart", "Start (s)", _document.TrimStart, Minimum: 0, Maximum: 86400, Increment: 0.01m, DecimalPlaces: 3),
        new("Region", "trimEnd", "End (s), 0 = source end", _document.TrimEnd, Minimum: 0, Maximum: 86400, Increment: 0.01m, DecimalPlaces: 3),
        new("Region", "fadeIn", "Fade in (s)", _document.FadeIn, Minimum: 0, Maximum: 86400, Increment: 0.01m, DecimalPlaces: 3),
        new("Region", "fadeOut", "Fade out (s)", _document.FadeOut, Minimum: 0, Maximum: 86400, Increment: 0.01m, DecimalPlaces: 3),
    ];

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) =>
        TryApplyInspectorValue(propertyPath, value);

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (propertyPath.ToLowerInvariant() is "volume" or "pitch" or "mindistance" or "maxdistance" or "falloff" or "trimstart" or "trimend" or "fadein" or "fadeout")
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float scalar) || !float.IsFinite(scalar)) return false;
            (float minimum, float maximum) = propertyPath.ToLowerInvariant() switch
            {
                "volume" => (0, 1), "pitch" => (.1f, 4), "mindistance" => (0, 4096), "maxdistance" => (.1f, 8192), "falloff" => (.1f, 8), _ => (0, 86400),
            };
            if (scalar < minimum || scalar > maximum) return false;
        }
        try
        {
            switch (propertyPath.ToLowerInvariant())
            {
                case "volume":
                    SetVolume(Convert.ToSingle(value, CultureInfo.InvariantCulture));
                    return true;
                case "pitch":
                    _pitchInput.Value = Math.Clamp(
                        (decimal)Convert.ToSingle(value, CultureInfo.InvariantCulture),
                        _pitchInput.Minimum,
                        _pitchInput.Maximum);
                    return true;
                case "loop":
                    SetLoop(Convert.ToBoolean(value, CultureInfo.InvariantCulture));
                    return true;
                case "spatial":
                    SetSpatial(Convert.ToBoolean(value, CultureInfo.InvariantCulture));
                    return true;
                case "bus":
                    int busIndex = Array.FindIndex(Buses, bus =>
                        string.Equals(bus, text, StringComparison.OrdinalIgnoreCase));
                    if (busIndex < 0) return false;
                    _busCombo.SelectedIndex = busIndex;
                    return true;
                case "mindistance":
                    _minDistanceInput.Value = Math.Clamp(
                        (decimal)Convert.ToSingle(value, CultureInfo.InvariantCulture),
                        _minDistanceInput.Minimum,
                        _minDistanceInput.Maximum);
                    return true;
                case "maxdistance":
                    _maxDistanceInput.Value = Math.Clamp(
                        (decimal)Convert.ToSingle(value, CultureInfo.InvariantCulture),
                        _maxDistanceInput.Minimum,
                        _maxDistanceInput.Maximum);
                    return true;
                case "falloff":
                    _falloffInput.Value = Math.Clamp(
                        (decimal)Convert.ToSingle(value, CultureInfo.InvariantCulture),
                        _falloffInput.Minimum,
                        _falloffInput.Maximum);
                    return true;
                case "environmentrole":
                    if (!Enum.TryParse(text, true, out EnvironmentAudioRole role) || !Enum.IsDefined(role)) return false;
                    _environmentRoleCombo.SelectedItem = role.ToString();
                    return _environmentRoleCombo.SelectedItem is not null;
                case "source":
                    return SelectSource(text);
                case "trimstart": return SetRegion(Convert.ToSingle(value, CultureInfo.InvariantCulture), _document.TrimEnd, _document.FadeIn, _document.FadeOut);
                case "trimend": return SetRegion(_document.TrimStart, Convert.ToSingle(value, CultureInfo.InvariantCulture), _document.FadeIn, _document.FadeOut);
                case "fadein": return SetRegion(_document.TrimStart, _document.TrimEnd, Convert.ToSingle(value, CultureInfo.InvariantCulture), _document.FadeOut);
                case "fadeout": return SetRegion(_document.TrimStart, _document.TrimEnd, _document.FadeIn, Convert.ToSingle(value, CultureInfo.InvariantCulture));
                default:
                    return false;
            }
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException
                                           or OverflowException)
        {
            return false;
        }
    }

    public override void Save()
    {
        if (LoadWarning is not null) throw new InvalidDataException("The unreadable audio document was preserved: " + LoadWarning);
        if (!ValidDocument(_document)) throw new InvalidDataException("Audio settings must be finite and within the supported ranges.");
        if (_document.Spatial && _document.MaxDistance <= _document.MinDistance) throw new InvalidDataException("Maximum distance must be greater than minimum distance for spatial sound.");
        if (!string.IsNullOrWhiteSpace(_document.Source) && _sourceClip is null) throw new InvalidDataException("Choose a readable WAV before saving; the existing audio resource was retained.");
        if (_sourceClip is not null) _sourceClip.ApplyRegion(Settings);
        _document.SchemaVersion = 4;
        ProjectAssetWriteRegistry.MarkLocalWrite(ResourcePath);
        SaveJson(_document);
        AcceptSave();
    }

    private void Commit(Action apply)
    {
        if (_syncing)
        {
            return;
        }

        string before = JsonConvert.SerializeObject(_document);
        apply();
        string after = JsonConvert.SerializeObject(_document);
        if (before == after) return;
        PushEdit("audio settings", () => Restore(after), () => Restore(before), maximumEntries: 100);
        RefreshDocument();
    }

    private void Restore(string snapshot)
    {
        _document = JsonConvert.DeserializeObject<AudioDocument>(snapshot) ?? throw new InvalidDataException("Invalid audio undo snapshot.");
        RefreshDocument();
    }

    private void RefreshDocument()
    {
        Stop(); _audio?.Dispose(); _audio = null;
        SynchronizePlaybackFields();
        PopulateSources(); DecodeWaveform();
        ApplyListener();

        // The readout and the drawn envelope both depend on the values just changed, and both
        // must refresh even when no audition voice exists — ApplyListener returns early in that
        // case, which is how the panel used to sit showing stale "Non-spatial" text at full
        // amplitude after the designer had already ticked Spatial and pulled the volume down.
        UpdateListenerLabel();
        _curvePanel.Invalidate();
        ApplyAudioLayout();
        RefreshAudioWorkflow();
        _waveformPanel.Invalidate();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static NumericUpDown MakeNumeric(decimal min, decimal max, decimal step, int decimals, decimal value)
    {
        var input = new NumericUpDown
        {
            DecimalPlaces = decimals,
            Increment = step,
            Maximum = max,
            Minimum = min,
            Width = 92,
        };
        input.Value = Math.Clamp(value, min, max);
        EditorChrome.StyleField(input);
        return input;
    }

    private static Panel LabelledRow(string caption, Control field)
    {
        var row = new TableLayoutPanel { Height = 32, Width = 244, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label
        {
            AutoSize = true,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Anchor = AnchorStyles.Left,
            Text = caption,
        }, 0, 0);
        field.Dock = DockStyle.Fill;
        row.Controls.Add(field, 1, 0);
        return row;
    }

    private void UpdateListenerLabel() =>
        _listenerLabel.Text = _document.Spatial
            ? $"{ListenerDistance:0.0} units → gain {GainAtDistance(ListenerDistance):0.00}"
            : "Non-spatial — plays at full gain";
    private string GetDisplayName(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "No source selected";
        string file = Path.Combine(ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string metaFile = file + ".meta";
        if (File.Exists(metaFile))
        {
            try
            {
                var json = System.IO.File.ReadAllText(metaFile);
                var meta = System.Text.Json.JsonSerializer.Deserialize<Genesis.Application.Core.Resources.AssetMetadata>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (meta != null && !string.IsNullOrWhiteSpace(meta.DisplayName))
                {
                    return meta.DisplayName;
                }
            }
            catch { }
        }
        return ResourceDisplayName.Format(file);
    }

    private void ChooseAudioResource()
    {
        ProjectAssetEntry? selected = AssetPickerService.PickAsset(
            new AssetPickerRequest(ProjectRoot, ResourceKind.Audio, null, "Choose Audio Clip"), FindForm());
        if (selected is null) return;
        try
        {
            AudioAssetSettings? settings = AudioAssetSettings.Load(selected.FullPath);
            if (string.IsNullOrWhiteSpace(settings?.Source)) return;
            string relative = Path.GetRelativePath(ProjectRoot,
                ResourceNames.ResolveFile(ProjectRoot, settings.Source, ResourceType.Audio)).Replace('\\', '/');
            PopulateSources();
            SelectSource(relative);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or System.Text.Json.JsonException)
        {
            MessageBox.Show(FindForm(), exception.Message, "Choose Audio Clip", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void PopulateSources()
    {
        _syncing = true;
        try
        {
            _choices.Clear();
            _sourceCombo.Items.Clear();
            _sourceCombo.Items.Add("(none)");
            string assets = Path.Combine(ProjectRoot, "Assets");
            if (Directory.Exists(assets))
            {
                foreach (string file in Directory.EnumerateFiles(assets, "*.*", SearchOption.AllDirectories)
                             .Where(file => IsSupportedAudioExtension(Path.GetExtension(file)))
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string relative = Path.GetRelativePath(ProjectRoot, file).Replace('\\', '/');
                    _choices.Add(relative);
                    _sourceCombo.Items.Add(GetDisplayName(relative));
                }
            }

            int selected = _document.Source is null ? 0 : _choices.IndexOf(_document.Source) + 1;
            _sourceCombo.SelectedIndex = Math.Max(0, selected);
            UpdateSourceTooltip();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ImportAudio()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Import Audio",
            Filter = "Audio (*.wav;*.ogg)|*.wav;*.ogg|WAV audio (*.wav)|*.wav|Ogg Vorbis audio (*.ogg)|*.ogg",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;

        try { _statusLabel.Text = "Imported " + ResourceDisplayName.Format(ImportWave(dialog.FileName)) + " into Assets/Audio."; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { MessageBox.Show(FindForm(), exception.Message, "Import Audio", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    public string ImportWave(string sourcePath)
    {
        string source = Path.GetFullPath(sourcePath);
        if (!IsSupportedAudioExtension(Path.GetExtension(source)))
            throw new ArgumentException("Choose a WAV or Ogg Vorbis audio file.", nameof(sourcePath));
        PcmAudioClip.Load(source);

        string audioDirectory = Path.Combine(ProjectRoot, "Assets", AssetKindNames.AudioFolder);
        Directory.CreateDirectory(audioDirectory);
        string destination = Path.Combine(audioDirectory, Path.GetFileName(source));
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            destination = UniqueImportPath(destination);
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, destination, overwrite: false);

        string relative = Path.GetRelativePath(ProjectRoot, destination).Replace('\\', '/');
        PopulateSources();
        if (!SelectSource(relative)) throw new InvalidOperationException("The imported audio could not be selected.");
        return relative;
    }

    private static bool IsSupportedAudioExtension(string extension) =>
        extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);

    private static string UniqueImportPath(string requested)
    {
        if (!File.Exists(requested)) return requested;
        string directory = Path.GetDirectoryName(requested) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(requested);
        string extension = Path.GetExtension(requested);
        for (int index = 2; ; index++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void UpdateSourceTooltip()
    {
        string? source = ResolveSourcePath();
        _sourceToolTip.SetToolTip(_sourceCombo, source ?? "No source selected");
    }

    private string? ResolveSourcePath() =>
        string.IsNullOrWhiteSpace(_document.Source)
            ? null
            : ResourceNames.ResolveFile(ProjectRoot, _document.Source, ResourceType.Audio);

    /// <summary>
    /// Audition the clip on the same mixer the game uses, so gain, pitch, bus, loop and
    /// distance attenuation all behave exactly as they will at runtime.
    /// </summary>
    public void Play()
    {
        string? path = ResolveSourcePath();
        if (path is null || !File.Exists(path))
        {
            _statusLabel.Text = "No source WAV selected.";
            return;
        }

        Stop();
        try
        {
        _audio?.Dispose();
        _audio = new XAudioSystem(ProjectRoot);
        }
        catch (Exception exception) when (exception is DllNotFoundException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            // No audio device (headless test host, RDP session without redirection).
            _statusLabel.Text = "No audio output device — settings still save and apply in game.";
            return;
        }

        int soundId = _audio.LoadSound(_document.Source!, Settings);
        if (soundId == 0)
        {
            _statusLabel.Text = "Could not decode " + _document.Source;
            return;
        }

        ApplyListener();
        _channel = _document.Spatial ? _audio.PlayAt(soundId, System.Numerics.Vector3.Zero) : _audio.Play(soundId);

        if (!_channel.IsValid)
        {
            _statusLabel.Text = "Mixer refused the clip (no free voice).";
            return;
        }

        string spatial = _document.Spatial ? $" · {ListenerDistance:0.0}u → {GainAtDistance(ListenerDistance):0.00}" : string.Empty;
        _statusLabel.Text = $"Playing {_document.Source} · {_document.Bus} · vol {_document.Volume:0.00} · pitch {_document.Pitch:0.00}"
            + (_document.Loop ? " · loop" : string.Empty) + spatial;
    }

    /// <summary>Move the audition listener so the falloff curve is audible while dragging.</summary>
    private void ApplyListener()
    {
        if (_audio is null)
        {
            return;
        }

        float distance = _document.Spatial ? ListenerDistance : 0f;
        _audio.SetListener(new System.Numerics.Vector3(0f, 0f, distance), new System.Numerics.Vector3(0f, 0f, -1f));
    }

    public void Stop()
    {
        if (_audio is not null && _channel.IsValid)
        {
            _audio.Stop(_channel);
        }

        _channel = AudioChannel.Invalid;
    }

    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        base.OnAssetDependenciesChanged(changes);
        Stop();
        _audio?.Dispose();
        _audio = null;
        DecodeWaveform();
        _waveformPanel.Invalidate();
        _statusLabel.Text = (_loadedInfo ?? "Audio source refreshed")
            + $" · live generation {changes.Generation}";
    }

    private void DecodeWaveform()
    {
        _waveformMin = []; _waveformMax = [];
        _sourceClip = _previewClip = null; _loadedInfo = null;
        string? path = ResolveSourcePath();
        if (path is null || !File.Exists(path))
        {
            _statusLabel.Text = "Choose a WAV source to preview and audition.";
            _waveformPanel.Invalidate(); return;
        }
        try
        {
            _sourceClip = PcmAudioClip.Load(path);
            _previewClip = _sourceClip.ApplyRegion(Settings);
            int sampleCount = _previewClip.Samples.Length / _previewClip.Channels;
            int columns = Math.Min(640, sampleCount);
            _waveformMin = new float[columns]; _waveformMax = new float[columns];
            for (int column = 0; column < columns; column++)
            {
                int start = (int)((long)column * sampleCount / columns);
                int end = (int)((long)(column + 1) * sampleCount / columns);
                for (int sample = start; sample < end; sample++)
                    for (int channel = 0; channel < _previewClip.Channels; channel++)
                    {
                        float value = _previewClip.Samples[sample * _previewClip.Channels + channel] / 32768f;
                        _waveformMin[column] = Math.Min(_waveformMin[column], value);
                        _waveformMax[column] = Math.Max(_waveformMax[column], value);
                    }
            }
            _loadedInfo = $"{_previewClip.SampleRate} Hz · {_previewClip.Channels} ch · {_previewClip.Duration:0.000}s region / {_sourceClip.Duration:0.000}s source";
            _statusLabel.Text = $"{_document.Source} — {_loadedInfo}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            _statusLabel.Text = "Could not decode audio: " + exception.Message;
        }
        _waveformPanel.Invalidate();
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged();
        ApplyAudioLayout();
    }

    private void ApplyAudioLayout()
    {
        if (_curvePanel is null || _inspectorPanel is null || _applyingAudioLayout) return;
        _applyingAudioLayout = true;
        _propertyRows.SuspendLayout();
        try
        {
            float scale = Math.Max(1f, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
            _inspectorPanel.Width = Math.Min((int)(EditorChrome.LeftPanelWidth * scale),
                Math.Max(220, ClientSize.Width / 2));
            _inspectorPanel.PerformLayout();
            int width = Math.Max(160, _inspectorPanel.ClientSize.Width - _propertyRows.Padding.Horizontal
                - SystemInformation.VerticalScrollBarWidth - 6);
            foreach (Control row in _propertyRows.Controls)
            {
                row.Width = width;
                if (row is TableLayoutPanel) row.Height = (int)(32 * scale);
                else if (row is Button) row.Height = (int)(31 * scale);
                else if (row is Panel) row.Height = (int)(72 * scale);
                else if (row is TrackBar) row.Height = (int)(30 * scale);
                else if (row is Label label)
                {
                    label.Font = label.Font.Bold ? EditorChrome.HeadingFont : EditorChrome.SmallFont;
                    label.MaximumSize = new Size(width, 0);
                    if (!label.AutoSize) label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 8;
                }
                if (row is ComboBox combo) { combo.Font = EditorChrome.BaseFont; combo.Height = combo.PreferredHeight; }
                if (row is CheckBox check) { check.Font = EditorChrome.BaseFont; check.AutoSize = false; check.Height = TextRenderer.MeasureText(check.Text, check.Font, new Size(width - 24, int.MaxValue), TextFormatFlags.WordBreak).Height + 12; }
                foreach (Label label in row.Controls.OfType<Label>())
                    label.Font = label.Font.Bold ? EditorChrome.HeadingFont : EditorChrome.SmallFont;
            }
            foreach (Label label in _inspectorPanel.Controls.OfType<Label>()) label.Font = EditorChrome.HeadingFont;
            _listenerLabel.Font = _statusLabel.Font = EditorChrome.SmallFont;
            _inspectorPanel.Visible = _waveformPanel.Visible = !_showAudioGameGuide;
            _curvePanel.Visible = _document.Spatial && !_showAudioGameGuide;
            foreach (Control field in _advancedAudioFields) field.Visible = _advancedAudio;
            foreach (Control field in _spatialAudioFields) field.Visible = _document.Spatial;
            _curvePanel.Height = Math.Max(96, Math.Min((int)(EditorChrome.BottomTimelineHeight * scale),
                Math.Max(96, ClientSize.Height / 3)));
            LayoutAudioGameGuide();
        }
        finally
        {
            _propertyRows.ResumeLayout();
            _applyingAudioLayout = false;
        }
    }
    private void PaintWaveform(object? sender, PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(EditorChrome.Canvas);
        Rectangle bounds = _waveformPanel.ClientRectangle;
        int toolbarHeight = _waveformPanel.Controls.OfType<ToolStrip>().Sum(strip => strip.Height);
        int plotTop = toolbarHeight + EditorChrome.SmallFont.Height + 24;
        int midY = plotTop + Math.Max(1, bounds.Height - plotTop) / 2;

        using Pen baseline = new(Color.FromArgb(70, EditorChrome.Muted));
        graphics.DrawLine(baseline, 0, midY, bounds.Width, midY);

        if (_waveformMin.Length == 0)
        {
            using SolidBrush muted = new(EditorChrome.Muted);
            using StringFormat format = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("No waveform.", EditorChrome.BaseFont, muted, bounds, format);
            return;
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using Pen wave = new(EditorChrome.Accent, 1f);
        // Scale the drawn envelope by the authored gain so the waveform shows what will be
        // heard, not just what is on disk — the slider has a visible consequence.
        float half = Math.Max(1, bounds.Height - plotTop) * 0.42f * Math.Clamp(_document.Volume, 0f, 1f);
        for (int x = 0; x < bounds.Width; x++)
        {
            int column = (int)((long)x * _waveformMin.Length / Math.Max(1, bounds.Width));
            float top = midY + _waveformMin[column] * half;
            float bottom = midY + _waveformMax[column] * half;
            graphics.DrawLine(wave, x, top, x, bottom);
        }

        if (_loadedInfo is not null)
        {
            using SolidBrush text = new(EditorChrome.Muted);
            graphics.DrawString(_loadedInfo, EditorChrome.SmallFont, text, 10, toolbarHeight + 8);
        }
    }

    private void PaintFalloffCurve(object? sender, PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(EditorChrome.Canvas);
        Rectangle bounds = _curvePanel.ClientRectangle;
        int left = TextRenderer.MeasureText("1.0", EditorChrome.SmallFont).Width + 12;
        int top = EditorChrome.SmallFont.Height + 16;
        var plot = new Rectangle(left, top, Math.Max(10, bounds.Width - left - 24),
            Math.Max(10, bounds.Height - top - EditorChrome.SmallFont.Height - 12));

        using SolidBrush muted = new(EditorChrome.Muted);
        graphics.DrawString("Attenuation vs. distance", EditorChrome.SmallFont, muted, 10, 5);

        using Pen frame = new(Color.FromArgb(90, EditorChrome.Border));
        graphics.DrawRectangle(frame, plot);

        if (!_document.Spatial)
        {
            using StringFormat format = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("Spatial off — constant gain.", EditorChrome.BaseFont, muted, plot, format);
            return;
        }

        AudioAssetSettings settings = Settings;
        float far = MathF.Max(1f, _document.MaxDistance * 1.2f);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var points = new PointF[plot.Width];
        for (int x = 0; x < plot.Width; x++)
        {
            float distance = x / (float)plot.Width * far;
            float gain = settings.AttenuationAt(distance);
            points[x] = new PointF(plot.Left + x, plot.Bottom - gain * plot.Height);
        }

        using (Pen curve = new(EditorChrome.Accent, 2f))
        {
            graphics.DrawLines(curve, points);
        }

        // Min/max distance guides.
        using (Pen guide = new(Color.FromArgb(120, EditorChrome.Success)) { DashStyle = DashStyle.Dot })
        {
            float minX = plot.Left + _document.MinDistance / far * plot.Width;
            float maxX = plot.Left + _document.MaxDistance / far * plot.Width;
            graphics.DrawLine(guide, minX, plot.Top, minX, plot.Bottom);
            graphics.DrawLine(guide, maxX, plot.Top, maxX, plot.Bottom);
        }

        // Listener marker.
        float listenerX = plot.Left + Math.Clamp(ListenerDistance / far, 0f, 1f) * plot.Width;
        float listenerY = plot.Bottom - settings.AttenuationAt(ListenerDistance) * plot.Height;
        using (SolidBrush marker = new(EditorChrome.Warning))
        {
            graphics.FillEllipse(marker, listenerX - 4f, listenerY - 4f, 8f, 8f);
        }

        graphics.DrawString("1.0", EditorChrome.SmallFont, muted, 12, plot.Top - 4);
        graphics.DrawString("0.0", EditorChrome.SmallFont, muted, 12, plot.Bottom - EditorChrome.SmallFont.Height);
        string farLabel = $"{far:0}u";
        graphics.DrawString(farLabel, EditorChrome.SmallFont, muted,
            plot.Right - graphics.MeasureString(farLabel, EditorChrome.SmallFont).Width, plot.Bottom + 4);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
            _audio?.Dispose();
            _audio = null;
            _sourceToolTip.Dispose();
        }

        base.Dispose(disposing);
    }

    private static ToolStripLabel ToolbarCaption(string text) => new(text)
    {
        AutoSize = true,
        ForeColor = EditorChrome.Muted,
        Margin = new Padding(8, 0, 4, 0),
        Padding = new Padding(0, 2, 0, 0),
    };
}


