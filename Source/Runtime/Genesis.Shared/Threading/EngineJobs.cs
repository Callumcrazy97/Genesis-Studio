using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Genesis.Shared.Threading;

/// <summary>
/// A job scheduled with <see cref="EngineJobs"/>: wait for it with <see cref="Complete"/>, or make
/// other jobs wait for it by passing it as their dependency. The default handle is complete.
/// </summary>
public readonly struct JobHandle
{
    internal readonly EngineJobs.Job Job;

    internal JobHandle(EngineJobs.Job job) => Job = job;

    /// <summary>Whether the job has finished.</summary>
    public bool IsCompleted => Job == null || Job.Done;

    /// <summary>Waits for the job, running other queued jobs meanwhile; rethrows a failure of the job's own.</summary>
    public void Complete() => EngineJobs.Wait(this);

    /// <summary>A handle that completes once both have.</summary>
    public static JobHandle Combine(JobHandle first, JobHandle second) => EngineJobs.Combine(first, second);
}

/// <summary>
/// The engine's job system: worker threads of its own for frame and loading work (culling, terrain,
/// generation), apart from the .NET thread pool (whose threads a game's start-up can tie up) and
/// from script jobs (NativeJobPool, below the game's threads in priority).
/// </summary>
/// <remarks>
/// <para><see cref="For"/> splits a loop into batches that the calling thread and idle workers take
/// one at a time until none are left, so a slow core or a long batch never holds the rest up.</para>
/// <para><see cref="Schedule"/> runs one piece of work, after a dependency if given. A worker
/// waiting for a job runs queued jobs meanwhile, so a job may wait for jobs it starts without tying
/// up a worker. The thread that calls <see cref="For"/> takes batches of its own loop, but never
/// other work.</para>
/// <para>A job's failure is rethrown by whoever waits for that job; jobs that depend on it still run.</para>
/// </remarks>
public static class EngineJobs
{
    internal class Job
    {
        private volatile bool _done;
        private Action _work;
        private Exception _failure;
        // Jobs to release once this one is done.
        private Job[] _dependents = [];
        private int _dependentCount;
        // Holds on the job before it may be queued: one per unfinished dependency, one while it is set up.
        internal int Holds;
        // How many times the job goes in the queue (a loop's shares; 1 for anything else).
        internal int Shares = 1;

        public bool Done => _done;

        public Job(Action work) => _work = work;

        public virtual void Run()
        {
            try { _work?.Invoke(); }
            catch (Exception exception) { _failure = exception; }
            _work = null;
            Finish();
        }

        protected void Fail(Exception exception) => Interlocked.CompareExchange(ref _failure, exception, null);

        protected void Finish()
        {
            Job[] dependents;
            int count;
            lock (this)
            {
                _done = true;
                dependents = _dependents;
                count = _dependentCount;
                _dependents = [];
                _dependentCount = 0;
            }
            WakeWaiters();
            for (int i = 0; i < count; i++) Release(dependents[i]);
        }

        /// <summary>Releases <paramref name="dependent"/> once this job is done; false when it is done already.</summary>
        public bool AddDependent(Job dependent)
        {
            lock (this)
            {
                if (_done) return false;
                if (_dependentCount == _dependents.Length) Array.Resize(ref _dependents, Math.Max(4, _dependents.Length * 2));
                _dependents[_dependentCount++] = dependent;
                return true;
            }
        }

        public void ThrowIfFailed()
        {
            if (_failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
        }
    }

    // A loop: batches are taken one at a time, by number, until none are left.
    private sealed class ForJob : Job
    {
        private readonly Action<int, int> _body;
        private readonly int _count, _batch, _batches;
        private int _next = -1, _left;

        public ForJob(int count, int batch, Action<int, int> body) : base(null)
        {
            _body = body;
            _count = count;
            _batch = batch;
            _batches = (count + batch - 1) / batch;
            _left = _batches;
        }

        public int Batches => _batches;

        // Run by each thread given a share (and by the thread that asked, in For): all take batches.
        public override void Run()
        {
            for (int index = Interlocked.Increment(ref _next); index < _batches; index = Interlocked.Increment(ref _next))
            {
                int start = index * _batch;
                int finished = 1;
                try { _body(start, Math.Min(_count, start + _batch)); }
                catch (Exception exception)
                {
                    Fail(exception);
                    // Batches not started yet are not run: the loop has failed. They count as finished.
                    int taken = Interlocked.Exchange(ref _next, _batches);
                    finished += Math.Max(0, _batches - 1 - taken);
                }
                if (Interlocked.Add(ref _left, -finished) == 0) Finish();
            }
        }
    }

    private static readonly ConcurrentQueue<Job> Queue = new();
    private static readonly SemaphoreSlim WorkReady = new(0);
    // Threads waiting for a job sleep on this until a job is done or there is work to run.
    private static readonly object WaitGate = new();
    private static int _waiters;
    private static Thread[] _workers;
    private static readonly object StartGate = new();

    /// <summary>Worker threads (a thread that waits for a job also runs jobs).</summary>
    public static int WorkerCount { get; } = Math.Clamp(Environment.ProcessorCount - 2, 1, 15);

