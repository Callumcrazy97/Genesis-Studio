using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.Linq;

namespace Genesis.Runtime.Diagnostics;

/// <summary>One garbage collection the runtime reported while the listener was attached.</summary>
public readonly record struct RuntimeGcRecord(DateTime TimestampUtc, int Generation, string Reason, double PauseMilliseconds);

/// <summary>A type the runtime's allocation sampling named, with the bytes those samples stand for.</summary>
public readonly record struct RuntimeAllocationSample(string TypeName, long Bytes, int Samples);

/// <summary>
/// An in-process listener for the .NET runtime's own garbage-collection events: every collection
/// with its generation, reason and pause, and the runtime's allocation sampling (one event per
/// ~100 KB allocated, naming the type), so a recording can say which types the game allocates
/// most. It is attached only while the debug screen's figures or a recording need it and costs
/// nothing once disposed. No package is needed: this is the runtime's built-in event source.
/// </summary>
public sealed class RuntimeGcEventListener : EventListener
{
    private const string RuntimeSourceName = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;

    private const int GcStartEvent = 1;
    private const int GcRestartEeEndEvent = 3;
    private const int GcSuspendEeBeginEvent = 9;
    private const int GcAllocationTickEvent = 10;

    private readonly object _gate = new();
    private readonly List<RuntimeGcRecord> _collections = new();
    private readonly Dictionary<string, (long Bytes, int Samples)> _allocations = new(StringComparer.Ordinal);
    private DateTime _suspendStarted;
    private int _pendingGeneration = -1;
    private string _pendingReason = string.Empty;
    private readonly bool _sampleAllocations;

    /// <param name="sampleAllocations">
    /// Also listen to allocation sampling (verbose level). A recording wants it; the live figures do not.
    /// </param>
    public RuntimeGcEventListener(bool sampleAllocations = true)
    {
        _sampleAllocations = sampleAllocations;
        // Sources created before this constructor body ran were enabled at Informational level by
        // OnEventSourceCreated; raise to Verbose now that the choice is known.
        if (_sampleAllocations)
            foreach (EventSource source in EventSource.GetSources())
                if (source.Name == RuntimeSourceName)
                    EnableEvents(source, EventLevel.Verbose, GcKeyword);
    }

    /// <summary>Collections seen since the listener was attached (or last <see cref="Clear"/>).</summary>
    public IReadOnlyList<RuntimeGcRecord> Collections
    {
        get
        {
            lock (_gate) return new ReadOnlyCollection<RuntimeGcRecord>(_collections.ToList());
        }
    }

    /// <summary>The most recent collection, if any.</summary>
    public RuntimeGcRecord? LastCollection
    {
        get
        {
            lock (_gate) return _collections.Count == 0 ? null : _collections[^1];
        }
    }

    /// <summary>The sampled types that stand for the most allocated bytes, most first.</summary>
    public IReadOnlyList<RuntimeAllocationSample> TopAllocations(int count)
    {
        lock (_gate)
        {
            return _allocations
                .Select(pair => new RuntimeAllocationSample(pair.Key, pair.Value.Bytes, pair.Value.Samples))
                .OrderByDescending(sample => sample.Bytes)
                .Take(Math.Max(0, count))
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _collections.Clear();
            _allocations.Clear();
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == RuntimeSourceName)
            EnableEvents(eventSource, EventLevel.Informational, GcKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData == null) return;
        try
        {
            switch (eventData.EventId)
            {
                case GcStartEvent:
                    lock (_gate)
                    {
                        _pendingGeneration = ReadInt(eventData, "Depth");
                        _pendingReason = ReasonName(ReadInt(eventData, "Reason"));
                    }
                    break;
                case GcSuspendEeBeginEvent:
                    lock (_gate) _suspendStarted = eventData.TimeStamp;
                    break;
                case GcRestartEeEndEvent:
                    lock (_gate)
                    {
                        if (_pendingGeneration < 0 || _suspendStarted == default) break;
                        double pause = Math.Max(0, (eventData.TimeStamp - _suspendStarted).TotalMilliseconds);
                        _collections.Add(new RuntimeGcRecord(eventData.TimeStamp.ToUniversalTime(), _pendingGeneration, _pendingReason, pause));
                        if (_collections.Count > 4096) _collections.RemoveRange(0, 1024);
                        _pendingGeneration = -1;
                        _suspendStarted = default;
                    }
                    break;
                case GcAllocationTickEvent:
                    if (!_sampleAllocations) break;
                    string type = ReadString(eventData, "TypeName");
                    long amount = ReadLong(eventData, "AllocationAmount64");
                    if (amount <= 0) amount = ReadLong(eventData, "AllocationAmount");
                    if (string.IsNullOrEmpty(type)) type = "(unnamed type)";
                    lock (_gate)
                    {
                        _allocations.TryGetValue(type, out var total);
                        _allocations[type] = (total.Bytes + amount, total.Samples + 1);
                    }
                    break;
            }
        }
        catch (Exception exception) when (exception is InvalidCastException or ArgumentException or IndexOutOfRangeException)
        {
            // A payload shaped differently on another runtime version is skipped, never fatal.
        }
    }

    private static object Payload(EventWrittenEventArgs data, string name)
    {
        if (data.PayloadNames == null || data.Payload == null) return null;
        int index = data.PayloadNames.IndexOf(name);
        return index >= 0 && index < data.Payload.Count ? data.Payload[index] : null;
    }

    private static int ReadInt(EventWrittenEventArgs data, string name) =>
        Payload(data, name) is { } value ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : -1;

    private static long ReadLong(EventWrittenEventArgs data, string name) =>
        Payload(data, name) is { } value ? Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) : 0;

    private static string ReadString(EventWrittenEventArgs data, string name) => Payload(data, name) as string;

    /// <summary>The runtime's GC reason code as a word.</summary>
    public static string ReasonName(int reason) => reason switch
    {
        0 => "AllocSmall",
        1 => "Induced",
        2 => "LowMemory",
        3 => "Empty",
        4 => "AllocLarge",
        5 => "OutOfSpaceSOH",
        6 => "OutOfSpaceLOH",
        7 => "InducedNotForced",
        8 => "Internal",
        9 => "InducedLowMemory",
        10 => "InducedCompacting",
        11 => "LowMemoryHost",
        12 => "PMFullGC",
        13 => "LowMemoryHostBlocking",
        _ => "Other",
    };
}
