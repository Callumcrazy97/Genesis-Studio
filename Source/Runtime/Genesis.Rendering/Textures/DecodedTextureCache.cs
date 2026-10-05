using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Genesis.Rendering.Textures
{
    /// <summary>What a <see cref="DecodedTextureCache"/> entry holds.</summary>
    public enum DecodedTextureKind
    {
        /// <summary>The picture's pixels as decoded, RGBA8, one level.</summary>
        Pixels = 0,
        /// <summary>A colour picture's RGBA8 mip chain, averaged in linear light.</summary>
        ColourMips = 1,
        /// <summary>A data picture's (normal, roughness) RGBA8 mip chain, averaged as stored.</summary>
        DataMips = 2,
    }

    /// <summary>
    /// Decoded pictures kept in a project's <c>.genesis/Cache/Textures</c> folder, beside the model
    /// caches, so a game that starts again does not decode every PNG and build its mip chain again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decoding a large PNG and building its mips takes tens of milliseconds of a worker's time,
    /// and a room with eighty pictures kept its loading screen up waiting for them on every run.
    /// An entry is the exact bytes the decoder and the mip builder produced, compressed with a
    /// fast deflate, so a cached picture is the same picture, pixel for pixel.
    /// </para>
    /// <para>
    /// The picture file stays the only authority. An entry records the size and modified time of
    /// the file it was made from and is ignored the moment either differs; deflate's own checksum
    /// catches a damaged entry. Deleting the folder costs one slow load. Only pictures inside a
    /// project (an <c>Assets</c> folder whose parent holds the project's <c>.genesis</c> folder or
    /// its <c>.genesisproj</c>) are cached, so the engine's own pictures never leave files beside
    /// the engine. GENESIS_TEXTURE_CACHE=0 turns it off.
    /// </para>
    /// </remarks>
    public static class DecodedTextureCache
    {
        private const uint Magic = 0x31435447;   // "GTC1"
        private const int FormatVersion = 1;
        // Magic, version, the source's length and modified time, kind, width, height, mip levels,
        // and the length of the payload once inflated.
        private const int HeaderBytes = 4 + 4 + 8 + 8 + 4 + 4 + 4 + 4 + 8;

        /// <summary>Pictures smaller than this decode quickly enough that an entry is not worth a file.</summary>
        public const long MinimumSourceBytes = 16 * 1024;

        /// <summary>Set false to read and write no entries. Also off when GENESIS_TEXTURE_CACHE=0.</summary>
        public static bool Enabled { get; set; } =
            !string.Equals(Environment.GetEnvironmentVariable("GENESIS_TEXTURE_CACHE"), "0", StringComparison.Ordinal);

        /// <summary>Entries read instead of decoding since the process started.</summary>
        public static int Hits => _hits;

        /// <summary>Entries written since the process started.</summary>
        public static int Writes => _writes;

        private static int _hits, _writes;

        // Whether a folder named Assets belongs to a project, asked once per folder.
        private static readonly ConcurrentDictionary<string, bool> ProjectAssets = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Where the entry for a picture lives, or null when the picture is not inside a project and
        /// so has nowhere to keep one.
        /// </summary>
        public static string PathFor(string imagePath, DecodedTextureKind kind)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            string full = Path.GetFullPath(imagePath);
            for (DirectoryInfo directory = new FileInfo(full).Directory; directory != null; directory = directory.Parent)
            {
                if (!directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase) || directory.Parent == null) continue;
                string project = directory.Parent.FullName;
                if (!ProjectAssets.GetOrAdd(project, IsProjectFolder)) return null;
                // Two pictures may share a file name in different folders; the path hash keeps them
                // apart. The path is taken from the project folder down, so a moved project keeps them.
                string relative = Path.GetRelativePath(project, full).Replace('\\', '/');
                byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(relative.ToUpperInvariant()));
                string name = Path.GetFileNameWithoutExtension(full);
                if (name.Length > 40) name = name[..40];
                return Path.Combine(project, ".genesis", "Cache", "Textures",
                    name + "-" + Convert.ToHexString(hash, 0, 6) + "-" + (int)kind + ".gtc");
            }

            return null;
        }

        private static bool IsProjectFolder(string folder)
        {
            try
            {
                return Directory.Exists(Path.Combine(folder, ".genesis"))
                    || Directory.EnumerateFiles(folder, "*.genesisproj").GetEnumerator().MoveNext();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Reads the entry for a picture if it was made from the file as it is now.</summary>
        public static bool TryLoad(string imagePath, DecodedTextureKind kind,
            out int width, out int height, out int mipLevels, out byte[] payload)
        {
            width = height = mipLevels = 0;
            payload = null;
            if (!Enabled) return false;
            try
            {
                string cachePath = PathFor(imagePath, kind);
                if (cachePath == null || !File.Exists(cachePath)) return false;
                var source = new FileInfo(imagePath);
                if (!source.Exists) return false;

                using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                Span<byte> header = stackalloc byte[HeaderBytes];
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
                    || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != FormatVersion
                    || BinaryPrimitives.ReadInt64LittleEndian(header[8..]) != source.Length
                    || BinaryPrimitives.ReadInt64LittleEndian(header[16..]) != source.LastWriteTimeUtc.Ticks
                    || BinaryPrimitives.ReadInt32LittleEndian(header[24..]) != (int)kind)
                    return false;
                int w = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
                int h = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
                int levels = BinaryPrimitives.ReadInt32LittleEndian(header[36..]);
                long length = BinaryPrimitives.ReadInt64LittleEndian(header[40..]);
                if (w <= 0 || h <= 0 || levels <= 0 || length <= 0 || length > int.MaxValue
                    || length < (long)w * h * 4) return false;

                byte[] bytes = new byte[length];
                using (var inflate = new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true))
                {
                    inflate.ReadExactly(bytes);
                    // Anything after the payload means the entry is not what its header says.
                    if (inflate.ReadByte() != -1) return false;
                }

                width = w;
                height = h;
                mipLevels = levels;
                payload = bytes;
                Interlocked.Increment(ref _hits);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                or NotSupportedException or ArgumentException or EndOfStreamException or OutOfMemoryException)
            {
                // A damaged or half-written entry is no reason to fail a load: decode the picture itself.
                width = height = mipLevels = 0;
                payload = null;
                return false;
            }
        }

        /// <summary>
        /// The size and modified time of a picture file, taken before it is decoded so that an
        /// entry is never stamped with a time the decoded pixels did not come from. Null when the
        /// picture is too small to be worth an entry, outside a project, or unreadable.
        /// </summary>
        public static (long Length, long Ticks)? StampForSaving(string imagePath)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(imagePath)) return null;
            try
            {
                var source = new FileInfo(imagePath);
                if (!source.Exists || source.Length < MinimumSourceBytes || PathFor(imagePath, DecodedTextureKind.Pixels) == null) return null;
                return (source.Length, source.LastWriteTimeUtc.Ticks);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// Writes the entry for a picture that has just been decoded, on a worker thread, stamped
        /// with what <see cref="StampForSaving"/> returned before decoding. The payload must not be
        /// changed afterwards; uploading it is fine. Failures are silent: the cache is an optimisation.
        /// </summary>
        public static void SaveInBackground(string imagePath, (long Length, long Ticks)? stamp, DecodedTextureKind kind,
            int width, int height, int mipLevels, byte[] payload)
        {
            if (!Enabled || stamp is not { } source || payload == null || payload.Length == 0 || width <= 0 || height <= 0) return;
            string cachePath;
            try
            {
                cachePath = PathFor(imagePath, kind);
                if (cachePath == null) return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return;
            }

            Task.Run(() => Write(cachePath, kind, source.Length, source.Ticks, width, height, mipLevels, payload));
        }

        /// <summary>Writes an entry now. Returns false when it could not be written.</summary>
        public static bool Save(string imagePath, DecodedTextureKind kind, int width, int height, int mipLevels, byte[] payload)
        {
            if (payload == null || payload.Length == 0 || width <= 0 || height <= 0) return false;
            try
            {
                string cachePath = PathFor(imagePath, kind);
                var source = new FileInfo(imagePath);
                if (cachePath == null || !source.Exists) return false;
                return Write(cachePath, kind, source.Length, source.LastWriteTimeUtc.Ticks, width, height, mipLevels, payload);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        private static bool Write(string cachePath, DecodedTextureKind kind, long sourceLength, long sourceTicks,
            int width, int height, int mipLevels, byte[] payload)
        {
            string temporary = cachePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    Span<byte> header = stackalloc byte[HeaderBytes];
                    BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
                    BinaryPrimitives.WriteInt32LittleEndian(header[4..], FormatVersion);
                    BinaryPrimitives.WriteInt64LittleEndian(header[8..], sourceLength);
                    BinaryPrimitives.WriteInt64LittleEndian(header[16..], sourceTicks);
                    BinaryPrimitives.WriteInt32LittleEndian(header[24..], (int)kind);
                    BinaryPrimitives.WriteInt32LittleEndian(header[28..], width);
                    BinaryPrimitives.WriteInt32LittleEndian(header[32..], height);
                    BinaryPrimitives.WriteInt32LittleEndian(header[36..], mipLevels);
                    BinaryPrimitives.WriteInt64LittleEndian(header[40..], payload.LongLength);
                    stream.Write(header);
                    using var deflate = new ZLibStream(stream, CompressionLevel.Fastest, leaveOpen: true);
                    deflate.Write(payload);
                }

                File.Move(temporary, cachePath, overwrite: true);
                Interlocked.Increment(ref _writes);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return false;
            }
        }
    }
}
