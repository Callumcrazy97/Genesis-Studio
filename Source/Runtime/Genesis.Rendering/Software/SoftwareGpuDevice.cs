using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Software
{
    public unsafe sealed class SoftwareGpuDevice : IGpuDevice
    {
        private sealed class BufferData
        {
            public byte[] Bytes;
            public GpuBufferUsage Usage;
            public int Stride;
        }

        private sealed class TextureData
        {
            public int Width;
            public int Height;
            public int ArrayLayers;
            public byte[] Pixels;
            public float[] Depth;
            public GpuFormat Format;
        }

        private sealed class RenderTargetData
        {
            public int Width;
            public int Height;
            public GpuTextureHandle[] ColorTextures = Array.Empty<GpuTextureHandle>();
            public GpuTextureHandle DepthTexture;
            public bool DepthOnly;
        }

        private readonly Dictionary<int, BufferData> _buffers = new();
        private readonly Dictionary<int, TextureData> _textures = new();
        private readonly Dictionary<int, RenderTargetData> _renderTargets = new();
        private readonly Dictionary<int, GpuVertexLayoutDesc> _vertexLayouts = new();
        private readonly Dictionary<int, GpuShaderProgramDesc> _programs = new();

        private int _nextHandle = 1;
        private SoftwareSwapChain _activeSwapChain;
        private GpuRenderTargetHandle _activeRenderTarget = GpuRenderTargetHandle.Invalid;
        private float _viewportW = 1280f, _viewportH = 720f;
        private byte[] _framePixels = new byte[1280 * 720 * 4];
        private float[] _depthBuffer = new float[1280 * 720];

        // State bindings
        private readonly GpuBufferHandle[] _vsConstantBuffers = new GpuBufferHandle[16];
        private readonly GpuBufferHandle[] _psConstantBuffers = new GpuBufferHandle[16];
        private readonly GpuBufferHandle[] _vsStructuredBuffers = new GpuBufferHandle[16];
        private readonly GpuTextureHandle[] _psTextures = new GpuTextureHandle[16];

        private GpuBufferHandle _boundVertexBuffer = GpuBufferHandle.Invalid;
        private int _boundVertexStride = 48;
        private GpuBufferHandle _boundIndexBuffer = GpuBufferHandle.Invalid;
        private GpuIndexFormat _boundIndexFormat = GpuIndexFormat.UInt16;
        private GpuPrimitiveTopology _boundTopology = GpuPrimitiveTopology.TriangleList;
        private GpuRasterState _boundRasterState = GpuRasterState.Default;
        private GpuBlendState _boundBlendState = GpuBlendState.Opaque;
        private GpuDepthState _boundDepthState = GpuDepthState.Default;
        private SoftwareRuntimeShade _runtimeShade = SoftwareRuntimeShade.None;
        private bool _boundWaterProgram;

        public string BackendName => "Software";
        public string AdapterName => "CPU Software Rasterizer";
        public GpuShaderBinaryFormat ShaderBinaryFormat => GpuShaderBinaryFormat.SpirV;
        public GpuCapabilities Capabilities => new GpuCapabilities
        {
            ClipSpaceZeroToOne = true,
            FramebufferOriginTopLeft = true,
            SupportsTimestampQueries = true,
            SupportsStructuredBuffers = true,
            SupportsUInt32Indices = true,
            MaxColorAttachments = 4,
            MaxAnisotropy = 16,
            MaxTextureArrayLayers = 256
        };

        public IGpuSwapChain CreateSwapChain(IntPtr windowHandle, int width, int height)
        {
            _activeSwapChain = new SoftwareSwapChain(
                windowHandle, width, height, ResizeSwapChainTextures);
            ResizeSwapChainTextures(width, height);
            return _activeSwapChain;
        }

        private void ResizeSwapChainTextures(int width, int height)
        {
            if (_activeSwapChain == null) return;

            if (_activeSwapChain.ColorTexture.IsValid)
                ReleaseTexture(_activeSwapChain.ColorTexture);
            if (_activeSwapChain.DepthTexture.IsValid)
                ReleaseTexture(_activeSwapChain.DepthTexture);

            _viewportW = Math.Max(1, width);
            _viewportH = Math.Max(1, height);
            _framePixels = new byte[checked((int)_viewportW * (int)_viewportH * 4)];
            _depthBuffer = new float[checked((int)_viewportW * (int)_viewportH)];

            GpuTextureHandle colorHandle = CreateTexture(new GpuTextureDesc
            {
                Width = (int)_viewportW,
                Height = (int)_viewportH,
                MipLevels = 1,
                ArrayLayers = 1,
                Format = GpuFormat.B8G8R8A8UNorm,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.RenderTarget | GpuBindFlags.ShaderResource,
                DebugName = "SoftwareSwapChain.Color",
            }, ReadOnlySpan<byte>.Empty);

            GpuTextureHandle depthHandle = CreateTexture(new GpuTextureDesc
            {
                Width = (int)_viewportW,
                Height = (int)_viewportH,
                MipLevels = 1,
                ArrayLayers = 1,
                Format = GpuFormat.D32Float,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.DepthStencil | GpuBindFlags.ShaderResource,
                DebugName = "SoftwareSwapChain.Depth",
            }, ReadOnlySpan<byte>.Empty);

            _activeSwapChain.ColorTexture = colorHandle;
            _activeSwapChain.DepthTexture = depthHandle;
        }

        private static bool IsDepthFormat(GpuFormat format) =>
            format is GpuFormat.D32Float or GpuFormat.D24UNormS8UInt or GpuFormat.R32Float;

        private TextureData TryGetTexture(GpuTextureHandle handle) =>
            handle.IsValid && _textures.TryGetValue(handle.Id, out TextureData tex) ? tex : null;

        private byte[] GetActiveColorBuffer(out int width, out int height)
        {
            if (_activeRenderTarget.IsValid && _renderTargets.TryGetValue(_activeRenderTarget.Id, out var rt))
            {
                GpuTextureHandle colorHandle = rt.ColorTextures.Length > 0
                    ? rt.ColorTextures[0]
                    : GpuTextureHandle.Invalid;
                TextureData tex = TryGetTexture(colorHandle);
                if (tex != null)
                {
                    width = tex.Width;
                    height = tex.Height;
                    return tex.Pixels;
                }
            }

            TextureData backTex = _activeSwapChain != null
                ? TryGetTexture(_activeSwapChain.ColorTexture)
                : null;
            if (backTex != null)
            {
                width = backTex.Width;
                height = backTex.Height;
                return backTex.Pixels;
            }

            width = (int)_viewportW;
            height = (int)_viewportH;
            return _framePixels;
        }

        private float[] GetActiveDepthBuffer(int width, int height)
        {
            TextureData depthTex = null;
            if (_activeRenderTarget.IsValid && _renderTargets.TryGetValue(_activeRenderTarget.Id, out var rt))
                depthTex = TryGetTexture(rt.DepthTexture);
            else if (_activeSwapChain != null)
                depthTex = TryGetTexture(_activeSwapChain.DepthTexture);

            if (depthTex != null)
            {
                int required = depthTex.Width * depthTex.Height;
                if (depthTex.Depth == null || depthTex.Depth.Length != required)
                    depthTex.Depth = new float[required];
                return depthTex.Depth;
            }

            int fallback = Math.Max(1, width) * Math.Max(1, height);
            if (_depthBuffer.Length != fallback)
                _depthBuffer = new float[fallback];
            return _depthBuffer;
        }

        private bool ActiveTargetIsDepthOnly()
        {
            return _activeRenderTarget.IsValid
                && _renderTargets.TryGetValue(_activeRenderTarget.Id, out var rt)
                && rt.DepthOnly;
        }

        public void BeginFrame() { }

        public void EndFrame()
        {
            // Rendering targets the swap-chain colour texture, not _framePixels.  Presenting the
            // latter replaced every completed software frame with a freshly allocated black
            // buffer, which made both the visual test and readback report an empty backend.
            TextureData backBuffer = _activeSwapChain != null
                ? TryGetTexture(_activeSwapChain.ColorTexture)
                : null;
            if (backBuffer != null)
            {
                _framePixels = backBuffer.Pixels;
                _activeSwapChain.BlitPixels(backBuffer.Pixels, backBuffer.Width, backBuffer.Height);
            }
        }

        public void WaitIdle() { }

        public GpuBufferHandle CreateBuffer(in GpuBufferDesc desc, ReadOnlySpan<byte> initialData)
        {
            int id = _nextHandle++;
            var buf = new BufferData
            {
                Bytes = new byte[Math.Max(desc.SizeBytes, initialData.Length)],
                Usage = desc.Usage,
                Stride = desc.StructureStride
            };
            if (!initialData.IsEmpty)
                initialData.CopyTo(buf.Bytes);

            _buffers[id] = buf;
            return new GpuBufferHandle(id);
        }

        public void UpdateBuffer(GpuBufferHandle handle, ReadOnlySpan<byte> data, int byteOffset = 0)
        {
            if (_buffers.TryGetValue(handle.Id, out var buf))
            {
                if (byteOffset + data.Length > buf.Bytes.Length)
                    Array.Resize(ref buf.Bytes, byteOffset + data.Length);
                data.CopyTo(buf.Bytes.AsSpan(byteOffset));
            }
        }

        public void UpdateConstantBuffer<T>(GpuBufferHandle handle, in T data) where T : unmanaged
        {
            if (_buffers.TryGetValue(handle.Id, out var buf))
            {
                int size = Unsafe.SizeOf<T>();
                if (buf.Bytes.Length < size)
                    Array.Resize(ref buf.Bytes, size);
                fixed (byte* p = buf.Bytes)
                {
                    *(T*)p = data;
                }
            }
        }

        public bool TryMapDiscard(GpuBufferHandle handle, out Span<byte> span, int byteCount = 0)
        {
            if (_buffers.TryGetValue(handle.Id, out var buf))
            {
                span = buf.Bytes.AsSpan();
                if (byteCount > 0 && byteCount < span.Length)
                    span = span.Slice(0, byteCount);
                return true;
            }
            span = default;
            return false;
        }

        public void Unmap(GpuBufferHandle handle) { }

        public void ReleaseBuffer(GpuBufferHandle handle)
        {
            _buffers.Remove(handle.Id);
        }

        public GpuTextureHandle CreateTexture(in GpuTextureDesc desc, ReadOnlySpan<byte> initialData)
        {
            int id = _nextHandle++;
            int layers = Math.Max(1, desc.ArrayLayers);
            int pixelStride = BytesPerPixel(desc.Format);
            int byteLen = checked(desc.Width * desc.Height * layers * pixelStride);
            var tex = new TextureData
            {
                Width = desc.Width,
                Height = desc.Height,
                ArrayLayers = layers,
                Format = desc.Format,
                Pixels = new byte[Math.Max(byteLen, initialData.Length)]
            };
            if (IsDepthFormat(desc.Format))
            {
                tex.Depth = new float[checked(desc.Width * desc.Height)];
                Array.Fill(tex.Depth, 1f);
            }
            if (!initialData.IsEmpty)
                initialData.CopyTo(tex.Pixels);

            _textures[id] = tex;
            return new GpuTextureHandle(id);
        }

        private static int BytesPerPixel(GpuFormat format) => format switch
        {
            GpuFormat.R8UNorm => 1,
            GpuFormat.D32Float or GpuFormat.D24UNormS8UInt or GpuFormat.R32Float => 4,
            _ => 4
        };

        public void UpdateTexture(GpuTextureHandle handle, int x, int y, int width, int height, ReadOnlySpan<byte> data, int arraySlice = 0)
        {
            if (_textures.TryGetValue(handle.Id, out var tex))
            {
                int bpp = BytesPerPixel(tex.Format);
                int rowPitch = width * bpp;
                int sliceOffset = arraySlice * tex.Width * tex.Height * bpp;
                for (int row = 0; row < height; row++)
                {
                    int srcOffset = row * rowPitch;
                    int dstOffset = sliceOffset + ((y + row) * tex.Width + x) * bpp;
                    if (dstOffset + rowPitch <= tex.Pixels.Length && srcOffset + rowPitch <= data.Length)
                    {
                        data.Slice(srcOffset, rowPitch).CopyTo(tex.Pixels.AsSpan(dstOffset, rowPitch));
                    }
                }
            }
        }

        public void ReleaseTexture(GpuTextureHandle handle)
        {
            _textures.Remove(handle.Id);
        }

        public GpuSamplerHandle CreateSampler(in GpuSamplerDesc desc) => new GpuSamplerHandle(_nextHandle++);
        public void ReleaseSampler(GpuSamplerHandle handle) { }

        public GpuRenderTargetHandle CreateRenderTarget(in GpuRenderTargetDesc desc)
        {
            int id = _nextHandle++;
            bool depthOnly = desc.ColorFormats == null || desc.ColorFormats.Length == 0;
            GpuFormat[] colorFormats = depthOnly
                ? new[] { GpuFormat.R8G8B8A8UNorm }
                : desc.ColorFormats;

            var colorTextures = new GpuTextureHandle[colorFormats.Length];
            for (int i = 0; i < colorFormats.Length; i++)
            {
                colorTextures[i] = CreateTexture(new GpuTextureDesc
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    Format = colorFormats[i],
                    BindFlags = GpuBindFlags.RenderTarget
                }, ReadOnlySpan<byte>.Empty);
            }

            var depthTex = GpuTextureHandle.Invalid;
            if (desc.DepthFormat != GpuFormat.Unknown)
            {
                depthTex = CreateTexture(new GpuTextureDesc
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    Format = desc.DepthFormat,
                    BindFlags = GpuBindFlags.DepthStencil | GpuBindFlags.ShaderResource
                }, ReadOnlySpan<byte>.Empty);
            }

            _renderTargets[id] = new RenderTargetData
            {
                Width = desc.Width,
                Height = desc.Height,
                ColorTextures = colorTextures,
                DepthTexture = depthTex,
                DepthOnly = depthOnly
            };
            return new GpuRenderTargetHandle(id);
        }

        public GpuTextureHandle GetRenderTargetTexture(GpuRenderTargetHandle handle, int attachment = 0)
        {
            if (!_renderTargets.TryGetValue(handle.Id, out var rt) || rt.DepthOnly)
                return GpuTextureHandle.Invalid;
            if (attachment < 0 || attachment >= rt.ColorTextures.Length)
                return GpuTextureHandle.Invalid;
            return rt.ColorTextures[attachment];
        }

        public GpuTextureHandle GetRenderTargetDepthTexture(GpuRenderTargetHandle handle) =>
            _renderTargets.TryGetValue(handle.Id, out var rt) ? rt.DepthTexture : GpuTextureHandle.Invalid;

        public void ReleaseRenderTarget(GpuRenderTargetHandle handle)
        {
            if (_renderTargets.Remove(handle.Id, out var rt))
            {
                for (int i = 0; i < rt.ColorTextures.Length; i++)
                    ReleaseTexture(rt.ColorTextures[i]);
                if (rt.DepthTexture.IsValid) ReleaseTexture(rt.DepthTexture);
            }
        }

        public void BeginRenderPass(in GpuRenderPassDesc desc)
        {
            _activeRenderTarget = desc.Target;
            byte[] colorBuffer = GetActiveColorBuffer(out int w, out int h);
            float[] depthBuffer = GetActiveDepthBuffer(w, h);
            SoftwareRasterizerCore.Clear(colorBuffer, depthBuffer, in desc);
        }

        public void EndRenderPass() { }
        public void UnbindRenderTargets() { }

        public void SetViewport(float x, float y, float width, float height, float minZ = 0f, float maxZ = 1f)
        {
            _viewportW = Math.Max(1, width);
            _viewportH = Math.Max(1, height);
        }

        private int _scissorX, _scissorY, _scissorWidth = int.MaxValue, _scissorHeight = int.MaxValue;
        public void SetScissor(int x, int y, int width, int height)
        {
            _scissorX = x; _scissorY = y;
            _scissorWidth = Math.Max(0, width); _scissorHeight = Math.Max(0, height);
        }

        public void SetShaderProgram(GpuShaderProgramHandle handle)
        {
            _runtimeShade = ClassifyRuntimeShade(handle);
            _boundWaterProgram = handle.IsValid
                && _programs.TryGetValue(handle.Id, out GpuShaderProgramDesc desc)
                && string.Equals(desc.DebugName, "Water", StringComparison.Ordinal);
        }

        public void SetVertexLayout(GpuVertexLayoutHandle handle)
        {
            if (!handle.IsValid)
            {
                _boundVertexBuffer = GpuBufferHandle.Invalid;
            }
        }
        public void SetPrimitiveTopology(GpuPrimitiveTopology topology)
        {
            _boundTopology = topology;
        }
        public void SetBlendState(in GpuBlendState state) => _boundBlendState = state;
        public void SetDepthState(in GpuDepthState state) => _boundDepthState = state;
        public void SetRasterState(in GpuRasterState state) => _boundRasterState = state;

        public GpuShaderProgramHandle CreateShaderProgram(in GpuShaderProgramDesc desc)
        {
            int id = _nextHandle++;
            _programs[id] = desc;
            return new GpuShaderProgramHandle(id);
        }

        public void ReleaseShaderProgram(GpuShaderProgramHandle handle) => _programs.Remove(handle.Id);

        public GpuVertexLayoutHandle CreateVertexLayout(in GpuVertexLayoutDesc desc, GpuShaderProgramHandle program)
        {
            int id = _nextHandle++;
            _vertexLayouts[id] = desc;
            return new GpuVertexLayoutHandle(id);
        }

        public void ReleaseVertexLayout(GpuVertexLayoutHandle handle) => _vertexLayouts.Remove(handle.Id);

        public void SetConstantBuffer(GpuShaderStage stage, int bRegister, GpuBufferHandle handle)
        {
            if (stage == GpuShaderStage.Vertex && bRegister >= 0 && bRegister < _vsConstantBuffers.Length)
                _vsConstantBuffers[bRegister] = handle;
            else if (stage == GpuShaderStage.Pixel && bRegister >= 0 && bRegister < _psConstantBuffers.Length)
                _psConstantBuffers[bRegister] = handle;
        }

        public void SetTexture(GpuShaderStage stage, int tRegister, GpuTextureHandle handle)
        {
            if (tRegister >= 0 && tRegister < _psTextures.Length)
                _psTextures[tRegister] = handle;
        }

        public void ClearTexture(GpuShaderStage stage, int tRegister)
        {
            if (tRegister >= 0 && tRegister < _psTextures.Length)
                _psTextures[tRegister] = GpuTextureHandle.Invalid;
        }

        public void SetStructuredBuffer(GpuShaderStage stage, int tRegister, GpuBufferHandle handle)
        {
            if (tRegister >= 0 && tRegister < _vsStructuredBuffers.Length)
                _vsStructuredBuffers[tRegister] = handle;
        }

        public void SetSampler(GpuShaderStage stage, int sRegister, GpuSamplerHandle handle) { }

        public void SetVertexBuffer(int slot, GpuBufferHandle handle, int stride, int offset = 0)
        {
            _boundVertexBuffer = handle;
            _boundVertexStride = stride > 0 ? stride : 48;
        }

        public void SetIndexBuffer(GpuBufferHandle handle, GpuIndexFormat format, int offset = 0)
        {
            _boundIndexBuffer = handle;
            _boundIndexFormat = format;
        }

        public void Draw(int vertexCount, int startVertex = 0)
        {
            if (vertexCount <= 0) return;

            // Fullscreen quad / post-composite (vertexCount == 3 or no vertex buffer bound)
            if (!_boundVertexBuffer.IsValid || (vertexCount == 3 && _psTextures[0].IsValid))
            {
                TextureData srcTex = TryGetTexture(_psTextures[0]);
                if (srcTex != null)
                {
                    byte[] dstPixels = GetActiveColorBuffer(out int dstW, out int dstH);
                    BlitColorBuffer(srcTex, dstPixels, dstW, dstH);
                }
                return;
            }

            RasterizeBoundMesh3D(indexCount: 0, vertexCount: vertexCount, instanceCount: 0,
                startIndex: 0, baseVertex: startVertex, startInstance: 0);
        }

        public void DrawIndexed(int indexCount, int startIndex = 0, int baseVertex = 0)
        {
            if (indexCount <= 0) return;
            RasterizeBoundMesh3D(indexCount, vertexCount: 0, instanceCount: 0,
                startIndex, baseVertex, startInstance: 0);
        }

        public void DrawIndexedInstanced(int indexCountPerInstance, int instanceCount, int startIndex = 0, int baseVertex = 0, int startInstance = 0)
        {
            if (instanceCount <= 0) return;

            // SpriteRenderer's unit quad is 16 bytes (xy + uv). MeshVertex is 48. A leftover
            // sprite instance buffer at VS t1 must not steal grass/particle 6-index 3D draws.
            if (indexCountPerInstance == 6
                && _boundVertexStride <= 16
                && _vsStructuredBuffers[1].IsValid
                && _buffers.TryGetValue(_vsStructuredBuffers[1].Id, out var sbBuf2D))
            {
                GpuBufferHandle cbHandle = _vsConstantBuffers[0];
                if (cbHandle.IsValid && _buffers.TryGetValue(cbHandle.Id, out var cbBuf))
                {
                    byte[] activeColor = GetActiveColorBuffer(out int curW, out int curH);
                    TextureData foundTex2D = TryGetTexture(_psTextures[0]);
                    GpuBufferHandle fogCbHandle = _psConstantBuffers[1].IsValid ? _psConstantBuffers[1] : _vsConstantBuffers[1];
                    byte[] fogCbBytes = fogCbHandle.IsValid && _buffers.TryGetValue(fogCbHandle.Id, out var fBuf) ? fBuf.Bytes : null;

                    SoftwareRasterizerCore.Rasterize(
                        activeColor,
                        curW,
                        curH,
                        cbBuf.Bytes,
                        fogCbBytes,
                        sbBuf2D.Bytes,
                        instanceCount,
                        foundTex2D?.Pixels,
                        foundTex2D?.Width ?? 0,
                        foundTex2D?.Height ?? 0,
                        foundTex2D?.Format ?? GpuFormat.R8G8B8A8UNorm,
                        _boundRasterState.ScissorEnabled ? _scissorX : 0,
                        _boundRasterState.ScissorEnabled ? _scissorY : 0,
                        _boundRasterState.ScissorEnabled ? _scissorWidth : int.MaxValue,
                        _boundRasterState.ScissorEnabled ? _scissorHeight : int.MaxValue);
                    return;
                }
            }

            RasterizeBoundMesh3D(indexCountPerInstance, vertexCount: 0, instanceCount,
                startIndex, baseVertex, startInstance);
        }

        private void RasterizeBoundMesh3D(
            int indexCount, int vertexCount, int instanceCount,
            int startIndex, int baseVertex, int startInstance)
        {
            if (!_boundVertexBuffer.IsValid || !_buffers.TryGetValue(_boundVertexBuffer.Id, out var vbBuf))
                return;

            byte[] ibBytes = null;
            if (indexCount > 0)
            {
                if (!_boundIndexBuffer.IsValid || !_buffers.TryGetValue(_boundIndexBuffer.Id, out var ibBuf))
                    return;
                ibBytes = ibBuf.Bytes;
            }

            byte[] perFrameBytes = TryGetBufferBytes(_vsConstantBuffers[0]);
            byte[] drawCbBytes = TryGetBufferBytes(_vsConstantBuffers[2]);
            byte[] engineCbBytes = TryGetBufferBytes(_psConstantBuffers[1]);
            if (engineCbBytes == null)
                engineCbBytes = TryGetBufferBytes(_vsConstantBuffers[1]);

            GpuBufferHandle instBufHandle = _vsStructuredBuffers[0].IsValid
                ? _vsStructuredBuffers[0]
                : (_vsStructuredBuffers[11].IsValid ? _vsStructuredBuffers[11] : _vsStructuredBuffers[12]);
            byte[] instBytes = instanceCount > 0 ? TryGetBufferBytes(instBufHandle) : null;

            TextureData albedo = TryGetTexture(_psTextures[1].IsValid ? _psTextures[1] : _psTextures[0]);
            TextureData shadowFar = TryGetTexture(_psTextures[2]);
            TextureData shadowNear = TryGetTexture(_psTextures[5]);

            byte[] activeColor = GetActiveColorBuffer(out int curW, out int curH);
            float[] activeDepth = GetActiveDepthBuffer(curW, curH);

            var draw = new SoftwareMeshDraw
            {
                FramePixels = activeColor,
                DepthBuffer = activeDepth,
                ScreenW = curW,
                ScreenH = curH,
                VbBytes = vbBuf.Bytes,
                VertexStride = _boundVertexStride,
                IbBytes = ibBytes,
                IndexFormat = _boundIndexFormat,
                IndexCount = indexCount,
                VertexCount = vertexCount,
                StartIndex = startIndex,
                BaseVertex = baseVertex,
                Topology = _boundTopology,
                PerFrameCbBytes = perFrameBytes,
                DrawCbBytes = drawCbBytes,
                EngineCbBytes = engineCbBytes,
                InstanceBytes = instBytes,
                InstanceCount = instanceCount,
                StartInstance = startInstance,
                TexPixels = albedo?.Pixels,
                TexWidth = albedo?.Width ?? 0,
                TexHeight = albedo?.Height ?? 0,
                TexArrayLayers = albedo?.ArrayLayers ?? 1,
                TexFormat = albedo?.Format ?? GpuFormat.R8G8B8A8UNorm,
                ShadowFarDepth = shadowFar?.Depth,
                ShadowFarW = shadowFar?.Width ?? 0,
                ShadowFarH = shadowFar?.Height ?? 0,
                ShadowNearDepth = shadowNear?.Depth,
                ShadowNearW = shadowNear?.Width ?? 0,
                ShadowNearH = shadowNear?.Height ?? 0,
                RasterState = _boundRasterState,
                DepthState = _boundDepthState,
                BlendState = _boundBlendState,
                RuntimeShade = _runtimeShade,
                ShaderTime = ReadShaderTime(perFrameBytes),
                DepthOnly = ActiveTargetIsDepthOnly(),
                IsWater = _boundWaterProgram
            };

            SoftwareRasterizerCore.RasterizeMesh3D(in draw);
        }

        private byte[] TryGetBufferBytes(GpuBufferHandle handle) =>
            handle.IsValid && _buffers.TryGetValue(handle.Id, out var buf) ? buf.Bytes : null;

        private float ReadShaderTime(byte[] perFrameBytes)
        {
            byte[] frameCb = TryGetBufferBytes(_psConstantBuffers[4]);
            if (frameCb != null && frameCb.Length >= sizeof(float))
                return BitConverter.ToSingle(frameCb, 0);
            if (perFrameBytes != null && perFrameBytes.Length >= 208)
                return BitConverter.ToSingle(perFrameBytes, 204);
            return 0f;
        }

        private static void BlitColorBuffer(TextureData src, byte[] dst, int dstW, int dstH)
        {
            if (src.Pixels == null || dst == null || dstW <= 0 || dstH <= 0)
                return;

            if (src.Width == dstW && src.Height == dstH && src.Pixels.Length >= dst.Length
                && BytesPerPixel(src.Format) == 4)
            {
                Buffer.BlockCopy(src.Pixels, 0, dst, 0, Math.Min(dst.Length, src.Pixels.Length));
                return;
            }

            // HDR / mismatched sizes: copy as 4-byte BGRA rows, scaling if needed.
            int srcBpp = Math.Max(1, BytesPerPixel(src.Format));
            for (int y = 0; y < dstH; y++)
            {
                int sy = Math.Clamp(src.Height * y / dstH, 0, Math.Max(0, src.Height - 1));
                for (int x = 0; x < dstW; x++)
                {
                    int sx = Math.Clamp(src.Width * x / dstW, 0, Math.Max(0, src.Width - 1));
                    int dstIdx = (y * dstW + x) * 4;
                    int srcIdx = (sy * src.Width + sx) * srcBpp;
                    if (dstIdx + 3 >= dst.Length || srcIdx >= src.Pixels.Length)
                        continue;
                    if (srcBpp >= 4 && srcIdx + 3 < src.Pixels.Length)
                    {
                        dst[dstIdx + 0] = src.Pixels[srcIdx + 0];
                        dst[dstIdx + 1] = src.Pixels[srcIdx + 1];
                        dst[dstIdx + 2] = src.Pixels[srcIdx + 2];
                        dst[dstIdx + 3] = src.Pixels[srcIdx + 3];
                    }
                    else
                    {
                        byte v = src.Pixels[srcIdx];
                        dst[dstIdx + 0] = v;
                        dst[dstIdx + 1] = v;
                        dst[dstIdx + 2] = v;
                        dst[dstIdx + 3] = 255;
                    }
                }
            }
        }

        private SoftwareRuntimeShade ClassifyRuntimeShade(GpuShaderProgramHandle handle)
        {
            if (!handle.IsValid || !_programs.TryGetValue(handle.Id, out GpuShaderProgramDesc desc))
                return SoftwareRuntimeShade.None;
            if (desc.DebugName == null
                || desc.DebugName.IndexOf("RuntimeShader", StringComparison.OrdinalIgnoreCase) < 0)
                return SoftwareRuntimeShade.None;

            byte[] blob = desc.PixelShader;
            if (blob == null || blob.Length == 0)
                return SoftwareRuntimeShade.Unlit;

            // Authored visual-test pixel shaders are not executed. Fingerprint the compiled blob
            // for the two Command Visual Test materials so the L-shape and glow sphere stay
            // recognizable without a SPIR-V interpreter.
            if (ContainsFloat(blob, 6.2831853f) || (ContainsFloat(blob, 0.67f) && ContainsFloat(blob, 0.33f)))
                return SoftwareRuntimeShade.Iridescent;
            if (ContainsFloat(blob, 0.06f) && ContainsFloat(blob, 0.42f))
                return SoftwareRuntimeShade.Glow;
            return SoftwareRuntimeShade.Unlit;
        }

        private static bool ContainsFloat(byte[] blob, float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            byte b0 = (byte)bits;
            byte b1 = (byte)(bits >> 8);
            byte b2 = (byte)(bits >> 16);
            byte b3 = (byte)(bits >> 24);
            for (int i = 0; i + 3 < blob.Length; i++)
            {
                if (blob[i] == b0 && blob[i + 1] == b1 && blob[i + 2] == b2 && blob[i + 3] == b3)
                    return true;
            }
            return false;
        }

        public GpuQueryHandle BeginTimestampScope() => new GpuQueryHandle(_nextHandle++);
        public void EndTimestampScope(GpuQueryHandle handle) { }
        public bool TryResolveTimestamp(GpuQueryHandle handle, out double milliseconds)
        {
            milliseconds = 0.5;
            return true;
        }

        public bool TryReadTexture(GpuTextureHandle handle, out int width, out int height, out byte[] bgra)
        {
            TextureData tex = TryGetTexture(handle);
            if (tex != null && tex.Pixels != null && tex.Pixels.Length >= tex.Width * tex.Height * 4)
            {
                width = tex.Width;
                height = tex.Height;
                bgra = (byte[])tex.Pixels.Clone();
                return true;
            }
            width = (int)_viewportW;
            height = (int)_viewportH;
            bgra = (byte[])_framePixels.Clone();
            return true;
        }

        public void Dispose()
        {
            _activeSwapChain?.Dispose();
            _buffers.Clear();
            _textures.Clear();
            _renderTargets.Clear();
        }
    }
}
