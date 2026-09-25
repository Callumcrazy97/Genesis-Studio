using System;
using System.Collections.Generic;
using System.IO;

namespace Genesis.Runtime.Modeling
{
    public sealed class RuntimeModelAssetRegistry
    {
        private sealed class Entry
        {
            public GModelAsset Asset;
            public long StampTicks;
        }

        private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

        public GModelAsset Load(string projectPath, string modelName)
        {
            string studioPath = StudioModelResourceLoader.Resolve(projectPath, modelName);
            bool isStudioResource = !string.IsNullOrWhiteSpace(studioPath);
            string path = isStudioResource ? studioPath : RuntimeModelStore.AssetPath(projectPath, modelName);
            string key = string.IsNullOrWhiteSpace(path) ? modelName ?? "" : Path.GetFullPath(path);
            long stamp = isStudioResource
                ? StudioModelResourceLoader.Stamp(path)
                : File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;

            if (_cache.TryGetValue(key, out Entry entry) && entry.StampTicks == stamp)
                return entry.Asset;

            GModelAsset asset = isStudioResource
                ? StudioModelResourceLoader.Load(path)
                : File.Exists(path) ? RuntimeModelStore.Load(path)
                : GModelPrimitiveFactory.CreatePlaceholder(modelName, "Missing .gmodel asset. Reimport required.");

            _cache[key] = new Entry { Asset = asset, StampTicks = stamp };
            return asset;
        }

        public void Invalidate(string projectPath, string modelName)
        {
            string studioPath = StudioModelResourceLoader.Resolve(projectPath, modelName);
            if (!string.IsNullOrWhiteSpace(studioPath))
                _cache.Remove(Path.GetFullPath(studioPath));
            string path = RuntimeModelStore.AssetPath(projectPath, modelName);
            if (!string.IsNullOrWhiteSpace(path))
                _cache.Remove(Path.GetFullPath(path));
        }

        public void Clear() => _cache.Clear();
    }
}
