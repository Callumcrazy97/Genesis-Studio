using System;

namespace Genesis.Rendering.Abstractions
{
    /// <summary>A window's presentable surface.</summary>
    public interface IGpuSwapChain : IDisposable
    {
        int  Width  { get; }
        int  Height { get; }

        /// <summary>False while the surface is minimised, mid-resize, or otherwise not renderable.</summary>
        bool IsReady { get; }

        /// <summary>
        /// Resizes the presentable surface. Must be called between frames, never inside a pass.
        /// Returns false when the backend declined and the previous buffers are still valid — the
        /// caller keeps rendering at the old size rather than losing the surface.
        /// </summary>
        bool Resize(int width, int height);

        void Present(bool vsync);

        /// <summary>
        /// The current back buffer as a texture, for a pass that targets the window directly.
        /// Only valid for the frame it was acquired in — flip-model presentation recycles buffers.
        /// </summary>
        GpuTextureHandle AcquireBackBuffer();

        /// <summary>
        /// The depth buffer as a sampleable texture, or Invalid when the backend cannot provide one.
        /// The screen-space fog pass reconstructs world position from it.
        /// </summary>
        GpuTextureHandle DepthTexture { get; }
    }
}
