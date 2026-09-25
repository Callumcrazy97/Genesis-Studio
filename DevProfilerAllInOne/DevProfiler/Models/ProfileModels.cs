using System.Collections.ObjectModel;

namespace DevProfiler.Models;

public enum TargetLanguage
{
    Python,
    DotNet,
    PowerShell
}

public enum ProfileAccuracy
{
    Exact,
    Sampled,
    Metric,
    Unavailable
}

public enum DiagnosticSeverity
{
    Info,
    Success,
    Warning,
    Critical
}

public sealed class ProfileTarget
{
    public TargetLanguage Language { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string RuntimePath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

public sealed class FunctionProfile
{
    public int Rank { get; set; }
    public int ProcessId { get; set; }
    public string Process { get; set; } = string.Empty;
    public long? Calls { get; set; }
    public long Samples { get; set; }
    public double SelfMs { get; set; }
    public double InclusiveMs { get; set; }
    public double PerCallMs => Calls is > 0 ? SelfMs / Calls.Value : 0;
    public string File { get; set; } = string.Empty;
    public int? Line { get; set; }
    public string Function { get; set; } = string.Empty;
    public string Category { get; set; } = "LOGIC";
    public string Source { get; set; } = string.Empty;
    public ProfileAccuracy Accuracy { get; set; }
    public string CallsDisplay => Calls?.ToString("N0") ?? (Samples > 0 ? $"{Samples:N0} samples" : "—");
}

public sealed class ProcessSnapshot
{
    public int ProcessId { get; set; }
    public int ParentProcessId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public double CpuPercent { get; set; }
    public double PrivateMemoryMb { get; set; }
    public double WorkingSetMb { get; set; }
    public int Threads { get; set; }
    public int Handles { get; set; }
    public double ReadMb { get; set; }
    public double WriteMb { get; set; }
    public double ReadMbPerSecond { get; set; }
    public double WriteMbPerSecond { get; set; }
    public bool HasExited { get; set; }
}

public sealed class MetricSample
{
    public double TimeSeconds { get; set; }
    public double CpuPercent { get; set; }
    public double PrivateMemoryMb { get; set; }
    public double WorkingSetMb { get; set; }
    public double Fps { get; set; }
    public int Threads { get; set; }
    public int ProcessCount { get; set; }
    public double ReadMbPerSecond { get; set; }
    public double WriteMbPerSecond { get; set; }
}

public sealed class DiagnosticEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public int? ProcessId { get; set; }
    public int? ThreadId { get; set; }
    public string Category { get; set; } = "SYSTEM";
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public DiagnosticSeverity Severity { get; set; }
    public string TimeDisplay => Timestamp.ToString("HH:mm:ss.fff");
    public string ProcessDisplay => ProcessId?.ToString() ?? "—";
}

public sealed class HealthWarning
{
    public DiagnosticSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string SeverityDisplay => Severity switch
    {
        DiagnosticSeverity.Critical => "CRITICAL",
        DiagnosticSeverity.Warning => "WARNING",
        DiagnosticSeverity.Success => "OK",
        _ => "INFO"
    };
}

public sealed class ProfileSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public required ProfileTarget Target { get; init; }
    public required string SessionDirectory { get; init; }
    /// <summary>Live copy of target stdout, stderr and launcher diagnostics.</summary>
    public string OutputLogPath { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int RootProcessId { get; set; }
    public string RawOutput { get; set; } = string.Empty;
    public ObservableCollection<FunctionProfile> Functions { get; } = [];
    public ObservableCollection<FunctionProfile> ProcessFunctions { get; } = [];
    public ObservableCollection<ProcessSnapshot> Processes { get; } = [];
    public ObservableCollection<MetricSample> Metrics { get; } = [];
    public ObservableCollection<DiagnosticEvent> Events { get; } = [];
    public ObservableCollection<HealthWarning> Warnings { get; } = [];
    public double ElapsedSeconds => ((EndedAt ?? DateTimeOffset.Now) - StartedAt).TotalSeconds;
}

public sealed class LaunchPlan
{
    public required string FileName { get; init; }
    public required string WorkingDirectory { get; init; }
    public string Arguments { get; init; } = string.Empty;
    public string? StartGateFile { get; init; }
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Diagnostics { get; init; } = [];
}
