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

        private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string Project, string Model), string> _resolvedRequests = new();
        private readonly int _freshnessCheckIntervalMilliseconds;

        public RuntimeModelAssetRegistry(int freshnessCheckIntervalMilliseconds = 0)
        {
            _freshnessCheckIntervalMilliseconds = Math.Max(0, freshnessCheckIntervalMilliseconds);
        }

        public GModelAsset Load(string projectPath, string modelName)
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

        // Staggered so models loaded together do not all re-stamp in the same frame.
        private long NextCheck(long now, string key) =>
            RuntimeAssetPolicy.NextCheck(now, _freshnessCheckIntervalMilliseconds,
                StringComparer.OrdinalIgnoreCase.GetHashCode(key));

        public void Invalidate(string projectPath, string modelName)
        {
            _resolvedRequests.Remove((projectPath ?? string.Empty, modelName ?? string.Empty));
            string studioPath = StudioModelResourceLoader.Resolve(projectPath, modelName);
            if (!string.IsNullOrWhiteSpace(studioPath))
                _cache.Remove(Path.GetFullPath(studioPath));
            string path = RuntimeModelStore.AssetPath(projectPath, modelName);
            if (!string.IsNullOrWhiteSpace(path))
                _cache.Remove(Path.GetFullPath(path));
        }

        public void Clear()
        {
            _resolvedRequests.Clear();
            _cache.Clear();
        }
    }
}
