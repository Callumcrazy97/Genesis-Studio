using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.D3dMath;
using Genesis.Shared.Rendering;

namespace Genesis.Rendering.Primitives
{
    // Probes placed in the room (the Room editor's Light Probe and Reflection Probe,
    // Engine.Lighting.Probes, PlacedProbes). Each is captured as the camera's probe is (six views
    // from where it stands, the sky where nothing was drawn) but at half its detail, into its own
    // 3 x 2 block of one atlas, with the light arriving from each side in a row under the blocks.
    // One probe is captured a frame: the frame before, culling keeps what is round it (like the
    // camera probe's PendingCapture), so a room's probes are ready a frame after the last of them
    // and no frame pays for more than one. The forward shader blends the probes covering each pixel
    // by their shares (PlacedProbeWeight) over the camera's probe, which keeps lighting where none
    // reaches; a room without any binds nothing and draws as before. Nothing runs on Software.
    internal sealed partial class ForwardRenderer
    {
        // Matches ForwardShaders' cbuffer PlacedProbeConstants (b7): three float4 rows a probe.
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct PlacedProbeCB
        {
            public fixed float Rows[PlacedProbes.MaxCount * 3 * 4];
        }

        private GpuBufferHandle _cbPlacedProbes;
        private GpuRenderTargetHandle _placedFaceTarget, _placedAtlasTarget, _placedAmbientTarget;
        private GpuTextureHandle _placedFaceTexture, _placedFaceDepth, _placedAtlasTexture, _placedAmbientTexture;
        // The atlas's layout: how many blocks and their views' pixels a side.
        private int _placedSlots, _placedFaceSize;
        private int _placedLayout = int.MinValue, _placedGeneration = int.MinValue, _placedFailedLayout = int.MinValue;
        private PlacedProbe[] _placedInAtlas = Array.Empty<PlacedProbe>();
        // Per block: the probe's revision when it was captured (int.MinValue until it is), and
        // whether the light has changed since.
        private int[] _placedRevision = Array.Empty<int>();
        private bool[] _placedStale = Array.Empty<bool>();
        // The block captured this frame, decided last frame so culling kept what is round it; -1 none.
        private int _placedPending = -1;
        private int _placedFramesSinceLayout;
        private bool _placedBound, _placedAnyCaptured;
        private (Vector3 Sun, Vector3 SunLight, Vector3 Sky, Vector3 Ground, float Lamps) _placedLighting;

        /// <summary>The placed probes this renderer has captured (each capture of one probe counts once).</summary>
        internal int PlacedProbeCaptures { get; private set; }

        /// <summary>Pixels a side of each view of a placed probe: half the camera probe's (16, 32 or 64).</summary>
        internal static int PlacedProbeFaceSize => Math.Max(LightProbes.FaceSize / 2, 16);

        private bool PlacedProbesWanted() => PlacedProbes.Enabled && PlacedProbes.Count > 0 && LightProbesWanted();

        /// <summary>
        /// EngineCB's PlacedProbeParams: x the probes bound (0 none), y their views' pixels a side,
        /// z the reflections' strength, w the bounced light's; zero unless they are bound.
        /// </summary>
        private Vector4 PlacedProbeParams()
        {
            if (!_placedBound) return Vector4.Zero;
            return new Vector4(_placedSlots, _placedFaceSize,
                LightProbes.Reflections ? LightProbes.ReflectionStrength : 0f,
                LightProbes.BouncedLight ? LightProbes.BouncedLightStrength : 0f);
        }

        /// <summary>Binds the placed probes for the main pass (t20, b7) once any is captured.</summary>
        private void BindPlacedProbes()
        {
            _placedBound = _placedSlots > 0 && _placedAnyCaptured && _placedAtlasTexture.IsValid && _cbPlacedProbes.IsValid
                && !_probeCaptureActive && PlacedProbesWanted() && !RenderDiagnosticsOff.LightProbes;
            if (!_placedBound) return;
            _gpu.SetTexture(GpuShaderStage.Pixel, 20, _placedAtlasTexture);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 7, _cbPlacedProbes);
        }

