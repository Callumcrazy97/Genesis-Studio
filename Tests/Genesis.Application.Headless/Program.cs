using System.Text;
using System.Text.Json;
using Genesis.Application.Runtime;

namespace Genesis.Application.Headless;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        StreamWriter stdout = new(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
        Console.SetOut(stdout);
        StreamWriter stderr = new(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
        Console.SetError(stderr);

        Genesis.Application.Core.Diagnostics.UnattendedSession.Enable();
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.SystemAware);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length>=3 && args[0]=="--rig-review") return RigReviewCaptureRunner.Run(Path.GetFullPath(args[1]),Path.GetFullPath(args[2]));
        if (args.Length>=2 && args[0]=="--layer-frame-review") return LayerFrameCaptureRunner.Run(Path.GetFullPath(args[1]));
        if (args.Length>=2 && args[0]=="--model-workspace-review") return ModelWorkspaceCaptureRunner.Run(Path.GetFullPath(args[1]));
        if (args.Length>=2 && args[0]=="--organic-water-review") return OrganicWaterCaptureRunner.Run(Path.GetFullPath(args[1]));

        if (args.Length >= 2 && args[0] == "--runner-lifecycle") return RunnerLifecycleRunner.Run(Path.GetFullPath(args[1]));
        if (args.Length >= 3 && args[0] == "--runner-parent-fixture") return RunnerLifecycleRunner.ParentFixture(Path.GetFullPath(args[1]),args[2]);

        if (args.Any(argument => string.Equals(argument, "--shell-layout", StringComparison.OrdinalIgnoreCase)))
            return Suites.ShellLayoutSuite.RunFocused();

        if (args.Any(argument => string.Equals(argument, "--resource-search", StringComparison.OrdinalIgnoreCase)))
            return Suites.ResourceSearchSuite.RunFocused();

        if (args.Any(argument => string.Equals(argument, "--image-layout-captures", StringComparison.OrdinalIgnoreCase)))
        {
            int outputIndex = Array.FindIndex(args, argument =>
                string.Equals(argument, "--output", StringComparison.OrdinalIgnoreCase));
            string output = outputIndex >= 0 && outputIndex + 1 < args.Length
                ? Path.GetFullPath(args[outputIndex + 1])
                : Path.Combine(AppContext.BaseDirectory, "TestResults", "ImageLayout");
            int width = ReadPositiveInt(args, "--width", 1360);
            int height = ReadPositiveInt(args, "--height", 840);
            return ImageLayoutCaptureRunner.Run(output, width, height);
        }

        int editorReviewIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, "--editor-review-captures", StringComparison.OrdinalIgnoreCase));
        if (editorReviewIndex >= 0)
        {
            if (editorReviewIndex + 1 >= args.Length
                || args[editorReviewIndex + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "Usage: --editor-review-captures <project-root> [--output <directory>] [--width <pixels>] [--height <pixels>]");
                return 2;
            }

            string projectRoot = Path.GetFullPath(args[editorReviewIndex + 1]);
            int outputIndex = Array.FindIndex(args, argument =>
                string.Equals(argument, "--output", StringComparison.OrdinalIgnoreCase));
            string output = outputIndex >= 0 && outputIndex + 1 < args.Length
                ? Path.GetFullPath(args[outputIndex + 1])
                : Path.Combine(AppContext.BaseDirectory, "TestResults", "EditorReview", "Images");
            int width = ReadPositiveInt(args, "--width", 1360);
            int height = ReadPositiveInt(args, "--height", 840);
            return EditorReviewCaptureRunner.Run(projectRoot, output, width, height);
        }

        if (args.Any(argument => string.Equals(argument, "--climbing-smoke", StringComparison.OrdinalIgnoreCase)))
        {
            string output = Path.Combine(AppContext.BaseDirectory, "TestResults", "ClimbingSmoke");
            return ClimbingSmokeRunner.Run(output);
        }

        int backendParityIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, "--parity", StringComparison.OrdinalIgnoreCase));
        if (backendParityIndex >= 0)
        {
            if (backendParityIndex + 1 >= args.Length
                || args[backendParityIndex + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    "Usage: --parity <dx11|dx12|vulkan|opengl|software> "
                    + "[--output <directory>] [--project <path>]");
                return 2;
            }

            string backend = args[backendParityIndex + 1];
            int outputIndex = Array.FindIndex(args, argument =>
                string.Equals(argument, "--output", StringComparison.OrdinalIgnoreCase));
            string output = outputIndex >= 0 && outputIndex + 1 < args.Length
                ? Path.GetFullPath(args[outputIndex + 1])
                : Path.Combine(
                    AppContext.BaseDirectory,
                    "TestResults",
                    "BackendParity",
                    BackendSmokeRunner.Normalize(backend));
            int projectIndex = Array.FindIndex(args, argument =>
                string.Equals(argument, "--project", StringComparison.OrdinalIgnoreCase));
            string? project = projectIndex >= 0 && projectIndex + 1 < args.Length
                ? Path.GetFullPath(args[projectIndex + 1])
                : null;
            return BackendParityRunner.Run(backend, output, project);
        }

        int backendSmokeIndex = Array.FindIndex(args, argument =>
            string.Equals(argument, "--backend-smoke", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "--backend", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "-backend", StringComparison.OrdinalIgnoreCase));
        if (backendSmokeIndex >= 0)
        {
            if (backendSmokeIndex + 1 >= args.Length || args[backendSmokeIndex + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("Usage: --backend <dx11|dx12|vulkan|opengl|software> [--output <directory>]");
                return 2;
            }

            string backend = args[backendSmokeIndex + 1];
            int outputIndex = Array.FindIndex(args, argument => string.Equals(argument, "--output", StringComparison.OrdinalIgnoreCase));
            string output = outputIndex >= 0 && outputIndex + 1 < args.Length
                ? Path.GetFullPath(args[outputIndex + 1])
                : Path.Combine(AppContext.BaseDirectory, "TestResults", "BackendSmoke", backend);
            int exitCode = BackendSmokeRunner.Run(backend, output);
            Environment.Exit(exitCode);
            return exitCode;
        }

        if (args.Any(argument => string.Equals(argument, "--phase0-baseline", StringComparison.OrdinalIgnoreCase)))
            return Phase0.Phase0BaselineRunner.Run(args);

        int benchmarkIndex = Array.FindIndex(args, argument => string.Equals(argument, "--pgsl-benchmark", StringComparison.OrdinalIgnoreCase));
        if (benchmarkIndex >= 0)
        {
            PgslPerformanceReport report = PgslPerformanceSuite.Run(samples: 7);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            if (benchmarkIndex + 1 < args.Length && !args[benchmarkIndex + 1].StartsWith("--", StringComparison.Ordinal))
            {
                string outputPath = Path.GetFullPath(args[benchmarkIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, json);
            }
            return 0;
        }

        if (args.Any(a => string.Equals(a, "--showcase", StringComparison.OrdinalIgnoreCase)))
        {
            string output = Path.Combine(AppContext.BaseDirectory, "TestResults", "Showcase");
            return Showcase.ShowcaseRunner.Run(output);
        }

        return HeadlessTestRunner.Run(args);
    }

    private static int ReadPositiveInt(string[] args, string option, int fallback)
    {
        int index = Array.FindIndex(args, argument =>
            string.Equals(argument, option, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return fallback;
        }

        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value) || value <= 0)
        {
            throw new ArgumentException($"{option} requires a positive integer value.");
        }

        return value;
    }
}
