using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;

namespace Genesis.Rendering.Textures
{
    /// <summary>
    /// Backend-neutral cache for file-backed renderer textures.
    /// </summary>
    /// <remarks>
    /// The cache deliberately keys by both canonical path and colour-space intent. Source-image
    /// fallback stays compatible with Genesis's RGBA8 path, while cooked BC7 can select sRGB or
    /// linear GPU formats without changing the caller contract.
    ///
    /// Source, sidecar and resolved cooked-file stamps are retained with the handle. Editing the
    /// source, creating a cook, or replacing a cooked DDS therefore invalidates the stale upload.
    /// </remarks>
    public sealed class TextureAssetCache
    {
        private readonly struct Key : IEquatable<Key>
        {
            public Key(string path, TextureColorSpace colorSpace)
            {
                Path = path;
                ColorSpace = colorSpace;
            }

            public string Path { get; }
            public TextureColorSpace ColorSpace { get; }

            public bool Equals(Key other) =>
                ColorSpace == other.ColorSpace &&
                StringComparer.OrdinalIgnoreCase.Equals(Path, other.Path);

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode() =>
                HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path), (int)ColorSpace);
        }

        private readonly struct AssetStamp : IEquatable<AssetStamp>
        {
            public AssetStamp(
                long sourceLength,
                long sourceLastWriteUtcTicks,
                long manifestLength,
                long manifestLastWriteUtcTicks,
                string cookedPath,
                long cookedLength,
                long cookedLastWriteUtcTicks)
            {
                SourceLength = sourceLength;
                SourceLastWriteUtcTicks = sourceLastWriteUtcTicks;
                ManifestLength = manifestLength;
                ManifestLastWriteUtcTicks = manifestLastWriteUtcTicks;
                CookedPath = cookedPath ?? string.Empty;
                CookedLength = cookedLength;
                CookedLastWriteUtcTicks = cookedLastWriteUtcTicks;
            }

            public long SourceLength { get; }
            public long SourceLastWriteUtcTicks { get; }
            public long ManifestLength { get; }
            public long ManifestLastWriteUtcTicks { get; }
            public string CookedPath { get; }
            public long CookedLength { get; }
            public long CookedLastWriteUtcTicks { get; }

            public bool Equals(AssetStamp other) =>
                SourceLength == other.SourceLength &&
                SourceLastWriteUtcTicks == other.SourceLastWriteUtcTicks &&
                ManifestLength == other.ManifestLength &&
                ManifestLastWriteUtcTicks == other.ManifestLastWriteUtcTicks &&
                CookedLength == other.CookedLength &&
                CookedLastWriteUtcTicks == other.CookedLastWriteUtcTicks &&
                StringComparer.OrdinalIgnoreCase.Equals(CookedPath, other.CookedPath);

            public override bool Equals(object obj) => obj is AssetStamp other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(
                SourceLength, SourceLastWriteUtcTicks, ManifestLength, ManifestLastWriteUtcTicks,
                StringComparer.OrdinalIgnoreCase.GetHashCode(CookedPath), CookedLength, CookedLastWriteUtcTicks);
        }

        private sealed class Entry
        {
            public TextureHandle Handle;
            public AssetStamp Stamp;
        }

        private readonly Dictionary<Key, Entry> _entries = new();
        private readonly object _gate = new();
        private int _loaded;
        private int _reused;
        private int _invalidated;

        /// <summary>File-backed textures uploaded because no valid cached entry existed.</summary>
        public int Loaded => Volatile.Read(ref _loaded);

        /// <summary>Requests answered by an unchanged cached entry.</summary>
        public int Reused => Volatile.Read(ref _reused);

        /// <summary>Entries discarded because source/cooked state changed or their handle was released.</summary>
        public int Invalidated => Volatile.Read(ref _invalidated);

        public int Count
        {
            get
            {
                lock (_gate) return _entries.Count;
            }
        }

        /// <summary>
        /// Returns one uploaded handle for a file/colour-space pair until its source/cook changes.
        /// </summary>
        /// <param name="loader">Uploads one uncached canonical file path.</param>
        /// <param name="releaseUncached">
        /// Releases a stale/racing handle without re-entering this cache. May be null when the
        /// caller owns lifetime elsewhere (useful for CPU-only tests).
        /// </param>
        public TextureHandle Load(
            string path,
            TextureColorSpace colorSpace,
            Func<string, TextureColorSpace, TextureHandle> loader,
            Action<TextureHandle> releaseUncached)
        {
            if (loader == null) throw new ArgumentNullException(nameof(loader));
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return TextureHandle.Invalid;

            string fullPath = Path.GetFullPath(path);
            AssetStamp stamp = CaptureStamp(fullPath);
            Key key = new(fullPath, colorSpace);
            TextureHandle stale = TextureHandle.Invalid;

            lock (_gate)
            {
                if (_entries.TryGetValue(key, out Entry existing))
                {
                    if (existing.Handle.IsValid && existing.Stamp.Equals(stamp))
                    {
                        Interlocked.Increment(ref _reused);
                        return existing.Handle;
                    }

                    stale = existing.Handle;
                    _entries.Remove(key);
                    Interlocked.Increment(ref _invalidated);
                }
            }

            if (stale.IsValid && releaseUncached != null)
                releaseUncached(stale);

            TextureHandle loaded = loader(fullPath, colorSpace);
            if (!loaded.IsValid) return TextureHandle.Invalid;

            // The source/cook may have been replaced while it was being decoded/uploaded. Record
            // the post-load state so the following request invalidates this upload if that happened.
            Entry candidate = new()
            {
                Handle = loaded,
                Stamp = CaptureStamp(fullPath),
            };

            TextureHandle winner = TextureHandle.Invalid;
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out Entry raced) && raced.Handle.IsValid &&
                    raced.Stamp.Equals(candidate.Stamp))
                {
                    winner = raced.Handle;
                    Interlocked.Increment(ref _reused);
                }
                else
                {
                    _entries[key] = candidate;
                    Interlocked.Increment(ref _loaded);
                }
            }

            if (winner.IsValid)
            {
                if (releaseUncached != null) releaseUncached(loaded);
                return winner;
            }

            return loaded;
        }

        private static AssetStamp CaptureStamp(string sourcePath)
        {
            FileInfo source = new(sourcePath);
            if (!source.Exists) return new AssetStamp(-1, 0, -1, 0, string.Empty, -1, 0);

            string manifestPath = CookedTextureManifestStore.GetManifestPath(sourcePath);
            FileInfo manifest = new(manifestPath);
            long manifestLength = manifest.Exists ? manifest.Length : -1;
            long manifestTicks = manifest.Exists ? manifest.LastWriteTimeUtc.Ticks : 0;

            string cookedPath = string.Empty;
            long cookedLength = -1;
            long cookedTicks = 0;
            if (CookedTextureManifestStore.TryResolve(sourcePath, out string resolved, out _))
            {
                cookedPath = Path.GetFullPath(resolved);
                FileInfo cooked = new(cookedPath);
                if (cooked.Exists)
                {
                    cookedLength = cooked.Length;
                    cookedTicks = cooked.LastWriteTimeUtc.Ticks;
                }
            }

            return new AssetStamp(
                source.Length, source.LastWriteTimeUtc.Ticks,
                manifestLength, manifestTicks,
                cookedPath, cookedLength, cookedTicks);
        }

        /// <summary>Forget every cache key that points at a public renderer handle.</summary>
        public void Remove(TextureHandle handle)
        {
            if (!handle.IsValid) return;
            lock (_gate)
            {
                List<Key> remove = null;
                foreach (KeyValuePair<Key, Entry> pair in _entries)
                {
                    if (pair.Value.Handle.Id != handle.Id) continue;
                    remove ??= new List<Key>();
                    remove.Add(pair.Key);
                }

                if (remove == null) return;
                for (int i = 0; i < remove.Count; i++) _entries.Remove(remove[i]);
                Interlocked.Add(ref _invalidated, remove.Count);
            }
        }

        public void Clear()
        {
            lock (_gate) _entries.Clear();
        }
    }
}
