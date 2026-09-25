using System.Drawing.Drawing2D;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Scene;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.World;
using Genesis.World.Terrain;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ParticleEditorControl
{
    private readonly List<ParticleSimulation> _previewSimulations = [];
    private readonly List<ParticleConfig> _previewEmitterConfigs = [];
    private readonly List<TextureHandle> _previewTextures = [];
    private readonly List<bool> _previewTexturesOwned = [];
    private readonly List<MeshHandle[]> _previewFrames = [];
    private readonly List<bool> _previewFramesOwned = [];
    private IRenderController? _particlePreviewRenderer;
    private readonly RuntimeModelAssetRegistry _particleMeshAssets = new();
    private readonly ModelGpuCache _particleMeshGpu = new();
    private readonly Dictionary<string, ComboBox> _advancedCombos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CheckBox> _advancedChecks = new(StringComparer.Ordinal);
    private readonly List<(Button Button, Func<string> Read, string Fallback)> _assetButtons = [];
    private readonly List<(Button Button, Func<string> Read)> _assetClearButtons = [];
    private FlowLayoutPanel? _presetGrid;
    private ParticleCurveEditor? _curveEditor;
    private ThemedComboBox? _curveSelector;
    private ToolStripComboBox? _targetTypeCombo;
    private ToolStripButton? _targetAssetButton;
    private ToolStripButton? _topPlayButton;
    private NumericUpDown? _burstCount;
    private SplitContainer? _referenceAuthoringSplit;
    private int _selectedEmitterIndex;
    private RuntimeModelRenderSystem? _targetModelRenderer;
    private string _resolvedTargetModel = string.Empty;
    private string _framedTargetModel = string.Empty;
    private Matrix4x4 _targetModelWorld = Matrix4x4.Identity;
    private Vector3 _previewOrigin;
    private IRenderController? _targetRenderer;
    private MeshHandle _terrainTargetMesh;
    private TerrainAsset? _terrainTarget;
    private string _terrainTargetKey = string.Empty;

    private IEnumerable<ParticleSimulation> AllPreviewSimulations => _previewSimulations;

    private void BuildReferenceWorkspace(EditorCommandBar toolbar, Panel timelinePanel) =>
        BuildParticleWorkbench(toolbar, timelinePanel);

    private Panel BuildModularInspectorDock()
    {
        Panel root = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };

        AddAdvancedInspectorControls();
        foreach (TabPage page in _inspectorTabs.TabPages)
        {
            if (page.Text == "Motion") page.Text = "Forces";
            else if (page.Text == "Appearance") page.Text = "Material";
            else if (page.Text == "Lifetime") page.Text = "Curves";
        }

        _inspectorTabs.Multiline = true;
        _inspectorTabs.SizeMode = TabSizeMode.FillToRight;
        _inspectorTabs.Dock = DockStyle.Fill;
        _inspectorTabs.Visible = true;
        root.Controls.Add(_inspectorTabs);
        root.Controls.Add(EditorChrome.SectionLabel("PARTICLE PROPERTIES"));
        _inspectorTabs.BringToFront();
        return root;
    }

    private void AddAdvancedInspectorControls()
    {
        foreach (string pageName in new[] { "Lifetime", "Collision", "Renderer" })
        {
            FlowLayoutPanel page = InspectorPage(pageName);
            foreach (Control placeholder in page.Controls.Cast<Control>()
                         .Where(control => control.Height >= 70 && control.Controls.OfType<Label>().Count() >= 2).ToArray())
            {
                page.Controls.Remove(placeholder);
                placeholder.Dispose();
            }
        }
        FlowLayoutPanel emission = InspectorPage("Emission");
        AddNumeric(emission, "Preview range", "duration", _effect.Duration, 0.05, 3600, value => _effect.Duration = value, 2);
        AddNumeric(emission, "Life minimum", "lifeMin", LifetimeMinimum(_config), 0.01, 3600, value => SetLifetimeRange(value, LifetimeMaximum(_config)), 2);
        AddNumeric(emission, "Life maximum", "lifeMax", LifetimeMaximum(_config), 0.01, 3600, value => SetLifetimeRange(LifetimeMinimum(_config), value), 2);
        AddNumeric(emission, "Max particles", "maxParticles", _config.MaxParticles, 1, 100000, value => _config.MaxParticles = (int)value);
        AddNumeric(emission, "Burst count", "burstCount", _config.BurstCount, 0, 100000, value => _config.BurstCount = (int)value);
        AddNumeric(emission, "Radius", "emitRadius", _config.EmitRadius, 0, 1000, value => _config.EmitRadius = value, 2);
        AddNumeric(emission, "Box X", "boxX", _config.BoxSizeX, 0.01, 1000, value => _config.BoxSizeX = value, 2);
        AddNumeric(emission, "Box Y", "boxY", _config.BoxSizeY, 0.01, 1000, value => _config.BoxSizeY = value, 2);
        AddNumeric(emission, "Box Z", "boxZ", _config.BoxSizeZ, 0.01, 1000, value => _config.BoxSizeZ = value, 2);
        _meshSurfaceField = AssetButton("Choose Model…", ResourceKind.Model, () => _config.MeshSurfaceAsset, value => _config.MeshSurfaceAsset = value);
        AddInspectorRow(emission, "Mesh surface", _meshSurfaceField);

        FlowLayoutPanel motion = InspectorPage("Motion");
        AddNumeric(motion, "Speed variation", "speedVariance", _config.SpeedVariance, 0, 1, value => _config.SpeedVariance = value, 2);
        AddNumeric(motion, "Spin speed", "rotationSpeed", _config.RotationSpeed, -720, 720, value => _config.RotationSpeed = value, 1);
        AddNumeric(motion, "Spin variation", "rotationVariance", _config.RotationVariance, 0, 720, value => _config.RotationVariance = value, 1);
        AddInspectorRow(motion, "Emit down", BoundCheckBox("downward", () => _config.DownwardEmit, value => _config.DownwardEmit = value));
        AddInspectorRow(emission, "Follow camera", BoundCheckBox("followCamera", () => _config.FollowCameraXZ, value => _config.FollowCameraXZ = value));
        AddNumeric(motion, "Gravity X", "gravityX", _config.GravityX, -100, 100, value => _config.GravityX = value, 2);
        AddNumeric(motion, "Gravity Z", "gravityZ", _config.GravityZ, -100, 100, value => _config.GravityZ = value, 2);
        AddNumeric(motion, "Wind X", "windX", _config.WindX, -100, 100, value => _config.WindX = value, 2);
        AddNumeric(motion, "Wind Z", "windZ", _config.WindZ, -100, 100, value => _config.WindZ = value, 2);

        FlowLayoutPanel collision = InspectorPage("Collision");
        ThemedComboBox collisionMode = EnumCombo("collisionMode", _config.CollisionMode, value => _config.CollisionMode = (ParticleCollisionMode)value);
        AddInspectorRow(collision, "Response", collisionMode);
        AddNumeric(collision, "Plane height", "collisionHeight", _config.CollisionPlaneHeight, -10000, 10000, value => _config.CollisionPlaneHeight = value, 2);
        AddNumeric(collision, "Bounce", "collisionBounce", _config.CollisionBounce, 0, 1.5, value => _config.CollisionBounce = value, 2);
        AddInspectorRow(collision, "Terrain", BoundCheckBox("collideTerrain", () => _config.CollideWithTerrain, value => _config.CollideWithTerrain = value));
        AddInspectorRow(collision, "Geometry", BoundCheckBox("collideGeometry", () => _config.CollideWithGeometry, value => _config.CollideWithGeometry = value));
        AddInfoCard(collision, "Preview collision", "Bounce / Die / Stick use the height plane or the selected terrain height. Arbitrary model-surface collision is not a full physics preview.");

        FlowLayoutPanel renderer = InspectorPage("Renderer");
        AddInspectorRow(renderer, "Blend", EnumCombo("blend", _config.BlendMode, value => _config.BlendMode = (ParticleBlendMode)value));
        AddInspectorRow(renderer, "Alignment", EnumCombo("alignment", _config.Alignment, value => _config.Alignment = (ParticleAlignment)value));
        AddNumeric(renderer, "Emissive", "emissive", _config.Emissive, 0, 20, value => _config.Emissive = value, 2);
        AddNumeric(renderer, "Colour jitter", "colorJitter", _config.ColorJitter, 0, 1, value => _config.ColorJitter = value, 2);
        AddInspectorRow(renderer, "Texture", AssetButton("Choose Image…", ResourceKind.Image, () => _config.TexturePath, value => _config.TexturePath = value));
        _meshParticleField = AssetButton("Choose Model…", ResourceKind.Model, () => _config.MeshParticleAsset, value => _config.MeshParticleAsset = value);
        AddInspectorRow(renderer, "Mesh particle", _meshParticleField);
        AddInspectorRow(renderer, "Flipbook", BoundCheckBox("flipbook", () => _config.UseFlipbook, value => _config.UseFlipbook = value));
        AddNumeric(renderer, "Columns", "flipColumns", _config.FlipbookColumns, 1, 64, value => _config.FlipbookColumns = (int)value);
        AddNumeric(renderer, "Rows", "flipRows", _config.FlipbookRows, 1, 64, value => _config.FlipbookRows = (int)value);
        AddNumeric(renderer, "Flipbook FPS", "flipFps", _config.FlipbookFps, 0.1, 240, value => _config.FlipbookFps = value, 1);

        FlowLayoutPanel material = InspectorPage("Appearance");
        AddNumeric(material, "Width scale", "sizeScaleX", _config.SizeXScale, 0.01, 100, value => _config.SizeXScale = value, 2);
        AddNumeric(material, "Height scale", "sizeScaleY", _config.SizeYScale, 0.01, 100, value => _config.SizeYScale = value, 2);
        FlowLayoutPanel curves = InspectorPage("Lifetime");
        AddInspectorRow(curves, "Custom size", BoundCheckBox("curveSize", () => _config.UseCustomSizeCurve, value => _config.UseCustomSizeCurve = value));
        AddInspectorRow(curves, "Custom speed", BoundCheckBox("curveSpeed", () => _config.UseCustomSpeedCurve, value => _config.UseCustomSpeedCurve = value));
        AddInspectorRow(curves, "Custom alpha", BoundCheckBox("curveAlpha", () => _config.UseCustomAlphaCurve, value => _config.UseCustomAlphaCurve = value));
        AddInspectorRow(curves, "Custom velocity", BoundCheckBox("curveVelocity", () => _config.UseCustomVelocityCurve, value => _config.UseCustomVelocityCurve = value));
        AddInfoCard(curves, "Curves and colour", "Choose the curve below the preview, then drag its handles. Untick its custom switch here to use the standard interpolation again. Escape cancels a drag.");
        FlowLayoutPanel appearance = AddInspectorPage("Effect Light");
        AddNumeric(appearance, "Light radius", "lightRadius", _effect.Light.Radius, 0.1, 1000, value => _effect.Light.Radius = value, 2);
        AddNumeric(appearance, "Light power", "lightPower", _effect.Light.Intensity, 0, 100, value => _effect.Light.Intensity = value, 2);
        AddNumeric(appearance, "Flicker", "lightFlicker", _effect.Light.FlickerAmount, 0, 1, value => _effect.Light.FlickerAmount = value, 2);
        AddNumeric(appearance, "Light falloff", "lightFalloff", _effect.Light.Falloff, 0.1, 16, value => _effect.Light.Falloff = value, 2);
        AddNumeric(appearance, "Flicker Hz", "lightFrequency", _effect.Light.FlickerFrequency, 0.1, 60, value => _effect.Light.FlickerFrequency = value, 1);
        AddNumeric(appearance, "Light height", "lightY", _effect.Light.OffsetY, -100, 100, value => _effect.Light.OffsetY = value, 2);
        AddInspectorRow(appearance, "Emit light", BoundCheckBox("emitLight", () => _effect.Light.Enabled, value => _effect.Light.Enabled = value));
        AddInspectorRow(appearance, "Light colour", MakeColorWell(() => _effect.Light.Color, PickLightColor));
        AddInfoCard(appearance, "One light per effect", "These settings belong to the entire effect, not its selected emitter. Preview lighting uses the 3D view.");
    }

    private FlowLayoutPanel InspectorPage(string title) => _inspectorTabs.TabPages.Cast<TabPage>()
        .First(page => string.Equals(page.Text, title, StringComparison.Ordinal))
        .Controls.OfType<FlowLayoutPanel>().First();

    private static double LifetimeMinimum(ParticleConfig config) => Math.Max(.01, config.Lifetime * (1d - Math.Clamp(config.LifetimeVariance, 0d, .99d)));
    private static double LifetimeMaximum(ParticleConfig config) => Math.Max(.01, config.Lifetime * (1d + Math.Clamp(config.LifetimeVariance, 0d, .99d)));

    private void SetLifetimeRange(double minimum, double maximum)
    {
        double min = Math.Max(.01, Math.Min(minimum, maximum));
        double max = Math.Max(min, maximum);
        _config.Lifetime = (min + max) * .5;
        _config.LifetimeVariance = max <= .0001 ? 0 : Math.Clamp((max - min) / (max + min), 0d, .99d);
    }

    private ThemedComboBox EnumCombo<T>(string key, T selected, Action<object> apply) where T : struct, Enum
    {
        ThemedComboBox combo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (T value in Enum.GetValues<T>()) combo.Items.Add(value);
        combo.SelectedItem = selected;
        EditorChrome.StyleField(combo);
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || combo.SelectedItem is null) return;
            apply(combo.SelectedItem);
            _activePreset = "Custom";
            ConfigChanged(reset: false);
        };
        _advancedCombos[key] = combo;
        return combo;
    }

    private CheckBox BoundCheckBox(string key, Func<bool> read, Action<bool> write)
    {
        CheckBox box = new() { Checked = read(), Text = "Enabled", ForeColor = EditorChrome.Text, AutoSize = true };
        box.CheckedChanged += (_, _) =>
        {
            if (_syncing) return;
            write(box.Checked);
            _activePreset = "Custom";
            ConfigChanged(reset: false);
        };
        _advancedChecks[key] = box;
        return box;
    }

    private Control AssetButton(string caption, ResourceKind kind, Func<string> read, Action<string> write)
    {
        Panel field = new() { Height = 28 };
        Button button = new() { Text = ShortAsset(read(), caption), Dock = DockStyle.Fill, Height = 28 };
        Button clear = new() { Text = "×", Dock = DockStyle.Right, Width = 24,
            AccessibleName = "Clear " + caption.Replace("Choose ", string.Empty, StringComparison.Ordinal),
            Enabled = !string.IsNullOrWhiteSpace(read()) };
        EditorChrome.StyleField(button); EditorChrome.StyleField(clear);
        button.Click += (_, _) =>
        {
            if (!PrepareParticleOperation()) return;
            ProjectAssetEntry? picked = AssetPickerService.PickAsset(new AssetPickerRequest(ProjectRoot, kind, read()), FindForm());
            if (picked is null) return;
            write(picked.Reference);
            _activePreset = "Custom";
            ConfigChanged(reset: false);
        };
        clear.Click += (_, _) =>
        {
            if (!PrepareParticleOperation() || string.IsNullOrEmpty(read())) return;
            write(string.Empty); _activePreset = "Custom"; ConfigChanged(reset: false);
        };
        field.Controls.Add(button); field.Controls.Add(clear);
        _assetButtons.Add((button, read, caption)); _assetClearButtons.Add((clear, read));
        return field;
    }

    private void SyncAdvancedControls()
    {
        SetCombo("collisionMode", _config.CollisionMode);
        SetCombo("blend", _config.BlendMode);
        SetCombo("alignment", _config.Alignment);
        SetCheck("collideTerrain", _config.CollideWithTerrain);
        SetCheck("collideGeometry", _config.CollideWithGeometry);
        SetCheck("flipbook", _config.UseFlipbook);
        SetCheck("downward", _config.DownwardEmit);
        SetCheck("followCamera", _config.FollowCameraXZ);
        SetCheck("curveSize", _config.UseCustomSizeCurve);
        SetCheck("curveSpeed", _config.UseCustomSpeedCurve);
        SetCheck("curveAlpha", _config.UseCustomAlphaCurve);
        SetCheck("curveVelocity", _config.UseCustomVelocityCurve);
        SetNumeric("speedVariance", _config.SpeedVariance);
        SetNumeric("rotationSpeed", _config.RotationSpeed);
        SetNumeric("rotationVariance", _config.RotationVariance);
        SetNumeric("sizeScaleX", _config.SizeXScale);
        SetNumeric("sizeScaleY", _config.SizeYScale);
        SetCheck("emitLight", _effect.Light.Enabled);
        foreach ((Button button, Func<string> read, string fallback) in _assetButtons)
            button.Text = ShortAsset(read(), fallback);
        foreach ((Button button, Func<string> read) in _assetClearButtons)
            button.Enabled = !string.IsNullOrWhiteSpace(read());
    }

    private void SetCombo(string key, object value)
    {
        if (_advancedCombos.TryGetValue(key, out ComboBox? combo)) combo.SelectedItem = value;
    }

    private void SetCheck(string key, bool value)
    {
        if (_advancedChecks.TryGetValue(key, out CheckBox? check)) check.Checked = value;
    }

    private static string ShortAsset(string path, string fallback) =>
        string.IsNullOrWhiteSpace(path) ? fallback : ResourceDisplayName.Format(path);

    private Panel BuildCurveTimeline(Panel oldTimeline)
    {
        EnsureGradientStopsFromLegacy(_config);
        oldTimeline.Dock = DockStyle.Top;
        oldTimeline.Height = 44;
        oldTimeline.Padding = new Padding(8, 3, 8, 3);
        _curveSelector = new ThemedComboBox
        {
            Dock = DockStyle.Left,
            Width = 170,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _curveSelector.Items.AddRange(["Size over lifetime", "Speed over lifetime", "Alpha over lifetime", "Velocity over lifetime"]);
        _curveSelector.SelectedIndex = 0;
        _curveSelector.SelectedIndexChanged += (_, _) => _curveEditor?.Invalidate();
        EditorChrome.StyleField(_curveSelector);

        _curveEditor = new ParticleCurveEditor(
            () => CurrentCurve,
            () => _config.GradientStops,
            curveChanged =>
            {
                if (curveChanged) EnableCurrentCustomCurve();
                _activePreset = "Custom";
                ConfigChanged(reset: false);
            }, BeginParticleGesture, FinishParticleGesture, CancelParticleGesture)
        {
            Dock = DockStyle.Fill,
        };
        Panel canvas = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Padding = new Padding(8, 0, 8, 8) };
        canvas.Controls.Add(_curveEditor);
        canvas.Controls.Add(_curveSelector);
        canvas.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            ForeColor = EditorChrome.Muted,
            Text = "Drag handles / keys · double-click bar adds, key edits colour · right-click removes · wheel = alpha",
            TextAlign = ContentAlignment.MiddleRight,
        });
        canvas.Controls.Add(EditorChrome.SectionLabel("TIMELINE  ·  BEZIER CURVE  ·  COLOUR / ALPHA GRADIENT"));
        Panel root = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        root.Controls.Add(canvas);
        root.Controls.Add(oldTimeline);
        return root;
    }

    private ParticleBezierCurve CurrentCurve => (_curveSelector?.SelectedIndex ?? 0) switch
    {
        1 => _config.SpeedOverLifetime,
        2 => _config.AlphaOverLifetime,
        3 => _config.VelocityOverLifetime,
        _ => _config.SizeOverLifetime,
    };

    private void EnableCurrentCustomCurve()
    {
        switch (_curveSelector?.SelectedIndex ?? 0)
        {
            case 1: _config.UseCustomSpeedCurve = true; break;
            case 2: _config.UseCustomAlphaCurve = true; break;
            case 3: _config.UseCustomVelocityCurve = true; break;
            default: _config.UseCustomSizeCurve = true; break;
        }
    }



    private void RebuildEmitterPreview()
    {
        DisposeGpuPreviewEmitters();
        ReleaseEmitterPreviewResources();
        _previewSimulations.Clear();
        _previewEmitterConfigs.Clear();
        _previewEmitterIds.Clear(); _previewKeys.Clear(); _previewSurfaceKeys.Clear();
        _simulation.LoadConfig(_effect);
        ConfigureMeshSurfacePreview(_simulation, _effect);
        if (_effect.EmitterEnabled)
        {
            _previewSimulations.Add(_simulation);
            _previewEmitterConfigs.Add(_effect);
            _previewEmitterIds.Add(_effect.EmitterId);
        }
        foreach (ParticleEmitterLayer layer in _effect.Emitters)
        {
            if (!layer.Enabled) continue;
            ParticleSimulation simulation = new();
            simulation.LoadConfig(layer.Config);
            ConfigureMeshSurfacePreview(simulation, layer.Config);
            _previewSimulations.Add(simulation);
            _previewEmitterConfigs.Add(layer.Config);
            _previewEmitterIds.Add(layer.Id);
        }
        if (_terrainTarget is not null) ConfigureTerrainEmitterPreview(_terrainTarget, reset: false);
        foreach (ParticleConfig config in _previewEmitterConfigs)
        {
            _previewKeys.Add(ParticlePreviewKey.From(config));
            _previewSurfaceKeys.Add(config.Shape + ":" + config.MeshSurfaceAsset);
        }
        ResetParticlePreview(_timelinePlaying);
    }

    private void ConfigureMeshSurfacePreview(ParticleSimulation simulation, ParticleConfig config)
    {
        // Clearing/changing a source must not reuse old mesh samples if loading the new one fails.
        simulation.SetMeshSurfaceSamples(ReadOnlySpan<Vector3>.Empty);
        if (config.Shape != ParticleEmitShape.MeshSurface || string.IsNullOrWhiteSpace(config.MeshSurfaceAsset)) return;
        try
        {
            GModelAsset asset = _particleMeshAssets.Load(ProjectRoot, config.MeshSurfaceAsset);
            List<Vector3> positions = [];
            foreach (GModelMesh mesh in asset.Meshes)
            {
                if (mesh.Vertices is { Length: > 0 }) positions.AddRange(mesh.Vertices.Select(vertex => vertex.Position));
                else if (mesh.SkinnedVertices is { Length: > 0 }) positions.AddRange(mesh.SkinnedVertices.Select(vertex => vertex.Position));
                if (positions.Count >= 100_000) break;
            }
            if (positions.Count > 0) simulation.SetMeshSurfaceSamples(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(positions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LoadWarning = "Mesh surface emitter could not be sampled: " + exception.Message;
        }
    }

    private void UpdateSelectedEmitterPreview(bool reset)
    {
        bool renderChanged = false;
        for (int i = 0; i < _previewSimulations.Count; i++)
        {
            ParticleConfig config = _previewEmitterConfigs[i];
            _previewSimulations[i].UpdateConfig(config);
            ParticlePreviewKey key = ParticlePreviewKey.From(config);
            if (_previewKeys[i] != key) { _previewKeys[i] = key; renderChanged = true; }
            string surface = config.Shape + ":" + config.MeshSurfaceAsset;
            if (_previewSurfaceKeys[i] != surface)
            {
                _previewSurfaceKeys[i] = surface;
                ConfigureMeshSurfacePreview(_previewSimulations[i], config);
            }
        }
        // Rates, forces, colours, curves and size edits keep both the in-flight particles and
        // their GPU resources. Re-register geometry/textures only when their definition changes.
        if (renderChanged) ReleaseEmitterPreviewResources();
        if (reset) ResetParticlePreview(_timelinePlaying);
        _viewport.Invalidate(true);
    }

    private void SelectEmitter(int index)
    {
        if (!PrepareParticleOperation()) { RefreshEmitterStack(); return; }
        _selectedEmitterIndex = Math.Clamp(index, 0, _effect.Emitters.Count);
        _config = ParticleEffectEditing.Emitter(_effect, _selectedEmitterIndex);
        if (_committedParticleState is not null)
            _committedParticleState = _committedParticleState with { Selected = _selectedEmitterIndex };
        SyncControls(); PushCodeFromConfig(); RefreshEmitterStack();
        _curveEditor?.Invalidate(); UpdateStatus();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void EnsureGradientStopsFromLegacy(ParticleConfig config)
    {
        if (config.GradientStops.Count > 0) return;
        config.GradientStops.Add(new ParticleGradientStop { Position = 0, Color = config.StartColor.Clone() });
        if (config.MidColor is not null)
            config.GradientStops.Add(new ParticleGradientStop { Position = config.ColorMidpoint, Color = config.MidColor.Clone() });
        config.GradientStops.Add(new ParticleGradientStop { Position = 1, Color = config.EndColor.Clone() });
    }

    private static void EnsureEffectGradients(ParticleConfig effect)
    {
        EnsureGradientStopsFromLegacy(effect);
        foreach (ParticleEmitterLayer layer in effect.Emitters) EnsureGradientStopsFromLegacy(layer.Config);
    }

    private static void SyncLegacyGradientStops(ParticleConfig config)
    {
        EnsureGradientStopsFromLegacy(config);
        ParticleGradientStop first = config.GradientStops.OrderBy(stop => stop.Position).First();
        ParticleGradientStop last = config.GradientStops.OrderBy(stop => stop.Position).Last();
        first.Color = config.StartColor.Clone();
        last.Color = config.EndColor.Clone();
        ParticleGradientStop? middle = config.GradientStops.FirstOrDefault(stop => !ReferenceEquals(stop, first) && !ReferenceEquals(stop, last));
        if (config.MidColor is not null)
        {
            middle ??= new ParticleGradientStop();
            middle.Position = config.ColorMidpoint;
            middle.Color = config.MidColor.Clone();
            if (!config.GradientStops.Contains(middle)) config.GradientStops.Add(middle);
        }
        else if (middle is not null)
        {
            config.GradientStops.Remove(middle);
        }
    }

    private void AddEmitter(string preset)
    {
        if (!PrepareParticleOperation() || ParticleEffectEditing.Count(_effect) >= ParticleEffectEditing.MaximumEmitters) return;
        _effect = ParticleEffectEditing.Add(_effect, ParticlePresets.Create(preset), preset, out int selected);
        CompleteEmitterOperation(selected, "Add " + preset + " emitter");
    }

    private void RemoveSelectedEmitter()
    {
        if (!PrepareParticleOperation() || ParticleEffectEditing.Count(_effect) <= 1) return;
        _effect = ParticleEffectEditing.Remove(_effect, _selectedEmitterIndex, out int selected);
        CompleteEmitterOperation(selected, "Remove particle emitter");
    }

    private void MoveSelectedEmitter(int direction)
    {
        if (!PrepareParticleOperation()) return;
        int target = Math.Clamp(_selectedEmitterIndex + direction, 0, _effect.Emitters.Count);
        if (target == _selectedEmitterIndex) return;
        _effect = ParticleEffectEditing.Move(_effect, _selectedEmitterIndex, target);
        CompleteEmitterOperation(target, "Reorder particle emitters");
    }

    private void BurstFromToolbar()
    {
        CancelPreviewSeek();
        int count = (int)(_burstCount?.Value ?? 150);
        BurstPreview(count);
        _viewport.Invalidate(true);
    }

    private void DrawEmitterStack2D(IRenderController renderer)
    {
        EnsureEmitterPreviewResources(renderer);
        DrawPreviewParticles2D(renderer);
    }

    private void DrawEmitterStack3D(IRenderController renderer)
    {
        EnsureEmitterPreviewResources(renderer);
        DrawPreviewParticles3D(renderer);
    }

    private void EnsureEmitterPreviewResources(IRenderController renderer)
    {
        if (!ReferenceEquals(_particlePreviewRenderer, renderer))
        {
            ReleaseEmitterPreviewResources();
            _particlePreviewRenderer = renderer;
        }
        while (_previewFrames.Count < _previewEmitterConfigs.Count)
        {
            ParticleConfig config = _previewEmitterConfigs[_previewFrames.Count];
            MeshHandle[] frames = [];
            bool ownsFrames = false;
            if (config.Alignment == ParticleAlignment.Mesh3D && !string.IsNullOrWhiteSpace(config.MeshParticleAsset))
            {
                try
                {
                    GModelAsset asset = _particleMeshAssets.Load(ProjectRoot, config.MeshParticleAsset);
                    ModelGpuCache.CachedAsset cached = _particleMeshGpu.GetOrCreate(renderer, asset);
                    MeshHandle mesh = cached.Meshes.FirstOrDefault(item => !item.IsSkinned && item.Lod == 0)?.Mesh ?? MeshHandle.Invalid;
                    if (mesh.IsValid) frames = [mesh];
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    LoadWarning = "Particle mesh could not be loaded: " + exception.Message;
                }
            }
            if (frames.Length == 0)
            {
                frames = ParticleRenderGeometry.RegisterFrames(renderer, config);
                ownsFrames = true;
            }
            _previewFrames.Add(frames);
            _previewFramesOwned.Add(ownsFrames);

            TextureHandle texture = TextureHandle.Invalid;
            if (!string.IsNullOrWhiteSpace(config.TexturePath))
            {
                string resolved = TexturePathResolver.Resolve(ProjectRoot, config.TexturePath);
                if (!string.IsNullOrWhiteSpace(resolved) && SpriteAssetLoader.IsSpriteDescriptorPath(resolved))
                {
                    SpriteRuntimeAsset sprite = SpriteAssetLoader.Load(resolved);
                    resolved = SpriteAssetLoader.ResolveFrameTexturePath(resolved, sprite, 0);
                }
                if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved)) texture = renderer.LoadTexture(resolved);
            }
            bool ownsTexture = false;
            if (!texture.IsValid)
            {
                texture = ParticleRenderGeometry.CreateDefaultTexture(renderer, config);
                ownsTexture = texture.IsValid;
            }
            _previewTextures.Add(texture);
            _previewTexturesOwned.Add(ownsTexture);
        }
    }

    private void ReleaseEmitterPreviewResources()
    {
        DisposeGpuPreviewEmitters();
        if (_particlePreviewRenderer is not null)
        {
            for (int i = 0; i < _previewFrames.Count; i++)
            {
                if (i < _previewFramesOwned.Count && _previewFramesOwned[i])
                    ParticleRenderGeometry.ReleaseFrames(_particlePreviewRenderer, _previewFrames[i]);
            }
            for (int i = 0; i < _previewTextures.Count; i++)
                if (i < _previewTexturesOwned.Count && _previewTexturesOwned[i] && _previewTextures[i].IsValid)
                    _particlePreviewRenderer.ReleaseTexture(_previewTextures[i]);
            _particleMeshGpu.Clear(_particlePreviewRenderer);
        }
        _previewFrames.Clear();
        _previewFramesOwned.Clear();
        _previewTextures.Clear();
        _previewTexturesOwned.Clear();
    }

    private void PickPreviewTarget()
    {
        if (!PrepareParticleOperation()) return;
        ResourceKind kind = _effect.PreviewTargetType switch
        {
            ParticlePreviewTargetType.Terrain => ResourceKind.Terrain,
            ParticlePreviewTargetType.Model => ResourceKind.Model,
            ParticlePreviewTargetType.Object => ResourceKind.GameObject,
            _ => ResourceKind.Unknown,
        };
        if (kind == ResourceKind.Unknown)
        {
            _effect.PreviewTargetAsset = string.Empty;
            RefreshTargetButton();
            InvalidatePreviewTarget();
            return;
        }
        ProjectAssetEntry? picked = AssetPickerService.PickAsset(
            new AssetPickerRequest(ProjectRoot, kind, _effect.PreviewTargetAsset, "Choose Particle Preview Target"), FindForm());
        if (picked is null) return;
        _effect.PreviewTargetAsset = picked.Reference.Replace('\\', '/');
        RefreshTargetButton();
        InvalidatePreviewTarget();
        CommitParticleEdit("Change particle preview target");
    }

    private void RefreshTargetButton()
    {
        if (_targetTypeCombo is not null) _targetTypeCombo.SelectedIndex = (int)_effect.PreviewTargetType;
        if (_targetAssetButton is not null)
        {
            _targetAssetButton.Enabled = _effect.PreviewTargetType != ParticlePreviewTargetType.None;
            _targetAssetButton.Text = string.IsNullOrWhiteSpace(_effect.PreviewTargetAsset)
                ? _effect.PreviewTargetType == ParticlePreviewTargetType.None ? "Target: Free preview" : "Target: (choose asset…)"
                : "Target: " + ResourceDisplayName.Format(_effect.PreviewTargetAsset);
        }
    }

    private void InvalidatePreviewTarget()
    {
        _resolvedTargetModel = string.Empty;
        _framedTargetModel = string.Empty;
        _targetModelWorld = Matrix4x4.Identity;
        _previewOrigin = Vector3.Zero;
        if (_targetRenderer is not null)
        {
            if (_terrainTargetMesh.IsValid) _targetRenderer.ReleaseMesh(_terrainTargetMesh);
        }
        _targetModelRenderer?.InvalidateAssets(_targetRenderer);
        _targetRenderer = null;
        _terrainTargetMesh = MeshHandle.Invalid;
        _terrainTarget = null;
        _terrainTargetKey = string.Empty;
        _viewport?.Invalidate(true);
    }

    private void DrawPreviewTarget(IRenderController renderer)
    {
        SubmitParticleLight(renderer);
        if (_effect.PreviewTargetType == ParticlePreviewTargetType.Terrain)
        {
            DrawTerrainTarget(renderer);
            return;
        }
        string model = ResolvePreviewModel();
        if (!string.IsNullOrWhiteSpace(model))
        {
            try
            {
                _targetModelRenderer ??= new RuntimeModelRenderSystem();
                _targetModelRenderer.BeginFrame();
                if (!string.Equals(_framedTargetModel, model, StringComparison.OrdinalIgnoreCase)
                    && _targetModelRenderer.TryGetBounds(ProjectRoot, model, out Vector3 min, out Vector3 max, includePivot: true))
                {
                    Vector3 size = Vector3.Max(max - min, new Vector3(.01f));
                    _targetModelWorld = Matrix4x4.CreateTranslation(-(min.X + max.X) * .5f, -min.Y, -(min.Z + max.Z) * .5f);
                    _viewport.Camera.Target = new Vector3(0, size.Y * .45f, 0);
                    _viewport.Camera.Distance = Math.Max(3f, size.Length() * 1.35f);
                    _viewport.FloorHeight = 0;
                    _framedTargetModel = model;
                }
                _targetModelRenderer.DrawModel(
                    ProjectRoot,
                    model,
                    string.Empty,
                    _targetModelWorld,
                    new Draw3DComponent { Visible = true, CastShadows = true, ReceiveShadows = true },
                    new ModelRendererComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1, CastShadows = true, ReceiveShadows = true },
                    default,
                    renderer);
                _targetModelRenderer.EndFrame();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                _targetModelRenderer?.EndFrame();
                LoadWarning = "Particle target could not be rendered: " + exception.Message;
            }
            return;
        }

    }

    private void DrawTerrainTarget(IRenderController renderer)
    {
        if (string.IsNullOrWhiteSpace(_effect.PreviewTargetAsset)) return;
        string path = ResourceNames.Resolve(ProjectRoot, _effect.PreviewTargetAsset);
        string key = Path.GetFullPath(path);
        try
        {
            if (_terrainTarget is null || !string.Equals(key, _terrainTargetKey, StringComparison.OrdinalIgnoreCase))
            {
                string binary = path + ".gterrain";
                if (File.Exists(binary)) _terrainTarget = TerrainAsset.Load(binary);
                else
                {
                    JObject settings = JObject.Parse(File.ReadAllText(path));
                    TerrainGenParams parameters = settings.ToObject<TerrainGenParams>() ?? new TerrainGenParams();
                    if (settings["resolution"] is JArray { Count: >= 2 } resolution)
                    {
                        parameters.ResolutionX = Math.Min(255, Math.Max(2, (int)resolution[0]));
                        parameters.ResolutionZ = Math.Min(255, Math.Max(2, (int)resolution[1]));
                    }
                    _terrainTarget = TerrainGenerator.Generate(parameters);
                }
                _terrainTargetKey = key;
                _terrainTargetMesh = MeshHandle.Invalid;
                if (_terrainTarget is not null)
                {
                    ConfigureTerrainEmitterPreview(_terrainTarget, reset: true);
                    float width = (_terrainTarget.ResolutionX - 1) * _terrainTarget.CellSize;
                    float depth = (_terrainTarget.ResolutionZ - 1) * _terrainTarget.CellSize;
                    _viewport.Camera.Target = new Vector3(_terrainTarget.OriginX + width * 0.5f, 0f, _terrainTarget.OriginZ + depth * 0.5f);
                    _viewport.Camera.Distance = Math.Max(6f, Math.Max(width, depth) * 0.8f);
                }
            }
            if (_terrainTarget is null) return;
            if (!ReferenceEquals(_targetRenderer, renderer))
            {
                _targetRenderer = renderer;
                _terrainTargetMesh = MeshHandle.Invalid;
            }
            if (!_terrainTargetMesh.IsValid)
            {
                MeshVertex[] vertices = new MeshVertex[_terrainTarget.ResolutionX * _terrainTarget.ResolutionZ];
                TerrainMeshBuilder.BuildVertices(_terrainTarget, TerrainMeshBuilder.DefaultLayerColors, vertices);
                ushort[] indices = TerrainMeshBuilder.BuildIndices(_terrainTarget.ResolutionX, _terrainTarget.ResolutionZ);
                _terrainTargetMesh = renderer.RegisterMesh(vertices, indices);
            }
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = _terrainTargetMesh,
                World = Matrix4x4.Identity,
                Tint = RenderColor.White,
                Flags = MeshDrawFlags.TerrainGround,
            });
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            LoadWarning = "Terrain preview target could not be loaded: " + exception.Message;
        }
    }

    private void ConfigureTerrainEmitterPreview(TerrainAsset terrain, bool reset)
    {
        float emitterX = terrain.OriginX + (terrain.ResolutionX - 1) * terrain.CellSize * .5f;
        float emitterZ = terrain.OriginZ + (terrain.ResolutionZ - 1) * terrain.CellSize * .5f;
        float emitterY = terrain.SampleHeight(emitterX, emitterZ);
        _previewOrigin = new Vector3(emitterX, emitterY, emitterZ);
        foreach (ParticleSimulation simulation in _previewSimulations)
        {
            simulation.SetCollisionHeightProvider(position => terrain.SampleHeight(position.X, position.Z));
            simulation.SetEmitterOrigin(_previewOrigin);
            if (reset) simulation.Reset();
        }
    }

    private string ResolvePreviewModel()
    {
        if (_effect.PreviewTargetType == ParticlePreviewTargetType.Model)
            return _effect.PreviewTargetAsset;
        if (_effect.PreviewTargetType != ParticlePreviewTargetType.Object
            || string.IsNullOrWhiteSpace(_effect.PreviewTargetAsset)) return string.Empty;
        if (_resolvedTargetModel.Length > 0) return _resolvedTargetModel;
        try
        {
            string path = ResourceNames.Resolve(ProjectRoot, _effect.PreviewTargetAsset);
            JObject prefab = ObjectDefinitionResolver.PreviewPrefab(ObjectDefinitionResolver.Load(ProjectRoot, path));
            _resolvedTargetModel = (string?)prefab["model"] ?? string.Empty;
            JObject? renderer = (prefab["components"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(component => string.Equals((string?)component["type"], "ModelRendererComponent", StringComparison.OrdinalIgnoreCase));
            _resolvedTargetModel = (string?)renderer?["props"]?["ModelAsset"] ?? _resolvedTargetModel;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
        {
            LoadWarning = "Particle preview target could not be loaded: " + exception.Message;
        }
        return _resolvedTargetModel;
    }

    private void SubmitParticleLight(IRenderController renderer)
    {
        ParticleLightConfig light = _effect.Light;
        if (light?.Enabled != true || light.Intensity <= 0 || light.Radius <= 0) return;
        float amount = (float)Math.Clamp(light.FlickerAmount, 0d, 1d);
        float frequency = (float)Math.Max(0.1, light.FlickerFrequency);
        float wave = MathF.Sin((float)_clock.Elapsed.TotalSeconds * frequency * MathF.Tau)
            * MathF.Sin((float)_clock.Elapsed.TotalSeconds * frequency * 0.47f * MathF.Tau);
        float intensity = (float)light.Intensity * MathF.Max(0f, 1f - amount * 0.5f + wave * amount * 0.5f);
        ParticleColor color = light.Color ?? new ParticleColor(1f, 0.45f, 0.1f, 1f);
        renderer.AddPointLight(
            _previewOrigin + new Vector3((float)light.OffsetX, (float)light.OffsetY, (float)light.OffsetZ),
            new Vector3(color.R, color.G, color.B),
            (float)light.Radius,
            intensity,
            (float)Math.Max(0.1, light.Falloff));
    }

    private void PickLightColor()
    {
        ParticleColor current = _effect.Light.Color;
        using ColorDialog dialog = new()
        {
            Color = Color.FromArgb((int)(current.R * 255), (int)(current.G * 255), (int)(current.B * 255)),
            FullOpen = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _effect.Light.Color = new ParticleColor(dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, 1f);
        ConfigChanged(reset: false);
    }

    private void SaveEffectAs()
    {
        if (!PrepareParticleOperation()) return;
        using Form prompt = new() { Text = "Save Particle Resource", ClientSize = new Size(430, 132),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false };
        Label label = new() { Text = "Resource name", Left = 16, Top = 14, Width = 390 };
        TextBox name = new() { Text = string.IsNullOrWhiteSpace(_effect.EffectName) ? "Particle Effect" : _effect.EffectName,
            Left = 16, Top = 38, Width = 394 };
        Button save = new() { Text = "Create", DialogResult = DialogResult.OK, Left = 326, Top = 86, Width = 84 };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 236, Top = 86, Width = 84 };
        prompt.Controls.AddRange([label, name, save, cancel]); prompt.AcceptButton = save; prompt.CancelButton = cancel;
        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
            string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Particle), ResourceKind.Particle, name.Text);
            string text = Genesis.Shared.Assets.ResourceReferenceRewriter.Normalize(ProjectRoot, path, JsonSerializer.Serialize(_effect, JsonOptions));
            File.WriteAllText(path, text);
            _statusLabel.Text = "Saved resource " + ResourceNames.Name(ProjectRoot, path);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "Save Particle Resource", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _codeApplyTimer.Stop();
            _codeApplyTimer.Dispose();
            _previewSeekTimer.Stop();
            _previewSeekTimer.Dispose();
            ReleaseEmitterPreviewResources();
            if (_targetRenderer is not null)
            {
                if (_terrainTargetMesh.IsValid) _targetRenderer.ReleaseMesh(_terrainTargetMesh);
            }
            _targetModelRenderer?.InvalidateAssets(_targetRenderer);
        }
        base.Dispose(disposing);
    }

    private sealed class ParticlePresetTile : Control
    {
        private readonly string _name;

        public ParticlePresetTile(string name)
        {
            _name = name;
            Size = new Size(102, 112);
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            AccessibleName = name + " particle preset";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool selected = ClientRectangle.Contains(PointToClient(Cursor.Position));
            using SolidBrush background = new(selected ? EditorChrome.Hover : EditorChrome.Raised);
            using Pen border = new(selected ? EditorChrome.Accent : EditorChrome.Border, selected ? 2f : 1f);
            Rectangle card = new(1, 1, Width - 3, Height - 3);
            e.Graphics.FillRectangle(background, card);
            e.Graphics.DrawRectangle(border, card);
            Rectangle preview = new(7, 7, Width - 14, 76);
            using LinearGradientBrush glow = new(preview, PresetColor(_name, true), PresetColor(_name, false), LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(glow, preview);
            using SolidBrush mote = new(Color.FromArgb(220, 255, 215, 120));
            int seed = ParticlePreviewClock.SeedForEmitter(1337, _name) & 0x0FFFFFFF;
            for (int i = 0; i < 9; i++)
            {
                int x = preview.Left + 8 + (seed + i * 31) % Math.Max(1, preview.Width - 16);
                int y = preview.Top + 8 + (seed / 7 + i * 17) % Math.Max(1, preview.Height - 16);
                e.Graphics.FillEllipse(mote, x, y, 3 + i % 3, 3 + i % 3);
            }
            TextRenderer.DrawText(e.Graphics, _name, EditorChrome.SmallFont, new Rectangle(4, 86, Width - 8, 22), EditorChrome.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }

        private static Color PresetColor(string name, bool top) => name switch
        {
            "Smoke" => top ? Color.FromArgb(90, 98, 110) : Color.FromArgb(25, 29, 36),
            "Rain" => top ? Color.FromArgb(58, 104, 155) : Color.FromArgb(18, 31, 49),
            "Snow" => top ? Color.FromArgb(210, 230, 255) : Color.FromArgb(53, 72, 97),
            "Magic" or "Portal" => top ? Color.FromArgb(76, 54, 210) : Color.FromArgb(18, 142, 196),
            _ => top ? Color.FromArgb(255, 132, 24) : Color.FromArgb(92, 17, 6),
        };
    }

    private sealed class ParticleCurveEditor : Control
    {
        private readonly Func<ParticleBezierCurve> _curve;
        private readonly Func<List<ParticleGradientStop>> _stops;
        private readonly Action<bool> _changed;
        private readonly Action _beginGesture;
        private readonly Action _endGesture;
        private readonly Action _cancelGesture;
        private int _dragControl;
        private ParticleGradientStop? _dragStop;

        public ParticleCurveEditor(Func<ParticleBezierCurve> curve, Func<List<ParticleGradientStop>> stops,
            Action<bool> changed, Action beginGesture, Action endGesture, Action cancelGesture)
        {
            _curve = curve;
            _stops = stops;
            _changed = changed;
            _beginGesture = beginGesture; _endGesture = endGesture; _cancelGesture = cancelGesture;
            TabStop = true;
            DoubleBuffered = true;
            BackColor = EditorChrome.Canvas;
            Cursor = Cursors.Cross;
            AccessibleName = "Particle lifetime Bezier and gradient editor";
            MouseDown += OnEditorMouseDown;
            MouseMove += OnEditorMouseMove;
            MouseUp += (_, _) => EndGesture();
            MouseCaptureChanged += (_, _) => { if (!Capture) EndGesture(); };
            KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Escape || (_dragControl == 0 && _dragStop is null)) return;
                _dragControl = 0; _dragStop = null;
                _cancelGesture(); Capture = false; Invalidate(); e.Handled = true;
            };
            MouseClick += OnEditorMouseClick;
            MouseWheel += OnEditorMouseWheel;
            MouseDoubleClick += OnEditorDoubleClick;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle graph = GraphBounds;
            using Pen grid = new(Color.FromArgb(65, EditorChrome.Border));
            for (int i = 0; i <= 4; i++)
            {
                int x = graph.Left + graph.Width * i / 4;
                int y = graph.Top + graph.Height * i / 4;
                e.Graphics.DrawLine(grid, x, graph.Top, x, graph.Bottom);
                e.Graphics.DrawLine(grid, graph.Left, y, graph.Right, y);
            }
            ParticleBezierCurve curve = _curve();
            PointF c1 = CurvePoint(graph, (float)curve.X1, (float)curve.Y1);
            PointF c2 = CurvePoint(graph, (float)curve.X2, (float)curve.Y2);
            PointF start = CurvePoint(graph, 0, 0);
            PointF end = CurvePoint(graph, 1, 1);
            using Pen handles = new(Color.FromArgb(130, EditorChrome.Muted), 1f);
            e.Graphics.DrawLine(handles, start, c1);
            e.Graphics.DrawLine(handles, end, c2);
            using Pen curvePen = new(EditorChrome.Accent, 2.5f);
            PointF previous = start;
            for (int i = 1; i <= 100; i++)
            {
                float t = i / 100f;
                PointF next = CurvePoint(graph, t, curve.Evaluate(t));
                e.Graphics.DrawLine(curvePen, previous, next);
                previous = next;
            }
            using SolidBrush point = new(EditorChrome.Accent);
            e.Graphics.FillEllipse(point, c1.X - 5, c1.Y - 5, 10, 10);
            e.Graphics.FillEllipse(point, c2.X - 5, c2.Y - 5, 10, 10);
            DrawGradient(e.Graphics, GradientBounds);
        }

        private Rectangle GraphBounds => new(12, 8, Math.Max(20, Width - 24), Math.Max(50, Height - 68));
        private Rectangle GradientBounds => new(12, Math.Max(16, Height - 46), Math.Max(20, Width - 24), 22);

        private void DrawGradient(Graphics graphics, Rectangle bounds)
        {
            List<ParticleGradientStop> stops = EnsureStops();
            using SolidBrush checkerA = new(EditorChrome.Canvas);
            using SolidBrush checkerB = new(EditorChrome.Raised);
            for (int y = 0; y < bounds.Height; y += 8)
                for (int x = 0; x < bounds.Width; x += 8)
                    graphics.FillRectangle(((x / 8 + y / 8) & 1) == 0 ? checkerA : checkerB,
                        bounds.Left + x, bounds.Top + y, Math.Min(8, bounds.Width - x), Math.Min(8, bounds.Height - y));
            using Pen strip = new(Color.White);
            for (int x = 0; x < bounds.Width; x++)
            {
                float t = x / (float)Math.Max(1, bounds.Width - 1);
                Vector4 color = SampleStopChannels(stops, t);
                strip.Color = Color.FromArgb((int)(Math.Clamp(color.W, 0f, 1f) * 255),
                    (int)(Math.Clamp(color.X, 0f, 1f) * 255), (int)(Math.Clamp(color.Y, 0f, 1f) * 255), (int)(Math.Clamp(color.Z, 0f, 1f) * 255));
                graphics.DrawLine(strip, bounds.Left + x, bounds.Top, bounds.Left + x, bounds.Bottom);
            }
            using Pen border = new(EditorChrome.Border);
            graphics.DrawRectangle(border, bounds);
            for (int i = 0; i < stops.Count; i++)
            {
                float x = bounds.Left + (float)Math.Clamp(stops[i].Position, 0d, 1d) * bounds.Width;
                PointF[] marker = [new(x, bounds.Bottom + 2), new(x - 5, bounds.Bottom + 10), new(x + 5, bounds.Bottom + 10)];
                using SolidBrush brush = new(ToColor(stops[i].Color));
                graphics.FillPolygon(brush, marker);
                graphics.DrawPolygon(Pens.White, marker);
            }
        }

        private void OnEditorMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Rectangle graph = GraphBounds;
            ParticleBezierCurve curve = _curve();
            PointF c1 = CurvePoint(graph, (float)curve.X1, (float)curve.Y1);
            PointF c2 = CurvePoint(graph, (float)curve.X2, (float)curve.Y2);
            if (Distance(e.Location, c1) < 11) _dragControl = 1;
            else if (Distance(e.Location, c2) < 11) _dragControl = 2;
            else
            {
                Rectangle gradient = GradientBounds;
                List<ParticleGradientStop> stops = EnsureStops();
                for (int i = 0; i < stops.Count; i++)
                {
                    float x = gradient.Left + (float)stops[i].Position * gradient.Width;
                    if (Math.Abs(e.X - x) <= 7 && e.Y >= gradient.Bottom && e.Y <= gradient.Bottom + 16)
                    { _dragStop = stops[i]; break; }
                }
            }
            if (_dragControl != 0 || _dragStop is not null) { _beginGesture(); Capture = true; }
        }

        private void EndGesture()
        {
            if (_dragControl == 0 && _dragStop is null) return;
            _dragControl = 0; _dragStop = null;
            _endGesture(); Capture = false;
        }

        private void OnEditorMouseMove(object? sender, MouseEventArgs e)
        {
            if (_dragControl > 0)
            {
                Rectangle graph = GraphBounds;
                double x = Math.Clamp((e.X - graph.Left) / (double)Math.Max(1, graph.Width), 0d, 1d);
                double y = Math.Clamp((graph.Bottom - e.Y) / (double)Math.Max(1, graph.Height), 0d, 1d);
                ParticleBezierCurve curve = _curve();
                if (_dragControl == 1) { curve.X1 = Math.Min(x, curve.X2); curve.Y1 = y; }
                else { curve.X2 = Math.Max(x, curve.X1); curve.Y2 = y; }
                _changed(true);
                Invalidate();
            }
            else if (_dragStop is not null)
            {
                Rectangle gradient = GradientBounds;
                List<ParticleGradientStop> stops = EnsureStops();
                if (stops.Contains(_dragStop))
                {
                    _dragStop.Position = Math.Clamp((e.X - gradient.Left) / (double)Math.Max(1, gradient.Width), 0d, 1d);
                    _changed(false);
                    Invalidate();
                }
            }
        }

        private void OnEditorDoubleClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Rectangle gradient = GradientBounds;
            if (e.Y < gradient.Top - 4 || e.Y > gradient.Bottom + 14) return;
            double position = Math.Clamp((e.X - gradient.Left) / (double)Math.Max(1, gradient.Width), 0d, 1d);
            List<ParticleGradientStop> stops = EnsureStops();
            ParticleGradientStop? existing = e.Y >= gradient.Bottom
                ? stops.FirstOrDefault(stop => Math.Abs(e.X - (gradient.Left + stop.Position * gradient.Width)) <= 7)
                : null;
            if (existing is null && stops.Count >= 128) return;
            ParticleColor sample = existing?.Color ?? SampleStops(stops, (float)position);
            using ColorDialog dialog = new() { Color = ToColor(sample), FullOpen = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            ParticleColor colour = new(dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, sample.A);
            if (existing is not null) existing.Color = colour;
            else stops.Add(new ParticleGradientStop { Position = position, Color = colour });
            _changed(false);
            Invalidate();
        }

        private void OnEditorMouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            Rectangle gradient = GradientBounds;
            List<ParticleGradientStop> stops = EnsureStops();
            if (stops.Count <= 2) return;
            int closest = -1;
            float distance = 9f;
            for (int i = 0; i < stops.Count; i++)
            {
                float x = gradient.Left + (float)stops[i].Position * gradient.Width;
                float next = Math.Abs(e.X - x);
                if (next < distance && e.Y >= gradient.Bottom) { closest = i; distance = next; }
            }
            if (closest < 0) return;
            stops.RemoveAt(closest);
            _changed(false);
            Invalidate();
        }

        private void OnEditorMouseWheel(object? sender, MouseEventArgs e)
        {
            Rectangle gradient = GradientBounds;
            if (e.Y < gradient.Top - 6 || e.Y > gradient.Bottom + 14) return;
            List<ParticleGradientStop> stops = EnsureStops();
            ParticleGradientStop? closest = stops.OrderBy(stop => Math.Abs(e.X - (gradient.Left + stop.Position * gradient.Width))).FirstOrDefault();
            if (closest is null || Math.Abs(e.X - (gradient.Left + closest.Position * gradient.Width)) > 10) return;
            closest.Color.A = Math.Clamp(closest.Color.A + Math.Sign(e.Delta) * .05f, 0f, 1f);
            _changed(false);
            Invalidate();
        }

        private List<ParticleGradientStop> EnsureStops()
        {
            List<ParticleGradientStop> stops = _stops();
            if (stops.Count == 0)
            {
                stops.Add(new ParticleGradientStop { Position = 0, Color = new ParticleColor(1f, 0.9f, 0.5f, 1f) });
                stops.Add(new ParticleGradientStop { Position = 1, Color = new ParticleColor(0.8f, 0.1f, 0.02f, 0f) });
            }
            return stops;
        }

        private static Vector4 SampleStopChannels(List<ParticleGradientStop> stops, float t)
        {
            // NormaliseConfig already orders stops after each accepted edit. No per-pixel sorting,
            // enumerators or ParticleColor allocations during a gradient drag.
            ParticleGradientStop before = stops[0], after = stops[^1];
            for (int i = 0; i < stops.Count; i++)
            {
                if (stops[i].Position <= t) before = stops[i];
                if (stops[i].Position >= t) { after = stops[i]; break; }
            }
            float range = (float)(after.Position - before.Position);
            float amount = range <= .0001f ? 0f : Math.Clamp((t - (float)before.Position) / range, 0f, 1f);
            return Vector4.Lerp(new Vector4(before.Color.R, before.Color.G, before.Color.B, before.Color.A),
                new Vector4(after.Color.R, after.Color.G, after.Color.B, after.Color.A), amount);
        }

        private static ParticleColor SampleStops(List<ParticleGradientStop> stops, float t)
        {
            Vector4 colour = SampleStopChannels(stops, t);
            return new ParticleColor(colour.X, colour.Y, colour.Z, colour.W);
        }

        private static PointF CurvePoint(Rectangle bounds, float x, float y) => new(bounds.Left + x * bounds.Width, bounds.Bottom - y * bounds.Height);
        private static float Distance(Point p, PointF q) => MathF.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
        private static Color ToColor(ParticleColor color) => Color.FromArgb(
            (int)(Math.Clamp(color.A, 0f, 1f) * 255), (int)(Math.Clamp(color.R, 0f, 1f) * 255),
            (int)(Math.Clamp(color.G, 0f, 1f) * 255), (int)(Math.Clamp(color.B, 0f, 1f) * 255));
    }
}
