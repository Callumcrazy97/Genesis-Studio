using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Genesis.Shared.Diagnostics;

/// <summary>
/// A nested timing tree of a game's start-up and of each room it loads, for finding where loading
/// time goes without a profiler. Off unless <c>GENESIS_LOAD_PROFILE=1</c>; when off, a span costs
/// one static read and nothing is recorded.
/// </summary>
/// <remarks>
/// A span opened inside another on the same thread is its child. Spans on the game's own thread
/// (see <see cref="UseCurrentThreadAsGameThread"/>) are listed under "game thread": their times
/// are wall time that held the game up. Spans on any other thread are listed under "worker
/// threads", where several threads' times are added together and may exceed the wall time.
/// </remarks>
public static class LoadProfile
{
    public const string EnvironmentVariable = "GENESIS_LOAD_PROFILE";

    /// <summary>
    /// Set to a file path to have every span's start and end and every moment written to that file
    /// at once, with the time and thread, so a start that stops part of the way shows where.
    /// </summary>
    public const string TraceEnvironmentVariable = "GENESIS_STARTUP_TRACE";

    private static readonly string TracePath = Environment.GetEnvironmentVariable(TraceEnvironmentVariable) is { Length: > 0 } trace ? trace : null;
    private static readonly object TraceGate = new();
    private static bool _enabled = Environment.GetEnvironmentVariable(EnvironmentVariable) == "1" || TracePath != null;

    private static int _traceWatchStarted;
    private static int _traceLines;
    private const int MaximumTraceLines = 5000;