    private static int _threadLimit;

    /// <summary>
    /// Most threads a loop (<see cref="For"/>) is shared between, the thread that asks included:
    /// 0 (automatic) for every worker, 1 to run each loop on the thread that asks. Work scheduled
    /// to run apart keeps at least one worker. Engine.Game.Threads.
    /// </summary>
    public static int ThreadLimit
    {
        get => Volatile.Read(ref _threadLimit);
        set => Volatile.Write(ref _threadLimit, Math.Clamp(value, 0, WorkerCount + 1));
    }

    /// <summary>The threads a loop is shared between now: the limit, or every worker and the thread that asks.</summary>
    public static int Threads => ThreadLimit is > 0 and var limit ? limit : WorkerCount + 1;

    private static void EnsureWorkers()
    {
        if (Volatile.Read(ref _workers) != null) return;
        lock (StartGate)
        {
            if (_workers != null) return;
            var workers = new Thread[WorkerCount];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = "Genesis engine worker " + (i + 1) };
                workers[i].Start();
            }
            Volatile.Write(ref _workers, workers);
        }
    }

    [ThreadStatic] private static bool _isWorker;

    private static void WorkerLoop()
    {
        _isWorker = true;
        while (true)
        {
            WorkReady.Wait();
            while (Queue.TryDequeue(out Job job)) job.Run();
        }
    }

    private static void Enqueue(Job job)
    {
        EnsureWorkers();
        for (int i = 0; i < job.Shares; i++) Queue.Enqueue(job);
        WorkReady.Release(job.Shares);
        WakeWaiters();
    }

    private static void WakeWaiters()
    {
        // The job's state was written before this read: a waiter that has not counted itself yet
        // sees that state when it looks.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref _waiters) == 0) return;
        lock (WaitGate) Monitor.PulseAll(WaitGate);
    }

    // Takes one hold off a job; the last one queues it.
    private static void Release(Job job)
    {
        if (Interlocked.Decrement(ref job.Holds) == 0) Enqueue(job);
    }

    // Queues a job after its dependencies: a hold for each, and one while they are added.
    private static JobHandle Start(Job job, JobHandle first, JobHandle second = default)
    {
        job.Holds = 3;
        if (first.Job == null || !first.Job.AddDependent(job)) Release(job);
        if (second.Job == null || !second.Job.AddDependent(job)) Release(job);
        Release(job);
        return new JobHandle(job);
    }

    /// <summary>Runs <paramref name="work"/> on a worker, once <paramref name="after"/> has finished.</summary>
    public static JobHandle Schedule(Action work, JobHandle after = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Start(new Job(work), after);
    }

    /// <summary>
    /// Calls <paramref name="body"/>(start, end) for consecutive ranges of <paramref name="batch"/>
    /// indices (the last may be shorter) covering 0 to <paramref name="count"/>, on this thread and
    /// the workers, and returns when all are done. A failure in a batch is rethrown here.
    /// </summary>
    public static void For(int count, int batch, Action<int, int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (count <= 0) return;
        batch = Math.Max(1, batch);
        int threads = Threads;
        if (count <= batch || threads <= 1)
        {
            body(0, count);
            return;
        }
        // This thread takes batches too, so the workers are given one share fewer than the batches.
        var job = new ForJob(count, batch, body);
        job.Shares = Math.Min(threads - 1, job.Batches - 1);
        Enqueue(job);
        job.Run();
        Wait(new JobHandle(job));
    }

    /// <summary>The loop of <see cref="For"/> as a job of its own, run by the workers once <paramref name="after"/> has finished.</summary>
    public static JobHandle ScheduleFor(int count, int batch, Action<int, int> body, JobHandle after = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (count <= 0) return Schedule(static () => { }, after);
        var job = new ForJob(count, Math.Max(1, batch), body);
        job.Shares = Math.Min(Math.Max(1, Math.Min(WorkerCount, Threads - 1)), job.Batches);
        return Start(job, after);
    }

    /// <summary>A handle that completes once both have.</summary>
    public static JobHandle Combine(JobHandle first, JobHandle second)
    {
        if (first.IsCompleted) return second;
        if (second.IsCompleted) return first;
        return Start(new Job(null), first, second);
    }

    /// <summary>
    /// Waits for a job; rethrows a failure of the job's own. A worker runs queued jobs meanwhile
    /// (so jobs waiting for jobs never run out of workers); any other thread, the game's included,
    /// only waits, so a frame is never held up by some unrelated long job it picked up.
    /// </summary>
    public static void Wait(JobHandle handle)
    {
        Job job = handle.Job;
        if (job == null) return;
        bool helps = _isWorker;
        while (!job.Done)
        {
            if (helps && Queue.TryDequeue(out Job other)) { other.Run(); continue; }
            Interlocked.Increment(ref _waiters);
            try
            {
                lock (WaitGate)
                    while (!job.Done && (!helps || Queue.IsEmpty)) Monitor.Wait(WaitGate);
            }
            finally { Interlocked.Decrement(ref _waiters); }
        }
        job.ThrowIfFailed();
    }
}
