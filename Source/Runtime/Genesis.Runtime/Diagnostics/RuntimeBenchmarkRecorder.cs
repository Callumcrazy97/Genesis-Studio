#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Diagnostics;

/// <summary>Opt-in wall-clock trace of a real Player. No synthetic frame times or quality verdicts.</summary>
public sealed class RuntimeBenchmarkRecorder : IDisposable
{
    private readonly string _directory;
    private readonly double _seconds;
    private readonly double _warmup;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly List<BenchmarkFrame> _frames;
    private readonly List<object> _captures = [];
    private long _previousTick;
    private long _firstTick;
    private long _previousAllocated;
    private TimeSpan _previousPause = GC.GetTotalPauseDuration();
    private double _nextCapture;
    private double _nextMemorySample;
    private long _workingSet;
    private long _heap;
    private bool _written;
    private bool _capturedFrame;

    public RuntimeBenchmarkRecorder(string directory, double seconds, double warmupSeconds = 5)
    {
        if (!double.IsFinite(seconds) || seconds < 1 || seconds > 3600
            || !double.IsFinite(warmupSeconds) || warmupSeconds < 0 || warmupSeconds > 120)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _seconds = seconds;
        _warmup = warmupSeconds;
        _frames = new List<BenchmarkFrame>((int)Math.Min(500_000, seconds * 180));
        _previousAllocated = GC.GetTotalAllocatedBytes(false);
    }

    public bool Complete { get; private set; }
    public string Error { get; private set; } = "";

    /// <summary>Call after present; includes pacing and streaming stalls and excludes boot/warmup.</summary>
    public void AfterPresent(GenesisRuntimeHost host)
    {
        if (Complete || host.Scene is null || host.Renderer is null
            || (host.BootSplash != null && !host.BootSplash.IsComplete)) return;
        long now = Stopwatch.GetTimestamp();
        if (_firstTick == 0) { _firstTick = now; _previousTick = now; return; }
        double elapsed = Stopwatch.GetElapsedTime(_firstTick, now).TotalSeconds;
        double frameMs = Stopwatch.GetElapsedTime(_previousTick, now).TotalMilliseconds;
        _previousTick = now;
        long allocated = GC.GetTotalAllocatedBytes(false);
        long frameAllocated = Math.Max(0, allocated - _previousAllocated);
        _previousAllocated = allocated;
        // Time the collector held every thread during this frame, so a long frame can be told
        // apart from one the game or the renderer caused.
        TimeSpan paused = GC.GetTotalPauseDuration();
        double gcPauseMs = (paused - _previousPause).TotalMilliseconds;
        _previousPause = paused;
        if (_capturedFrame) { _capturedFrame = false; return; }
        if (elapsed < _warmup) return;
        double measured = elapsed - _warmup;
        if (measured >= _nextMemorySample)
        {
            // Environment.WorkingSet asks about this process only. Process.Refresh walks every
            // process on the machine and was itself the longest frame of some runs.
            _workingSet = Environment.WorkingSet;
            _heap = GC.GetTotalMemory(false);
            _nextMemorySample = measured + 1;
        }
        RenderStats stats = host.Renderer.GetStats();
        int loaded = 0, pending = 0;
        // The scene owns its streaming manager; it is not one of the listed subsystems.
        foreach (var provider in host.Scene.Streaming.Providers)
        { loaded += provider.Stats.CellsLoaded; pending += provider.Stats.CellsPending; }
        _frames.Add(new BenchmarkFrame(measured, frameMs, host.LastSimulationMilliseconds,
            stats.GpuMs > 0 ? stats.GpuMs : null, frameAllocated, _workingSet, _heap,
            stats.DrawCalls, stats.Triangles, stats.InstancesDrawn, loaded, pending,
            host.Scene.Camera3D.Position.X, host.Scene.Camera3D.Position.Y, host.Scene.Camera3D.Position.Z,
            gcPauseMs, host.LastCollectMilliseconds, host.LastDrawMilliseconds, host.LastPresentMilliseconds));
        if (measured >= _seconds)
        {
            Complete = true;
            Write(host.Renderer.BackendName, host.Renderer.AdapterName,
                host.Renderer.PixelWidth, host.Renderer.PixelHeight, true);
        }
    }

