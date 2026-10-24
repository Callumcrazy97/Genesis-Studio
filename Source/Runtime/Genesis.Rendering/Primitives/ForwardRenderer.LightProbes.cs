using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Diagnostics;
using Probes = Genesis.Shared.Rendering.LightProbes;

namespace Genesis.Rendering.Primitives
{
    // Automatic reflections and bounced light (Engine.Lighting.*). The scene is drawn six times from
    // one point into a small atlas (LightProbeShaders), with the sky the forward shader would mirror
    // where nothing was drawn, and each side is averaged into a 6 x 1 row. The forward shader then
    // mirrors the atlas on smooth surfaces (t18) and lights every surface from the row (t19).
    // When a room starts, a script asks or the detail changes, all six views are drawn at once from
    // what that frame draws (usually behind the room's loading cover), so its picture is lit by them.
    // Then, and whenever the camera has travelled far or the light has changed, the views are drawn
    // again one a frame into the second atlas while the first stays in use, so this never costs a
    // long frame; culling keeps what is behind the camera for those frames (Probes.PendingCapture).
    // Nothing runs on the Software renderer.
    internal sealed partial class ForwardRenderer
    {
        // Matches LightProbeShaders.cbuffer LightProbeConstants (b0).
        [StructLayout(LayoutKind.Sequential)]
        private struct LightProbeCB
        {
            public Vector4 FaceParams;
            public Vector4 AmbientSky;
            public Vector4 AmbientGround;
            public Vector4 SkyParams;
            public Vector4 SkyZenith;
            public Vector4 SkyHorizon;
            public Vector4 SkyGround;
        }

        private const float ProbeFarPlane = 1000f;

        private GpuShaderProgramHandle _probeComposeProgram, _probeAverageProgram;
        private bool _probeResourcesTried;
        private GpuBufferHandle _cbLightProbe;
        private GpuRenderTargetHandle _probeFaceTarget;
        private GpuTextureHandle _probeFaceTexture, _probeFaceDepth;
        // Two of each: the one in use (_probeFront) and the one being captured.
        private readonly GpuRenderTargetHandle[] _probeAtlasTargets = new GpuRenderTargetHandle[2], _probeAmbientTargets = new GpuRenderTargetHandle[2];
        private readonly GpuTextureHandle[] _probeAtlasTextures = new GpuTextureHandle[2], _probeAmbientTextures = new GpuTextureHandle[2];
        private int _probeFront, _probeFaceSize;
        private bool _probeReady, _probeCaptureActive, _probeCapturePending;
        // The next view of a capture spread over frames; -1 when none is under way.
        private int _probeNextFace = -1;
        private int _probeGeneration = int.MinValue;
        private Vector3 _probePosition, _probePendingPosition;

        /// <summary>CPU milliseconds the last capture's views took to record this frame (0 until one has run).</summary>
        public double LastLightProbeMs { get; private set; }

        /// <summary>True once a probe has been captured and surfaces are lit from it.</summary>
        internal bool LightProbeReady => _probeReady;

        /// <summary>Where the probe in use was captured.</summary>
        internal Vector3 LightProbePosition => _probePosition;

        /// <summary>Captures this renderer has finished.</summary>
        internal int LightProbeCaptures { get; private set; }

        private bool LightProbesWanted() =>
            Probes.Available
            && (Probes.Reflections || Probes.BouncedLight)
            && _state.LightingEnabled
            && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

        // True between the main pass binding the probe and its end (which unbinds t15-t20): only
        // then do the constants say it is there. Other forward draws (model layers) go without it.
        private bool _probeBound;

        /// <summary>
        /// The probe's constants for EngineCB: x the reflections' strength (0 off), y the bounced
        /// light's, z face pixels, w 1 while it is bound; zero unless it is bound.
        /// </summary>
        private Vector4 LightProbeParams()
        {
            if (!_probeBound) return Vector4.Zero;
            return new Vector4(Probes.Reflections ? Probes.ReflectionStrength : 0f,
                Probes.BouncedLight ? Probes.BouncedLightStrength : 0f, _probeFaceSize, 1f);
        }

