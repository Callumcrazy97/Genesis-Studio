using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Post-process anti-aliasing (FXAA, SMAA 1x). With it on, the tonemap/fog composite and the held
    // items draw into an image of their own; the anti-aliasing pass reads it and writes the next
    // stage's input: the project's post effects when there are any, otherwise the real target. The
    // GUI is drawn after the forward renderer has finished, so it is never softened.
    internal sealed partial class ForwardRenderer
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct AntiAliasCB
        {
            public Vector4 Metrics;
            public Vector4 Params;
        }

        // FXAA: sub-pixel blend 0.75; a step must reach 1/8 of the brightest neighbour and 1/16 of
        // full brightness (FXAA's "high quality" thresholds).
        private static readonly Vector4 FxaaParams = new(0.75f, 0.125f, 0.0625f, 0f);

        // SMAA: edge threshold 0.1, local contrast factor 2, each edge walked up to 32 pixels both
        // ways (SMAA's high preset).
        private static readonly Vector4 SmaaParams = new(0.1f, 2f, 32f, 0f);

        private byte[] _antiAliasVertexShader;
        private GpuShaderProgramHandle _fxaaProgram;
        private GpuShaderProgramHandle _smaaEdgesProgram;
        private GpuShaderProgramHandle _smaaWeightsProgram;
        private GpuShaderProgramHandle _smaaBlendProgram;
        private bool _fxaaTried;
        private bool _smaaTried;
        private GpuBufferHandle _cbAntiAlias;
        private GpuRenderTargetHandle _aaSourceTarget;
        private GpuRenderTargetHandle _smaaEdgesTarget;
        private GpuRenderTargetHandle _smaaWeightsTarget;
        private GpuTextureHandle _aaSourceTexture;
        private GpuTextureHandle _smaaEdgesTexture;
        private GpuTextureHandle _smaaWeightsTexture;
        private int _aaWidth;
        private int _aaHeight;
        private AntiAliasingMode _antiAliasLogged;

        /// <summary>The anti-aliasing the last 3D frame was drawn with (Off when none ran).</summary>
        public AntiAliasingMode LastAntiAliasing { get; private set; }

        /// <summary>CPU time spent recording the last anti-aliasing pass (0 when none ran).</summary>
        public double LastAntiAliasMs { get; private set; }

        /// <summary>
        /// The anti-aliasing this frame runs: the state's choice, when the frame has a post-process
        /// target, the backend runs shaders (not the CPU rasterizer) and the programs could be made.
        /// </summary>
        private AntiAliasingMode AntiAliasingThisFrame(bool canPost)
        {
            AntiAliasingMode wanted = canPost ? _state.AntiAliasing : AntiAliasingMode.Off;
            AntiAliasingMode mode = wanted switch
            {
                AntiAliasingMode.Fxaa when !IsSoftwareBackend && EnsureFxaaProgram() => AntiAliasingMode.Fxaa,
                AntiAliasingMode.Smaa when !IsSoftwareBackend && EnsureSmaaPrograms() => AntiAliasingMode.Smaa,
                _ => AntiAliasingMode.Off,
            };
            if (mode != _antiAliasLogged)
            {
                _antiAliasLogged = mode;
                RenderLog.Line("Anti-aliasing: " + AntiAliasingModes.Name(mode)
                    + (mode != wanted ? $" ({AntiAliasingModes.Name(wanted)} asked for; not available on {_gpu.BackendName})" : string.Empty));
            }
            if (mode == AntiAliasingMode.Off)
            {
                ReleaseAntiAliasTargets();
                LastAntiAliasMs = 0;
            }
            LastAntiAliasing = mode;
            return mode;
        }

        private bool IsSoftwareBackend => string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        private bool EnsureFxaaProgram()
        {
            if (!_fxaaTried)
            {
                _fxaaTried = true;
                _fxaaProgram = MakeAntiAliasProgram("PS_Fxaa", "FXAA");
            }
            return _fxaaProgram.IsValid;
        }

        private bool EnsureSmaaPrograms()
        {
            if (!_smaaTried)
            {
                _smaaTried = true;
                _smaaEdgesProgram = MakeAntiAliasProgram("PS_SmaaEdges", "SMAA edges");
                _smaaWeightsProgram = MakeAntiAliasProgram("PS_SmaaWeights", "SMAA blend weights");
                _smaaBlendProgram = MakeAntiAliasProgram("PS_SmaaBlend", "SMAA neighbourhood blend");
            }
            return _smaaEdgesProgram.IsValid && _smaaWeightsProgram.IsValid && _smaaBlendProgram.IsValid;
        }

        private GpuShaderProgramHandle MakeAntiAliasProgram(string entry, string name)
        {
            try
            {
                _antiAliasVertexShader ??= CompileShader(AntiAliasingShaders.Source, "VS", GpuShaderStage.Vertex);
                byte[] pixel = CompileShader(AntiAliasingShaders.Source, entry, GpuShaderStage.Pixel);
                if (!_cbAntiAlias.IsValid) _cbAntiAlias = MakeCB<AntiAliasCB>("Anti-aliasing constants");
                return _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = _antiAliasVertexShader,
                    PixelShader = pixel,
                    DebugName = "Anti-aliasing " + name,
                });
            }
            catch (Exception error)
            {
                // A game must keep drawing without it: the frame goes out as it was composed.
                RenderLog.Line($"Anti-aliasing {name} could not be made on {_gpu.BackendName}: {error.GetBaseException().Message}");
                return GpuShaderProgramHandle.Invalid;
            }
        }

        /// <summary>The image the composite and the held items draw into when anti-aliasing follows.</summary>
        private GpuRenderTargetHandle AntiAliasInput(AntiAliasingMode mode, int width, int height)
        {
            EnsureAntiAliasTargets(mode, Math.Max(1, width), Math.Max(1, height));
            return _aaSourceTarget;
        }

        private void EnsureAntiAliasTargets(AntiAliasingMode mode, int width, int height)
        {
            if (_aaWidth != width || _aaHeight != height)
                ReleaseAntiAliasTargets();
            _aaWidth = width;
            _aaHeight = height;
            // Display-encoded, eight bits a channel: what the display target would have held, so
            // pixels the pass leaves alone reach the screen exactly as they would without it.
            if (!_aaSourceTarget.IsValid)
                _aaSourceTexture = MakeAntiAliasTarget("Anti-aliasing source image", out _aaSourceTarget);
            if (mode == AntiAliasingMode.Smaa)
            {
                if (!_smaaEdgesTarget.IsValid)
                    _smaaEdgesTexture = MakeAntiAliasTarget("SMAA edges", out _smaaEdgesTarget);
                if (!_smaaWeightsTarget.IsValid)
                    _smaaWeightsTexture = MakeAntiAliasTarget("SMAA blend weights", out _smaaWeightsTarget);
            }
            else
            {
                ReleaseSmaaTargets();
            }
        }

        private GpuTextureHandle MakeAntiAliasTarget(string name, out GpuRenderTargetHandle target)
        {
            target = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = _aaWidth,
                Height = _aaHeight,
                ColorFormats = new[] { GpuFormat.R8G8B8A8UNorm },
                DepthFormat = GpuFormat.Unknown,
                DepthSampleable = false,
                DebugName = name,
            });
            return _gpu.GetRenderTargetTexture(target, 0);
        }

        private void ReleaseSmaaTargets()
        {
            if (_smaaEdgesTarget.IsValid) _gpu.ReleaseRenderTarget(_smaaEdgesTarget);
            if (_smaaWeightsTarget.IsValid) _gpu.ReleaseRenderTarget(_smaaWeightsTarget);
            _smaaEdgesTarget = _smaaWeightsTarget = GpuRenderTargetHandle.Invalid;
            _smaaEdgesTexture = _smaaWeightsTexture = GpuTextureHandle.Invalid;
        }

        private void ReleaseAntiAliasTargets()
        {
            if (_aaSourceTarget.IsValid) _gpu.ReleaseRenderTarget(_aaSourceTarget);
            _aaSourceTarget = GpuRenderTargetHandle.Invalid;
            _aaSourceTexture = GpuTextureHandle.Invalid;
            ReleaseSmaaTargets();
            _aaWidth = _aaHeight = 0;
        }

        private void ReleaseAntiAliasResources()
        {
            ReleaseAntiAliasTargets();
            foreach (GpuShaderProgramHandle program in new[] { _fxaaProgram, _smaaEdgesProgram, _smaaWeightsProgram, _smaaBlendProgram })
                if (program.IsValid) _gpu.ReleaseShaderProgram(program);
            _fxaaProgram = _smaaEdgesProgram = _smaaWeightsProgram = _smaaBlendProgram = GpuShaderProgramHandle.Invalid;
            if (_cbAntiAlias.IsValid) _gpu.ReleaseBuffer(_cbAntiAlias);
            _cbAntiAlias = GpuBufferHandle.Invalid;
        }

        /// <summary>Anti-aliases the composed image into <paramref name="output"/>.</summary>
        private void RunAntiAliasing(AntiAliasingMode mode, GpuRenderTargetHandle output, int width, int height)
        {
            long started = Stopwatch.GetTimestamp();
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var metrics = new Vector4(1f / width, 1f / height, width, height);
            if (mode == AntiAliasingMode.Smaa)
            {
                DrawAntiAliasPass(_smaaEdgesTarget, _smaaEdgesProgram, metrics, SmaaParams, "SMAA edges",
                    _aaSourceTexture, GpuTextureHandle.Invalid, GpuTextureHandle.Invalid);
                DrawAntiAliasPass(_smaaWeightsTarget, _smaaWeightsProgram, metrics, SmaaParams, "SMAA blend weights",
                    GpuTextureHandle.Invalid, _smaaEdgesTexture, GpuTextureHandle.Invalid);
                DrawAntiAliasPass(output, _smaaBlendProgram, metrics, SmaaParams, "SMAA neighbourhood blend",
                    _aaSourceTexture, GpuTextureHandle.Invalid, _smaaWeightsTexture);
            }
            else
            {
                DrawAntiAliasPass(output, _fxaaProgram, metrics, FxaaParams, "FXAA",
                    _aaSourceTexture, GpuTextureHandle.Invalid, GpuTextureHandle.Invalid);
            }
            LastAntiAliasMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        private void DrawAntiAliasPass(
            GpuRenderTargetHandle target,
            GpuShaderProgramHandle program,
            Vector4 metrics,
            Vector4 parameters,
            string debugName,
            GpuTextureHandle color,
            GpuTextureHandle edges,
            GpuTextureHandle weights)
        {
            _gpu.SetViewport(0, 0, metrics.Z, metrics.W);
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = new[] { GpuAttachmentAction.Keep() },
                HasDepth = false,
                DebugName = debugName,
            });
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(program);
            _gpu.UpdateConstantBuffer(_cbAntiAlias, new AntiAliasCB { Metrics = metrics, Params = parameters });
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbAntiAlias);
            if (color.IsValid) _gpu.SetTexture(GpuShaderStage.Pixel, 0, color);
            if (edges.IsValid) _gpu.SetTexture(GpuShaderStage.Pixel, 1, edges);
            if (weights.IsValid) _gpu.SetTexture(GpuShaderStage.Pixel, 2, weights);
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
