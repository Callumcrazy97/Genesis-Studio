using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Diagnostics;

/// <summary>What the host measured of one frame, handed to <see cref="RuntimeFrameProfiler.Observe"/>.</summary>
public struct DebugHostTimings
{
    /// <summary>Wall-clock time since the previous frame.</summary>
    public double FrameMilliseconds;
    public double SimulationMilliseconds;
    public double CollectMilliseconds;
    public double DrawMilliseconds;
    public double PresentMilliseconds;
    public double OverlayMilliseconds;
}

/// <summary>One frame as the debug screen and a recording see it.</summary>
public struct DebugFrameSample
{
    public long Frame;
    public double TimeMilliseconds;
    public double FrameMilliseconds;
    /// <summary>Game-thread work: update, gathering draws, issuing them and the overlay. Excludes waiting to present.</summary>
    public double CpuMilliseconds;
    public double GpuMilliseconds;
    public double ProcessCpuPercent;
    public long WorkingSetBytes;
    public long PrivateBytes;
    public long ManagedHeapBytes;
    /// <summary>Video memory this process uses (0 when the driver does not say).</summary>
    public long VideoMemoryBytes;
    public long AllocatedBytes;
    public int Gen0Collections;
    public int Gen1Collections;
    public int Gen2Collections;
    public double GcPauseMilliseconds;
    public double PgslMilliseconds;
    public long PgslCalls;
    public int ScriptErrors;

    // Engine category (developer builds only).
    public double SimulationMilliseconds;
    public double CollectMilliseconds;
    public double DrawMilliseconds;
    public double PresentMilliseconds;
    public double OverlayMilliseconds;
    public int DrawCalls;
    public int Triangles;
    public int Batches;
    public int InstancesDrawn;
    public double AoMilliseconds;
    public double ContactShadowMilliseconds;
    public double VolumetricMilliseconds;
    public double BloomMilliseconds;
    public double CloudsMilliseconds;
    public double AtmosphereMilliseconds;
    public string Parts;
}

/// <summary>
/// The debug screen's measurements: frame times and their percentiles, process CPU, working set
/// and private memory, the managed heap, allocation rate, collections per generation and their
/// pauses, PGSL time, and (in developer builds) the engine's passes. All of it comes from the
/// process and runtime APIs (<see cref="GC"/>, the process's own memory counters, an in-process
/// <see cref="RuntimeGcEventListener"/>), never from a package.
/// While the debug screen is closed and nothing is recording, <see cref="Observe"/> returns at once.
/// </summary>
public sealed class RuntimeFrameProfiler : IDisposable
{
    private const int HistoryLength = 600;

    private readonly float[] _history = new float[HistoryLength];
    private int _historyCount;
    private int _historyNext;
    private long _frame;

    private bool _primed;
    private long _lastAllocated;
    private TimeSpan _lastPause;
    private readonly int[] _lastCollections = new int[3];
    private long _lastPgslTicks;
    private long _lastPgslCalls;
    private TimeSpan _lastCpu;
    private long _lastCpuStamp;
    private long _lastSlowStamp;
    private long _allocWindowStart = -1;
    private long _allocWindowBytes;
    private double _allocWindowSeconds;
    private int _pendingScriptErrors;
    private RuntimeGcEventListener _gcListener;
    private bool _gcListenerSamplesAllocations;
    private int _threadCountBusy;

    /// <summary>True while the debug screen is showing live figures.</summary>
    public bool LiveSampling { get; set; }

    /// <summary>True when the Engine category is shown and recorded.</summary>
    public bool EngineEnabled { get; set; } = DebugCategories.EngineRequested;

    /// <summary>The recording in progress, or null.</summary>
    public DebugProfileRecording Recording { get; private set; }
    public bool IsRecording => Recording != null;

    /// <summary>Counts each object's live instances; set by whoever owns the script host.</summary>
    public Func<IReadOnlyDictionary<string, int>> InstanceCounter { get; set; }

