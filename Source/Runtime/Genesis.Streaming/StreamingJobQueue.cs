using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Genesis.Streaming
{
    /// <summary>
    /// Bounded background worker pool plus rate-limited main-thread completion drain.
    /// </summary>
    public sealed class StreamingJobQueue : IDisposable
    {
        private readonly SemaphoreSlim _slots;
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private int _activeJobs;
        private int _maxConcurrent;

        public StreamingJobQueue(int maxConcurrentJobs)
        {
            _maxConcurrent = Math.Max(1, maxConcurrentJobs);
            const int poolCeiling = 64;
            _slots = new SemaphoreSlim(poolCeiling, poolCeiling);
        }

        public int ActiveJobs => _activeJobs;

        public void SetMaxConcurrent(int maxConcurrentJobs) =>
            _maxConcurrent = Math.Max(1, maxConcurrentJobs);

        /// <summary>Try to start background work. Returns false when the pool is saturated.</summary>
        public bool TryRunBackground(Action work)
        {
            while (true)
            {
                int active = Volatile.Read(ref _activeJobs);
                int maxConcurrent = Volatile.Read(ref _maxConcurrent);
                if (active >= maxConcurrent)
                    return false;

                if (Interlocked.CompareExchange(ref _activeJobs, active + 1, active) == active)
                    break;
            }

            if (!_slots.Wait(0))
            {
                Interlocked.Decrement(ref _activeJobs);
                return false;
            }

            Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CRITICAL] Streaming job crashed: {ex}");
                }
                finally
                {
                    Interlocked.Decrement(ref _activeJobs);
                    _slots.Release();
                }
            });
            return true;
        }

        public void PostMainThread(Action work) => _mainThread.Enqueue(work);

        public void ProcessMainThread(int maxCallbacks)
        {
            for (int i = 0; i < maxCallbacks && _mainThread.TryDequeue(out Action work); i++)
                work();
        }

        public void Dispose() => _slots.Dispose();
    }
}
