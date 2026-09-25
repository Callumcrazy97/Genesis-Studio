using System;
using System.Numerics;

namespace Genesis.Shared.Interfaces;

/// <summary>
/// AF2.6 headless / PGSL sky authoring bridge. <c>Engine.Sky</c> mutates these when no live
/// <c>RuntimeScene</c> climate/atmosphere is bound; <see cref="Apply"/> stamps them onto
/// <see cref="Mesh3DState"/>. Room Climate/Atmosphere options overwrite after Apply (room wins).
/// Defaults match today's AF2.3–2.5 constants so goldens stay stable.
/// </summary>
public static class SkyAuthoringDefaults
{
    public const float DefaultDayOfYear = 215f;
    public const float DefaultLatitudeDegrees = 53.9f;
    public const float DefaultCloudBaseHeight = 180f;
    public const float DefaultCloudThickness = 85f;
    public const float DefaultCloudCoverageScale = 1f;
    public const float DefaultCloudDensityScale = 1f;
    public const float DefaultTimeOfDayHours = 7.5f;

    /// <summary>Default composite intensity for raymarched clouds (matches RaymarchedCloudsMath).</summary>
    public const float DefaultCloudIntensity = 0.85f;

    public static float DayOfYear { get; set; } = DefaultDayOfYear;
    public static float LatitudeDegrees { get; set; } = DefaultLatitudeDegrees;
    public static float CloudBaseHeight { get; set; } = DefaultCloudBaseHeight;
    public static float CloudThickness { get; set; } = DefaultCloudThickness;
    public static float CloudCoverageScale { get; set; } = DefaultCloudCoverageScale;
    public static float CloudDensityScale { get; set; } = DefaultCloudDensityScale;
    public static float TimeOfDayHours { get; set; } = DefaultTimeOfDayHours;

    /// <summary>Restore installation defaults (gate teardown / prefs reset).</summary>
    public static void Reset()
    {
        DayOfYear = DefaultDayOfYear;
        LatitudeDegrees = DefaultLatitudeDegrees;
        CloudBaseHeight = DefaultCloudBaseHeight;
        CloudThickness = DefaultCloudThickness;
        CloudCoverageScale = DefaultCloudCoverageScale;
        CloudDensityScale = DefaultCloudDensityScale;
        TimeOfDayHours = DefaultTimeOfDayHours;
    }

    /// <summary>Copy authoring bridge fields onto <paramref name="state"/> (clamped).</summary>
    public static void Apply(ref Mesh3DState state)
    {
        state.DayOfYear = ClampDayOfYear(DayOfYear);
        state.LatitudeDegrees = ClampLatitude(LatitudeDegrees);
        state.CloudBaseHeight = ClampBaseHeight(CloudBaseHeight);
        state.CloudThickness = ClampThickness(CloudThickness);
        state.CloudCoverageScale = ClampCoverageScale(CloudCoverageScale);
        state.CloudDensityScale = ClampDensityScale(CloudDensityScale);
    }

    /// <summary>
    /// Resolve raymarch <c>CloudParams</c> (x=base, y=thickness, z=steps, w=intensity×density)
    /// from state. Defaults produce the historical AF2.3 pack.
    /// </summary>
    public static Vector4 ResolveCloudParams(in Mesh3DState state, float stepCount = 24f)
    {
        float intensity = DefaultCloudIntensity * ClampDensityScale(state.CloudDensityScale);
        return new Vector4(
            ClampBaseHeight(state.CloudBaseHeight),
            ClampThickness(state.CloudThickness),
            stepCount,
            intensity);
    }

    /// <summary>Coverage scale for weather-map upload / shader multiply (1 = identity).</summary>
    public static float ResolveCoverageScale(in Mesh3DState state) =>
        ClampCoverageScale(state.CloudCoverageScale);

    public static float ClampDayOfYear(float value) => Math.Clamp(value, 1f, 366f);
    public static float ClampLatitude(float value) => Math.Clamp(value, -89f, 89f);
    public static float ClampBaseHeight(float value) => Math.Clamp(value, 10f, 10000f);
    public static float ClampThickness(float value) => Math.Clamp(value, 5f, 2000f);
    public static float ClampCoverageScale(float value) => Math.Clamp(value, 0f, 4f);
    public static float ClampDensityScale(float value) => Math.Clamp(value, 0f, 4f);
}
