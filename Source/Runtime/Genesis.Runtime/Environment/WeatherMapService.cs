using System;
using System.Numerics;

namespace Genesis.Runtime.Climate;

/// <summary>
/// AF2.2 advected weather map. Owns a CPU grid rebuilt from
/// <see cref="EnvironmentFrame"/> each update. AF2.3 packs the cache to RGBA8 for the
/// raymarched cloud pass; FogVolumes remain the Software / disabled fallback.
/// </summary>
public sealed class WeatherMapService
{
    private readonly WeatherMapSample[] _cache;
    private EnvironmentFrame _lastFrame;
    private int _seed = 1337;
    private float _mapScale = WeatherMapMath.DefaultMapScale;
    private bool _hasCache;

    public WeatherMapService(int resolution = WeatherMapMath.DefaultResolution)
    {
        Resolution = Math.Clamp(resolution, 8, 256);
        _cache = new WeatherMapSample[Resolution * Resolution];
        // Half-extent so cache UV spans roughly one map-scale period around the origin.
        WorldHalfExtent = 0.5f / MathF.Max(1e-6f, WeatherMapMath.DefaultMapScale);
    }

    /// <summary>Grid resolution (samples along one axis).</summary>
    public int Resolution { get; }

    /// <summary>World XZ half-extent of the cached grid centred on the origin.</summary>
    public float WorldHalfExtent { get; private set; }

    /// <summary>World XZ centre of the cached grid. Default callers retain an origin-centred map.</summary>
    public Vector2 WorldCenter { get; private set; }

    /// <summary>Cover the visible cloud layer, rather than only the ground immediately around
    /// the origin. Quantized centres keep orbiting/paused repaints from rebuilding the grid.</summary>
    public bool ConfigureViewCoverage(Vector3 observer, float cloudBase, float cloudThickness)
    {
        float extent = Math.Clamp((MathF.Abs(cloudBase - observer.Y) + cloudThickness) * 24f, 4096f, 262144f);
        extent = MathF.Ceiling(extent / 1024f) * 1024f;
        // Move by whole texels so the overlapping world samples do not change when
        // the observer crosses a coverage boundary.
        float cell = 2f * extent / Math.Max(1, Resolution - 1) * 2f;
        Vector2 center = new(MathF.Round(observer.X / cell) * cell, MathF.Round(observer.Z / cell) * cell);
        if (WorldCenter == center && WorldHalfExtent == extent) return false;
        WorldCenter = center; WorldHalfExtent = extent; _hasCache = false;
        return true;
    }

    /// <summary><see cref="EnvironmentFrame.ElapsedRealSeconds"/> from the last <see cref="Update"/>.</summary>
    public double LastUpdateElapsedSeconds { get; private set; }

    /// <summary>Mean coverage of the last cached grid (debug / F6).</summary>
    public float CurrentMeanCoverage { get; private set; }

    /// <summary>
    /// Rebuilds the origin-centred cache from <paramref name="frame"/> only
    /// (wind, elapsed real seconds, cloud cover / kind). No second clock.
    /// </summary>
    public void Update(in EnvironmentFrame frame, int seed = 1337)
    {
        _lastFrame = frame;
        _seed = seed;
        LastUpdateElapsedSeconds = frame.ElapsedRealSeconds;

        float mean = 0f;
        float extent = WorldHalfExtent;
        float step = (extent * 2f) / Math.Max(1, Resolution - 1);
        for (int y = 0; y < Resolution; y++)
        {
            float wz = WorldCenter.Y - extent + y * step;
            int row = y * Resolution;
            for (int x = 0; x < Resolution; x++)
            {
                float wx = WorldCenter.X - extent + x * step;
                WeatherMapSample sample = WeatherMapMath.Sample(
                    new Vector2(wx, wz), in frame, seed, _mapScale);
                _cache[row + x] = sample;
                mean += sample.Coverage;
            }
        }

        CurrentMeanCoverage = mean / (Resolution * Resolution);
        _hasCache = true;
    }

