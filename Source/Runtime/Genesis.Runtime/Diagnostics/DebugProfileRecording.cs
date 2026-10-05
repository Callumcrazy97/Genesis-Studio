using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Genesis.Runtime.Scripting;

namespace Genesis.Runtime.Diagnostics;

/// <summary>
/// A profile recorded from the debug screen. It writes one folder per session under the project's
/// debug folder (<c>Debug/Profiles/&lt;date-time&gt;</c>) holding <c>frames.csv</c> (one row per
/// frame), <c>summary.json</c> (the figures a tool reads) and <c>report.md</c> (the same findings
/// for a person: slowest frames, hottest PGSL objects and events, allocation, GC and long-frame
/// warnings). Engine columns and sections appear only when the Engine category is enabled.
/// </summary>
public sealed class DebugProfileRecording : IDisposable
{
    public const string CsvFileName = "frames.csv";
    public const string SummaryFileName = "summary.json";
    public const string ReportFileName = "report.md";

    /// <summary>The columns every recording has.</summary>
    public static readonly string[] GameColumns =
    {
        "frame", "time_ms", "frame_ms", "fps", "cpu_ms", "gpu_ms", "process_cpu_pct",
        "working_set_mb", "private_mb", "managed_heap_mb", "vram_mb", "alloc_kb", "gen0", "gen1", "gen2",
        "gc_pause_ms", "pgsl_ms", "pgsl_calls", "script_errors",
    };

    /// <summary>The columns added when the Engine category is enabled.</summary>
    public static readonly string[] EngineColumns =
    {
        "sim_ms", "collect_ms", "draw_ms", "present_ms", "overlay_ms", "draw_calls", "triangles",
        "batches", "instances", "ao_ms", "contact_shadow_ms", "volumetric_ms", "bloom_ms",
        "clouds_ms", "atmosphere_ms", "parts",
    };

    private const double LongFrameMilliseconds = 1000d / 30d;
    private const double HitchMilliseconds = 100d;
    private const int SlowestKept = 10;

    private readonly StreamWriter _csv;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly long _startedStamp = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly List<float> _frameTimes = new();
    private readonly List<DebugFrameSample> _slowest = new();
    private readonly Dictionary<string, int> _peakInstances = new(StringComparer.Ordinal);
    private readonly List<string> _scriptErrors = new();
    private readonly List<long> _hitchFrames = new();
    private readonly StringBuilder _line = new(256);
    private long _lastInstanceCount = long.MinValue;
    private int _scriptErrorCount;
    private bool _finished;

    private double _cpuSum, _cpuMax, _gpuSum, _gpuMax, _processCpuSum, _processCpuMax, _pgslSum;
    private double _gcPauseSum, _gcPauseMax, _engineSim, _engineCollect, _engineDraw, _enginePresent, _engineOverlay;
    private double _engineAo, _engineContact, _engineVolumetric, _engineBloom, _engineClouds, _engineAtmosphere;
    private long _drawCallsSum, _trianglesSum;
    private int _drawCallsMax, _trianglesMax;
    private long _videoMemoryPeak;
    private long _pgslCalls, _allocated, _workingSetPeak, _privatePeak, _privateFirst, _privateLast, _heapFirst, _heapLast, _heapPeak;
    private int _gen0, _gen1, _gen2, _longFrames;

    private DebugProfileRecording(string folder, string project, string room, bool engine)
    {
        Folder = folder;
        Project = project ?? string.Empty;
        Room = room ?? string.Empty;
        EngineEnabled = engine;
        Directory.CreateDirectory(folder);
        _csv = new StreamWriter(Path.Combine(folder, CsvFileName), append: false, new UTF8Encoding(false), 1 << 16);
        _csv.WriteLine(string.Join(',', engine ? GameColumns.Concat(EngineColumns) : GameColumns));
    }

    /// <summary>Opens a new session folder named after the current local time.</summary>
    public static DebugProfileRecording Create(string profilesDirectory, string project, string room, bool engine)
    {
        if (string.IsNullOrWhiteSpace(profilesDirectory)) throw new ArgumentException("A profiles folder is required.", nameof(profilesDirectory));
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        string folder = Path.Combine(profilesDirectory, stamp);
        for (int suffix = 2; Directory.Exists(folder); suffix++)
            folder = Path.Combine(profilesDirectory, stamp + "_" + suffix.ToString(CultureInfo.InvariantCulture));
        return new DebugProfileRecording(folder, project, room, engine);
    }

    public string Folder { get; }
    public string Project { get; }
    public string Room { get; }
    public bool EngineEnabled { get; }
    public int FrameCount => _frameTimes.Count;
    public TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(_startedStamp);

