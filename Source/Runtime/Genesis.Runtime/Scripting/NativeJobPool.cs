#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Bounded native work, and script functions run on a worker VM of their own (see
/// <c>PgslCommands.ScriptJobs</c>). Never schedules gameplay scripts or accesses ECS/GPU state.
/// </summary>
internal sealed class NativeJobPool
{
    internal readonly record struct Result(string Kind, object Value);
    internal readonly record struct Snapshot(string State, string Kind, object? Value, string Error);
    private sealed class Job(string kind)
    {
        public readonly CancellationTokenSource Cancellation = new();
        public string State = "queued";
        public readonly string Kind = kind;
        public object? Value;
        /// <summary>A prepared job's set-up, until it is launched.</summary>
        public object? Payload;
        public string Error = "";
        public bool Released, LeaseDropped, WorkEnded, CancellationDisposed;
    }

    private static readonly SemaphoreSlim Workers = new(4, 4);

    // Script jobs run on threads of their own, not the thread pool: a job may compute for many
    // milliseconds, and the engine's own background work (textures, collision, files) must still
    // find pool threads. All but two of the processors (at least one), started as jobs need them,
    // below normal priority so the game's own threads always come first; the rest wait in order.
    /// <summary>Most script jobs that run at once.</summary>
    internal static readonly int ScriptWorkerCount = Math.Max(1, Environment.ProcessorCount - 2);
    private static readonly BlockingCollection<Action> ScriptQueue = new();
    // Urgent jobs (JobScriptUrgent) are taken before any waiting in the normal queue.
    private static readonly BlockingCollection<Action> UrgentQueue = new();
    private static readonly BlockingCollection<Action>[] Queues = [UrgentQueue, ScriptQueue];
    private static int _scriptThreads, _idleScriptThreads;
    /// <summary>Script worker threads started so far.</summary>
    internal static int ScriptThreads => Volatile.Read(ref _scriptThreads);
    private static int _reserved;
    internal const int MaximumGlobalJobs = 32;
    internal const int MaximumContextJobs = 16;
    internal static int ReservedJobs => Volatile.Read(ref _reserved);
    private readonly Dictionary<int, Job> _jobs = [];
    private int _next;
    private readonly object _gate = new();
    // Launched jobs held until the jobs they wait for (JobScriptAfter) have finished.
    private sealed record Waiting(Job Job, Job[] After, Func<CancellationToken, Result> Operation, Action? Ended, bool Urgent);
    private readonly List<Waiting> _waiting = [];

    public int Start(string kind, Func<CancellationToken, Result> operation)
    {
        lock (_gate)
        {
            (int id, Job job) = Reserve(kind);
            Run(job, kind, operation, Workers);
            return id;
        }
    }

    /// <summary>A job that holds its set-up and starts later (<see cref="Launch"/>); it counts against the limits from now.</summary>
    public int Prepare(string kind, object payload)
    {
        lock (_gate)
        {
            (int id, Job job) = Reserve(kind);
            lock (job) { job.State = "prepared"; job.Payload = payload; job.WorkEnded = true; }
            return id;
        }
    }

