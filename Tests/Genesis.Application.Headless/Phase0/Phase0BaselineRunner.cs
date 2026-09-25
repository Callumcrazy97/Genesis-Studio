using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Genesis.Application.Runtime;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Phase0;

internal static class Phase0BaselineRunner
{
    private const int DefaultMeasuredFrames = 360;
    private const int DefaultWarmupFrames = 60;
    private const double SameBackendTileDeltaLimit = 2.0;
    private const double HistogramDistanceLimit = 0.02;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        int measuredFrames = ReadPositiveInt(args, "--frames", DefaultMeasuredFrames);
        int warmupFrames = ReadPositiveInt(args, "--warmup", DefaultWarmupFrames);
        bool updateBaselines = args.Any(argument =>
            string.Equals(argument, "--update-baselines", StringComparison.OrdinalIgnoreCase));
        string outputRoot = ResolveOutput(args);
        string captures = Path.Combine(outputRoot, "Images");
        Directory.CreateDirectory(captures);

        Phase0RunReport report = new()
        {
            StartedUtc = DateTimeOffset.UtcNow,
            MachineName = Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            RuntimeVersion = Environment.Version.ToString(),
            OutputDirectory = outputRoot,
            MeasuredFramesPerScene = measuredFrames,
            WarmupFramesPerScene = warmupFrames,
            UpdateBaselines = updateBaselines,
        };

        Console.WriteLine();
        Console.WriteLine("=== Genesis Studio Phase 0 baseline ===");
        Console.WriteLine(
            $"Warmup: {warmupFrames} frames; measured: {measuredFrames} frames per scene; "
            + $"update baselines: {updateBaselines}");

        string? repositoryRoot = FindRepositoryRoot();
        RunProbes(report, repositoryRoot);

        int controlFrames = Math.Clamp(measuredFrames / 3, 60, 180);
        Phase0FrameMetrics thermalStart = RunThermalControl(controlFrames);

        foreach (GenesisScene scene in GenesisSceneCatalog.Scenes)
        {
            Console.WriteLine();
            Console.WriteLine($"SCENE {scene.Id} — {scene.Name}");
            Phase0SceneResult result = RunScene(
                scene,
                warmupFrames,
                measuredFrames,
                captures,
                ResolveBaselineDirectory(repositoryRoot, outputRoot),
                updateBaselines);
            report.Scenes.Add(result);

            Console.WriteLine(
                $"  {result.Frames.AverageFps:0.0} FPS avg, "
                + $"{result.Frames.OnePercentLowFps:0.0} FPS 1% low, "
                + $"{result.LoadMilliseconds:0.0} ms load, parity {result.Parity.Outcome}");
        }

        Phase0FrameMetrics thermalEnd = RunThermalControl(controlFrames);
        double drift = thermalStart.AverageFps <= 0
            ? 0
            : ((thermalEnd.AverageFps - thermalStart.AverageFps) / thermalStart.AverageFps) * 100.0;
        report.ThermalDrift = new ThermalDriftResult(
            thermalStart.AverageFps,
            thermalEnd.AverageFps,
            drift);

        report.CompletedUtc = DateTimeOffset.UtcNow;
        report.Passed =
            report.Probes.All(static probe => probe.Outcome != ProbeOutcome.Failed)
            && report.Scenes.All(static scene => scene.Parity.Outcome != ParityOutcome.Failed);

        Directory.CreateDirectory(outputRoot);
        string jsonFile = Path.Combine(outputRoot, "phase0-results.json");
        File.WriteAllText(jsonFile, JsonSerializer.Serialize(report, JsonOptions));

        string markdownFile = Path.Combine(outputRoot, "phase0-report.md");
        Phase0ReportWriter.WriteMarkdown(report, markdownFile);

