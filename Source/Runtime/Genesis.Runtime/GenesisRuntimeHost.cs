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
            _renderer = RenderControllerFactory.Create();
            _renderer.Initialize(_window.NativeHandle, _windowW, _windowH);
            StartupGate?.Report("Game runtime", "Game-owned graphics device and pipelines initialized", 2, 4);
            _renderer.SetVSync(_window.VSync);

            RenderLog.Init();
            RenderLog.Line($"OnLoad handle=0x{_window.NativeHandle.ToInt64():X} size={_windowW}x{_windowH}");

            _scene = new RuntimeScene("Runtime");
            _commandBinding = new EngineRuntimeCommandBinding(_scene, _window, _options, _renderer);
            SceneDefaults.PrepareDemo(_options);
            _build(_scene, _renderer);

            _scene.Input = _window.Input;

            // Construct the in-game debugger (always present; only active in debug mode).
            // Studio keeps diagnostics available in ordinary F5 play, not only F6/debug launches.
            Debugger ??= new DebugOverlay();
            if (IsDebugMode) Debugger.IsVisible = true;
            Debugger.BindScriptHost(ScriptHost);
            Debugger.BindScene(_scene, DebugRoomName);
            Debugger.BindWindow(_window);

            _options.FrustumCulling = RenderAutoState.FrustumCulling;

            SceneBuilt?.Invoke(this);
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

            BootSplash?.Update(fdt, _renderer);
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

            // Feed the last resolved (non-stalling) GPU timestamp into the budget arbiter before
            // the variable ECS phase. The value naturally lags by a few frames on real backends.
            _scene.ObserveRenderFrame(_renderer?.LastGpuMilliseconds ?? 0.0);

            // When the debugger is paused, hold the scene fixed unless a single step was requested.
            bool paused = IsPlayPaused || (Debugger != null && Debugger.IsPaused && !Debugger.ConsumeStepRequest());
            if (!paused)
            {
                _scene.RebuildSpatialGrid();
                int steps = _scene.FixedTimestep.Advance(fdt);
                for (int i = 0; i < steps; i++)
                    _scene.UpdateFixed(_scene.FixedTimestep.FixedDelta);

                _scene.UpdateVariable(fdt);
            }

            // Per-frame host hook (audio voice recycling, network pump, etc.).
            FrameUpdate?.Invoke(fdt);

            if (_scene.Input?.WasPressed(Key.F12) == true && !string.IsNullOrWhiteSpace(ScreenshotProjectPath))
                _screenshotPending = true;
        }

        /// <summary>Current room name shown in the F6 debug overlay. Set by the player.</summary>
        public string DebugRoomName { get; set; }
        public bool IsPlayPaused { get; set; }

        /// <summary>
        /// Raised once per variable update frame with the delta time. Host-owned
        /// services that aren't ECS subsystems (audio voice recycling, the in-game
        /// debugger overlay) subscribe here.
        /// </summary>
        public event Action<float> FrameUpdate;

        private void OnRender(double dt)
        {
            if (!_ready || _renderer == null) return;

            SyncLiveWindowSize();

            // Minimized / zero client area — skip DXGI work.
            if (_window.Width <= 0 || _window.Height <= 0)
                return;

            if (!_renderer.IsFramebufferReady)
                return;

            if ((_frame++ % 120) == 0)
                RenderLog.Line($"render frame={_frame} buffer={_renderer.PixelWidth}x{_renderer.PixelHeight} window={_windowW}x{_windowH} fps={_window.CurrentFps:F0}");

            _renderer.BeginFrame();

            bool booting = BootSplash != null && !BootSplash.IsComplete;
            _startupFrameWasSplash = booting;
            RoomRenderSubsystem roomPresentation = null;
            if (!booting)
            {
                for (int i = 0; i < _scene.Subsystems.Count; i++)
                    if (_scene.Subsystems[i] is RoomRenderSubsystem rr) { roomPresentation = rr; break; }
            }

            bool twoDRoom = roomPresentation?.IsTwoD == true;
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
                roomPresentation.Render2D(_scene, _renderer, _frameQueue);
                ScriptHost?.DispatchRenderFrame(_renderer, _frameQueue);
                ScriptHost?.DispatchPgslWorldDraw(_renderer, _frameQueue);
                _frameQueue.Flush(_renderer, includeMeshes: false, includeSprites: true);

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
                // Offscreen ports share the renderer, so history from another camera must never be reused.
                if (roomPresentation?.HasThreeDViewports == true) meshState.CloudTemporalEnabled = false;
                _renderer.SetMesh3DState(meshState);

                pipeline.Enter(ComponentPipelinePhase.Submit);
                RenderAutoState.AllowDrawSubmit = true;
                Engine.SetDrawCommandSink(_frameQueue);
                _frameQueue.Reset();
                _commandBinding?.ApplyFrameRenderState();
                ScriptHost?.DispatchRenderFrame(_renderer, _frameQueue);
                ScriptHost?.DispatchPgslWorldDraw(_renderer, _frameQueue);
                int drawCount = 0;
                _scene.CollectMeshes(_meshBuffer, ref drawCount, _renderer);
                int instanceCount = _scene.SubmitInstanceBatches(_renderer);
                _renderer.Set3DFrameActive(drawCount > 0 || instanceCount > 0 || _frameQueue.MeshCount > 0 || meshState.AuthoredSkyEnabled);
                if (drawCount > 0)
                    _renderer.DrawMeshBatch(_meshBuffer.AsSpan(0, drawCount));
                _frameQueue.Flush(_renderer, includeMeshes: true, includeSprites: true);
                RenderAutoState.AllowDrawSubmit = false;
                Engine.SetDrawCommandSink(null);
                pipeline.Enter(ComponentPipelinePhase.Present);
            }

            _renderer.Advance3DTime((float)dt);
            if (roomPresentation?.RenderViewports3D(_scene, _renderer, RenderCamera) != true) RenderCamera();

            try
            {
                _renderer.EndFrame();
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
                _renderer?.Present();
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
            try
            {
                if (ScriptHost != null || _overlayDraw != null || Debugger != null)
                    ComposeGameplayOverlay();
            }
            catch (Exception ex)
            {
                RenderLog.Line("ComposeGameplayOverlay error: " + ex.Message);
                if (StartupGate != null && !StartupGate.IsActivated) throw;
            }

            EndFrame?.Invoke(_renderer);
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
            if (scriptsReady)
            {
                // PGSL shapes use the sprite renderer while its text uses the overlay. Draw and
                // flush the shapes first, then replay the buffered text onto the overlay so a
                // scripted panel cannot cover its own labels (NEXT-074).
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
        }

        /// <summary>
        /// Records HUD calls whose Direct2D target is not available until after the PGSL sprite
        /// layer has been flushed. Commands retain their authored order when replayed.
        /// </summary>
        private sealed class BufferedHudCanvas : IHudCanvas
        {
            private enum Kind { Text, TextCentered, Rect, Line }

            private readonly List<Command> _commands = new();

            private readonly struct Command
            {
                public readonly Kind Type;
                public readonly string Value;
                public readonly float A, B, C, D, E;
                public readonly Vector4 Color;
                public readonly bool Filled;

                public Command(Kind type, string value, float a, float b, float c, float d, float e,
                    Vector4 color, bool filled = true)
                {
                    Type = type;
                    Value = value;
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
            }

            public void Text(string text, float x, float y, float size, Vector4 color) =>
                _commands.Add(new Command(Kind.Text, text, x, y, size, 0f, 0f, color));

            public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color) =>
                _commands.Add(new Command(Kind.TextCentered, text, centerX, y, width, size, 0f, color));

            public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) =>
                _commands.Add(new Command(Kind.Rect, null, x, y, w, h, 0f, color, filled));

            public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) =>
                _commands.Add(new Command(Kind.Line, null, x1, y1, x2, y2, thickness, color));

            public void Replay(IHudCanvas destination)
            {
                if (destination == null) return;
                foreach (Command command in _commands)
                {
                    switch (command.Type)
                    {
                        case Kind.Text:
                            destination.Text(command.Value, command.A, command.B, command.C, command.Color);
                            break;
                        case Kind.TextCentered:
                            destination.TextCentered(command.Value, command.A, command.B, command.C, command.D, command.Color);
                            break;
                        case Kind.Rect:
                            destination.Rect(command.A, command.B, command.C, command.D, command.Color, command.Filled);
                            break;
                        case Kind.Line:
                            destination.Line(command.A, command.B, command.C, command.D, command.Color, command.E);
                            break;
                    }
                }
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
