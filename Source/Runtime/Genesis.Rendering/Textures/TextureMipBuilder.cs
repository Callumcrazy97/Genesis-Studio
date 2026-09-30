using System;
using System.Numerics;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Textures
{
    /// <summary>sRGB transfer functions for colours authored in display space.</summary>
    public static class SrgbColor
    {
        public static float ToLinear(float value)
        {
            value = Math.Clamp(value, 0f, 1f);
            return value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
        }

        public static float ToSrgb(float value)
        {
            value = Math.Clamp(value, 0f, 1f);
            return value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
        }

        /// <summary>Linearizes RGB and keeps W (alpha or intensity) unchanged.</summary>
        public static Vector4 ToLinear(Vector4 color) =>
            new(ToLinear(color.X), ToLinear(color.Y), ToLinear(color.Z), color.W);

        public static Vector3 ToLinear(Vector3 color) =>
            new(ToLinear(color.X), ToLinear(color.Y), ToLinear(color.Z));
    }

    /// <summary>
    /// Builds tightly packed RGBA8 mip chains for uploads that have no pre-cooked DDS. Without mips,
    /// every uncooked texture shimmered at distance and sampled at full resolution everywhere.
    /// </summary>
    public static class TextureMipBuilder
    {
        private static readonly float[] DecodeSrgb = BuildDecodeTable();
        private static readonly byte[] EncodeSrgb = BuildEncodeTable();

        /// <summary>
        /// Returns mip 0 followed by every smaller level down to 1×1, each a 2×2 box filter of the
        /// previous level. Colour data (<paramref name="srgb"/>) is averaged in linear light so
        /// minified textures keep their brightness; alpha is always averaged linearly.
        /// </summary>
        public static byte[] BuildChain(ReadOnlySpan<byte> rgba, int width, int height, bool srgb, out int mipLevels)
        {
            mipLevels = GpuTextureLayout.FullMipCount(width, height);
            int total = GpuTextureLayout.GetMipChainSize(GpuFormat.R8G8B8A8UNorm, width, height, mipLevels);
            byte[] chain = new byte[total];
            int baseBytes = checked(width * height * 4);
            rgba[..baseBytes].CopyTo(chain);

            int source = 0;
            int sourceWidth = width;
            int sourceHeight = height;
            int destination = baseBytes;
            for (int level = 1; level < mipLevels; level++)
            {
                int levelWidth = Math.Max(1, sourceWidth >> 1);
                int levelHeight = Math.Max(1, sourceHeight >> 1);
                for (int y = 0; y < levelHeight; y++)
                {
                    int y0 = Math.Min(sourceHeight - 1, y * 2);
                    int y1 = Math.Min(sourceHeight - 1, y * 2 + 1);
                    for (int x = 0; x < levelWidth; x++)
                    {
                        int x0 = Math.Min(sourceWidth - 1, x * 2);
                        int x1 = Math.Min(sourceWidth - 1, x * 2 + 1);
                        int a = source + (y0 * sourceWidth + x0) * 4;
                        int b = source + (y0 * sourceWidth + x1) * 4;
                        int c = source + (y1 * sourceWidth + x0) * 4;
                        int d = source + (y1 * sourceWidth + x1) * 4;
                        int output = destination + (y * levelWidth + x) * 4;
                        for (int channel = 0; channel < 3; channel++)
                        {
                            if (srgb)
                            {
                                float average = (DecodeSrgb[chain[a + channel]] + DecodeSrgb[chain[b + channel]]
                                    + DecodeSrgb[chain[c + channel]] + DecodeSrgb[chain[d + channel]]) * 0.25f;
                                chain[output + channel] = EncodeSrgb[(int)MathF.Round(Math.Clamp(average, 0f, 1f) * (EncodeSrgb.Length - 1))];
                            }
                            else
                            {
                                chain[output + channel] = (byte)((chain[a + channel] + chain[b + channel]
                                    + chain[c + channel] + chain[d + channel] + 2) >> 2);
                            }
                        }
                        chain[output + 3] = (byte)((chain[a + 3] + chain[b + 3] + chain[c + 3] + chain[d + 3] + 2) >> 2);
                    }
                }
                source = destination;
                destination += levelWidth * levelHeight * 4;
                sourceWidth = levelWidth;
                sourceHeight = levelHeight;
            }
            return chain;
        }

        private static float[] BuildDecodeTable()
        {
            var table = new float[256];
            for (int i = 0; i < table.Length; i++) table[i] = SrgbColor.ToLinear(i / 255f);
            return table;
        }

        private static byte[] BuildEncodeTable()
        {
            // 4096 linear steps keep dark gradients distinct after encoding.
            var table = new byte[4096];
            for (int i = 0; i < table.Length; i++)
                table[i] = (byte)MathF.Round(SrgbColor.ToSrgb(i / (float)(table.Length - 1)) * 255f);
            return table;
        }
    }
}
