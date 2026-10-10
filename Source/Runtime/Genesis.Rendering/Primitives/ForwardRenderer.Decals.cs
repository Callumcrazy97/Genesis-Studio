using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// Projected decals (bullet holes, scorch marks, blood): drawn over the opaque world once it is lit,
/// before water and see-through draws, by reading the scene depth (see <see cref="DecalShaders"/>).
/// Decals sharing a picture and a blend are one instanced draw, so hundreds of bullet holes are one
/// draw call; see-through (alpha) decals keep the order they were given in, newer over older.
/// Nothing is compiled or drawn for a frame without decals.
/// </summary>
internal sealed partial class ForwardRenderer
{
    /// <summary>Decals one frame may draw; more are left out.</summary>
    public const int MaxDecalsPerFrame = 4096;

    // Full strength while the surface is within 60 degrees of the decal's own facing; gone at 80.
    private const float DecalFullAngleCos = 0.5f;
    private const float DecalZeroAngleCos = 0.17f;

    [StructLayout(LayoutKind.Sequential)]
    private struct DecalGpu
    {
        public Vector4 Center;
        public Vector4 AxisX;
        public Vector4 AxisY;
        public Vector4 AxisZ;
        public Vector4 Color;
        public Vector4 Params;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DecalCB
    {
        public Matrix4x4 ViewProjection;
        public Matrix4x4 InvViewProjection;
        public Vector4 CameraPos;
        public Vector4 Viewport;
        public Vector4 Params;
    }

    private struct PendingDecal
    {
        public DecalGpu Gpu;
        public GpuTextureHandle Texture;
        public int TextureId;
        public byte Blend;
    }

    private PendingDecal[] _pendingDecals = new PendingDecal[64];
    private int _pendingDecalCount;
    private long[] _decalKeys = new long[64];
    private int[] _decalOrder = new int[64];
    private DecalGpu[] _decalStaging = new DecalGpu[64];
    private GpuBufferHandle _decalBuffer;
    private int _decalBufferCapacity;
    private GpuBufferHandle _cbDecal;
    private GpuShaderProgramHandle _decalProgram;
    private bool _decalResourcesFailed;
    private MeshHandle _decalQuad;
    private GpuBlendState _bsDecalMultiply, _bsDecalAlpha, _bsDecalAdd;

    /// <summary>Decals drawn by the last frame.</summary>
    public int LastDecalsDrawn { get; private set; }

    /// <summary>Draw calls the last frame's decals took (one per run of decals sharing a picture and blend).</summary>
    public int LastDecalDrawCalls { get; private set; }

    /// <summary>Decals given for the frame being made.</summary>
    public int DecalsSubmitted => _pendingDecalCount;

