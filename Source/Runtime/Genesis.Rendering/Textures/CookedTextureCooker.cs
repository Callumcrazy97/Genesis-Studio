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
                    encoder.EncodeToStream(image.Data, image.Width, image.Height, PixelFormat.Rgba32, output);
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
