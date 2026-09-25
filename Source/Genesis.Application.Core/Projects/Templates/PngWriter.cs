using System.Buffers.Binary;
using System.IO.Compression;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>
/// Writes a 32-bit RGBA buffer as a PNG, with no dependencies beyond the BCL.
/// </summary>
/// <remarks>
/// `Genesis.Application.Core` has zero package references by design — it is the leaf every other
/// project builds on. Pulling in System.Drawing.Common or SkiaSharp just so the project template can
/// emit four small images would trade that away for very little, so this writes the format directly:
/// signature, IHDR, one IDAT, IEND. `ZLibStream` supplies the compression and Adler-32; only CRC-32
/// has to be hand-rolled, because the BCL's lives in a NuGet package.
///
/// Deliberately minimal: 8-bit RGBA, no interlacing, no ancillary chunks. It is a template writer,
/// not a general encoder, and it should not grow into one.
/// </remarks>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Write <paramref name="rgba"/> (row-major, 4 bytes per pixel) to a PNG file.</summary>
    public static void Write(string path, byte[] rgba, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        int expected = width * height * 4;
        if (rgba.Length < expected)
        {
            throw new ArgumentException(
                $"Pixel buffer holds {rgba.Length} bytes; {width}x{height} RGBA needs {expected}.", nameof(rgba));
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR: 8-bit, colour type 6 (truecolour with alpha), no interlace.
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(file, "IHDR"u8, header);

        // Each scanline is prefixed with its filter type. Filter 0 (None) keeps this simple and
        // costs only size, which does not matter for template art.
        byte[] raw = new byte[height * ((width * 4) + 1)];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            int rawRow = y * (stride + 1);
            raw[rawRow] = 0;
            Array.Copy(rgba, y * stride, raw, rawRow + 1, stride);
        }

        using MemoryStream compressed = new();
        using (ZLibStream deflate = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        WriteChunk(file, "IDAT"u8, compressed.ToArray());
        WriteChunk(file, "IEND"u8, []);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);

        // The CRC covers the chunk type and its data, but not the length field.
        uint crc = Crc(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in type)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < 256; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
