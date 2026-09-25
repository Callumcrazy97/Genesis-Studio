using System;
using Genesis.Rendering.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Genesis.Rendering.SilkNet.DX12
{
    /// <summary>Translation between the backend-neutral enums and D3D12/DXGI values.</summary>
    internal static class Dx12GpuFormats
    {
        public static Format ToDxgi(GpuFormat format) => format switch
        {
            GpuFormat.R8G8B8A8UNorm => Format.FormatR8G8B8A8Unorm,
            GpuFormat.R8G8B8A8UNormSrgb => Format.FormatR8G8B8A8UnormSrgb,
            GpuFormat.B8G8R8A8UNorm => Format.FormatB8G8R8A8Unorm,
            GpuFormat.BC5UNorm => Format.FormatBC5Unorm,
            GpuFormat.BC7UNorm => Format.FormatBC7Unorm,
            GpuFormat.BC7UNormSrgb => Format.FormatBC7UnormSrgb,
            GpuFormat.R16G16B16A16Float => Format.FormatR16G16B16A16Float,
            GpuFormat.R11G11B10Float => Format.FormatR11G11B10Float,
            GpuFormat.R8UNorm => Format.FormatR8Unorm,
            GpuFormat.R16Float => Format.FormatR16Float,
            GpuFormat.R32Float => Format.FormatR32Float,
            GpuFormat.D32Float => Format.FormatD32Float,
            GpuFormat.D24UNormS8UInt => Format.FormatD24UnormS8Uint,
            GpuFormat.R32Float2 => Format.FormatR32G32Float,
            GpuFormat.R32Float3 => Format.FormatR32G32B32Float,
            GpuFormat.R32Float4 => Format.FormatR32G32B32A32Float,
            GpuFormat.R32UInt => Format.FormatR32Uint,
            _ => Format.FormatUnknown,
        };

        /// <summary>
        /// The typeless format a depth resource must be created with to also be sampleable.
        /// </summary>
        /// <remarks>
        /// A resource cannot carry both a depth-stencil view and a shader-resource view of the same
        /// typed format. Created typeless, it takes a typed DSV and a typed SRV — which is what the
        /// screen-space fog pass needs, since it reconstructs world position from depth.
        /// </remarks>
        public static Format ToTypelessDepth(GpuFormat format) => format switch
        {
            GpuFormat.D32Float => Format.FormatR32Typeless,
            GpuFormat.D24UNormS8UInt => Format.FormatR24G8Typeless,
            _ => ToDxgi(format),
        };

        /// <summary>The format a shader sees when sampling a depth resource.</summary>
        public static Format ToDepthShaderView(GpuFormat format) => format switch
        {
            GpuFormat.D32Float => Format.FormatR32Float,
            GpuFormat.D24UNormS8UInt => Format.FormatR24UnormX8Typeless,
            _ => ToDxgi(format),
        };

        public static bool IsDepth(GpuFormat format) =>
            format is GpuFormat.D32Float or GpuFormat.D24UNormS8UInt;

        public static bool IsBlockCompressed(GpuFormat format) =>
            format is GpuFormat.BC5UNorm or GpuFormat.BC7UNorm or GpuFormat.BC7UNormSrgb;

        /// <summary>Bytes per pixel, or bytes per 4x4 block for a compressed format.</summary>
        public static int BytesPerUnit(GpuFormat format) => format switch
        {
            GpuFormat.R8UNorm => 1,
            GpuFormat.R16Float => 2,
            GpuFormat.R8G8B8A8UNorm => 4,
            GpuFormat.R8G8B8A8UNormSrgb => 4,
            GpuFormat.B8G8R8A8UNorm => 4,
            GpuFormat.R11G11B10Float => 4,
            GpuFormat.R32Float => 4,
            GpuFormat.D32Float => 4,
            GpuFormat.D24UNormS8UInt => 4,
            GpuFormat.R16G16B16A16Float => 8,
            GpuFormat.BC5UNorm => 16,
            GpuFormat.BC7UNorm => 16,
            GpuFormat.BC7UNormSrgb => 16,
            GpuFormat.R32Float2 => 8,
            GpuFormat.R32Float3 => 12,
            GpuFormat.R32Float4 => 16,
            GpuFormat.R32UInt => 4,
            _ => 4,
        };

        /// <summary>
        /// Unpadded bytes in one row of an image, counting blocks rather than pixels when compressed.
        /// </summary>
        /// <remarks>
        /// Getting this wrong for BC formats is the classic first-attempt DX12 texture bug: a BC7
        /// row covers four pixel rows and one block spans four pixels, so treating it as
        /// width × bytes-per-pixel over-reads by a factor of four and shears the image.
        /// </remarks>
        public static int RowBytes(GpuFormat format, int width)
        {
            if (IsBlockCompressed(format))
            {
                int blocks = Math.Max(1, (width + 3) / 4);
                return blocks * BytesPerUnit(format);
            }

            return width * BytesPerUnit(format);
        }

        /// <summary>Number of stored rows: image rows, or block rows when compressed.</summary>
        public static int RowCount(GpuFormat format, int height) =>
            IsBlockCompressed(format) ? Math.Max(1, (height + 3) / 4) : height;

        public static ComparisonFunc ToComparison(GpuCompare compare) => compare switch
        {
            GpuCompare.Never => ComparisonFunc.Never,
            GpuCompare.Less => ComparisonFunc.Less,
            GpuCompare.Equal => ComparisonFunc.Equal,
            GpuCompare.LessEqual => ComparisonFunc.LessEqual,
            GpuCompare.Greater => ComparisonFunc.Greater,
            GpuCompare.NotEqual => ComparisonFunc.NotEqual,
            GpuCompare.GreaterEqual => ComparisonFunc.GreaterEqual,
            _ => ComparisonFunc.Always,
        };

        public static Blend ToBlend(GpuBlendFactor factor) => factor switch
        {
            GpuBlendFactor.Zero => Blend.Zero,
            GpuBlendFactor.One => Blend.One,
            GpuBlendFactor.SrcColor => Blend.SrcColor,
            GpuBlendFactor.InvSrcColor => Blend.InvSrcColor,
            GpuBlendFactor.SrcAlpha => Blend.SrcAlpha,
            GpuBlendFactor.InvSrcAlpha => Blend.InvSrcAlpha,
            GpuBlendFactor.DestColor => Blend.DestColor,
            GpuBlendFactor.InvDestColor => Blend.InvDestColor,
            GpuBlendFactor.DestAlpha => Blend.DestAlpha,
            GpuBlendFactor.InvDestAlpha => Blend.InvDestAlpha,
            _ => Blend.One,
        };

        public static BlendOp ToBlendOp(GpuBlendOp op) => op switch
        {
            GpuBlendOp.Add => BlendOp.Add,
            GpuBlendOp.Subtract => BlendOp.Subtract,
            GpuBlendOp.ReverseSubtract => BlendOp.RevSubtract,
            GpuBlendOp.Min => BlendOp.Min,
            GpuBlendOp.Max => BlendOp.Max,
            _ => BlendOp.Add,
        };

        public static CullMode ToCull(GpuCullMode mode) => mode switch
        {
            GpuCullMode.None => CullMode.None,
            GpuCullMode.Front => CullMode.Front,
            _ => CullMode.Back,
        };

        public static FillMode ToFill(GpuFillMode mode) =>
            mode == GpuFillMode.Wireframe ? FillMode.Wireframe : FillMode.Solid;

        public static TextureAddressMode ToAddress(GpuAddressMode mode) => mode switch
        {
            GpuAddressMode.Wrap => TextureAddressMode.Wrap,
            GpuAddressMode.Clamp => TextureAddressMode.Clamp,
            GpuAddressMode.Mirror => TextureAddressMode.Mirror,
            _ => TextureAddressMode.Border,
        };

        public static Filter ToFilter(GpuFilter filter, bool comparison) => filter switch
        {
            GpuFilter.Point => comparison ? Filter.ComparisonMinMagMipPoint : Filter.MinMagMipPoint,
            GpuFilter.Anisotropic => comparison ? Filter.ComparisonAnisotropic : Filter.Anisotropic,
            _ => comparison ? Filter.ComparisonMinMagMipLinear : Filter.MinMagMipLinear,
        };

        public static D3DPrimitiveTopology ToTopology(GpuPrimitiveTopology topology) => topology switch
        {
            GpuPrimitiveTopology.TriangleStrip => D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglestrip,
            GpuPrimitiveTopology.LineList => D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist,
            GpuPrimitiveTopology.PointList => D3DPrimitiveTopology.D3DPrimitiveTopologyPointlist,
            _ => D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist,
        };

        /// <summary>
        /// The topology <i>class</i> a PSO is built for, which is coarser than the topology itself.
        /// </summary>
        public static PrimitiveTopologyType ToTopologyType(GpuPrimitiveTopology topology) => topology switch
        {
            GpuPrimitiveTopology.LineList => PrimitiveTopologyType.Line,
            GpuPrimitiveTopology.PointList => PrimitiveTopologyType.Point,
            _ => PrimitiveTopologyType.Triangle,
        };
    }
}
