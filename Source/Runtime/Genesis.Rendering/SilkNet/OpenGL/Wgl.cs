using System;
using System.Runtime.InteropServices;

namespace Genesis.Rendering.SilkNet.OpenGL
{
    /// <summary>The Win32 and WGL entry points needed to put a GL context on an existing HWND.</summary>
    /// <remarks>
    /// Silk.NET's windowing layer creates its own window through GLFW, which is no use here: Genesis
    /// renders into a WinForms control that already owns its HWND. Driving WGL directly is the only
    /// way to attach a context to a window someone else created.
    /// </remarks>
    internal static unsafe class Wgl
    {
        public const int PfdTypeRgba = 0;
        public const int PfdMainPlane = 0;
        public const uint PfdDoubleBuffer = 0x00000001;
        public const uint PfdDrawToWindow = 0x00000004;
        public const uint PfdSupportOpenGL = 0x00000020;

        // wglCreateContextAttribsARB attribute names.
        public const int ContextMajorVersionArb = 0x2091;
        public const int ContextMinorVersionArb = 0x2092;
        public const int ContextFlagsArb = 0x2094;
        public const int ContextProfileMaskArb = 0x9126;
        public const int ContextCoreProfileBitArb = 0x00000001;
        public const int ContextDebugBitArb = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        public struct PixelFormatDescriptor
        {
            public ushort Size;
            public ushort Version;
            public uint Flags;
            public byte PixelType;
            public byte ColorBits;
            public byte RedBits, RedShift, GreenBits, GreenShift, BlueBits, BlueShift;
            public byte AlphaBits, AlphaShift;
            public byte AccumBits, AccumRedBits, AccumGreenBits, AccumBlueBits, AccumAlphaBits;
            public byte DepthBits, StencilBits, AuxBuffers;
            public byte LayerType, Reserved;
            public uint LayerMask, VisibleMask, DamageMask;
        }

        public const uint GlVendor = 0x1F00;
        public const uint GlRenderer = 0x1F01;
        public const uint GlVersion = 0x1F02;

        [DllImport("opengl32.dll", EntryPoint = "glGetString")]
        public static extern byte* GlGetString(uint name);

        [DllImport("user32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr CreateWindowExA(
            uint exStyle,
            string className,
            string windowName,
            uint style,
            int x, int y, int width, int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int ChoosePixelFormat(IntPtr hdc, PixelFormatDescriptor* descriptor);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool SetPixelFormat(IntPtr hdc, int format, PixelFormatDescriptor* descriptor);

        [DllImport("gdi32.dll")]
        public static extern bool SwapBuffers(IntPtr hdc);

        [DllImport("opengl32.dll", SetLastError = true)]
        public static extern IntPtr wglCreateContext(IntPtr hdc);

        [DllImport("opengl32.dll", SetLastError = true)]
        public static extern bool wglDeleteContext(IntPtr context);

        [DllImport("opengl32.dll", SetLastError = true)]
        public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr context);

        [DllImport("opengl32.dll")]
        public static extern IntPtr wglGetCurrentContext();

        [DllImport("opengl32.dll")]
        public static extern IntPtr wglGetCurrentDC();

        [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
        public static extern IntPtr wglGetProcAddress(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetModuleHandleA(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr LoadLibraryA(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr module, string name);

        /// <summary>
        /// Resolves a GL entry point, trying the extension mechanism before the core export.
        /// </summary>
        /// <remarks>
        /// Neither source alone is sufficient on Windows. <c>opengl32.dll</c> only exports GL 1.1;
        /// everything after that must come from <c>wglGetProcAddress</c>, which in turn returns null
        /// for the GL 1.1 functions. Querying just one silently loses half the API.
        ///
        /// <c>wglGetProcAddress</c> also has a notorious wrinkle: some drivers return 1, 2, 3 or -1
        /// for an unsupported function rather than 0, so those values must be treated as failures.
        /// </remarks>
        public static IntPtr GetAnyProcAddress(IntPtr openglModule, string name)
        {
            IntPtr address = wglGetProcAddress(name);
            if (address != IntPtr.Zero
                && address != (IntPtr)1
                && address != (IntPtr)2
                && address != (IntPtr)3
                && address != (IntPtr)(-1))
            {
                return address;
            }

            return openglModule != IntPtr.Zero ? GetProcAddress(openglModule, name) : IntPtr.Zero;
        }
    }
}
