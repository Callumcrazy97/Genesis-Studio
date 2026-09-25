using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Genesis.Rendering.SilkNet.DX12
{
    /// <summary>
    /// The Direct3D 12 implementation of <see cref="IGpuDevice"/>.
    /// </summary>
    /// <remarks>
    /// <para>Presents an immediate-mode surface over an explicit API. That is deliberate and is what
    /// the interface was designed for: pipeline setters mutate a pending key and the first draw
    /// after a change resolves it to a PSO, and dynamic constant writes sub-allocate from a
    /// per-frame upload ring exactly as <see cref="IGpuDevice.UpdateConstantBuffer{T}"/> licenses.
    /// The 2800-line ForwardRenderer runs unchanged on top of it.</para>
    ///
    /// <para>Two rules dominate everything here, and both are places D3D11 did the work for you:
    /// every COM pointer is taken through <c>GetAddressOf()</c> rather than
    /// <c>new ComPtr&lt;T&gt;(raw)</c>, which AddRefs a second time and leaks; and nothing is
    /// destroyed the moment its handle is released, because a command list may still reference it —
    /// see <c>Defer</c> in the Internals partial.</para>
    /// </remarks>
    internal sealed unsafe partial class Dx12GpuDevice : IGpuComputeDevice
    {
        private const int UploadRingBytes = 32 * 1024 * 1024;
        private const int ConstantAlignment = 256;          // D3D12_CONSTANT_BUFFER_DATA_PLACEMENT_ALIGNMENT
        private const int TextureRowAlignment = 256;        // D3D12_TEXTURE_DATA_PITCH_ALIGNMENT
        private const int TexturePlacementAlignment = 512;  // D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT
        private const int MaxTimestampScopes = 256;

        /// <summary>
        /// State a sampleable texture must be in for both vertex (height maps) and pixel shaders.
        /// PixelShaderResource alone is undefined for a VS fetch and reads as garbage displacement.
        /// </summary>
        private const ResourceStates ShaderResourceState =
            ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource;

        /// <summary>Sampleable textures that can exist at once; matches the staging heap's size.</summary>
        private const int StagingSrvCapacity = 4096;

        private sealed class BufferResource
        {
            public ComPtr<ID3D12Resource> Resource;
            public int SizeBytes;
            public int StructureStride;
            public GpuBufferUsage Usage;
            public GpuBindFlags BindFlags;
            public ResourceStates State;

            /// <summary>For a dynamic buffer: where this frame's contents live in the upload ring.</summary>
            public ulong DynamicAddress;
            public int DynamicSize;

            /// <summary>
            /// Which ring buffer holds those contents, and at what byte offset.
            /// </summary>
            /// <remarks>
            /// A GPU virtual address is enough for a constant or vertex buffer, but a shader
            /// resource view has to name an actual <c>ID3D12Resource</c> — so a dynamic structured
            /// buffer needs the ring resource itself, not just an address into it.
            /// </remarks>
            public int DynamicRingSlot = -1;
            public ulong DynamicRingOffset;

            public void* MappedScratch;
        }

        private sealed class TextureResource
        {
            public ComPtr<ID3D12Resource> Resource;
            public int Width, Height, MipLevels, ArrayLayers;
            public GpuFormat Format;
            public ResourceStates State;
            public bool IsDepth;

            /// <summary>Slot in the staging heap holding this texture's SRV, or -1 if unsampleable.</summary>
            public int SrvSlot = -1;

            /// <summary>True when the resource is owned by a swap chain and must not be released.</summary>
            public bool External;
        }

        private sealed class RenderTargetResource
        {
            public GpuTextureHandle[] Colors;
            public GpuTextureHandle Depth;
            public GpuFormat[] ColorFormats;
            public GpuFormat DepthFormat;
            public ComPtr<ID3D12DescriptorHeap> RtvHeap;
            public ComPtr<ID3D12DescriptorHeap> DsvHeap;
            public int Width, Height;
        }

        private sealed class ShaderProgramResource
        {
            public byte[] VertexShader;
            public byte[] PixelShader;
            public byte[] ComputeShader;
            public ComPtr<ID3D12PipelineState> ComputePipeline;
        }

        private sealed class TimestampResource
        {
            public int Index;
            public bool Ended;
        }

        private readonly Dx12Runtime _runtime;
        private readonly Dx12FrameRing _frames;
        private readonly Dx12Bindings _bindings;

        private readonly Dictionary<int, BufferResource> _buffers = new();
        private readonly Dictionary<int, TextureResource> _textures = new();
        private readonly Dictionary<int, SamplerDesc> _samplers = new();
        private readonly Dictionary<int, RenderTargetResource> _renderTargets = new();
        private readonly Dictionary<int, ShaderProgramResource> _shaderPrograms = new();
        private readonly Dictionary<int, GpuVertexLayoutDesc> _vertexLayouts = new();
        private readonly Dictionary<int, TimestampResource> _queries = new();
        private readonly Dictionary<GpuPipelineKey, nint> _pipelines = new();

        /// <summary>COM objects awaiting the fence value that makes them safe to destroy.</summary>
        private readonly List<(nint Object, ulong Fence)> _deferred = new();

        private readonly Stack<int> _freeSrvSlots = new();
        private readonly Stack<int> _freeTimestampSlots = new();
        private int _nextSrvSlot;
        private int _nextBuffer = 1, _nextTexture = 1, _nextSampler = 1, _nextRenderTarget = 1;
        private int _nextShaderProgram = 1, _nextVertexLayout = 1, _nextQuery = 1;

        // Pending pipeline state, resolved to a PSO on the next draw.
        private GpuPipelineKey _pipeline;
        private nint _boundPipeline;

        // Bindings, applied to descriptor tables at draw time.
        private readonly GpuBufferHandle[] _vsCbv = new GpuBufferHandle[Dx12Bindings.VertexCbvCount];
        private readonly GpuBufferHandle[] _psCbv = new GpuBufferHandle[Dx12Bindings.PixelCbvCount];
        private readonly GpuTextureHandle[] _vsSrv = new GpuTextureHandle[Dx12Bindings.VertexSrvCount];
        private readonly GpuTextureHandle[] _psSrv = new GpuTextureHandle[Dx12Bindings.PixelSrvCount];
        private readonly GpuBufferHandle[] _vsStructured = new GpuBufferHandle[Dx12Bindings.VertexSrvCount];
        private readonly GpuBufferHandle[] _psStructured = new GpuBufferHandle[Dx12Bindings.PixelSrvCount];
        private readonly GpuSamplerHandle[] _vsSampler = new GpuSamplerHandle[Dx12Bindings.SamplerCount];
        private readonly GpuSamplerHandle[] _psSampler = new GpuSamplerHandle[Dx12Bindings.SamplerCount];

        private ComPtr<ID3D12Resource>[] _uploadRing;
        private byte*[] _uploadCursorBase;
        private int[] _uploadUsed;

        private ComPtr<ID3D12QueryHeap> _timestampHeap;
        private ComPtr<ID3D12Resource> _timestampReadback;
        private int _nextTimestampIndex;
        private ulong _timestampFrequency;

        private Dx12SwapChain _activeSwapChain;
        private GpuTextureHandle _backBufferTexture;

        /// <summary>
        /// The swap chain's depth buffer, published as an ordinary sampleable texture handle.
        /// </summary>
        /// <remarks>
        /// This existing as a *handle* is what lets 3D render at all. The swap chain has always
        /// owned a real D32 depth buffer and <see cref="BeginRenderPass"/> has always bound its DSV
        /// directly, so depth testing worked — but <c>IGpuSwapChain.DepthTexture</c> returned
        /// <see cref="GpuTextureHandle.Invalid"/>, and <c>ForwardRenderer.Flush</c> opens with
        /// "no depth texture, nothing to do" and returns before submitting a single triangle. Every
        /// 3D frame was discarded at that line, silently, with no log and no exception.
        /// </remarks>
        internal GpuTextureHandle _depthTexture;
        private GpuRenderTargetHandle _activeTarget;
        private GpuTextureHandle _dummyTexture;
        private GpuBufferHandle _indexBuffer;
        private GpuIndexFormat _indexFormat;
        private int _indexOffset;
        private readonly GpuBufferHandle[] _vertexBuffers = new GpuBufferHandle[4];
        private readonly int[] _vertexStrides = new int[4];
        private readonly int[] _vertexOffsets = new int[4];
        private int _framesSubmitted;
        private bool _disposed;

        public Dx12GpuDevice()
        {
            _runtime = Dx12Runtime.EnsureDevice();
            _frames = new Dx12FrameRing(_runtime);
            _bindings = new Dx12Bindings(_runtime);

            _pipeline.Topology = GpuPrimitiveTopology.TriangleList;
            _pipeline.Blend = GpuBlendState.Opaque;
            _pipeline.Depth = GpuDepthState.Default;
            _pipeline.Raster = GpuRasterState.Default;

            CreateUploadRing();
            CreateTimestampResources();
            CreateDummyTexture();
        }

        // ── Identity ────────────────────────────────────────────────────────────

        public string BackendName => "Direct3D 12";

        public string AdapterName => _runtime.AdapterName;

        public GpuShaderBinaryFormat ShaderBinaryFormat => GpuShaderBinaryFormat.Dxil;

        public GpuCapabilities Capabilities => new()
        {
            ClipSpaceZeroToOne = true,
            FramebufferOriginTopLeft = true,
            SupportsStructuredBuffers = true,
            SupportsUInt32Indices = true,
            SupportsTimestampQueries = true,
            SupportsComputeShaders = true,
            SupportsIndirectDraw = true,
            MaxTextureArrayLayers = 2048,
            MaxAnisotropy = 16,
            MaxColorAttachments = 8,
            DepthBiasScale = 1f,
        };

        public IGpuSwapChain CreateSwapChain(IntPtr windowHandle, int width, int height)
        {
            ThrowIfDisposed();
            if (windowHandle == IntPtr.Zero)
            {
                throw new ArgumentException("A real HWND is required.", nameof(windowHandle));
            }

            var chain = new Dx12SwapChain(_runtime, _frames, windowHandle, width, height);
            var adapter = new SwapChainAdapter(this, chain);
            _activeSwapChain = chain;
            return adapter;
        }

        // ── Frame envelope ──────────────────────────────────────────────────────

        public void BeginFrame()
        {
            ThrowIfDisposed();

            // Texture and buffer creates call EnsureRecording, which opens a command list before
            // the first real frame. Rewinding the upload ring or descriptor cursor in that case
            // overwrites staging bytes that CopyTextureRegion still points at — the GPU then
            // copies garbage into every texture created during initialise (checker floor, dummy
            // white, cooked albedos). That is the missing-tile / staircase picture.
            bool alreadyRecording = _frames.IsRecording;
            _frames.BeginFrame();

            // Now that a frame boundary has been crossed, anything the GPU has finished with can
            // finally be destroyed.
            DrainDeferred(force: false);

            if (!alreadyRecording)
            {
                _bindings.BeginFrame(_frames.FrameIndex);
                _uploadUsed[_frames.FrameIndex] = 0;
            }

            _bindings.BindHeaps(_frames.List);
            _frames.List->SetGraphicsRootSignature(_bindings.RootSignature);
            _boundPipeline = 0;
        }

        public void EndFrame()
        {
            ThrowIfDisposed();
            if (!_frames.IsRecording)
            {
                return;
            }

            // A back buffer must be in Present state before the swap chain shows it.
            if (_activeSwapChain is { IsReady: true } && _backBufferTexture.IsValid)
            {
                TransitionBackBuffer(ResourceStates.Present);
            }

            _frames.EndFrame();

            // Checked on every frame, not only under the debug layer: GetDeviceRemovedReason needs
            // no validation layer, and it is the only thing that names the actual fault. Without
            // it a removal surfaces as DEVICE_REMOVED from whatever unrelated call comes next.
            _runtime.DrainDiagnostics();
            string removal = _runtime.DescribeRemoval();
            if (removal is not null)
            {
                throw new InvalidOperationException(
                    $"Direct3D 12 device removed after frame {_framesSubmitted}: {removal}");
            }

            _framesSubmitted++;
        }

        public void WaitIdle()
        {
            ThrowIfDisposed();
            _frames.WaitIdle();
        }

        // ── Buffers ─────────────────────────────────────────────────────────────

        public GpuBufferHandle CreateBuffer(in GpuBufferDesc desc, ReadOnlySpan<byte> initialData)
        {
            ThrowIfDisposed();
            int size = Math.Max(1, desc.SizeBytes);
            bool dynamic = desc.Usage == GpuBufferUsage.Dynamic;

            var resource = new BufferResource
            {
                SizeBytes = size,
                StructureStride = desc.StructureStride,
                Usage = desc.Usage,
                BindFlags = desc.BindFlags,
                State = dynamic ? ResourceStates.GenericRead : ResourceStates.Common,
            };

            // Dynamic buffers live wholly in the upload ring and own no committed resource; their
            // storage is handed out per write. Everything else gets a default-heap resource.
            if (!dynamic)
            {
                resource.Resource = CreateCommittedBuffer(
                    size, HeapType.Default, ResourceStates.Common,
                    (desc.BindFlags & GpuBindFlags.UnorderedAccess) != 0 ? ResourceFlags.AllowUnorderedAccess : ResourceFlags.None);

                if (!initialData.IsEmpty)
                {
                    UploadIntoBuffer(resource, initialData, 0);
                }
            }
            else if (!initialData.IsEmpty)
            {
                // A dynamic buffer created with contents has nowhere to keep them until the first
                // frame opens, so they are staged on the CPU and written on the next update.
                WriteDynamic(resource, initialData);
            }

            int id = _nextBuffer++;
            _buffers[id] = resource;
            return new GpuBufferHandle(id);
        }

        public void UpdateBuffer(GpuBufferHandle handle, ReadOnlySpan<byte> data, int byteOffset = 0)
        {
            BufferResource buffer = Require(_buffers, handle.Id, nameof(handle));
            if (buffer.Usage == GpuBufferUsage.Dynamic)
            {
                WriteDynamic(buffer, data);
                return;
            }

            UploadIntoBuffer(buffer, data, byteOffset);
        }

        public void UpdateConstantBuffer<T>(GpuBufferHandle handle, in T data) where T : unmanaged
        {
            BufferResource buffer = Require(_buffers, handle.Id, nameof(handle));
            ReadOnlySpan<byte> bytes = new(Unsafe.AsPointer(ref Unsafe.AsRef(in data)), sizeof(T));
            WriteDynamic(buffer, bytes);
        }

        public bool TryMapDiscard(GpuBufferHandle handle, out Span<byte> span, int byteCount = 0)
        {
            BufferResource buffer = Require(_buffers, handle.Id, nameof(handle));
            if (buffer.Usage != GpuBufferUsage.Dynamic)
            {
                span = default;
                return false;
            }

            // Hand back a fresh ring slice. The contract already tells callers a backend may
            // sub-allocate, so nothing may assume an earlier draw sees this write.
            EnsureRecording();
            int writtenBytes = byteCount > 0 ? Math.Min(byteCount, buffer.SizeBytes) : buffer.SizeBytes;
            int viewSize = ConstantViewSize(buffer, writtenBytes);
            void* pointer = AllocateUpload(
                viewSize, DynamicAlignment(buffer),
                out ulong address, out int slot, out ulong offset);
            if (viewSize > buffer.SizeBytes)
            {
                new Span<byte>(pointer, viewSize).Clear();
            }

            buffer.DynamicAddress = address;
            buffer.DynamicSize = viewSize;
            buffer.DynamicRingSlot = slot;
            buffer.DynamicRingOffset = offset;
            span = new Span<byte>(pointer, viewSize);
            if (byteCount > 0 && byteCount < span.Length)
                span = span.Slice(0, byteCount);
            return true;
        }

        public void Unmap(GpuBufferHandle handle)
        {
            // Ring memory stays mapped for the life of the device; there is nothing to undo.
            _ = Require(_buffers, handle.Id, nameof(handle));
        }

        public void ReleaseBuffer(GpuBufferHandle handle)
        {
            if (!_buffers.Remove(handle.Id, out BufferResource buffer))
            {
                return;
            }

            DeferResource(ref buffer.Resource);
            if (buffer.MappedScratch != null)
            {
                NativeMemory.Free(buffer.MappedScratch);
                buffer.MappedScratch = null;
            }
        }

        // ── Textures and samplers ───────────────────────────────────────────────

        public GpuTextureHandle CreateTexture(in GpuTextureDesc desc, ReadOnlySpan<byte> initialData)
        {
            ThrowIfDisposed();
            bool depth = Dx12GpuFormats.IsDepth(desc.Format);
            bool sampleable = (desc.BindFlags & GpuBindFlags.ShaderResource) != 0;
            int mips = desc.MipLevels <= 0 ? 1 : desc.MipLevels;
            int layers = Math.Max(1, desc.ArrayLayers);

            ResourceFlags flags = ResourceFlags.None;
            if ((desc.BindFlags & GpuBindFlags.RenderTarget) != 0) flags |= ResourceFlags.AllowRenderTarget;
            if (depth) flags |= ResourceFlags.AllowDepthStencil;
            if (depth && !sampleable) flags |= ResourceFlags.DenyShaderResource;

            var resourceDesc = new ResourceDesc
            {
                Dimension = ResourceDimension.Texture2D,
                Width = (ulong)Math.Max(1, desc.Width),
                Height = (uint)Math.Max(1, desc.Height),
                DepthOrArraySize = (ushort)layers,
                MipLevels = (ushort)mips,
                Format = depth
                    ? Dx12GpuFormats.ToTypelessDepth(desc.Format)
                    : Dx12GpuFormats.ToDxgi(desc.Format),
                SampleDesc = new SampleDesc(1u, 0u),
                Layout = TextureLayout.LayoutUnknown,
                Flags = flags,
            };

            var heap = new HeapProperties
            {
                Type = HeapType.Default,
                CreationNodeMask = 1u,
                VisibleNodeMask = 1u,
            };

            ResourceStates initial = depth ? ResourceStates.DepthWrite : ResourceStates.Common;

            ClearValue clear = default;
            ClearValue* clearPtr = null;
            if (depth)
            {
                clear.Format = Dx12GpuFormats.ToDxgi(desc.Format);
                clear.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1f };
                clearPtr = &clear;
            }
            else if ((desc.BindFlags & GpuBindFlags.RenderTarget) != 0)
            {
                clear.Format = Dx12GpuFormats.ToDxgi(desc.Format);
                clearPtr = &clear;
            }

            ComPtr<ID3D12Resource> resource = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateCommittedResource(
                &heap, HeapFlags.None, &resourceDesc, initial, clearPtr,
                SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)resource.GetAddressOf()));

            var texture = new TextureResource
            {
                Resource = resource,
                Width = Math.Max(1, desc.Width),
                Height = Math.Max(1, desc.Height),
                MipLevels = mips,
                ArrayLayers = layers,
                Format = desc.Format,
                State = initial,
                IsDepth = depth,
            };

            int id = _nextTexture++;
            _textures[id] = texture;

            if (sampleable)
            {
                // Allocated from a free list rather than keyed on the handle id: ids climb forever
                // as resources are created and released, so using one as a heap offset walks off
                // the end of the staging heap after enough churn.
                texture.SrvSlot = AllocateSrvSlot();
                CreateSrv(texture, texture.SrvSlot);
            }

            if (!initialData.IsEmpty)
            {
                UploadMipChain(texture, initialData);
            }

            return new GpuTextureHandle(id);
        }

        public void UpdateTexture(
            GpuTextureHandle handle, int x, int y, int width, int height,
            ReadOnlySpan<byte> data, int arraySlice = 0)
        {
            TextureResource texture = Require(_textures, handle.Id, nameof(handle));
            UploadTexture(texture, x, y, width, height, data, arraySlice);
        }

        public void ReleaseTexture(GpuTextureHandle handle)
        {
            if (!_textures.Remove(handle.Id, out TextureResource texture))
            {
                return;
            }

            if (texture.SrvSlot >= 0)
            {
                _freeSrvSlots.Push(texture.SrvSlot);
            }

            // A swap chain's back buffers are not ours to release.
            if (texture.External)
            {
                texture.Resource = default;
                return;
            }

            DeferResource(ref texture.Resource);
        }

        /// <summary>Takes a staging-heap slot for a texture's SRV, reusing released ones.</summary>
        private int AllocateSrvSlot()
        {
            if (_freeSrvSlots.Count > 0)
            {
                return _freeSrvSlots.Pop();
            }

            if (_nextSrvSlot >= StagingSrvCapacity)
            {
                throw new InvalidOperationException(
                    $"Direct3D 12 SRV staging heap exhausted at {StagingSrvCapacity} live sampleable "
                    + "textures. Raise StagingSrvCapacity.");
            }

            return _nextSrvSlot++;
        }

        public GpuSamplerHandle CreateSampler(in GpuSamplerDesc desc)
        {
            bool comparison = desc.CompareOp != GpuCompare.Never;
            var sampler = new SamplerDesc
            {
                Filter = Dx12GpuFormats.ToFilter(desc.Filter, comparison),
                AddressU = Dx12GpuFormats.ToAddress(desc.AddressU),
                AddressV = Dx12GpuFormats.ToAddress(desc.AddressV),
                AddressW = Dx12GpuFormats.ToAddress(desc.AddressW),
                MaxAnisotropy = (uint)Math.Clamp(desc.MaxAnisotropy, 1, 16),

                // Left at Never for a non-comparison sampler: the validation layer warns that
                // ComparisonFunc is ignored when the filter is not a comparison filter, and Never
                // is the honest way to say "unused" rather than claiming Always.
                ComparisonFunc = comparison
                    ? Dx12GpuFormats.ToComparison(desc.CompareOp)
                    : ComparisonFunc.Never,
                MinLOD = 0f,
                MaxLOD = float.MaxValue,
            };
            sampler.BorderColor[0] = desc.BorderColorR;
            sampler.BorderColor[1] = desc.BorderColorG;
            sampler.BorderColor[2] = desc.BorderColorB;
            sampler.BorderColor[3] = desc.BorderColorA;

            int id = _nextSampler++;
            _samplers[id] = sampler;
            return new GpuSamplerHandle(id);
        }

        public void ReleaseSampler(GpuSamplerHandle handle) => _samplers.Remove(handle.Id);

        // ── Render targets ──────────────────────────────────────────────────────

        public GpuRenderTargetHandle CreateRenderTarget(in GpuRenderTargetDesc desc)
        {
            ThrowIfDisposed();
            int colorCount = desc.ColorFormats?.Length ?? 0;
            var target = new RenderTargetResource
            {
                Colors = new GpuTextureHandle[colorCount],
                ColorFormats = desc.ColorFormats ?? Array.Empty<GpuFormat>(),
                DepthFormat = desc.DepthFormat,
                Width = desc.Width,
                Height = desc.Height,
            };

            if (colorCount > 0)
            {
                var heapDesc = new DescriptorHeapDesc
                {
                    Type = DescriptorHeapType.Rtv,
                    NumDescriptors = (uint)colorCount,
                };
                ComPtr<ID3D12DescriptorHeap> rtvHeap = default;
                SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateDescriptorHeap(
                    &heapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)rtvHeap.GetAddressOf()));
                target.RtvHeap = rtvHeap;

                uint stride = _runtime.Device.Handle->GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
                CpuDescriptorHandle rtv = rtvHeap.Handle->GetCPUDescriptorHandleForHeapStart();

                for (int i = 0; i < colorCount; i++)
                {
                    target.Colors[i] = CreateTexture(new GpuTextureDesc
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        MipLevels = 1,
                        ArrayLayers = 1,
                        Format = desc.ColorFormats[i],
                        BindFlags = GpuBindFlags.RenderTarget | GpuBindFlags.ShaderResource,
                        DebugName = desc.DebugName,
                    }, ReadOnlySpan<byte>.Empty);

                    TextureResource colour = _textures[target.Colors[i].Id];
                    _runtime.Device.Handle->CreateRenderTargetView(
                        colour.Resource, (RenderTargetViewDesc*)null, rtv);
                    rtv.Ptr += (nuint)stride;
                }
            }

            if (desc.DepthFormat != GpuFormat.Unknown)
            {
                target.Depth = CreateTexture(new GpuTextureDesc
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Format = desc.DepthFormat,
                    BindFlags = desc.DepthSampleable
                        ? GpuBindFlags.DepthStencil | GpuBindFlags.ShaderResource
                        : GpuBindFlags.DepthStencil,
                    DebugName = desc.DebugName,
                }, ReadOnlySpan<byte>.Empty);

                var dsvHeapDesc = new DescriptorHeapDesc
                {
                    Type = DescriptorHeapType.Dsv,
                    NumDescriptors = 1u,
                };
                ComPtr<ID3D12DescriptorHeap> dsvHeap = default;
                SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateDescriptorHeap(
                    &dsvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)dsvHeap.GetAddressOf()));
                target.DsvHeap = dsvHeap;

                var dsvDesc = new DepthStencilViewDesc
                {
                    Format = Dx12GpuFormats.ToDxgi(desc.DepthFormat),
                    ViewDimension = DsvDimension.Texture2D,
                };
                _runtime.Device.Handle->CreateDepthStencilView(
                    _textures[target.Depth.Id].Resource, &dsvDesc,
                    dsvHeap.Handle->GetCPUDescriptorHandleForHeapStart());
            }

            int id = _nextRenderTarget++;
            _renderTargets[id] = target;
            return new GpuRenderTargetHandle(id);
        }

        public GpuTextureHandle GetRenderTargetTexture(GpuRenderTargetHandle handle, int attachment = 0)
        {
            RenderTargetResource target = Require(_renderTargets, handle.Id, nameof(handle));
            return attachment >= 0 && attachment < target.Colors.Length
                ? target.Colors[attachment]
                : GpuTextureHandle.Invalid;
        }

        public GpuTextureHandle GetRenderTargetDepthTexture(GpuRenderTargetHandle handle) =>
            Require(_renderTargets, handle.Id, nameof(handle)).Depth;

        public void ReleaseRenderTarget(GpuRenderTargetHandle handle)
        {
            if (!_renderTargets.Remove(handle.Id, out RenderTargetResource target))
            {
                return;
            }

            foreach (GpuTextureHandle colour in target.Colors)
            {
                ReleaseTexture(colour);
            }

            ReleaseTexture(target.Depth);

            // Descriptor heaps are referenced by recorded commands exactly as resources are.
            DeferHeap(ref target.RtvHeap);
            DeferHeap(ref target.DsvHeap);
        }

        // ── Passes ──────────────────────────────────────────────────────────────

        public void BeginRenderPass(in GpuRenderPassDesc desc)
        {
            ThrowIfDisposed();
            EnsureRecording();

            CpuDescriptorHandle* rtvs = stackalloc CpuDescriptorHandle[8];
            uint rtvCount = 0;
            CpuDescriptorHandle dsv = default;
            bool hasDsv = false;

            if (desc.Target.IsValid)
            {
                RenderTargetResource target = Require(_renderTargets, desc.Target.Id, nameof(desc.Target));
                uint stride = _runtime.Device.Handle->GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);

                if (target.Colors.Length > 0)
                {
                    CpuDescriptorHandle handle = target.RtvHeap.Handle->GetCPUDescriptorHandleForHeapStart();
                    for (int i = 0; i < target.Colors.Length && i < 8; i++)
                    {
                        TransitionTexture(target.Colors[i], ResourceStates.RenderTarget);
                        rtvs[rtvCount++] = handle;
                        handle.Ptr += (nuint)stride;
                    }
                }

                if (target.Depth.IsValid)
                {
                    TransitionTexture(target.Depth, ResourceStates.DepthWrite);
                    dsv = target.DsvHeap.Handle->GetCPUDescriptorHandleForHeapStart();
                    hasDsv = true;
                }

                _activeTarget = desc.Target;
            }
            else
            {
                if (_activeSwapChain is not { IsReady: true })
                {
                    throw new InvalidOperationException("A back-buffer pass requires a ready swap chain.");
                }

                // A pass is about to draw into the current buffer, so the last present no longer
                // describes what readback should look at.
                _activeSwapChain.ClearPresented();
                EnsureBackBufferTexture();
                TransitionBackBuffer(ResourceStates.RenderTarget);

                // The composite pass samples this same depth buffer, which leaves it in
                // PixelShaderResource. Put it back before binding it as a DSV, or the next frame
                // asks D3D12 to depth-write a resource it believes is a shader resource.
                EnsureSwapChainDepthTexture(_activeSwapChain);
                TransitionSwapChainDepth(ResourceStates.DepthWrite);

                rtvs[rtvCount++] = _activeSwapChain.CurrentRtv;
                dsv = _activeSwapChain.Dsv;
                hasDsv = true;
                _activeTarget = GpuRenderTargetHandle.Invalid;
            }

            _frames.List->OMSetRenderTargets(
                rtvCount, rtvs, false, desc.HasDepth && hasDsv ? &dsv : null);

            int actions = desc.ColorActions?.Length ?? 0;
            float* colour = stackalloc float[4];
            for (int i = 0; i < rtvCount && i < actions; i++)
            {
                if (desc.ColorActions[i].Load != GpuLoadAction.Clear) continue;
                colour[0] = desc.ColorActions[i].ClearR;
                colour[1] = desc.ColorActions[i].ClearG;
                colour[2] = desc.ColorActions[i].ClearB;
                colour[3] = desc.ColorActions[i].ClearA;
                _frames.List->ClearRenderTargetView(rtvs[i], colour, 0u, (Silk.NET.Maths.Box2D<int>*)null);
            }

            if (desc.HasDepth && hasDsv && desc.DepthAction.Load == GpuLoadAction.Clear)
            {
                _frames.List->ClearDepthStencilView(
                    dsv, ClearFlags.Depth, desc.DepthAction.ClearR, 0, 0u,
                    (Silk.NET.Maths.Box2D<int>*)null);
            }
        }

        /// <summary>Nothing to close: passes here are plain OM bindings, not RenderPass objects.</summary>
        public void EndRenderPass()
        {
        }

        public void UnbindRenderTargets()
        {
            // D3D12 holds no persistent binding to drop, but the swap chain cannot resize while a
            // queued list still references a back buffer.
            _frames.WaitIdle();
            DrainDeferred(force: true);
            DisposeComputeResources();
            _activeTarget = GpuRenderTargetHandle.Invalid;
            if (_backBufferTexture.IsValid)
            {
                _textures.Remove(_backBufferTexture.Id);
                _backBufferTexture = GpuTextureHandle.Invalid;
            }
        }

        public void SetViewport(float x, float y, float width, float height, float minZ = 0f, float maxZ = 1f)
        {
            EnsureRecording();
            var viewport = new Viewport
            {
                TopLeftX = x, TopLeftY = y, Width = width, Height = height,
                MinDepth = minZ, MaxDepth = maxZ,
            };
            _frames.List->RSSetViewports(1u, &viewport);

            // D3D12 always clips to the scissor rect, and its default is empty — omit this and
            // nothing is drawn at all, with no error anywhere to say why.
            var scissor = new Silk.NET.Maths.Box2D<int>(
                (int)x, (int)y, (int)(x + width), (int)(y + height));
            _frames.List->RSSetScissorRects(1u, &scissor);
        }

        public void SetScissor(int x, int y, int width, int height)
        {
            EnsureRecording();
            var rect = new Silk.NET.Maths.Box2D<int>(x, y, x + width, y + height);
            _frames.List->RSSetScissorRects(1u, &rect);
        }

        // ── Pipeline state ──────────────────────────────────────────────────────

        public void SetShaderProgram(GpuShaderProgramHandle handle) => _pipeline.Program = handle;

        public void SetVertexLayout(GpuVertexLayoutHandle handle) => _pipeline.VertexLayout = handle;

        public void SetPrimitiveTopology(GpuPrimitiveTopology topology) => _pipeline.Topology = topology;

        public void SetBlendState(in GpuBlendState state) => _pipeline.Blend = state;

        public void SetDepthState(in GpuDepthState state) => _pipeline.Depth = state;

        public void SetRasterState(in GpuRasterState state) => _pipeline.Raster = state;

        public GpuShaderProgramHandle CreateShaderProgram(in GpuShaderProgramDesc desc)
        {
            int id = _nextShaderProgram++;
            _shaderPrograms[id] = new ShaderProgramResource
            {
                VertexShader = desc.VertexShader,
                PixelShader = desc.PixelShader,
                ComputeShader = desc.ComputeShader,
            };
            return new GpuShaderProgramHandle(id);
        }

        public void ReleaseShaderProgram(GpuShaderProgramHandle handle)
        {
            if (!_shaderPrograms.Remove(handle.Id, out ShaderProgramResource program)) return;
            Defer((nint)program.ComputePipeline.Handle);
            program.ComputePipeline = default;
        }

        public GpuVertexLayoutHandle CreateVertexLayout(
            in GpuVertexLayoutDesc desc, GpuShaderProgramHandle program)
        {
            // D3D12 folds the input layout into the PSO, so this only records the description;
            // there is nothing to validate against the shader until a pipeline is built.
            int id = _nextVertexLayout++;
            _vertexLayouts[id] = desc;
            return new GpuVertexLayoutHandle(id);
        }

        public void ReleaseVertexLayout(GpuVertexLayoutHandle handle) => _vertexLayouts.Remove(handle.Id);

        // ── Bindings ────────────────────────────────────────────────────────────

        public void SetConstantBuffer(GpuShaderStage stage, int bRegister, GpuBufferHandle handle)
        {
            if ((stage & GpuShaderStage.Compute) != 0)
            {
                if ((uint)bRegister >= _csCbv.Length) throw new ArgumentOutOfRangeException(nameof(bRegister));
                _csCbv[bRegister] = handle;
            }
            if ((stage & GpuShaderStage.Vertex) != 0 && bRegister < _vsCbv.Length) _vsCbv[bRegister] = handle;
            if ((stage & GpuShaderStage.Pixel) != 0 && bRegister < _psCbv.Length) _psCbv[bRegister] = handle;
        }

        public void SetTexture(GpuShaderStage stage, int tRegister, GpuTextureHandle handle)
        {
            GpuTextureHandle effective = handle.IsValid ? handle : _dummyTexture;
            if ((stage & GpuShaderStage.Vertex) != 0 && tRegister < _vsSrv.Length)
            {
                _vsSrv[tRegister] = effective;
                _vsStructured[tRegister] = GpuBufferHandle.Invalid;
            }

            if ((stage & GpuShaderStage.Pixel) != 0 && tRegister < _psSrv.Length)
            {
                _psSrv[tRegister] = effective;
                _psStructured[tRegister] = GpuBufferHandle.Invalid;
            }
        }

        public void ClearTexture(GpuShaderStage stage, int tRegister)
        {
            if ((stage & GpuShaderStage.Vertex) != 0 && tRegister < _vsSrv.Length)
            {
                _vsSrv[tRegister] = GpuTextureHandle.Invalid;
                _vsStructured[tRegister] = GpuBufferHandle.Invalid;
            }

            if ((stage & GpuShaderStage.Pixel) != 0 && tRegister < _psSrv.Length)
            {
                _psSrv[tRegister] = GpuTextureHandle.Invalid;
                _psStructured[tRegister] = GpuBufferHandle.Invalid;
            }
        }

        public void SetStructuredBuffer(GpuShaderStage stage, int tRegister, GpuBufferHandle handle)
        {
            if ((stage & GpuShaderStage.Compute) != 0)
            {
                if ((uint)tRegister >= _csSrv.Length) throw new ArgumentOutOfRangeException(nameof(tRegister));
                _csSrv[tRegister] = handle;
            }
            if ((stage & GpuShaderStage.Vertex) != 0 && tRegister < _vsStructured.Length)
            {
                _vsStructured[tRegister] = handle;
                _vsSrv[tRegister] = GpuTextureHandle.Invalid;
            }

            if ((stage & GpuShaderStage.Pixel) != 0 && tRegister < _psStructured.Length)
            {
                _psStructured[tRegister] = handle;
                _psSrv[tRegister] = GpuTextureHandle.Invalid;
            }
        }

        public void SetSampler(GpuShaderStage stage, int sRegister, GpuSamplerHandle handle)
        {
            if ((stage & GpuShaderStage.Vertex) != 0 && sRegister < _vsSampler.Length) _vsSampler[sRegister] = handle;
            if ((stage & GpuShaderStage.Pixel) != 0 && sRegister < _psSampler.Length) _psSampler[sRegister] = handle;
        }

        public void SetVertexBuffer(int slot, GpuBufferHandle handle, int stride, int offset = 0)
        {
            if (slot < 0 || slot >= _vertexBuffers.Length) return;
            _vertexBuffers[slot] = handle;
            _vertexStrides[slot] = stride;
            _vertexOffsets[slot] = offset;
        }

        public void SetIndexBuffer(GpuBufferHandle handle, GpuIndexFormat format, int offset = 0)
        {
            _indexBuffer = handle;
            _indexFormat = format;
            _indexOffset = offset;
        }

        // ── Draws ───────────────────────────────────────────────────────────────

        public void Draw(int vertexCount, int startVertex = 0)
        {
            if (!PrepareDraw()) return;
            _frames.List->DrawInstanced((uint)vertexCount, 1u, (uint)startVertex, 0u);
        }

        public void DrawIndexed(int indexCount, int startIndex = 0, int baseVertex = 0)
        {
            if (!PrepareDraw()) return;
            _frames.List->DrawIndexedInstanced((uint)indexCount, 1u, (uint)startIndex, baseVertex, 0u);
        }

        public void DrawIndexedInstanced(
            int indexCountPerInstance, int instanceCount,
            int startIndex = 0, int baseVertex = 0, int startInstance = 0)
        {
            if (!PrepareDraw()) return;
            _frames.List->DrawIndexedInstanced(
                (uint)indexCountPerInstance, (uint)instanceCount,
                (uint)startIndex, baseVertex, (uint)startInstance);
        }

        // ── Timestamps ──────────────────────────────────────────────────────────

        /// <remarks>
        /// Slots come from a free list, not a monotonic counter. The contract calls these
        /// "ring-buffered" and the D3D11 reference honours that by destroying its query objects on
        /// resolve; a counter that only ever climbs exhausts the heap after
        /// <see cref="MaxTimestampScopes"/> frames — about four seconds of play — after which every
        /// call returns Invalid and the reported GPU time freezes at whatever it last was, silently
        /// and for the life of the process.
        /// </remarks>
        public GpuQueryHandle BeginTimestampScope()
        {
            if (!_frames.IsRecording)
            {
                return GpuQueryHandle.Invalid;
            }

            int index;
            if (_freeTimestampSlots.Count > 0)
            {
                index = _freeTimestampSlots.Pop();
            }
            else if (_nextTimestampIndex + 2 <= MaxTimestampScopes * 2)
            {
                index = _nextTimestampIndex;
                _nextTimestampIndex += 2;
            }
            else
            {
                // Every slot is genuinely in flight. Skipping this frame's timing is correct and
                // self-correcting, unlike wedging permanently.
                return GpuQueryHandle.Invalid;
            }

            _frames.List->EndQuery(_timestampHeap, QueryType.Timestamp, (uint)index);

            int id = _nextQuery++;
            _queries[id] = new TimestampResource { Index = index };
            return new GpuQueryHandle(id);
        }

        public void EndTimestampScope(GpuQueryHandle handle)
        {
            if (!handle.IsValid || !_queries.TryGetValue(handle.Id, out TimestampResource query)) return;
            if (!_frames.IsRecording) return;

            _frames.List->EndQuery(_timestampHeap, QueryType.Timestamp, (uint)(query.Index + 1));
            _frames.List->ResolveQueryData(
                _timestampHeap, QueryType.Timestamp, (uint)query.Index, 2u,
                _timestampReadback, (ulong)(query.Index * sizeof(ulong)));
            query.Ended = true;
        }

        public bool TryResolveTimestamp(GpuQueryHandle handle, out double milliseconds)
        {
            milliseconds = 0;
            if (!handle.IsValid || !_queries.TryGetValue(handle.Id, out TimestampResource query) || !query.Ended)
            {
                return false;
            }

            // Non-stalling by contract: the caller asks again next frame rather than blocking.
            void* mapped = null;
            var range = new Silk.NET.Direct3D12.Range
            {
                Begin = 0,
                End = (nuint)(MaxTimestampScopes * 2 * sizeof(ulong)),
            };
            if (_timestampReadback.Handle->Map(0u, &range, &mapped) < 0)
            {
                return false;
            }

            ulong* values = (ulong*)mapped;
            ulong begin = values[query.Index];
            ulong end = values[query.Index + 1];
            var empty = new Silk.NET.Direct3D12.Range();
            _timestampReadback.Handle->Unmap(0u, &empty);

            if (end <= begin || _timestampFrequency == 0)
            {
                // Not ready yet, or a scope that will never produce a usable span. Retire the
                // latter: returning false without releasing the slot strands it forever, which is
                // the same leak by a quieter route.
                if (_timestampFrequency != 0 && end != 0 && end <= begin)
                {
                    RetireTimestamp(handle.Id, query);
                }

                return false;
            }

            milliseconds = (end - begin) * 1000.0 / _timestampFrequency;
            RetireTimestamp(handle.Id, query);
            return true;
        }

        /// <summary>Returns a resolved scope's heap slots to the free list.</summary>
        private void RetireTimestamp(int id, TimestampResource query)
        {
            _queries.Remove(id);
            _freeTimestampSlots.Push(query.Index);
        }

        // ── Readback ────────────────────────────────────────────────────────────

        public bool TryReadTexture(GpuTextureHandle handle, out int width, out int height, out byte[] bgra)
        {
            width = 0;
            height = 0;
            bgra = null;

            if (!_textures.TryGetValue(handle.Id, out TextureResource texture))
            {
                return false;
            }

            // Drained here as well as at EndFrame: readback is where a removal has been surfacing,
            // and a message printed after the throw is a message nobody reads.
            _runtime.DrainDiagnostics();
            string removed = _runtime.DescribeRemoval();
            if (removed is not null)
            {
                throw new InvalidOperationException(
                    $"Direct3D 12 device was already removed before readback (frame {_framesSubmitted}): {removed}");
            }

            width = texture.Width;
            height = texture.Height;

            int unpaddedRow = width * 4;
            int paddedRow = Align(unpaddedRow, TextureRowAlignment);
            int totalBytes = paddedRow * height;

            ComPtr<ID3D12Resource> readback = CreateCommittedBuffer(
                totalBytes, HeapType.Readback, ResourceStates.CopyDest, ResourceFlags.None);

            EnsureRecording();
            ResourceStates previous = texture.State;
            TransitionResource(texture.Resource, ref texture.State, ResourceStates.CopySource);

            var destination = new TextureCopyLocation
            {
                PResource = readback,
                Type = TextureCopyType.PlacedFootprint,
            };
            destination.Anonymous.PlacedFootprint = new PlacedSubresourceFootprint
            {
                Offset = 0ul,
                Footprint = new SubresourceFootprint
                {
                    Format = Dx12GpuFormats.ToDxgi(texture.Format),
                    Width = (uint)width,
                    Height = (uint)height,
                    Depth = 1u,
                    RowPitch = (uint)paddedRow,
                },
            };

            var source = new TextureCopyLocation
            {
                PResource = texture.Resource,
                Type = TextureCopyType.SubresourceIndex,
            };
            source.Anonymous.SubresourceIndex = 0u;

            _frames.List->CopyTextureRegion(&destination, 0u, 0u, 0u, &source, (Box*)null);
            TransitionResource(texture.Resource, ref texture.State, previous);

            // The copy has to have executed before the CPU can read it.
            _frames.FlushAndReopen();
            RebindListState();

            void* mapped = null;
            var range = new Silk.NET.Direct3D12.Range { Begin = 0, End = (nuint)totalBytes };
            if (readback.Handle->Map(0u, &range, &mapped) < 0)
            {
                readback.Dispose();
                return false;
            }

            bgra = new byte[unpaddedRow * height];
            byte* src = (byte*)mapped;
            bool swizzle = texture.Format is GpuFormat.R8G8B8A8UNorm or GpuFormat.R8G8B8A8UNormSrgb;

            for (int row = 0; row < height; row++)
            {
                byte* line = src + ((long)row * paddedRow);
                int destinationOffset = row * unpaddedRow;
                for (int column = 0; column < width; column++)
                {
                    int s = column * 4;
                    int d = destinationOffset + s;
                    if (swizzle)
                    {
                        // The contract is BGRA; an RGBA surface has to be reordered, not memcpy'd.
                        bgra[d + 0] = line[s + 2];
                        bgra[d + 1] = line[s + 1];
                        bgra[d + 2] = line[s + 0];
                        bgra[d + 3] = line[s + 3];
                    }
                    else
                    {
                        bgra[d + 0] = line[s + 0];
                        bgra[d + 1] = line[s + 1];
                        bgra[d + 2] = line[s + 2];
                        bgra[d + 3] = line[s + 3];
                    }
                }
            }

            var emptyRange = new Silk.NET.Direct3D12.Range();
            readback.Handle->Unmap(0u, &emptyRange);

            // Safe to destroy immediately: FlushAndReopen waited for the copy that used it.
            readback.Dispose();
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _frames.WaitIdle();

            // Everything queued for deletion is now safe: the GPU has finished every submission.
            DrainDeferred(force: true);

            foreach (nint pso in _pipelines.Values)
            {
                if (pso != 0)
                {
                    ((ID3D12PipelineState*)pso)->Release();
                }
            }

            _pipelines.Clear();

            foreach (BufferResource buffer in _buffers.Values)
            {
                buffer.Resource.Dispose();
                if (buffer.MappedScratch != null) NativeMemory.Free(buffer.MappedScratch);
            }

            _buffers.Clear();

            foreach (TextureResource texture in _textures.Values)
            {
                if (!texture.External) texture.Resource.Dispose();
            }

            _textures.Clear();

            foreach (RenderTargetResource target in _renderTargets.Values)
            {
                target.RtvHeap.Dispose();
                target.DsvHeap.Dispose();
            }

            _renderTargets.Clear();

            for (int i = 0; i < _uploadRing.Length; i++)
            {
                _uploadRing[i].Dispose();
            }

            _timestampReadback.Dispose();
            _timestampHeap.Dispose();
            _bindings.Dispose();
            _frames.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Dx12GpuDevice));
        }

        private void EnsureRecording()
        {
            if (!_frames.IsRecording)
            {
                BeginFrame();
            }
        }

        /// <summary>
        /// Restores the state a command list loses when it is reset.
        /// </summary>
        /// <remarks>
        /// <c>Reset</c> clears the root signature, the bound descriptor heaps and the pipeline
        /// state. Anything that reopens the list mid-frame — a readback flush, for instance — has
        /// to put them back, or the next draw reads its descriptor tables from nothing.
        /// </remarks>
        private void RebindListState()
        {
            if (!_frames.IsRecording)
            {
                return;
            }

            _bindings.BindHeaps(_frames.List);
            _frames.List->SetGraphicsRootSignature(_bindings.RootSignature);
            _boundPipeline = 0;
        }

        private static T Require<T>(Dictionary<int, T> map, int id, string name) =>
            map.TryGetValue(id, out T value)
                ? value
                : throw new ArgumentException($"Unknown {name} handle {id}.", name);

        private static int Align(int value, int alignment) =>
            (value + alignment - 1) / alignment * alignment;

        /// <summary>
        /// A CBV's SizeInBytes must be a multiple of 256, so the ring slice we hand the view has
        /// to be that wide too — otherwise the extra bytes are whatever the previous occupant left
        /// and a shader that reads a padded cbuffer tail picks up garbage matrices.
        /// </summary>
        private static int ConstantViewSize(BufferResource buffer, int writtenBytes)
        {
            int size = Math.Max(writtenBytes, 1);
            return (buffer.BindFlags & GpuBindFlags.ConstantBuffer) != 0
                ? Align(size, ConstantAlignment)
                : size;
        }

        /// <summary>Adapts <see cref="Dx12SwapChain"/> to the backend-neutral surface contract.</summary>
        private sealed class SwapChainAdapter : IGpuSwapChain
        {
            private readonly Dx12GpuDevice _device;
            private readonly Dx12SwapChain _chain;

            public SwapChainAdapter(Dx12GpuDevice device, Dx12SwapChain chain)
            {
                _device = device;
                _chain = chain;
            }

            public int Width => _chain.Width;

            public int Height => _chain.Height;

            public bool IsReady => _chain.IsReady;

            public GpuTextureHandle DepthTexture
            {
                get
                {
                    // Published on demand: the renderer reads this before the first pass of a frame,
                    // which is earlier than any back-buffer acquisition would create it.
                    _device.EnsureSwapChainDepthTexture(_chain);
                    return _device._depthTexture;
                }
            }

            public bool Resize(int width, int height)
            {
                _device.UnbindRenderTargets();

                // Retire the published handle first: the resize destroys the depth resource behind
                // it, and a descriptor left pointing at freed memory is a use-after-free waiting for
                // the next sample.
                _device.ReleaseSwapChainDepthTexture();
                return _chain.Resize(width, height);
            }

            public void Present(bool vsync) => _chain.Present(vsync);

            public GpuTextureHandle AcquireBackBuffer()
            {
                // Readback is the only caller once a frame has been presented, and it wants the
                // frame that was shown — not the buffer the next one will be drawn into.
                _device.EnsureBackBufferTexture(preferPresented: true);
                return _device._backBufferTexture;
            }

            public void Dispose()
            {
                _device.ReleaseSwapChainDepthTexture();
                if (ReferenceEquals(_device._activeSwapChain, _chain))
                {
                    _device._activeSwapChain = null;
                }

                _chain.Dispose();
            }
        }
    }
}
