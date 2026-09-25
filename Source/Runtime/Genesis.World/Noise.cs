using System;

namespace Genesis.World
{
    /// <summary>Deterministic value noise for procedural terrain (no external deps).</summary>
    public static class Noise
    {
        public static float Fbm2(int seed, float x, float z, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Value(seed + i * 7919, x * freq, z * freq) * amp;
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return sum / Math.Max(norm, 0.0001f);
        }

        public static float Fbm3(int seed, float x, float y, float z, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Value3(seed + i * 7919, x * freq, y * freq, z * freq) * amp;
                norm += amp;
                amp *= gain;
                freq *= lacunarity;
            }
            return sum / Math.Max(norm, 0.0001f);
        }

        public static float Value(int seed, float x, float z)
        {
            int x0 = (int)MathF.Floor(x);
            int z0 = (int)MathF.Floor(z);
            float tx = x - x0;
            float tz = z - z0;

            float v00 = Hash(seed, x0, z0);
            float v10 = Hash(seed, x0 + 1, z0);
            float v01 = Hash(seed, x0, z0 + 1);
            float v11 = Hash(seed, x0 + 1, z0 + 1);

            float sx = Smooth(tx);
            float sz = Smooth(tz);
            float ix0 = Lerp(v00, v10, sx);
            float ix1 = Lerp(v01, v11, sx);
            return Lerp(ix0, ix1, sz);
        }

        public static float Value3(int seed, float x, float y, float z)
        {
            int x0 = (int)MathF.Floor(x);
            int y0 = (int)MathF.Floor(y);
            int z0 = (int)MathF.Floor(z);
            float tx = x - x0;
            float ty = y - y0;
            float tz = z - z0;

            float c000 = Hash3(seed, x0, y0, z0);
            float c100 = Hash3(seed, x0 + 1, y0, z0);
            float c010 = Hash3(seed, x0, y0 + 1, z0);
            float c110 = Hash3(seed, x0 + 1, y0 + 1, z0);
            float c001 = Hash3(seed, x0, y0, z0 + 1);
            float c101 = Hash3(seed, x0 + 1, y0, z0 + 1);
            float c011 = Hash3(seed, x0, y0 + 1, z0 + 1);
            float c111 = Hash3(seed, x0 + 1, y0 + 1, z0 + 1);

            float sx = Smooth(tx);
            float sy = Smooth(ty);
            float sz = Smooth(tz);

            float x00 = Lerp(c000, c100, sx);
            float x10 = Lerp(c010, c110, sx);
            float x01 = Lerp(c001, c101, sx);
            float x11 = Lerp(c011, c111, sx);
            float y0v = Lerp(x00, x10, sy);
            float y1v = Lerp(x01, x11, sy);
            return Lerp(y0v, y1v, sz);
        }

        private static float Hash(int seed, int x, int z)
        {
            uint h = (uint)(seed ^ x * 374761393 ^ z * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            return (h & 0xFFFF) / 65535f;
        }

        private static float Hash3(int seed, int x, int y, int z)
        {
            uint h = (uint)(seed ^ x * 374761393 ^ y * 668265263 ^ z * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177;
            return (h & 0xFFFF) / 65535f;
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
