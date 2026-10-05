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

/// <summary>
/// How a terrain's paint layers are drawn. The defaults keep the original four-layer surface: tiled
/// albedo, with normal and ORM baked into one whole-terrain texture.
/// </summary>
public sealed record TerrainSurfaceOptions(bool TiledLayerMaps = false, float HeightBlendSharpness = 0f)
{
    public static readonly TerrainSurfaceOptions Default = new();

    /// <summary>Per-layer tiled normal/ORM maps (and the eight-layer shader) are needed.</summary>
    public bool UsesLayerAtlas(int layerCount) => layerCount > 4 || TiledLayerMaps || HeightBlendSharpness > 0f;
}

/// <summary>
/// Every layer's albedo (alpha = blend height), normal and ORM packed side by side, each cell holding
/// one repeat of the layer's image with a wrapped border so tiled, mipmapped sampling never bleeds
/// into a neighbouring layer. Plain 2D textures work on every backend, including OpenGL, which has no
/// texture arrays, and three textures fit the shader slots four separate layers used to need.
/// </summary>
public sealed record TerrainLayerAtlas(int Columns, int Rows, int CellContent, int Width, int Height,
    byte[] Albedo, byte[] Normal, byte[] Orm)
{
    /// <summary>Border on each side of a cell, as a fraction of its content (one sixteenth).</summary>
    public const int GutterDivisor = 16;
    /// <summary>Largest per-layer atlas cell: eight 1024 layers make three 4608 x 2304 textures.</summary>
    public const int MaximumCellContent = 1024;
}

public sealed record TerrainSurfaceMaterialPixels(int Size, byte[] Color, byte[] Normal, byte[] Orm,
    List<TerrainMaterialLayer> Layers, ImageMaterialPixels[] Images, float WorldWidth, float WorldHeight)
{
    public TerrainSurfaceOptions Options { get; init; } = TerrainSurfaceOptions.Default;
    /// <summary>Per-layer maps for the eight-layer shader; null for the original four-layer surface.</summary>
    public TerrainLayerAtlas Atlas { get; init; }
}

/// <summary>Bakes the saved Image layers and terrain paint with the same rules in Studio and Player.</summary>
public static class TerrainSurfaceMaterialBaker
{
    /// <summary>Paint layers a terrain material can blend (two RGBA splat planes).</summary>
    public const int MaximumLayers = 8;

    private sealed class Document
    {
        public Document() { }
        public List<TerrainMaterialLayer> Layers { get; set; } = [];
        public bool TiledLayerMaps { get; set; }
        public float HeightBlendSharpness { get; set; }
    }
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static List<TerrainMaterialLayer> LoadLayers(string resource)
        => (JsonSerializer.Deserialize<Document>(File.ReadAllText(resource), Options)?.Layers ?? []).Take(MaximumLayers).ToList();

    /// <summary>The terrain document's layers together with its surface options.</summary>
    public static (List<TerrainMaterialLayer> Layers, TerrainSurfaceOptions Options) LoadSurface(string resource)
    {
        Document document = JsonSerializer.Deserialize<Document>(File.ReadAllText(resource), Options) ?? new();
        float sharpness = float.IsFinite(document.HeightBlendSharpness) ? Math.Clamp(document.HeightBlendSharpness, 0f, 1f) : 0f;
        return (document.Layers.Take(MaximumLayers).ToList(), new TerrainSurfaceOptions(document.TiledLayerMaps, sharpness));
    }