    /// <summary>
    /// Samples the weather map at world XZ. Uses bilinear cache when available and in-bounds;
    /// otherwise evaluates <see cref="WeatherMapMath.Sample"/> with the last frame.
    /// </summary>
    public WeatherMapSample Sample(Vector2 worldXZ)
    {
        if (_hasCache
            && worldXZ.X >= WorldCenter.X - WorldHalfExtent && worldXZ.X <= WorldCenter.X + WorldHalfExtent
            && worldXZ.Y >= WorldCenter.Y - WorldHalfExtent && worldXZ.Y <= WorldCenter.Y + WorldHalfExtent
            && Resolution > 1)
        {
            return SampleBilinear(worldXZ);
        }

        return WeatherMapMath.Sample(worldXZ, in _lastFrame, _seed, _mapScale);
    }

    private WeatherMapSample SampleBilinear(Vector2 worldXZ)
    {
        float extent = WorldHalfExtent;
        float u = (worldXZ.X - WorldCenter.X + extent) / (extent * 2f) * (Resolution - 1);
        float v = (worldXZ.Y - WorldCenter.Y + extent) / (extent * 2f) * (Resolution - 1);
        int x0 = Math.Clamp((int)MathF.Floor(u), 0, Resolution - 1);
        int y0 = Math.Clamp((int)MathF.Floor(v), 0, Resolution - 1);
        int x1 = Math.Min(x0 + 1, Resolution - 1);
        int y1 = Math.Min(y0 + 1, Resolution - 1);
        float tx = u - x0;
        float ty = v - y0;

        WeatherMapSample a = _cache[y0 * Resolution + x0];
        WeatherMapSample b = _cache[y0 * Resolution + x1];
        WeatherMapSample c = _cache[y1 * Resolution + x0];
        WeatherMapSample d = _cache[y1 * Resolution + x1];

        return new WeatherMapSample(
            Bilerp(a.Coverage, b.Coverage, c.Coverage, d.Coverage, tx, ty),
            Bilerp(a.CloudType, b.CloudType, c.CloudType, d.CloudType, tx, ty),
            Bilerp(a.Erosion, b.Erosion, c.Erosion, d.Erosion, tx, ty),
            Bilerp(a.VerticalDevelopment, b.VerticalDevelopment, c.VerticalDevelopment, d.VerticalDevelopment, tx, ty));
    }

    private static float Bilerp(float a, float b, float c, float d, float tx, float ty) =>
        float.Lerp(float.Lerp(a, b, tx), float.Lerp(c, d, tx), ty);

    /// <summary>
    /// Packs the cached grid to RGBA8 (R=Coverage, G=Type, B=Erosion, A=Vertical) for
    /// <c>ForwardRenderer.SetWeatherMapRgba8</c>. When <paramref name="coverageScale"/> is not 1,
    /// multiplies the Coverage channel (scale=1 is byte-identical). Returns false when the cache
    /// is empty or <paramref name="dest"/> is too small.
    /// </summary>
    public bool TryCopyRgba8(
        Span<byte> dest,
        out int width,
        out int height,
        out float halfExtent,
        float coverageScale = 1f)
    {
        width = Resolution;
        height = Resolution;
        halfExtent = WorldHalfExtent;
        int byteCount = width * height * 4;
        if (!_hasCache || dest.Length < byteCount)
            return false;

        float scale = Math.Clamp(coverageScale, 0f, 4f);
        for (int i = 0; i < _cache.Length; i++)
        {
            WeatherMapSample s = _cache[i];
            int o = i * 4;
            dest[o] = ToByte(s.Coverage * scale);
            dest[o + 1] = ToByte(s.CloudType);
            dest[o + 2] = ToByte(s.Erosion);
            dest[o + 3] = ToByte(s.VerticalDevelopment);
        }

        return true;
    }

    private static byte ToByte(float unit) =>
        (byte)Math.Clamp((int)MathF.Round(Math.Clamp(unit, 0f, 1f) * 255f), 0, 255);
}