    public DebugFrameSample Last { get; private set; }
    public double ProcessCpuPercent { get; private set; }
    public long WorkingSetBytes { get; private set; }
    public long PeakWorkingSetBytes { get; private set; }
    public long PrivateBytes { get; private set; }
    public long ManagedHeapBytes { get; private set; }
    public long CommittedBytes { get; private set; }
    /// <summary>Dedicated video memory this process uses, per the display driver; 0 when unknown.</summary>
    public long VideoMemoryBytes { get; private set; }
    /// <summary>The video memory budget the driver offers this process; 0 when unknown.</summary>
    public long VideoMemoryBudgetBytes { get; private set; }
    public double GcPauseTimePercent { get; private set; }
    public double AllocationMegabytesPerSecond { get; private set; }
    public int HandleCount { get; private set; }
    public int ThreadCount { get; private set; }
    public RuntimeGcRecord? LastCollection => _gcListener?.LastCollection;

    /// <summary>Frames observed so far (live or recording).</summary>
    public long FrameCount => _frame;

    /// <summary>Called once per presented frame by the host.</summary>
    public void Observe(in DebugHostTimings timings, IRenderController renderer, string parts = null)
    {
        if (!LiveSampling && Recording == null) return;
        EnsureGcListener();

        long now = Stopwatch.GetTimestamp();
        var sample = new DebugFrameSample
        {
            Frame = ++_frame,
            FrameMilliseconds = timings.FrameMilliseconds,
            CpuMilliseconds = timings.SimulationMilliseconds + timings.CollectMilliseconds
                + timings.DrawMilliseconds + timings.OverlayMilliseconds,
            SimulationMilliseconds = timings.SimulationMilliseconds,
            CollectMilliseconds = timings.CollectMilliseconds,
            DrawMilliseconds = timings.DrawMilliseconds,
            PresentMilliseconds = timings.PresentMilliseconds,
            OverlayMilliseconds = timings.OverlayMilliseconds,
        };

        // Allocation and collections since the last observed frame: exact, and cheap enough to read every frame.
        long allocated = GC.GetTotalAllocatedBytes(false);
        sample.AllocatedBytes = _primed ? Math.Max(0, allocated - _lastAllocated) : 0;
        _lastAllocated = allocated;
        TimeSpan pause = GC.GetTotalPauseDuration();
        sample.GcPauseMilliseconds = _primed ? Math.Max(0, (pause - _lastPause).TotalMilliseconds) : 0;
        _lastPause = pause;
        for (int generation = 0; generation < 3; generation++)
        {
            int count = GC.CollectionCount(generation);
            int delta = _primed ? Math.Max(0, count - _lastCollections[generation]) : 0;
            _lastCollections[generation] = count;
            if (generation == 0) sample.Gen0Collections = delta;
            else if (generation == 1) sample.Gen1Collections = delta;
            else sample.Gen2Collections = delta;
        }

        _primed = true;

        long pgslTicks = PgslProfiler.TotalTicks;
        long pgslCalls = PgslProfiler.TotalCalls;
        sample.PgslMilliseconds = pgslTicks >= _lastPgslTicks ? (pgslTicks - _lastPgslTicks) * 1000d / Stopwatch.Frequency : 0;
        sample.PgslCalls = Math.Max(0, pgslCalls - _lastPgslCalls);
        _lastPgslTicks = pgslTicks;
        _lastPgslCalls = pgslCalls;
        sample.ScriptErrors = Interlocked.Exchange(ref _pendingScriptErrors, 0);

        // Process CPU over windows of a quarter second: the OS counts processor time in ~15 ms
        // ticks, so a single frame's share would be mostly noise.
        if (_lastCpuStamp == 0 || Stopwatch.GetElapsedTime(_lastCpuStamp, now).TotalMilliseconds >= 250)
            SampleCpu(now);
        sample.ProcessCpuPercent = ProcessCpuPercent;

        // Memory: four times a second (two cheap system calls and the GC's own figures).
        if (_lastSlowStamp == 0 || Stopwatch.GetElapsedTime(_lastSlowStamp, now).TotalMilliseconds >= 250)
        {
            _lastSlowStamp = now;
            SampleMemory();
        }

        sample.WorkingSetBytes = WorkingSetBytes;
        sample.PrivateBytes = PrivateBytes;
        sample.ManagedHeapBytes = ManagedHeapBytes;
        sample.VideoMemoryBytes = VideoMemoryBytes;

        if (renderer != null)
        {
            RenderStats stats = renderer.GetStats();
            sample.GpuMilliseconds = stats.GpuMs;
            if (EngineEnabled)
            {
                sample.DrawCalls = Math.Max(stats.DrawCalls, stats.DrawCalls2D + stats.DrawCalls3D);
                sample.Triangles = stats.Triangles;
                sample.Batches = stats.Batches;
                sample.InstancesDrawn = stats.InstancesDrawn;
                sample.AoMilliseconds = stats.AoMs;
                sample.ContactShadowMilliseconds = stats.ContactShadowMs;
                sample.VolumetricMilliseconds = stats.LocalVolumetricMs;
                sample.BloomMilliseconds = stats.BloomMs;
                sample.CloudsMilliseconds = stats.RaymarchedCloudsMs;
                sample.AtmosphereMilliseconds = stats.AtmosphereLutMs;
            }
        }

        if (EngineEnabled) sample.Parts = parts ?? string.Empty;

        // Allocation rate over roughly the last second.
        if (_allocWindowStart < 0) _allocWindowStart = now;
        _allocWindowBytes += sample.AllocatedBytes;
        _allocWindowSeconds = Stopwatch.GetElapsedTime(_allocWindowStart, now).TotalSeconds;
        if (_allocWindowSeconds >= 0.5)
        {
            AllocationMegabytesPerSecond = _allocWindowBytes / 1048576d / _allocWindowSeconds;
            _allocWindowStart = now;
            _allocWindowBytes = 0;
        }

        if (sample.FrameMilliseconds > 0)
        {
            _history[_historyNext] = (float)sample.FrameMilliseconds;
            _historyNext = (_historyNext + 1) % HistoryLength;
            _historyCount = Math.Min(HistoryLength, _historyCount + 1);
        }

        Recording?.Write(ref sample, InstanceCounter);
        Last = sample;
    }

