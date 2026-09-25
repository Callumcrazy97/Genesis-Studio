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
