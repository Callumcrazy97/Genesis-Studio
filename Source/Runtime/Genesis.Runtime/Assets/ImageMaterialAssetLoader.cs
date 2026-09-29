#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Genesis.Runtime.Imaging;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Assets;

public sealed record ImageMaterialPixels(int Width, int Height, byte[] Albedo, byte[] Normal, byte[] Orm);

/// <summary>Composites the Image Editor's saved material-channel layers for editor and Player use.</summary>
public static class ImageMaterialAssetLoader
{
    private sealed class Document
    {
        public Document() { }
        public List<Layer> Layers { get; set; } = [];
        public List<Channel> MaterialChannels { get; set; } = [];
    }
    private sealed class Layer
    {
        public Layer() { }
        public string Id { get; set; } = "";
        public bool Visible { get; set; } = true;
        public float Opacity { get; set; } = 1;
        public PixelBlendMode BlendMode { get; set; }
        public List<Cel> Cels { get; set; } = [];
    }
    private sealed class Cel
    {
        public Cel() { }
        public string FrameId { get; set; } = "";
        public string Source { get; set; } = "";
    }
    private enum ChannelKind { Albedo, Normal, Metallic, Roughness, Emissive, Occlusion, Height, Mask }
    private sealed class Channel
    {
        public Channel() { }
        public string LayerId { get; set; } = "";
        public ChannelKind Kind { get; set; }
    }
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ImageMaterialPixels Load(string project, string reference, int frameIndex = 0)
    {
        string path = SpriteAssetLoader.ResolveDescriptorPath(project, reference);
        SpriteRuntimeAsset sprite = SpriteAssetLoader.Load(project, reference);
        int frame = SpriteAssetLoader.NormalizeFrameIndex(frameIndex, sprite.Frames.Count);
        string frameId = sprite.Frames.Count > 0 ? sprite.Frames[frame].Id : "";
        int width = sprite.Canvas.Width, height = sprite.Canvas.Height;
        if (width < 1 || height < 1 || (long)width * height > 4_194_304)
            throw new InvalidDataException("Image materials support a maximum canvas of 4 million pixels.");
        Document document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Image material document is empty.");
        string directory = Path.GetDirectoryName(path)!;
        byte[] Normalize(byte[] decoded, int sourceWidth, int sourceHeight)
        {
            byte[] result = new byte[width * height * 4];
            for (int row = 0; row < Math.Min(height, sourceHeight); row++)
                Buffer.BlockCopy(decoded, row * sourceWidth * 4, result, row * width * 4, Math.Min(width, sourceWidth) * 4);
            return result;
        }
        byte[] Read(string source)
        {
            string full = Path.GetFullPath(Path.Combine(directory, source.Replace('/', Path.DirectorySeparatorChar)));
            byte[] decoded = ImageAssetDecoder.DecodeToRgba(full, out int sourceWidth, out int sourceHeight);
            if (decoded.Length == 0) throw new FileNotFoundException("Image material layer source is missing.", full);
            return Normalize(decoded, sourceWidth, sourceHeight);
        }
        byte[] Composite(ChannelKind kind, byte red, byte green, byte blue)
        {
            HashSet<string> ids = document.MaterialChannels.Where(channel => channel.Kind == kind).Select(channel => channel.LayerId).ToHashSet();
            byte[] pixels = new byte[width * height * 4];
            if (!document.Layers.Any(layer => ids.Contains(layer.Id)))
            {
                for (int index = 0; index < pixels.Length; index += 4)
                { pixels[index] = red; pixels[index + 1] = green; pixels[index + 2] = blue; pixels[index + 3] = 255; }
                return pixels;
            }
            foreach (Layer layer in document.Layers)
            {
                if (!layer.Visible || !ids.Contains(layer.Id)) continue;
                Cel? cel = layer.Cels.FirstOrDefault(cel => cel.FrameId == frameId);
                if (string.IsNullOrWhiteSpace(cel?.Source)) continue;
                PixelLayerCompositor.Composite(pixels, Read(cel.Source), layer.Opacity, layer.BlendMode);
            }
            return pixels;
        }
        string albedoPath = SpriteAssetLoader.ResolveFrameTexturePath(path, sprite, frame);
        byte[] albedo = string.IsNullOrWhiteSpace(albedoPath) ? new byte[width * height * 4] : Read(albedoPath);
        byte[] normal = Composite(ChannelKind.Normal, 128, 128, 255);
        byte[] roughness = Composite(ChannelKind.Roughness, 184, 184, 184);
        byte[] occlusion = Composite(ChannelKind.Occlusion, 255, 255, 255);
        byte[] metallic = Composite(ChannelKind.Metallic, 0, 0, 0);
        byte[] orm = new byte[albedo.Length];
        for (int index = 0; index < orm.Length; index += 4)
        { orm[index] = occlusion[index]; orm[index + 1] = roughness[index]; orm[index + 2] = metallic[index]; orm[index + 3] = 255; }
        return new(width, height, albedo, normal, orm);
    }
}
