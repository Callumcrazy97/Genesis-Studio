namespace Genesis.Application.Editors.Image.Imaging;

/// <summary>RGBA adjustments independent of the old IDE.</summary>
public static class ImageAdjustmentOperations
{
    public static byte[] Tint(byte[] source, Color tint, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte[] result = (byte[])source.Clone();
        byte[] colour = [tint.R, tint.G, tint.B];
        for (int i = 0; i < result.Length; i += 4)
            for (int c = 0; c < 3; c++) result[i + c] = Byte(source[i + c] * (1 - amount) + colour[c] * amount);
        return result;
    }

    public static byte[] Fade(byte[] source, float amount)
    {
        byte[] result = (byte[])source.Clone();
        for (int i = 3; i < result.Length; i += 4) result[i] = Byte(source[i] * (1 - Math.Clamp(amount, 0, 1)));
        return result;
    }

    public static byte[] ReduceDetail(byte[] source, int width, int height, int percent)
    {
        int rw = Math.Max(1, width * Math.Clamp(percent, 1, 100) / 100), rh = Math.Max(1, height * Math.Clamp(percent, 1, 100) / 100);
        byte[] result = new byte[source.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sx = (x * rw / width) * width / rw, sy = (y * rh / height) * height / rh;
                Buffer.BlockCopy(source, (sy * width + sx) * 4, result, (y * width + x) * 4, 4);
            }
        return result;
    }

    public static byte[] DepthContrast(byte[] source, float amount)
    {
        byte[] result = (byte[])source.Clone();
        for (int i = 0; i < result.Length; i += 4)
        {
            float luminance = source[i] * 0.2126f + source[i + 1] * 0.7152f + source[i + 2] * 0.0722f;
            float offset = (luminance - 128) * amount;
            for (int c = 0; c < 3; c++) result[i + c] = Byte(source[i + c] + offset);
        }
        return result;
    }

    public static byte[] Sharpen(byte[] source, int width, int height, bool diagonals)
    {
        byte[] result = (byte[])source.Clone();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float value = source[i + c];
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if ((dx == 0 && dy == 0) || (!diagonals && dx != 0 && dy != 0)) continue;
                            int j = (Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)) * 4;
                            // Hidden RGB must not introduce fringes around transparent sprite edges.
                            value += (source[i + c] - source[j + c]) * (source[j + 3] / 255f);
                        }
                    result[i + c] = Byte(value);
                }
            }
        return result;
    }

    public static byte[] SelectiveBlur(byte[] source, int width, int height, float strength, int threshold)
    {
        byte[] result = (byte[])source.Clone();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (source[i + 3] == 0) continue;
                float red = 0, green = 0, blue = 0, total = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int j = (Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)) * 4;
                        if (Math.Abs(source[j] - source[i]) > threshold || Math.Abs(source[j + 1] - source[i + 1]) > threshold || Math.Abs(source[j + 2] - source[i + 2]) > threshold) continue;
                        float weight = source[j + 3] / 255f;
                        red += source[j] * weight; green += source[j + 1] * weight; blue += source[j + 2] * weight; total += weight;
                    }
                if (total <= 0) continue;
                result[i] = Byte(source[i] * (1 - strength) + red / total * strength);
                result[i + 1] = Byte(source[i + 1] * (1 - strength) + green / total * strength);
                result[i + 2] = Byte(source[i + 2] * (1 - strength) + blue / total * strength);
            }
        return result;
    }

    public static byte[] RotatedGridSmooth(byte[] source, int width, int height, int samples)
    {
        samples = Math.Clamp(samples, 1, 8);
        byte[] result = new byte[source.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float red = 0, green = 0, blue = 0, alpha = 0;
                for (int sample = 0; sample < samples; sample++)
                {
                    float angle = (sample + 0.375f) * MathF.Tau / samples;
                    float sx = x + MathF.Cos(angle) * 0.5f, sy = y + MathF.Sin(angle) * 0.5f;
                    int left = (int)MathF.Floor(sx), top = (int)MathF.Floor(sy);
                    float fx = sx - left, fy = sy - top;
                    for (int yy = 0; yy <= 1; yy++)
                        for (int xx = 0; xx <= 1; xx++)
                        {
                            int i = (Math.Clamp(top + yy, 0, height - 1) * width + Math.Clamp(left + xx, 0, width - 1)) * 4;
                            float weight = (xx == 0 ? 1 - fx : fx) * (yy == 0 ? 1 - fy : fy) / samples;
                            float a = source[i + 3] / 255f * weight;
                            alpha += a; red += source[i] * a; green += source[i + 1] * a; blue += source[i + 2] * a;
                        }
                }
                int target = (y * width + x) * 4;
                if (alpha > 0) { result[target] = Byte(red / alpha); result[target + 1] = Byte(green / alpha); result[target + 2] = Byte(blue / alpha); }
                result[target + 3] = Byte(alpha * 255f);
            }
        return result;
    }

    private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
}
