using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Genesis.Runtime.Input;
using Genesis.Runtime.Project;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The run, test and profile loop: play the game unattended with scripted input, take pictures at
/// set moments, optionally profile it, and read back what happened (the Player's log, its errors,
/// the pictures and the profile report). The input is written as an engine input recording and
/// played with the Player's own replay, so the game sees it exactly as it would a person's.
/// </summary>
public static class GameRunCommands
{
    public static IEnumerable<IAutomationCommand> All() => [new GameRun(), new GameBenchmark()];

    public const float FramesPerSecond = 60f;

    public sealed class InputStep
    {
        [Description("How long the step lasts, seconds (default one frame).")] public float? Seconds { get; set; }
        [Description("Keys held down through the step: W, A, S, D, Space, LeftShift, Up, Enter, E...")] public List<string>? Hold { get; set; }
        [Description("Keys tapped at the step's start (down for its first frame).")] public List<string>? Press { get; set; }
        [Description("Mouse-look through the step, [x, y] in pixels a second (turning the camera).")] public float[]? Look { get; set; }
        [Description("Where the mouse pointer is, [x, y] in window pixels, from this step on.")] public float[]? Mouse { get; set; }
        [Description("A mouse button clicked at the step's start: Left, Right or Middle.")] public string? Click { get; set; }
        [Description("A controller's left stick through the step, [x, y] from -1 to 1 (up is y = 1).")] public float[]? Stick { get; set; }
        [Description("Text typed at the step's start.")] public string? Type { get; set; }
    }

    /// <summary>The frames of scripted input, at <see cref="FramesPerSecond"/>, with presses and releases worked out as a person's would be.</summary>
    public static InputFrame[] Frames(IReadOnlyList<InputStep> steps)
    {
        var frames = new List<InputFrame>();
        bool pad = steps.Any(step => step.Stick is not null);
        UInt128 previousKeys = UInt128.Zero;
        byte previousMouse = 0;
        Vector2 pointer = Vector2.Zero;
        foreach (InputStep step in steps)
        {
            UInt128 held = Keys(step.Hold), tapped = Keys(step.Press);
            byte click = step.Click is null ? (byte)0 : (byte)(1 << (int)Parse<MouseButton>(step.Click, "mouse button"));
            if (step.Mouse is not null) pointer = Pair(step.Mouse, "mouse");
            Vector2 look = step.Look is null ? Vector2.Zero : Pair(step.Look, "look") / FramesPerSecond;
            Vector2 stick = step.Stick is null ? Vector2.Zero : Vector2.Clamp(Pair(step.Stick, "stick"), -Vector2.One, Vector2.One);
            if (step.Seconds is < 0) throw new AutomationException("A step's seconds cannot be negative.");
            int count = Math.Max(1, (int)MathF.Round((step.Seconds ?? 0f) * FramesPerSecond));
            for (int i = 0; i < count; i++)
            {
                UInt128 keys = held | (i == 0 ? tapped : UInt128.Zero);
                byte mouse = i == 0 ? click : (byte)0;
                frames.Add(new InputFrame
                {
                    Delta = 1f / FramesPerSecond,
                    KeysDown = keys,
                    KeysPressed = keys & ~previousKeys,
                    KeysReleased = previousKeys & ~keys,
                    MouseDown = mouse,
                    MousePressed = (byte)(mouse & ~previousMouse),
                    MouseReleased = (byte)(previousMouse & ~mouse),
                    MouseX = pointer.X,
                    MouseY = pointer.Y,
                    LookX = look.X,
                    LookY = look.Y,
                    GamepadConnected = pad,
                    LeftStick = stick,
                    LeftStickRaw = stick,
                    Typed = i == 0 && !string.IsNullOrEmpty(step.Type) ? step.Type : null,
                });
                previousKeys = keys;
                previousMouse = mouse;
            }
        }
        // Let go of everything on a last frame, as a person lifting their hands would.
        if (previousKeys != UInt128.Zero || previousMouse != 0)
            frames.Add(new InputFrame { Delta = 1f / FramesPerSecond, KeysReleased = previousKeys, MouseReleased = previousMouse, MouseX = pointer.X, MouseY = pointer.Y, GamepadConnected = pad });
        return [.. frames];
    }

