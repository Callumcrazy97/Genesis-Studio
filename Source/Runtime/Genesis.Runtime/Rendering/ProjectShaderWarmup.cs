using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Primitives;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Makes the project's own shaders on worker threads while a game starts, so no draw compiles
    /// one. A mesh, sprite or Fullscreen Shader resource used to be compiled the first time
    /// something drew with it, on the game's thread, by a compiler process the frame waited for:
    /// about 0.04-1.2 s per shader on an idle PC, and once 45 s on a busy one.
    /// </summary>
    /// <remarks>
    /// The programs are those <see cref="ProjectShaderPrograms"/> lists (the draw paths' own
    /// sources, entry points and keys), so what a draw asks for afterwards is found made. An
    /// exported game ships them compiled, and this then only reads them. While a resource's
    /// programs are still being made, <see cref="IsPending"/> is true for it and its draws use the
    /// engine's own shading for those frames instead of waiting; the room's loading cover stays up
    /// until <see cref="Pending"/> is zero (within the cover's time limit).
    /// </remarks>
    public static class ProjectShaderWarmup
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, int> PendingByShader = new(StringComparer.OrdinalIgnoreCase);
        private static int _pending;
        private static string _startedFor;
        private static string _summary;
        private static Action<string> _report;

        /// <summary>Programs not made yet (counting the listing itself until it is done).</summary>
        public static int Pending => Volatile.Read(ref _pending);

        /// <summary>Programs this process's warm-up made: read from a cache, compiled, or failed to compile.</summary>
        public static int Read { get; private set; }
        public static int Compiled { get; private set; }
        public static int Failed { get; private set; }

        /// <summary>
        /// One line for each program the warm-up could not compile: the Shader resource's name and
        /// file, the entry point, and the start of the compiler's error. Also written to the log.
        /// </summary>
        public static IReadOnlyList<string> Failures { get; private set; } = Array.Empty<string>();

        // The log names this many failing programs; the rest are counted.
        private const int FailuresLogged = 12;

        /// <summary>
        /// True while the warm-up is still making a program of this Shader resource (named as a
        /// draw names it, or by its file's full path).
        /// </summary>
        public static bool IsPending(string shader)
        {
            if (Volatile.Read(ref _pending) == 0 || string.IsNullOrWhiteSpace(shader)) return false;
            lock (Gate) return PendingByShader.ContainsKey(shader.Trim());
        }

        /// <summary>
        /// Starts making every program of the project's Shader resources for one binary format,
        /// once per project and format in a process (one warm-up at a time).
        /// </summary>
        public static void Start(string projectPath, GpuShaderBinaryFormat format, int workers = 0)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return;
            string startedFor = System.IO.Path.GetFullPath(projectPath) + "|" + format;
            lock (Gate)
            {
                if (string.Equals(_startedFor, startedFor, StringComparison.OrdinalIgnoreCase)
                    || Volatile.Read(ref _pending) != 0) return;
                _startedFor = startedFor;
                _summary = null;
                PendingByShader.Clear();
                Interlocked.Increment(ref _pending);
            }

            var thread = new Thread(() => Run(projectPath, format, workers))
            {
                IsBackground = true,
                Name = "Genesis project shader warm-up",
            };
            thread.Start();
        }

        /// <summary>
        /// Where to write a line saying what the warm-up did, and a line for each shader that did
        /// not compile, when it is done (at once if it is).
        /// </summary>
        public static void ReportTo(Action<string> report)
        {
            string summary;
            lock (Gate)
            {
                _report = report;
                summary = _summary;
            }

            if (summary != null && report != null)
                foreach (string line in summary.Split('\n')) report(line);
        }

        /// <summary>Waits until every program is made, or the time is up. True when they all are.</summary>
        public static bool WaitUntilDone(TimeSpan timeout)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (Pending > 0)
            {
                if (clock.Elapsed >= timeout) return false;
                Thread.Sleep(5);
            }

            return true;
        }

        /// <summary>Forgets a finished warm-up so another can start (tests).</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                if (Volatile.Read(ref _pending) != 0) throw new InvalidOperationException("A shader warm-up is still running.");
                _startedFor = null;
                _summary = null;
                _report = null;
                PendingByShader.Clear();
                Read = Compiled = Failed = 0;
                Failures = Array.Empty<string>();
            }
        }

        private static void Run(string projectPath, GpuShaderBinaryFormat format, int workers)
        {
            Stopwatch clock = Stopwatch.StartNew();
            ProjectShaderProgram[] programs = Array.Empty<ProjectShaderProgram>();
            int read = 0, compiled = 0, failed = 0;
            var failures = new List<string>();
            try
            {
                // Mesh and sprite programs first (drawn as soon as the room is), the largest first
                // so the longest compile starts at once; Fullscreen effects only run when turned on.
                programs = ProjectShaderPrograms.Enumerate(projectPath, everyVariant: false)
                    .OrderBy(program => program.Pipeline == ShaderAssetPipeline.Fullscreen ? 1 : 0)
                    .ThenByDescending(program => program.Source.Length)
                    .ToArray();
                lock (Gate)
                {
                    foreach (ProjectShaderProgram program in programs)
                    {
                        Mark(program.ShaderPath, +1);
                        Mark(program.Name, +1);
                    }
                }

                Interlocked.Add(ref _pending, programs.Length);
                int next = -1;
                void Work()
                {
                    for (int index = Interlocked.Increment(ref next); index < programs.Length; index = Interlocked.Increment(ref next))
                    {
                        ProjectShaderProgram program = programs[index];
                        try
                        {
                            ShaderCompileResult result = ShaderCompiler.CompileForBackend(
                                program.Source, program.Entry, program.Stage, format, program.ShaderPath,
                                ShaderCompiler.BuildDefaultIncludeSearchPaths(program.ShaderPath, projectPath));
                            if (result.CacheHit) Interlocked.Increment(ref read);
                            else Interlocked.Increment(ref compiled);
                        }
                        catch (Exception error)
                        {
                            // The draw that uses it compiles it again and reports it too, but a post
                            // effect that is never turned on never draws: name it and say why here.
                            Interlocked.Increment(ref failed);
                            string failure = DescribeFailure(program, error);
                            lock (failures) failures.Add(failure);
                        }
                        finally
                        {
                            lock (Gate)
                            {
                                Mark(program.ShaderPath, -1);
                                Mark(program.Name, -1);
                            }

                            Interlocked.Decrement(ref _pending);
                        }
                    }
                }

                // Each DXC compile is its own process, so a second worker halves the wait for a
                // project with several shaders; more would compete with the game's own loading.
                int count = workers > 0 ? workers : Math.Clamp(Environment.ProcessorCount / 8, 1, 2);
                var helpers = new List<Thread>();
                for (int i = 1; i < Math.Min(count, programs.Length); i++)
                {
                    var helper = new Thread(Work) { IsBackground = true, Name = "Genesis project shader warm-up" };
                    helper.Start();
                    helpers.Add(helper);
                }

                Work();
                foreach (Thread helper in helpers) helper.Join();
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException
                or System.IO.InvalidDataException or ArgumentException or InvalidOperationException)
            {
                // Listing the resources failed; each draw makes its own shader as before.
            }
            finally
            {
                string summary = $"project shaders: {programs.Length} program{(programs.Length == 1 ? string.Empty : "s")} for {format} made on workers in "
                    + $"{clock.Elapsed.TotalMilliseconds:F0} ms ({read} read from the shader cache, {compiled} compiled"
                    + (failed > 0 ? $", {failed} did not compile: listed below" : string.Empty) + ")";
                string[] failureLines;
                lock (failures)
                {
                    failures.Sort(StringComparer.Ordinal);
                    failureLines = failures.ToArray();
                }

                var lines = new List<string> { summary };
                lines.AddRange(failureLines.Take(FailuresLogged));
                if (failureLines.Length > FailuresLogged)
                    lines.Add($"project shaders: {failureLines.Length - FailuresLogged} more did not compile (open them in the Shader Editor)");
                Action<string> report;
                lock (Gate)
                {
                    Read = read;
                    Compiled = compiled;
                    Failed = failed;
                    Failures = failureLines;
                    PendingByShader.Clear();
                    _summary = string.Join("\n", lines);
                    report = _report;
                }

                Interlocked.Decrement(ref _pending);
                if (report != null) foreach (string line in lines) report(line);
            }
        }

        /// <summary>
        /// "project shader 'Name' (Fullscreen pixel MainPS, Assets/Shaders/Name.shader.json) did not
        /// compile: [first lines of the compiler's error]", with a hint when the code declares an
        /// HLSL reserved word (line, point, sample...) as a name.
        /// </summary>
        internal static string DescribeFailure(ProjectShaderProgram program, Exception error)
        {
            string[] errorLines = (error?.Message ?? "unknown error")
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Take(3)
                .ToArray();
            string message = string.Join(" | ", errorLines);
            if (message.Length > 600) message = message[..600] + "...";
            string variant = string.IsNullOrEmpty(program.Variant) ? string.Empty : $", variant {program.Variant}";
            string file = string.IsNullOrWhiteSpace(program.ShaderPath) ? string.Empty : ", " + program.ShaderPath;
            string hint = HlslReservedNames.Describe(HlslReservedNames.Find(program.Source), withLines: false);
            return $"project shader '{program.Name}' ({program.Pipeline} {program.Stage.ToString().ToLowerInvariant()} {program.Entry}{variant}{file}) did not compile: {message}"
                + (hint.Length > 0 ? " Hint: " + hint : string.Empty);
        }

        private static void Mark(string shader, int delta)
        {
            if (string.IsNullOrWhiteSpace(shader)) return;
            PendingByShader.TryGetValue(shader, out int count);
            count += delta;
            if (count > 0) PendingByShader[shader] = count;
            else PendingByShader.Remove(shader);
        }
    }
}
