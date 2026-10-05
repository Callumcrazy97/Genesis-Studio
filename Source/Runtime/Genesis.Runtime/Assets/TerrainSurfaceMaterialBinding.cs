using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Rendering.Primitives;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;

namespace Genesis.Runtime.Assets;

/// <summary>Shared Terrain/Room/Player binding. Retains baked PBR maps and samples tile albedo at its original resolution.</summary>
public static class TerrainSurfaceMaterialBinding
{
    private sealed class ShaderState { public RuntimeShaderHandle Shader, AtlasShader; }
    private static readonly ConditionalWeakTable<IRenderController, ShaderState> Shaders = new();

    /// <param name="paint">Four bytes per sample, or eight (layers 1-4 then 5-8), as <c>TerrainAsset.CaptureSplatState</c> returns.</param>
    public static MeshDrawCall Create(IRenderController renderer, TerrainSurfaceMaterialPixels pixels,
        byte[] paint, int width, int height)
    {
        List<TextureHandle> owned = new();
        TextureHandle Texture(int w, int h, byte[] data)
        {
            TextureHandle handle = renderer.CreateTexture(w, h, data); owned.Add(handle); return handle;
        }
        TextureHandle MipTexture(int w, int h, byte[] data, TextureColorSpace space)
        {
            TextureHandle handle = renderer.CreateTexture(w, h, data, space); owned.Add(handle); return handle;
        }
        try
        {
            bool software = renderer.BackendName.Equals("Software", StringComparison.OrdinalIgnoreCase);
            MeshDrawCall material = new()
            {
                Texture = software ? Texture(pixels.Size, pixels.Size, pixels.Color) : TextureHandle.Invalid,
                NormalMap = Texture(pixels.Size, pixels.Size, pixels.Normal),
                OrmMap = Texture(pixels.Size, pixels.Size, pixels.Orm),
                World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1,
                SurfaceParams = new(1, 0, 0, 0), DetailParams = new(0, 0, 0, 1),
            };
            // Software's limited shader interpreter uses the same baked material fallback.
            if (software) return material;
            ShaderState shader = Shaders.GetOrCreateValue(renderer);
            if (pixels.Atlas is { } atlas)
            {
                if (!shader.AtlasShader.IsValid)
                    shader.AtlasShader = renderer.RegisterRuntimeShader(TerrainSurfaceShaders.LayerAtlasSource, "PS", ShaderPreviewProfile.MeshPipeline);
                material.Shader = shader.AtlasShader;
                material.Texture = Texture(1, 1, [255, 255, 255, 255]);
                material.FlowMap = Texture(width, height, Weights(pixels, paint, width, height, 0));
                // Layers 5-8 weigh from the height slot; terrain is never displaced by it.
                material.HeightMap = Texture(width, height, Weights(pixels, paint, width, height, 1));
                material.AuthoredTextures.Add(21, MipTexture(atlas.Width, atlas.Height, atlas.Albedo, TextureColorSpace.Srgb));
                material.AuthoredTextures.Add(22, MipTexture(atlas.Width, atlas.Height, atlas.Normal, TextureColorSpace.Linear));
                material.AuthoredTextures.Add(23, MipTexture(atlas.Width, atlas.Height, atlas.Orm, TextureColorSpace.Linear));
                float[] modes = new float[2];
                for (int index = 0; index < pixels.Layers.Count && index < TerrainSurfaceShaders.AtlasLayerCount; index++)
                {
                    TerrainMaterialLayer layer = pixels.Layers[index];
                    float repeat = layer.Addressing == "Tile" ? pixels.WorldWidth / layer.Tiling : layer.Addressing == "Stretch" ? 1 : layer.Tiling;
                    int mode = layer.Addressing == "Tile" ? 1 : layer.Addressing is "Clamp" or "Stretch" ? 2 : 0;
                    if (index < 4) material.ShaderParams0[index] = repeat; else material.ShaderParams1[index - 4] = repeat;
                    modes[index / 4] += mode * (1 << (2 * (index % 4)));
                }
                float aspect = pixels.WorldWidth > 0 ? pixels.WorldHeight / pixels.WorldWidth : 1;
                material.ShaderParams2 = new Vector4(modes[0], modes[1], aspect, Math.Clamp(pixels.Options.HeightBlendSharpness, 0, 1));
                bool longView = MathF.Max(pixels.WorldWidth, pixels.WorldHeight) >= TerrainSurfaceShaders.LongViewWidth;
                material.ShaderParams3 = new Vector4(atlas.Columns, atlas.Rows, longView ? .85f : 0, longView ? 1 : 0);
                return material;
            }
            if (!shader.Shader.IsValid)
                shader.Shader = renderer.RegisterRuntimeShader(TerrainSurfaceShaders.Source, "PS", ShaderPreviewProfile.MeshPipeline);
            material.Shader = shader.Shader;
            byte[] weights = Weights(pixels, paint, width, height, 0);
            material.FlowMap = Texture(width, height, weights);
            for (int index = 0; index < 4; index++)
            {
                TerrainMaterialLayer layer = index < pixels.Layers.Count ? pixels.Layers[index] : new();
                ImageMaterialPixels image = index < pixels.Images.Length ? pixels.Images[index] : null;
                byte[] color = image?.Albedo ?? [(byte)(layer.Color[0] * 255), (byte)(layer.Color[1] * 255), (byte)(layer.Color[2] * 255), 255];
                TextureHandle albedo = Texture(image?.Width ?? 1, image?.Height ?? 1, color);
                if (index == 0) material.Texture = albedo;
                else material.AuthoredTextures.Add(20 + index, albedo);
                material.ShaderParams0[index] = layer.Addressing == "Tile" ? pixels.WorldWidth / layer.Tiling : layer.Addressing == "Stretch" ? 1 : layer.Tiling;
                material.ShaderParams1[index] = layer.Addressing == "Tile" ? pixels.WorldHeight / layer.Tiling : layer.Addressing == "Stretch" ? 1 : layer.Tiling;
                material.ShaderParams2[index] = layer.Addressing is "Clamp" or "Stretch" ? 1 : 0;
            }
            // A terrain you can see across needs its tiling hidden and its fields varied.
            if (MathF.Max(pixels.WorldWidth, pixels.WorldHeight) >= TerrainSurfaceShaders.LongViewWidth)
                material.ShaderParams3 = new Vector4(0.85f, 1f, 0f, 0f);
            return material;
        }
        catch
        {
            foreach (TextureHandle texture in owned) if (texture.IsValid) renderer.ReleaseTexture(texture);
            throw;
        }
    }