    /// <summary>Writes one line to the start-up trace file (GENESIS_STARTUP_TRACE) at once; nothing when it is not set.</summary>
    public static void Trace(string text)
    {
        if (TracePath == null || text == null) return;
        if (Interlocked.Exchange(ref _traceWatchStarted, 1) == 0)
        {
            // Every two seconds, the thread pool's state: a start held up by work queued to a pool with
            // no thread free shows here. A thread of its own, so a busy pool cannot stop it.
            var watch = new Thread(() =>
            {
                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(2000);
                    Trace($"thread pool: {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} work items queued, {ThreadPool.CompletedWorkItemCount} done");
                }
            }) { IsBackground = true, Name = "Genesis start-up trace" };
            watch.Start();
        }
        // The start is what it is for: a game's frames would fill it without end.
        int written = Interlocked.Increment(ref _traceLines);
        if (written > MaximumTraceLines) return;
        if (written == MaximumTraceLines) text = $"(trace stops here: {MaximumTraceLines} lines)";
        string line = $"{Stopwatch.GetElapsedTime(ProcessStartTicks).TotalSeconds:F3} [{Environment.CurrentManagedThreadId}] {text}{Environment.NewLine}";
        try
        {
            lock (TraceGate) System.IO.File.AppendAllText(TracePath, line);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            // A trace that cannot be written must not change the start it describes.
        }
    }

    /// <summary>True when spans are recorded. Set from <c>GENESIS_LOAD_PROFILE=1</c>; tests may set it.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    internal sealed class Node
    {
        public readonly string Name;
        public readonly Node Parent;
        public readonly Node Root;
        public long Ticks, MaxTicks;
        public int Count;
        public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
        public readonly List<Node> Order = new();

        public Node(string name, Node parent)
        {
            Name = name;
            Parent = parent;
            Root = parent?.Root ?? this;
        }

        public Node Child(string name)
        {
            lock (Children)
            {
                if (Children.TryGetValue(name, out Node child)) return child;
                child = new Node(name, this);
                Children.Add(name, child);
                Order.Add(child);
                return child;
            }
        }

        public void Add(long ticks)
        {
            Interlocked.Add(ref Ticks, ticks);
            Interlocked.Increment(ref Count);
            long max;
            while (ticks > (max = Interlocked.Read(ref MaxTicks)))
                if (Interlocked.CompareExchange(ref MaxTicks, ticks, max) == max) break;
        }
    }

    private static Node _gameRoot = new("game thread", null);
    private static Node _workerRoot = new("worker threads", null);
    private static int _gameThread;
    private static readonly List<(string Name, double Seconds)> Marks = new();
    private static readonly long ProcessStartTicks = ProcessStart();

    [ThreadStatic] private static Node _current;

    private static long ProcessStart()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            TimeSpan since = DateTime.Now - process.StartTime;
            return Stopwatch.GetTimestamp() - (long)(since.TotalSeconds * Stopwatch.Frequency);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return Stopwatch.GetTimestamp();
        }
    }

    /// <summary>Names the calling thread as the game's thread: its spans are wall time that held the game up.</summary>
    public static void UseCurrentThreadAsGameThread() => _gameThread = Environment.CurrentManagedThreadId;

    /// <summary>One span being timed; disposing it records the time.</summary>
    public readonly struct Span : IDisposable
    {
        private readonly Node _node;
        private readonly Node _parent;
        private readonly long _started;

        internal Span(Node node, Node parent, long started)
        {
            _node = node;
            _parent = parent;
            _started = started;
        }

        public void Dispose()
        {
            if (_node == null) return;
            _node.Add(Stopwatch.GetTimestamp() - _started);
            if (TracePath != null) Trace("end " + _node.Name);
            // A span closed out of order (an exception unwinding several) still leaves its parent current.
            _current = _parent;
        }
    }

    /// <summary>Times the work until the returned span is disposed, as a child of the span open on this thread.</summary>
    public static Span Begin(string name)
    {
        if (!_enabled || name == null) return default;
        if (TracePath != null) Trace("begin " + name);
        Node parent = CurrentFor();
        Node node = parent.Child(name);
        _current = node;
        return new Span(node, parent, Stopwatch.GetTimestamp());
    }

    /// <summary>Records work timed elsewhere (a frame's parts, a wait) as a child of the span open on this thread.</summary>
    public static void Add(string name, double milliseconds)
    {
        if (!_enabled || name == null || !(milliseconds >= 0)) return;
        CurrentFor().Child(name).Add((long)(milliseconds * Stopwatch.Frequency / 1000.0));
    }

    /// <summary>Notes a moment of the start-up, in seconds since the process started.</summary>
    public static void Mark(string name)
    {
        if (!_enabled || name == null) return;
        if (TracePath != null) Trace("moment " + name);
        double seconds = Stopwatch.GetElapsedTime(ProcessStartTicks).TotalSeconds;
        lock (Marks) Marks.Add((name, seconds));
    }

    private static Node CurrentFor()
    {
        Node root = _gameThread == 0 || Environment.CurrentManagedThreadId == _gameThread ? _gameRoot : _workerRoot;
        // A thread whose spans were recorded before a report started a new tree begins again at its root.
        if (_current == null || !ReferenceEquals(_current.Root, root)) _current = root;
        return _current;
    }

    /// <summary>Forgets everything recorded so far, so the next report covers only what follows.</summary>
    public static void Reset()
    {
        if (!_enabled) return;
        Interlocked.Exchange(ref _gameRoot, new Node("game thread", null));
        Interlocked.Exchange(ref _workerRoot, new Node("worker threads", null));
        _current = null;
        lock (Marks) Marks.Clear();
    }

    /// <summary>
    /// The tree recorded since the last report, one line per span, with the moments noted, and
    /// starts a new tree. Spans still open keep timing into the old tree and are not reported.
    /// </summary>
    public static List<string> TakeReport(string title)
    {
        var lines = new List<string>();
        if (!_enabled) return lines;
        Node game = Interlocked.Exchange(ref _gameRoot, new Node("game thread", null));
        Node workers = Interlocked.Exchange(ref _workerRoot, new Node("worker threads", null));
        _current = null;
        lines.Add($"Load profile: {title}, {Stopwatch.GetElapsedTime(ProcessStartTicks).TotalSeconds:F2} s after the process started");
        lock (Marks)
        {
            if (Marks.Count > 0)
            {
                var text = new StringBuilder("  moments (s since the process started): ");
                for (int i = 0; i < Marks.Count; i++)
                {
                    if (i > 0) text.Append(", ");
                    text.Append(Marks[i].Name).Append(' ').Append(Marks[i].Seconds.ToString("F2", CultureInfo.InvariantCulture));
                }

                lines.Add(text.ToString());
                Marks.Clear();
            }
        }

        Write(lines, game, 1, "wall time on the game's thread");
        Write(lines, workers, 1, "summed over threads, which overlap");
        return lines;
    }

    private static void Write(List<string> lines, Node node, int depth, string note)
    {
        List<Node> children;
        lock (node.Children) children = new List<Node>(node.Order);
        if (children.Count == 0) return;
        if (note != null) lines.Add($"  {node.Name} ({note}):");
        children.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
        long shown = 0;
        int hidden = 0;
        foreach (Node child in children)
        {
            double ms = Milliseconds(child.Ticks);
            // Tiny pieces would bury the ones that matter.
            if (ms < 0.5) { hidden++; continue; }
            shown += child.Ticks;
            var text = new StringBuilder();
            text.Append(' ', 2 + depth * 2).Append(child.Name).Append(' ')
                .Append(ms.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms");
            if (child.Count > 1)
                text.Append(" (").Append(child.Count).Append(" times, longest ")
                    .Append(Milliseconds(child.MaxTicks).ToString("F0", CultureInfo.InvariantCulture)).Append(" ms)");
            lines.Add(text.ToString());
            Write(lines, child, depth + 1, null);
        }

        // What a span's children do not account for is its own work.
        if (node.Parent != null && node.Ticks > 0 && children.Count > 0)
        {
            double rest = Milliseconds(node.Ticks - shown);
            if (rest >= 1)
                lines.Add(new string(' ', 2 + depth * 2) + "(not in a smaller span) " + rest.ToString("F0", CultureInfo.InvariantCulture) + " ms"
                    + (hidden > 0 ? $", with {hidden} pieces under 0.5 ms" : string.Empty));
        }
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
