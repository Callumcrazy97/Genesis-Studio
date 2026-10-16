using System;
using System.Diagnostics;
using System.Globalization;

namespace Genesis.Shared.Diagnostics;

/// <summary>
/// Timed phases written to standard output, only when something is capturing it (a profiler such
/// as ProfileWiz, or a script). A profiler's event log then shows when each phase started and how
/// long it took next to its samples, instead of only time spread across threads. A normal launch
/// has no captured output and writes nothing.
/// </summary>
public static class ConsoleTimings
{
    private static readonly bool Captured = IsCaptured();

    public static bool Enabled => Captured;

    /// <summary>Times until disposed: <c>using (ConsoleTimings.Measure("Open Room editor")) …</c>.</summary>
    public static Phase Measure(string name) => new(Captured ? name : null);

    /// <summary>One line, stamped with the time of day like the engine's own console lines.</summary>
    public static void Write(string line)
    {
        if (!Captured) return;
        try
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:HH:mm:ss.fff}] Genesis: {line}"));
            Console.Out.Flush();
        }
        catch (Exception error) when (error is System.IO.IOException or ObjectDisposedException)
        {
            // A closed pipe must never break the app.
        }
    }

    private static bool IsCaptured()
    {
        try { return Console.IsOutputRedirected; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { return false; }
    }

    public readonly struct Phase : IDisposable
    {
        private readonly string _name;
        private readonly long _started;

        internal Phase(string name)
        {
            _name = name;
            _started = name == null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_name == null) return;
            double milliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            Write(string.Create(CultureInfo.InvariantCulture, $"{_name} took {milliseconds:F0} ms"));
        }
    }
}
