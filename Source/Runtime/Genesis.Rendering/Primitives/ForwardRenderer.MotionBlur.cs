using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Camera motion blur (off by default). With it on, the composite draws into an image of its own;
    // the blur reads it with the scene depth and last frame's view and writes the image the held
    // items, anti-aliasing and post effects carry on from. Held items and the GUI stay sharp.
    internal sealed partial class ForwardRenderer
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MotionBlurCB
        {
            public Matrix4x4 InvViewProjection;
            public Matrix4x4 PrevViewProjection;
            public Vector4 Metrics;
            public Vector4 Params;
            public Vector4 CameraPosition;
        }

        // Samples along each pixel's path, and the longest blur as a share of the image's width.
        private const int MotionBlurSamples = 12;
        private const float MotionBlurLongest = 0.04f;
        // A camera that jumps further than this, or turns more than this, in one frame has cut to
        // another view: that frame is not blurred.
        private const float MotionBlurCutDistance = 4f;
        private const float MotionBlurCutCosine = 0.7f;

        private GpuShaderProgramHandle _motionBlurProgram;
        private bool _motionBlurTried;
        private GpuBufferHandle _cbMotionBlur;
        private GpuRenderTargetHandle _motionSourceTarget;
        private GpuTextureHandle _motionSourceTexture;
        private int _motionWidth, _motionHeight;
        private Matrix4x4 _motionPrevViewProj;
        private Vector3 _motionPrevCamera;
        private Vector3 _motionPrevForward;
        private bool _motionHistory;

        /// <summary>CPU time spent recording the last motion blur pass (0 when none ran).</summary>
        public double LastMotionBlurMs { get; private set; }

        /// <summary>Whether this frame is blurred: asked for, a GPU backend, last frame's view known and no cut.</summary>
        private bool MotionBlurThisFrame(bool canPost)
        {
            float shutter = _state.MotionBlur;
            bool run = canPost && shutter > 0.001f && !IsSoftwareBackend && _motionHistory
                && Vector3.Distance(_motionPrevCamera, _cameraPos) <= MotionBlurCutDistance
                && Vector3.Dot(_motionPrevForward, _viewForward) >= MotionBlurCutCosine
                && EnsureMotionBlurProgram();
            if (!run)
            {
                LastMotionBlurMs = 0;
                if (shutter <= 0.001f) ReleaseMotionBlurTarget();
            }
            return run;
        }

        /// <summary>Remembers this frame's view for the next frame's blur. Called once the main view is drawn.</summary>
        private void RememberMotionBlurView()
        {
            _motionPrevViewProj = SceneViewProj;
            _motionPrevCamera = _cameraPos;
            _motionPrevForward = _viewForward;
            _motionHistory = _viewForward.LengthSquared() > 0.5f;
        }

        private bool EnsureMotionBlurProgram()
        {
            if (_motionBlurTried) return _motionBlurProgram.IsValid;
            _motionBlurTried = true;
            try
            {
                _motionBlurProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = CompileShader(MotionBlurShaders.Source, "VS", GpuShaderStage.Vertex),
                    PixelShader = CompileShader(MotionBlurShaders.Source, "PS_MotionBlur", GpuShaderStage.Pixel),
                    DebugName = "Camera motion blur",
                });
                _cbMotionBlur = MakeCB<MotionBlurCB>("Motion blur constants");
            }
            catch (Exception error)
            {
                RenderLog.Line($"Motion blur could not be made on {_gpu.BackendName}: {error.GetBaseException().Message}");
                _motionBlurProgram = GpuShaderProgramHandle.Invalid;
            }
            return _motionBlurProgram.IsValid;
        }

        /// <summary>The image the composite draws into when the blur follows.</summary>
        private GpuRenderTargetHandle MotionBlurInput(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (_motionSourceTarget.IsValid && (_motionWidth != width || _motionHeight != height))
                ReleaseMotionBlurTarget();
            if (!_motionSourceTarget.IsValid)
            {
                _motionSourceTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                {
                    Width = width,
                    Height = height,
                    ColorFormats = new[] { GpuFormat.R8G8B8A8UNorm },
                    DepthFormat = GpuFormat.Unknown,
                    DepthSampleable = false,
                    DebugName = "Motion blur source image",
                });
                _motionSourceTexture = _gpu.GetRenderTargetTexture(_motionSourceTarget, 0);
                _motionWidth = width;
                _motionHeight = height;
            }
            return _motionSourceTarget;
        }

        private void ReleaseMotionBlurTarget()
        {
            if (_motionSourceTarget.IsValid) _gpu.ReleaseRenderTarget(_motionSourceTarget);
            _motionSourceTarget = GpuRenderTargetHandle.Invalid;
            _motionSourceTexture = GpuTextureHandle.Invalid;
            _motionWidth = _motionHeight = 0;
        }

        private void ReleaseMotionBlurResources()
        {
            ReleaseMotionBlurTarget();
            if (_motionBlurProgram.IsValid) _gpu.ReleaseShaderProgram(_motionBlurProgram);
            _motionBlurProgram = GpuShaderProgramHandle.Invalid;
            if (_cbMotionBlur.IsValid) _gpu.ReleaseBuffer(_cbMotionBlur);
            _cbMotionBlur = GpuBufferHandle.Invalid;
        }

        /// <summary>Blurs the composed image along the camera's motion into <paramref name="output"/>.</summary>
        private void RunMotionBlur(GpuRenderTargetHandle output, GpuTextureHandle depthTexture, int width, int height)
        {
            long started = Stopwatch.GetTimestamp();
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (!Matrix4x4.Invert(SceneViewProj, out Matrix4x4 inverse)) inverse = Matrix4x4.Identity;
            _gpu.SetViewport(0, 0, width, height);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = output,
                ColorActions = new[] { GpuAttachmentAction.Keep() },
                HasDepth = false,
                DebugName = "Camera motion blur",
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(_motionBlurProgram);
            _gpu.UpdateConstantBuffer(_cbMotionBlur, new MotionBlurCB
            {
                InvViewProjection = inverse,
                PrevViewProjection = _motionPrevViewProj,
                Metrics = new Vector4(1f / width, 1f / height, width, height),
                Params = new Vector4(Math.Clamp(_state.MotionBlur, 0f, 1f), MathF.Max(2f, MotionBlurLongest * width),
                    SceneDepthFlag, MotionBlurSamples),
                CameraPosition = new Vector4(_cameraPos, 1f),
            });
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbMotionBlur);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, _motionSourceTexture);
            _gpu.SetTexture(GpuShaderStage.Pixel, 1, depthTexture);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
            _gpu.EndRenderPass();
            LastMotionBlurMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }
}
