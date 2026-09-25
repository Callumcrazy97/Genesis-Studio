using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using D3D11Viewport = Silk.NET.Direct3D11.Viewport;

namespace Genesis.Rendering.SilkNet.DX11
{
    /// <summary>
    /// D3D11 implementation of the backend-neutral GPU contract. It deliberately owns a separate
    /// handle registry instead of exposing COM pointers; the shared sprite and forward renderers
    /// therefore stay reusable without allowing DX11 types to leak back into them.
    /// </summary>
    public sealed unsafe class Dx11GpuDevice : IGpuDevice
    {
        private sealed class BufferResource
        {
            public ID3D11Buffer* Buffer;
            public ID3D11ShaderResourceView* Srv;
            public int SizeBytes;
            public GpuBufferUsage Usage;
            public bool Mapped;
            public byte[] Shadow;
            public bool ShadowValid;
        }

        private sealed class TextureResource
        {
            public ID3D11Texture2D* Texture;
            public ID3D11ShaderResourceView* Srv;
            public int Width;
            public int Height;
            public int ArrayLayers;
            public GpuFormat Format;
            public GpuBufferUsage Usage;
            public int MipLevels = 1;
            public byte[] UploadShadow;
        }

        private sealed class ShaderProgramResource
        {
            public ID3D11VertexShader* VertexShader;
            public ID3D11PixelShader* PixelShader;
            public ID3D11ComputeShader* ComputeShader;
            public byte[] VertexBytecode = Array.Empty<byte>();
        }

        private sealed class VertexLayoutResource
        {
            public ID3D11InputLayout* Layout;
            public GpuVertexLayoutDesc Desc;
        }

        private sealed class RenderTargetResource
        {
            public GpuTextureHandle[] Colors = Array.Empty<GpuTextureHandle>();
            public nint[] Rtvs = Array.Empty<nint>();
            public GpuTextureHandle Depth;
            public ID3D11DepthStencilView* Dsv;
            public bool HasStencil;
        }

        private sealed class TimestampResource
        {
            public ID3D11Query* Disjoint;
            public ID3D11Query* Begin;
            public ID3D11Query* End;
            public bool Ended;
        }

        private sealed class SwapChainAdapter : IGpuSwapChain
        {
            private readonly Dx11GpuDevice _device;
            internal readonly Dx11SwapChain Native;
            private GpuTextureHandle _backBuffer;
            private GpuTextureHandle _depth;
            private bool _disposed;

            internal SwapChainAdapter(Dx11GpuDevice device, IntPtr hwnd, int width, int height)
            {
                _device = device;
                Native = new Dx11SwapChain(device._runtime, hwnd, width, height);
                RefreshDepthHandle();
            }

            public int Width => Native.Width;
            public int Height => Native.Height;
            public bool IsReady => !_disposed && Native.IsReady;
            public GpuTextureHandle DepthTexture => _depth;

            public bool Resize(int width, int height)
            {
                ThrowIfDisposed();
                ReleaseExternalHandles();
                bool resized = Native.Resize(width, height);
                RefreshDepthHandle();
                return resized;
            }

            public void Present(bool vsync)
            {
                ThrowIfDisposed();
                ReleaseBackBuffer();
                Native.Present(vsync);
            }

            public GpuTextureHandle AcquireBackBuffer()
            {
                ThrowIfDisposed();
                _device._activeSwapChain = this;
                ReleaseBackBuffer();
                ID3D11Texture2D* texture = Native.AcquireBackBufferTexture();
                if (texture == null) return GpuTextureHandle.Invalid;
                _backBuffer = _device.AddExternalTexture(
                    texture, null, Width, Height, GpuFormat.B8G8R8A8UNorm);
                return _backBuffer;
            }

            internal void EndFrame() => ReleaseBackBuffer();

            private void RefreshDepthHandle()
            {
                ID3D11Texture2D* texture = Native.DepthTextureHandle;
                ID3D11ShaderResourceView* srv = (ID3D11ShaderResourceView*)Native.DepthSrv;
                if (texture == null) return;
                ((IUnknown*)texture)->AddRef();
                if (srv != null) ((IUnknown*)srv)->AddRef();
                _depth = _device.AddExternalTexture(
                    texture, srv, Width, Height, GpuFormat.D24UNormS8UInt);
            }

            private void ReleaseBackBuffer()
            {
                if (!_backBuffer.IsValid) return;
                _device.ReleaseTexture(_backBuffer);
                _backBuffer = GpuTextureHandle.Invalid;
            }

            private void ReleaseExternalHandles()
            {
                ReleaseBackBuffer();
                if (_depth.IsValid) _device.ReleaseTexture(_depth);
                _depth = GpuTextureHandle.Invalid;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                ReleaseExternalHandles();
                if (ReferenceEquals(_device._activeSwapChain, this))
                    _device._activeSwapChain = null;
                Native.Dispose();
                _device._swapChains.Remove(this);
            }

