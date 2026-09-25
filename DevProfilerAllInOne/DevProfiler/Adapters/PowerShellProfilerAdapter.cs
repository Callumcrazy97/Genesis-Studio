using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DevProfiler.Models;
using DevProfiler.Services;

namespace DevProfiler.Adapters;

public sealed class PowerShellProfilerAdapter : IProfilerAdapter
{
    private const string BootstrapResource = "DevProfiler.Resources.devprofiler_powershell_bootstrap.ps1";
    private const string EventFileName = "powershell.events.jsonl";
    private const string SummaryFileName = "powershell.summary.json";

    private int _processId;
    private string _runtime = string.Empty;

    public string DisplayName => "PowerShell profiler";
    public TargetLanguage Language => TargetLanguage.PowerShell;
    public string CaptureDescription => "PowerShell streams, GUI-compatible STA execution, exact script wall time, and process-tree system metrics.";

    public async Task<LaunchPlan> PrepareAsync(ProfileSession session, CancellationToken cancellationToken)
    {
        string bootstrapPath = Path.Combine(session.SessionDirectory, "devprofiler_powershell_bootstrap.ps1");
        string eventPath = Path.Combine(session.SessionDirectory, EventFileName);
        string summaryPath = Path.Combine(session.SessionDirectory, SummaryFileName);
        string startGatePath = Path.Combine(session.SessionDirectory, "powershell.start.ready");
        await ExtractEmbeddedResourceAsync(BootstrapResource, bootstrapPath, cancellationToken);

        _runtime = ResolvePowerShellRuntime(session.Target);
        IReadOnlyList<string> targetArguments = SplitCommandLine(session.Target.Arguments);
        string argumentJson = JsonSerializer.Serialize(targetArguments);
        string argumentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(argumentJson));

        string arguments = string.Join(" ",
            "-NoLogo",
            "-NoProfile",
            "-STA",
            "-ExecutionPolicy Bypass",
            "-File", Quote(bootstrapPath),
            "-Target", Quote(session.Target.TargetPath),
            "-EventFile", Quote(eventPath),
            "-SummaryFile", Quote(summaryPath),
            "-ArgumentsBase64", Quote(argumentBase64));