        /// <summary>
        /// Lays out the room's probes when they change, captures the one decided last frame, decides
        /// the next (one not yet captured, then one the light has changed for), and uploads their volumes.
        /// </summary>
        private void UpdatePlacedProbes(GpuTextureHandle whiteTexture)
        {
            if (!PlacedProbesWanted())
            {
                if (_placedPending >= 0) LightProbes.PendingPlacedCapture = default;
                _placedPending = -1;
                if (PlacedProbes.Count == 0 && _placedSlots > 0) ReleasePlacedTargets();
                return;
            }
            IReadOnlyList<PlacedProbe> probes = PlacedProbes.All;
            int faceSize = PlacedProbeFaceSize;
            if (PlacedProbes.Layout != _placedLayout || faceSize != _placedFaceSize
                || Math.Min(probes.Count, PlacedProbes.MaxCount) != _placedSlots || !SameProbes(probes))
            {
                // A layout this renderer could not make (no light probe programs) is not tried every frame.
                if (PlacedProbes.Layout == _placedFailedLayout) return;
                if (!LayOutPlacedProbes(probes, faceSize))
                {
                    _placedFailedLayout = PlacedProbes.Layout;
                    return;
                }
            }
            if (_placedSlots == 0) return;

            if (PlacedProbes.Generation != _placedGeneration)
            {
                _placedGeneration = PlacedProbes.Generation;
                Array.Fill(_placedStale, true);
            }
            var lighting = PlacedProbeLighting();
            if (ProbeLightingMoved(lighting, _placedLighting))
            {
                _placedLighting = lighting;
                Array.Fill(_placedStale, true);
            }

            _placedFramesSinceLayout++;
            if (_placedPending >= 0)
            {
                int slot = _placedPending;
                _placedPending = -1;
                LightProbes.PendingPlacedCapture = default;
                if (slot < _placedSlots) CapturePlacedProbe(slot, whiteTexture);
            }

            int next = NextPlacedProbe();
            if (next >= 0)
            {
                _placedPending = next;
                LightProbes.PendingPlacedCapture = new Vector4(_placedInAtlas[next].Position, LightProbes.CaptureRadius);
            }
            else if (PlacedProbes.FramesToCaptureAll == 0)
            {
                PlacedProbes.FramesToCaptureAll = _placedFramesSinceLayout;
            }
            UploadPlacedProbes();
            PlacedProbes.BlendingAtCamera = PlacedProbes.BlendingAt(_cameraPos);
        }

        private bool SameProbes(IReadOnlyList<PlacedProbe> probes)
        {
            for (int i = 0; i < _placedSlots; i++)
                if (!ReferenceEquals(probes[i], _placedInAtlas[i])) return false;
            return true;
        }

        // The light a placed probe was captured under: the sun and the sky (lamps are its own Recapture()).
        private (Vector3 Sun, Vector3 SunLight, Vector3 Sky, Vector3 Ground, float Lamps) PlacedProbeLighting() =>
            (_state.LightDirection, _state.SunColor * _state.SunIntensity, _state.AmbientColor, _state.AmbientGroundColor, 0f);

        /// <summary>The block to capture next: one never captured, then one moved or under changed light; -1 for none.</summary>
        private int NextPlacedProbe()
        {
            for (int i = 0; i < _placedSlots; i++)
                if (_placedRevision[i] == int.MinValue) return i;
            for (int i = 0; i < _placedSlots; i++)
                if (_placedStale[i] || _placedRevision[i] != _placedInAtlas[i].Revision) return i;
            return -1;
        }

