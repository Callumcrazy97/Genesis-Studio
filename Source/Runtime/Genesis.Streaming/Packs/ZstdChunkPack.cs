using System;
using System.IO;
using ZstdSharp;

namespace Genesis.Streaming.Packs
{
    /// <summary>
    /// Chunk/sector asset packs: raw payload → Zstd → disk/network → decompress on worker.
    /// Format: magic "GZPK" + version + codec(Zstd=2) + uncompressed size + zstd payload.
    /// </summary>
    public static class ZstdChunkPack
    {
        public const uint Magic = 0x4B505A47; // "GZPK"
        public const byte Version = 1;
        public const byte CodecZstd = 2;

        public static byte[] Pack(ReadOnlySpan<byte> raw)
        {
            using var compressor = new Compressor();
            byte[] compressed = compressor.Wrap(raw).ToArray();
            using var ms = new MemoryStream(13 + compressed.Length);
            using var bw = new BinaryWriter(ms);
            bw.Write(Magic);
            bw.Write(Version);
            bw.Write(CodecZstd);
            bw.Write(raw.Length);
            bw.Write(compressed);
            return ms.ToArray();
        }

        public static byte[] Unpack(ReadOnlySpan<byte> packed)
        {
            if (packed.Length < 13)
                throw new InvalidDataException("Chunk pack too short.");

            uint magic = BitConverter.ToUInt32(packed);
            if (magic != Magic)
                throw new InvalidDataException("Not a Genesis chunk pack.");
            byte ver = packed[4];
            byte codec = packed[5];
            int size = BitConverter.ToInt32(packed.Slice(6, 4));
            if (ver != Version)
                throw new InvalidDataException($"Unsupported pack version {ver}.");
            if (codec != CodecZstd)
                throw new InvalidDataException($"Unsupported codec {codec}.");
            if (size < 0 || size > 256 * 1024 * 1024)
                throw new InvalidDataException("Invalid uncompressed size.");

            ReadOnlySpan<byte> payload = packed.Slice(10);
            using var decompressor = new Decompressor();
            byte[] decompressed = decompressor.Unwrap(payload).ToArray();
            if (decompressed.Length != size)
                throw new InvalidDataException("Decompressed size mismatch.");
            return decompressed;
        }

        public static bool IdentityRoundTrip(ReadOnlySpan<byte> raw)
        {
            byte[] packed = Pack(raw);
            byte[] back = Unpack(packed);
            if (back.Length != raw.Length) return false;
            for (int i = 0; i < raw.Length; i++)
                if (back[i] != raw[i]) return false;
            return true;
        }

        public static void WriteFile(string path, ReadOnlySpan<byte> raw)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, Pack(raw));
        }

        public static byte[] ReadFile(string path) => Unpack(File.ReadAllBytes(path));
    }
}
