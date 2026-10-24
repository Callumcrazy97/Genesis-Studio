using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;
using Focus = Genesis.Shared.Rendering.DepthOfField;

namespace Genesis.Rendering.Primitives
{
    // Depth of field (Engine.Camera.Focus.*, off unless a game turns it on): after the main pass the
    // HDR scene is blurred by each pixel's distance from the focus (DepthOfFieldShaders) into an
    // image of the same size, which bloom, eye adaptation and the composite then read instead.
    internal sealed partial class ForwardRenderer
    {
        // Matches DepthOfFieldShaders.cbuffer DepthOfFieldConstants (b0).
        [StructLayout(LayoutKind.Sequential)]
        private struct DepthOfFieldCB
        {
            public Vector4 Focus;
            public Vector4 Planes;
            public Vector4 Flags;
        }

        private GpuShaderProgramHandle _dofProgram;
        private bool _dofResourcesTried;
        private GpuBufferHandle _cbDepthOfField;
        private GpuRenderTargetHandle _dofTarget;
        private GpuTextureHandle _dofTexture;
        private int _dofWidth, _dofHeight;

        /// <summary>CPU milliseconds the depth-of-field pass took to record (0 when off).</summary>
        public double LastDepthOfFieldMs { get; private set; }

        private bool DepthOfFieldThisFrame(bool canPost) =>
            Focus.Enabled && Focus.Blur > 0f && canPost
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        /// <summary>The scene blurred by depth of field, or <paramref name="scene"/> when it cannot be made.</summary>
        private GpuTextureHandle DepthOfFieldPass(GpuTextureHandle scene, GpuTextureHandle depth, int width, int height)
        {
            LastDepthOfFieldMs = 0;
            if (!scene.IsValid || !depth.IsValid || !EnsureDepthOfField(width, height)) return scene;
            long started = Stopwatch.GetTimestamp();
            float far = _state.CameraFarPlane > 0f ? _state.CameraFarPlane : 1000f;
            var constants = new DepthOfFieldCB
            {
                // The blur is given for a 1080-line picture; a smaller or larger one blurs as much of it.
                Focus = new Vector4(Focus.Distance, Focus.Range, Focus.Blur * height / 1080f, Focus.Auto ? 1f : 0f),
                Planes = new Vector4(_nearPlane, far, 1f / Math.Max(1, width), 1f / Math.Max(1, height)),
                Flags = new Vector4(SceneDepthFlag, 0f, 0f, 0f),
            };
            _gpu.UpdateConstantBuffer(_cbDepthOfField, constants);
            _gpu.SetViewport(0, 0, width, height);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = _dofTarget,
                ColorActions = ClearToBlack,
                HasDepth = false,
                DebugName = "Depth of field",
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(_dofProgram);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbDepthOfField);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, scene);
            _gpu.SetTexture(GpuShaderStage.Pixel, 1, depth);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
            _gpu.EndRenderPass();
            LastDepthOfFieldMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return _dofTexture;
        }

        private bool EnsureDepthOfField(int width, int height)
        {
            if (!_dofProgram.IsValid)
            {
                if (_dofResourcesTried) return false;
                _dofResourcesTried = true;
                try
                {
                    _dofProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                    {
                        BinaryFormat = _gpu.ShaderBinaryFormat,
                        VertexShader = CompileShader(DepthOfFieldShaders.Source, "VS", GpuShaderStage.Vertex),
                        PixelShader = CompileShader(DepthOfFieldShaders.Source, "PS_DepthOfField", GpuShaderStage.Pixel),
                        DebugName = "Depth of field",
                    });
                    _cbDepthOfField = MakeCB<DepthOfFieldCB>("Depth of field constants");
                }
                catch (Exception exception)
                {
                    RenderLog.Line("Depth of field is unavailable on this renderer: " + exception.Message);
                    ReleaseDepthOfField();
                    _dofResourcesTried = true;
                    return false;
                }
            }
            if (_dofTarget.IsValid && _dofWidth == width && _dofHeight == height) return true;
            if (_dofTarget.IsValid) _gpu.ReleaseRenderTarget(_dofTarget);
            _dofTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = width,
                Height = height,
                ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                DepthFormat = GpuFormat.Unknown,
                DepthSampleable = false,
                DebugName = "Depth of field",
            });
            _dofTexture = _gpu.GetRenderTargetTexture(_dofTarget, 0);
            _dofWidth = width;
            _dofHeight = height;
            return _dofTexture.IsValid;
        }

        private void ReleaseDepthOfField()
        {
            if (_dofTarget.IsValid) _gpu.ReleaseRenderTarget(_dofTarget);
            if (_dofProgram.IsValid) _gpu.ReleaseShaderProgram(_dofProgram);
            if (_cbDepthOfField.IsValid) _gpu.ReleaseBuffer(_cbDepthOfField);
            _dofTarget = default;
            _dofTexture = default;
            _dofProgram = default;
            _cbDepthOfField = default;
            _dofWidth = _dofHeight = 0;
        }
    }
}
