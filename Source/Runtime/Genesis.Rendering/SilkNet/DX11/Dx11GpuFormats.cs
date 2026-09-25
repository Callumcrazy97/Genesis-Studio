using System;
using Genesis.Rendering.Abstractions;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Genesis.Rendering.SilkNet.DX11
{
    /// <summary>Translation between the backend-neutral enums and D3D11's.</summary>
    /// <remarks>
    /// Every mapping throws on an unhandled value rather than falling back to a default. A silent
    /// default here would surface as a wrong pixel format or a subtly wrong blend three layers
    /// away, which is exactly the class of bug the abstraction is meant to make impossible.
    /// </remarks>
    internal static class Dx11GpuFormats
    {
        public static Format ToDxgi(GpuFormat format) => format switch
        {
            GpuFormat.R8G8B8A8UNorm     => Format.FormatR8G8B8A8Unorm,
            GpuFormat.R8G8B8A8UNormSrgb => (Format)29, // DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
            GpuFormat.B8G8R8A8UNorm     => Format.FormatB8G8R8A8Unorm,
            GpuFormat.BC5UNorm           => (Format)83, // DXGI_FORMAT_BC5_UNORM
            GpuFormat.BC7UNorm           => (Format)98, // DXGI_FORMAT_BC7_UNORM
            GpuFormat.BC7UNormSrgb       => (Format)99, // DXGI_FORMAT_BC7_UNORM_SRGB
            GpuFormat.R16G16B16A16Float => Format.FormatR16G16B16A16Float,
            GpuFormat.R11G11B10Float    => Format.FormatR11G11B10Float,
            GpuFormat.R8UNorm           => Format.FormatR8Unorm,
            GpuFormat.R16Float          => Format.FormatR16Float,
            GpuFormat.R32Float          => Format.FormatR32Float,
            GpuFormat.D32Float          => Format.FormatD32Float,
            GpuFormat.D24UNormS8UInt    => Format.FormatD24UnormS8Uint,
            GpuFormat.R32Float2         => Format.FormatR32G32Float,
            GpuFormat.R32Float3         => Format.FormatR32G32B32Float,
            GpuFormat.R32Float4         => Format.FormatR32G32B32A32Float,
            GpuFormat.R32UInt           => Format.FormatR32Uint,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unmapped GpuFormat."),
        };

        /// <summary>A depth format has to be created typeless to also be readable as a texture.</summary>
        public static bool IsDepth(GpuFormat format) =>
            format == GpuFormat.D32Float || format == GpuFormat.D24UNormS8UInt;

        public static Format ToTypeless(GpuFormat format) => format switch
        {
            GpuFormat.D32Float       => Format.FormatR32Typeless,
            GpuFormat.D24UNormS8UInt => Format.FormatR24G8Typeless,
            _ => ToDxgi(format),
        };

        public static Format ToDepthSrv(GpuFormat format) => format switch
        {
            GpuFormat.D32Float       => Format.FormatR32Float,
            GpuFormat.D24UNormS8UInt => Format.FormatR24UnormX8Typeless,
            _ => ToDxgi(format),
        };

        public static ComparisonFunc ToComparison(GpuCompare compare) => compare switch
        {
            GpuCompare.Never        => ComparisonFunc.Never,
            GpuCompare.Less         => ComparisonFunc.Less,
            GpuCompare.Equal        => ComparisonFunc.Equal,
            GpuCompare.LessEqual    => ComparisonFunc.LessEqual,
            GpuCompare.Greater      => ComparisonFunc.Greater,
            GpuCompare.NotEqual     => ComparisonFunc.NotEqual,
            GpuCompare.GreaterEqual => ComparisonFunc.GreaterEqual,
            GpuCompare.Always       => ComparisonFunc.Always,
            _ => throw new ArgumentOutOfRangeException(nameof(compare), compare, "Unmapped GpuCompare."),
        };

        public static Blend ToBlend(GpuBlendFactor factor) => factor switch
        {
            GpuBlendFactor.Zero        => Blend.Zero,
            GpuBlendFactor.One         => Blend.One,
            GpuBlendFactor.SrcColor    => Blend.SrcColor,
            GpuBlendFactor.InvSrcColor => Blend.InvSrcColor,
            GpuBlendFactor.SrcAlpha    => Blend.SrcAlpha,
            GpuBlendFactor.InvSrcAlpha => Blend.InvSrcAlpha,
            GpuBlendFactor.DestColor   => Blend.DestColor,
            GpuBlendFactor.InvDestColor=> Blend.InvDestColor,
            GpuBlendFactor.DestAlpha   => Blend.DestAlpha,
            GpuBlendFactor.InvDestAlpha=> Blend.InvDestAlpha,
            _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, "Unmapped GpuBlendFactor."),
        };

        public static BlendOp ToBlendOp(GpuBlendOp op) => op switch
        {
            GpuBlendOp.Add             => BlendOp.Add,
            GpuBlendOp.Subtract        => BlendOp.Subtract,
            GpuBlendOp.ReverseSubtract => BlendOp.RevSubtract,
            GpuBlendOp.Min             => BlendOp.Min,
            GpuBlendOp.Max             => BlendOp.Max,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unmapped GpuBlendOp."),
        };

        public static CullMode ToCullMode(GpuCullMode mode) => mode switch
        {
            GpuCullMode.None  => CullMode.None,
            GpuCullMode.Front => CullMode.Front,
            GpuCullMode.Back  => CullMode.Back,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unmapped GpuCullMode."),
        };

        public static Silk.NET.Direct3D11.FillMode ToFillMode(GpuFillMode mode) => mode switch
        {
            GpuFillMode.Solid     => Silk.NET.Direct3D11.FillMode.Solid,
            GpuFillMode.Wireframe => Silk.NET.Direct3D11.FillMode.Wireframe,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unmapped GpuFillMode."),
        };

        public static TextureAddressMode ToAddressMode(GpuAddressMode mode) => mode switch
        {
            GpuAddressMode.Wrap   => TextureAddressMode.Wrap,
            GpuAddressMode.Clamp  => TextureAddressMode.Clamp,
            GpuAddressMode.Mirror => TextureAddressMode.Mirror,
            GpuAddressMode.Border => TextureAddressMode.Border,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unmapped GpuAddressMode."),
        };

        public static Silk.NET.Core.Native.D3DPrimitiveTopology ToTopology(GpuPrimitiveTopology topology) => topology switch
        {
            GpuPrimitiveTopology.TriangleList  => Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist,
            GpuPrimitiveTopology.TriangleStrip => Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglestrip,
            GpuPrimitiveTopology.LineList      => Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist,
            GpuPrimitiveTopology.PointList     => Silk.NET.Core.Native.D3DPrimitiveTopology.D3DPrimitiveTopologyPointlist,
            _ => throw new ArgumentOutOfRangeException(nameof(topology), topology, "Unmapped GpuPrimitiveTopology."),
        };

        public static Filter ToFilter(GpuFilter filter, GpuCompare compare)
        {
            bool comparison = compare != GpuCompare.Never;
            return filter switch
            {
                GpuFilter.Point => comparison
                    ? Filter.ComparisonMinMagMipPoint
                    : Filter.MinMagMipPoint,
                GpuFilter.Linear => comparison
                    ? Filter.ComparisonMinMagMipLinear
                    : Filter.MinMagMipLinear,
                GpuFilter.Anisotropic => comparison
                    ? Filter.ComparisonAnisotropic
                    : Filter.Anisotropic,
                _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unmapped GpuFilter."),
            };
        }

        public static uint ToBindFlags(GpuBindFlags flags)
        {
            uint result = 0;
            if ((flags & GpuBindFlags.VertexBuffer) != 0)   result |= (uint)BindFlag.VertexBuffer;
            if ((flags & GpuBindFlags.IndexBuffer) != 0)    result |= (uint)BindFlag.IndexBuffer;
            if ((flags & GpuBindFlags.ConstantBuffer) != 0) result |= (uint)BindFlag.ConstantBuffer;
            if ((flags & GpuBindFlags.ShaderResource) != 0) result |= (uint)BindFlag.ShaderResource;
            if ((flags & GpuBindFlags.StructuredBuffer) != 0) result |= (uint)BindFlag.ShaderResource;
            if ((flags & GpuBindFlags.RenderTarget) != 0)   result |= (uint)BindFlag.RenderTarget;
            if ((flags & GpuBindFlags.DepthStencil) != 0)   result |= (uint)BindFlag.DepthStencil;
            if ((flags & GpuBindFlags.UnorderedAccess) != 0) result |= (uint)BindFlag.UnorderedAccess;
            return result;
        }

        public static (Usage usage, uint cpuAccess) ToUsage(GpuBufferUsage usage) => usage switch
        {
            GpuBufferUsage.Immutable => (Usage.Default, 0u),
            GpuBufferUsage.Gpu       => (Usage.Default, 0u),
            GpuBufferUsage.Dynamic   => (Usage.Dynamic, (uint)CpuAccessFlag.Write),
            GpuBufferUsage.Staging   => (Usage.Staging, (uint)CpuAccessFlag.Read),
            _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, "Unmapped GpuBufferUsage."),
        };
    }
}
