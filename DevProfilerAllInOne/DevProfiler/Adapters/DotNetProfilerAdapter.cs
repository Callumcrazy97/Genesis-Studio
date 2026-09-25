using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using DevProfiler.Models;
using DevProfiler.Services;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.IO;

namespace DevProfiler.Adapters;

public sealed class DotNetProfilerAdapter : IProfilerAdapter
{
    private sealed class MethodSamples
    {
        public long SelfSamples;
        public long InclusiveSamples;
    }

    private readonly ConcurrentDictionary<string, MethodSamples> _methods = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _eventCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<DiagnosticEvent> _importantEvents = new();
    private readonly StringBuilder _raw = new();
    private CancellationTokenSource? _captureCancellation;
    private EventPipeSession? _pipeSession;
    private Task? _captureTask;
    private string? _nettracePath;
    private int _pid;

    public string DisplayName => ".NET 10 EventPipe";
    public TargetLanguage Language => TargetLanguage.DotNet;
    public string CaptureDescription => "Sampled managed call stacks plus GC, exception, contention, loader, threading and JIT events for the root .NET process.";

    public Task<LaunchPlan> PrepareAsync(ProfileSession session, CancellationToken cancellationToken)
    {
        ProfileTarget target = session.Target;
        bool isDll = string.Equals(Path.GetExtension(target.TargetPath), ".dll", StringComparison.OrdinalIgnoreCase);
        string fileName = isDll
            ? (string.IsNullOrWhiteSpace(target.RuntimePath) ? "dotnet.exe" : target.RuntimePath)
            : target.TargetPath;
        string arguments = isDll ? Quote(target.TargetPath) : string.Empty;
        if (!string.IsNullOrWhiteSpace(target.Arguments))
            arguments = string.IsNullOrWhiteSpace(arguments) ? target.Arguments : arguments + " " + target.Arguments;

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_EnableDiagnostics"] = "1"
        };
        var diagnostics = new List<string>();

        string? glfwDirectory = SilkNetNativeResolver.FindGlfwDirectory(
            target.TargetPath,
            target.WorkingDirectory);
        if (!string.IsNullOrWhiteSpace(glfwDirectory))
        {
            string inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            environment["PATH"] = glfwDirectory + Path.PathSeparator + inheritedPath;
            diagnostics.Add(
                $"Silk.NET native companion detected; prepended to target PATH: {glfwDirectory}");
        }