            private void ThrowIfDisposed()
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SwapChainAdapter));
            }
        }

        private readonly SilkNetDx11Runtime _runtime;
        private readonly Dictionary<int, BufferResource> _buffers = new();
        private readonly Dictionary<int, TextureResource> _textures = new();
        private readonly Dictionary<int, nint> _samplers = new();
        private readonly Dictionary<int, RenderTargetResource> _renderTargets = new();
        private readonly Dictionary<int, ShaderProgramResource> _shaderPrograms = new();
        private readonly Dictionary<int, VertexLayoutResource> _vertexLayouts = new();
        private readonly Dictionary<int, TimestampResource> _queries = new();
        private readonly Dictionary<GpuBlendState, nint> _blendStates = new();
        private readonly Dictionary<GpuDepthState, nint> _depthStates = new();
        private readonly Dictionary<GpuRasterState, nint> _rasterStates = new();
        private readonly HashSet<SwapChainAdapter> _swapChains = new();

        private int _nextBuffer = 1;
        private int _nextTexture = 1;
        private int _nextSampler = 1;
        private int _nextRenderTarget = 1;
        private int _nextShaderProgram = 1;
        private int _nextVertexLayout = 1;
        private int _nextQuery = 1;
        private bool _disposed;
        private SwapChainAdapter _activeSwapChain;
        private GpuRenderTargetHandle _activeTarget;
        private GpuTextureHandle _dummyTexture;
        private GpuPipelineKey _pipeline;

        public Dx11GpuDevice()
        {
            _runtime = SilkNetDx11Runtime.EnsureDevice();
            _pipeline.Topology = GpuPrimitiveTopology.TriangleList;
            _pipeline.Blend = GpuBlendState.Opaque;
            _pipeline.Depth = GpuDepthState.Default;
            _pipeline.Raster = GpuRasterState.Default;
        }

        public string BackendName => "Direct3D 11";
        public string AdapterName => "Default DXGI adapter";
        public GpuShaderBinaryFormat ShaderBinaryFormat => GpuShaderBinaryFormat.Dxbc;
        public GpuCapabilities Capabilities => new GpuCapabilities
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

        private ID3D11Device* Device => _runtime.Device.Handle;
        private ID3D11DeviceContext* Context => _runtime.ImmediateContext.Handle;

        public IGpuSwapChain CreateSwapChain(IntPtr windowHandle, int width, int height)
        {
            ThrowIfDisposed();
            if (windowHandle == IntPtr.Zero) throw new ArgumentException("A real HWND is required.", nameof(windowHandle));
            var result = new SwapChainAdapter(this, windowHandle, width, height);
            _swapChains.Add(result);
            _activeSwapChain = result;
            return result;
        }

        public void BeginFrame()
        {
            ThrowIfDisposed();
        }

        public void EndFrame()
        {
            ThrowIfDisposed();
            _activeSwapChain?.EndFrame();
            Context->Flush();
        }

        public void WaitIdle()
        {
            ThrowIfDisposed();
            Context->Flush();
        }

        public GpuBufferHandle CreateBuffer(in GpuBufferDesc desc, ReadOnlySpan<byte> initialData)
        {
            ThrowIfDisposed();
            if (desc.SizeBytes <= 0) throw new ArgumentOutOfRangeException(nameof(desc.SizeBytes));
            if (initialData.Length > desc.SizeBytes) throw new ArgumentException("Initial data exceeds buffer size.", nameof(initialData));
            if (desc.Usage == GpuBufferUsage.Immutable && initialData.Length != desc.SizeBytes)
                throw new ArgumentException("Immutable buffers require a complete initial payload.", nameof(initialData));
            if ((desc.BindFlags & GpuBindFlags.ConstantBuffer) != 0 && (desc.SizeBytes & 15) != 0)
                throw new ArgumentException("D3D11 constant-buffer size must be a multiple of 16.", nameof(desc));
            if ((desc.BindFlags & GpuBindFlags.StructuredBuffer) != 0 &&
                (desc.StructureStride <= 0 || desc.SizeBytes % desc.StructureStride != 0))
                throw new ArgumentException("A structured buffer needs a positive stride dividing its size.", nameof(desc));

            (Usage usage, uint cpuAccess) = Dx11GpuFormats.ToUsage(desc.Usage);
            uint bind = desc.Usage == GpuBufferUsage.Staging ? 0u : Dx11GpuFormats.ToBindFlags(desc.BindFlags);
            var nativeDesc = new BufferDesc
            {
                ByteWidth = (uint)desc.SizeBytes,
                Usage = usage,
                BindFlags = bind,
                CPUAccessFlags = cpuAccess,
                MiscFlags = (desc.BindFlags & GpuBindFlags.StructuredBuffer) != 0
                    ? (uint)ResourceMiscFlag.BufferStructured : 0u,
                StructureByteStride = (uint)Math.Max(0, desc.StructureStride),
            };

            ID3D11Buffer* buffer = null;
            fixed (byte* data = initialData)
            {
                SubresourceData init = new SubresourceData { PSysMem = data };
                SilkMarshal.ThrowHResult(Device->CreateBuffer(
                    &nativeDesc, initialData.IsEmpty ? null : &init, &buffer));
            }

            ID3D11ShaderResourceView* srv = null;
            try
            {
                if ((desc.BindFlags & GpuBindFlags.StructuredBuffer) != 0)
                {
                    var srvDesc = new ShaderResourceViewDesc
                    {
                        Format = Format.FormatUnknown,
                        ViewDimension = D3DSrvDimension.D3D11SrvDimensionBuffer,
                    };
                    srvDesc.Anonymous.Buffer.Anonymous1.FirstElement = 0;
                    srvDesc.Anonymous.Buffer.Anonymous2.NumElements =
                        (uint)(desc.SizeBytes / desc.StructureStride);
                    SilkMarshal.ThrowHResult(Device->CreateShaderResourceView(
                        (ID3D11Resource*)buffer, &srvDesc, &srv));
                }

                int id = _nextBuffer++;
                _buffers.Add(id, new BufferResource
                {
                    Buffer = buffer,
                    Srv = srv,
                    SizeBytes = desc.SizeBytes,
                    Usage = desc.Usage,
                    Shadow = desc.Usage == GpuBufferUsage.Dynamic ? new byte[desc.SizeBytes] : null,
                    ShadowValid = desc.Usage == GpuBufferUsage.Dynamic,
                });
                if (desc.Usage == GpuBufferUsage.Dynamic && !initialData.IsEmpty)
                    initialData.CopyTo(_buffers[id].Shadow);
                return new GpuBufferHandle(id);
            }
            catch
            {
                if (srv != null) srv->Release();
                if (buffer != null) buffer->Release();
                throw;
            }
        }

        public void UpdateBuffer(GpuBufferHandle handle, ReadOnlySpan<byte> data, int byteOffset = 0)
        {
            BufferResource resource = Require(_buffers, handle.Id, nameof(handle));
            if (byteOffset < 0 || data.Length > resource.SizeBytes - byteOffset)
                throw new ArgumentOutOfRangeException(nameof(byteOffset));

            if (resource.Usage == GpuBufferUsage.Dynamic)
            {
                if (byteOffset != 0 || data.Length != resource.SizeBytes)
                {
                    if (!resource.ShadowValid)
                        throw new InvalidOperationException(
                            "A partial update cannot follow direct mapping because untouched bytes are unknown.");
                    data.CopyTo(resource.Shadow.AsSpan(byteOffset));
                }
                else
                {
                    data.CopyTo(resource.Shadow);
                }
                MappedSubresource mapped;
                SilkMarshal.ThrowHResult(Context->Map(
                    (ID3D11Resource*)resource.Buffer, 0, Map.WriteDiscard, 0, &mapped));
                try
                {
                    resource.Shadow.AsSpan().CopyTo(new Span<byte>(mapped.PData, resource.SizeBytes));
                }
                finally { Context->Unmap((ID3D11Resource*)resource.Buffer, 0); }
                return;
            }

            if (resource.Usage == GpuBufferUsage.Staging)
                throw new InvalidOperationException("A readback staging buffer cannot be updated by the CPU.");

            fixed (byte* source = data)
            {
                if (byteOffset == 0 && data.Length == resource.SizeBytes)
                {
                    Context->UpdateSubresource((ID3D11Resource*)resource.Buffer, 0, null, source, 0, 0);
                }
                else
                {
                    var box = new Box
                    {
                        Left = (uint)byteOffset,
                        Right = (uint)(byteOffset + data.Length),
                        Top = 0, Bottom = 1, Front = 0, Back = 1,
                    };
                    Context->UpdateSubresource((ID3D11Resource*)resource.Buffer, 0, &box, source, 0, 0);
                }
            }
        }

        public void UpdateConstantBuffer<T>(GpuBufferHandle handle, in T data) where T : unmanaged
        {
            BufferResource resource = Require(_buffers, handle.Id, nameof(handle));
            if (Unsafe.SizeOf<T>() > resource.SizeBytes)
                throw new ArgumentException("Constant data exceeds the destination buffer.", nameof(data));
            MappedSubresource mapped;
            SilkMarshal.ThrowHResult(Context->Map(
                (ID3D11Resource*)resource.Buffer, 0, Map.WriteDiscard, 0, &mapped));
            resource.Shadow.AsSpan().Clear();
            Unsafe.WriteUnaligned(ref resource.Shadow[0], data);
            resource.ShadowValid = true;
            try { resource.Shadow.AsSpan().CopyTo(new Span<byte>(mapped.PData, resource.SizeBytes)); }
            finally { Context->Unmap((ID3D11Resource*)resource.Buffer, 0); }
        }

        public bool TryMapDiscard(GpuBufferHandle handle, out Span<byte> span, int byteCount = 0)
        {
            BufferResource resource = Require(_buffers, handle.Id, nameof(handle));
            if (resource.Usage != GpuBufferUsage.Dynamic || resource.Mapped)
            {
                span = default;
                return false;
            }
            MappedSubresource mapped;
            if (Context->Map((ID3D11Resource*)resource.Buffer, 0, Map.WriteDiscard, 0, &mapped) < 0)
            {
                span = default;
                return false;
            }
            resource.Mapped = true;
            resource.ShadowValid = false;
            span = new Span<byte>(mapped.PData, resource.SizeBytes);
            if (byteCount > 0 && byteCount < span.Length)
                span = span.Slice(0, byteCount);
            return true;
        }

        public void Unmap(GpuBufferHandle handle)
        {
            BufferResource resource = Require(_buffers, handle.Id, nameof(handle));
            if (!resource.Mapped) return;
            Context->Unmap((ID3D11Resource*)resource.Buffer, 0);
            resource.Mapped = false;
        }

        public void ReleaseBuffer(GpuBufferHandle handle)
        {
            if (!_buffers.Remove(handle.Id, out BufferResource resource)) return;
            if (resource.Mapped) Context->Unmap((ID3D11Resource*)resource.Buffer, 0);
            Release(resource.Srv);
            Release(resource.Buffer);
        }

        public GpuTextureHandle CreateTexture(in GpuTextureDesc desc, ReadOnlySpan<byte> initialData)
        {
            ThrowIfDisposed();
            if (desc.Width <= 0 || desc.Height <= 0) throw new ArgumentOutOfRangeException(nameof(desc));
            int layers = Math.Max(1, desc.ArrayLayers);
            int mipLevels = desc.MipLevels == 0
                ? GpuTextureLayout.FullMipCount(desc.Width, desc.Height)
                : Math.Max(1, desc.MipLevels);
            bool compressed = GpuTextureLayout.IsBlockCompressed(desc.Format);
            int bytesPerLayer = compressed
                ? GpuTextureLayout.GetMipChainSize(desc.Format, desc.Width, desc.Height, mipLevels)
                : GpuTextureLayout.GetSlicePitch(desc.Format, desc.Width, desc.Height);
            if (!initialData.IsEmpty && initialData.Length < checked(bytesPerLayer * layers))
                throw new ArgumentException("Initial texture data does not contain every array layer/mip payload.", nameof(initialData));

            (Usage usage, uint cpuAccess) = Dx11GpuFormats.ToUsage(desc.Usage);
            uint bind = desc.Usage == GpuBufferUsage.Staging ? 0u : Dx11GpuFormats.ToBindFlags(desc.BindFlags);
            var nativeDesc = new Texture2DDesc
            {
                Width = (uint)desc.Width,
                Height = (uint)desc.Height,
                MipLevels = (uint)mipLevels,
                ArraySize = (uint)layers,
                Format = Dx11GpuFormats.ToTypeless(desc.Format),
                SampleDesc = new SampleDesc { Count = 1 },
                Usage = usage,
                BindFlags = bind,
                CPUAccessFlags = cpuAccess,
            };

            ID3D11Texture2D* texture = null;
            SilkMarshal.ThrowHResult(Device->CreateTexture2D(&nativeDesc, null, &texture));
            ID3D11ShaderResourceView* srv = null;
            try
            {
                if (!initialData.IsEmpty)
                {
                    fixed (byte* source = initialData)
                    {
                        for (int layer = 0; layer < layers; layer++)
                        {
                            int layerOffset = layer * bytesPerLayer;
                            if (compressed)
                            {
                                int mipOffset = 0;
                                int mipWidth = desc.Width;
                                int mipHeight = desc.Height;
                                for (int mip = 0; mip < mipLevels; mip++)
                                {
                                    int rowPitch = GpuTextureLayout.GetRowPitch(desc.Format, mipWidth);
                                    int slicePitch = GpuTextureLayout.GetSlicePitch(desc.Format, mipWidth, mipHeight);
                                    uint subresource = (uint)(layer * mipLevels + mip);
                                    Context->UpdateSubresource(
                                        (ID3D11Resource*)texture, subresource, null,
                                        source + layerOffset + mipOffset, (uint)rowPitch, (uint)slicePitch);
                                    mipOffset += slicePitch;
                                    mipWidth = Math.Max(1, mipWidth >> 1);
                                    mipHeight = Math.Max(1, mipHeight >> 1);
                                }
                            }
                            else
                            {
                                uint subresource = (uint)(layer * mipLevels);
                                if (desc.Usage == GpuBufferUsage.Dynamic)
                                    UploadDynamicTexture(texture, subresource,
                                        initialData.Slice(layerOffset, bytesPerLayer), desc.Width, desc.Height, BytesPerPixel(desc.Format));
                                else
                                    Context->UpdateSubresource(
                                        (ID3D11Resource*)texture, subresource, null,
                                        source + layerOffset,
                                        (uint)GpuTextureLayout.GetRowPitch(desc.Format, desc.Width), 0);
                            }
                        }
                    }
                }

                if ((desc.BindFlags & GpuBindFlags.ShaderResource) != 0)
                {
                    var srvDesc = new ShaderResourceViewDesc
                    {
                        Format = Dx11GpuFormats.IsDepth(desc.Format)
                            ? Dx11GpuFormats.ToDepthSrv(desc.Format) : Dx11GpuFormats.ToDxgi(desc.Format),
                        ViewDimension = layers > 1
                            ? D3DSrvDimension.D3D11SrvDimensionTexture2Darray
                            : D3DSrvDimension.D3D11SrvDimensionTexture2D,
                    };
                    if (layers > 1)
                    {
                        srvDesc.Anonymous.Texture2DArray.MostDetailedMip = 0;
                        srvDesc.Anonymous.Texture2DArray.MipLevels = (uint)mipLevels;
                        srvDesc.Anonymous.Texture2DArray.FirstArraySlice = 0;
                        srvDesc.Anonymous.Texture2DArray.ArraySize = (uint)layers;
                    }
                    else
                    {
                        srvDesc.Anonymous.Texture2D.MostDetailedMip = 0;
                        srvDesc.Anonymous.Texture2D.MipLevels = (uint)mipLevels;
                    }
                    SilkMarshal.ThrowHResult(Device->CreateShaderResourceView(
                        (ID3D11Resource*)texture, &srvDesc, &srv));
                }

                GpuTextureHandle handle = AddExternalTexture(texture, srv, desc.Width, desc.Height, desc.Format, layers);
                TextureResource resource = _textures[handle.Id];
                resource.Usage = desc.Usage; resource.MipLevels = mipLevels;
                if (desc.Usage == GpuBufferUsage.Dynamic && !initialData.IsEmpty)
                    resource.UploadShadow = initialData[..(bytesPerLayer * layers)].ToArray();
                return handle;
            }
            catch
            {
                Release(srv);
                Release(texture);
                throw;
            }
        }

        public void UpdateTexture(GpuTextureHandle handle, int x, int y, int width, int height,
            ReadOnlySpan<byte> data, int arraySlice = 0)
        {
            TextureResource resource = Require(_textures, handle.Id, nameof(handle));
            if (GpuTextureLayout.IsBlockCompressed(resource.Format))
                throw new InvalidOperationException("Block-compressed textures are immutable; recook and reload the asset instead.");
            int bpp = BytesPerPixel(resource.Format);
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > resource.Width ||
                y + height > resource.Height || arraySlice < 0 || arraySlice >= resource.ArrayLayers ||
                data.Length < checked(width * height * bpp))
                throw new ArgumentOutOfRangeException(nameof(width), "Texture update region or payload is invalid.");
            if (resource.Usage == GpuBufferUsage.Dynamic)
            {
                // UpdateSubresource silently rejects D3D11_USAGE_DYNAMIC textures.
                // Retain the CPU image so a discard map also preserves pixels outside
                // a partial update; row pitch belongs to the driver, not the image.
                int sliceBytes = checked(resource.Width * resource.Height * bpp);
                resource.UploadShadow ??= new byte[checked(sliceBytes * resource.ArrayLayers)];
                int destination = arraySlice * sliceBytes + (y * resource.Width + x) * bpp;
                for (int row = 0; row < height; row++)
                    data.Slice(row * width * bpp, width * bpp).CopyTo(
                        resource.UploadShadow.AsSpan(destination + row * resource.Width * bpp, width * bpp));
                UploadDynamicTexture(resource.Texture, (uint)(arraySlice * resource.MipLevels),
                    resource.UploadShadow.AsSpan(arraySlice * sliceBytes, sliceBytes), resource.Width, resource.Height, bpp);
                return;
            }
            var box = new Box
            {
                Left = (uint)x, Top = (uint)y, Front = 0,
                Right = (uint)(x + width), Bottom = (uint)(y + height), Back = 1,
            };
            fixed (byte* source = data)
            {
                Context->UpdateSubresource((ID3D11Resource*)resource.Texture, (uint)(arraySlice * resource.MipLevels),
                    &box, source, (uint)(width * bpp), 0);
            }
        }

        private void UploadDynamicTexture(ID3D11Texture2D* texture, uint subresource,
            ReadOnlySpan<byte> data, int width, int height, int bytesPerPixel)
        {
            MappedSubresource mapped;
            SilkMarshal.ThrowHResult(Context->Map((ID3D11Resource*)texture, subresource, Map.WriteDiscard, 0, &mapped));
            try
            {
                int rowBytes = checked(width * bytesPerPixel);
                for (int row = 0; row < height; row++)
                    data.Slice(row * rowBytes, rowBytes).CopyTo(
                        new Span<byte>((byte*)mapped.PData + row * mapped.RowPitch, rowBytes));
            }
            finally { Context->Unmap((ID3D11Resource*)texture, subresource); }
        }

        public void ReleaseTexture(GpuTextureHandle handle)
        {
            if (!_textures.Remove(handle.Id, out TextureResource resource)) return;
            Release(resource.Srv);
            Release(resource.Texture);
            if (_dummyTexture.Id == handle.Id) _dummyTexture = GpuTextureHandle.Invalid;
        }

        public GpuSamplerHandle CreateSampler(in GpuSamplerDesc desc)
        {
            var native = new SamplerDesc
            {
                Filter = Dx11GpuFormats.ToFilter(desc.Filter, desc.CompareOp),
                AddressU = Dx11GpuFormats.ToAddressMode(desc.AddressU),
                AddressV = Dx11GpuFormats.ToAddressMode(desc.AddressV),
                AddressW = Dx11GpuFormats.ToAddressMode(desc.AddressW),
                MaxAnisotropy = (uint)Math.Clamp(desc.MaxAnisotropy, 1, 16),
                ComparisonFunc = Dx11GpuFormats.ToComparison(desc.CompareOp),
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            };
            native.BorderColor[0] = desc.BorderColorR;
            native.BorderColor[1] = desc.BorderColorG;
            native.BorderColor[2] = desc.BorderColorB;
            native.BorderColor[3] = desc.BorderColorA;
            ID3D11SamplerState* sampler = null;
            SilkMarshal.ThrowHResult(Device->CreateSamplerState(&native, &sampler));
            int id = _nextSampler++;
            _samplers.Add(id, (nint)sampler);
            return new GpuSamplerHandle(id);
        }

        public void ReleaseSampler(GpuSamplerHandle handle)
        {
            if (_samplers.Remove(handle.Id, out nint sampler)) Release((ID3D11SamplerState*)sampler);
        }

        public GpuRenderTargetHandle CreateRenderTarget(in GpuRenderTargetDesc desc)
        {
            if (desc.Width <= 0 || desc.Height <= 0 || desc.ColorFormats == null)
                throw new ArgumentException("A render target needs dimensions and a colour format array.", nameof(desc));
            if (desc.ColorFormats.Length > Capabilities.MaxColorAttachments)
                throw new ArgumentException("Too many colour attachments.", nameof(desc));

            var target = new RenderTargetResource
            {
                Colors = new GpuTextureHandle[desc.ColorFormats.Length],
                Rtvs = new nint[desc.ColorFormats.Length],
            };
            try
            {
                for (int i = 0; i < desc.ColorFormats.Length; i++)
                {
                    var textureDesc = new GpuTextureDesc
                    {
                        Width = desc.Width, Height = desc.Height, MipLevels = 1, ArrayLayers = 1,
                        Format = desc.ColorFormats[i], Usage = GpuBufferUsage.Immutable,
                        BindFlags = GpuBindFlags.RenderTarget | GpuBindFlags.ShaderResource,
                    };
                    target.Colors[i] = CreateTexture(textureDesc, ReadOnlySpan<byte>.Empty);
                    TextureResource texture = Require(_textures, target.Colors[i].Id, "colour attachment");
                    ID3D11RenderTargetView* rtv = null;
                    SilkMarshal.ThrowHResult(Device->CreateRenderTargetView(
                        (ID3D11Resource*)texture.Texture, null, &rtv));
                    target.Rtvs[i] = (nint)rtv;
                }

                if (desc.DepthFormat != GpuFormat.Unknown)
                {
                    var depthDesc = new GpuTextureDesc
                    {
                        Width = desc.Width, Height = desc.Height, MipLevels = 1, ArrayLayers = 1,
                        Format = desc.DepthFormat, Usage = GpuBufferUsage.Immutable,
                        BindFlags = GpuBindFlags.DepthStencil |
                            (desc.DepthSampleable ? GpuBindFlags.ShaderResource : GpuBindFlags.None),
                    };
                    target.Depth = CreateTexture(depthDesc, ReadOnlySpan<byte>.Empty);
                    TextureResource depth = Require(_textures, target.Depth.Id, "depth attachment");
                    var dsvDesc = new DepthStencilViewDesc
                    {
                        Format = Dx11GpuFormats.ToDxgi(desc.DepthFormat),
                        ViewDimension = DsvDimension.Texture2D,
                    };
                    dsvDesc.Anonymous.Texture2D.MipSlice = 0;
                    ID3D11DepthStencilView* dsv = null;
                    SilkMarshal.ThrowHResult(Device->CreateDepthStencilView(
                        (ID3D11Resource*)depth.Texture, &dsvDesc, &dsv));
                    target.Dsv = dsv;
                    target.HasStencil = desc.DepthFormat == GpuFormat.D24UNormS8UInt;
                }

                int id = _nextRenderTarget++;
                _renderTargets.Add(id, target);
                return new GpuRenderTargetHandle(id);
            }
            catch
            {
                DestroyRenderTarget(target);
                throw;
            }
        }

        public GpuTextureHandle GetRenderTargetTexture(GpuRenderTargetHandle handle, int attachment = 0)
        {
            RenderTargetResource target = Require(_renderTargets, handle.Id, nameof(handle));
            return attachment >= 0 && attachment < target.Colors.Length
                ? target.Colors[attachment] : GpuTextureHandle.Invalid;
        }

        public GpuTextureHandle GetRenderTargetDepthTexture(GpuRenderTargetHandle handle)
        {
            RenderTargetResource target = Require(_renderTargets, handle.Id, nameof(handle));
            if (!target.Depth.IsValid) return GpuTextureHandle.Invalid;
            TextureResource depth = Require(_textures, target.Depth.Id, "depth attachment");
            return depth.Srv != null ? target.Depth : GpuTextureHandle.Invalid;
        }

        public void ReleaseRenderTarget(GpuRenderTargetHandle handle)
        {
            if (!_renderTargets.Remove(handle.Id, out RenderTargetResource target)) return;
            if (_activeTarget.Id == handle.Id) _activeTarget = GpuRenderTargetHandle.Invalid;
            DestroyRenderTarget(target);
        }

        public void BeginRenderPass(in GpuRenderPassDesc desc)
        {
            ID3D11RenderTargetView** rtvs;
            uint count;
            ID3D11DepthStencilView* dsv;
            bool hasStencil = false;

            if (desc.Target.IsValid)
            {
                RenderTargetResource target = Require(_renderTargets, desc.Target.Id, nameof(desc.Target));
                uint targetCount = (uint)target.Rtvs.Length;
                if (desc.ColorActions != null)
                    targetCount = Math.Min(targetCount, (uint)desc.ColorActions.Length);
                ID3D11DepthStencilView* targetDsv = target.Dsv;
                bool targetHasStencil = target.HasStencil;
                if (desc.DepthTexture.IsValid)
                    ResolveDepthAttachment(desc.DepthTexture, out targetDsv, out targetHasStencil);
                fixed (nint* pinned = target.Rtvs)
                {
                    BindAndClear((ID3D11RenderTargetView**)pinned, targetCount,
                        targetDsv, targetHasStencil, desc);
                }
                _activeTarget = desc.Target;
                return;
            }

            if (_activeSwapChain == null || !_activeSwapChain.IsReady)
                throw new InvalidOperationException("A back-buffer pass requires an active, ready swap chain.");
            ID3D11RenderTargetView* back = _activeSwapChain.Native.Rtv;
            rtvs = &back;
            count = 1;
            dsv = _activeSwapChain.Native.Dsv;
            hasStencil = true;
            if (desc.DepthTexture.IsValid)
                ResolveDepthAttachment(desc.DepthTexture, out dsv, out hasStencil);
            BindAndClear(rtvs, count, dsv, hasStencil, desc);
            _activeTarget = GpuRenderTargetHandle.Invalid;
        }

        private void BindAndClear(ID3D11RenderTargetView** rtvs, uint count, ID3D11DepthStencilView* dsv,
            bool hasStencil, in GpuRenderPassDesc desc)
        {
            Context->OMSetRenderTargets(count, rtvs, desc.HasDepth ? dsv : null);
            int actions = desc.ColorActions?.Length ?? 0;
            float* color = stackalloc float[4];
            for (int i = 0; i < count && i < actions; i++)
            {
                if (desc.ColorActions[i].Load != GpuLoadAction.Clear) continue;
                color[0] = desc.ColorActions[i].ClearR;
                color[1] = desc.ColorActions[i].ClearG;
                color[2] = desc.ColorActions[i].ClearB;
                color[3] = desc.ColorActions[i].ClearA;
                Context->ClearRenderTargetView(rtvs[i], color);
            }
            if (desc.HasDepth && dsv != null && desc.DepthAction.Load == GpuLoadAction.Clear)
            {
                ClearFlag flags = ClearFlag.Depth;
                if (hasStencil) flags |= ClearFlag.Stencil;
                Context->ClearDepthStencilView(dsv, (uint)flags, desc.DepthAction.ClearR, 0);
            }
        }

        private void ResolveDepthAttachment(GpuTextureHandle handle,
            out ID3D11DepthStencilView* dsv, out bool hasStencil)
        {
            if (_activeSwapChain != null && _activeSwapChain.DepthTexture.Id == handle.Id)
            {
                dsv = _activeSwapChain.Native.Dsv;
                hasStencil = true;
                return;
            }

            foreach (RenderTargetResource target in _renderTargets.Values)
            {
                if (target.Depth.Id != handle.Id) continue;
                dsv = target.Dsv;
                hasStencil = target.HasStencil;
                return;
            }

            throw new ArgumentException("Texture is not a live depth attachment.", nameof(handle));
        }

        public void EndRenderPass()
        {
            // D3D11 keeps the previous DSV bound until the next OMSetRenderTargets. Water (and any
            // other pass that then samples scene depth) would otherwise hit a simultaneous DSV+SRV
            // bind. Unbind here; do not ClearState — that would drop shaders and textures too.
            Context->OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        }

        /// <inheritdoc />
        public void UnbindRenderTargets()
        {
            // ClearState alone is not enough: it leaves the output-merger's references alive until
            // the next flush, and ResizeBuffers is called before that would happen.
            Context->OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
            Context->ClearState();
            Context->Flush();
            _activeTarget = GpuRenderTargetHandle.Invalid;
        }

        public void SetViewport(float x, float y, float width, float height, float minZ = 0, float maxZ = 1)
        {
            var viewport = new D3D11Viewport
            {
                TopLeftX = x, TopLeftY = y, Width = width, Height = height,
                MinDepth = minZ, MaxDepth = maxZ,
            };
            Context->RSSetViewports(1, &viewport);
        }

        public void SetScissor(int x, int y, int width, int height)
        {
            var rect = new Box2D<int>(x, y, x + width, y + height);
            Context->RSSetScissorRects(1, &rect);
        }

        public void SetShaderProgram(GpuShaderProgramHandle handle)
        {
            ShaderProgramResource program = handle.IsValid
                ? Require(_shaderPrograms, handle.Id, nameof(handle))
                : null;
            _pipeline.Program = handle;

            ID3D11VertexShader* vertex = program == null ? null : program.VertexShader;
            ID3D11PixelShader* pixel = program == null ? null : program.PixelShader;
            ID3D11ComputeShader* compute = program == null ? null : program.ComputeShader;
            Context->VSSetShader(vertex, null, 0);
            Context->PSSetShader(pixel, null, 0);
            Context->CSSetShader(compute, null, 0);
        }

        public void SetVertexLayout(GpuVertexLayoutHandle handle)
        {
            VertexLayoutResource layout = handle.IsValid
                ? Require(_vertexLayouts, handle.Id, nameof(handle))
                : null;
            _pipeline.VertexLayout = handle;
            Context->IASetInputLayout(layout == null ? null : layout.Layout);
        }
        public void SetPrimitiveTopology(GpuPrimitiveTopology topology)
        {
            _pipeline.Topology = topology;
            Context->IASetPrimitiveTopology(Dx11GpuFormats.ToTopology(topology));
        }

        public void SetBlendState(in GpuBlendState state)
        {
            _pipeline.Blend = state;
            if (!_blendStates.TryGetValue(state, out nint nativeAddress))
            {
                ID3D11BlendState* native = null;
                var desc = new BlendDesc { IndependentBlendEnable = state.IndependentBlend };
                desc.RenderTarget[0] = new RenderTargetBlendDesc
                {
                    BlendEnable = state.Enabled,
                    SrcBlend = Dx11GpuFormats.ToBlend(state.SrcColor),
                    DestBlend = Dx11GpuFormats.ToBlend(state.DstColor),
                    BlendOp = Dx11GpuFormats.ToBlendOp(state.ColorOp),
                    SrcBlendAlpha = Dx11GpuFormats.ToBlend(state.SrcAlpha),
                    DestBlendAlpha = Dx11GpuFormats.ToBlend(state.DstAlpha),
                    BlendOpAlpha = Dx11GpuFormats.ToBlendOp(state.AlphaOp),
                    RenderTargetWriteMask = ColorWriteMask(state),
                };
                if (state.IndependentBlend)
                {
                    desc.RenderTarget[1] = new RenderTargetBlendDesc
                    {
                        BlendEnable = state.SecondaryEnabled,
                        SrcBlend = Dx11GpuFormats.ToBlend(state.SecondarySrcColor),
                        DestBlend = Dx11GpuFormats.ToBlend(state.SecondaryDstColor),
                        BlendOp = Dx11GpuFormats.ToBlendOp(state.SecondaryColorOp),
                        SrcBlendAlpha = Dx11GpuFormats.ToBlend(state.SecondarySrcAlpha),
                        DestBlendAlpha = Dx11GpuFormats.ToBlend(state.SecondaryDstAlpha),
                        BlendOpAlpha = Dx11GpuFormats.ToBlendOp(state.SecondaryAlphaOp),
                        RenderTargetWriteMask = SecondaryColorWriteMask(state),
                    };
                }
                SilkMarshal.ThrowHResult(Device->CreateBlendState(&desc, &native));
                _blendStates.Add(state, (nint)native);
                nativeAddress = (nint)native;
            }
            ID3D11BlendState* nativeState = (ID3D11BlendState*)nativeAddress;
            float* factors = stackalloc float[4] { 1, 1, 1, 1 };
            Context->OMSetBlendState(nativeState, factors, uint.MaxValue);
        }

        public void SetDepthState(in GpuDepthState state)
        {
            _pipeline.Depth = state;
            if (!_depthStates.TryGetValue(state, out nint nativeAddress))
            {
                ID3D11DepthStencilState* native = null;
                var desc = new DepthStencilDesc
                {
                    DepthEnable = state.TestEnabled,
                    DepthWriteMask = state.WriteEnabled ? DepthWriteMask.All : DepthWriteMask.Zero,
                    DepthFunc = Dx11GpuFormats.ToComparison(state.Compare),
                };
                SilkMarshal.ThrowHResult(Device->CreateDepthStencilState(&desc, &native));
                _depthStates.Add(state, (nint)native);
                nativeAddress = (nint)native;
            }
            Context->OMSetDepthStencilState((ID3D11DepthStencilState*)nativeAddress, 0);
        }

        public void SetRasterState(in GpuRasterState state)
        {
            _pipeline.Raster = state;
            if (!_rasterStates.TryGetValue(state, out nint nativeAddress))
            {
                ID3D11RasterizerState* native = null;
                var desc = new RasterizerDesc
                {
                    FillMode = Dx11GpuFormats.ToFillMode(state.FillMode),
                    CullMode = Dx11GpuFormats.ToCullMode(state.CullMode),
                    FrontCounterClockwise = state.FrontCounterClockwise,
                    ScissorEnable = state.ScissorEnabled,
                    DepthClipEnable = state.DepthClipEnabled,
                    DepthBias = (int)MathF.Round(state.DepthBias),
                    SlopeScaledDepthBias = state.SlopeScaledDepthBias,
                };
                SilkMarshal.ThrowHResult(Device->CreateRasterizerState(&desc, &native));
                _rasterStates.Add(state, (nint)native);
                nativeAddress = (nint)native;
            }
            Context->RSSetState((ID3D11RasterizerState*)nativeAddress);
        }

        public GpuShaderProgramHandle CreateShaderProgram(in GpuShaderProgramDesc desc)
        {
            ThrowIfDisposed();
            if (desc.BinaryFormat != GpuShaderBinaryFormat.Dxbc)
            {
                throw new ArgumentException(
                    $"Direct3D 11 requires DXBC shader bytecode, not {desc.BinaryFormat}.",
                    nameof(desc));
            }

            byte[] vertexBytecode = desc.VertexShader ?? Array.Empty<byte>();
            byte[] pixelBytecode = desc.PixelShader ?? Array.Empty<byte>();
            byte[] computeBytecode = desc.ComputeShader ?? Array.Empty<byte>();
            if (vertexBytecode.Length == 0 && pixelBytecode.Length == 0 && computeBytecode.Length == 0)
                throw new ArgumentException("A shader program requires at least one compiled stage.", nameof(desc));
            if (pixelBytecode.Length > 0 && vertexBytecode.Length == 0)
                throw new ArgumentException("A graphics program with a pixel shader also needs a vertex shader.", nameof(desc));

            var program = new ShaderProgramResource
            {
                VertexBytecode = vertexBytecode.Length == 0 ? Array.Empty<byte>() : [.. vertexBytecode],
            };
            try
            {
                if (vertexBytecode.Length > 0)
                {
                    fixed (byte* bytes = vertexBytecode)
                    {
                        ID3D11VertexShader* shader = null;
                        SilkMarshal.ThrowHResult(Device->CreateVertexShader(
                            bytes, (nuint)vertexBytecode.Length, null, &shader));
                        program.VertexShader = shader;
                    }
                }
                if (pixelBytecode.Length > 0)
                {
                    fixed (byte* bytes = pixelBytecode)
                    {
                        ID3D11PixelShader* shader = null;
                        SilkMarshal.ThrowHResult(Device->CreatePixelShader(
                            bytes, (nuint)pixelBytecode.Length, null, &shader));
                        program.PixelShader = shader;
                    }
                }
                if (computeBytecode.Length > 0)
                {
                    fixed (byte* bytes = computeBytecode)
                    {
                        ID3D11ComputeShader* shader = null;
                        SilkMarshal.ThrowHResult(Device->CreateComputeShader(
                            bytes, (nuint)computeBytecode.Length, null, &shader));
                        program.ComputeShader = shader;
                    }
                }

                int id = _nextShaderProgram++;
                _shaderPrograms.Add(id, program);
                return new GpuShaderProgramHandle(id);
            }
            catch
            {
                DestroyShaderProgram(program);
                throw;
            }
        }

        public void ReleaseShaderProgram(GpuShaderProgramHandle handle)
        {
            if (!_shaderPrograms.Remove(handle.Id, out ShaderProgramResource program)) return;
            if (_pipeline.Program.Id == handle.Id)
            {
                SetShaderProgram(GpuShaderProgramHandle.Invalid);
            }
            DestroyShaderProgram(program);
        }

        public GpuVertexLayoutHandle CreateVertexLayout(
            in GpuVertexLayoutDesc desc,
            GpuShaderProgramHandle programHandle)
        {
            if (desc.Elements == null || desc.Elements.Length == 0)
                throw new ArgumentException("A vertex layout requires elements.", nameof(desc));
            ShaderProgramResource program = Require(
                _shaderPrograms, programHandle.Id, nameof(programHandle));
            if (program.VertexBytecode.Length == 0)
                throw new ArgumentException("The vertex-layout program has no vertex shader.", nameof(programHandle));

            var nativeElements = new InputElementDesc[desc.Elements.Length];
            var semanticNames = new IntPtr[desc.Elements.Length];
            ID3D11InputLayout* nativeLayout = null;
            try
            {
                for (int i = 0; i < desc.Elements.Length; i++)
                {
                    GpuVertexElement element = desc.Elements[i];
                    if (string.IsNullOrWhiteSpace(element.Semantic))
                        throw new ArgumentException($"Vertex element {i} has no semantic name.", nameof(desc));
                    semanticNames[i] = Marshal.StringToHGlobalAnsi(element.Semantic);
                    nativeElements[i] = new InputElementDesc
                    {
                        SemanticName = (byte*)semanticNames[i],
                        SemanticIndex = (uint)element.SemanticIndex,
                        Format = Dx11GpuFormats.ToDxgi(element.Format),
                        InputSlot = (uint)element.Slot,
                        AlignedByteOffset = (uint)element.OffsetBytes,
                        InputSlotClass = element.InstanceStepRate > 0
                            ? InputClassification.PerInstanceData
                            : InputClassification.PerVertexData,
                        InstanceDataStepRate = (uint)Math.Max(0, element.InstanceStepRate),
                    };
                }

                fixed (InputElementDesc* elements = nativeElements)
                fixed (byte* bytecode = program.VertexBytecode)
                {
                    SilkMarshal.ThrowHResult(Device->CreateInputLayout(
                        elements,
                        (uint)nativeElements.Length,
                        bytecode,
                        (nuint)program.VertexBytecode.Length,
                        &nativeLayout));
                }

                int id = _nextVertexLayout++;
                _vertexLayouts.Add(id, new VertexLayoutResource { Layout = nativeLayout, Desc = desc });
                return new GpuVertexLayoutHandle(id);
            }
            catch
            {
                Release(nativeLayout);
                throw;
            }
            finally
            {
                foreach (IntPtr semanticName in semanticNames)
                {
                    if (semanticName != IntPtr.Zero) Marshal.FreeHGlobal(semanticName);
                }
            }
        }

        public void ReleaseVertexLayout(GpuVertexLayoutHandle handle)
        {
            if (!_vertexLayouts.Remove(handle.Id, out VertexLayoutResource layout)) return;
            if (_pipeline.VertexLayout.Id == handle.Id)
            {
                SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            }
            Release(layout.Layout);
        }

        public void SetConstantBuffer(GpuShaderStage stage, int slot, GpuBufferHandle handle)
        {
            ID3D11Buffer* buffer = handle.IsValid ? Require(_buffers, handle.Id, nameof(handle)).Buffer : null;
            if ((stage & GpuShaderStage.Vertex) != 0) Context->VSSetConstantBuffers((uint)slot, 1, &buffer);
            if ((stage & GpuShaderStage.Pixel) != 0) Context->PSSetConstantBuffers((uint)slot, 1, &buffer);
            if ((stage & GpuShaderStage.Compute) != 0) Context->CSSetConstantBuffers((uint)slot, 1, &buffer);
        }

        public void SetTexture(GpuShaderStage stage, int slot, GpuTextureHandle handle)
        {
            if (!handle.IsValid) handle = EnsureDummyTexture();
            ID3D11ShaderResourceView* srv = Require(_textures, handle.Id, nameof(handle)).Srv;
            if ((stage & GpuShaderStage.Vertex) != 0) Context->VSSetShaderResources((uint)slot, 1, &srv);
            if ((stage & GpuShaderStage.Pixel) != 0) Context->PSSetShaderResources((uint)slot, 1, &srv);
            if ((stage & GpuShaderStage.Compute) != 0) Context->CSSetShaderResources((uint)slot, 1, &srv);
        }

        public void ClearTexture(GpuShaderStage stage, int slot)
        {
            ID3D11ShaderResourceView* srv = null;
            if ((stage & GpuShaderStage.Vertex) != 0) Context->VSSetShaderResources((uint)slot, 1, &srv);
            if ((stage & GpuShaderStage.Pixel) != 0) Context->PSSetShaderResources((uint)slot, 1, &srv);
            if ((stage & GpuShaderStage.Compute) != 0) Context->CSSetShaderResources((uint)slot, 1, &srv);
        }

        public void SetStructuredBuffer(GpuShaderStage stage, int slot, GpuBufferHandle handle)
        {
            ID3D11ShaderResourceView* srv = handle.IsValid
                ? Require(_buffers, handle.Id, nameof(handle)).Srv : null;
            if ((stage & GpuShaderStage.Vertex) != 0) Context->VSSetShaderResources((uint)slot, 1, &srv);
            if ((stage & GpuShaderStage.Pixel) != 0) Context->PSSetShaderResources((uint)slot, 1, &srv);
            if ((stage & GpuShaderStage.Compute) != 0) Context->CSSetShaderResources((uint)slot, 1, &srv);
        }

        public void SetSampler(GpuShaderStage stage, int slot, GpuSamplerHandle handle)
        {
            ID3D11SamplerState* sampler = handle.IsValid
                ? (ID3D11SamplerState*)Require(_samplers, handle.Id, nameof(handle)) : null;
            if ((stage & GpuShaderStage.Vertex) != 0) Context->VSSetSamplers((uint)slot, 1, &sampler);
            if ((stage & GpuShaderStage.Pixel) != 0) Context->PSSetSamplers((uint)slot, 1, &sampler);
            if ((stage & GpuShaderStage.Compute) != 0) Context->CSSetSamplers((uint)slot, 1, &sampler);
        }

        public void SetVertexBuffer(int slot, GpuBufferHandle handle, int stride, int offset = 0)
        {
            ID3D11Buffer* buffer = handle.IsValid ? Require(_buffers, handle.Id, nameof(handle)).Buffer : null;
            uint nativeStride = (uint)stride;
            uint nativeOffset = (uint)offset;
            Context->IASetVertexBuffers((uint)slot, 1, &buffer, &nativeStride, &nativeOffset);
        }

        public void SetIndexBuffer(GpuBufferHandle handle, GpuIndexFormat format, int offset = 0)
        {
            ID3D11Buffer* buffer = handle.IsValid ? Require(_buffers, handle.Id, nameof(handle)).Buffer : null;
            Context->IASetIndexBuffer(buffer,
                format == GpuIndexFormat.UInt16 ? Format.FormatR16Uint : Format.FormatR32Uint,
                (uint)offset);
        }

        public void Draw(int vertexCount, int startVertex = 0) =>
            Context->Draw((uint)vertexCount, (uint)startVertex);

        public void DrawIndexed(int indexCount, int startIndex = 0, int baseVertex = 0) =>
            Context->DrawIndexed((uint)indexCount, (uint)startIndex, baseVertex);

        public void DrawIndexedInstanced(int indexCountPerInstance, int instanceCount,
            int startIndex = 0, int baseVertex = 0, int startInstance = 0) =>
            Context->DrawIndexedInstanced((uint)indexCountPerInstance, (uint)instanceCount,
                (uint)startIndex, baseVertex, (uint)startInstance);

        public GpuQueryHandle BeginTimestampScope()
        {
            var disjointDesc = new QueryDesc { Query = Query.TimestampDisjoint };
            var timestampDesc = new QueryDesc { Query = Query.Timestamp };
            ID3D11Query* disjoint = null;
            ID3D11Query* begin = null;
            ID3D11Query* end = null;
            try
            {
                SilkMarshal.ThrowHResult(Device->CreateQuery(&disjointDesc, &disjoint));
                SilkMarshal.ThrowHResult(Device->CreateQuery(&timestampDesc, &begin));
                SilkMarshal.ThrowHResult(Device->CreateQuery(&timestampDesc, &end));
                Context->Begin((ID3D11Asynchronous*)disjoint);
                Context->End((ID3D11Asynchronous*)begin);
                int id = _nextQuery++;
                _queries.Add(id, new TimestampResource { Disjoint = disjoint, Begin = begin, End = end });
                return new GpuQueryHandle(id);
            }
            catch
            {
                Release(end); Release(begin); Release(disjoint);
                throw;
            }
        }

        public void EndTimestampScope(GpuQueryHandle handle)
        {
            TimestampResource query = Require(_queries, handle.Id, nameof(handle));
            if (query.Ended) throw new InvalidOperationException("Timestamp scope already ended.");
            Context->End((ID3D11Asynchronous*)query.End);
            Context->End((ID3D11Asynchronous*)query.Disjoint);
            query.Ended = true;
        }

        public bool TryResolveTimestamp(GpuQueryHandle handle, out double milliseconds)
        {
            milliseconds = 0;
            TimestampResource query = Require(_queries, handle.Id, nameof(handle));
            if (!query.Ended) return false;
            QueryDataTimestampDisjoint disjoint;
            ulong begin;
            ulong end;
            if (Context->GetData((ID3D11Asynchronous*)query.Disjoint, &disjoint,
                    (uint)sizeof(QueryDataTimestampDisjoint), 0) != 0 ||
                Context->GetData((ID3D11Asynchronous*)query.Begin, &begin, (uint)sizeof(ulong), 0) != 0 ||
                Context->GetData((ID3D11Asynchronous*)query.End, &end, (uint)sizeof(ulong), 0) != 0)
                return false;
            if (!disjoint.Disjoint && disjoint.Frequency != 0 && end >= begin)
                milliseconds = (end - begin) * 1000.0 / disjoint.Frequency;
            _queries.Remove(handle.Id);
            DestroyQuery(query);
            return true;
        }

        public bool TryReadTexture(GpuTextureHandle handle, out int width, out int height, out byte[] bgra)
        {
            width = 0; height = 0; bgra = Array.Empty<byte>();
            if (!_textures.TryGetValue(handle.Id, out TextureResource source)) return false;
            if (source.Format != GpuFormat.B8G8R8A8UNorm && source.Format != GpuFormat.R8G8B8A8UNorm)
                return false;

            var desc = new Texture2DDesc
            {
                Width = (uint)source.Width, Height = (uint)source.Height,
                MipLevels = 1, ArraySize = 1, Format = Dx11GpuFormats.ToDxgi(source.Format),
                SampleDesc = new SampleDesc { Count = 1 }, Usage = Usage.Staging,
                CPUAccessFlags = (uint)CpuAccessFlag.Read,
            };
            ID3D11Texture2D* staging = null;
            if (Device->CreateTexture2D(&desc, null, &staging) < 0 || staging == null) return false;
            try
            {
                Context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0,
                    (ID3D11Resource*)source.Texture, 0, null);
                MappedSubresource mapped;
                if (Context->Map((ID3D11Resource*)staging, 0, Map.Read, 0, &mapped) < 0) return false;
                try
                {
                    width = source.Width;
                    height = source.Height;
                    bgra = new byte[checked(width * height * 4)];
                    for (int y = 0; y < height; y++)
                    {
                        ReadOnlySpan<byte> row = new((byte*)mapped.PData + (y * mapped.RowPitch), width * 4);
                        Span<byte> output = bgra.AsSpan(y * width * 4, width * 4);
                        row.CopyTo(output);
                        if (source.Format == GpuFormat.R8G8B8A8UNorm)
                        {
                            for (int x = 0; x < width; x++)
                                (output[x * 4], output[x * 4 + 2]) = (output[x * 4 + 2], output[x * 4]);
                        }
                    }
                    return true;
                }
                finally { Context->Unmap((ID3D11Resource*)staging, 0); }
            }
            finally { staging->Release(); }
        }

        // Transitional controller-only bridges. R4 removes these when overlay/readback and the
        // controller's immediate clear/bind operations move behind the shared contract.
        internal Dx11SwapChain GetNativeSwapChain(IGpuSwapChain swapChain) =>
            swapChain is SwapChainAdapter adapter
                ? adapter.Native
                : throw new ArgumentException("Swap chain was not created by this device.", nameof(swapChain));

        internal nint GetNativeRenderTargetView(GpuRenderTargetHandle handle, int attachment = 0)
        {
            if (!handle.IsValid || !_renderTargets.TryGetValue(handle.Id, out RenderTargetResource target) ||
                attachment < 0 || attachment >= target.Rtvs.Length)
                return 0;
            return target.Rtvs[attachment];
        }

        internal nint GetNativeDepthStencilView(GpuRenderTargetHandle handle) =>
            handle.IsValid && _renderTargets.TryGetValue(handle.Id, out RenderTargetResource target)
                ? (nint)target.Dsv
                : 0;

        internal nint GetNativeTexture(GpuTextureHandle handle) =>
            handle.IsValid && _textures.TryGetValue(handle.Id, out TextureResource resource)
                ? (nint)resource.Texture
                : 0;

        internal nint GetNativeShaderResourceView(GpuTextureHandle handle) =>
            handle.IsValid && _textures.TryGetValue(handle.Id, out TextureResource resource)
                ? (nint)resource.Srv
                : 0;

        private GpuTextureHandle AddExternalTexture(ID3D11Texture2D* texture,
            ID3D11ShaderResourceView* srv, int width, int height, GpuFormat format, int layers = 1)
        {
            int id = _nextTexture++;
            _textures.Add(id, new TextureResource
            {
                Texture = texture, Srv = srv, Width = width, Height = height,
                ArrayLayers = layers, Format = format,
            });
            return new GpuTextureHandle(id);
        }

        private GpuTextureHandle EnsureDummyTexture()
        {
            if (_dummyTexture.IsValid) return _dummyTexture;
            byte[] white = { 255, 255, 255, 255 };
            var desc = new GpuTextureDesc
            {
                Width = 1, Height = 1, MipLevels = 1, ArrayLayers = 1,
                Format = GpuFormat.R8G8B8A8UNorm, Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.ShaderResource,
            };
            _dummyTexture = CreateTexture(desc, white);
            return _dummyTexture;
        }

        private void DestroyRenderTarget(RenderTargetResource target)
        {
            foreach (nint rtv in target.Rtvs) Release((ID3D11RenderTargetView*)rtv);
            Release(target.Dsv);
            foreach (GpuTextureHandle color in target.Colors) ReleaseTexture(color);
            if (target.Depth.IsValid) ReleaseTexture(target.Depth);
        }

        private static void DestroyQuery(TimestampResource query)
        {
            Release(query.End); Release(query.Begin); Release(query.Disjoint);
        }

        private static void DestroyShaderProgram(ShaderProgramResource program)
        {
            Release(program.ComputeShader);
            Release(program.PixelShader);
            Release(program.VertexShader);
        }

        private static int BytesPerPixel(GpuFormat format) => format switch
        {
            GpuFormat.R8G8B8A8UNorm or GpuFormat.R8G8B8A8UNormSrgb or GpuFormat.B8G8R8A8UNorm or GpuFormat.R11G11B10Float
                or GpuFormat.R32Float or GpuFormat.D32Float or GpuFormat.D24UNormS8UInt
                or GpuFormat.R32UInt => 4,
            GpuFormat.R16G16B16A16Float or GpuFormat.R32Float2 => 8,
            GpuFormat.R8UNorm => 1,
            GpuFormat.R16Float => 2,
            GpuFormat.R32Float3 => 12,
            GpuFormat.R32Float4 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown texel size."),
        };

        private static int FullMipCount(int width, int height) => GpuTextureLayout.FullMipCount(width, height);

        private static byte ColorWriteMask(in GpuBlendState state)
        {
            byte mask = 0;
            if (state.WriteR) mask |= (byte)ColorWriteEnable.Red;
            if (state.WriteG) mask |= (byte)ColorWriteEnable.Green;
            if (state.WriteB) mask |= (byte)ColorWriteEnable.Blue;
            if (state.WriteA) mask |= (byte)ColorWriteEnable.Alpha;
            return mask;
        }

        private static byte SecondaryColorWriteMask(in GpuBlendState state)
        {
            byte mask = 0;
            if (state.SecondaryWriteR) mask |= (byte)ColorWriteEnable.Red;
            if (state.SecondaryWriteG) mask |= (byte)ColorWriteEnable.Green;
            if (state.SecondaryWriteB) mask |= (byte)ColorWriteEnable.Blue;
            if (state.SecondaryWriteA) mask |= (byte)ColorWriteEnable.Alpha;
            return mask;
        }

        private static T Require<T>(Dictionary<int, T> resources, int id, string name)
        {
            if (id <= 0 || !resources.TryGetValue(id, out T value))
                throw new ArgumentException($"Invalid or released GPU handle {id}.", name);
            return value;
        }

        private static void Release<T>(T* value) where T : unmanaged
        {
            if (value != null) ((IUnknown*)value)->Release();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Dx11GpuDevice));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Context->ClearState();

            foreach (SwapChainAdapter swapChain in new List<SwapChainAdapter>(_swapChains)) swapChain.Dispose();
            foreach (TimestampResource query in _queries.Values) DestroyQuery(query);
            foreach (RenderTargetResource target in new List<RenderTargetResource>(_renderTargets.Values)) DestroyRenderTarget(target);
            _renderTargets.Clear();
            foreach (BufferResource buffer in _buffers.Values)
            {
                if (buffer.Mapped) Context->Unmap((ID3D11Resource*)buffer.Buffer, 0);
                Release(buffer.Srv); Release(buffer.Buffer);
            }
            _buffers.Clear();
            foreach (TextureResource texture in _textures.Values) { Release(texture.Srv); Release(texture.Texture); }
            _textures.Clear();
            foreach (nint sampler in _samplers.Values) Release((ID3D11SamplerState*)sampler);
            _samplers.Clear();
            foreach (VertexLayoutResource layout in _vertexLayouts.Values) Release(layout.Layout);
            _vertexLayouts.Clear();
            foreach (ShaderProgramResource program in _shaderPrograms.Values) DestroyShaderProgram(program);
            _shaderPrograms.Clear();
            foreach (nint state in _blendStates.Values) Release((ID3D11BlendState*)state);
            foreach (nint state in _depthStates.Values) Release((ID3D11DepthStencilState*)state);
            foreach (nint state in _rasterStates.Values) Release((ID3D11RasterizerState*)state);
            _blendStates.Clear(); _depthStates.Clear(); _rasterStates.Clear(); _queries.Clear();
            Context->Flush();
        }
    }
}
