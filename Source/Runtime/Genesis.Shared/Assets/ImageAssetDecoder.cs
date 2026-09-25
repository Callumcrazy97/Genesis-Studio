using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using StbImageSharp;

namespace Genesis.Shared.Assets;

public sealed class DecodedImageFrame
{
    public byte[] Rgba { get; init; } = Array.Empty<byte>();
    public int DurationMilliseconds { get; init; } = 100;
}

public sealed class DecodedImageAsset
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string FormatName { get; init; } = string.Empty;
    public IReadOnlyList<DecodedImageFrame> Frames { get; init; } = Array.Empty<DecodedImageFrame>();
}

/// <summary>Shared image decode path for editor and runtime texture loading.</summary>
public static class ImageAssetDecoder
{
    private static readonly string[] SupportedExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tga" };

    public static IReadOnlyList<string> Extensions => SupportedExtensions;

    public static bool IsSupportedExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string extension = Path.GetExtension(path);
        foreach (string supported in SupportedExtensions)
        {
            if (string.Equals(extension, supported, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string ResolveImagePath(string projectPath, string folder, string resourceName)
    {
        if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(resourceName)) return null;

        string baseDir = Path.Combine(projectPath, folder, resourceName);
        if (Directory.Exists(baseDir))
        {
            foreach (string ext in SupportedExtensions)
            {
                string p = Path.Combine(baseDir, resourceName + ext);
                if (File.Exists(p)) return p;
            }
            foreach (string file in Directory.EnumerateFiles(baseDir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                foreach (string supported in SupportedExtensions)
                    if (ext == supported) return file;
            }
        }

        foreach (string ext in SupportedExtensions)
        {
            string flat = Path.Combine(projectPath, folder, resourceName + ext);
            if (File.Exists(flat)) return flat;
        }

        return null;
    }

    public static byte[] DecodeToRgba(string path, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<byte>();

        DecodedImageAsset result = DecodeFrames(path);
        width = result.Width;
        height = result.Height;
        return result.Frames.Count == 0 ? Array.Empty<byte>() : result.Frames[0].Rgba;
    }

    public static bool TryDecodeFrames(string path, out DecodedImageAsset result, out string diagnostic)
    {
        try
        {
            result = DecodeFrames(path);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            result = new DecodedImageAsset();
            diagnostic = $"Could not decode '{Path.GetFileName(path)}': {ex.Message}";
            return false;
        }
    }

    public static DecodedImageAsset DecodeFrames(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An image path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Image file was not found.", path);
        if (!IsSupportedExtension(path))
            throw new NotSupportedException(
                $"'{Path.GetExtension(path)}' is not supported. Supported formats: {string.Join(", ", SupportedExtensions)}.");

        using FileStream stream = File.OpenRead(path);
        using SKManagedStream skStream = new(stream, disposeManagedStream: false);
        using SKCodec codec = SKCodec.Create(skStream);
        if (codec == null)
        {
            stream.Position = 0;
            ImageResult fallback = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            return new DecodedImageAsset
            {
                Width = fallback.Width,
                Height = fallback.Height,
                FormatName = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                Frames = new[]
                {
                    new DecodedImageFrame { Rgba = fallback.Data, DurationMilliseconds = 100 },
                },
            };
        }

        SKImageInfo sourceInfo = codec.Info;
        SKImageInfo decodeInfo = new(
            sourceInfo.Width,
            sourceInfo.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        SKCodecFrameInfo[] frameInfos = codec.FrameInfo;
        int frameCount = Math.Max(1, frameInfos.Length);
        List<DecodedImageFrame> frames = new(frameCount);
        for (int index = 0; index < frameCount; index++)
        {
            using SKBitmap bitmap = new(decodeInfo);
            SKCodecOptions options = new(index);
            SKCodecResult result = codec.GetPixels(decodeInfo, bitmap.GetPixels(), options);
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                throw new InvalidDataException($"Image frame {index} could not be decoded ({result}).");
            byte[] rgba = new byte[checked(sourceInfo.Width * sourceInfo.Height * 4)];
            Marshal.Copy(bitmap.GetPixels(), rgba, 0, rgba.Length);
            int duration = index < frameInfos.Length ? frameInfos[index].Duration : 100;
            frames.Add(new DecodedImageFrame
            {
                Rgba = rgba,
                DurationMilliseconds = duration <= 0 ? 100 : duration,
            });
        }
        return new DecodedImageAsset
        {
            Width = sourceInfo.Width,
            Height = sourceInfo.Height,
            FormatName = codec.EncodedFormat.ToString(),
            Frames = frames,
        };
    }
}