    /// <summary>A script error raised this frame; counted into the next sample.</summary>
    public void NoteScriptError(ScriptDiagnostic diagnostic)
    {
        Interlocked.Increment(ref _pendingScriptErrors);
        Recording?.NoteScriptError(diagnostic);
    }

    /// <summary>The frame-time percentile (0–100) over the last <see cref="HistoryLength"/> frames.</summary>
    public double FramePercentile(double percentile)
    {
        if (_historyCount == 0) return 0;
        var sorted = new float[_historyCount];
        Array.Copy(_history, sorted, _historyCount);
        Array.Sort(sorted);
        return DebugProfileRecording.Percentile(sorted, percentile);
    }

    /// <summary>Average FPS of the slowest 1% of recent frames.</summary>
    public double OnePercentLowFps()
    {
        if (_historyCount == 0) return 0;
        var sorted = new float[_historyCount];
        Array.Copy(_history, sorted, _historyCount);
        Array.Sort(sorted);
        int count = Math.Max(1, (int)Math.Ceiling(sorted.Length * 0.01));
        double sum = 0;
        for (int index = sorted.Length - count; index < sorted.Length; index++) sum += sorted[index];
        double average = sum / count;
        return average > 0 ? 1000d / average : 0;
    }

    /// <summary>Average of the most recent <paramref name="frames"/> frame times (about half a second at 60 FPS).</summary>
    public double RecentFrameMilliseconds(int frames = 30)
    {
        int count = Math.Min(Math.Max(1, frames), _historyCount);
        if (count == 0) return 0;
        double sum = 0;
        for (int step = 1; step <= count; step++)
            sum += _history[(_historyNext - step + HistoryLength) % HistoryLength];
        return sum / count;
    }

    public double AverageFrameMilliseconds()
    {
        if (_historyCount == 0) return 0;
        double sum = 0;
        for (int index = 0; index < _historyCount; index++) sum += _history[index];
        return sum / _historyCount;
    }

    /// <summary>
    /// Starts writing a recording into a new folder under <paramref name="profilesDirectory"/>.
    /// The PGSL profiler is switched on and reset so the recording's object and event figures are its own.
    /// </summary>
    public DebugProfileRecording StartRecording(string profilesDirectory, string project, string room)
    {
        if (Recording != null) return Recording;
        PgslProfiler.Reset();
        PgslProfiler.Enabled = true;
        _lastPgslTicks = 0;
        _lastPgslCalls = 0;
        Recording = DebugProfileRecording.Create(profilesDirectory, project, room, EngineEnabled);
        EnsureGcListener();
        return Recording;
    }

