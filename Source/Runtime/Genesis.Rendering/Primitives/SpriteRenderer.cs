using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Backend-neutral 2D textured sprite renderer plus primitive lines and rectangles.
    /// The active <see cref="IGpuDevice"/> owns every resource and draw command.
    /// </summary>
    internal sealed class SpriteRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct UnitVertex
        {
            public float X, Y;
            public float U, V;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpriteCB
        {
            public Matrix4x4 Transform;
            public uint InstanceOffset;
            public uint Pad0, Pad1, Pad2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpriteFogCB
        {
            public Vector4 FogColor;
            // x=enabled, y=depth/start, z=inverse thickness, w=max alpha
            public Vector4 FogParams;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ShaderParametersCB
        {
            public Vector4 Row0, Row1, Row2, Row3;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpriteInstanceGpu
        {
            public Vector4 PosOrigin;
            public Vector4 SizeRot;
            public Vector4 Color;
            public Vector4 DepthPad;
            public Vector4 UvRect;
        }

        private struct DrawEntry : IComparable<DrawEntry>
        {
            public SpriteDrawCall Call;
            public ulong SortKey;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int CompareTo(DrawEntry other) => SortKey.CompareTo(other.SortKey);
        }

        /// <summary>GPU instance buffer slots for 2D sprites (same order of magnitude as mesh instances).</summary>
        public const int InstanceCapacity = 32768;
        private const int MaxSprites = InstanceCapacity;
        private const float DepthRangeMin = -10240f;
        private const float DepthRangeMax = 10240f;

        private static readonly GpuBlendState SpriteBlend = new()
        {
            Enabled = true,
            SrcColor = GpuBlendFactor.SrcAlpha,
            DstColor = GpuBlendFactor.InvSrcAlpha,
            ColorOp = GpuBlendOp.Add,
            SrcAlpha = GpuBlendFactor.One,
            DstAlpha = GpuBlendFactor.Zero,
            AlphaOp = GpuBlendOp.Add,
            WriteR = true,
            WriteG = true,
            WriteB = true,
            WriteA = true,
        };

        private static readonly GpuRasterState SpriteRaster = new()
        {
            CullMode = GpuCullMode.None,
            FillMode = GpuFillMode.Solid,
            FrontCounterClockwise = false,
            ScissorEnabled = true,
            DepthClipEnabled = false,
        };

        private readonly IGpuDevice _gpu;
        private readonly byte[] _vertexShaderBytecode;
        private readonly List<DrawEntry> _pending = new();
        private readonly List<DrawEntry> _sorted = new();
        private readonly SpriteInstanceGpu[] _instanceScratch = new SpriteInstanceGpu[MaxSprites];
        private readonly Dictionary<int, GpuShaderProgramHandle> _runtimePrograms = new();
        private int _nextRuntimeProgramId = 1;
        private bool _gpuInstancesReady;

        private GpuShaderProgramHandle _program;
        private GpuShaderProgramHandle _overrideProgram;
        private GpuVertexLayoutHandle _layout;
        private GpuBufferHandle _quadVb;
        private GpuBufferHandle _quadIb;
        private GpuBufferHandle _constantBuffer;
        private GpuBufferHandle _fogConstantBuffer;
        private GpuBufferHandle _shaderParametersBuffer;
        private GpuBufferHandle _instanceBuffer;
        private GpuSamplerHandle _samplerLinear;
        private GpuSamplerHandle _samplerPoint;
        private SamplerFilter _samplerFilter = SamplerFilter.Linear;
        private RoomFogState _fog = RoomFogState.Disabled;
        private int _seq;
        private bool _disposed;

        public int LastDrawCalls;
        public int LastTriangles;
        public int LastInstancesDrawn;
        public int LastTextureSwitches;

        public int FrameDrawCalls { get; private set; }
        public int FrameTriangles { get; private set; }
        public int FrameInstancesDrawn { get; private set; }
        public int FrameTextureSwitches { get; private set; }

        public void BeginFrame()
        {
            FrameDrawCalls = 0;
            FrameTriangles = 0;
            FrameInstancesDrawn = 0;
            FrameTextureSwitches = 0;
        }

        public bool HasPixelShaderOverride => _overrideProgram.IsValid;
        public bool HasRuntimeShaders => _runtimePrograms.Count > 0;

        public SpriteRenderer(
            IGpuDevice gpu,
            ReadOnlySpan<byte> vertexShaderBytecode,
            ReadOnlySpan<byte> pixelShaderBytecode)
        {
            _gpu = gpu ?? throw new ArgumentNullException(nameof(gpu));
            if (vertexShaderBytecode.IsEmpty)
                throw new ArgumentException("Sprite rendering requires a compiled vertex shader.", nameof(vertexShaderBytecode));
            if (pixelShaderBytecode.IsEmpty)
                throw new ArgumentException("Sprite rendering requires a compiled pixel shader.", nameof(pixelShaderBytecode));

            _vertexShaderBytecode = vertexShaderBytecode.ToArray();
            try
            {
                _program = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = _vertexShaderBytecode,
                    PixelShader = pixelShaderBytecode.ToArray(),
                    DebugName = "SpriteRenderer.Default",
                });
                CreateBuffers();
                CreateLayout();
                CreateSamplers();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void CreateBuffers()
        {
            UnitVertex[] quadVertices =
            {
                new() { X = 0, Y = 0, U = 0, V = 0 },
                new() { X = 1, Y = 0, U = 1, V = 0 },
                new() { X = 1, Y = 1, U = 1, V = 1 },
                new() { X = 0, Y = 1, U = 0, V = 1 },
            };
            ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes(quadVertices.AsSpan());
            _quadVb = _gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = vertexBytes.Length,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.VertexBuffer,
                DebugName = "SpriteRenderer.UnitQuadVertices",
            }, vertexBytes);

            ushort[] quadIndices = { 0, 1, 2, 0, 2, 3 };
            ReadOnlySpan<byte> indexBytes = MemoryMarshal.AsBytes(quadIndices.AsSpan());
            _quadIb = _gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = indexBytes.Length,
                Usage = GpuBufferUsage.Immutable,
                BindFlags = GpuBindFlags.IndexBuffer,
                DebugName = "SpriteRenderer.UnitQuadIndices",
            }, indexBytes);

            _constantBuffer = _gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = Unsafe.SizeOf<SpriteCB>(),
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.ConstantBuffer,
                DebugName = "SpriteRenderer.Constants",
            }, ReadOnlySpan<byte>.Empty);

            _fogConstantBuffer = _gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = Unsafe.SizeOf<SpriteFogCB>(),
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.ConstantBuffer,
                DebugName = "SpriteRenderer.FogConstants",
            }, ReadOnlySpan<byte>.Empty);

            _shaderParametersBuffer = _gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = Unsafe.SizeOf<ShaderParametersCB>(),
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.ConstantBuffer,
                DebugName = "SpriteRenderer.ShaderParameters",
            }, ReadOnlySpan<byte>.Empty);

            int instanceStride = Unsafe.SizeOf<SpriteInstanceGpu>();
            _instanceBuffer = _gpu.CreateBuffer(new GpuBufferDesc
            {
                SizeBytes = checked(instanceStride * MaxSprites),
                Usage = GpuBufferUsage.Dynamic,
                BindFlags = GpuBindFlags.StructuredBuffer,
                StructureStride = instanceStride,
                DebugName = "SpriteRenderer.Instances",
            }, ReadOnlySpan<byte>.Empty);
        }

        private void CreateLayout()
        {
            _layout = _gpu.CreateVertexLayout(new GpuVertexLayoutDesc
            {
                Elements =
                [
                    new GpuVertexElement
                    {
                        Semantic = "POSITION",
                        SemanticIndex = 0,
                        Format = GpuFormat.R32Float2,
                        Slot = 0,
                        OffsetBytes = 0,
                    },
                    new GpuVertexElement
                    {
                        Semantic = "TEXCOORD",
                        SemanticIndex = 0,
                        Format = GpuFormat.R32Float2,
                        Slot = 0,
                        OffsetBytes = 8,
                    },
                ],
                SlotStrides = [Unsafe.SizeOf<UnitVertex>()],
                DebugName = "SpriteRenderer.UnitQuadLayout",
            }, _program);
        }

        private void CreateSamplers()
        {
            var desc = new GpuSamplerDesc
            {
                Filter = GpuFilter.Linear,
                AddressU = GpuAddressMode.Clamp,
                AddressV = GpuAddressMode.Clamp,
                AddressW = GpuAddressMode.Clamp,
                MaxAnisotropy = 1,
                CompareOp = GpuCompare.Never,
                DebugName = "SpriteRenderer.LinearClamp",
            };
            _samplerLinear = _gpu.CreateSampler(desc);
            desc.Filter = GpuFilter.Point;
            desc.DebugName = "SpriteRenderer.PointClamp";
            _samplerPoint = _gpu.CreateSampler(desc);
        }

        public void SetPixelShaderOverride(ReadOnlySpan<byte> pixelShaderBytecode)
            => SetShaderProgramOverride(ReadOnlySpan<byte>.Empty, pixelShaderBytecode);

        public void SetShaderProgramOverride(
            ReadOnlySpan<byte> vertexShaderBytecode,
            ReadOnlySpan<byte> pixelShaderBytecode)
        {
            if (pixelShaderBytecode.IsEmpty)
                throw new ArgumentException("The sprite pixel-shader override is empty.", nameof(pixelShaderBytecode));

            byte[] vertex = vertexShaderBytecode.IsEmpty
                ? _vertexShaderBytecode
                : vertexShaderBytecode.ToArray();

            GpuShaderProgramHandle replacement = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vertex,
                PixelShader = pixelShaderBytecode.ToArray(),
                DebugName = "SpriteRenderer.PreviewOverride",
            });
            GpuShaderProgramHandle previous = _overrideProgram;
            _overrideProgram = replacement;
            if (previous.IsValid) _gpu.ReleaseShaderProgram(previous);
        }

        public void ClearPixelShaderOverride()
        {
            if (!_overrideProgram.IsValid) return;
            _gpu.ReleaseShaderProgram(_overrideProgram);
            _overrideProgram = GpuShaderProgramHandle.Invalid;
        }

        public RuntimeShaderHandle RegisterRuntimeShader(ReadOnlySpan<byte> pixelShaderBytecode)
            => RegisterRuntimeShaderProgram(ReadOnlySpan<byte>.Empty, pixelShaderBytecode);

        public RuntimeShaderHandle RegisterRuntimeShaderProgram(
            ReadOnlySpan<byte> vertexShaderBytecode,
            ReadOnlySpan<byte> pixelShaderBytecode)
        {
            if (pixelShaderBytecode.IsEmpty) throw new ArgumentException("Runtime shader bytecode is empty.", nameof(pixelShaderBytecode));
            byte[] vertex = vertexShaderBytecode.IsEmpty
                ? _vertexShaderBytecode
                : vertexShaderBytecode.ToArray();
            GpuShaderProgramHandle program = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vertex,
                PixelShader = pixelShaderBytecode.ToArray(),
                DebugName = "SpriteRenderer.RuntimeShader",
            });
            int id = _nextRuntimeProgramId++;
            _runtimePrograms.Add(id, program);
            return new RuntimeShaderHandle(id);
        }

        public void ReleaseRuntimeShader(RuntimeShaderHandle handle)
        {
            if (!handle.IsValid || !_runtimePrograms.Remove(handle.Id, out GpuShaderProgramHandle program)) return;
            _gpu.ReleaseShaderProgram(program);
        }

        public void SetSamplerFilter(SamplerFilter filter) => _samplerFilter = filter;

        public void SetFog(RoomFogState fog) => _fog = fog;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Submit(SpriteDrawCall call)
        {
            // Lower depths are nearer. Submit farther sprites first (descending depth order).
            // Pack [Inverted Depth: 32 bits | Sequence: 32 bits] into a 64-bit sort key.
            uint depthKey = (uint)(10240 - call.Depth);
            uint seqKey = (uint)(_seq++ & 0xFFFFFFFF);
            ulong sortKey = ((ulong)depthKey << 32) | seqKey;

            _pending.Add(new DrawEntry { Call = call, SortKey = sortKey });
        }

        public void SubmitLine(
            float x1,
            float y1,
            float x2,
            float y2,
            RenderColor color,
            float thickness,
            int depth = -10000)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            float length = MathF.Sqrt((dx * dx) + (dy * dy));
            if (length < 0.001f) return;

            Submit(new SpriteDrawCall
            {
                Texture = TextureHandle.Invalid,
                X = x1,
                Y = y1 - (thickness * 0.5f),
                Width = length,
                Height = thickness,
                OriginX = 0,
                OriginY = thickness * 0.5f,
                Rotation = MathF.Atan2(dy, dx) * (180f / MathF.PI),
                ScaleX = 1,
                ScaleY = 1,
                Alpha = color.A,
                Tint = color,
                Depth = depth,
            });
        }

        public void SubmitRect(
            float x,
            float y,
            float width,
            float height,
            RenderColor color,
            bool filled,
            int depth = -10000)
        {
            if (!filled)
            {
                const float thickness = 1f;
                SubmitLine(x, y, x + width, y, color, thickness, depth);
                SubmitLine(x + width, y, x + width, y + height, color, thickness, depth);
                SubmitLine(x + width, y + height, x, y + height, color, thickness, depth);
                SubmitLine(x, y + height, x, y, color, thickness, depth);
                return;
            }

            Submit(new SpriteDrawCall
            {
                Texture = TextureHandle.Invalid,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                ScaleX = 1,
                ScaleY = 1,
                Alpha = color.A,
                Tint = color,
                Depth = depth,
            });
        }

        public void UploadPendingInstances()
        {
            if (_pending.Count == 0 || _gpuInstancesReady) return;
            PrepareGpuInstances();
        }

        private void PrepareGpuInstances()
        {
            if (_gpuInstancesReady) return;
            if (_pending.Count == 0) return;

            LastDrawCalls = 0;
            LastTriangles = 0;
            LastInstancesDrawn = 0;
            LastTextureSwitches = 0;

            // Lower UI depths are nearer. Submit farther sprites first, preserving submission order
            // at equal depth, so alpha blending is deterministic without a depth test.
            _sorted.Clear();
            _sorted.AddRange(_pending);
            _sorted.Sort();

            int count = Math.Min(_sorted.Count, Math.Min(MaxSprites, RenderCapacityDefaults.SpriteInstanceCap));
            Span<DrawEntry> sorted = CollectionsMarshal.AsSpan(_sorted);
            for (int i = 0; i < count; i++)
            {
                BuildInstance(ref sorted[i].Call, ref _instanceScratch[i]);
            }
            LastInstancesDrawn = count;

            if (!_gpu.TryMapDiscard(_instanceBuffer, out Span<byte> mappedInstances, count * Unsafe.SizeOf<SpriteInstanceGpu>()))
                throw new InvalidOperationException("The sprite instance buffer could not be mapped for this frame.");
            try
            {
                MemoryMarshal.AsBytes(_instanceScratch.AsSpan(0, count)).CopyTo(mappedInstances);
            }
            finally
            {
                _gpu.Unmap(_instanceBuffer);
            }

            _gpuInstancesReady = true;
        }

        public void Flush(
            int viewWidth,
            int viewHeight,
            Func<int, GpuTextureHandle> resolveTexture,
            GpuTextureHandle whiteTexture,
            float cameraX = 0f,
            float cameraY = 0f,
            float zoom = 1f)
        {
            ArgumentNullException.ThrowIfNull(resolveTexture);
            if (_pending.Count == 0) return;

            PrepareGpuInstances();
            int count = Math.Min(_sorted.Count, Math.Min(MaxSprites, RenderCapacityDefaults.SpriteInstanceCap));
            if (count == 0) return;

            float safeZoom = MathF.Max(zoom, 0.001f);
            float halfWidth = viewWidth / (2f * safeZoom);
            float halfHeight = viewHeight / (2f * safeZoom);
            Matrix4x4 transform = Matrix4x4.CreateOrthographicOffCenter(
                cameraX - halfWidth,
                cameraX + halfWidth,
                cameraY + halfHeight,
                cameraY - halfHeight,
                0f,
                1f);

            _gpu.SetRasterState(SpriteRaster);
            _gpu.SetBlendState(SpriteBlend);
            _gpu.SetDepthState(GpuDepthState.Disabled);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.SetVertexLayout(_layout);
            _gpu.SetVertexBuffer(0, _quadVb, Unsafe.SizeOf<UnitVertex>());
            _gpu.SetIndexBuffer(_quadIb, GpuIndexFormat.UInt16);
            _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 1, _instanceBuffer);
            _gpu.SetStructuredBuffer(GpuShaderStage.Pixel, 1, _instanceBuffer);

            float thickness = MathF.Max(0.001f, _fog.Thickness);
            var fogConstants = new SpriteFogCB
            {
                FogColor = _fog.Color,
                FogParams = new Vector4(
                    _fog.Enabled ? 1f : 0f,
                    _fog.Depth,
                    1f / thickness,
                    _fog.Alpha),
            };
            _gpu.UpdateConstantBuffer(_fogConstantBuffer, fogConstants);
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 1, _fogConstantBuffer);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _fogConstantBuffer);

            _gpu.SetSampler(
                GpuShaderStage.Pixel,
                0,
                _samplerFilter == SamplerFilter.Point ? _samplerPoint : _samplerLinear);

            int runStart = 0;
            SpriteDrawCall current = _sorted[0].Call;
            for (int i = 0; i <= count; i++)
            {
                if (i < count && SameMaterial(current, _sorted[i].Call)) continue;

                GpuTextureHandle texture = current.Texture.Id > 0
                    ? resolveTexture(current.Texture.Id)
                    : whiteTexture;
                if (!texture.IsValid) texture = whiteTexture;
                GpuShaderProgramHandle runtimeProgram = current.Shader.IsValid
                    && _runtimePrograms.TryGetValue(current.Shader.Id, out GpuShaderProgramHandle found)
                        ? found : GpuShaderProgramHandle.Invalid;
                _gpu.SetShaderProgram(_overrideProgram.IsValid
                    ? _overrideProgram
                    : runtimeProgram.IsValid ? runtimeProgram : _program);
                if (runtimeProgram.IsValid)
                {
                    _gpu.UpdateConstantBuffer(_shaderParametersBuffer, new ShaderParametersCB
                    {
                        Row0 = current.ShaderParams0, Row1 = current.ShaderParams1,
                        Row2 = current.ShaderParams2, Row3 = current.ShaderParams3,
                    });
                    _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 5, _shaderParametersBuffer);
                }
                _gpu.SetTexture(GpuShaderStage.Pixel, 0, texture);
                _gpu.SetSampler(GpuShaderStage.Pixel, 0,
                    current.SmoothSampling || _samplerFilter != SamplerFilter.Point ? _samplerLinear : _samplerPoint);
                BindAuthoredTextures(current, resolveTexture);

                int runCount = i - runStart;
                var constants = new SpriteCB
                {
                    Transform = transform,
                    InstanceOffset = (uint)runStart,
                };
                _gpu.UpdateConstantBuffer(_constantBuffer, constants);
                _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 0, _constantBuffer);
                _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _constantBuffer);
                Vector4 clip = current.ClipRect;
                int clipX = 0, clipY = 0, clipRight = viewWidth, clipBottom = viewHeight;
                if (clip.Z > 0 && clip.W > 0 && float.IsFinite(clip.X) && float.IsFinite(clip.Y)
                    && float.IsFinite(clip.X + clip.Z) && float.IsFinite(clip.Y + clip.W))
                {
                    clipX = (int)Math.Clamp(Math.Floor(clip.X), 0, viewWidth);
                    clipY = (int)Math.Clamp(Math.Floor(clip.Y), 0, viewHeight);
                    clipRight = (int)Math.Clamp(Math.Ceiling(clip.X + clip.Z), clipX, viewWidth);
                    clipBottom = (int)Math.Clamp(Math.Ceiling(clip.Y + clip.W), clipY, viewHeight);
                }
                _gpu.SetScissor(clipX, clipY, clipRight - clipX, clipBottom - clipY);
                _gpu.DrawIndexedInstanced(6, runCount);
                LastDrawCalls++;
                LastTextureSwitches++;
                LastTriangles += runCount * 2;

                runStart = i;
                if (i < count) current = _sorted[i].Call;
            }

            _gpu.SetScissor(0, 0, viewWidth, viewHeight);
            _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 1, GpuBufferHandle.Invalid);
            FrameDrawCalls += LastDrawCalls;
            FrameTriangles += LastTriangles;
            FrameInstancesDrawn += LastInstancesDrawn;
            FrameTextureSwitches += LastTextureSwitches;
            _pending.Clear();
            _gpuInstancesReady = false;
            _seq = 0;
        }

        private void BindAuthoredTextures(in SpriteDrawCall call, Func<int, GpuTextureHandle> resolveTexture)
        {
            BindAuthoredSlot(call.AuthoredTextures.Count, 0, call.AuthoredTextures.Slot0, call.AuthoredTextures.Handle0, resolveTexture);
            BindAuthoredSlot(call.AuthoredTextures.Count, 1, call.AuthoredTextures.Slot1, call.AuthoredTextures.Handle1, resolveTexture);
            BindAuthoredSlot(call.AuthoredTextures.Count, 2, call.AuthoredTextures.Slot2, call.AuthoredTextures.Handle2, resolveTexture);
            BindAuthoredSlot(call.AuthoredTextures.Count, 3, call.AuthoredTextures.Slot3, call.AuthoredTextures.Handle3, resolveTexture);
        }

        private void BindAuthoredSlot(
            int count,
            int index,
            int slot,
            TextureHandle handle,
            Func<int, GpuTextureHandle> resolveTexture)
        {
            if (index >= count || !handle.IsValid) return;
            GpuTextureHandle gpu = resolveTexture(handle.Id);
            if (gpu.IsValid)
                _gpu.SetTexture(GpuShaderStage.Pixel, slot, gpu);
        }

        private static bool SameMaterial(in SpriteDrawCall a, in SpriteDrawCall b) =>
            a.Texture.Id == b.Texture.Id && a.Shader.Id == b.Shader.Id && a.ClipRect == b.ClipRect
            && a.SmoothSampling == b.SmoothSampling
            && a.ShaderParams0 == b.ShaderParams0 && a.ShaderParams1 == b.ShaderParams1
            && a.ShaderParams2 == b.ShaderParams2 && a.ShaderParams3 == b.ShaderParams3
            && a.AuthoredTextures.SameBindings(b.AuthoredTextures);

        private static float DepthToWorldZ(int depth)
        {
            float t = Math.Clamp(
                (depth - DepthRangeMin) / (DepthRangeMax - DepthRangeMin),
                0f,
                1f);
            // System.Numerics' right-handed orthographic matrix expects visible view-space Z < 0.
            return -t;
        }

        private static void BuildInstance(ref SpriteDrawCall call, ref SpriteInstanceGpu instance)
        {
            float scaleX = call.ScaleX != 0 ? call.ScaleX : 1f;
            float scaleY = call.ScaleY != 0 ? call.ScaleY : 1f;
            float width = call.Width * scaleX;
            float height = call.Height * scaleY;

            float cosRotation = 1f;
            float sinRotation = 0f;
            if (call.Rotation != 0f)
            {
                float radians = call.Rotation * (MathF.PI / 180f);
                cosRotation = MathF.Cos(radians);
                sinRotation = MathF.Sin(radians);
            }

            instance.PosOrigin = new Vector4(call.X, call.Y, call.OriginX, call.OriginY);
            instance.SizeRot = new Vector4(width, height, cosRotation, sinRotation);
            instance.Color = new Vector4(
                call.Tint.R,
                call.Tint.G,
                call.Tint.B,
                call.Tint.A * call.Alpha);
            // x drives the orthographic clip-space Z. y preserves the authored 2D Z/layer
            // depth so fog can use the same conceptual depth axis as 3D without confusing it with
            // the renderer's normalized clip-depth encoding.
            instance.DepthPad = new Vector4(DepthToWorldZ(call.Depth), call.Depth, 0f, 0f);
            Vector4 uv = call.UvRect;
            if (uv.Z <= uv.X || uv.W <= uv.Y) uv = new Vector4(0f, 0f, 1f, 1f);
            instance.UvRect = uv;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClearPixelShaderOverride();
            foreach (GpuShaderProgramHandle program in _runtimePrograms.Values) _gpu.ReleaseShaderProgram(program);
            _runtimePrograms.Clear();
            if (_layout.IsValid) _gpu.ReleaseVertexLayout(_layout);
            if (_program.IsValid) _gpu.ReleaseShaderProgram(_program);
            if (_samplerPoint.IsValid) _gpu.ReleaseSampler(_samplerPoint);
            if (_samplerLinear.IsValid) _gpu.ReleaseSampler(_samplerLinear);
            if (_instanceBuffer.IsValid) _gpu.ReleaseBuffer(_instanceBuffer);
            if (_fogConstantBuffer.IsValid) _gpu.ReleaseBuffer(_fogConstantBuffer);
            if (_shaderParametersBuffer.IsValid) _gpu.ReleaseBuffer(_shaderParametersBuffer);
            if (_constantBuffer.IsValid) _gpu.ReleaseBuffer(_constantBuffer);
            if (_quadIb.IsValid) _gpu.ReleaseBuffer(_quadIb);
            if (_quadVb.IsValid) _gpu.ReleaseBuffer(_quadVb);
        }
    }
}
