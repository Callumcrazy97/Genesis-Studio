using System;
using Genesis.Rendering.Abstractions;
using Silk.NET.OpenGL;

namespace Genesis.Rendering.SilkNet.OpenGL
{
    /// <summary>A window's presentable surface, rendered offscreen and blitted on present.</summary>
    /// <remarks>
    /// <para><b>Why not draw straight to the window.</b> OpenGL's default framebuffer is not a
    /// texture and can never be sampled, but <see cref="IGpuSwapChain.AcquireBackBuffer"/> has to
    /// return something the rest of the engine can bind, read back and post-process. Rendering into
    /// an owned colour texture and blitting it to the window in <see cref="Present"/> satisfies all
    /// of that with one extra full-screen copy, and it is what makes the headless capture path work
    /// at all — <c>glReadPixels</c> on the default framebuffer of a hidden window reads undefined
    /// content.</para>
    ///
    /// <para>The depth attachment is a texture rather than a renderbuffer for the same reason: the
    /// screen-space fog pass reconstructs world position by sampling it.</para>
    /// </remarks>
    internal sealed unsafe class OpenGLSwapChain : IGpuSwapChain
    {
        private const GpuFormat ColorGpuFormat = GpuFormat.R8G8B8A8UNorm;
        private const GpuFormat DepthGpuFormat = GpuFormat.D32Float;

        private readonly OpenGLGpuDevice _device;
        private readonly OpenGLRuntime _runtime;
        private readonly IntPtr _window;
        private readonly IntPtr _hdc;

        private uint _framebuffer;
        private GpuTextureHandle _color = GpuTextureHandle.Invalid;
        private GpuTextureHandle _depth = GpuTextureHandle.Invalid;
        private bool _disposed;

        public OpenGLSwapChain(OpenGLGpuDevice device, OpenGLRuntime runtime, IntPtr windowHandle, int width, int height)
        {
            _device = device;
            _runtime = runtime;
            _window = windowHandle;

            _hdc = Wgl.GetDC(windowHandle);
            if (_hdc == IntPtr.Zero)
            {
                throw new InvalidOperationException("Could not obtain a device context for the OpenGL viewport.");
            }

            // Must carry the same pixel format as the context, or wglMakeCurrent will refuse it.
            OpenGLRuntime.ApplyPixelFormat(_hdc);
            _runtime.MakeCurrent(_hdc);

            CreateSurface(Math.Max(1, width), Math.Max(1, height));
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public bool IsReady => !_disposed && Width > 0 && Height > 0 && _framebuffer != 0;

        /// <summary>The framebuffer object every back-buffer pass renders into.</summary>
        public uint Framebuffer => _framebuffer;

        public GpuTextureHandle ColorTexture => _color;

        public GpuTextureHandle DepthTexture => _depth;

        /// <summary>
        /// Binds this surface's device context. False when the window is no longer bindable.
        /// </summary>
        /// <remarks>
        /// Best-effort rather than throwing: a frame can begin while the viewport's window is being
        /// destroyed, and losing that frame is the correct outcome — not a dialog.
        /// </remarks>
        public bool MakeCurrent() => _runtime.TryMakeCurrent(_hdc);

        public bool Resize(int width, int height)
        {
            if (_disposed || width <= 0 || height <= 0)
            {
                return false;
            }

            if (width == Width && height == Height)
            {
                return true;
            }

            if (!_runtime.TryMakeCurrent(_hdc))
            {
                return false;
            }

            DestroySurface();
            CreateSurface(width, height);
            return true;
        }

        public void Present(bool vsync)
        {
            if (!IsReady)
            {
                return;
            }

            GL gl = _runtime.Api;

            // A window being torn down cannot be presented to, and that is not an error worth
            // interrupting the user for — the next frame simply will not happen.
            if (!_runtime.TryMakeCurrent(_hdc))
            {
                return;
            }

            // Resolve the offscreen colour attachment onto the window itself.
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _framebuffer);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            gl.DrawBuffer(DrawBufferMode.Back);

            // The destination Y range is inverted on purpose. Clip control is GL_UPPER_LEFT, so the
            // colour attachment is stored top-down like Direct3D — but the *default* framebuffer is
            // still displayed bottom-up by the window system. A 1:1 blit would therefore put the
            // image's top row along the bottom of the window. Readback needs no such flip, because
            // it reads the attachment directly and never touches the default framebuffer.
            gl.BlitFramebuffer(
                0, 0, Width, Height,
                0, Height, Width, 0,
                (uint)ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);

            _device.SetSwapInterval(vsync);
            Wgl.SwapBuffers(_hdc);
        }

        public GpuTextureHandle AcquireBackBuffer() => _color;

        private void CreateSurface(int width, int height)
        {
            GL gl = _runtime.Api;
            Width = width;
            Height = height;

            _color = _device.CreateSurfaceTexture(width, height, ColorGpuFormat);
            _depth = _device.CreateSurfaceTexture(width, height, DepthGpuFormat);

            _framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _device.TextureName(_color),
                0);
            gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D,
                _device.TextureName(_depth),
                0);

            GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                throw new InvalidOperationException(
                    $"The OpenGL viewport framebuffer is incomplete ({status}) at {width}x{height}.");
            }
        }

        private void DestroySurface()
        {
            GL gl = _runtime.Api;

            if (_framebuffer != 0)
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(_framebuffer);
                _framebuffer = 0;
            }

            if (_color.IsValid) _device.ReleaseTexture(_color);
            if (_depth.IsValid) _device.ReleaseTexture(_depth);
            _color = GpuTextureHandle.Invalid;
            _depth = GpuTextureHandle.Invalid;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Deliberately the hidden window's DC, never this surface's. Disposal is normally
            // reached from the viewport's OnHandleDestroyed — the HWND is already gone by then, so
            // its device context is invalid and wglMakeCurrent fails with ERROR_INVALID_HANDLE.
            // The hidden window outlives every viewport, so the context always has somewhere valid
            // to be current while these GL objects are deleted.
            _runtime.MakeHiddenCurrent();
            DestroySurface();

            if (_hdc != IntPtr.Zero)
            {
                // Harmless on a destroyed window: the DC died with it and this simply returns 0.
                Wgl.ReleaseDC(_window, _hdc);
            }
        }
    }
}
