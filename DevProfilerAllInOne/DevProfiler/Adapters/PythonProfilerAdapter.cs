using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DevProfiler.Models;
using DevProfiler.Services;
using System.IO;

namespace DevProfiler.Adapters;

public sealed class PythonProfilerAdapter : IProfilerAdapter
{
    private readonly Dictionary<string, long> _fpsOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(DateTimeOffset At, double Fps)> _recentFps = new();

    public string DisplayName => "Python cProfile";
    public TargetLanguage Language => TargetLanguage.Python;
    public string CaptureDescription => "Exact Python calls and timings; inherited by Python child interpreters; Pygame FPS hooks.";

    public async Task<LaunchPlan> PrepareAsync(ProfileSession session, CancellationToken cancellationToken)
    {
        string helperPath = Path.Combine(session.SessionDirectory, "sitecustomize.py");
        string bootstrapPath = Path.Combine(session.SessionDirectory, "devprofiler_bootstrap.py");
        await ExtractEmbeddedResourceAsync("DevProfiler.Resources.sitecustomize.py", helperPath, cancellationToken);
        await ExtractEmbeddedResourceAsync("DevProfiler.Resources.devprofiler_bootstrap.py", bootstrapPath, cancellationToken);

        ProfileTarget target = session.Target;
        string runtime = ResolvePythonRuntime(target);
        string existingPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH") ?? string.Empty;
        string pythonPath = string.IsNullOrWhiteSpace(existingPythonPath)
            ? session.SessionDirectory
            : session.SessionDirectory + Path.PathSeparator + existingPythonPath;

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DEVPROFILER_SESSION_DIR"] = session.SessionDirectory,
            ["PYTHONPATH"] = pythonPath,
            ["PYTHONDONTWRITEBYTECODE"] = "1"
        };

        string targetLiteral = PythonStringLiteral(target.TargetPath);
        string bootstrapCode = $"import devprofiler_bootstrap,runpy,sys,os;sys.path.insert(0,os.path.dirname({targetLiteral}));sys.argv=[{targetLiteral}]+sys.argv[1:];runpy.run_path({targetLiteral},run_name='__main__')";
        string arguments = $"-c {Quote(bootstrapCode)}";
        if (!string.IsNullOrWhiteSpace(target.Arguments))
            arguments += " " + target.Arguments;

