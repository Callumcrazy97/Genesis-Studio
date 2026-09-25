using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Particles;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Visual properties or a validated declarative emitter definition.</summary>
public enum ParticleAuthoringMode
{
    Properties,
    Code,
}

/// <summary>
/// Particle authoring against the exact <see cref="ParticleConfig"/> and
/// <see cref="ParticleSimulation"/> used by the runtime. Legacy editor-only documents are migrated
/// on load; saves always use the canonical flat runtime schema.
/// </summary>
public sealed partial class ParticleEditorControl : EditorSurfaceControl, IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private readonly EditorViewport3D _viewport;
    private readonly Label _statusLabel;
    private readonly ParticleSimulation _simulation = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, NumericUpDown> _numericControls = new(StringComparer.Ordinal);
    private readonly ToolStripDropDownButton _presetMenu = new("Preset");
    private readonly ThemedComboBox _shapeCombo = new() { Width = 72 };
    private readonly ThemedComboBox _sizeCurveCombo = new();
    private readonly ThemedComboBox _alphaCurveCombo = new();
    private readonly ToolStripButton _loopButton;
    private readonly EditorDimensionChrome.DimensionToggle _dimensionToggle;
    private readonly TabControl _inspectorTabs;
    private readonly ParticleLifetimePreview _lifetimePreview;
    private readonly TrackBar _timeline;
    private readonly Button _playPauseButton;
    private readonly Label _timelineLabel;
    private readonly Panel _authoringHost;
    private readonly Panel _propertiesSurface;
    private readonly Panel _codeSurface;
    private readonly CodeEditor _code;
    private readonly ToolStrip _modeRail;
    private readonly Dictionary<ParticleAuthoringMode, ToolStripButton> _modeButtons = [];
    private readonly System.Windows.Forms.Timer _codeApplyTimer;
    private ParticleConfig _config;
    private ParticleConfig _effect;
    private SpriteDrawCall[] _spriteCalls = new SpriteDrawCall[1];
    private double _lastTime;
    private string _activePreset = "Custom";
    private bool _syncing;
    private bool _syncingCode;
    private bool _timelinePlaying = true;
    private double _timelineSeconds;
    private EditorFloorStyle _floorStyle = EditorFloorStyle.Checkerboard;
    private ParticleAuthoringMode _authoringMode = ParticleAuthoringMode.Properties;

    public ParticleEditorControl(string resourcePath, string projectRoot)
        : this(resourcePath, projectRoot, LoadConfig(resourcePath))
    {
    }

    // Validate source syntax before the base control subscribes to global theme/document events.
    private ParticleEditorControl(string resourcePath, string projectRoot, ParticleConfig loadedConfig)
        : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        _config = loadedConfig;
        NormaliseConfig(_config);
        _effect = _config;
        EnsureEffectGradients(_effect);
        _activePreset = ParticlePresets.Names.Contains(_effect.EffectName, StringComparer.Ordinal)
            ? _effect.EffectName
            : "Custom";
        _simulation.LoadConfig(_config);

        EditorCommandBar toolbar = EditorChrome.MakeToolbar();
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildFileMenu(this));
        toolbar.Items.Add(EditorDocumentMenuChrome.BuildEditMenu(this));
        toolbar.Items.Add(new ToolStripSeparator());
        _presetMenu.ForeColor = EditorChrome.Text;
        _presetMenu.ToolTipText = "Apply a runtime particle preset";
        _presetMenu.AccessibleName = "Particle preset";
        foreach (string name in ParticlePresets.Names)
        {
            string captured = name;
            _presetMenu.DropDownItems.Add(name, null, (_, _) => ApplyPreset(captured));
        }
        toolbar.Items.Add(ToolbarCaption("Preset"));
        toolbar.Items.Add(_presetMenu);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(ToolbarCaption("Preview"));
        _loopButton = EditorChrome.ToolButton("Loop", "Toggle continuous emission", () =>
        {
            if (!PrepareParticleOperation()) { SyncControls(); return; }
            _config.Loop = !_config.Loop;
            _activePreset = "Custom";
            ConfigChanged(reset: true);
        }, toggle: true);
        _loopButton.Checked = _config.Loop;
        toolbar.Items.Add(_loopButton);
        toolbar.Items.Add(EditorChrome.ToolButton("Burst", "Emit the configured one-shot burst", Burst));

        // Properties on the left (Suite convention) with Properties/Code authoring modes —
        // matching Shader Editor so particles are not a right-rail outlier.
        _propertiesSurface = new Panel
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
        };
        _inspectorTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = EditorChrome.BaseFont,
        };
        FlowLayoutPanel emissionPage = AddInspectorPage("Emission");
        FlowLayoutPanel motionPage = AddInspectorPage("Motion");
        FlowLayoutPanel appearancePage = AddInspectorPage("Appearance");
        FlowLayoutPanel lifetimePage = AddInspectorPage("Lifetime");
        FlowLayoutPanel collisionPage = AddInspectorPage("Collision");
        FlowLayoutPanel rendererPage = AddInspectorPage("Renderer");
        _propertiesSurface.Controls.Add(_inspectorTabs);
        _propertiesSurface.Controls.Add(EditorChrome.SectionLabel("Particle Properties"));

        _code = new CodeEditor { Dock = DockStyle.Fill };
        _code.SetRules(BuildJsonRules());
        _codeSurface = new Panel
        {
            BackColor = EditorChrome.Canvas,
            Dock = DockStyle.Fill,
            Visible = false,
        };
        _codeSurface.Controls.Add(_code);
        _codeApplyTimer = new System.Windows.Forms.Timer { Interval = 450 };
        _codeApplyTimer.Tick += (_, _) =>
        {
            _codeApplyTimer.Stop();
            ApplyCodeFromEditor();
        };
        _code.TextChangedByUser += (_, _) =>
        {
            if (_syncingCode) return;
            _codeDraftDirty = !string.Equals(_code.CodeText, _appliedCode, StringComparison.Ordinal);
            ShowParticleDraftMessage(null);
            SyncParticleDirtyState();
            _codeApplyTimer.Stop();
            if (_codeDraftDirty) _codeApplyTimer.Start();
        };

        _authoringHost = EditorChrome.SidePanel(EditorChrome.LeftPanelWidth, DockStyle.Left);
        _authoringHost.Controls.Add(_codeSurface);
        _authoringHost.Controls.Add(_propertiesSurface);

        _modeRail = EditorChrome.MakeModeRail(EditorChrome.ModeRailWidth);
        foreach (ParticleAuthoringMode mode in Enum.GetValues<ParticleAuthoringMode>())
        {
            ParticleAuthoringMode captured = mode;
            ToolStripButton button = EditorChrome.ModeButton(
                mode.ToString(),
                mode == ParticleAuthoringMode.Properties
                    ? "Edit emission, motion and appearance with the property inspector"
                    : "Edit the selected emitter definition with inline validation",
                () => SetAuthoringMode(captured));
            _modeButtons[mode] = button;
            _modeRail.Items.Add(button);
        }

        foreach (ParticleEmitShape shape in Enum.GetValues<ParticleEmitShape>()) _shapeCombo.Items.Add(shape);
        _shapeCombo.SelectedItem = _config.Shape;
        EditorChrome.StyleField(_shapeCombo);
        _shapeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _shapeCombo.SelectedItem is ParticleEmitShape shape)
            {
                SetShape(shape);
            }
        };
        AddInspectorRow(emissionPage, "Shape", _shapeCombo);
        AddNumeric(emissionPage, "Rate", "rate", _config.EmitRate, 0, 5000, value => _config.EmitRate = value);
        AddNumeric(emissionPage, "Lifetime", "life", _config.Lifetime, 0.01, 3600, value => _config.Lifetime = value, 2);
        AddNumeric(emissionPage, "Speed", "speed", _config.Speed, 0, 100, value => _config.Speed = value, 2);
        AddNumeric(emissionPage, "Spread °", "spread", _config.SpreadDegrees, 0, 360, value => _config.SpreadDegrees = value, 1);

        AddNumeric(motionPage, "Gravity", "gravity", _config.Gravity, -100, 100, value => _config.Gravity = value, 2);
        AddNumeric(motionPage, "Drag", "drag", _config.Drag, 0, 20, value => _config.Drag = value, 2);
        AddNumeric(motionPage, "Turbulence", "turbulence", _config.TurbulenceStrength, 0, 20, value => _config.TurbulenceStrength = value, 2);

        AddNumeric(appearancePage, "Start size", "startSize", _config.StartSize, 0.01, 20, value => _config.StartSize = value, 2);
        AddNumeric(appearancePage, "End size", "endSize", _config.EndSize, 0, 20, value => _config.EndSize = value, 2);
        foreach (ParticleInterpolationCurve curve in Enum.GetValues<ParticleInterpolationCurve>())
        {
            _sizeCurveCombo.Items.Add(curve);
            _alphaCurveCombo.Items.Add(curve);
        }
        EditorChrome.StyleField(_sizeCurveCombo);
        EditorChrome.StyleField(_alphaCurveCombo);
        _sizeCurveCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _sizeCurveCombo.SelectedItem is ParticleInterpolationCurve curve)
            {
                _config.SizeCurve = curve;
                ConfigChanged(reset: false);
            }
        };
        _alphaCurveCombo.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _alphaCurveCombo.SelectedItem is ParticleInterpolationCurve curve)
            {
                _config.AlphaCurve = curve;
                ConfigChanged(reset: false);
            }
        };
        AddInspectorRow(appearancePage, "Size curve", _sizeCurveCombo);
        AddInspectorRow(appearancePage, "Alpha curve", _alphaCurveCombo);
        _lifetimePreview = new ParticleLifetimePreview(() => _config)
        {
            Height = 112,
            Margin = new Padding(0, 4, 0, 10),
            Width = 254,
        };
        appearancePage.Controls.Add(_lifetimePreview);
        Control startColor = MakeColorWell(() => _config.StartColor, () => PickColor(start: true));
        Control midColor = MakeColorWell(() => _config.MidColor ?? new ParticleColor((_config.StartColor.R + _config.EndColor.R) * 0.5f, (_config.StartColor.G + _config.EndColor.G) * 0.5f, (_config.StartColor.B + _config.EndColor.B) * 0.5f, 1f), PickMidColor);
        Control endColor = MakeColorWell(() => _config.EndColor, () => PickColor(start: false));
        Button clearMidColor = MakeInspectorButton("Clear midpoint", ClearMidColor);
        AddInspectorRow(appearancePage, "Gradient start", startColor);
        AddInspectorRow(appearancePage, "Gradient middle", midColor);
        AddNumeric(appearancePage, "Midpoint", "colorMidpoint", _config.ColorMidpoint, 0.05, 0.95,
            value => _config.ColorMidpoint = value, 2);
        AddInspectorRow(appearancePage, "", clearMidColor);
        AddInspectorRow(appearancePage, "Gradient end", endColor);
        AddInfoCard(lifetimePage, "Lifetime curve", "Size and alpha curves are shown here so lifetime editing has its own home.");
        AddInfoCard(collisionPage, "Collision", "Runtime collision hooks are planned here; current preview is free-flight.");
        AddInfoCard(rendererPage, "Renderer", "Blend, billboard and budget controls will live here as the renderer surface expands.");

        _viewport = new EditorViewport3D
        {
            Mode2D = _config.Preview2D,
            Background2D = () => (0.05f, 0.06f, 0.10f),
            SceneStateFactory = () => EditorSceneLighting.Create(
                showFloor: _floorStyle.DrawsPlate() && !_effect.Preview2D),
        };
        _viewport.Camera.Target = new Vector3(0f, 1.5f, 0f);
        _viewport.Camera.Distance = 9f;
        _viewport.FloorStyle = _floorStyle;
        _viewport.DrawScene2D += DrawParticles2D;
        _viewport.DrawScene += DrawParticles3D;
        _viewport.DrawOverlay += DrawParticleViewportOverlay;
        _viewport.SelectionWorldPoint = () => Vector3.Zero;
        EditorViewportChrome.Attached chrome = EditorViewportChrome.Attach(
            toolbar,
            new EditorViewportChrome.Options
            {
                Viewport = _viewport,
                GetIs2D = () => _effect.Preview2D,
                SetIs2D = SetPreview2D,
                ViewTooltip = "Grid and reference-floor options for the particle preview",
                FloorStyle = new EditorViewMenuChrome.FloorStyleBinding
                {
                    Read = () => _floorStyle,
                    Write = SetFloorStyle,
                    Invalidate = () => _viewport.Host.Invalidate(),
                },
                IncludeGizmo = false,
                Invalidate = () => _viewport.Invalidate(true),
                IncludeRotateGizmo = false,
                IncludeScaleGizmo = false,
            });
        _dimensionToggle = chrome.Dimension
            ?? throw new InvalidOperationException("Particle Editor requires the shared dimension chrome.");

        _statusLabel = EditorChrome.MakeStatusBar();

        Panel timelinePanel = new()
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Bottom,
            Height = 78,
            Padding = EditorChrome.CompactInsets,
        };
        _playPauseButton = new Button
        {
            AccessibleName = "Play or pause particle preview",
            Dock = DockStyle.Left,
            Text = "Pause",
            Width = 70,
        };
        EditorChrome.StyleField(_playPauseButton);
        _playPauseButton.Click += (_, _) => ToggleTimelinePlayback();
        Button timelineRestart = new()
        {
            AccessibleName = "Restart particle timeline",
            Dock = DockStyle.Left,
            Text = "Restart",
            Width = 72,
        };
        EditorChrome.StyleField(timelineRestart);
        timelineRestart.Click += (_, _) => Restart();
        _timelineLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Right,
            ForeColor = EditorChrome.Muted,
            TextAlign = ContentAlignment.MiddleRight,
            Width = 92,
        };
        _timeline = new TrackBar
        {
            AutoSize = false,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            Height = 34,
            Maximum = 1000,
            TickStyle = TickStyle.None,
        };
        _timeline.Scroll += (_, _) => SetTimelinePosition(_timeline.Value / 1000f);
        timelinePanel.Controls.Add(_timeline);
        timelinePanel.Controls.Add(_timelineLabel);
        timelinePanel.Controls.Add(timelineRestart);
        timelinePanel.Controls.Add(_playPauseButton);

        Controls.Add(_viewport);
        Controls.Add(_authoringHost);
        Controls.Add(_modeRail);
        Controls.Add(timelinePanel);
        Controls.Add(toolbar);
        Controls.Add(_statusLabel);
        BuildReferenceWorkspace(toolbar, timelinePanel);
        PushCodeFromConfig();
        SetAuthoringMode(ParticleAuthoringMode.Properties);
        SyncControls();
        UpdateStatus();
        InitialiseParticleAuthoring();
    }

    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        base.OnAssetDependenciesChanged(changes);
        RebuildEmitterPreview();
        InvalidatePreviewTarget();
        _timelineSeconds = 0d;
        _viewport.Invalidate(true);
        UpdateStatus();
    }

    /// <summary>A detached copy of the runtime configuration currently being authored.</summary>
    public ParticleConfig Config => _effect.Clone();
    public EditorViewport3D Viewport => _viewport;
    public ParticleAuthoringMode AuthoringMode => _authoringMode;
    public int LiveParticleCount => PreviewLiveParticleCount;
    public string ActivePreset => _activePreset;
    public bool TimelinePlaying => _timelinePlaying;
    public bool ShowEditorFloor => _floorStyle.DrawsPlate();
    public EditorFloorStyle FloorStyle => _floorStyle;
    public float TimelinePosition => _timeline.Value / 1000f;
    public IReadOnlyList<string> InspectorSections => _inspectorTabs.TabPages.Cast<TabPage>().Select(page => page.Text).ToArray();
    public int ActiveInspectorSection
    {
        get => _inspectorTabs.SelectedIndex;
        set => _inspectorTabs.SelectedIndex = Math.Clamp(value, 0, _inspectorTabs.TabCount - 1);
    }

    public void SetLifetimeCurves(ParticleInterpolationCurve size, ParticleInterpolationCurve alpha)
    {
        if (!PrepareParticleOperation()) return;
        if (!Enum.IsDefined(size) || !Enum.IsDefined(alpha)) throw new ArgumentOutOfRangeException(nameof(size));
        _config.SizeCurve = size;
        _config.AlphaCurve = alpha;
        _activePreset = "Custom";
        ConfigChanged(reset: false);
        SyncControls();
    }

    public void SetGradientMidpoint(ParticleColor? color, double position)
    {
        if (!PrepareParticleOperation()) return;
        if (!double.IsFinite(position)) throw new ArgumentOutOfRangeException(nameof(position));
        _config.MidColor = color?.Clone();
        _config.ColorMidpoint = Math.Clamp(position, 0.05, 0.95);
        SyncLegacyGradientStops(_config);
        _activePreset = "Custom";
        ConfigChanged(reset: false);
        SyncControls();
    }

    public void ApplyPreset(string name)
    {
        if (!PrepareParticleOperation()) return;
        ParticlePreviewTargetType previewTargetType = _effect.PreviewTargetType;
        string previewTargetAsset = _effect.PreviewTargetAsset;
        bool preview2D = _effect.Preview2D;
        _effect = ParticlePresets.Create(name);
        _effect.PreviewTargetType = previewTargetType;
        _effect.PreviewTargetAsset = previewTargetAsset;
        _effect.Preview2D = preview2D;
        EnsureEffectGradients(_effect);
        _selectedEmitterIndex = 0;
        _config = _effect;
        _activePreset = ParticlePresets.Names.Contains(name, StringComparer.Ordinal) ? name : "Fire";
        _effect.EffectName = _activePreset;
        _presetMenu.Text = "Preset: " + _activePreset;
        RebuildEmitterPreview();
        ResetParticlePreview(true);
        RefreshEmitterStack();
        SyncControls();
        PushCodeFromConfig();
        CommitParticleEdit("Apply " + name + " preset");
        UpdateStatus();
    }

    public void SetShape(ParticleEmitShape shape)
    {
        if (!PrepareParticleOperation()) return;
        if (!Enum.IsDefined(shape)) throw new ArgumentOutOfRangeException(nameof(shape));
        _config.Shape = shape;
        _activePreset = "Custom";
        ConfigChanged(reset: false);
        SyncControls();
    }

    public void SetPreview2D(bool enabled)
    {
        if (!PrepareParticleOperation()) return;
        _effect.Preview2D = enabled;
        _viewport.Mode2D = enabled;
        _dimensionToggle.Sync(enabled);
        if (_preview2DCheck is not null && _preview2DCheck.Checked != enabled)
        {
            bool wasSyncing = _syncing;
            _syncing = true;
            try { _preview2DCheck.Checked = enabled; }
            finally { _syncing = wasSyncing; }
        }
        CommitParticleEdit("Change particle preview dimension");
        _viewport.Invalidate(true);
        UpdateStatus();
    }

    public void SetEditorFloorVisible(bool visible) =>
        SetFloorStyle(visible ? EditorFloorStyle.Checkerboard : EditorFloorStyle.None);

    public void SetFloorStyle(EditorFloorStyle style)
    {
        _floorStyle = style;
        _viewport.FloorStyle = style;
        bool wasSyncing = _syncing;
        _syncing = true;
        try { if (_previewFloorCheck is not null) _previewFloorCheck.Checked = style.DrawsPlate(); }
        finally { _syncing = wasSyncing; }
        _viewport.Host.Invalidate();
        UpdateStatus();
    }

    private void DrawParticleViewportOverlay(IRenderController renderer)
    {
        if (!_effect.Preview2D && _floorStyle == EditorFloorStyle.GridOnly)
            EditorViewportGridHelper.DrawGrid3D(
                _viewport,
                renderer,
                cellSize: 1f,
                rgba: [1f, 1f, 1f, 0.08f],
                horizontalExtent: 8f,
                verticalExtent: 4f);

        string target = string.IsNullOrWhiteSpace(_effect.PreviewTargetAsset)
            ? "Free preview"
            : ResourceDisplayName.Format(_effect.PreviewTargetAsset);
        renderer.DrawRect(9, 9, Math.Min(330, renderer.PixelWidth - 18), 28, new RenderColor(0.03f, 0.045f, 0.065f, 0.78f), true);
        renderer.DrawText($"{renderer.BackendName} · 60 Hz simulation · {target}", 18, 15, 13, new RenderColor(0.82f, 0.88f, 0.96f));

        if (_effect.Preview2D) return;
        float cx = renderer.PixelWidth * 0.5f;
        float cy = renderer.PixelHeight * 0.58f;
        float radius = Math.Clamp((float)_config.EmitRadius * 24f, 18f, 92f);
        RenderColor cyan = new(0.25f, 0.78f, 1f, 0.9f);
        if (_config.Shape == ParticleEmitShape.Box)
        {
            renderer.DrawRect(cx - radius, cy - radius * 0.55f, radius * 2, radius * 1.1f, cyan, false);
        }
        else
        {
            const int segments = 28;
            float px = cx + radius;
            float py = cy;
            for (int i = 1; i <= segments; i++)
            {
                float angle = i / (float)segments * MathF.Tau;
                float nx = cx + MathF.Cos(angle) * radius;
                float ny = cy + MathF.Sin(angle) * radius * 0.36f;
                renderer.DrawLine(px, py, nx, ny, cyan, 1.2f);
                px = nx; py = ny;
            }
        }
        if (_config.Shape is ParticleEmitShape.Cone or ParticleEmitShape.Point)
        {
            float height = Math.Clamp((float)_config.Speed * 18f, 44f, 150f);
            renderer.DrawLine(cx - radius, cy, cx, cy - height, cyan, 1.2f);
            renderer.DrawLine(cx + radius, cy, cx, cy - height, cyan, 1.2f);
        }
    }

    public void Burst()
    {
        CancelPreviewSeek();
        BurstPreview();
        UpdateStatus();
    }

    public void Restart()
    {
        ResetParticlePreview(_timelinePlaying);
        UpdateTimelineVisual(); UpdateStatus();
    }

    public void ToggleTimelinePlayback()
    {
        _previewSeekTimer.Stop();
        _previewClock.SetPlaying(!_timelinePlaying);
        SyncPreviewClock();
        _lastTime = _clock.Elapsed.TotalSeconds;
        UpdateTimelineVisual();
        _viewport.Invalidate(true);
    }

    public void SetTimelinePosition(float normalised)
    {
        if (!float.IsFinite(normalised)) return;
        float position = Math.Clamp(normalised, 0f, 1f);
        ResetParticlePreview(false);
        _previewClock.BeginSeek(position * PreviewDuration);
        SyncPreviewClock();
        UpdateTimelineVisual(); UpdateStatus();
        if (_previewClock.Seeking) _previewSeekTimer.Start();
    }

    private double PreviewDuration => Math.Clamp(_effect.Duration, 0.05, ParticlePreviewClock.MaximumSeekSeconds);

    /// <summary>Steps the real runtime simulation for deterministic headless editor tests.</summary>
    public void StepForTest(float deltaSeconds)
    {
        _previewSeekTimer.Stop();
        while (_previewClock.Seeking) _previewClock.PumpSeek(StepPreviewSimulations);
        if (float.IsFinite(deltaSeconds) && deltaSeconds > 0)
        {
            int frames = (int)Math.Round(Math.Min(deltaSeconds, 60) / ParticlePreviewClock.StepSeconds);
            bool playing = _previewClock.Playing;
            for (int i = 0; i < frames; i++) _previewClock.StepOne(StepPreviewSimulations);
            _previewClock.SetPlaying(playing);
        }
        SyncPreviewClock(); UpdateTimelineVisual(); UpdateStatus();
    }

    public override void Save()
    {
        FinishParticleGesture();
        if (!TryApplyParticleDraft())
            throw new InvalidOperationException("Particle definition has unapplied errors: " + _draftError);
        SaveJson(_effect);
        _savedParticleContent = JsonSerializer.Serialize(_effect, JsonOptions);
        AcceptSave();
    }

    public void SetAuthoringMode(ParticleAuthoringMode mode)
    {
        if (mode == ParticleAuthoringMode.Code && _authoringMode == ParticleAuthoringMode.Properties)
            PushCodeFromConfig();
        if (mode == ParticleAuthoringMode.Properties && _authoringMode == ParticleAuthoringMode.Code
            && !TryApplyParticleDraft()) return;

        _authoringMode = mode;
        bool codeVisible = mode == ParticleAuthoringMode.Code;
        _codeSurface.Visible = codeVisible;
        if (_referenceAuthoringSplit is not null)
            _referenceAuthoringSplit.Panel1Collapsed = !codeVisible;
        if (mode == ParticleAuthoringMode.Code) _code.Focus();
        else _inspectorTabs.Focus();
        foreach ((ParticleAuthoringMode key, ToolStripButton button) in _modeButtons)
            button.Checked = key == mode;
        UpdateStatus();
    }

    private void PushCodeFromConfig()
    {
        _syncingCode = true;
        try
        {
            _code.CodeText = ParticleCodeCodec.Serialize(_config);
            _appliedCode = _code.CodeText;
            _codeDraftDirty = false;
            ShowParticleDraftMessage(null);
        }
        finally
        {
            _syncingCode = false;
        }
    }

    private void ApplyCodeFromEditor(bool force = false)
    {
        if (_syncingCode && !force) return;
        TryApplyParticleDraft();
    }

    private static List<HighlightRule> BuildJsonRules()
    {
        Color keyword = EditorChrome.FromHex("#56D4E4");
        Color key = EditorChrome.FromHex("#9CDCFE");
        Color value = EditorChrome.FromHex("#DCDCAA");
        Color number = EditorChrome.FromHex("#B5CEA8");
        Color comment = EditorChrome.FromHex("#6A9955");
        return
        [
            new HighlightRule(new Regex(@"\b(particle|true|false|Point|Cone|Sphere|Disc|Ring|Box|MeshSurface|Alpha|Additive|Multiply|Billboard|Velocity|Horizontal|Mesh3D|Bounce|Die|Stick)\b", RegexOptions.Compiled), keyword),
            new HighlightRule(new Regex(@"\b(?:emission|motion|lifetime|appearance|render|collision)\.[A-Za-z]+(?=\s*:)", RegexOptions.Compiled), key),
            new HighlightRule(new Regex(@"""[^""\\]*(?:\\.[^""\\]*)*""", RegexOptions.Compiled), value),
            new HighlightRule(new Regex(@"\b-?\d+(?:\.\d+)?\b", RegexOptions.Compiled), number),
            new HighlightRule(new Regex(@"//.*$", RegexOptions.Compiled | RegexOptions.Multiline), comment),
        ];
    }

    private void AddNumeric(FlowLayoutPanel page, string caption, string key, double value,
        double min, double max, Action<double> apply, int decimals = 0)
    {
        NumericUpDown numeric = new()
        {
            DecimalPlaces = decimals,
            Increment = decimals > 0 ? 0.05m : 5m,
            Maximum = (decimal)max,
            Minimum = (decimal)min,
            Value = (decimal)Math.Clamp(value, min, max),
            Width = 140,
        };
        EditorChrome.StyleField(numeric);
        numeric.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            apply((double)numeric.Value);
            _activePreset = "Custom";
            ConfigChanged(reset: false);
        };
        _numericControls.Add(key, numeric);
        AddInspectorRow(page, caption, numeric);
    }

    private FlowLayoutPanel AddInspectorPage(string title)
    {
        TabPage page = new(title)
        {
            BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Text,
            Padding = new Padding(0),
        };
        FlowLayoutPanel content = new()
        {
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = EditorChrome.PanelInsets,
            WrapContents = false,
        };
        page.Controls.Add(content);
        _inspectorTabs.TabPages.Add(page);
        return content;
    }

    private static void AddInspectorRow(FlowLayoutPanel page, string caption, Control field)
    {
        TableLayoutPanel row = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            Height = 36,
            Margin = new Padding(0, 0, 0, 4),
            Width = 254,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104f));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        Label label = new()
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = EditorChrome.Muted,
            Text = caption,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, 4, 0, 4);
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(field, 1, 0);
        page.Controls.Add(row);
    }

    private static void AddInfoCard(FlowLayoutPanel page, string title, string body)
    {
        Panel card = new()
        {
            BackColor = EditorChrome.Raised,
            Height = 82,
            Margin = new Padding(0, 0, 0, 8),
            Padding = EditorChrome.CompactInsets,
            Width = 254,
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
        page.Controls.Add(card);
    }

    private Control MakeColorWell(Func<ParticleColor> getColor, Action onClick)
    {
        Panel well = new()
        {
            Height = 28,
            BorderStyle = BorderStyle.None,
            Cursor = Cursors.Hand,
        };
        well.Paint += (_, e) =>
        {
            ParticleColor c = getColor();
            using SolidBrush b = new(Color.FromArgb(255, (int)Math.Clamp(c.R * 255, 0, 255), (int)Math.Clamp(c.G * 255, 0, 255), (int)Math.Clamp(c.B * 255, 0, 255)));
            e.Graphics.FillRectangle(b, well.ClientRectangle);
        };
        well.Click += (_, _) => onClick();
        return well;
    }

    private static Button MakeInspectorButton(string text, Action onClick)
    {
        Button button = new() { Text = text, Height = 28 };
        EditorChrome.StyleField(button);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void ConfigChanged(bool reset)
    {
        CancelPreviewSeek();
        NormaliseConfig(_config);
        UpdateSelectedEmitterPreview(reset);
        _lifetimePreview.Invalidate();
        if (_authoringMode == ParticleAuthoringMode.Properties && !_syncingCode && !_particleGesture)
            PushCodeFromConfig();
        CommitParticleEdit();
        UpdateConditionalParticleFields();
        if (!_particleGesture) SyncControls();
        _inspectorTabs.Invalidate(true);
        _curveEditor?.Invalidate();
        _viewport.Invalidate(true);
        UpdateStatus();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? InspectorStateChanged;

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues() =>
    [
        new("Emission", "emitRate", "Emit rate", _config.EmitRate, Minimum: 0, Maximum: 5000, Increment: 5, DecimalPlaces: 0),
        new("Emission", "lifetime", "Lifetime", _config.Lifetime, Minimum: 0.01m, Maximum: 3600m, Increment: 0.05m, DecimalPlaces: 2),
        new("Emission", "speed", "Speed", _config.Speed, Minimum: 0, Maximum: 100, Increment: 0.05m, DecimalPlaces: 2),
        new("Emission", "spreadDegrees", "Spread °", _config.SpreadDegrees, Minimum: 0, Maximum: 360, Increment: 1, DecimalPlaces: 1),
        new("Emission", "shape", "Shape", _config.Shape.ToString(), Choices: Enum.GetNames<ParticleEmitShape>()),
        new("Emission", "loop", "Loop", _config.Loop),
        new("Motion", "gravity", "Gravity", _config.Gravity, Minimum: -100, Maximum: 100, Increment: 0.05m, DecimalPlaces: 2),
        new("Motion", "drag", "Drag", _config.Drag, Minimum: 0, Maximum: 20, Increment: 0.05m, DecimalPlaces: 2),
        new("Motion", "turbulenceStrength", "Turbulence", _config.TurbulenceStrength, Minimum: 0, Maximum: 20, Increment: 0.05m, DecimalPlaces: 2),
        new("Appearance", "startSize", "Start size", _config.StartSize, Minimum: 0.01m, Maximum: 20m, Increment: 0.05m, DecimalPlaces: 2),
        new("Appearance", "endSize", "End size", _config.EndSize, Minimum: 0, Maximum: 20, Increment: 0.05m, DecimalPlaces: 2),
        new("Renderer", "alignment", "Alignment", _config.Alignment.ToString(), Choices: Enum.GetNames<ParticleAlignment>()),
        new("Renderer", "blendMode", "Blend", _config.BlendMode.ToString(), Choices: Enum.GetNames<ParticleBlendMode>()),
        new("Lighting", "light.enabled", "Emit light", _effect.Light.Enabled),
        new("Lighting", "light.intensity", "Light intensity", _effect.Light.Intensity, Minimum: 0, Maximum: 100, Increment: 0.05m, DecimalPlaces: 2),
        new("Lighting", "light.radius", "Light radius", _effect.Light.Radius, Minimum: 0.1m, Maximum: 1000, Increment: 0.1m, DecimalPlaces: 2),
    ];

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) =>
        TryApplyInspectorValue(propertyPath, value);

    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        if (!PrepareParticleOperation()) return false;
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        try
        {
            switch (propertyPath.ToLowerInvariant())
            {
                case "emitrate":
                    ApplyNumeric("rate", value, v => _config.EmitRate = v);
                    return true;
                case "lifetime":
                    ApplyNumeric("life", value, v => _config.Lifetime = v);
                    return true;
                case "speed":
                    ApplyNumeric("speed", value, v => _config.Speed = v);
                    return true;
                case "spreaddegrees":
                    ApplyNumeric("spread", value, v => _config.SpreadDegrees = v);
                    return true;
                case "gravity":
                    ApplyNumeric("gravity", value, v => _config.Gravity = v);
                    return true;
                case "drag":
                    ApplyNumeric("drag", value, v => _config.Drag = v);
                    return true;
                case "turbulencestrength":
                    ApplyNumeric("turbulence", value, v => _config.TurbulenceStrength = v);
                    return true;
                case "startsize":
                    ApplyNumeric("startSize", value, v => _config.StartSize = v);
                    return true;
                case "endsize":
                    ApplyNumeric("endSize", value, v => _config.EndSize = v);
                    return true;
                case "loop":
                    _config.Loop = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    SyncControls();
                    _activePreset = "Custom";
                    ConfigChanged(reset: true);
                    return true;
                case "shape":
                    if (!Enum.TryParse(text, ignoreCase: true, out ParticleEmitShape shape) || !Enum.IsDefined(shape)) return false;
                    SetShape(shape);
                    return true;
                case "alignment":
                    if (!Enum.TryParse(text, ignoreCase: true, out ParticleAlignment alignment) || !Enum.IsDefined(alignment)) return false;
                    _config.Alignment = alignment;
                    ConfigChanged(reset: false);
                    SyncControls();
                    return true;
                case "blendmode":
                    if (!Enum.TryParse(text, ignoreCase: true, out ParticleBlendMode blend) || !Enum.IsDefined(blend)) return false;
                    _config.BlendMode = blend;
                    ConfigChanged(reset: false);
                    SyncControls();
                    return true;
                case "light.enabled":
                    _effect.Light.Enabled = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    ConfigChanged(reset: false);
                    SyncControls();
                    return true;
                case "light.intensity":
                    _effect.Light.Intensity = ValidatedRange(value, 0, 100);
                    ConfigChanged(reset: false);
                    SyncControls();
                    return true;
                case "light.radius":
                    _effect.Light.Radius = ValidatedRange(value, .1, 1000);
                    ConfigChanged(reset: false);
                    SyncControls();
                    return true;
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

    private void ApplyNumeric(string controlKey, object? value, Action<double> apply)
    {
        double next = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(next)) throw new FormatException("Enter a finite number.");
        if (_numericControls.TryGetValue(controlKey, out NumericUpDown? control))
        {
            _syncing = true;
            try
            {
                next = Math.Clamp(next, (double)control.Minimum, (double)control.Maximum);
                control.Value = (decimal)next;
            }
            finally
            {
                _syncing = false;
            }
        }

        apply(next);
        _activePreset = "Custom";
        ConfigChanged(reset: false);
    }

    private static double ValidatedRange(object? value, double minimum, double maximum)
    {
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new FormatException("Enter a finite number.");
        return Math.Clamp(number, minimum, maximum);
    }

    private void SyncControls()
    {
        _syncing = true;
        try
        {
            _shapeCombo.SelectedItem = _config.Shape;
            _sizeCurveCombo.SelectedItem = _config.SizeCurve;
            _alphaCurveCombo.SelectedItem = _config.AlphaCurve;
            SetNumeric("rate", _config.EmitRate);
            SetNumeric("life", _config.Lifetime);
            SetNumeric("speed", _config.Speed);
            SetNumeric("spread", _config.SpreadDegrees);
            SetNumeric("gravity", _config.Gravity);
            SetNumeric("drag", _config.Drag);
            SetNumeric("turbulence", _config.TurbulenceStrength);
            SetNumeric("startSize", _config.StartSize);
            SetNumeric("endSize", _config.EndSize);
            SetNumeric("colorMidpoint", _config.ColorMidpoint);
            SetNumeric("maxParticles", _config.MaxParticles);
            SetNumeric("duration", _effect.Duration);
            SetNumeric("lifeMin", LifetimeMinimum(_config));
            SetNumeric("lifeMax", LifetimeMaximum(_config));
            SetNumeric("burstCount", _config.BurstCount);
            SetNumeric("emitRadius", _config.EmitRadius);
            SetNumeric("boxX", _config.BoxSizeX);
            SetNumeric("boxY", _config.BoxSizeY);
            SetNumeric("boxZ", _config.BoxSizeZ);
            SetNumeric("gravityX", _config.GravityX);
            SetNumeric("gravityZ", _config.GravityZ);
            SetNumeric("windX", _config.WindX);
            SetNumeric("windZ", _config.WindZ);
            SetNumeric("collisionHeight", _config.CollisionPlaneHeight);
            SetNumeric("collisionBounce", _config.CollisionBounce);
            SetNumeric("emissive", _config.Emissive);
            SetNumeric("colorJitter", _config.ColorJitter);
            SetNumeric("flipColumns", _config.FlipbookColumns);
            SetNumeric("flipRows", _config.FlipbookRows);
            SetNumeric("flipFps", _config.FlipbookFps);
            SetNumeric("lightRadius", _effect.Light.Radius);
            SetNumeric("lightPower", _effect.Light.Intensity);
            SetNumeric("lightFlicker", _effect.Light.FlickerAmount);
            SetNumeric("lightFalloff", _effect.Light.Falloff);
            SetNumeric("lightFrequency", _effect.Light.FlickerFrequency);
            SetNumeric("lightY", _effect.Light.OffsetY);
            _loopButton.Checked = _config.Loop;
            _dimensionToggle.Sync(_effect.Preview2D);
            _viewport.Mode2D = _effect.Preview2D;
            if (_preview2DCheck is not null) _preview2DCheck.Checked = _effect.Preview2D;
            _lifetimePreview.Invalidate();
            UpdateTimelineVisual();
            RefreshTargetButton();
            RefreshEmitterStack();
            SyncAdvancedControls();
            UpdateConditionalParticleFields();
            _curveEditor?.Invalidate();
            _inspectorTabs.Invalidate(true);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SetNumeric(string key, double value)
    {
        if (!_numericControls.TryGetValue(key, out NumericUpDown? control)) return;
        control.Value = (decimal)Math.Clamp(double.IsFinite(value) ? value : 0, (double)control.Minimum, (double)control.Maximum);
    }

    private void PickColor(bool start)
    {
        ParticleColor current = start ? _config.StartColor : _config.EndColor;
        using ColorDialog dialog = new()
        {
            Color = Color.FromArgb(
                (int)(Math.Clamp(current.R, 0f, 1f) * 255f),
                (int)(Math.Clamp(current.G, 0f, 1f) * 255f),
                (int)(Math.Clamp(current.B, 0f, 1f) * 255f)),
            FullOpen = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ParticleColor next = new(dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, current.A);
        if (start) _config.StartColor = next;
        else _config.EndColor = next;
        SyncLegacyGradientStops(_config);
        _activePreset = "Custom";
        ConfigChanged(reset: false);
    }

    private void PickMidColor()
    {
        ParticleColor start = _config.StartColor;
        ParticleColor end = _config.EndColor;
        ParticleColor current = _config.MidColor ?? new ParticleColor(
            (start.R + end.R) * 0.5f,
            (start.G + end.G) * 0.5f,
            (start.B + end.B) * 0.5f,
            (start.A + end.A) * 0.5f);
        using ColorDialog dialog = new()
        {
            Color = Color.FromArgb(
                (int)(Math.Clamp(current.R, 0f, 1f) * 255f),
                (int)(Math.Clamp(current.G, 0f, 1f) * 255f),
                (int)(Math.Clamp(current.B, 0f, 1f) * 255f)),
            FullOpen = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _config.MidColor = new ParticleColor(
            dialog.Color.R / 255f,
            dialog.Color.G / 255f,
            dialog.Color.B / 255f,
            current.A);
        SyncLegacyGradientStops(_config);
        _activePreset = "Custom";
        ConfigChanged(reset: false);
    }

    private void ClearMidColor()
    {
        _config.MidColor = null;
        SyncLegacyGradientStops(_config);
        _activePreset = "Custom";
        ConfigChanged(reset: false);
    }

    private void DrawParticles2D(IRenderController renderer)
    {
        StepClock();
        DrawEmitterStack2D(renderer);
    }

    private void DrawParticles3D(IRenderController renderer)
    {
        StepClock();
        DrawPreviewTarget(renderer);
        DrawEmitterStack3D(renderer);
    }

    private void StepClock()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double delta = Math.Clamp(now - _lastTime, 0.0, 0.1);
        _lastTime = now;
        if (!_previewClock.Seeking) _previewClock.Advance(delta, StepPreviewSimulations);
        SyncPreviewClock(); UpdateTimelineVisual(); UpdateStatus();
    }

    private void UpdateTimelineVisual()
    {
        if (_timeline is null || _timelineLabel is null || _playPauseButton is null) return;
        double duration = PreviewDuration;
        double display = _previewClock.Seeking ? _previewClock.SeekTarget
            : _timelinePlaying ? _timelineSeconds % duration : Math.Clamp(_timelineSeconds, 0, duration);
        int value = (int)Math.Round(Math.Clamp(display / duration, 0, 1) * _timeline.Maximum);
        if (_timeline.Value != value) _timeline.Value = Math.Clamp(value, _timeline.Minimum, _timeline.Maximum);
        _timelineLabel.Text = _previewClock.Seeking ? $"Seeking {_timelineSeconds:0.00}s" : $"{display:0.00} / {duration:0.00}s";
        _playPauseButton.Text = _timelinePlaying ? "Pause" : "Play";
        if (_topPlayButton is not null) _topPlayButton.Text = _timelinePlaying ? "Pause" : "Play";
    }

    private void EnsureSpriteCapacity()
    {
        int capacity = Math.Max(1, _simulation.Capacity);
        if (_spriteCalls.Length != capacity) _spriteCalls = new SpriteDrawCall[capacity];
    }

    private void UpdateStatus()
    {
        string preview = _effect.Preview2D ? "2D" : "3D";
        string floor = _effect.Preview2D ? "floor n/a" : _floorStyle.StatusLabel();
        string mode = _authoringMode == ParticleAuthoringMode.Code ? "Code" : "Properties";
        ParticleExecutionDecision execution = ParticleExecutionPolicy.Resolve(_particlePreviewRenderer);
        string executionText = execution.Target == ParticleExecutionTarget.Pending
            ? "particle backend pending"
            : $"{execution.BackendName}: {execution.StatusText}";
        _statusLabel.Text =
            $"{_activePreset} · {mode} · {preview} · {executionText} · "
            + $"{LiveParticleCount}/{PreviewParticleCapacity} live · "
            + $"{_config.Shape} · {_config.EmitRate:0.#}/s · life {_config.Lifetime:0.##}s · "
            + $"{_config.BlendMode} · {floor} · {_previewClock.Speed:0.##}×"
            + (string.IsNullOrEmpty(PreviewGpuDiagnostics) ? "" : " · " + PreviewGpuDiagnostics)
            + (_previewClock.Seeking ? " · Seeking (Stop cancels)" : "");
    }

    private static void NormaliseConfig(ParticleConfig config)
    {
        config.MaxParticles = Math.Clamp(config.MaxParticles, 1, 100_000);
        config.StartColor ??= new ParticleColor();
        config.EndColor ??= new ParticleColor(1f, 1f, 1f, 0f);
        config.TexturePath ??= string.Empty;
        config.MeshSurfaceAsset ??= string.Empty;
        config.MeshParticleAsset ??= string.Empty;
        config.BackdropSprite ??= string.Empty;
        config.PreviewTargetAsset ??= string.Empty;
        config.Notes ??= string.Empty;
        config.Script ??= string.Empty;
        config.EffectName ??= "Particle Effect";
        config.EmitterName ??= "Primary";
        config.EmitterId ??= "primary";
        config.Emitters ??= [];
        config.Emitters.RemoveAll(layer => layer is null);
        config.EventLinks ??= [];
        config.EventLinks.RemoveAll(link => link is null);
        config.Light ??= new ParticleLightConfig();
        config.Light.Color ??= new ParticleColor(1f, .48f, .12f, 1f);
        config.SizeOverLifetime ??= new ParticleBezierCurve();
        config.SpeedOverLifetime ??= new ParticleBezierCurve();
        config.AlphaOverLifetime ??= new ParticleBezierCurve();
        config.VelocityOverLifetime ??= new ParticleBezierCurve();
        config.GradientStops ??= [];
        config.GradientStops.RemoveAll(stop => stop is null);
        foreach (ParticleGradientStop stop in config.GradientStops)
        {
            stop.Position = Math.Clamp(stop.Position, 0d, 1d);
            stop.Color ??= new ParticleColor();
        }
        config.GradientStops.Sort((left, right) => left.Position.CompareTo(right.Position));
        foreach (ParticleEmitterLayer layer in config.Emitters)
        {
            if (layer is null) continue;
            layer.Id ??= Guid.NewGuid().ToString("N");
            layer.Name ??= "Emitter";
            layer.Config ??= new ParticleConfig();
            NormaliseConfig(layer.Config);
            layer.Config.Emitters = [];
        }
    }

    private static ParticleConfig LoadConfig(string path)
    {
        if (!File.Exists(path)) return ParticlePresets.Fire();
        try
        {
            string json = File.ReadAllText(path);
            using JsonDocument parsed = JsonDocument.Parse(json);
            JsonElement root = parsed.RootElement;
            if (root.TryGetProperty("emission", out JsonElement emission)
                && emission.ValueKind == JsonValueKind.Object)
            {
                ParticleConfig migrated = ParticlePresets.Fire();
                if (emission.TryGetProperty("rate", out JsonElement rate)
                    && rate.TryGetDouble(out double emitRate))
                    migrated.EmitRate = emitRate;
                if (root.TryGetProperty("loop", out JsonElement loop)
                    && loop.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    migrated.Loop = loop.GetBoolean();
                ReadLegacyNumber(root, "lifetime", value => migrated.Lifetime = value);
                ReadLegacyNumber(root, "speed", value => migrated.Speed = value / 40.0);
                ReadLegacyNumber(root, "spreadDegrees", value => migrated.SpreadDegrees = value);
                ReadLegacyNumber(root, "gravity", value => migrated.Gravity = -value / 40.0);
                ReadLegacyNumber(root, "startSize", value => migrated.StartSize = value / 16.0);
                ReadLegacyNumber(root, "endSize", value => migrated.EndSize = value / 16.0);
                if (root.TryGetProperty("shape", out JsonElement shape)
                    && shape.ValueKind == JsonValueKind.Object
                    && shape.TryGetProperty("type", out JsonElement type)
                    && Enum.TryParse(type.GetString(), true, out ParticleEmitShape parsedShape))
                    migrated.Shape = parsedShape;
                migrated.Notes = "Migrated from Genesis Particle Editor schema 1. " + migrated.Notes;
                return migrated;
            }

            return JsonSerializer.Deserialize<ParticleConfig>(json, JsonOptions)
                ?? throw new InvalidDataException("The particle resource is empty. Its original contents were not changed.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidDataException("Cannot open this particle resource safely. Its original contents were not changed. " + exception.Message, exception);
        }
    }

    private static void ReadLegacyNumber(JsonElement root, string name, Action<double> apply)
    {
        if (root.TryGetProperty(name, out JsonElement property) && property.TryGetDouble(out double value))
            apply(value);
    }

    /// <summary>Compact three-stop gradient and lifetime-curve preview used by the inspector.</summary>
    private sealed class ParticleLifetimePreview : Control
    {
        private readonly Func<ParticleConfig> _config;

        public ParticleLifetimePreview(Func<ParticleConfig> config)
        {
            _config = config;
            AccessibleName = "Particle gradient and lifetime curves";
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            ParticleConfig config = _config();
            e.Graphics.Clear(EditorChrome.Raised);
            Rectangle gradient = new(8, 8, Math.Max(1, Width - 16), 28);
            for (int x = 0; x < gradient.Width; x++)
            {
                float t = gradient.Width <= 1 ? 0f : x / (float)(gradient.Width - 1);
                ParticleColor sample = SampleGradient(config, t);
                using Pen pen = new(Color.FromArgb(
                    255,
                    (int)(Math.Clamp(sample.R, 0f, 1f) * 255f),
                    (int)(Math.Clamp(sample.G, 0f, 1f) * 255f),
                    (int)(Math.Clamp(sample.B, 0f, 1f) * 255f)));
                e.Graphics.DrawLine(pen, gradient.X + x, gradient.Top, gradient.X + x, gradient.Bottom);
            }
            using (Pen border = new(EditorChrome.Border))
            {
                e.Graphics.DrawRectangle(border, gradient);
            }

            Rectangle graph = new(8, 48, Math.Max(1, Width - 16), Math.Max(24, Height - 58));
            using (Pen grid = new(Color.FromArgb(70, EditorChrome.Border)))
            {
                e.Graphics.DrawRectangle(grid, graph);
                e.Graphics.DrawLine(grid, graph.Left, graph.Top + graph.Height / 2, graph.Right, graph.Top + graph.Height / 2);
            }
            DrawCurve(e.Graphics, graph, config.SizeCurve, EditorChrome.Accent);
            DrawCurve(e.Graphics, graph, config.AlphaCurve, EditorChrome.Warning);
            using SolidBrush legend = new(EditorChrome.Muted);
            e.Graphics.DrawString("SIZE", EditorChrome.SmallFont, legend, graph.Left + 4, graph.Top + 3);
            e.Graphics.DrawString("ALPHA", EditorChrome.SmallFont, legend, graph.Right - 43, graph.Top + 3);
        }

        private static void DrawCurve(Graphics graphics, Rectangle bounds, ParticleInterpolationCurve curve, Color color)
        {
            using Pen pen = new(color, 2f);
            PointF previous = new(bounds.Left, bounds.Bottom);
            for (int x = 1; x <= bounds.Width; x++)
            {
                float t = x / (float)Math.Max(1, bounds.Width);
                float value = ParticleCurveMath.Evaluate(curve, t);
                PointF next = new(bounds.Left + x, bounds.Bottom - value * bounds.Height);
                graphics.DrawLine(pen, previous, next);
                previous = next;
            }
        }

        private static ParticleColor SampleGradient(ParticleConfig config, float t)
        {
            if (config.GradientStops is { Count: > 0 })
            {
                ParticleGradientStop before = config.GradientStops.OrderBy(stop => stop.Position)
                    .LastOrDefault(stop => stop.Position <= t) ?? config.GradientStops[0];
                ParticleGradientStop after = config.GradientStops.OrderBy(stop => stop.Position)
                    .FirstOrDefault(stop => stop.Position >= t) ?? config.GradientStops[^1];
                float range = (float)(after.Position - before.Position);
                return Lerp(before.Color, after.Color, range <= 0.0001f ? 0f : Math.Clamp((t - (float)before.Position) / range, 0f, 1f));
            }
            ParticleColor start = config.StartColor;
            ParticleColor end = config.EndColor;
            ParticleColor? middle = config.MidColor;
            float midpoint = (float)Math.Clamp(config.ColorMidpoint, 0.05, 0.95);
            if (middle is null)
            {
                return Lerp(start, end, t);
            }

            return t <= midpoint
                ? Lerp(start, middle, t / midpoint)
                : Lerp(middle, end, (t - midpoint) / (1f - midpoint));
        }

        private static ParticleColor Lerp(ParticleColor a, ParticleColor b, float t) => new(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t,
            a.A + (b.A - a.A) * t);
    }

    private static ToolStripLabel ToolbarCaption(string text) => new(text)
    {
        AutoSize = true,
        ForeColor = EditorChrome.Muted,
        Margin = new Padding(0, 0, 6, 0),
        Padding = new Padding(0, 2, 0, 0),
    };
}
