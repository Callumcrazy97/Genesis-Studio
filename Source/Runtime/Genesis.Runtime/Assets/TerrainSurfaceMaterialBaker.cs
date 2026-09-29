using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace Genesis.Runtime.Assets;

public sealed class TerrainMaterialLayer
{
    public string Name { get; set; } = "Layer";
    public float[] Color { get; set; } = [.4f, .5f, .3f];
    public string Image { get; set; } = "";
    public float Tiling { get; set; } = 8;
    public string Addressing { get; set; } = "Repeat";
    public int Resolution { get; set; } = 2048;
}

public sealed record TerrainSurfaceMaterialPixels(int Size, byte[] Color, byte[] Normal, byte[] Orm,
    List<TerrainMaterialLayer> Layers, ImageMaterialPixels[] Images, float WorldWidth, float WorldHeight);

/// <summary>Bakes the saved Image layers and terrain paint with the same rules in Studio and Player.</summary>
public static class TerrainSurfaceMaterialBaker
{
    private sealed class Document
    {
        public Document() { }
        public List<TerrainMaterialLayer> Layers { get; set; } = [];
    }
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static List<TerrainMaterialLayer> LoadLayers(string resource)
        => (JsonSerializer.Deserialize<Document>(File.ReadAllText(resource), Options)?.Layers ?? []).Take(4).ToList();

    public static TerrainSurfaceMaterialPixels Bake(string project, List<TerrainMaterialLayer> layers, byte[] splats,
        int width, int height, float worldWidth, float worldHeight, int size = 2048)
    {
        if (width < 1 || height < 1 || splats.Length != (long)width * height * 4 || size < 2 || size > 2048)
            throw new InvalidDataException("Terrain material paint must match its heightfield dimensions.");
        layers = layers.Take(4).ToList();
        foreach (TerrainMaterialLayer layer in layers)
        {
            if (layer.Color is not { Length: >= 3 } || layer.Color.Take(3).Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("Terrain layer colors require three finite channels.");
            if (!float.IsFinite(layer.Tiling) || layer.Tiling <= 0) throw new InvalidDataException("Terrain image tiling must be positive.");
        }
        ImageMaterialPixels[] images = layers.Select(layer => string.IsNullOrWhiteSpace(layer.Image) ? null
            : Reduce(ImageMaterialAssetLoader.Load(project, layer.Image), layer.Resolution)).ToArray();
        byte[] color = new byte[size * size * 4], normal = new byte[color.Length], orm = new byte[color.Length];
        TerrainSurfaceMaterialPixels surface = new(size, color, normal, orm, layers, images, worldWidth, worldHeight);
        BakeArea(surface, 0, 0, size, size, splats, width, height, color, normal, orm);
        return surface;
    }

    public static void BakeArea(TerrainSurfaceMaterialPixels surface, int left, int top, int areaWidth, int areaHeight,
        byte[] splats, int width, int height, byte[] color, byte[] normal, byte[] orm)
    {
        int size = surface.Size;
        for (int y = top; y < top + areaHeight; y++)
        for (int x = left; x < left + areaWidth; x++)
        {
            float u = x / (float)(size - 1), v = y / (float)(size - 1);
            Vector4 weights = Sample(splats, width, height, u, v, false);
            // Normalize only channels which have an authored layer. A sparse old palette must
            // not turn terrain black where a removed layer used to carry its paint weight.
            for (int index = surface.Layers.Count; index < 4; index++) weights[index] = 0;
            float total = weights.X + weights.Y + weights.Z + weights.W;
            weights = total > .00001f ? weights / total : new Vector4(1, 0, 0, 0);
            Vector3 c = default, n = default, o = default;
            for (int layer = 0; layer < surface.Layers.Count; layer++)
            {
                float weight = weights[layer]; if (weight < .0001f) continue;
                TerrainMaterialLayer definition = surface.Layers[layer]; ImageMaterialPixels image = surface.Images[layer];
                if (image is null)
                {
                    c += new Vector3(definition.Color[0], definition.Color[1], definition.Color[2]) * weight;
                    n += Vector3.UnitZ * weight; o += new Vector3(1, .72f, 0) * weight; continue;
                }
                float su = definition.Addressing == "Stretch" ? u : definition.Addressing == "Tile" ? u * surface.WorldWidth / definition.Tiling : u * definition.Tiling;
                float sv = definition.Addressing == "Stretch" ? v : definition.Addressing == "Tile" ? v * surface.WorldHeight / definition.Tiling : v * definition.Tiling;
                bool repeat = definition.Addressing is "Repeat" or "Tile";
                Vector4 a = Sample(image.Albedo, image.Width, image.Height, su, sv, repeat);
                Vector4 b = Sample(image.Normal, image.Width, image.Height, su, sv, repeat);
                Vector4 d = Sample(image.Orm, image.Width, image.Height, su, sv, repeat);
                c += new Vector3(a.X, a.Y, a.Z) * weight;
                n += (new Vector3(b.X, b.Y, b.Z) * 2 - Vector3.One) * weight;
                o += new Vector3(d.X, d.Y, d.Z) * weight;
            }
            int pixel = ((y - top) * areaWidth + x - left) * 4;
            Write(color, pixel, c);
            Write(normal, pixel, (n.LengthSquared() > .00001f ? Vector3.Normalize(n) : Vector3.UnitZ) * .5f + new Vector3(.5f));
            Write(orm, pixel, o);
        }
    }

    private static ImageMaterialPixels Reduce(ImageMaterialPixels image, int resolution)
    {
        int width = Math.Min(image.Width, Math.Clamp(resolution, 256, 2048)), height = Math.Min(image.Height, Math.Clamp(resolution, 256, 2048));
        if (width == image.Width && height == image.Height) return image;
        byte[] Resize(byte[] source)
        {
            byte[] result = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                Vector4 pixel = Sample(source, image.Width, image.Height, x / (float)Math.Max(1, width - 1), y / (float)Math.Max(1, height - 1), false);
                int at = (y * width + x) * 4; for (int channel = 0; channel < 4; channel++) result[at + channel] = (byte)(pixel[channel] * 255);
            }
            return result;
        }
        return new(width, height, Resize(image.Albedo), Resize(image.Normal), Resize(image.Orm));
    }

    private static Vector4 Sample(byte[] pixels, int width, int height, float u, float v, bool repeat)
    {
        u = repeat ? u - MathF.Floor(u) : Math.Clamp(u, 0, 1); v = repeat ? v - MathF.Floor(v) : Math.Clamp(v, 0, 1);
        float x = u * (width - 1), y = v * (height - 1); int x0 = (int)x, y0 = (int)y;
        Vector4 At(int px, int py) { int i = (py * width + px) * 4; return new Vector4(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) / 255f; }
        return Vector4.Lerp(Vector4.Lerp(At(x0, y0), At(Math.Min(x0 + 1, width - 1), y0), x - x0),
            Vector4.Lerp(At(x0, Math.Min(y0 + 1, height - 1)), At(Math.Min(x0 + 1, width - 1), Math.Min(y0 + 1, height - 1)), x - x0), y - y0);
    }

    private static void Write(byte[] pixels, int index, Vector3 value)
    {
        pixels[index] = (byte)Math.Clamp(value.X * 255, 0, 255); pixels[index + 1] = (byte)Math.Clamp(value.Y * 255, 0, 255);
        pixels[index + 2] = (byte)Math.Clamp(value.Z * 255, 0, 255); pixels[index + 3] = 255;
    }
}
