using System.Text;

namespace Genesis.Application.Headless.Phase0;

internal static class Phase0ReportWriter
{
    public static void WriteMarkdown(Phase0RunReport run, string file)
    {
        StringBuilder report = new();
        report.AppendLine("# Genesis Studio — Phase 0 baseline");
        report.AppendLine();
        report.AppendLine($"Generated: {run.CompletedUtc:O}");
        report.AppendLine($"Machine: `{Escape(run.MachineName)}`");
        report.AppendLine($"OS: {Escape(run.OsDescription)}");
        report.AppendLine($"Runtime: `{Escape(run.RuntimeVersion)}`");
        report.AppendLine();

        report.AppendLine("## Gate policy");
        report.AppendLine();
        report.AppendLine(
            "Performance values are **baseline-only in Phase 0**. No FPS, load-time or triangle "
            + "threshold is guessed before the first real-machine measurements exist. Correctness "
            + "probes and visual parity can fail the run.");
        report.AppendLine();
        report.AppendLine(
            "Screenshot/readback instrumentation runs after frame sampling, so it is excluded from "
            + "the 1% low and p99 statistics.");
        report.AppendLine();

        report.AppendLine("## Component probes");
        report.AppendLine();
        report.AppendLine("| Component | Probe | Result | Time | Measure |");
        report.AppendLine("|---|---|---:|---:|---|");
        foreach (ComponentProbeResult probe in run.Probes)
        {
            string outcome = probe.Outcome switch
            {
                ProbeOutcome.Passed => "PASS",
                ProbeOutcome.Skipped => "SKIP",
                _ => "FAIL",
            };
            string detail = string.IsNullOrWhiteSpace(probe.Detail)
                ? string.Empty
                : $" — {Escape(probe.Detail!)}";
            report.AppendLine(
                $"| {Escape(probe.Component)} | {Escape(probe.Name)} | {outcome} | "
                + $"{probe.Milliseconds:0.00} ms | {Escape(probe.Measure)}{detail} |");
        }
        report.AppendLine();

        report.AppendLine("## Scene baseline");
        report.AppendLine();
        report.AppendLine(
            $"Each scene uses {run.WarmupFramesPerScene} warm-up frames followed by "
            + $"{run.MeasuredFramesPerScene} measured presented frames.");
        report.AppendLine();
        report.AppendLine(
            "| Scene | Dim. | Avg FPS | 1% low | Frame ms avg / p99 | CPU / GPU ms | "
            + "Load ms | Avg tris 2D / 3D | Parity |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (Phase0SceneResult scene in run.Scenes)
        {
            Phase0FrameMetrics frames = scene.Frames;
            report.AppendLine(
                $"| {Escape(scene.Name)} | {Escape(scene.Dimension)} | {frames.AverageFps:0.0} | "
                + $"{frames.OnePercentLowFps:0.0} | "
                + $"{frames.AverageFrameMilliseconds:0.00} / {frames.NinetyNinthPercentileFrameMilliseconds:0.00} | "
                + $"{frames.AverageCpuFrameMilliseconds:0.00} / {frames.AverageGpuFrameMilliseconds:0.00} | "
                + $"{scene.LoadMilliseconds:0.0} | "
                + $"{scene.Workload.AverageTriangles2D:0} / {scene.Workload.AverageTriangles3D:0} | "
                + $"{scene.Parity.Outcome} |");
        }
        report.AppendLine();

        report.AppendLine("### Submitted work");
        report.AppendLine();
        report.AppendLine("| Scene | Final 2D tris | Final 3D tris | Draw calls | Instances | Capture |");
        report.AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (Phase0SceneResult scene in run.Scenes)
        {
            report.AppendLine(
                $"| {Escape(scene.Name)} | {scene.Workload.FinalTriangles2D:N0} | "
                + $"{scene.Workload.FinalTriangles3D:N0} | {scene.Workload.FinalDrawCalls:N0} | "
                + $"{scene.Workload.FinalInstancesDrawn:N0} | `{Escape(scene.CaptureFile)}` |");
        }
        report.AppendLine();

        report.AppendLine("## Visual parity");
        report.AppendLine();
        foreach (Phase0SceneResult scene in run.Scenes)
        {
            Phase0ParityResult parity = scene.Parity;
            report.Append($"- **{Escape(scene.Name)}:** {parity.Outcome}. ");
            report.Append(
                $"self delta {parity.SelfDelta:0.00}/255; baseline delta "
                + $"{parity.BaselineDelta:0.00}/255; histogram {parity.HistogramDistance:P2}.");
            if (!string.IsNullOrWhiteSpace(parity.Detail))
            {
                report.Append($" {Escape(parity.Detail!)}");
            }
            report.AppendLine();
        }
        report.AppendLine();

        if (run.ThermalDrift is ThermalDriftResult drift)
        {
            report.AppendLine("## Within-run thermal drift");
            report.AppendLine();
            report.AppendLine(
                $"The same lightweight 2D control sample ran before and after the catalogue: "
                + $"{drift.StartAverageFps:0.0} → {drift.EndAverageFps:0.0} FPS "
                + $"({drift.PercentChange:+0.0;-0.0;0.0}%).");
            report.AppendLine(
                "Use this to judge whether the machine changed underneath the benchmark. Compare "
                + "performance changes within the same run; do not treat results from separate runs "
                + "as directly comparable when thermal/power state differs.");
            report.AppendLine();
        }

        report.AppendLine("## Result");
        report.AppendLine();
        report.AppendLine(run.Passed ? "**PASS**" : "**FAIL**");
        report.AppendLine();

        string[] failures = run.Probes
            .Where(static probe => probe.Outcome == ProbeOutcome.Failed)
            .Select(probe => $"Probe {probe.Component}/{probe.Name}: {probe.Detail}")
            .Concat(run.Scenes
                .Where(static scene => scene.Parity.Outcome == ParityOutcome.Failed)
                .Select(scene => $"Parity {scene.Name}: {scene.Parity.Detail}"))
            .ToArray();

        if (failures.Length > 0)
        {
            report.AppendLine("Failures:");
            foreach (string failure in failures)
            {
                report.AppendLine($"- {Escape(failure)}");
            }
            report.AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, report.ToString());
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
