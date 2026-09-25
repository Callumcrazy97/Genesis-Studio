using System.Runtime.InteropServices;
using Genesis.Application.Core.Images;
using SkiaSharp;

namespace Genesis.Application.Editors.Image.Imaging;

public static class ImageWorkspaceStorage
{
    public static string GetDataDirectory(string spriteDocumentPath)
    {
        string fullPath = Path.GetFullPath(spriteDocumentPath);
        string fileName = Path.GetFileName(fullPath);
        string name = fileName.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".image.json".Length]
            : Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(Path.GetDirectoryName(fullPath)!, name + ".spritedata");
    }

    public static ImageWorkspace Load(ImageDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ImageDocument document = session.Document;
        string? documentPath = session.DocumentPath;
        if (documentPath != null)
        {
            string dataDirectory = GetDataDirectory(documentPath);
            ImageWorkspace? fromAssociates = LoadAssociates(document, dataDirectory);
            if (fromAssociates != null) return fromAssociates;
        }

        if (!string.IsNullOrWhiteSpace(document.Import.Source))
        {
            string source = ResolveSource(documentPath, document.Import.Source);
            if (Genesis.Shared.Assets.ImageAssetDecoder.TryDecodeFrames(source, out var decoded, out _))
                return FromDecoded(decoded);
        }

        return ImageWorkspace.CreateBlank(
            Math.Max(1, document.Canvas.Width),
            Math.Max(1, document.Canvas.Height),
            Color.Transparent);
    }

    public static ImageWorkspace Import(ImageDocumentSession session, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(session);
        Genesis.Shared.Assets.DecodedImageAsset decoded =
            Genesis.Shared.Assets.ImageAssetDecoder.DecodeFrames(sourcePath);
        ImageDocument document = session.Document;
        document.Canvas.Width = decoded.Width;
        document.Canvas.Height = decoded.Height;
        document.Import.Mode = decoded.Frames.Count > 1
            ? ImageImportMode.AnimatedImage
            : ImageImportMode.SingleImage;
        document.Frames.Clear();
        document.Layers.Clear();
        ImageWorkspace workspace = FromDecoded(decoded);
        string? documentPath = session.DocumentPath;
        if (documentPath != null)
        {
            string dataDirectory = GetDataDirectory(documentPath);
            Directory.CreateDirectory(dataDirectory);
            string importFileName = Path.GetFileName(sourcePath);
            string importDirectory = Path.Combine(dataDirectory, "import");
            Directory.CreateDirectory(importDirectory);
            string importDestination = Path.Combine(importDirectory, importFileName);
            File.Copy(sourcePath, importDestination, overwrite: true);
            document.Import.Source = Path.Combine(
                    Path.GetFileName(dataDirectory),
                    "import",
                    importFileName)
                .Replace('\\', '/');
        }
        else
        {
            document.Import.Source = null;
        }
        for (int i = 0; i < decoded.Frames.Count; i++)
        {
            string id = workspace.Frames[i].Id.ToString("N");
            document.Frames.Add(new ImageFrame
            {
                Id = id,
                Name = $"Frame {i + 1}",
                DurationMilliseconds = decoded.Frames[i].DurationMilliseconds,
                SourceRectangle = new ImageRectangle
                {
                    Width = decoded.Width,
                    Height = decoded.Height,
                },
            });
        }
        document.Layers.Add(new ImageLayer
        {
            Name = "Layer 1",
            Cels = document.Frames.Select(frame => new ImageCel { FrameId = frame.Id }).ToList(),
        });
        return workspace;
    }

    public static void Save(ImageDocumentSession session, ImageWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(workspace);
        if (session.DocumentPath == null)
            throw new InvalidOperationException("The image session has no document path.");
        string dataDirectory = GetDataDirectory(session.DocumentPath);
        string temporary = dataDirectory + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string backup = dataDirectory + "." + Guid.NewGuid().ToString("N") + ".old";
        bool promoted = false;
        bool committed = false;
        Directory.CreateDirectory(temporary);
        try
        {
            SynchronizeSchema(session.Document, workspace);
            for (int frameIndex = 0; frameIndex < workspace.Frames.Count; frameIndex++)
            {
                ImageFrameBuffer frame = workspace.Frames[frameIndex];
                string frameId = session.Document.Frames[frameIndex].Id;
                string flattened = Path.Combine(temporary, "frames", frameId + ".png");
                WritePng(flattened, workspace.CompositeCurrentFrameFor(frameIndex));
                session.Document.Frames[frameIndex].Source = Path.Combine(
                    Path.GetFileName(dataDirectory), "frames", frameId + ".png").Replace('\\', '/');

                for (int layerIndex = 0; layerIndex < frame.Layers.Count; layerIndex++)
                {
                    ImageLayerBuffer layer = frame.Layers[layerIndex];
                    string layerId = session.Document.Layers[layerIndex].Id;
                    string layerPath = Path.Combine(temporary, "layers", frameId, layerId + ".png");
                    WritePng(layerPath, workspace.Width, workspace.Height, layer.Pixels);
                    ImageCel cel = session.Document.Layers[layerIndex].Cels
                        .First(item => item.FrameId == frameId);
                    cel.Source = Path.Combine(
                        Path.GetFileName(dataDirectory), "layers", frameId, layerId + ".png").Replace('\\', '/');
                }
            }

            if (Directory.Exists(dataDirectory))
            {
                Directory.Move(dataDirectory, backup);
                Directory.Move(temporary, dataDirectory);
            }
            else
            {
                Directory.Move(temporary, dataDirectory);
            }
            promoted = true;

            if (session.Document.Frames.Count > 0
                && !string.IsNullOrWhiteSpace(session.Document.Frames[0].Source))
            {
                session.Document.Import.Source = session.Document.Frames[0].Source;
            }
            else
            {
                session.Document.Import.Source = null;
            }

            session.Save();
            committed = true;
        }
        finally
        {
            if (!committed && Directory.Exists(backup))
            {
                if (promoted && Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
                Directory.Move(backup, dataDirectory);
            }
            else if (!committed && promoted && Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
            if (committed && Directory.Exists(backup))
            {
                // A successful save stays successful when a scanner temporarily locks old files.
                try { Directory.Delete(backup, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.TraceWarning($"Image saved; old pixel files could not be removed from '{backup}': {ex.Message}");
                }
            }
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    public static void WritePng(string path, int width, int height, byte[] rgba)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKBitmap bitmap = new(info);
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), checked(width * height * 4));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream output = File.Create(path);
        encoded.SaveTo(output);
    }

    private static void WritePng(string path, (int Width, int Height, byte[] Pixels) surface) =>
        WritePng(path, surface.Width, surface.Height, surface.Pixels);

    public static string? ResolveSessionImagePath(ImageDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string? documentPath = session.DocumentPath;
        if (documentPath == null)
            return null;

        string dataDirectory = GetDataDirectory(documentPath);
        ImageDocument document = session.Document;
        if (!string.IsNullOrWhiteSpace(document.Import.Source))
        {
            string importPath = ResolveAssociate(dataDirectory, document.Import.Source);
            if (File.Exists(importPath))
                return importPath;
        }

        foreach (ImageFrame frame in document.Frames)
        {
            if (TryResolveAssociateFile(dataDirectory, frame.Source, out string? framePath))
                return framePath;
            foreach (ImageLayer layer in document.Layers)
            {
                ImageCel? cel = layer.Cels.FirstOrDefault(item => item.FrameId == frame.Id);
                if (TryResolveAssociateFile(dataDirectory, cel?.Source, out string? celPath))
                    return celPath;
            }
        }

        return null;
    }

    private static bool TryResolveAssociateFile(string dataDirectory, string? relative, out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(relative))
            return false;
        string candidate = ResolveAssociate(dataDirectory, relative);
        if (!File.Exists(candidate))
            return false;
        fullPath = candidate;
        return true;
    }

    private static bool HasResolvableAssociate(ImageDocument document, string dataDirectory)
    {
        foreach (ImageFrame frame in document.Frames)
        {
            if (TryResolveAssociateFile(dataDirectory, frame.Source, out _)) return true;
            foreach (ImageLayer layer in document.Layers)
            {
                ImageCel? cel = layer.Cels.FirstOrDefault(item => item.FrameId == frame.Id);
                if (TryResolveAssociateFile(dataDirectory, cel?.Source, out _)) return true;
            }
        }
        return false;
    }

    private static ImageWorkspace? LoadAssociates(ImageDocument document, string dataDirectory)
    {
        // A sprite document may intentionally reference pixel data owned by a sibling
        // resource (for example a gameplay alias of an imported source sprite). Such
        // documents do not have to own a local .spritedata directory, so requiring that
        // directory here collapsed a multi-frame document to one blank fallback frame.
        if (document.Frames.Count == 0) return null;
        // If none of the authored frame/cel associates can be resolved, preserve the older
        // fallback to Import.Source (an external GIF/APNG may still contain all frames).
        if (!HasResolvableAssociate(document, dataDirectory) && !string.IsNullOrWhiteSpace(document.Import.Source))
            return null;
        if (!TryResolveWorkspaceDimensions(document, dataDirectory, out int width, out int height))
        {
            width = Math.Max(1, document.Canvas.Width);
            height = Math.Max(1, document.Canvas.Height);
        }

        if (width != document.Canvas.Width || height != document.Canvas.Height)
        {
            document.Canvas.Width = width;
            document.Canvas.Height = height;
        }

        ImageWorkspace workspace = new(width, height);
        foreach (ImageFrame frameSchema in document.Frames)
        {
            Guid frameId = Guid.TryParse(frameSchema.Id, out Guid parsedId) ? parsedId : Guid.NewGuid();
            ImageFrameBuffer frame = new()
            {
                Id = frameId,
                Name = frameSchema.Name,
                DurationMilliseconds = frameSchema.DurationMilliseconds,
            };
            foreach (ImageLayer layerSchema in document.Layers)
            {
                ImageCel? cel = layerSchema.Cels.FirstOrDefault(item => item.FrameId == frameSchema.Id);
                byte[] pixels = new byte[workspace.Width * workspace.Height * 4];
                if (!string.IsNullOrWhiteSpace(cel?.Source))
                {
                    string path = ResolveAssociate(dataDirectory, cel.Source);
                    if (File.Exists(path))
                    {
                        byte[] decoded = Genesis.Shared.Assets.ImageAssetDecoder.DecodeToRgba(path, out int decodedWidth, out int decodedHeight);
                        pixels = NormalizeLoadedPixels(decoded, workspace.Width, workspace.Height, decodedWidth, decodedHeight);
                    }
                }
                frame.Layers.Add(new ImageLayerBuffer
                {
                    Id = Guid.TryParse(layerSchema.Id, out Guid parsedLayerId) ? parsedLayerId : Guid.NewGuid(),
                    Name = layerSchema.Name,
                    Visible = layerSchema.Visible,
                    Locked = layerSchema.Locked,
                    Opacity = (float)layerSchema.Opacity,
                    BlendMode = MapBlendMode(layerSchema.BlendMode),
                    Channel = MapMaterialChannel(document.MaterialChannels
                        .FirstOrDefault(channel => string.Equals(channel.LayerId, layerSchema.Id, StringComparison.OrdinalIgnoreCase))?.Kind),
                    Pixels = pixels,
                });
            }
            // Older imported images store only a flattened frame while retaining an empty
            // default layer schema. Honour that frame when there are no authored cel sources.
            if (frame.Layers.Count > 0 && !string.IsNullOrWhiteSpace(frameSchema.Source)
                && document.Layers.All(layer => layer.Cels.All(cel => cel.FrameId != frameSchema.Id || string.IsNullOrWhiteSpace(cel.Source))))
            {
                var colorLayer = frame.Layers.FirstOrDefault(layer => layer.Channel == ImageMaterialChannel.Color);
                if (colorLayer is not null) colorLayer.Pixels = LoadFlattened(frameSchema, dataDirectory, workspace.Width, workspace.Height);
            }
            if (frame.Layers.Count == 0)
            {
                frame.Layers.Add(new ImageLayerBuffer
                {
                    Name = "Layer 1",
                    Pixels = LoadFlattened(frameSchema, dataDirectory, workspace.Width, workspace.Height),
                });
            }
            workspace.Frames.Add(frame);
        }
        workspace.Touch();
        return workspace;
    }

    private static byte[] LoadFlattened(ImageFrame frame, string dataDirectory, int width, int height)
    {
        if (!string.IsNullOrWhiteSpace(frame.Source))
        {
            string path = ResolveAssociate(dataDirectory, frame.Source);
            if (File.Exists(path))
            {
                byte[] decoded = Genesis.Shared.Assets.ImageAssetDecoder.DecodeToRgba(path, out int decodedWidth, out int decodedHeight);
                return NormalizeLoadedPixels(decoded, width, height, decodedWidth, decodedHeight);
            }
        }
        return new byte[width * height * 4];
    }

    private static bool TryResolveWorkspaceDimensions(
        ImageDocument document,
        string dataDirectory,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        foreach (ImageFrame frame in document.Frames)
        {
            if (TryProbeImageDimensions(frame.Source, dataDirectory, ref width, ref height))
                return true;
            foreach (ImageLayer layer in document.Layers)
            {
                ImageCel? cel = layer.Cels.FirstOrDefault(item => item.FrameId == frame.Id);
                if (TryProbeImageDimensions(cel?.Source, dataDirectory, ref width, ref height))
                    return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(document.Import.Source))
        {
            string importPath = ResolveAssociate(dataDirectory, document.Import.Source);
            if (File.Exists(importPath)
                && Genesis.Shared.Assets.ImageAssetDecoder.TryDecodeFrames(importPath, out var decoded, out _)
                && decoded.Width > 0
                && decoded.Height > 0)
            {
                width = decoded.Width;
                height = decoded.Height;
                return true;
            }
        }

        return width > 0 && height > 0;
    }

    private static bool TryProbeImageDimensions(
        string? relativeSource,
        string dataDirectory,
        ref int width,
        ref int height)
    {
        if (string.IsNullOrWhiteSpace(relativeSource))
            return false;
        string path = ResolveAssociate(dataDirectory, relativeSource);
        if (!File.Exists(path))
            return false;
        Genesis.Shared.Assets.ImageAssetDecoder.DecodeToRgba(path, out int decodedWidth, out int decodedHeight);
        if (decodedWidth <= 0 || decodedHeight <= 0)
            return false;
        width = decodedWidth;
        height = decodedHeight;
        return true;
    }

    private static byte[] NormalizeLoadedPixels(
        byte[] decoded,
        int workspaceWidth,
        int workspaceHeight,
        int decodedWidth,
        int decodedHeight)
    {
        int expected = checked(workspaceWidth * workspaceHeight * 4);
        if (decoded.Length == expected)
            return decoded;
        if (decodedWidth == workspaceWidth && decodedHeight == workspaceHeight && decoded.Length >= expected)
            return decoded.AsSpan(0, expected).ToArray();

        byte[] normalized = new byte[expected];
        if (decodedWidth <= 0 || decodedHeight <= 0)
        {
            Array.Copy(decoded, normalized, Math.Min(decoded.Length, normalized.Length));
            return normalized;
        }

        int copyWidth = Math.Min(workspaceWidth, decodedWidth);
        int copyHeight = Math.Min(workspaceHeight, decodedHeight);
        for (int y = 0; y < copyHeight; y++)
        {
            int sourceOffset = y * decodedWidth * 4;
            int destinationOffset = y * workspaceWidth * 4;
            Array.Copy(decoded, sourceOffset, normalized, destinationOffset, copyWidth * 4);
        }
        return normalized;
    }

    private static ImageWorkspace FromDecoded(Genesis.Shared.Assets.DecodedImageAsset decoded)
    {
        ImageWorkspace workspace = new(decoded.Width, decoded.Height);
        for (int i = 0; i < decoded.Frames.Count; i++)
        {
            ImageFrameBuffer frame = new()
            {
                Name = $"Frame {i + 1}",
                DurationMilliseconds = decoded.Frames[i].DurationMilliseconds,
            };
            frame.Layers.Add(new ImageLayerBuffer
            {
                Name = "Layer 1",
                Pixels = (byte[])decoded.Frames[i].Rgba.Clone(),
            });
            workspace.Frames.Add(frame);
        }
        workspace.Touch();
        return workspace;
    }

    public static void SynchronizeDocument(ImageDocument document, ImageWorkspace workspace) =>
        SynchronizeSchema(document, workspace);

    private static void SynchronizeSchema(ImageDocument document, ImageWorkspace workspace)
    {
        document.Canvas.Width = workspace.Width;
        document.Canvas.Height = workspace.Height;
        while (document.Frames.Count < workspace.Frames.Count)
            document.Frames.Add(new ImageFrame());
        if (document.Frames.Count > workspace.Frames.Count)
            document.Frames.RemoveRange(workspace.Frames.Count, document.Frames.Count - workspace.Frames.Count);
        int layerCount = workspace.Frames.Count == 0 ? 0 : workspace.Frames.Max(frame => frame.Layers.Count);
        while (document.Layers.Count < layerCount)
            document.Layers.Add(new ImageLayer());
        if (document.Layers.Count > layerCount)
            document.Layers.RemoveRange(layerCount, document.Layers.Count - layerCount);

        for (int frameIndex = 0; frameIndex < workspace.Frames.Count; frameIndex++)
        {
            ImageFrameBuffer source = workspace.Frames[frameIndex];
            ImageFrame target = document.Frames[frameIndex];
            target.Id = source.Id.ToString("N");
            target.Name = source.Name;
            target.DurationMilliseconds = Math.Max(1, source.DurationMilliseconds);
            target.SourceRectangle.Width = workspace.Width;
            target.SourceRectangle.Height = workspace.Height;
        }

        for (int layerIndex = 0; layerIndex < document.Layers.Count; layerIndex++)
        {
            ImageLayerBuffer? source = workspace.Frames
                .Select(frame => layerIndex < frame.Layers.Count ? frame.Layers[layerIndex] : null)
                .FirstOrDefault(layer => layer != null);
            ImageLayer target = document.Layers[layerIndex];
            if (source != null) target.Id = source.Id.ToString("N");
            target.Name = source?.Name ?? $"Layer {layerIndex + 1}";
            target.Visible = source?.Visible ?? true;
            target.Locked = source?.Locked ?? false;
            target.Opacity = source?.Opacity ?? 1f;
            target.BlendMode = MapBlendMode(source?.BlendMode ?? ImageBlendMode.Normal);
            target.Cels = document.Frames.Select(frame => new ImageCel { FrameId = frame.Id }).ToList();
        }

        document.MaterialChannels = document.Layers.Select((layer, index) =>
            (Layer: layer, Source: workspace.Frames
                .Select(frame => index < frame.Layers.Count ? frame.Layers[index] : null)
                .FirstOrDefault(candidate => candidate != null)))
            .Where(pair => pair.Source is { Channel: not ImageMaterialChannel.Color })
            .Select(pair => new ImageMaterialChannelDefinition
            {
                Name = pair.Source!.Channel.ToString(),
                Kind = MapMaterialChannel(pair.Source.Channel),
                LayerId = pair.Layer.Id,
                Srgb = pair.Source.Channel is ImageMaterialChannel.Color or ImageMaterialChannel.Emission,
            }).ToList();
    }

    private static string ResolveSource(string? documentPath, string source)
    {
        if (Path.IsPathRooted(source)) return source;
        string basePath = documentPath == null
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(documentPath)!;
        return Path.GetFullPath(Path.Combine(basePath, source.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string ResolveAssociate(string dataDirectory, string relative)
    {
        string normalized = relative.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
            return Path.GetFullPath(normalized);

        string fullDataDirectory = Path.GetFullPath(dataDirectory);
        string documentDirectory = Path.GetDirectoryName(fullDataDirectory)!;
        string dataName = Path.GetFileName(fullDataDirectory);

        // Paths written by ImageWorkspaceStorage.Save are document-relative and begin
        // with the owning .spritedata directory name.
        if (normalized.StartsWith(dataName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.Combine(documentDirectory, normalized));

        // Explicit relative paths (../Original/..., ./Shared/...) are relative to the
        // sprite document, not to its optional .spritedata directory.  This is how
        // Genesis template aliases can reuse original supplied frames without copying
        // or modifying those bytes.
        if (normalized == "." || normalized == ".."
            || normalized.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || normalized.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Path.GetFullPath(Path.Combine(documentDirectory, normalized));

        // Legacy authored cels normally contain paths such as frames/<id>.png or
        // layers/<frame>/<layer>.png inside their .spritedata directory.
        string insideData = Path.GetFullPath(Path.Combine(fullDataDirectory, normalized));
        if (File.Exists(insideData) || Directory.Exists(fullDataDirectory))
            return insideData;

        // A final document-relative fallback keeps read-only/shared resources usable
        // even when they deliberately have no local data directory.
        return Path.GetFullPath(Path.Combine(documentDirectory, normalized));
    }

    // Explicit mapping preserves existing serialized values and all eight authoring modes.
    private static ImageBlendMode MapBlendMode(ImageLayerBlendMode mode) =>
        mode switch
        {
            ImageLayerBlendMode.Multiply => ImageBlendMode.Multiply,
            ImageLayerBlendMode.Screen => ImageBlendMode.Screen,
            ImageLayerBlendMode.Overlay => ImageBlendMode.Overlay,
            ImageLayerBlendMode.Add => ImageBlendMode.Add,
            ImageLayerBlendMode.Subtract => ImageBlendMode.Subtract,
            ImageLayerBlendMode.Darken => ImageBlendMode.Darken,
            ImageLayerBlendMode.Lighten => ImageBlendMode.Lighten,
            _ => ImageBlendMode.Normal,
        };

    private static ImageLayerBlendMode MapBlendMode(ImageBlendMode mode) =>
        mode switch
        {
            ImageBlendMode.Multiply => ImageLayerBlendMode.Multiply,
            ImageBlendMode.Screen => ImageLayerBlendMode.Screen,
            ImageBlendMode.Overlay => ImageLayerBlendMode.Overlay,
            ImageBlendMode.Add => ImageLayerBlendMode.Add,
            ImageBlendMode.Subtract => ImageLayerBlendMode.Subtract,
            ImageBlendMode.Darken => ImageLayerBlendMode.Darken,
            ImageBlendMode.Lighten => ImageLayerBlendMode.Lighten,
            _ => ImageLayerBlendMode.Normal,
        };

    private static ImageMaterialChannel MapMaterialChannel(ImageMaterialChannelKind? channel) => channel switch
    {
        ImageMaterialChannelKind.Normal => ImageMaterialChannel.Normal,
        ImageMaterialChannelKind.Roughness => ImageMaterialChannel.Roughness,
        ImageMaterialChannelKind.Metallic => ImageMaterialChannel.Metallic,
        ImageMaterialChannelKind.Emissive => ImageMaterialChannel.Emission,
        ImageMaterialChannelKind.Height => ImageMaterialChannel.Height,
        ImageMaterialChannelKind.Occlusion => ImageMaterialChannel.Occlusion,
        _ => ImageMaterialChannel.Color,
    };

    private static ImageMaterialChannelKind MapMaterialChannel(ImageMaterialChannel channel) => channel switch
    {
        ImageMaterialChannel.Normal => ImageMaterialChannelKind.Normal,
        ImageMaterialChannel.Roughness => ImageMaterialChannelKind.Roughness,
        ImageMaterialChannel.Metallic => ImageMaterialChannelKind.Metallic,
        ImageMaterialChannel.Emission => ImageMaterialChannelKind.Emissive,
        ImageMaterialChannel.Height => ImageMaterialChannelKind.Height,
        ImageMaterialChannel.Occlusion => ImageMaterialChannelKind.Occlusion,
        _ => ImageMaterialChannelKind.Albedo,
    };
}