    /// <summary>Stops the recording, writes its summary and report, and returns its folder (or null).</summary>
    public string StopRecording()
    {
        DebugProfileRecording recording = Recording;
        if (recording == null) return null;
        Recording = null;
        string folder;
        try
        {
            folder = recording.Finish(PgslProfiler.Snapshot(), _gcListener);
        }
        finally
        {
            recording.Dispose();
            EnsureGcListener();
        }

        return folder;
    }

    private void EnsureGcListener()
    {
        bool wanted = LiveSampling || Recording != null;
        bool sampleAllocations = Recording != null;
        if (!wanted)
        {
            _gcListener?.Dispose();
            _gcListener = null;
            return;
        }

        if (_gcListener != null && _gcListenerSamplesAllocations == sampleAllocations) return;
        _gcListener?.Dispose();
        _gcListener = new RuntimeGcEventListener(sampleAllocations);
        _gcListenerSamplesAllocations = sampleAllocations;
    }

    /// <summary>
    /// Called for frames nobody is watching: releases the GC listener, and forgets the last
    /// counters so the first watched frame after a gap does not carry the gap's allocations.
    /// </summary>
    public void Idle()
    {
        _primed = false;
        if (!LiveSampling && Recording == null && _gcListener != null)
        {
            _gcListener.Dispose();
            _gcListener = null;
        }
    }

    private void SampleCpu(long now)
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            if (_lastCpuStamp != 0)
            {
                double elapsed = Stopwatch.GetElapsedTime(_lastCpuStamp, now).TotalMilliseconds;
                if (elapsed > 0.01)
                    ProcessCpuPercent = Math.Clamp((cpu - _lastCpu).TotalMilliseconds / (elapsed * Environment.ProcessorCount) * 100d, 0d, 100d);
            }

            _lastCpu = cpu;
            _lastCpuStamp = now;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            _lastCpuStamp = now;
        }
    }

    private void SampleMemory()
    {
        if (TryReadProcessMemory(out long workingSet, out long peakWorkingSet, out long privateBytes))
        {
            WorkingSetBytes = workingSet;
            PeakWorkingSetBytes = peakWorkingSet;
            PrivateBytes = privateBytes;
        }

        if (OperatingSystem.IsWindows() && GetProcessHandleCount(GetCurrentProcess(), out uint handles))
            HandleCount = (int)handles;

        if (Genesis.Rendering.Diagnostics.VideoMemoryProbe.TryQuery(out long videoMemory, out long videoBudget))
        {
            VideoMemoryBytes = videoMemory;
            VideoMemoryBudgetBytes = videoBudget;
        }

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        ManagedHeapBytes = info.HeapSizeBytes > 0 ? info.HeapSizeBytes : GC.GetTotalMemory(false);
        CommittedBytes = info.TotalCommittedBytes;
        GcPauseTimePercent = info.PauseTimePercentage;

        // Counting threads walks every process on the machine, so it is done off the game thread.
        if (LiveSampling && Interlocked.CompareExchange(ref _threadCountBusy, 1, 0) == 0)
        {
            Task.Run(() =>
            {
                try
                {
                    using Process process = Process.GetCurrentProcess();
                    ThreadCount = process.Threads.Count;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }
                finally
                {
                    Interlocked.Exchange(ref _threadCountBusy, 0);
                }
            });
        }
    }

    /// <summary>The process's working set, peak working set and private (committed) bytes.</summary>
    public static bool TryReadProcessMemory(out long workingSet, out long peakWorkingSet, out long privateBytes)
    {
        workingSet = peakWorkingSet = privateBytes = 0;
        if (!OperatingSystem.IsWindows()) return false;
        var counters = new ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
        if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size)) return false;
        workingSet = (long)counters.WorkingSetSize;
        peakWorkingSet = (long)counters.PeakWorkingSetSize;
        privateBytes = (long)counters.PrivateUsage;
        return true;
    }

    public void Dispose()
    {
        if (Recording != null)
        {
            try { StopRecording(); }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
            {
            }
        }

        _gcListener?.Dispose();
        _gcListener = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);
}
