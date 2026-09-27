using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Genesis.Rendering.Abstractions;
using Silk.NET.OpenGL;

namespace Genesis.Rendering.SilkNet.OpenGL
{
    /// <summary>The OpenGL 4.6 core-profile implementation of <see cref="IGpuDevice"/>.</summary>
    /// <remarks>
    /// <para><b>Clip control does the heavy lifting.</b> Genesis's shaders and projection matrices
    /// are written for Direct3D: depth in 0..1 and the framebuffer origin at the top left. GL
    /// defaults to -1..1 depth and a bottom-left origin, which would mean either patching every
    /// projection matrix or flipping every readback. <c>glClipControl(UPPER_LEFT, ZERO_TO_ONE)</c>
    /// — core since 4.5 — makes GL agree with D3D exactly, so
    /// <see cref="GpuCapabilities.ClipSpaceZeroToOne"/> and
    /// <see cref="GpuCapabilities.FramebufferOriginTopLeft"/> are both true and no coordinate
    /// fixups appear anywhere else. The one consequence is that flipping Y reverses triangle
    /// winding, so <see cref="SetRasterState"/> negates the requested front face.</para>
    ///
    /// <para><b>State is applied at draw, not when set.</b> The interface's setters are immediate but
    /// resolved lazily, matching the other backends; GL is a state machine so this amounts to
    /// tracking what changed and issuing the minimum number of calls before each draw.</para>
    /// </remarks>
    internal sealed unsafe partial class OpenGLGpuDevice : IGpuComputeDevice
    {
        private const int MaxVertexSlots = 4;
        private const int MaxTextureUnits = 16;
        private const int MaxSamplerRegisters = 4;
        private const int MaxUniformBindings = 8;

        private readonly OpenGLRuntime _runtime;
        private readonly GL _gl;

        private readonly Dictionary<int, BufferResource> _buffers = new();
        private readonly Dictionary<int, TextureResource> _textures = new();
        private readonly Dictionary<int, uint> _samplers = new();
        private readonly Dictionary<int, RenderTargetResource> _renderTargets = new();
        private readonly Dictionary<int, ProgramResource> _programs = new();
        private readonly Dictionary<int, GpuVertexLayoutDesc> _vertexLayouts = new();
        private readonly Dictionary<int, TimestampScope> _timestamps = new();

        private int _nextId = 1;
        private bool _disposed;

        // ── Tracked pipeline state ──────────────────────────────────────────────
        private GpuPrimitiveTopology _topology = GpuPrimitiveTopology.TriangleList;
        private GpuBlendState _blend = GpuBlendState.Opaque;
        private GpuDepthState _depth = GpuDepthState.Default;
        private GpuRasterState _raster = GpuRasterState.Default;
        private GpuShaderProgramHandle _program;
        private GpuVertexLayoutHandle _vertexLayout;

        private readonly GpuBufferHandle[] _vertexBuffers = new GpuBufferHandle[MaxVertexSlots];
        private readonly int[] _vertexStrides = new int[MaxVertexSlots];
        private readonly int[] _vertexOffsets = new int[MaxVertexSlots];
        private GpuBufferHandle _indexBuffer;
        private GpuIndexFormat _indexFormat = GpuIndexFormat.UInt16;
        private int _indexOffset;

        private readonly GpuTextureHandle[] _boundTextures = new GpuTextureHandle[MaxTextureUnits];
        private readonly GpuSamplerHandle[] _boundSamplers = new GpuSamplerHandle[MaxSamplerRegisters];

        private uint _vao;
        private uint _fallbackTexture;
        private uint _defaultSampler;
        private OpenGLSwapChain _activeSwapChain;
        private uint _boundFramebuffer;
        private int _passWidth, _passHeight;
        private bool _vsyncApplied;
        private bool _swapIntervalKnown;

        private delegate int SwapIntervalExt(int interval);
        private readonly SwapIntervalExt _swapInterval;

        public OpenGLGpuDevice()
        {
            // Shared, not created: every viewport's device drives the same context so their GL
            // objects live in one namespace and no device's teardown unbinds another's context.
            _runtime = OpenGLRuntime.Acquire();
            _gl = _runtime.Api;

            // The single most important call in this file — see the class remarks.
            _gl.ClipControl(ClipControlOrigin.UpperLeft, ClipControlDepth.ZeroToOne);

            _vao = _gl.GenVertexArray();
            _gl.BindVertexArray(_vao);

            _fallbackTexture = CreateFallbackTexture();
            _defaultSampler = _gl.GenSampler();

            IntPtr swapAddress = Wgl.wglGetProcAddress("wglSwapIntervalEXT");
            if (swapAddress != IntPtr.Zero && swapAddress != (IntPtr)(-1))
            {
                _swapInterval = Marshal.GetDelegateForFunctionPointer<SwapIntervalExt>(swapAddress);
            }

            Capabilities = new GpuCapabilities
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
        }

        // ── Identity ────────────────────────────────────────────────────────────

        public string BackendName => "OpenGL";

        public string AdapterName => _runtime.Renderer;

        public GpuShaderBinaryFormat ShaderBinaryFormat => GpuShaderBinaryFormat.GlslUtf8;

        public GpuCapabilities Capabilities { get; }

        public IGpuSwapChain CreateSwapChain(IntPtr windowHandle, int width, int height)
        {
            var swapChain = new OpenGLSwapChain(this, _runtime, windowHandle, width, height);
            _activeSwapChain = swapChain;
            return swapChain;
        }

