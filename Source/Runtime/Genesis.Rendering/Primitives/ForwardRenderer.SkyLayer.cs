using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.D3dMath;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // The sky layer (MeshDrawCall.Layer = MeshDrawCall.SkyLayer): a game's own sun, moon, stars or
    // sky dome. Its meshes are drawn first in the main pass, right after the frame is cleared to the
    // sky's colour, centred on the camera and without writing depth, so everything the world draws
    // afterwards covers them and the post composite still sees sky there (its haze, clouds and stars
    // go over them as over the sky). Nothing else changes for a game that does not use it.
    internal sealed partial class ForwardRenderer
    {
        private readonly List<WorldMesh> _skyMeshes = new();

        private void AddToSkyLayer(in WorldMesh mesh) => _skyMeshes.Add(mesh);

        /// <summary>
        /// How far from the eye sky meshes are drawn: half the far plane, where nothing is clipped.
        /// About the eye a mesh looks the same at any size, so its own size never matters.
        /// </summary>
        private float SkyLayerReach()
        {
            float far = _state.CameraFarPlane > 0f ? _state.CameraFarPlane : float.PositiveInfinity;
            // The projection's own far plane: row-vector perspective of either handedness.
            float denominator = _proj.M33 - _proj.M34;
            if (MathF.Abs(_proj.M34) > 0.5f && MathF.Abs(denominator) > 1e-6f)
            {
                float projected = MathF.Abs(_proj.M43 / denominator);
                if (float.IsFinite(projected) && projected > _nearPlane) far = MathF.Min(far, projected);
            }
            if (!float.IsFinite(far) || far <= _nearPlane * 4f) far = MathF.Max(1000f, _nearPlane * 1000f);
            return far * 0.5f;
        }

        private void DrawSkyLayer(GpuTextureHandle whiteTexture)
        {
            if (_skyMeshes.Count == 0) return;
            float reach = SkyLayerReach();
            _gpu.SetDepthState(_dssNoWrite);
            int lastShader = -1;
            byte lastBlend = byte.MaxValue;
            MeshDrawFlags lastRaster = (MeshDrawFlags)(-1);
            foreach (WorldMesh wm in _skyMeshes)
            {
                if (!TryGetMesh(wm.MeshId, out MeshEntry mesh)) continue;
                // A shader's stencil passes have no place in the sky: only its surface is drawn.
                if (wm.Shader.IsValid && RuntimePassMode(wm.Shader) != Genesis.Shared.Assets.ShaderMeshPassMode.Surface) continue;

                // Centred on the eye and scaled so the mesh's far side sits at the reach.
                Vector3 centre = Vector3.Transform(mesh.BoundsCenter, wm.World);
                float extent = centre.Length() + (mesh.BoundsRadius * MatrixScaleHelper.MaxScale(wm.World));
                float scale = extent > 1e-6f && float.IsFinite(extent) ? reach / extent : 1f;
                Matrix4x4 world = wm.World * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(_cameraPos);

                byte blend = wm.Multiply ? (byte)3 : wm.Additive ? (byte)2 : wm.Transparent ? (byte)1 : (byte)0;
                if (blend != lastBlend)
                {
                    _gpu.SetBlendState(blend switch { 3 => _bsMultiply, 2 => _bsAdd, 1 => _bsAlpha, _ => _bsOpaque });
                    lastBlend = blend;
                }
                if (wm.RasterOverride != lastRaster)
                {
                    _gpu.SetRasterState(CurrentRasterizer(wm.RasterOverride));
                    lastRaster = wm.RasterOverride;
                }
                int shaderId = wm.Shader.IsValid ? wm.Shader.Id : 0;
                if (shaderId != lastShader)
                {
                    _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false, wm.Shader));
                    lastShader = shaderId;
                }
                BindShaderParameters(wm.Shader, wm.ShaderParams0, wm.ShaderParams1, wm.ShaderParams2, wm.ShaderParams3);
                if (wm.Shader.IsValid) wm.AuthoredTextures.Bind(_gpu);

                SetMeshBuffers(ref mesh);
                _gpu.SetTexture(GpuShaderStage.Pixel, 1, wm.Texture.IsValid ? wm.Texture : whiteTexture);
                // Its own colours, unlit, not fogged here: the composite fogs the sky, and these with it.
                UploadDrawCB(world, wm.Color,
                    emissive: wm.Emissive, unlit: 1f, isFloor: 0f, useInstancing: 0, instOffset: 0,
                    noFog: true, noReceiveShadow: true, materialFactors: wm.MaterialFactors);
                _gpu.DrawIndexed(mesh.IndexCount);
                LastDrawCalls++;
                LastTriangles += mesh.IndexCount / 3;
            }

            // Back to what the main pass expects next.
            if (lastShader > 0)
            {
                _gpu.SetShaderProgram(CurrentForwardProgram(skinned: false));
                BindShaderParameters(default, default, default, default, default);
            }
            _gpu.SetBlendState(_bsOpaque);
            _gpu.SetDepthState(_dssDefault);
            _gpu.SetRasterState(CurrentRasterizer());
        }
    }
}