        private bool LayOutPlacedProbes(IReadOnlyList<PlacedProbe> probes, int faceSize)
        {
            ReleasePlacedTargets();
            _placedLayout = PlacedProbes.Layout;
            _placedGeneration = PlacedProbes.Generation;
            _placedFramesSinceLayout = 0;
            _placedLighting = PlacedProbeLighting();
            int count = Math.Min(probes.Count, PlacedProbes.MaxCount);
            _placedInAtlas = new PlacedProbe[count];
            _placedRevision = new int[count];
            _placedStale = new bool[count];
            for (int i = 0; i < count; i++)
            {
                _placedInAtlas[i] = probes[i];
                _placedRevision[i] = int.MinValue;
                probes[i].Captured = false;
            }
            _placedFaceSize = faceSize;
            if (count == 0) return true;
            if (!EnsureLightProbeResources(LightProbes.FaceSize)) return false;
            try
            {
                if (!_cbPlacedProbes.IsValid) _cbPlacedProbes = MakeCB<PlacedProbeCB>("Placed probe constants");
                _placedFaceTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                {
                    Width = faceSize, Height = faceSize, ColorFormats = new[] { GpuFormat.R16G16B16A16Float, GpuFormat.R8UNorm },
                    DepthFormat = SceneDepthFormat, DepthClearsToZero = _reversedDepth, DepthSampleable = true, DebugName = "Placed probe view",
                });
                _placedFaceTexture = _gpu.GetRenderTargetTexture(_placedFaceTarget, 0);
                _placedFaceDepth = _gpu.GetRenderTargetDepthTexture(_placedFaceTarget);
                _placedAtlasTarget = MakeColorTarget(faceSize * 3, (faceSize * 2 + 1) * count, "Placed probes");
                _placedAtlasTexture = _gpu.GetRenderTargetTexture(_placedAtlasTarget, 0);
                _placedAmbientTarget = MakeColorTarget(6, 1, "Placed probe ambient");
                _placedAmbientTexture = _gpu.GetRenderTargetTexture(_placedAmbientTarget, 0);
            }
            catch (Exception exception)
            {
                Genesis.Rendering.Diagnostics.RenderLog.Line("Placed light probes are unavailable on this renderer: " + exception.Message);
                ReleasePlacedTargets();
                return false;
            }
            if (!_placedFaceTexture.IsValid || !_placedFaceDepth.IsValid || !_placedAtlasTexture.IsValid || !_placedAmbientTexture.IsValid)
            {
                ReleasePlacedTargets();
                return false;
            }
            _placedSlots = count;
            return true;

