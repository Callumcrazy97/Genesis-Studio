using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Modeling
{
    public sealed class RuntimeModelAssetRegistry
    {
        private sealed class Entry
        {
            public GModelAsset Asset;
            public long StampTicks;
            public long NextFreshnessCheckMilliseconds;
            public long Generation;
        }

        /// <summary>
        /// The registry a running game shares between drawing, collision fitting and terrain parts.
        /// A model file is read and parsed once, however many objects use it and however many rooms
        /// they appear in. Entries are re-checked against disk on the usual frame-path interval, and
        /// dropped by <see cref="RuntimeAssetPolicy.Invalidate"/> when an asset is saved.
        /// </summary>
        public static RuntimeModelAssetRegistry Shared { get; } =
            new(RuntimeAssetPolicy.DefaultFramePathIntervalMilliseconds);

        // Room loading and drawing normally share one thread; the lock keeps the cache whole if a
        // loader ever runs on another.
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Project, string Model), string> _resolvedRequests = new();
        private readonly int _freshnessCheckIntervalMilliseconds;

        public RuntimeModelAssetRegistry(int freshnessCheckIntervalMilliseconds = 0)
        {
            _freshnessCheckIntervalMilliseconds = Math.Max(0, freshnessCheckIntervalMilliseconds);
        }

        public GModelAsset Load(string projectPath, string modelName)
        {
            lock (_gate) return LoadCore(projectPath, modelName);
        }

        private GModelAsset LoadCore(string projectPath, string modelName)
        {
            long now = Environment.TickCount64;
            long generation = RuntimeAssetPolicy.Generation;
            // Request aliasing is safe whenever entries may be trusted between requests: a positive
            // interval, or an exported game where polling is disabled altogether.
            bool trustRequests = _freshnessCheckIntervalMilliseconds > 0 || !RuntimeAssetPolicy.PollingEnabled;
            var request = (projectPath ?? string.Empty, modelName ?? string.Empty);
            // Resolving a storage reference walks its parent directories. Repeating that work
            // before checking a fresh cache entry defeats the runtime's freshness interval.
            // This returns only an already loaded asset; fresh disk reads still use the resolver.
            if (trustRequests
                && _resolvedRequests.TryGetValue(request, out string previousKey)
                && _cache.TryGetValue(previousKey, out Entry fresh)
                && fresh.Generation == generation
                && now < fresh.NextFreshnessCheckMilliseconds)
            {
                return fresh.Asset;
            }
            string studioPath = StudioModelResourceLoader.Resolve(projectPath, modelName);
            bool isStudioResource = !string.IsNullOrWhiteSpace(studioPath);
            string path = isStudioResource ? studioPath : RuntimeModelStore.AssetPath(projectPath, modelName);
            string key = string.IsNullOrWhiteSpace(path) ? modelName ?? "" : Path.GetFullPath(path);
            if (trustRequests)
            {
                if (_resolvedRequests.Count >= 4096) _resolvedRequests.Clear();
                _resolvedRequests[request] = key;
            }
            if (_cache.TryGetValue(key, out Entry entry)
                && entry.Generation == generation
                && now < entry.NextFreshnessCheckMilliseconds)
            {
                return entry.Asset;
            }

            if (!isStudioResource) AssetIoCounters.Check(2);
            long stamp = isStudioResource
                ? StudioModelResourceLoader.Stamp(path)
                : File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;

            if (entry != null && entry.StampTicks == stamp)
            {
                entry.Generation = generation;
                entry.NextFreshnessCheckMilliseconds = NextCheck(now, key);
                return entry.Asset;
            }

            GModelAsset asset = isStudioResource
                ? StudioModelResourceLoader.Load(path)
                : File.Exists(path) ? RuntimeModelStore.Load(path)
                : GModelPrimitiveFactory.CreatePlaceholder(modelName, "Missing .gmodel asset. Reimport required.");

            _cache[key] = new Entry
            {
                Asset = asset,
                StampTicks = stamp,
                NextFreshnessCheckMilliseconds = NextCheck(now, key),
                Generation = generation,
            };
            return asset;
        }

        /// <summary>
        /// Starts reading a model on a worker thread if this registry does not hold it yet, so the
        /// <see cref="Load"/> that follows finds it read or being read. Safe to call for any name.
        /// </summary>
        public void Prefetch(string projectPath, string modelName, long keepMilliseconds = RuntimeModelStore.PrefetchKeepMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(modelName) || !RuntimeModelStore.PrefetchEnabled) return;
            try
            {
                lock (_gate)
                {
                    if (_resolvedRequests.TryGetValue((projectPath ?? string.Empty, modelName), out string known)
                        && _cache.ContainsKey(known))
                        return;
                    string studioPath = StudioModelResourceLoader.Resolve(projectPath, modelName);
                    if (!string.IsNullOrWhiteSpace(studioPath))
                    {
                        if (!_cache.ContainsKey(Path.GetFullPath(studioPath))) StudioModelResourceLoader.Prefetch(studioPath, keepMilliseconds);
                        return;
                    }

                    string path = RuntimeModelStore.AssetPath(projectPath, modelName);
                    if (!string.IsNullOrWhiteSpace(path) && !_cache.ContainsKey(Path.GetFullPath(path)))
                        RuntimeModelStore.Prefetch(path, keepMilliseconds);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Reading ahead is an optimisation; the load itself reports a model it cannot read.
            }
        }

        // Staggered so models loaded together do not all re-stamp in the same frame.
        private long NextCheck(long now, string key) =>
            RuntimeAssetPolicy.NextCheck(now, _freshnessCheckIntervalMilliseconds,
                StringComparer.OrdinalIgnoreCase.GetHashCode(key));

        public void Invalidate(string projectPath, string modelName)
        {
            lock (_gate)
            {
                _resolvedRequests.Remove((projectPath ?? string.Empty, modelName ?? string.Empty));
                string studioPath = StudioModelResourceLoader.Resolve(projectPath, modelName);
                if (!string.IsNullOrWhiteSpace(studioPath))
                    _cache.Remove(Path.GetFullPath(studioPath));
                string path = RuntimeModelStore.AssetPath(projectPath, modelName);
                if (!string.IsNullOrWhiteSpace(path))
                    _cache.Remove(Path.GetFullPath(path));
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _resolvedRequests.Clear();
                _cache.Clear();
            }
        }
    }
}
