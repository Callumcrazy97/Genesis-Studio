using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Primitives;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The engine's built-in shaders ship compiled in a read-only PrecompiledShaders folder beside
/// Studio and the Player. These cases prove a built-in program is read from there without a
/// compile, that a changed source or compiler misses it and compiles, and that a damaged or
/// missing file (or manifest) falls back to compiling instead of handing the driver bad bytes.
/// </summary>
internal static class ShaderPrecompiledSuite
{
    // Small built-in programs, so the cases take moments; the forward shader takes seconds in fxc.
    private static readonly EngineShaderJob[] Jobs =
    [
        new("SpriteShaders.hlsl", SpriteShaders.Source, "VS", GpuShaderStage.Vertex),
        new("SpriteShaders.hlsl", SpriteShaders.Source, "PS", GpuShaderStage.Pixel),
        new("BloomShaders.hlsl", BloomShaders.Source, "PS_Downsample", GpuShaderStage.Pixel),
    ];

    private static readonly GpuShaderBinaryFormat[] Formats = [GpuShaderBinaryFormat.Dxbc, GpuShaderBinaryFormat.SpirV];

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Render.Shaders.Precompiled.BuiltInReadWithoutCompiling", () =>
        {
            string shipped = Build(ctx, "served");
            Within(shipped, () =>
            {
                string cache = FreshFolder(ctx, "served-cache");
                foreach (GpuShaderBinaryFormat format in Formats)
                {
                    foreach (EngineShaderJob job in Jobs)
                    {
                        int compiles = ShaderCompiler.CompilesOnThisThread, reads = ShaderCompiler.PrecompiledReadsOnThisThread;
                        ShaderCompileResult result = EngineShaderCatalog.Compile(job, format, cache);
                        Check(result.CacheHit, $"{format} {job.Label}:{job.Entry} was not served from the precompiled folder.");
                        Check(ShaderCompiler.CompilesOnThisThread == compiles, $"{format} {job.Label}:{job.Entry} compiled although it was precompiled.");
                        Check(ShaderCompiler.PrecompiledReadsOnThisThread == reads + 1, $"{format} {job.Label}:{job.Entry} was not read from the precompiled folder.");
                        Check(result.Blob.AsSpan().SequenceEqual(File.ReadAllBytes(ShippedFile(shipped, job, format))),
                            $"{format} {job.Label}:{job.Entry} differs from the shipped file.");
                    }
                }
                // Read from the package, not copied into the user's cache: nothing was written there.
                Check(!Directory.Exists(cache) || Directory.GetFiles(cache, "*", SearchOption.AllDirectories).Length == 0,
                    "A precompiled program was copied into the runtime cache.");
            });

            // A rebuild with unchanged shaders keeps every file instead of compiling again.
            PrecompiledShaderBuildResult again = PrecompiledShaders.Build(shipped, Formats, jobs: Jobs);
            Check(again.Success && again.Compiled == 0 && again.Reused == again.Files && again.Files == Jobs.Length * Formats.Length,
                $"Rebuilding unchanged shaders compiled {again.Compiled} and kept {again.Reused} of {again.Files}.");
            File.WriteAllLines(Path.Combine(ctx.Logs, "shader-precompiled-manifest.txt"), File.ReadAllLines(Path.Combine(shipped, "manifest.txt")));
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Shaders.Precompiled.ChangedSourceOrCompilerCompiles", () =>
        {
            string shipped = Build(ctx, "changed");
            Within(shipped, () =>
            {
                string cache = FreshFolder(ctx, "changed-cache");
                EngineShaderJob job = Jobs[1];
                EngineShaderJob edited = job with { Source = job.Source + "\n// edited after the engine was packaged\n" };
                int compiles = ShaderCompiler.CompilesOnThisThread;
                ShaderCompileResult result = EngineShaderCatalog.Compile(edited, GpuShaderBinaryFormat.Dxbc, cache);
                Check(!result.CacheHit && ShaderCompiler.CompilesOnThisThread == compiles + 1,
                    "A changed built-in source was served from the precompiled folder instead of compiling.");

                ShaderCompiler.CompilerIdentitySuffix = "|another-compiler-version";
                try
                {
                    compiles = ShaderCompiler.CompilesOnThisThread;
                    result = EngineShaderCatalog.Compile(job, GpuShaderBinaryFormat.Dxbc, cache);
                    Check(!result.CacheHit && ShaderCompiler.CompilesOnThisThread == compiles + 1,
                        "A different compiler version was served the precompiled program instead of compiling.");
                }
                finally
                {
                    ShaderCompiler.CompilerIdentitySuffix = string.Empty;
                }

                // The unchanged program from the same compiler is still a precompiled read.
                compiles = ShaderCompiler.CompilesOnThisThread;
                result = EngineShaderCatalog.Compile(job, GpuShaderBinaryFormat.SpirV, cache);
                Check(result.CacheHit && ShaderCompiler.CompilesOnThisThread == compiles, "The unchanged program compiled.");
            });
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Shaders.Precompiled.DamagedOrMissingFallsBackToCompiling", () =>
        {
            string shipped = Build(ctx, "damaged");
            Within(shipped, () =>
            {
                string cache = FreshFolder(ctx, "damaged-cache");

                // Damaged bytes (same length, so only the hash can tell).
                string damaged = ShippedFile(shipped, Jobs[1], GpuShaderBinaryFormat.Dxbc);
                byte[] good = File.ReadAllBytes(damaged);
                byte[] bad = (byte[])good.Clone();
                for (int i = bad.Length / 2; i < bad.Length / 2 + 16 && i < bad.Length; i++) bad[i] ^= 0x5A;
                File.WriteAllBytes(damaged, bad);
                int compiles = ShaderCompiler.CompilesOnThisThread;
                ShaderCompileResult result = EngineShaderCatalog.Compile(Jobs[1], GpuShaderBinaryFormat.Dxbc, cache);
                Check(!result.CacheHit && ShaderCompiler.CompilesOnThisThread == compiles + 1, "A damaged precompiled file did not fall back to compiling.");
                Check(result.Blob.AsSpan().SequenceEqual(good), "Compiling after a damaged precompiled file gave a different program.");

                // A truncated file.
                string truncated = ShippedFile(shipped, Jobs[0], GpuShaderBinaryFormat.SpirV);
                File.WriteAllBytes(truncated, File.ReadAllBytes(truncated)[..16]);
                compiles = ShaderCompiler.CompilesOnThisThread;
                result = EngineShaderCatalog.Compile(Jobs[0], GpuShaderBinaryFormat.SpirV, cache);
                Check(!result.CacheHit && ShaderCompiler.CompilesOnThisThread == compiles + 1 && result.Blob.Length > 16,
                    "A truncated precompiled file did not fall back to compiling.");

                // A missing file.
                File.Delete(ShippedFile(shipped, Jobs[2], GpuShaderBinaryFormat.Dxbc));
                compiles = ShaderCompiler.CompilesOnThisThread;
                result = EngineShaderCatalog.Compile(Jobs[2], GpuShaderBinaryFormat.Dxbc, cache);
                Check(!result.CacheHit && ShaderCompiler.CompilesOnThisThread == compiles + 1, "A missing precompiled file did not fall back to compiling.");

                // No manifest: the folder is not used at all, even for intact files.
                File.Delete(Path.Combine(shipped, "manifest.txt"));
                ShaderBinaryCache.ForgetPrecompiledIndexes();
                compiles = ShaderCompiler.CompilesOnThisThread;
                result = EngineShaderCatalog.Compile(Jobs[2], GpuShaderBinaryFormat.SpirV, cache);
                Check(!result.CacheHit && ShaderCompiler.CompilesOnThisThread == compiles + 1, "A folder without its manifest was still read.");
            });
        });

        HeadlessHarness.RunCase(ctx.Report, "Render.Shaders.Precompiled.CatalogCoversEngineStartupPrograms", () =>
        {
            // The renderer's own start-up programs, the GPU particle kernels and the tiled terrain
            // programs are all in what ships; a program left out would compile after an update.
            foreach (EngineShaderJob job in new EngineShaderJob[]
            {
                new("Forward", ForwardShaders.Source, "PS", GpuShaderStage.Pixel),
                new("Sprite", SpriteShaders.Source, "PS", GpuShaderStage.Pixel),
                new("TerrainImages", TerrainSurfaceShaders.Source, "PS", GpuShaderStage.Pixel),
                new("TerrainLayerAtlas", TerrainSurfaceShaders.LayerAtlasSource, "PS", GpuShaderStage.Pixel),
                new("ParticleDraw", Genesis.Rendering.Particles.GpuParticleShaders.Draw, "VS", GpuShaderStage.Vertex),
                new("PostEffectVertex", ShaderPreviewFullscreenShaders.Source, "PreviewVS", GpuShaderStage.Vertex),
            }.Concat(Genesis.Rendering.Particles.GpuParticleShaders.ComputeEntries.Select(entry =>
                new EngineShaderJob("ParticleCompute", Genesis.Rendering.Particles.GpuParticleShaders.Compute, entry, GpuShaderStage.Compute))))
            {
                Check(EngineShaderCatalog.PrecompiledJobs.Any(shipped => string.Equals(shipped.Source, job.Source, StringComparison.Ordinal)
                        && shipped.Entry == job.Entry && shipped.Stage == job.Stage),
                    $"{job.Label}:{job.Entry} does not ship precompiled.");
            }
            Check(PrecompiledShaders.AllFormats.Count == 4, "The precompiled folder no longer covers DX11, DX12, Vulkan and OpenGL.");
            ShaderBinaryCache.PrecompiledRootOverride = string.Empty;
            string expected = Path.Combine(AppContext.BaseDirectory, PrecompiledShaders.FolderName);
            Check(string.Equals(ShaderBinaryCache.ResolvePrecompiledRoot(), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)
                    || Environment.GetEnvironmentVariable("GENESIS_PRECOMPILED_SHADERS") != null,
                "The precompiled folder is not looked for beside the running program.");
        });
    }

