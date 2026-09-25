using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using Genesis.Application.Editors.Suite;
using Genesis.Rendering.Meshes;
using Genesis.Rendering.Core;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Commands;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Studio.Forms;

/// <summary>
/// Live 3D + HUD host for Help → Commands Visual Test. Draws the permanent five-subject
/// reference scene every frame, then applies the command currently under test so it stays visible.
/// </summary>
public sealed class CommandVisualTestPanel : Panel
{
    private static readonly string[] DemoResourceFiles =
    [
        "Assets/Models/Visual Test Floor.model.json",
        "Assets/Models/Visual Test Cube.model.json",
        "Assets/Models/Visual Test Sphere.model.json",
        "Assets/Models/Visual Test Pyramid.model.json",
        "Assets/Models/Visual Test Cylinder.model.json",
        "Assets/Models/Visual Test Cone.model.json",
        "Assets/Models/Visual Test Ring.model.json",
        "Assets/Models/Visual Test Spark.model.json",
        "Assets/Models/Visual Test Shadow.model.json",
        "Assets/Shaders/Visual Test Checker.shader.json",
        "Assets/Shaders/Visual Test Iridescent.shader.json",
        "Assets/Shaders/Visual Test Glow.shader.json",
        "Assets/Particles/Visual Test Sparks.particle.json",
        "Assets/Scripts/Command Visual Baseline.pgsl",
    ];

