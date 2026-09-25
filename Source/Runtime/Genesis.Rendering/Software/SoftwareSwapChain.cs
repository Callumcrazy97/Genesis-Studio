using System;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Software
{
    public sealed class SoftwareSwapChain : IGpuSwapChain
    {
        private readonly IntPtr _hwnd;
        private readonly Action<int, int> _resized;
        private int _width;
        private int _height;
        private bool _disposed;
        private byte[] _backBuffer = Array.Empty<byte>();

        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern int StretchDIBits(
            IntPtr hdc, int xDest, int yDest, int DestWidth, int DestHeight,
            int xSrc, int ySrc, int SrcWidth, int SrcHeight,
            byte[] lpBits, ref BITMAPINFO lpbmi, uint iUsage, uint rop);

        public IntPtr WindowHandle => _hwnd;
        public int Width => _width;
        public int Height => _height;
        public GpuFormat Format => GpuFormat.B8G8R8A8UNorm;
        public bool IsReady => !_disposed && _width > 0 && _height > 0 && _hwnd != IntPtr.Zero;
        public GpuTextureHandle ColorTexture { get; internal set; } = GpuTextureHandle.Invalid;
        public GpuTextureHandle DepthTexture { get; internal set; } = GpuTextureHandle.Invalid;

        public SoftwareSwapChain(IntPtr hwnd, int width, int height, Action<int, int> resized)
        {
            _hwnd = hwnd;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _resized = resized;
            _backBuffer = new byte[_width * _height * 4];
        }

        public void BlitPixels(ReadOnlySpan<byte> rgbaPixels, int width, int height)
        {
            if (_disposed || rgbaPixels.IsEmpty) return;

            int targetLen = width * height * 4;
            if (_backBuffer.Length != targetLen)
                _backBuffer = new byte[targetLen];

            rgbaPixels.CopyTo(_backBuffer);
        }

        public GpuTextureHandle AcquireBackBuffer() => ColorTexture;

        public void Present(bool vsync)
        {
            if (_disposed || _hwnd == IntPtr.Zero || _backBuffer.Length == 0) return;

            IntPtr hdc = GetDC(_hwnd);
            if (hdc == IntPtr.Zero) return;

            try
            {
                var bmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = _width,
                        biHeight = -_height, // Top-down
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0, // BI_RGB
                    }
                };

                StretchDIBits(hdc, 0, 0, _width, _height, 0, 0, _width, _height, _backBuffer, ref bmi, 0, 0x00CC0020); // SRCCOPY
            }
            finally
            {
                ReleaseDC(_hwnd, hdc);
            }
        }

        public bool Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            _width = width;
            _height = height;
            _backBuffer = new byte[_width * _height * 4];
            _resized?.Invoke(_width, _height);
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
