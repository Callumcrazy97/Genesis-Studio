using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json;

namespace Genesis.Runtime.Modeling
{
    /// <summary>
    /// A fast-loading copy of a parsed <c>.gmodel</c>, kept in the project's
    /// <c>.genesis/Cache/Models</c> folder: the project's own place for what can be made again,
    /// which exports and resource scans already leave alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>.gmodel</c> is JSON: every vertex, index and animation matrix is a run of decimal
    /// numbers, and a character with sixty thousand vertices and five hundred animation frames is
    /// thirty megabytes that take seconds to parse. The cache is the same document with its large
    /// arrays stored as raw bytes, which reads back in a fraction of the time.
    /// </para>
    /// <para>
    /// The <c>.gmodel</c> stays the only authority. A cache records the size and modified time of
    /// the file it was made from and is ignored the moment either differs, so editing, reimporting
    /// or replacing a model can never show stale geometry. Deleting the folder costs one slow load.
    /// </para>
    /// </remarks>
    public static class ModelBinaryCache
    {
        private const uint Magic = 0x31434D47;   // "GMC1"
        // 4: each large array's bytes follow the text, which holds where they are; in 3 they were base64
        // text, so a load made a string of 2.7 times the array, then its bytes, then the array (Kinfield).
        private const int FormatVersion = 4;
        private const int HashBytes = 32;
        // Magic, version, the source's length and modified time, the payload's length and its hash,
        // flags, and (for a sealed cache) the hash of the source file.
        private const int HeaderBytes = 4 + 4 + 8 + 8 + 8 + HashBytes + 4 + HashBytes;
        private const int FlagsOffset = 32 + HashBytes, SourceHashOffset = FlagsOffset + 4;
        private const int SealedFlag = 1;

        /// <summary>Models smaller than this parse quickly enough that a cache is not worth a file.</summary>
        public const long MinimumSourceBytes = 256 * 1024;

        /// <summary>Set false to read and write no caches. Also off when GENESIS_MODEL_CACHE=0.</summary>
        public static bool Enabled { get; set; } =
            !string.Equals(Environment.GetEnvironmentVariable("GENESIS_MODEL_CACHE"), "0", StringComparison.Ordinal);

        /// <summary>Caches read instead of parsing JSON since the process started.</summary>
        public static int Hits => _hits;

        /// <summary>Caches written since the process started.</summary>
        public static int Writes => _writes;

        /// <summary>Sealed caches accepted by comparing the model file's content, its date having changed.</summary>
        public static int SealedHits => _sealedHits;

        /// <summary>
        /// <see cref="Hits"/> made by loads on the calling thread only. Read-ahead workers and other
        /// loaders count in <see cref="Hits"/> too, so a check of one load counts on its own thread.
        /// </summary>
        public static int HitsOnThisThread => _threadHits;

        /// <summary><see cref="SealedHits"/> made by loads on the calling thread only.</summary>
        public static int SealedHitsOnThisThread => _threadSealedHits;

        private static int _hits, _writes, _sealedHits;
        [ThreadStatic] private static int _threadHits, _threadSealedHits;

        private static readonly JsonSerializerSettings Settings = CreateSettings();

