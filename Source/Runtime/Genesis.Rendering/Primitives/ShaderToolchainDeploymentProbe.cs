using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Validates that a published Genesis payload owns a complete, pinned DXC toolchain.
    /// This is intentionally stricter than normal developer-time shader compilation: it does not
    /// accept PATH or the user's NuGet cache as evidence that an installed Studio/Player is whole.
    /// </summary>
    public static class ShaderToolchainDeploymentProbe
    {
        private const string ManifestFileName = "toolchain.manifest";

        public static string ValidateLocalDeployment()
        {
            string dxcPath = DxcToolchain.ResolveDxcPathForDeployment(requireLocal: true);
            string toolDirectory = Path.GetDirectoryName(dxcPath)
                ?? throw new InvalidOperationException("Bundled DXC path has no parent directory.");

            string expectedDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Tools", "DXC"));
            if (!string.Equals(
                    Path.GetFullPath(toolDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    expectedDirectory.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Published DXC resolved outside Tools\\DXC: '{dxcPath}'.");
            }

            string compilerDll = Path.Combine(toolDirectory, "dxcompiler.dll");
            string dxilDll = Path.Combine(toolDirectory, "dxil.dll");
            string manifestPath = Path.Combine(toolDirectory, ManifestFileName);
            RequireFile(dxcPath);
            RequireFile(compilerDll);
            RequireFile(dxilDll);
            RequireFile(manifestPath);

            Dictionary<string, string> manifest = ReadManifest(manifestPath);
            RequireManifestValue(manifest, "Package", "Microsoft.Direct3D.DXC");
            RequireManifestValue(manifest, "Version", DxcToolchain.PackageVersion);
            RequireManifestValue(manifest, "Architecture", "win-x64");
            ValidateHash(manifest, "dxc.exe", dxcPath);
            ValidateHash(manifest, "dxcompiler.dll", compilerDll);
            ValidateHash(manifest, "dxil.dll", dxilDll);

            const string source = "float4 VSMain(float4 position : POSITION) : SV_Position { return position; }";
            byte[] dxil = DxcToolchain.CompileDxil(source, "VSMain", "vs_6_0", "DeploymentProbe.hlsl");
            byte[] spirv = DxcToolchain.CompileSpirV(source, "VSMain", "vs_6_0", "DeploymentProbe.hlsl");
            if (dxil.Length == 0 || spirv.Length == 0)
            {
                throw new InvalidOperationException("Bundled DXC produced an empty deployment-probe shader.");
            }

            return $"Microsoft.Direct3D.DXC {DxcToolchain.PackageVersion}; DXIL={dxil.Length} bytes; SPIR-V={spirv.Length} bytes";
        }

        private static Dictionary<string, string> ReadManifest(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0 || equals == line.Length - 1) continue;
                values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
            return values;
        }

        private static void RequireManifestValue(
            IReadOnlyDictionary<string, string> manifest,
            string key,
            string expected)
        {
            if (!manifest.TryGetValue(key, out string actual)
                || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Bundled DXC manifest '{key}' expected '{expected}', got '{actual ?? "<missing>"}'.");
            }
        }

        private static void ValidateHash(
            IReadOnlyDictionary<string, string> manifest,
            string fileName,
            string path)
        {
            string key = fileName + ".sha256";
            if (!manifest.TryGetValue(key, out string expected) || string.IsNullOrWhiteSpace(expected))
            {
                throw new InvalidOperationException($"Bundled DXC manifest has no hash for {fileName}.");
            }

            using FileStream stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Bundled DXC file hash mismatch for {fileName}; deployment is incomplete or mixed-version.");
            }
        }

        private static void RequireFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Required bundled DXC file is missing.", path);
            }
        }
    }
}
