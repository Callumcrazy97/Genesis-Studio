using System;
using System.Collections.Generic;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace Genesis.Rendering.SilkNet.DX12
{
    /// <summary>
    /// The root signature every Genesis shader is compiled against, and the heaps that feed it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why per-stage tables.</b> D3D11 gives each shader stage its own slot space, so
    /// <c>b0</c> in a vertex shader and <c>b0</c> in a pixel shader are different bindings — and the
    /// renderer relies on that, because <see cref="Abstractions.IGpuDevice.SetConstantBuffer"/>
    /// takes a stage. A single all-visibility table would silently collapse the two onto one
    /// descriptor and feed the pixel shader the vertex shader's constants. So vertex and pixel each
    /// get their own CBV, SRV and sampler tables.</para>
    ///
    /// <para><b>Why the register counts here differ from
    /// <see cref="Core.Dx12BackendReadiness.RootSignatureRanges"/>.</b> That plan reserved
    /// b0..b15 / t0..t63 / u0..u15 as headroom. Descriptors for a table are copied per draw, so the
    /// declared width is what every draw costs: those ranges come to 176 descriptors per draw and
    /// would exhaust a heap in a few hundred draws. These are sized to the renderer's actual
    /// surface (b0..b4, t0..t20, s0..s1) plus a little room — AF1.1 mid cascade is t14 and
    /// AF1.3 omni faces are t15..t20 — instead of the full readiness headroom.</para>
    /// </remarks>
    internal sealed unsafe class Dx12Bindings : IDisposable
    {
        public const int VertexCbvCount = 8;    // b0..b7
        public const int VertexSrvCount = 24;   // t0..t23 (covers AF1.3 OmniFace0..5 at t15..t20)
        public const int PixelCbvCount = 8;
        public const int PixelSrvCount = 24;    // t0..t23
        public const int SamplerCount = 4;      // s0..s3, per stage

        /// <summary>Descriptors one draw consumes from the shader-visible CBV/SRV/UAV heap.</summary>
        public const int DescriptorsPerDraw =
            VertexCbvCount + VertexSrvCount + PixelCbvCount + PixelSrvCount;

        // Room for ~1100 draws per frame. A descriptor is tens of bytes, so the whole ring is a
        // few megabytes — far cheaper than the alternative, which is running out mid-frame.
        private const int DescriptorsPerFrame = 65536;
        private const int SamplerHeapSize = 2048;

        // Root parameter order. Referenced by SetGraphicsRootDescriptorTable at draw time.
        public const uint RootVertexCbv = 0;
        public const uint RootVertexSrv = 1;
        public const uint RootVertexSampler = 2;
        public const uint RootPixelCbv = 3;
        public const uint RootPixelSrv = 4;
        public const uint RootPixelSampler = 5;

        private readonly Dx12Runtime _runtime;
        private readonly Dictionary<SamplerBlockKey, uint> _samplerBlocks = new();

        private ComPtr<ID3D12RootSignature> _rootSignature;
        private ComPtr<ID3D12DescriptorHeap> _shaderVisible;
        private ComPtr<ID3D12DescriptorHeap> _samplerHeap;
        private ComPtr<ID3D12DescriptorHeap> _stagingCbvSrvUav;
        private ComPtr<ID3D12DescriptorHeap> _stagingSampler;

        private uint _cbvSrvUavStride;
        private uint _samplerStride;
        private int _ringCursor;
        private int _frameSlot;
        private uint _samplerCursor;
        private bool _disposed;

        public Dx12Bindings(Dx12Runtime runtime)
        {
            _runtime = runtime;
            _cbvSrvUavStride = _runtime.Device.Handle->GetDescriptorHandleIncrementSize(
                DescriptorHeapType.CbvSrvUav);
            _samplerStride = _runtime.Device.Handle->GetDescriptorHandleIncrementSize(
                DescriptorHeapType.Sampler);

            CreateRootSignature();

            _shaderVisible = CreateHeap(
                DescriptorHeapType.CbvSrvUav,
                (uint)(DescriptorsPerFrame * Dx12FrameRing.FramesInFlight),
                shaderVisible: true);
            _samplerHeap = CreateHeap(DescriptorHeapType.Sampler, SamplerHeapSize, shaderVisible: true);

            // Views are created into these non-shader-visible heaps first and copied into the ring
            // per draw: CreateXView cannot target a shader-visible heap on some drivers, and
            // copying is the documented way to assemble a table from scattered sources.
            _stagingCbvSrvUav = CreateHeap(DescriptorHeapType.CbvSrvUav, 4096u, shaderVisible: false);
            _stagingSampler = CreateHeap(DescriptorHeapType.Sampler, 256u, shaderVisible: false);
        }

        public ID3D12RootSignature* RootSignature => _rootSignature.Handle;

        public uint CbvSrvUavStride => _cbvSrvUavStride;

        public uint SamplerStride => _samplerStride;

        /// <summary>Binds both shader-visible heaps. Required once per command list.</summary>
        public void BindHeaps(ID3D12GraphicsCommandList* list)
        {
            ID3D12DescriptorHeap** heaps = stackalloc ID3D12DescriptorHeap*[2];
            heaps[0] = _shaderVisible.Handle;
            heaps[1] = _samplerHeap.Handle;
            list->SetDescriptorHeaps(2u, heaps);
        }

        /// <summary>Resets this frame's slice of the ring. Called once at frame start.</summary>
        public void BeginFrame(int frameSlot)
        {
            _frameSlot = frameSlot;
            _ringCursor = 0;
        }

        /// <summary>
        /// Reserves a contiguous run in this frame's ring and returns where it starts.
        /// </summary>
        /// <remarks>
        /// Throws rather than wrapping. Wrapping would overwrite descriptors the GPU is still
        /// reading this frame and produce corrupt draws that look like a shader bug; a clear failure
        /// naming the limit is far easier to act on.
        /// </remarks>
        public void Reserve(int count, out CpuDescriptorHandle cpu, out GpuDescriptorHandle gpu)
        {
            if (_ringCursor + count > DescriptorsPerFrame)
            {
                throw new InvalidOperationException(
                    $"Direct3D 12 descriptor ring exhausted: {DescriptorsPerFrame} descriptors per frame "
                    + $"allows about {DescriptorsPerFrame / DescriptorsPerDraw} draws, and this frame "
                    + "asked for more. Raise DescriptorsPerFrame.");
            }

            int index = (_frameSlot * DescriptorsPerFrame) + _ringCursor;
            _ringCursor += count;

            cpu = _shaderVisible.Handle->GetCPUDescriptorHandleForHeapStart();
            cpu.Ptr += (nuint)((ulong)index * _cbvSrvUavStride);
            gpu = _shaderVisible.Handle->GetGPUDescriptorHandleForHeapStart();
            gpu.Ptr += (ulong)index * _cbvSrvUavStride;
        }

        /// <summary>A scratch CPU descriptor slot to build a view in before copying it.</summary>
        public CpuDescriptorHandle StagingCbvSrvUav(int slot)
        {
            CpuDescriptorHandle handle = _stagingCbvSrvUav.Handle->GetCPUDescriptorHandleForHeapStart();
            handle.Ptr += (nuint)((ulong)slot * _cbvSrvUavStride);
            return handle;
        }

        public void CopyDescriptor(CpuDescriptorHandle destination, CpuDescriptorHandle source) =>
            _runtime.Device.Handle->CopyDescriptorsSimple(
                1u, destination, source, DescriptorHeapType.CbvSrvUav);

        /// <summary>
        /// Returns a shader-visible sampler table for this combination, creating it once.
        /// </summary>
        /// <remarks>
        /// Samplers are not ringed per draw. A shader-visible sampler heap holds at most 2048
        /// descriptors on every tier, so per-draw allocation would cap a frame at a couple of
        /// hundred draws. The set of distinct sampler combinations a renderer uses is tiny — a
        /// handful — so caching by content costs nothing and removes the limit entirely.
        /// </remarks>
        public GpuDescriptorHandle SamplerTable(in SamplerBlockKey key, ReadOnlySpan<SamplerDesc> descs)
        {
            if (_samplerBlocks.TryGetValue(key, out uint cached))
            {
                return SamplerGpuHandle(cached);
            }

            if (_samplerCursor + SamplerCount > SamplerHeapSize)
            {
                throw new InvalidOperationException(
                    $"Direct3D 12 sampler heap exhausted after {SamplerHeapSize / SamplerCount} distinct "
                    + "sampler combinations. Something is creating samplers per frame rather than once.");
            }

            uint start = _samplerCursor;
            _samplerCursor += SamplerCount;

            CpuDescriptorHandle destination = _samplerHeap.Handle->GetCPUDescriptorHandleForHeapStart();
            destination.Ptr += (nuint)((ulong)start * _samplerStride);

            for (int i = 0; i < SamplerCount; i++)
            {
                SamplerDesc desc = i < descs.Length ? descs[i] : DefaultSampler();
                _runtime.Device.Handle->CreateSampler(&desc, destination);
                destination.Ptr += (nuint)_samplerStride;
            }

            _samplerBlocks[key] = start;
            return SamplerGpuHandle(start);
        }

        private GpuDescriptorHandle SamplerGpuHandle(uint index)
        {
            GpuDescriptorHandle handle = _samplerHeap.Handle->GetGPUDescriptorHandleForHeapStart();
            handle.Ptr += (ulong)index * _samplerStride;
            return handle;
        }

        /// <summary>Fills an unused sampler slot with something harmless and valid.</summary>
        /// <remarks>
        /// ComparisonFunc is Never, not Always: the validation layer warns on a comparison function
        /// paired with a non-comparison filter, and it is right to — it means the two were set
        /// independently rather than deliberately.
        /// </remarks>
        internal static SamplerDesc DefaultSampler() => new()
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunc.Never,
            MaxLOD = float.MaxValue,
        };

        private ComPtr<ID3D12DescriptorHeap> CreateHeap(
            DescriptorHeapType type, uint count, bool shaderVisible)
        {
            var desc = new DescriptorHeapDesc
            {
                Type = type,
                NumDescriptors = count,
                Flags = shaderVisible
                    ? DescriptorHeapFlags.ShaderVisible
                    : DescriptorHeapFlags.None,
            };

            ComPtr<ID3D12DescriptorHeap> heap = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateDescriptorHeap(
                &desc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)heap.GetAddressOf()));
            return heap;
        }

        /// <remarks>
        /// No UAV table. The renderer uses none, and declaring one meant writing a descriptor into
        /// a range whose declared type did not match it — a descriptor-type mismatch inside a table
        /// is undefined behaviour and removed the device outright. An unused table is not free.
        /// </remarks>
        private void CreateRootSignature()
        {
            const int TableCount = 6;
            DescriptorRange* ranges = stackalloc DescriptorRange[TableCount];
            RootParameter* parameters = stackalloc RootParameter[TableCount];

            ranges[0] = Range(DescriptorRangeType.Cbv, VertexCbvCount, 0);
            ranges[1] = Range(DescriptorRangeType.Srv, VertexSrvCount, 0);
            ranges[2] = Range(DescriptorRangeType.Sampler, SamplerCount, 0);
            ranges[3] = Range(DescriptorRangeType.Cbv, PixelCbvCount, 0);
            ranges[4] = Range(DescriptorRangeType.Srv, PixelSrvCount, 0);
            ranges[5] = Range(DescriptorRangeType.Sampler, SamplerCount, 0);

            ShaderVisibility[] visibilities =
            {
                ShaderVisibility.Vertex, ShaderVisibility.Vertex, ShaderVisibility.Vertex,
                ShaderVisibility.Pixel, ShaderVisibility.Pixel, ShaderVisibility.Pixel,
            };

            for (int i = 0; i < TableCount; i++)
            {
                parameters[i] = new RootParameter
                {
                    ParameterType = RootParameterType.TypeDescriptorTable,
                    ShaderVisibility = visibilities[i],
                };
                parameters[i].Anonymous.DescriptorTable = new RootDescriptorTable
                {
                    NumDescriptorRanges = 1u,
                    PDescriptorRanges = &ranges[i],
                };
            }

            var desc = new RootSignatureDesc
            {
                NumParameters = TableCount,
                PParameters = parameters,
                NumStaticSamplers = 0u,
                PStaticSamplers = null,
                Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
            };

            ID3D10Blob* serialized = null;
            ID3D10Blob* error = null;
            int hr = _runtime.Api.SerializeRootSignature(
                &desc, D3DRootSignatureVersion.Version10, &serialized, &error);

            if (hr < 0)
            {
                string message = error != null
                    ? SilkMarshal.PtrToString((nint)error->GetBufferPointer())
                    : "unknown";
                if (error != null) error->Release();
                if (serialized != null) serialized->Release();
                throw new InvalidOperationException($"Root signature could not be serialised: {message}");
            }

            ComPtr<ID3D12RootSignature> signature = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateRootSignature(
                0u,
                serialized->GetBufferPointer(),
                serialized->GetBufferSize(),
                SilkMarshal.GuidPtrOf<ID3D12RootSignature>(),
                (void**)signature.GetAddressOf()));
            _rootSignature = signature;

            serialized->Release();
            if (error != null) error->Release();
        }

        private static DescriptorRange Range(DescriptorRangeType type, uint count, uint baseRegister) => new()
        {
            RangeType = type,
            NumDescriptors = count,
            BaseShaderRegister = baseRegister,
            RegisterSpace = 0u,
            OffsetInDescriptorsFromTableStart = 0u,
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _stagingSampler.Dispose();
            _stagingSampler = default;
            _stagingCbvSrvUav.Dispose();
            _stagingCbvSrvUav = default;
            _samplerHeap.Dispose();
            _samplerHeap = default;
            _shaderVisible.Dispose();
            _shaderVisible = default;
            _rootSignature.Dispose();
            _rootSignature = default;
        }

        /// <summary>Identity of a bound sampler set, so identical sets share one table.</summary>
        internal readonly record struct SamplerBlockKey(int S0, int S1, int S2, int S3);
    }
}