        return new LaunchPlan
        {
            FileName = runtime,
            WorkingDirectory = target.WorkingDirectory,
            Arguments = arguments,
            Environment = env
        };
    }

    public Task OnProcessStartedAsync(ProfileSession session, Process process, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task StopAsync(ProfileSession session, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public async Task FinalizeAsync(ProfileSession session, CancellationToken cancellationToken)
    {
        var allRows = new List<FunctionProfile>();
        var raw = new StringBuilder();

        foreach (string profileFile in Directory.EnumerateFiles(session.SessionDirectory, "proc_*.profile.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using FileStream stream = File.OpenRead(profileFile);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                JsonElement root = document.RootElement;
                JsonElement meta = root.GetProperty("meta");
                int pid = meta.GetProperty("pid").GetInt32();
                string script = meta.TryGetProperty("script", out JsonElement scriptValue)
                    ? Path.GetFileName(scriptValue.GetString() ?? string.Empty)
                    : $"PID {pid}";

                if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
                {
                    string? message = error.GetString();
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        session.Events.Add(new DiagnosticEvent
                        {
                            ProcessId = pid,
                            Category = "ERROR",
                            Source = "Python adapter",
                            Name = "Profile parse error",
                            Details = message,
                            Severity = DiagnosticSeverity.Warning
                        });
                    }
                }

                foreach (JsonElement row in root.GetProperty("functions").EnumerateArray())
                {
                    string file = row.GetProperty("file").GetString() ?? string.Empty;
                    string function = row.GetProperty("function").GetString() ?? "?";
                    long calls = row.GetProperty("calls").GetInt64();
                    allRows.Add(new FunctionProfile
                    {
                        ProcessId = pid,
                        Process = script,
                        Calls = calls,
                        SelfMs = row.GetProperty("selfSeconds").GetDouble() * 1000d,
                        InclusiveMs = row.GetProperty("inclusiveSeconds").GetDouble() * 1000d,
                        File = Path.GetFileName(file),
                        Line = row.GetProperty("line").GetInt32(),
                        Function = function,
                        Category = ProfileCategorizer.Categorize(file, function),
                        Source = "Python cProfile",
                        Accuracy = ProfileAccuracy.Exact
                    });
                }
            }
            catch (Exception ex)
            {
                session.Events.Add(new DiagnosticEvent
                {
                    Category = "ERROR",
                    Source = "Python adapter",
                    Name = "Unable to read profile",
                    Details = $"{Path.GetFileName(profileFile)}: {ex.Message}",
                    Severity = DiagnosticSeverity.Warning
                });
            }
        }

        foreach (string pstatsFile in Directory.EnumerateFiles(session.SessionDirectory, "proc_*.pstats.txt"))
        {
            raw.AppendLine($"===== {Path.GetFileName(pstatsFile)} =====");
            raw.AppendLine(await File.ReadAllTextAsync(pstatsFile, cancellationToken));
        }

        foreach (FunctionProfile row in allRows.OrderByDescending(x => x.InclusiveMs))
            session.ProcessFunctions.Add(row);

        foreach (FunctionProfile row in Merge(allRows))
            session.Functions.Add(row);

        session.RawOutput += raw.ToString();
    }

    public double ReadLiveFps(ProfileSession session)
    {
        foreach (string file in Directory.EnumerateFiles(session.SessionDirectory, "proc_*.fps.jsonl"))
        {
            try
            {
                long offset = _fpsOffsets.GetValueOrDefault(file, 0);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (offset > stream.Length)
                    offset = 0;
                stream.Position = offset;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(line);
                        double fps = document.RootElement.GetProperty("fps").GetDouble();
                        if (fps is > 0 and < 2000)
                            _recentFps.Enqueue((DateTimeOffset.UtcNow, fps));
                    }
                    catch { }
                }
                _fpsOffsets[file] = stream.Position;
            }
            catch { }
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddSeconds(-2);
        while (_recentFps.Count > 0 && _recentFps.Peek().At < cutoff)
            _recentFps.Dequeue();

        return _recentFps.Count == 0 ? 0 : _recentFps.Average(x => x.Fps);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<FunctionProfile> Merge(IEnumerable<FunctionProfile> rows)
    {
        var merged = rows
            .GroupBy(x => new { x.File, x.Line, x.Function, x.Category })
            .Select(group => new FunctionProfile
            {
                ProcessId = group.Select(x => x.ProcessId).Distinct().Count() == 1 ? group.First().ProcessId : 0,
                Process = group.Select(x => x.ProcessId).Distinct().Count() == 1
                    ? group.First().Process
                    : $"{group.Select(x => x.ProcessId).Distinct().Count()} processes",
                Calls = group.Sum(x => x.Calls ?? 0),
                SelfMs = group.Sum(x => x.SelfMs),
                InclusiveMs = group.Max(x => x.InclusiveMs),
                File = group.Key.File,
                Line = group.Key.Line,
                Function = group.Key.Function,
                Category = group.Key.Category,
                Source = "Python cProfile",
                Accuracy = ProfileAccuracy.Exact
            })
            .OrderByDescending(x => x.InclusiveMs)
            .ToList();

        for (int index = 0; index < merged.Count; index++)
            merged[index].Rank = index + 1;

        return merged;
    }

    private static string ResolvePythonRuntime(ProfileTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.RuntimePath))
            return target.RuntimePath;

        foreach (string environment in new[] { ".venv", "venv", "env" })
        {
            string candidate = Path.Combine(target.WorkingDirectory, environment, "Scripts", "python.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return "python.exe";
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

    private static string PythonStringLiteral(string value)
        => "r\"" + value.Replace("\"", "\\\"") + "\"";

}
