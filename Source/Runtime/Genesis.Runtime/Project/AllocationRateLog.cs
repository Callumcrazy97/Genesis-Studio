using System;
using System.Diagnostics;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Writes to the game's log how much managed memory the running game allocates, a line every
    /// few seconds: megabytes, frames, kilobytes a frame and how many collections that cost. A game
    /// that allocates every frame collects often, and every collection can be a hitch. Off unless
    /// <see cref="EnvironmentVariable"/> is "1" or the game runs with the debugger.
    /// </summary>
    public sealed class AllocationRateLog
    {
        /// <summary>Set to "1" to write the allocation rate to the game's log.</summary>
        public const string EnvironmentVariable = "GENESIS_ALLOCATION_LOG";

        private readonly Action<string> _write;
        private readonly double _intervalSeconds;
        private long _windowStarted;
        private long _allocatedAtStart;
        private int _collectionsAtStart;
        private int _frames;

        public AllocationRateLog(Action<string> write, double intervalSeconds = 5)
        {
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _intervalSeconds = intervalSeconds > 0 ? intervalSeconds : 5;
        }

        /// <summary>True when the person running the game asked for the allocation rate.</summary>
        public static bool Requested(bool debugMode) =>
            debugMode || Environment.GetEnvironmentVariable(EnvironmentVariable) is "1" or "types";

        /// <summary>
        /// Called when a frame has been presented. Frames that are not the game's (the loading
        /// screen, a room change) restart the window rather than being counted.
        /// </summary>
        public void FrameEnded(bool counted)
        {
            long now = Stopwatch.GetTimestamp();
            if (!counted || _windowStarted == 0)
            {
                Restart(now);
                return;
            }

            _frames++;
            double seconds = Stopwatch.GetElapsedTime(_windowStarted, now).TotalSeconds;
            if (seconds < _intervalSeconds) return;

            long allocated = GC.GetTotalAllocatedBytes(precise: false) - _allocatedAtStart;
            int collections = GC.CollectionCount(0) - _collectionsAtStart;
            _write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Managed allocations: {allocated / (1024.0 * 1024.0):F1} MB in {seconds:F1} s over {_frames} frames "
                + $"({allocated / 1024.0 / Math.Max(1, _frames):F1} KB a frame, {allocated / (1024.0 * 1024.0) / seconds:F1} MB/s), "
                + $"{collections} gen0 collections"));
            string types = _types?.TakeSummary();
            if (!string.IsNullOrEmpty(types)) _write("Most allocated types: " + types);
            Restart(now);
        }

        private AllocationTypeSampler _types;

        /// <summary>
        /// With <see cref="EnvironmentVariable"/> set to "types", also names the types allocated
        /// most, from the runtime's allocation samples (one about every 100 KB).
        /// </summary>
        public void SampleTypes()
        {
            if (Environment.GetEnvironmentVariable(EnvironmentVariable) == "types") _types ??= new AllocationTypeSampler();
        }

        /// <summary>Listens to the runtime's allocation samples and counts them by type.</summary>
        private sealed class AllocationTypeSampler : System.Diagnostics.Tracing.EventListener
        {
            private readonly System.Collections.Generic.Dictionary<string, long> _bytes = new(StringComparer.Ordinal);

            protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime")
                    EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
            }

            protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs data)
            {
                if (data.EventName == null || !data.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || data.Payload == null) return;
                int typeIndex = data.PayloadNames.IndexOf("TypeName");
                int amountIndex = data.PayloadNames.IndexOf("AllocationAmount64");
                if (typeIndex < 0) return;
                string type = data.Payload[typeIndex] as string ?? "?";
                long amount = amountIndex >= 0 && data.Payload[amountIndex] is ulong big ? (long)big : 100_000;
                lock (_bytes)
                {
                    _bytes.TryGetValue(type, out long sum);
                    _bytes[type] = sum + amount;
                }
            }

            public string TakeSummary()
            {
                lock (_bytes)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var pair in System.Linq.Enumerable.Take(System.Linq.Enumerable.OrderByDescending(_bytes, pair => pair.Value), 12))
                        parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{pair.Key} {pair.Value / (1024.0 * 1024.0):F1} MB"));
                    _bytes.Clear();
                    return string.Join(", ", parts);
                }
            }
        }

        private void Restart(long now)
        {
            _windowStarted = now;
            _allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
            _collectionsAtStart = GC.CollectionCount(0);
            _frames = 0;
        }
    }
}
