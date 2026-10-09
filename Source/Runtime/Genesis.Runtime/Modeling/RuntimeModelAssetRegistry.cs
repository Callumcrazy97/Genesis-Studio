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

        // The debug screen's Resources tab lists the shared registry's models.
        static RuntimeModelAssetRegistry() =>
            Genesis.Shared.Diagnostics.DebugResourceCatalog.Register("models", Shared, () => Shared.Describe());

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

        /// <summary>
        /// Each loaded model, with the memory its vertices and indices take, for the debug screen's
        /// Resources tab. Reads nothing from disk.
        /// </summary>
        public List<Genesis.Shared.Diagnostics.DebugResourceRow> Describe()
        {
            lock (_gate)
            {
                var rows = new List<Genesis.Shared.Diagnostics.DebugResourceRow>(_cache.Count);
                foreach (KeyValuePair<string, Entry> entry in _cache)
                {
                    GModelAsset asset = entry.Value.Asset;
                    if (asset == null) continue;
                    long bytes = 0;
                    int triangles = 0;
                    foreach (GModelMesh mesh in asset.Meshes)
                    {
                        bytes += (long)(mesh.Vertices?.Length ?? 0) * System.Runtime.CompilerServices.Unsafe.SizeOf<Genesis.Shared.Interfaces.MeshVertex>();
                        bytes += (long)(mesh.SkinnedVertices?.Length ?? 0) * System.Runtime.CompilerServices.Unsafe.SizeOf<Genesis.Shared.Interfaces.SkinnedMeshVertex>();
                        bytes += (long)(mesh.Indices?.Length ?? 0) * sizeof(ushort);
                        triangles += (mesh.Indices?.Length ?? 0) / 3;
                    }

                    string name = string.IsNullOrWhiteSpace(asset.Name) ? Path.GetFileNameWithoutExtension(entry.Key) : asset.Name;
                    rows.Add(new Genesis.Shared.Diagnostics.DebugResourceRow(
                        "Model", name, 1, bytes, $"{asset.Meshes.Count} meshes · {triangles:N0} triangles · {entry.Key}"));
                }

                return rows;
            }
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

            GModelAsset asset;
            using (LoadClock.Measure(LoadWork.ModelRead))
            {
                asset = isStudioResource
                    ? StudioModelResourceLoader.Load(path)
                    : File.Exists(path) ? RuntimeModelStore.Load(path)
                    : GModelPrimitiveFactory.CreatePlaceholder(modelName, "Missing .gmodel asset. Reimport required.");
            }

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

        /// <summary>
        /// Reads a model into this registry on the calling (worker) thread, so the game's first
        /// <see cref="Load"/> of it finds it held instead of parsing it in a frame. The registry is
        /// not held while the file is parsed: the game thread asking for other models meanwhile is
        /// not kept waiting. Never imports (a model that needs importing is left to the game's own
        /// load). True when a model was read; false when it was held already, could not be read, or
        /// its saved geometry is larger than <paramref name="maximumBytes"/> (0: any size).
        /// </summary>
        public bool Warm(string projectPath, string modelName, long maximumBytes = 0) =>
            Warm(projectPath, modelName, maximumBytes, out _);

        /// <summary>The same, also giving the size of the saved geometry read (0 when nothing was read).</summary>
        public bool Warm(string projectPath, string modelName, long maximumBytes, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrWhiteSpace(modelName)) return false;
            string path, key;
            bool isStudioResource;
            lock (_gate)
            {
                string studioPath = StudioModelResourceLoader.Resolve(projectPath, modelName);
                isStudioResource = !string.IsNullOrWhiteSpace(studioPath);
                path = isStudioResource ? studioPath : RuntimeModelStore.AssetPath(projectPath, modelName);
                if (string.IsNullOrWhiteSpace(path)) return false;
                key = Path.GetFullPath(path);
                if (_cache.ContainsKey(key)) return false;
            }

            if (!File.Exists(path)) return false;
            var geometry = new FileInfo(isStudioResource ? StudioModelResourceLoader.CanonicalPath(path) : path);
            if (!geometry.Exists || (maximumBytes > 0 && geometry.Length > maximumBytes)) return false;
            if (isStudioResource)
            {
                // A model with its import source beside it may need importing again, which only
                // the game's own load may do: leave it to that load.
                string source = StudioModelResourceLoader.ResolveSource(path);
                if (!string.IsNullOrWhiteSpace(source) && File.Exists(source)) return false;
            }
            long stamp = isStudioResource ? StudioModelResourceLoader.Stamp(path) : File.GetLastWriteTimeUtc(path).Ticks;
            GModelAsset asset = isStudioResource ? StudioModelResourceLoader.LoadReadOnly(path) : RuntimeModelStore.Load(path);
            if (asset == null || asset.ImportRequired) return false;
            long now = Environment.TickCount64;
            lock (_gate)
            {
                // The game may have loaded it while this thread was reading.
                if (_cache.ContainsKey(key)) return false;
                _cache[key] = new Entry
                {
                    Asset = asset,
                    StampTicks = stamp,
                    NextFreshnessCheckMilliseconds = NextCheck(now, key),
                    Generation = RuntimeAssetPolicy.Generation,
                };
            }

            bytes = geometry.Length;
            return true;
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