            GpuRenderTargetHandle MakeColorTarget(int width, int height, string name) =>
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
        /// Draws the six views of one placed probe into its block of the atlas, then the light
        /// arriving from each side into its row under the blocks (through a 6 x 1 target, as the
        /// atlas cannot be read while it is drawn into).
        /// </summary>
        private void CapturePlacedProbe(int slot, GpuTextureHandle whiteTexture)
        {
            PlacedProbe probe = _placedInAtlas[slot];
            int faceSize = _placedFaceSize;
            long started = Stopwatch.GetTimestamp();
            Vector3 position = probe.Position;
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
                AtlasParams = new Vector4(slot, faceSize, faceSize * 3, (faceSize * 2 + 1) * _placedSlots),
            };
            _probeCaptureActive = true;
            try
            {
                _screenFogActiveThisFrame = false;
                _cameraPos = position;
                _rtWidth = _rtHeight = faceSize;
                _proj = D3dMatrixHelper.CreatePerspectiveLh(MathF.PI / 2f, 1f, MathF.Max(_nearPlane, 0.05f), ProbeFarPlane);
                for (int face = 0; face < 6; face++)
                {
                    _view = D3dMatrixHelper.CreateLookAtLh(position, position + LightProbeShaders.FaceForward(face), LightProbeShaders.FaceUp(face));
                    MainPass(_placedFaceTarget, _placedFaceDepth, whiteTexture, postProcessTarget: true);
                    constants.FaceParams = new Vector4(face, SceneDepthFlag, 1f / faceSize, 0f);
                    DrawLightProbePass(_placedAtlasTarget, face % 3 * faceSize, (face / 3 + 2 * slot) * faceSize, faceSize, faceSize,
                        _probeComposeProgram, constants, _placedFaceTexture, _placedFaceDepth, GpuTextureHandle.Invalid);
                }
                DrawLightProbePass(_placedAmbientTarget, 0, 0, 6, 1, _probeAverageProgram, constants,
                    GpuTextureHandle.Invalid, GpuTextureHandle.Invalid, _placedAtlasTexture);
                DrawLightProbePass(_placedAtlasTarget, 0, faceSize * 2 * _placedSlots + slot, 6, 1, _probeCopyRowProgram, constants,
                    GpuTextureHandle.Invalid, GpuTextureHandle.Invalid, _placedAmbientTexture);
                _placedRevision[slot] = probe.Revision;
                _placedStale[slot] = false;
                _placedAnyCaptured = true;
                probe.Captured = true;
                PlacedProbeCaptures++;
            }
            finally
            {
                _view = savedView; _proj = savedProjection; _cameraPos = savedCamera;
                _rtWidth = savedWidth; _rtHeight = savedHeight;
                _screenFogActiveThisFrame = savedFog;
                _probeCaptureActive = false;
                if (_rtWidth > 0 && _rtHeight > 0) _gpu.SetViewport(0, 0, _rtWidth, _rtHeight);
            }
            double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            PlacedProbes.LastCaptureMs = milliseconds;
            PlacedProbes.CaptureMsTotal += milliseconds;
            PlacedProbes.CapturesMade++;
        }

        private unsafe void UploadPlacedProbes()
        {
            if (!_cbPlacedProbes.IsValid) return;
            var data = new PlacedProbeCB();
            for (int i = 0; i < _placedSlots; i++)
            {
                PlacedProbe probe = _placedInAtlas[i];
                bool sphere = probe.Shape == PlacedProbeShape.Sphere;
                Vector3 half = probe.HalfSize;
                int row = i * 12;
                data.Rows[row + 0] = probe.Position.X;
                data.Rows[row + 1] = probe.Position.Y;
                data.Rows[row + 2] = probe.Position.Z;
                data.Rows[row + 3] = sphere ? MathF.Max(probe.Radius, 0.01f) : 0f;
                data.Rows[row + 4] = half.X;
                data.Rows[row + 5] = half.Y;
                data.Rows[row + 6] = half.Z;
                data.Rows[row + 7] = MathF.Max(probe.BlendDistance, 0f);
                data.Rows[row + 8] = probe.Kind == PlacedProbeKind.Reflection ? 1f : 0f;
                data.Rows[row + 9] = probe.BoxProjection && !sphere ? 1f : 0f;
                data.Rows[row + 10] = probe.Active && _placedRevision[i] != int.MinValue ? 1f : 0f;
            }
            _gpu.UpdateConstantBuffer(_cbPlacedProbes, data);
        }

        private void ReleasePlacedTargets()
        {
            if (_placedPending >= 0) LightProbes.PendingPlacedCapture = default;
            _placedPending = -1;
            if (_placedFaceTarget.IsValid) _gpu.ReleaseRenderTarget(_placedFaceTarget);
            if (_placedAtlasTarget.IsValid) _gpu.ReleaseRenderTarget(_placedAtlasTarget);
            if (_placedAmbientTarget.IsValid) _gpu.ReleaseRenderTarget(_placedAmbientTarget);
            _placedFaceTarget = _placedAtlasTarget = _placedAmbientTarget = default;
            _placedFaceTexture = _placedFaceDepth = _placedAtlasTexture = _placedAmbientTexture = default;
            _placedSlots = 0;
            _placedAnyCaptured = false;
            _placedBound = false;
            // Laid out again at the next frame that wants them.
            _placedLayout = int.MinValue;
        }

        private void ReleasePlacedProbes()
        {
            ReleasePlacedTargets();
            if (_cbPlacedProbes.IsValid) _gpu.ReleaseBuffer(_cbPlacedProbes);
            _cbPlacedProbes = default;
        }
    }
}