    private readonly EditorViewport3D _viewport = new() { Dock = DockStyle.Fill };
    private readonly Panel _modeBanner = new() { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 7, 8, 7) };
    private readonly Label _modeBadge = new() { Dock = DockStyle.Left, Width = 220, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 8, 0) };
    private readonly Label _modeDescription = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 8, 0) };
    private readonly PgslContext _pgsl = new()
    {
        RoomWidth = 1280,
        RoomHeight = 720,
        DrawColor = Color.FromArgb(255, 70, 170, 255),
        DrawAlpha = 1d,
    };

    private string _hudTitle = "Visual Test";
    private string _hudCommand = "Ready";
    private string _hudDetail = "";
    private MemberInfo? _liveMember;
    private object[]? _liveArgs;
    private bool _liveIsEngine;
    private bool _liveInScene;
    private IRenderController? _activeHud;
    private bool _fogEnabled;
    private float _fogStart = 18f;
    private float _fogEnd = 70f;
    private float _fogDensity = 0.04f;
    private Vector4 _fogColor = new(0.55f, 0.62f, 0.72f, 0.85f);
    private bool _lighting = true;
    private Vector3 _lightDirection = Vector3.Normalize(new Vector3(0.55f, -0.72f, 0.42f));
    private static readonly Vector3 BaselineAmbient = new(0.026f, 0.030f, 0.038f);
    private Vector3 _ambient = BaselineAmbient;
    private RenderDebugView _debugView = RenderDebugView.Shaded;
    private float _fov = 60f;
    private bool _orbitEnabled;
    private float _scenePhaseSeconds;
    private float _orbitSmoothedDelta = 1f / 60f;
    private long _lastOrbitTimestamp;
    private bool _visualIsEngine;
    private IRenderController? _meshOwner;
    private MeshHandle _checkerFloorMesh;
    private MeshHandle _pyramidMesh;
    private MeshHandle _cylinderMesh;
    private MeshHandle _coneMesh;
    private MeshHandle _ringMesh;
    private CompiledScriptAsset? _pgslBaseline;
    private PgslVm? _pgslBaselineVm;
    private string _pgslBaselineError = string.Empty;
    private Exception? _lastCommandException;
    private long _commandInvocationCount;

    private bool _sceneClockFrozen;

    private static string DemoProjectPath
    {
        get
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "CommandVisualTest");
            if (File.Exists(Path.Combine(bundled, "Assets", "Scripts", "Command Visual Baseline.pgsl")))
                return bundled;

            string? search = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(search))
            {
                string candidate = Path.Combine(
                    search,
                    "Source",
                    "Genesis.Application.Studio",
                    "Assets",
                    "CommandVisualTest");
                if (File.Exists(Path.Combine(candidate, "Assets", "Scripts", "Command Visual Baseline.pgsl")))
                    return candidate;
                string? parent = Path.GetDirectoryName(search);
                if (string.Equals(parent, search, StringComparison.OrdinalIgnoreCase))
                    break;
                search = parent;
            }

            return bundled;
        }
    }

    public CommandVisualTestPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(28, 32, 40);
        _modeBanner.Controls.Add(_modeDescription);
        _modeBanner.Controls.Add(_modeBadge);
        Controls.Add(_viewport);
        Controls.Add(_modeBanner);
        _modeBanner.BringToFront();
        _viewport.Camera.Distance = 8.5f;
        _viewport.Camera.Pitch = -0.30f;
        _viewport.Camera.Yaw = MathF.PI + 0.08f;
        _viewport.Camera.Target = new Vector3(0f, 1.35f, 0f);
        _viewport.FieldOfViewDegrees = _fov;
        _viewport.FloorStyle = EditorFloorStyle.None;
        _viewport.SceneStateFactory = BuildSceneState;
        _viewport.DrawScene += DrawScene;
        _viewport.DrawOverlay += DrawHud;
        SetVisualMode(engine: false);
        EditorChrome.Changed += OnChromeChanged;
        SubscribeEngine();
    }

    public EditorViewport3D Viewport => _viewport;

    public string VisualModeBadge => _visualIsEngine
        ? "ENGINE BACKEND / API"
        : "PGSL GAME CODE";

    public string VisualModeDescription => _visualIsEngine
        ? "Engine.* commands drive renderer/backend state directly."
        : "PGSL commands execute through the VM and game-code draw surface.";

    /// <summary>Short, unambiguous renderer name shown in the viewport HUD.</summary>
    public string ActiveRendererLabel
    {
        get
        {
            RenderBackendOption backend = _viewport.BackendOverride ?? RenderBackendSelection.EffectiveBackend;
            RenderBackendDescriptor descriptor = RenderBackendCatalog.Describe(backend);
            return descriptor.ShortName == descriptor.DisplayName
                ? descriptor.DisplayName
                : $"{descriptor.DisplayName} ({descriptor.ShortName})";
        }
    }

    /// <summary>Explicit test path name shown in the viewport HUD.</summary>
    public string VisualTestModeLabel => _visualIsEngine ? "ENGINE API" : "PGSL GAME CODE";

    public string HudRendererLabel => $"Renderer: {ActiveRendererLabel}";

    public string HudModeLabel => $"Test: {VisualTestModeLabel}";

    public int VisualDemoSubjectCount => 5;

    public int VisualDemoResourceCount => DemoResourceFiles.Count(file =>
        File.Exists(Path.Combine(DemoProjectPath, file.Replace('/', Path.DirectorySeparatorChar))));

    public bool PgslBaselineReady => _pgslBaseline is not null && string.IsNullOrWhiteSpace(_pgslBaselineError);

    public Exception? LastCommandException => _lastCommandException;
    public long CommandInvocationCount => _commandInvocationCount;

    public void SetVisualMode(bool engine)
    {
        _visualIsEngine = engine;
        ApplyModeBanner();
    }

    public void SetStatus(string title, string command, string detail)
    {
        _hudTitle = title;
        _hudCommand = command;
        _hudDetail = detail;
        ApplyModeBanner();
    }

    public void SetLivePgsl(MemberInfo? member, object[]? args, bool inScene)
    {
        ResetBaselineEnvironment();
        SetVisualMode(engine: false);
        _liveMember = member;
        _liveArgs = args;
        _liveIsEngine = false;
        _liveInScene = inScene;
        _lastCommandException = null;
        _commandInvocationCount = 0;
        _viewport.Host.ClearRenderFault();
    }

    public void SetLiveEngine(MethodInfo? method, object[]? args)
    {
        ResetBaselineEnvironment();
        SetVisualMode(engine: true);
        _liveMember = method;
        _liveArgs = args;
        _liveIsEngine = true;
        _liveInScene = false;
        _lastCommandException = null;
        _commandInvocationCount = 0;
        _viewport.Host.ClearRenderFault();
    }

    public void ClearLive()
    {
        _liveMember = null;
        _liveArgs = null;
        ResetBaselineEnvironment();
    }

    private void ResetBaselineEnvironment()
    {
        _fogEnabled = false;
        _lighting = true;
        _ambient = BaselineAmbient;
        _lightDirection = Vector3.Normalize(new Vector3(0.55f, -0.72f, 0.42f));
        _debugView = RenderDebugView.Shaded;
        _fov = 60f;
        _viewport.FieldOfViewDegrees = _fov;
    }

    public void SetOrbiting(bool enabled)
    {
        _orbitEnabled = enabled;
        _viewport.NavigationEnabled = !enabled;
        if (enabled)
        {
            _viewport.Camera.Target = new Vector3(0f, 1.35f, 0f);
            ResetOrbitMotion();
        }
    }

    /// <summary>
    /// Pins the visual-test scene clock and camera so backend captures compare the same pose.
    /// </summary>
    public void FreezeSceneClock(float seconds)
    {
        _sceneClockFrozen = true;
        _orbitEnabled = false;
        _viewport.NavigationEnabled = false;
        _scenePhaseSeconds = Math.Max(0f, seconds);
        _orbitSmoothedDelta = 1f / 60f;
        _lastOrbitTimestamp = 0;
        _viewport.Camera.Distance = 8.5f;
        _viewport.Camera.Pitch = -0.30f;
        _viewport.Camera.Yaw = MathF.PI + 0.08f;
        _viewport.Camera.Target = new Vector3(0f, 1.35f, 0f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            EditorChrome.Changed -= OnChromeChanged;
            UnsubscribeEngine();
        }
        base.Dispose(disposing);
    }

    private void OnChromeChanged(object? sender, EventArgs e) => ApplyModeBanner();

    private void ApplyModeBanner()
    {
        _modeBanner.BackColor = EditorChrome.Surface;
        _modeBanner.Font = EditorChrome.BaseFont;
        _modeBadge.Text = VisualModeBadge;
        _modeBadge.Font = EditorChrome.HeadingFont;
        _modeBadge.ForeColor = EditorChrome.IsDark ? Color.FromArgb(20, 24, 32) : Color.White;
        _modeBadge.BackColor = _visualIsEngine ? EditorChrome.Warning : EditorChrome.Accent;
        string command = string.IsNullOrWhiteSpace(_hudCommand) ? "Ready" : _hudCommand;
        _modeDescription.Text = $"{VisualModeDescription}   •   Renderer: {ActiveRendererLabel}   •   {command}";
        _modeDescription.Font = EditorChrome.BaseFont;
        _modeDescription.ForeColor = EditorChrome.Text;
        _modeDescription.BackColor = EditorChrome.Raised;
    }

    private void SubscribeEngine()
    {
        Engine.LightingChanged += OnLightingChanged;
        Engine.FogChanged += OnFogChanged;
        Engine.FogStartChanged += OnFogStartChanged;
        Engine.FogEndChanged += OnFogEndChanged;
        Engine.FogDensityChanged += OnFogDensityChanged;
        Engine.FogColourChanged += OnFogColourChanged;
        Engine.AmbientLightChanged += OnAmbientLightChanged;
        Engine.GlobalLightDirectionChanged += OnGlobalLightDirectionChanged;
        Engine.DebugViewChanged += OnDebugViewChanged;
        Engine.CameraFovChanged += OnCameraFovChanged;
        Engine.CameraPositionChanged += OnCameraPositionChanged;
        Engine.CameraTargetChanged += OnCameraTargetChanged;
        Engine.CameraFarPlaneChanged += OnCameraFarPlaneChanged;
        Engine.CameraNearPlaneChanged += OnCameraNearPlaneChanged;
        Engine.WindowTitleChanged += OnWindowTitleChanged;
        Engine.RectDrawSubmitted += OnRectDrawn;
        Engine.LineDrawSubmitted += OnLineDrawn;
        Engine.TextDrawSubmitted += OnTextDrawn;
    }

    private void UnsubscribeEngine()
    {
        Engine.LightingChanged -= OnLightingChanged;
        Engine.FogChanged -= OnFogChanged;
        Engine.FogStartChanged -= OnFogStartChanged;
        Engine.FogEndChanged -= OnFogEndChanged;
        Engine.FogDensityChanged -= OnFogDensityChanged;
        Engine.FogColourChanged -= OnFogColourChanged;
        Engine.AmbientLightChanged -= OnAmbientLightChanged;
        Engine.GlobalLightDirectionChanged -= OnGlobalLightDirectionChanged;
        Engine.DebugViewChanged -= OnDebugViewChanged;
        Engine.CameraFovChanged -= OnCameraFovChanged;
        Engine.CameraPositionChanged -= OnCameraPositionChanged;
        Engine.CameraTargetChanged -= OnCameraTargetChanged;
        Engine.CameraFarPlaneChanged -= OnCameraFarPlaneChanged;
        Engine.CameraNearPlaneChanged -= OnCameraNearPlaneChanged;
        Engine.WindowTitleChanged -= OnWindowTitleChanged;
        Engine.RectDrawSubmitted -= OnRectDrawn;
        Engine.LineDrawSubmitted -= OnLineDrawn;
        Engine.TextDrawSubmitted -= OnTextDrawn;
    }

    private void OnLightingChanged(bool value) => _lighting = value;
    private void OnFogChanged(bool value) => _fogEnabled = value;
    private void OnFogStartChanged(float value) => _fogStart = Math.Clamp(value, 1f, 200f);
    private void OnFogEndChanged(float value) => _fogEnd = Math.Clamp(value, _fogStart + 4f, 400f);
    private void OnFogDensityChanged(float value) => _fogDensity = Math.Clamp(value, 0.001f, 0.2f);
    private void OnFogColourChanged(Vector4 value) => _fogColor = value;
    private void OnAmbientLightChanged(Vector3 value) => _ambient = value;
    private void OnGlobalLightDirectionChanged(Vector3 value)
    {
        if (value.LengthSquared() > 0.001f)
            _lightDirection = Vector3.Normalize(value);
    }
    private void OnDebugViewChanged(RenderDebugView value) => _debugView = value;
    private void OnCameraFovChanged(float value)
    {
        _fov = Math.Clamp(value, 20f, 110f);
        _viewport.FieldOfViewDegrees = _fov;
    }
    private void OnCameraPositionChanged(Vector3 value)
    {
        if (!_orbitEnabled)
            _viewport.Camera.Target = value;
    }
    private void OnCameraTargetChanged(Vector3 value)
    {
        if (!_orbitEnabled)
            _viewport.Camera.Target = value;
    }
    private void OnCameraFarPlaneChanged(float value) => _viewport.FarPlane = Math.Clamp(value, 20f, 4000f);
    private void OnCameraNearPlaneChanged(float value) => _viewport.NearPlane = Math.Clamp(value, 0.05f, 10f);
    private void OnWindowTitleChanged(string value) => _hudDetail = "Window title: " + value;

    private void OnRectDrawn(Engine.RectDrawSpec spec)
    {
        if (_activeHud is null) return;
        _activeHud.DrawRect(
            spec.X, spec.Y, spec.W, spec.H,
            new RenderColor(spec.R, spec.G, spec.B, spec.A),
            spec.Filled,
            depth: -9780);
    }

    private void OnLineDrawn(Engine.LineDrawSpec spec)
    {
        if (_activeHud is null) return;
        _activeHud.DrawLine(
            spec.X1, spec.Y1, spec.X2, spec.Y2,
            new RenderColor(spec.R, spec.G, spec.B, spec.A),
            thickness: 2.5f,
            depth: -9780);
    }

    private void OnTextDrawn(Engine.TextDrawSpec spec)
    {
        if (_activeHud is null) return;
        _activeHud.DrawText(
            spec.Text ?? "",
            spec.X, spec.Y, spec.Size <= 0f ? 14f : spec.Size,
            new RenderColor(spec.R, spec.G, spec.B, spec.A <= 0f ? 1f : spec.A));
    }

    private Mesh3DState BuildSceneState()
    {
        Mesh3DState state = EditorSceneLighting.Create(showFloor: false);
        state.LightingEnabled = _lighting;
        state.LightingWeight = _lighting ? 1f : 0f;
        state.LightDirection = _lightDirection;
        // This reference scene has no editor sun: the emissive sphere is its key light and
        // the pyramid particle emitter is its only secondary light. Engine lighting commands
        // may still temporarily alter the state while their individual visual test is active.
        state.SunColor = Vector3.Zero;
        state.SunIntensity = 0f;
        state.AmbientColor = _ambient;
        state.AmbientGroundColor = _ambient;
        state.FogEnabled = _fogEnabled;
        state.FogScreenSpace = _fogEnabled;
        state.FogStart = _fogStart;
        state.FogEnd = _fogEnd;
        state.FogDensity = _fogDensity;
        state.FogColor = _fogColor;
        state.DebugView = _debugView;
        state.CameraFarPlane = _viewport.FarPlane;
        state.FrustumCullingEnabled = true;
        state.ShowFloor = false;
        state.ShowSunVisual = false;
        state.BackgroundColor = new Vector3(0.075f, 0.082f, 0.095f);
        return state;
    }

    private void DrawScene(IRenderController renderer)
    {
        float seconds = AdvanceSceneClock();
        if (_orbitEnabled)
        {
            _viewport.Camera.Target = new Vector3(0f, 1.35f, 0f);
            _viewport.Camera.Yaw = MathF.PI + 0.08f + seconds * 0.23f;
            _viewport.Camera.Pitch = -0.30f + 0.035f * MathF.Sin(seconds * 0.28f);
        }

        BindPgsl(renderer, is3D: true);
        if (_visualIsEngine)
            DrawEngineBaseline(renderer, seconds);
        else
            DrawPgslBaseline(seconds);

        if (!_liveIsEngine && _liveInScene)
            InvokeLive(overlay: false);
    }

    private void DrawHud(IRenderController renderer)
    {
        // Run the command overlay first. Canonical labels are drawn afterwards so a command that
        // submits an arbitrary rectangle or text cannot obscure the renderer/mode identification
        // in a captured frame.
        _activeHud = renderer;
        try
        {
            BindPgsl(renderer, is3D: false);
            Engine.SetDrawCommandSink(renderer);
            if (_liveIsEngine || !_liveInScene)
                InvokeLive(overlay: true);
        }
        finally
        {
            Engine.SetDrawCommandSink(null!);
            _activeHud = null;
        }

        Vector3 eye = _viewport.Camera.Eye;
        string position = $"Position: {MathF.Round(eye.X):0}, {MathF.Round(eye.Y):0}, {MathF.Round(eye.Z):0}";
        RenderColor panel = new(0.015f, 0.018f, 0.024f, 0.82f);
        RenderColor text = new(1f, 1f, 1f, 1f);

        // Keep the reference scene HUD literal and readable. The labels are repeated in the
        // viewport (not only in the WinForms banner) so a captured frame is self-describing when
        // comparing backends or PGSL versus Engine runs.
        renderer.DrawRect(10f, 10f, 132f, 34f, panel, filled: true, depth: -9800);
        renderer.DrawText("FPS: 60", 16f, 15f, 18f, text);
        renderer.DrawRect(10f, 50f, 286f, 34f, panel, filled: true, depth: -9800);
        renderer.DrawText(position, 16f, 55f, 18f, text);
        renderer.DrawRect(10f, 90f, 360f, 30f, panel, filled: true, depth: -9800);
        renderer.DrawText(HudRendererLabel, 16f, 96f, 15f, text);
        renderer.DrawRect(10f, 124f, 300f, 30f, panel, filled: true, depth: -9800);
        renderer.DrawText(HudModeLabel, 16f, 130f, 15f, text);

        string detail = !string.IsNullOrWhiteSpace(_pgslBaselineError) && !_visualIsEngine
            ? "PGSL resource scene: " + _pgslBaselineError
            : string.IsNullOrWhiteSpace(_hudDetail) ? HudStats() : _hudDetail;
        float footerY = Math.Max(162f, _viewport.SurfaceHeight - 34f);
        renderer.DrawRect(10f, footerY, Math.Min(900f, Math.Max(360f, _viewport.SurfaceWidth - 20f)), 26f,
            new RenderColor(0.015f, 0.018f, 0.024f, 0.72f), filled: true, depth: -9800);
        renderer.DrawText($"{_hudCommand}  ·  {detail}", 16f, footerY + 5f, 11f,
            new RenderColor(0.90f, 0.93f, 0.98f, 0.96f));
    }

    private string HudStats() =>
        $"{ActiveRendererLabel} · {RendererMode()} · Fog {(_fogEnabled ? "on" : "off")} · Lit {(_lighting ? "on" : "off")} · FOV {_fov:0}";

    private string RendererMode() => _visualIsEngine ? "Engine scene" : "Real PGSL resources";

    private float DemoSubjectSpacing()
    {
        float height = Math.Max(1f, _viewport.SurfaceHeight);
        float aspect = Math.Max(1f, _viewport.SurfaceWidth / height);
        return Math.Clamp(aspect * 1.28f, 3f, 5f);
    }

    private void ResetOrbitMotion()
    {
        _scenePhaseSeconds = 0f;
        _orbitSmoothedDelta = 1f / 60f;
        _lastOrbitTimestamp = 0;
    }

    private float AdvanceSceneClock()
    {
        if (_sceneClockFrozen)
            return _scenePhaseSeconds;

        long now = Stopwatch.GetTimestamp();
        if (_lastOrbitTimestamp != 0)
        {
            float rawDelta = (float)((now - _lastOrbitTimestamp) / (double)Stopwatch.Frequency);
            if (rawDelta > 0f)
            {
                rawDelta = Math.Clamp(rawDelta, 1f / 240f, 1f / 30f);
                _orbitSmoothedDelta += (rawDelta - _orbitSmoothedDelta) * 0.25f;
            }
        }

        _lastOrbitTimestamp = now;
        _scenePhaseSeconds += _orbitSmoothedDelta;
        return _scenePhaseSeconds;
    }

    private void BindPgsl(IRenderController renderer, bool is3D)
    {
        var hud = new OverlayHud(renderer, _viewport.SurfaceWidth, _viewport.SurfaceHeight);
        _pgsl.DrawSurface = new PgslRenderDrawSurface(
            renderer,
            hud,
            _viewport.SurfaceWidth,
            _viewport.SurfaceHeight,
            commands: renderer,
            projectPath: DemoProjectPath,
            is3DActive: is3D);
        _pgsl.View3D = _viewport.ViewMatrix;
        _pgsl.Proj3D = _viewport.ProjectionMatrix;
        PgslCommands.BindContext(_pgsl);
    }

    private void InvokeLive(bool overlay)
    {
        if (_liveMember is null) return;
        bool previousSubmit = RenderAutoState.AllowDrawSubmit;
        RenderAutoState.AllowDrawSubmit = true;
        try
        {
            object? sample = null;
            if (_liveMember is MethodInfo method)
                sample = method.Invoke(null, _liveArgs);
            else if (_liveMember is PropertyInfo property)
            {
                sample = property.CanRead ? property.GetValue(null) : null;
                if (property.CanWrite && sample is not null)
                    property.SetValue(null, sample);
            }

            _commandInvocationCount++;

            if (overlay && sample is not null && sample is not DBNull)
                _hudDetail = sample.ToString() ?? _hudDetail;
        }
        catch (Exception exception)
        {
            _lastCommandException = exception is TargetInvocationException { InnerException: not null } wrapper
                ? wrapper.InnerException
                : exception;
        }
        finally
        {
            RenderAutoState.AllowDrawSubmit = previousSubmit;
        }
    }

    private void DrawEngineBaseline(IRenderController renderer, float seconds)
    {
        EnsureDemoMeshes(renderer);
        float spacing = DemoSubjectSpacing();

        DrawMesh(renderer, _checkerFloorMesh, Vector3.Zero, Vector3.One, Vector3.Zero,
            RenderColor.White, MeshDrawFlags.IsFloor | MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow);
        DrawEngineGroundShadows(renderer, spacing);

        MeshDrawCall cube = CreateDraw(
            renderer.GetBuiltinMesh(BuiltinMeshKind.Cube),
            new Vector3(-2f * spacing, 1.1f, 0f),
            new Vector3(2.2f),
            new Vector3(0f, seconds * 24f, 0f),
            RenderColor.White,
            MeshDrawFlags.None);
        ObjectDrawPass.TryApplyMeshShader(
            renderer,
            DemoProjectPath,
            "Assets/Shaders/Visual Test Iridescent.shader.json",
            ref cube);
        renderer.DrawMesh(cube);

        MeshDrawCall sphere = CreateDraw(
            renderer.GetBuiltinMesh(BuiltinMeshKind.Sphere),
            new Vector3(-spacing, 1.1f, 0f),
            new Vector3(2.2f),
            Vector3.Zero,
            new RenderColor(1f, 0.62f, 0.10f),
            MeshDrawFlags.Emissive | MeshDrawFlags.NoShadow);
        sphere.Emissive = 2.8f;
        ObjectDrawPass.TryApplyMeshShader(
            renderer,
            DemoProjectPath,
            "Assets/Shaders/Visual Test Glow.shader.json",
            ref sphere);
        renderer.DrawMesh(sphere);
        renderer.AddPointLight(new Vector3(-spacing, 1.35f, 0f), new Vector3(1f, 0.34f, 0.035f), 24f, 4.2f);
        for (int halo = 1; halo <= 3; halo++)
        {
            float alpha = 0.13f - halo * 0.025f;
            DrawMesh(renderer, renderer.GetBuiltinMesh(BuiltinMeshKind.Sphere), new Vector3(-spacing, 1.1f, 0f),
                new Vector3(2.2f + halo * 0.36f), Vector3.Zero,
                new RenderColor(1f, 0.34f, 0.035f, alpha),
                MeshDrawFlags.Additive | MeshDrawFlags.Emissive | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow,
                alpha: alpha,
                emissive: 3.5f);
        }

        DrawMesh(renderer, _pyramidMesh, new Vector3(0f, 1.2f, 0f), new Vector3(2.2f, 2.4f, 2.2f),
            new Vector3(0f, seconds * 18f, 0f), new RenderColor(1f, 0.58f, 0.16f));
        renderer.AddPointLight(new Vector3(0f, 2.55f, 0f), new Vector3(1f, 0.76f, 0.18f), 7f, 1.35f);
        DrawEngineSparks(renderer, seconds);

        DrawMesh(renderer, _cylinderMesh, new Vector3(spacing, 1.15f, 0f), new Vector3(2.1f, 2.3f, 2.1f),
            new Vector3(0f, seconds * 720f, 0f), new RenderColor(0.72f, 0.76f, 0.82f));
        for (int ring = 0; ring < 3; ring++)
        {
            float scale = 2.65f + ring * 0.15f;
            DrawMesh(renderer, _ringMesh, new Vector3(spacing, 0.75f + ring * 0.4f, 0f), new Vector3(scale),
                new Vector3(0f, seconds * (360f + ring * 60f), 0f),
                new RenderColor(0.86f, 0.91f, 1f, 0.24f),
                MeshDrawFlags.Additive | MeshDrawFlags.NoShadow | MeshDrawFlags.NoDepthWrite,
                alpha: 0.24f);
        }

        float pulse = 1f + MathF.Sin(seconds * 2.62f) * 0.10f;
        DrawMesh(renderer, _coneMesh, new Vector3(2f * spacing, 1.2f, 0f), new Vector3(2.2f, 2.4f, 2.2f) * pulse,
            Vector3.Zero, new RenderColor(0.82f, 0.86f, 0.92f));
        for (int ghost = 1; ghost <= 3; ghost++)
        {
            float grow = 1f + ghost * 0.18f;
            DrawMesh(renderer, _coneMesh, new Vector3(2f * spacing, 1.2f, 0f), new Vector3(2.2f, 2.4f, 2.2f) * pulse * grow,
                Vector3.Zero,
                new RenderColor(0.86f, 0.92f, 1f, 0.16f),
                MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow,
                alpha: 0.16f);
        }
        for (int ring = 0; ring < 3; ring++)
        {
            float y = 0.18f + ring * 0.46f;
            float scale = 3.1f - ring * 0.78f;
            DrawMesh(renderer, _ringMesh, new Vector3(2f * spacing, y, 0f), new Vector3(scale), Vector3.Zero,
                new RenderColor(0.88f, 0.94f, 1f, 0.32f),
                MeshDrawFlags.Additive | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow,
                alpha: 0.32f);
        }
    }

    private static void DrawEngineGroundShadows(IRenderController renderer, float spacing)
    {
        MeshHandle sphere = renderer.GetBuiltinMesh(BuiltinMeshKind.Sphere);
        if (!sphere.IsValid) return;
        const MeshDrawFlags flags = MeshDrawFlags.Transparent
            | MeshDrawFlags.NoDepthWrite
            | MeshDrawFlags.NoShadow
            | MeshDrawFlags.NoCull;
        RenderColor shadow = new(0.008f, 0.010f, 0.014f, 0.62f);

        // The shadows extend away from the warm sphere at -spacing. The pyramid's compact
        // contact shadow also represents the small light immediately above its emitter.
        DrawMesh(renderer, sphere, new Vector3(-2f * spacing - 0.62f, 0.025f, 0.18f),
            new Vector3(2.8f, 0.045f, 1.45f), Vector3.Zero, shadow, flags, alpha: 0.62f);
        DrawMesh(renderer, sphere, new Vector3(0.45f, 0.025f, 0.12f),
            new Vector3(2.1f, 0.04f, 1.2f), Vector3.Zero, shadow, flags, alpha: 0.52f);
        DrawMesh(renderer, sphere, new Vector3(spacing + 0.88f, 0.025f, 0.16f),
            new Vector3(3.0f, 0.045f, 1.55f), Vector3.Zero, shadow, flags, alpha: 0.55f);
        DrawMesh(renderer, sphere, new Vector3(2f * spacing + 1.18f, 0.025f, 0.18f),
            new Vector3(3.5f, 0.05f, 1.8f), Vector3.Zero, shadow, flags, alpha: 0.48f);
    }

    private void DrawEngineSparks(IRenderController renderer, float seconds)
    {
        MeshHandle sphere = renderer.GetBuiltinMesh(BuiltinMeshKind.Sphere);
        if (!sphere.IsValid) return;
        for (int index = 0; index < 18; index++)
        {
            float phase = (seconds * 0.72f + index / 18f) % 1f;
            float angle = index * 2.399963f + seconds * 1.7f;
            float spread = 0.08f + phase * 0.36f;
            Vector3 position = new(
                MathF.Cos(angle) * spread,
                2.45f + phase * 4.2f,
                MathF.Sin(angle) * spread);
            float size = 0.19f + 0.16f * (1f - phase);
            DrawMesh(renderer, sphere, position, new Vector3(size), Vector3.Zero,
                new RenderColor(1f, 0.86f, 0.16f, 0.78f),
                MeshDrawFlags.Additive | MeshDrawFlags.Emissive | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow,
                alpha: 0.78f,
                emissive: 3f);
        }
    }

    private void DrawPgslBaseline(float seconds)
    {
        EnsurePgslBaseline();
        if (_pgslBaseline is null || _pgslBaselineVm is null) return;
        try
        {
            PgslCommands.ProjectPath = DemoProjectPath;
            _pgsl.ActiveVm = _pgslBaselineVm;
            VMEngine.Bridge.SetContext(_pgsl);
            PgslCommands.BindContext(_pgsl);
            _pgslBaselineVm.SetVariable("demo_time", (double)seconds);
            _pgslBaselineVm.SetVariable("demo_spacing", (double)DemoSubjectSpacing());
            _pgslBaselineVm.Execute(
                _pgslBaseline.CompileResult.Instructions,
                _pgslBaseline.CompileResult.Constants,
                clearVariables: false);
            _pgslBaselineError = string.Empty;
        }
        catch (Exception exception)
        {
            _pgslBaselineError = exception.GetBaseException().Message;
        }
    }

    private void EnsurePgslBaseline()
    {
        if (_pgslBaseline is not null || !string.IsNullOrWhiteSpace(_pgslBaselineError)) return;
        try
        {
            string missing = DemoResourceFiles.FirstOrDefault(file =>
                !File.Exists(Path.Combine(DemoProjectPath, file.Replace('/', Path.DirectorySeparatorChar)))) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(missing))
                throw new FileNotFoundException("Missing visual-test resource", missing);

            string scriptPath = Path.Combine(
                DemoProjectPath,
                "Assets",
                "Scripts",
                "Command Visual Baseline.pgsl");
            PgslCommands.ProjectPath = DemoProjectPath;
            _pgslBaseline = ScriptAssetCompiler.Compile(
                "Command Visual Baseline",
                File.ReadAllText(scriptPath));
            _pgslBaselineVm = VMEngine.CreateVm(debug: false);
            _pgslBaselineError = string.Empty;
        }
        catch (Exception exception)
        {
            _pgslBaselineError = exception.GetBaseException().Message;
        }
    }

    private void EnsureDemoMeshes(IRenderController renderer)
    {
        if (!ReferenceEquals(_meshOwner, renderer))
        {
            _meshOwner = renderer;
            _checkerFloorMesh = MeshHandle.Invalid;
            _pyramidMesh = MeshHandle.Invalid;
            _cylinderMesh = MeshHandle.Invalid;
            _coneMesh = MeshHandle.Invalid;
            _ringMesh = MeshHandle.Invalid;
        }
        if (!_checkerFloorMesh.IsValid)
        {
            (MeshVertex[] vertices, ushort[] indices) = MeshGeometry.BuildCheckerFloor(
                new RenderColor(0.006f, 0.007f, 0.009f),
                new RenderColor(0.82f, 0.84f, 0.88f));
            _checkerFloorMesh = renderer.RegisterMesh(vertices, indices);
        }
        if (!_pyramidMesh.IsValid)
            _pyramidMesh = MeshGeometry.RegisterPyramid(renderer, RenderColor.White);
        if (!_cylinderMesh.IsValid)
            _cylinderMesh = MeshGeometry.RegisterCylinder(renderer, RenderColor.White);
        if (!_coneMesh.IsValid)
            _coneMesh = MeshGeometry.RegisterCone(renderer, RenderColor.White);
        if (!_ringMesh.IsValid)
            _ringMesh = MeshGeometry.RegisterTorus(renderer, RenderColor.White);
    }

    private static MeshDrawCall CreateDraw(
        MeshHandle mesh,
        Vector3 position,
        Vector3 scale,
        Vector3 rotationDegrees,
        RenderColor tint,
        MeshDrawFlags flags = MeshDrawFlags.None,
        float alpha = 1f,
        float emissive = 0f)
    {
        const float DegreesToRadians = MathF.PI / 180f;
        return new MeshDrawCall
        {
            Mesh = mesh,
            World = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromYawPitchRoll(
                    rotationDegrees.Y * DegreesToRadians,
                    rotationDegrees.X * DegreesToRadians,
                    rotationDegrees.Z * DegreesToRadians)
                * Matrix4x4.CreateTranslation(position),
            Tint = tint,
            Alpha = alpha,
            Emissive = emissive,
            Flags = flags,
        };
    }

    private static void DrawMesh(
        IRenderController renderer,
        MeshHandle mesh,
        Vector3 position,
        Vector3 scale,
        Vector3 rotationDegrees,
        RenderColor tint,
        MeshDrawFlags flags = MeshDrawFlags.None,
        float alpha = 1f,
        float emissive = 0f)
    {
        if (!mesh.IsValid) return;
        MeshDrawCall draw = CreateDraw(mesh, position, scale, rotationDegrees, tint, flags, alpha, emissive);
        renderer.DrawMesh(draw);
    }

    private sealed class OverlayHud : IHudCanvas
    {
        private readonly IRenderController _renderer;
        public OverlayHud(IRenderController renderer, int width, int height)
        {
            _renderer = renderer;
            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }

        public void Text(string text, float x, float y, float size, Vector4 color) =>
            _renderer.DrawText(text, x, y, size, new RenderColor(color.X, color.Y, color.Z, color.W));

        public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color) =>
            Text(text, centerX - width * 0.25f, y, size, color);

        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) =>
            _renderer.DrawRect(x, y, w, h, new RenderColor(color.X, color.Y, color.Z, color.W), filled, depth: -9790);

        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) =>
            _renderer.DrawLine(x1, y1, x2, y2, new RenderColor(color.X, color.Y, color.Z, color.W), thickness, depth: -9790);
    }
}
