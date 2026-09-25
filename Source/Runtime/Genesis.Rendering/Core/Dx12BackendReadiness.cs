using System;
using System.Collections.Generic;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.RenderGraph;

namespace Genesis.Rendering.Core
{
    public enum Dx12PlannedResourceState
    {
        Common,
        RenderTarget,
        DepthWrite,
        ShaderResource,
        UnorderedAccess,
        CopySource,
        CopyDestination,
        Present,
    }

    public readonly record struct Dx12DescriptorRangePlan(
        char RegisterClass,
        int BaseRegister,
        int RegisterCount,
        int HeapOffset);

    /// <summary>
    /// Concrete Phase 6 DX12 plan expressed without taking a dependency on a D3D12 package yet.
    /// The future backend translates these plans directly to root-signature ranges and resource states.
    /// </summary>
    public static class Dx12BackendReadiness
    {
        /// <summary>
        /// Set to true only when <c>--backend-smoke dx12</c> passes; gates promotion.
        /// </summary>
        /// <remarks>
        /// Deliberately separate from <c>IsImplemented</c>. "The code exists" and "a frame survives
        /// on real hardware" are different claims, and conflating them is how a backend reaches a
        /// Preferences menu before it can draw anything.
        /// </remarks>
        public const bool Dx12RenderPathVerified = true;

        public const int FramesInFlight = 3;
        public const int CbvSrvUavHeapCapacity = 256;
        public const int SamplerHeapCapacity = 32;

        // Current renderer use is b0..b4, t0..t20 (mid cascade + omni faces) and s0..s1.
        // Deliberate headroom keeps the root signature stable while materials/compute grow.
        public static IReadOnlyList<Dx12DescriptorRangePlan> RootSignatureRanges { get; } =
        [
            new('b', 0, 16, 0),
            new('t', 0, 64, 16),
            new('u', 0, 16, 80),
            new('s', 0, 16, 0),
        ];

        public static Dx12PlannedResourceState StateFor(RenderResourceUsage usage) =>
            usage switch
            {
                RenderResourceUsage.Undefined => Dx12PlannedResourceState.Common,
                RenderResourceUsage.ColorAttachment => Dx12PlannedResourceState.RenderTarget,
                RenderResourceUsage.DepthAttachment => Dx12PlannedResourceState.DepthWrite,
                RenderResourceUsage.ShaderRead => Dx12PlannedResourceState.ShaderResource,
                RenderResourceUsage.ShaderWrite => Dx12PlannedResourceState.UnorderedAccess,
                RenderResourceUsage.TransferSource => Dx12PlannedResourceState.CopySource,
                RenderResourceUsage.TransferDestination => Dx12PlannedResourceState.CopyDestination,
                RenderResourceUsage.Present => Dx12PlannedResourceState.Present,
                _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, null),
            };

        public static void Validate()
        {
            RenderBackendDescriptor dx12 = RenderBackendCatalog.Describe(RenderBackendOption.Direct3D12);
            if (dx12.ShaderBinaryFormat != GpuShaderBinaryFormat.Dxil)
            {
                throw new InvalidOperationException("DX12 readiness drifted away from the DXIL shader contract.");
            }

            // The device type must exist and satisfy the contract whether or not the backend is
            // promoted — that is what stops the implementation rotting while it is unselectable.
            Type device = Type.GetType(
                "Genesis.Rendering.SilkNet.DX12.Dx12GpuDevice, Genesis.Rendering", throwOnError: false);
            if (device is null || !typeof(Abstractions.IGpuDevice).IsAssignableFrom(device))
            {
                throw new InvalidOperationException(
                    "Dx12GpuDevice is missing or no longer implements IGpuDevice, so the factory "
                    + "could not build the backend even once it is promoted.");
            }

            // Promotion and a working device have to move together. Flipping IsImplemented without
            // a device that survives a frame puts a crashing option in Preferences.
            if (dx12.IsImplemented && !Dx12RenderPathVerified)
            {
                throw new InvalidOperationException(
                    "DX12 is marked implemented but its render path has not been verified. Set "
                    + "Dx12RenderPathVerified once the backend smoke passes.");
            }

            foreach (RenderResourceUsage usage in Enum.GetValues<RenderResourceUsage>())
            {
                _ = StateFor(usage);
            }

            int cbvSrvUavEnd = 0;
            int samplerEnd = 0;
            foreach (Dx12DescriptorRangePlan range in RootSignatureRanges)
            {
                if (range.RegisterCount <= 0 || range.BaseRegister < 0 || range.HeapOffset < 0)
                {
                    throw new InvalidOperationException("DX12 root-signature descriptor range is invalid.");
                }

                int end = range.HeapOffset + range.RegisterCount;
                if (range.RegisterClass == 's')
                {
                    samplerEnd = Math.Max(samplerEnd, end);
                }
                else
                {
                    cbvSrvUavEnd = Math.Max(cbvSrvUavEnd, end);
                }
            }

            if (cbvSrvUavEnd > CbvSrvUavHeapCapacity || samplerEnd > SamplerHeapCapacity)
            {
                throw new InvalidOperationException("DX12 descriptor plan exceeds its declared heap capacity.");
            }
        }
    }
}
