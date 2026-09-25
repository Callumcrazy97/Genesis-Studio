using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Deterministic command-line bridge to the pinned DirectX Shader Compiler package.
    /// DXIL and Vulkan SPIR-V are produced by the same DXC binary so HLSL remains Genesis's
    /// single authored shader language.
    /// </summary>
    internal static class DxcToolchain
    {
        internal const string PackageId = "microsoft.direct3d.dxc";
        internal const string PackageVersion = "1.9.2607.13";
        private const string HlslLanguageVersion = "2021";
        private const string Optimization = "O3";

        private static readonly object ResolveLock = new();
        private static string _resolvedPath;
        private static string _resolvedVersion;

        public static string CompilerIdentity(GpuShaderBinaryFormat binaryFormat)
        {
            string path = ResolveDxcPath();
            string version = ResolveDxcVersion(path);
            return binaryFormat switch
            {
                GpuShaderBinaryFormat.Dxil =>
                    $"dxc-dxil|package={PackageVersion}|file={version}|HV={HlslLanguageVersion}|{Optimization}|v2",
                GpuShaderBinaryFormat.SpirV =>
                    $"dxc-spirv|package={PackageVersion}|file={version}|HV={HlslLanguageVersion}|{Optimization}|"
                    + VulkanShaderBindingPolicy.CompilerPolicyIdentity + "|v2",
                _ => throw new ArgumentOutOfRangeException(nameof(binaryFormat), binaryFormat, null),
            };
        }

        public static byte[] CompileDxil(
            string expandedSource,
            string entryPoint,
            string profile,
            string sourcePath)
        {
            byte[] blob = CompileCore(
                expandedSource,
                entryPoint,
                profile,
                sourcePath,
                GpuShaderBinaryFormat.Dxil,
                Array.Empty<string>());

            if (!IsDxilContainer(blob))
            {
                throw new InvalidOperationException(
                    $"DXC returned a container for {profile}/{entryPoint}, but it contains no DXIL part.");
            }

            return blob;
        }

        /// <param name="useDirectXLayout">
        /// True to keep Direct3D's constant packing rules. False emits std140/std430-expressible
        /// blocks accepted by Vulkan, OpenGL translation, and WebGPU's SPIR-V frontend.
        /// </param>
        /// <remarks>
        /// OpenGL is the reason this is a parameter. Desktop GLSL has no scalar-block-layout escape
        /// hatch, so SPIRV-Cross rejects a DX-packed block outright with "cannot be expressed as any
        /// of std430, std140, scalar". Genesis's constant buffers are explicitly padded to 16-byte
        /// boundaries already, so the standard rules produce the same offsets the CPU side writes —
        /// which is what <see cref="Diagnostics.ShaderLayoutAudit"/> exists to keep true.
        /// </remarks>
        public static byte[] CompileSpirV(
            string expandedSource,
            string entryPoint,
            string profile,
            string sourcePath,
            bool useDirectXLayout = false)
        {
            var options = new List<string>
            {
                "-spirv",
                "-fspv-target-env=" + VulkanShaderBindingPolicy.TargetEnvironment,
                "-fvk-stage-io-order=decl",
                "-fvk-auto-shift-bindings",
                "-fvk-b-shift", VulkanShaderBindingPolicy.CbvBaseBinding.ToString(), "all",
                "-fvk-t-shift", VulkanShaderBindingPolicy.SrvBaseBinding.ToString(), "all",
                "-fvk-s-shift", VulkanShaderBindingPolicy.SamplerBaseBinding.ToString(), "all",
                "-fvk-u-shift", VulkanShaderBindingPolicy.UavBaseBinding.ToString(), "all",
            };

            if (useDirectXLayout)
            {
                options.Insert(2, "-fvk-use-dx-layout");
            }

            byte[] blob = CompileCore(
                expandedSource,
                entryPoint,
                profile,
                sourcePath,
                GpuShaderBinaryFormat.SpirV,
                options);

            return blob;
        }

        private static byte[] CompileCore(
            string expandedSource,
            string entryPoint,
            string profile,
            string sourcePath,
            GpuShaderBinaryFormat binaryFormat,
            IReadOnlyList<string> targetOptions)
        {
            if (expandedSource == null) throw new ArgumentNullException(nameof(expandedSource));
            if (string.IsNullOrWhiteSpace(entryPoint)) throw new ArgumentException("DXC entry point is required.", nameof(entryPoint));
            if (string.IsNullOrWhiteSpace(profile)) throw new ArgumentException("DXC target profile is required.", nameof(profile));

            string dxcPath = ResolveDxcPath();
            string workRoot = Path.Combine(
                Path.GetTempPath(),
                "GenesisRuntime",
                "Dxc",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workRoot);

            string fileName = string.IsNullOrWhiteSpace(sourcePath)
                ? "shader.hlsl"
                : Path.GetFileName(sourcePath);
            if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
                fileName += ".hlsl";

            string inputPath = Path.Combine(workRoot, fileName);
            string outputPath = Path.Combine(
                workRoot,
                binaryFormat == GpuShaderBinaryFormat.SpirV ? "shader.spv" : "shader.dxil");

            try
            {
                File.WriteAllText(inputPath, expandedSource, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var start = new ProcessStartInfo
                {
                    FileName = dxcPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workRoot,
                };

                start.ArgumentList.Add(inputPath);
                start.ArgumentList.Add("-E");
                start.ArgumentList.Add(entryPoint);
                start.ArgumentList.Add("-T");
                start.ArgumentList.Add(profile);
                start.ArgumentList.Add("-HV");
                start.ArgumentList.Add(HlslLanguageVersion);
                start.ArgumentList.Add("-" + Optimization);
                start.ArgumentList.Add("-Fo");
                start.ArgumentList.Add(outputPath);

                foreach (string option in targetOptions)
                    start.ArgumentList.Add(option);

                using Process process = Process.Start(start)
                    ?? throw new InvalidOperationException($"Failed to start DXC at '{dxcPath}'.");

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    throw new TimeoutException($"DXC did not finish compiling {fileName}:{entryPoint} within 120 seconds.");
                }
                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();

                if (process.ExitCode != 0 || !File.Exists(outputPath))
                {
                    string diagnostics = string.Join(
                        Environment.NewLine,
                        new[] { stderr, stdout }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (string.IsNullOrWhiteSpace(diagnostics))
                        diagnostics = "DXC produced no diagnostics.";

                    throw new InvalidOperationException(
                        $"DXC compile failed ({profile}/{entryPoint}, exit {process.ExitCode}) in {fileName}:"
                        + Environment.NewLine
                        + diagnostics.Trim());
                }

                byte[] blob = File.ReadAllBytes(outputPath);
                if (blob.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"DXC produced an empty {binaryFormat} shader ({profile}/{entryPoint}).");
                }

                return blob;
            }
            finally
            {
                TryDeleteDirectory(workRoot);
            }
        }

        internal static string ResolveDxcPathForDeployment(bool requireLocal = true) =>
            ResolveDxcPath(requireLocal);

        internal static bool IsLocalDeploymentPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                string baseDir = Path.GetFullPath(AppContext.BaseDirectory);
                string toolsDir = Path.GetFullPath(Path.Combine(baseDir, "Tools", "DXC"));
                string engineDir = Environment.GetEnvironmentVariable("GENESIS_ENGINE_DIR");
                string engineTools = string.IsNullOrWhiteSpace(engineDir) ? null : Path.GetFullPath(Path.Combine(engineDir, "Tools", "Dxc"));
                return (engineTools != null && full.StartsWith(engineTools + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    || full.StartsWith(toolsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetDirectoryName(full), baseDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static string ResolveDxcPath(bool requireLocal = false)
        {
            bool localOnly = requireLocal || ParseBooleanEnvironment("GENESIS_DXC_LOCAL_ONLY");

            lock (ResolveLock)
            {
                if (!string.IsNullOrWhiteSpace(_resolvedPath)
                    && File.Exists(_resolvedPath)
                    && (!localOnly || IsLocalDeploymentPath(_resolvedPath)))
                {
                    return _resolvedPath;
                }

                string overridePath = Environment.GetEnvironmentVariable("GENESIS_DXC_PATH");
                string resolvedOverride = ResolveCandidate(overridePath);
                if (resolvedOverride != null && (!localOnly || IsLocalDeploymentPath(resolvedOverride)))
                    return _resolvedPath = resolvedOverride;

                foreach (string candidate in LocalCandidates())
                {
                    string resolved = ResolveCandidate(candidate);
                    if (resolved != null)
                        return _resolvedPath = resolved;
                }

                if (localOnly)
                {
                    throw new FileNotFoundException(
                        "Genesis requires the bundled DirectX Shader Compiler for this run, but "
                        + "Tools\\DXC\\dxc.exe was not found beside the application. "
                        + "Rebuild/publish Genesis so the pinned compiler payload is staged.");
                }

                foreach (string packageRoot in NuGetPackageRoots())
                {
                    string versionRoot = Path.Combine(packageRoot, PackageId, PackageVersion);
                    if (!Directory.Exists(versionRoot)) continue;

                    string match = Directory
                        .EnumerateFiles(versionRoot, "dxc.exe", SearchOption.AllDirectories)
                        .OrderByDescending(IsX64CompilerPath)
                        .ThenBy(path => path.Length)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(match))
                        return _resolvedPath = Path.GetFullPath(match);
                }

                throw new FileNotFoundException(
                    "The pinned DirectX Shader Compiler could not be found. Restore the "
                    + $"Microsoft.Direct3D.DXC {PackageVersion} package or set GENESIS_DXC_PATH to dxc.exe.");
            }
        }

        private static bool ParseBooleanEnvironment(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "1", StringComparison.Ordinal)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> LocalCandidates()
        {
            string baseDir = AppContext.BaseDirectory;
            yield return Path.Combine(baseDir, "dxc.exe");
            yield return Path.Combine(baseDir, "Tools", "DXC", "dxc.exe");
            yield return Path.Combine(baseDir, "tools", "dxc", "dxc.exe");
        }

        private static IEnumerable<string> NuGetPackageRoots()
        {
            string overrideRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            if (!string.IsNullOrWhiteSpace(overrideRoot))
                yield return Path.GetFullPath(overrideRoot);

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                yield return Path.Combine(home, ".nuget", "packages");
        }

        private static string ResolveCandidate(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return null;
            try
            {
                string full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
                if (Directory.Exists(full))
                {
                    string exe = Path.Combine(full, "dxc.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }

            return null;
        }

        private static bool IsX64CompilerPath(string path)
        {
            string normalized = path.Replace('/', '\\');
            return normalized.Contains("\\x64\\", StringComparison.OrdinalIgnoreCase)
                && !normalized.Contains("\\arm64\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveDxcVersion(string path)
        {
            lock (ResolveLock)
            {
                if (!string.IsNullOrWhiteSpace(_resolvedVersion)) return _resolvedVersion;
                try
                {
                    string version = FileVersionInfo.GetVersionInfo(path).FileVersion;
                    if (!string.IsNullOrWhiteSpace(version))
                        return _resolvedVersion = version;
                }
                catch (FileNotFoundException)
                {
                }

                return _resolvedVersion = PackageVersion;
            }
        }

        private static bool IsDxilContainer(ReadOnlySpan<byte> blob)
        {
            // DXIL uses the DXBC container envelope but must contain a DXIL part. Checking only the
            // first four bytes would let legacy SM5 DXBC masquerade as a Direct3D 12 shader.
            if (blob.Length < 32
                || blob[0] != (byte)'D'
                || blob[1] != (byte)'X'
                || blob[2] != (byte)'B'
                || blob[3] != (byte)'C')
            {
                return false;
            }

            uint partCount = ReadUInt32(blob, 28);
            if (partCount > 4096 || blob.Length < 32 + checked((int)partCount * 4))
                return false;

            for (int i = 0; i < partCount; i++)
            {
                uint offset = ReadUInt32(blob, 32 + i * 4);
                if (offset > int.MaxValue || offset + 8u > blob.Length) continue;
                int p = (int)offset;
                if (blob[p] == (byte)'D'
                    && blob[p + 1] == (byte)'X'
                    && blob[p + 2] == (byte)'I'
                    && blob[p + 3] == (byte)'L')
                {
                    return true;
                }
            }

            return false;
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
            (uint)(data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24));

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
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
