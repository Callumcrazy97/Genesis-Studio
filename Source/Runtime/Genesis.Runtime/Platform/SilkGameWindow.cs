using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Input;
using Genesis.Shared.Interfaces;
using Silk.NET.Core;
using StbImageSharp;
using GCursorMode = Genesis.Shared.Interfaces.CursorMode;
using EngineInput = Genesis.Runtime.Input.InputState;
using GKey = Genesis.Runtime.Input.Key;
using GMouseButton = Genesis.Runtime.Input.MouseButton;

namespace Genesis.Runtime.Platform
{
    /// <summary>
    /// Desktop (Silk.NET) implementation of <see cref="IGameWindow"/>. This is the single
    /// window/loop used by the runtime, exported games, and (once editors move in-window) the
    /// editor tools. A console backend can implement the same IGameWindow contract without any
    /// engine changes.
    ///
    /// The window owns the OS surface + input + frame pacing. It does NOT own the renderer:
    /// the host creates an IRenderController against <see cref="NativeHandle"/> in Load.
    /// </summary>
    public sealed class SilkGameWindow : IGameWindow
    {
        private IWindow _window;
        private IInputContext _silkInput;
        private readonly EngineInput _input = new EngineInput();

        private string _title;
        private int _targetFps;
        private bool _vsync;
        private WindowMode _mode = WindowMode.Windowed;
        private bool _running;

        // FPS measurement.
        private double _fpsAccum;
        private int _fpsFrames;
        private double _currentFps;

        // Mouse-look accumulation; published while the cursor is captured.
        private Vector2 _lastMousePos;
        private Vector2 _lookAccum;
        private bool _haveMousePos;
        private bool _mouseCaptured;
        private GCursorMode _desiredCursorMode = GCursorMode.Normal;
        private bool _cursorApplyPending = true;
        private IMouse _primaryMouse;

        // Gamepad previous-frame button states for edge detection.
        private bool _gpA, _gpLB, _gpRB, _gpLT, _gpRT, _gpL3, _gpR3, _gpStart, _gpB, _gpX, _gpY, _gpDUp, _gpDDown, _gpDLeft, _gpDRight, _gpBackBtn;

        // Saved windowed placement, restored when leaving fullscreen/borderless.
        private Vector2D<int> _savedPos;
        private Vector2D<int> _savedSize;
        private bool _savedPlacement;
        private bool _pendingModeApply;
        private bool _f11Held;

        public event Action            Load;
        public event Action<double>    Update;
        public event Action<double>    Render;
        public event Action<int, int>  Resize;
        public event Action            Closing;
        public event Action<Exception> FatalError;
        public void SetStartupVisible(bool visible) { if (_window != null) _window.IsVisible = visible; }

        /// <summary>Engine input state, refreshed each frame. Assign to RuntimeScene.Input.</summary>
        public EngineInput Input => _input;

