using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Single HLSL compilation entry point for every Genesis renderer backend.
    /// </summary>
    /// <remarks>
    /// Direct3D 11 currently compiles through D3DCompiler to DXBC. The target-aware API is already
    /// explicit about DXIL, SPIR-V, and GLSL so Direct3D 12, Vulkan, and OpenGL can be introduced
    /// without changing the shared renderers again. Every successful result is content-addressed
    /// on disk; include contents are expanded before hashing, so editing an include invalidates the
    /// cache automatically.
    /// </remarks>
    public static unsafe class ShaderCompiler
    {
        private const string DxbcCompilerIdentity = "d3dcompiler_47|flags=0|v1";

        private static readonly Regex IncludeRegex = new(
            @"^\s*#include\s+""([^""]+)""",
            RegexOptions.Compiled | RegexOptions.Multiline);

        [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile")]
        private static extern int D3DCompileNative(
            void*  pSrcData,
            nuint  SrcDataSize,
            void*  pSourceName,
            void*  pDefines,
            void*  pInclude,
            void*  pEntrypoint,
            void*  pTarget,
            uint   Flags1,
            uint   Flags2,
            void** ppCode,
            void** ppErrorMsgs);

        private static byte* BlobPtr(void* blob)
            => (byte*)((delegate* unmanaged[Stdcall]<void*, void*>)(*(void***)blob)[3])(blob);

        private static nuint BlobSize(void* blob)
            => ((delegate* unmanaged[Stdcall]<void*, nuint>)(*(void***)blob)[4])(blob);

        private static void BlobRelease(void* blob)
            => ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)blob)[2])(blob);

        /// <summary>
        /// Legacy profile-based entry point retained for the existing DX11 Shader Editor and direct
        /// DX11 preview plumbing. It now benefits from the same on-disk cache as backend-aware calls.
        /// </summary>
        public static byte[] Compile(
            string source,
            string entry,
            string profile,
            string sourcePath = null,
            IEnumerable<string> includeSearchPaths = null)
        {
            return CompileWithProfile(
                source,
                entry,
                profile,
                GpuShaderBinaryFormat.Dxbc,
                sourcePath,
                includeSearchPaths,
                null).Blob;
        }

        /// <summary>
        /// Compiles one HLSL stage into the representation consumed by <paramref name="binaryFormat"/>.
        /// </summary>
        /// <remarks>
        /// DXBC remains the Direct3D 11 reference path. DXIL and SPIR-V are compiled by the pinned
        /// DXC toolchain; GLSL remains a later translation stage. A backend can therefore never
        /// receive DXBC merely because it was the old default.
        /// </remarks>
        public static ShaderCompileResult CompileForBackend(
            string source,
            string entry,
            GpuShaderStage stage,
            GpuShaderBinaryFormat binaryFormat,
            string sourcePath = null,
            IEnumerable<string> includeSearchPaths = null,
            string cacheRoot = null)
        {
            string profile = GetProfile(stage, binaryFormat);
            return CompileWithProfile(
                source,
                entry,
                profile,
                binaryFormat,
                sourcePath,
                includeSearchPaths,
                cacheRoot);
        }

        /// <summary>Returns the HLSL profile used as the source-side contract for a target.</summary>
        public static string GetProfile(GpuShaderStage stage, GpuShaderBinaryFormat binaryFormat)
        {
            if (stage != GpuShaderStage.Vertex
                && stage != GpuShaderStage.Pixel
                && stage != GpuShaderStage.Compute)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stage), stage, "A compile request must name exactly one shader stage.");
            }

            bool shaderModel6 = binaryFormat == GpuShaderBinaryFormat.Dxil
                || binaryFormat == GpuShaderBinaryFormat.SpirV
                || binaryFormat == GpuShaderBinaryFormat.GlslUtf8;
            if (binaryFormat != GpuShaderBinaryFormat.Dxbc && !shaderModel6)
                throw new ArgumentOutOfRangeException(nameof(binaryFormat), binaryFormat, null);

            string suffix = shaderModel6 ? "6_0" : "5_0";
            return stage switch
            {
                GpuShaderStage.Vertex => "vs_" + suffix,
                GpuShaderStage.Pixel => "ps_" + suffix,
                GpuShaderStage.Compute => "cs_" + suffix,
                _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null),
            };
        }

        /// <summary>Tries common pixel-shader entry points (MainPS, main, PSMain).</summary>
        public static (byte[] Blob, string EntryPoint) CompilePixelShader(
            string source,
            string sourcePath = null,
            IEnumerable<string> includeSearchPaths = null)
        {
            ReadOnlySpan<string> entries = ["MainPS", "main", "PSMain"];
            Exception last = null;
            foreach (string entry in entries)
            {
                try
                {
                    return (Compile(source, entry, "ps_5_0", sourcePath, includeSearchPaths), entry);
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            throw last ?? new Exception(
                "HLSL compile failed: no entry point found (expected MainPS, main, or PSMain).");
        }

        public static IReadOnlyList<string> BuildDefaultIncludeSearchPaths(
            string sourcePath,
            string projectPath)
        {
            var roots = new List<string>();

            if (!string.IsNullOrEmpty(sourcePath))
            {
                string dir = Path.GetDirectoryName(sourcePath);
                if (!string.IsNullOrEmpty(dir))
                    roots.Add(dir);
            }

            if (!string.IsNullOrEmpty(projectPath))
            {
                string projectShaders = Path.Combine(projectPath, "Shaders");
                if (Directory.Exists(projectShaders))
                    roots.Add(projectShaders);
            }

            string engineShaders = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Shaders");
            if (Directory.Exists(engineShaders))
                roots.Add(engineShaders);

            return roots;
        }

        private static ShaderCompileResult CompileWithProfile(
            string source,
            string entry,
            string profile,
            GpuShaderBinaryFormat binaryFormat,
            string sourcePath,
            IEnumerable<string> includeSearchPaths,
            string cacheRoot)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(entry)) throw new ArgumentException("Shader entry point is required.", nameof(entry));
            if (string.IsNullOrWhiteSpace(profile)) throw new ArgumentException("Shader profile is required.", nameof(profile));

            string expanded = ExpandIncludes(source, sourcePath, includeSearchPaths);
            string compilerIdentity = CompilerIdentity(binaryFormat);
            string root = ShaderBinaryCache.ResolveRoot(cacheRoot);
            string key = ShaderBinaryCache.BuildKey(
                expanded, entry, profile, binaryFormat, compilerIdentity);

            if (ShaderBinaryCache.TryRead(root, key, binaryFormat, out byte[] cached))
            {
                return new ShaderCompileResult(
                    cached, entry, profile, binaryFormat, cacheHit: true);
            }

            byte[] blob = binaryFormat switch
            {
                GpuShaderBinaryFormat.Dxbc => CompileDxbcExpanded(expanded, entry, profile, sourcePath),
                GpuShaderBinaryFormat.Dxil => DxcToolchain.CompileDxil(expanded, entry, profile, sourcePath),
                GpuShaderBinaryFormat.SpirV => DxcToolchain.CompileSpirV(expanded, entry, profile, sourcePath),
                GpuShaderBinaryFormat.GlslUtf8 => CompileGlsl(expanded, entry, profile, sourcePath),
                _ => throw new ArgumentOutOfRangeException(nameof(binaryFormat), binaryFormat, null),
            };

            ShaderBinaryCache.TryWrite(root, key, binaryFormat, blob);
            return new ShaderCompileResult(blob, entry, profile, binaryFormat, cacheHit: false);
        }

        private static string CompilerIdentity(GpuShaderBinaryFormat binaryFormat) => binaryFormat switch
        {
            GpuShaderBinaryFormat.Dxbc => DxbcCompilerIdentity,
            GpuShaderBinaryFormat.Dxil => DxcToolchain.CompilerIdentity(GpuShaderBinaryFormat.Dxil),
            GpuShaderBinaryFormat.SpirV => DxcToolchain.CompilerIdentity(GpuShaderBinaryFormat.SpirV),

            // Both stages participate: GLSL is derived from SPIR-V, so a DXC change invalidates the
            // GLSL cache just as surely as a SPIRV-Cross change does.
            GpuShaderBinaryFormat.GlslUtf8 =>
                DxcToolchain.CompilerIdentity(GpuShaderBinaryFormat.SpirV)
                + "|std-layout|" + SpirvCrossToolchain.CompilerIdentity(GlslTargetVersion),
            _ => throw new ArgumentOutOfRangeException(nameof(binaryFormat), binaryFormat, null),
        };

        private static GpuShaderStage StageFromProfile(string profile)
        {
            if (profile.StartsWith("vs_", StringComparison.OrdinalIgnoreCase)) return GpuShaderStage.Vertex;
            if (profile.StartsWith("ps_", StringComparison.OrdinalIgnoreCase)) return GpuShaderStage.Pixel;
            if (profile.StartsWith("cs_", StringComparison.OrdinalIgnoreCase)) return GpuShaderStage.Compute;
            throw new ArgumentException($"Unsupported shader profile '{profile}'.", nameof(profile));
        }

        /// <summary>
        /// GLSL version emitted for the OpenGL backend, matching the 4.6 core context it requests.
        /// </summary>
        private const uint GlslTargetVersion = 460;

        /// <summary>Produces GLSL by compiling to SPIR-V first and translating it.</summary>
        /// <remarks>
        /// The intermediate module is <i>not</i> byte-identical to the one Vulkan consumes: it is
        /// compiled with standard block layout instead of Direct3D packing, because desktop GLSL
        /// cannot express DX-packed blocks at all. Same HLSL and same compiler, one deliberate
        /// difference in layout rules.
        /// </remarks>
        private static byte[] CompileGlsl(
            string expanded, string entry, string profile, string sourcePath)
        {
            byte[] spirv = DxcToolchain.CompileSpirV(
                expanded, entry, profile, sourcePath, useDirectXLayout: false);
            return SpirvCrossToolchain.TranspileToGlsl(spirv, GlslTargetVersion);
        }

        private static byte[] CompileDxbcExpanded(
            string expanded,
            string entry,
            string profile,
            string sourcePath)
        {
            byte[] srcBytes = Encoding.UTF8.GetBytes(expanded);
            byte[] entryBytes = Encoding.ASCII.GetBytes(entry + "\0");
            byte[] targetBytes = Encoding.ASCII.GetBytes(profile + "\0");
            byte[] nameBytes = sourcePath != null
                ? Encoding.UTF8.GetBytes(sourcePath + "\0")
                : null;

            void* pCode = null;
            void* pErrors = null;
            int hr;

            fixed (byte* pSrc = srcBytes, pEntry = entryBytes, pTarget = targetBytes)
            fixed (byte* pName = nameBytes)
            {
                hr = D3DCompileNative(
                    pSrc,
                    (nuint)srcBytes.Length,
                    pName,
                    null,
                    null,
                    pEntry,
                    pTarget,
                    0,
                    0,
                    &pCode,
                    &pErrors);
            }

            string errorText = null;
            if (pErrors != null)
            {
                errorText = Encoding.UTF8.GetString(BlobPtr(pErrors), (int)BlobSize(pErrors));
                BlobRelease(pErrors);
            }

            if (hr < 0)
            {
                throw new Exception(
                    $"HLSL compile failed ({profile}/{entry}): {errorText ?? "unknown error"} "
                    + $"(HRESULT 0x{hr:X8})");
            }

            if (pCode == null)
                throw new Exception($"HLSL compile returned no bytecode ({profile}/{entry}).");

            byte* codePtr = BlobPtr(pCode);
            nuint codeSize = BlobSize(pCode);
            byte[] result = new byte[(int)codeSize];
            new Span<byte>(codePtr, (int)codeSize).CopyTo(result);
            BlobRelease(pCode);
            return result;
        }

        private static string ExpandIncludes(
            string source,
            string sourcePath,
            IEnumerable<string> includeSearchPaths)
        {
            if (string.IsNullOrEmpty(source)
                || !source.Contains("#include", StringComparison.Ordinal))
            {
                return source;
            }

            string sourceDir = !string.IsNullOrEmpty(sourcePath)
                ? Path.GetDirectoryName(sourcePath)
                : null;

            var roots = new List<string>();
            if (!string.IsNullOrEmpty(sourceDir))
                roots.Add(sourceDir);
            if (includeSearchPaths != null)
            {
                foreach (string path in includeSearchPaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    bool exists = false;
                    foreach (string root in roots)
                    {
                        if (string.Equals(root, path, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists) roots.Add(path);
                }
            }

            var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(sourcePath))
                stack.Add(Path.GetFullPath(sourcePath));

            return ExpandIncludesCore(source, sourcePath ?? "shader", roots, stack);
        }

        private static string ExpandIncludesCore(
            string source,
            string logicalPath,
            List<string> roots,
            HashSet<string> stack)
        {
            var sb = new StringBuilder(source.Length + 256);
            int lineNum = 1;
            foreach (string rawLine in source.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine;
                Match match = IncludeRegex.Match(line);
                if (match.Success)
                {
                    string includeName = match.Groups[1].Value;
                    string resolved = ResolveInclude(includeName, roots);
                    if (resolved == null)
                    {
                        throw new Exception(
                            $"Cannot open include file '{includeName}' (while compiling '{logicalPath}')");
                    }

                    string full = Path.GetFullPath(resolved);
                    if (!stack.Add(full))
                    {
                        throw new Exception(
                            $"Circular #include detected: '{includeName}' in '{logicalPath}'");
                    }

                    string included = File.ReadAllText(resolved);
                    sb.AppendLine($"#line 1 \"{resolved.Replace('\\', '/')}\"");
                    sb.Append(ExpandIncludesCore(included, resolved, roots, stack));
                    stack.Remove(full);
                    sb.AppendLine();
                    sb.AppendLine($"#line {lineNum + 1} \"{logicalPath.Replace('\\', '/')}\"");
                }
                else
                {
                    sb.AppendLine(line);
                }
                lineNum++;
            }
            return sb.ToString();
        }

        private static string ResolveInclude(string includeName, List<string> roots)
        {
            foreach (string root in roots)
            {
                string candidate = Path.Combine(root, includeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    }
}
