using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Rendering.Primitives;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Assets;

/// <summary>Shared Terrain/Room/Player binding. Retains baked PBR maps and samples tile albedo at its original resolution.</summary>
public static class TerrainSurfaceMaterialBinding
{
    private sealed class ShaderState { public RuntimeShaderHandle Shader; }
    private static readonly ConditionalWeakTable<IRenderController, ShaderState> Shaders = new();

    public static MeshDrawCall Create(IRenderController renderer, TerrainSurfaceMaterialPixels pixels,
        byte[] paint, int width, int height)
    {
        List<TextureHandle> owned = new();
        TextureHandle Texture(int w, int h, byte[] data)
        {
            TextureHandle handle = renderer.CreateTexture(w, h, data); owned.Add(handle); return handle;
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
            if (!shader.Shader.IsValid)
                shader.Shader = renderer.RegisterRuntimeShader(TerrainSurfaceShaders.Source, "PS", ShaderPreviewProfile.MeshPipeline);
            material.Shader = shader.Shader;
            byte[] weights = Weights(pixels, paint);
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
        foreach (TextureHandle texture in new[] { material.Texture, material.NormalMap, material.OrmMap, material.FlowMap,
            material.AuthoredTextures.Handle0, material.AuthoredTextures.Handle1, material.AuthoredTextures.Handle2, material.AuthoredTextures.Handle3 })
            if (texture.IsValid) renderer.ReleaseTexture(texture);
    }

    public static void UpdatePaint(IRenderController renderer, in MeshDrawCall material,
        TerrainSurfaceMaterialPixels pixels, byte[] paint, int width, int height)
    {
        if (material.FlowMap.IsValid) renderer.UpdateTexture(material.FlowMap, width, height, Weights(pixels, paint));
        else renderer.UpdateTexture(material.Texture, pixels.Size, pixels.Size, pixels.Color);
        renderer.UpdateTexture(material.NormalMap, pixels.Size, pixels.Size, pixels.Normal);
        renderer.UpdateTexture(material.OrmMap, pixels.Size, pixels.Size, pixels.Orm);
    }

    private static byte[] Weights(TerrainSurfaceMaterialPixels pixels, byte[] paint)
    {
        byte[] weights = (byte[])paint.Clone();
        for (int index = 0; index < weights.Length; index += 4)
            for (int layer = pixels.Layers.Count; layer < 4; layer++) weights[index + layer] = 0;
        return weights;
    }
}