        public SilkGameWindow(string title, int width, int height)
        {
            _title = title;

            var options = WindowOptions.Default;
            options.Title           = title;
            options.IsVisible = Environment.GetEnvironmentVariable("GENESIS_BOOT_COORDINATED") != "1";
            options.Size            = new Vector2D<int>(Math.Max(1, width), Math.Max(1, height));
            options.WindowBorder    = WindowBorder.Resizable;
            options.VSync           = false;
            // FramesPerSecond/UpdatesPerSecond must be > 0 (or IsEventDriven must be false) so
            // Silk.NET/GLFW uses glfwPollEvents rather than glfwWaitEvents.  With both set to 0
            // the loop blocks indefinitely waiting for an OS input event — harmless in interactive
            // play but fatal in headless autoshot runs where no user ever moves the mouse.
            // We set a non-zero target here; the actual rate is uncapped by the render path.
            options.FramesPerSecond  = 200;  // high ceiling — renderer self-limits via vsync/sleep
            options.UpdatesPerSecond = 200;
            options.IsEventDriven    = false; // always poll, never wait
            // No client rendering API — we drive Direct3D 11 against the HWND ourselves, so the
            // backend must not create an OpenGL context that would fight the DXGI swap chain.
            options.API = GraphicsAPI.None;

            _window = Window.Create(options);

            _window.Load += () =>
            {
                _running   = true;
                ApplyWindowIcon();
                _silkInput = _window.CreateInput();
                HookInput(_silkInput);
                ApplyMode();
                ApplyUnattendedPlacement();
                Load?.Invoke();
            };

            _window.Update += dt =>
            {
                if (_cursorApplyPending)
                    ApplyCursorModeIfReady();

                _input.LookDelta = _mouseCaptured ? _lookAccum : Vector2.Zero;
                _lookAccum = Vector2.Zero;

                PollGamepad((float)dt);

                try { Update?.Invoke(dt); }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Window] Update error: {ex}");
                    FatalError?.Invoke(ex);
                }
                finally
                {
                    // Input edges belong to simulation/update frames. Clearing them from Render
                    // could discard a keyboard event that arrived after the previous Update but
                    // before Present, making KeyPressed intermittently miss short taps. Held state
                    // remains intact; only pressed/released/wheel edges advance here.
                    _input.NextFrame();
                }
            };

