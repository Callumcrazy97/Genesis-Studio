using System.Diagnostics;

namespace Genesis.Application.Headless.Phase0;

internal enum ProbeOutcome
{
    Passed,
    Failed,
    Skipped,
}

internal sealed record ComponentProbeResult(
    string Component,
    string Name,
    ProbeOutcome Outcome,
    double Milliseconds,
    string Measure,
    string? Detail = null);

/// <summary>Runs one isolated correctness probe and records a useful measured value with it.</summary>
internal sealed class ComponentProbeRunner
{
    private readonly List<ComponentProbeResult> _results = [];

    public IReadOnlyList<ComponentProbeResult> Results => _results;
    public int Failed => _results.Count(static result => result.Outcome == ProbeOutcome.Failed);

    public ComponentProbeResult Run(string component, string name, Func<string> work)
    {
        long started = Stopwatch.GetTimestamp();
        ComponentProbeResult result;
        try
        {
            string measure = work();
            result = new ComponentProbeResult(
                component,
                name,
                ProbeOutcome.Passed,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                measure);
        }
        catch (Exception exception)
        {
            result = new ComponentProbeResult(
                component,
                name,
                ProbeOutcome.Failed,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                "—",
                $"{exception.GetType().Name}: {exception.Message}");
        }

        _results.Add(result);
        return result;
    }

    public ComponentProbeResult Skip(string component, string name, string reason)
    {
        ComponentProbeResult result = new(
            component,
            name,
            ProbeOutcome.Skipped,
            0,
            "—",
            reason);
        _results.Add(result);
        return result;
    }

    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
