using System;
using Genesis.Rendering.Abstractions;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>Presents <see cref="VulkanSwapChain"/> through the backend-neutral interface.</summary>
    /// <remarks>
    /// Acquisition happens at frame start and presentation at frame end, because Vulkan's acquire
    /// signals a semaphore the frame's submission must wait on — so the two cannot be separated the
    /// way <see cref="IGpuSwapChain.Present"/> implies. The adapter holds that ordering so the
    /// renderer keeps its backend-neutral shape.
    /// </remarks>
    internal sealed unsafe class VulkanSwapChainAdapter : IGpuSwapChain
    {
        private readonly VulkanGpuDevice _device;
        private readonly VulkanSwapChain _inner;

        private GpuTextureHandle _backBuffer = GpuTextureHandle.Invalid;
        private GpuTextureHandle _depth = GpuTextureHandle.Invalid;
        private bool _needsRecreate;
        private bool _disposed;

        public VulkanSwapChainAdapter(VulkanGpuDevice device, VulkanSwapChain inner)
        {
            _device = device;
            _inner = inner;
        }

        internal VulkanSwapChain Inner => _inner;

        internal bool HasAcquiredImage => _inner.AcquiredIndex >= 0;

        public int Width => _inner.Width;

        public int Height => _inner.Height;

        public bool IsReady => !_disposed && _inner.IsReady;

        /// <summary>
        /// The swap chain's depth attachment, published on demand.
        /// </summary>
        /// <remarks>
        /// Resolved lazily rather than in the constructor because the renderer reads it before the
        /// first pass of a frame, and a resize replaces the underlying image — the wrapper
        /// re-points itself when it sees a different one.
        /// </remarks>
        public GpuTextureHandle DepthTexture
        {
            get
            {
                if (!IsReady) return GpuTextureHandle.Invalid;
                _depth = _device.WrapSwapChainDepthImage(
                    _depth, _inner.DepthImage, _inner.Width, _inner.Height, VulkanSwapChain.DepthFormat);
                return _depth;
            }
        }

        public bool Resize(int width, int height)
        {
            if (_disposed) return false;

            _device.WaitIdle();

            // These handles own additional image views even though they do not own the images.
            // Drop the views before VulkanSwapChain destroys the old swap-chain images.
            _device.ReleaseSwapChainTextureWrapper(_backBuffer);
            _device.ReleaseSwapChainTextureWrapper(_depth);
            _backBuffer = GpuTextureHandle.Invalid;
            _depth = GpuTextureHandle.Invalid;
            _device.ClearPendingPresentSemaphore();
            AcquiredSemaphore = default;
            AcquiredThisFrame = false;
            _depthLayout = ImageLayout.Undefined;

            RenderPass previous = _inner.RenderPass;
            RenderPass previousColorOnly = _inner.ColorOnlyRenderPass;
            bool rebuilt = _inner.Recreate(width, height);

            // Pipelines are only compatible with the pass they were built against, and a resize
            // creates a new one; a draw through a stale pipeline is undefined rather than an error.
            if (previous.Handle != 0) _device.OnSwapChainRecreated(previous);
            if (previousColorOnly.Handle != 0) _device.OnSwapChainRecreated(previousColorOnly);
            _needsRecreate = false;
            return rebuilt;
        }

        /// <summary>Rebuilds a surface invalidated by presentation, before frame recording starts.</summary>
        internal void EnsureReadyForFrame()
        {
            if (_needsRecreate)
            {
                Resize(Math.Max(1, _inner.Width), Math.Max(1, _inner.Height));
            }
        }

        internal VkSemaphore AcquiredSemaphore { get; private set; }
        internal VkSemaphore RenderFinishedSemaphore => _inner.RenderFinishedSemaphore;

        /// <summary>
        /// True when <see cref="AcquireForFrame"/> actually took an image this frame, rather than
        /// reusing one a previous frame acquired and never presented.
        /// </summary>
        /// <remarks>
        /// The frame's submission may only wait on the acquire semaphore when an acquire actually
        /// signalled it — waiting on a semaphore nothing will signal deadlocks the queue.
        /// </remarks>
        internal bool AcquiredThisFrame { get; private set; }

        /// <summary>Acquires this frame's image, rebuilding the surface if the window changed.</summary>
        /// <remarks>
        /// Re-entrancy matters here in a way it does not on D3D. Frame readback ends a frame and
        /// immediately begins another *without presenting* — a shortcut that is free under
        /// flip-discard but not under Vulkan, where every acquire must be matched by a present.
        /// Acquiring again while still holding an image takes a second one from a pool of three and
        /// never gives the first back; the third capture in a row then blocks in
        /// <c>vkAcquireNextImageKHR</c> forever. Reusing the image we already hold is both correct
        /// and what the caller actually wants: it is still drawing into the same frame.
        /// </remarks>
        internal void AcquireForFrame(VkSemaphore signal)
        {
            if (!IsReady) return;

            // Recreating here is unsafe: VulkanGpuDevice.BeginFrame has already opened its command
            // buffer. EnsureReadyForFrame handles this at the between-frame boundary instead.
            if (_needsRecreate) return;

            if (HasAcquiredImage)
            {
                // Still holding an unpresented image. Keep the semaphore the acquiring frame
                // signalled and tell EndFrame not to wait on it a second time.
                AcquiredThisFrame = false;
                return;
            }

            AcquiredSemaphore = signal;
            AcquiredThisFrame = _inner.TryAcquire(signal, out bool needsRecreate);
            if (!AcquiredThisFrame)
            {
                _needsRecreate = needsRecreate;
            }
        }

        /// <summary>Moves the acquired image into a layout the render pass can draw to.</summary>
        internal void PrepareForRendering(CommandBuffer cmd, bool includeDepth)
        {
            if (!HasAcquiredImage) return;

            int index = _inner.AcquiredIndex;
            _device.TransitionSwapChainImage(
                cmd, _inner.AcquiredImage, _inner.LayoutAt(index), ImageLayout.ColorAttachmentOptimal,
                ImageAspectFlags.ColorBit);
            _inner.SetLayout(index, ImageLayout.ColorAttachmentOptimal);

            if (!includeDepth) return;

            // The depth-bearing render pass declares depth already in its optimal layout, so the
            // image has to be moved there before the first pass that reads that declaration.
            _device.TransitionSwapChainImage(
                cmd, _inner.DepthImage, _depthLayout, ImageLayout.DepthStencilAttachmentOptimal,
                ImageAspectFlags.DepthBit);
            _depthLayout = ImageLayout.DepthStencilAttachmentOptimal;
        }

        private ImageLayout _depthLayout = ImageLayout.Undefined;

        /// <summary>Moves the acquired image into the layout presentation requires.</summary>
        internal void PrepareForPresent(CommandBuffer cmd)
        {
            if (!HasAcquiredImage) return;

            int index = _inner.AcquiredIndex;
            _device.TransitionSwapChainImage(
                cmd, _inner.AcquiredImage, _inner.LayoutAt(index), ImageLayout.PresentSrcKhr,
                ImageAspectFlags.ColorBit);
            _inner.SetLayout(index, ImageLayout.PresentSrcKhr);
        }

        public void Present(bool vsync)
        {
            if (!HasAcquiredImage) return;

            VkSemaphore wait = _device.PendingPresentSemaphore;
            if (wait.Handle == 0)
            {
                throw new InvalidOperationException(
                    "Vulkan presentation has no render-finished semaphore; the frame was not submitted.");
            }

            bool presented = _inner.Present(wait, out bool needsRecreate);
            _device.ClearPendingPresentSemaphore();
            AcquiredThisFrame = false;
            if (!presented)
            {
                _needsRecreate = needsRecreate;
            }
        }

        /// <summary>
        /// The back buffer as a texture. Vulkan owns the image, so the wrapper never destroys it.
        /// </summary>
        public GpuTextureHandle AcquireBackBuffer()
        {
            if (!HasAcquiredImage) return GpuTextureHandle.Invalid;

            _backBuffer = _device.WrapSwapChainImage(
                _backBuffer, _inner.AcquiredImage, _inner.Width, _inner.Height);
            return _backBuffer;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _device.WaitIdle();
            _device.ReleaseSwapChainTextureWrapper(_backBuffer);
            _device.ReleaseSwapChainTextureWrapper(_depth);
            _backBuffer = GpuTextureHandle.Invalid;
            _depth = GpuTextureHandle.Invalid;
            _device.ClearPendingPresentSemaphore();
            _device.ForgetSwapChain(this);
            _inner.Dispose();
        }
    }
}