        return new LaunchPlan
        {
            FileName = _runtime,
            WorkingDirectory = session.Target.WorkingDirectory,
            Arguments = arguments,
            StartGateFile = startGatePath,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DEVPROFILER_SESSION_DIR"] = session.SessionDirectory,
                ["DEVPROFILER_TARGET"] = session.Target.TargetPath,
                ["DEVPROFILER_LAUNCH_GATE"] = startGatePath
            }
        };
    }

    public Task OnProcessStartedAsync(ProfileSession session, Process process, CancellationToken cancellationToken)
    {
        _processId = process.Id;
        return Task.CompletedTask;
    }

    public Task StopAsync(ProfileSession session, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task FinalizeAsync(ProfileSession session, CancellationToken cancellationToken)
    {
        string eventPath = Path.Combine(session.SessionDirectory, EventFileName);
        string summaryPath = Path.Combine(session.SessionDirectory, SummaryFileName);
        await WaitForFileAsync(summaryPath, TimeSpan.FromMilliseconds(1200), cancellationToken);

        PowerShellSummary? summary = await ReadSummaryAsync(summaryPath, cancellationToken);
        double elapsedMs = summary is { ElapsedMs: > 0 }
            ? summary.ElapsedMs
            : Math.Max(0, session.ElapsedSeconds * 1000d);
        string processName = session.Processes.FirstOrDefault(x => x.ProcessId == _processId)?.Name
            ?? Path.GetFileNameWithoutExtension(_runtime)
            ?? "PowerShell";

        var scriptRow = new FunctionProfile
        {
            Rank = 1,
            ProcessId = _processId,
            Process = processName,
            Calls = 1,
            SelfMs = elapsedMs,
            InclusiveMs = elapsedMs,
            File = Path.GetFileName(session.Target.TargetPath),
            Function = "<script>",
            Category = "LOGIC",
            Source = "PowerShell wrapper",
            Accuracy = ProfileAccuracy.Exact
        };
        session.Functions.Add(scriptRow);
        session.ProcessFunctions.Add(scriptRow);

        int structuredEvents = await ReadEventsAsync(eventPath, session, cancellationToken);
        AppendSummaryEvent(session, summary, structuredEvents);
        session.RawOutput += BuildRawSummary(summary, structuredEvents, eventPath, summaryPath);
    }

    public double ReadLiveFps(ProfileSession session) => 0;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<int> ReadEventsAsync(
        string eventPath,
        ProfileSession session,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(eventPath))
            return 0;

        int count = 0;
        using var stream = new FileStream(eventPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string streamName = GetString(root, "stream", "Output");
                string details = GetString(root, "details", string.Empty);
                string sourcePath = GetString(root, "script", string.Empty);
                int lineNumber = GetInt32(root, "line");
                int columnNumber = GetInt32(root, "column");
                string location = BuildLocation(sourcePath, lineNumber, columnNumber);
                string recordType = GetString(root, "recordType", streamName);

                session.Events.Add(new DiagnosticEvent
                {
                    Timestamp = GetTimestamp(root, session.StartedAt),
                    ProcessId = session.RootProcessId,
                    Category = CategorizeStream(streamName),
                    Source = "PowerShell " + streamName,
                    Name = recordType,
                    Details = string.IsNullOrWhiteSpace(location) ? details : $"{details} [{location}]",
                    Severity = GetSeverity(streamName)
                });
                count++;
            }
            catch (Exception ex)
            {
                session.Events.Add(new DiagnosticEvent
                {
                    ProcessId = session.RootProcessId,
                    Category = "ERROR",
                    Source = "PowerShell adapter",
                    Name = "Structured event parse failed",
                    Details = ex.Message,
                    Severity = DiagnosticSeverity.Warning
                });
            }
        }

        return count;
    }

    private static async Task<PowerShellSummary?> ReadSummaryAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<PowerShellSummary>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static void AppendSummaryEvent(ProfileSession session, PowerShellSummary? summary, int structuredEvents)
    {
        if (summary is null)
        {
            session.Events.Add(new DiagnosticEvent
            {
                ProcessId = session.RootProcessId,
                Category = "WARNING",
                Source = "PowerShell adapter",
                Name = "Summary unavailable",
                Details = "The target exited before the PowerShell wrapper wrote its final summary. Process metrics and captured output remain available.",
                Severity = DiagnosticSeverity.Warning
            });
            return;
        }

        session.Events.Add(new DiagnosticEvent
        {
            ProcessId = session.RootProcessId,
            Category = summary.Success ? "SYSTEM" : "ERROR",
            Source = "PowerShell adapter",
            Name = summary.Success ? "PowerShell script completed" : "PowerShell script completed with errors",
            Details = $"PowerShell {summary.PowerShellVersion} ({summary.Edition}); " +
                      $"elapsed {summary.ElapsedMs:N1} ms; exit code {summary.ExitCode}; " +
                      $"{summary.ErrorCount:N0} error(s), {summary.WarningCount:N0} warning(s), " +
                      $"{structuredEvents:N0} structured record(s).",
            Severity = summary.Success ? DiagnosticSeverity.Success : DiagnosticSeverity.Warning
        });
    }

    private static string BuildRawSummary(
        PowerShellSummary? summary,
        int structuredEvents,
        string eventPath,
        string summaryPath)
    {
        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine("===== PowerShell profiler summary =====");
        text.AppendLine($"Structured event file: {eventPath}");
        text.AppendLine($"Summary file: {summaryPath}");
        text.AppendLine($"Structured records: {structuredEvents:N0}");
        if (summary is null)
        {
            text.AppendLine("The final PowerShell summary was unavailable (normally caused by a forced termination).");
            return text.ToString();
        }

        text.AppendLine($"Runtime: {summary.RuntimePath}");
        text.AppendLine($"PowerShell: {summary.PowerShellVersion} ({summary.Edition})");
        text.AppendLine($"Target: {summary.Target}");
        text.AppendLine($"Elapsed: {summary.ElapsedMs:N3} ms");
        text.AppendLine($"Success: {summary.Success}");
        text.AppendLine($"Exit code: {summary.ExitCode}");
        text.AppendLine($"Output={summary.OutputCount:N0}; Information={summary.InformationCount:N0}; Verbose={summary.VerboseCount:N0}; Debug={summary.DebugCount:N0}; Warnings={summary.WarningCount:N0}; Errors={summary.ErrorCount:N0}");
        return text.ToString();
    }

    private static string ResolvePowerShellRuntime(ProfileTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.RuntimePath))
            return target.RuntimePath;

        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        string[] candidates =
        [
            Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"),
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "pwsh.exe"),
            string.IsNullOrWhiteSpace(systemRoot)
                ? string.Empty
                : Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
        ];

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        string? fromPath = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe");
        return fromPath ?? throw new FileNotFoundException(
            "PowerShell was not found. Install PowerShell 7 or browse to pwsh.exe/powershell.exe in the Runtime field.");
    }

    private static string? FindOnPath(string executable)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), executable);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }
        return null;
    }

    private static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];

        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        int index = 0;

        while (index < commandLine.Length)
        {
            if (char.IsWhiteSpace(commandLine[index]) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                index++;
                continue;
            }

            if (commandLine[index] == '\\')
            {
                int slashCount = 0;
                while (index < commandLine.Length && commandLine[index] == '\\')
                {
                    slashCount++;
                    index++;
                }

                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    current.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 0)
                        quoted = !quoted;
                    else
                        current.Append('"');
                    index++;
                }
                else
                {
                    current.Append('\\', slashCount);
                }
                continue;
            }

            if (commandLine[index] == '"')
            {
                quoted = !quoted;
                index++;
                continue;
            }

            current.Append(commandLine[index]);
            index++;
        }

        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static string CategorizeStream(string streamName) => streamName.ToUpperInvariant() switch
    {
        "ERROR" => "ERROR",
        "WARNING" => "WARNING",
        "VERBOSE" or "DEBUG" => "TRACE",
        "INFORMATION" => "EVENT",
        _ => "OUTPUT"
    };

    private static DiagnosticSeverity GetSeverity(string streamName) => streamName.ToUpperInvariant() switch
    {
        "ERROR" => DiagnosticSeverity.Warning,
        "WARNING" => DiagnosticSeverity.Warning,
        _ => DiagnosticSeverity.Info
    };

    private static DateTimeOffset GetTimestamp(JsonElement root, DateTimeOffset fallback)
    {
        string value = GetString(root, "timestamp", string.Empty);
        return DateTimeOffset.TryParse(value, out DateTimeOffset timestamp) ? timestamp : fallback;
    }

    private static string GetString(JsonElement root, string property, string fallback)
        => root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int GetInt32(JsonElement root, string property)
        => root.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static string BuildLocation(string file, int line, int column)
    {
        if (string.IsNullOrWhiteSpace(file))
            return string.Empty;
        string name = Path.GetFileName(file);
        if (line <= 0)
            return name;
        return column > 0 ? $"{name}:{line}:{column}" : $"{name}:{line}";
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + timeout;
        while (!File.Exists(path) && DateTimeOffset.UtcNow < until)
        {
            try { await Task.Delay(50, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task ExtractEmbeddedResourceAsync(string name, string outputPath, CancellationToken cancellationToken)
    {
        await using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' was not found.");
        await using FileStream output = File.Create(outputPath);
        await resource.CopyToAsync(output, cancellationToken);
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";

    private sealed class PowerShellSummary
    {
        public string RuntimePath { get; set; } = string.Empty;
        public string PowerShellVersion { get; set; } = string.Empty;
        public string Edition { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public double ElapsedMs { get; set; }
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public int OutputCount { get; set; }
        public int InformationCount { get; set; }
        public int VerboseCount { get; set; }
        public int DebugCount { get; set; }
        public int WarningCount { get; set; }
        public int ErrorCount { get; set; }
    }
}
