using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Climate;

/// <summary>High-level authored sky profiles. The renderer remains backend neutral: this service
/// produces scene colours and the existing analytic fog-volume contract.</summary>
public enum AtmospherePreset
{
    Natural,
    ClearDay,
    GoldenHour,
    Overcast,
    Storm,
    Night,
    Alien,
}

public sealed class AtmosphereOptions
{
    public AtmospherePreset Preset { get; set; } = AtmospherePreset.Natural;
    public bool VolumetricClouds { get; set; } = true;
    public int CloudQuality { get; set; } = 2;
    public float CloudBaseHeight { get; set; } = 180f;
    public float CloudThickness { get; set; } = 85f;
    public float CloudCoverageScale { get; set; } = 1f;
    /// <summary>AF2.6 density → raymarch intensity multiplier (default 1).</summary>
    public float CloudDensityScale { get; set; } = 1f;
    public float Haze { get; set; } = 0.15f;
    public int Seed { get; set; } = 1337;

    public AtmosphereOptions Clone() => (AtmosphereOptions)MemberwiseClone();
}

public readonly record struct AtmosphereFrame(
    Vector3 ZenithColor,
    Vector3 HorizonColor,
    Vector3 SunColor,
    float SunIntensity,
    float CloudCoverage,
    float CloudDensity,
    float Haze,
    IReadOnlyList<FogVolume> CloudVolumes);

/// <summary>
/// Deterministic atmosphere and cloud evaluator. It deliberately consumes the authoritative
/// <see cref="EnvironmentFrame"/> so lighting, rain, wind, cloud drift and ambience cannot disagree.
/// </summary>
public sealed class AtmosphereService
{
    private readonly List<FogVolume> _cloudVolumes = new(6);

    public AtmosphereService(AtmosphereOptions options = null)
    {
        Options = (options ?? new AtmosphereOptions()).Clone();
        Validate(Options);
    }

    public AtmosphereOptions Options { get; }
    public AtmosphereFrame Current { get; private set; }

    public AtmosphereFrame Update(in EnvironmentFrame environment, Vector3 observerPosition)
    {
        float cloud = Math.Clamp(environment.Weather.CloudCover * Options.CloudCoverageScale, 0f, 1f);
        float haze = Math.Clamp(Options.Haze + environment.Weather.FogDensity * 2.5f, 0f, 1f);
        float night = environment.NightFactor;
        Vector3 horizon = new(environment.BackgroundColor.X, environment.BackgroundColor.Y, environment.BackgroundColor.Z);
        Vector3 zenith = environment.AmbientSky.LengthSquared() > 0.000001f
            ? Vector3.Normalize(environment.AmbientSky) * Math.Clamp(environment.AmbientSky.Length() * 2.1f, 0.04f, 1f)
            : horizon * 0.65f;
        Vector3 sun = Vector3.Lerp(new Vector3(1f, 0.48f, 0.18f), new Vector3(1f, 0.96f, 0.84f),
            Math.Clamp(environment.SunDirection.Y * -2f, 0f, 1f));
        float intensity = Math.Clamp((1f - night) * (1f - cloud * 0.62f), 0.02f, 1f);

        ApplyPreset(Options.Preset, ref zenith, ref horizon, ref sun, ref intensity, ref cloud, ref haze);
        BuildCloudVolumes(environment, observerPosition, cloud, zenith, horizon);
        Current = new AtmosphereFrame(zenith, horizon, sun, intensity, cloud,
            cloud * (0.22f + 0.2f * environment.LocalRain), haze, _cloudVolumes.ToArray());
        return Current;
    }

    public float SampleCloudDensity(Vector3 worldPosition, double elapsedSeconds)
    {
        float height = (worldPosition.Y - Options.CloudBaseHeight) / Options.CloudThickness;
        if (height < -0.5f || height > 0.5f) return 0f;
        float driftX = (float)(elapsedSeconds * 0.018);
        float driftZ = (float)(elapsedSeconds * 0.011);
        float n0 = Noise(worldPosition.X * 0.009f + driftX, worldPosition.Z * 0.009f + driftZ, Options.Seed);
        float n1 = Noise(worldPosition.X * 0.027f - driftZ, worldPosition.Z * 0.027f + driftX, Options.Seed + 71);
        float shape = MathF.Pow(Math.Clamp(1f - MathF.Abs(height) * 2f, 0f, 1f), 0.65f);
        return Math.Clamp((n0 * 0.72f + n1 * 0.28f - 0.43f) * 2.2f * shape, 0f, 1f);
    }