        internal void SetSwapInterval(bool vsync)
        {
            if (_swapInterval == null || (_swapIntervalKnown && _vsyncApplied == vsync))
            {
                return;
            }

            _swapInterval(vsync ? 1 : 0);
            _vsyncApplied = vsync;
            _swapIntervalKnown = true;
        }

        // ── Frame envelope ──────────────────────────────────────────────────────

        public void BeginFrame()
        {
            _activeSwapChain?.MakeCurrent();
        }

        public void EndFrame()
        {
            // Nothing to submit: GL commands are already in the driver's queue by this point.
        }

        public void WaitIdle() => _gl.Finish();

        // ── Buffers ─────────────────────────────────────────────────────────────

        private sealed class BufferResource
        {
            public uint Name;
            public BufferTargetARB Target;
            public int SizeBytes;
            public int StructureStride;
            public GpuBufferUsage Usage;
            public GpuBindFlags BindFlags;
            public byte[] MapScratch;
        }

        public GpuBufferHandle CreateBuffer(in GpuBufferDesc desc, ReadOnlySpan<byte> initialData)
        {
            uint name = _gl.GenBuffer();
            BufferTargetARB target = TargetFor(desc.BindFlags);

            _gl.BindBuffer(target, name);
            BufferUsageARB usage = desc.Usage switch
            {
                GpuBufferUsage.Dynamic => BufferUsageARB.DynamicDraw,
                GpuBufferUsage.Staging => BufferUsageARB.StreamRead,
                _ => BufferUsageARB.StaticDraw,
            };

            int size = Math.Max(desc.SizeBytes, initialData.Length);
            if (!initialData.IsEmpty)
            {
                fixed (byte* data = initialData)
                {
                    _gl.BufferData(target, (nuint)size, data, usage);
                }
            }
            else
            {
                _gl.BufferData(target, (nuint)size, null, usage);
            }

            int id = _nextId++;
            _buffers[id] = new BufferResource
            {
                Name = name,
                Target = target,
                SizeBytes = size,
                StructureStride = desc.StructureStride,
                Usage = desc.Usage,
                BindFlags = desc.BindFlags,
            };

            return new GpuBufferHandle(id);
        }

        private static BufferTargetARB TargetFor(GpuBindFlags flags)
        {
            if ((flags & GpuBindFlags.IndexBuffer) != 0) return BufferTargetARB.ElementArrayBuffer;
            if ((flags & GpuBindFlags.ConstantBuffer) != 0) return BufferTargetARB.UniformBuffer;
            if ((flags & GpuBindFlags.ShaderResource) != 0) return BufferTargetARB.ShaderStorageBuffer;
            return BufferTargetARB.ArrayBuffer;
        }

        public void UpdateBuffer(GpuBufferHandle handle, ReadOnlySpan<byte> data, int byteOffset = 0)
        {
            if (!_buffers.TryGetValue(handle.Id, out BufferResource buffer) || data.IsEmpty)
            {
                return;
            }

            _gl.BindBuffer(buffer.Target, buffer.Name);
            fixed (byte* source = data)
            {
                _gl.BufferSubData(buffer.Target, (nint)byteOffset, (nuint)data.Length, source);
            }
        }

        public void UpdateConstantBuffer<T>(GpuBufferHandle handle, in T data) where T : unmanaged
        {
            fixed (T* value = &data)
            {
                UpdateBuffer(handle, new ReadOnlySpan<byte>(value, Unsafe.SizeOf<T>()));
            }
        }

        public bool TryMapDiscard(GpuBufferHandle handle, out Span<byte> span, int byteCount = 0)
        {
            span = default;
            if (!_buffers.TryGetValue(handle.Id, out BufferResource buffer))
            {
                return false;
            }

            // Staged through managed memory rather than glMapBufferRange. The caller may fill only
            // part of the span and GL would otherwise leave the remainder as whatever the driver
            // last had there, which differs from the discard semantics the other backends provide.
            buffer.MapScratch ??= new byte[buffer.SizeBytes];
            Array.Clear(buffer.MapScratch);
            span = buffer.MapScratch;
            if (byteCount > 0 && byteCount < span.Length)
                span = span.Slice(0, byteCount);
            return true;
        }

        public void Unmap(GpuBufferHandle handle)
        {
            if (!_buffers.TryGetValue(handle.Id, out BufferResource buffer) || buffer.MapScratch == null)
            {
                return;
            }

            _gl.BindBuffer(buffer.Target, buffer.Name);
            fixed (byte* source = buffer.MapScratch)
            {
                // Orphan first: respecifying the store lets the driver hand back fresh memory rather
                // than stalling until the previous contents are no longer in flight.
                _gl.BufferData(buffer.Target, (nuint)buffer.SizeBytes, null, BufferUsageARB.DynamicDraw);
                _gl.BufferSubData(buffer.Target, 0, (nuint)buffer.SizeBytes, source);
            }
        }

        public void ReleaseBuffer(GpuBufferHandle handle)
        {
            if (_buffers.Remove(handle.Id, out BufferResource buffer))
            {
                _gl.DeleteBuffer(buffer.Name);
            }
        }

        // ── Textures and samplers ───────────────────────────────────────────────

        private sealed class TextureResource
        {
            public uint Name;
            public int Width, Height, MipLevels;
            public GpuFormat Format;
            public bool IsDepth;
        }

        internal uint TextureName(GpuTextureHandle handle) =>
            _textures.TryGetValue(handle.Id, out TextureResource texture) ? texture.Name : 0u;

