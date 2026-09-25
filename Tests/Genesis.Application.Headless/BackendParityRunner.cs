using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Studio.Forms;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.Primitives;
using Genesis.Rendering.Viewport;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Shared.Interfaces;
using WinFormsApplication = System.Windows.Forms.Application;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless;

/// <summary>
/// Captures the Help → Commands visual-test scene (PGSL and Engine) plus a 10-second F5 autoshot
/// of a real project, for one backend at a time.
/// </summary>
internal static class BackendParityRunner
{
    public const float VisualSceneSeconds = 2.4f;
    public const float GameplaySeconds = 10f;
    private const int VisualWidth = 1280;
    private const int VisualHeight = 720;
    private const int GameplayTimeoutMs = 90_000;

    public static int Run(string backendName, string outputDirectory, string? projectPath)
    {
        try
        {
            RenderBackendOption backend = ParseBackend(backendName);
            RenderBackendDescriptor descriptor = RenderBackendCatalog.Describe(backend);
            if (!descriptor.IsImplemented || !RenderBackendSelection.IsAvailable(backend))
            {
                Console.Error.WriteLine($"{descriptor.DisplayName} is not available on this machine.");
                return 2;
            }

            outputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            string project = ResolveGameplayProject(projectPath);
            Console.WriteLine($"PARITY  backend={descriptor.DisplayName} ({descriptor.SettingsValue})");
            Console.WriteLine($"PARITY  output={outputDirectory}");
            Console.WriteLine($"PARITY  gameplay={project}");

            MeshRasterDefaults.Configure(FaceCullingOverride.Back, FrontFaceWindingOverride.CounterClockwise);
            RenderBackendSelection.Configure(backend);
            bool previousVSync = EditorPreviewSettings.VSync;
            EditorPreviewSettings.Configure(vsync: false);

            try
            {
                if (descriptor.ShaderBinaryFormat == GpuShaderBinaryFormat.GlslUtf8)
                {
                    Console.WriteLine("PARITY  compiling GLSL catalog…");
                    EngineShaderCatalog.CompileAll(GpuShaderBinaryFormat.GlslUtf8);
                }

                CaptureVisualTests(backend, descriptor, outputDirectory);
                CaptureGameplay(backend, descriptor, project, outputDirectory);
            }
            finally
            {
                EditorPreviewSettings.Configure(previousVSync);
            }

            Console.WriteLine($"PARITY PASSED — {descriptor.DisplayName}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PARITY FAILED — {backendName}");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void CaptureVisualTests(
        RenderBackendOption backend,
        RenderBackendDescriptor descriptor,
        string outputDirectory)
    {
        using HeadlessHostForm host = UnattendedWindowing.NewHost(VisualWidth, VisualHeight);
        CommandVisualTestPanel visual = new();
        host.Controls.Add(visual);
        visual.Viewport.BackendOverride = backend;
        visual.Viewport.Host.DriveMode = ViewportDriveMode.External;
        visual.FreezeSceneClock(VisualSceneSeconds);
        UnattendedWindowing.ShowWithoutFocus(host);
        PumpUntilReady(visual, descriptor.DisplayName);

        visual.SetVisualMode(engine: false);
        visual.ClearLive();
        visual.SetStatus("PGSL GAME CODE", descriptor.ShortName, "DX11 baseline");
        visual.FreezeSceneClock(VisualSceneSeconds);
        SaveVisualFrame(
            visual,
            Path.Combine(outputDirectory, "visual-pgsl.png"),
            $"{descriptor.DisplayName} PGSL Game Code visual test");
        if (!visual.PgslBaselineReady)
        {
            throw new InvalidOperationException(
                "PGSL Game Code baseline did not compile or execute.");
        }

        visual.SetVisualMode(engine: true);
        visual.ClearLive();
        visual.SetStatus("ENGINE API", descriptor.ShortName, "DX11 baseline");
        visual.FreezeSceneClock(VisualSceneSeconds);
        SaveVisualFrame(
            visual,
            Path.Combine(outputDirectory, "visual-engine.png"),
            $"{descriptor.DisplayName} Engine API visual test");
    }

    private static void PumpUntilReady(CommandVisualTestPanel visual, string backendName)
    {
        const int timeoutMs = 8000;
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            WinFormsApplication.DoEvents();
            if (visual.Viewport.Host.Renderer?.IsFramebufferReady == true)
            {
                using Bitmap? probe = visual.Viewport.CaptureFrame(settleFrames: 1);
                if (probe is not null && probe.Width > 8 && probe.Height > 8)
                    return;
            }

            Thread.Sleep(40);
        }

        string fault = visual.Viewport.Host.LastRenderException?.ToString() ?? "no framebuffer";
        throw new InvalidOperationException(
            $"{backendName} visual-test viewport did not become ready: {fault}");
    }

    private static void SaveVisualFrame(CommandVisualTestPanel visual, string path, string label)
    {
        using Bitmap? frame = visual.Viewport.CaptureFrame(settleFrames: 4);
        if (frame is null)
        {
            string fault = visual.Viewport.Host.LastRenderException?.ToString() ?? "readback returned null";
            throw new InvalidOperationException($"{label} capture failed: {fault}");
        }

        if (visual.Viewport.Host.LastRenderException is Exception renderFault)
            throw new InvalidOperationException($"{label} render fault: {renderFault}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        frame.Save(path, ImageFormat.Png);
        RuntimeImageMetrics metrics = RuntimeImageMetrics.Measure(frame);
        WriteMetrics(path, metrics);
        Console.WriteLine(
            $"PARITY  {label}: {metrics.Width}x{metrics.Height}, "
            + $"{metrics.UniqueSampledColors} colours, lum={metrics.AverageLuminance:F1}");
        Console.WriteLine($"PARITY  wrote {path}");
        if (metrics.UniqueSampledColors < 16)
        {
            throw new InvalidOperationException(
                $"{label} looks blank/flat ({metrics.UniqueSampledColors} sampled colours).");
        }
    }

    private static void CaptureGameplay(
        RenderBackendOption backend,
        RenderBackendDescriptor descriptor,
        string projectPath,
        string outputDirectory)
    {
        string runtimeDir = RuntimePaths.ResolveRuntimeDir()
            ?? throw new InvalidOperationException(
                "GenesisEngine.exe was not found. Build the player (Build.bat) first.");

        ProjectRunLauncher.CompileOutcome compile = ProjectRunLauncher.CompileScripts(projectPath, runtimeDir);
        if (!compile.Success)
        {
            throw new InvalidOperationException(
                "Gameplay script compile failed: " + (compile.ErrorMessage ?? "unknown"));
        }

        string imagesDir = ProjectPaths.ImagesDir(projectPath);
        ProjectPaths.EnsureDebugDirs(projectPath);
        DateTime launchUtc = DateTime.UtcNow.AddSeconds(-1);

        Dictionary<string, string> environment = EngineRenderingDefaults.BuildEnvironment(backend);
        environment[RenderBackendSelection.EnvironmentVariable] = descriptor.SettingsValue;
        environment[MeshRasterDefaults.CullingEnvironmentVariable] = FaceCullingOverride.Back.ToString();
        environment[MeshRasterDefaults.WindingEnvironmentVariable] = FrontFaceWindingOverride.CounterClockwise.ToString();
        environment["GENESIS_AUTOSHOT"] = GameplaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment["GENESIS_PERF_LABEL"] = $"gameplay-{descriptor.ShortName}";
        environment["GENESIS_UNATTENDED_WINDOW"] = "1";

        Console.WriteLine($"PARITY  launching GenesisEngine.exe for {GameplaySeconds:0}s…");
        ProjectRunLauncher.LaunchOutcome launch = ProjectRunLauncher.Launch(
            projectPath,
            roomName: null,
            runtimeDir: runtimeDir,
            waitForExit: false,
            extraEnvironment: environment);
        if (!launch.Success || launch.Process is null)
        {
            throw new InvalidOperationException(
                "Gameplay launch failed: " + (launch.ErrorMessage ?? "unknown"));
        }

        using Process player = launch.Process;
        if (!player.WaitForExit(GameplayTimeoutMs))
        {
            try { player.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new InvalidOperationException(
                $"GenesisEngine.exe did not exit within {GameplayTimeoutMs} ms of a {GameplaySeconds:0}s autoshot.");
        }

        if (player.ExitCode != 0)
        {
            string log = Path.Combine(ProjectPaths.LogsDir(projectPath), "project_player.log");
            string tail = File.Exists(log) ? File.ReadAllText(log) : "(no player log)";
            throw new InvalidOperationException(
                $"GenesisEngine.exe exited {player.ExitCode}. Log:\n{tail}");
        }

        string[] shots = Directory.Exists(imagesDir)
            ? Directory.EnumerateFiles(imagesDir, "*.png")
                .Where(path => File.GetLastWriteTimeUtc(path) >= launchUtc)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray()
            : [];
        if (shots.Length == 0)
        {
            throw new InvalidOperationException(
                $"No new gameplay screenshot appeared in {imagesDir}.");
        }

        string destination = Path.Combine(outputDirectory, "gameplay.png");
        File.Copy(shots[0], destination, overwrite: true);
        using Bitmap gameplay = new(destination);
        RuntimeImageMetrics metrics = RuntimeImageMetrics.Measure(gameplay);
        WriteMetrics(destination, metrics);
        Console.WriteLine(
            $"PARITY  Gameplay: {metrics.Width}x{metrics.Height}, "
            + $"{metrics.UniqueSampledColors} colours, lum={metrics.AverageLuminance:F1}");
        Console.WriteLine($"PARITY  wrote {destination}");
        if (metrics.UniqueSampledColors < 16)
        {
            throw new InvalidOperationException(
                $"Gameplay capture looks blank/flat ({metrics.UniqueSampledColors} sampled colours).");
        }
    }

    private static void WriteMetrics(string imagePath, RuntimeImageMetrics metrics)
    {
        string jsonPath = Path.ChangeExtension(imagePath, ".metrics.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
        {
            metrics.Width,
            metrics.Height,
            metrics.UniqueSampledColors,
            metrics.AverageLuminance,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ResolveGameplayProject(string? projectPath)
    {
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            string full = Path.GetFullPath(projectPath);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException(full);
            return full;
        }

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string world = Path.Combine(documents, "Genesis Projects", "My 3D World");
        if (!Directory.Exists(world))
        {
            throw new DirectoryNotFoundException(
                "Expected gameplay project at " + world);
        }

        return world;
    }

    private static RenderBackendOption ParseBackend(string name)
    {
        string slug = BackendSmokeRunner.Normalize(name);
        return slug switch
        {
            "dx11" => RenderBackendOption.SilkNetDx11,
            "dx12" => RenderBackendOption.Direct3D12,
            "vulkan" => RenderBackendOption.Vulkan,
            "opengl" => RenderBackendOption.OpenGL,
            "software" => RenderBackendOption.Software,
            _ => throw new ArgumentException($"Unknown backend '{name}'.", nameof(name)),
        };
    }
}
