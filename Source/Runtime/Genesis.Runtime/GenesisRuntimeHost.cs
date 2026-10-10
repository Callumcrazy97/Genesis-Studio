using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Rendering.Core;
using Genesis.Rendering.Diagnostics;
using Genesis.Runtime.Culling;
using Genesis.Runtime.Debugger;
using Genesis.Runtime.Demos;
using Genesis.Runtime.Input;
using Genesis.Runtime.Pipeline;
using Genesis.Runtime.Platform;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Commands;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;
using Genesis.Runtime.Startup;

namespace Genesis.Runtime
{
    /// <summary>
    /// Backend-agnostic game/runtime host. Owns the <see cref="IGameWindow"/>, the
    /// <see cref="IRenderController"/> and a <see cref="RuntimeScene"/>, and wires them
    /// together through the window's lifecycle events.
    ///
    /// Silk/GLFW note: DXGI ResizeBuffers is unreliable on GLFW-owned HWNDs (INVALID_CALL).
    /// The swap chain stays at its creation size; DXGI Scaling.Stretch fills the window.
    /// Camera aspect tracks the live window size so 3D projection stays correct.
    /// </summary>
    public sealed class GenesisRuntimeHost : IDisposable
    {
        private readonly SilkGameWindow _window;
        private readonly Action<RuntimeScene, IRenderController> _build;
        private readonly RendererOptions _options = new RendererOptions();
        private readonly MeshDrawCall[] _meshBuffer = new MeshDrawCall[131072];
        private readonly FrameRenderQueue _frameQueue = new();

        private RuntimeScene _scene;
        private IRenderController _renderer;
        private EngineRuntimeCommandBinding _commandBinding;
        private Action<IOverlayCanvas> _overlayDraw;
        private readonly BufferedHudCanvas _pgslHud = new();
        private bool _ready;
        private long _frame;
        public string ScreenshotProjectPath { get; set; }
        private bool _screenshotPending;

        /// <summary>The in-game debugger overlay (F6). Constructed in debug mode.</summary>
        public DebugOverlay Debugger { get; private set; }
        /// <summary>When true, the debugger overlay is enabled (set via the --debug launch flag).</summary>
        public bool IsDebugMode { get; set; }

        // Live window client size (for aspect ratio). Swap-chain pixels stay at init size.
        private int _windowW;
        private int _windowH;

        public IGameWindow Window => _window;
        public RuntimeScene Scene => _scene;
        public IRenderController Renderer => _renderer;

        private ScriptHostSystem _scriptHost;

        /// <summary>When set, script behaviours receive Update / HUD / per-frame render dispatch.</summary>
        public ScriptHostSystem ScriptHost
        {
            get => _scriptHost;
            set
            {
                _scriptHost = value;
                Debugger?.BindScriptHost(value);
            }
        }

        /// <summary>Universal project boot splash (logo, asset scan, shader warm). Null when disabled.</summary>
        public ProjectBootSplash BootSplash { get; set; }

        public IRuntimeStartupGate StartupGate { get; set; }
        private bool _startupReadySent, _startupWindowShown, _startupFrameWasSplash;
        public Exception StartupFailure { get; private set; }

        private void FailStartup(Exception error)
        {
            StartupFailure ??= error;
            _ready = false;
            RenderLog.Line("Runtime failure: " + error);
            try { StartupGate?.SignalFailure(error); }
            finally { _window.Close(); }
        }

        /// <summary>Invoked after <c>EndFrame</c> and before the overlay / present (screenshot hook).</summary>
        public event Action<IRenderController> EndFrame;

        /// <summary>Invoked when a frame starts gathering what to draw, before scripts' render hooks.</summary>
        public event Action BeforeRenderSubmit;

        /// <summary>Invoked before each fixed step of the simulation; a frame may have none or several.</summary>
        public event Action FixedStepStarting;

        /// <summary>Invoked before the once-per-frame update, after the frame's fixed steps.</summary>
        public event Action VariableUpdateStarting;

        /// <summary>
        /// Invoked after <see cref="IRenderController.Present"/>. Autoshot closes the window here
        /// so Present cannot run against a renderer already disposed by <c>Closing</c>.
        /// </summary>
        public event Action AfterPresent;
        /// <summary>Fired once before scene and graphics resources are destroyed. Dispose game/mod services here.</summary>
        public event Action ShuttingDown;
        private bool _closing;

        /// <summary>Register a backend-neutral overlay draw callback (menus, HUD text).</summary>
        public void SetOverlayDraw(Action<IOverlayCanvas> draw) => _overlayDraw = draw;

        /// <summary>Invoked after <see cref="RuntimeScene"/> is built in <c>OnLoad</c>.</summary>
        public event Action<GenesisRuntimeHost> SceneBuilt;

        public GenesisRuntimeHost(string title, int width, int height,
                                  Action<RuntimeScene, IRenderController> build)
        {
            _build  = build ?? throw new ArgumentNullException(nameof(build));
            _window = new SilkGameWindow(title, width, height);

            _window.Load    += OnLoad;
            _window.Update  += OnUpdate;
            _window.Render  += OnRender;
            _window.Resize  += OnWindowResize;
            _window.Closing += OnClosing;
            _window.FatalError += error =>
            {
                FailStartup(error);
            };
        }

        public void Run() => _window.Run();

        /// <summary>Launches the spinning-cubes demo in a single cross-platform window.</summary>
        public static void RunSpinningCubes()
        {
            using var host = new GenesisRuntimeHost(
                "Genesis Engine — Runtime", 1280, 720, SpinningCubesScene.Build);
            host.Run();
        }