    /// <summary>Appends one frame. Called by <see cref="RuntimeFrameProfiler.Observe"/>.</summary>
    public void Write(ref DebugFrameSample sample, Func<IReadOnlyDictionary<string, int>> instanceCounter)
    {
        if (_finished) return;
        sample.TimeMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(_startedStamp).TotalMilliseconds;
        int index = _frameTimes.Count;
        _frameTimes.Add((float)sample.FrameMilliseconds);

        _cpuSum += sample.CpuMilliseconds; _cpuMax = Math.Max(_cpuMax, sample.CpuMilliseconds);
        _gpuSum += sample.GpuMilliseconds; _gpuMax = Math.Max(_gpuMax, sample.GpuMilliseconds);
        _processCpuSum += sample.ProcessCpuPercent; _processCpuMax = Math.Max(_processCpuMax, sample.ProcessCpuPercent);
        _pgslSum += sample.PgslMilliseconds; _pgslCalls += sample.PgslCalls;
        _gcPauseSum += sample.GcPauseMilliseconds; _gcPauseMax = Math.Max(_gcPauseMax, sample.GcPauseMilliseconds);
        _allocated += sample.AllocatedBytes;
        _gen0 += sample.Gen0Collections; _gen1 += sample.Gen1Collections; _gen2 += sample.Gen2Collections;
        _workingSetPeak = Math.Max(_workingSetPeak, sample.WorkingSetBytes);
        _privatePeak = Math.Max(_privatePeak, sample.PrivateBytes);
        _heapPeak = Math.Max(_heapPeak, sample.ManagedHeapBytes);
        _videoMemoryPeak = Math.Max(_videoMemoryPeak, sample.VideoMemoryBytes);
        if (index == 0) { _privateFirst = sample.PrivateBytes; _heapFirst = sample.ManagedHeapBytes; }
        _privateLast = sample.PrivateBytes;
        _heapLast = sample.ManagedHeapBytes;
        if (sample.FrameMilliseconds > LongFrameMilliseconds) _longFrames++;
        if (sample.FrameMilliseconds > HitchMilliseconds && _hitchFrames.Count < 50) _hitchFrames.Add(sample.Frame);
        if (EngineEnabled)
        {
            _engineSim += sample.SimulationMilliseconds; _engineCollect += sample.CollectMilliseconds;
            _engineDraw += sample.DrawMilliseconds; _enginePresent += sample.PresentMilliseconds;
            _engineOverlay += sample.OverlayMilliseconds;
            _engineAo += sample.AoMilliseconds; _engineContact += sample.ContactShadowMilliseconds;
            _engineVolumetric += sample.VolumetricMilliseconds; _engineBloom += sample.BloomMilliseconds;
            _engineClouds += sample.CloudsMilliseconds; _engineAtmosphere += sample.AtmosphereMilliseconds;
            _drawCallsSum += sample.DrawCalls; _drawCallsMax = Math.Max(_drawCallsMax, sample.DrawCalls);
            _trianglesSum += sample.Triangles; _trianglesMax = Math.Max(_trianglesMax, sample.Triangles);
        }

        KeepIfSlow(sample);

        // Instance counts are read about once a second; the peak per object goes in the report.
        long second = (long)(sample.TimeMilliseconds / 1000d);
        if (instanceCounter != null && second != _lastInstanceCount)
        {
            _lastInstanceCount = second;
            IReadOnlyDictionary<string, int> counts = instanceCounter();
            if (counts != null)
                foreach (KeyValuePair<string, int> pair in counts)
                    if (!_peakInstances.TryGetValue(pair.Key, out int peak) || pair.Value > peak)
                        _peakInstances[pair.Key] = pair.Value;
        }

        WriteCsv(sample);
        // Flushed every couple of seconds, so a game that is killed keeps nearly all its rows.
        if ((index & 127) == 127) _csv.Flush();
    }

    public void NoteScriptError(ScriptDiagnostic diagnostic)
    {
        if (_finished || diagnostic == null) return;
        _scriptErrorCount++;
        if (_scriptErrors.Count < 20) _scriptErrors.Add(diagnostic.ToDisplayString());
    }

    private void KeepIfSlow(DebugFrameSample sample)
    {
        if (_slowest.Count >= SlowestKept && sample.FrameMilliseconds <= _slowest[^1].FrameMilliseconds) return;
        int at = _slowest.FindIndex(existing => sample.FrameMilliseconds > existing.FrameMilliseconds);
        if (at < 0) _slowest.Add(sample); else _slowest.Insert(at, sample);
        if (_slowest.Count > SlowestKept) _slowest.RemoveAt(_slowest.Count - 1);
    }