    private void BuildCloudVolumes(in EnvironmentFrame environment, Vector3 observer, float cloud,
        Vector3 zenith, Vector3 horizon)
    {
        _cloudVolumes.Clear();
        if (!Options.VolumetricClouds || cloud < 0.08f) return;
        int count = Math.Clamp(Options.CloudQuality + 1, 2, 6);
        Vector3 wind = environment.LocalWind.LengthSquared() > 0.001f
            ? Vector3.Normalize(environment.LocalWind)
            : Vector3.UnitX;
        Vector3 cross = new(-wind.Z, 0f, wind.X);
        float clock = (float)environment.ElapsedRealSeconds;
        Vector3 color = Vector3.Lerp(horizon, zenith, 0.58f);
        for (int i = 0; i < count; i++)
        {
            float phase = i * (MathF.Tau / count) + clock * 0.006f;
            float distance = 80f + i * 34f;
            Vector3 offset = wind * ((i - count * 0.5f) * 46f + clock * MathF.Max(0.25f, environment.LocalWind.Length()) * 0.18f)
                + cross * MathF.Sin(phase) * distance;
            float variation = 0.72f + 0.28f * Noise(i * 3.1f, clock * 0.004f, Options.Seed);
            _cloudVolumes.Add(new FogVolume
            {
                Center = new Vector3(observer.X + offset.X, Options.CloudBaseHeight + MathF.Cos(phase) * Options.CloudThickness * 0.12f, observer.Z + offset.Z),
                Extents = new Vector3(95f + 35f * variation, Options.CloudThickness * 0.5f, 70f + 45f * variation),
                Color = color,
                Density = Math.Clamp(cloud * 0.36f * variation, 0.02f, 0.58f),
                FalloffCurve = 1.25f,
                Shape = FogVolumeShape.Ellipsoid,
                Kind = FogVolumeKind.Cloud,
            });
        }
    }

    private static void ApplyPreset(AtmospherePreset preset, ref Vector3 zenith, ref Vector3 horizon,
        ref Vector3 sun, ref float intensity, ref float cloud, ref float haze)
    {
        switch (preset)
        {
            case AtmospherePreset.ClearDay:
                zenith = Vector3.Lerp(zenith, new Vector3(0.12f, 0.38f, 0.88f), 0.55f);
                horizon = Vector3.Lerp(horizon, new Vector3(0.62f, 0.8f, 0.98f), 0.45f);
                cloud *= 0.35f; haze *= 0.5f; break;
            case AtmospherePreset.GoldenHour:
                zenith = Vector3.Lerp(zenith, new Vector3(0.18f, 0.27f, 0.58f), 0.5f);
                horizon = Vector3.Lerp(horizon, new Vector3(1f, 0.34f, 0.1f), 0.7f);
                sun = new Vector3(1f, 0.34f, 0.08f); intensity *= 0.72f; break;
            case AtmospherePreset.Overcast:
                zenith = Vector3.Lerp(zenith, new Vector3(0.3f, 0.34f, 0.39f), 0.75f);
                horizon = Vector3.Lerp(horizon, new Vector3(0.5f, 0.52f, 0.54f), 0.72f);
                cloud = MathF.Max(cloud, 0.82f); intensity *= 0.55f; haze = MathF.Max(haze, 0.25f); break;
            case AtmospherePreset.Storm:
                zenith = Vector3.Lerp(zenith, new Vector3(0.055f, 0.075f, 0.11f), 0.88f);
                horizon = Vector3.Lerp(horizon, new Vector3(0.19f, 0.23f, 0.27f), 0.82f);
                cloud = 1f; intensity *= 0.22f; haze = MathF.Max(haze, 0.48f); break;
            case AtmospherePreset.Night:
                zenith = new Vector3(0.008f, 0.015f, 0.055f);
                horizon = new Vector3(0.03f, 0.045f, 0.1f);
                sun = new Vector3(0.42f, 0.55f, 0.9f); intensity = 0.035f; break;
            case AtmospherePreset.Alien:
                zenith = new Vector3(0.18f, 0.025f, 0.31f);
                horizon = new Vector3(0.1f, 0.63f, 0.46f);
                sun = new Vector3(0.9f, 0.25f, 1f); haze = MathF.Max(haze, 0.32f); break;
        }
    }

    private static float Noise(float x, float y, int seed)
    {
        int xi = (int)MathF.Floor(x);
        int yi = (int)MathF.Floor(y);
        float tx = x - xi;
        float ty = y - yi;
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

    private static void Validate(AtmosphereOptions options)
    {
        options.CloudQuality = Math.Clamp(options.CloudQuality, 0, 3);
        options.CloudBaseHeight = Math.Clamp(options.CloudBaseHeight, 10f, 10000f);
        options.CloudThickness = Math.Clamp(options.CloudThickness, 5f, 2000f);
        options.CloudCoverageScale = Math.Clamp(options.CloudCoverageScale, 0f, 4f);
        options.CloudDensityScale = Math.Clamp(options.CloudDensityScale, 0f, 4f);
        options.Haze = Math.Clamp(options.Haze, 0f, 1f);
    }
}
