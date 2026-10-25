using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;
using Scale = Genesis.Shared.Rendering.RenderScale;

namespace Genesis.Rendering.Primitives
{
    // Render scale (Engine.Display.Resolution.*, 1 unless a game sets it): the scene and every post
    // pass run at a share of the view's size into an image of their own (Flush swaps it in for the
    // target), then UpscalePass stretches and sharpens it onto the real target. The GUI is drawn
    // after Flush, so it keeps every pixel.
    internal sealed partial class ForwardRenderer
    {
        // Matches UpscaleShaders.cbuffer UpscaleConstants (b0).
        [StructLayout(LayoutKind.Sequential)]
        private struct UpscaleCB
        {
            public Vector4 Source;
        }

        private GpuShaderProgramHandle _upscaleProgram;
        private bool _upscaleTried;
        private GpuBufferHandle _cbUpscale;
        private GpuRenderTargetHandle _scaledTarget;
        private GpuTextureHandle _scaledTexture;
        private int _scaledWidth, _scaledHeight;
        private long _lastFlushTicks;

        /// <summary>The share of the view's size the last frame's scene was drawn at.</summary>
        public float LastRenderScale { get; private set; } = 1f;

        /// <summary>The size the last frame's scene was drawn at.</summary>
        public int LastSceneWidth { get; private set; }
        public int LastSceneHeight { get; private set; }

        /// <summary>CPU milliseconds the stretch onto the target took to record (0 at full size).</summary>
        public double LastUpscaleMs { get; private set; }

        // The scale for this frame: the game's, or dynamic resolution's from the scene's last GPU time.
        private float RenderScaleThisFrame()
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = _lastFlushTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_lastFlushTicks, now).TotalSeconds;
            _lastFlushTicks = now;
            return Scale.Advance(LastGpuMs, seconds);
        }

        /// <summary>The image a scaled frame is drawn into, or an invalid handle when one cannot be made.</summary>
        private GpuRenderTargetHandle ScaledFrameTarget(int width, int height)
        {
            if (!EnsureUpscale(width, height)) return default;
            return _scaledTarget;
        }

        /// <summary>The scaled frame stretched onto <paramref name="target"/> at its full size, sharpened.</summary>
        private void UpscalePass(GpuRenderTargetHandle target, int width, int height)
        {
            long started = Stopwatch.GetTimestamp();
            _gpu.UpdateConstantBuffer(_cbUpscale, new UpscaleCB
            {
                Source = new Vector4(1f / Math.Max(1, _scaledWidth), 1f / Math.Max(1, _scaledHeight), Scale.Sharpness, 0f),
            });
            _gpu.SetViewport(0, 0, width, height);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = ClearToBlack,
                HasDepth = false,
                DebugName = "Render scale",
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(_upscaleProgram);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbUpscale);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, _scaledTexture);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.EndRenderPass();
            LastUpscaleMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        private bool EnsureUpscale(int width, int height)
        {
            if (!_upscaleProgram.IsValid)
            {
                if (_upscaleTried) return false;
                _upscaleTried = true;
                try
                {
                    _upscaleProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                    {
                        BinaryFormat = _gpu.ShaderBinaryFormat,
                        VertexShader = CompileShader(UpscaleShaders.Source, "VS", GpuShaderStage.Vertex),
                        PixelShader = CompileShader(UpscaleShaders.Source, "PS_Upscale", GpuShaderStage.Pixel),
                        DebugName = "Render scale",
                    });
                    _cbUpscale = MakeCB<UpscaleCB>("Render scale constants");
                }
                catch (Exception exception)
                {
                    RenderLog.Line("Render scale is unavailable on this renderer: " + exception.Message);
                    ReleaseRenderScale();
                    _upscaleTried = true;
                    return false;
                }
            }
            if (_scaledTarget.IsValid && _scaledWidth == width && _scaledHeight == height) return true;
            if (_scaledTarget.IsValid) _gpu.ReleaseRenderTarget(_scaledTarget);
            _scaledTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = width,
                Height = height,
                ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                DepthFormat = GpuFormat.Unknown,
                DepthSampleable = false,
                DebugName = "Render scale frame",
            });
            _scaledTexture = _gpu.GetRenderTargetTexture(_scaledTarget, 0);
            _scaledWidth = width;
            _scaledHeight = height;
            return _scaledTexture.IsValid;
        }

        private void ReleaseRenderScale()
        {
            if (_scaledTarget.IsValid) _gpu.ReleaseRenderTarget(_scaledTarget);
            if (_upscaleProgram.IsValid) _gpu.ReleaseShaderProgram(_upscaleProgram);
            if (_cbUpscale.IsValid) _gpu.ReleaseBuffer(_cbUpscale);
            _scaledTarget = default;
            _scaledTexture = default;
            _upscaleProgram = default;
            _cbUpscale = default;
            _scaledWidth = _scaledHeight = 0;
        }
    }
}
