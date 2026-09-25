using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DevProfiler.Services;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Usage: TargetLaunchProbe <target.exe> <results-directory> [native-library-directory]");
    return 2;
}

string targetPath = Path.GetFullPath(args[0]);
string resultsDirectory = Path.GetFullPath(args[1]);
if (!File.Exists(targetPath))
{
    Console.Error.WriteLine($"Target does not exist: {targetPath}");
    return 2;
}

Directory.CreateDirectory(resultsDirectory);
string? nativeLibraryDirectory = args.Length >= 3 ? Path.GetFullPath(args[2]) : null;
var probe = new LaunchProbe(targetPath, resultsDirectory, nativeLibraryDirectory);
return await probe.RunAsync();

internal sealed class LaunchProbe
{
    private static readonly TimeSpan[] CaptureTimes =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromSeconds(1),
        // This Silk.NET single-file target is still booting during the four
        // requested sub-second captures. Keep one later proof-of-window frame.
        TimeSpan.FromSeconds(3)
    ];

    private readonly object _logGate = new();
    private readonly string _targetPath;
    private readonly string _workingDirectory;
    private readonly string _resultsDirectory;
    private readonly string _probeLogPath;
    private readonly string _stdoutLogPath;
    private readonly string _stderrLogPath;
    private readonly string? _nativeLibraryDirectory;
    private readonly Stopwatch _clock = new();
    private readonly List<SnapshotResult> _snapshots = [];
    private Process? _process;

    public LaunchProbe(string targetPath, string resultsDirectory, string? nativeLibraryDirectory)
    {
        _targetPath = targetPath;
        _workingDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("The target has no working directory.");
        _resultsDirectory = resultsDirectory;
        _probeLogPath = Path.Combine(resultsDirectory, "probe.log");
        _stdoutLogPath = Path.Combine(resultsDirectory, "stdout.log");
        _stderrLogPath = Path.Combine(resultsDirectory, "stderr.log");
        _nativeLibraryDirectory = nativeLibraryDirectory ??
            SilkNetNativeResolver.FindGlfwDirectory(targetPath, _workingDirectory);
    }

    public async Task<int> RunAsync()
    {
        Log($"Probe started at {DateTimeOffset.Now:O}");
        Log($"Target: {_targetPath}");
        Log($"Working directory: {_workingDirectory}");
        Log($"Native library directory: {_nativeLibraryDirectory ?? "<none>"}");
        Log($"Desktop: {SystemInformation.VirtualScreen}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _targetPath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["DOTNET_EnableDiagnostics"] = "1";
        if (!string.IsNullOrWhiteSpace(_nativeLibraryDirectory))
        {
            string inheritedPath = startInfo.Environment.TryGetValue("PATH", out string? path)
                ? path ?? string.Empty
                : string.Empty;
            startInfo.Environment["PATH"] = _nativeLibraryDirectory +
                Path.PathSeparator + inheritedPath;
        }

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                AppendTargetOutput(_stdoutLogPath, "STDOUT", eventArgs.Data);
        };
        _process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
                AppendTargetOutput(_stderrLogPath, "STDERR", eventArgs.Data);
        };
        _process.Exited += (_, _) =>
        {
            try { Log($"Target exited at {_clock.Elapsed.TotalMilliseconds:N0} ms with code {_process.ExitCode}."); }
            catch (Exception exception) { Log($"Could not read target exit code: {exception.Message}"); }
        };

        try
        {
            _clock.Start();
            if (!_process.Start())
                throw new InvalidOperationException("Process.Start returned false.");

            Log($"Started PID {_process.Id}.");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            foreach (TimeSpan captureTime in CaptureTimes)
            {
                TimeSpan delay = captureTime - _clock.Elapsed;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay);
                CaptureSnapshot(captureTime);
            }

            await WaitUntilAsync(TimeSpan.FromSeconds(4));
            RecordProcessState("Final observation");
        }
        catch (Exception exception)
        {
            Log($"Probe failure: {exception}");
        }
        finally
        {
            await StopTargetAsync();
            WriteResult();
            _process.Dispose();
        }

        bool sawWindow = _snapshots.Any(snapshot => snapshot.VisibleWindows.Count > 0);
        bool wroteError = File.Exists(_stderrLogPath) && new FileInfo(_stderrLogPath).Length > 0;
        Log($"Result: visible target window observed={sawWindow}; stderr captured={wroteError}.");
        return sawWindow ? 0 : 1;
    }

    private async Task WaitUntilAsync(TimeSpan desiredElapsed)
    {
        TimeSpan delay = desiredElapsed - _clock.Elapsed;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
    }

    private void CaptureSnapshot(TimeSpan requestedTime)
    {
        double actualMilliseconds = _clock.Elapsed.TotalMilliseconds;
        string timeLabel = $"{requestedTime.TotalMilliseconds:0000}ms";
        string screenshotPath = Path.Combine(_resultsDirectory, $"screen-{timeLabel}.png");
        Rectangle screen = SystemInformation.VirtualScreen;

        using (var bitmap = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(screen.Left, screen.Top, 0, 0, screen.Size, CopyPixelOperation.SourceCopy);
            bitmap.Save(screenshotPath, ImageFormat.Png);
        }

        IReadOnlyList<WindowInfo> windows = GetProcessWindows(_process?.Id ?? 0);
        var snapshot = new SnapshotResult(
            requestedTime.TotalMilliseconds,
            actualMilliseconds,
            !_process?.HasExited ?? false,
            windows,
            screenshotPath);
        _snapshots.Add(snapshot);

        Log(
            $"Snapshot requested={requestedTime.TotalMilliseconds:N0} ms, " +
            $"captured={actualMilliseconds:N0} ms, alive={snapshot.ProcessAlive}, " +
            $"visible target windows={windows.Count}, image={screenshotPath}");
        foreach (WindowInfo window in windows)
            Log($"Window 0x{window.Handle:X}: \"{window.Title}\", bounds={window.Bounds}");
    }

    private void RecordProcessState(string label)
    {
        if (_process is null)
            return;

        try
        {
            _process.Refresh();
            IReadOnlyList<WindowInfo> windows = GetProcessWindows(_process.Id);
            Log(
                $"{label}: alive={!_process.HasExited}, responding={(!_process.HasExited && _process.Responding)}, " +
                $"mainWindow=0x{_process.MainWindowHandle.ToInt64():X}, title=\"{_process.MainWindowTitle}\", " +
                $"visibleWindows={windows.Count}, threads={(_process.HasExited ? 0 : _process.Threads.Count)}.");
        }
        catch (Exception exception)
        {
            Log($"{label}: unable to inspect process: {exception.Message}");
        }
    }

    private async Task StopTargetAsync()
    {
        if (_process is null)
            return;

        try
        {
            if (_process.HasExited)
            {
                _process.WaitForExit();
                return;
            }

            Log("Requesting target shutdown.");
            _process.StandardInput.Close();
            if (_process.CloseMainWindow())
            {
                using var gracefulTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await _process.WaitForExitAsync(gracefulTimeout.Token); }
                catch (OperationCanceledException) { }
            }

            if (!_process.HasExited)
            {
                Log("Target did not close normally; terminating the probe-owned process tree.");
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.WaitForExit();
        }
        catch (Exception exception)
        {
            Log($"Target cleanup failure: {exception}");
        }
    }

    private void AppendTargetOutput(string path, string source, string line)
    {
        string formatted = $"[{_clock.Elapsed.TotalMilliseconds,8:N0} ms] {line}";
        lock (_logGate)
        {
            File.AppendAllText(path, formatted + Environment.NewLine, Encoding.UTF8);
            File.AppendAllText(_probeLogPath, $"[{source}] {formatted}{Environment.NewLine}", Encoding.UTF8);
        }
    }

    private void Log(string message)
    {
        string formatted = $"[{_clock.Elapsed.TotalMilliseconds,8:N0} ms] {message}";
        lock (_logGate)
            File.AppendAllText(_probeLogPath, formatted + Environment.NewLine, Encoding.UTF8);
        Console.WriteLine(formatted);
    }

    private void WriteResult()
    {
        int? exitCode = null;
        try
        {
            if (_process?.HasExited == true)
                exitCode = _process.ExitCode;
        }
        catch { }

        var result = new
        {
            target = _targetPath,
            workingDirectory = _workingDirectory,
            nativeLibraryDirectory = _nativeLibraryDirectory,
            processId = _process?.Id,
            exitCode,
            visibleWindowObserved = _snapshots.Any(snapshot => snapshot.VisibleWindows.Count > 0),
            snapshots = _snapshots
        };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_resultsDirectory, "result.json"), json, Encoding.UTF8);
    }

    private static IReadOnlyList<WindowInfo> GetProcessWindows(int processId)
    {
        var windows = new List<WindowInfo>();
        if (processId <= 0)
            return windows;

        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(windowHandle, out uint ownerProcessId);
            if (ownerProcessId != processId || !NativeMethods.IsWindowVisible(windowHandle))
                return true;

            int titleLength = NativeMethods.GetWindowTextLength(windowHandle);
            var title = new StringBuilder(titleLength + 1);
            NativeMethods.GetWindowText(windowHandle, title, title.Capacity);
            NativeMethods.GetWindowRect(windowHandle, out NativeMethods.Rect rectangle);
            windows.Add(new WindowInfo(
                windowHandle.ToInt64(),
                title.ToString(),
                new Rectangle(
                    rectangle.Left,
                    rectangle.Top,
                    rectangle.Right - rectangle.Left,
                    rectangle.Bottom - rectangle.Top)));
            return true;
        }, IntPtr.Zero);

        return windows;
    }
}

internal sealed record SnapshotResult(
    double RequestedMilliseconds,
    double ActualMilliseconds,
    bool ProcessAlive,
    IReadOnlyList<WindowInfo> VisibleWindows,
    string ScreenshotPath);

internal sealed record WindowInfo(long Handle, string Title, Rectangle Bounds);

internal static class NativeMethods
{
    internal delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr windowHandle, out Rect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
