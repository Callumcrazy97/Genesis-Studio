using System.Diagnostics;
using System.Globalization;

namespace Genesis.Application.Core.Diagnostics;

/// <summary>
/// Where the time of opening a project goes: each step of an open (reading the project file,
/// listing its resources, building Studio's window...) is timed, and Studio writes the breakdown to
/// its log once the window is up, so a slow open names its cause instead of only feeling slow.
/// </summary>
/// <remarks>
/// <see cref="Begin"/> starts a breakdown and <see cref="Mark"/> ends the current step and names the
/// next. Marks made with no breakdown started are ignored. Everything here is called on the thread
/// that opens the project; the lock only keeps a stray call from elsewhere harmless.
/// </remarks>
public static class ProjectOpenTimings
{
    private static readonly object Gate = new();
    private static readonly List<(string Step, double Milliseconds)> Steps = [];
    private static readonly Stopwatch Clock = new();
    private static string _current = string.Empty;

    /// <summary>Starts a new breakdown whose first step is <paramref name="firstStep"/>.</summary>
    public static void Begin(string firstStep)
    {
        lock (Gate)
        {
            Steps.Clear();
            _current = firstStep;
            Clock.Restart();
        }
    }

    /// <summary>Ends the current step and starts <paramref name="nextStep"/>.</summary>
    public static void Mark(string nextStep)
    {
        lock (Gate)
        {
            if (!Clock.IsRunning) return;
            Steps.Add((_current, Clock.Elapsed.TotalMilliseconds));
            _current = nextStep;
            Clock.Restart();
        }
    }

    /// <summary>
    /// Ends the breakdown and returns its steps (empty when none was started), slowest steps
    /// included in the order they ran.
    /// </summary>
    public static IReadOnlyList<(string Step, double Milliseconds)> End()
    {
        lock (Gate)
        {
            if (!Clock.IsRunning) return [];
            Steps.Add((_current, Clock.Elapsed.TotalMilliseconds));
            Clock.Stop();
            var result = Steps.ToArray();
            Steps.Clear();
            return result;
        }
    }

    /// <summary>One line for a log: the total, then every step that took at least a millisecond.</summary>
    public static string Describe(IReadOnlyList<(string Step, double Milliseconds)> steps)
    {
        double total = steps.Sum(step => step.Milliseconds);
        IEnumerable<string> parts = steps
            .Where(step => step.Milliseconds >= 1)
            .Select(step => string.Create(CultureInfo.InvariantCulture, $"{step.Step.TrimEnd('…', '.')} {step.Milliseconds:F0} ms"));
        return string.Create(CultureInfo.InvariantCulture, $"Project opened in {total:F0} ms: ") + string.Join("; ", parts);
    }
}
