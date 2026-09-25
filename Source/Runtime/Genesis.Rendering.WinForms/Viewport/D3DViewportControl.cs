using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Genesis.Rendering.Core;
using Genesis.Rendering.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Viewport
{
    /// <summary>How frames are driven for a <see cref="D3DViewportControl"/>.</summary>
    public enum ViewportDriveMode
    {
        /// <summary>Internal WinForms timer fires at TargetFps on the UI thread (legacy default).
        /// Low resolution (~15.6 ms) and message-pump bound — capped near 60 fps regardless of cost.</summary>
        Timer,
        /// <summary>A dedicated high-precision <see cref="RenderLoop"/> thread paces frames and posts
        /// each one to the UI thread for D3D. Removes the WM_TIMER ceiling/jitter; TargetFps = 0 = uncapped.</summary>
        Threaded,
        /// <summary>The caller drives rendering by calling <see cref="RenderFrame"/> (e.g. an external loop).</summary>
        External,
    }

    // WinForms control that hosts the currently selected Genesis render backend.
    //
    // Render-drive modes (see ViewportDriveMode):
    //   Timer     — internal WinForms timer at TargetFps (UI thread). Backward-compatible default.
    //   Threaded  — dedicated RenderLoop thread → posts RenderFrame to the UI thread. TargetFps 0 = uncapped.
    //   External  — caller drives via RenderFrame().
    // TimerEnabled is preserved for compatibility: true => Timer, false => External.
    //
    // Thread contract: exactly one thread calls RenderFrame() at a time (RenderFrameCore takes
    // _renderLock). In Threaded mode the loop posts via BeginInvoke, so D3D still runs on the UI
    // thread — the win is precise pacing free of WM_TIMER, not off-thread rendering.
    //
    // Resize handling: the swap chain follows the control's client size. OnResize records the new
    // size on the UI thread and the render path applies it between frames, debounced by
    // ResizeSettleFrames so an interactive drag does not rebuild the chain on every WM_SIZE —
    // DXGI's Scaling.Stretch covers those few frames.
    //
    // This used to be a permanently fixed 1280x720 chain relying on that stretch, which left every
    // viewport resampled (and visibly soft) at any other size. It also could not survive a second
    // backend: a Vulkan swap chain whose extent does not match its surface returns SUBOPTIMAL/
    // OUT_OF_DATE every frame, and OpenGL's default framebuffer *is* the window.
    public class D3DViewportControl : Control
    {
        // Size used before the handle exists and the real client rect can be read.
        private const int DefaultBufferWidth  = 1280;
        private const int DefaultBufferHeight = 720;

        /// <summary>
        /// Frames a new client size must hold steady before the swap chain is rebuilt to match.
        /// </summary>
        private const int ResizeSettleFrames = 2;

        /// <summary>Optional hook for WM_INPUT / raw input before default processing.</summary>
        public WndProcHookHandler WndProcHook;

        public delegate void WndProcHookHandler(ref Message m);
        private readonly object _renderLock = new object();
        private IRenderController _renderer;
        private System.Windows.Forms.Timer _renderTimer;
        private volatile bool _handleReady;
        private RenderBackendOption? _backendOverride;
        private ViewportDriveMode _driveMode = ViewportDriveMode.Timer;
        private int  _targetFps = 60;   // 0 = uncapped (Threaded mode only)
        // Seeded from the Studio preference rather than hardcoded off, and kept in step with it
        // while the control lives — changing "Use VSync in editor previews" used to save a value
        // that every viewport then ignored.
        private bool _vsync = Genesis.Rendering.Core.EditorPreviewSettings.VSync;
        private bool _renderingSuspended;

        // Threaded drive
        private Thread _renderThread;
        private CancellationTokenSource _renderCts;

        // Frame-time measurement (Phase 0)
        private double _lastCpuMs;

        private volatile int _clientWidth  = DefaultBufferWidth;
        private volatile int _clientHeight = DefaultBufferHeight;

        // Debounce state for swap-chain resize. Touched only under _renderLock.
        private int _settlingWidth;
        private int _settlingHeight;
        private int _resizeSettle;

        public event Action<IRenderController> OnRender;

        /// <summary>Raised after <see cref="IRenderController.EndFrame"/> and before Present — use for D2D HUD overlays.</summary>
        public event Action<IRenderController> OnPostFrame;

        /// <summary>
        /// Last renderer exception observed by this viewport. Timer-driven rendering remains
        /// alive after a fault, but the fault is retained and counted so tests and UI diagnostics
        /// cannot mistake a swallowed exception for a successful black frame.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Exception LastRenderException { get; private set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public long RenderFaultCount => Interlocked.Read(ref _renderFaultCount);

        private long _renderFaultCount;

        public void ClearRenderFault()
        {
            LastRenderException = null;
            Interlocked.Exchange(ref _renderFaultCount, 0);
        }

        private void RecordRenderFault(string operation, Exception exception)
        {
            LastRenderException = exception;
            Interlocked.Increment(ref _renderFaultCount);
            string detail = $"[Viewport:{operation}] backend={_renderer?.BackendName ?? "uninitialised"} {exception}";
            Debug.WriteLine(detail);
            RenderLog.Line(detail);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int TargetFps
        {
            get => _targetFps;
            set
            {
                _targetFps = value < 0 ? 0 : value;
                // A WinForms timer cannot truly uncap; 1 ms ≈ 1000 fps ceiling in Timer mode.
                // Real uncapping (0) is delivered by the threaded RenderLoop, which reads _targetFps.
                _renderTimer.Interval = _targetFps <= 0 ? 1 : Math.Max(1000 / _targetFps, 1);
                if (_driveMode == ViewportDriveMode.Timer && _handleReady) _renderTimer.Start();
            }
        }

        /// <summary>Frame driver. Setting it at runtime restarts the active driver.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ViewportDriveMode DriveMode
        {
            get => _driveMode;
            set
            {
                if (_driveMode == value) return;
                _driveMode = value;
                if (!_handleReady) return;
                StopActiveDriver();
                StartActiveDriver();
            }
        }

        /// <summary>Compatibility shim: true => Timer, false => External (caller drives RenderFrame()).</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool TimerEnabled
        {
            get => _driveMode == ViewportDriveMode.Timer;
            set => DriveMode = value ? ViewportDriveMode.Timer : ViewportDriveMode.External;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool VSync
        {
            get => _vsync;
            set
            {
                _vsync = value;
                lock (_renderLock) { _renderer?.SetVSync(value); }
            }
        }

        public IRenderController Renderer => _renderer;

        /// <summary>
        /// When set, this viewport creates that backend instead of the process-wide preference.
        /// Help → Commands Visual Test uses this so a backend sweep does not retarget Studio's editors.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public RenderBackendOption? BackendOverride
        {
            get => _backendOverride;
            set
            {
                if (_backendOverride == value) return;
                _backendOverride = value;
                if (_handleReady && IsHandleCreated)
                    RecreateRenderer();
            }
        }

        /// <summary>Smoothed CPU milliseconds for the last frame (BeginFrame→Present).</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double LastCpuMs => _lastCpuMs;

        /// <summary>GPU milliseconds for the last resolved frame (a few frames of latency).</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double LastGpuMs => _renderer?.LastGpuMilliseconds ?? 0.0;

        /// <summary>Live WinForms client width (for aspect ratio / UI layout).</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int ClientWidth => _clientWidth > 0 ? _clientWidth : DefaultBufferWidth;

        /// <summary>Live WinForms client height (for aspect ratio / UI layout).</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int ClientHeight => _clientHeight > 0 ? _clientHeight : DefaultBufferHeight;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int PixelWidth => ClientWidth;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int PixelHeight => ClientHeight;

        /// <summary>
        /// Swap-chain render width. Tracks <see cref="ClientWidth"/>, lagging it by at most
        /// <see cref="ResizeSettleFrames"/> rendered frames while a resize settles.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int RenderWidth => _renderer?.PixelWidth ?? DefaultBufferWidth;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int RenderHeight => _renderer?.PixelHeight ?? DefaultBufferHeight;

        public D3DViewportControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.Opaque              |
                ControlStyles.UserPaint           |
                ControlStyles.ResizeRedraw,
                true);

            _renderTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _renderTimer.Tick += OnTimerTick;

            HandleCreated   += OnHandleCreated;
            HandleDestroyed += OnHandleDestroyed;
            RenderBackendSelection.EffectiveBackendChanged += OnEffectiveBackendChanged;
            Genesis.Rendering.Core.EditorPreviewSettings.Changed += OnPreviewSettingsChanged;
        }

        /// <summary>Applies a preference change to an already-open viewport.</summary>
        private void OnPreviewSettingsChanged()
        {
            if (IsDisposed) return;
            VSync = Genesis.Rendering.Core.EditorPreviewSettings.VSync;
        }

        /// <summary>
        /// Stops this viewport rendering without tearing down its device.
        /// </summary>
        /// <remarks>
        /// Used by "Pause preview when Studio loses focus": a handful of D3D viewports redrawing at
        /// 60fps behind another application is exactly the cost that option exists to avoid. The
        /// swap chain and textures stay alive, so resuming is instant and nothing has to reload.
        /// </remarks>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool RenderingSuspended
        {
            get => _renderingSuspended;
            set
            {
                if (_renderingSuspended == value) return;
                _renderingSuspended = value;
                if (value)
                {
                    StopActiveDriver();
                }
                else if (_handleReady)
                {
                    StartActiveDriver();
                }
            }
        }

        private void OnHandleCreated(object sender, EventArgs e)
        {
            QueueClientResize();
            lock (_renderLock)
            {
                try
                {
                    _renderer = CreateController();
                    // Create at the real client size (QueueClientResize above has already sampled it)
                    // so the first presented frame is already correct rather than a stretched default.
                    _renderer.Initialize(Handle, ClientWidth, ClientHeight);
                    _renderer.SetVSync(_vsync);
                    _handleReady = true;
                }
                catch (Exception ex)
                {
                    RecordRenderFault("Initialize", ex);
                    try { _renderer?.Dispose(); } catch { /* already failing */ }
                    _renderer = null;
                    _handleReady = false;
                    return;
                }
            }
            StartActiveDriver();
        }


        private IRenderController CreateController() =>
            RenderControllerFactory.Create(_backendOverride ?? RenderBackendSelection.EffectiveBackend);

        private void RecreateRenderer()
        {
            if (!_handleReady || !IsHandleCreated)
                return;

            StopActiveDriver();
            lock (_renderLock)
            {
                try
                {
                    _renderer?.Dispose();
                    _renderer = CreateController();
                    _renderer.Initialize(Handle, ClientWidth, ClientHeight);
                    _renderer.SetVSync(_vsync);
                    _settlingWidth = ClientWidth;
                    _settlingHeight = ClientHeight;
                    _resizeSettle = 0;
                    _handleReady = true;
                }
                catch (Exception ex)
                {
                    RecordRenderFault("RecreateRenderer", ex);
                    try { _renderer?.Dispose(); } catch { /* already failing */ }
                    _renderer = null;
                    _handleReady = false;
                    return;
                }
            }
            StartActiveDriver();
            Invalidate();
        }

        private void OnEffectiveBackendChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((Action)(() => OnEffectiveBackendChanged(sender, e)));
                }
                catch (InvalidOperationException)
                {
                    // The handle is being torn down; the next handle creation will use the new backend.
                }
                return;
            }

            // A visual-test override owns this viewport; ignore the Studio-wide preference flip.
            if (_backendOverride.HasValue)
                return;

            RecreateRenderer();
        }

        private void OnHandleDestroyed(object sender, EventArgs e)
        {
            // Stop drivers (incl. joining the render thread) BEFORE disposing the renderer so no
            // posted frame can run against a disposed device.
            StopActiveDriver();
            lock (_renderLock)
            {
                _handleReady = false;
                _renderer?.Dispose();
                _renderer = null;
            }
        }

        // ── Drive management ────────────────────────────────────────────────────
        private void StartActiveDriver()
        {
            if (!_handleReady || _renderingSuspended) return;
            switch (_driveMode)
            {
                case ViewportDriveMode.Timer:    _renderTimer.Start(); break;
                case ViewportDriveMode.Threaded: StartRenderThread();  break;
                // External: caller drives RenderFrame()
            }
        }

        private void StopActiveDriver()
        {
            _renderTimer.Stop();
            StopRenderThread();
        }

        private void StartRenderThread()
        {
            if (_renderThread != null) return;
            (_renderThread, _renderCts) = RenderLoop.Create(
                onTick:         RenderFrame,
                postToUiThread: PostToUi,
                getTargetFps:   () => _targetFps,   // 0 = uncapped
                isFocused:      () => true,          // editor renders regardless of control focus
                name:           "GenesisEditorRender");
            _renderThread.Start();
        }

        private void StopRenderThread()
        {
            try { _renderCts?.Cancel(); } catch { /* ignore */ }
            try { _renderThread?.Join(500); } catch { /* ignore */ }
            _renderThread = null;
            try { _renderCts?.Dispose(); } catch { /* ignore */ }
            _renderCts = null;
        }

        private void PostToUi(Action a)
        {
            // Render thread → UI thread. Guard against handle teardown between check and invoke.
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
            }
            catch { /* handle went away — drop this frame */ }
        }

        // Record the new size; DXGI resize runs after Present (see RenderFrameCore).
        protected override void OnResize(EventArgs e)
        {
            // Sample the new client rect BEFORE raising Resize. base.OnResize is what fires the
            // public event, and subscribers routinely re-fit their content against ClientWidth /
            // ClientHeight — reading those first gave them the *previous* size, so a viewport whose
            // first layout pass was degenerate stayed fitted for that degenerate size.
            QueueClientResize();
            base.OnResize(e);
        }

        public void SyncClientSize() => QueueClientResize();

        /// <summary>
        /// Advances the backend's animated-scene clock by one explicit step, between frames.
        /// Deterministic capture harnesses need to control this precisely: readback renders several
        /// frames to let the swap chain settle, so advancing time inside the render callback would
        /// make the result depend on how many frames that happened to take.
        /// </summary>
        public void AdvanceSceneTime(float deltaSeconds)
        {
            lock (_renderLock)
            {
                if (!_handleReady || _renderer?.IsInitialized != true) return;
                _renderer.Advance3DTime(deltaSeconds);
            }
        }

        private void QueueClientResize()
        {
            var (w, h) = GetClientSizePixels();
            if (w <= 0 || h <= 0) return;
            _clientWidth  = w;
            _clientHeight = h;
        }

        private (int w, int h) GetClientSizePixels()
        {
            // IsHandleCreated, not Handle == Zero — the Handle getter would attempt
            // (and throw on) cross-thread handle creation from the render thread.
            if (!IsHandleCreated)
                return (Math.Max(Width, 1), Math.Max(Height, 1));

            if (!GetClientRect(Handle, out var r))
                return (Math.Max(Width, 1), Math.Max(Height, 1));

            int w = Math.Max(r.Right - r.Left, 1);
            int h = Math.Max(r.Bottom - r.Top, 1);
            return (w, h);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private void OnTimerTick(object sender, EventArgs e) => RenderFrameCore();

        public void RenderFrame() => RenderFrameCore();

        // Rendering is serialized per HWND/renderer.  This must not be static: the editor can host
        // several independent preview controls, and a busy threaded viewport must not cause an
        // Image Viewer or Image Editor frame to be silently dropped.
        private bool _isRendering;

        private void RenderFrameCore()
        {
            if (_isRendering) return; // Drop only re-entrant work for this viewport.

            _isRendering = true;
            try
            {
                lock (_renderLock)
                {
                    if (!_handleReady || _renderer?.IsInitialized != true) return;

                    if (!_renderer.IsFramebufferReady)
                        return;

                    if (_clientWidth <= 0 || _clientHeight <= 0)
                        return;

                    ApplyPendingResize(immediate: false);

                    long startTs = Stopwatch.GetTimestamp();
                    try
                    {
                        _renderer.BeginFrame();
                        OnRender?.Invoke(_renderer);
                        _renderer.EndFrame();
                        OnPostFrame?.Invoke(_renderer);
                        _renderer.Present();
                    }
                    catch (Exception ex)
                    {
                        RecordRenderFault("RenderFrame", ex);
                    }

                    long endTs = Stopwatch.GetTimestamp();
                    double ms = (endTs - startTs) * 1000.0 / Stopwatch.Frequency;
                    _lastCpuMs += (ms - _lastCpuMs) * 0.1;  // light exponential smoothing
                }
            }
            finally
            {
                _isRendering = false;
            }
        }

        /// <summary>
        /// Rebuilds the swap chain when the control's client size has changed. Caller must hold
        /// <see cref="_renderLock"/>, and this must run between frames — never between BeginFrame
        /// and Present — because the backend drops every pipeline binding that could still
        /// reference a swap-chain view before resizing.
        /// </summary>
        /// <param name="immediate">
        /// Skip the settle delay. Used by readback, which is a deliberate one-shot capture rather
        /// than a frame in a drag, so it must land on the requested size straight away.
        /// </param>
        private void ApplyPendingResize(bool immediate)
        {
            int w = _clientWidth;
            int h = _clientHeight;
            if (w <= 0 || h <= 0 || _renderer == null) return;

            if (_renderer.PixelWidth == w && _renderer.PixelHeight == h)
            {
                _resizeSettle = 0;
                return;
            }

            if (!immediate)
            {
                // Restart the settle whenever the target moves, so a drag only rebuilds once it stops.
                if (w != _settlingWidth || h != _settlingHeight)
                {
                    _settlingWidth = w;
                    _settlingHeight = h;
                    _resizeSettle = 0;
                    return;
                }

                if (++_resizeSettle < ResizeSettleFrames) return;
            }

            _resizeSettle = 0;
            if (!_renderer.TryResize(w, h))
            {
                // Not fatal — the previous buffers are still valid and DXGI keeps stretching them,
                // so say so and carry on rather than dropping the viewport.
                Debug.WriteLine(
                    $"[Viewport] Swap-chain resize to {w}x{h} was refused; " +
                    $"continuing at {_renderer.PixelWidth}x{_renderer.PixelHeight}.");
            }
        }

        // Render exactly one frame under the render lock. Used by ReadbackFrameToBitmap so we can
        // render a final frame WITHOUT presenting (flip-discard recycles the back buffer on Present,
        // so the readback must happen before the present). Caller must hold _renderLock.
        private bool RenderOneFrame(bool present)
        {
            if (!_handleReady || _renderer?.IsInitialized != true || !_renderer.IsFramebufferReady) return false;
            try
            {
                _renderer.BeginFrame();
                OnRender?.Invoke(_renderer);
                _renderer.EndFrame();
                OnPostFrame?.Invoke(_renderer);
                if (present)
                {
                    _renderer.Present();
                }
                else
                {
                    // Present normally drains queued DrawText commands. Readback deliberately
                    // skips Present because flip-model swap chains recycle the back buffer, so
                    // explicitly compose the pending overlay before copying the final frame.
                    _renderer.ComposeOverlay(static _ => { });
                }
                return true;
            }
            catch (Exception ex)
            {
                RecordRenderFault("Readback", ex);
                return false;
            }
        }

        public Bitmap ReadbackFrameToBitmap(int settleFrames = 2)
        {
            lock (_renderLock)
            {
                if (!_handleReady || _renderer == null) return null;
                if (!_renderer.IsInitialized || !_renderer.IsFramebufferReady) return null;
                if (_clientWidth <= 0 || _clientHeight <= 0) return null;

                ApplyPendingResize(immediate: true);

                for (int i = 0; i < Math.Max(0, settleFrames); i++)
                {
                    if (!RenderOneFrame(present: true)) return null;
                }

                if (!RenderOneFrame(present: false)) return null;

                if (!_renderer.TryReadFramePixels(out int w, out int h, out byte[] bgra) || bgra == null)
                {
                    return null;
                }

                var bmp  = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, w, h);
                var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
                try
                {
                    int rowBytes = w * 4;
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(bgra, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
                }
                finally { bmp.UnlockBits(data); }
                return bmp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            WndProcHook?.Invoke(ref m);
            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RenderBackendSelection.EffectiveBackendChanged -= OnEffectiveBackendChanged;
                Genesis.Rendering.Core.EditorPreviewSettings.Changed -= OnPreviewSettingsChanged;
                StopActiveDriver();
                _renderTimer?.Dispose();
                lock (_renderLock)
                {
                    _handleReady = false;
                    _renderer?.Dispose();
                    _renderer = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
