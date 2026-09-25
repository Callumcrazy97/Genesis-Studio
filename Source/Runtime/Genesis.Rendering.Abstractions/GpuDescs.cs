using System;

namespace Genesis.Rendering.Abstractions
{
    public struct GpuBufferDesc
    {
        public int            SizeBytes;
        public GpuBufferUsage Usage;
        public GpuBindFlags   BindFlags;
        /// <summary>Element stride for a StructuredBuffer; 0 for every other kind.</summary>
        public int            StructureStride;
        /// <summary>Optional name for graphics-debugger captures.</summary>
        public string         DebugName;
    }

    public struct GpuTextureDesc
    {
        public int        Width;
        public int        Height;
        public int        MipLevels;      // 0 = full chain
        public int        ArrayLayers;    // >1 for a texture array (the atlas at t4)
        public GpuFormat  Format;
        public GpuBindFlags BindFlags;
        public GpuBufferUsage Usage;
        public string     DebugName;
    }

    public struct GpuSamplerDesc
    {
        public GpuFilter      Filter;
        public GpuAddressMode AddressU;
        public GpuAddressMode AddressV;
        public GpuAddressMode AddressW;
        public int            MaxAnisotropy;
        /// <summary>
        /// Anything but <see cref="GpuCompare.Never"/> makes this a comparison sampler — HLSL's
        /// SamplerComparisonState at s1, used by the shadow cascades. On OpenGL this becomes
        /// GL_TEXTURE_COMPARE_MODE on the sampler object rather than anything in the shader.
        /// </summary>
        public GpuCompare     CompareOp;
        public float          BorderColorR, BorderColorG, BorderColorB, BorderColorA;
        public string         DebugName;
    }

    /// <summary>
    /// Backend-compatible shader binaries for one graphics or compute program.
    /// </summary>
    /// <remarks>
    /// The rendering layer owns compilation and supplies the representation required by the active
    /// backend: DXBC/DXIL for Direct3D, SPIR-V for Vulkan, and translated GLSL/source for OpenGL.
    /// Keeping compilation outside <see cref="IGpuDevice"/> lets the Phase 5 shader toolchain evolve
    /// independently while resource ownership and binding stay identical on every backend.
    /// </remarks>
    public struct GpuShaderProgramDesc
    {
        /// <summary>Representation of every non-empty stage in this program.</summary>
        public GpuShaderBinaryFormat BinaryFormat;
        public byte[] VertexShader;
        public byte[] PixelShader;
        public byte[] ComputeShader;
        public string DebugName;
    }

    public struct GpuVertexElement
    {
        /// <summary>Semantic name as written in the HLSL input struct (POSITION, NORMAL, …).</summary>
        public string    Semantic;
        public int       SemanticIndex;
        public GpuFormat Format;
        public int       Slot;
        public int       OffsetBytes;
        /// <summary>0 = per-vertex; 1 = per-instance.</summary>
        public int       InstanceStepRate;
    }

    public struct GpuVertexLayoutDesc
    {
        public GpuVertexElement[] Elements;
        public int[]              SlotStrides;
        public string             DebugName;
    }

    public struct GpuColorAttachmentDesc
    {
        public GpuTextureHandle Texture;
        public GpuFormat        Format;
    }

    public struct GpuRenderTargetDesc
    {
        public int       Width;
        public int       Height;
        /// <summary>
        /// Colour attachments, in binding order. More than one is a genuine MRT pass — the main
        /// forward pass writes scene colour and the fog-skip mask together.
        /// </summary>
        public GpuFormat[] ColorFormats;
        /// <summary><see cref="GpuFormat.Unknown"/> for a colour-only target.</summary>
        public GpuFormat DepthFormat;
        /// <summary>
        /// Whether the depth attachment must be bindable as a texture afterwards. The screen-space
        /// fog pass reconstructs world position from depth, so it needs this; most targets do not,
        /// and on Vulkan it costs an extra layout transition at EndRenderPass.
        /// </summary>
        public bool      DepthSampleable;
        public string    DebugName;
    }

    /// <summary>One colour attachment's load/store behaviour and clear value for a pass.</summary>
    public struct GpuAttachmentAction
    {
        public GpuLoadAction  Load;
        public GpuStoreAction Store;
        public float          ClearR, ClearG, ClearB, ClearA;

        public static GpuAttachmentAction Clear(float r, float g, float b, float a = 1f) =>
            new GpuAttachmentAction
            {
                Load = GpuLoadAction.Clear,
                Store = GpuStoreAction.Store,
                ClearR = r, ClearG = g, ClearB = b, ClearA = a,
            };

        public static GpuAttachmentAction Keep() =>
            new GpuAttachmentAction { Load = GpuLoadAction.Load, Store = GpuStoreAction.Store };
    }

    /// <summary>
    /// Everything a pass needs, declared up front. This is the one shape change the renderer sees
    /// versus today's immediate OMSetRenderTargets/Clear calls, and it exists because Vulkan needs
    /// the render-pass signature to build a pipeline — a purely immediate interface cannot be
    /// implemented there without guessing.
    /// </summary>
    public struct GpuRenderPassDesc
    {
        /// <summary>Invalid targets the swap chain's back buffer.</summary>
        public GpuRenderTargetHandle Target;
        /// <summary>
        /// Optional depth attachment override. This lets a post-processing MRT render against the
        /// viewport's existing depth texture without copying it. Invalid uses the target's own
        /// depth attachment (or the swap chain depth for a back-buffer pass).
        /// </summary>
        public GpuTextureHandle      DepthTexture;
        public GpuAttachmentAction[] ColorActions;
        public GpuAttachmentAction   DepthAction;
        public bool                  HasDepth;
        public string                DebugName;

        public static GpuRenderPassDesc BackBuffer(GpuAttachmentAction color, GpuAttachmentAction depth) =>
            new GpuRenderPassDesc
            {
                Target = GpuRenderTargetHandle.Invalid,
                ColorActions = new[] { color },
                DepthAction = depth,
                HasDepth = true,
            };
    }
}
