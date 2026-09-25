using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF2.5 headless-pure celestial extras: lunar phase, earthshine, star intensity, sidereal
/// rotation, Milky Way band weight, and moon disc/terminator. Shared by the FogPost sky branch
/// and the <c>Render.CelestialExtras.MathAndEnableWiring</c> gate. No GPU devices.
/// </summary>
/// <remarks>
/// Formulas follow SkyForge <c>CelestialSystem</c> / <c>skybox.frag</c> (sidereal rate, synodic
/// month, soft exp galactic band, disc terminator + 0.16 earthshine). Night and moon direction
/// fall back from <c>Mesh3DState.LightDirection</c> when climate day-of-year is not on the state.
/// </remarks>
public static class CelestialExtrasMath
{
    /// <summary>Mean synodic month in days (SkyForge lunar cycle).</summary>
    public const float SynodicMonthDays = 29.5306f;

    /// <summary>Earthshine scale on the unlit limb (SkyForge <c>(1-lit)*0.16</c>).</summary>
    public const float DefaultEarthshineScale = 0.16f;

    /// <summary>Sidereal angular rate matching SkyForge <c>0.017202124</c> rad per day-fraction.</summary>
    public const float SiderealRate = 0.017202124f;

    /// <summary>Default day-of-year when climate is not wired onto Mesh3DState (EnvironmentOptions).</summary>
    public const float DefaultDayOfYear = 215f;

    /// <summary>Default observer latitude in degrees (EnvironmentOptions default ≈ Manchester).</summary>
    public const float DefaultLatitudeDegrees = 53.9f;

    /// <summary>Apparent moon disc angular radius in radians (~0.42°).</summary>
    public const float MoonAngularRadiusRadians = 0.42f * (MathF.PI / 180f);

    /// <summary>
    /// Simple lunar illumination cycle in 0..1 from calendar day-of-year.
    /// <c>cycle = frac(|day| / 29.5306)</c>; <c>phase = 0.5 − 0.5·cos(2π·cycle)</c>
    /// (0 ≈ new, ~1 ≈ full — SkyForge).
    /// </summary>
    public static float MoonPhaseFromDayOfYear(float dayOfYear)
    {
        float absDay = MathF.Abs(dayOfYear);
        float cycle = absDay % SynodicMonthDays / SynodicMonthDays;
        return 0.5f - 0.5f * MathF.Cos(cycle * MathF.Tau);
    }

    /// <summary>
    /// Soft earthshine on the dark limb: <c>(1 − litAmount) · 0.16</c>.
    /// <paramref name="phase"/> is reserved for future brightness modulation (unused in v1).
    /// </summary>
    public static float EarthshineFactor(float phase, float litAmount)
    {
        _ = phase;
        float lit = Math.Clamp(litAmount, 0f, 1f);
        return (1f - lit) * DefaultEarthshineScale;
    }

    /// <summary>
    /// Star field intensity: 0 in full day, rises with night (<c>night²</c> so twilight stays quiet).
    /// </summary>
    public static float StarIntensity(float nightFactor)
    {
        float n = Math.Clamp(nightFactor, 0f, 1f);
        return n * n;
    }

    /// <summary>
    /// Sidereal sky rotation angle in radians.
    /// <paramref name="elapsedOrHours"/> is treated as hours when ≤ 48, otherwise as seconds
    /// (converted to hours). Angle = <c>(dayOfYear + hours/24) · SiderealRate</c> wrapped to
    /// <c>[0, 2π)</c>.
    /// </summary>
    public static float SiderealAngle(double elapsedOrHours, float dayOfYear)
    {
        double hours = elapsedOrHours > 48.0 ? elapsedOrHours / 3600.0 : elapsedOrHours;
        float angle = (dayOfYear + (float)hours / 24f) * SiderealRate;
        angle %= MathF.Tau;
        if (angle < 0f)
            angle += MathF.Tau;
        return angle;
    }

