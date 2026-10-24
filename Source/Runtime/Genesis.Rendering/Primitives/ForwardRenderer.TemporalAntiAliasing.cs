using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Temporal anti-aliasing (TemporalAntiAliasingShaders). With it on, every frame's scene is drawn
    // from a camera nudged by under a pixel, a different nudge each frame (8 points of the Halton
    // 2, 3 sequence), and the pass blends the composed frame into the history of earlier ones,
    // which it keeps in two images it draws into in turn. Planar reflections are drawn without the
    // nudge, and the held items are drawn after the pass so they stay sharp.
    internal sealed partial class ForwardRenderer
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct TemporalCB
        {
            public Matrix4x4 InvViewProjection;
            public Matrix4x4 PreviousViewProjection;
            public Vector4 Metrics;
            public Vector4 Params;
        }

        // The new frame's share of each pixel: about the last ten frames are in the image.
        private const float TemporalBlend = 0.1f;

        private bool _taaThisFrame;
        private Matrix4x4 _taaJitter = Matrix4x4.Identity;
        private int _taaFrame;
        private Matrix4x4 _taaPreviousViewProj;
        private bool _taaHistoryValid;
        private bool _taaTried;
        private GpuShaderProgramHandle _taaProgram;
        private GpuShaderProgramHandle _taaCopyProgram;
        private GpuBufferHandle _cbTaa;
        private readonly GpuRenderTargetHandle[] _taaTargets = new GpuRenderTargetHandle[2];
        private readonly GpuTextureHandle[] _taaTextures = new GpuTextureHandle[2];
        private int _taaCurrent;
        private int _taaWidth;
        private int _taaHeight;

        /// <summary>A projection with this frame's nudge, when temporal anti-aliasing runs (never for a reflection).</summary>
        private Matrix4x4 Jittered(Matrix4x4 projection) =>
            _taaThisFrame && !_reflectionPassActive && !_probeCaptureActive ? projection * _taaJitter : projection;

        /// <summary>The scene's camera without the nudge, for working out where a point was last frame.</summary>
        private Matrix4x4 UnjitteredSceneViewProj => _view * (_reversedDepth ? _proj * ReverseDepthClip : _proj);

        /// <summary>Whether this frame is anti-aliased over time, and its nudge: before anything is drawn.</summary>
        private void PrepareTemporalJitter(bool canPost, int width, int height)
        {
            _taaThisFrame = canPost && width > 0 && height > 0 && _state.AntiAliasing == AntiAliasingMode.Taa
                && !IsSoftwareBackend && EnsureTaaPrograms();
            if (!_taaThisFrame)
            {
                _taaJitter = Matrix4x4.Identity;
                _taaHistoryValid = false;
                return;
            }
            int index = (_taaFrame++ & 7) + 1;
            // In clip space: x and y move by the nudge times w, so every depth moves by the same pixels.
            _taaJitter = Matrix4x4.Identity;
            _taaJitter.M41 = (Halton(index, 2) - 0.5f) * 2f / width;
            _taaJitter.M42 = (Halton(index, 3) - 0.5f) * 2f / height;
        }

        private bool EnsureTaaPrograms()
        {
            if (!_taaTried)
            {
                _taaTried = true;
                _taaProgram = MakeTemporalProgram("PS_Taa", "TAA");
                _taaCopyProgram = MakeTemporalProgram("PS_TaaCopy", "TAA copy");
            }
            return _taaProgram.IsValid && _taaCopyProgram.IsValid;
        }

        private GpuShaderProgramHandle MakeTemporalProgram(string entry, string name)
        {
            try
            {
                byte[] vertex = CompileShader(TemporalAntiAliasingShaders.Source, "VS", GpuShaderStage.Vertex);
                byte[] pixel = CompileShader(TemporalAntiAliasingShaders.Source, entry, GpuShaderStage.Pixel);
                if (!_cbTaa.IsValid) _cbTaa = MakeCB<TemporalCB>("Temporal anti-aliasing constants");
                return _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = vertex,
                    PixelShader = pixel,
                    DebugName = "Anti-aliasing " + name,
                });
            }
            catch (Exception error)
            {
                // A game must keep drawing without it: the frame goes out without the nudge.
                RenderLog.Line($"Anti-aliasing {name} could not be made on {_gpu.BackendName}: {error.GetBaseException().Message}");
                return GpuShaderProgramHandle.Invalid;
            }
        }

        private void EnsureTaaTargets(int width, int height)
        {
            if (_taaWidth == width && _taaHeight == height && _taaTargets[0].IsValid) return;
            ReleaseTaaTargets();
            _taaWidth = width;
            _taaHeight = height;
            for (int i = 0; i < 2; i++)
            {
                _taaTargets[i] = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                {
                    Width = width,
                    Height = height,
                    ColorFormats = new[] { GpuFormat.R8G8B8A8UNorm },
                    DepthFormat = GpuFormat.Unknown,
                    DepthSampleable = false,
                    DebugName = "Temporal anti-aliasing history " + (i + 1),
                });
                _taaTextures[i] = _gpu.GetRenderTargetTexture(_taaTargets[i], 0);
            }
            // A new size starts the history again.
            _taaHistoryValid = false;
        }

        private void ReleaseTaaTargets()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_taaTargets[i].IsValid) _gpu.ReleaseRenderTarget(_taaTargets[i]);
                _taaTargets[i] = GpuRenderTargetHandle.Invalid;
                _taaTextures[i] = GpuTextureHandle.Invalid;
            }
            _taaWidth = _taaHeight = 0;
            _taaHistoryValid = false;
        }

        private void ReleaseTaaResources()
        {
            ReleaseTaaTargets();
            foreach (GpuShaderProgramHandle program in new[] { _taaProgram, _taaCopyProgram })
                if (program.IsValid) _gpu.ReleaseShaderProgram(program);
            _taaProgram = _taaCopyProgram = GpuShaderProgramHandle.Invalid;
            if (_cbTaa.IsValid) _gpu.ReleaseBuffer(_cbTaa);
            _cbTaa = GpuBufferHandle.Invalid;
            _taaTried = false;
        }

        /// <summary>Blends the composed frame into the history and writes the result to <paramref name="output"/>.</summary>
        private void RunTemporalAntiAliasing(GpuRenderTargetHandle output, GpuTextureHandle depth, int width, int height)
        {
            EnsureTaaTargets(width, height);
            int previous = _taaCurrent, current = 1 - _taaCurrent;
            Matrix4x4 viewProj = UnjitteredSceneViewProj;
            if (!Matrix4x4.Invert(viewProj, out Matrix4x4 inverse))
            {
                inverse = Matrix4x4.Identity;
                _taaHistoryValid = false;
            }
            var constants = new TemporalCB
            {
                InvViewProjection = inverse,
                PreviousViewProjection = _taaPreviousViewProj,
                Metrics = new Vector4(1f / width, 1f / height, width, height),
                Params = new Vector4(TemporalBlend, _taaHistoryValid && depth.IsValid ? 1f : 0f, 0f, 0f),
            };
            DrawTemporalPass(_taaTargets[current], _taaProgram, constants, "TAA", _aaSourceTexture, _taaTextures[previous], depth);
            DrawTemporalPass(output, _taaCopyProgram, constants, "TAA copy", _taaTextures[current], GpuTextureHandle.Invalid, GpuTextureHandle.Invalid);
            _taaCurrent = current;
            _taaPreviousViewProj = viewProj;
            _taaHistoryValid = true;
        }

        private void DrawTemporalPass(GpuRenderTargetHandle target, GpuShaderProgramHandle program, TemporalCB constants, string debugName,
            GpuTextureHandle color, GpuTextureHandle history, GpuTextureHandle depth)
        {
            _gpu.SetViewport(0, 0, constants.Metrics.Z, constants.Metrics.W);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = KeepColor,
                HasDepth = false,
                DebugName = debugName,
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(program);
            _gpu.UpdateConstantBuffer(_cbTaa, constants);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbTaa);
            if (color.IsValid) _gpu.SetTexture(GpuShaderStage.Pixel, 0, color);
            if (history.IsValid) _gpu.SetTexture(GpuShaderStage.Pixel, 1, history);
            if (depth.IsValid) _gpu.SetTexture(GpuShaderStage.Pixel, 2, depth);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
            _gpu.EndRenderPass();
        }
    }
}
