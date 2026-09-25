using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Diagnostics;

public sealed class LiveProfilerSnapshot
{
    public DateTime TimestampUtc { get; set; }
    public string Room { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
    public double Fps { get; set; }
    public double OnePercentLowFps { get; set; }
    public double FrameMilliseconds { get; set; }
    public double CpuPercent { get; set; }
    public double GpuMilliseconds { get; set; }
    public long WorkingSetBytes { get; set; }
    public long ManagedHeapBytes { get; set; }
    public int DrawCalls { get; set; }
    public int DrawCalls2D { get; set; }
    public int DrawCalls3D { get; set; }
    public int Triangles { get; set; }
    public int Batches { get; set; }
    public int ItemsSubmitted { get; set; }
    public int InstancesDrawn { get; set; }
    public int InstancesCulled { get; set; }
    public int LightsUsed { get; set; }
    public double AoMilliseconds { get; set; }
    public double ContactShadowMilliseconds { get; set; }
    public double VolumetricMilliseconds { get; set; }
    public double BloomMilliseconds { get; set; }
    public double CloudMilliseconds { get; set; }
    public List<PgslProfilerSample> Pgsl { get; set; } = [];
}

public sealed class PgslProfilerSample
{
    public string ObjectName { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public long CallCount { get; set; }
    public long FailedCalls { get; set; }
    public double AverageMicroseconds { get; set; }
    public double MaximumMicroseconds { get; set; }
}

/// <summary>Writes a small atomic snapshot consumed by Studio's Profiler and Frame Debugger.</summary>
public sealed class LiveProfilerTelemetry : IDisposable
{
    private readonly string _path;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Queue<double> _frameTimes = new(600);
    private TimeSpan _previousCpu;
    private DateTime _previousCpuAt;
    private double _publishAccumulator;

    public LiveProfilerTelemetry(string projectRoot)
    {
        string directory = Path.Combine(projectRoot, ".genesis", "Diagnostics");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "live-profiler.json");
        _previousCpu = _process.TotalProcessorTime;
        _previousCpuAt = DateTime.UtcNow;
    }

    public void Sample(float deltaTime, IRenderController renderer, string room)
    {
        if (deltaTime <= 0f || renderer is null) return;
        double milliseconds = deltaTime * 1000d;
        _frameTimes.Enqueue(milliseconds);
        while (_frameTimes.Count > 600) _frameTimes.Dequeue();
        _publishAccumulator += deltaTime;
        if (_publishAccumulator < 0.25d) return;
        _publishAccumulator = 0d;

        DateTime now = DateTime.UtcNow;
        TimeSpan cpu = _process.TotalProcessorTime;
        double elapsed = Math.Max(0.001d, (now - _previousCpuAt).TotalSeconds);
        double cpuPercent = Math.Clamp(
            (cpu - _previousCpu).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100d,
            0d,
            100d);
        _previousCpu = cpu;
        _previousCpuAt = now;
        RenderStats stats = renderer.GetStats();
        double averageMs = _frameTimes.Count == 0 ? 0d : _frameTimes.Average();
        int lowCount = Math.Max(1, (int)Math.Ceiling(_frameTimes.Count * 0.01d));
        double slowAverage = _frameTimes.OrderByDescending(value => value).Take(lowCount).Average();
        LiveProfilerSnapshot snapshot = new()
        {
            TimestampUtc = now,
            Room = room ?? string.Empty,
            Backend = renderer.BackendName ?? string.Empty,
            Fps = averageMs > 0d ? 1000d / averageMs : 0d,
            OnePercentLowFps = slowAverage > 0d ? 1000d / slowAverage : 0d,
            FrameMilliseconds = averageMs,
            CpuPercent = cpuPercent,
            GpuMilliseconds = stats.GpuMs,
            WorkingSetBytes = _process.WorkingSet64,
            ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
            DrawCalls = stats.DrawCalls,
            DrawCalls2D = stats.DrawCalls2D,
            DrawCalls3D = stats.DrawCalls3D,
            Triangles = stats.Triangles,
            Batches = stats.Batches,
            ItemsSubmitted = stats.ItemsSubmitted,
            InstancesDrawn = stats.InstancesDrawn,
            InstancesCulled = stats.InstancesCulled,
            LightsUsed = stats.LightsUsed,
            AoMilliseconds = stats.AoMs,
            ContactShadowMilliseconds = stats.ContactShadowMs,
            VolumetricMilliseconds = stats.LocalVolumetricMs,
            BloomMilliseconds = stats.BloomMs,
            CloudMilliseconds = stats.RaymarchedCloudsMs,
            Pgsl = PgslProfiler.Snapshot(24).Select(profile => new PgslProfilerSample
            {
                ObjectName = profile.ObjectName,
                EventName = profile.EventName,
                CallCount = profile.CallCount,
                FailedCalls = profile.FailedCalls,
                AverageMicroseconds = profile.AverageMicroseconds,
                MaximumMicroseconds = profile.MaximumMicroseconds,
            }).ToList(),
        };
        WriteAtomic(snapshot);
    }

    private void WriteAtomic(LiveProfilerSnapshot snapshot)
    {
        string temp = _path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public void Dispose() => _process.Dispose();
}
