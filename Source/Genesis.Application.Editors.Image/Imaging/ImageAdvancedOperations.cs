using System.Numerics;

namespace Genesis.Application.Editors.Image.Imaging;

public static class ImageAdvancedOperations
{
    public static byte[] AdjustHsv(
        ReadOnlySpan<byte> source,
        float hueDegrees,
        float saturationScale,
        float valueScale,
        CancellationToken cancellationToken = default)
    {
        byte[] result = source.ToArray();
        for (int i = 0; i < result.Length; i += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ColorToHsv(result[i], result[i + 1], result[i + 2], out float h, out float s, out float v);
            h = (h + hueDegrees) % 360f;
            if (h < 0f) h += 360f;
            s = Math.Clamp(s * saturationScale, 0f, 1f);
            v = Math.Clamp(v * valueScale, 0f, 1f);
            HsvToColor(h, s, v, out result[i], out result[i + 1], out result[i + 2]);
        }
        return result;
    }

    public static byte[] Levels(
        ReadOnlySpan<byte> source,
        int inputBlack,
        int inputWhite,
        float gamma,
        int outputBlack,
        int outputWhite,
        CancellationToken cancellationToken = default)
    {
        byte[] result = source.ToArray();
        float range = Math.Max(1, inputWhite - inputBlack);
        float inverseGamma = 1f / Math.Max(0.01f, gamma);
        for (int i = 0; i < result.Length; i += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int c = 0; c < 3; c++)
            {
                float normalized = Math.Clamp((result[i + c] - inputBlack) / range, 0f, 1f);
                float corrected = MathF.Pow(normalized, inverseGamma);
                result[i + c] = (byte)Math.Clamp(
                    (int)MathF.Round(outputBlack + corrected * (outputWhite - outputBlack)), 0, 255);
            }
        }
        return result;
    }

    public static byte[] ReplaceColor(
        ReadOnlySpan<byte> source,
        Color from,
        Color to,
        int tolerance,
        bool preserveLuminance,
        CancellationToken cancellationToken = default)
    {
        byte[] result = source.ToArray();
        for (int i = 0; i < result.Length; i += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int distance = Math.Abs(result[i] - from.R)
                + Math.Abs(result[i + 1] - from.G)
                + Math.Abs(result[i + 2] - from.B);
            if (distance > tolerance * 3) continue;
            float luminance = preserveLuminance
                ? (result[i] * 0.2126f + result[i + 1] * 0.7152f + result[i + 2] * 0.0722f) / 255f
                : 1f;
            result[i] = (byte)Math.Clamp((int)MathF.Round(to.R * luminance), 0, 255);
            result[i + 1] = (byte)Math.Clamp((int)MathF.Round(to.G * luminance), 0, 255);
            result[i + 2] = (byte)Math.Clamp((int)MathF.Round(to.B * luminance), 0, 255);
            result[i + 3] = (byte)Math.Clamp((int)MathF.Round(result[i + 3] * (to.A / 255f)), 0, 255);
        }
        return result;
    }

    public static byte[] ReshadePalette(
        ReadOnlySpan<byte> source,
        IReadOnlyList<Color> palette,
        bool preserveLuminance,
        CancellationToken cancellationToken = default)
    {
        if (palette == null || palette.Count == 0) return source.ToArray();
        byte[] result = source.ToArray();
        for (int i = 0; i < result.Length; i += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result[i + 3] == 0) continue;
            Color best = palette[0];
            int bestDistance = int.MaxValue;
            foreach (Color candidate in palette)
            {
                int dr = result[i] - candidate.R;
                int dg = result[i + 1] - candidate.G;
                int db = result[i + 2] - candidate.B;
                int distance = dr * dr + dg * dg + db * db;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = candidate;
            }

            if (preserveLuminance)
            {
                float oldLuma = Math.Max(0.001f, Luma(result[i], result[i + 1], result[i + 2]));
                float newLuma = Math.Max(0.001f, Luma(best.R, best.G, best.B));
                float scale = oldLuma / newLuma;
                result[i] = ClampByte(best.R * scale);
                result[i + 1] = ClampByte(best.G * scale);
                result[i + 2] = ClampByte(best.B * scale);
            }
            else
            {
                result[i] = best.R;
                result[i + 1] = best.G;
                result[i + 2] = best.B;
            }
        }
        return result;
    }

    public static byte[] GenerateNoise(
        int width,
        int height,
        int seed,
        float scale,
        Color low,
        Color high,
        CancellationToken cancellationToken = default)
    {
        byte[] result = new byte[checked(width * height * 4)];
        Random random = new(seed);
        float frequency = Math.Max(0.001f, scale);
        int cellsX = Math.Max(2, (int)MathF.Ceiling(width * frequency) + 2);
        int cellsY = Math.Max(2, (int)MathF.Ceiling(height * frequency) + 2);
        float[] lattice = new float[cellsX * cellsY];
        for (int i = 0; i < lattice.Length; i++) lattice[i] = random.NextSingle();
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float fy = y * frequency;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, cellsY - 2);
            float ty = Smooth(fy - y0);
            for (int x = 0; x < width; x++)
            {
                float fx = x * frequency;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, cellsX - 2);
                float tx = Smooth(fx - x0);
                float a = Lerp(lattice[y0 * cellsX + x0], lattice[y0 * cellsX + x0 + 1], tx);
                float b = Lerp(lattice[(y0 + 1) * cellsX + x0], lattice[(y0 + 1) * cellsX + x0 + 1], tx);
                float value = Lerp(a, b, ty);
                int i = (y * width + x) * 4;
                result[i] = ClampByte(Lerp(low.R, high.R, value));
                result[i + 1] = ClampByte(Lerp(low.G, high.G, value));
                result[i + 2] = ClampByte(Lerp(low.B, high.B, value));
                result[i + 3] = ClampByte(Lerp(low.A, high.A, value));
            }
        }
        return result;
    }

    public static byte[] GenerateOutline(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        Color color,
        int radius,
        bool outsideOnly,
        CancellationToken cancellationToken = default)
    {
        byte[] result = source.ToArray();
        int r = Math.Clamp(radius, 1, 64);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool opaque = source[i + 3] > 0;
                if (outsideOnly && opaque) continue;
                bool edge = false;
                for (int oy = -r; oy <= r && !edge; oy++)
                {
                    for (int ox = -r; ox <= r; ox++)
                    {
                        if (ox * ox + oy * oy > r * r) continue;
                        int sx = x + ox;
                        int sy = y + oy;
                        if ((uint)sx >= (uint)width || (uint)sy >= (uint)height) continue;
                        bool neighborOpaque = source[(sy * width + sx) * 4 + 3] > 0;
                        if (neighborOpaque != opaque) { edge = true; break; }
                    }
                }
                if (!edge) continue;
                result[i] = color.R;
                result[i + 1] = color.G;
                result[i + 2] = color.B;
                result[i + 3] = color.A;
            }
        }
        return result;
    }

    public static byte[] OrderedDither(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        IReadOnlyList<Color> palette,
        CancellationToken cancellationToken = default)
    {
        if (palette == null || palette.Count == 0) return source.ToArray();
        int[,] bayer =
        {
            { 0, 8, 2, 10 },
            { 12, 4, 14, 6 },
            { 3, 11, 1, 9 },
            { 15, 7, 13, 5 },
        };
        byte[] adjusted = source.ToArray();
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                int bias = (bayer[y & 3, x & 3] - 8) * 4;
                adjusted[i] = (byte)Math.Clamp(adjusted[i] + bias, 0, 255);
                adjusted[i + 1] = (byte)Math.Clamp(adjusted[i + 1] + bias, 0, 255);
                adjusted[i + 2] = (byte)Math.Clamp(adjusted[i + 2] + bias, 0, 255);
            }
        }
        return ReshadePalette(adjusted, palette, preserveLuminance: false, cancellationToken);
    }

    public static byte[] NormalFromHeight(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        float strength,
        CancellationToken cancellationToken = default)
    {
        byte[] result = new byte[checked(width * height * 4)];
        float s = Math.Max(0.01f, strength);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                float left = HeightAt(source, width, height, x - 1, y);
                float right = HeightAt(source, width, height, x + 1, y);
                float up = HeightAt(source, width, height, x, y - 1);
                float down = HeightAt(source, width, height, x, y + 1);
                Vector3 normal = Vector3.Normalize(new Vector3((left - right) * s, (up - down) * s, 1f));
                int i = (y * width + x) * 4;
                result[i] = ClampByte((normal.X * 0.5f + 0.5f) * 255f);
                result[i + 1] = ClampByte((normal.Y * 0.5f + 0.5f) * 255f);
                result[i + 2] = ClampByte((normal.Z * 0.5f + 0.5f) * 255f);
                result[i + 3] = 255;
            }
        }
        return result;
    }

    /// <summary>Brighten RGB by strength (0–100). Transparent pixels unchanged.</summary>
    public static byte[] WindWaker(
        ReadOnlySpan<byte> source,
        int strength = 70,
        CancellationToken cancellationToken = default)
    {
        float factor = Math.Clamp(strength, 0, 100) / 100f;
        byte[] result = source.ToArray();
        for (int i = 0; i < result.Length; i += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result[i + 3] == 0) continue;
            result[i] = ClampByte(result[i] * (1f + factor));
            result[i + 1] = ClampByte(result[i + 1] * (1f + factor));
            result[i + 2] = ClampByte(result[i + 2] * (1f + factor));
        }
        return result;
    }

    /// <summary>Half-resolution bilinear downsample then upsample (N64 soft look).</summary>
    public static byte[] N64Filter(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        ValidateSize(source, width, height);
        int lowW = Math.Max(1, width / 2);
        int lowH = Math.Max(1, height / 2);
        byte[] low = new byte[checked(lowW * lowH * 4)];
        for (int y = 0; y < lowH; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float srcY = (y + 0.5f) * height / lowH - 0.5f;
            for (int x = 0; x < lowW; x++)
            {
                float srcX = (x + 0.5f) * width / lowW - 0.5f;
                SampleBilinear(source, width, height, srcX, srcY, low, (y * lowW + x) * 4);
            }
        }

        byte[] result = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float srcY = (y + 0.5f) * lowH / height - 0.5f;
            for (int x = 0; x < width; x++)
            {
                float srcX = (x + 0.5f) * lowW / width - 0.5f;
                SampleBilinear(low, lowW, lowH, srcX, srcY, result, (y * width + x) * 4);
            }
        }
        return result;
    }

    /// <summary>Darken every other scanline by strength (0–100).</summary>
    public static byte[] CrtScanlines(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int strength = 50,
        CancellationToken cancellationToken = default)
    {
        ValidateSize(source, width, height);
        float factor = Math.Clamp(strength, 0, 100) / 100f;
        byte[] result = source.ToArray();
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (y % 2 == 0) continue;
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                result[i] = ClampByte(result[i] * (1f - factor * 0.5f));
                result[i + 1] = ClampByte(result[i + 1] * (1f - factor * 0.5f));
                result[i + 2] = ClampByte(result[i + 2] * (1f - factor * 0.5f));
            }
        }
        return result;
    }

    /// <summary>Extract bright pixels, blur, and additive-blend onto the source.</summary>
    public static byte[] Bloom(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int threshold = 80,
        CancellationToken cancellationToken = default)
    {
        ValidateSize(source, width, height);
        float lumaFloor = Math.Clamp(threshold, 0, 100) * 2.55f;
        byte[] bright = new byte[source.Length];
        for (int i = 0; i < source.Length; i += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float lum = source[i] * 0.299f + source[i + 1] * 0.587f + source[i + 2] * 0.114f;
            if (lum < lumaFloor)
            {
                bright[i + 3] = 0;
                continue;
            }

            bright[i] = source[i];
            bright[i + 1] = source[i + 1];
            bright[i + 2] = source[i + 2];
            bright[i + 3] = source[i + 3];
        }

        float[,] blurKernel =
        {
            { 1 / 16f, 2 / 16f, 1 / 16f },
            { 2 / 16f, 4 / 16f, 2 / 16f },
            { 1 / 16f, 2 / 16f, 1 / 16f },
        };
        byte[] blurred = ApplyConvolution(bright, width, height, blurKernel, cancellationToken);
        byte[] result = source.ToArray();
        for (int i = 0; i < result.Length; i += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blurred[i + 3] == 0) continue;
            float srcA = result[i + 3] / 255f;
            float bloomA = blurred[i + 3] / 255f;
            float outA = Math.Clamp(srcA + bloomA * (1f - srcA), 0f, 1f);
            if (outA <= 0f)
            {
                result[i] = result[i + 1] = result[i + 2] = result[i + 3] = 0;
                continue;
            }

            result[i] = ClampByte((result[i] * srcA + blurred[i] * bloomA) / outA);
            result[i + 1] = ClampByte((result[i + 1] * srcA + blurred[i + 1] * bloomA) / outA);
            result[i + 2] = ClampByte((result[i + 2] * srcA + blurred[i + 2] * bloomA) / outA);
            result[i + 3] = ClampByte(outA * 255f);
        }
        return result;
    }

    /// <summary>Boost saturation more for less-saturated pixels. amount 100 = identity; 120 = old default.</summary>
    public static byte[] Vibrance(
        ReadOnlySpan<byte> source,
        int amount = 120,
        CancellationToken cancellationToken = default)
    {
        float factor = (amount - 100) / 100f;
        byte[] result = source.ToArray();
        for (int i = 0; i < result.Length; i += 4)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result[i + 3] == 0) continue;
            ColorToHsv(result[i], result[i + 1], result[i + 2], out float h, out float s, out float v);
            s = Math.Clamp(s * (1f + factor * (1f - s)), 0f, 1f);
            HsvToColor(h, s, v, out result[i], out result[i + 1], out result[i + 2]);
        }
        return result;
    }

    public static byte[] Emboss(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        float[,] kernel =
        {
            { -2f, -1f, 0f },
            { -1f, 1f, 1f },
            { 0f, 1f, 2f },
        };
        return ApplyConvolution(source, width, height, kernel, cancellationToken);
    }

    /// <summary>Outside outline using alpha tolerance (legacy ReShade Outline).</summary>
    public static byte[] ReshadeOutline(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        Color color,
        int thickness = 1,
        int tolerance = 10,
        CancellationToken cancellationToken = default)
    {
        ValidateSize(source, width, height);
        int r = Math.Clamp(thickness, 1, 64);
        int alphaFloor = Math.Clamp(tolerance, 0, 255);
        byte[] result = new byte[source.Length];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                int a = source[i + 3];
                if (a > alphaFloor)
                {
                    result[i] = source[i];
                    result[i + 1] = source[i + 1];
                    result[i + 2] = source[i + 2];
                    result[i + 3] = source[i + 3];
                    continue;
                }

                bool edge = false;
                for (int dy = -r; dy <= r && !edge; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        if (dx * dx + dy * dy > r * r) continue;
                        int nx = x + dx;
                        int ny = y + dy;
                        if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;
                        if (source[(ny * width + nx) * 4 + 3] > alphaFloor)
                        {
                            edge = true;
                            break;
                        }
                    }
                }

                if (!edge) continue;
                result[i] = color.R;
                result[i + 1] = color.G;
                result[i + 2] = color.B;
                result[i + 3] = color.A == 0 ? (byte)255 : color.A;
            }
        }
        return result;
    }

    private static byte[] ApplyConvolution(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        float[,] kernel,
        CancellationToken cancellationToken)
    {
        ValidateSize(source, width, height);
        int kH = kernel.GetLength(0);
        int kW = kernel.GetLength(1);
        int oy = kH / 2;
        int ox = kW / 2;
        byte[] result = new byte[source.Length];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                float r = 0f, g = 0f, b = 0f, a = 0f, wSum = 0f;
                for (int ky = 0; ky < kH; ky++)
                {
                    for (int kx = 0; kx < kW; kx++)
                    {
                        int sx = Math.Clamp(x + kx - ox, 0, width - 1);
                        int sy = Math.Clamp(y + ky - oy, 0, height - 1);
                        int si = (sy * width + sx) * 4;
                        float w = kernel[ky, kx];
                        r += source[si] * w;
                        g += source[si + 1] * w;
                        b += source[si + 2] * w;
                        a += source[si + 3] * MathF.Abs(w);
                        wSum += MathF.Abs(w);
                    }
                }

                int i = (y * width + x) * 4;
                result[i] = ClampByte(r);
                result[i + 1] = ClampByte(g);
                result[i + 2] = ClampByte(b);
                result[i + 3] = wSum <= 0f ? source[i + 3] : ClampByte(a / wSum);
            }
        }
        return result;
    }

    private static void SampleBilinear(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        float x,
        float y,
        byte[] dest,
        int destIndex)
    {
        x = Math.Clamp(x, 0f, width - 1);
        y = Math.Clamp(y, 0f, height - 1);
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        float tx = x - x0;
        float ty = y - y0;
        int i00 = (y0 * width + x0) * 4;
        int i10 = (y0 * width + x1) * 4;
        int i01 = (y1 * width + x0) * 4;
        int i11 = (y1 * width + x1) * 4;
        for (int c = 0; c < 4; c++)
        {
            float top = Lerp(source[i00 + c], source[i10 + c], tx);
            float bottom = Lerp(source[i01 + c], source[i11 + c], tx);
            dest[destIndex + c] = ClampByte(Lerp(top, bottom, ty));
        }
    }

    private static void ValidateSize(ReadOnlySpan<byte> source, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        if (source.Length < checked(width * height * 4))
            throw new ArgumentException("Source buffer is smaller than width*height*4.", nameof(source));
    }

    private static float HeightAt(ReadOnlySpan<byte> source, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        int i = (y * width + x) * 4;
        return Luma(source[i], source[i + 1], source[i + 2]);
    }

    private static float Luma(byte r, byte g, byte b) => (r * 0.2126f + g * 0.7152f + b * 0.0722f) / 255f;
    private static float Smooth(float value) => value * value * (3f - 2f * value);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static byte ClampByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static void ColorToHsv(byte r, byte g, byte b, out float hue, out float saturation, out float value)
    {
        float rf = r / 255f;
        float gf = g / 255f;
        float bf = b / 255f;
        float max = MathF.Max(rf, MathF.Max(gf, bf));
        float min = MathF.Min(rf, MathF.Min(gf, bf));
        float delta = max - min;
        hue = delta <= 0f
            ? 0f
            : max == rf
                ? 60f * (((gf - bf) / delta) % 6f)
                : max == gf
                    ? 60f * (((bf - rf) / delta) + 2f)
                    : 60f * (((rf - gf) / delta) + 4f);
        if (hue < 0f) hue += 360f;
        saturation = max <= 0f ? 0f : delta / max;
        value = max;
    }

    private static void HsvToColor(float h, float s, float v, out byte r, out byte g, out byte b)
    {
        float c = v * s;
        float x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
        float m = v - c;
        (float rf, float gf, float bf) = h switch
        {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x),
        };
        r = ClampByte((rf + m) * 255f);
        g = ClampByte((gf + m) * 255f);
        b = ClampByte((bf + m) * 255f);
    }
}