    private static string Build(HeadlessContext ctx, string name)
    {
        string shipped = FreshFolder(ctx, name);
        PrecompiledShaderBuildResult result = PrecompiledShaders.Build(shipped, Formats, jobs: Jobs);
        Check(result.Success, "Building the precompiled folder failed: " + string.Join("; ", result.Failures));
        Check(result.Files == Jobs.Length * Formats.Length && File.Exists(Path.Combine(shipped, "manifest.txt")),
            $"The precompiled folder holds {result.Files} programs, expected {Jobs.Length * Formats.Length}.");
        return shipped;
    }

    private static void Within(string shipped, Action action)
    {
        ShaderBinaryCache.PrecompiledRootOverride = shipped;
        ShaderBinaryCache.ForgetPrecompiledIndexes();
        try { action(); }
        finally
        {
            ShaderBinaryCache.PrecompiledRootOverride = string.Empty;
            ShaderBinaryCache.ForgetPrecompiledIndexes();
        }
    }

    private static string ShippedFile(string shipped, EngineShaderJob job, GpuShaderBinaryFormat format)
    {
        string key = ShaderCompiler.CacheKey(job.Source, job.Entry, job.Stage, format, job.Label);
        return Path.Combine(shipped, ShaderBinaryCache.RelativePath(key, format).Replace('/', Path.DirectorySeparatorChar));
    }

    private static string FreshFolder(HeadlessContext ctx, string name)
    {
        // Short: under the run's workspace, with a unique tail so a rerun never reads old files.
        string path = Path.Combine(ctx.Workspace, "sp", name + "-" + Guid.NewGuid().ToString("N")[..6]);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return path;
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