    private void WriteCsv(in DebugFrameSample s)
    {
        StringBuilder line = _line.Clear();
        Append(line, s.Frame); Append(line, s.TimeMilliseconds, "0.###"); Append(line, s.FrameMilliseconds, "0.###");
        Append(line, s.FrameMilliseconds > 0 ? 1000d / s.FrameMilliseconds : 0, "0.##");
        Append(line, s.CpuMilliseconds, "0.###"); Append(line, s.GpuMilliseconds, "0.###"); Append(line, s.ProcessCpuPercent, "0.##");
        Append(line, s.WorkingSetBytes / 1048576d, "0.##"); Append(line, s.PrivateBytes / 1048576d, "0.##");
        Append(line, s.ManagedHeapBytes / 1048576d, "0.##"); Append(line, s.VideoMemoryBytes / 1048576d, "0.##");
        Append(line, s.AllocatedBytes / 1024d, "0.##");
        Append(line, s.Gen0Collections); Append(line, s.Gen1Collections); Append(line, s.Gen2Collections);
        Append(line, s.GcPauseMilliseconds, "0.###"); Append(line, s.PgslMilliseconds, "0.####"); Append(line, s.PgslCalls);
        Append(line, s.ScriptErrors);
        if (EngineEnabled)
        {
            Append(line, s.SimulationMilliseconds, "0.###"); Append(line, s.CollectMilliseconds, "0.###");
            Append(line, s.DrawMilliseconds, "0.###"); Append(line, s.PresentMilliseconds, "0.###");
            Append(line, s.OverlayMilliseconds, "0.###"); Append(line, s.DrawCalls); Append(line, s.Triangles);
            Append(line, s.Batches); Append(line, s.InstancesDrawn); Append(line, s.AoMilliseconds, "0.###");
            Append(line, s.ContactShadowMilliseconds, "0.###"); Append(line, s.VolumetricMilliseconds, "0.###");
            Append(line, s.BloomMilliseconds, "0.###"); Append(line, s.CloudsMilliseconds, "0.###");
            Append(line, s.AtmosphereMilliseconds, "0.###");
            line.Append(",\"").Append((s.Parts ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        _csv.WriteLine(line);
    }

    private static void Append(StringBuilder line, long value)
    {
        if (line.Length > 0) line.Append(',');
        line.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void Append(StringBuilder line, double value, string format)
    {
        if (line.Length > 0) line.Append(',');
        line.Append(double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "0");
    }

    /// <summary>Linear-interpolated percentile of an ascending array.</summary>
    public static double Percentile(float[] sorted, double percentile)
    {
        if (sorted == null || sorted.Length == 0) return 0;
        double rank = Math.Clamp(percentile, 0, 100) / 100d * (sorted.Length - 1);
        int low = (int)Math.Floor(rank);
        int high = Math.Min(sorted.Length - 1, low + 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    /// <summary>
    /// Closes the CSV and writes <c>summary.json</c> and <c>report.md</c>. <paramref name="pgsl"/>
    /// is the PGSL profiler's snapshot (reset when the recording started, so it is this recording's).
    /// </summary>
    public string Finish(IReadOnlyList<PgslEventProfile> pgsl, RuntimeGcEventListener gc)
    {
        if (_finished) return Folder;
        _finished = true;
        _csv.Flush();
        _csv.Dispose();

        DebugProfileSummary summary = BuildSummary(pgsl ?? Array.Empty<PgslEventProfile>(), gc);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // A figure a driver reported as NaN must not cost the whole summary.
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        File.WriteAllText(Path.Combine(Folder, SummaryFileName), JsonSerializer.Serialize(summary, options), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Folder, ReportFileName), BuildReport(summary), new UTF8Encoding(false));
        return Folder;
    }

    private DebugProfileSummary BuildSummary(IReadOnlyList<PgslEventProfile> pgsl, RuntimeGcEventListener gc)
    {
        int frames = _frameTimes.Count;
        float[] sorted = _frameTimes.ToArray();
        Array.Sort(sorted);
        double seconds = Math.Max(0.001, Elapsed.TotalSeconds);
        double frameSum = 0;
        foreach (float value in sorted) frameSum += value;
        double average = frames == 0 ? 0 : frameSum / frames;
        int lowCount = Math.Max(1, (int)Math.Ceiling(frames * 0.01));
        double lowAverage = frames == 0 ? 0 : sorted.Skip(Math.Max(0, frames - lowCount)).Average(value => (double)value);
        double perFrame(double sum) => frames == 0 ? 0 : sum / frames;
        const double mb = 1048576d;

        var summary = new DebugProfileSummary
        {
            Project = Project,
            Room = Room,
            StartedUtc = _startedUtc,
            DurationSeconds = Math.Round(seconds, 3),
            Frames = frames,
            EngineCategory = EngineEnabled,
            FrameMilliseconds = new DebugProfileDistribution
            {
                Average = average,
                Minimum = frames == 0 ? 0 : sorted[0],
                P50 = Percentile(sorted, 50),
                P90 = Percentile(sorted, 90),
                P95 = Percentile(sorted, 95),
                P99 = Percentile(sorted, 99),
                Maximum = frames == 0 ? 0 : sorted[^1],
            },
            AverageFps = average > 0 ? 1000d / average : 0,
            OnePercentLowFps = lowAverage > 0 ? 1000d / lowAverage : 0,
            CpuMilliseconds = new DebugProfileAverage { Average = perFrame(_cpuSum), Maximum = _cpuMax },
            GpuMilliseconds = new DebugProfileAverage { Average = perFrame(_gpuSum), Maximum = _gpuMax },
            ProcessCpuPercent = new DebugProfileAverage { Average = perFrame(_processCpuSum), Maximum = _processCpuMax },
            Memory = new DebugProfileMemory
            {
                WorkingSetPeakMegabytes = _workingSetPeak / mb,
                PrivatePeakMegabytes = _privatePeak / mb,
                PrivateStartMegabytes = _privateFirst / mb,
                PrivateEndMegabytes = _privateLast / mb,
                ManagedHeapStartMegabytes = _heapFirst / mb,
                ManagedHeapEndMegabytes = _heapLast / mb,
                ManagedHeapPeakMegabytes = _heapPeak / mb,
                VideoMemoryPeakMegabytes = _videoMemoryPeak / mb,
            },
            Allocations = new DebugProfileAllocations
            {
                TotalMegabytes = _allocated / mb,
                MegabytesPerSecond = _allocated / mb / seconds,
                KilobytesPerFrame = frames == 0 ? 0 : _allocated / 1024d / frames,
                TopTypes = (gc?.TopAllocations(12) ?? Array.Empty<RuntimeAllocationSample>())
                    .Select(sample => new DebugProfileAllocationType { Type = sample.TypeName, SampledMegabytes = sample.Bytes / mb, Samples = sample.Samples })
                    .ToList(),
            },
            Gc = new DebugProfileGc
            {
                Gen0 = _gen0,
                Gen1 = _gen1,
                Gen2 = _gen2,
                PauseTotalMilliseconds = Math.Max(_gcPauseSum, gc?.Collections.Sum(record => record.PauseMilliseconds) ?? 0),
                PauseMaximumMilliseconds = Math.Max(_gcPauseMax, gc?.Collections.Select(record => record.PauseMilliseconds).DefaultIfEmpty(0).Max() ?? 0),
                Reasons = (gc?.Collections ?? Array.Empty<RuntimeGcRecord>())
                    .GroupBy(record => $"gen{record.Generation} {record.Reason}")
                    .ToDictionary(group => group.Key, group => group.Count()),
            },
            LongFrames = new DebugProfileLongFrames
            {
                Over33Milliseconds = _longFrames,
                Over100Milliseconds = _hitchFrames.Count,
                HitchFrames = _hitchFrames.ToList(),
            },
            Pgsl = new DebugProfilePgsl
            {
                TotalMilliseconds = _pgslSum,
                Calls = _pgslCalls,
                AverageMillisecondsPerFrame = perFrame(_pgslSum),
                Events = pgsl.Select(profile => new DebugProfilePgslEvent
                {
                    Object = profile.ObjectName,
                    Event = profile.EventName,
                    Calls = profile.CallCount,
                    Failures = profile.FailedCalls,
                    TotalMilliseconds = profile.TotalMicroseconds / 1000d,
                    AverageMicroseconds = profile.AverageMicroseconds,
                    MaximumMicroseconds = profile.MaximumMicroseconds,
                }).OrderByDescending(item => item.TotalMilliseconds).ToList(),
            },
            ScriptErrors = new DebugProfileScriptErrors { Count = _scriptErrorCount, Recent = _scriptErrors.ToList() },
            SlowestFrames = _slowest.Select(sample => new DebugProfileFrame
            {
                Frame = sample.Frame,
                TimeMilliseconds = sample.TimeMilliseconds,
                FrameMilliseconds = sample.FrameMilliseconds,
                CpuMilliseconds = sample.CpuMilliseconds,
                GpuMilliseconds = sample.GpuMilliseconds,
                PgslMilliseconds = sample.PgslMilliseconds,
                GcPauseMilliseconds = sample.GcPauseMilliseconds,
                Collections = sample.Gen0Collections + sample.Gen1Collections + sample.Gen2Collections,
                AllocatedKilobytes = sample.AllocatedBytes / 1024d,
                Parts = EngineEnabled ? sample.Parts ?? string.Empty : null,
            }).ToList(),
            Files = new DebugProfileFiles { Csv = CsvFileName, Summary = SummaryFileName, Report = ReportFileName },
        };

        summary.Pgsl.Objects = summary.Pgsl.Events
            .GroupBy(item => item.Object, StringComparer.Ordinal)
            .Select(group => new DebugProfilePgslObject
            {
                Object = group.Key,
                PeakInstances = _peakInstances.TryGetValue(group.Key, out int peak) ? peak : 0,
                Calls = group.Sum(item => item.Calls),
                Failures = group.Sum(item => item.Failures),
                TotalMilliseconds = group.Sum(item => item.TotalMilliseconds),
                MaximumMicroseconds = group.Max(item => item.MaximumMicroseconds),
            })
            .Concat(_peakInstances.Keys
                .Where(name => summary.Pgsl.Events.All(item => !string.Equals(item.Object, name, StringComparison.Ordinal)))
                .Select(name => new DebugProfilePgslObject { Object = name, PeakInstances = _peakInstances[name] }))
            .OrderByDescending(item => item.TotalMilliseconds)
            .ThenBy(item => item.Object, StringComparer.Ordinal)
            .ToList();

        if (EngineEnabled)
        {
            summary.Engine = new DebugProfileEngine
            {
                SimulationMilliseconds = perFrame(_engineSim),
                CollectMilliseconds = perFrame(_engineCollect),
                DrawMilliseconds = perFrame(_engineDraw),
                PresentMilliseconds = perFrame(_enginePresent),
                OverlayMilliseconds = perFrame(_engineOverlay),
                DrawCallsAverage = perFrame(_drawCallsSum),
                DrawCallsMaximum = _drawCallsMax,
                TrianglesAverage = perFrame(_trianglesSum),
                TrianglesMaximum = _trianglesMax,
                PassMilliseconds = new Dictionary<string, double>
                {
                    ["ambientOcclusion"] = perFrame(_engineAo),
                    ["contactShadows"] = perFrame(_engineContact),
                    ["localVolumetrics"] = perFrame(_engineVolumetric),
                    ["bloom"] = perFrame(_engineBloom),
                    ["clouds"] = perFrame(_engineClouds),
                    ["atmosphereLut"] = perFrame(_engineAtmosphere),
                },
            };
        }

        summary.Warnings = BuildWarnings(summary);
        return summary;
    }

    private static List<string> BuildWarnings(DebugProfileSummary summary)
    {
        var warnings = new List<string>();
        CultureInfo c = CultureInfo.InvariantCulture;
        if (summary.Frames == 0)
        {
            warnings.Add("No frames were recorded.");
            return warnings;
        }

        if (summary.Allocations.MegabytesPerSecond >= 50)
            warnings.Add(string.Format(c, "Allocation: {0:0.0} MB/s is high; expect frequent collections. Reuse lists and avoid per-frame strings.", summary.Allocations.MegabytesPerSecond));
        else if (summary.Allocations.MegabytesPerSecond >= 10)
            warnings.Add(string.Format(c, "Allocation: {0:0.0} MB/s; worth watching in long sessions.", summary.Allocations.MegabytesPerSecond));
        if (summary.Gc.Gen2 > 0)
            warnings.Add(string.Format(c, "GC: {0} full (gen 2) collection(s) during the recording.", summary.Gc.Gen2));
        if (summary.Gc.PauseMaximumMilliseconds >= 10)
            warnings.Add(string.Format(c, "GC: the longest pause was {0:0.0} ms, enough to drop a frame.", summary.Gc.PauseMaximumMilliseconds));
        double longShare = summary.LongFrames.Over33Milliseconds * 100d / summary.Frames;
        if (longShare >= 1)
            warnings.Add(string.Format(c, "Long frames: {0} of {1} frames ({2:0.0}%) took longer than 33.3 ms.", summary.LongFrames.Over33Milliseconds, summary.Frames, longShare));
        if (summary.LongFrames.Over100Milliseconds > 0)
            warnings.Add(string.Format(c, "Hitches: {0} frame(s) took longer than 100 ms (frames {1}).", summary.LongFrames.Over100Milliseconds,
                string.Join(", ", summary.LongFrames.HitchFrames.Take(10).Select(frame => frame.ToString(c)))));
        if (summary.FrameMilliseconds.P50 > 0 && summary.FrameMilliseconds.P99 > summary.FrameMilliseconds.P50 * 2)
            warnings.Add(string.Format(c, "Stutter: the 99th percentile frame ({0:0.0} ms) is more than twice the median ({1:0.0} ms).",
                summary.FrameMilliseconds.P99, summary.FrameMilliseconds.P50));
        if (summary.ScriptErrors.Count > 0)
            warnings.Add(string.Format(c, "Scripts: {0} script error(s) were raised.", summary.ScriptErrors.Count));
        if (summary.FrameMilliseconds.Average > 0 && summary.Pgsl.AverageMillisecondsPerFrame >= summary.FrameMilliseconds.Average * 0.25)
        {
            DebugProfilePgslEvent hottest = summary.Pgsl.Events.FirstOrDefault();
            warnings.Add(string.Format(c, "PGSL: scripts take {0:0.00} ms a frame, a quarter or more of the frame{1}.",
                summary.Pgsl.AverageMillisecondsPerFrame, hottest == null ? string.Empty : $"; hottest is {hottest.Object}.{hottest.Event}"));
        }

        double growth = summary.Memory.PrivateEndMegabytes - summary.Memory.PrivateStartMegabytes;
        if (growth >= 50)
            warnings.Add(string.Format(c, "Memory: private memory grew by {0:0} MB during the recording; check for something that is never released.", growth));
        return warnings;
    }

    private static string BuildReport(DebugProfileSummary s)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        var md = new StringBuilder();
        void Line(string text = "") => md.Append(text).Append('\n');
        string F(double value, string format = "0.00") => value.ToString(format, c);

        Line($"# Genesis profile: {Escape(s.Project)} / {Escape(s.Room)}");
        Line();
        Line($"Recorded {s.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", c)} for {F(s.DurationSeconds, "0.0")} s, {s.Frames} frames. "
            + (s.EngineCategory ? "Engine figures included (developer build)." : "Game figures only."));
        Line();
        Line("## Summary");
        Line();
        Line("| Measure | Value |");
        Line("|---|---|");
        Line($"| Average FPS | {F(s.AverageFps, "0.0")} (1% low {F(s.OnePercentLowFps, "0.0")}) |");
        Line($"| Frame time avg / p50 / p95 / p99 / max | {F(s.FrameMilliseconds.Average)} / {F(s.FrameMilliseconds.P50)} / {F(s.FrameMilliseconds.P95)} / {F(s.FrameMilliseconds.P99)} / {F(s.FrameMilliseconds.Maximum)} ms |");
        Line($"| CPU (game thread) avg / max | {F(s.CpuMilliseconds.Average)} / {F(s.CpuMilliseconds.Maximum)} ms |");
        Line($"| GPU avg / max | {(s.GpuMilliseconds.Maximum > 0 ? $"{F(s.GpuMilliseconds.Average)} / {F(s.GpuMilliseconds.Maximum)} ms" : "not measured by this renderer")} |");
        Line($"| Process CPU avg / max | {F(s.ProcessCpuPercent.Average, "0.0")} / {F(s.ProcessCpuPercent.Maximum, "0.0")} % of all cores |");
        Line($"| Working set peak | {F(s.Memory.WorkingSetPeakMegabytes, "0.0")} MB |");
        Line($"| Private memory start / end / peak | {F(s.Memory.PrivateStartMegabytes, "0.0")} / {F(s.Memory.PrivateEndMegabytes, "0.0")} / {F(s.Memory.PrivatePeakMegabytes, "0.0")} MB |");
        Line($"| Video memory peak | {(s.Memory.VideoMemoryPeakMegabytes > 0 ? F(s.Memory.VideoMemoryPeakMegabytes, "0.0") + " MB" : "not reported by the driver")} |");
        Line($"| Managed heap start / end / peak | {F(s.Memory.ManagedHeapStartMegabytes, "0.0")} / {F(s.Memory.ManagedHeapEndMegabytes, "0.0")} / {F(s.Memory.ManagedHeapPeakMegabytes, "0.0")} MB |");
        Line($"| Allocated | {F(s.Allocations.TotalMegabytes, "0.0")} MB ({F(s.Allocations.MegabytesPerSecond)} MB/s, {F(s.Allocations.KilobytesPerFrame, "0.0")} KB/frame) |");
        Line($"| Collections gen0 / gen1 / gen2 | {s.Gc.Gen0} / {s.Gc.Gen1} / {s.Gc.Gen2} (pauses {F(s.Gc.PauseTotalMilliseconds)} ms total, {F(s.Gc.PauseMaximumMilliseconds)} ms longest) |");
        Line($"| PGSL | {F(s.Pgsl.TotalMilliseconds)} ms in {s.Pgsl.Calls} event calls ({F(s.Pgsl.AverageMillisecondsPerFrame, "0.000")} ms/frame) |");
        Line($"| Long frames (>33.3 ms / >100 ms) | {s.LongFrames.Over33Milliseconds} / {s.LongFrames.Over100Milliseconds} |");
        Line($"| Script errors | {s.ScriptErrors.Count} |");
        Line();

        Line("## Warnings");
        Line();
        if (s.Warnings.Count == 0) Line("None. Nothing in this recording crossed a warning threshold.");
        foreach (string warning in s.Warnings) Line("- " + Escape(warning));
        Line();

        Line("## Slowest frames");
        Line();
        Line(s.EngineCategory
            ? "| Frame | At (s) | Frame ms | CPU ms | GPU ms | PGSL ms | GC pause ms | GCs | Alloc KB | Engine parts |"
            : "| Frame | At (s) | Frame ms | CPU ms | GPU ms | PGSL ms | GC pause ms | GCs | Alloc KB |");
        Line(s.EngineCategory ? "|---|---|---|---|---|---|---|---|---|---|" : "|---|---|---|---|---|---|---|---|---|");
        foreach (DebugProfileFrame frame in s.SlowestFrames)
            Line($"| {frame.Frame} | {F(frame.TimeMilliseconds / 1000d, "0.00")} | {F(frame.FrameMilliseconds)} | {F(frame.CpuMilliseconds)} | {F(frame.GpuMilliseconds)} | {F(frame.PgslMilliseconds, "0.000")} | {F(frame.GcPauseMilliseconds)} | {frame.Collections} | {F(frame.AllocatedKilobytes, "0.0")} |"
                + (s.EngineCategory ? $" {Escape(frame.Parts)} |" : string.Empty));
        Line();

        Line("## Hottest PGSL objects");
        Line();
        if (s.Pgsl.Objects.Count == 0) Line("No PGSL object ran during the recording.");
        else
        {
            Line("| Object | Peak instances | Calls | Total ms | Max µs | Failures |");
            Line("|---|---|---|---|---|---|");
            foreach (DebugProfilePgslObject item in s.Pgsl.Objects.Take(15))
                Line($"| {Escape(item.Object)} | {item.PeakInstances} | {item.Calls} | {F(item.TotalMilliseconds, "0.000")} | {F(item.MaximumMicroseconds, "0.0")} | {item.Failures} |");
        }

        Line();
        Line("## Hottest PGSL events");
        Line();
        if (s.Pgsl.Events.Count == 0) Line("No PGSL event ran during the recording.");
        else
        {
            Line("| Object.Event | Calls | Total ms | Avg µs | Max µs | Failures |");
            Line("|---|---|---|---|---|---|");
            foreach (DebugProfilePgslEvent item in s.Pgsl.Events.Take(20))
                Line($"| {Escape(item.Object)}.{Escape(item.Event)} | {item.Calls} | {F(item.TotalMilliseconds, "0.000")} | {F(item.AverageMicroseconds, "0.0")} | {F(item.MaximumMicroseconds, "0.0")} | {item.Failures} |");
        }

        Line();
        Line("## Allocation and GC");
        Line();
        if (s.Allocations.TopTypes.Count > 0)
        {
            Line("Types the runtime's allocation sampling named most (each sample stands for about 100 KB):");
            Line();
            Line("| Type | Sampled MB | Samples |");
            Line("|---|---|---|");
            foreach (DebugProfileAllocationType type in s.Allocations.TopTypes)
                Line($"| {Escape(type.Type)} | {F(type.SampledMegabytes, "0.0")} | {type.Samples} |");
            Line();
        }
        else
        {
            Line("The runtime sampled no allocations (fewer than about 100 KB were allocated, or sampling was unavailable).");
            Line();
        }

        if (s.Gc.Reasons.Count > 0)
            Line("Collections by reason: " + string.Join(", ", s.Gc.Reasons.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} ×{pair.Value}")) + ".");
        else
            Line("No collection was reported during the recording.");
        Line();

        if (s.ScriptErrors.Recent.Count > 0)
        {
            Line("## Script errors");
            Line();
            foreach (string error in s.ScriptErrors.Recent) Line("- " + Escape(error));
            Line();
        }

        if (s.Engine != null)
        {
            Line("## Engine (developer)");
            Line();
            Line("| Measure | Average |");
            Line("|---|---|");
            Line($"| Simulation / collect / draw / present / overlay | {F(s.Engine.SimulationMilliseconds)} / {F(s.Engine.CollectMilliseconds)} / {F(s.Engine.DrawMilliseconds)} / {F(s.Engine.PresentMilliseconds)} / {F(s.Engine.OverlayMilliseconds)} ms |");
            Line($"| Draw calls avg / max | {F(s.Engine.DrawCallsAverage, "0")} / {s.Engine.DrawCallsMaximum} |");
            Line($"| Triangles avg / max | {F(s.Engine.TrianglesAverage, "0")} / {s.Engine.TrianglesMaximum} |");
            foreach (KeyValuePair<string, double> pass in s.Engine.PassMilliseconds)
                Line($"| GPU pass {pass.Key} | {F(pass.Value, "0.000")} ms |");
            Line();
        }

        Line("## Files");
        Line();
        Line($"- `{s.Files.Csv}`: one row per frame.");
        Line($"- `{s.Files.Summary}`: these figures for tools.");
        return md.ToString();
    }

    private static string Escape(string text) => (text ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        _csv.Dispose();
    }
}

public sealed class DebugProfileSummary
{
    public string Schema { get; set; } = "genesis.profile/1";
    public string Project { get; set; } = string.Empty;
    public string Room { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public double DurationSeconds { get; set; }
    public int Frames { get; set; }
    public bool EngineCategory { get; set; }
    public double AverageFps { get; set; }
    public double OnePercentLowFps { get; set; }
    public DebugProfileDistribution FrameMilliseconds { get; set; } = new();
    public DebugProfileAverage CpuMilliseconds { get; set; } = new();
    public DebugProfileAverage GpuMilliseconds { get; set; } = new();
    public DebugProfileAverage ProcessCpuPercent { get; set; } = new();
    public DebugProfileMemory Memory { get; set; } = new();
    public DebugProfileAllocations Allocations { get; set; } = new();
    public DebugProfileGc Gc { get; set; } = new();
    public DebugProfileLongFrames LongFrames { get; set; } = new();
    public DebugProfilePgsl Pgsl { get; set; } = new();
    public DebugProfileScriptErrors ScriptErrors { get; set; } = new();
    public List<DebugProfileFrame> SlowestFrames { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    /// <summary>Present only when the Engine category was enabled.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DebugProfileEngine Engine { get; set; }
    public DebugProfileFiles Files { get; set; } = new();
}

public sealed class DebugProfileDistribution
{
    public double Average { get; set; }
    public double Minimum { get; set; }
    public double P50 { get; set; }
    public double P90 { get; set; }
    public double P95 { get; set; }
    public double P99 { get; set; }
    public double Maximum { get; set; }
}

public sealed class DebugProfileAverage
{
    public double Average { get; set; }
    public double Maximum { get; set; }
}

public sealed class DebugProfileMemory
{
    public double WorkingSetPeakMegabytes { get; set; }
    public double PrivateStartMegabytes { get; set; }
    public double PrivateEndMegabytes { get; set; }
    public double PrivatePeakMegabytes { get; set; }
    public double ManagedHeapStartMegabytes { get; set; }
    public double ManagedHeapEndMegabytes { get; set; }
    public double ManagedHeapPeakMegabytes { get; set; }
    /// <summary>Dedicated video memory this process used at most; 0 when the driver did not say.</summary>
    public double VideoMemoryPeakMegabytes { get; set; }
}

public sealed class DebugProfileAllocations
{
    public double TotalMegabytes { get; set; }
    public double MegabytesPerSecond { get; set; }
    public double KilobytesPerFrame { get; set; }
    public List<DebugProfileAllocationType> TopTypes { get; set; } = new();
}

public sealed class DebugProfileAllocationType
{
    public string Type { get; set; } = string.Empty;
    public double SampledMegabytes { get; set; }
    public int Samples { get; set; }
}

public sealed class DebugProfileGc
{
    public int Gen0 { get; set; }
    public int Gen1 { get; set; }
    public int Gen2 { get; set; }
    public double PauseTotalMilliseconds { get; set; }
    public double PauseMaximumMilliseconds { get; set; }
    public Dictionary<string, int> Reasons { get; set; } = new();
}

public sealed class DebugProfileLongFrames
{
    public int Over33Milliseconds { get; set; }
    public int Over100Milliseconds { get; set; }
    public List<long> HitchFrames { get; set; } = new();
}

public sealed class DebugProfilePgsl
{
    public double TotalMilliseconds { get; set; }
    public long Calls { get; set; }
    public double AverageMillisecondsPerFrame { get; set; }
    public List<DebugProfilePgslObject> Objects { get; set; } = new();
    public List<DebugProfilePgslEvent> Events { get; set; } = new();
}

public sealed class DebugProfilePgslObject
{
    public string Object { get; set; } = string.Empty;
    public int PeakInstances { get; set; }
    public long Calls { get; set; }
    public long Failures { get; set; }
    public double TotalMilliseconds { get; set; }
    public double MaximumMicroseconds { get; set; }
}

public sealed class DebugProfilePgslEvent
{
    public string Object { get; set; } = string.Empty;
    public string Event { get; set; } = string.Empty;
    public long Calls { get; set; }
    public long Failures { get; set; }
    public double TotalMilliseconds { get; set; }
    public double AverageMicroseconds { get; set; }
    public double MaximumMicroseconds { get; set; }
}

public sealed class DebugProfileScriptErrors
{
    public int Count { get; set; }
    public List<string> Recent { get; set; } = new();
}

public sealed class DebugProfileFrame
{
    public long Frame { get; set; }
    public double TimeMilliseconds { get; set; }
    public double FrameMilliseconds { get; set; }
    public double CpuMilliseconds { get; set; }
    public double GpuMilliseconds { get; set; }
    public double PgslMilliseconds { get; set; }
    public double GcPauseMilliseconds { get; set; }
    public int Collections { get; set; }
    public double AllocatedKilobytes { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string Parts { get; set; }
}

public sealed class DebugProfileEngine
{
    public double SimulationMilliseconds { get; set; }
    public double CollectMilliseconds { get; set; }
    public double DrawMilliseconds { get; set; }
    public double PresentMilliseconds { get; set; }
    public double OverlayMilliseconds { get; set; }
    public double DrawCallsAverage { get; set; }
    public int DrawCallsMaximum { get; set; }
    public double TrianglesAverage { get; set; }
    public int TrianglesMaximum { get; set; }
    public Dictionary<string, double> PassMilliseconds { get; set; } = new();
}

public sealed class DebugProfileFiles
{
    public string Csv { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Report { get; set; } = string.Empty;
}
