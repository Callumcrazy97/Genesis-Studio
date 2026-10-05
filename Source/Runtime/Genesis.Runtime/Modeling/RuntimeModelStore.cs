using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Genesis.Runtime.Modeling
{
    public static class RuntimeModelStore
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            // One line per file: an indented model is several times the size on disk and slower to read.
            Formatting = Formatting.None,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
        };

        public static string ModelsDirectory(string projectPath)
            => Path.Combine(projectPath ?? "", "Models");

        public static string AssetPath(string projectPath, string modelName)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(modelName))
                return "";

            string name = modelName.Trim().Replace('/', Path.DirectorySeparatorChar);
            // Terrain sections own canonical models beside their .terrainpart.json files.
            // Resolve those existing private payloads inside the project before the legacy Models folder.
            if (name.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase))
            {
                string privatePath = global::Genesis.Shared.Assets.ResourceCatalog.ResolveFile(projectPath, modelName);
                if (privatePath.Length > 0) return privatePath;
            }
            if (Path.IsPathRooted(name))
                return Path.ChangeExtension(name, ".gmodel");

            if (!name.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase))
                name += ".gmodel";

            return Path.Combine(ModelsDirectory(projectPath), name);
        }

        public static string MetaPath(string projectPath, string modelName)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(modelName))
                return "";
            string name = modelName.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (name.EndsWith(".gmodel", StringComparison.OrdinalIgnoreCase))
                name = name[..^7];
            return Path.Combine(ModelsDirectory(projectPath), name + ".meta");
        }

        public static bool Exists(string projectPath, string modelName)
            => File.Exists(AssetPath(projectPath, modelName));

        public static GModelAsset CreateEmpty(string name, string message = "Reimport required")
        {
            var asset = new GModelAsset
            {
                Name = name ?? "",
                ImportRequired = true,
                ImportMessage = message ?? "",
            };
            asset.RecalculateBounds();
            return asset;
        }

        /// <summary>A model file being read ahead of the load that will ask for it.</summary>
        private sealed class Prefetched
        {
            public int State;                 // 0 waiting for a worker, 1 a worker has it, 2 the loader took it back
            public long Length, WriteTicks, QueuedMilliseconds, KeepMilliseconds;
            public Task<GModelAsset> Reading;
        }

        private const int PrefetchWaiting = 0, PrefetchReading = 1, PrefetchReclaimed = 2;
        private const long PrefetchMinimumBytes = 32 * 1024;
        public const long PrefetchKeepMilliseconds = 30_000;

        private static readonly ConcurrentDictionary<string, Prefetched> PrefetchedByPath = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim PrefetchSlots = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 8));
        private static int _prefetchesStarted, _prefetchesUsed;

        /// <summary>Set false to read every model on the thread that asks for it.</summary>
        public static bool PrefetchEnabled { get; set; } = true;

        /// <summary>Model files handed to worker threads since the process started.</summary>
        public static int PrefetchesStarted => _prefetchesStarted;

        /// <summary>Loads that were answered by a file a worker had already read, or was reading.</summary>
        public static int PrefetchesUsed => _prefetchesUsed;

        /// <summary>Model files a worker has been given and has not finished reading.</summary>
        public static int PrefetchesPending
        {
            get
            {
                int pending = 0;
                foreach (var pair in PrefetchedByPath)
                    if (!pair.Value.Reading.IsCompleted) pending++;
                return pending;
            }
        }

        /// <summary>
        /// Starts reading a model file on a worker thread. The next <see cref="Load"/> of the same
        /// file takes the result instead of reading it again, waiting for the worker if it has not
        /// finished. A room that names forty models reads several at once this way, while the
        /// thread that will need them is busy with something else, instead of one at a time on
        /// the first frame. A result nobody asks for within <paramref name="keepMilliseconds"/>
        /// (half a minute unless said otherwise) is dropped the next time something is read ahead.
        /// </summary>
        public static void Prefetch(string path, long keepMilliseconds = PrefetchKeepMilliseconds)
        {
            if (!PrefetchEnabled || string.IsNullOrWhiteSpace(path)) return;
            string full;
            long length, ticks;
            try
            {
                full = Path.GetFullPath(path);
                var file = new FileInfo(full);
                if (!file.Exists || file.Length < PrefetchMinimumBytes) return;
                length = file.Length;
                ticks = file.LastWriteTimeUtc.Ticks;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return;
            }

            long now = Environment.TickCount64;
            foreach (var pair in PrefetchedByPath)
            {
                if (now - pair.Value.QueuedMilliseconds > pair.Value.KeepMilliseconds && pair.Value.Reading.IsCompleted)
                    PrefetchedByPath.TryRemove(pair);
            }

            if (PrefetchedByPath.ContainsKey(full)) return;
            var entry = new Prefetched
            {
                Length = length, WriteTicks = ticks, QueuedMilliseconds = now,
                KeepMilliseconds = Math.Max(1000, keepMilliseconds),
            };
            entry.Reading = ReadWhenFreeAsync(entry, full);
            if (PrefetchedByPath.TryAdd(full, entry)) Interlocked.Increment(ref _prefetchesStarted);
        }

        /// <summary>
        /// Reads a file on a worker once one of the few reading slots is free. Waiting for a slot
        /// holds no thread: a room that names hundreds of models must not tie up the thread pool
        /// that terrain tiles, scatter cells and simplified meshes are also made on.
        /// </summary>
        private static async Task<GModelAsset> ReadWhenFreeAsync(Prefetched entry, string full)
        {
            await PrefetchSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                // The loader may have reached this file first and be reading it itself.
                if (Interlocked.CompareExchange(ref entry.State, PrefetchReading, PrefetchWaiting) != PrefetchWaiting)
                    return null;
                return await Task.Run(() =>
                {
                    using (Genesis.Shared.Diagnostics.LoadProfile.Begin("model file read ahead"))
                        return Read(full);
                }).ConfigureAwait(false);
            }
            finally
            {
                PrefetchSlots.Release();
            }
        }

        private static bool TryTakePrefetched(string path, out GModelAsset asset)
        {
            asset = null;
            if (PrefetchedByPath.IsEmpty) return false;
            string full = Path.GetFullPath(path);
            if (!PrefetchedByPath.TryRemove(full, out Prefetched entry)) return false;
            // No worker has started on it: reading it here is quicker than waiting for a free one.
            if (Interlocked.CompareExchange(ref entry.State, PrefetchReclaimed, PrefetchWaiting) == PrefetchWaiting) return false;
            using (Genesis.Shared.Diagnostics.LoadProfile.Begin("wait for a worker reading it ahead"))
                asset = entry.Reading.GetAwaiter().GetResult();
            if (asset == null) return false;
            var file = new FileInfo(full);
            if (!file.Exists || file.Length != entry.Length || file.LastWriteTimeUtc.Ticks != entry.WriteTicks)
            {
                // Saved again since the worker read it.
                asset = null;
                return false;
            }

            Interlocked.Increment(ref _prefetchesUsed);
            return true;
        }

        public static GModelAsset Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return CreateEmpty(Path.GetFileNameWithoutExtension(path), "Missing .gmodel asset");

            try
            {
                if (TryTakePrefetched(path, out GModelAsset ready)) return ready;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Fall through and read the file here.
            }

            return Read(path);
        }

        /// <summary>
        /// Writes a sealed cache for every large model under a project's Assets folder, several at
        /// a time. Used when a game is exported, so the player's first load reads caches instead
        /// of parsing every model. Returns how many were written.
        /// </summary>
        public static int WriteSealedCaches(string projectRoot, CancellationToken cancellation = default)
        {
            string assets = Path.Combine(projectRoot ?? "", "Assets");
            if (!ModelBinaryCache.Enabled || !Directory.Exists(assets)) return 0;
            int written = 0;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6),
                CancellationToken = cancellation,
            };
            Parallel.ForEach(Directory.EnumerateFiles(assets, "*.gmodel", SearchOption.AllDirectories), options, path =>
            {
                try
                {
                    if (new FileInfo(path).Length < ModelBinaryCache.MinimumSourceBytes) return;
                    byte[] bytes = File.ReadAllBytes(path);
                    // Skip a byte-order mark; the text itself is UTF-8 either way.
                    int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                    GModelAsset asset = JsonConvert.DeserializeObject<GModelAsset>(
                        System.Text.Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip), Settings);
                    if (asset == null) return;
                    Finish(asset, path);
                    if (ModelBinaryCache.SaveSealed(path, asset, bytes)) Interlocked.Increment(ref written);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                    or ArgumentException or NotSupportedException)
                {
                    // A model that cannot be cached is still loaded the ordinary way by the game.
                }
            });
            return written;
        }

        /// <summary>What every freshly parsed model is given before it is used or cached.</summary>
        private static void Finish(GModelAsset asset, string path)
        {
            if (string.IsNullOrWhiteSpace(asset.Name))
                asset.Name = Path.GetFileNameWithoutExtension(path);
            // Saved canonical assets already carry authoritative bounds. Re-scanning every
            // vertex after parsing made large environment models pay a second full geometry
            // traversal each time Model, Room, or Terrain Editor opened them.
            if (!HasUsableBounds(asset.Bounds)) asset.RecalculateBounds();
            GModelPrimitiveFactory.EnsureRigIntegrity(asset);
        }

        private static GModelAsset Read(string path)
        {
            try
            {
                // A cache made from this exact file reads back many times faster than the JSON.
                bool cached;
                GModelAsset asset;
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("model file: read its binary cache"))
                    cached = ModelBinaryCache.TryLoad(path, out asset);
                if (!cached)
                {
                    using (Genesis.Shared.Diagnostics.LoadProfile.Begin("model file: parse the JSON (no cache yet)"))
                        asset = JsonConvert.DeserializeObject<GModelAsset>(File.ReadAllText(path), Settings);
                }
                bool parsed = !cached && asset != null;
                asset ??= CreateEmpty(Path.GetFileNameWithoutExtension(path), "Invalid .gmodel asset");
                using (Genesis.Shared.Diagnostics.LoadProfile.Begin("model file: bounds and rig check"))
                    Finish(asset, path);
                if (parsed) ModelBinaryCache.SaveInBackground(path, asset);
                return asset;
            }
            catch (Exception ex)
            {
                return CreateEmpty(Path.GetFileNameWithoutExtension(path), "Failed to read .gmodel: " + ex.Message);
            }
        }

        public static GModelAsset LoadByName(string projectPath, string modelName)
            => Load(AssetPath(projectPath, modelName));

        public static void Save(string path, GModelAsset asset)
        {
            if (asset == null || string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            asset.ImportedUtc = asset.ImportedUtc == default ? DateTime.UtcNow : asset.ImportedUtc;
            asset.RecalculateBounds();
            // Clips borrowed from animation libraries stay in their libraries.
            System.Collections.Generic.List<GModelAnimationClip> all = asset.Animations;
            if (asset.LibraryClipNames is { Count: > 0 } borrowed && all != null)
                asset.Animations = all.FindAll(clip => !borrowed.Contains(clip?.Name ?? string.Empty));
            try { File.WriteAllText(path, JsonConvert.SerializeObject(asset, Settings)); }
            finally { asset.Animations = all; }
        }

        public static void SaveByName(string projectPath, string modelName, GModelAsset asset)
            => Save(AssetPath(projectPath, modelName), asset);

        private static bool HasUsableBounds(GModelBounds bounds)
        {
            if (bounds == null) return false;
            return IsFinite(bounds.Min) && IsFinite(bounds.Max)
                && bounds.Min.X <= bounds.Max.X
                && bounds.Min.Y <= bounds.Max.Y
                && bounds.Min.Z <= bounds.Max.Z;
        }

        private static bool IsFinite(System.Numerics.Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
