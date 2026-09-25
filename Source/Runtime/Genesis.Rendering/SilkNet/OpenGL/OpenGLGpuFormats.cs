using System;
using Genesis.Rendering.Abstractions;
using Silk.NET.OpenGL;

namespace Genesis.Rendering.SilkNet.OpenGL
{
    /// <summary>Translation between the backend-neutral enums and OpenGL values.</summary>
    internal static class OpenGLGpuFormats
    {
        /// <summary>The sized internal format used for storage allocation.</summary>
        public static InternalFormat ToInternal(GpuFormat format) => format switch
        {
            GpuFormat.R8G8B8A8UNorm => InternalFormat.Rgba8,
            GpuFormat.R8G8B8A8UNormSrgb => InternalFormat.Srgb8Alpha8,

            // GL has no BGRA sized internal format. Storage is RGBA8 and the swizzle happens on
            // upload and readback through the pixel format, which is where GL expects it.
            GpuFormat.B8G8R8A8UNorm => InternalFormat.Rgba8,

            // Spelled as the GL token values rather than Silk.NET enum members: the compressed
            // format names differ between Silk versions, the numbers never do.
            GpuFormat.BC5UNorm => (InternalFormat)0x8DBD,        // GL_COMPRESSED_RG_RGTC2
            GpuFormat.BC7UNorm => (InternalFormat)0x8E8C,        // GL_COMPRESSED_RGBA_BPTC_UNORM
            GpuFormat.BC7UNormSrgb => (InternalFormat)0x8E8D,    // GL_COMPRESSED_SRGB_ALPHA_BPTC_UNORM
            GpuFormat.R16G16B16A16Float => InternalFormat.Rgba16f,
            GpuFormat.R11G11B10Float => InternalFormat.R11fG11fB10f,
            GpuFormat.R8UNorm => InternalFormat.R8,
            GpuFormat.R16Float => InternalFormat.R16f,
            GpuFormat.R32Float => InternalFormat.R32f,
            GpuFormat.D32Float => InternalFormat.DepthComponent32f,
            GpuFormat.D24UNormS8UInt => InternalFormat.Depth24Stencil8,
            _ => InternalFormat.Rgba8,
        };

        /// <summary>The client-side channel order for uploads and readback.</summary>
        public static PixelFormat ToPixelFormat(GpuFormat format) => format switch
        {
            GpuFormat.B8G8R8A8UNorm => PixelFormat.Bgra,
            GpuFormat.R8UNorm or GpuFormat.R16Float or GpuFormat.R32Float => PixelFormat.Red,
            GpuFormat.D32Float => PixelFormat.DepthComponent,
            GpuFormat.D24UNormS8UInt => PixelFormat.DepthStencil,
            _ => PixelFormat.Rgba,
        };

        public static PixelType ToPixelType(GpuFormat format) => format switch
        {
            GpuFormat.R16G16B16A16Float or GpuFormat.R16Float => PixelType.HalfFloat,
            GpuFormat.R32Float or GpuFormat.D32Float => PixelType.Float,
            GpuFormat.R11G11B10Float => PixelType.UnsignedInt10f11f11fRev,
            GpuFormat.D24UNormS8UInt => PixelType.UnsignedInt248,
            _ => PixelType.UnsignedByte,
        };

        public static bool IsDepth(GpuFormat format) =>
            format is GpuFormat.D32Float or GpuFormat.D24UNormS8UInt;

        public static bool HasStencil(GpuFormat format) => format is GpuFormat.D24UNormS8UInt;

        public static bool IsBlockCompressed(GpuFormat format) =>
            format is GpuFormat.BC5UNorm or GpuFormat.BC7UNorm or GpuFormat.BC7UNormSrgb;

        /// <summary>Bytes per pixel, or bytes per 4x4 block for a compressed format.</summary>
        public static int BytesPerUnit(GpuFormat format) => format switch
        {
            GpuFormat.R8UNorm => 1,
            GpuFormat.R16Float => 2,
            GpuFormat.R8G8B8A8UNorm or GpuFormat.R8G8B8A8UNormSrgb or GpuFormat.B8G8R8A8UNorm => 4,
            GpuFormat.R11G11B10Float or GpuFormat.R32Float => 4,
            GpuFormat.D32Float or GpuFormat.D24UNormS8UInt => 4,
            GpuFormat.R16G16B16A16Float => 8,
            GpuFormat.BC5UNorm => 16,
            GpuFormat.BC7UNorm or GpuFormat.BC7UNormSrgb => 16,
            GpuFormat.R32Float2 => 8,
            GpuFormat.R32Float3 => 12,
            GpuFormat.R32Float4 => 16,
            GpuFormat.R32UInt => 4,
            _ => 4,
        };

        /// <summary>Unpadded bytes in one row, counting blocks rather than pixels when compressed.</summary>
        public static int RowBytes(GpuFormat format, int width) =>
            IsBlockCompressed(format)
                ? Math.Max(1, (width + 3) / 4) * BytesPerUnit(format)
                : width * BytesPerUnit(format);

        public static int RowCount(GpuFormat format, int height) =>
            IsBlockCompressed(format) ? Math.Max(1, (height + 3) / 4) : height;

        /// <summary>Total bytes for one mip level of a texture.</summary>
        public static int LevelBytes(GpuFormat format, int width, int height) =>
            RowBytes(format, width) * RowCount(format, height);