    /// <summary>Adds a decal for this frame (see IRenderController.DrawDecals).</summary>
    public void AddDecal(GpuTextureHandle texture, int textureId, in DecalDrawCall call)
    {
        if (_pendingDecalCount >= MaxDecalsPerFrame) return;
        if (!Finite(call.Position) || !Finite(call.Normal) || !Finite(call.Tangent)) return;
        if (!(call.Width > 0f) || !(call.Height > 0f) || !(call.Depth > 0f)
            || !float.IsFinite(call.Width) || !float.IsFinite(call.Height) || !float.IsFinite(call.Depth)) return;
        float opacity = float.IsFinite(call.Color.W) ? Math.Clamp(call.Color.W, 0f, 1f) : 0f;
        if (opacity <= 0f) return;
        if (call.Normal.LengthSquared() < 1e-10f) return;
        Vector3 normal = Vector3.Normalize(call.Normal);
        // The tangent made perpendicular to the normal; any one will do when it was not given.
        Vector3 tangent = call.Tangent - normal * Vector3.Dot(call.Tangent, normal);
        if (tangent.LengthSquared() < 1e-10f)
            tangent = Vector3.Cross(MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitZ, normal);
        tangent = Vector3.Normalize(tangent);
        Vector3 bitangent = Vector3.Cross(normal, tangent);
        Vector3 tint = new(
            float.IsFinite(call.Color.X) ? Math.Clamp(call.Color.X, 0f, 16f) : 1f,
            float.IsFinite(call.Color.Y) ? Math.Clamp(call.Color.Y, 0f, 16f) : 1f,
            float.IsFinite(call.Color.Z) ? Math.Clamp(call.Color.Z, 0f, 16f) : 1f);

        if (_pendingDecalCount == _pendingDecals.Length)
            Array.Resize(ref _pendingDecals, Math.Min(_pendingDecals.Length * 2, MaxDecalsPerFrame));
        _pendingDecals[_pendingDecalCount++] = new PendingDecal
        {
            Gpu = new DecalGpu
            {
                Center = new Vector4(call.Position, 0f),
                AxisX = new Vector4(tangent * call.Width, 0f),
                AxisY = new Vector4(bitangent * call.Height, 0f),
                AxisZ = new Vector4(normal * call.Depth, 0f),
                Color = new Vector4(ToLinearColor(tint), opacity),
                Params = new Vector4(DecalFullAngleCos, DecalZeroAngleCos, 0f, 0f),
            },
            Texture = texture,
            TextureId = textureId,
            Blend = (byte)Math.Clamp((int)call.Blend, 0, 2),
        };
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private void ClearDecals() => _pendingDecalCount = 0;

    private bool EnsureDecalResources()
    {
        if (_decalProgram.IsValid) return true;
        if (_decalResourcesFailed) return false;
        try
        {
            // The CPU renderer runs no shaders: it recognises the program by name and draws the
            // pass itself (SoftwareDecals).
            bool software = string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);
            byte[] vs = software ? Array.Empty<byte>() : CompileShader(DecalShaders.Source, "VS_Decal", GpuShaderStage.Vertex);
            byte[] ps = software ? Array.Empty<byte>() : CompileShader(DecalShaders.Source, "PS_Decal", GpuShaderStage.Pixel);
            _cbDecal = MakeCB<DecalCB>("Decal constants");
            // A unit quad whose corners say which corner of the decal's screen rectangle they are.
            MeshVertex[] corners =
            {
                new() { Position = new Vector3(0f, 0f, 0f), Normal = Vector3.UnitZ, Color = Vector4.One },
                new() { Position = new Vector3(0f, 1f, 0f), Normal = Vector3.UnitZ, Color = Vector4.One },
                new() { Position = new Vector3(1f, 1f, 0f), Normal = Vector3.UnitZ, Color = Vector4.One },
                new() { Position = new Vector3(1f, 0f, 0f), Normal = Vector3.UnitZ, Color = Vector4.One },
            };
            _decalQuad = RegisterMesh(corners, new ushort[] { 0, 1, 2, 0, 2, 3 });
            _bsDecalMultiply = DecalBlendState(0);
            _bsDecalAlpha = DecalBlendState(1);
            _bsDecalAdd = DecalBlendState(2);
            _decalProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vs,
                PixelShader = ps,
                DebugName = DecalProgramName,
            });
            return _decalProgram.IsValid;
        }
        catch (Exception ex)
        {
            _decalResourcesFailed = true;
            RenderLog.Line("Decals unavailable: " + ex.Message);
            return false;
        }
    }

    /// <summary>The decal program's name, which the Software renderer recognises.</summary>
    internal const string DecalProgramName = "Decals";

    /// <summary>
    /// Colour attachment 0 is scaled (multiply), painted over (alpha) or added to (add). A multiply
    /// decal also scales attachment 1's ambient term (gba) by the same factor and keeps its fog flag
    /// (r); the formula is the same on both attachments, so a backend that blends every attachment
    /// alike (OpenGL here) gets it right too. Alpha and add decals leave attachment 1 alone.
    /// </summary>
    private static GpuBlendState DecalBlendState(byte mode)
    {
        bool multiply = mode == 0;
        var state = new GpuBlendState
        {
            Enabled = true,
            ColorOp = GpuBlendOp.Add,
            SrcAlpha = multiply ? GpuBlendFactor.DestAlpha : GpuBlendFactor.Zero,
            DstAlpha = multiply ? GpuBlendFactor.Zero : GpuBlendFactor.One,
            AlphaOp = GpuBlendOp.Add,
            WriteR = true, WriteG = true, WriteB = true, WriteA = false,
            IndependentBlend = true,
            SecondaryEnabled = multiply,
            SecondarySrcColor = GpuBlendFactor.DestColor,
            SecondaryDstColor = GpuBlendFactor.Zero,
            SecondaryColorOp = GpuBlendOp.Add,
            SecondarySrcAlpha = GpuBlendFactor.DestAlpha,
            SecondaryDstAlpha = GpuBlendFactor.Zero,
            SecondaryAlphaOp = GpuBlendOp.Add,
            SecondaryWriteR = false,
            SecondaryWriteG = multiply, SecondaryWriteB = multiply, SecondaryWriteA = multiply,
        };
        switch (mode)
        {
            case 0:
                state.SrcColor = GpuBlendFactor.DestColor;
                state.DstColor = GpuBlendFactor.Zero;
                break;
            case 1:
                state.SrcColor = GpuBlendFactor.SrcAlpha;
                state.DstColor = GpuBlendFactor.InvSrcAlpha;
                break;
            default:
                state.SrcColor = GpuBlendFactor.One;
                state.DstColor = GpuBlendFactor.One;
                break;
        }
        return state;
    }

    private void EnsureDecalBuffer(int count)
    {
        if (_decalBuffer.IsValid && _decalBufferCapacity >= count) return;
        if (_decalBuffer.IsValid) _gpu.ReleaseBuffer(_decalBuffer);
        int capacity = 256;
        while (capacity < count) capacity *= 2;
        _decalBuffer = CreateStructuredBuffer<DecalGpu>(capacity, "Decals");
        _decalBufferCapacity = capacity;
    }

    /// <summary>
    /// Draws this frame's decals into the scene colour after the opaque world: the main pass is
    /// ended (the depth is read as a texture, as water does) and started again afterwards with the
    /// forward pass's own bindings.
    /// </summary>
    private void DrawDecalPass()
    {
        LastDecalsDrawn = 0;
        LastDecalDrawCalls = 0;
        int count = _pendingDecalCount;
        if (count == 0 || _reflectionPassActive || !_mainPassHdrMrt || !_currentDepthTexture.IsValid) return;
        if (!EnsureDecalResources() || !TryGetMesh(_decalQuad.Id, out MeshEntry quad)) return;

        // Order: multiply, then alpha, then add. Multiply and add decals do not depend on order, so
        // they are grouped by picture (one draw each); alpha decals keep the order they were given in.
        if (_decalKeys.Length < count)
        {
            int size = Math.Max(count, _decalKeys.Length * 2);
            _decalKeys = new long[size];
            _decalOrder = new int[size];
            _decalStaging = new DecalGpu[size];
        }
        for (int i = 0; i < count; i++)
        {
            ref PendingDecal decal = ref _pendingDecals[i];
            long picture = decal.Blend == 1 ? 0L : (uint)decal.TextureId;
            _decalKeys[i] = ((long)decal.Blend << 60) | (picture << 24) | (uint)i;
            _decalOrder[i] = i;
        }
        Array.Sort(_decalKeys, _decalOrder, 0, count);
        for (int i = 0; i < count; i++)
            _decalStaging[i] = _pendingDecals[_decalOrder[i]].Gpu;

        EnsureDecalBuffer(count);
        int stride = Marshal.SizeOf<DecalGpu>();
        if (!_gpu.TryMapDiscard(_decalBuffer, out Span<byte> mapped, count * stride)) return;
        MemoryMarshal.AsBytes(_decalStaging.AsSpan(0, count)).CopyTo(mapped);
        _gpu.Unmap(_decalBuffer);

        Matrix4x4 viewProj = SceneViewProj;
        if (!Matrix4x4.Invert(viewProj, out Matrix4x4 invViewProj)) return;
        int width = _rtWidth > 0 ? _rtWidth : 1, height = _rtHeight > 0 ? _rtHeight : 1;
        var constants = new DecalCB
        {
            ViewProjection = viewProj,
            InvViewProjection = invViewProj,
            CameraPos = new Vector4(_cameraPos, SceneDepthFlag),
            Viewport = new Vector4(width, height, 1f / width, 1f / height),
        };

        _gpu.EndRenderPass();
        BeginForwardColorPass(hasDepth: false, "Decals");
        _gpu.SetDepthState(_dssNoTest);
        _gpu.SetRasterState(_rsCullNone);
        _gpu.SetShaderProgram(_decalProgram);
        _gpu.SetVertexLayout(_layout);
        _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
        SetMeshBuffers(ref quad);
        _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 0, _cbDecal);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbDecal);
        _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 0, _decalBuffer);
        _gpu.SetTexture(GpuShaderStage.Pixel, 1, _currentDepthTexture);
        _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);

        int start = 0;
        while (start < count)
        {
            ref PendingDecal first = ref _pendingDecals[_decalOrder[start]];
            int end = start + 1;
            while (end < count)
            {
                ref PendingDecal next = ref _pendingDecals[_decalOrder[end]];
                if (next.Blend != first.Blend || next.TextureId != first.TextureId) break;
                end++;
            }
            constants.Params = new Vector4(start, first.Blend, LinearPipeline ? 1f : 0f, end - start);
            _gpu.UpdateConstantBuffer(_cbDecal, constants);
            _gpu.SetBlendState(first.Blend switch { 0 => _bsDecalMultiply, 1 => _bsDecalAlpha, _ => _bsDecalAdd });
            _gpu.SetTexture(GpuShaderStage.Pixel, 2, first.Texture);
            _gpu.DrawIndexedInstanced(quad.IndexCount, end - start);
            LastDecalDrawCalls++;
            LastDrawCalls++;
            start = end;
        }
        LastDecalsDrawn = count;

        _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
        _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
        _gpu.EndRenderPass();
        BeginForwardColorPass(hasDepth: true, "Forward after decals");

        // Back to the forward pass's own bindings for water and the see-through draws.
        BindCommonShaderState(shadowPass: false);
        _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbPerFrame);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbEngine);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _cbDraw);
        _gpu.SetTexture(GpuShaderStage.Pixel, 2, _shadowsActiveThisFrame ? _shadowTexture : GpuTextureHandle.Invalid);
        _gpu.SetTexture(GpuShaderStage.Pixel, 3, _flatNormalTexture);
        _gpu.SetSampler(GpuShaderStage.Pixel, 0, _albedoSampler);
        _gpu.SetBlendState(_bsOpaque);
        _gpu.SetDepthState(_dssDefault);
        _gpu.SetRasterState(CurrentRasterizer());
    }

    private void ReleaseDecalResources()
    {
        if (_decalBuffer.IsValid) _gpu.ReleaseBuffer(_decalBuffer);
        if (_cbDecal.IsValid) _gpu.ReleaseBuffer(_cbDecal);
        if (_decalProgram.IsValid) _gpu.ReleaseShaderProgram(_decalProgram);
        _decalBuffer = GpuBufferHandle.Invalid;
        _cbDecal = GpuBufferHandle.Invalid;
        _decalProgram = GpuShaderProgramHandle.Invalid;
        _decalBufferCapacity = 0;
    }
}
