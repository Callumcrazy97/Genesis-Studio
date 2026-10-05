using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Primitives
{
    /// <summary>What <see cref="PrecompiledShaders.Build"/> did.</summary>
    public sealed record PrecompiledShaderBuildResult(
        int Compiled,
        int Reused,
        int Files,
        long Bytes,
        IReadOnlyList<string> Failures,
        IReadOnlyList<string> Timings,
        TimeSpan Elapsed)
    {
        public bool Success => Failures.Count == 0;
    }

    /// <summary>
    /// The engine's built-in shaders, compiled when Studio and the Player are packaged and shipped
    /// beside them in a read-only <c>PrecompiledShaders</c> folder. After an engine update the
    /// runtime cache holds nothing for the new shaders; without this folder the first game (and the
    /// first 3D viewport) compiled every built-in program before it could draw at all.
    /// </summary>
    /// <remarks>
    /// Entries use the runtime cache's keys (expanded source, entry point, profile, binary format
    /// and compiler identity), so a changed shader or a different compiler simply misses and is
    /// compiled as before. Each file's length and SHA-256 are in <c>manifest.txt</c>; a damaged file
    /// is a miss, never a bad program handed to the graphics driver. Project shaders are not here:
    /// they compile at run time and are cached per user as before.
    /// </remarks>
    public static class PrecompiledShaders
    {
        public const string FolderName = ShaderBinaryCache.PrecompiledFolderName;
        public const string CommandLineSwitch = "--precompile-shaders";

        /// <summary>Every format a shipped backend consumes: DX11, DX12, Vulkan and OpenGL.</summary>
        public static IReadOnlyList<GpuShaderBinaryFormat> AllFormats { get; } =
        [
            GpuShaderBinaryFormat.Dxbc,
            GpuShaderBinaryFormat.Dxil,
            GpuShaderBinaryFormat.SpirV,
            GpuShaderBinaryFormat.GlslUtf8,
        ];

        /// <summary>
        /// Compiles every <see cref="EngineShaderCatalog.PrecompiledJobs"/> entry for each format into
        /// <paramref name="outputDirectory"/>, replacing what is there. Entries already present with
        /// the same key and an intact file are kept rather than compiled again, so a rebuild with
        /// unchanged shaders takes moments. The folder is replaced only when everything compiled.
        /// </summary>
        public static PrecompiledShaderBuildResult Build(
            string outputDirectory,
            IEnumerable<GpuShaderBinaryFormat> formats = null,
            Action<string> log = null,
            int maxParallelism = 0,
            IEnumerable<EngineShaderJob> jobs = null,
            IEnumerable<string> reuseFrom = null)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("An output folder is required.", nameof(outputDirectory));

            Stopwatch total = Stopwatch.StartNew();
            string output = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            GpuShaderBinaryFormat[] targets = (formats ?? AllFormats).Distinct().ToArray();
            // Earlier builds of this folder (and, for a new package, the last one's) whose programs
            // are kept when the key and the file's hash still match.
            var previous = new List<(string Root, ShaderBinaryCache.PrecompiledIndex Index)> { (output, ShaderBinaryCache.PrecompiledIndex.Load(output)) };
            foreach (string folder in reuseFrom ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                previous.Add((root, ShaderBinaryCache.PrecompiledIndex.Load(root)));
            }
            string staging = output + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.CreateDirectory(staging);

            // Longest sources first, so the slowest programs (the forward shader) start at once.
            var work = new List<(EngineShaderJob Job, GpuShaderBinaryFormat Format)>();
            EngineShaderJob[] programs = (jobs ?? EngineShaderCatalog.PrecompiledJobs).ToArray();
            foreach (GpuShaderBinaryFormat format in targets)
                foreach (EngineShaderJob job in programs)
                    work.Add((job, format));
            work.Sort((a, b) => b.Job.Source.Length.CompareTo(a.Job.Source.Length));

            var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var failures = new List<string>();
            var timings = new List<string>();
            var gate = new object();
            int compiled = 0, reused = 0;
            long bytes = 0;
            int parallelism = maxParallelism > 0 ? maxParallelism : Math.Clamp(Environment.ProcessorCount, 1, 8);

            try
            {
                Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, item =>
                {
                    string name = $"{item.Format} {item.Job.Label}:{item.Job.Entry}";
                    try
                    {
                        string key = ShaderCompiler.CacheKey(item.Job.Source, item.Job.Entry, item.Job.Stage, item.Format, item.Job.Label);
                        string relative = ShaderBinaryCache.RelativePath(key, item.Format);
                        lock (gate)
                        {
                            // Two catalog jobs can be the same program (one source, one entry).
                            if (manifest.ContainsKey(relative)) return;
                            manifest[relative] = string.Empty;
                        }

                        Stopwatch one = Stopwatch.StartNew();
                        byte[] blob = null;
                        foreach ((string root, ShaderBinaryCache.PrecompiledIndex index) in previous)
                            if ((blob = TryReuse(root, relative, index)) != null) break;
                        bool wasReused = blob != null;
                        if (blob == null)
                            blob = ShaderCompiler.CompileForPrecompiledFolder(item.Job.Source, item.Job.Entry, item.Job.Stage, item.Format, item.Job.Label).Blob;
                        if (blob.Length == 0) throw new InvalidOperationException("The compiler returned an empty program.");

                        string path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllBytes(path, blob);
                        string line = $"{relative} {blob.Length.ToString(CultureInfo.InvariantCulture)} {Convert.ToHexString(SHA256.HashData(blob))} {item.Job.Label}:{item.Job.Entry}";
                        lock (gate)
                        {
                            manifest[relative] = line;
                            bytes += blob.Length;
                            if (wasReused) reused++;
                            else
                            {
                                compiled++;
                                timings.Add($"{one.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture),7} s  {name}");
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        lock (gate) failures.Add(name + ": " + error.GetBaseException().Message);
                    }
                });

                if (failures.Count == 0)
                {
                    var text = new StringBuilder();
                    text.Append(ShaderBinaryCache.PrecompiledManifestHeader).Append('\n');
                    foreach (string line in manifest.Values) text.Append(line).Append('\n');
                    // The manifest is written last: a folder without one is never read.
                    File.WriteAllText(Path.Combine(staging, ShaderBinaryCache.PrecompiledManifestName), text.ToString(), new UTF8Encoding(false));
                    if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
                    Directory.Move(staging, output);
                    staging = null;
                    ShaderBinaryCache.ForgetPrecompiledIndexes();
                }
            }
            finally
            {
                if (staging != null)
                {
                    try { Directory.Delete(staging, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            timings.Sort(StringComparer.Ordinal);
            timings.Reverse();
            var result = new PrecompiledShaderBuildResult(compiled, reused, manifest.Count, bytes, failures, timings, total.Elapsed);
            if (log != null)
            {
                foreach (string timing in timings) log(timing);
                foreach (string failure in failures) log("FAILED " + failure);
                log($"{result.Files} programs ({string.Join(", ", targets)}): {compiled} compiled, {reused} kept, "
                    + $"{bytes / 1024} KiB, {total.Elapsed.TotalSeconds:0.0} s -> {output}");
            }
            return result;
        }

        private static byte[] TryReuse(string output, string relative, ShaderBinaryCache.PrecompiledIndex previous)
        {
            if (!previous.Entries.TryGetValue(relative, out ShaderBinaryCache.PrecompiledEntry entry)) return null;
            try
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar)));
                return bytes.Length == entry.Length
                    && string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? bytes : null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// <c>--precompile-shaders &lt;folder&gt; [--formats dxbc,dxil,spirv,glsl] [--parallel n] [--reuse &lt;folder&gt;]</c>:
        /// the packaging step's entry point (GenesisEngine.exe). <c>--reuse</c> names an earlier
        /// package's folder whose unchanged programs are kept. Exit 0 when every program compiled.
        /// </summary>
        public static int RunCommandLine(string[] args)
        {
            int at = Array.FindIndex(args, value => string.Equals(value, CommandLineSwitch, StringComparison.OrdinalIgnoreCase));
            if (at < 0 || at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Usage: {CommandLineSwitch} <folder> [--formats dxbc,dxil,spirv,glsl] [--parallel n] [--reuse <folder>]");
                return 2;
            }

            var formats = new List<GpuShaderBinaryFormat>();
            int formatsAt = Array.FindIndex(args, value => string.Equals(value, "--formats", StringComparison.OrdinalIgnoreCase));
            if (formatsAt >= 0 && formatsAt + 1 < args.Length)
            {
                foreach (string name in args[formatsAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    GpuShaderBinaryFormat? format = name.ToLowerInvariant() switch
                    {
                        "dxbc" or "dx11" => GpuShaderBinaryFormat.Dxbc,
                        "dxil" or "dx12" => GpuShaderBinaryFormat.Dxil,
                        "spirv" or "spv" or "vulkan" => GpuShaderBinaryFormat.SpirV,
                        "glsl" or "opengl" => GpuShaderBinaryFormat.GlslUtf8,
                        _ => null,
                    };
                    if (format == null)
                    {
                        Console.Error.WriteLine($"Unknown shader format '{name}'. Choose dxbc, dxil, spirv or glsl.");
                        return 2;
                    }
                    formats.Add(format.Value);
                }
            }

            int parallelAt = Array.FindIndex(args, value => string.Equals(value, "--parallel", StringComparison.OrdinalIgnoreCase));
            int parallel = parallelAt >= 0 && parallelAt + 1 < args.Length
                && int.TryParse(args[parallelAt + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
            var reuse = new List<string>();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "--reuse", StringComparison.OrdinalIgnoreCase)) reuse.Add(args[i + 1]);

            try
            {
                PrecompiledShaderBuildResult result = Build(args[at + 1], formats.Count > 0 ? formats : null, Console.Out.WriteLine, parallel, reuseFrom: reuse);
                Console.Out.Flush();
                return result.Success ? 0 : 1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                Console.Error.WriteLine("Precompiling the engine's shaders failed: " + error.Message);
                return 1;
            }
        }
    }
}
