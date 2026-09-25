namespace Genesis.Application.Headless.Phase0;

internal sealed record Phase0WorkloadMetrics(
    double AverageTriangles2D,
    double AverageTriangles3D,
    double AverageDrawCalls,
    double AverageInstancesDrawn,
    int FinalTriangles2D,
    int FinalTriangles3D,
    int FinalDrawCalls,
    int FinalInstancesDrawn);

internal enum ParityOutcome
{
    Passed,
    Recorded,
    Updated,
    Failed,
}

internal sealed record Phase0ParityResult(
    ParityOutcome Outcome,
    string BaselineFile,
    double SelfDelta,
    double BaselineDelta,
    double HistogramDistance,
    string? Detail);

internal sealed record Phase0SceneResult(
    string Id,
    string Name,
    string Dimension,
    double LoadMilliseconds,
    Phase0FrameMetrics Frames,
    Phase0WorkloadMetrics Workload,
    Phase0ParityResult Parity,
    string CaptureFile);

internal sealed record ThermalDriftResult(
    double StartAverageFps,
    double EndAverageFps,
    double PercentChange);

internal sealed class Phase0RunReport
{
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public required string MachineName { get; init; }
    public required string OsDescription { get; init; }
    public required string RuntimeVersion { get; init; }
    public required string OutputDirectory { get; init; }
    public int MeasuredFramesPerScene { get; init; }
    public int WarmupFramesPerScene { get; init; }
    public bool UpdateBaselines { get; init; }
    public bool Passed { get; set; }
    public List<ComponentProbeResult> Probes { get; } = [];
    public List<Phase0SceneResult> Scenes { get; } = [];
    public ThermalDriftResult? ThermalDrift { get; set; }
}
