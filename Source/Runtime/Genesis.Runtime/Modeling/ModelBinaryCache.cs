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
        private const int FormatVersion = 3;
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

        private static int _hits, _writes, _sealedHits;

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

        /// <summary>Writes an array of plain values as one base64 string of its bytes.</summary>
        private sealed class RawArrayConverter<T> : JsonConverter<T[]> where T : unmanaged
        {
            public override void WriteJson(JsonWriter writer, T[] value, JsonSerializer serializer)
            {
                if (value == null) { writer.WriteNull(); return; }
                writer.WriteValue(Convert.ToBase64String(MemoryMarshal.AsBytes(value.AsSpan())));
            }

            public override T[] ReadJson(JsonReader reader, Type objectType, T[] existingValue, bool hasExistingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return Array.Empty<T>();
                if (reader.TokenType != JsonToken.String)
                    throw new JsonSerializationException("A model cache array is not stored as bytes.");
                byte[] bytes = Convert.FromBase64String((string)reader.Value ?? "");
                int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
                if (bytes.Length % size != 0)
                    throw new JsonSerializationException("A model cache array has a partial element.");
                var values = new T[bytes.Length / size];
                bytes.AsSpan().CopyTo(MemoryMarshal.AsBytes(values.AsSpan()));
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

                byte[] bytes = File.ReadAllBytes(cachePath);
                if (bytes.Length <= HeaderBytes) return false;
                ReadOnlySpan<byte> header = bytes.AsSpan(0, HeaderBytes);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
                    || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != FormatVersion
                    || BinaryPrimitives.ReadInt64LittleEndian(header[8..]) != source.Length
                    || BinaryPrimitives.ReadInt64LittleEndian(header[24..]) != bytes.Length - HeaderBytes)
                    return false;

                bool viaContent = false;
                if (BinaryPrimitives.ReadInt64LittleEndian(header[16..]) != source.LastWriteTimeUtc.Ticks)
                {
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

                using var stream = new MemoryStream(bytes, HeaderBytes, bytes.Length - HeaderBytes, writable: false);
                using var text = new StreamReader(stream, Encoding.UTF8, false, 1 << 16);
                using var reader = new JsonTextReader(text) { MaxDepth = 256 };
                asset = JsonSerializer.Create(Settings).Deserialize<GModelAsset>(reader);
                if (asset == null) return false;
                System.Threading.Interlocked.Increment(ref _hits);
                if (viaContent) System.Threading.Interlocked.Increment(ref _sealedHits);
                return true;
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

        private static byte[] Serialize(GModelAsset asset, long sourceLength, long sourceWriteTicks, byte[] sourceHash = null)
        {
            using var memory = new MemoryStream(1 << 20);
            memory.Write(new byte[HeaderBytes]);
            using (var text = new StreamWriter(memory, new UTF8Encoding(false), 1 << 16, leaveOpen: true))
            using (var writer = new JsonTextWriter(text))
                JsonSerializer.Create(Settings).Serialize(writer, asset);
            byte[] bytes = memory.ToArray();
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
