using System;
using System.Runtime.InteropServices;
using System.Text;
using Genesis.Rendering.Diagnostics;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;

namespace Genesis.Rendering.SilkNet.OpenGL
{
    /// <summary>
    /// The process's OpenGL context: one core-profile context on a hidden window, made current on
    /// each viewport's DC as it renders.
    /// </summary>
    /// <remarks>
    /// <para><b>Why one context rather than one per window.</b> <see cref="Abstractions.IGpuDevice"/>
    /// separates the device from the swap chain, but OpenGL has no such split — a context is created
    /// against a device context and owns every object made through it. Creating a context per
    /// viewport would put textures, buffers and programs in separate namespaces and force
    /// <c>wglShareLists</c> plumbing for no gain. Instead the device creates one context on a hidden
    /// window and re-binds it to each viewport's DC with <c>wglMakeCurrent</c>. That is legal
    /// provided every DC involved has the <i>same pixel format</i>, which is why
    /// <see cref="ApplyPixelFormat"/> replays one descriptor everywhere.</para>
    ///
    /// <para><b>Why the bootstrap dance.</b> Asking for a 4.6 core profile needs
    /// <c>wglCreateContextAttribsARB</c>, which is an extension function, and extension functions can
    /// only be resolved while some context is already current. So a throwaway legacy context is
    /// created first purely to look up that one entry point, then destroyed. There is no way around
    /// this on Win32; it is the documented sequence.</para>
    /// </remarks>
    internal sealed unsafe class OpenGLRuntime : IDisposable
    {
        /// <summary>Versions attempted, best first. GLSL output follows whichever is granted.</summary>
        private static readonly (int Major, int Minor)[] DesiredVersions = [(4, 6), (4, 5)];

        private delegate IntPtr CreateContextAttribsArb(IntPtr hdc, IntPtr shareContext, int* attributes);

        private static CreateContextAttribsArb _createContextAttribs;
        private static bool _bootstrapped;
        private static bool? _supported;

        private static readonly object SharedGate = new();
        private static OpenGLRuntime _shared;
        private static int _shareCount;

        /// <summary>
        /// Returns the process-wide context, creating it on first use.
        /// </summary>
        /// <remarks>
        /// <para><b>One context for the whole process, not one per device.</b> The Studio opens a
        /// device per viewport, and a context per device would put each viewport's textures,
        /// buffers and programs in a separate namespace — but worse, tearing one down calls
        /// <c>wglMakeCurrent(null, null)</c>, which unbinds the context for every viewport still
        /// running. The next entry point any of them resolved then came back null, surfacing as
        /// "OpenGL entry point 'glDeleteProgram' was not found" far from the cause.</para>
        ///
        /// <para>Reference counted so the last device out turns off the lights.</para>
        /// </remarks>
        public static OpenGLRuntime Acquire()
        {
            lock (SharedGate)
            {
                _shared ??= Create();
                _shareCount++;
                return _shared;
            }
        }

        /// <summary>Releases one reference taken by <see cref="Acquire"/>.</summary>
        public static void Release()
        {
            lock (SharedGate)
            {
                if (_shareCount <= 0)
                {
                    return;
                }

                _shareCount--;
                if (_shareCount > 0)
                {
                    return;
                }

                _shared?.Dispose();
                _shared = null;
            }
        }

        private readonly IntPtr _openglModule;
        private IntPtr _hiddenWindow;
        private IntPtr _hiddenDc;
        private IntPtr _context;
        private WglNativeContext _nativeContext;
        private bool _disposed;

        private OpenGLRuntime(
            IntPtr hiddenWindow, IntPtr hiddenDc, IntPtr context, IntPtr openglModule, int major, int minor)
        {
            _hiddenWindow = hiddenWindow;
            _hiddenDc = hiddenDc;
            _context = context;
            _openglModule = openglModule;
            VersionMajor = major;
            VersionMinor = minor;

            _nativeContext = new WglNativeContext(openglModule);
            Api = GL.GetApi(_nativeContext);

            Vendor = QueryString(Wgl.GlVendor);
            Renderer = QueryString(Wgl.GlRenderer);
            VersionString = QueryString(Wgl.GlVersion);

            if (IsDebugRequested())
            {
                EnableDebugOutput();
            }
        }