    /// <param name="splats">Four bytes per sample (layers 1-4), or eight: the first plane followed by layers 5-8.</param>
    public static TerrainSurfaceMaterialPixels Bake(string project, List<TerrainMaterialLayer> layers, byte[] splats,
        int width, int height, float worldWidth, float worldHeight, int size = 2048, TerrainSurfaceOptions options = null)
    {
        long samples = (long)width * height * 4;
        if (width < 1 || height < 1 || (splats.Length != samples && splats.Length != samples * 2) || size < 2 || size > 2048)
            throw new InvalidDataException("Terrain material paint must match its heightfield dimensions.");
        options ??= TerrainSurfaceOptions.Default;
        layers = layers.Take(MaximumLayers).ToList();
        foreach (TerrainMaterialLayer layer in layers)
        {
            if (layer.Color is not { Length: >= 3 } || layer.Color.Take(3).Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("Terrain layer colors require three finite channels.");
            if (!float.IsFinite(layer.Tiling) || layer.Tiling <= 0) throw new InvalidDataException("Terrain image tiling must be positive.");
        }
        ImageMaterialPixels[] images = layers.Select(layer => string.IsNullOrWhiteSpace(layer.Image) ? null
            : Reduce(ImageMaterialAssetLoader.Load(project, layer.Image), layer.Resolution)).ToArray();
        byte[] color = new byte[size * size * 4], normal = new byte[color.Length], orm = new byte[color.Length];
        TerrainSurfaceMaterialPixels surface = new(size, color, normal, orm, layers, images, worldWidth, worldHeight)
        {
            Options = options,
            Atlas = options.UsesLayerAtlas(layers.Count) && layers.Count > 0 ? BuildAtlas(layers, images) : null,
        };
        BakeArea(surface, 0, 0, size, size, splats, width, height, color, normal, orm);
        return surface;
    }

    /// <summary>Packs every layer's albedo, normal and ORM into atlas cells with wrapped borders.</summary>
    public static TerrainLayerAtlas BuildAtlas(IReadOnlyList<TerrainMaterialLayer> layers, IReadOnlyList<ImageMaterialPixels> images)
    {
        int count = Math.Clamp(layers.Count, 1, MaximumLayers);
        int content = 64;
        for (int index = 0; index < count; index++)
            if (index < images.Count && images[index] is { } image)
                content = Math.Max(content, Math.Max(image.Width, image.Height));
        content = (int)Math.Min(TerrainLayerAtlas.MaximumCellContent, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)content));
        int gutter = content / TerrainLayerAtlas.GutterDivisor, cell = content + gutter * 2;
        int columns = Math.Min(count, 4), rows = (count + columns - 1) / columns;
        int atlasWidth = columns * cell, atlasHeight = rows * cell;
        byte[] albedo = new byte[atlasWidth * atlasHeight * 4], normal = new byte[albedo.Length], orm = new byte[albedo.Length];
        for (int index = 0; index < count; index++)
        {
            TerrainMaterialLayer layer = layers[index];
            ImageMaterialPixels image = index < images.Count ? images[index] : null;
            int originX = index % columns * cell, originY = index / columns * cell;
            byte[] solid = [(byte)Math.Clamp(layer.Color[0] * 255, 0, 255), (byte)Math.Clamp(layer.Color[1] * 255, 0, 255), (byte)Math.Clamp(layer.Color[2] * 255, 0, 255), 255];
            for (int y = 0; y < cell; y++)
            for (int x = 0; x < cell; x++)
            {
                int at = ((originY + y) * atlasWidth + originX + x) * 4;
                if (image is null)
                {
                    solid.CopyTo(albedo, at);
                    normal[at] = 128; normal[at + 1] = 128; normal[at + 2] = 255; normal[at + 3] = 255;
                    orm[at] = 255; orm[at + 1] = 184; orm[at + 2] = 0; orm[at + 3] = 255;
                    continue;
                }
                // The border repeats the opposite edge, as a tiled image would continue.
                float u = (x - gutter + .5f) / content, v = (y - gutter + .5f) / content;
                Write4(albedo, at, SampleWrapped(image.Albedo, image.Width, image.Height, u, v));
                Write4(normal, at, SampleWrapped(image.Normal, image.Width, image.Height, u, v));
                Write4(orm, at, SampleWrapped(image.Orm, image.Width, image.Height, u, v));
            }
        }
        return new TerrainLayerAtlas(columns, rows, content, atlasWidth, atlasHeight, albedo, normal, orm);
    }

    public static void BakeArea(TerrainSurfaceMaterialPixels surface, int left, int top, int areaWidth, int areaHeight,
        byte[] splats, int width, int height, byte[] color, byte[] normal, byte[] orm)
    {
        int size = surface.Size;
        long plane = (long)width * height * 4;
        byte[] second = null;
        if (splats.Length == plane * 2 && surface.Layers.Count > 4)
        {
            second = new byte[plane];
            Buffer.BlockCopy(splats, (int)plane, second, 0, (int)plane);
        }
        Span<float> all = stackalloc float[MaximumLayers];
        for (int y = top; y < top + areaHeight; y++)
        for (int x = left; x < left + areaWidth; x++)
        {
            float u = x / (float)(size - 1), v = y / (float)(size - 1);
            Vector4 weights = Sample(splats, width, height, u, v, false);
            if (second is null)
            {
                // Normalize only channels which have an authored layer. A sparse old palette must
                // not turn terrain black where a removed layer used to carry its paint weight.
                for (int index = surface.Layers.Count; index < 4; index++) weights[index] = 0;
                float sum = weights.X + weights.Y + weights.Z + weights.W;
                weights = sum > .00001f ? weights / sum : new Vector4(1, 0, 0, 0);
                all.Clear();
                for (int index = 0; index < 4; index++) all[index] = weights[index];
            }
            else
            {
                Vector4 extra = Sample(second, width, height, u, v, false);
                for (int index = 0; index < 4; index++) { all[index] = weights[index]; all[index + 4] = extra[index]; }
                for (int index = surface.Layers.Count; index < MaximumLayers; index++) all[index] = 0;
                float total = 0;
                for (int index = 0; index < MaximumLayers; index++) total += all[index];
                if (total > .00001f) for (int index = 0; index < MaximumLayers; index++) all[index] /= total;
                else { all.Clear(); all[0] = 1; }
            }
            Vector3 c = default, n = default, o = default;
            for (int layer = 0; layer < surface.Layers.Count; layer++)
            {
                float weight = all[layer]; if (weight < .0001f) continue;
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

    // Texel-centred bilinear sample of a repeating image: an atlas cell the image's own size copies
    // it exactly, and its border continues across the image edge.
    private static Vector4 SampleWrapped(byte[] pixels, int width, int height, float u, float v)
    {
        float x = u * width - .5f, y = v * height - .5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        Vector4 At(int px, int py)
        {
            px = ((px % width) + width) % width; py = ((py % height) + height) % height;
            int i = (py * width + px) * 4; return new Vector4(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) / 255f;
        }
        return Vector4.Lerp(Vector4.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Vector4.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
    }

    private static void Write4(byte[] pixels, int index, Vector4 value)
    {
        for (int channel = 0; channel < 4; channel++) pixels[index + channel] = (byte)Math.Clamp(MathF.Round(value[channel] * 255), 0, 255);
    }

    private static void Write(byte[] pixels, int index, Vector3 value)
    {
        pixels[index] = (byte)Math.Clamp(value.X * 255, 0, 255); pixels[index + 1] = (byte)Math.Clamp(value.Y * 255, 0, 255);
        pixels[index + 2] = (byte)Math.Clamp(value.Z * 255, 0, 255); pixels[index + 3] = 255;
    }
}
