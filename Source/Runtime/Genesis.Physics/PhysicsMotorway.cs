using System;
using System.Threading;

namespace Genesis.Physics;

/// <summary>
/// Dedicated physics worker for R7.12. Runs Bepu <c>Timestep</c> off the window Update thread so
/// the sim motorway is isolated from render; the caller still waits for each step so ECS Pre/Post
/// physics systems stay ordered on the main thread.
/// </summary>
public sealed class PhysicsMotorway : IDisposable
{
    public const string WorkerThreadName = "Genesis-PhysicsMotorway";

    private readonly object _workGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly Thread _thread;
    private Action? _pending;
    private Exception? _fault;
    private volatile bool _stop;
    private int _lastWorkerThreadId;

    public PhysicsMotorway()
    {
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = WorkerThreadName,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Managed thread id observed inside the last <see cref="Run"/> body.</summary>
    public int LastWorkerThreadId => _lastWorkerThreadId;

    /// <summary>True after the worker has started (always once constructed).</summary>
    public bool IsRunning => _thread.IsAlive;

    /// <summary>Execute <paramref name="work"/> on the physics worker and block until it completes.</summary>
    public void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_stop)
            throw new ObjectDisposedException(nameof(PhysicsMotorway));

        _idle.Wait();
        lock (_workGate)
        {
            _fault = null;
            _pending = work;
            _idle.Reset();
        }

        _wake.Set();
        _idle.Wait();

        Exception? fault = _fault;
        if (fault != null)
            throw new InvalidOperationException("Physics motorway worker failed.", fault);
    }

    private void WorkerLoop()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop)
                break;

            Action? work;
            lock (_workGate)
            {
                work = _pending;
                _pending = null;
            }

            if (work != null)
            {
                try
                {
                    _lastWorkerThreadId = Environment.CurrentManagedThreadId;
                    work();
                }
                catch (Exception ex)
                {
                    _fault = ex;
                }
            }

            _idle.Set();
        }

        _idle.Set();
    }

    public void Dispose()
    {
        if (_stop)
            return;
        _stop = true;
        _wake.Set();
        if (!_thread.Join(millisecondsTimeout: 2000))
        {
            // Background thread — abandon on shutdown rather than hang Studio exit.
        }

        _wake.Dispose();
        _idle.Dispose();
    }
}
