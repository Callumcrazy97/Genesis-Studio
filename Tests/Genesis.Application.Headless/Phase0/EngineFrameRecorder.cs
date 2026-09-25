using System.Diagnostics;

namespace Genesis.Application.Headless.Phase0;

/// <summary>Present-to-present frame statistics for one measured Genesis scene.</summary>
internal sealed record Phase0FrameMetrics(
    long PresentedFrames,
    double DurationSeconds,
    double AverageFps,
    double MinimumFps,
    double MaximumFps,
    double OnePercentLowFps,
    double AverageFrameMilliseconds,
    double MedianFrameMilliseconds,
    double NinetyNinthPercentileFrameMilliseconds,
    double MaximumFrameMilliseconds,
    double AverageCpuFrameMilliseconds,
    double AverageGpuFrameMilliseconds)
{
    public static Phase0FrameMetrics Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Records only presented benchmark frames. Screenshot/readback work is deliberately performed after
/// sampling so instrumentation cannot poison the 1% low or p99.
/// </summary>
internal sealed class EngineFrameRecorder
{
    private readonly List<double> _frameMilliseconds = new(8_192);
    private readonly List<double> _cpuMilliseconds = new(8_192);
    private readonly List<double> _gpuMilliseconds = new(8_192);
    private long _firstPresentTimestamp = -1;
    private long _lastPresentTimestamp = -1;
    private long _lastRecordedTimestamp = -1;
    private long _presentedFrames;
    private int _skipIntervals;

    public void SkipIntervals(int count) => _skipIntervals = Math.Max(_skipIntervals, count);

    public void RecordPresent(long presentTimestamp, double cpuMilliseconds, double gpuMilliseconds)
    {
        _presentedFrames++;
        if (_firstPresentTimestamp < 0)
        {
            _firstPresentTimestamp = presentTimestamp;
        }

        if (_skipIntervals > 0)
        {
            _skipIntervals--;
            _lastPresentTimestamp = presentTimestamp;
            _lastRecordedTimestamp = presentTimestamp;
            return;
        }

        if (_lastPresentTimestamp >= 0)
        {
            double interval = Stopwatch.GetElapsedTime(_lastPresentTimestamp, presentTimestamp).TotalMilliseconds;
            if (interval > 0)
            {
                _frameMilliseconds.Add(interval);
            }
        }

        _lastPresentTimestamp = presentTimestamp;
        _lastRecordedTimestamp = presentTimestamp;
        _cpuMilliseconds.Add(Math.Max(0, cpuMilliseconds));
        if (gpuMilliseconds > 0)
        {
            _gpuMilliseconds.Add(gpuMilliseconds);
        }
    }

    public Phase0FrameMetrics Summarise()
    {
        if (_frameMilliseconds.Count == 0)
        {
            return Phase0FrameMetrics.Empty;
        }

        double[] sorted = [.. _frameMilliseconds];
        Array.Sort(sorted);
        double average = _frameMilliseconds.Average();
        double duration = _firstPresentTimestamp < 0 || _lastRecordedTimestamp < 0
            ? 0
            : Stopwatch.GetElapsedTime(_firstPresentTimestamp, _lastRecordedTimestamp).TotalSeconds;

        return new Phase0FrameMetrics(
            PresentedFrames: _presentedFrames,
            DurationSeconds: duration,
            AverageFps: average > 0 ? 1000.0 / average : 0,
            MinimumFps: sorted[^1] > 0 ? 1000.0 / sorted[^1] : 0,
            MaximumFps: sorted[0] > 0 ? 1000.0 / sorted[0] : 0,
            OnePercentLowFps: OnePercentLow(sorted),
            AverageFrameMilliseconds: average,
            MedianFrameMilliseconds: Percentile(sorted, 0.50),
            NinetyNinthPercentileFrameMilliseconds: Percentile(sorted, 0.99),
            MaximumFrameMilliseconds: sorted[^1],
            AverageCpuFrameMilliseconds: _cpuMilliseconds.Count == 0 ? 0 : _cpuMilliseconds.Average(),
            AverageGpuFrameMilliseconds: _gpuMilliseconds.Count == 0 ? 0 : _gpuMilliseconds.Average());
    }

    private static double OnePercentLow(double[] sortedAscending)
    {
        int count = Math.Max(1, sortedAscending.Length / 100);
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            total += sortedAscending[^(i + 1)];
        }

        double mean = total / count;
        return mean > 0 ? 1000.0 / mean : 0;
    }

    private static double Percentile(double[] sortedAscending, double fraction)
    {
        int index = (int)Math.Clamp(
            MathF.Round((float)(fraction * (sortedAscending.Length - 1))),
            0,
            sortedAscending.Length - 1);
        return sortedAscending[index];
    }
}
