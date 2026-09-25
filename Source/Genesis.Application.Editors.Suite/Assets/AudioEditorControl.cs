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
public sealed class AudioEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private sealed class AudioDocument
    {
        // v2 added Pitch / Bus / MinDistance / MaxDistance / Falloff. v1 documents load
        // unchanged: the missing members keep their defaults, which reproduce v1 behaviour.
        public int SchemaVersion { get; set; } = 3;
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
    }

    private static readonly string[] Buses = ["sfx", "music", "master"];

    private readonly AudioDocument _document;
    private readonly ThemedComboBox _sourceCombo;
    private readonly ThemedComboBox _busCombo;
    private readonly ThemedComboBox _environmentRoleCombo;
    private readonly Panel _waveformPanel;
    private readonly Panel _curvePanel;
    private readonly Label _statusLabel;
    private readonly TrackBar _volumeSlider;
    private readonly CheckBox _loopCheck;
    private readonly CheckBox _spatialCheck;
    private readonly NumericUpDown _pitchInput;
    private readonly NumericUpDown _minDistanceInput;
    private readonly NumericUpDown _maxDistanceInput;
    private readonly NumericUpDown _falloffInput;
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

        ToolStrip toolbar = EditorChrome.MakeToolbar();
        EditorViewportChrome.AttachDocumentMenus(toolbar, this);
        toolbar.Items.Add(new ToolStripLabel(ResourceDisplayName.Format(ResourcePath)) { ForeColor = EditorChrome.Text, Font = EditorChrome.HeadingFont, ToolTipText = ResourcePath });
        ToolStripButton save = EditorChrome.ToolButton("Save", "Save audio resource (Ctrl+S)", Save); save.BackColor = EditorChrome.Accent; toolbar.Items.Add(save);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(ToolbarCaption("Source"));
        toolbar.Items.Add(new ToolStripLabel("File") { ForeColor = EditorChrome.Muted });
        _sourceCombo = new ThemedComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        _sourceCombo.Enabled = false;
        EditorChrome.StyleField(_sourceCombo);
        toolbar.Items.Add(new ToolStripControlHost(_sourceCombo)
        {
            AutoSize = false,
            Margin = new Padding(0, 4, 6, 0),
            Size = new Size(248, 28),
        });
        toolbar.Items.Add(EditorChrome.ToolButton("Choose Clip…", "Choose an existing project Audio resource", ChooseAudioResource));
        ToolStripButton import = EditorChrome.ToolButton("+ Import Audio", "Copy a WAV, OGG or MP3 file into this project's Audio assets", ImportAudio);
        import.BackColor = EditorChrome.Accent;
        import.ForeColor = Color.White;
        toolbar.Items.Add(import);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(ToolbarCaption("Playback"));
        toolbar.Items.Add(EditorChrome.ToolButton("Play", "Audition through the runtime mixer", Play));
        toolbar.Items.Add(EditorChrome.ToolButton("Stop", "Stop playback", Stop));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Volume") { ForeColor = EditorChrome.Muted });
        _volumeSlider = new TrackBar
        {
            AutoSize = false,
            BackColor = EditorChrome.Surface,
            Height = 26,
            Maximum = 100,
            Minimum = 0,
            TickStyle = TickStyle.None,
            Value = (int)Math.Clamp(_document.Volume * 100f, 0f, 100f),
            Width = 120,
        };
        toolbar.Items.Add(new ToolStripControlHost(_volumeSlider) { AutoSize = false, Width = 124 });
        _loopCheck = new CheckBox { BackColor = Color.Transparent, Checked = _document.Loop, ForeColor = EditorChrome.Text, Text = "Loop" };
        _spatialCheck = new CheckBox { BackColor = Color.Transparent, Checked = _document.Spatial, ForeColor = EditorChrome.Text, Text = "Spatial" };
        toolbar.Items.Add(new ToolStripControlHost(_loopCheck));
        toolbar.Items.Add(new ToolStripControlHost(_spatialCheck));
        ToolStripDropDownButton presets = new("Preset")
        {
            ForeColor = EditorChrome.Text,
            ToolTipText = "Apply a complete playback or adaptive-environment preset",
        };
        presets.DropDownItems.Add("General SFX", null, (_, _) => ApplyPlaybackPreset("General SFX"));
        presets.DropDownItems.Add("Music", null, (_, _) => ApplyPlaybackPreset("Music"));
        presets.DropDownItems.Add(new ToolStripSeparator());
        foreach (EnvironmentAudioRole role in Enum.GetValues<EnvironmentAudioRole>().Where(role => role != EnvironmentAudioRole.None))
        {
            EnvironmentAudioRole captured = role;
            presets.DropDownItems.Add(role.ToString(), null, (_, _) => ApplyEnvironmentPreset(captured));
        }
        toolbar.Items.Add(presets);

        // Options on the left (Suite convention) — source summary + mixing/spatial inspector.
        _pitchInput = MakeNumeric(0.10m, 4.00m, 0.05m, 2, (decimal)_document.Pitch);
        _minDistanceInput = MakeNumeric(0m, 4096m, 1m, 1, (decimal)_document.MinDistance);
        _maxDistanceInput = MakeNumeric(0.1m, 8192m, 1m, 1, (decimal)_document.MaxDistance);
        _falloffInput = MakeNumeric(0.10m, 8.00m, 0.10m, 2, (decimal)_document.Falloff);
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

        FlowLayoutPanel rows = new()
        {
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(10, 8, 8, 8),
            WrapContents = false,
        };
        rows.Controls.Add(EditorChrome.DividerLabel("Source"));
        rows.Controls.Add(InfoCard("Clip", GetDisplayName(_document.Source)));
        rows.Controls.Add(EditorChrome.DividerLabel("Regions"));
        rows.Controls.Add(InfoCard("Loop region", _document.Loop ? "Whole clip · loop enabled" : "None yet"));
        rows.Controls.Add(InfoCard("Cue markers", "Add trim/fade/cue editing here"));
        rows.Controls.Add(EditorChrome.SectionLabel("Mixing"));
        rows.Controls.Add(LabelledRow("Bus", _busCombo));
        rows.Controls.Add(LabelledRow("Pitch", _pitchInput));
        rows.Controls.Add(EditorChrome.SectionLabel("Adaptive ambience"));
        rows.Controls.Add(LabelledRow("Role", _environmentRoleCombo));
        rows.Controls.Add(EditorChrome.SectionLabel("Spatial falloff"));
        rows.Controls.Add(LabelledRow("Min dist", _minDistanceInput));
        rows.Controls.Add(LabelledRow("Max dist", _maxDistanceInput));
        rows.Controls.Add(LabelledRow("Curve", _falloffInput));
        rows.Controls.Add(EditorChrome.SectionLabel("Audition listener"));
        rows.Controls.Add(_listenerLabel);
        rows.Controls.Add(_listenerSlider);
        rows.Controls.Add(EditorChrome.DividerLabel("Audition"));
        rows.Controls.Add(InfoCard("Runtime mixer", "Play uses the same XAudio path as the game."));

        Panel inspector = EditorChrome.SidePanel(EditorChrome.LeftPanelWidth, DockStyle.Left);
        inspector.Controls.Add(rows);
        inspector.Controls.Add(EditorChrome.SectionLabel("Audio Properties"));

        _waveformPanel = new Panel { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill };
        _waveformPanel.Paint += PaintWaveform;
        _waveformPanel.Resize += (_, _) => _waveformPanel.Invalidate();

        _curvePanel = new Panel { BackColor = EditorChrome.Canvas, Dock = DockStyle.Bottom, Height = EditorChrome.BottomTimelineHeight };
        _curvePanel.Paint += PaintFalloffCurve;
        _curvePanel.Resize += (_, _) => _curvePanel.Invalidate();

        _statusLabel = EditorChrome.MakeStatusBar();

        Controls.Add(_waveformPanel);
        Controls.Add(_curvePanel);
        Controls.Add(inspector);
        Controls.Add(toolbar);
        Controls.Add(_statusLabel);

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
            _document.Source = index > 0 && index - 1 < _choices.Count ? _choices[index - 1] : null;
            UpdateSourceTooltip();
            Stop();
            DecodeWaveform();
            MarkDirty();
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
        _listenerSlider.ValueChanged += (_, _) =>
        {
            UpdateListenerLabel();
            ApplyListener();
            _curvePanel.Invalidate();
        };
    }

    private Panel BuildSourcePanel()
    {
        Panel panel = EditorChrome.SidePanel(EditorChrome.LeftPanelWidth, DockStyle.Left);
        FlowLayoutPanel stack = EditorChrome.MakeSectionStack();
        stack.Padding = new Padding(12, 10, 12, 12);
        stack.Controls.Add(EditorChrome.DividerLabel("Source"));
        stack.Controls.Add(InfoCard("Clip", GetDisplayName(_document.Source)));
        stack.Controls.Add(EditorChrome.DividerLabel("Regions"));
        stack.Controls.Add(InfoCard("Loop region", _document.Loop ? "Whole clip · loop enabled" : "None yet"));
        stack.Controls.Add(InfoCard("Cue markers", "Add trim/fade/cue editing here"));
        stack.Controls.Add(EditorChrome.DividerLabel("Audition"));
        stack.Controls.Add(InfoCard("Runtime mixer", "Play uses the same XAudio path as the game."));
        panel.Controls.Add(stack);
        return panel;
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
    };

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
        SynchronizePlaybackFields();
        MarkDirty();
        UpdateListenerLabel(); _curvePanel.Invalidate(); _waveformPanel.Invalidate();
    }

    public void ApplyPlaybackPreset(string preset)
    {
        bool music = string.Equals(preset, "Music", StringComparison.OrdinalIgnoreCase);
        _document.EnvironmentRole = EnvironmentAudioRole.None.ToString();
        _document.Bus = music ? "music" : "sfx";
        _document.Volume = music ? 0.8f : 1f;
        _document.Pitch = 1f;
        _document.Loop = music;
        _document.Spatial = false;
        _document.MinDistance = 1f; _document.MaxDistance = 48f; _document.Falloff = 1f;
        SynchronizePlaybackFields();
        MarkDirty();
        UpdateListenerLabel(); _curvePanel.Invalidate(); _waveformPanel.Invalidate();
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
        }
        finally { _syncing = false; }
    }

    public bool SelectSource(string projectRelativePath)
    {
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
    ];

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) =>
        TryApplyInspectorValue(propertyPath, value);

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
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
                    _environmentRoleCombo.SelectedItem = text;
                    return _environmentRoleCombo.SelectedItem is not null;
                case "source":
                    return SelectSource(text);
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
        _document.SchemaVersion = 3;
        SaveJson(_document);
        AcceptSave();
    }

    private void Commit(Action apply)
    {
        if (_syncing)
        {
            return;
        }

        apply();
        MarkDirty();
        ApplyListener();

        // The readout and the drawn envelope both depend on the values just changed, and both
        // must refresh even when no audition voice exists — ApplyListener returns early in that
        // case, which is how the panel used to sit showing stale "Non-spatial" text at full
        // amplitude after the designer had already ticked Spatial and pulled the volume down.
        UpdateListenerLabel();
        _curvePanel.Invalidate();
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
        var row = new Panel { Height = 28, Width = 206 };
        row.Controls.Add(new Label
        {
            AutoSize = false,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Location = new Point(0, 6),
            Text = caption,
            Width = 74,
        });
        field.Location = new Point(78, 2);
        row.Controls.Add(field);
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
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(selected.FullPath));
            if (!document.RootElement.TryGetProperty("source", out System.Text.Json.JsonElement source)
                || source.ValueKind != System.Text.Json.JsonValueKind.String
                || string.IsNullOrWhiteSpace(source.GetString())) return;
            string relative = source.GetString()!.Replace('\\', '/');
            _document.Source = relative;
            PopulateSources();
            SelectSource(relative);
            Stop();
            DecodeWaveform();
            MarkDirty();
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
            Filter = "Supported audio (*.wav;*.ogg;*.mp3)|*.wav;*.ogg;*.mp3|WAV audio (*.wav)|*.wav|Ogg Vorbis (*.ogg)|*.ogg|MP3 audio (*.mp3)|*.mp3|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;

        string source = Path.GetFullPath(dialog.FileName);
        if (!IsSupportedAudioExtension(Path.GetExtension(source)))
        {
            MessageBox.Show(FindForm(), "Choose a WAV, OGG or MP3 audio file.", "Import Audio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string audioDirectory = Path.Combine(ProjectRoot, "Assets", AssetKindNames.AudioFolder);
        Directory.CreateDirectory(audioDirectory);
        string destination = Path.Combine(audioDirectory, Path.GetFileName(source));
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            destination = UniqueImportPath(destination);
        if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            File.Copy(source, destination, overwrite: false);

        string relative = Path.GetRelativePath(ProjectRoot, destination).Replace('\\', '/');
        _document.Source = relative;
        PopulateSources();
        SelectSource(relative);
        Stop();
        DecodeWaveform();
        MarkDirty();
        _statusLabel.Text = $"Imported {ResourceDisplayName.Format(relative)} into Assets/Audio.";
    }

    private static bool IsSupportedAudioExtension(string extension) =>
        extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);

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
            : Path.Combine(ProjectRoot, _document.Source.Replace('/', Path.DirectorySeparatorChar));

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
            _audio ??= new XAudioSystem(ProjectRoot);
        }
        catch (Exception exception) when (exception is DllNotFoundException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            // No audio device (headless test host, RDP session without redirection).
            _statusLabel.Text = "No audio output device — settings still save and apply in game.";
            return;
        }

        int soundId = _audio.LoadSound(_document.Source!);
        if (soundId == 0)
        {
            _statusLabel.Text = "Could not decode " + _document.Source;
            return;
        }

        ApplyListener();
        _channel = _audio.PlayAt(
            soundId,
            new System.Numerics.Vector3(0f, 0f, 0f),
            _document.Volume,
            _document.Pitch,
            _document.Loop);

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
        _waveformMin = [];
        _waveformMax = [];
        _loadedInfo = null;
        string? path = ResolveSourcePath();
        if (path is null || !File.Exists(path))
        {
            _statusLabel.Text = "Pick a WAV source to preview its waveform.";
            _waveformPanel.Invalidate();
            return;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            {
                _statusLabel.Text = "Not a RIFF WAV file.";
                _waveformPanel.Invalidate();
                return;
            }

            int channels = BitConverter.ToInt16(bytes, 22);
            int sampleRate = BitConverter.ToInt32(bytes, 24);
            int bitsPerSample = BitConverter.ToInt16(bytes, 34);

            // Find the data chunk.
            int offset = 12;
            int dataOffset = -1;
            int dataLength = 0;
            while (offset + 8 <= bytes.Length)
            {
                int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
                if (bytes[offset] == 'd' && bytes[offset + 1] == 'a' && bytes[offset + 2] == 't' && bytes[offset + 3] == 'a')
                {
                    dataOffset = offset + 8;
                    dataLength = Math.Min(chunkSize, bytes.Length - dataOffset);
                    break;
                }

                offset += 8 + chunkSize + (chunkSize & 1);
            }

            if (dataOffset < 0 || bitsPerSample != 16 || channels < 1)
            {
                _statusLabel.Text = $"WAV loaded ({bitsPerSample}-bit, {channels} ch) — waveform preview supports 16-bit PCM.";
                _waveformPanel.Invalidate();
                return;
            }

            int sampleCount = dataLength / 2 / channels;
            const int columns = 640;
            _waveformMin = new float[columns];
            _waveformMax = new float[columns];
            for (int column = 0; column < columns; column++)
            {
                int start = (int)((long)column * sampleCount / columns);
                int end = (int)((long)(column + 1) * sampleCount / columns);
                float min = 0f;
                float max = 0f;
                for (int sample = start; sample < end; sample++)
                {
                    short value = BitConverter.ToInt16(bytes, dataOffset + sample * channels * 2);
                    float f = value / 32768f;
                    min = MathF.Min(min, f);
                    max = MathF.Max(max, f);
                }

                _waveformMin[column] = min;
                _waveformMax[column] = max;
            }

            double seconds = sampleCount / (double)Math.Max(1, sampleRate);
            _loadedInfo = $"{sampleRate} Hz · {channels} ch · 16-bit · {seconds:0.00}s";
            _statusLabel.Text = $"{_document.Source} — {_loadedInfo}";
        }
        catch (Exception exception) when (exception is IOException or ArgumentOutOfRangeException)
        {
            _statusLabel.Text = "Could not decode WAV: " + exception.Message;
        }

        _waveformPanel.Invalidate();
    }

    private void PaintWaveform(object? sender, PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(EditorChrome.Canvas);
        Rectangle bounds = _waveformPanel.ClientRectangle;
        int midY = bounds.Height / 2;

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
        float half = bounds.Height * 0.42f * Math.Clamp(_document.Volume, 0.02f, 1f);
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
            graphics.DrawString(_loadedInfo, EditorChrome.SmallFont, text, 10, 8);
        }
    }

    private void PaintFalloffCurve(object? sender, PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(EditorChrome.Canvas);
        Rectangle bounds = _curvePanel.ClientRectangle;
        var plot = new Rectangle(48, 22, Math.Max(10, bounds.Width - 72), Math.Max(10, bounds.Height - 44));

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
        graphics.DrawString("0.0", EditorChrome.SmallFont, muted, 12, plot.Bottom - 12);
        graphics.DrawString($"{far:0}u", EditorChrome.SmallFont, muted, plot.Right - 26, plot.Bottom + 4);
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