    /// <summary>
    /// Soft exponential Milky Way band weight for a unit view direction. Latitude tilts the
    /// galactic axis around X so the band rises/falls with observer latitude.
    /// </summary>
    public static float MilkyWayBandWeight(Vector3 viewDir, float latitudeRadians)
    {
        Vector3 dir = Vector3.Normalize(viewDir);
        // SkyForge base galactic normal, then tilt by latitude.
        Vector3 axis = Vector3.Normalize(new Vector3(0.12f, 0.42f, 0.9f));
        float s = MathF.Sin(latitudeRadians);
        float c = MathF.Cos(latitudeRadians);
        Vector3 tilted = Vector3.Normalize(new Vector3(
            axis.X,
            axis.Y * c - axis.Z * s,
            axis.Y * s + axis.Z * c));
        float galacticDistance = MathF.Abs(Vector3.Dot(dir, tilted));
        return MathF.Exp(-MathF.Pow(galacticDistance * 8.2f, 1.35f));
    }

    /// <summary>
    /// Moon disc mask (0..1) and terminator lit amount (0..1) for a view ray.
    /// <paramref name="moonDir"/> is the direction toward the moon (SkyForge disc convention).
    /// </summary>
    public static (float Mask, float LitAmount) MoonDiscLit(
        Vector3 viewDir,
        Vector3 moonDir,
        float phase)
    {
        Vector3 ray = Vector3.Normalize(viewDir);
        Vector3 moon = Vector3.Normalize(moonDir);
        float moonDot = Vector3.Dot(ray, moon);
        float moonVisible = SmoothStep(-0.035f, 0.015f, moon.Y);
        float radius = MoonAngularRadiusRadians;
        float mask = SmoothStep(MathF.Cos(radius * 1.05f), MathF.Cos(radius), moonDot) * moonVisible;

        Vector3 upRef = MathF.Abs(moon.Y) > 0.95f ? Vector3.UnitX : Vector3.UnitY;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(upRef, moon));
        Vector3 bitangent = Vector3.Cross(moon, tangent);
        float invSin = 1f / MathF.Max(MathF.Sin(radius), 1e-5f);
        float moonUvX = Vector3.Dot(ray, tangent) * invSin;

        phase = Math.Clamp(phase, 0.02f, 0.98f);
        float lit = SmoothStep(-0.075f, 0.075f, moonUvX + Lerp(0.88f, -0.88f, phase));
        if (phase > 0.5f)
            lit = 1f - SmoothStep(-0.075f, 0.075f, -moonUvX + Lerp(-0.88f, 0.88f, phase));

        return (mask, lit);
    }

    /// <summary>
    /// Night factor from sun travel direction (<c>Mesh3DState.LightDirection</c>): skyward sun → 0,
    /// below horizon → 1. Matches SkyForge <c>saturate((-towardSun.Y + 0.02) / 0.22)</c>.
    /// </summary>
    public static float NightFactorFromLightDirection(Vector3 lightDirection)
    {
        Vector3 towardSun = -SafeNormalize(lightDirection);
        return Math.Clamp((-towardSun.Y + 0.02f) / 0.22f, 0f, 1f);
    }

    /// <summary>
    /// Direction toward the moon for disc rendering: anti-sun plus a cheap lunar orbit offset
    /// (SkyForge <c>CelestialSystem</c>).
    /// </summary>
    public static Vector3 MoonDirectionToward(
        Vector3 lightDirection,
        float dayOfYear,
        float timeOfDayHours = 12f)
    {
        Vector3 towardSun = -SafeNormalize(lightDirection);
        float moonOrbit = (dayOfYear * 0.22997f + timeOfDayHours / 24f) * MathF.Tau;
        return Vector3.Normalize(new Vector3(
            -towardSun.X + 0.22f * MathF.Sin(moonOrbit),
            -towardSun.Y + 0.16f * MathF.Sin(moonOrbit * 0.83f),
            -towardSun.Z + 0.22f * MathF.Cos(moonOrbit)));
    }

    /// <summary>Latitude radians from degrees (clamped to ±89°).</summary>
    public static float LatitudeRadiansFromDegrees(float latitudeDegrees)
    {
        float deg = Math.Clamp(latitudeDegrees, -89f, 89f);
        return deg * (MathF.PI / 180f);
    }

    private static Vector3 SafeNormalize(Vector3 v)
    {
        float lenSq = v.LengthSquared();
        return lenSq > 1e-12f ? v / MathF.Sqrt(lenSq) : -Vector3.UnitY;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(edge1 - edge0, 1e-5f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