        return Task.FromResult(new LaunchPlan
        {
            FileName = fileName,
            WorkingDirectory = target.WorkingDirectory,
            Arguments = arguments,
            Environment = environment,
            Diagnostics = diagnostics
        });
    }

    public Task OnProcessStartedAsync(ProfileSession session, Process process, CancellationToken cancellationToken)
    {
        _pid = process.Id;
        _nettracePath = Path.Combine(session.SessionDirectory, $"dotnet_{process.Id}.nettrace");

        // Do not link this token to the coordinator token. EventPipe needs an orderly
        // Stop() so that the nettrace stream is finalised before ETLX conversion.
        _captureCancellation = new CancellationTokenSource();
        _captureTask = Task.Run(
            () => CaptureAsync(process.Id, _nettracePath, _captureCancellation.Token),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(ProfileSession session, CancellationToken cancellationToken)
    {
        try { _pipeSession?.Stop(); } catch { }

        if (_captureTask is not null)
        {
            try
            {
                await _captureTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (TimeoutException)
            {
                _captureCancellation?.Cancel();
                try { await _captureTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); } catch { }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Capture errors are written to the raw adapter output.
            }
        }
    }

    public async Task FinalizeAsync(ProfileSession session, CancellationToken cancellationToken)
    {
        await StopAsync(session, cancellationToken);

        if (!string.IsNullOrWhiteSpace(_nettracePath) && File.Exists(_nettracePath))
            AnalyseTraceFile(_nettracePath, session);
        else
            _raw.AppendLine("No .nettrace file was produced by the EventPipe session.");

        const double estimatedSampleMs = 1.0;
        var rows = _methods
            .Select(pair => new FunctionProfile
            {
                ProcessId = _pid,
                Process = Path.GetFileName(session.Target.TargetPath),
                Calls = null,
                Samples = pair.Value.InclusiveSamples,
                SelfMs = pair.Value.SelfSamples * estimatedSampleMs,
                InclusiveMs = pair.Value.InclusiveSamples * estimatedSampleMs,
                File = GetModule(pair.Key),
                Function = pair.Key,
                Category = ProfileCategorizer.Categorize(GetModule(pair.Key), pair.Key),
                Source = ".NET EventPipe",
                Accuracy = ProfileAccuracy.Sampled
            })
            .OrderByDescending(x => x.InclusiveMs)
            .ToList();

        for (int index = 0; index < rows.Count; index++)
        {
            rows[index].Rank = index + 1;
            session.Functions.Add(rows[index]);
            session.ProcessFunctions.Add(rows[index]);
        }

        foreach (DiagnosticEvent item in _importantEvents.Take(500))
            session.Events.Add(item);

        foreach ((string eventName, long count) in _eventCounts.OrderByDescending(x => x.Value))
        {
            session.Events.Add(new DiagnosticEvent
            {
                ProcessId = _pid,
                Category = CategorizeRuntimeEvent(eventName),
                Source = ".NET EventPipe",
                Name = eventName,
                Details = $"Observed {count:N0} event(s).",
                Severity = eventName.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                    ? DiagnosticSeverity.Warning
                    : DiagnosticSeverity.Info
            });
        }

        session.RawOutput += _raw.ToString();
    }

    public double ReadLiveFps(ProfileSession session) => 0;

    public async ValueTask DisposeAsync()
    {
        try { _pipeSession?.Stop(); } catch { }
        _captureCancellation?.Cancel();
        if (_captureTask is not null)
        {
            try { await _captureTask; } catch { }
        }
        try { _pipeSession?.Dispose(); } catch { }
        _captureCancellation?.Dispose();
    }

    private async Task CaptureAsync(int processId, string tracePath, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 30 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            bool attached = false;
            try
            {
                var client = new DiagnosticsClient(processId);
                long keywords = (long)(
                    ClrTraceEventParser.Keywords.GC |
                    ClrTraceEventParser.Keywords.Exception |
                    ClrTraceEventParser.Keywords.Contention |
                    ClrTraceEventParser.Keywords.Loader |
                    ClrTraceEventParser.Keywords.Threading |
                    ClrTraceEventParser.Keywords.Jit);

                var providers = new List<EventPipeProvider>
                {
                    new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational),
                    new("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, keywords)
                };

                _pipeSession = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 256);
                attached = true;
                await using var output = new FileStream(
                    tracePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 1024 * 128,
                    useAsync: true);

                _raw.AppendLine($"Attached EventPipe to PID {processId} at {DateTimeOffset.Now:O}.");
                await _pipeSession.EventStream.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(CancellationToken.None);
                _raw.AppendLine($"EventPipe capture written to {tracePath}.");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { _pipeSession?.Dispose(); } catch { }
                _pipeSession = null;

                // Once attached, an exception normally means Stop() closed the
                // stream or the target exited. The bytes already written remain a
                // valid best-effort trace and must not trigger a second attachment.
                if (attached)
                {
                    _raw.AppendLine($"EventPipe stream completed: {ex.Message}");
                    return;
                }

                if (attempt < 29)
                {
                    try { await Task.Delay(200, cancellationToken); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        if (lastError is not null)
            _raw.AppendLine($"EventPipe attach failed: {lastError}");
    }

    private void AnalyseTraceFile(string tracePath, ProfileSession session)
    {
        string etlxPath = Path.ChangeExtension(tracePath, ".etlx");
        try
        {
            _raw.AppendLine($"Converting EventPipe trace to ETLX: {etlxPath}");
            string convertedPath = TraceLog.CreateFromEventPipeDataFile(
                tracePath,
                etlxPath,
                new TraceLogOptions { ContinueOnError = true });

            using TraceLog traceLog = TraceLog.OpenOrConvert(convertedPath);
            foreach (TraceEvent data in traceLog.Events)
                AnalyseTraceEvent(data, session);

            _raw.AppendLine(
                $"ETLX analysis completed: {_methods.Count:N0} methods and {_eventCounts.Count:N0} event types.");
        }
        catch (Exception ex)
        {
            _raw.AppendLine($"EventPipe trace analysis failed: {ex}");
            _importantEvents.Enqueue(new DiagnosticEvent
            {
                ProcessId = _pid,
                Category = "ERROR",
                Source = ".NET EventPipe",
                Name = "Trace analysis failed",
                Details = ex.Message,
                Severity = DiagnosticSeverity.Critical
            });
        }
    }

    private void AnalyseTraceEvent(TraceEvent data, ProfileSession session)
    {
        try
        {
            string provider = data.ProviderName ?? string.Empty;
            string eventName = string.IsNullOrWhiteSpace(data.EventName) ? "Unknown" : data.EventName;
            _eventCounts.AddOrUpdate(eventName, 1, (_, count) => count + 1);

            if (provider.Contains("SampleProfiler", StringComparison.OrdinalIgnoreCase))
            {
                TraceCallStack? stack = data.CallStack();
                bool leaf = true;
                int depth = 0;
                while (stack is not null && depth++ < 256)
                {
                    string method = stack.CodeAddress?.FullMethodName
                        ?? stack.CodeAddress?.ToString()
                        ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(method))
                    {
                        MethodSamples samples = _methods.GetOrAdd(method, _ => new MethodSamples());
                        Interlocked.Increment(ref samples.InclusiveSamples);
                        if (leaf)
                            Interlocked.Increment(ref samples.SelfSamples);
                        leaf = false;
                    }
                    stack = stack.Caller;
                }
                return;
            }

            if (eventName.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                _importantEvents.Enqueue(new DiagnosticEvent
                {
                    Timestamp = session.StartedAt.AddMilliseconds(data.TimeStampRelativeMSec),
                    ProcessId = data.ProcessID,
                    ThreadId = data.ThreadID,
                    Category = "ERROR",
                    Source = ".NET EventPipe",
                    Name = eventName,
                    Details = BuildPayload(data, 8),
                    Severity = DiagnosticSeverity.Warning
                });
            }
        }
        catch (Exception ex)
        {
            _raw.AppendLine($"Skipped unsupported trace event: {ex.Message}");
        }
    }

    private static string BuildPayload(TraceEvent data, int maximumFields)
    {
        var parts = new List<string>();
        for (int index = 0; index < Math.Min(data.PayloadNames.Length, maximumFields); index++)
        {
            try
            {
                object? value = data.PayloadValue(index);
                parts.Add($"{data.PayloadNames[index]}={value}");
            }
            catch { }
        }
        return string.Join("; ", parts);
    }

    private static string CategorizeRuntimeEvent(string eventName)
    {
        if (eventName.Contains("Exception", StringComparison.OrdinalIgnoreCase)) return "ERROR";
        if (eventName.Contains("GC", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Allocation", StringComparison.OrdinalIgnoreCase)) return "GC";
        if (eventName.Contains("Contention", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Wait", StringComparison.OrdinalIgnoreCase)) return "BLOCKING";
        if (eventName.Contains("Loader", StringComparison.OrdinalIgnoreCase) || eventName.Contains("Module", StringComparison.OrdinalIgnoreCase)) return "IMPORT";
        return "RUNTIME";
    }

    private static string GetModule(string method)
    {
        int bang = method.IndexOf('!');
        if (bang > 0)
            return method[..bang];
        int separator = method.IndexOf("::", StringComparison.Ordinal);
        if (separator > 0)
            return method[..separator];
        return ".NET";
    }

    private static string Quote(string value)
        => "\"" + value.Replace("\"", "\\\"") + "\"";
}
