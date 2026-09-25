using System.Diagnostics;
using Genesis.Application.Core.Diagnostics;

namespace Genesis.Application.Studio;

/// <summary>One named piece of startup work, and how long it took.</summary>
public sealed record StartupStep(string Label, Action Work)
{
    /// <summary>Milliseconds the step took, once it has run.</summary>
    public double ElapsedMilliseconds { get; internal set; }

    /// <summary>What went wrong, when a non-fatal step failed.</summary>
    public string? Failure { get; internal set; }
}

/// <summary>
/// The work the application actually does before it can show you anything.
/// </summary>
/// <remarks>
/// The splash used to run a timer to 100% while loading nothing: the bar and its captions
/// ("Loading editor modules…") were a fixed animation with no relationship to startup, so it
/// reported progress it had not made and cost roughly a second and a half of deliberate delay.
/// This is the real list — each step names something that genuinely happens, the bar moves when a
/// step finishes, and every step's duration is logged so a slow start can be attributed instead
/// of guessed at.
///
/// A step that fails does not stop startup: warming a cache or probing for the player is not worth
/// refusing to open the application over. The failure is recorded on the step and logged, so it
/// surfaces as information rather than as a silent gap.
/// </remarks>
public sealed class StartupSequence
{
    private readonly List<StartupStep> _steps = [];
    private readonly StudioLog _log;

    public StartupSequence(StudioLog log) => _log = log ?? throw new ArgumentNullException(nameof(log));

    public IReadOnlyList<StartupStep> Steps => _steps;

    /// <summary>Total time the sequence took, in milliseconds.</summary>
    public double TotalMilliseconds { get; private set; }

    public StartupSequence Add(string label, Action work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(work);
        _steps.Add(new StartupStep(label, work));
        return this;
    }

    /// <summary>
    /// Runs every step in order, reporting each one before it starts.
    /// </summary>
    /// <param name="report">
    /// Called as <c>(label, completed, total)</c> — before the step with the count it has already
    /// finished, so a progress bar shows work done rather than work claimed.
    /// </param>
    public void Run(Action<string, int, int> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Stopwatch total = Stopwatch.StartNew();

        for (int index = 0; index < _steps.Count; index++)
        {
            StartupStep step = _steps[index];
            report(step.Label, index, _steps.Count);

            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                step.Work();
            }
            catch (Exception exception)
            {
                step.Failure = exception.Message;
                _log.Warning("Startup", $"{step.Label} failed: {exception.Message}");
            }

            clock.Stop();
            step.ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds;
            _log.Information("Startup", $"{step.Label} — {step.ElapsedMilliseconds:0} ms");
        }

        total.Stop();
        TotalMilliseconds = total.Elapsed.TotalMilliseconds;
        report("Ready.", _steps.Count, _steps.Count);
        _log.Information("Startup", $"Startup complete in {TotalMilliseconds:0} ms across {_steps.Count} step(s).");
    }
}
