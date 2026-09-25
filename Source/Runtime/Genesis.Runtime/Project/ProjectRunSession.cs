using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Genesis.Runtime.Project;

/// <summary>Owns one editor-launched player and its control/diagnostic pipes.</summary>
public sealed class ProjectRunSession : IDisposable
{
    private readonly Process _process;
    private int _disposed;
    public bool IsPaused { get; private set; }
    public string ConfirmedState { get; private set; } = "starting";
    public bool IsRunning { get { try { return !_process.HasExited; } catch (InvalidOperationException) { return false; } } }

    internal ProjectRunSession(Process process, Action<int, string> exited)
    {
        _process = process;
        _ = ObserveExit(exited);
    }

    private async Task ObserveExit(Action<int, string> exited)
    {
        try
        {
            Task<string> errors = _process.StandardError.ReadToEndAsync();
            // Drain stdout too: scripts must never block the runner on a full pipe.
            Task output = ReadOutput();
            await _process.WaitForExitAsync().ConfigureAwait(false);
            string details = await errors.ConfigureAwait(false);
            await output.ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0) exited?.Invoke(_process.ExitCode, details);
        }
        catch (Exception error) { Trace.TraceError("Player monitoring: " + error); }
    }

    private async Task ReadOutput()
    {
        while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is string line)
            if (line.StartsWith("GENESIS_PLAYER_STATE ", StringComparison.Ordinal))
                ConfirmedState = line.Substring("GENESIS_PLAYER_STATE ".Length);
    }

    private bool Send(string command)
    {
        try
        {
            if (!IsRunning) return false;
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
            return true;
        }
        catch (Exception error) when (error is System.IO.IOException or InvalidOperationException)
        { return false; }
    }

    public void Pause() { if (Send("pause")) IsPaused = true; }
    public void Resume() { if (Send("resume")) IsPaused = false; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Send("stop");
        _ = StopAndDispose();
    }

    private async Task StopAndDispose()
    {
        try
        {
            if (IsRunning)
            {
                await Task.WhenAny(_process.WaitForExitAsync(), Task.Delay(2000)).ConfigureAwait(false);
                if (IsRunning) _process.Kill(entireProcessTree: true);
            }
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Trace.TraceWarning("Player cleanup: " + error.Message); }
        finally { _process.Dispose(); }
    }
}