    /// <summary>Rare captures are excluded from frame percentiles, and separately recorded as overhead.</summary>
    public void Capture(IRenderController renderer)
    {
        if (_firstTick == 0 || Complete) return;
        double elapsed = Stopwatch.GetElapsedTime(_firstTick).TotalSeconds - _warmup;
        if (elapsed < _nextCapture) return;
        _nextCapture = elapsed + Math.Max(2, _seconds / 6);
        long started = Stopwatch.GetTimestamp();
        if (!renderer.TryReadSubmittedFramePixels(out int width, out int height, out byte[] pixels)
            || pixels.Length != width * height * 4) { Error = "Submitted frame capture unavailable."; return; }
        string file = Path.Combine(_directory, $"frame-{_captures.Count:00}.png");
        using Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int row = 0; row < height; row++) Marshal.Copy(pixels, row * width * 4, data.Scan0 + row * data.Stride, width * 4); }
        finally { bitmap.UnlockBits(data); }
        bitmap.Save(file, ImageFormat.Png);
        _captures.Add(new { File = file, Seconds = elapsed, Width = width, Height = height,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))),
            CaptureMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        // Captures can stall readback/compression. Reset the interval; never disguise it as gameplay.
        _previousTick = Stopwatch.GetTimestamp();
        _previousAllocated = GC.GetTotalAllocatedBytes(false);
        _capturedFrame = true;
    }

    public static double Percentile(IEnumerable<double> samples, double percentile)
    {
        double[] sorted = samples.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return 0;
        return sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private void Write(string backend, string adapter, int width, int height, bool completed)
    {
        if (_written) return;
        _written = true;
        File.WriteAllText(Path.Combine(_directory, "frames.json"), JsonSerializer.Serialize(_frames));
        var summary = new
        {
            Completed = completed, Error, Backend = backend, Adapter = adapter, Width = width, Height = height,
            RequestedSeconds = _seconds, WarmupSeconds = _warmup, Frames = _frames.Count,
            MeasuredSeconds = _frames.Count > 0 ? _frames[^1].Seconds - _frames[0].Seconds : 0,
            P95Milliseconds = Percentile(_frames.Select(frame => frame.FrameMilliseconds), .95),
            P99Milliseconds = Percentile(_frames.Select(frame => frame.FrameMilliseconds), .99),
            MaximumFrameMilliseconds = _frames.Count > 0 ? _frames.Max(frame => frame.FrameMilliseconds) : 0,
            PeakWorkingSetBytes = _frames.Count > 0 ? _frames.Max(frame => frame.WorkingSetBytes) : 0,
            AllocatedBytes = _frames.Sum(frame => frame.AllocatedBytes),
            GpuTimingAvailable = _frames.Any(frame => frame.GpuMilliseconds.HasValue),
            VramMeasurementAvailable = false,
            CaptureFramesExcluded = _captures.Count,
            VisualAcceptance = "Pending native image and motion review",
            Captures = _captures,
            Pgsl = PgslProfiler.Snapshot(48),
        };
        string path = Path.Combine(_directory, "benchmark.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    public void Dispose()
    {
        try { if (!_written) Write("", "", 0, 0, false); }
        finally { _process.Dispose(); }
    }

    public readonly record struct BenchmarkFrame(double Seconds, double FrameMilliseconds,
        double SimulationMilliseconds, double? GpuMilliseconds, long AllocatedBytes, long WorkingSetBytes,
        long ManagedHeapBytes, int DrawCalls, int Triangles, int InstancesDrawn, int LoadedCells,
        int PendingCells, float CameraX, float CameraY, float CameraZ, double GcPauseMilliseconds = 0,
        double CollectMilliseconds = 0, double DrawMilliseconds = 0, double PresentMilliseconds = 0);
}
