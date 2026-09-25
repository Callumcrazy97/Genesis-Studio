using System;
using System.IO;
using Newtonsoft.Json;

namespace Genesis.Shared.Assets
{
    /// <summary>GPU-ready texture formats Genesis can cook from an editable source image.</summary>
    public enum CookedTextureFormat
    {
        Bc7Color = 0,
        Bc5Normal = 1,
    }

    /// <summary>
    /// Sidecar contract between editor-time texture cooking and backend texture loading.
    /// </summary>
    /// <remarks>
    /// The editable PNG/JPEG remains authoritative. A cooked file is only accepted while the source
    /// length and write stamp still match, so saving an image can never make the runtime silently
    /// use stale GPU bytes.
    /// </remarks>
    public sealed class CookedTextureManifest
    {
        public const int CurrentVersion = 1;

        [JsonProperty("version")] public int Version { get; set; } = CurrentVersion;
        [JsonProperty("sourceLength")] public long SourceLength { get; set; }
        [JsonProperty("sourceLastWriteUtcTicks")] public long SourceLastWriteUtcTicks { get; set; }
        [JsonProperty("cookedFile")] public string CookedFile { get; set; } = string.Empty;
        [JsonProperty("format")] public CookedTextureFormat Format { get; set; }
        [JsonProperty("mipmaps")] public bool Mipmaps { get; set; } = true;
    }

    public static class CookedTextureManifestStore
    {
        public const string ManifestSuffix = ".genesis-texture.json";

        public static string GetManifestPath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("A source path is required.", nameof(sourcePath));
            return Path.GetFullPath(sourcePath) + ManifestSuffix;
        }

        /// <summary>Create the manifest that should accompany an already-written cooked file.</summary>
        public static CookedTextureManifest Create(
            string sourcePath,
            string cookedPath,
            CookedTextureFormat format,
            bool mipmaps)
        {
            string source = RequireFile(sourcePath, nameof(sourcePath));
            string cooked = RequireFile(cookedPath, nameof(cookedPath));
            string sourceDirectory = Path.GetDirectoryName(source) ?? string.Empty;
            string cookedDirectory = Path.GetDirectoryName(cooked) ?? string.Empty;
            if (!string.Equals(sourceDirectory, cookedDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cooked textures must sit beside their editable source file.");

            FileInfo info = new(source);
            return new CookedTextureManifest
            {
                SourceLength = info.Length,
                SourceLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                CookedFile = Path.GetFileName(cooked),
                Format = format,
                Mipmaps = mipmaps,
            };
        }

        public static void Write(string sourcePath, CookedTextureManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            string path = GetManifestPath(sourcePath);
            File.WriteAllText(path, JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        /// <summary>
        /// Resolve a fresh cooked file. Returns false for malformed, missing or stale sidecars.
        /// </summary>
        public static bool TryResolve(
            string sourcePath,
            out string cookedPath,
            out CookedTextureManifest manifest)
        {
            cookedPath = string.Empty;
            manifest = null;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return false;

            string source = Path.GetFullPath(sourcePath);
            string manifestPath = GetManifestPath(source);
            if (!File.Exists(manifestPath)) return false;

            try
            {
                CookedTextureManifest parsed =
                    JsonConvert.DeserializeObject<CookedTextureManifest>(File.ReadAllText(manifestPath));
                if (parsed == null || parsed.Version != CookedTextureManifest.CurrentVersion ||
                    string.IsNullOrWhiteSpace(parsed.CookedFile) ||
                    Path.GetFileName(parsed.CookedFile) != parsed.CookedFile)
                    return false;

                FileInfo sourceInfo = new(source);
                if (sourceInfo.Length != parsed.SourceLength ||
                    sourceInfo.LastWriteTimeUtc.Ticks != parsed.SourceLastWriteUtcTicks)
                    return false;

                string directory = Path.GetDirectoryName(source) ?? string.Empty;
                string candidate = Path.GetFullPath(Path.Combine(directory, parsed.CookedFile));
                if (!File.Exists(candidate)) return false;

                cookedPath = candidate;
                manifest = parsed;
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string RequireFile(string path, string parameter)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException($"Texture file for {parameter} does not exist.", path);
            return Path.GetFullPath(path);
        }
    }
}