    /// <summary>The set-up of a job that is prepared and not yet launched; null otherwise.</summary>
    public object? PreparedPayload(int id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out Job? job)) return null;
            lock (job) return job.State == "prepared" ? job.Payload : null;
        }
    }

    /// <summary>
    /// Starts a prepared job on a script worker; false when it is not prepared. <paramref name="ended"/>
    /// runs once on the worker when the job's work has ended, whether it ran, failed or was cancelled
    /// first. With <paramref name="after"/>, the job reads as "waiting" until those jobs of this pool
    /// have finished (succeeded, failed or cancelled), then goes to a worker.
    /// </summary>
    public bool Launch(int id, Func<CancellationToken, Result> operation, Action? ended = null, IReadOnlyList<int>? after = null, bool urgent = false)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out Job? job)) return false;
            List<Job>? pending = null;
            if (after != null)
                foreach (int other in after)
                    if (other != id && _jobs.TryGetValue(other, out Job? dependency) && !IsFinished(dependency))
                        (pending ??= []).Add(dependency);
            lock (job)
            {
                if (job.State != "prepared") return false;
                job.State = pending == null ? "queued" : "waiting"; job.Payload = null; job.WorkEnded = false;
            }
            if (pending == null) RunOnScriptThread(job, job.Kind, operation, ended, this, urgent);
            else _waiting.Add(new Waiting(job, [.. pending], operation, ended, urgent));
            return true;
        }
    }

    private static bool IsFinished(Job job) { lock (job) return Finished(job); }

    // A job of this pool has ended: those waiting for it go to a worker once everything they wait for
    // has ended; a waiting job cancelled or released meanwhile ends without running.
    private void OnJobEnded()
    {
        lock (_gate)
        {
            for (int i = 0; i < _waiting.Count; i++)
            {
                Waiting waiting = _waiting[i];
                bool cancelled; lock (waiting.Job) cancelled = waiting.Job.Cancellation.IsCancellationRequested;
                if (!cancelled && !Array.TrueForAll(waiting.After, IsFinished)) continue;
                _waiting.RemoveAt(i--);
                if (cancelled) { EndWaiting(waiting); continue; }
                lock (waiting.Job) waiting.Job.State = "queued";
                RunOnScriptThread(waiting.Job, waiting.Job.Kind, waiting.Operation, waiting.Ended, this, waiting.Urgent);
            }
        }
    }

    private static void EndWaiting(Waiting waiting)
    {
        try { waiting.Ended?.Invoke(); }
        catch (Exception) { }
        lock (waiting.Job)
        {
            waiting.Job.State = "cancelled"; waiting.Job.WorkEnded = true;
            if (waiting.Job.Released) DropLease(waiting.Job);
        }
    }

    // A waiting job cancelled or released leaves the queue now, not when what it waits for ends.
    private void DropWaiting(Job job)
    {
        int at = _waiting.FindIndex(w => ReferenceEquals(w.Job, job));
        if (at < 0) return;
        Waiting waiting = _waiting[at];
        _waiting.RemoveAt(at);
        EndWaiting(waiting);
    }

    /// <summary>
    /// For checks: every script worker waits until <paramref name="release"/> is set, so jobs started
    /// meanwhile queue behind them; <paramref name="holding"/> is signalled by each worker as it waits.
    /// </summary>
    internal static void HoldWorkers(ManualResetEventSlim release, CountdownEvent holding)
    {
        for (int i = 0; i < ScriptWorkerCount; i++)
        {
            ScriptQueue.Add(() => { holding.Signal(); release.Wait(); });
            StartThreadsFor();
        }
    }

    private static void RunOnScriptThread(Job job, string kind, Func<CancellationToken, Result> operation, Action? ended, NativeJobPool owner, bool urgent = false)
    {
        (urgent ? UrgentQueue : ScriptQueue).Add(() =>
        {
            bool endedCalled = false;
            void End()
            {
                if (endedCalled) return;
                endedCalled = true;
                try { ended?.Invoke(); }
                catch (Exception) { }
            }
            try
            {
                lock (job) { job.Cancellation.Token.ThrowIfCancellationRequested(); job.State = "running"; }
                Result result = operation(job.Cancellation.Token);
                // A job that reads as succeeded has let go of what it held.
                End();
                if (result.Kind != kind) throw new InvalidOperationException("Native operation returned an incorrect result kind.");
                lock (job) { job.Value = result.Value; job.State = "succeeded"; }
            }
            catch (OperationCanceledException) { lock (job) job.State = "cancelled"; }
            catch (Exception error) { lock (job) { job.State = "failed"; job.Error = error.Message; } }
            finally
            {
                End();
                lock (job) { job.WorkEnded = true; if (job.Released) DropLease(job); }
                owner.OnJobEnded();
            }
        });
        StartThreadsFor();
    }

    // More threads while more jobs wait than threads are free, up to the limit.
    private static void StartThreadsFor()
    {
        while (ScriptQueue.Count + UrgentQueue.Count > Volatile.Read(ref _idleScriptThreads))
        {
            int started = Volatile.Read(ref _scriptThreads);
            if (started >= ScriptWorkerCount) break;
            if (Interlocked.CompareExchange(ref _scriptThreads, started + 1, started) != started) continue;
            Interlocked.Increment(ref _idleScriptThreads);
            new Thread(ScriptWorker) { IsBackground = true, Name = "PGSL job worker " + (started + 1), Priority = ThreadPriority.BelowNormal }.Start();
        }
    }

    // A started thread counts as free until it takes its first job, so a burst of jobs starts
    // as many threads as it needs at once.
    private static void ScriptWorker()
    {
        while (true)
        {
            // The urgent queue first (TakeFromAny takes from the first collection with work).
            BlockingCollection<Action>.TakeFromAny(Queues, out Action? work);
            if (work == null) continue;
            Interlocked.Decrement(ref _idleScriptThreads);
            work();
            Interlocked.Increment(ref _idleScriptThreads);
        }
    }

    private (int Id, Job Job) Reserve(string kind)
    {
        if (_jobs.Count >= MaximumContextJobs) throw new InvalidOperationException("Release finished jobs; this Object has the maximum 16 jobs.");
        if (_next == int.MaxValue) throw new InvalidOperationException("Job handle space exhausted.");
        if (Interlocked.Increment(ref _reserved) > MaximumGlobalJobs)
        { Interlocked.Decrement(ref _reserved); throw new InvalidOperationException("Native job capacity is full (32 retained/queued jobs globally)."); }
        int id = ++_next;
        Job job = new(kind); _jobs.Add(id, job);
        return (id, job);
    }

    private static void Run(Job job, string kind, Func<CancellationToken, Result> operation, SemaphoreSlim workers)
    {
        _ = Task.Run(async () =>
        {
            bool acquired = false;
            try
            {
                await workers.WaitAsync(job.Cancellation.Token).ConfigureAwait(false); acquired = true;
                lock (job) { job.Cancellation.Token.ThrowIfCancellationRequested(); job.State = "running"; }
                Result result = operation(job.Cancellation.Token);
                if (result.Kind != kind) throw new InvalidOperationException("Native operation returned an incorrect result kind.");
                lock (job) { job.Value = result.Value; job.State = "succeeded"; }
            }
            catch (OperationCanceledException) { lock (job) job.State = "cancelled"; }
            catch (Exception error) { lock (job) { job.State = "failed"; job.Error = error.Message; } }
            finally
            {
                if (acquired) workers.Release();
                lock (job) { job.WorkEnded = true; if (job.Released) DropLease(job); }
            }
        });
    }

    public Snapshot Read(int id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out Job? job)) return new("invalid", "", null, "Unknown or released job handle.");
            lock (job) return new(job.State, job.Kind, job.Value, job.Error);
        }
    }

    public bool Cancel(int id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out Job? job)) return false;
            lock (job)
            {
                if (Finished(job)) return false;
                // A prepared job has no work to stop, and a script job still waiting for a thread
                // will not start: both are cancelled at once.
                if (job.State == "prepared") { job.State = "cancelled"; job.Payload = null; return true; }
                if (job.State == "queued" && job.Kind == "script") job.State = "cancelled";
                job.Cancellation.Cancel();
                if (job.State == "waiting") DropWaiting(job);
                return true;
            }
        }
    }

    public bool Release(int id)
    {
        lock (_gate)
        {
            if (!_jobs.Remove(id, out Job? job)) return false;
            lock (job)
            {
                job.Released = true; job.Value = null; job.Payload = null;
                if (Finished(job) || job.State == "prepared") DropLease(job);
                else
                {
                    job.Cancellation.Cancel();
                    if (job.State == "waiting") DropWaiting(job);
                }
            }
            return true;
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (int id in new List<int>(_jobs.Keys)) Release(id);
        }
    }

    private static bool Finished(Job job) => job.State is "succeeded" or "failed" or "cancelled";
    private static void DropLease(Job job)
    {
        if (!job.LeaseDropped)
        {
            job.LeaseDropped = true; job.Value = null;
            Interlocked.Decrement(ref _reserved);
        }
        if (job.WorkEnded && !job.CancellationDisposed)
        { job.CancellationDisposed = true; job.Cancellation.Dispose(); }
    }
}