    private static UInt128 Keys(List<string>? names)
    {
        UInt128 bits = UInt128.Zero;
        foreach (string name in names ?? [])
        {
            Key key = Parse<Key>(name, "key");
            if (key == Key.Escape) throw new AutomationException("Escape hands a replay back to the real keyboard, so a run cannot press it.");
            if ((int)key is <= 0 or >= 128) throw new AutomationException($"'{name}' cannot be played back.");
            bits |= UInt128.One << (int)key;
        }
        return bits;
    }

    private static T Parse<T>(string name, string what) where T : struct, Enum =>
        Enum.TryParse(name.Trim(), true, out T value) && Enum.IsDefined(value) ? value
            : throw new AutomationException($"'{name}' is not a {what}. {what[..1].ToUpperInvariant() + what[1..]}s: {string.Join(", ", Enum.GetNames<T>().Take(120))}.");

    private static Vector2 Pair(float[] values, string what) =>
        values is { Length: 2 } ? new Vector2(values[0], values[1]) : throw new AutomationException($"'{what}' is [x, y].");

    public sealed class RunInput
    {
        [Description("The room to start in, by name (default the game's start room).")] public string? Room { get; set; }
        [Description("The scripted input, step by step. The game closes when the steps end; end with a step of only seconds to watch longer.")]
        public List<InputStep>? Steps { get; set; }
        [Description("Seconds of play at which to take pictures.")] public List<float>? Shots { get; set; }
        [Description("Record the debug screen's profile (frame phases, hottest scripts, garbage) for the run.")] public bool? Profile { get; set; }
        [Description("Seed for the game's random numbers (default 1), so a run can be repeated exactly.")] public int? Seed { get; set; }
        [Description("Give up after this many seconds (default the steps' length plus 90).")] public float? TimeoutSeconds { get; set; }
    }

    public sealed class GameRun : AutomationCommand<RunInput>
    {
        public override string Name => "game.run";
        public override string Description => "Plays the game unattended with scripted input (keys, mouse, look, controller stick), takes pictures at set seconds, optionally profiles it, and reports the checks the game's scripts made (PGSL TestCheck), the log, its errors, the pictures and the profile.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, RunInput input)
        {
            List<InputStep> steps = input.Steps is { Count: > 0 } given ? given : [new InputStep { Seconds = 5 }];
            InputFrame[] frames = Frames(steps);
            float length = frames.Length / FramesPerSecond;
            float[] shots = [.. (input.Shots ?? []).OrderBy(s => s)];
            if (shots.Any(s => s < 0 || s > length))
                throw new AutomationException($"The run lasts {length:0.##} s, so pictures can be taken from 0 to {length:0.##} s.");

            string root = context.Root;
            if (ProjectRunLauncher.HasCSharpScripts(root))
            {
                ProjectRunLauncher.CompileOutcome compiled = ProjectRunLauncher.CompileScripts(root);
                if (!compiled.Success) throw new AutomationException("The game's C# scripts did not compile: " + compiled.ErrorMessage);
            }

            string folder = Path.Combine(context.Project.InternalPath, "Automation", "Runs", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            string recording = Path.Combine(folder, "input" + InputReplay.FileExtension);
            InputReplay.Write(recording, input.Seed ?? 1, frames);
            var environment = new Dictionary<string, string>
            {
                [InputReplay.ReplayEnvironmentVariable] = recording,
                [TimedShots.EnvironmentVariable] = string.Join(",", shots.Select(s => s.ToString(CultureInfo.InvariantCulture))),
                [ProjectPlayerApp.ProfileEnvironmentVariable] = input.Profile == true ? "1" : "0",
                [Genesis.Rendering.Core.EngineRenderingDefaults.UncappedEnvironmentVariable] = "0",
            };

            // The game's own self-tests (PGSL TestCheck) of this run only.
            string checksPath = Path.Combine(ProjectPaths.LogsDir(root), Genesis.Runtime.Scripting.PgslCommands.TestChecksFileName);
            if (File.Exists(checksPath)) File.Delete(checksPath);

            var clock = Stopwatch.StartNew();
            ProjectRunLauncher.LaunchOutcome launched = ProjectRunLauncher.Launch(root, input.Room, extraEnvironment: environment);
            if (!launched.Success) throw new AutomationException("The game could not start: " + launched.ErrorMessage);
            using Process process = launched.Process;
            float timeout = input.TimeoutSeconds ?? length + 90f;
            bool timedOut = !process.WaitForExit(TimeSpan.FromSeconds(Math.Max(5f, timeout)));
            if (timedOut)
            {
                try { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } catch (InvalidOperationException) { }
            }
            clock.Stop();

            string logPath = Path.Combine(ProjectPaths.LogsDir(root), "project_player.log");
            string[] log = File.Exists(logPath) ? File.ReadAllLines(logPath) : [];
            string[] pictures = [.. log.Select(line => After(line, "screenshot saved: ")).Where(path => path is not null && File.Exists(path)).Select(path => path!)];
            string? profileFolder = log.Select(line => After(line, "profile recording: ")).LastOrDefault(path => path is not null);
            string[] problems = [.. log.Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase)).Take(60)];

