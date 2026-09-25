using System;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;

namespace Genesis.Rendering.SilkNet.DX12
{
    /// <summary>
    /// A window's presentable surface: flip-model swap chain, its render targets, and a depth buffer.
    /// </summary>
    /// <remarks>
    /// <para><b>Every COM pointer here is acquired through <c>GetAddressOf()</c>, never
    /// <c>new ComPtr&lt;T&gt;(raw)</c>.</b> That constructor AddRefs what it wraps, leaving a
    /// refcount of two, and this is the exact shape of the bug that made the D3D11 swap chain
    /// unresizable for the life of the process: a render-target view survived its own Dispose,
    /// <c>ResizeBuffers</c> returned <c>DXGI_ERROR_INVALID_CALL</c> forever after, and DXGI hid the
    /// failure by stretching a stale back buffer instead of reporting it. Writing straight into the
    /// ComPtr's own storage takes ownership of the single reference the API returned.</para>
    ///
    /// <para>D3D12 is stricter than D3D11 was: resizing requires not only that every back-buffer
    /// reference is released, but that no queued command list still refers to one. Hence the
    /// <c>WaitIdle</c> in <see cref="Resize"/> — it is not defensive, it is required.</para>
    /// </remarks>
    internal sealed unsafe class Dx12SwapChain : IDisposable
    {
        private const Format BackBufferFormat = Format.FormatR8G8B8A8Unorm;
        private const Format DepthFormat = Format.FormatD32Float;
        private const int BufferCount = Dx12FrameRing.FramesInFlight;

        private readonly Dx12Runtime _runtime;
        private readonly Dx12FrameRing _frames;
        private readonly IntPtr _windowHandle;

        private ComPtr<IDXGISwapChain3> _swapChain;
        private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
        private ComPtr<ID3D12DescriptorHeap> _dsvHeap;
        private readonly ComPtr<ID3D12Resource>[] _backBuffers = new ComPtr<ID3D12Resource>[BufferCount];
        private ComPtr<ID3D12Resource> _depthBuffer;
        private uint _rtvStride;
        private bool _disposed;

        public Dx12SwapChain(Dx12Runtime runtime, Dx12FrameRing frames, IntPtr windowHandle, int width, int height)
        {
            _runtime = runtime;
            _frames = frames;
            _windowHandle = windowHandle;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);

            var desc = new SwapChainDesc1
            {
                Width = (uint)Width,
                Height = (uint)Height,
                Format = BackBufferFormat,
                Stereo = false,
                SampleDesc = new SampleDesc(1u, 0u),
                BufferUsage = DXGI.UsageRenderTargetOutput,
                BufferCount = BufferCount,
                Scaling = Scaling.None,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Unspecified,
                Flags = (uint)SwapChainFlag.AllowModeSwitch,
            };

            ComPtr<IDXGISwapChain1> created = default;
            SilkMarshal.ThrowHResult(_runtime.Factory.Handle->CreateSwapChainForHwnd(
                (IUnknown*)_runtime.Queue.Handle,
                _windowHandle,
                &desc,
                (SwapChainFullscreenDesc*)null,
                (IDXGIOutput*)null,
                created.GetAddressOf()));

            // Alt+Enter belongs to the application, not DXGI — a full-screen transition it performs
            // behind our back resizes buffers nothing here is expecting to be resized.
            _runtime.Factory.Handle->MakeWindowAssociation(_windowHandle, 1u /* DXGI_MWA_NO_ALT_ENTER */);

            ComPtr<IDXGISwapChain3> chain3 = default;
            SilkMarshal.ThrowHResult(created.Handle->QueryInterface(
                SilkMarshal.GuidPtrOf<IDXGISwapChain3>(), (void**)chain3.GetAddressOf()));
            created.Dispose();
            _swapChain = chain3;

