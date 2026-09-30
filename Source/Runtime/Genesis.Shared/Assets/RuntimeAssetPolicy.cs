using System;
using System.Threading;

namespace Genesis.Shared.Assets
{
    /// <summary>
    /// How long frame-path asset caches may trust an entry before re-validating it against disk.
    /// <para>
    /// Explicit invalidation (Studio saves, the F5 player's asset watcher) bumps
    /// <see cref="Generation"/>, which every participating cache treats as immediate expiry, so the
    /// poll interval is only a fallback for external writers. Exported games load assets once:
    /// <see cref="DisablePolling"/> makes every frame-path cache trust its entries indefinitely.
    /// </para>
    /// </summary>
    public static class RuntimeAssetPolicy
    {
        /// <summary>Default fallback re-validation interval for caches on the per-frame draw path.</summary>
        public const int DefaultFramePathIntervalMilliseconds = 1000;

        private static int _framePathInterval = DefaultFramePathIntervalMilliseconds;
        private static long _generation;

        /// <summary>
        /// Fallback re-validation interval for per-frame caches, or <see cref="Timeout.Infinite"/>
        /// when polling is disabled (exported games).
        /// </summary>
        public static int FramePathIntervalMilliseconds => Volatile.Read(ref _framePathInterval);

        public static bool PollingEnabled => FramePathIntervalMilliseconds != Timeout.Infinite;

        /// <summary>Incremented by every explicit asset invalidation.</summary>
        public static long Generation => Interlocked.Read(ref _generation);

        public static void Invalidate() => Interlocked.Increment(ref _generation);

        public static void DisablePolling() => Volatile.Write(ref _framePathInterval, Timeout.Infinite);

        public static void SetFramePathInterval(int milliseconds) =>
            Volatile.Write(ref _framePathInterval, milliseconds < 0 ? Timeout.Infinite : milliseconds);

        /// <summary>
        /// The next time a cache entry should be re-validated. Entries loaded together would otherwise
        /// expire together and put every file check for a scene into one frame, so intervals of at
        /// least 200 ms are stretched by up to a quarter again, spread by <paramref name="keyHash"/>.
        /// Returns <see cref="long.MaxValue"/> when polling is disabled. An interval of zero or less
        /// keeps the caller's "check on every request" semantics while polling is enabled.
        /// </summary>
        public static long NextCheck(long nowMilliseconds, int intervalMilliseconds, int keyHash)
        {
            if (!PollingEnabled) return long.MaxValue;
            if (intervalMilliseconds <= 0) return nowMilliseconds;
            long spread = intervalMilliseconds >= 200 ? intervalMilliseconds / 4 : 0;
            long jitter = spread <= 0 ? 0 : (uint)keyHash % (spread + 1);
            return nowMilliseconds + intervalMilliseconds + jitter;
        }
    }
}
