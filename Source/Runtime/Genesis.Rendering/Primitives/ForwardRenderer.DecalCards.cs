using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// Decals on see-through surfaces (<see cref="Genesis.Shared.Interfaces.DecalDrawCall.SeeThrough"/>):
/// a bullet hole in a window pane. Glass writes no depth, so the projected pass cannot find it;
/// these decals are drawn instead as flat pictures in their own plane, right after the see-through
/// world draws, tested against the scene depth (only opaque things in front hide them) and writing
/// none. The same records, blends and grouping as the projected decals; nothing is compiled or
/// drawn for a frame without one.
/// </summary>
internal sealed partial class ForwardRenderer
{
    private PendingDecal[] _pendingDecalCards = new PendingDecal[16];
    private int _pendingDecalCardCount;
    private long[] _decalCardKeys = new long[16];
    private int[] _decalCardOrder = new int[16];
    private DecalGpu[] _decalCardStaging = new DecalGpu[16];
    private GpuBufferHandle _decalCardBuffer;
    private int _decalCardBufferCapacity;
    private GpuShaderProgramHandle _decalCardProgram;
    private bool _decalCardResourcesFailed;

    /// <summary>The see-through decal program's name, which the Software renderer recognises.</summary>
    internal const string DecalCardProgramName = "Decal cards";

    private void AddDecalCard(in PendingDecal decal)
    {
        if (_pendingDecalCardCount == _pendingDecalCards.Length)
            Array.Resize(ref _pendingDecalCards, Math.Min(_pendingDecalCards.Length * 2, MaxDecalsPerFrame));
        _pendingDecalCards[_pendingDecalCardCount++] = decal;
    }

    private bool EnsureDecalCardResources()
    {
        if (_decalCardProgram.IsValid) return true;
        if (_decalCardResourcesFailed) return false;
        // The constants, the quad and the blend states are the projected pass's.
        if (!EnsureDecalResources()) return false;
        try
        {
            bool software = string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);
            byte[] vs = software ? Array.Empty<byte>() : CompileShader(DecalShaders.Source, "VS_DecalCard", GpuShaderStage.Vertex);
            byte[] ps = software ? Array.Empty<byte>() : CompileShader(DecalShaders.Source, "PS_DecalCard", GpuShaderStage.Pixel);
            _decalCardProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = vs,
                PixelShader = ps,
                DebugName = DecalCardProgramName,
            });
            return _decalCardProgram.IsValid;
        }
        catch (Exception ex)
        {
            _decalCardResourcesFailed = true;
            RenderLog.Line("See-through decals unavailable: " + ex.Message);
            return false;
        }
    }

    private void EnsureDecalCardBuffer(int count)
    {
        if (_decalCardBuffer.IsValid && _decalCardBufferCapacity >= count) return;
        if (_decalCardBuffer.IsValid) _gpu.ReleaseBuffer(_decalCardBuffer);
        int capacity = 64;
        while (capacity < count) capacity *= 2;
        _decalCardBuffer = CreateStructuredBuffer<DecalGpu>(capacity, "See-through decals");
        _decalCardBufferCapacity = capacity;
    }

    /// <summary>
    /// Draws this frame's see-through decals inside the forward pass, after the see-through world
    /// draws: multiply, then alpha in the order given, then add, one instanced draw per run of the
    /// same picture and blend. Puts the forward pass's bindings back afterwards.
    /// </summary>
    private void DrawDecalCardPass()
    {
        int count = _pendingDecalCardCount;
        if (count == 0 || _reflectionPassActive || _probeCaptureActive || !_mainPassHdrMrt) return;
        if (!EnsureDecalCardResources() || !TryGetMesh(_decalQuad.Id, out MeshEntry quad)) return;

        if (_decalCardKeys.Length < count)
        {
            int size = Math.Max(count, _decalCardKeys.Length * 2);
            _decalCardKeys = new long[size];
            _decalCardOrder = new int[size];
            _decalCardStaging = new DecalGpu[size];
        }
        for (int i = 0; i < count; i++)
        {
            ref PendingDecal decal = ref _pendingDecalCards[i];
            long picture = decal.Blend == 1 ? 0L : (uint)decal.TextureId;
            _decalCardKeys[i] = ((long)decal.Blend << 60) | (picture << 24) | (uint)i;
            _decalCardOrder[i] = i;
        }
        Array.Sort(_decalCardKeys, _decalCardOrder, 0, count);
        for (int i = 0; i < count; i++)
            _decalCardStaging[i] = _pendingDecalCards[_decalCardOrder[i]].Gpu;

        EnsureDecalCardBuffer(count);
        int stride = Marshal.SizeOf<DecalGpu>();
        if (!_gpu.TryMapDiscard(_decalCardBuffer, out Span<byte> mapped, count * stride)) return;
        MemoryMarshal.AsBytes(_decalCardStaging.AsSpan(0, count)).CopyTo(mapped);
        _gpu.Unmap(_decalCardBuffer);

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

        _gpu.SetDepthState(_dssNoWrite);
        _gpu.SetRasterState(_rsCullNone);
        _gpu.SetShaderProgram(_decalCardProgram);
        _gpu.SetVertexLayout(_layout);
        _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
        SetMeshBuffers(ref quad);
        _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 0, _cbDecal);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbDecal);
        _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 0, _decalCardBuffer);
        _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);

        int start = 0;
        while (start < count)
        {
            ref PendingDecal first = ref _pendingDecalCards[_decalCardOrder[start]];
            int end = start + 1;
            while (end < count)
            {
                ref PendingDecal next = ref _pendingDecalCards[_decalCardOrder[end]];
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
        LastDecalsDrawn += count;

        // Back to the forward pass's own bindings for what is drawn after.
        _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
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

    private void ReleaseDecalCardResources()
    {
        if (_decalCardBuffer.IsValid) _gpu.ReleaseBuffer(_decalCardBuffer);
        if (_decalCardProgram.IsValid) _gpu.ReleaseShaderProgram(_decalCardProgram);
        _decalCardBuffer = GpuBufferHandle.Invalid;
        _decalCardProgram = GpuShaderProgramHandle.Invalid;
        _decalCardBufferCapacity = 0;
    }
}
