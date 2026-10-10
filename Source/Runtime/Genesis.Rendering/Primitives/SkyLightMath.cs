using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>What the sky the engine draws, and the sunlit ground below it, give the surfaces of a scene.</summary>
/// <param name="Up">Diffuse light on a surface facing straight up (the sky dome, cosine weighted).</param>
/// <param name="Down">Diffuse light on a surface facing straight down (the lit ground).</param>
/// <param name="MirrorZenith">The sky's radiance straight up, for mirror-like reflections.</param>
/// <param name="MirrorHorizon">The sky's radiance at the horizon.</param>
/// <param name="MirrorGround">The ground's radiance, for reflections below the horizon.</param>
public readonly record struct SkyLightTerms(
    Vector3 Up, Vector3 Down, Vector3 MirrorZenith, Vector3 MirrorHorizon, Vector3 MirrorGround);

/// <summary>The sky as the frame draws it, in the colour space lighting is done in.</summary>
/// <param name="Zenith">The authored sky's zenith colour (used when <paramref name="AuthoredSky"/>).</param>
/// <param name="Horizon">The authored sky's horizon colour (used when <paramref name="AuthoredSky"/>).</param>
/// <param name="Background">The clear colour, which is the sky when no authored sky is drawn.</param>
/// <param name="AuthoredSky">The composite draws the horizon-to-zenith gradient (a dynamic sky).</param>
/// <param name="AtmosphereLut">The composite adds the atmosphere LUT's transmittance, multi-scatter and sky-view terms.</param>
/// <param name="SunHeight">Sine of the sun's elevation as the sky composite uses it.</param>
/// <param name="LightHeight">Sine of the elevation of the light that lights the ground (the sun, or the moon at night).</param>
/// <param name="LightRadiance">That light's colour times intensity.</param>
public readonly record struct SkyDome(
    Vector3 Zenith, Vector3 Horizon, Vector3 Background, bool AuthoredSky, bool AtmosphereLut,
    float SunHeight, float LightHeight, Vector3 LightRadiance);

/// <summary>
/// The hemisphere sky light (request 64a): an irradiance estimate of the sky dome the composite
/// draws (<see cref="FogPostShaders"/>), worked out on the CPU whenever the sky changes. The sky the
/// engine draws has no azimuthal structure apart from the sun's disc, so its light is the integral
/// over elevation: a surface facing up receives <c>2 ∫ L(y) y dy</c> (cosine weighted, in the
/// units the shaders' ambient terms use), and one facing down receives the ground, a warm grey of
/// albedo 0.2 lit by the sun and that sky. The shaders blend the two by the surface normal as they
/// always have, so terrain, foliage, water and every backend (the CPU rasterizer included) take
/// it with no change; specular reflections use the sky's own radiance (<see cref="SkyLightTerms.MirrorZenith"/>).
/// </summary>
public static class SkyLightMath
{
    /// <summary>The ground's reflectance: dry soil, asphalt and grass average about a fifth, a little warm.</summary>
    public static readonly Vector3 GroundAlbedo = new(0.2f, 0.19f, 0.17f);

    /// <summary>Elevation samples of the dome (midpoint rule in the sine of elevation).</summary>
    public const int Samples = 32;

    private static readonly Vector3 Luma = new(0.2126f, 0.7152f, 0.0722f);
    private static readonly Lazy<byte[]> Lut = new(AtmosphereLutMath.BakeRgba8);
    private static readonly Func<Vector2, Vector3> SampleLut = uv => AtmosphereLutMath.SampleBakedRgba8(Lut.Value, uv);

    /// <summary>
    /// The sky's radiance at elevation sine <paramref name="y"/> (0 at the horizon, 1 straight up),
    /// as the composite draws it: the authored gradient (or the clear colour), then the atmosphere
    /// LUT's terms. The sun's disc is left out: it is the directional light itself.
    /// </summary>
    public static Vector3 SkyRadiance(float y, in SkyDome dome)
    {
        float up = Math.Clamp(y, 0f, 1f);
        if (!dome.AtmosphereLut) return dome.Background;
        Vector3 baseSky = dome.AuthoredSky
            ? Vector3.Lerp(dome.Horizon, dome.Zenith, MathF.Pow(up, 0.45f))
            : dome.Background;
        float daylight = AtmosphereLutMath.DaylightFromSunHeight(dome.SunHeight);
        float viewUp = Math.Clamp(y * 0.5f + 0.5f, 0f, 1f);
        float horizon = MathF.Exp(-MathF.Abs(y) * 4.5f);
        return Vector3.Max(Vector3.Zero, AtmosphereLutMath.ComposeSky(baseSky, dome.SunHeight, viewUp, daylight, horizon, SampleLut));
    }

    /// <summary>The light a surface facing straight up receives from the sky alone.</summary>
    public static Vector3 SkyIrradianceUp(in SkyDome dome)
    {
        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < Samples; i++)
        {
            float y = (i + 0.5f) / Samples;
            sum += SkyRadiance(y, dome) * y;
        }
        return sum * (2f / Samples);
    }

    /// <summary>
    /// The sky light's five terms for the shaders, with its strength, tint and saturation applied
    /// (saturation to the diffuse terms only: a mirror shows the sky as it is drawn).
    /// </summary>
    public static SkyLightTerms Compute(in SkyDome dome, float strength, Vector3 tint, float saturation)
    {
        Vector3 up = SkyIrradianceUp(dome);
        Vector3 sunOnGround = Vector3.Max(Vector3.Zero, dome.LightRadiance) * MathF.Max(0f, dome.LightHeight);
        Vector3 ground = GroundAlbedo * (sunOnGround + up);
        Vector3 scale = tint * MathF.Max(0f, strength);
        return new SkyLightTerms(
            Saturate(up, saturation) * scale,
            Saturate(ground, saturation) * scale,
            SkyRadiance(1f, dome) * scale,
            SkyRadiance(0f, dome) * scale,
            ground * scale);
    }

    /// <summary>Moves a colour towards grey of the same brightness (0) or keeps it (1); above 1 deepens it.</summary>
    public static Vector3 Saturate(Vector3 color, float saturation)
    {
        float luma = Vector3.Dot(color, Luma);
        return Vector3.Max(Vector3.Zero, new Vector3(luma) + (color - new Vector3(luma)) * saturation);
    }

    /// <summary>Brightness of a colour (Rec. 709 weights).</summary>
    public static float Luminance(Vector3 color) => Vector3.Dot(color, Luma);
}
