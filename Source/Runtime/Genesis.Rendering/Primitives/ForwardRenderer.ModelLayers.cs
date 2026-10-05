using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Model layers: models drawn into an image of their own, with a camera of their own, after the
    // world. Layer 1 is the first-person layer (arms and a held weapon: their own field of view and
    // near plane, never inside a wall, casting no shadow); layers from 100 up are models drawn into
    // GUI rectangles (an inventory portrait). The images are then drawn like any GUI image.
    internal sealed partial class ForwardRenderer
    {
        private sealed class ModelLayer
        {
            public Matrix4x4 View = Matrix4x4.Identity, Projection = Matrix4x4.Identity;
            public int Width = 1, Height = 1;
            public bool NoReceiveShadow;
            public readonly List<WorldMesh> Meshes = new();
            public GpuRenderTargetHandle Target = GpuRenderTargetHandle.Invalid;
            public GpuTextureHandle Texture = GpuTextureHandle.Invalid;
            public int TargetWidth, TargetHeight;
            public bool Drawn;
            public int IdleFrames;
        }

        private readonly Dictionary<int, ModelLayer> _modelLayers = new();

        /// <summary>The camera and image size of a model layer for this frame.</summary>
        public void SetModelLayerCamera(int layer, Matrix4x4 view, Matrix4x4 projection, int width, int height, bool receiveShadows)
        {
            if (layer <= 0) return;
            if (!_modelLayers.TryGetValue(layer, out ModelLayer entry)) _modelLayers[layer] = entry = new ModelLayer();
            entry.View = view;
            entry.Projection = projection;
            entry.Width = Math.Clamp(width, 1, 8192);
            entry.Height = Math.Clamp(height, 1, 8192);
            entry.NoReceiveShadow = !receiveShadows;
        }

        /// <summary>The image a model layer was drawn into by the last frame; false when it drew nothing.</summary>
        public bool TryGetModelLayerTexture(int layer, out GpuTextureHandle texture)
        {
            texture = GpuTextureHandle.Invalid;
            if (!_modelLayers.TryGetValue(layer, out ModelLayer entry) || !entry.Drawn || !entry.Texture.IsValid) return false;
            texture = entry.Texture;
            return true;
        }

        private void AddToModelLayer(int layer, in WorldMesh mesh)
        {
            if (!_modelLayers.TryGetValue(layer, out ModelLayer entry)) _modelLayers[layer] = entry = new ModelLayer();
            entry.Meshes.Add(mesh);
        }

        /// <summary>Draws every model layer that has meshes this frame into its image, then empties them.</summary>
        public void RenderModelLayers(GpuTextureHandle whiteTexture)
        {
            if (_modelLayers.Count == 0) return;
            List<int> stale = null;
            foreach ((int id, ModelLayer layer) in _modelLayers)
            {
                if (layer.Meshes.Count == 0)
                {
                    layer.Drawn = false;
                    // A layer nothing has used for a while gives its image back.
                    if (++layer.IdleFrames > 120) (stale ??= new List<int>()).Add(id);
                    continue;
                }
                layer.IdleFrames = 0;
                DrawModelLayer(layer, whiteTexture);
                layer.Meshes.Clear();
            }
            if (stale != null)
                foreach (int id in stale)
                {
                    ReleaseModelLayer(_modelLayers[id]);
                    _modelLayers.Remove(id);
                }
        }

        private void EnsureModelLayerTarget(ModelLayer layer)
        {
            if (layer.Target.IsValid && layer.TargetWidth == layer.Width && layer.TargetHeight == layer.Height) return;
            ReleaseModelLayer(layer);
            layer.Target = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
            {
                Width = layer.Width,
                Height = layer.Height,
                // The forward shader writes its colour and its fog/ambient flag; the colour is
                // encoded for display here, as GUI images are.
                ColorFormats = new[] { GpuFormat.R8G8B8A8UNorm, GpuFormat.R16G16B16A16Float },
                DepthFormat = SceneDepthFormat,
                DepthClearsToZero = _reversedDepth,
                DepthSampleable = false,
                DebugName = "Model layer",
            });
            layer.Texture = _gpu.GetRenderTargetTexture(layer.Target, 0);
            layer.TargetWidth = layer.Width;
            layer.TargetHeight = layer.Height;
        }

        private void ReleaseModelLayer(ModelLayer layer)
        {
            if (layer.Target.IsValid) _gpu.ReleaseRenderTarget(layer.Target);
            layer.Target = GpuRenderTargetHandle.Invalid;
            layer.Texture = GpuTextureHandle.Invalid;
            layer.Drawn = false;
        }

        private void ReleaseModelLayers()
        {
            foreach (ModelLayer layer in _modelLayers.Values) ReleaseModelLayer(layer);
            _modelLayers.Clear();
        }

        private void DrawModelLayer(ModelLayer layer, GpuTextureHandle whiteTexture)
        {
            EnsureModelLayerTarget(layer);
            if (!layer.Target.IsValid) return;

            Vector3 sceneCamera = _cameraPos;
            Matrix4x4.Invert(layer.View, out Matrix4x4 inverse);
            _cameraPos = new Vector3(inverse.M41, inverse.M42, inverse.M43);
            Matrix4x4 projection = _reversedDepth ? layer.Projection * ReverseDepthClip : layer.Projection;
            Matrix4x4 lightFar = ComputeLightViewProj(ShadowCascadeKind.Far);
            Matrix4x4 lightMid = _cascade.CascadeCount >= 3 ? ComputeLightViewProj(ShadowCascadeKind.Mid) : lightFar;
            Matrix4x4 lightNear = ComputeLightViewProj(ShadowCascadeKind.Near);
            UploadPerFrame(layer.View * projection, lightFar, lightNear, lightMid);
            // The layer's image is a GUI image: its colour is written for display.
            _encodeOutputThisPass = LinearPipeline;
            UploadEngineCB();
            _encodeOutputThisPass = false;

            _gpu.BeginRenderPass(new GpuRenderPassDesc
            {
                Target = layer.Target,
                ColorActions = new[] { GpuAttachmentAction.Clear(0f, 0f, 0f, 0f), GpuAttachmentAction.Clear(0f, 0f, 0f, 0f) },
                DepthAction = GpuAttachmentAction.Clear(SceneClearDepth, 0f, 0f, 0f),
                HasDepth = true,
                DebugName = "Model layer",
            });
            _gpu.SetViewport(0, 0, layer.Width, layer.Height);
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetDepthState(_dssDefault);
            BindCommonShaderState(shadowPass: false);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 0, _cbPerFrame);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 1, _cbEngine);
            _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 2, _cbDraw);
            bool shadows = _shadowsActiveThisFrame && !layer.NoReceiveShadow;
            _gpu.SetTexture(GpuShaderStage.Pixel, 2, shadows ? _shadowTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 5, shadows ? _shadowNearTexture : GpuTextureHandle.Invalid);
            _gpu.SetTexture(GpuShaderStage.Pixel, 14, shadows && _cascade.CascadeCount >= 3 ? _shadowMidTexture : GpuTextureHandle.Invalid);
            _gpu.ClearTexture(GpuShaderStage.Pixel, 15);
            _gpu.SetConstantBuffer(GpuShaderStage.Vertex, 4, _cbOmni);

            int lastShader = -1;
            bool lastSkinned = false;
            MeshDrawFlags lastRaster = (MeshDrawFlags)(-1);
            foreach (WorldMesh wm in layer.Meshes)
            {
                if (!TryGetMesh(wm.MeshId, out MeshEntry mesh)) continue;
                if (wm.RasterOverride != lastRaster)
                {
                    _gpu.SetRasterState(CurrentRasterizer(wm.RasterOverride));
                    lastRaster = wm.RasterOverride;
                }
                SkinPaletteEntry skinPalette = default;
                bool skinned = wm.SkinPaletteId > 0 && TryGetSkinPalette(wm.SkinPaletteId, out skinPalette);
                int shaderId = wm.Shader.IsValid ? wm.Shader.Id : 0;
                if (lastShader < 0 || skinned != lastSkinned || shaderId != lastShader)
                {
                    _gpu.SetVertexLayout(skinned ? _layoutSkinned : _layout);
                    _gpu.SetShaderProgram(CurrentForwardProgram(skinned, wm.Shader));
                    lastSkinned = skinned;
                    lastShader = shaderId;
                }
                if (skinned) _gpu.SetStructuredBuffer(GpuShaderStage.Vertex, 12, skinPalette.Buffer);
                if (wm.Shader.IsValid)
                {
                    BindShaderParameters(wm.Shader, wm.ShaderParams0, wm.ShaderParams1, wm.ShaderParams2, wm.ShaderParams3);
                    wm.AuthoredTextures.Bind(_gpu);
                }

                SetMeshBuffers(ref mesh);
                _gpu.SetTexture(GpuShaderStage.Pixel, 1, wm.Texture.IsValid ? wm.Texture : whiteTexture);
                _gpu.SetTexture(GpuShaderStage.Pixel, 3, wm.Normal.IsValid ? wm.Normal : _flatNormalTexture);
                _gpu.SetTexture(GpuShaderStage.Pixel, 7, wm.Orm.IsValid ? wm.Orm : whiteTexture);
                _gpu.SetTexture(GpuShaderStage.Pixel, 9, wm.EmissionMap.IsValid ? wm.EmissionMap : whiteTexture);
                UploadDrawCB(wm.World, wm.Color,
                    emissive: wm.Emissive, unlit: wm.Unlit ? 1f : 0f, isFloor: 0f, useInstancing: 0, instOffset: 0,
                    noFog: true, gpuSkinning: skinned, skinMatrixOffset: 0,
                    surfaceParams: wm.SurfaceParams, detailParams: wm.DetailParams,
                    materialFeatures: new Vector4(wm.Orm.IsValid ? 1f : 0f, 0f, wm.EmissionMap.IsValid ? 1f : 0f, 0f),
                    noReceiveShadow: wm.NoReceiveShadow || layer.NoReceiveShadow, materialFactors: wm.MaterialFactors);
                _gpu.DrawIndexed(mesh.IndexCount);
                LastDrawCalls++;
                LastTriangles += mesh.IndexCount / 3;
            }

            _gpu.EndRenderPass();
            layer.Drawn = true;

            // Back to the scene's camera and state for whatever draws next.
            _cameraPos = sceneCamera;
            if (lastSkinned) _gpu.SetVertexLayout(_layout);
            _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
            _gpu.SetRasterState(CurrentRasterizer());
            Matrix4x4 lightVPMid = _cascade.CascadeCount >= 3 ? ComputeLightViewProj(ShadowCascadeKind.Mid) : lightFar;
            UploadPerFrame(SceneViewProj, lightFar, lightNear, lightVPMid);
            UploadEngineCB();
            if (_rtWidth > 0 && _rtHeight > 0) _gpu.SetViewport(0, 0, _rtWidth, _rtHeight);
        }
    }
}
