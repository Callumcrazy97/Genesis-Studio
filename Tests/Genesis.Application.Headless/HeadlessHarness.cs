using System.Diagnostics;
using System.Text.Json;

namespace Genesis.Application.Headless;

internal sealed class TestReport
{
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public required string MachineName { get; set; }
    public required string RuntimeVersion { get; set; }
    public required string OutputDirectory { get; set; }
    public bool Passed { get; set; }
    public List<TestCaseResult> Tests { get; } = [];
    public List<ImageResult> Images { get; } = [];
    public string? CurrentMajor { get; set; }
    public bool FastBuildGate { get; set; }
    public string Profile { get; set; } = "Full Regression";
}

internal sealed record TestCaseResult(
    string Major,
    string Name,
    bool Passed,
    long DurationMilliseconds,
    string? Error);

internal sealed record ImageResult(
    string Name,
    string File,
    int Width,
    int Height,
    int UniqueSampledColors,
    double AverageLuminance)
{
    public static ImageResult From(string name, string file, ImageMetrics metrics) =>
        new(name, file, metrics.Width, metrics.Height, metrics.UniqueSampledColors, metrics.AverageLuminance);
}

internal sealed record ResourceFixture(
    string CharactersFolder,
    string GameplayFolder,
    string DestinationFolder,
    string HeroObject,
    string HeroCopy,
    string Script,
    string Note);

internal sealed class HeadlessContext
{
    public required TestReport Report { get; init; }
    public required string OutputRoot { get; init; }
    public required string Workspace { get; init; }
    public required string Captures { get; init; }
    public required string Logs { get; init; }

    /// <summary>
    /// True when the run was started with <c>--update-baselines</c>. Visual baselines are only ever
    /// overwritten deliberately: a gate that silently re-records whatever the code now produces
    /// cannot catch a regression, it just adopts it.
    /// </summary>
    public bool UpdateBaselines { get; init; }
    public Genesis.Application.Core.Projects.ProjectSession? Project { get; set; }
    public Genesis.Application.Core.Resources.ResourceService? Resources { get; set; }
    public Genesis.Application.Studio.StudioServices? StudioServices { get; set; }
    public ResourceFixture? Fixture { get; set; }
}

internal static class HeadlessHarness
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void BeginMajor(TestReport report, string major)
    {
        report.CurrentMajor = major;
        Console.WriteLine();
        WriteConsoleLine($"=== {major} ===", ConsoleColor.Yellow);
    }

    public static void RunCase(TestReport report, string name, Action action)
    {
        string major = report.CurrentMajor ?? InferMajor(name);
        Stopwatch stopwatch = Stopwatch.StartNew();
        WriteConsoleLine($"BEGIN {name}", ConsoleColor.Cyan);
        try
        {
            action();
            stopwatch.Stop();
            report.Tests.Add(new TestCaseResult(major, name, true, stopwatch.ElapsedMilliseconds, null));
            WriteConsoleLine($"PASS  {name} ({stopwatch.ElapsedMilliseconds} ms)", ConsoleColor.Green);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            report.Tests.Add(
                new TestCaseResult(major, name, false, stopwatch.ElapsedMilliseconds, exception.ToString()));
            WriteConsoleLine($"FAIL  {name}: {exception.Message}", ConsoleColor.Red);
        }
    }

    private static void WriteConsoleLine(string message, ConsoleColor color)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(message);
            Console.Out.Flush();
            return;
        }

        ConsoleColor previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = previous;
            Console.Out.Flush();
        }
    }

    /// <summary>
    /// Runs one named stage of a consolidated case, so a failure says which stage failed.
    /// </summary>
    /// <remarks>
    /// The build gate is one test per editor rather than thirty small ones, which only works if a
    /// failure is still pinpointable. Steps give that: the case is the unit of reporting, the step is
    /// the unit of blame. The step label is prepended to whatever the assertion said, and the
    /// original exception is kept as the inner one so the stack survives.
    /// </remarks>
    public static void Step(string label, Action action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"[{label}] {exception.Message}", exception);
        }
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static T Require<T>(T? value, string description) where T : class =>
        value ?? throw new InvalidOperationException(
            $"{description} is unavailable because an earlier test failed.");

    public static void AssertPixelsEqual(byte[] actual, byte[] expected, string label)
    {
        Assert(actual.Length == expected.Length,
            $"{label}: length mismatch actual={actual.Length} expected={expected.Length}.");
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] == expected[i]) continue;
            int pixel = i / 4;
            throw new InvalidOperationException(
                $"{label}: byte[{i}] (pixel {pixel} ch {i % 4}) actual={actual[i]} expected={expected[i]}.");
        }
    }

    public static void AssertPixel(byte[] rgba, int width, int height, int x, int y, Color expected, string label)
    {
        Color actual = Genesis.Application.Editors.Image.Imaging.RasterOperations.GetPixel(
            rgba, width, height, x, y);
        Assert(
            actual.R == expected.R && actual.G == expected.G && actual.B == expected.B && actual.A == expected.A,
            $"{label}: ({x},{y}) actual={actual} expected={expected}.");
    }

    public static void WriteSummary(TestReport report, string file)
    {
        List<string> lines =
        [
            "Genesis Application Headless Tests",
            $"Profile: {report.Profile}",
            $"Passed: {report.Passed}",
            $"Started: {report.StartedUtc:O}",
            $"Completed: {report.CompletedUtc:O}",
            string.Empty,
        ];

        foreach (IGrouping<string, TestCaseResult> group in report.Tests.GroupBy(test => test.Major))
        {
            lines.Add($"=== {group.Key} ===");
            foreach (TestCaseResult test in group)
            {
                lines.Add(
                    $"  {(test.Passed ? "PASS" : "FAIL")} {test.Name} ({test.DurationMilliseconds} ms)" +
                    (string.IsNullOrWhiteSpace(test.Error) ? string.Empty : Environment.NewLine + test.Error));
            }

            lines.Add(string.Empty);
        }

        lines.AddRange(report.Images.Select(image => $"IMAGE {image.File} {image.Width}x{image.Height}"));
        File.WriteAllLines(file, lines);
    }

    private static string InferMajor(string name)
    {
        if (name.StartsWith("Runtime.", StringComparison.Ordinal)) return "Runtime";
        if (name.StartsWith("Render.", StringComparison.Ordinal)) return "Render";
        if (name.StartsWith("Shell.", StringComparison.Ordinal)) return "Shell";
        if (name.StartsWith("Editor.Suite.", StringComparison.Ordinal)) return "Editor.Suite";
        if (name.StartsWith("Editor.Image.", StringComparison.Ordinal)) return "Editor.Image";
        if (name.StartsWith("Image.", StringComparison.Ordinal)) return "Editor.Image";
        if (name.StartsWith("UI.", StringComparison.Ordinal) ||
            name.StartsWith("Project.", StringComparison.Ordinal) ||
            name.StartsWith("Resources.", StringComparison.Ordinal) ||
            name.StartsWith("Services.", StringComparison.Ordinal) ||
            name.StartsWith("Assets.", StringComparison.Ordinal) ||
            name.StartsWith("Editor.Foundation", StringComparison.Ordinal))
        {
            return "Shell";
        }

        return "Other";
    }
}
