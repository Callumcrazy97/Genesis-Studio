using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Genesis.Runtime.Scripting
{
    /// <summary>
    /// Exact, low-overhead timing for authored PGSL object events. The profiler is disabled during
    /// normal play and enabled by the project player's Debug launch path.
    /// </summary>
    public static class PgslProfiler
    {
        private sealed class MutableEventProfile
        {
            public string ObjectName;
            public string EventName;
            public int LastEntityId;
            public long CallCount;
            public long FailedCalls;
            public long TotalTicks;
            public long MaximumTicks;
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<string, MutableEventProfile> Events =
            new(StringComparer.Ordinal);
        private static volatile bool _enabled;

        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public static void Reset()
        {
            lock (Gate)
                Events.Clear();
        }

        internal static void Record(
            string objectName,
            string eventName,
            int entityId,
            long elapsedTicks,
            bool failed)
        {
            if (!_enabled) return;

            objectName ??= "<object>";
            eventName ??= "<event>";
            string key = objectName + '\u001f' + eventName;
            lock (Gate)
            {
                if (!Events.TryGetValue(key, out MutableEventProfile profile))
                {
                    profile = new MutableEventProfile
                    {
                        ObjectName = objectName,
                        EventName = eventName,
                    };
                    Events.Add(key, profile);
                }

                profile.LastEntityId = entityId;
                profile.CallCount++;
                if (failed) profile.FailedCalls++;
                profile.TotalTicks += Math.Max(0, elapsedTicks);
                profile.MaximumTicks = Math.Max(profile.MaximumTicks, elapsedTicks);
            }
        }

        public static IReadOnlyList<PgslEventProfile> Snapshot(int maximum = int.MaxValue)
        {
            if (maximum <= 0) return Array.Empty<PgslEventProfile>();

            var result = new List<PgslEventProfile>();
            lock (Gate)
            {
                foreach (MutableEventProfile profile in Events.Values)
                {
                    result.Add(new PgslEventProfile(
                        profile.ObjectName,
                        profile.EventName,
                        profile.LastEntityId,
                        profile.CallCount,
                        profile.FailedCalls,
                        TicksToMicroseconds(profile.TotalTicks),
                        TicksToMicroseconds(profile.MaximumTicks)));
                }
            }

            result.Sort((left, right) => right.TotalMicroseconds.CompareTo(left.TotalMicroseconds));
            if (result.Count > maximum)
                result.RemoveRange(maximum, result.Count - maximum);
            return result;
        }

        private static double TicksToMicroseconds(long ticks) =>
            ticks * 1_000_000d / Stopwatch.Frequency;
    }

    public sealed record PgslEventProfile(
        string ObjectName,
        string EventName,
        int LastEntityId,
        long CallCount,
        long FailedCalls,
        double TotalMicroseconds,
        double MaximumMicroseconds)
    {
        public double AverageMicroseconds =>
            CallCount == 0 ? 0d : TotalMicroseconds / CallCount;
    }
}
