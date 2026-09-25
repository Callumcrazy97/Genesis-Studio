using System;
using System.Numerics;

namespace Genesis.Runtime.Climate;

/// <summary>
/// AF2.2 CPU weather-map sample: coverage, cloud type, erosion, and vertical development.
/// Advection and evolution use <see cref="EnvironmentFrame.ElapsedRealSeconds"/> only —
/// no second weather clock.
/// </summary>
public readonly record struct WeatherMapSample(
    float Coverage,
    float CloudType,
    float Erosion,
    float VerticalDevelopment);

/// <summary>
/// Deterministic 2D weather-map math (SkyForge-style advection + value-noise FBM).
/// Time MUST come from <see cref="EnvironmentFrame.ElapsedRealSeconds"/> only.
/// </summary>
public static class WeatherMapMath
{
    /// <summary>Default CPU grid resolution for <see cref="WeatherMapService"/>.</summary>
    public const int DefaultResolution = 64;

    /// <summary>Default world-XZ → UV scale (≈400 m per UV unit).</summary>
    public const float DefaultMapScale = 0.0025f;

    /// <summary>
    /// Wind gain applied with map scale so metres/second of
    /// <see cref="EnvironmentFrame.LocalWind"/> visibly advect the field (SkyForge CLOUD_WIND_GAIN).
    /// </summary>
    public const float DefaultWindScale = 4f;

    /// <summary>
    /// Advects weather-map UV from world XZ using local wind and real elapsed seconds.
    /// <paramref name="elapsedRealSeconds"/> must be <see cref="EnvironmentFrame.ElapsedRealSeconds"/>.
    /// </summary>
    public static Vector2 AdvectUv(
        Vector2 worldXZ,
        Vector3 localWind,
        double elapsedRealSeconds,
        float scale,
        float windScale)
    {
        float t = (float)elapsedRealSeconds;
        Vector2 wind = new Vector2(localWind.X, localWind.Z) * t * scale * windScale;
        // Slow isotropic evolution so the field is not a still photograph when wind is calm.
        Vector2 evolutionDrift = new(t * 0.00031f, -t * 0.00023f);
        return worldXZ * scale + wind + evolutionDrift;
    }

    /// <summary>
    /// Samples the four weather channels at <paramref name="worldXZ"/>.
    /// Uses <paramref name="frame"/>.LocalWind, ElapsedRealSeconds, Weather.CloudCover, and Weather.Kind only.
    /// </summary>
    public static WeatherMapSample Sample(
        Vector2 worldXZ,
        in EnvironmentFrame frame,
        int seed,
        float mapScale = DefaultMapScale)
    {
        float scale = Math.Clamp(mapScale, 0.00004f, 0.01f);
        Vector2 uv = AdvectUv(worldXZ, frame.LocalWind, frame.ElapsedRealSeconds, scale, DefaultWindScale);

        float coverageNoise = Fbm(uv, seed, octaves: 3);
        float typeNoise = ValueNoise(uv * 0.53f + new Vector2(9f, 3f), seed + 17);
        float erosionBase = ValueNoise(uv * 2.2f + new Vector2(4f, 8f), seed + 31);
        float erosionAlt = ValueNoise(uv * 1.17f + new Vector2(2f, 13f), seed + 47);
        float erosionNoise = float.Lerp(erosionBase, erosionAlt, 0.28f);
        float verticalNoise = ValueNoise(uv * 0.34f + new Vector2(17f, 1f), seed + 71);

        KindBias(frame.Weather.Kind, out float typeBias, out float verticalBias);

        float cloudCover = Math.Clamp(frame.Weather.CloudCover, 0f, 1f);
        float coverage = Math.Clamp(coverageNoise * cloudCover, 0f, 1f);
        float cloudType = Math.Clamp(typeNoise * 0.65f + typeBias * 0.35f, 0f, 1f);
        float erosion = Math.Clamp(erosionNoise, 0f, 1f);
        float vertical = Math.Clamp(verticalNoise * 0.55f + verticalBias * 0.45f, 0f, 1f);

        return new WeatherMapSample(coverage, cloudType, erosion, vertical);
    }

    private static void KindBias(WeatherKind kind, out float typeBias, out float verticalBias)
    {
        switch (kind)
        {
            case WeatherKind.Clear:
                typeBias = 0.12f;
                verticalBias = 0.08f;
                break;
            case WeatherKind.Thunderstorm:
                typeBias = 0.88f;
                verticalBias = 0.92f;
                break;
            case WeatherKind.Overcast:
            case WeatherKind.Rain:
                typeBias = 0.52f;
                verticalBias = 0.42f;
                break;
            case WeatherKind.Hail:
                typeBias = 0.72f;
                verticalBias = 0.68f;
                break;
            case WeatherKind.Snow:
            case WeatherKind.Fog:
            case WeatherKind.Wind:
            default:
                typeBias = 0.40f;
                verticalBias = 0.28f;
                break;
        }
    }

    private static float Fbm(Vector2 p, int seed, int octaves)
    {
        float sum = 0f;
        float amp = 0.5f;
        float freq = 1f;
        float norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += ValueNoise(p * freq, seed + i * 101) * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2.03f;
        }

        return norm > 1e-6f ? sum / norm : 0f;
    }

    private static float ValueNoise(Vector2 p, int seed)
    {
        int xi = (int)MathF.Floor(p.X);
        int yi = (int)MathF.Floor(p.Y);
        float tx = p.X - xi;
        float ty = p.Y - yi;
        float a = Hash01(xi, yi, seed);
        float b = Hash01(xi + 1, yi, seed);
        float c = Hash01(xi, yi + 1, seed);
        float d = Hash01(xi + 1, yi + 1, seed);
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        return float.Lerp(float.Lerp(a, b, tx), float.Lerp(c, d, tx), ty);
    }

    private static float Hash01(int x, int y, int seed)
    {
        uint h = unchecked((uint)(x * 374761393 + y * 668265263 + seed * 69069));
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }
}
