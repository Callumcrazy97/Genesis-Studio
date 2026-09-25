using System;

namespace Genesis.Rendering.Abstractions
{
    /// <summary>
    /// The GPU abstraction every backend implements. One renderer runs on every backend.
    /// </summary>
    /// <remarks>
    /// <para><b>Immediate-mode, not record-then-submit.</b> ForwardRenderer is a large immediate
    /// renderer; a record-then-submit interface would turn porting it
    /// from mechanical (swap one call for another) into semantic (restructure the control flow),
    /// which is the big-bang rewrite this design exists to avoid. Vulkan implements this correctly
    /// by owning the frame's command buffer and recording as calls arrive. The only thing given up
    /// is multithreaded recording, which Genesis cannot do anyway — every viewport serialises to
    /// the UI thread under a render lock.</para>
    ///
    /// <para><b>Two contracts make that work.</b> Pipeline state is set immediately but resolved at
    /// draw time (see <see cref="GpuPipelineKey"/>), and dynamic constant buffers follow the
    /// documented rule on <see cref="UpdateConstantBuffer{T}"/>. Neither changes the shape of the
    /// calling code.</para>
    ///
    /// <para><b>Binding slots are HLSL register indices.</b> <c>SetConstantBuffer(stage, 2, h)</c>
    /// means <c>b2</c> on every backend. D3D11 passes it through; Vulkan applies fixed DXC shift
    /// constants; OpenGL maps texture registers to units one-to-one. There is no reflection at bind
    /// time, ever — the full register surface is small and non-overlapping (b0..b4, t0..t20,
    /// s0..s1), so the mapping is arithmetic rather than a lookup.</para>
    /// </remarks>
    public interface IGpuDevice : IDisposable
    {
        // ── Identity ────────────────────────────────────────────────────────────
        string                BackendName        { get; }
        string                AdapterName        { get; }
        GpuCapabilities       Capabilities       { get; }
        GpuShaderBinaryFormat ShaderBinaryFormat { get; }

        IGpuSwapChain CreateSwapChain(IntPtr windowHandle, int width, int height);

        // ── Frame envelope ──────────────────────────────────────────────────────
        // Vulkan acquires an image and begins its command buffer here, and ends/submits in EndFrame.
        void BeginFrame();
        void EndFrame();
        void WaitIdle();

        // ── Buffers ─────────────────────────────────────────────────────────────
        GpuBufferHandle CreateBuffer(in GpuBufferDesc desc, ReadOnlySpan<byte> initialData);
        void UpdateBuffer(GpuBufferHandle handle, ReadOnlySpan<byte> data, int byteOffset = 0);

        /// <summary>
        /// Overwrites a dynamic constant buffer.
        /// </summary>
        /// <remarks>
        /// The written data is valid for every draw issued between this call and the next update of
        /// the same buffer. Backends may sub-allocate, so callers must not assume an earlier draw
        /// sees a later write. This is what lets Vulkan use a per-frame ring with dynamic offsets
        /// where D3D11 uses Map(WriteDiscard) — the renderer rewrites its per-draw constants dozens
        /// of times a frame and its calling shape does not change at all.
        /// </remarks>
        void UpdateConstantBuffer<T>(GpuBufferHandle handle, in T data) where T : unmanaged;

        bool TryMapDiscard(GpuBufferHandle handle, out Span<byte> span, int byteCount = 0);
        void Unmap(GpuBufferHandle handle);
        void ReleaseBuffer(GpuBufferHandle handle);

        // ── Textures and samplers ───────────────────────────────────────────────
        GpuTextureHandle CreateTexture(in GpuTextureDesc desc, ReadOnlySpan<byte> initialData);
        void UpdateTexture(
            GpuTextureHandle handle, int x, int y, int width, int height,
            ReadOnlySpan<byte> data, int arraySlice = 0);
        void ReleaseTexture(GpuTextureHandle handle);

        GpuSamplerHandle CreateSampler(in GpuSamplerDesc desc);
        void ReleaseSampler(GpuSamplerHandle handle);

        // ── Render targets ──────────────────────────────────────────────────────
        GpuRenderTargetHandle CreateRenderTarget(in GpuRenderTargetDesc desc);
        GpuTextureHandle GetRenderTargetTexture(GpuRenderTargetHandle handle, int attachment = 0);
        /// <summary>Invalid unless the target was created with DepthSampleable.</summary>
        GpuTextureHandle GetRenderTargetDepthTexture(GpuRenderTargetHandle handle);
        void ReleaseRenderTarget(GpuRenderTargetHandle handle);