        /// <summary>Allocates a colour or depth attachment for a swap chain surface.</summary>
        internal GpuTextureHandle CreateSurfaceTexture(int width, int height, GpuFormat format) =>
            CreateTexture(new GpuTextureDesc
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArrayLayers = 1,
                Format = format,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.ShaderResource | GpuBindFlags.RenderTarget,
                DebugName = "OpenGLSwapChain.Surface",
            }, ReadOnlySpan<byte>.Empty);

        public GpuTextureHandle CreateTexture(in GpuTextureDesc desc, ReadOnlySpan<byte> initialData)
        {
            uint name = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, name);

            int mipLevels = Math.Max(1, desc.MipLevels);
            _gl.TexStorage2D(
                TextureTarget.Texture2D,
                (uint)mipLevels,
                (SizedInternalFormat)OpenGLGpuFormats.ToInternal(desc.Format),
                (uint)desc.Width,
                (uint)desc.Height);

            int id = _nextId++;
            var texture = new TextureResource
            {
                Name = name,
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = mipLevels,
                Format = desc.Format,
                IsDepth = OpenGLGpuFormats.IsDepth(desc.Format),
            };
            _textures[id] = texture;

            if (!initialData.IsEmpty)
            {
                UploadMipChain(texture, initialData);
            }

