using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// Froxel volumetric fog orchestration (<see cref="FroxelFogShaders"/>). Runs after the shadow maps
/// and before the main pass, so the forward, water and post shaders can all sample the integrated
/// volume. Replaces the per-pixel fog ray march (three hand-synced copies) and the separate
/// half-resolution local-light scatter pass.
/// </summary>
internal sealed partial class ForwardRenderer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FroxelCB
    {
        public Matrix4x4 InvViewProjection;
        public Matrix4x4 PrevViewProjection;
        public Vector4 Grid;      // width, height, slices, far depth
        public Vector4 Camera;    // camera position, near depth
        public Vector4 Forward;   // camera forward, fog volume count
        public Vector4 Jitter;    // cell jitter, history weight
        public Vector4 Lighting;  // local light scatter, shadowed floor, horizon boost, unused
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FroxelApplyCB
    {
        public Vector4 Grid;      // width, height, slices, far depth
        public Vector4 Params;    // active, near depth, horizon boost, unused
    }

    /// <summary>Placed fog volumes the froxel pass accepts per frame (EngineCB still mirrors the first 8).</summary>
    private const int MaxFogVolumes = 64;
    private const float FroxelShadowedFloor = 0.35f;

    private GpuRenderTargetHandle _froxelInjectTarget;
    private GpuRenderTargetHandle _froxelIntegratedTarget;
    private readonly GpuRenderTargetHandle[] _froxelHistoryTargets = new GpuRenderTargetHandle[2];
    private GpuTextureHandle _froxelInjectTexture;
    private GpuTextureHandle _froxelIntegratedTexture;
    private readonly GpuTextureHandle[] _froxelHistoryTextures = new GpuTextureHandle[2];
    private int _froxelWidth, _froxelHeight, _froxelSlices;
    private int _froxelHistoryIndex;
    private bool _froxelHistoryValid;
    private Matrix4x4 _froxelPreviousViewProjection;
    private GpuShaderProgramHandle _froxelInjectProgram;
    private GpuShaderProgramHandle _froxelTemporalProgram;
    private GpuShaderProgramHandle _froxelIntegrateProgram;
    private GpuBufferHandle _cbFroxel;
    private GpuBufferHandle _cbFroxelApply;
    private GpuBufferHandle _fogVolumeBuf;
    private bool _froxelsActiveThisFrame;

    /// <summary>True when the last frame built the froxel volume (fog on, GPU backend).</summary>
    public bool LastFrameFroxelFog { get; private set; }

    private void CreateFroxelResources()
    {
        _cbFroxel = MakeCB<FroxelCB>("Froxel fog constants");
        _cbFroxelApply = MakeCB<FroxelApplyCB>("Froxel fog apply constants");
        _fogVolumeBuf = CreateStructuredBuffer<FogVolumeData>(MaxFogVolumes, "Froxel fog volumes");
        // The CPU rasterizer has no froxel path; it keeps its own analytic fog.
        if (string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase))
            return;
        byte[] vs = CompileShader(FroxelFogShaders.Source, "VS", GpuShaderStage.Vertex);
        GpuShaderProgramHandle Program(string entry, string name) => _gpu.CreateShaderProgram(new GpuShaderProgramDesc
        {
            BinaryFormat = _gpu.ShaderBinaryFormat,
            VertexShader = vs,
            PixelShader = CompileShader(FroxelFogShaders.Source, entry, GpuShaderStage.Pixel),
            DebugName = name,
        });
        _froxelInjectProgram = Program("PS_FroxelInject", "Froxel inject");
        _froxelTemporalProgram = Program("PS_FroxelTemporal", "Froxel temporal");
        _froxelIntegrateProgram = Program("PS_FroxelIntegrate", "Froxel integrate");
    }

    private bool ShouldRunFroxels() =>
        _state.FogEnabled
        && _froxelInjectProgram.IsValid
        && _froxelTemporalProgram.IsValid
        && _froxelIntegrateProgram.IsValid;

    private void EnsureFroxelTargets(int width, int height, int slices)
    {
        if (_froxelInjectTarget.IsValid && width == _froxelWidth && height == _froxelHeight && slices == _froxelSlices)
            return;
        ReleaseFroxelTargets();
        _froxelWidth = width;
        _froxelHeight = height;
        _froxelSlices = slices;
        GpuRenderTargetHandle Target(string name) => _gpu.CreateRenderTarget(new GpuRenderTargetDesc
        {
            Width = width,
            Height = height * slices,
            ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
            DepthFormat = GpuFormat.Unknown,
            DepthSampleable = false,
            DebugName = name,
        });
        _froxelInjectTarget = Target("Froxel inject");
        _froxelIntegratedTarget = Target("Froxel integrated");
        _froxelHistoryTargets[0] = Target("Froxel history A");
        _froxelHistoryTargets[1] = Target("Froxel history B");
        _froxelInjectTexture = _gpu.GetRenderTargetTexture(_froxelInjectTarget, 0);
        _froxelIntegratedTexture = _gpu.GetRenderTargetTexture(_froxelIntegratedTarget, 0);
        _froxelHistoryTextures[0] = _gpu.GetRenderTargetTexture(_froxelHistoryTargets[0], 0);
        _froxelHistoryTextures[1] = _gpu.GetRenderTargetTexture(_froxelHistoryTargets[1], 0);
        _froxelHistoryValid = false;
    }

    private void ReleaseFroxelTargets()
    {
        if (_froxelInjectTarget.IsValid) _gpu.ReleaseRenderTarget(_froxelInjectTarget);
        if (_froxelIntegratedTarget.IsValid) _gpu.ReleaseRenderTarget(_froxelIntegratedTarget);
        for (int i = 0; i < 2; i++)
        {
            if (_froxelHistoryTargets[i].IsValid) _gpu.ReleaseRenderTarget(_froxelHistoryTargets[i]);
            _froxelHistoryTargets[i] = GpuRenderTargetHandle.Invalid;
            _froxelHistoryTextures[i] = GpuTextureHandle.Invalid;
        }
        _froxelInjectTarget = GpuRenderTargetHandle.Invalid;
        _froxelIntegratedTarget = GpuRenderTargetHandle.Invalid;
        _froxelInjectTexture = GpuTextureHandle.Invalid;
        _froxelIntegratedTexture = GpuTextureHandle.Invalid;
        _froxelWidth = _froxelHeight = _froxelSlices = 0;
        _froxelHistoryValid = false;
    }

    private void ReleaseFroxelResources()
    {
        ReleaseFroxelTargets();
        _gpu.ReleaseShaderProgram(_froxelInjectProgram);
        _gpu.ReleaseShaderProgram(_froxelTemporalProgram);
        _gpu.ReleaseShaderProgram(_froxelIntegrateProgram);
        _gpu.ReleaseBuffer(_cbFroxel);
        _gpu.ReleaseBuffer(_cbFroxelApply);
        _gpu.ReleaseBuffer(_fogVolumeBuf);
    }

    /// <summary>Halton (2, 3, 5) jitter in [-0.5, 0.5), indexed by the scene clock rather than the
    /// frame count, so repeated frames of a paused scene converge to the same image.</summary>
    private static Vector3 FroxelJitter(float time)
    {
        int index = ((int)MathF.Floor(MathF.Max(time, 0f) * 60f) & 15) + 1;
        return new Vector3(Halton(index, 2) - 0.5f, Halton(index, 3) - 0.5f, Halton(index, 5) - 0.5f);
    }

    private static float Halton(int index, int radix)
    {
        float result = 0f, fraction = 1f / radix;
        for (int i = index; i > 0; i /= radix, fraction /= radix)
            result += fraction * (i % radix);
        return result;
    }

    private float FroxelHorizonBoost => Math.Clamp(_state.FogAerialBlend, 0f, 1f) * 0.30f * 0.02f;

    /// <summary>Builds this frame's fog volume, or marks it inactive so every shader skips it.</summary>
    private void FroxelFogPass(Matrix4x4 lightVPFar, Matrix4x4 lightVPNear, Matrix4x4 lightVPMid)
    {
        _froxelsActiveThisFrame = false;
        LastFrameFroxelFog = false;
        if (!ShouldRunFroxels())
        {
            _froxelHistoryValid = false;
            UploadFroxelApplyCB();
            return;
        }

        (int width, int height, int slices) = FroxelFogMath.GridForQuality(_state.VolumetricFogQuality);
        EnsureFroxelTargets(width, height, slices);
        if (!_froxelInjectTarget.IsValid || !_froxelIntegratedTarget.IsValid)
        {
            UploadFroxelApplyCB();
            return;
        }

        // Main-camera constants: the injection samples the cascades and the clustered light lists
        // (UploadEngineCB builds them), exactly as the main pass will.
        Matrix4x4 viewProjection = _view * _proj;
        UploadPerFrame(viewProjection, lightVPFar, lightVPNear, lightVPMid);
        UploadEngineCB();

        int volumeCount = Math.Min(_fogVolumeCount, MaxFogVolumes);
        if (volumeCount > 0 && _gpu.TryMapDiscard(_fogVolumeBuf, out Span<byte> mapped, volumeCount * Marshal.SizeOf<FogVolumeData>()))
        {
            MemoryMarshal.AsBytes(_fogVolumes.AsSpan(0, volumeCount)).CopyTo(mapped);
            _gpu.Unmap(_fogVolumeBuf);
        }

        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse);
        Vector4 farCentre = Vector4.Transform(new Vector4(0f, 0f, 1f, 1f), inverse);
        Vector3 forward = new Vector3(farCentre.X, farCentre.Y, farCentre.Z) / farCentre.W - _cameraPos;
        forward = forward.LengthSquared() > 1e-10f ? Vector3.Normalize(forward) : Vector3.UnitZ;

        // VolumetricTemporalBlend: 1 = stable analytic volume (no jitter, no history); lower values
        // jitter the froxel sample points and blend in the reprojected history to hide the grid.
        float temporal = 1f - Math.Clamp(_state.VolumetricTemporalBlend, 0f, 1f);
        Vector3 jitter = temporal > 0f ? FroxelJitter(_time) * temporal : Vector3.Zero;
        float historyWeight = _froxelHistoryValid ? 0.9f * temporal : 0f;
        // Local lights scatter only when local volumetrics are enabled (off by default), at the
        // strength the retired half-resolution pass used.
        float localScatter = _state.LocalVolumetricsEnabled ? LocalVolumetricMath.DefaultStrength : 0f;

        _gpu.UpdateConstantBuffer(_cbFroxel, new FroxelCB
        {
            InvViewProjection = inverse,
            PrevViewProjection = _froxelHistoryValid ? _froxelPreviousViewProjection : viewProjection,
            Grid = new Vector4(width, height, slices, FroxelFogMath.DefaultFarDepth),
            Camera = new Vector4(_cameraPos, FroxelFogMath.NearDepth),
            Forward = new Vector4(forward, volumeCount),
            Jitter = new Vector4(jitter, historyWeight),
            Lighting = new Vector4(localScatter, FroxelShadowedFloor, FroxelHorizonBoost, 0f),
        });

        // The integrated volume and history are sampled by the previous frame's passes; unbind
        // them before they become render targets again.
        _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
        _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
        _gpu.ClearTexture(GpuShaderStage.Pixel, 16);

        RunFroxelPass(_froxelInjectTarget, _froxelInjectProgram, "Froxel inject", width, height * slices,
            GpuTextureHandle.Invalid, GpuTextureHandle.Invalid, inject: true);
        if (temporal > 0f)
        {
            int next = _froxelHistoryIndex ^ 1;
            RunFroxelPass(_froxelHistoryTargets[next], _froxelTemporalProgram, "Froxel temporal", width, height * slices,
                _froxelInjectTexture, _froxelHistoryTextures[_froxelHistoryIndex], inject: false);
            RunFroxelPass(_froxelIntegratedTarget, _froxelIntegrateProgram, "Froxel integrate", width, height * slices,
                _froxelHistoryTextures[next], GpuTextureHandle.Invalid, inject: false);
            _froxelHistoryIndex = next;
            _froxelHistoryValid = true;
        }
        else
        {
            // Stable analytic volume: no history, so integrate this frame's injection directly.
            RunFroxelPass(_froxelIntegratedTarget, _froxelIntegrateProgram, "Froxel integrate", width, height * slices,
                _froxelInjectTexture, GpuTextureHandle.Invalid, inject: false);
            _froxelHistoryValid = false;
        }
        _froxelPreviousViewProjection = viewProjection;
        _froxelsActiveThisFrame = true;
        LastFrameFroxelFog = true;
        UploadFroxelApplyCB();
    }

    private void RunFroxelPass(GpuRenderTargetHandle target, GpuShaderProgramHandle program, string name,
        int width, int height, GpuTextureHandle source, GpuTextureHandle history, bool inject)
    {
        _gpu.BeginRenderPass(new GpuRenderPassDesc
        {
            Target = target,
            ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 1f) },
            HasDepth = false,
            DebugName = name,
        });
        _gpu.SetViewport(0, 0, width, height);
        _gpu.SetRasterState(_rsCullNone);
        _gpu.SetDepthState(_dssFogOff);
        _gpu.SetBlendState(_bsOpaque);
        _gpu.SetShaderProgram(program);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbPerFrame);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbEngine);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _cbFroxel);
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 4, _cbOmni);
        _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
        _gpu.SetSampler(GpuShaderStage.Pixel, 1, _shadowSampler);
        if (inject)
        {
            _gpu.SetTexture(GpuShaderStage.Pixel, 2, _shadowsActiveThisFrame ? _shadowTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 5, _shadowsActiveThisFrame ? _shadowNearTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 14,
                _shadowsActiveThisFrame && _cascade.CascadeCount >= 3 ? _shadowMidTexture : GpuTextureHandle.Invalid);
            if (_localShadowsActiveThisFrame && _localShadowAtlasTexture.IsValid)
                _gpu.SetTexture(GpuShaderStage.Pixel, 15, _localShadowAtlasTexture);
            else
                _gpu.ClearTexture(GpuShaderStage.Pixel, 15);
            _gpu.SetStructuredBuffer(GpuShaderStage.Pixel, 6, _clusterLightBuf);
            _gpu.SetStructuredBuffer(GpuShaderStage.Pixel, 7, _fogVolumeBuf);
            _gpu.SetStructuredBuffer(GpuShaderStage.Pixel, 13, _tileLightIndexBuf);
        }
        else
        {
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, source);
            if (history.IsValid)
                _gpu.SetTexture(GpuShaderStage.Pixel, 1, history);
            else
                _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
        }
        _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
        _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
        _gpu.Draw(3);
        _gpu.EndRenderPass();
        _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
        _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
    }

    private void UploadFroxelApplyCB()
    {
        _gpu.UpdateConstantBuffer(_cbFroxelApply, new FroxelApplyCB
        {
            Grid = new Vector4(
                Math.Max(1, _froxelWidth), Math.Max(1, _froxelHeight), Math.Max(1, _froxelSlices),
                FroxelFogMath.DefaultFarDepth),
            Params = new Vector4(_froxelsActiveThisFrame ? 1f : 0f, FroxelFogMath.NearDepth, FroxelHorizonBoost, 0f),
        });
    }

    /// <summary>Binds the integrated volume (t16), its sampler (s2) and apply constants (b6) for a fogging pass.</summary>
    private void BindFroxelApply()
    {
        _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 6, _cbFroxelApply);
        _gpu.SetSampler(GpuShaderStage.Pixel, 2, _linearSampler);
        if (_froxelsActiveThisFrame && _froxelIntegratedTexture.IsValid)
            _gpu.SetTexture(GpuShaderStage.Pixel, 16, _froxelIntegratedTexture);
        else
            _gpu.ClearTexture(GpuShaderStage.Pixel, 16);
    }
}