        // ── Passes ──────────────────────────────────────────────────────────────
        void BeginRenderPass(in GpuRenderPassDesc desc);
        void EndRenderPass();

        /// <summary>
        /// Drops every binding that could still reference a swap chain's buffers, so it can resize.
        /// </summary>
        /// <remarks>
        /// A surface cannot be resized while anything still holds one of its back buffers. D3D11
        /// enforces this by returning <c>DXGI_ERROR_INVALID_CALL</c> from <c>ResizeBuffers</c>
        /// — permanently, for the life of the surface, and with the failure hidden behind a
        /// stretched stale frame rather than surfaced as an error. D3D12 additionally requires
        /// every frame in flight to have completed. Callers invoke this immediately before
        /// <see cref="IGpuSwapChain.Resize"/>; a backend with nothing to release implements it as a
        /// no-op.
        /// </remarks>
        void UnbindRenderTargets();
        void SetViewport(float x, float y, float width, float height, float minZ = 0f, float maxZ = 1f);
        void SetScissor(int x, int y, int width, int height);

        // ── Pipeline state (immediate setters, resolved at draw) ────────────────
        void SetShaderProgram(GpuShaderProgramHandle handle);
        void SetVertexLayout(GpuVertexLayoutHandle handle);
        void SetPrimitiveTopology(GpuPrimitiveTopology topology);
        void SetBlendState(in GpuBlendState state);
        void SetDepthState(in GpuDepthState state);
        void SetRasterState(in GpuRasterState state);

        GpuShaderProgramHandle CreateShaderProgram(in GpuShaderProgramDesc desc);
        void ReleaseShaderProgram(GpuShaderProgramHandle handle);

        /// <summary>
        /// Creates a vertex layout compatible with <paramref name="program"/>'s vertex shader.
        /// Direct3D validates this against shader bytecode; Vulkan and OpenGL may use the program
        /// only for validation while retaining the same backend-neutral call shape.
        /// </summary>
        GpuVertexLayoutHandle CreateVertexLayout(
            in GpuVertexLayoutDesc desc,
            GpuShaderProgramHandle program);
        void ReleaseVertexLayout(GpuVertexLayoutHandle handle);

        // ── Bindings — the slot number IS the HLSL register index ───────────────
        void SetConstantBuffer(GpuShaderStage stage, int bRegister, GpuBufferHandle handle);
        /// <summary>An Invalid handle binds the backend's safe fallback texture.</summary>
        void SetTexture(GpuShaderStage stage, int tRegister, GpuTextureHandle handle);
        /// <summary>
        /// Binds no texture at the slot. This is distinct from <see cref="SetTexture"/> with an
        /// Invalid handle, which deliberately binds the backend's safe fallback texture.
        /// </summary>
        void ClearTexture(GpuShaderStage stage, int tRegister);
        void SetStructuredBuffer(GpuShaderStage stage, int tRegister, GpuBufferHandle handle);
        void SetSampler(GpuShaderStage stage, int sRegister, GpuSamplerHandle handle);
        void SetVertexBuffer(int slot, GpuBufferHandle handle, int stride, int offset = 0);
        void SetIndexBuffer(GpuBufferHandle handle, GpuIndexFormat format, int offset = 0);

        // ── Draws ───────────────────────────────────────────────────────────────
        void Draw(int vertexCount, int startVertex = 0);
        void DrawIndexed(int indexCount, int startIndex = 0, int baseVertex = 0);
        void DrawIndexedInstanced(
            int indexCountPerInstance, int instanceCount,
            int startIndex = 0, int baseVertex = 0, int startInstance = 0);

        // ── Timestamps ──────────────────────────────────────────────────────────
        // Ring-buffered and non-stalling: a scope's result arrives several frames later, and asking
        // for it early returns false rather than blocking the pipeline.
        GpuQueryHandle BeginTimestampScope();
        void EndTimestampScope(GpuQueryHandle handle);
        bool TryResolveTimestamp(GpuQueryHandle handle, out double milliseconds);

        // ── Readback ────────────────────────────────────────────────────────────
        /// <summary>
        /// Copies a texture into tightly-packed BGRA. Backends whose framebuffer origin is
        /// bottom-left flip rows so the result is always top-down — every visual baseline in the
        /// headless suite depends on this being consistent.
        /// </summary>
        bool TryReadTexture(GpuTextureHandle handle, out int width, out int height, out byte[] bgra);
    }
}
