using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Outlines and the object images. A draw with an outline id (MeshDrawCall.OutlineId) is drawn a
    // second time, after the main pass, into ObjectMarks (rgb its outline colour, a its width) and
    // ObjectIds (its id); where several marked draws cover a pixel the nearest is kept. A draw that
    // does not show through walls is marked only where the scene shows it. A full-screen pass then
    // draws the outlines over the finished frame, before held items and project post effects, and
    // the post effects can read both images (t3 and t4). Nothing here runs in a frame without marks.
    internal sealed partial class ForwardRenderer
    {
        private struct OutlineDraw
        {
            public int MeshId;
            public int SkinPaletteId;
            public Matrix4x4 World;
            public Vector4 ColorWidth;
            public int Id;
            public bool ThroughWalls;
            public MeshDrawFlags RasterOverride;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OutlineCB
        {
            public Vector4 Params; // x = widest outline (pixels), yz = image size
        }

        private readonly List<OutlineDraw> _outlineDraws = new();
        private GpuShaderProgramHandle _outlineMarkProgram, _outlineMarkSkinnedProgram, _outlineCompositeProgram;
        private bool _outlineProgramsFailed;
        private GpuRenderTargetHandle _outlineTarget;
        private GpuTextureHandle _objectMarksTexture, _objectIdsTexture, _noObjectsTexture;
        private int _outlineW, _outlineH;
        private bool _outlineReversedDepth;
        private GpuBufferHandle _cbOutline;
        private GpuBlendState _bsOutline;
        private GpuDepthState _dssOutlineMark;
        private bool _objectImagesThisFrame;

        /// <summary>Marked draws last frame, and the widest outline among them.</summary>
        public int LastOutlineDraws { get; private set; }

        /// <summary>CPU time of the last outline pass (both its parts), milliseconds.</summary>
        public double LastOutlineMs { get; private set; }

        private void AddOutline(int meshId, int skinPaletteId, in Matrix4x4 world, Vector4 colorWidth, int id,
            bool throughWalls, MeshDrawFlags rasterOverride)
        {
            _outlineDraws.Add(new OutlineDraw
            {
                MeshId = meshId,
                SkinPaletteId = skinPaletteId,
                World = world,
                ColorWidth = new Vector4(
                    Math.Clamp(colorWidth.X, 0f, 1f), Math.Clamp(colorWidth.Y, 0f, 1f), Math.Clamp(colorWidth.Z, 0f, 1f),
                    Math.Clamp(float.IsFinite(colorWidth.W) ? colorWidth.W : 0f, 0f, OutlineShaders.MaxWidth)),
                Id = id,
                ThroughWalls = throughWalls,
                RasterOverride = rasterOverride,
            });
        }

        private bool EnsureOutlinePrograms()
        {
            if (_outlineMarkProgram.IsValid && _outlineCompositeProgram.IsValid) return true;
            if (_outlineProgramsFailed) return false;
            try
            {
                byte[] mark = CompileShader(OutlineShaders.MarkSource, "PS_OutlineMark", GpuShaderStage.Pixel);
                byte[] composite = CompileShader(OutlineShaders.CompositeSource, "PS_OutlineComposite", GpuShaderStage.Pixel);
                _postEffectVertexShader ??= CompilePostEffectVertexShader(_gpu.ShaderBinaryFormat);
                _outlineMarkProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                    { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = _mainVertexShader, PixelShader = mark, DebugName = "Outline marks" });
                _outlineMarkSkinnedProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                    { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = _skinnedVertexShader, PixelShader = mark, DebugName = "Outline marks skinned" });
                _outlineCompositeProgram = _gpu.CreateShaderProgram(new GpuShaderProgramDesc
                    { BinaryFormat = _gpu.ShaderBinaryFormat, VertexShader = _postEffectVertexShader, PixelShader = composite, DebugName = "Outline composite" });
                _cbOutline = MakeCB<OutlineCB>("Outline constants");
                _bsOutline = new GpuBlendState
                {
                    Enabled = true,
                    SrcColor = GpuBlendFactor.SrcAlpha,
                    DstColor = GpuBlendFactor.InvSrcAlpha,
                    ColorOp = GpuBlendOp.Add,
                    SrcAlpha = GpuBlendFactor.Zero,
                    DstAlpha = GpuBlendFactor.One,
                    AlphaOp = GpuBlendOp.Add,
                    WriteR = true, WriteG = true, WriteB = true, WriteA = true,
                };
                return true;
            }
            catch (Exception error)
            {
                // A backend that cannot make them draws no outlines; the game goes on.
                _outlineProgramsFailed = true;
                RenderLog.Line("Outlines unavailable: " + error.Message);
                ReleaseOutlinePrograms();
                return false;
            }
        }

        private void EnsureOutlineTarget(int width, int height)
        {
            if (_outlineTarget.IsValid && _outlineW == width && _outlineH == height && _outlineReversedDepth == _reversedDepth) return;
            _outlineReversedDepth = _reversedDepth;
            if (_outlineTarget.IsValid) _gpu.ReleaseRenderTarget(_outlineTarget);
            _outlineTarget = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = width,
                Height = height,
                ColorFormats = new[] { GpuFormat.R8G8B8A8UNorm, GpuFormat.R32Float },
                DepthFormat = SceneDepthFormat,
                DepthClearsToZero = _reversedDepth,
                DepthSampleable = false,
                DebugName = "Object marks and ids",
            });
            _objectMarksTexture = _gpu.GetRenderTargetTexture(_outlineTarget, 0);
            _objectIdsTexture = _gpu.GetRenderTargetTexture(_outlineTarget, 1);
            _outlineW = width;
            _outlineH = height;
        }

        /// <summary>
        /// Marks this frame's outlined draws and draws their outlines into <paramref name="target"/>
        /// (the composited frame). Scene depth must be the main pass's.
        /// </summary>
        private void OutlinePass(GpuRenderTargetHandle target, GpuTextureHandle sceneDepth, int width, int height)
        {
            _objectImagesThisFrame = false;
            LastOutlineDraws = _outlineDraws.Count;
            // The CPU renderer runs no shaders of these kinds: it draws no outlines.
            if (_outlineDraws.Count == 0 || width <= 0 || height <= 0 || !sceneDepth.IsValid
                || string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase))
            {
                LastOutlineMs = 0;
                return;
            }
            if (!EnsureOutlinePrograms()) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureOutlineTarget(width, height);

            // ── Marks: through-walls draws first, so a mark the scene shows is kept over a hidden one
            // at the same depth; the target's own depth keeps the nearest.
            _dssOutlineMark = new GpuDepthState
            {
                TestEnabled = true,
                WriteEnabled = true,
                Compare = _reversedDepth ? GpuCompare.GreaterEqual : GpuCompare.LessEqual,
            };
            _outlineDraws.Sort(static (a, b) => b.ThroughWalls.CompareTo(a.ThroughWalls));
            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = _outlineTarget,
                ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 0f), GpuAttachmentAction.Clear(0f, 0f, 0f, 0f) },
                DepthAction = GpuAttachmentAction.Clear(SceneClearDepth, 0f, 0f, 0f),
                HasDepth = true,
                DebugName = "Object marks",
            });
            _gpu.SetViewport(0, 0, width, height);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetDepthState(_dssOutlineMark);
            BindCommonShaderState(shadowPass: false);
            Matrix4x4 lightFar = ComputeLightViewProj(ShadowCascadeKind.Far);
            Matrix4x4 lightMid = _cascade.CascadeCount >= 3 ? ComputeLightViewProj(ShadowCascadeKind.Mid) : lightFar;
            UploadPerFrame(SceneViewProj, lightFar, ComputeLightViewProj(ShadowCascadeKind.Near), lightMid);
            UploadEngineCB();
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbPerFrame);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _cbDraw);
            _gpu.SetTexture(GpuShaderStage.Pixel, 17, sceneDepth);

            float widest = 0f;
            bool skinnedBound = false;
            MeshDrawFlags lastRaster = (MeshDrawFlags)(-1);
            foreach (OutlineDraw draw in _outlineDraws)
            {
                if (!TryGetMesh(draw.MeshId, out MeshEntry mesh)) continue;
                SkinPaletteEntry palette = default;
                bool skinned = draw.SkinPaletteId > 0 && TryGetSkinPalette(draw.SkinPaletteId, out palette);
                if (skinned != skinnedBound || lastRaster == (MeshDrawFlags)(-1))
                {
                    _gpu.SetVertexLayout(skinned ? _layoutSkinned : _layout);
                    _gpu.SetShaderProgram(skinned ? _outlineMarkSkinnedProgram : _outlineMarkProgram);
                    skinnedBound = skinned;
                }
                if (draw.RasterOverride != lastRaster)
                {
                    // Both sides: a mark is a silhouette, whichever faces the camera sees.
                    _gpu.SetRasterState(CullNoneRasterizer(draw.RasterOverride));
                    lastRaster = draw.RasterOverride;
                }
                if (skinned) _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 12, palette.Buffer);
                SetMeshBuffers(ref mesh);
                UploadDrawCB(draw.World, new Vector4(draw.ColorWidth.X, draw.ColorWidth.Y, draw.ColorWidth.Z, 1f),
                    emissive: draw.Id, unlit: draw.ThroughWalls ? 1f : 0f, isFloor: draw.ColorWidth.W,
                    useInstancing: 0, instOffset: 0, gpuSkinning: skinned);
                _gpu.DrawIndexed(mesh.IndexCount);
                LastDrawCalls++;
                LastTriangles += mesh.IndexCount / 3;
                widest = MathF.Max(widest, draw.ColorWidth.W);
            }
            _gpu.ClearTexture(GpuShaderStage.Pixel, 17);
            _gpu.EndRenderPass();
            if (skinnedBound) _gpu.SetVertexLayout(_layout);
            _objectImagesThisFrame = true;

            // ── Outlines over the frame.
            int rings = (int)MathF.Ceiling(widest);
            if (rings > 0)
            {
                _gpu.UpdateConstantBuffer(_cbOutline, new OutlineCB { Params = new Vector4(rings, width, height, 0f) });
                _gpu.BeginRenderPass(new GpuRenderPassDesc
                {
                    Target = target,
                    ColorActions = new[] { GpuAttachmentAction.Keep() },
                    HasDepth = false,
                    DebugName = "Outlines",
                });
                _gpu.SetViewport(0, 0, width, height);
                _gpu.SetDepthState(_dssFogOff);
                _gpu.SetBlendState(_bsOutline);
                _gpu.SetRasterState(_rsCullNone);
                _gpu.SetShaderProgram(_outlineCompositeProgram);
                _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbOutline);
                _gpu.SetTexture(GpuShaderStage.Pixel, 0, _objectMarksTexture);
                _gpu.SetTexture(GpuShaderStage.Pixel, 1, _objectIdsTexture);
                _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
                _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
                _gpu.Draw(3);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
                _gpu.EndRenderPass();
                _gpu.SetBlendState(_bsOpaque);
                LastDrawCalls++;
            }

            _gpu.SetRasterState(CurrentRasterizer());
            _gpu.SetDepthState(_dssDefault);
            LastOutlineMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        /// <summary>
        /// Binds ObjectMarks (t3) and ObjectIds (t4) for a project post effect: this frame's, or an
        /// empty image in a frame without marks.
        /// </summary>
        private void BindObjectImages()
        {
            if (!_noObjectsTexture.IsValid)
                _noObjectsTexture = CreateTexture(1, 1, GpuFormat.R8G8B8A8UNorm, new byte[4], "No object marks");
            bool marked = _objectImagesThisFrame && _objectMarksTexture.IsValid;
            _gpu.SetTexture(GpuShaderStage.Pixel, 3, marked ? _objectMarksTexture : _noObjectsTexture);
            _gpu.SetTexture(GpuShaderStage.Pixel, 4, marked ? _objectIdsTexture : _noObjectsTexture);
        }

        private void ReleaseOutlinePrograms()
        {
            _gpu.ReleaseShaderProgram(_outlineMarkProgram);
            _gpu.ReleaseShaderProgram(_outlineMarkSkinnedProgram);
            _gpu.ReleaseShaderProgram(_outlineCompositeProgram);
            _outlineMarkProgram = _outlineMarkSkinnedProgram = _outlineCompositeProgram = GpuShaderProgramHandle.Invalid;
        }

        private void ReleaseOutlineResources()
        {
            ReleaseOutlinePrograms();
            if (_outlineTarget.IsValid) _gpu.ReleaseRenderTarget(_outlineTarget);
            _outlineTarget = GpuRenderTargetHandle.Invalid;
            _objectMarksTexture = _objectIdsTexture = GpuTextureHandle.Invalid;
            if (_noObjectsTexture.IsValid) _gpu.ReleaseTexture(_noObjectsTexture);
            _noObjectsTexture = GpuTextureHandle.Invalid;
            if (_cbOutline.IsValid) _gpu.ReleaseBuffer(_cbOutline);
            _cbOutline = GpuBufferHandle.Invalid;
        }
    }
}
