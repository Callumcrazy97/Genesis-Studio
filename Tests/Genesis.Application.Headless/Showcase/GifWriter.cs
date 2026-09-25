using System.Drawing;
using System.Drawing.Imaging;

namespace Genesis.Application.Headless.Showcase;

/// <summary>
/// Minimal animated GIF89a encoder: median-cut quantisation to one global palette, LZW compression,
/// and a Netscape looping extension.
/// </summary>
/// <remarks>
/// Written directly rather than taking a package, for the same reason <c>PngWriter</c> was
/// (NEXT-008): the showcase needs one format, once, and a dependency-free encoder keeps the build
/// self-contained. A single global palette across every frame is deliberate — a per-frame local
/// table would quantise the same UI colours slightly differently each frame and the result visibly
/// shimmers.
/// </remarks>
internal static class GifWriter
{
    /// <summary>Writes <paramref name="frames"/> as a looping GIF. Frames must all be the same size.</summary>
    /// <param name="frameDelayMs">Delay per frame. GIF stores hundredths of a second, so this rounds.</param>
    public static void Write(string outputFile, IReadOnlyList<Bitmap> frames, int frameDelayMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("A GIF needs at least one frame.", nameof(frames));
        }

        int width = frames[0].Width;
        int height = frames[0].Height;
        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i].Width != width || frames[i].Height != height)
            {
                throw new ArgumentException(
                    $"Frame {i} is {frames[i].Width}x{frames[i].Height} but frame 0 is {width}x{height}; "
                    + "every frame must match.", nameof(frames));
            }
        }

        // One pass to read pixels (GetPixel is far too slow at this volume), one to quantise.
        List<int[]> rgbFrames = frames.Select(f => ReadArgb(f)).ToList();
        int[] palette = BuildPalette(rgbFrames);
        var cache = new Dictionary<int, byte>(4096);
        List<byte[]> indexed = rgbFrames
            .Select(pixels => MapToPalette(pixels, palette, cache))
            .ToList();

        string? directory = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Explicit FileStream rather than File.Create: with System.Security.AccessControl in scope
        // the one-argument File.Create binds to the ACL extension overload instead.
        using FileStream stream = new(outputFile, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream);

        // ── Header + logical screen descriptor ──────────────────────────────────
        writer.Write("GIF89a".Select(c => (byte)c).ToArray());
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write((byte)0xF7);   // global table present, 8 bits/pixel, 256 entries
        writer.Write((byte)0);      // background colour index
        writer.Write((byte)0);      // default pixel aspect ratio

        foreach (int rgb in palette)
        {
            writer.Write((byte)((rgb >> 16) & 0xFF));
            writer.Write((byte)((rgb >> 8) & 0xFF));
            writer.Write((byte)(rgb & 0xFF));
        }

        // ── Netscape looping extension ──────────────────────────────────────────
        writer.Write((byte)0x21);
        writer.Write((byte)0xFF);
        writer.Write((byte)11);
        writer.Write("NETSCAPE2.0".Select(c => (byte)c).ToArray());
        writer.Write((byte)3);
        writer.Write((byte)1);
        writer.Write((ushort)0);    // 0 = loop forever
        writer.Write((byte)0);

        int delayHundredths = Math.Max(2, (int)Math.Round(frameDelayMs / 10.0));

        foreach (byte[] frame in indexed)
        {
            // Graphic control extension — disposal 1 (leave in place; every frame is full-size).
            writer.Write((byte)0x21);
            writer.Write((byte)0xF9);
            writer.Write((byte)4);
            writer.Write((byte)0x04);
            writer.Write((ushort)delayHundredths);
            writer.Write((byte)0);  // no transparent index
            writer.Write((byte)0);

            // Image descriptor — full frame, no local table, not interlaced.
            writer.Write((byte)0x2C);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)width);
            writer.Write((ushort)height);
            writer.Write((byte)0);

            WriteLzw(writer, frame, 8);
        }

        writer.Write((byte)0x3B);   // trailer
    }

    private static int[] ReadArgb(Bitmap bitmap)
    {
        var pixels = new int[bitmap.Width * bitmap.Height];
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // Drop alpha: the showcase composites opaque frames, and GIF has no alpha channel.
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] &= 0x00FFFFFF;
        }

        return pixels;
    }

    // ── Median-cut quantisation ─────────────────────────────────────────────────

    private sealed class ColorBox
    {
        public List<int> Colors = [];
        public int Range;
        public int Channel;

        public void Measure()
        {
            int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            foreach (int c in Colors)
            {
                int r = (c >> 16) & 0xFF, g = (c >> 8) & 0xFF, b = c & 0xFF;
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
            }

            int dr = maxR - minR, dg = maxG - minG, db = maxB - minB;
            // Weighted toward green, which the eye resolves best.
            int wr = dr * 30, wg = dg * 59, wb = db * 11;
            if (wg >= wr && wg >= wb) { Channel = 1; Range = wg; }
            else if (wr >= wb)        { Channel = 0; Range = wr; }
            else                      { Channel = 2; Range = wb; }
        }

        public int Average()
        {
            long r = 0, g = 0, b = 0;
            foreach (int c in Colors)
            {
                r += (c >> 16) & 0xFF;
                g += (c >> 8) & 0xFF;
                b += c & 0xFF;
            }

            int n = Math.Max(1, Colors.Count);
            return (int)((r / n) << 16 | (g / n) << 8 | (b / n));
        }
    }

    private static int[] BuildPalette(List<int[]> frames)
    {
        // Sample rather than read every pixel of every frame: a UI capture has far fewer distinct
        // colours than pixels, and sampling keeps quantisation off the critical path.
        var distinct = new HashSet<int>();
        foreach (int[] frame in frames)
        {
            int step = Math.Max(1, frame.Length / 60000);
            for (int i = 0; i < frame.Length; i += step)
            {
                distinct.Add(frame[i]);
            }
        }

        if (distinct.Count <= 256)
        {
            int[] exact = new int[256];
            int n = 0;
            foreach (int c in distinct) exact[n++] = c;
            for (; n < 256; n++) exact[n] = 0;
            return exact;
        }

        var boxes = new List<ColorBox> { new ColorBox { Colors = [.. distinct] } };
        boxes[0].Measure();

        while (boxes.Count < 256)
        {
            // Split the box spanning the widest weighted channel — the classic median cut.
            ColorBox? widest = null;
            foreach (ColorBox box in boxes)
            {
                if (box.Colors.Count > 1 && (widest is null || box.Range > widest.Range))
                {
                    widest = box;
                }
            }

            if (widest is null) break;

            int channel = widest.Channel;
            widest.Colors.Sort((a, b) => Shift(a, channel).CompareTo(Shift(b, channel)));
            int mid = widest.Colors.Count / 2;

            ColorBox lower = new() { Colors = widest.Colors.GetRange(0, mid) };
            ColorBox upper = new() { Colors = widest.Colors.GetRange(mid, widest.Colors.Count - mid) };
            boxes.Remove(widest);
            lower.Measure();
            upper.Measure();
            boxes.Add(lower);
            boxes.Add(upper);
        }

        int[] palette = new int[256];
        for (int i = 0; i < palette.Length; i++)
        {
            palette[i] = i < boxes.Count ? boxes[i].Average() : 0;
        }

        return palette;
    }

    private static int Shift(int color, int channel) => channel switch
    {
        0 => (color >> 16) & 0xFF,
        1 => (color >> 8) & 0xFF,
        _ => color & 0xFF,
    };

    private static byte[] MapToPalette(int[] pixels, int[] palette, Dictionary<int, byte> cache)
    {
        byte[] indexed = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            int color = pixels[i];
            if (!cache.TryGetValue(color, out byte index))
            {
                index = Nearest(color, palette);
                cache[color] = index;
            }

            indexed[i] = index;
        }

        return indexed;
    }

    private static byte Nearest(int color, int[] palette)
    {
        int r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF;
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            int p = palette[i];
            int dr = r - ((p >> 16) & 0xFF);
            int dg = g - ((p >> 8) & 0xFF);
            int db = b - (p & 0xFF);
            int distance = (dr * dr * 30) + (dg * dg * 59) + (db * db * 11);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
                if (distance == 0) break;
            }
        }

        return (byte)best;
    }

    // ── LZW ─────────────────────────────────────────────────────────────────────

    private static void WriteLzw(BinaryWriter writer, byte[] indexed, int minimumCodeSize)
    {
        writer.Write((byte)minimumCodeSize);

        int clearCode = 1 << minimumCodeSize;
        int endCode = clearCode + 1;
        int nextCode = endCode + 1;
        int codeSize = minimumCodeSize + 1;

        var dictionary = new Dictionary<(int Prefix, byte Suffix), int>();
        var block = new List<byte>(255);
        int bitBuffer = 0;
        int bitCount = 0;

        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += codeSize;
            while (bitCount >= 8)
            {
                block.Add((byte)(bitBuffer & 0xFF));
                bitBuffer >>= 8;
                bitCount -= 8;
                if (block.Count == 255)
                {
                    writer.Write((byte)255);
                    writer.Write(block.ToArray());
                    block.Clear();
                }
            }
        }

        Emit(clearCode);

        int prefix = indexed.Length > 0 ? indexed[0] : endCode;
        for (int i = 1; i < indexed.Length; i++)
        {
            byte suffix = indexed[i];
            if (dictionary.TryGetValue((prefix, suffix), out int combined))
            {
                prefix = combined;
                continue;
            }

            Emit(prefix);
            dictionary[(prefix, suffix)] = nextCode;
            nextCode++;

            if (nextCode > (1 << codeSize) && codeSize < 12)
            {
                codeSize++;
            }
            else if (nextCode >= 4096)
            {
                // Table full — reset, which every decoder understands.
                Emit(clearCode);
                dictionary.Clear();
                nextCode = endCode + 1;
                codeSize = minimumCodeSize + 1;
            }

            prefix = suffix;
        }

        Emit(prefix);
        Emit(endCode);

        // Flush the partial byte, then the final sub-block and its terminator.
        if (bitCount > 0)
        {
            block.Add((byte)(bitBuffer & 0xFF));
        }

        if (block.Count > 0)
        {
            writer.Write((byte)block.Count);
            writer.Write(block.ToArray());
        }

        writer.Write((byte)0);
    }
}