        // ── Window lifecycle ──────────────────────────────────────────────────────

        private void OnLoad()
        {
            _windowW = _window.Width;
            _windowH = _window.Height;

            StartupGate?.Report("Game runtime", "Native game window created", 1, 4);
            Genesis.Shared.Diagnostics.LoadProfile.Mark("window open");
            using (Genesis.Shared.Diagnostics.LoadProfile.Begin("create the graphics device and its built-in pipelines"))
            {
                _renderer = RenderControllerFactory.Create();
                _renderer.Initialize(_window.NativeHandle, _windowW, _windowH);
            }
            Genesis.Shared.Diagnostics.LoadProfile.Mark("graphics device ready");
            StartupGate?.Report("Game runtime", "Game-owned graphics device and pipelines initialized", 2, 4);
            _renderer.SetVSync(_window.VSync);

            RenderLog.Init();
            RenderLog.Line($"OnLoad handle=0x{_window.NativeHandle.ToInt64():X} size={_windowW}x{_windowH}");

            _scene = new RuntimeScene("Runtime");
            _commandBinding = new EngineRuntimeCommandBinding(_scene, _window, _options, _renderer);
            SceneDefaults.PrepareDemo(_options);
            using (Genesis.Shared.Diagnostics.LoadProfile.Begin("set up the game and build its first room"))
                _build(_scene, _renderer);

            _scene.Input = _window.Input;

            // Construct the in-game debugger (always present; only active in debug mode).
            // Studio keeps diagnostics available in ordinary F5 play, not only F6/debug launches.
            Debugger ??= new DebugOverlay();
            if (IsDebugMode) Debugger.IsVisible = true;
            Debugger.BindScriptHost(ScriptHost);
            Debugger.BindScene(_scene, DebugRoomName);
            Debugger.BindWindow(_window);
            Debugger.ProfilesDirectory = DebugProfilesDirectory;
            Debugger.ProjectName = DebugProjectName ?? string.Empty;
            Debugger.EngineCategoryEnabled = DebugEngineCategory;

            _options.FrustumCulling = RenderAutoState.FrustumCulling;

            SceneBuilt?.Invoke(this);
            // After SceneBuilt, so whoever listens for recordings hears this one start.
            if (IsDebugMode && DebugRecordOnStart) Debugger.StartRecording();
            StartupGate?.Report("Game runtime", "Scene and runtime services constructed", 3, 4);
            _ready = true;
        }

        private void OnWindowResize(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            _windowW = width;
            _windowH = height;
            RenderLog.Line($"OnWindowResize {width}x{height}");
            try
            {
                _renderer?.TryResize(width, height);
            }
            catch (Exception ex)
            {
                RenderLog.Line($"OnWindowResize error: {ex.Message}");
            }
        }

        private long _nextSurfaceRetry;