            _window.Render += dt =>
            {
                _fpsAccum += dt;
                _fpsFrames++;
                if (_fpsAccum >= 0.5)
                {
                    _currentFps = _fpsFrames / _fpsAccum;
                    _fpsAccum = 0;
                    _fpsFrames = 0;
                }

                // A single bad frame (e.g. mid window-mode transition) must never crash the
                // process — log and keep the loop alive so the next frame can recover.
                try
                {
                    FlushPendingLayout();
                    Render?.Invoke(dt);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Window] Render error: {ex}");
                    Genesis.Rendering.Diagnostics.RenderLog.Line("[Window] Render error: " + ex);
                    FatalError?.Invoke(ex);
                }
            };

            _window.FramebufferResize += size =>
            {
                try { Resize?.Invoke(size.X, size.Y); }
                catch (Exception ex) { Debug.WriteLine($"[Window] Resize error: {ex}"); }
            };

            _window.Closing += () =>
            {
                _running = false;
                Closing?.Invoke();
            };
        }

        // ── IGameWindow properties ────────────────────────────────────────────────

        public string Title
        {
            get => _title;
            set { _title = value; if (_window != null) _window.Title = value; }
        }

        public int Width  => _window != null ? _window.FramebufferSize.X : 0;
        public int Height => _window != null ? _window.FramebufferSize.Y : 0;

        public int TargetFps
        {
            get => _targetFps;
            set
            {
                _targetFps = Math.Max(0, value);
                if (_window != null)
                {
                    _window.FramesPerSecond  = _targetFps;
                    _window.UpdatesPerSecond = _targetFps;
                }
            }
        }

        public double CurrentFps => _currentFps;

        public bool VSync
        {
            get => _vsync;
            set
            {
                _vsync = value;
                // GraphicsAPI.None: DXGI / SDL GPU own presentation. Pushing GLFW's swap
                // interval on a window with no client GL context can stack extra waits.
            }
        }

        public WindowMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                if (_running)
                    _pendingModeApply = true;
                else if (_window != null)
                    ApplyMode();
            }
        }

        public bool IsRunning => _running;

        public bool MouseCaptured => _mouseCaptured;

        public void SetMouseCaptured(bool captured)
            => SetCursorMode(captured ? GCursorMode.Locked : GCursorMode.Normal);

        public void SetCursorMode(GCursorMode mode)
        {
            if (_window == null) return;
            _desiredCursorMode = mode;
            _cursorApplyPending = true;
            ApplyCursorModeIfReady();
        }

        private void ApplyCursorModeIfReady()
        {
            bool locked = _desiredCursorMode == GCursorMode.Locked;
            _mouseCaptured = locked;
            _lookAccum = Vector2.Zero;
            _haveMousePos = false;

            if (_primaryMouse?.Cursor == null)
                return;

            var silkCursor = _primaryMouse.Cursor;
            silkCursor.CursorMode = _desiredCursorMode switch
            {
                GCursorMode.Locked => Silk.NET.Input.CursorMode.Disabled,
                GCursorMode.Hidden => Silk.NET.Input.CursorMode.Hidden,
                _ => Silk.NET.Input.CursorMode.Normal,
            };

            if (_desiredCursorMode == GCursorMode.Normal)
            {
                try { silkCursor.StandardCursor = StandardCursor.Arrow; }
                catch { /* older GLFW builds may not support every standard cursor */ }
                EnsureWindowsCursorVisible();
            }

            _cursorApplyPending = false;
        }

        /// <summary>
        /// Win32 can keep the display counter negative after GLFW cursor capture; reset so the OS
        /// pointer is visible again when menus release capture.
        /// </summary>
        private static void EnsureWindowsCursorVisible()
        {
            if (!OperatingSystem.IsWindows()) return;
            while (ShowCursor(true) < 0) { }
        }

        [DllImport("user32.dll")]
        private static extern int ShowCursor(bool bShow);

        /// <summary>Gives the GLFW window its own icon instead of relying on the executable resource.</summary>
        private void ApplyWindowIcon()
        {
            string path = GenesisBranding.ResolveSplashPath(
                Environment.GetEnvironmentVariable("GENESIS_GAME_ICON"));
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                ImageResult image = ImageResult.FromMemory(
                    File.ReadAllBytes(path),
                    ColorComponents.RedGreenBlueAlpha);
                // GLFW puts the supplied bitmap straight into HICON. Supplying only the 596px
                // artwork left Windows to shrink that oversized handle for both caption and
                // taskbar; explicit native sizes keep each DPI path sharp and independently set.
                int[] sizes = { 16, 32, 48, 64, 128, 256 };
                RawImage[] icons = new RawImage[sizes.Length];
                for (int index = 0; index < sizes.Length; index++)
                {
                    int size = sizes[index];
                    icons[index] = new RawImage(
                        size,
                        size,
                        ResizeRgba(image.Data, image.Width, image.Height, size, size));
                }
                _window.SetWindowIcon(icons);
            }
            catch (Exception exception)
            {
                // Branding is optional at runtime; an unreadable replacement must not stop a game.
                Debug.WriteLine($"[Window] Icon load failed: {exception.Message}");
            }
        }

        /// <summary>Resamples RGBA artwork without bringing GDI or WinForms into the runtime.</summary>
        private static byte[] ResizeRgba(
            byte[] source,
            int sourceWidth,
            int sourceHeight,
            int targetWidth,
            int targetHeight)
        {
            byte[] target = new byte[targetWidth * targetHeight * 4];
            for (int y = 0; y < targetHeight; y++)
            {
                float sourceY = ((y + 0.5f) * sourceHeight / targetHeight) - 0.5f;
                int y0 = Math.Clamp((int)MathF.Floor(sourceY), 0, sourceHeight - 1);
                int y1 = Math.Min(y0 + 1, sourceHeight - 1);
                float fy = Math.Clamp(sourceY - y0, 0f, 1f);

                for (int x = 0; x < targetWidth; x++)
                {
                    float sourceX = ((x + 0.5f) * sourceWidth / targetWidth) - 0.5f;
                    int x0 = Math.Clamp((int)MathF.Floor(sourceX), 0, sourceWidth - 1);
                    int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                    float fx = Math.Clamp(sourceX - x0, 0f, 1f);
                    int targetOffset = (y * targetWidth + x) * 4;

                    for (int channel = 0; channel < 4; channel++)
                    {
                        float top = Lerp(
                            source[(y0 * sourceWidth + x0) * 4 + channel],
                            source[(y0 * sourceWidth + x1) * 4 + channel],
                            fx);
                        float bottom = Lerp(
                            source[(y1 * sourceWidth + x0) * 4 + channel],
                            source[(y1 * sourceWidth + x1) * 4 + channel],
                            fx);
                        target[targetOffset + channel] = (byte)Math.Clamp(
                            (int)MathF.Round(Lerp(top, bottom, fy)),
                            0,
                            255);
                    }
                }
            }

            return target;
        }

        private static float Lerp(float from, float to, float amount)
            => from + (to - from) * amount;

        public IntPtr NativeHandle
        {
            get
            {
                var native = _window?.Native;
                if (native != null && native.Win32.HasValue)
                    return native.Win32.Value.Hwnd;
                return IntPtr.Zero;
            }
        }

        // ── Control ───────────────────────────────────────────────────────────────

        public void Run() => _window.Run();

        public void Close() => _window?.Close();

        public void SetSize(int width, int height)
        {
            if (_window == null || width <= 0 || height <= 0) return;
            if (_mode != WindowMode.Windowed) return;   // borderless/fullscreen own the size
            _window.Size = new Vector2D<int>(width, height);
        }

        /// <summary>F11 toggles between windowed and borderless-fullscreen.</summary>
        public void ToggleFullscreen() =>
            Mode = _mode == WindowMode.Fullscreen ? WindowMode.Windowed : WindowMode.Fullscreen;

        /// <summary>
        /// Applies a deferred window-mode change on the render tick, after the previous Present.
        /// Called by <see cref="GenesisRuntimeHost"/> at the start of each render frame.
        /// </summary>
        internal void FlushPendingLayout()
        {
            if (!_pendingModeApply) return;
            _pendingModeApply = false;
            ApplyMode();
        }

        public void Dispose()
        {
            _silkInput?.Dispose();
            _window?.Dispose();
        }

        // ── Internals ───────────────────────────────────────────────────────────────

        private void ApplyUnattendedPlacement()
        {
            if (_window == null || !ShouldRunOffScreen())
            {
                return;
            }

            _window.WindowState = WindowState.Normal;
            _window.WindowBorder = WindowBorder.Fixed;

            // Off-screen HWNDs make SDL_WaitAndAcquireGPUSwapchainTexture wait on DWM's
            // occluded-window clock (~15 Hz). Cloak the window on a real monitor so the
            // compositor still produces swapchain images at display rate, without a visible flash.
            if (!TryCloakWindow(NativeHandle, cloak: true))
            {
                string backend = Environment.GetEnvironmentVariable("GENESIS_RENDER_BACKEND") ?? string.Empty;
                if (backend.IndexOf("SDL", StringComparison.OrdinalIgnoreCase) >= 0)
                    _window.Position = new Vector2D<int>(48, 48);
                else
                    _window.Position = new Vector2D<int>(-12_000, -12_000);
            }
        }

        private static bool TryCloakWindow(IntPtr hwnd, bool cloak)
        {
            if (hwnd == IntPtr.Zero) return false;
            int value = cloak ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int)) == 0;
        }

        private const int DwmwaCloak = 13;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        private static bool ShouldRunOffScreen()
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("GENESIS_UNATTENDED_WINDOW"),
                    "1",
                    StringComparison.Ordinal))
            {
                return true;
            }

            string autoshot = Environment.GetEnvironmentVariable("GENESIS_AUTOSHOT");
            return float.TryParse(autoshot, out float seconds) && seconds > 0f;
        }

        // Fullscreen is implemented as BORDERLESS WINDOWED fullscreen (a hidden-border window
        // covering the monitor), never WindowState.Fullscreen. GLFW's exclusive fullscreen does
        // a display-mode change that destabilises a DXGI swap chain we own on the HWND — that is
        // what crashed. Borderless covers the screen with no mode change and is rock-solid.
        private void ApplyMode()
        {
            if (_window == null) return;

            switch (_mode)
            {
                case WindowMode.Windowed:
                    _window.WindowState  = WindowState.Normal;
                    _window.WindowBorder = WindowBorder.Resizable;
                    if (_savedPlacement)
                    {
                        _window.Size     = _savedSize;
                        _window.Position = _savedPos;
                        _savedPlacement  = false;
                    }
                    else
                    {
                        // Fallback if we entered fullscreen from maximized (no saved placement).
                        _window.Size = new Vector2D<int>(1280, 720);
                    }
                    break;

                case WindowMode.Borderless:
                case WindowMode.Fullscreen:
                    if (!_savedPlacement)
                    {
                        _savedSize      = _window.Size;
                        _savedPos       = _window.Position;
                        _savedPlacement = true;
                    }

                    _window.WindowState  = WindowState.Normal;   // not Maximized/Fullscreen
                    _window.WindowBorder = WindowBorder.Hidden;

                    var monitor = _window.Monitor;
                    if (monitor != null)
                    {
                        _window.Position = monitor.Bounds.Origin;
                        _window.Size     = monitor.Bounds.Size;
                    }
                    else
                    {
                        // No monitor info — fall back to maximised borderless.
                        _window.WindowState = WindowState.Maximized;
                    }
                    break;
            }

            NotifyClientResize();
        }

        private void NotifyClientResize()
        {
            if (_window == null) return;
            var size = _window.FramebufferSize;
            if (size.X <= 0 || size.Y <= 0) return;
            try { Resize?.Invoke(size.X, size.Y); }
            catch (Exception ex) { Debug.WriteLine($"[Window] Resize error: {ex}"); }
        }

        private void HookInput(IInputContext input)
        {
            if (input == null) return;

            for (int i = 0; i < input.Keyboards.Count; i++)
            {
                IKeyboard kb = input.Keyboards[i];
                kb.KeyDown += (_, key, _) =>
                {
                    if (key == Key.F11)
                    {
                        if (!_f11Held)
                        {
                            _f11Held = true;
                            ToggleFullscreen();
                        }
                        return;
                    }
                    GKey g = Map(key); if (g != GKey.Unknown) _input.OnKeyDown(g);
                };
                kb.KeyUp   += (_, key, _) =>
                {
                    if (key == Key.F11) { _f11Held = false; return; }
                    GKey g = Map(key); if (g != GKey.Unknown) _input.OnKeyUp(g);
                };
            }

            for (int i = 0; i < input.Mice.Count; i++)
            {
                IMouse mouse = input.Mice[i];
                if (_primaryMouse == null)
                {
                    _primaryMouse = mouse;
                    ApplyCursorModeIfReady();
                }
                mouse.MouseMove += (_, pos) =>
                {
                    _input.OnMouseMove(pos.X, pos.Y);
                    if (_haveMousePos && _mouseCaptured)
                    {
                        Vector2 delta = pos - _lastMousePos;
                        if (delta.LengthSquared() <= 2500f)
                            _lookAccum += delta;
                    }
                    _lastMousePos = pos;
                    _haveMousePos = true;
                };
                mouse.MouseDown += (_, btn) => _input.OnMouseDown(MapMouse(btn));
                mouse.MouseUp += (_, btn) => _input.OnMouseUp(MapMouse(btn));
                mouse.Scroll += (_, wheel) => _input.OnWheel(wheel.Y);
            }
        }

        // ── Native gamepad (Xbox / Legion Go etc.) ───────────────────────────────────
        // Polled each frame and translated into the same InputState the rest of the engine reads:
        //   left stick → analog move, right stick → look, LT/RT → LMB/RMB, LB/RB → inv cycle,
        //   A → jump (Space), L3/R3 → sprint/crouch, Start → pause, B → back/cancel.
        private void PollGamepad(float dt)
        {
            if (_silkInput == null || _silkInput.Gamepads.Count == 0) { _input.GamepadConnected = false; return; }

            IGamepad gp = _silkInput.Gamepads[0];
            if (gp == null || !gp.IsConnected) { _input.GamepadConnected = false; return; }
            _input.GamepadConnected = true;

            Vector2 ls = ReadStick(gp, 0);
            ls.Y = -ls.Y; // Invert forwards/backwards
            Vector2 rs = ReadStick(gp, 1);
            _input.LeftStick = ls;
            _input.RightStick = rs;
            float lt = ReadTrigger(gp, 0);
            float rt = ReadTrigger(gp, 1);
            _input.LeftTrigger = lt;
            _input.RightTrigger = rt;

            // Right stick → camera look (same units as accumulated mouse delta).
            if (_mouseCaptured && rs.LengthSquared() > 0.0004f)
                _input.LookDelta += new Vector2(rs.X, rs.Y) * (1100f * dt);

            // A → Space (jump) + confirm
            bool a = Btn(gp, ButtonName.A);
            if (a != _gpA) { if (a) { _input.OnKeyDown(GKey.Space); _input.ConfirmPressed = true; } else _input.OnKeyUp(GKey.Space); _gpA = a; }

            // L3 sprint (Control held), R3 crouch (Shift held)
            bool l3 = Btn(gp, ButtonName.LeftStick);
            if (l3 && !_gpL3) _input.LeftStickPressed = true;
            HoldKey(l3, ref _gpL3, GKey.Control);
            HoldKey(Btn(gp, ButtonName.RightStick), ref _gpR3, GKey.Shift);

            // RT → left mouse (mine), LT → right mouse (place)
            bool rtD = rt > 0.5f;
            if (rtD != _gpRT) { if (rtD) _input.OnMouseDown(GMouseButton.Left); else _input.OnMouseUp(GMouseButton.Left); _gpRT = rtD; }
            bool ltD = lt > 0.5f;
            if (ltD != _gpLT) { if (ltD) _input.OnMouseDown(GMouseButton.Right); else _input.OnMouseUp(GMouseButton.Right); _gpLT = ltD; }

            // LB / RB → inventory cycle bumper events
            bool lb = Btn(gp, ButtonName.LeftBumper);
            if (lb && !_gpLB) { _input.LeftBumperPressed = true; } _gpLB = lb;
            bool rb = Btn(gp, ButtonName.RightBumper);
            if (rb && !_gpRB) { _input.RightBumperPressed = true; } _gpRB = rb;

            // Start → pause, B → back/cancel
            bool start = Btn(gp, ButtonName.Start);
            if (start && !_gpStart) _input.StartPressed = true; _gpStart = start;
            bool b = Btn(gp, ButtonName.B);
            if (b && !_gpB) _input.BackPressed = true; _gpB = b;

            // X, Y, DPad
            bool x = Btn(gp, ButtonName.X);
            if (x && !_gpX) _input.XPressed = true; _gpX = x;
            bool y = Btn(gp, ButtonName.Y);
            if (y && !_gpY) _input.YPressed = true; _gpY = y;
            bool up = Btn(gp, ButtonName.DPadUp);
            if (up && !_gpDUp) _input.DPadUpPressed = true; _gpDUp = up;
            bool down = Btn(gp, ButtonName.DPadDown);
            if (down && !_gpDDown) _input.DPadDownPressed = true; _gpDDown = down;
            bool left = Btn(gp, ButtonName.DPadLeft);
            if (left && !_gpDLeft) _input.DPadLeftPressed = true; _gpDLeft = left;
            bool right = Btn(gp, ButtonName.DPadRight);
            if (right && !_gpDRight) _input.DPadRightPressed = true; _gpDRight = right;
            bool backBtn = Btn(gp, ButtonName.Back);
            if (backBtn && !_gpBackBtn) _input.RecipeBookPressed = true; _gpBackBtn = backBtn;
        }

        private void HoldKey(bool now, ref bool prev, GKey key)
        {
            if (now == prev) return;
            if (now) _input.OnKeyDown(key); else _input.OnKeyUp(key);
            prev = now;
        }

        private static Vector2 ReadStick(IGamepad gp, int index)
        {
            if (gp.Thumbsticks.Count <= index) return Vector2.Zero;
            var t = gp.Thumbsticks[index];
            var v = new Vector2(t.X, t.Y);
            const float dead = 0.18f;
            return v.Length() < dead ? Vector2.Zero : v;
        }

        private static float ReadTrigger(IGamepad gp, int index)
        {
            if (gp.Triggers.Count <= index) return 0f;
            return gp.Triggers[index].Position;
        }

        private static bool Btn(IGamepad gp, ButtonName name)
        {
            var bs = gp.Buttons;
            for (int i = 0; i < bs.Count; i++)
                if (bs[i].Name == name) return bs[i].Pressed;
            return false;
        }

        private static GMouseButton MapMouse(MouseButton b) => b switch
        {
            MouseButton.Left   => GMouseButton.Left,
            MouseButton.Right  => GMouseButton.Right,
            MouseButton.Middle => GMouseButton.Middle,
            _                  => GMouseButton.Left,
        };

        private static GKey Map(Key k) => k switch
        {
            Key.A => GKey.A, Key.B => GKey.B, Key.C => GKey.C, Key.D => GKey.D,
            Key.E => GKey.E, Key.F => GKey.F, Key.G => GKey.G, Key.H => GKey.H,
            Key.I => GKey.I, Key.J => GKey.J, Key.K => GKey.K, Key.L => GKey.L,
            Key.M => GKey.M, Key.N => GKey.N, Key.O => GKey.O, Key.P => GKey.P,
            Key.Q => GKey.Q, Key.R => GKey.R, Key.S => GKey.S, Key.T => GKey.T,
            Key.U => GKey.U, Key.V => GKey.V, Key.W => GKey.W, Key.X => GKey.X,
            Key.Y => GKey.Y, Key.Z => GKey.Z,
            Key.Space        => GKey.Space,
            Key.Enter        => GKey.Enter,
            Key.Escape       => GKey.Escape,
            Key.Tab          => GKey.Tab,
            Key.Backspace    => GKey.Backspace,
            Key.Delete       => GKey.Delete,
            Key.ShiftLeft    => GKey.Shift,
            Key.ShiftRight   => GKey.Shift,
            Key.ControlLeft  => GKey.Control,
            Key.ControlRight => GKey.Control,
            Key.AltLeft      => GKey.Alt,
            Key.AltRight     => GKey.Alt,
            Key.Up           => GKey.Up,
            Key.Down         => GKey.Down,
            Key.Left         => GKey.Left,
            Key.Right        => GKey.Right,
            Key.Number1      => GKey.D1,
            Key.Number2      => GKey.D2,
            Key.Number3      => GKey.D3,
            Key.Number4      => GKey.D4,
            Key.Number5      => GKey.D5,
            Key.Number6      => GKey.D6,
            Key.Number7      => GKey.D7,
            Key.Number8      => GKey.D8,
            Key.Number9      => GKey.D9,
            Key.Number0      => GKey.D0,
            Key.F1           => GKey.F1,
            Key.F2           => GKey.F2,
            Key.F3           => GKey.F3,
            Key.F4           => GKey.F4,
            Key.F5           => GKey.F5,
            Key.F6           => GKey.F6,
            Key.F7           => GKey.F7,
            Key.F8           => GKey.F8,
            Key.F9           => GKey.F9,
            Key.F10          => GKey.F10,
            Key.F11          => GKey.F11,
            Key.F12          => GKey.F12,
            _                => GKey.Unknown,
        };
    }
}
