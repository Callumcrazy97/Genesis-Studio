using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Small content-addressed disk cache shared by every shader backend. The key includes the
    /// fully-expanded HLSL source and compiler identity, so changing an include, target, entry point,
    /// profile, or compiler policy cannot accidentally reuse stale bytecode.
    /// </summary>
    internal static class ShaderBinaryCache
    {
        private const string CacheVersion = "genesis-shader-cache-v1";

        public static string ResolveRoot(string overrideRoot)
        {
            if (!string.IsNullOrWhiteSpace(overrideRoot))
                return Path.GetFullPath(overrideRoot);

            string environmentRoot = Environment.GetEnvironmentVariable("GENESIS_SHADER_CACHE");
            if (!string.IsNullOrWhiteSpace(environmentRoot))
                return Path.GetFullPath(environmentRoot);

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                local = AppContext.BaseDirectory;

            return Path.Combine(local, "GenesisRuntime", "ShaderCache");
        }

        public static string BuildKey(
            string expandedSource,
            string entryPoint,
            string profile,
            GpuShaderBinaryFormat binaryFormat,
            string compilerIdentity)
        {
            string material = string.Join("\n", new[]
            {
                CacheVersion,
                compilerIdentity,
                binaryFormat.ToString(),
                profile,
                entryPoint,
                expandedSource,
            });
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static bool TryRead(
            string root,
            string key,
            GpuShaderBinaryFormat binaryFormat,
            out byte[] blob)
        {
            string path = CachePath(root, key, binaryFormat);
            try
            {
                if (!File.Exists(path))
                {
                    blob = Array.Empty<byte>();
                    return false;
                }

                blob = File.ReadAllBytes(path);
                if (blob.Length > 0)
                    return true;

                TryDelete(path);
            }
            catch (IOException)
            {
                // The cache is an optimisation. A locked/corrupt entry must never stop a game.
            }
            catch (UnauthorizedAccessException)
            {
                // Same rule: compile normally if this machine cannot read the cache directory.
            }

            blob = Array.Empty<byte>();
            return false;
        }

        public static void TryWrite(
            string root,
            string key,
            GpuShaderBinaryFormat binaryFormat,
            ReadOnlySpan<byte> blob)
        {
            if (blob.IsEmpty) return;

            string path = CachePath(root, key, binaryFormat);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(temp, blob.ToArray());
                File.Move(temp, path, true);
            }
            catch (IOException)
            {
                // A parallel viewport may have won the race, or the cache may be unavailable.
            }
            catch (UnauthorizedAccessException)
            {
                // Runtime shader compilation still succeeds; only the optimisation is lost.
            }
            finally
            {
                TryDelete(temp);
            }
        }

        private static string CachePath(string root, string key, GpuShaderBinaryFormat format) =>
            Path.Combine(root, FormatFolder(format), key + Extension(format));

        /// <summary>Where an entry lives below a cache root: <c>dxbc/&lt;key&gt;.dxbc</c> and so on.</summary>
        internal static string RelativePath(string key, GpuShaderBinaryFormat format) =>
            FormatFolder(format) + "/" + key + Extension(format);

        // ── Precompiled (read-only) layer ─────────────────────────────────────────

        /// <summary>
        /// Folder beside Studio and the Player that holds the engine's own shaders, compiled when the
        /// package was built (<see cref="PrecompiledShaders.Build"/>). Same keys and layout as the
        /// runtime cache, plus a manifest naming each file's length and SHA-256.
        /// </summary>
        internal const string PrecompiledFolderName = "PrecompiledShaders";
        internal const string PrecompiledManifestName = "manifest.txt";
        internal const string PrecompiledManifestHeader = "genesis-precompiled-shaders-v1";

        /// <summary>Tests point the precompiled layer at their own folder; null means the usual one.</summary>
        internal static string PrecompiledRootOverride;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PrecompiledIndex> PrecompiledIndexes =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The precompiled folder this process reads, or null when there is none:
        /// GENESIS_PRECOMPILED_SHADERS (a folder, or 0 to turn the layer off), else
        /// <c>PrecompiledShaders</c> beside the running program.
        /// </summary>
        internal static string ResolvePrecompiledRoot()
        {
            string root = PrecompiledRootOverride;
            if (string.IsNullOrWhiteSpace(root))
            {
                string environment = Environment.GetEnvironmentVariable("GENESIS_PRECOMPILED_SHADERS");
                if (environment == "0") return null;
                root = string.IsNullOrWhiteSpace(environment)
                    ? Path.Combine(AppContext.BaseDirectory, PrecompiledFolderName)
                    : environment;
            }
            try { return Path.GetFullPath(root); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        /// <summary>Forgets every manifest read so far (tests that rewrite a precompiled folder).</summary>
        internal static void ForgetPrecompiledIndexes() => PrecompiledIndexes.Clear();

        /// <summary>
        /// Reads an entry from the precompiled folder. A missing manifest or file, or one whose
        /// length or SHA-256 differs from the manifest (a damaged install), is a miss: the shader
        /// is then compiled as if the folder were not there.
        /// </summary>
        public static bool TryReadPrecompiled(string key, GpuShaderBinaryFormat binaryFormat, out byte[] blob)
        {
            blob = Array.Empty<byte>();
            string root = ResolvePrecompiledRoot();
            if (root == null) return false;
            PrecompiledIndex index = PrecompiledIndexes.GetOrAdd(root, PrecompiledIndex.Load);
            if (index.Entries.Count == 0) return false;
            string relative = RelativePath(key, binaryFormat);
            if (!index.Entries.TryGetValue(relative, out PrecompiledEntry entry)) return false;
            try
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(root, FormatFolder(binaryFormat), key + Extension(binaryFormat)));
                if (bytes.Length != entry.Length
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
                blob = bytes;
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        internal readonly record struct PrecompiledEntry(long Length, string Sha256);

        internal sealed class PrecompiledIndex
        {
            public System.Collections.Generic.Dictionary<string, PrecompiledEntry> Entries { get; } =
                new(StringComparer.Ordinal);

            public static PrecompiledIndex Load(string root)
            {
                var index = new PrecompiledIndex();
                try
                {
                    string manifest = Path.Combine(root, PrecompiledManifestName);
                    if (!File.Exists(manifest)) return index;
                    string[] lines = File.ReadAllLines(manifest);
                    if (lines.Length == 0 || !string.Equals(lines[0].Trim(), PrecompiledManifestHeader, StringComparison.Ordinal))
                        return index;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        // <format>/<key><ext> <length> <sha256> <label:entry>
                        string[] parts = lines[i].Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3 || !long.TryParse(parts[1], out long length) || parts[2].Length != 64) continue;
                        index.Entries[parts[0]] = new PrecompiledEntry(length, parts[2]);
                    }
                }
                catch (IOException)
                {
                    index.Entries.Clear();
                }
                catch (UnauthorizedAccessException)
                {
                    index.Entries.Clear();
                }
                return index;
            }
        }

        private static string FormatFolder(GpuShaderBinaryFormat format) => format switch
        {
            GpuShaderBinaryFormat.Dxbc => "dxbc",
            GpuShaderBinaryFormat.Dxil => "dxil",
            GpuShaderBinaryFormat.SpirV => "spirv",
            GpuShaderBinaryFormat.GlslUtf8 => "glsl",
            _ => "unknown",
        };

        private static string Extension(GpuShaderBinaryFormat format) => format switch
        {
            GpuShaderBinaryFormat.Dxbc => ".dxbc",
            GpuShaderBinaryFormat.Dxil => ".dxil",
            GpuShaderBinaryFormat.SpirV => ".spv",
            GpuShaderBinaryFormat.GlslUtf8 => ".glsl",
            _ => ".bin",
        };

        private static void TryDelete(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
