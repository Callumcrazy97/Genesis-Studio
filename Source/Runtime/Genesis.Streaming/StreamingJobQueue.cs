using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Genesis.Streaming
{
    /// <summary>
    /// Bounded background worker pool plus rate-limited main-thread completion drain.
    /// </summary>
    public sealed class StreamingJobQueue : IDisposable
    {
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private readonly object _gate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private int _activeJobs;
        private int _maxConcurrent;
        private bool _disposed, _cancellationIssued, _lifetimeDisposed;

        public StreamingJobQueue(int maxConcurrentJobs)
        {
            _maxConcurrent = Math.Clamp(maxConcurrentJobs, 1, 64);
        }

        public int ActiveJobs => Volatile.Read(ref _activeJobs);
        public int PendingMainThreadCallbacks => _mainThread.Count;

        public void SetMaxConcurrent(int maxConcurrentJobs) =>
            Volatile.Write(ref _maxConcurrent, Math.Clamp(maxConcurrentJobs, 1, 64));

        /// <summary>Try to start background work. Returns false when the pool is saturated.</summary>
        public bool TryRunBackground(Action work)
        {
            ArgumentNullException.ThrowIfNull(work);
            return TryRunBackground(token => { token.ThrowIfCancellationRequested(); work(); });
        }

        /// <summary>Native work can observe scene shutdown; gameplay/ECS/GPU work belongs in the completion callback.</summary>
        public bool TryRunBackground(Action<CancellationToken> work)
        {
            ArgumentNullException.ThrowIfNull(work);
            lock (_gate)
            {
                if (_disposed || _activeJobs >= Volatile.Read(ref _maxConcurrent)) return false;
                _activeJobs++;
            }
            // Do not retain the caller's ambient script context in native streaming workers.
            if (ExecutionContext.IsFlowSuppressed()) _ = Task.Run(() => RunWork(work));
            else
            {
                using (ExecutionContext.SuppressFlow()) _ = Task.Run(() => RunWork(work));
            }
            return true;
        }

        private void RunWork(Action<CancellationToken> work)
        {
            try { CancellationToken token = _lifetime.Token; token.ThrowIfCancellationRequested(); work(token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { Console.WriteLine($"[CRITICAL] Streaming job crashed: {ex}"); }
            finally
            {
                lock (_gate)
                {
                    _activeJobs--;
                    DisposeLifetimeIfFinished();
                }
            }
        }

        public void PostMainThread(Action work)
        {
            ArgumentNullException.ThrowIfNull(work);
            lock (_gate) { if (!_disposed) _mainThread.Enqueue(work); }
        }

        public void ProcessMainThread(int maxCallbacks)
            => ProcessMainThread(maxCallbacks, TimeSpan.Zero);

        /// <summary>Drain within both count and elapsed-time budgets. An individual callback cannot be pre-empted.</summary>
        public void ProcessMainThread(int maxCallbacks, TimeSpan timeBudget)
        {
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < maxCallbacks; i++)
            {
                Action work;
                lock (_gate) { if (_disposed || !_mainThread.TryDequeue(out work)) break; }
                work();
                if (timeBudget > TimeSpan.Zero && Stopwatch.GetElapsedTime(started) >= timeBudget) break;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                while (_mainThread.TryDequeue(out _)) { }
            }
            // Cancellation callbacks may call back into this queue; never invoke them under its lock.
            try { _lifetime.Cancel(); }
            finally
            {
                lock (_gate) { _cancellationIssued = true; DisposeLifetimeIfFinished(); }
            }
        }

        private void DisposeLifetimeIfFinished()
        {
            if (_disposed && _cancellationIssued && _activeJobs == 0 && !_lifetimeDisposed)
            { _lifetimeDisposed = true; _lifetime.Dispose(); }
        }
    }
}
