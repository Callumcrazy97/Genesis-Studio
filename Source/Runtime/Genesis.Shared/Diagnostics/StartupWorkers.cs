using System;
using System.Threading;

namespace Genesis.Shared.Diagnostics;

/// <summary>
/// Runs a loop's items on a few threads of its own, with the calling thread taking items too, for
/// work a game does while it starts (reading and decoding files a newly installed game opens for
/// the first time, which an antivirus scans one open at a time per thread).
/// </summary>
/// <remarks>
/// Not the thread pool: <c>Parallel.For</c> called from a thread outside the pool waits until a
/// pool thread takes its first share of the work, and while a game starts the pool's threads may
/// all be waiting on work that needs the very thread that is waiting (a lock it holds, a lazy
/// value it is making). These threads depend on nothing but the work.
/// </remarks>
public static class StartupWorkers
{
    /// <summary>How many threads (the caller included) a start-up loop uses on this PC.</summary>
    public static int DefaultCount => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>
    /// Calls <paramref name="body"/> once for each index below <paramref name="count"/>, on
    /// <paramref name="threads"/> threads at most (the caller is one of them), and returns when all
    /// are done. The body must handle its own failures; one that escapes is rethrown here once
    /// every thread has stopped.
    /// </summary>
    public static void For(int count, Action<int> body, int threads = 0, string name = "Genesis start-up worker")
    {
        ArgumentNullException.ThrowIfNull(body);
        if (count <= 0) return;
        if (threads <= 0) threads = DefaultCount;
        threads = Math.Min(threads, count);
        int next = -1;
        Exception failure = null;

        void Work()
        {
            try
            {
                for (int index = Interlocked.Increment(ref next); index < count; index = Interlocked.Increment(ref next))
                    body(index);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
                // The other threads stop at their next item.
                Interlocked.Exchange(ref next, count);
            }
        }

        var helpers = new Thread[threads - 1];
        for (int i = 0; i < helpers.Length; i++)
        {
            helpers[i] = new Thread(Work) { IsBackground = true, Name = name };
            helpers[i].Start();
        }

        Work();
        foreach (Thread helper in helpers) helper.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
