using System;
using Genesis.Rendering.Abstractions;
using Silk.NET.Vulkan;
using VkFormat = Silk.NET.Vulkan.Format;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>Translation between the backend-neutral enums and Vulkan values.</summary>
    internal static class VulkanGpuFormats
    {
        public static VkFormat ToVulkan(GpuFormat format) => format switch
        {
            GpuFormat.R8G8B8A8UNorm => VkFormat.R8G8B8A8Unorm,
            GpuFormat.R8G8B8A8UNormSrgb => VkFormat.R8G8B8A8Srgb,
            GpuFormat.B8G8R8A8UNorm => VkFormat.B8G8R8A8Unorm,
            GpuFormat.BC5UNorm => VkFormat.BC5UnormBlock,
            GpuFormat.BC7UNorm => VkFormat.BC7UnormBlock,
            GpuFormat.BC7UNormSrgb => VkFormat.BC7SrgbBlock,
            GpuFormat.R16G16B16A16Float => VkFormat.R16G16B16A16Sfloat,
            GpuFormat.R11G11B10Float => VkFormat.B10G11R11UfloatPack32,
            GpuFormat.R8UNorm => VkFormat.R8Unorm,
            GpuFormat.R16Float => VkFormat.R16Sfloat,
            GpuFormat.R32Float => VkFormat.R32Sfloat,
            GpuFormat.D32Float => VkFormat.D32Sfloat,
            GpuFormat.D24UNormS8UInt => VkFormat.D24UnormS8Uint,
            GpuFormat.R32Float2 => VkFormat.R32G32Sfloat,
            GpuFormat.R32Float3 => VkFormat.R32G32B32Sfloat,
            GpuFormat.R32Float4 => VkFormat.R32G32B32A32Sfloat,
            GpuFormat.R32UInt => VkFormat.R32Uint,
            _ => VkFormat.Undefined,
        };

        public static bool IsDepth(GpuFormat format) =>
            format is GpuFormat.D32Float or GpuFormat.D24UNormS8UInt;

        public static bool HasStencil(GpuFormat format) => format is GpuFormat.D24UNormS8UInt;

        public static bool IsBlockCompressed(GpuFormat format) =>
            format is GpuFormat.BC5UNorm or GpuFormat.BC7UNorm or GpuFormat.BC7UNormSrgb;

        public static BufferUsageFlags ToVulkanUsage(GpuBufferUsage usage, GpuBindFlags bind)
        {
            BufferUsageFlags flags = BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit;
            if (bind.HasFlag(GpuBindFlags.IndirectArguments)) flags |= BufferUsageFlags.IndirectBufferBit;
            if (bind.HasFlag(GpuBindFlags.VertexBuffer)) flags |= BufferUsageFlags.VertexBufferBit;
            if (bind.HasFlag(GpuBindFlags.IndexBuffer)) flags |= BufferUsageFlags.IndexBufferBit;
            if (usage == GpuBufferUsage.Staging) flags |= BufferUsageFlags.TransferSrcBit;

            // Uniform and storage usage are granted unconditionally rather than from the bind flags.
            // A descriptor write fails outright if the buffer lacks the usage matching its descriptor
            // type, and Genesis's bind flags do not always distinguish the two — a structured buffer
            // reaching a StorageBuffer descriptor without STORAGE_BUFFER_BIT is a validation error,
            // not a silent one. The cost of the extra flags is nil on every desktop driver.
            flags |= BufferUsageFlags.UniformBufferBit | BufferUsageFlags.StorageBufferBit;
            return flags;
        }

        /// <summary>Bytes per pixel, or bytes per 4x4 block for a compressed format.</summary>
        public static int BytesPerUnit(GpuFormat format) => format switch
        {
            GpuFormat.R8UNorm => 1,
            GpuFormat.R16Float => 2,
            GpuFormat.R8G8B8A8UNorm or GpuFormat.R8G8B8A8UNormSrgb or GpuFormat.B8G8R8A8UNorm => 4,
            GpuFormat.R11G11B10Float or GpuFormat.R32Float => 4,
            GpuFormat.D32Float or GpuFormat.D24UNormS8UInt => 4,
            GpuFormat.R16G16B16A16Float => 8,
            GpuFormat.BC5UNorm or GpuFormat.BC7UNorm or GpuFormat.BC7UNormSrgb => 16,
            GpuFormat.R32Float2 => 8,
            GpuFormat.R32Float3 => 12,
            GpuFormat.R32Float4 => 16,
            GpuFormat.R32UInt => 4,
            _ => 4,
        };

        /// <summary>
        /// Unpadded bytes in one row, counting blocks rather than pixels when compressed.
        /// </summary>
        /// <remarks>
        /// A BC row covers four pixel rows and one block spans four pixels, so treating it as
        /// width x bytes-per-pixel over-reads fourfold and shears the image.
        /// </remarks>
        public static int RowBytes(GpuFormat format, int width) =>
            IsBlockCompressed(format)
                ? Math.Max(1, (width + 3) / 4) * BytesPerUnit(format)
                : width * BytesPerUnit(format);

        public static int RowCount(GpuFormat format, int height) =>
            IsBlockCompressed(format) ? Math.Max(1, (height + 3) / 4) : height;

        public static CompareOp ToCompare(GpuCompare compare) => compare switch
        {
            GpuCompare.Never => CompareOp.Never,
            GpuCompare.Less => CompareOp.Less,
            GpuCompare.Equal => CompareOp.Equal,
            GpuCompare.LessEqual => CompareOp.LessOrEqual,
            GpuCompare.Greater => CompareOp.Greater,
            GpuCompare.NotEqual => CompareOp.NotEqual,
            GpuCompare.GreaterEqual => CompareOp.GreaterOrEqual,
            _ => CompareOp.Always,
        };

        public static BlendFactor ToBlend(GpuBlendFactor factor) => factor switch
        {
            GpuBlendFactor.Zero => BlendFactor.Zero,
            GpuBlendFactor.One => BlendFactor.One,
            GpuBlendFactor.SrcColor => BlendFactor.SrcColor,
            GpuBlendFactor.InvSrcColor => BlendFactor.OneMinusSrcColor,
            GpuBlendFactor.SrcAlpha => BlendFactor.SrcAlpha,
            GpuBlendFactor.InvSrcAlpha => BlendFactor.OneMinusSrcAlpha,
            GpuBlendFactor.DestColor => BlendFactor.DstColor,
            GpuBlendFactor.InvDestColor => BlendFactor.OneMinusDstColor,
            GpuBlendFactor.DestAlpha => BlendFactor.DstAlpha,
            GpuBlendFactor.InvDestAlpha => BlendFactor.OneMinusDstAlpha,
            _ => BlendFactor.One,
        };

        public static BlendOp ToBlendOp(GpuBlendOp op) => op switch
        {
            GpuBlendOp.Add => BlendOp.Add,
            GpuBlendOp.Subtract => BlendOp.Subtract,
            GpuBlendOp.ReverseSubtract => BlendOp.ReverseSubtract,
            GpuBlendOp.Min => BlendOp.Min,
            GpuBlendOp.Max => BlendOp.Max,
            _ => BlendOp.Add,
        };

        public static CullModeFlags ToCull(GpuCullMode mode) => mode switch
        {
            GpuCullMode.None => CullModeFlags.None,
            GpuCullMode.Front => CullModeFlags.FrontBit,
            _ => CullModeFlags.BackBit,
        };

        public static PolygonMode ToFill(GpuFillMode mode) =>
            mode == GpuFillMode.Wireframe ? PolygonMode.Line : PolygonMode.Fill;

        public static SamplerAddressMode ToAddress(GpuAddressMode mode) => mode switch
        {
            GpuAddressMode.Wrap => SamplerAddressMode.Repeat,
            GpuAddressMode.Clamp => SamplerAddressMode.ClampToEdge,
            GpuAddressMode.Mirror => SamplerAddressMode.MirroredRepeat,
            _ => SamplerAddressMode.ClampToBorder,
        };

        public static Filter ToFilter(GpuFilter filter) =>
            filter == GpuFilter.Point ? Filter.Nearest : Filter.Linear;

        public static SamplerMipmapMode ToMipmapMode(GpuFilter filter) =>
            filter == GpuFilter.Point ? SamplerMipmapMode.Nearest : SamplerMipmapMode.Linear;

        public static PrimitiveTopology ToTopology(GpuPrimitiveTopology topology) => topology switch
        {
            GpuPrimitiveTopology.TriangleStrip => PrimitiveTopology.TriangleStrip,
            GpuPrimitiveTopology.LineList => PrimitiveTopology.LineList,
            GpuPrimitiveTopology.PointList => PrimitiveTopology.PointList,
            _ => PrimitiveTopology.TriangleList,
        };

        public static ColorComponentFlags ToWriteMask(bool r, bool g, bool b, bool a)
        {
            ColorComponentFlags mask = 0;
            if (r) mask |= ColorComponentFlags.RBit;
            if (g) mask |= ColorComponentFlags.GBit;
            if (b) mask |= ColorComponentFlags.BBit;
            if (a) mask |= ColorComponentFlags.ABit;
            return mask;
        }
    }
}
