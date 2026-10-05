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

            // Every 3D viewport (each editor, each game window) asks for the same few dozen
            // built-in programs. They are kept in memory once made, and a program another thread
            // is already compiling (the start-up warm-up) is waited for rather than compiled twice.
            string memoryKey = root + "|" + key;
            if (Compiled.TryGetValue(memoryKey, out byte[] remembered))
                return new ShaderCompileResult(remembered, entry, profile, binaryFormat, cacheHit: true);

            Lazy<(byte[] Blob, bool CacheHit)> work = Compiling.GetOrAdd(memoryKey, _ => new Lazy<(byte[] Blob, bool CacheHit)>(
                () => ReadOrCompile(root, key, expanded, entry, profile, binaryFormat, sourcePath),
                System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                (byte[] blob, bool cacheHit) = work.Value;
                // A shader being written in the Shader editor is a new program on every edit; the
                // memory kept is bounded, and the disk cache still holds everything.
                if (Compiled.Count >= 1024) Compiled.Clear();
                Compiled.TryAdd(memoryKey, blob);
                return new ShaderCompileResult(blob, entry, profile, binaryFormat, cacheHit);
            }
            finally
            {
                // A failed compile is not remembered: the next request reports it again.
                Compiling.TryRemove(new KeyValuePair<string, Lazy<(byte[] Blob, bool CacheHit)>>(memoryKey, work));
            }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Compiled =
            new(StringComparer.Ordinal);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(byte[] Blob, bool CacheHit)>> Compiling =
            new(StringComparer.Ordinal);

        private static (byte[] Blob, bool CacheHit) ReadOrCompile(
            string root, string key, string expanded, string entry, string profile,
            GpuShaderBinaryFormat binaryFormat, string sourcePath)
        {
            // The engine's own shaders ship compiled beside the program (same key), so a new
            // engine version starts without compiling them; a project's shaders never match there.
            if (ShaderBinaryCache.TryReadPrecompiled(key, binaryFormat, out byte[] shipped))
            {
                System.Threading.Interlocked.Increment(ref _precompiledReads);
                _threadPrecompiledReads++;
                return (shipped, true);
            }

            if (ShaderBinaryCache.TryRead(root, key, binaryFormat, out byte[] cached))
                return (cached, true);

            byte[] blob = CompileExpanded(expanded, entry, profile, binaryFormat, sourcePath);
            ShaderBinaryCache.TryWrite(root, key, binaryFormat, blob);
            return (blob, false);
        }

        private static byte[] CompileExpanded(
            string expanded, string entry, string profile, GpuShaderBinaryFormat binaryFormat, string sourcePath)
        {
            System.Threading.Interlocked.Increment(ref _compiles);
            _threadCompiles++;
            return binaryFormat switch
            {
                GpuShaderBinaryFormat.Dxbc => CompileDxbcExpanded(expanded, entry, profile, sourcePath),
                GpuShaderBinaryFormat.Dxil => DxcToolchain.CompileDxil(expanded, entry, profile, sourcePath),
                GpuShaderBinaryFormat.SpirV => DxcToolchain.CompileSpirV(expanded, entry, profile, sourcePath),
                GpuShaderBinaryFormat.GlslUtf8 => CompileGlsl(expanded, entry, profile, sourcePath),
                _ => throw new ArgumentOutOfRangeException(nameof(binaryFormat), binaryFormat, null),
            };
        }

        private static long _compiles;
        private static long _precompiledReads;

        /// <summary>Shaders this process has actually compiled (not read from any cache).</summary>
        public static long CompilesPerformed => System.Threading.Interlocked.Read(ref _compiles);

        /// <summary>Shaders this process has read from the precompiled folder shipped with it.</summary>
        public static long PrecompiledReads => System.Threading.Interlocked.Read(ref _precompiledReads);

        // Per thread, for tests that count what one request did while warm-up threads run.
        [ThreadStatic] private static int _threadCompiles;
        [ThreadStatic] private static int _threadPrecompiledReads;
        internal static int CompilesOnThisThread => _threadCompiles;
        internal static int PrecompiledReadsOnThisThread => _threadPrecompiledReads;

        /// <summary>
        /// Appended to the compiler identity of compiles requested on this thread. Tests use it to
        /// stand for a different compiler version; it is null otherwise.
        /// </summary>
        [ThreadStatic] internal static string CompilerIdentitySuffix;

        /// <summary>
        /// Compiles one stage with no cache at all, returning the key the runtime cache would file
        /// it under. Used to build the precompiled folder, which must never inherit a damaged or
        /// stale entry from this machine's own cache.
        /// </summary>
        internal static (string Key, byte[] Blob) CompileForPrecompiledFolder(
            string source, string entry, GpuShaderStage stage, GpuShaderBinaryFormat binaryFormat, string sourcePath)
        {
            string profile = GetProfile(stage, binaryFormat);
            string expanded = ExpandIncludes(source, sourcePath, null);
            string key = ShaderBinaryCache.BuildKey(expanded, entry, profile, binaryFormat, CompilerIdentity(binaryFormat));
            return (key, CompileExpanded(expanded, entry, profile, binaryFormat, sourcePath));
        }

        /// <summary>The cache key of one stage, exactly as <see cref="CompileForBackend"/> files it.</summary>
        internal static string CacheKey(string source, string entry, GpuShaderStage stage, GpuShaderBinaryFormat binaryFormat, string sourcePath = null)
        {
            string profile = GetProfile(stage, binaryFormat);
            return ShaderBinaryCache.BuildKey(
                ExpandIncludes(source, sourcePath, null), entry, profile, binaryFormat, CompilerIdentity(binaryFormat));
        }

        /// <summary>
        /// Starts compiling every built-in Direct3D 11 program on worker threads, so a game window
        /// that is about to open finds them made (or being made) instead of compiling them one
        /// after another while its window stays blank.
        /// </summary>
        public static void WarmBuiltInDxbcInBackground() => WarmBuiltInInBackground(GpuShaderBinaryFormat.Dxbc);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<GpuShaderBinaryFormat, bool> Warming = new();

        /// <summary>
        /// Starts making every built-in program for one backend's format on worker threads (once
        /// per process and format). The renderer then finds each one made, or waits for the
        /// worker already making it, instead of making them one after another. With the
        /// precompiled folder present this only reads files; without it (an engine built without
        /// that step) the independent programs compile side by side. DXC runs as one process per
        /// compile, so the DXIL, SPIR-V and GLSL formats compile in parallel as well as DXBC.
        /// </summary>
        public static void WarmBuiltInInBackground(GpuShaderBinaryFormat binaryFormat)
        {
            if (!Warming.TryAdd(binaryFormat, true)) return;

            // The largest programs first, so the longest compile starts at once. Dedicated
            // threads, not the thread pool: start-up work that waits on a pool thread must not
            // queue behind seconds of compiling.
            IReadOnlyList<EngineShaderJob> catalog = EngineShaderCatalog.PrecompiledJobs;
            EngineShaderJob[] jobs = new EngineShaderJob[catalog.Count];
            for (int i = 0; i < jobs.Length; i++) jobs[i] = catalog[i];
            Array.Sort(jobs, static (a, b) => b.Source.Length.CompareTo(a.Source.Length));
            int next = -1;
            int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            for (int w = 0; w < workers; w++)
            {
                var thread = new System.Threading.Thread(() =>
                {
                    for (int index = System.Threading.Interlocked.Increment(ref next); index < jobs.Length;
                         index = System.Threading.Interlocked.Increment(ref next))
                    {
                        try { EngineShaderCatalog.Compile(jobs[index], binaryFormat); }
                        catch (Exception) { /* The renderer compiles it again and reports the error itself. */ }
                    }
                })
                {
                    // Normal priority on purpose: the renderer waits for a program these threads
                    // are already making, and a lower priority would make it wait behind every
                    // other busy program on the machine.
                    IsBackground = true,
                    Name = "Genesis shader warm-up",
                };
                thread.Start();
            }
        }

        private static string CompilerIdentity(GpuShaderBinaryFormat binaryFormat) =>
            BaseCompilerIdentity(binaryFormat) + (CompilerIdentitySuffix ?? string.Empty);

        private static string BaseCompilerIdentity(GpuShaderBinaryFormat binaryFormat) => binaryFormat switch
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
        /// GLSL baseline shared by every supported OpenGL context, including the 4.5 fallback.
        /// The target participates in the compiler identity, so cached 4.6 binaries cannot leak in.
        /// </summary>
        private const uint GlslTargetVersion = 450;

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
