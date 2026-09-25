using System.Text;
using DevProfiler.Models;
using System.IO;

namespace DevProfiler.Services;

public static class MarkdownExporter
{
    public static async Task ExportAsync(ProfileSession session, string outputPath, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        string targetName = Path.GetFileName(session.Target.TargetPath);
        text.AppendLine($"# DevProfiler Report — `{targetName}`");
        text.AppendLine();
        text.AppendLine($"> Generated: **{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}**  ");
        text.AppendLine($"> Language: **{session.Target.Language}**  ");
        text.AppendLine($"> Target: `{session.Target.TargetPath}`");
        text.AppendLine();

        text.AppendLine("## Executive Summary");
        text.AppendLine();
        text.AppendLine("| Metric | Value |");
        text.AppendLine("|:--|--:|");
        text.AppendLine($"| Runtime | `{session.ElapsedSeconds:N3}s` |");
        text.AppendLine($"| Processes observed | `{session.Processes.Select(x => x.ProcessId).Distinct().Count()}` |");
        text.AppendLine($"| Functions/methods | `{session.Functions.Count:N0}` |");
        text.AppendLine($"| Events | `{session.Events.Count:N0}` |");
        if (session.Metrics.Count > 0)
        {
            text.AppendLine($"| Peak target CPU | `{session.Metrics.Max(x => x.CpuPercent):N1}%` |");
            text.AppendLine($"| Peak private memory | `{session.Metrics.Max(x => x.PrivateMemoryMb):N0} MB` |");
            IEnumerable<double> fps = session.Metrics.Where(x => x.Fps > 0).Select(x => x.Fps);
            if (fps.Any())
                text.AppendLine($"| Average FPS | `{fps.Average():N1}` |");
        }
        text.AppendLine();

        text.AppendLine("## Health Warnings");
        text.AppendLine();
        foreach (HealthWarning warning in session.Warnings)
        {
            text.AppendLine($"### {warning.SeverityDisplay} — {warning.Title}");
            text.AppendLine();
            text.AppendLine(warning.Body);
            text.AppendLine();
        }

        text.AppendLine("## Process Inventory");
        text.AppendLine();
        text.AppendLine("| PID | PPID | Process | Peak/last CPU | Private MB | Working Set MB | Threads | Handles | Read MB | Write MB |");
        text.AppendLine("|--:|--:|:--|--:|--:|--:|--:|--:|--:|--:|");
        foreach (ProcessSnapshot process in session.Processes.OrderBy(x => x.ProcessId))
        {
            text.AppendLine($"| {process.ProcessId} | {process.ParentProcessId} | `{Escape(process.Name)}` | {process.CpuPercent:N1}% | {process.PrivateMemoryMb:N1} | {process.WorkingSetMb:N1} | {process.Threads} | {process.Handles} | {process.ReadMb:N1} | {process.WriteMb:N1} |");
        }
        text.AppendLine();

        text.AppendLine("## Top Hotspots");
        text.AppendLine();
        text.AppendLine("| Rank | Process | Calls / Samples | Self ms | Inclusive ms | File | Function | Category | Source | Accuracy |");
        text.AppendLine("|--:|:--|--:|--:|--:|:--|:--|:--|:--|:--|");
        foreach (FunctionProfile row in session.Functions.Take(200))
        {
            text.AppendLine($"| {row.Rank} | `{Escape(row.Process)}` | {Escape(row.CallsDisplay)} | {row.SelfMs:N3} | {row.InclusiveMs:N3} | `{Escape(row.File)}` | `{Escape(row.Function)}` | {row.Category} | {row.Source} | {row.Accuracy} |");
        }
        text.AppendLine();

        text.AppendLine("## Event Log");
        text.AppendLine();
        text.AppendLine("| Time | PID | Category | Source | Name | Details |");
        text.AppendLine("|:--|--:|:--|:--|:--|:--|");
        foreach (DiagnosticEvent item in session.Events.TakeLast(500))
        {
            text.AppendLine($"| `{item.TimeDisplay}` | {item.ProcessDisplay} | {Escape(item.Category)} | {Escape(item.Source)} | {Escape(item.Name)} | {Escape(item.Details)} |");
        }
        text.AppendLine();

        text.AppendLine("## Raw Output");
        text.AppendLine();
        text.AppendLine("```text");
        text.AppendLine(session.RawOutput.Length > 50000 ? session.RawOutput[..50000] : session.RawOutput);
        text.AppendLine("```");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        await File.WriteAllTextAsync(outputPath, text.ToString(), Encoding.UTF8, cancellationToken);
    }

    private static string Escape(string? value) => (value ?? string.Empty)
        .Replace("|", "\\|")
        .Replace("\r", " ")
        .Replace("\n", "<br>");
}