        // ── Vertex attribute description ────────────────────────────────────────

        public static int ComponentCount(GpuFormat format) => format switch
        {
            GpuFormat.R32Float or GpuFormat.R32UInt or GpuFormat.R8UNorm or GpuFormat.R16Float => 1,
            GpuFormat.R32Float2 => 2,
            GpuFormat.R32Float3 => 3,
            _ => 4,
        };

        public static VertexAttribPointerType ToAttribType(GpuFormat format) => format switch
        {
            GpuFormat.R32Float or GpuFormat.R32Float2 or GpuFormat.R32Float3 or GpuFormat.R32Float4 =>
                VertexAttribPointerType.Float,
            GpuFormat.R16Float => VertexAttribPointerType.HalfFloat,
            GpuFormat.R32UInt => VertexAttribPointerType.UnsignedInt,
            GpuFormat.R8G8B8A8UNorm or GpuFormat.B8G8R8A8UNorm or GpuFormat.R8UNorm =>
                VertexAttribPointerType.UnsignedByte,
            _ => VertexAttribPointerType.Float,
        };

        /// <summary>True when the attribute is an integer that must not be normalised or converted.</summary>
        public static bool IsIntegerAttribute(GpuFormat format) => format is GpuFormat.R32UInt;

        /// <summary>True when fixed-point data should be scaled into 0..1 rather than cast.</summary>
        public static bool IsNormalisedAttribute(GpuFormat format) =>
            format is GpuFormat.R8G8B8A8UNorm or GpuFormat.B8G8R8A8UNorm or GpuFormat.R8UNorm;

        // ── Pipeline state ──────────────────────────────────────────────────────

        public static DepthFunction ToCompare(GpuCompare compare) => compare switch
        {
            GpuCompare.Never => DepthFunction.Never,
            GpuCompare.Less => DepthFunction.Less,
            GpuCompare.Equal => DepthFunction.Equal,
            GpuCompare.LessEqual => DepthFunction.Lequal,
            GpuCompare.Greater => DepthFunction.Greater,
            GpuCompare.NotEqual => DepthFunction.Notequal,
            GpuCompare.GreaterEqual => DepthFunction.Gequal,
            _ => DepthFunction.Always,
        };

        public static BlendingFactor ToBlend(GpuBlendFactor factor) => factor switch
        {
            GpuBlendFactor.Zero => BlendingFactor.Zero,
            GpuBlendFactor.One => BlendingFactor.One,
            GpuBlendFactor.SrcColor => BlendingFactor.SrcColor,
            GpuBlendFactor.InvSrcColor => BlendingFactor.OneMinusSrcColor,
            GpuBlendFactor.SrcAlpha => BlendingFactor.SrcAlpha,
            GpuBlendFactor.InvSrcAlpha => BlendingFactor.OneMinusSrcAlpha,
            GpuBlendFactor.DestColor => BlendingFactor.DstColor,
            GpuBlendFactor.InvDestColor => BlendingFactor.OneMinusDstColor,
            GpuBlendFactor.DestAlpha => BlendingFactor.DstAlpha,
            GpuBlendFactor.InvDestAlpha => BlendingFactor.OneMinusDstAlpha,
            _ => BlendingFactor.One,
        };

        public static BlendEquationModeEXT ToBlendOp(GpuBlendOp op) => op switch
        {
            GpuBlendOp.Add => BlendEquationModeEXT.FuncAdd,
            GpuBlendOp.Subtract => BlendEquationModeEXT.FuncSubtract,
            GpuBlendOp.ReverseSubtract => BlendEquationModeEXT.FuncReverseSubtract,
            GpuBlendOp.Min => BlendEquationModeEXT.Min,
            GpuBlendOp.Max => BlendEquationModeEXT.Max,
            _ => BlendEquationModeEXT.FuncAdd,
        };

        public static PrimitiveType ToTopology(GpuPrimitiveTopology topology) => topology switch
        {
            GpuPrimitiveTopology.TriangleStrip => PrimitiveType.TriangleStrip,
            GpuPrimitiveTopology.LineList => PrimitiveType.Lines,
            GpuPrimitiveTopology.PointList => PrimitiveType.Points,
            _ => PrimitiveType.Triangles,
        };

        public static GLEnum ToAddress(GpuAddressMode mode) => mode switch
        {
            GpuAddressMode.Wrap => GLEnum.Repeat,
            GpuAddressMode.Clamp => GLEnum.ClampToEdge,
            GpuAddressMode.Mirror => GLEnum.MirroredRepeat,
            _ => GLEnum.ClampToBorder,
        };

        /// <summary>Magnification filter, which has no mip component.</summary>
        public static GLEnum ToMagFilter(GpuFilter filter) =>
            filter == GpuFilter.Point ? GLEnum.Nearest : GLEnum.Linear;

        /// <summary>Minification filter, which folds the mip mode into a single enum.</summary>
        public static GLEnum ToMinFilter(GpuFilter filter, bool hasMips)
        {
            if (!hasMips)
            {
                return filter == GpuFilter.Point ? GLEnum.Nearest : GLEnum.Linear;
            }

            return filter == GpuFilter.Point
                ? GLEnum.NearestMipmapNearest
                : GLEnum.LinearMipmapLinear;
        }
    }
}
