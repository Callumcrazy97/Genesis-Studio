using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Eye adaptation (request 64b, off by default). After the main pass, the HDR scene is metered
    // into a 64x32 grid of log luminance, reduced to 8x4 and then to the scene's weighted
    // log-average, and last frame's adapted value moves towards it (AutoExposureShaders). The value
    // stays on the GPU: the composite reads it at t13 and multiplies the exposure by key / adapted.
    // Nothing here runs, and the composite multiplies by exactly 1, while the room leaves it off.
    internal sealed partial class ForwardRenderer
    {
        // Matches AutoExposureShaders.cbuffer AutoExposureConstants (b0).
        [StructLayout(LayoutKind.Sequential)]
        private struct AutoExposureCB
        {
            public Vector4 MeterParams;
            public Vector4 AdaptParams;
            public Vector4 TargetParams;
            public Vector4 SourceSize;
            public Matrix4x4 InvViewProjection;
            public Vector4 CameraPosition;
            public Vector4 SkyZenith;
            public Vector4 SkyHorizon;
        }

        private const int AutoExposureMeterWidth = 64, AutoExposureMeterHeight = 32;
        private const int AutoExposureReduceWidth = 8, AutoExposureReduceHeight = 4;

        private GpuShaderProgramHandle _aeMeterProgram, _aeReduceProgram, _aeAdaptProgram;
        private bool _aeResourcesTried;
        private GpuBufferHandle _cbAutoExposure;
        private GpuRenderTargetHandle _aeMeterTarget, _aeReduceTarget;
        private GpuTextureHandle _aeMeterTexture, _aeReduceTexture;
        private readonly GpuRenderTargetHandle[] _aeStateTargets = new GpuRenderTargetHandle[2];
        private readonly GpuTextureHandle[] _aeStateTextures = new GpuTextureHandle[2];
        private int _aeCurrent;
        private bool _aeStateValid, _aeRanLastFrame;
        private int _aeResetId;
        private float _aeLastTime;
        private bool _autoExposureThisFrame;

        /// <summary>CPU milliseconds the last auto exposure passes took to record (0 when off).</summary>
        public double LastAutoExposureMs { get; private set; }

        /// <summary>True when the last frame's composite was exposed by eye adaptation.</summary>
        public bool AutoExposureActive => _autoExposureThisFrame;

        private bool ShouldRunAutoExposure(bool canPost) =>
            _state.AutoExposureEnabled
            && canPost
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        /// <summary>The programs, constants and targets, made the first time a room turns it on.</summary>
        private bool EnsureAutoExposureResources()
        {
            if (_aeAdaptProgram.IsValid) return true;
            if (_aeResourcesTried) return false;
            _aeResourcesTried = true;
            try
            {
                byte[] vs = CompileShader(AutoExposureShaders.Source, "VS", GpuShaderStage.Vertex);
                _aeMeterProgram = MakeProgram(vs, "PS_Meter", "Auto exposure meter");
                _aeReduceProgram = MakeProgram(vs, "PS_Reduce", "Auto exposure reduce");
                _aeAdaptProgram = MakeProgram(vs, "PS_Adapt", "Auto exposure adapt");
                _cbAutoExposure = MakeCB<AutoExposureCB>("Auto exposure constants");
                _aeMeterTarget = MakeTarget(AutoExposureMeterWidth, AutoExposureMeterHeight, GpuFormat.R16G16B16A16Float, "Auto exposure meter");
                _aeMeterTexture = _gpu.GetRenderTargetTexture(_aeMeterTarget, 0);
                _aeReduceTarget = MakeTarget(AutoExposureReduceWidth, AutoExposureReduceHeight, GpuFormat.R16G16B16A16Float, "Auto exposure reduce");
                _aeReduceTexture = _gpu.GetRenderTargetTexture(_aeReduceTarget, 0);
                // 32-bit: a slow adaptation moves the value by less than half precision can hold.
                for (int i = 0; i < 2; i++)
                {
                    _aeStateTargets[i] = MakeTarget(1, 1, GpuFormat.R32Float, $"Auto exposure state {i}");
                    _aeStateTextures[i] = _gpu.GetRenderTargetTexture(_aeStateTargets[i], 0);
                }
                _aeStateValid = false;
                return true;
            }
            catch (Exception exception)
            {
                RenderLog.Line("Auto exposure is unavailable on this renderer: " + exception.Message);
                ReleaseAutoExposure();
                _aeResourcesTried = true;
                return false;
            }

            GpuShaderProgramHandle MakeProgram(byte[] vertex, string entry, string name) =>
                _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = vertex,
                    PixelShader = CompileShader(AutoExposureShaders.Source, entry, GpuShaderStage.Pixel),
                    DebugName = name,
                });

            GpuRenderTargetHandle MakeTarget(int width, int height, GpuFormat format, string name) =>
                _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                {
                    Width = width,
                    Height = height,
                    ColorFormats = new[] { format },
                    DepthFormat = GpuFormat.Unknown,
                    DepthSampleable = false,
                    DebugName = name,
                });
        }

        private void ReleaseAutoExposure()
        {
            if (_aeMeterProgram.IsValid) _gpu.ReleaseShaderProgram(_aeMeterProgram);
            if (_aeReduceProgram.IsValid) _gpu.ReleaseShaderProgram(_aeReduceProgram);
            if (_aeAdaptProgram.IsValid) _gpu.ReleaseShaderProgram(_aeAdaptProgram);
            if (_cbAutoExposure.IsValid) _gpu.ReleaseBuffer(_cbAutoExposure);
            if (_aeMeterTarget.IsValid) _gpu.ReleaseRenderTarget(_aeMeterTarget);
            if (_aeReduceTarget.IsValid) _gpu.ReleaseRenderTarget(_aeReduceTarget);
            for (int i = 0; i < 2; i++)
            {
                if (_aeStateTargets[i].IsValid) _gpu.ReleaseRenderTarget(_aeStateTargets[i]);
                _aeStateTargets[i] = GpuRenderTargetHandle.Invalid;
                _aeStateTextures[i] = GpuTextureHandle.Invalid;
            }
            _aeMeterProgram = _aeReduceProgram = _aeAdaptProgram = GpuShaderProgramHandle.Invalid;
            _cbAutoExposure = GpuBufferHandle.Invalid;
            _aeMeterTarget = _aeReduceTarget = GpuRenderTargetHandle.Invalid;
            _aeMeterTexture = _aeReduceTexture = GpuTextureHandle.Invalid;
            _aeStateValid = false;
        }

        /// <summary>
        /// Decides whether eye adaptation runs this frame and, if so, meters the scene and moves the
        /// adapted value. Called once a frame after the main pass, before the composite.
        /// </summary>
        private void RunAutoExposure(bool canPost, GpuTextureHandle sceneTexture, GpuTextureHandle depthTexture, int width, int height)
        {
            bool enabled = ShouldRunAutoExposure(canPost);
            _autoExposureThisFrame = enabled && AutoExposurePass(sceneTexture, depthTexture, width, height);
            _aeRanLastFrame = _autoExposureThisFrame;
            if (!_autoExposureThisFrame) LastAutoExposureMs = 0;
        }

        private bool AutoExposurePass(GpuTextureHandle sceneTexture, GpuTextureHandle depthTexture, int width, int height)
        {
            if (!sceneTexture.IsValid || width <= 0 || height <= 0 || !EnsureAutoExposureResources()) return false;
            long started = Stopwatch.GetTimestamp();

            float dt = _time - _aeLastTime;
            dt = float.IsFinite(dt) ? Math.Clamp(dt, 0f, 0.5f) : 0f;
            _aeLastTime = _time;
            bool jump = !_aeStateValid || !_aeRanLastFrame || _state.AutoExposureResetId != _aeResetId;
            _aeResetId = _state.AutoExposureResetId;

            // Engine-sky pixels hold the clear colour until the composite draws the sky over them,
            // so the meter puts the drawn sky's own radiance in their place.
            SkyDome dome = SkyDomeFor(_state);
            bool skyEstimate = depthTexture.IsValid && dome.AuthoredSky && dome.AtmosphereLut;
            float key = AutoExposureDefaults.ClampKey(_state.AutoExposureKey);
            float minEv = MathF.Min(_state.AutoExposureMinEv, _state.AutoExposureMaxEv);
            float maxEv = MathF.Max(_state.AutoExposureMinEv, _state.AutoExposureMaxEv);
            if (!Matrix4x4.Invert(SceneViewProj, out Matrix4x4 inverse)) inverse = Matrix4x4.Identity;
            var cb = new AutoExposureCB
            {
                MeterParams = new Vector4(1f / AutoExposureMeterWidth, 1f / AutoExposureMeterHeight,
                    AutoExposureDefaults.ClampCenterWeight(_state.AutoExposureCenterWeight), skyEstimate ? 1f : 0f),
                AdaptParams = new Vector4(dt,
                    AutoExposureDefaults.ClampSeconds(_state.AutoExposureDarkenSeconds),
                    AutoExposureDefaults.ClampSeconds(_state.AutoExposureBrightenSeconds),
                    jump ? 1f : 0f),
                TargetParams = new Vector4(MathF.Log2(key), minEv, maxEv, SceneDepthFlag),
                SourceSize = new Vector4(width, height, 0f, 0f),
                InvViewProjection = inverse,
                CameraPosition = new Vector4(_cameraPos, 1f),
                SkyZenith = new Vector4(skyEstimate ? SkyLightMath.SkyRadiance(1f, dome) : Vector3.Zero, 0f),
                SkyHorizon = new Vector4(skyEstimate ? SkyLightMath.SkyRadiance(0f, dome) : Vector3.Zero, 0f),
            };

            DrawAutoExposurePass(cb, _aeMeterTarget, AutoExposureMeterWidth, AutoExposureMeterHeight, _aeMeterProgram,
                sceneTexture, skyEstimate ? depthTexture : GpuTextureHandle.Invalid, GpuTextureHandle.Invalid, "Auto exposure meter");
            cb.MeterParams = new Vector4(1f / AutoExposureMeterWidth, 1f / AutoExposureMeterHeight, 0f, 0f);
            DrawAutoExposurePass(cb, _aeReduceTarget, AutoExposureReduceWidth, AutoExposureReduceHeight, _aeReduceProgram,
                _aeMeterTexture, GpuTextureHandle.Invalid, GpuTextureHandle.Invalid, "Auto exposure reduce");
            int next = 1 - _aeCurrent;
            DrawAutoExposurePass(cb, _aeStateTargets[next], 1, 1, _aeAdaptProgram,
                _aeReduceTexture, GpuTextureHandle.Invalid, _aeStateTextures[_aeCurrent], "Auto exposure adapt");
            _aeCurrent = next;
            _aeStateValid = true;

            if (_rtWidth > 0 && _rtHeight > 0) _gpu.SetViewport(0, 0, _rtWidth, _rtHeight);
            LastAutoExposureMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return true;
        }

        private void DrawAutoExposurePass(in AutoExposureCB cb, GpuRenderTargetHandle target, int width, int height,
            GpuShaderProgramHandle program, GpuTextureHandle source, GpuTextureHandle depth, GpuTextureHandle previous, string name)
        {
            _gpu.UpdateConstantBuffer(_cbAutoExposure, cb);
            _gpu.SetViewport(0, 0, width, height);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 1f) },
                HasDepth = false,
                DebugName = name,
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(program);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbAutoExposure);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, source);
            _gpu.SetTexture(GpuShaderStage.Pixel, 1, depth);
            _gpu.SetTexture(GpuShaderStage.Pixel, 2, previous);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
            _gpu.EndRenderPass();
        }

        /// <summary>The composite's eye adaptation constants: x = on, y = key (FogPostCB.AutoExposureParams).</summary>
        private Vector4 AutoExposureCompositeParams() =>
            _autoExposureThisFrame
                ? new Vector4(1f, AutoExposureDefaults.ClampKey(_state.AutoExposureKey), 0f, 0f)
                : Vector4.Zero;

        /// <summary>The adapted value for the composite (t13), or nothing while eye adaptation is off.</summary>
        private GpuTextureHandle AutoExposureCompositeTexture() =>
            _autoExposureThisFrame ? _aeStateTextures[_aeCurrent] : GpuTextureHandle.Invalid;
    }
}