        /// <summary>Binds the probe in use for the main pass (t18 atlas, t19 ambient row), or nothing.</summary>
        private void BindLightProbe()
        {
            _probeBound = _probeReady && !_probeCaptureActive && LightProbesWanted();
            if (_probeBound)
            {
                _gpu.SetTexture(GpuShaderStage.Pixel, 18, _probeAtlasTextures[_probeFront]);
                _gpu.SetTexture(GpuShaderStage.Pixel, 19, _probeAmbientTextures[_probeFront]);
            }
            else
            {
                _gpu.ClearTexture(GpuShaderStage.Pixel, 18);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 19);
            }
        }

        /// <summary>Makes a capture decided last frame, goes on with one under way, or decides one for the next frame.</summary>
        private void UpdateLightProbe(GpuTextureHandle whiteTexture)
        {
            LastLightProbeMs = 0;
            if (!LightProbesWanted())
            {
                if (_probeCapturePending || _probeNextFace >= 0) Probes.PendingCapture = default;
                _probeCapturePending = false;
                _probeNextFace = -1;
                return;
            }
            int generation = Probes.Generation;
            if (_probeCapturePending)
            {
                _probeCapturePending = false;
                _probeNextFace = 0;
                CaptureLightProbeFaces(whiteTexture, 1);
                return;
            }
            if (_probeNextFace >= 0)
            {
                if (generation == _probeGeneration)
                {
                    CaptureLightProbeFaces(whiteTexture, 1);
                    return;
                }
                // A new room or a script's Recapture() while travelling: start again, whole.
                _probeNextFace = -1;
            }
            bool whole = generation != _probeGeneration || !_probeReady || _probeFaceSize != Probes.FaceSize;
            bool travelled = Vector3.DistanceSquared(_cameraPos, _probePosition) > Probes.RecaptureDistance * Probes.RecaptureDistance;
            if (!whole && !travelled && !LightingMovedSinceProbe()) return;
            _probeGeneration = generation;
            _probeLighting = CurrentProbeLighting();
            _probePendingPosition = _cameraPos;
            if (whole)
            {
                // At once, from what this frame draws, so its own picture is lit by it...
                _probeNextFace = 0;
                CaptureLightProbeFaces(whiteTexture, 6);
                if (!_probeReady) return;
            }
            // ...then a view a frame from the next, with what is behind the camera kept for it.
            _probeCapturePending = true;
            Probes.PendingCapture = new Vector4(_cameraPos, Probes.CaptureRadius);
        }

        // The light a probe was captured under: the sun's direction and its light, and the ambient colours.
        private (Vector3 Sun, Vector3 SunLight, Vector3 Sky, Vector3 Ground) _probeLighting;

        private (Vector3 Sun, Vector3 SunLight, Vector3 Sky, Vector3 Ground) CurrentProbeLighting() =>
            (_state.LightDirection, _state.SunColor * _state.SunIntensity, _state.AmbientColor, _state.AmbientGroundColor);

        /// <summary>
        /// True when the light has changed enough since the probe was captured to show (the sun turned
        /// about 5 degrees, or its light or the ambient colours changed by a twentieth): a day passing.
        /// </summary>
        private bool LightingMovedSinceProbe()
        {
            var now = CurrentProbeLighting();
            var then = _probeLighting;
            float sunTurn = now.Sun.LengthSquared() > 1e-8f && then.Sun.LengthSquared() > 1e-8f
                ? Vector3.Dot(Vector3.Normalize(now.Sun), Vector3.Normalize(then.Sun))
                : 1f;
            return sunTurn < 0.996f
                || Changed(now.SunLight, then.SunLight) || Changed(now.Sky, then.Sky) || Changed(now.Ground, then.Ground);

            static bool Changed(Vector3 a, Vector3 b) =>
                Vector3.Distance(a, b) > 0.05f * MathF.Max(MathF.Max(a.Length(), b.Length()), 0.2f);
        }

