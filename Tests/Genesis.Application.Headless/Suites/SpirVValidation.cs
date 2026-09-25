using System.Diagnostics;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Primitives;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Validates Genesis's SPIR-V with the Vulkan SDK's own validator rather than with Genesis's code.
/// </summary>
/// <remarks>
/// <para><b>Why an external tool.</b> SPIR-V is shared: the OpenGL backend compiles HLSL to SPIR-V
/// and translates it to GLSL, so a malformed module or a mistaken DXC flag breaks a shipping
/// backend. Checking that with assertions written against the same assumptions that produced the
/// module proves very little; <c>spirv-val</c> is an independent implementation of the
/// specification.</para>
///
/// <para><b>On absence.</b> The Vulkan SDK is a developer tool and will not be on every machine. A
/// missing validator is reported loudly and skipped rather than failing the build — but it is never
/// skipped silently, because a check that quietly does nothing is worse than no check at all.</para>
/// </remarks>
internal static class SpirVValidation
{
    /// <summary>Locates spirv-val, preferring the SDK the developer actually has configured.</summary>
    public static string? FindValidator()
    {
        string? sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (!string.IsNullOrWhiteSpace(sdk))
        {
            string fromEnvironment = Path.Combine(sdk, "Bin", "spirv-val.exe");
            if (File.Exists(fromEnvironment))
            {
                return fromEnvironment;
            }
        }

        // Newest first, so a machine with several SDKs validates against the most current rules.
        if (Directory.Exists(@"C:\VulkanSDK"))
        {
            foreach (string directory in Directory.GetDirectories(@"C:\VulkanSDK")
                         .OrderByDescending(static d => d, StringComparer.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(directory, "Bin", "spirv-val.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Compiles every built-in shader to SPIR-V and validates each module.</summary>
    /// <returns>One human-readable failure per invalid module; empty when all are well-formed.</returns>
    public static IReadOnlyList<string> ValidateEngineShaders(
        string validator, string workDirectory, out int moduleCount)
    {
        Directory.CreateDirectory(workDirectory);

        IReadOnlyList<ShaderCompileResult> modules = EngineShaderCatalog.CompileAll(
            GpuShaderBinaryFormat.SpirV, Path.Combine(workDirectory, "cache"));

        moduleCount = modules.Count;
        var failures = new List<string>();

        for (int i = 0; i < modules.Count; i++)
        {
            ShaderCompileResult module = modules[i];
            string path = Path.Combine(workDirectory, $"module-{i:00}-{module.EntryPoint}-{module.Profile}.spv");
            File.WriteAllBytes(path, module.Blob);

            (int exitCode, string output) = Run(validator, path);
            if (exitCode != 0)
            {
                failures.Add($"{Path.GetFileName(path)}: {output.Trim()}");
            }
        }

        return failures;
    }

    private static (int ExitCode, string Output) Run(string executable, string argument)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start '{executable}'.");

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
