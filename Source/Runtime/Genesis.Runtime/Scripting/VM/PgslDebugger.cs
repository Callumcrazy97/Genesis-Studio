using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Genesis.Runtime.Scripting.VM;

public sealed record PgslDebugLocation(
    string SourceName,
    string EventName,
    string FunctionName,
    int Line,
    int ProgramCounter,
    IReadOnlyList<string> CallStack,
    IReadOnlyDictionary<string, object> Variables,
    string ErrorMessage = "");

/// <summary>
/// Cooperative instruction debugger used by both the Object Editor and F6-capable VM hosts.
/// The VM runs on a worker while the authoring UI controls this pause gate.
/// </summary>
public sealed class PgslDebugController : IDisposable
{
    private readonly ManualResetEventSlim _resume = new(false);
    private readonly object _gate = new();
    private HashSet<int> _breakpoints = [];
    private volatile bool _pauseNext;
    private volatile bool _stopRequested;
    private bool _disposed;

    public event EventHandler<PgslDebugLocation> Paused;
    public event EventHandler Continued;
    public event EventHandler Completed;

    public bool BreakOnStart { get; set; } = true;
    public bool PauseOnError { get; set; } = true;
    public bool IsPaused { get; private set; }
    public PgslDebugLocation Current { get; private set; }

    public void SetBreakpoints(IEnumerable<int> oneBasedLines)
    {
        lock (_gate)
            _breakpoints = oneBasedLines.Where(line => line > 0).ToHashSet();
    }

    public void Pause() => _pauseNext = true;

    public void Continue()
    {
        _pauseNext = false;
        Resume();
    }

    public void StepInto()
    {
        _pauseNext = true;
        Resume();
    }

    public void Stop()
    {
        _stopRequested = true;
        _resume.Set();
    }

    internal void BeforeInstruction(PgslVm vm, PgslDebugLocation location)
    {
        if (_stopRequested) throw new PgslDebugStopException();
        bool breakpoint;
        lock (_gate) breakpoint = _breakpoints.Contains(location.Line);
        bool shouldPause = BreakOnStart || _pauseNext || breakpoint;
        BreakOnStart = false;
        if (!shouldPause) return;
        PauseAt(location);
    }

    internal void OnError(PgslDebugLocation location)
    {
        if (PauseOnError && !_stopRequested) PauseAt(location);
    }

    internal void OnCompleted()
    {
        IsPaused = false;
        Current = null;
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private void PauseAt(PgslDebugLocation location)
    {
        Current = location;
        IsPaused = true;
        _pauseNext = false;
        _resume.Reset();
        Paused?.Invoke(this, location);
        _resume.Wait();
        IsPaused = false;
        if (_stopRequested) throw new PgslDebugStopException();
    }

    private void Resume()
    {
        if (!IsPaused) return;
        Continued?.Invoke(this, EventArgs.Empty);
        _resume.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _resume.Dispose();
    }
}

public sealed class PgslDebugStopException : OperationCanceledException
{
    public PgslDebugStopException() : base("PGSL debugging stopped.") { }
}
