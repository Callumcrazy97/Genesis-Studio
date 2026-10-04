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
            // A cut-out picture (leaves, grass, a fence) keeps the share of its texels that pass the
            // alpha cut-off at every size; averaging alone thins a forest to twigs at a distance.
            bool cutOut = IsCutOut(rgba[..baseBytes]);
            float coverage = cutOut ? Coverage(rgba[..baseBytes], AlphaCutoff) : 0f;

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
                if (cutOut) PreserveCoverage(chain.AsSpan(destination, levelWidth * levelHeight * 4), coverage, AlphaCutoff);
                source = destination;
                destination += levelWidth * levelHeight * 4;
                sourceWidth = levelWidth;
                sourceHeight = levelHeight;
            }
            return chain;
        }

        /// <summary>The engine's alpha cut-off, which coverage is kept against.</summary>
        public const float AlphaCutoff = 0.35f;

        /// <summary>
        /// True for a picture whose alpha is a cut-out (nearly all texels clearly in or out, with
        /// some of each), not a soft or blended one, whose smaller sizes are left as averaged.
        /// </summary>
        public static bool IsCutOut(ReadOnlySpan<byte> rgba)
        {
            int pixels = rgba.Length / 4;
            if (pixels < 16) return false;
            int clear = 0, solid = 0;
            for (int i = 3; i < rgba.Length; i += 4)
            {
                byte alpha = rgba[i];
                if (alpha <= 26) clear++;
                else if (alpha >= 229) solid++;
            }
            return clear + solid >= pixels * 0.9f && clear >= pixels / 100 + 1 && solid >= pixels / 100 + 1;
        }

        /// <summary>The share of texels whose alpha reaches <paramref name="cutoff"/>.</summary>
        public static float Coverage(ReadOnlySpan<byte> rgba, float cutoff)
        {
            int pixels = rgba.Length / 4, passing = 0;
            int threshold = (int)MathF.Ceiling(cutoff * 255f);
            for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] >= threshold) passing++;
            return pixels == 0 ? 0f : passing / (float)pixels;
        }

        /// <summary>Scales one level's alpha so the share passing the cut-off matches <paramref name="target"/>.</summary>
        public static void PreserveCoverage(Span<byte> level, float target, float cutoff)
        {
            int pixels = level.Length / 4;
            if (pixels == 0) return;
            int[] histogram = new int[256];
            for (int i = 3; i < level.Length; i += 4) histogram[level[i]]++;
            float CoverageAt(float scale)
            {
                // A texel passes when alpha * scale reaches the cut-off.
                int first = (int)MathF.Ceiling(cutoff * 255f / scale);
                int passing = 0;
                for (int a = Math.Clamp(first, 0, 256); a < 256; a++) passing += histogram[a];
                return passing / (float)pixels;
            }
            float low = 0.25f, high = 16f;
            for (int iteration = 0; iteration < 14; iteration++)
            {
                float middle = (low + high) * 0.5f;
                if (CoverageAt(middle) < target) low = middle;
                else high = middle;
            }
            float best = high;
            if (MathF.Abs(best - 1f) < 1e-3f) return;
            for (int i = 3; i < level.Length; i += 4)
                level[i] = (byte)Math.Clamp((int)MathF.Round(level[i] * best), 0, 255);
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