        Console.WriteLine();
        Console.WriteLine(report.Passed ? "PHASE 0 BASELINE PASSED" : "PHASE 0 BASELINE FAILED");
        Console.WriteLine($"Report: {markdownFile}");
        Console.WriteLine($"JSON:   {jsonFile}");
        Console.WriteLine($"Images: {captures}");
        return report.Passed ? 0 : 1;
    }

    private static void RunProbes(Phase0RunReport report, string? repositoryRoot)
    {
        ComponentProbeRunner probes = new();

        probes.Run("Harness", "FrameRecorder.OnePercentLow", () =>
        {
            EngineFrameRecorder recorder = new();
            long timestamp = Stopwatch.GetTimestamp();
            recorder.RecordPresent(timestamp, 2.0, 3.0);

            for (int interval = 1; interval <= 100; interval++)
            {
                double milliseconds = interval == 100 ? 50.0 : 10.0;
                timestamp += (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);
                recorder.RecordPresent(timestamp, 2.0, 3.0);
            }

            Phase0FrameMetrics metrics = recorder.Summarise();
            ComponentProbeRunner.Require(
                Math.Abs(metrics.OnePercentLowFps - 20.0) < 0.2,
                $"Synthetic 1% low was {metrics.OnePercentLowFps:0.00} FPS, expected 20 FPS.");
            ComponentProbeRunner.Require(
                metrics.PresentedFrames == 101,
                $"Recorder counted {metrics.PresentedFrames} frames, expected 101.");
            return $"1% low {metrics.OnePercentLowFps:0.0} FPS from 100 intervals";
        });

        probes.Run("Harness", "FrameRecorder.InstrumentationExclusion", () =>
        {
            EngineFrameRecorder recorder = new();
            long timestamp = Stopwatch.GetTimestamp();
            recorder.RecordPresent(timestamp, 1.0, 1.0);

            timestamp += MillisecondsToTicks(10);
            recorder.RecordPresent(timestamp, 1.0, 1.0);

            recorder.SkipIntervals(1);
            timestamp += MillisecondsToTicks(200);
            recorder.RecordPresent(timestamp, 200.0, 0);

            timestamp += MillisecondsToTicks(10);
            recorder.RecordPresent(timestamp, 1.0, 1.0);

            Phase0FrameMetrics metrics = recorder.Summarise();
            ComponentProbeRunner.Require(
                metrics.MaximumFrameMilliseconds < 15.0,
                $"Skipped instrumentation leaked into timing ({metrics.MaximumFrameMilliseconds:0.00} ms max).");
            return $"worst retained interval {metrics.MaximumFrameMilliseconds:0.00} ms";
        });

        probes.Run("Harness", "SceneCatalog.IsolationThenIntegration", () =>
        {
            IReadOnlyList<GenesisScene> scenes = GenesisSceneCatalog.Scenes;
            ComponentProbeRunner.Require(scenes.Count >= 3, "The Phase 0 scene catalogue is too small.");
            ComponentProbeRunner.Require(
                scenes.Select(static scene => scene.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == scenes.Count,
                "Scene ids are not unique.");
            ComponentProbeRunner.Require(
                scenes.Any(static scene => scene.Dimension == GenesisSceneDimension.TwoDimensional),
                "The catalogue has no 2D isolation scene.");
            ComponentProbeRunner.Require(
                scenes.Any(static scene => scene.Dimension == GenesisSceneDimension.ThreeDimensional),
                "The catalogue has no 3D isolation scene.");
            ComponentProbeRunner.Require(
                scenes[^1].IntegrationScene,
                "The integration scene must be last so isolated costs are measured first.");
            ComponentProbeRunner.Require(
                scenes.Take(scenes.Count - 1).All(static scene => !scene.IntegrationScene),
                "An integration scene appears before the isolated scenes.");
            return $"{scenes.Count} current Genesis scenes";
        });

        if (repositoryRoot is null)
        {
            probes.Skip(
                "Architecture",
                "EngineComponent.GraphicsSeparation",
                "Repository root was not available; the source-reference gate could not run.");
        }
        else
        {
            probes.Run("Architecture", "EngineComponent.GraphicsSeparation", () =>
            {
                string source = Path.Combine(repositoryRoot, "Source");
                string[] componentProjects = Directory.Exists(source)
                    ? Directory.GetFiles(source, "Engine.Component.*.csproj", SearchOption.AllDirectories)
                    : [];

                foreach (string project in componentProjects)
                {
                    string projectText = File.ReadAllText(project);
                    ComponentProbeRunner.Require(
                        !projectText.Contains("Genesis.Rendering", StringComparison.OrdinalIgnoreCase),
                        $"{Path.GetFileName(project)} references Genesis.Rendering; Engine.Component.* must stay CPU-only.");
                }

                return componentProjects.Length == 0
                    ? "0 Engine.Component assemblies yet; separation gate armed"
                    : $"{componentProjects.Length} Engine.Component assemblies checked";
            });
        }

        report.Probes.AddRange(probes.Results);
        foreach (ComponentProbeResult probe in probes.Results)
        {
            Console.WriteLine(
                $"{(probe.Outcome == ProbeOutcome.Passed ? "PASS" : probe.Outcome == ProbeOutcome.Skipped ? "SKIP" : "FAIL")} "
                + $"{probe.Component}.{probe.Name} — {probe.Measure}"
                + (string.IsNullOrWhiteSpace(probe.Detail) ? string.Empty : $" ({probe.Detail})"));
        }
    }

    private static Phase0SceneResult RunScene(
        GenesisScene scene,
        int warmupFrames,
        int measuredFrames,
        string captures,
        string baselineDirectory,
        bool updateBaselines)
    {
        using IPhase0SceneHost host = CreateSceneHost(scene.Kind);

        host.InitializeHost();
        Stopwatch load = Stopwatch.StartNew();
        host.PrepareScene();
        load.Stop();

        for (int frame = 0; frame < warmupFrames; frame++)
        {
            _ = host.RenderFrame();
        }

        EngineFrameRecorder recorder = new();
        WorkloadAccumulator workload = new();

        for (int frame = 0; frame < measuredFrames; frame++)
        {
            long started = Stopwatch.GetTimestamp();
            RenderStats stats = host.RenderFrame();
            long presented = Stopwatch.GetTimestamp();
            double cpuMilliseconds = Stopwatch.GetElapsedTime(started, presented).TotalMilliseconds;

            recorder.RecordPresent(presented, cpuMilliseconds, stats.GpuMs);
            workload.Record(stats);
        }

        Phase0FrameMetrics metrics = recorder.Summarise();

        string captureFileName = $"{scene.Id}-dx11.png";
        string captureFile = Path.Combine(captures, captureFileName);
        string repeatFile = Path.Combine(captures, $"{scene.Id}-dx11-repeat.png");
        RuntimeImageMetrics first = host.Capture(captureFile);
        RuntimeImageMetrics second = host.Capture(repeatFile);

        Phase0ParityResult parity = EvaluateParity(
            scene,
            first,
            second,
            baselineDirectory,
            updateBaselines);

        return new Phase0SceneResult(
            scene.Id,
            scene.Name,
            scene.Dimension == GenesisSceneDimension.TwoDimensional ? "2D" : "3D",
            load.Elapsed.TotalMilliseconds,
            metrics,
            workload.Summarise(),
            parity,
            Path.Combine("Images", captureFileName));
    }

    private static Phase0ParityResult EvaluateParity(
        GenesisScene scene,
        RuntimeImageMetrics first,
        RuntimeImageMetrics second,
        string baselineDirectory,
        bool updateBaselines)
    {
        double selfDelta = RuntimeImageMetrics.MaxTileDelta(first, second);
        string baselineFile = Path.Combine(baselineDirectory, $"{scene.Id}-dx11.json");

        if (selfDelta != 0)
        {
            return new Phase0ParityResult(
                ParityOutcome.Failed,
                baselineFile,
                selfDelta,
                0,
                0,
                $"Two captures in the same run differ; the scene is not deterministic.");
        }

        if (!File.Exists(baselineFile))
        {
            WriteBaseline(baselineFile, first);
            return new Phase0ParityResult(
                ParityOutcome.Recorded,
                baselineFile,
                selfDelta,
                0,
                0,
                "No baseline existed, so the deterministic first capture was recorded.");
        }

        VisualBaseline baseline = ReadBaseline(baselineFile);
        RuntimeImageMetrics expected = baseline.ToMetrics();
        if (first.Width != expected.Width || first.Height != expected.Height)
        {
            return new Phase0ParityResult(
                ParityOutcome.Failed,
                baselineFile,
                selfDelta,
                0,
                0,
                $"Capture is {first.Width}x{first.Height}; baseline is {expected.Width}x{expected.Height}.");
        }

        if (updateBaselines)
        {
            double replacedDelta = RuntimeImageMetrics.MaxTileDelta(expected, first);
            double replacedHistogram = RuntimeImageMetrics.HistogramDistance(expected, first);
            WriteBaseline(baselineFile, first);
            return new Phase0ParityResult(
                ParityOutcome.Updated,
                baselineFile,
                selfDelta,
                replacedDelta,
                replacedHistogram,
                "Baseline replaced only because --update-baselines was supplied.");
        }

        double delta = RuntimeImageMetrics.MaxTileDelta(expected, first);
        double histogram = RuntimeImageMetrics.HistogramDistance(expected, first);
        if (delta > SameBackendTileDeltaLimit || histogram > HistogramDistanceLimit)
        {
            int worst = RuntimeImageMetrics.WorstTileIndex(expected, first);
            return new Phase0ParityResult(
                ParityOutcome.Failed,
                baselineFile,
                selfDelta,
                delta,
                histogram,
                $"Visual drift exceeded the DX11 parity gate (worst tile {worst}).");
        }

        return new Phase0ParityResult(
            ParityOutcome.Passed,
            baselineFile,
            selfDelta,
            delta,
            histogram,
            null);
    }

    private static Phase0FrameMetrics RunThermalControl(int measuredFrames)
    {
        using RuntimeViewportHarness harness = new();
        _ = harness.RenderBenchmarkFrame2D();
        for (int frame = 0; frame < 30; frame++)
        {
            _ = harness.RenderBenchmarkFrame2D();
        }

        EngineFrameRecorder recorder = new();
        for (int frame = 0; frame < measuredFrames; frame++)
        {
            long started = Stopwatch.GetTimestamp();
            RenderStats stats = harness.RenderBenchmarkFrame2D();
            long presented = Stopwatch.GetTimestamp();
            recorder.RecordPresent(
                presented,
                Stopwatch.GetElapsedTime(started, presented).TotalMilliseconds,
                stats.GpuMs);
        }

        return recorder.Summarise();
    }

    private static IPhase0SceneHost CreateSceneHost(GenesisSceneKind kind) =>
        kind switch
        {
            GenesisSceneKind.Runtime2D => new RuntimeSceneHost(twoDimensional: true),
            GenesisSceneKind.Runtime3D => new RuntimeSceneHost(twoDimensional: false),
            GenesisSceneKind.Golden3D => new GoldenSceneHost(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Phase 0 scene."),
        };

    private static long MillisecondsToTicks(double milliseconds) =>
        (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);

    private static int ReadPositiveInt(string[] args, string name, int fallback)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(args[index + 1], out int parsed) && parsed > 0)
            {
                return parsed;
            }

            throw new ArgumentException($"{name} requires a positive integer.");
        }

        return fallback;
    }

    private static string ResolveOutput(string[] args)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--output", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        string? repositoryRoot = FindRepositoryRoot();
        return repositoryRoot is null
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestResults", "Phase0", "latest"))
            : Path.Combine(repositoryRoot, "TestResults", "Phase0", "latest");
    }

    private static string ResolveBaselineDirectory(string? repositoryRoot, string outputRoot) =>
        repositoryRoot is null
            ? Path.Combine(Path.GetDirectoryName(outputRoot) ?? outputRoot, "Baselines")
            : Path.Combine(repositoryRoot, "TestResults", "Baselines", "Phase0");

    private static string? FindRepositoryRoot()
    {
        static string? SearchFrom(string start)
        {
            DirectoryInfo? probe = new(start);
            while (probe is not null)
            {
                if (File.Exists(Path.Combine(probe.FullName, "Build.bat"))
                    && File.Exists(Path.Combine(probe.FullName, "Genesis.Application.slnx")))
                {
                    return probe.FullName;
                }

                probe = probe.Parent;
            }

            return null;
        }

        return SearchFrom(AppContext.BaseDirectory) ?? SearchFrom(Environment.CurrentDirectory);
    }

    private static void WriteBaseline(string file, RuntimeImageMetrics metrics)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(VisualBaseline.From(metrics), JsonOptions));
    }

    private static VisualBaseline ReadBaseline(string file) =>
        JsonSerializer.Deserialize<VisualBaseline>(File.ReadAllText(file), JsonOptions)
        ?? throw new InvalidOperationException($"Baseline '{file}' is empty or unreadable.");

    private interface IPhase0SceneHost : IDisposable
    {
        void InitializeHost();

        void PrepareScene();

        RenderStats RenderFrame();

        RuntimeImageMetrics Capture(string outputFile);
    }

    private sealed class RuntimeSceneHost(bool twoDimensional) : IPhase0SceneHost
    {
        private readonly RuntimeViewportHarness _harness = new();

        public void InitializeHost() => _harness.InitializeBenchmarkHost();

        public void PrepareScene()
        {
            if (twoDimensional)
            {
                _harness.PrepareBenchmark2D();
            }
            else
            {
                _harness.PrepareBenchmark3D();
            }
        }

        public RenderStats RenderFrame() =>
            twoDimensional
                ? _harness.RenderBenchmarkFrame2D()
                : _harness.RenderBenchmarkFrame3D();

        public RuntimeImageMetrics Capture(string outputFile) =>
            twoDimensional
                ? _harness.Capture2D(outputFile)
                : _harness.Capture3D(outputFile);

        public void Dispose() => _harness.Dispose();
    }

    private sealed class GoldenSceneHost : IPhase0SceneHost
    {
        private readonly RenderParityHarness _harness = new();

        public void InitializeHost() => _harness.InitializeBenchmarkHost();

        public void PrepareScene() => _harness.PrepareBenchmark();

        public RenderStats RenderFrame() => _harness.RenderBenchmarkFrame();

        public RuntimeImageMetrics Capture(string outputFile) => _harness.Capture(outputFile);

        public void Dispose() => _harness.Dispose();
    }

    private sealed class WorkloadAccumulator
    {
        private long _samples;
        private long _triangles;
        private long _triangles3D;
        private long _drawCalls;
        private long _instances;
        private RenderStats _last;

        public void Record(RenderStats stats)
        {
            _samples++;
            _triangles += stats.Triangles;
            _triangles3D += stats.Triangles3D;
            _drawCalls += stats.DrawCalls;
            _instances += stats.InstancesDrawn;
            _last = stats;
        }

        public Phase0WorkloadMetrics Summarise()
        {
            if (_samples == 0)
            {
                return new Phase0WorkloadMetrics(0, 0, 0, 0, 0, 0, 0, 0);
            }

            long triangles2D = Math.Max(0, _triangles - _triangles3D);
            int finalTriangles2D = Math.Max(0, _last.Triangles - _last.Triangles3D);
            return new Phase0WorkloadMetrics(
                AverageTriangles2D: triangles2D / (double)_samples,
                AverageTriangles3D: _triangles3D / (double)_samples,
                AverageDrawCalls: _drawCalls / (double)_samples,
                AverageInstancesDrawn: _instances / (double)_samples,
                FinalTriangles2D: finalTriangles2D,
                FinalTriangles3D: _last.Triangles3D,
                FinalDrawCalls: _last.DrawCalls,
                FinalInstancesDrawn: _last.InstancesDrawn);
        }
    }

    private sealed record VisualBaseline(
        int Width,
        int Height,
        int UniqueSampledColors,
        double AverageLuminance,
        double[] Tiles,
        int[] Histogram)
    {
        public static VisualBaseline From(RuntimeImageMetrics metrics)
        {
            double[] flat = new double[metrics.Tiles.Count * 3];
            for (int index = 0; index < metrics.Tiles.Count; index++)
            {
                flat[(index * 3) + 0] = metrics.Tiles[index].R;
                flat[(index * 3) + 1] = metrics.Tiles[index].G;
                flat[(index * 3) + 2] = metrics.Tiles[index].B;
            }

            return new VisualBaseline(
                metrics.Width,
                metrics.Height,
                metrics.UniqueSampledColors,
                metrics.AverageLuminance,
                flat,
                [.. metrics.Histogram]);
        }

        public RuntimeImageMetrics ToMetrics()
        {
            TileColor[] tiles = new TileColor[Tiles.Length / 3];
            for (int index = 0; index < tiles.Length; index++)
            {
                tiles[index] = new TileColor(
                    Tiles[(index * 3) + 0],
                    Tiles[(index * 3) + 1],
                    Tiles[(index * 3) + 2]);
            }

            return new RuntimeImageMetrics(Width, Height, UniqueSampledColors, AverageLuminance)
            {
                Tiles = tiles,
                Histogram = Histogram,
            };
        }
    }
}