    public static void Release(IRenderController renderer, in MeshDrawCall material)
    {
        foreach (TextureHandle texture in new[] { material.Texture, material.NormalMap, material.OrmMap, material.FlowMap, material.HeightMap,
            material.AuthoredTextures.Handle0, material.AuthoredTextures.Handle1, material.AuthoredTextures.Handle2, material.AuthoredTextures.Handle3 })
            if (texture.IsValid) renderer.ReleaseTexture(texture);
    }

    public static void UpdatePaint(IRenderController renderer, in MeshDrawCall material,
        TerrainSurfaceMaterialPixels pixels, byte[] paint, int width, int height)
    {
        if (material.FlowMap.IsValid) renderer.UpdateTexture(material.FlowMap, width, height, Weights(pixels, paint, width, height, 0));
        else renderer.UpdateTexture(material.Texture, pixels.Size, pixels.Size, pixels.Color);
        if (material.HeightMap.IsValid) renderer.UpdateTexture(material.HeightMap, width, height, Weights(pixels, paint, width, height, 1));
        renderer.UpdateTexture(material.NormalMap, pixels.Size, pixels.Size, pixels.Normal);
        renderer.UpdateTexture(material.OrmMap, pixels.Size, pixels.Size, pixels.Orm);
    }

    // One RGBA plane of paint weights (plane 0: layers 1-4, plane 1: layers 5-8), with channels of
    // layers the material does not define cleared so they cannot darken the blend.
    private static byte[] Weights(TerrainSurfaceMaterialPixels pixels, byte[] paint, int width, int height, int plane)
    {
        int length = width * height * 4;
        byte[] weights = new byte[length];
        if (paint.Length >= length * (plane + 1)) Buffer.BlockCopy(paint, length * plane, weights, 0, length);
        for (int index = 0; index < weights.Length; index += 4)
            for (int layer = Math.Max(pixels.Layers.Count - plane * 4, 0); layer < 4; layer++) weights[index + layer] = 0;
        return weights;
    }
}