            return new GpuTextureHandle(id);
        }

        /// <summary>
        /// Uploads Genesis's tightly-packed mip payload, which has no per-row or per-level padding.
        /// </summary>
        private void UploadMipChain(TextureResource texture, ReadOnlySpan<byte> data)
        {
            bool compressed = OpenGLGpuFormats.IsBlockCompressed(texture.Format);

            // Genesis packs rows with no padding at all; GL's default is 4-byte row alignment, which
            // shears any texture whose row length is not a multiple of four.
            if (!compressed)
            {
                _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            }

            int offset = 0;
            for (int level = 0; level < texture.MipLevels; level++)
            {
                int levelWidth = Math.Max(1, texture.Width >> level);
                int levelHeight = Math.Max(1, texture.Height >> level);
                int levelBytes = OpenGLGpuFormats.LevelBytes(texture.Format, levelWidth, levelHeight);
                if (offset + levelBytes > data.Length)
                {
                    break;
                }

                fixed (byte* source = data.Slice(offset, levelBytes))
                {
                    if (compressed)
                    {
                        _gl.CompressedTexSubImage2D(
                            TextureTarget.Texture2D, level, 0, 0,
                            (uint)levelWidth, (uint)levelHeight,
                            (GLEnum)OpenGLGpuFormats.ToInternal(texture.Format),
                            (uint)levelBytes, source);
                    }
                    else
                    {
                        _gl.TexSubImage2D(
                            TextureTarget.Texture2D, level, 0, 0,
                            (uint)levelWidth, (uint)levelHeight,
                            OpenGLGpuFormats.ToPixelFormat(texture.Format),
                            OpenGLGpuFormats.ToPixelType(texture.Format),
                            source);
                    }
                }

                offset += levelBytes;
            }
        }

        public void UpdateTexture(
            GpuTextureHandle handle, int x, int y, int width, int height,
            ReadOnlySpan<byte> data, int arraySlice = 0)
        {
            if (!_textures.TryGetValue(handle.Id, out TextureResource texture) || data.IsEmpty)
            {
                return;
            }

            _gl.BindTexture(TextureTarget.Texture2D, texture.Name);
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            fixed (byte* source = data)
            {
                if (OpenGLGpuFormats.IsBlockCompressed(texture.Format))
                {
                    _gl.CompressedTexSubImage2D(
                        TextureTarget.Texture2D, 0, x, y, (uint)width, (uint)height,
                        (GLEnum)OpenGLGpuFormats.ToInternal(texture.Format),
                        (uint)data.Length, source);
                }
                else
                {
                    _gl.TexSubImage2D(
                        TextureTarget.Texture2D, 0, x, y, (uint)width, (uint)height,
                        OpenGLGpuFormats.ToPixelFormat(texture.Format),
                        OpenGLGpuFormats.ToPixelType(texture.Format),
                        source);
                }
            }
        }

        public void ReleaseTexture(GpuTextureHandle handle)
        {
            if (_textures.Remove(handle.Id, out TextureResource texture))
            {
                _gl.DeleteTexture(texture.Name);
            }
        }

        public GpuSamplerHandle CreateSampler(in GpuSamplerDesc desc)
        {
            uint name = _gl.GenSampler();

            _gl.SamplerParameter(name, SamplerParameterI.WrapS, (int)OpenGLGpuFormats.ToAddress(desc.AddressU));
            _gl.SamplerParameter(name, SamplerParameterI.WrapT, (int)OpenGLGpuFormats.ToAddress(desc.AddressV));
            _gl.SamplerParameter(name, SamplerParameterI.WrapR, (int)OpenGLGpuFormats.ToAddress(desc.AddressW));
            _gl.SamplerParameter(name, SamplerParameterI.MagFilter, (int)OpenGLGpuFormats.ToMagFilter(desc.Filter));
            _gl.SamplerParameter(name, SamplerParameterI.MinFilter, (int)OpenGLGpuFormats.ToMinFilter(desc.Filter, hasMips: true));

            if (desc.Filter == GpuFilter.Anisotropic && desc.MaxAnisotropy > 1)
            {
                _gl.SamplerParameter(name, (SamplerParameterF)GLEnum.TextureMaxAnisotropy, desc.MaxAnisotropy);
            }

            // Anything but Never makes this HLSL's SamplerComparisonState — the shadow cascades at s1.
            if (desc.CompareOp != GpuCompare.Never)
            {
                _gl.SamplerParameter(name, SamplerParameterI.CompareMode, (int)GLEnum.CompareRefToTexture);
                _gl.SamplerParameter(name, SamplerParameterI.CompareFunc, (int)OpenGLGpuFormats.ToCompare(desc.CompareOp));
            }

            float* border = stackalloc float[4]
            {
                desc.BorderColorR, desc.BorderColorG, desc.BorderColorB, desc.BorderColorA,
            };
            _gl.SamplerParameter(name, SamplerParameterF.BorderColor, border);

            int id = _nextId++;
            _samplers[id] = name;
            return new GpuSamplerHandle(id);
        }

        public void ReleaseSampler(GpuSamplerHandle handle)
        {
            if (_samplers.Remove(handle.Id, out uint name))
            {
                _gl.DeleteSampler(name);
            }
        }

        // ── Render targets ──────────────────────────────────────────────────────

        private sealed class RenderTargetResource
        {
            public uint Framebuffer;
            public GpuTextureHandle[] Colors;
            public GpuTextureHandle Depth;
            public int Width, Height;
        }

        public GpuRenderTargetHandle CreateRenderTarget(in GpuRenderTargetDesc desc)
        {
            uint framebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);

            GpuFormat[] colorFormats = desc.ColorFormats ?? Array.Empty<GpuFormat>();
            var colors = new GpuTextureHandle[colorFormats.Length];
            var drawBuffers = stackalloc GLEnum[8];

            for (int i = 0; i < colorFormats.Length && i < 8; i++)
            {
                colors[i] = CreateSurfaceTexture(desc.Width, desc.Height, colorFormats[i]);
                _gl.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0 + i,
                    TextureTarget.Texture2D,
                    TextureName(colors[i]),
                    0);
                drawBuffers[i] = GLEnum.ColorAttachment0 + i;
            }

            GpuTextureHandle depth = GpuTextureHandle.Invalid;
            if (desc.DepthFormat != GpuFormat.Unknown)
            {
                depth = CreateSurfaceTexture(desc.Width, desc.Height, desc.DepthFormat);
                _gl.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    OpenGLGpuFormats.HasStencil(desc.DepthFormat)
                        ? FramebufferAttachment.DepthStencilAttachment
                        : FramebufferAttachment.DepthAttachment,
                    TextureTarget.Texture2D,
                    TextureName(depth),
                    0);
            }

            if (colorFormats.Length > 0)
            {
                _gl.DrawBuffers((uint)Math.Min(colorFormats.Length, 8), drawBuffers);
            }
            else
            {
                // A depth-only target still has to say it writes no colour, or it is incomplete.
                _gl.DrawBuffer(DrawBufferMode.None);
                _gl.ReadBuffer(ReadBufferMode.None);
            }

            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                throw new InvalidOperationException(
                    $"OpenGL render target '{desc.DebugName}' is incomplete ({status}).");
            }

            int id = _nextId++;
            _renderTargets[id] = new RenderTargetResource
            {
                Framebuffer = framebuffer,
                Colors = colors,
                Depth = depth,
                Width = desc.Width,
                Height = desc.Height,
            };

            RestoreFramebuffer();
            return new GpuRenderTargetHandle(id);
        }

        public GpuTextureHandle GetRenderTargetTexture(GpuRenderTargetHandle handle, int attachment = 0) =>
            _renderTargets.TryGetValue(handle.Id, out RenderTargetResource target)
            && attachment >= 0 && attachment < target.Colors.Length
                ? target.Colors[attachment]
                : GpuTextureHandle.Invalid;

        public GpuTextureHandle GetRenderTargetDepthTexture(GpuRenderTargetHandle handle) =>
            _renderTargets.TryGetValue(handle.Id, out RenderTargetResource target)
                ? target.Depth
                : GpuTextureHandle.Invalid;

        public void ReleaseRenderTarget(GpuRenderTargetHandle handle)
        {
            if (!_renderTargets.Remove(handle.Id, out RenderTargetResource target))
            {
                return;
            }

            _gl.DeleteFramebuffer(target.Framebuffer);
            foreach (GpuTextureHandle color in target.Colors)
            {
                if (color.IsValid) ReleaseTexture(color);
            }

            if (target.Depth.IsValid) ReleaseTexture(target.Depth);
        }

        // ── Passes ──────────────────────────────────────────────────────────────

        public void BeginRenderPass(in GpuRenderPassDesc desc)
        {
            if (desc.Target.IsValid && _renderTargets.TryGetValue(desc.Target.Id, out RenderTargetResource target))
            {
                _boundFramebuffer = target.Framebuffer;
                _passWidth = target.Width;
                _passHeight = target.Height;
            }
            else if (_activeSwapChain != null)
            {
                _boundFramebuffer = _activeSwapChain.Framebuffer;
                _passWidth = _activeSwapChain.Width;
                _passHeight = _activeSwapChain.Height;
            }

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _boundFramebuffer);
            _gl.Viewport(0, 0, (uint)Math.Max(1, _passWidth), (uint)Math.Max(1, _passHeight));

            ApplyClears(desc);
        }

        private void ApplyClears(in GpuRenderPassDesc desc)
        {
            GpuAttachmentAction[] colorActions = desc.ColorActions ?? Array.Empty<GpuAttachmentAction>();

            // Hoisted out of the loop: a stackalloc inside one grows the frame on every iteration.
            float* clearValue = stackalloc float[4];

            for (int i = 0; i < colorActions.Length; i++)
            {
                if (colorActions[i].Load != GpuLoadAction.Clear)
                {
                    continue;
                }

                // Scissor would silently restrict a clear, and colour writes must be on for one to
                // land — a pass beginning is exactly when the caller means the whole attachment.
                _gl.Disable(EnableCap.ScissorTest);
                _gl.ColorMask(true, true, true, true);

                GpuAttachmentAction action = colorActions[i];
                clearValue[0] = action.ClearR;
                clearValue[1] = action.ClearG;
                clearValue[2] = action.ClearB;
                clearValue[3] = action.ClearA;
                _gl.ClearBuffer(GLEnum.Color, i, clearValue);
            }

            if (desc.HasDepth && desc.DepthAction.Load == GpuLoadAction.Clear)
            {
                // Depth writes must be on for a depth clear to land, whatever the current state says.
                _gl.Disable(EnableCap.ScissorTest);
                _gl.DepthMask(true);
                float depthValue = desc.DepthAction.ClearR;
                _gl.ClearBuffer(GLEnum.Depth, 0, &depthValue);
            }
        }

        public void EndRenderPass()
        {
        }

        public void UnbindRenderTargets()
        {
            _boundFramebuffer = 0;
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        private void RestoreFramebuffer() =>
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _boundFramebuffer);

        public void SetViewport(float x, float y, float width, float height, float minZ = 0f, float maxZ = 1f)
        {
            _gl.Viewport((int)x, (int)y, (uint)Math.Max(1f, width), (uint)Math.Max(1f, height));
            _gl.DepthRange(minZ, maxZ);
        }

        public void SetScissor(int x, int y, int width, int height) =>
            _gl.Scissor(x, Math.Max(0, _passHeight - y - height), (uint)Math.Max(0, width), (uint)Math.Max(0, height));

        // ── Pipeline state ──────────────────────────────────────────────────────

        public void SetShaderProgram(GpuShaderProgramHandle handle) => _program = handle;
        public void SetVertexLayout(GpuVertexLayoutHandle handle) => _vertexLayout = handle;
        public void SetPrimitiveTopology(GpuPrimitiveTopology topology) => _topology = topology;
        public void SetBlendState(in GpuBlendState state) => _blend = state;
        public void SetDepthState(in GpuDepthState state) => _depth = state;
        public void SetRasterState(in GpuRasterState state) => _raster = state;

        private sealed class ProgramResource
        {
            public uint Name;
            /// <summary>Sampler register feeding each texture unit, from the transpiler's map.</summary>
            public int[] SamplerRegisterForUnit;
            public bool IsCompute;
        }

        public GpuShaderProgramHandle CreateShaderProgram(in GpuShaderProgramDesc desc)
        {
            if (desc.BinaryFormat != GpuShaderBinaryFormat.GlslUtf8)
            {
                throw new InvalidOperationException(
                    $"The OpenGL backend requires GLSL shaders; got {desc.BinaryFormat}.");
            }

            if (desc.ComputeShader?.Length > 0) return CreateComputeProgram(desc);

            string vertexSource = Encoding.UTF8.GetString(desc.VertexShader ?? Array.Empty<byte>());
            string pixelSource = Encoding.UTF8.GetString(desc.PixelShader ?? Array.Empty<byte>());

            uint vertex = CompileStage(ShaderType.VertexShader, vertexSource, desc.DebugName);
            uint fragment = CompileStage(ShaderType.FragmentShader, pixelSource, desc.DebugName);

            uint program = _gl.CreateProgram();
            if (vertex != 0) _gl.AttachShader(program, vertex);
            if (fragment != 0) _gl.AttachShader(program, fragment);
            _gl.LinkProgram(program);
            _gl.GetProgram(program, GLEnum.LinkStatus, out int linked);

            if (linked == 0)
            {
                string log = _gl.GetProgramInfoLog(program);
                _gl.DeleteProgram(program);
                if (vertex != 0) _gl.DeleteShader(vertex);
                if (fragment != 0) _gl.DeleteShader(fragment);
                throw new InvalidOperationException(
                    $"OpenGL program '{desc.DebugName}' failed to link:\n{log}");
            }

            if (vertex != 0) { _gl.DetachShader(program, vertex); _gl.DeleteShader(vertex); }
            if (fragment != 0) { _gl.DetachShader(program, fragment); _gl.DeleteShader(fragment); }

            int id = _nextId++;
            _programs[id] = new ProgramResource
            {
                Name = program,
                SamplerRegisterForUnit = ParseSamplerMap(vertexSource, pixelSource),
            };

            return new GpuShaderProgramHandle(id);
        }

        private uint CompileStage(ShaderType type, string source, string debugName)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return 0;
            }

            uint shader = _gl.CreateShader(type);
            _gl.ShaderSource(shader, source);
            _gl.CompileShader(shader);
            _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);

            if (status == 0)
            {
                string log = _gl.GetShaderInfoLog(shader);
                _gl.DeleteShader(shader);
                throw new InvalidOperationException(
                    $"OpenGL {type} for '{debugName}' failed to compile:\n{log}");
            }

            return shader;
        }

        /// <summary>
        /// Recovers the texture-unit to sampler-register pairing that SPIRV-Cross recorded.
        /// </summary>
        /// <remarks>
        /// See <see cref="Primitives.SpirvCrossToolchain"/>: combining an image and a sampler into
        /// one GLSL object erases which <c>s</c> register supplied the filtering, and the linked
        /// program cannot be asked. The transpiler writes the pairing into a leading comment.
        /// </remarks>
        private static int[] ParseSamplerMap(string vertexSource, string pixelSource)
        {
            var map = new int[MaxTextureUnits];
            foreach (string source in new[] { vertexSource, pixelSource })
            {
                if (string.IsNullOrEmpty(source)) continue;

                int start = source.IndexOf("//!genesis-samplers ", StringComparison.Ordinal);
                if (start < 0) continue;

                start += "//!genesis-samplers ".Length;
                int end = source.IndexOf('\n', start);
                if (end < 0) end = source.Length;

                foreach (string pair in source[start..end].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = pair.Split('=');
                    if (parts.Length == 2
                        && int.TryParse(parts[0], out int unit)
                        && int.TryParse(parts[1], out int register)
                        && unit >= 0 && unit < MaxTextureUnits)
                    {
                        map[unit] = register;
                    }
                }
            }

            return map;
        }

        public void ReleaseShaderProgram(GpuShaderProgramHandle handle)
        {
            if (_programs.Remove(handle.Id, out ProgramResource program))
            {
                _gl.DeleteProgram(program.Name);
            }
        }

        public GpuVertexLayoutHandle CreateVertexLayout(in GpuVertexLayoutDesc desc, GpuShaderProgramHandle program)
        {
            int id = _nextId++;
            _vertexLayouts[id] = desc;
            return new GpuVertexLayoutHandle(id);
        }

        public void ReleaseVertexLayout(GpuVertexLayoutHandle handle) => _vertexLayouts.Remove(handle.Id);

        // ── Bindings ────────────────────────────────────────────────────────────

        public void SetConstantBuffer(GpuShaderStage stage, int bRegister, GpuBufferHandle handle)
        {
            if (bRegister < 0 || bRegister >= MaxUniformBindings
                || !_buffers.TryGetValue(handle.Id, out BufferResource buffer))
            {
                return;
            }

            // The binding index is the HLSL b register, exactly as the translated GLSL declares it.
            _gl.BindBufferBase(BufferTargetARB.UniformBuffer, (uint)bRegister, buffer.Name);
        }

        public void SetTexture(GpuShaderStage stage, int tRegister, GpuTextureHandle handle)
        {
            if (tRegister < 0 || tRegister >= MaxTextureUnits)
            {
                return;
            }

            _boundTextures[tRegister] = handle;
            _gl.ActiveTexture(TextureUnit.Texture0 + tRegister);
            _gl.BindTexture(
                TextureTarget.Texture2D,
                _textures.TryGetValue(handle.Id, out TextureResource texture) ? texture.Name : _fallbackTexture);
        }

        public void ClearTexture(GpuShaderStage stage, int tRegister)
        {
            if (tRegister < 0 || tRegister >= MaxTextureUnits)
            {
                return;
            }

            _boundTextures[tRegister] = GpuTextureHandle.Invalid;
            _gl.ActiveTexture(TextureUnit.Texture0 + tRegister);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        public void SetStructuredBuffer(GpuShaderStage stage, int tRegister, GpuBufferHandle handle)
        {
            if (tRegister < 0) throw new ArgumentOutOfRangeException(nameof(tRegister));
            if ((stage & GpuShaderStage.Compute) != 0 && tRegister >= GpuComputeLimits.ReadOnlyBuffers)
                throw new ArgumentOutOfRangeException(nameof(tRegister));
            if (handle.IsValid && !_buffers.ContainsKey(handle.Id)) throw new ArgumentException("Unknown buffer.", nameof(handle));
            uint name = handle.IsValid ? _buffers[handle.Id].Name : 0;
            // Read-only compute t0..t3 and writable u0..u3 occupy distinct SSBO binding ranges.
            _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, (uint)tRegister, name);
        }

        public void SetSampler(GpuShaderStage stage, int sRegister, GpuSamplerHandle handle)
        {
            if (sRegister >= 0 && sRegister < MaxSamplerRegisters)
            {
                _boundSamplers[sRegister] = handle;
            }
        }

        public void SetVertexBuffer(int slot, GpuBufferHandle handle, int stride, int offset = 0)
        {
            if (slot < 0 || slot >= MaxVertexSlots)
            {
                return;
            }

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
            if (!BindForDraw()) return;
            _gl.DrawArrays(OpenGLGpuFormats.ToTopology(_topology), startVertex, (uint)vertexCount);
        }

        public void DrawIndexed(int indexCount, int startIndex = 0, int baseVertex = 0)
        {
            if (!BindForDraw()) return;

            _gl.DrawElementsBaseVertex(
                OpenGLGpuFormats.ToTopology(_topology),
                (uint)indexCount,
                IndexType(),
                (void*)(nint)(_indexOffset + (startIndex * IndexSize())),
                baseVertex);
        }

        public void DrawIndexedInstanced(
            int indexCountPerInstance, int instanceCount,
            int startIndex = 0, int baseVertex = 0, int startInstance = 0)
        {
            if (!BindForDraw()) return;

            _gl.DrawElementsInstancedBaseVertexBaseInstance(
                OpenGLGpuFormats.ToTopology(_topology),
                (uint)indexCountPerInstance,
                IndexType(),
                (void*)(nint)(_indexOffset + (startIndex * IndexSize())),
                (uint)instanceCount,
                baseVertex,
                (uint)startInstance);
        }

        private DrawElementsType IndexType() =>
            _indexFormat == GpuIndexFormat.UInt16 ? DrawElementsType.UnsignedShort : DrawElementsType.UnsignedInt;

        private int IndexSize() => _indexFormat == GpuIndexFormat.UInt16 ? 2 : 4;

        /// <summary>Resolves every deferred setter into GL state. False when the draw cannot proceed.</summary>
        private bool BindForDraw()
        {
            if (!_programs.TryGetValue(_program.Id, out ProgramResource program) || program.IsCompute)
            {
                return false;
            }

            _gl.UseProgram(program.Name);
            ApplyBlend();
            ApplyDepth();
            ApplyRaster();
            ApplySamplers(program);
            return ApplyVertexState();
        }

        private void ApplyBlend()
        {
            if (_blend.Enabled)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFuncSeparate(
                    OpenGLGpuFormats.ToBlend(_blend.SrcColor),
                    OpenGLGpuFormats.ToBlend(_blend.DstColor),
                    OpenGLGpuFormats.ToBlend(_blend.SrcAlpha),
                    OpenGLGpuFormats.ToBlend(_blend.DstAlpha));
                _gl.BlendEquationSeparate(
                    OpenGLGpuFormats.ToBlendOp(_blend.ColorOp),
                    OpenGLGpuFormats.ToBlendOp(_blend.AlphaOp));
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
            }

            _gl.ColorMask(_blend.WriteR, _blend.WriteG, _blend.WriteB, _blend.WriteA);

            if (_blend.IndependentBlend)
            {
                // Attachment 1 is the forward pass's fog-skip mask, which overwrites rather than blends.
                if (_blend.SecondaryEnabled) _gl.Enable(EnableCap.Blend, 1);
                else _gl.Disable(EnableCap.Blend, 1);

                _gl.ColorMask(1,
                    _blend.SecondaryWriteR, _blend.SecondaryWriteG,
                    _blend.SecondaryWriteB, _blend.SecondaryWriteA);
            }
        }

        private void ApplyDepth()
        {
            if (_depth.TestEnabled) _gl.Enable(EnableCap.DepthTest);
            else _gl.Disable(EnableCap.DepthTest);

            _gl.DepthMask(_depth.WriteEnabled);
            _gl.DepthFunc(OpenGLGpuFormats.ToCompare(_depth.Compare));
        }

        private void ApplyRaster()
        {
            if (_raster.CullMode == GpuCullMode.None)
            {
                _gl.Disable(EnableCap.CullFace);
            }
            else
            {
                _gl.Enable(EnableCap.CullFace);
                _gl.CullFace(_raster.CullMode == GpuCullMode.Front
                    ? TriangleFace.Front
                    : TriangleFace.Back);
            }

            // GL_UPPER_LEFT also negates the polygon-area sign used by front-face determination.
            // The enum therefore maps directly; do not apply another backend-specific inversion.
            _gl.FrontFace(_raster.FrontCounterClockwise ? FrontFaceDirection.Ccw : FrontFaceDirection.CW);

            _gl.PolygonMode(
                TriangleFace.FrontAndBack,
                _raster.FillMode == GpuFillMode.Wireframe ? PolygonMode.Line : PolygonMode.Fill);

            if (_raster.ScissorEnabled) _gl.Enable(EnableCap.ScissorTest);
            else _gl.Disable(EnableCap.ScissorTest);

            if (_raster.DepthBias != 0 || _raster.SlopeScaledDepthBias != 0f)
            {
                _gl.Enable(EnableCap.PolygonOffsetFill);
                _gl.PolygonOffset(_raster.SlopeScaledDepthBias, _raster.DepthBias);
            }
            else
            {
                _gl.Disable(EnableCap.PolygonOffsetFill);
            }
        }

        private void ApplySamplers(ProgramResource program)
        {
            for (int unit = 0; unit < MaxTextureUnits; unit++)
            {
                if (!_boundTextures[unit].IsValid)
                {
                    continue;
                }

                int register = program.SamplerRegisterForUnit[unit];
                uint sampler = _defaultSampler;
                if (register >= 0 && register < MaxSamplerRegisters
                    && _boundSamplers[register].IsValid
                    && _samplers.TryGetValue(_boundSamplers[register].Id, out uint bound))
                {
                    sampler = bound;
                }

                _gl.BindSampler((uint)unit, sampler);
            }
        }

        private bool ApplyVertexState()
        {
            _gl.BindVertexArray(_vao);

            if (!_vertexLayouts.TryGetValue(_vertexLayout.Id, out GpuVertexLayoutDesc layout))
            {
                return true;
            }

            GpuVertexElement[] elements = layout.Elements ?? Array.Empty<GpuVertexElement>();

            for (int i = 0; i < elements.Length; i++)
            {
                GpuVertexElement element = elements[i];
                if (element.Slot < 0 || element.Slot >= MaxVertexSlots
                    || !_buffers.TryGetValue(_vertexBuffers[element.Slot].Id, out BufferResource vertexBuffer))
                {
                    continue;
                }

                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBuffer.Name);
                _gl.EnableVertexAttribArray((uint)i);

                // Attribute index is the declaration index: DXC numbered the shader's inputs in
                // declaration order (-fvk-stage-io-order=decl) and SPIRV-Cross preserved those
                // locations, so element i in this layout is location i in the GLSL.
                nint offset = _vertexOffsets[element.Slot] + element.OffsetBytes;
                if (OpenGLGpuFormats.IsIntegerAttribute(element.Format))
                {
                    _gl.VertexAttribIPointer(
                        (uint)i,
                        OpenGLGpuFormats.ComponentCount(element.Format),
                        (VertexAttribIType)OpenGLGpuFormats.ToAttribType(element.Format),
                        (uint)_vertexStrides[element.Slot],
                        (void*)offset);
                }
                else
                {
                    _gl.VertexAttribPointer(
                        (uint)i,
                        OpenGLGpuFormats.ComponentCount(element.Format),
                        OpenGLGpuFormats.ToAttribType(element.Format),
                        OpenGLGpuFormats.IsNormalisedAttribute(element.Format),
                        (uint)_vertexStrides[element.Slot],
                        (void*)offset);
                }

                _gl.VertexAttribDivisor((uint)i, (uint)Math.Max(0, element.InstanceStepRate));
            }

            if (_buffers.TryGetValue(_indexBuffer.Id, out BufferResource indexBuffer))
            {
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, indexBuffer.Name);
            }

            return true;
        }

        // ── Timestamps ──────────────────────────────────────────────────────────

        private sealed class TimestampScope
        {
            public uint Start;
            public uint End;
            public bool Closed;
        }

        public GpuQueryHandle BeginTimestampScope()
        {
            var scope = new TimestampScope
            {
                Start = _gl.GenQuery(),
                End = _gl.GenQuery(),
            };

            _gl.QueryCounter(scope.Start, QueryCounterTarget.Timestamp);

            int id = _nextId++;
            _timestamps[id] = scope;
            return new GpuQueryHandle(id);
        }

        public void EndTimestampScope(GpuQueryHandle handle)
        {
            if (_timestamps.TryGetValue(handle.Id, out TimestampScope scope) && !scope.Closed)
            {
                _gl.QueryCounter(scope.End, QueryCounterTarget.Timestamp);
                scope.Closed = true;
            }
        }

        public bool TryResolveTimestamp(GpuQueryHandle handle, out double milliseconds)
        {
            milliseconds = 0;
            if (!_timestamps.TryGetValue(handle.Id, out TimestampScope scope) || !scope.Closed)
            {
                return false;
            }

            // Non-stalling by contract: report "not yet" rather than blocking the pipeline.
            _gl.GetQueryObject(scope.End, QueryObjectParameterName.ResultAvailable, out int available);
            if (available == 0)
            {
                return false;
            }

            _gl.GetQueryObject(scope.Start, QueryObjectParameterName.Result, out ulong start);
            _gl.GetQueryObject(scope.End, QueryObjectParameterName.Result, out ulong end);

            milliseconds = (end - start) / 1_000_000.0;

            _gl.DeleteQuery(scope.Start);
            _gl.DeleteQuery(scope.End);
            _timestamps.Remove(handle.Id);
            return true;
        }

        // ── Readback ────────────────────────────────────────────────────────────

        public bool TryReadTexture(GpuTextureHandle handle, out int width, out int height, out byte[] bgra)
        {
            width = 0;
            height = 0;
            bgra = null;

            if (!_textures.TryGetValue(handle.Id, out TextureResource texture) || texture.IsDepth)
            {
                return false;
            }

            width = texture.Width;
            height = texture.Height;
            bgra = new byte[width * height * 4];

            // Rows are tightly packed and BGRA is requested directly, so no repacking or channel
            // swizzle is needed on the CPU. No vertical flip either: UPPER_LEFT clip control already
            // stored the image top-down, matching what every visual baseline expects.
            _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            _gl.BindTexture(TextureTarget.Texture2D, texture.Name);

            fixed (byte* destination = bgra)
            {
                _gl.GetTexImage(
                    TextureTarget.Texture2D, 0,
                    PixelFormat.Bgra, PixelType.UnsignedByte,
                    destination);
            }

            return true;
        }

        private uint CreateFallbackTexture()
        {
            // Opaque white, so an unbound slot multiplies through rather than blackening the result.
            uint name = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, name);
            _gl.TexStorage2D(TextureTarget.Texture2D, 1, SizedInternalFormat.Rgba8, 1, 1);

            uint white = 0xFFFFFFFFu;
            _gl.TexSubImage2D(
                TextureTarget.Texture2D, 0, 0, 0, 1, 1,
                PixelFormat.Rgba, PixelType.UnsignedByte, &white);
            return name;
        }

        public void Dispose()
        {
            if (_disposed) return;
            DisposeComputeResources();
            _disposed = true;

            foreach (BufferResource buffer in _buffers.Values) _gl.DeleteBuffer(buffer.Name);
            foreach (TextureResource texture in _textures.Values) _gl.DeleteTexture(texture.Name);
            foreach (uint sampler in _samplers.Values) _gl.DeleteSampler(sampler);
            foreach (ProgramResource program in _programs.Values) _gl.DeleteProgram(program.Name);
            foreach (RenderTargetResource target in _renderTargets.Values) _gl.DeleteFramebuffer(target.Framebuffer);
            foreach (TimestampScope scope in _timestamps.Values)
            {
                _gl.DeleteQuery(scope.Start);
                _gl.DeleteQuery(scope.End);
            }

            _buffers.Clear();
            _textures.Clear();
            _samplers.Clear();
            _programs.Clear();
            _renderTargets.Clear();
            _timestamps.Clear();

            if (_fallbackTexture != 0) { _gl.DeleteTexture(_fallbackTexture); _fallbackTexture = 0; }
            if (_defaultSampler != 0) { _gl.DeleteSampler(_defaultSampler); _defaultSampler = 0; }
            if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }

            OpenGLRuntime.Release();
        }
    }
}