        private static JsonSerializerSettings CreateSettings()
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.None,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
            };
            settings.Converters.Add(new RawArrayConverter<MeshVertex>());
            settings.Converters.Add(new RawArrayConverter<SkinnedMeshVertex>());
            settings.Converters.Add(new RawArrayConverter<Matrix4x4>());
            settings.Converters.Add(new RawArrayConverter<Vector4>());
            settings.Converters.Add(new RawArrayConverter<Vector3>());
            settings.Converters.Add(new RawArrayConverter<Vector2>());
            settings.Converters.Add(new RawArrayConverter<ushort>());
            settings.Converters.Add(new RawArrayConverter<int>());
            settings.Converters.Add(new RawArrayConverter<float>());
            return settings;
        }

        /// <summary>The bytes after a cache's text: where arrays are written to, or read from.</summary>
        private sealed class ArrayBytes
        {
            public MemoryStream Output;
            public byte[] Input;
            public int Start;
        }

        // The arrays of the cache being written or read on this thread (Json.NET runs converters on
        // the caller's thread).
        [ThreadStatic] private static ArrayBytes _arrays;

        /// <summary>
        /// Writes an array of plain values as its raw bytes after the text (their length first),
        /// leaving in the text only where they start.
        /// </summary>
        private sealed class RawArrayConverter<T> : JsonConverter<T[]> where T : unmanaged
        {
            public override void WriteJson(JsonWriter writer, T[] value, JsonSerializer serializer)
            {
                if (value == null) { writer.WriteNull(); return; }
                ArrayBytes arrays = _arrays ?? throw new JsonSerializationException("A model cache array was written outside a cache.");
                long offset = arrays.Output.Length;
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(value.AsSpan());
                Span<byte> length = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
                arrays.Output.Write(length);
                arrays.Output.Write(bytes);
                writer.WriteValue(offset);
            }

            public override T[] ReadJson(JsonReader reader, Type objectType, T[] existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return Array.Empty<T>();
                if (reader.TokenType != JsonToken.Integer || _arrays is not { } arrays)
                    throw new JsonSerializationException("A model cache array is not stored as bytes.");
                long offset = Convert.ToInt64(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
                long at = arrays.Start + offset;
                if (offset < 0 || at + 8 > arrays.Input.Length)
                    throw new JsonSerializationException("A model cache array lies outside the file.");
                long byteLength = BinaryPrimitives.ReadInt64LittleEndian(arrays.Input.AsSpan((int)at, 8));
                int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
                if (byteLength < 0 || byteLength % size != 0 || at + 8 + byteLength > arrays.Input.Length)
                    throw new JsonSerializationException("A model cache array has a partial element.");
                var values = new T[byteLength / size];
                arrays.Input.AsSpan((int)at + 8, (int)byteLength).CopyTo(MemoryMarshal.AsBytes(values.AsSpan()));
                return values;
            }
        }

        /// <summary>
        /// Where the cache for a model file lives, or null when the file is not inside a project
        /// (no <c>Assets</c> folder above it) and so has nowhere to keep one.
        /// </summary>
        public static string PathFor(string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath)) return null;
            string full = Path.GetFullPath(modelPath);
            for (DirectoryInfo directory = new FileInfo(full).Directory; directory != null; directory = directory.Parent)
            {
                if (!directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase) || directory.Parent == null) continue;
                // Two models may share a file name in different folders; the path hash keeps them apart.
                // The path is taken from the project folder down, so a project that is moved or
                // renamed keeps its caches.
                string relative = Path.GetRelativePath(directory.Parent.FullName, full).Replace('\\', '/');
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(relative.ToUpperInvariant()));
                string name = Path.GetFileNameWithoutExtension(full);
                if (name.Length > 40) name = name[..40];
                return Path.Combine(directory.Parent.FullName, ".genesis", "Cache", "Models",
                    name + "-" + Convert.ToHexString(hash, 0, 6) + ".gmc");
            }

            return null;
        }

        /// <summary>Reads the cache for a model file if it was made from the file as it is now.</summary>
        public static bool TryLoad(string modelPath, out GModelAsset asset)
        {
            asset = null;
            if (!Enabled) return false;
            try
            {
                string cachePath = PathFor(modelPath);
                if (cachePath == null || !File.Exists(cachePath)) return false;
                var source = new FileInfo(modelPath);
                if (!source.Exists) return false;
                return TryRead(cachePath, modelPath, source.Length, source.LastWriteTimeUtc.Ticks, sealable: true, out asset);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or FormatException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                // A damaged or half-written cache is no reason to fail a load: parse the model itself.
                asset = null;
                return false;
            }
        }

        /// <summary>
        /// Reads a cache made from a source of <paramref name="length"/> bytes stamped
        /// <paramref name="ticks"/>; a sealable one is also taken when only the date differs and the
        /// content of <paramref name="modelPath"/> is still what it was made from.
        /// </summary>
        private static bool TryRead(string cachePath, string modelPath, long length, long ticks, bool sealable, out GModelAsset asset)
        {
            asset = null;
            if (!File.Exists(cachePath)) return false;
            byte[] bytes = File.ReadAllBytes(cachePath);
            if (bytes.Length <= HeaderBytes) return false;
            ReadOnlySpan<byte> header = bytes.AsSpan(0, HeaderBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
                || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != FormatVersion
                || BinaryPrimitives.ReadInt64LittleEndian(header[8..]) != length
                || BinaryPrimitives.ReadInt64LittleEndian(header[24..]) != bytes.Length - HeaderBytes)
                return false;

            bool viaContent = false;
            if (BinaryPrimitives.ReadInt64LittleEndian(header[16..]) != ticks)
            {
                if (!sealable) return false;
                // An exported game's files lose their exact dates in an archive, so its caches
                // are sealed with the hash of the model they were made from. Checking it means
                // reading the model file, which is still far quicker than parsing it. A cache
                // that is not sealed belongs to a file that has since been saved again.
                if ((BinaryPrimitives.ReadInt32LittleEndian(header[FlagsOffset..]) & SealedFlag) == 0) return false;
                Span<byte> sourceHash = stackalloc byte[HashBytes];
                using (FileStream model = new(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                    SHA256.HashData(model, sourceHash);
                if (!sourceHash.SequenceEqual(header.Slice(SourceHashOffset, HashBytes))) return false;
                viaContent = true;
            }

            // A damaged cache can still be well-formed text that reads back as a different
            // model, so the content is checked against the hash taken when it was written.
            Span<byte> hash = stackalloc byte[HashBytes];
            SHA256.HashData(bytes.AsSpan(HeaderBytes), hash);
            if (!hash.SequenceEqual(header.Slice(32, HashBytes))) return false;

            // The text, then the arrays' bytes.
            int textLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(HeaderBytes, 4));
            if (textLength <= 0 || (long)HeaderBytes + 4 + textLength > bytes.Length) return false;
            using var stream = new MemoryStream(bytes, HeaderBytes + 4, textLength, writable: false);
            using var text = new StreamReader(stream, Encoding.UTF8, false, 1 << 16);
            using var reader = new JsonTextReader(text) { MaxDepth = 256 };
            _arrays = new ArrayBytes { Input = bytes, Start = HeaderBytes + 4 + textLength };
            try { asset = JsonSerializer.Create(Settings).Deserialize<GModelAsset>(reader); }
            finally { _arrays = null; }
            if (asset == null) return false;
            System.Threading.Interlocked.Increment(ref _hits);
            _threadHits++;
            if (viaContent)
            {
                System.Threading.Interlocked.Increment(ref _sealedHits);
                _threadSealedHits++;
            }
            return true;
        }

        /// <summary>
        /// Whether the cache for a model file was made from the file as it is now (its size and
        /// date), reading only the cache's header. A cache is written on a worker after a slow load,
        /// so this says when that write for this file has finished.
        /// </summary>
        public static bool IsCurrent(string modelPath)
        {
            try
            {
                string cachePath = PathFor(modelPath);
                var source = new FileInfo(modelPath);
                if (cachePath == null || !source.Exists || !File.Exists(cachePath)) return false;
                Span<byte> header = stackalloc byte[HeaderBytes];
                using var cache = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (cache.Length <= HeaderBytes) return false;
                cache.ReadExactly(header);
                return BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic
                    && BinaryPrimitives.ReadInt32LittleEndian(header[4..]) == FormatVersion
                    && BinaryPrimitives.ReadInt64LittleEndian(header[8..]) == source.Length
                    && BinaryPrimitives.ReadInt64LittleEndian(header[16..]) == source.LastWriteTimeUtc.Ticks
                    && BinaryPrimitives.ReadInt64LittleEndian(header[24..]) == cache.Length - HeaderBytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the cache of a model a game imported from its source file (a <c>.glb</c>, say)
        /// because the project holds no cooked copy and a game never writes one into the project.
        /// Valid while the source file and <paramref name="settings"/> (the descriptor's date, the
        /// import options, the engine's build) are as they were. Without it the game imported the
        /// source again at every launch (Kinfield: an 88 MB village and twenty more, over a second
        /// before the first frame).
        /// </summary>
        public static bool TryLoadImport(string descriptorPath, string sourcePath, long settings, out GModelAsset asset)
        {
            asset = null;
            if (!Enabled) return false;
            try
            {
                string cachePath = ImportPathFor(descriptorPath);
                var source = new FileInfo(sourcePath);
                if (cachePath == null || !source.Exists) return false;
                return TryRead(cachePath, sourcePath, source.Length, ImportStamp(source, settings), sealable: false, out asset);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or FormatException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                asset = null;
                return false;
            }
        }

        /// <summary>Keeps a model just imported by a game for <see cref="TryLoadImport"/>; written on a worker.</summary>
        public static void SaveImportInBackground(string descriptorPath, string sourcePath, long settings, GModelAsset asset)
        {
            if (!Enabled || asset == null) return;
            string cachePath;
            long length, ticks;
            try
            {
                cachePath = ImportPathFor(descriptorPath);
                var source = new FileInfo(sourcePath);
                if (cachePath == null || !source.Exists || source.Length < MinimumSourceBytes) return;
                length = source.Length;
                ticks = ImportStamp(source, settings);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return;
            }

            byte[] payload;
            try
            {
                payload = Serialize(asset, length, ticks);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                return;
            }

            Task.Run(() => Write(cachePath, payload));
        }

        private static string ImportPathFor(string descriptorPath) =>
            PathFor(descriptorPath) is { } path ? path[..^".gmc".Length] + "-import.gmc" : null;

        private static long ImportStamp(FileInfo source, long settings) =>
            unchecked(source.LastWriteTimeUtc.Ticks ^ (settings * (long)0x9E3779B97F4A7C15));

        /// <summary>
        /// Writes the cache for a model that has just been parsed. The model is copied into the
        /// cache's form here, before the caller can change it, which costs a few percent of the
        /// parse that just happened; the file is written on a worker thread. Failures are silent:
        /// the cache is an optimisation.
        /// </summary>
        public static void SaveInBackground(string modelPath, GModelAsset asset)
        {
            if (!Enabled || asset == null) return;
            string cachePath;
            long length, ticks;
            try
            {
                cachePath = PathFor(modelPath);
                if (cachePath == null) return;
                var source = new FileInfo(modelPath);
                if (!source.Exists || source.Length < MinimumSourceBytes) return;
                length = source.Length;
                ticks = source.LastWriteTimeUtc.Ticks;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return;
            }

            byte[] payload;
            try
            {
                payload = Serialize(asset, length, ticks);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                return;
            }

            Task.Run(() => Write(cachePath, payload));
        }

        /// <summary>
        /// Makes the cache of a model that is about to be saved, on whatever thread the caller is on
        /// (the Model editor's import makes it on its worker, with the model's text).
        /// <see cref="SavePrepared"/> writes it once the model file is saved, so the first load after
        /// an import reads the cache instead of parsing the JSON. Null when caches are off or the
        /// model cannot be written; clips borrowed from libraries are left out, as the model file does.
        /// </summary>
        public static byte[] Prepare(GModelAsset asset)
        {
            if (!Enabled || asset == null) return null;
            System.Collections.Generic.List<GModelAnimationClip> all = asset.Animations;
            if (asset.LibraryClipNames is { Count: > 0 } borrowed && all != null)
                asset.Animations = all.FindAll(clip => !borrowed.Contains(clip?.Name ?? string.Empty));
            try { return Serialize(asset, 0, 0); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                return null;
            }
            finally { asset.Animations = all; }
        }

        /// <summary>
        /// Writes a cache made by <see cref="Prepare"/> for the model file just saved at
        /// <paramref name="modelPath"/>, stamped with that file's size and time. False when it was not
        /// written (caches off, a small model, a failed write): the model then loads from its JSON.
        /// </summary>
        public static bool SavePrepared(string modelPath, byte[] prepared)
        {
            if (!Enabled || prepared == null || prepared.Length < HeaderBytes) return false;
            try
            {
                string cachePath = PathFor(modelPath);
                if (cachePath == null) return false;
                var source = new FileInfo(modelPath);
                if (!source.Exists || source.Length < MinimumSourceBytes) return false;
                BinaryPrimitives.WriteInt64LittleEndian(prepared.AsSpan(8), source.Length);
                BinaryPrimitives.WriteInt64LittleEndian(prepared.AsSpan(16), source.LastWriteTimeUtc.Ticks);
                return Write(cachePath, prepared);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        private static byte[] Serialize(GModelAsset asset, long sourceLength, long sourceWriteTicks, byte[] sourceHash = null)
        {
            using var memory = new MemoryStream(1 << 20);
            memory.Write(new byte[HeaderBytes + 4]);
            var arrays = new ArrayBytes { Output = new MemoryStream(1 << 20) };
            _arrays = arrays;
            try
            {
                using var text = new StreamWriter(memory, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
                using var writer = new JsonTextWriter(text);
                JsonSerializer.Create(Settings).Serialize(writer, asset);
            }
            finally { _arrays = null; }
            int textLength = checked((int)(memory.Length - HeaderBytes - 4));
            arrays.Output.Position = 0;
            arrays.Output.CopyTo(memory);
            byte[] bytes = memory.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HeaderBytes, 4), textLength);
            Span<byte> header = bytes.AsSpan(0, HeaderBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], FormatVersion);
            BinaryPrimitives.WriteInt64LittleEndian(header[8..], sourceLength);
            BinaryPrimitives.WriteInt64LittleEndian(header[16..], sourceWriteTicks);
            BinaryPrimitives.WriteInt64LittleEndian(header[24..], bytes.Length - HeaderBytes);
            SHA256.HashData(bytes.AsSpan(HeaderBytes), header.Slice(32, HashBytes));
            if (sourceHash is { Length: HashBytes })
            {
                BinaryPrimitives.WriteInt32LittleEndian(header[FlagsOffset..], SealedFlag);
                sourceHash.CopyTo(header.Slice(SourceHashOffset, HashBytes));
            }

            return bytes;
        }

        private static bool Write(string cachePath, byte[] payload)
        {
            string temporary = cachePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                File.WriteAllBytes(temporary, payload);
                File.Move(temporary, cachePath, overwrite: true);
                System.Threading.Interlocked.Increment(ref _writes);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return false;
            }
        }

        /// <summary>
        /// Writes a cache that stays valid when the model file's date changes but its content does
        /// not: for an exported game, whose files are copied, archived and unpacked on the way to
        /// the player. Returns false when it could not be written.
        /// </summary>
        public static bool SaveSealed(string modelPath, GModelAsset asset, byte[] modelFileBytes)
        {
            if (asset == null || modelFileBytes == null) return false;
            try
            {
                string cachePath = PathFor(modelPath);
                if (cachePath == null) return false;
                var source = new FileInfo(modelPath);
                if (!source.Exists || source.Length != modelFileBytes.Length) return false;
                return Write(cachePath, Serialize(asset, source.Length, source.LastWriteTimeUtc.Ticks, SHA256.HashData(modelFileBytes)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Writes a cache now. Returns false when it could not be written.</summary>
        public static bool Save(string cachePath, GModelAsset asset, long sourceLength, long sourceWriteTicks)
        {
            try
            {
                return Write(cachePath, Serialize(asset, sourceLength, sourceWriteTicks));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                return false;
            }
        }
    }
}
