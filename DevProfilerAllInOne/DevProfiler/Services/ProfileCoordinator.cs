using System.Diagnostics;
using System.Text;
using System.Windows;
using DevProfiler.Adapters;
using DevProfiler.Models;
using System.IO;

namespace DevProfiler.Services;

public sealed class ProfileCoordinator : IAsyncDisposable
{
    private readonly ProcessTreeService _processTree = new();
    private readonly SemaphoreSlim _finishGate = new(1, 1);
    private readonly StringBuilder _output = new();
    private CancellationTokenSource? _runCancellation;
    private Process? _rootProcess;
    private JobObject? _job;
    private IProfilerAdapter? _adapter;
    private Task? _monitorTask;
    private bool _finishing;
    private bool _completed;
    private bool _stopRequested;

    public ProfileSession? Session { get; private set; }
    public bool IsRunning { get; private set; }

    public event Action<string, string>? StatusChanged;
    public event Action<MetricSample>? MetricUpdated;
    public event Action<string>? OutputReceived;
    public event Action? SessionCompleted;

    public async Task<ProfileSession> StartAsync(ProfileTarget target)
    {
        if (IsRunning)
            throw new InvalidOperationException("A profiling session is already running.");
        ValidateTarget(target);

        string sessionDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevProfiler",
            "Sessions",
            DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(sessionDirectory);

        if (_adapter is not null)
            await _adapter.DisposeAsync();
        _rootProcess?.Dispose();
        _job?.Dispose();
        _runCancellation?.Dispose();
        _adapter = null;
        _rootProcess = null;
        _job = null;
        _runCancellation = null;
        _monitorTask = null;
        _processTree.Reset();
        lock (_output)
            _output.Clear();

        _completed = false;
        _finishing = false;
        _stopRequested = false;
        Session = new ProfileSession
        {
            Target = target,
            SessionDirectory = sessionDirectory,
            OutputLogPath = Path.Combine(sessionDirectory, "target-output.log"),
            StartedAt = DateTimeOffset.Now
        };

        AddSystemOutput($"Session folder: {sessionDirectory}");

        _adapter = target.Language switch
        {
            TargetLanguage.Python => new PythonProfilerAdapter(),
            TargetLanguage.DotNet => new DotNetProfilerAdapter(),
            TargetLanguage.PowerShell => new PowerShellProfilerAdapter(),
            _ => throw new NotSupportedException($"Unsupported language: {target.Language}")
        };

        _runCancellation = new CancellationTokenSource();
        LaunchPlan plan = await _adapter.PrepareAsync(Session, _runCancellation.Token);
        foreach (string diagnostic in plan.Diagnostics)
            AddSystemOutput(diagnostic);
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.FileName,
            Arguments = plan.Arguments,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            // Output is captured below, so a second, empty console window only obscures
            // startup errors. Console APIs in the target still write into the redirected
            // streams and are shown immediately in the Raw Output tab.
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach ((string key, string value) in plan.Environment)
            startInfo.Environment[key] = value;

        _rootProcess = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _rootProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) AddOutput(false, e.Data); };
        _rootProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) AddOutput(true, e.Data); };

        if (!_rootProcess.Start())
            throw new InvalidOperationException("The target process could not be started.");

        Session.RootProcessId = _rootProcess.Id;
        AddSystemOutput($"Started PID {_rootProcess.Id}: {plan.FileName} {plan.Arguments}".TrimEnd());
        AddSystemOutput($"Working directory: {plan.WorkingDirectory}");
        _rootProcess.BeginOutputReadLine();
        _rootProcess.BeginErrorReadLine();

        try
        {
            _job = new JobObject();
            if (!_job.AddProcess(_rootProcess))
            {
                _job.Dispose();
                _job = null;
                throw new InvalidOperationException(
                    "The target already belongs to a Windows Job Object that does not allow DevProfiler ownership. " +
                    "Process-tree monitoring will continue, but child-only lifetime control may be limited.");
            }
        }
        catch (Exception ex)
        {
            AddEvent(new DiagnosticEvent
            {
                ProcessId = _rootProcess.Id,
                Category = "SYSTEM",
                Source = "Process supervisor",
                Name = "Job Object unavailable",
                Details = ex.Message,
                Severity = DiagnosticSeverity.Warning
            });
        }
        finally
        {
            SignalStartGate(plan.StartGateFile);
        }

        IsRunning = true;
        StatusChanged?.Invoke("RUNNING", _adapter.CaptureDescription);
        AddEvent(new DiagnosticEvent
        {
            ProcessId = _rootProcess.Id,
            Category = "SYSTEM",
            Source = _adapter.DisplayName,
            Name = "Target started",
            Details = $"{plan.FileName} {plan.Arguments}",
            Severity = DiagnosticSeverity.Info
        });

        await _adapter.OnProcessStartedAsync(Session, _rootProcess, _runCancellation.Token);
        _monitorTask = Task.Run(() => MonitorAsync(_runCancellation.Token));
        _ = ObserveProcessExitAsync(_rootProcess);
        return Session;
    }

    public async Task StopAsync()
    {
        if (!IsRunning || Session is null)
            return;
        _stopRequested = true;
        StatusChanged?.Invoke("STOPPING", "Stopping profiler and target process tree...");
        if (_rootProcess is { HasExited: false })
        {
            try
            {
                if (_rootProcess.CloseMainWindow())
                    await _rootProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { }
        }

        _runCancellation?.Cancel();
        if (_adapter is not null)
        {
            try { await _adapter.StopAsync(Session, CancellationToken.None); } catch { }
        }
        await RequestProcessTreeCloseAsync();
        try
        {
            // The PowerShell host may already have exited while a launched WinForms/WPF application remains.
            // Always terminate the owned job on Stop, not only while the root process is alive.
            _job?.Terminate();
        }
        catch { }
        try
        {
            if (_rootProcess is { HasExited: false })
                _rootProcess.Kill(entireProcessTree: true);
        }
        catch { }
        await FinishAsync(true);
    }


    private async Task ObserveProcessExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            process.WaitForExit(); // Drain asynchronous stdout/stderr callbacks after process termination.
            int exitCode = process.ExitCode;
            AddSystemOutput($"Target PID {process.Id} exited with code {exitCode}.", exitCode != 0);
            AddEvent(new DiagnosticEvent
            {
                ProcessId = process.Id,
                Category = exitCode == 0 ? "SYSTEM" : "ERROR",
                Source = "Process supervisor",
                Name = "Target exited",
                Details = $"Exit code {exitCode}.",
                Severity = exitCode == 0 ? DiagnosticSeverity.Info : DiagnosticSeverity.Critical
            });
            await WaitForOwnedProcessTreeExitAsync();
            await FinishAsync(_stopRequested);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            AddEvent(new DiagnosticEvent
            {
                ProcessId = process.Id,
                Category = "ERROR",
                Source = "Process supervisor",
                Name = "Exit observer failed",
                Details = ex.Message,
                Severity = DiagnosticSeverity.Warning
            });
        }
    }

    private static void SignalStartGate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // The PowerShell bootstrap has a timeout fallback, so a gate write failure is non-fatal.
        }
    }

    private async Task WaitForOwnedProcessTreeExitAsync()
    {
        if (_job is null || _runCancellation is null)
            return;

        int lastReportedCount = -1;
        while (!_runCancellation.IsCancellationRequested)
        {
            int activeProcesses;
            try
            {
                activeProcesses = _job.ActiveProcessCount;
            }
            catch (Exception ex)
            {
                AddEvent(new DiagnosticEvent
                {
                    ProcessId = _rootProcess?.Id,
                    Category = "WARNING",
                    Source = "Process supervisor",
                    Name = "Process-tree lifetime query failed",
                    Details = ex.Message,
                    Severity = DiagnosticSeverity.Warning
                });
                return;
            }

            if (activeProcesses == 0)
                return;

            if (activeProcesses != lastReportedCount)
            {
                lastReportedCount = activeProcesses;
                StatusChanged?.Invoke("RUNNING", $"Root process exited; profiling {activeProcesses:N0} launched child process(es)...");
            }
            try
            {
                await Task.Delay(100, _runCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RequestProcessTreeCloseAsync()
    {
        if (Session is null)
            return;

        var processIds = Session.Processes
            .Select(x => x.ProcessId)
            .Concat(GetOwnedProcessIdsSafe())
            .Append(Session.RootProcessId)
            .Where(x => x > 0)
            .Distinct()
            .ToArray();

        bool closeRequested = false;
        foreach (int processId in processIds)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (!process.HasExited && process.CloseMainWindow())
                    closeRequested = true;
            }
            catch { }
        }

        if (closeRequested)
        {
            try { await Task.Delay(750); }
            catch { }
        }
    }

    private IReadOnlyList<int> GetOwnedProcessIdsSafe()
    {
        if (_job is null)
            return [];
        try { return _job.GetProcessIds(); }
        catch { return []; }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        if (Session is null)
            return;
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<ProcessSnapshot> tree = _processTree.CaptureTree(
                    Session.RootProcessId,
                    GetOwnedProcessIdsSafe());
                double fps = _adapter?.ReadLiveFps(Session) ?? 0;
                var metric = new MetricSample
                {
                    TimeSeconds = stopwatch.Elapsed.TotalSeconds,
                    CpuPercent = tree.Sum(x => x.CpuPercent),
                    PrivateMemoryMb = tree.Sum(x => x.PrivateMemoryMb),
                    WorkingSetMb = tree.Sum(x => x.WorkingSetMb),
                    Fps = fps,
                    Threads = tree.Sum(x => x.Threads),
                    ProcessCount = tree.Count,
                    ReadMbPerSecond = tree.Sum(x => x.ReadMbPerSecond),
                    WriteMbPerSecond = tree.Sum(x => x.WriteMbPerSecond)
                };

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    UpdateProcesses(Session, tree);
                    Session.Metrics.Add(metric);
                    while (Session.Metrics.Count > 7200)
                        Session.Metrics.RemoveAt(0);
                    MetricUpdated?.Invoke(metric);
                });
            }
            catch (Exception ex)
            {
                AddEvent(new DiagnosticEvent
                {
                    Category = "ERROR",
                    Source = "Metrics",
                    Name = "Sampling error",
                    Details = ex.Message,
                    Severity = DiagnosticSeverity.Warning
                });
            }

            try { await Task.Delay(500, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task FinishAsync(bool stoppedByUser)
    {
        await _finishGate.WaitAsync();
        try
        {
            if (_finishing || _completed || Session is null)
                return;
            _finishing = true;
            IsRunning = false;
            _runCancellation?.Cancel();
            if (_monitorTask is not null && Task.CurrentId != _monitorTask.Id)
            {
                try { await _monitorTask; } catch { }
            }

            Session.EndedAt = DateTimeOffset.Now;
            if (_adapter is not null)
            {
                try
                {
                    await Application.Current.Dispatcher.InvokeAsync(
                        () => _adapter.FinalizeAsync(Session, CancellationToken.None)).Task.Unwrap();
                }
                catch (Exception ex)
                {
                    AddEvent(new DiagnosticEvent
                    {
                        Category = "ERROR",
                        Source = _adapter.DisplayName,
                        Name = "Finalization failed",
                        Details = ex.ToString(),
                        Severity = DiagnosticSeverity.Critical
                    });
                }
            }

            Session.RawOutput = _output.ToString() + Environment.NewLine + Session.RawOutput;
            IReadOnlyList<HealthWarning> warnings = HealthAnalyzer.Analyze(Session);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Session.Warnings.Clear();
                foreach (HealthWarning warning in warnings)
                    Session.Warnings.Add(warning);
            });

            StatusChanged?.Invoke(stoppedByUser ? "STOPPED" : "COMPLETE",
                $"Captured {Session.Functions.Count:N0} functions/methods and {Session.Events.Count:N0} events.");
            _completed = true;
            SessionCompleted?.Invoke();
        }
        finally
        {
            _finishing = false;
            _finishGate.Release();
        }
    }

    private static void UpdateProcesses(ProfileSession session, IReadOnlyList<ProcessSnapshot> snapshots)
    {
        foreach (ProcessSnapshot existing in session.Processes)
            existing.HasExited = true;

        foreach (ProcessSnapshot snapshot in snapshots)
        {
            ProcessSnapshot? existing = session.Processes.FirstOrDefault(x => x.ProcessId == snapshot.ProcessId);
            if (existing is null)
            {
                session.Processes.Add(snapshot);
                continue;
            }
            existing.ParentProcessId = snapshot.ParentProcessId;
            existing.Name = snapshot.Name;
            existing.Path = snapshot.Path;
            existing.CpuPercent = snapshot.CpuPercent;
            existing.PrivateMemoryMb = snapshot.PrivateMemoryMb;
            existing.WorkingSetMb = snapshot.WorkingSetMb;
            existing.Threads = snapshot.Threads;
            existing.Handles = snapshot.Handles;
            existing.ReadMb = snapshot.ReadMb;
            existing.WriteMb = snapshot.WriteMb;
            existing.ReadMbPerSecond = snapshot.ReadMbPerSecond;
            existing.WriteMbPerSecond = snapshot.WriteMbPerSecond;
            existing.HasExited = snapshot.HasExited;
        }
    }

    private void AddOutput(bool error, string line)
    {
        AppendOutput(error ? "STDERR" : "STDOUT", error, line);
    }

    private void AddSystemOutput(string line, bool error = false)
    {
        AppendOutput("SYSTEM", error, line);
    }

    private void AppendOutput(string source, bool error, string line)
    {
        string formatted = $"[{source}] {line}";
        lock (_output)
        {
            _output.AppendLine(formatted);
            WriteOutputLog(formatted);
        }

        OutputReceived?.Invoke(formatted);
        AddEvent(new DiagnosticEvent
        {
            ProcessId = _rootProcess?.Id,
            Category = error ? "ERROR" : "OUTPUT",
            Source = source.ToLowerInvariant(),
            Name = error ? "Error output" : "Output",
            Details = line,
            Severity = error ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info
        });
    }

    private void WriteOutputLog(string line)
    {
        if (string.IsNullOrWhiteSpace(Session?.OutputLogPath))
            return;

        try
        {
            File.AppendAllText(Session.OutputLogPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // The in-app copy remains available if a file system or antivirus lock prevents logging.
        }
    }

    private void AddEvent(DiagnosticEvent item)
    {
        if (Session is null)
            return;
        Application.Current.Dispatcher.InvokeAsync(() => Session.Events.Add(item));
    }

    private static void ValidateTarget(ProfileTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.TargetPath) || !File.Exists(target.TargetPath))
            throw new FileNotFoundException("Select a valid target file.", target.TargetPath);
        if (string.IsNullOrWhiteSpace(target.WorkingDirectory) || !Directory.Exists(target.WorkingDirectory))
            throw new DirectoryNotFoundException("Select a valid working directory.");
        if (!string.IsNullOrWhiteSpace(target.RuntimePath) && !File.Exists(target.RuntimePath))
            throw new FileNotFoundException("The selected runtime was not found.", target.RuntimePath);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
            await StopAsync();
        if (_adapter is not null)
            await _adapter.DisposeAsync();
        _rootProcess?.Dispose();
        _job?.Dispose();
        _runCancellation?.Dispose();
        _finishGate.Dispose();
    }
}