            var rtvHeapDesc = new DescriptorHeapDesc
            {
                Type = DescriptorHeapType.Rtv,
                NumDescriptors = BufferCount,
                Flags = DescriptorHeapFlags.None,
            };
            ComPtr<ID3D12DescriptorHeap> rtvHeap = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateDescriptorHeap(
                &rtvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)rtvHeap.GetAddressOf()));
            _rtvHeap = rtvHeap;
            _rtvStride = _runtime.Device.Handle->GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);

            var dsvHeapDesc = new DescriptorHeapDesc
            {
                Type = DescriptorHeapType.Dsv,
                NumDescriptors = 1u,
                Flags = DescriptorHeapFlags.None,
            };
            ComPtr<ID3D12DescriptorHeap> dsvHeap = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateDescriptorHeap(
                &dsvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)dsvHeap.GetAddressOf()));
            _dsvHeap = dsvHeap;

            AcquireBuffers();
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public bool IsReady => !_disposed && _swapChain.Handle != null && Width > 0 && Height > 0;

        /// <summary>Index of the buffer the next present will show.</summary>
        public uint CurrentBackBufferIndex => _swapChain.Handle->GetCurrentBackBufferIndex();

        public ID3D12Resource* CurrentBackBuffer => _backBuffers[CurrentBackBufferIndex].Handle;

        /// <summary>
        /// The buffer most recently handed to <see cref="Present"/>, or -1 if none since the last
        /// pass rendered into the surface.
        /// </summary>
        /// <remarks>
        /// Flip-model presentation rotates buffers: once <c>Present</c> returns,
        /// <see cref="CurrentBackBufferIndex"/> names the buffer the <i>next</i> frame will draw
        /// into, not the one just shown. Reading back "the current back buffer" after presenting
        /// therefore samples a buffer holding an older frame — or nothing but the clear, which is
        /// exactly how a capture comes out blank while the draw calls all reported success. D3D11
        /// hid this rotation behind <c>GetBuffer(0)</c>; D3D12 does not.
        /// </remarks>
        public int PresentedBackBufferIndex { get; private set; } = -1;

        public ID3D12Resource* BackBufferAt(uint index) => _backBuffers[index].Handle;

        /// <summary>Forgets the last present, because a new pass is drawing into the surface.</summary>
        public void ClearPresented() => PresentedBackBufferIndex = -1;

        public ID3D12Resource* DepthBuffer => _depthBuffer.Handle;

        public CpuDescriptorHandle CurrentRtv
        {
            get
            {
                CpuDescriptorHandle handle = _rtvHeap.Handle->GetCPUDescriptorHandleForHeapStart();
                handle.Ptr += CurrentBackBufferIndex * _rtvStride;
                return handle;
            }
        }

        public CpuDescriptorHandle Dsv => _dsvHeap.Handle->GetCPUDescriptorHandleForHeapStart();

        public static Format ColorFormat => BackBufferFormat;

        public static Format DepthStencilFormat => DepthFormat;

        /// <summary>
        /// Resizes the surface, or returns false leaving the existing buffers usable.
        /// </summary>
        public bool Resize(int width, int height)
        {
            if (_disposed || width <= 0 || height <= 0)
            {
                return false;
            }

            if (width == Width && height == Height)
            {
                return true;
            }

            // Nothing may reference a back buffer: not a descriptor, not this object's own ComPtrs,
            // and not a command list the GPU has yet to finish.
            _frames.WaitIdle();
            ReleaseBuffers();
            PresentedBackBufferIndex = -1;

            int hr = _swapChain.Handle->ResizeBuffers(
                BufferCount, (uint)width, (uint)height, BackBufferFormat,
                (uint)SwapChainFlag.AllowModeSwitch);

            if (hr < 0)
            {
                // The old buffers are gone, so re-acquire at the previous size rather than leaving
                // the surface in a state where nothing can be presented.
                AcquireBuffers();
                return false;
            }

            Width = width;
            Height = height;
            AcquireBuffers();
            return true;
        }

        public void Present(bool vsync)
        {
            if (!IsReady)
            {
                return;
            }

            // Recorded before presenting, while the index still names the buffer being shown.
            PresentedBackBufferIndex = (int)CurrentBackBufferIndex;
            _swapChain.Handle->Present(vsync ? 1u : 0u, 0u);
        }

        private void AcquireBuffers()
        {
            CpuDescriptorHandle rtv = _rtvHeap.Handle->GetCPUDescriptorHandleForHeapStart();
            for (uint i = 0; i < BufferCount; i++)
            {
                ComPtr<ID3D12Resource> buffer = default;
                SilkMarshal.ThrowHResult(_swapChain.Handle->GetBuffer(
                    i, SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)buffer.GetAddressOf()));
                _backBuffers[i] = buffer;

                _runtime.Device.Handle->CreateRenderTargetView(buffer, (RenderTargetViewDesc*)null, rtv);
                rtv.Ptr += _rtvStride;
            }

            CreateDepthBuffer();
        }

        private void CreateDepthBuffer()
        {
            var heapProperties = new HeapProperties
            {
                Type = HeapType.Default,
                CPUPageProperty = CpuPageProperty.Unknown,
                MemoryPoolPreference = MemoryPool.Unknown,
                CreationNodeMask = 1u,
                VisibleNodeMask = 1u,
            };

            var resourceDesc = new ResourceDesc
            {
                Dimension = ResourceDimension.Texture2D,
                Alignment = 0ul,
                Width = (ulong)Width,
                Height = (uint)Height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = DepthFormat,
                SampleDesc = new SampleDesc(1u, 0u),
                Layout = TextureLayout.LayoutUnknown,
                Flags = ResourceFlags.AllowDepthStencil,
            };

            var clear = new ClearValue { Format = DepthFormat };
            clear.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1f, Stencil = 0 };

            ComPtr<ID3D12Resource> depth = default;
            SilkMarshal.ThrowHResult(_runtime.Device.Handle->CreateCommittedResource(
                &heapProperties,
                HeapFlags.None,
                &resourceDesc,
                ResourceStates.DepthWrite,
                &clear,
                SilkMarshal.GuidPtrOf<ID3D12Resource>(),
                (void**)depth.GetAddressOf()));
            _depthBuffer = depth;

            var dsvDesc = new DepthStencilViewDesc
            {
                Format = DepthFormat,
                ViewDimension = DsvDimension.Texture2D,
                Flags = DsvFlags.None,
            };
            _runtime.Device.Handle->CreateDepthStencilView(
                _depthBuffer, &dsvDesc, _dsvHeap.Handle->GetCPUDescriptorHandleForHeapStart());
        }

        private void ReleaseBuffers()
        {
            for (int i = 0; i < BufferCount; i++)
            {
                _backBuffers[i].Dispose();
                _backBuffers[i] = default;
            }

            _depthBuffer.Dispose();
            _depthBuffer = default;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _frames.WaitIdle();
            ReleaseBuffers();
            _rtvHeap.Dispose();
            _rtvHeap = default;
            _dsvHeap.Dispose();
            _dsvHeap = default;
            _swapChain.Dispose();
            _swapChain = default;
        }
    }
}