        /// <summary>
        /// Rebuilds a swap chain that could not be rebuilt when the window last changed size, twice
        /// a second while the window has a size. Vulkan reports no surface for a moment around a
        /// minimise and restore, and a failed DXGI resize keeps the old buffers "to retry": with
        /// no further resize event the game would otherwise stay undrawn for good.
        /// </summary>
        private void RetrySurface()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now < _nextSurfaceRetry || _window.Width <= 0 || _window.Height <= 0) return;
            _nextSurfaceRetry = now + System.Diagnostics.Stopwatch.Frequency / 2;
            try
            {
                if (_renderer.TryResize(_window.Width, _window.Height))
                    RenderLog.Line($"Surface rebuilt at {_window.Width}x{_window.Height} after it was not ready");
            }
            catch (Exception ex)
            {
                RenderLog.Line("Surface rebuild error: " + ex.Message);
            }
        }

        private void SyncLiveWindowSize()
        {
            int w = _window.Width;
            int h = _window.Height;
            if (w <= 0 || h <= 0) return;
            if (w == _windowW && h == _windowH) return;
            _windowW = w;
            _windowH = h;
        }

        private int OverlayWidth => _renderer?.PixelWidth > 0 ? _renderer.PixelWidth : _windowW;
        private int OverlayHeight => _renderer?.PixelHeight > 0 ? _renderer.PixelHeight : _windowH;

        private void OnUpdate(double dt)
        {
            if (!_ready) return;
            using Genesis.Shared.Diagnostics.LoadProfile.Span profiled = Genesis.Shared.Diagnostics.LoadProfile.Begin("frames: update");
            UpdateFrame(dt);
        }

        private void UpdateFrame(double dt)
        {
            float fdt = (float)dt * (Debugger != null && Debugger.TimeScale > 0f ? Debugger.TimeScale : 1.0f);

            SyncLiveWindowSize();

            if (_windowW > 0 && _windowH > 0)
            {
                _scene.Camera3D.AspectRatio = (float)_windowW / _windowH;
                _scene.RenderViewportHeightPixels = _windowH;
            }

            // Debugger: handle input and advance frame telemetry
            if (Debugger != null) Debugger.RoomName = DebugRoomName;
            Debugger?.HandleInput(_scene?.Input, true);
            Debugger?.Advance((float)dt);

            if (BootSplash != null && !BootSplash.IsComplete)
            {
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("start-up screen: preparing the first room's assets"))
                    BootSplash.Update(fdt, _renderer);
            }
            if (BootSplash != null && StartupGate != null && !_startupReadySent)
                StartupGate.Report("Game resource preparation", BootSplash.Status, BootSplash.JobsDone, BootSplash.JobsTotal);
            if (StartupGate?.Failure != null) { FailStartup(StartupGate.Failure); return; }
            // Neither physics, simulation, scripts nor game time advance behind a loading screen.
            if (BootSplash != null && !BootSplash.IsComplete) return;
            if (StartupGate != null)
            {
                if (!StartupGate.IsActivated) return;
                if (!_startupWindowShown)
                {
                    _window.SetStartupVisible(true);
                    _startupWindowShown = true;
                    StartupGate.SignalStarted();
                    return; // Do not feed the loading wall-clock interval into the first gameplay tick.
                }
            }
            _scene.AllowStreamingUpdates = true;
            // A shake runs on real time, so it carries on through slow motion and a hit-stop.
            _scene.Camera3D.AdvanceShake((float)dt);

            // Feed the last resolved (non-stalling) GPU timestamp into the budget arbiter before
            // the variable ECS phase. The value naturally lags by a few frames on real backends.
            _scene.ObserveRenderFrame(_renderer?.LastGpuMilliseconds ?? 0.0);

            long simulationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            // When the debugger is paused, hold the scene fixed unless a single step was requested.
            bool paused = IsPlayPaused || (Debugger != null && Debugger.IsPaused && !Debugger.ConsumeStepRequest());
            if (!paused)
            {
                // A recording takes this frame's input and time step; a replay replaces them with
                // the recorded ones (and asks the window for the recorded size) before anything reads
                // them. Frames behind a room change's cover step nothing and take as long as the
                // files do, so they are neither recorded nor replayed.
                if (InputReplay.IsActive && _scene.RoomChange == null)
                {
                    int replayWidth = _windowW, replayHeight = _windowH;
                    InputReplay.BeginFrame(_scene.Input, ref fdt, ref replayWidth, ref replayHeight, out bool replayStarted);
                    if (replayStarted) _scene.FixedTimestep.Reset();
                    if ((replayWidth != _windowW || replayHeight != _windowH) && replayWidth > 0 && replayHeight > 0)
                        _window.SetSize(replayWidth, replayHeight);
                }

                // The 2D spatial grid is not queried by collisions or scripts, so it is no longer
                // cleared and rebuilt (with string matching per entity) every update. Callers that
                // need it can call RuntimeScene.RebuildSpatialGrid on demand.
                // Game time may run slower or faster than real time (slow motion, a hit-stop).
                float gameDelta = fdt * Genesis.Runtime.Core.GameSpeed.Scale;
                // Flashes and decals age before the game updates: one made in this frame's Step is
                // drawn as new in this frame, however short its life.
                Genesis.Runtime.Rendering.FlashLights.Advance(gameDelta);
                Genesis.Runtime.Rendering.WorldDecals.Advance(gameDelta);
                int steps = _scene.FixedTimestep.Advance(gameDelta);
                for (int i = 0; i < steps; i++)
                {
                    FixedStepStarting?.Invoke();
                    using (Genesis.Shared.Diagnostics.LoadProfile.Begin("fixed steps"))
                        _scene.UpdateFixed(_scene.FixedTimestep.FixedDelta);
                }

                VariableUpdateStarting?.Invoke();
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("scene update"))
                    _scene.UpdateVariable(gameDelta);
            }

            // Per-frame host hook (audio voice recycling, network pump, etc.).
            FrameUpdate?.Invoke(fdt);
            LastSimulationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(simulationStarted).TotalMilliseconds;

            // A stop requested from FrameUpdate may close/dispose the scene synchronously.
            if (_scene?.Input?.WasPressed(Key.F12) == true && !string.IsNullOrWhiteSpace(ScreenshotProjectPath))
                _screenshotPending = true;
        }

        /// <summary>Current room name shown in the F6 debug overlay. Set by the player.</summary>
        public string DebugRoomName { get; set; }

        /// <summary>Where the debug screen writes recordings (a folder per session). Null: recording is unavailable.</summary>
        public string DebugProfilesDirectory { get; set; }

        /// <summary>The project named in a recording's report.</summary>
        public string DebugProjectName { get; set; }

        /// <summary>True to start recording as soon as a debug run starts (Studio's preference, on by default).</summary>
        public bool DebugRecordOnStart { get; set; }

        /// <summary>True in developer builds: the debug screen's Engine tab and Engine recording columns.</summary>
        public bool DebugEngineCategory { get; set; } = Genesis.Runtime.Diagnostics.DebugCategories.EngineRequested;
        public bool IsPlayPaused { get; set; }

        /// <summary>
        /// Raised once per variable update frame with the delta time. Host-owned
        /// services that aren't ECS subsystems (audio voice recycling, the in-game
        /// debugger overlay) subscribe here.
        /// </summary>
        public event Action<float> FrameUpdate;
        /// <summary>Elapsed CPU-side simulation/host work; excludes rendering and presentation.</summary>
        public double LastSimulationMilliseconds { get; private set; }

        /// <summary>Time the last frame spent gathering what to draw: culling, level of detail, streaming uploads.</summary>
        public double LastCollectMilliseconds { get; private set; }

        /// <summary>Time the last frame spent turning the gathered draws into GPU commands.</summary>
        public double LastDrawMilliseconds { get; private set; }

        /// <summary>Time the last frame spent presenting, which includes waiting for the GPU to catch up.</summary>
        public double LastPresentMilliseconds { get; private set; }

        /// <summary>
        /// Time the last frame spent after its drawing was issued and before it was presented:
        /// the HUD and overlay the game draws, and whatever is hooked to the end of a frame (a
        /// screenshot, an acceptance run, a benchmark).
        /// </summary>
        public double LastOverlayMilliseconds { get; private set; }

        /// <summary>
        /// Raised for each frame of a finished room that is drawn behind a room change's cover,
        /// after the cover and the loading screen are on it. The ordinary end-of-frame hooks do
        /// not see these frames; this is for something that wants the loading screen itself.
        /// </summary>
        public event Action<IRenderController, RoomChangeProgress> RoomChangeCovered;

        private void OnRender(double dt)
        {
            if (!_ready || _renderer == null) return;
            using Genesis.Shared.Diagnostics.LoadProfile.Span profiled = Genesis.Shared.Diagnostics.LoadProfile.Begin("frames: render");
            RenderFrame(dt);
        }

        private void RenderFrame(double dt)
        {

            SyncLiveWindowSize();

            // Minimized / zero client area — skip DXGI work.
            if (_window.Width <= 0 || _window.Height <= 0)
                return;

            if (!_renderer.IsFramebufferReady)
            {
                RetrySurface();
                return;
            }

            if ((_frame++ % 120) == 0)
                RenderLog.Line($"render frame={_frame} buffer={_renderer.PixelWidth}x{_renderer.PixelHeight} window={_windowW}x{_windowH} fps={_window.CurrentFps:F0}");

            long beginStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            using (Genesis.Shared.Diagnostics.LoadProfile.Begin("beginning the frame"))
                _renderer.BeginFrame();
            // Beginning a frame waits for the graphics card to give back the frame's buffers;
            // named, so a slow-frame line can say so instead of calling it time outside the frame.
            _scene?.WorkTimes.Add("waiting for the graphics card to begin", beginStarted);

            bool booting = BootSplash != null && !BootSplash.IsComplete;
            _startupFrameWasSplash = booting;
            RoomRenderSubsystem roomPresentation = null;
            if (!booting)
            {
                for (int i = 0; i < _scene.Subsystems.Count; i++)
                    if (_scene.Subsystems[i] is RoomRenderSubsystem rr) { roomPresentation = rr; break; }
            }

            bool twoDRoom = roomPresentation?.IsTwoD == true;
            // A 3D room's sprites and GUI sprites follow its pixel-art setting too (2D rooms set it
            // again as they draw). Without this a 3D room's HUD was always smoothed.
            if (roomPresentation != null) _renderer.SetSamplerState(roomPresentation.SpriteFilter);
            // The project's post effects (Fullscreen Shader resources) the room and its scripts ask for.
            if (!booting)
            {
                long effectsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("project post effects"))
                    _renderer.SetPostEffects(Genesis.Runtime.Rendering.ProjectPostEffects.RequestsFor(
                        Genesis.Runtime.Scripting.PgslCommands.ProjectPath, _renderer));
                _scene.WorkTimes.Add("project post effects", effectsStarted);
            }
            RoomFogState spriteFog = twoDRoom
                ? RoomFogState.Create(
                    _scene.Environment.FogEnabled,
                    _scene.Environment.FogColor,
                    _scene.Environment.FogStart,
                    _scene.Environment.FogEnd)
                : RoomFogState.Disabled;
            _renderer.SetRoomFog(spriteFog);

            Vector4 bg = booting
                ? new Vector4(0.07f, 0.09f, 0.14f, 1f)
                : _scene.Environment.BackgroundColor;
            if (twoDRoom)
                bg = spriteFog.ApplyToBackground(bg);
            _renderer.Clear(bg.X, bg.Y, bg.Z, bg.W);

            _renderer.SetViewport(0, 0, _renderer.PixelWidth, _renderer.PixelHeight);

            if (booting)
            {
                _renderer.Set3DFrameActive(true);
                BootSplash.DrawLogo(_renderer);
                _renderer.Advance3DTime((float)dt);
                _renderer.EndFrame();
                // Do NOT fire EndFrame during the boot splash: the screenshot hook
                // (OnAutoshotEndFrame) calls TryReadTexture → DeviceWaitIdle while the
                // swapchain image is mid-flight (acquired, not yet presented), which
                // deadlocks the GPU timeline.  The flag stays set and is honoured on
                // the first real gameplay frame once the splash completes.
                ComposeBootOverlay();
                FinishRender();
                return;
            }

            _lastRenderDelta = (float)dt;
            RoomChangeProgress roomChange = _scene.RoomChange;
            if (roomChange != null && !roomChange.RoomBuilt)
            {
                // The room is half made: none of it is drawn. The cover and the loading screen are.
                _renderer.Set3DFrameActive(false);
                _renderer.Advance3DTime((float)dt);
                _renderer.EndFrame();
                try { ComposeRoomChangeOverlay(roomChange, 1f); }
                catch (Exception ex) { RenderLog.Line("ComposeRoomChangeOverlay error: " + ex.Message); }
                FinishRender();
                return;
            }

            if (roomPresentation?.IsTwoD == true)
            {
                // A 2D room has no 3D pass. The boot splash sets this true and nothing ever set it
                // back, so every 2D frame was running the whole forward pipeline — shadow cascades,
                // screen-space fog and the fullscreen composite — over a scene with no meshes in it,
                // which both cost the frame and composited over the room.
                _renderer.Set3DFrameActive(false);

                var pipeline2d = ComponentPipelineGateway.Current;
                pipeline2d.BeginFrame((int)_frame);
                pipeline2d.Enter(ComponentPipelinePhase.Visibility);
                pipeline2d.Enter(ComponentPipelinePhase.Submit);
                RenderAutoState.AllowDrawSubmit = true;
                Engine.SetDrawCommandSink(_frameQueue);

                _frameQueue.Reset();
                Genesis.Runtime.Rendering.ScriptMeshes.BeginFrame();
                roomPresentation.Render2D(_scene, _renderer, _frameQueue);
                ScriptHost?.DispatchRenderFrame(_renderer, _frameQueue);
                ScriptHost?.DispatchPgslWorldDraw(_renderer, _frameQueue);
                _frameQueue.Flush(_renderer, includeMeshes: false, includeSprites: true);
                Genesis.Runtime.Rendering.ModelLayers.Submit(_renderer, Genesis.Runtime.Scripting.PgslCommands.ProjectPath, null, OverlayWidth, OverlayHeight);

                RenderAutoState.AllowDrawSubmit = false;
                Engine.SetDrawCommandSink(null);
                pipeline2d.Enter(ComponentPipelinePhase.Present);

                _renderer.Advance3DTime((float)dt);
                _renderer.EndFrame();
                InvokePostRenderHooks();
                FinishRender();
                return;
            }

            // Canonical 3D frame: Visibility → Submit (queue) → GPU Shadow→Main→Post via Flush.
            void RenderCamera()
            {
                var pipeline = ComponentPipelineGateway.Current;
                pipeline.BeginFrame((int)_frame);
                RenderAutoState.ResetFrameCounters();
                RenderAutoState.AllowDrawSubmit = false;
                _options.FrustumCulling = RenderAutoState.FrustumCulling;

                Matrix4x4 viewProjection = _scene.Camera3D.ViewMatrix * _scene.Camera3D.ProjectionMatrix;
                pipeline.Enter(ComponentPipelinePhase.Visibility);
                Visibility.Occluders.Clear();
                if (RenderAutoState.OcclusionCulling)
                {
                    OccluderBoundsRegistry.ForEach((min, max) =>
                        Visibility.Occluders.RasterizeOccluderAabb(viewProjection, min, max));
                }

                bool wireframe = _options.Wireframe
                    || (Debugger != null && Debugger.IsVisible && Debugger.ShowWireframe);
                RenderDebugView debugView = ResolveDebugView();

                _renderer.SetCamera3D(_scene.Camera3D.ViewMatrix, _scene.Camera3D.ProjectionMatrix);
                Mesh3DState meshState = EnvironmentMapper.ToMesh3DState(
                    _scene.Environment,
                    _scene.Camera3D,
                    _options,
                    wireframe,
                    debugView);
                EnvironmentMapper.StampClimateAtmosphere(ref meshState, _scene.Climate, _scene.Atmosphere);
                // A script's own ambient colour outranks the sky's; the sky used to overwrite it.
                if (_scene.Environment.AmbientOverrideColor is Vector3 ambientOverride)
                    meshState.AmbientColor = ambientOverride;
                EnvironmentMapper.ApplyLightningFlash(ref meshState, _scene.Environment.LightningFlash);
                // Offscreen ports share the renderer, so history from another camera must never be reused.
                if (roomPresentation?.HasThreeDViewports == true) meshState.CloudTemporalEnabled = false;
                _renderer.SetMesh3DState(meshState);

                pipeline.Enter(ComponentPipelinePhase.Submit);
                RenderAutoState.AllowDrawSubmit = true;
                Engine.SetDrawCommandSink(_frameQueue);
                _frameQueue.Reset();
                _commandBinding?.ApplyFrameRenderState();
                // The scripts' Draw events are part of gathering what to draw. Left out of it, a
                // slow-frame line put their time "outside the frame's own work".
                long collectStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("scripts' render-frame events and lights"))
                {
                    BeforeRenderSubmit?.Invoke();
                    Genesis.Runtime.Rendering.FlashLights.Submit(_renderer);
                    Genesis.Runtime.Rendering.WorldDecals.Submit(_renderer, Genesis.Runtime.Scripting.PgslCommands.ProjectPath);
                    Genesis.Runtime.Rendering.ScriptMeshes.BeginFrame();
                    ScriptHost?.DispatchRenderFrame(_renderer, _frameQueue);
                    ScriptHost?.DispatchPgslWorldDraw(_renderer, _frameQueue);
                }
                _scene.WorkTimes.Add("scripts' Draw events", collectStarted);
                int drawCount = 0;
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("gathering what to draw"))
                    _scene.CollectMeshes(_meshBuffer, ref drawCount, _renderer);
                LastCollectMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(collectStarted).TotalMilliseconds;
                Genesis.Shared.Diagnostics.LoadProfile.Span submitProfile = Genesis.Shared.Diagnostics.LoadProfile.Begin("submitting instances, layers and draws");
                int instanceCount = _scene.SubmitInstanceBatches(_renderer);
                Genesis.Runtime.Rendering.ModelLayers.Submit(_renderer, Genesis.Runtime.Scripting.PgslCommands.ProjectPath,
                    _scene.Camera3D, OverlayWidth, OverlayHeight);
                _renderer.Set3DFrameActive(drawCount > 0 || instanceCount > 0 || _frameQueue.MeshCount > 0 || meshState.AuthoredSkyEnabled);
                if (drawCount > 0)
                    _renderer.DrawMeshBatch(_meshBuffer.AsSpan(0, drawCount));
                _frameQueue.Flush(_renderer, includeMeshes: true, includeSprites: true);
                submitProfile.Dispose();
                RenderAutoState.AllowDrawSubmit = false;
                Engine.SetDrawCommandSink(null);
                pipeline.Enter(ComponentPipelinePhase.Present);
            }

            _renderer.Advance3DTime((float)dt);
            LastCollectMilliseconds = 0;
            if (roomPresentation?.RenderViewports3D(_scene, _renderer, RenderCamera) != true) RenderCamera();

            long drawStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("drawing"))
                    _renderer.EndFrame();
                LastDrawMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;
            }
            catch (Exception ex)
            {
                RenderLog.Line("EndFrame error: " + ex.Message);
                FailStartup(ex); return;
            }

            InvokePostRenderHooks();

            if (_screenshotPending)
            {
                _screenshotPending = false;
                try { RuntimeScreenshot.CaptureFrame(_renderer, ScreenshotProjectPath); }
                catch (Exception ex) { RenderLog.Line("RuntimeScreenshot error: " + ex.Message); }
            }

            FinishRender();
        }

        /// <summary>
        /// F6 pass dropdown drives the live debug view when the overlay is visible;
        /// otherwise Engine.DebugView / command binding wins.
        /// </summary>
        private RenderDebugView ResolveDebugView()
        {
            if (Debugger != null && Debugger.IsVisible)
            {
                return Debugger.ActiveRenderPass switch
                {
                    DebugRenderPass.Normals => RenderDebugView.Normals,
                    DebugRenderPass.Depth => RenderDebugView.SceneDepth,
                    _ => _commandBinding?.DebugView ?? RenderDebugView.Shaded,
                };
            }

            return _commandBinding?.DebugView ?? RenderDebugView.Shaded;
        }

        private void FinishRender()
        {
            PresentFrame();
        }

        private void PresentFrame()
        {
            try
            {
                long presentStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("presenting"))
                    _renderer?.Present();
                LastPresentMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(presentStarted).TotalMilliseconds;
                // The debug screen's figures and recording see each presented game frame (not the
                // start-up splash). With the screen closed and nothing recording this is one timestamp.
                if (!_startupFrameWasSplash)
                    Debugger?.ObserveFrame(LastSimulationMilliseconds, LastCollectMilliseconds, LastDrawMilliseconds,
                        LastPresentMilliseconds, LastOverlayMilliseconds, _renderer);
                BootSplash?.NotifyPresented();
                if (StartupGate != null && !_startupReadySent && !_startupFrameWasSplash && (BootSplash == null || BootSplash.IsComplete))
                {
                    if (BootSplash != null)
                        StartupGate.Report("Game resource preparation", "All declared runtime preparation jobs complete", BootSplash.JobsTotal, BootSplash.JobsTotal);
                    StartupGate.Report("Game runtime", "First prepared frame presented successfully", 4, 4);
                    _startupReadySent = true;
                    StartupGate.SignalReady();
                }
            }
            catch (Exception ex)
            {
                RenderLog.Line("Present error: " + ex);
                FailStartup(ex);
                return;
            }
            try { AfterPresent?.Invoke(); }
            catch (Exception ex) { RenderLog.Line("AfterPresent error: " + ex.Message); }
        }

        /// <summary>
        /// Runs gameplay overlay composition and screenshot hooks after the GPU frame is submitted.
        /// Kept separate from <see cref="EndFrame"/> so a HUD/script failure cannot skip autoshot.
        /// </summary>
        private void InvokePostRenderHooks()
        {
            using Genesis.Shared.Diagnostics.LoadProfile.Span profiled = Genesis.Shared.Diagnostics.LoadProfile.Begin("HUD and hooks");
            long hooksStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            // Asked before the cover is faded: the frame in which the last of it is drawn is
            // still a covered frame, though nothing is left of the change once it has been.
            bool covered = _scene?.RoomChange != null || _scene?.RoomReveal != null;
            try
            {
                RoomChangeProgress roomChange = _scene?.RoomChange;
                if (roomChange != null)
                {
                    // The finished room was drawn so that what it shows is loaded; nobody sees it yet.
                    ComposeRoomChangeOverlay(roomChange, 1f);
                    RoomChangeCovered?.Invoke(_renderer, roomChange);
                }
                else
                {
                    if (ScriptHost != null || _overlayDraw != null || Debugger != null)
                        ComposeGameplayOverlay();
                    FadeRoomChangeCover();
                }
            }
            catch (Exception ex)
            {
                RenderLog.Line("ComposeGameplayOverlay error: " + ex.Message);
                if (StartupGate != null && !StartupGate.IsActivated) throw;
            }

            // A screenshot, an acceptance run and a benchmark are of the game, not of the cover a
            // room change draws over it: they wait for the first frame with no cover in it.
            if (!covered)
            {
                // Pictures a script asked for this frame (ScreenshotSave), of the finished frame.
                if (Genesis.Runtime.Project.ScriptScreenshots.PendingCount > 0)
                    Genesis.Runtime.Project.ScriptScreenshots.CaptureFrame(_renderer, Genesis.Runtime.Scripting.PgslCommands.ProjectPath);
                EndFrame?.Invoke(_renderer);
            }
            LastOverlayMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(hooksStarted).TotalMilliseconds;
        }

        private float _lastRenderDelta;

        private void ComposeRoomChangeOverlay(RoomChangeProgress change, float strength) =>
            RoomChangeScreen.Compose(_renderer, OverlayWidth, OverlayHeight, change, strength, ScriptHost);

        /// <summary>Fades the cover of a room change that has just finished, in real time.</summary>
        private void FadeRoomChangeCover()
        {
            RoomChangeProgress reveal = _scene?.RoomReveal;
            if (reveal == null) return;
            float strength = reveal.RevealSeconds > 0f ? reveal.RevealRemaining / reveal.RevealSeconds : 0f;
            if (strength > 0f) ComposeRoomChangeOverlay(reveal, MathF.Min(strength, 0.999f));
            reveal.RevealRemaining -= MathF.Max(0f, _lastRenderDelta);
            if (reveal.RevealRemaining <= 0f) _scene.RoomReveal = null;
        }

        private void ComposeGameplayOverlay()
        {
            if (ScriptHost == null && _overlayDraw == null && Debugger == null) return;
            if (_renderer == null) return;

            // Asset problems are found in the draw path, which has no route to the overlay of its
            // own (NEXT-092). Draining here puts them on the same red banner as script failures.
            Debugger?.DrainAssetDiagnostics();

            bool scriptsReady = ScriptHost != null && (BootSplash == null || BootSplash.IsComplete)
                && (StartupGate == null || StartupGate.IsActivated);
            _pgslHud.Reset(OverlayWidth, OverlayHeight);
            // Model layers 1-8: over the world, under the GUI, in number order.
            for (int layer = 1; layer <= Genesis.Runtime.Rendering.ModelLayers.MaxOverlay; layer++)
                if (Genesis.Runtime.Rendering.ModelLayers.IsVisible(layer)
                    && _renderer.TryGetModelLayerTexture(layer, out TextureHandle layerImage))
                    _renderer.DrawSprite(new SpriteDrawCall
                    {
                        Texture = layerImage, Width = OverlayWidth, Height = OverlayHeight,
                        ScaleX = 1f, ScaleY = 1f, Alpha = 1f, Tint = RenderColor.White, SmoothSampling = true,
                    });
            if (scriptsReady)
            {
                // A PGSL GUI's shapes, text and images are buffered together and replayed onto the
                // overlay in the order the script drew them, so a panel drawn after a label or an
                // icon covers it. (A sprite the GUI canvas cannot carry is flushed here, beneath.)
                ScriptHost.DispatchPgslGuiDraw(_renderer, _pgslHud);
                _renderer.FlushOverlaySprites();
            }

            _renderer.ComposeOverlay(canvas =>
            {
                if (scriptsReady)
                {
                    var hud = new OverlayHudCanvas(canvas, OverlayWidth, OverlayHeight);
                    ScriptHost.DispatchDrawHud(hud);
                    _pgslHud.Replay(hud);
                }
                // In-game debugger draws on the same HUD canvas (F6 overlay).
                if (Debugger != null && (Debugger.IsVisible || Debugger.ShowNavigation || Debugger.HasScriptErrors || IsDebugMode))
                    Debugger.Draw(new OverlayHudCanvas(canvas, OverlayWidth, OverlayHeight), _renderer, OverlayWidth, OverlayHeight);
                _overlayDraw?.Invoke(canvas);
            });

            // Native HUD sprites intentionally sit above overlay text (icons/cursors). Submit
            // them before the overlay flush so they appear in this frame, not the next one.
            if (scriptsReady)
                ScriptHost?.DispatchDrawHudOverlay(_renderer);
            _renderer.FlushOverlaySprites();
            // Text drawn in the overlay event labels that event's sprites, so it goes on top of
            // them in this frame. It used to wait in the queue for the next frame's overlay, which
            // is drawn beneath these sprites: an icon in the overlay could never carry a caption.
            // With nothing queued this does no work.
            _renderer.ComposeOverlay(static _ => { });
        }

        /// <summary>
        /// Records HUD calls whose Direct2D target is not available until after the PGSL sprite
        /// layer has been flushed. Commands retain their authored order when replayed.
        /// </summary>
        private sealed class BufferedHudCanvas : IHudCanvas
        {
            private enum Kind { Text, TextCentered, Rect, Line, Clip, Sprite, BlendLinear, Rects }

            private readonly List<Command> _commands = new();
            private readonly List<SpriteDrawCall> _sprites = new();
            // Rectangle batches (DrawRectanglesFromList): one command each, replayed as one.
            private readonly List<GuiRectangle> _rects = new();

            public void Rects(ReadOnlySpan<GuiRectangle> rectangles)
            {
                if (rectangles.IsEmpty) return;
                _commands.Add(new Command(Kind.Rects, null, _rects.Count, rectangles.Length, 0f, 0f, 0f, default));
                _rects.AddRange(rectangles);
            }

            // Replayed onto the overlay, which draws sprites in order with text and shapes.
            public bool SupportsSprites => true;

            public void Sprite(in SpriteDrawCall call)
            {
                _commands.Add(new Command(Kind.Sprite, null, _sprites.Count, 0f, 0f, 0f, 0f, default));
                _sprites.Add(call);
            }

            private readonly struct Command
            {
                public readonly Kind Type;
                public readonly string Value;
                public readonly string Font;
                public readonly float A, B, C, D, E;
                public readonly Vector4 Color;
                public readonly bool Filled;

                public Command(Kind type, string value, float a, float b, float c, float d, float e,
                    Vector4 color, bool filled = true, string font = null)
                {
                    Type = type;
                    Value = value;
                    Font = font;
                    A = a; B = b; C = c; D = d; E = e;
                    Color = color;
                    Filled = filled;
                }
            }

            public int Width { get; private set; }
            public int Height { get; private set; }

            public void Reset(int width, int height)
            {
                Width = width;
                Height = height;
                _commands.Clear();
                _sprites.Clear();
                _rects.Clear();
            }

            public void Text(string text, float x, float y, float size, Vector4 color) =>
                _commands.Add(new Command(Kind.Text, text, x, y, size, 0f, 0f, color));

            public void Text(string text, float x, float y, float size, Vector4 color, string font) =>
                _commands.Add(new Command(Kind.Text, text, x, y, size, 0f, 0f, color, font: font));

            public void Text(string text, float x, float y, float size, Vector4 color, string font, float tracking) =>
                _commands.Add(new Command(Kind.Text, text, x, y, size, 0f, tracking, color, font: font));

            public void SetClip(float x, float y, float width, float height) =>
                _commands.Add(new Command(Kind.Clip, null, x, y, width, height, 0f, default));

            public void SetBlendLinear(bool linear) =>
                _commands.Add(new Command(Kind.BlendLinear, null, linear ? 1f : 0f, 0f, 0f, 0f, 0f, default));

            public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color) =>
                _commands.Add(new Command(Kind.TextCentered, text, centerX, y, width, size, 0f, color));
            public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color, string font) =>
                _commands.Add(new Command(Kind.TextCentered, text, centerX, y, width, size, 0f, color, font: font));

            public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) =>
                _commands.Add(new Command(Kind.Rect, null, x, y, w, h, 0f, color, filled));

            public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) =>
                _commands.Add(new Command(Kind.Line, null, x1, y1, x2, y2, thickness, color));

            public void Replay(IHudCanvas destination)
            {
                if (destination == null) return;
                bool linear = false;
                foreach (Command command in _commands)
                {
                    switch (command.Type)
                    {
                        case Kind.Text:
                            if (command.E != 0f)
                                destination.Text(command.Value, command.A, command.B, command.C, command.Color, command.Font, command.E);
                            else
                                destination.Text(command.Value, command.A, command.B, command.C, command.Color, command.Font);
                            break;
                        case Kind.Clip:
                            destination.SetClip(command.A, command.B, command.C, command.D);
                            break;
                        case Kind.BlendLinear:
                            linear = command.A != 0f;
                            destination.SetBlendLinear(linear);
                            break;
                        case Kind.Sprite:
                            destination.Sprite(_sprites[(int)command.A]);
                            break;
                        case Kind.TextCentered:
                            destination.TextCentered(command.Value, command.A, command.B, command.C, command.D, command.Color, command.Font ?? "Segoe UI");
                            break;
                        case Kind.Rect:
                            destination.Rect(command.A, command.B, command.C, command.D, command.Color, command.Filled);
                            break;
                        case Kind.Line:
                            destination.Line(command.A, command.B, command.C, command.D, command.Color, command.E);
                            break;
                        case Kind.Rects:
                            destination.Rects(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_rects)
                                .Slice((int)command.A, (int)command.B));
                            break;
                    }
                }

                // A script that forgot DrawResetClip must not clip whatever is drawn after the GUI.
                destination.SetClip(0f, 0f, 0f, 0f);
                // Nor may linear blending reach the debug overlay drawn after it.
                if (linear) destination.SetBlendLinear(false);
            }
        }

        /// <summary>
        /// Presents a loading screen saying what the game is doing while it is still being made,
        /// before its first real frame: the window would otherwise stay blank (or white) for the
        /// whole of that time. Call it between the long steps of starting a game. It draws no 3D
        /// and never fails the start: a frame that cannot be shown is skipped.
        /// </summary>
        /// <returns>True when the screen was presented.</returns>
        public bool ShowStartupProgress(string status, float progress)
        {
            if (_renderer == null || _closing || _window.Width <= 0 || _window.Height <= 0) return false;
            try
            {
                if (!_renderer.IsFramebufferReady) return false;
                _renderer.BeginFrame();
                _renderer.Clear(0.07f, 0.09f, 0.14f, 1f);
                _renderer.SetViewport(0, 0, _renderer.PixelWidth, _renderer.PixelHeight);
                _renderer.Set3DFrameActive(false);
                _renderer.Advance3DTime(0f);
                _renderer.EndFrame();
                int width = OverlayWidth, height = OverlayHeight;
                _renderer.ComposeOverlay(canvas => ProjectBootSplash.DrawStartupOverlay(canvas, width, height, status, progress));
                _renderer.Present();
                return true;
            }
            catch (Exception ex)
            {
                RenderLog.Line("Startup progress frame failed: " + ex.Message);
                return false;
            }
        }

        private void ComposeBootOverlay()
        {
            if (BootSplash == null || BootSplash.IsComplete || _renderer == null) return;
            _renderer.ComposeOverlay(canvas => BootSplash.DrawOverlay(canvas, OverlayWidth, OverlayHeight));
        }

        private void OnClosing()
        {
            if (_closing) return; _closing = true; _ready = false;
            Exception first = null;
            void Clean(Action action) { try { action(); } catch (Exception error) { first ??= error; } }
            // A recording still running when the game closes is saved, not cut off mid-buffer.
            Clean(InputReplay.StopAll);
            if (ShuttingDown != null)
                foreach (Action handler in ShuttingDown.GetInvocationList()) Clean(handler);
            Clean(() => _commandBinding?.Dispose()); _commandBinding = null;
            Clean(() => Debugger?.Dispose()); Debugger = null;
            Clean(() => BootSplash?.Dispose()); BootSplash = null;
            Clean(() => _scene?.Dispose()); _scene = null;
            Clean(() => _renderer?.Dispose()); _renderer = null;
            if (first != null) System.Diagnostics.Trace.WriteLine("Runtime shutdown error: " + first);
        }

        public void Dispose()
        {
            try { OnClosing(); } finally { _window?.Dispose(); }
        }
    }
}