        private bool EnsureLightProbeResources(int faceSize)
        {
            if (!_probeComposeProgram.IsValid)
            {
                if (_probeResourcesTried) return false;
                _probeResourcesTried = true;
                try
                {
                    byte[] vs = CompileShader(LightProbeShaders.Source, "VS", GpuShaderStage.Vertex);
                    _probeComposeProgram = MakeProgram(vs, "PS_Compose", "Light probe compose");
                    _probeAverageProgram = MakeProgram(vs, "PS_Average", "Light probe average");
                    _cbLightProbe = MakeCB<LightProbeCB>("Light probe constants");
                }
                catch (Exception exception)
                {
                    RenderLog.Line("Reflections and bounced light are unavailable on this renderer: " + exception.Message);
                    ReleaseLightProbe();
                    _probeResourcesTried = true;
                    return false;
                }
            }
            if (_probeFaceSize == faceSize && _probeFaceTarget.IsValid) return true;
            ReleaseLightProbeTargets();
            _probeFaceTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = faceSize, Height = faceSize, ColorFormats = new[] { GpuFormat.R16G16B16A16Float, GpuFormat.R8UNorm },
                DepthFormat = SceneDepthFormat, DepthClearsToZero = _reversedDepth, DepthSampleable = true, DebugName = "Light probe view",
            });
            _probeFaceTexture = _gpu.GetRenderTargetTexture(_probeFaceTarget, 0);
            _probeFaceDepth = _gpu.GetRenderTargetDepthTexture(_probeFaceTarget);
            bool made = _probeFaceTexture.IsValid && _probeFaceDepth.IsValid;
            for (int i = 0; i < 2; i++)
            {
                _probeAtlasTargets[i] = MakeTarget(faceSize * 3, faceSize * 2, $"Light probe {i}");
                _probeAtlasTextures[i] = _gpu.GetRenderTargetTexture(_probeAtlasTargets[i], 0);
                _probeAmbientTargets[i] = MakeTarget(6, 1, $"Light probe ambient {i}");
                _probeAmbientTextures[i] = _gpu.GetRenderTargetTexture(_probeAmbientTargets[i], 0);
                made &= _probeAtlasTextures[i].IsValid && _probeAmbientTextures[i].IsValid;
            }
            _probeFaceSize = faceSize;
            _probeReady = false;
            return made;

            GpuShaderProgramHandle MakeProgram(byte[] vertex, string entry, string name) =>
                _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                {
                    BinaryFormat = _gpu.ShaderBinaryFormat,
                    VertexShader = vertex,
                    PixelShader = CompileShader(LightProbeShaders.Source, entry, GpuShaderStage.Pixel),
                    DebugName = name,
                });

            GpuRenderTargetHandle MakeTarget(int width, int height, string name) =>
                _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                {
                    Width = width,
                    Height = height,
                    ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                    DepthFormat = GpuFormat.Unknown,
                    DepthSampleable = false,
                    DebugName = name,
                });
        }

        /// <summary>
        /// Draws up to <paramref name="count"/> more views of the capture under way into the atlas not
        /// in use; after the sixth, its ambient row, and the new probe takes the old one's place.
        /// </summary>
        private void CaptureLightProbeFaces(GpuTextureHandle whiteTexture, int count)
        {
            int faceSize = Probes.FaceSize;
            // A first capture, or another detail, draws into new targets: all six views at once.
            if (_probeFaceSize != faceSize) count = 6;
            if (!EnsureLightProbeResources(faceSize))
            {
                _probeNextFace = -1;
                Probes.PendingCapture = default;
                return;
            }
            long started = Stopwatch.GetTimestamp();
            int back = _probeReady ? 1 - _probeFront : _probeFront;
            Vector3 position = _probePendingPosition;
            Matrix4x4 savedView = _view, savedProjection = _proj;
            Vector3 savedCamera = _cameraPos;
            int savedWidth = _rtWidth, savedHeight = _rtHeight;
            bool savedFog = _screenFogActiveThisFrame;
            ResolveSkyLight(_state, out Vector3 ambientSky, out Vector3 ambientGround,
                out Vector4 skyParams, out Vector4 zenith, out Vector4 horizon, out Vector4 ground);
            var constants = new LightProbeCB
            {
                AmbientSky = new Vector4(ambientSky, 1f),
                AmbientGround = new Vector4(ambientGround, 1f),
                SkyParams = skyParams,
                SkyZenith = zenith,
                SkyHorizon = horizon,
                SkyGround = ground,
            };
            _probeCaptureActive = true;
            try
            {
                _screenFogActiveThisFrame = false;
                _cameraPos = position;
                _rtWidth = _rtHeight = faceSize;
                _proj = D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 2f, 1f, MathF.Max(_nearPlane, 0.05f), ProbeFarPlane);
                for (int drawn = 0; drawn < count && _probeNextFace < 6; drawn++, _probeNextFace++)
                {
                    int face = _probeNextFace;
                    _view = D3dMatrixHelper.CreateLookAtLh(position, position + LightProbeShaders.FaceForward(face), LightProbeShaders.FaceUp(face));
                    // The complete submitted scene, as the planar water reflection draws it.
                    MainPass(_probeFaceTarget, _probeFaceDepth, whiteTexture, postProcessTarget: true);
                    constants.FaceParams = new Vector4(face, SceneDepthFlag, 1f / faceSize, 0f);
                    DrawLightProbePass(_probeAtlasTargets[back], face % 3 * faceSize, face / 3 * faceSize, faceSize, faceSize,
                        _probeComposeProgram, constants, _probeFaceTexture, _probeFaceDepth, GpuTextureHandle.Invalid);
                }
                if (_probeNextFace >= 6)
                {
                    DrawLightProbePass(_probeAmbientTargets[back], 0, 0, 6, 1, _probeAverageProgram, constants,
                        GpuTextureHandle.Invalid, GpuTextureHandle.Invalid, _probeAtlasTextures[back]);
                    _probeFront = back;
                    _probeReady = true;
                    _probePosition = position;
                    _probeNextFace = -1;
                    Probes.PendingCapture = default;
                    LightProbeCaptures++;
                }
            }
            finally
            {
                _view = savedView; _proj = savedProjection; _cameraPos = savedCamera;
                _rtWidth = savedWidth; _rtHeight = savedHeight;
                _screenFogActiveThisFrame = savedFog;
                _probeCaptureActive = false;
                if (_rtWidth > 0 && _rtHeight > 0) _gpu.SetViewport(0, 0, _rtWidth, _rtHeight);
            }
            LastLightProbeMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        private void DrawLightProbePass(GpuRenderTargetHandle target, int x, int y, int width, int height,
            GpuShaderProgramHandle program, in LightProbeCB constants, GpuTextureHandle color, GpuTextureHandle depth, GpuTextureHandle atlas)
        {
            _gpu.UpdateConstantBuffer(_cbLightProbe, constants);
            // Keep: each view fills its own tile of the atlas.
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = target,
                ColorActions = KeepColor,
                HasDepth = false,
                DebugName = "Light probe",
            });
            _gpu.SetViewport(x, y, width, height);
            _gpu.SetDepthState(_dssFogOff);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetRasterState(_rsCullNone);
            _gpu.SetShaderProgram(program);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbLightProbe);
            _gpu.SetTexture(GpuShaderStage.Pixel, 0, color);
            _gpu.SetTexture(GpuShaderStage.Pixel, 1, depth);
            _gpu.SetTexture(GpuShaderStage.Pixel, 2, atlas);
            _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
            _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _gpu.Draw(3);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
            _gpu.EndRenderPass();
        }

        private void ReleaseLightProbeTargets()
        {
            if (_probeFaceTarget.IsValid) _gpu.ReleaseRenderTarget(_probeFaceTarget);
            _probeFaceTarget = default;
            _probeFaceTexture = _probeFaceDepth = default;
            for (int i = 0; i < 2; i++)
            {
                if (_probeAtlasTargets[i].IsValid) _gpu.ReleaseRenderTarget(_probeAtlasTargets[i]);
                if (_probeAmbientTargets[i].IsValid) _gpu.ReleaseRenderTarget(_probeAmbientTargets[i]);
                _probeAtlasTargets[i] = _probeAmbientTargets[i] = default;
                _probeAtlasTextures[i] = _probeAmbientTextures[i] = default;
            }
            _probeFaceSize = 0;
            _probeFront = 0;
            _probeReady = false;
        }

        private void ReleaseLightProbe()
        {
            ReleaseLightProbeTargets();
            if (_probeComposeProgram.IsValid) _gpu.ReleaseShaderProgram(_probeComposeProgram);
            if (_probeAverageProgram.IsValid) _gpu.ReleaseShaderProgram(_probeAverageProgram);
            if (_cbLightProbe.IsValid) _gpu.ReleaseBuffer(_cbLightProbe);
            _probeComposeProgram = _probeAverageProgram = default;
            _cbLightProbe = default;
        }
    }
}