        public GL Api { get; }

        public int VersionMajor { get; }

        public int VersionMinor { get; }

        /// <summary>GLSL version SPIRV-Cross should emit, matching the granted context.</summary>
        public uint GlslVersion => (uint)((VersionMajor * 100) + (VersionMinor * 10));

        public string Vendor { get; }

        public string Renderer { get; }

        public string VersionString { get; }

        public IntPtr Context => _context;

        /// <summary>True when an OpenGL 4.5-or-better core profile can be created here.</summary>
        public static bool IsSupported()
        {
            if (_supported.HasValue)
            {
                return _supported.Value;
            }

            // A live shared context is proof enough. Probing would create a second context and then
            // destroy it, and that teardown unbinds the context the running viewports are using.
            lock (SharedGate)
            {
                if (_shared != null)
                {
                    _supported = true;
                    return true;
                }
            }

            try
            {
                using OpenGLRuntime probe = Create();
                _supported = probe.VersionMajor >= 4 && (probe.VersionMajor > 4 || probe.VersionMinor >= 5);
            }
            catch (Exception ex)
            {
                RenderLog.Line($"[OpenGL] Probe failed: {ex.Message}");
                _supported = false;
            }

            return _supported.Value;
        }

        public static OpenGLRuntime Create()
        {
            Bootstrap();

            IntPtr module = Wgl.GetModuleHandleA("opengl32.dll");
            if (module == IntPtr.Zero)
            {
                module = Wgl.LoadLibraryA("opengl32.dll");
            }

            IntPtr window = CreateHiddenWindow();
            IntPtr dc = IntPtr.Zero;
            IntPtr context = IntPtr.Zero;

            try
            {
                dc = Wgl.GetDC(window);
                if (dc == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Could not obtain a device context for the OpenGL window.");
                }

                ApplyPixelFormat(dc);
                context = CreateCoreContext(dc, out int major, out int minor);

                if (!Wgl.wglMakeCurrent(dc, context))
                {
                    throw new InvalidOperationException(
                        $"wglMakeCurrent failed for the new context (Win32 error {Marshal.GetLastWin32Error()}).");
                }

                return new OpenGLRuntime(window, dc, context, module, major, minor);
            }
            catch
            {
                if (context != IntPtr.Zero) Wgl.wglDeleteContext(context);
                if (dc != IntPtr.Zero) Wgl.ReleaseDC(window, dc);
                if (window != IntPtr.Zero) Wgl.DestroyWindow(window);
                throw;
            }
        }

        /// <summary>
        /// Gives a window the one pixel format every context and DC in this process shares.
        /// </summary>
        /// <remarks>
        /// Win32 allows a window's pixel format to be set exactly once, and it can never be changed
        /// afterwards. Calling this twice on the same HWND is not an error to guard against so much
        /// as a fact to respect — the second <c>SetPixelFormat</c> simply fails, which is why the
        /// result is checked against the format already installed rather than assumed.
        /// </remarks>
        public static void ApplyPixelFormat(IntPtr hdc)
        {
            var descriptor = new Wgl.PixelFormatDescriptor
            {
                Size = (ushort)sizeof(Wgl.PixelFormatDescriptor),
                Version = 1,
                Flags = Wgl.PfdDrawToWindow | Wgl.PfdSupportOpenGL | Wgl.PfdDoubleBuffer,
                PixelType = Wgl.PfdTypeRgba,
                ColorBits = 32,
                AlphaBits = 8,
                DepthBits = 24,
                StencilBits = 8,
                LayerType = Wgl.PfdMainPlane,
            };

            int format = Wgl.ChoosePixelFormat(hdc, &descriptor);
            if (format == 0)
            {
                throw new InvalidOperationException(
                    $"No pixel format supports an OpenGL double-buffered RGBA window "
                    + $"(Win32 error {Marshal.GetLastWin32Error()}).");
            }

            if (!Wgl.SetPixelFormat(hdc, format, &descriptor))
            {
                // A window that already carries the right format is fine; anything else is fatal.
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"SetPixelFormat({format}) failed (Win32 error {error}). A window's pixel format "
                    + "can only be set once, so this window was already claimed by another API.");
            }
        }

