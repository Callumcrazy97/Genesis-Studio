using System;

namespace Genesis.Rendering.Abstractions
{
    /// <summary>
    /// Texture and vertex-attribute formats. Only what the renderer actually uses — this list grows
    /// on demand rather than mirroring every format the three APIs happen to share.
    /// </summary>
    public enum GpuFormat
    {
        Unknown = 0,

        // Colour
        R8G8B8A8UNorm,
        R8G8B8A8UNormSrgb,
        B8G8R8A8UNorm,         // the swap-chain format; byte-identical to GDI+ Format32bppArgb
        BC5UNorm,
        BC7UNorm,
        BC7UNormSrgb,
        R16G16B16A16Float,
        R11G11B10Float,
        R8UNorm,
        R16Float,
        R32Float,

        // Depth
        D32Float,
        D24UNormS8UInt,

        // Vertex attributes
        R32Float2,
        R32Float3,
        R32Float4,
        R32UInt,
    }

    [Flags]
    public enum GpuBufferUsage
    {
        /// <summary>Written once at creation, never again — the fastest path on every backend.</summary>
        Immutable = 0,
        /// <summary>Rewritten by the CPU, typically every frame (constant buffers, instance data).</summary>
        Dynamic = 1,
        /// <summary>Written by the GPU and read back by the CPU.</summary>
        Staging = 2,
        /// <summary>Device-local, shader-writable storage; no CPU shadow or mapping.</summary>
        Gpu = 4,
    }

    [Flags]
    public enum GpuBindFlags
    {
        None = 0,
        VertexBuffer = 1,
        IndexBuffer = 2,
        ConstantBuffer = 4,
        /// <summary>Readable by a shader: a texture, or a StructuredBuffer/SSBO.</summary>
        ShaderResource = 8,
        RenderTarget = 16,
        DepthStencil = 32,
        /// <summary>Element-addressed buffer (HLSL StructuredBuffer, GLSL SSBO, Vulkan STORAGE_BUFFER).</summary>
        StructuredBuffer = 64,
        UnorderedAccess = 128,
        IndirectArguments = 256,
    }

    public enum GpuIndexFormat
    {
        UInt16 = 0,
        /// <summary>
        /// Available from day one even though the render contract is still ushort-only, so widening
        /// that contract later is a change in one place rather than a change in every backend.
        /// </summary>
        UInt32 = 1,
    }

    public enum GpuCompare
    {
        Never = 0,
        Less,
        Equal,
        LessEqual,
        Greater,
        NotEqual,
        GreaterEqual,
        Always,
    }

    public enum GpuBlendFactor
    {
        Zero = 0,
        One,
        SrcColor,
        InvSrcColor,
        SrcAlpha,
        InvSrcAlpha,
        DestColor,
        InvDestColor,
        DestAlpha,
        InvDestAlpha,
    }

    public enum GpuBlendOp
    {
        Add = 0,
        Subtract,
        ReverseSubtract,
        Min,
        Max,
    }

    public enum GpuCullMode
    {
        None = 0,
        Front,
        Back,
    }

    public enum GpuFillMode
    {
        Solid = 0,
        Wireframe,
    }

    public enum GpuFilter
    {
        Point = 0,
        Linear,
        Anisotropic,
    }

    public enum GpuAddressMode
    {
        Wrap = 0,
        Clamp,
        Mirror,
        Border,
    }

    public enum GpuPrimitiveTopology
    {
        TriangleList = 0,
        TriangleStrip,
        LineList,
        PointList,
    }

    [Flags]
    public enum GpuShaderStage
    {
        None = 0,
        Vertex = 1,
        Pixel = 2,
        Compute = 4,
        All = Vertex | Pixel | Compute,
    }

    /// <summary>
    /// Binary representation consumed by a graphics backend. HLSL remains Genesis's source
    /// language; the shader toolchain produces the representation requested by the active device.
    /// </summary>
    public enum GpuShaderBinaryFormat
    {
        /// <summary>Shader Model 5 bytecode produced by D3DCompiler for Direct3D 11.</summary>
        Dxbc = 0,
        /// <summary>Shader Model 6 DXIL produced by DXC for Direct3D 12.</summary>
        Dxil,
        /// <summary>SPIR-V produced by DXC for Vulkan.</summary>
        SpirV,
        /// <summary>UTF-8 GLSL translated from the canonical SPIR-V representation for OpenGL.</summary>
        GlslUtf8,
    }

    /// <summary>What happens to an attachment's existing contents when a pass begins.</summary>
    public enum GpuLoadAction
    {
        /// <summary>Contents are undefined — cheapest, and correct when the pass covers everything.</summary>
        DontCare = 0,
        Load,
        Clear,
    }

    public enum GpuStoreAction
    {
        /// <summary>Results are discarded (a depth buffer nothing samples afterwards).</summary>
        DontCare = 0,
        Store,
    }
}
