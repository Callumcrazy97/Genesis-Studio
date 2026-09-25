namespace Genesis.Rendering.Abstractions
{
    /// <summary>
    /// The handful of places the three APIs genuinely differ, surfaced so the shared renderer can
    /// absorb them instead of each backend quietly diverging.
    /// </summary>
    /// <remarks>
    /// Kept deliberately small. Anything that can be hidden inside a backend should be — this type
    /// is for differences the renderer must actually reason about, not a feature-level dump.
    /// </remarks>
    public struct GpuCapabilities
    {
        /// <summary>
        /// True when clip-space Z is [0,1] (D3D, Vulkan) rather than [-1,1] (OpenGL's default).
        /// Projections are built with System.Numerics in application code — every editor and the
        /// runtime harness — so rewriting them per backend is out of the question. The GL backend
        /// instead calls glClipControl(GL_UPPER_LEFT, GL_ZERO_TO_ONE) and consumes the identical
        /// matrices; if a context cannot provide that, it refuses to initialise and falls back,
        /// because absent beats subtly wrong.
        /// </summary>
        public bool ClipSpaceZeroToOne;

        /// <summary>
        /// True when framebuffer origin is top-left. GL_UPPER_LEFT makes OpenGL agree, which kills
        /// the whole texture-V-flip bug class at once — at the cost of inverted front-face winding,
        /// absorbed by <see cref="GpuRasterState.FrontCounterClockwise"/> inside the GL backend.
        /// </summary>
        public bool FramebufferOriginTopLeft;

        public bool SupportsStructuredBuffers;
        public bool SupportsUInt32Indices;
        public bool SupportsTimestampQueries;
        public bool SupportsComputeShaders;
        public bool SupportsIndirectDraw;

        public int  MaxTextureArrayLayers;
        public int  MaxAnisotropy;
        public int  MaxColorAttachments;

        /// <summary>
        /// Multiplier applied to shadow depth bias. Depth-bias units are not comparable across
        /// APIs and depth formats, so the shared renderer scales by this rather than each backend
        /// silently rendering different shadow acne. 1.0 on D3D11, so it cannot move the baseline.
        /// </summary>
        public float DepthBiasScale;
    }
}