        /// <summary>
        /// Binds this context to a device context. False when it could not be bound.
        /// </summary>
        /// <remarks>
        /// Non-throwing because the common failure is entirely expected: a viewport disposes its
        /// swap chain from <c>OnHandleDestroyed</c>, by which point the HWND is gone and its device
        /// context is invalid, so <c>wglMakeCurrent</c> returns ERROR_INVALID_HANDLE. Throwing there
        /// escapes through WndProc as an unhandled exception and puts a modal dialog in front of the
        /// user while they are only closing a window.
        /// </remarks>
        public bool TryMakeCurrent(IntPtr hdc)
        {
            if (_disposed || _context == IntPtr.Zero)
            {
                return false;
            }

            IntPtr target = hdc != IntPtr.Zero ? hdc : _hiddenDc;
            if (target == IntPtr.Zero)
            {
                return false;
            }

            if (Wgl.wglGetCurrentContext() == _context && Wgl.wglGetCurrentDC() == target)
            {
                return true;
            }

            return Wgl.wglMakeCurrent(target, _context);
        }

        /// <summary>
        /// Binds this context for rendering, where a failure genuinely is a defect worth surfacing.
        /// </summary>
        public void MakeCurrent(IntPtr hdc)
        {
            if (!TryMakeCurrent(hdc))
            {
                throw new InvalidOperationException(
                    $"wglMakeCurrent failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }
        }

        /// <summary>Returns to the hidden window so no viewport DC is held current.</summary>
        /// <remarks>Best-effort: used on teardown paths, which must not throw.</remarks>
        public bool MakeHiddenCurrent() => TryMakeCurrent(_hiddenDc);

        private static void Bootstrap()
        {
            if (_bootstrapped)
            {
                return;
            }

            IntPtr window = CreateHiddenWindow();
            IntPtr dc = IntPtr.Zero;
            IntPtr legacy = IntPtr.Zero;

            try
            {
                dc = Wgl.GetDC(window);
                if (dc == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Could not obtain a device context for the bootstrap window.");
                }

                ApplyPixelFormat(dc);

                legacy = Wgl.wglCreateContext(dc);
                if (legacy == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        $"wglCreateContext failed (Win32 error {Marshal.GetLastWin32Error()}). "
                        + "This machine has no usable OpenGL driver.");
                }

                if (!Wgl.wglMakeCurrent(dc, legacy))
                {
                    throw new InvalidOperationException(
                        $"wglMakeCurrent failed for the bootstrap context (Win32 error {Marshal.GetLastWin32Error()}).");
                }

                IntPtr address = Wgl.wglGetProcAddress("wglCreateContextAttribsARB");
                if (address != IntPtr.Zero)
                {
                    _createContextAttribs =
                        Marshal.GetDelegateForFunctionPointer<CreateContextAttribsArb>(address);
                }

                _bootstrapped = true;
            }
            finally
            {
                Wgl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                if (legacy != IntPtr.Zero) Wgl.wglDeleteContext(legacy);
                if (dc != IntPtr.Zero) Wgl.ReleaseDC(window, dc);
                if (window != IntPtr.Zero) Wgl.DestroyWindow(window);
            }
        }

        private static IntPtr CreateCoreContext(IntPtr hdc, out int major, out int minor)
        {
            if (_createContextAttribs == null)
            {
                throw new InvalidOperationException(
                    "WGL_ARB_create_context is unavailable, so no core-profile context can be created. "
                    + "Genesis requires OpenGL 4.5 or newer.");
            }

            bool debug = IsDebugRequested();
            int* attributes = stackalloc int[9];
            attributes[4] = Wgl.ContextProfileMaskArb;
            attributes[5] = Wgl.ContextCoreProfileBitArb;
            attributes[6] = Wgl.ContextFlagsArb;
            attributes[7] = debug ? Wgl.ContextDebugBitArb : 0;
            attributes[8] = 0;

            foreach ((int wantMajor, int wantMinor) in DesiredVersions)
            {
                attributes[0] = Wgl.ContextMajorVersionArb;
                attributes[1] = wantMajor;
                attributes[2] = Wgl.ContextMinorVersionArb;
                attributes[3] = wantMinor;

                IntPtr context = _createContextAttribs(hdc, IntPtr.Zero, attributes);
                if (context != IntPtr.Zero)
                {
                    major = wantMajor;
                    minor = wantMinor;
                    return context;
                }
            }

            throw new InvalidOperationException(
                "No OpenGL 4.5 or 4.6 core-profile context could be created on this adapter.");
        }

        private static IntPtr CreateHiddenWindow()
        {
            // STATIC is a predefined class, so no window class has to be registered or later
            // unregistered. The window is never shown; it exists only to own a device context.
            IntPtr window = Wgl.CreateWindowExA(
                0, "STATIC", "GenesisOpenGL", 0, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Could not create the hidden OpenGL window (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            return window;
        }

        private static bool IsDebugRequested() =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GENESIS_GL_DEBUG"));

        private void EnableDebugOutput()
        {
            Api.Enable(EnableCap.DebugOutput);
            Api.Enable(EnableCap.DebugOutputSynchronous);
            Api.DebugMessageCallback(OnDebugMessage, null);
            RenderLog.Line("[OpenGL] Debug output enabled (GENESIS_GL_DEBUG).");
        }

        private static void OnDebugMessage(
            GLEnum source, GLEnum type, int id, GLEnum severity, int length, IntPtr message, IntPtr userParam)
        {
            if (severity == GLEnum.DebugSeverityNotification)
            {
                return;
            }

            string text = message != IntPtr.Zero
                ? Marshal.PtrToStringAnsi(message, length)
                : "(no message)";
            RenderLog.Line($"[OpenGL] {severity} {type} ({id}): {text}");
        }

        private string QueryString(uint name)
        {
            byte* value = Wgl.GlGetString(name);
            if (value == null)
            {
                return "Unknown";
            }

            int length = 0;
            while (value[length] != 0) length++;
            return Encoding.ASCII.GetString(value, length);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Wgl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);

            if (_context != IntPtr.Zero)
            {
                Wgl.wglDeleteContext(_context);
                _context = IntPtr.Zero;
            }

            if (_hiddenDc != IntPtr.Zero)
            {
                Wgl.ReleaseDC(_hiddenWindow, _hiddenDc);
                _hiddenDc = IntPtr.Zero;
            }

            if (_hiddenWindow != IntPtr.Zero)
            {
                Wgl.DestroyWindow(_hiddenWindow);
                _hiddenWindow = IntPtr.Zero;
            }

            _nativeContext?.Dispose();
            _nativeContext = null;
        }

        /// <summary>Lets Silk.NET resolve GL entry points through WGL.</summary>
        private sealed class WglNativeContext : INativeContext
        {
            private readonly IntPtr _module;

            public WglNativeContext(IntPtr module) => _module = module;

            public nint GetProcAddress(string proc, int? slot = null)
            {
                nint address = Wgl.GetAnyProcAddress(_module, proc);
                if (address == 0)
                {
                    throw new EntryPointNotFoundException($"OpenGL entry point '{proc}' was not found.");
                }

                return address;
            }

            public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
            {
                addr = Wgl.GetAnyProcAddress(_module, proc);
                return addr != 0;
            }

            public void Dispose()
            {
            }
        }
    }
}