            var checks = new JsonArray();
            int failedChecks = 0;
            foreach (string line in File.Exists(checksPath) ? File.ReadAllLines(checksPath) : [])
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 2) continue;
                bool passed = parts[0] == "PASS";
                if (!passed) failedChecks++;
                checks.Add(new JsonObject { ["passed"] = passed, ["name"] = parts[1], ["detail"] = parts.Length > 2 ? parts[2] : string.Empty });
            }

            var data = new JsonObject
            {
                ["room"] = launched.RoomName,
                ["checks"] = checks,
                ["exitCode"] = timedOut ? (int?)null : process.ExitCode,
                ["timedOut"] = timedOut,
                ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
                ["inputSeconds"] = Math.Round(length, 2),
                ["input"] = context.Relative(recording),
                ["pictures"] = new JsonArray([.. pictures.Select(p => (JsonNode?)Shown(context, p))]),
                ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode?)p)]),
                ["log"] = Shown(context, logPath),
                ["logTail"] = new JsonArray([.. log.TakeLast(40).Select(p => (JsonNode?)p)]),
            };
            if (profileFolder is not null)
            {
                string report = Path.Combine(profileFolder, "report.md");
                data["profile"] = Shown(context, profileFolder);
                if (File.Exists(report)) data["profileReport"] = Truncate(File.ReadAllText(report), 6000);
            }
            bool ok = !timedOut && process.ExitCode == 0 && failedChecks == 0;
            string checkText = checks.Count == 0 ? string.Empty : $", {checks.Count - failedChecks} of {checks.Count} checks passed";
            string message = timedOut ? $"The run did not finish within {timeout:0} s and was stopped{checkText}."
                : process.ExitCode != 0 ? $"The game closed with code {process.ExitCode}{checkText}."
                : $"Ran {length:0.#} s of input in {launched.RoomName}: {pictures.Length} pictures, {problems.Length} log problems{checkText}.";
            return ok ? AutomationResult.Success(message, data) : AutomationResult.Failure(message, data);
        }

        private static string? After(string line, string marker)
        {
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            return at < 0 ? null : line[(at + marker.Length)..].Trim();
        }

        /// <summary>A path inside the project as the project-relative path commands use; anywhere else as it is.</summary>
        internal static string Shown(AutomationContext context, string path)
        {
            string full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(context.Root), StringComparison.OrdinalIgnoreCase) ? context.Relative(full) : full;
        }

        private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "\n...";
    }

    public sealed class BenchmarkInput
    {
        [Description("The room to measure, by name (default the game's start room).")] public string? Room { get; set; }
        [Description("Seconds measured after the warm-up (default 20).")] public float? Seconds { get; set; }
        [Description("Seconds of play before measuring starts, not counted (default 5).")] public float? WarmupSeconds { get; set; }
        [Description("Scripted input played meanwhile, as game.run takes it (a walk or a camera turn); without it the game is left standing.")]
        public List<InputStep>? Steps { get; set; }
        [Description("Vsync and frame caps off, so frame times show the real headroom (default true); false keeps the game's own pacing.")] public bool? Uncapped { get; set; }
        [Description("Fail the run if the 95th-percentile frame takes longer than this, milliseconds.")] public double? P95BudgetMilliseconds { get; set; }
        [Description("Fail the run if the 99th-percentile frame takes longer than this, milliseconds.")] public double? P99BudgetMilliseconds { get; set; }
        [Description("Fail the run if the game allocates more than this many kilobytes a frame on average.")] public double? AllocationBudgetKilobytesPerFrame { get; set; }
    }

    /// <summary>
    /// The Player's own benchmark (--benchmark-output) as a command: frame times, allocation, memory,
    /// draw calls and the scripts that cost most, with budgets that fail the run, so "faster" and
    /// "slower" are numbers a caller can compare from one change to the next.
    /// </summary>
    public sealed class GameBenchmark : AutomationCommand<BenchmarkInput>
    {
        public override string Name => "game.benchmark";
        public override string Description => "Measures the game running (optionally with scripted input): median, 95th and 99th percentile and worst frame times, allocation a frame, collector pauses, memory, draw calls and the costliest scripts; budgets given fail the run when exceeded.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, BenchmarkInput input)
        {
            double seconds = input.Seconds ?? 20, warmup = input.WarmupSeconds ?? 5;
            if (seconds is < 1 or > 900 || warmup is < 0 or > 120) throw new AutomationException("Measure 1 to 900 seconds after a warm-up of 0 to 120.");
            string root = context.Root;
            if (ProjectRunLauncher.HasCSharpScripts(root))
            {
                ProjectRunLauncher.CompileOutcome compiled = ProjectRunLauncher.CompileScripts(root);
                if (!compiled.Success) throw new AutomationException("The game's C# scripts did not compile: " + compiled.ErrorMessage);
            }

            string folder = Path.Combine(context.Project.InternalPath, "Automation", "Benchmarks", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var environment = new Dictionary<string, string>
            {
                [TimedShots.EnvironmentVariable] = string.Empty,
                [ProjectPlayerApp.ProfileEnvironmentVariable] = "0",
                [InputReplay.ReplayEnvironmentVariable] = string.Empty,
                [Genesis.Rendering.Core.EngineRenderingDefaults.UncappedEnvironmentVariable] = input.Uncapped == false ? "0" : "1",
            };
            if (input.Steps is { Count: > 0 } given)
            {
                // A replay closes the game when it ends: steps shorter than the run are padded with
                // standing still, so the benchmark is not cut short.
                List<InputStep> steps = [.. given];
                float length = Frames(steps).Length / FramesPerSecond, needed = (float)(seconds + warmup + 10);
                if (length < needed) steps.Add(new InputStep { Seconds = needed - length });
                string recording = Path.Combine(folder, "input" + InputReplay.FileExtension);
                InputReplay.Write(recording, 1, Frames(steps));
                environment[InputReplay.ReplayEnvironmentVariable] = recording;
            }
            string arguments = string.Create(CultureInfo.InvariantCulture,
                $"--benchmark-output \"{folder}\" --benchmark-seconds {seconds} --benchmark-warmup-seconds {warmup}");

            ProjectRunLauncher.LaunchOutcome launched = ProjectRunLauncher.Launch(root, input.Room, extraEnvironment: environment,
                extraArguments: arguments, liveReload: false);
            if (!launched.Success) throw new AutomationException("The game could not start: " + launched.ErrorMessage);
            using Process process = launched.Process;
            bool timedOut = !process.WaitForExit(TimeSpan.FromSeconds(seconds + warmup + 120));
            if (timedOut)
            {
                try { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } catch (InvalidOperationException) { }
                throw new AutomationException($"The benchmark did not finish within {seconds + warmup + 120:0} s and was stopped.");
            }

            string summaryPath = Path.Combine(folder, "benchmark.json");
            if (!File.Exists(summaryPath))
                throw new AutomationException($"The game closed with code {process.ExitCode} before writing its benchmark; see {GameRun.Shown(context, Path.Combine(ProjectPaths.LogsDir(root), "project_player.log"))}.");
            JsonObject summary = JsonNode.Parse(File.ReadAllText(summaryPath))!.AsObject();
            JsonArray frames = File.Exists(Path.Combine(folder, "frames.json"))
                ? JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "frames.json"))) as JsonArray ?? [] : [];
            double[] times = [.. frames.Select(f => f?["FrameMilliseconds"]?.GetValue<double>() ?? 0).Order()];
            double Mean(string field) => frames.Count == 0 ? 0 : frames.Average(f => f?[field] is JsonValue v && v.TryGetValue(out double d) ? d : 0);
            double Sum(string field) => frames.Sum(f => f?[field] is JsonValue v && v.TryGetValue(out double d) ? d : 0);
            double median = times.Length == 0 ? 0 : times[times.Length / 2];
            double p95 = summary["P95Milliseconds"]?.GetValue<double>() ?? 0, p99 = summary["P99Milliseconds"]?.GetValue<double>() ?? 0;
            double allocationKb = frames.Count == 0 ? 0 : Sum("AllocatedBytes") / frames.Count / 1024.0;

            var data = new JsonObject
            {
                ["room"] = launched.RoomName,
                ["completed"] = summary["Completed"]?.GetValue<bool>() ?? false,
                ["backend"] = summary["Backend"]?.DeepClone(),
                ["resolution"] = new JsonArray(summary["Width"]?.DeepClone(), summary["Height"]?.DeepClone()),
                ["frames"] = frames.Count,
                ["medianMilliseconds"] = Math.Round(median, 2),
                ["medianFps"] = median > 0 ? Math.Round(1000 / median, 1) : 0,
                ["p95Milliseconds"] = Math.Round(p95, 2),
                ["p99Milliseconds"] = Math.Round(p99, 2),
                ["worstMilliseconds"] = Math.Round(summary["MaximumFrameMilliseconds"]?.GetValue<double>() ?? 0, 2),
                ["allocationKilobytesPerFrame"] = Math.Round(allocationKb, 2),
                ["collectorPauseMilliseconds"] = Math.Round(Sum("GcPauseMilliseconds"), 1),
                ["peakWorkingSetMegabytes"] = Math.Round((summary["PeakWorkingSetBytes"]?.GetValue<long>() ?? 0) / 1048576.0, 1),
                ["averageDrawCalls"] = Math.Round(Mean("DrawCalls"), 1),
                ["averageTriangles"] = Math.Round(Mean("Triangles")),
                ["scripts"] = new JsonArray([.. (summary["Pgsl"] as JsonArray ?? []).Take(12).Select(s => s?.DeepClone())]),
                ["folder"] = GameRun.Shown(context, folder),
            };
            var over = new List<string>();
            if (input.P95BudgetMilliseconds is double p95Budget && p95 > p95Budget) over.Add($"95th percentile {p95:0.##} ms over {p95Budget:0.##} ms");
            if (input.P99BudgetMilliseconds is double p99Budget && p99 > p99Budget) over.Add($"99th percentile {p99:0.##} ms over {p99Budget:0.##} ms");
            if (input.AllocationBudgetKilobytesPerFrame is double allocationBudget && allocationKb > allocationBudget)
                over.Add($"{allocationKb:0.##} KB a frame over {allocationBudget:0.##} KB");
            data["overBudget"] = new JsonArray([.. over.Select(o => (JsonNode?)o)]);
            string message = $"{launched.RoomName}: median {median:0.##} ms ({(median > 0 ? 1000 / median : 0):0} fps), 95th {p95:0.##} ms, 99th {p99:0.##} ms, {allocationKb:0.#} KB a frame over {frames.Count} frames"
                + (over.Count == 0 ? "." : "; over budget: " + string.Join("; ", over) + ".");
            return over.Count == 0 && process.ExitCode == 0 ? AutomationResult.Success(message, data) : AutomationResult.Failure(message, data);
        }
    }
}
