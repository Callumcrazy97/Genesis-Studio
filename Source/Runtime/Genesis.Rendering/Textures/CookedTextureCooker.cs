using System;
using System.IO;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Genesis.Shared.Assets;
using StbImageSharp;

namespace Genesis.Rendering.Textures
{
    /// <summary>
    /// Editor/import-time cooker that converts editable images into GPU-ready BC5/BC7 DDS files.
    /// </summary>
    /// <remarks>
    /// The editable source remains authoritative. The manifest records its file stamp, so runtime
    /// loading automatically falls back to the source when the cook is absent or stale.
    /// </remarks>
    public static class CookedTextureCooker
    {
        public static string GetCookedPath(string sourcePath, CookedTextureFormat format)
        {
            string source = Path.GetFullPath(sourcePath ?? throw new ArgumentNullException(nameof(sourcePath)));
            string directory = Path.GetDirectoryName(source) ?? throw new InvalidOperationException("Texture source has no directory.");
            string stem = Path.GetFileNameWithoutExtension(source);
            string suffix = format == CookedTextureFormat.Bc5Normal ? ".bc5.dds" : ".bc7.dds";
            return Path.Combine(directory, stem + suffix);
        }

        public static string Cook(string sourcePath, CookedTextureFormat format, bool mipmaps = true)
        {
            string source = Path.GetFullPath(sourcePath ?? throw new ArgumentNullException(nameof(sourcePath)));
            if (!File.Exists(source)) throw new FileNotFoundException("Texture source was not found.", source);

            ImageResult image;
            using (FileStream input = File.OpenRead(source))
            {
                image = ImageResult.FromStream(input, ColorComponents.RedGreenBlueAlpha)
                    ?? throw new InvalidDataException("Texture source could not be decoded.");
            }
            if (image.Width <= 0 || image.Height <= 0 || image.Data == null || image.Data.Length < checked(image.Width * image.Height * 4))
                throw new InvalidDataException("Texture source decoded to an invalid RGBA image.");

            // D3D requires BC texture base dimensions to be block-aligned. Resample the
            // cooked copy across the same UV extent; padding would introduce visible seams.
            // The original image, its size and the source-stamp contract remain untouched.
            int width = checked((image.Width + 3) / 4 * 4);
            int height = checked((image.Height + 3) / 4 * 4);
            byte[] pixels = image.Data;
            if (width != image.Width || height != image.Height)
            {
                pixels = new byte[checked(width * height * 4)];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float sx = Math.Clamp((x + .5f) * image.Width / width - .5f, 0, image.Width - 1);
                    float sy = Math.Clamp((y + .5f) * image.Height / height - .5f, 0, image.Height - 1);
                    int x0 = (int)sx, y0 = (int)sy;
                    int x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
                    for (int c = 0; c < 4; c++)
                    {
                        float top = float.Lerp(image.Data[(y0 * image.Width + x0) * 4 + c], image.Data[(y0 * image.Width + x1) * 4 + c], sx - x0);
                        float bottom = float.Lerp(image.Data[(y1 * image.Width + x0) * 4 + c], image.Data[(y1 * image.Width + x1) * 4 + c], sx - x0);
                        pixels[(y * width + x) * 4 + c] = (byte)Math.Clamp(MathF.Round(float.Lerp(top, bottom, sy - y0)), 0, 255);
                    }
                }
            }

            string cooked = GetCookedPath(source, format);
            string temporary = cooked + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                BcEncoder encoder = new();
                encoder.OutputOptions.GenerateMipMaps = mipmaps;
                encoder.OutputOptions.Quality = CompressionQuality.Balanced;
                encoder.OutputOptions.Format = format == CookedTextureFormat.Bc5Normal
                    ? CompressionFormat.Bc5
                    : CompressionFormat.Bc7;
                encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;

                using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    encoder.EncodeToStream(pixels, width, height, PixelFormat.Rgba32, output);
                    output.Flush(flushToDisk: true);
                }

                File.Move(temporary, cooked, overwrite: true);
                CookedTextureManifest manifest = CookedTextureManifestStore.Create(source, cooked, format, mipmaps);
                CookedTextureManifestStore.Write(source, manifest);
                return cooked;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
