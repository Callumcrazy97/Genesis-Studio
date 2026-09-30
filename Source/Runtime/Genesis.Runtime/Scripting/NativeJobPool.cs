#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Genesis.Runtime.Scripting;

/// <summary>Bounded native work only. Never schedules gameplay scripts or accesses ECS/GPU state.</summary>
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
        public string Error = "";
        public bool Released, LeaseDropped, WorkEnded, CancellationDisposed;
    }

    private static readonly SemaphoreSlim Workers = new(4, 4);
    private static int _reserved;
    internal const int MaximumGlobalJobs = 32;
    internal const int MaximumContextJobs = 16;
    internal static int ReservedJobs => Volatile.Read(ref _reserved);
    private readonly Dictionary<int, Job> _jobs = [];
    private int _next;
    private readonly object _gate = new();

    public int Start(string kind, Func<CancellationToken, Result> operation)
    {
        lock (_gate)
        {
            if (_jobs.Count >= MaximumContextJobs) throw new InvalidOperationException("Release finished jobs; this Object has the maximum 16 jobs.");
            if (_next == int.MaxValue) throw new InvalidOperationException("Job handle space exhausted.");
            if (Interlocked.Increment(ref _reserved) > MaximumGlobalJobs)
            { Interlocked.Decrement(ref _reserved); throw new InvalidOperationException("Native job capacity is full (32 retained/queued jobs globally)."); }
            int id = ++_next;
            Job job = new(kind); _jobs.Add(id, job);
            _ = Task.Run(async () =>
            {
                bool acquired = false;
                try
                {
                    await Workers.WaitAsync(job.Cancellation.Token).ConfigureAwait(false); acquired = true;
                    lock (job) { job.Cancellation.Token.ThrowIfCancellationRequested(); job.State = "running"; }
                    Result result = operation(job.Cancellation.Token);
                    if (result.Kind != kind) throw new InvalidOperationException("Native operation returned an incorrect result kind.");
                    lock (job) { job.Value = result.Value; job.State = "succeeded"; }
                }
                catch (OperationCanceledException) { lock (job) job.State = "cancelled"; }
                catch (Exception error) { lock (job) { job.State = "failed"; job.Error = error.Message; } }
                finally
                {
                    if (acquired) Workers.Release();
                    lock (job) { job.WorkEnded = true; if (job.Released) DropLease(job); }
                }
            });
            return id;
        }
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
                job.Cancellation.Cancel(); return true;
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
                job.Released = true; job.Value = null;
                if (Finished(job)) DropLease(job);
                else job.Cancellation.Cancel();
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
