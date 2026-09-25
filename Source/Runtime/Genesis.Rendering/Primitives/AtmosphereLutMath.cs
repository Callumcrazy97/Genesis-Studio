using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF2.1 CPU half of the SkyForge atmosphere atlas: bake transmittance / multi-scatter / sky-view
/// into a packed 256×64 RGBA8 LUT, sample UVs matching <c>skybox.frag</c>, and compose a sky colour
/// for the headless math gate. No GPU devices; ForwardRenderer uploads the bake once.
/// </summary>
/// <remarks>
/// Ported from SkyForge <c>ProceduralTextures.CreateAtmosphereLut</c> and the LUT sample/compose in
/// <c>skybox.frag</c>. Region layout (pixel space):
/// <list type="bullet">
/// <item>A — x&lt;128, y&lt;32: transmittance (sunHeight=x/127, viewHeight=y/31)</item>
/// <item>B — 128≤x&lt;160, y&lt;32: multi-scatter (daylight=(x-128)/31, viewHeight=y/31)</item>
/// <item>C — x&lt;128, y≥32: sky-view (viewUp=x/127, horizon=(y-32)/31)</item>
/// <item>Rest — black</item>
/// </list>
/// Optical bake coefficients below mirror the procedural source (DefaultExtinction-style docs).
/// Sun height comes from the existing light direction / EnvironmentFrame path — no second weather clock.
/// </remarks>
public static class AtmosphereLutMath
{
    public const int Width = 256;
    public const int Height = 64;
    public const int ByteCount = Width * Height * 4;

    /// <summary>Base optical depth scale in transmittance region A (SkyForge bake).</summary>
    public const float DefaultOpticalBase = 1.4f;

    /// <summary>Extra optical depth when the sun is low (SkyForge bake).</summary>
    public const float DefaultOpticalSunFalloff = 2.4f;

    /// <summary>Transmittance red channel bias at high sun.</summary>
    public const float DefaultTransmittanceRedBias = 0.72f;

    /// <summary>Transmittance green channel bias at high sun.</summary>
    public const float DefaultTransmittanceGreenBias = 0.82f;

    /// <summary>Compose mix weight toward transmittance (skybox.frag).</summary>
    public const float DefaultTransmittanceMix = 0.38f;

    /// <summary>Multi-scatter add scale (skybox.frag).</summary>
    public const float DefaultMultiScatterScale = 0.22f;

    /// <summary>Sky-view add scale (skybox.frag).</summary>
    public const float DefaultSkyViewScale = 0.12f;

    /// <summary>
    /// Bakes the packed atmosphere LUT into <paramref name="destination"/> (must be at least
    /// <see cref="ByteCount"/> bytes). Matches SkyForge <c>ProceduralTextures.CreateAtmosphereLut</c>.
    /// </summary>
    public static void BakeRgba8(Span<byte> destination)
    {
        if (destination.Length < ByteCount)
            throw new ArgumentException($"Atmosphere LUT bake needs {ByteCount} bytes.", nameof(destination));

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                Vector3 colour = Vector3.Zero;
                if (x < 128 && y < 32)
                {
                    float sunHeight = x / 127f;
                    float viewHeight = y / 31f;
                    float optical = MathF.Exp(
                        -(1f - viewHeight) * (DefaultOpticalBase + (1f - sunHeight) * DefaultOpticalSunFalloff));
                    colour = new Vector3(
                        optical * (DefaultTransmittanceRedBias + sunHeight * 0.28f),
                        optical * (DefaultTransmittanceGreenBias + sunHeight * 0.18f),
                        optical);
                }
                else if (x >= 128 && x < 160 && y < 32)
                {
                    float daylight = (x - 128) / 31f;
                    float viewHeight = y / 31f;
                    colour = Vector3.Lerp(
                        new Vector3(0.015f, 0.025f, 0.07f),
                        new Vector3(0.34f, 0.49f, 0.78f),
                        daylight) * (0.35f + viewHeight * 0.65f);
                }
                else if (x < 128 && y >= 32)
                {
                    float viewUp = x / 127f;
                    float horizon = (y - 32) / 31f;
                    colour = Vector3.Lerp(
                        new Vector3(0.03f, 0.08f, 0.20f),
                        new Vector3(0.46f, 0.66f, 0.88f),
                        MathF.Pow(1f - viewUp, 0.65f)) * (0.40f + horizon * 0.60f);
                }

                int offset = (y * Width + x) * 4;
                destination[offset] = ToByte(colour.X);
                destination[offset + 1] = ToByte(colour.Y);
                destination[offset + 2] = ToByte(colour.Z);
                destination[offset + 3] = 255;
            }
        }
    }

    /// <summary>Allocates and bakes a fresh RGBA8 atlas.</summary>
    public static byte[] BakeRgba8()
    {
        byte[] pixels = new byte[ByteCount];
        BakeRgba8(pixels);
        return pixels;
    }

    /// <summary>UV for transmittance sample — <c>sat(0.5*(sunHeight+1))*0.49, viewUp*0.49</c>.</summary>
    public static Vector2 SampleUvTransmittance(float sunHeight, float viewUp) =>
        new(Saturate(0.5f * (sunHeight + 1f)) * 0.49f, Saturate(viewUp) * 0.49f);

    /// <summary>UV for multi-scatter — <c>0.505 + daylight*0.115, 0.02 + viewUp*0.46</c>.</summary>
    public static Vector2 SampleUvMultiScatter(float daylight, float viewUp) =>
        new(0.505f + Saturate(daylight) * 0.115f, 0.02f + Saturate(viewUp) * 0.46f);

    /// <summary>UV for sky-view — <c>viewUp*0.49, 0.51 + horizon*0.47</c>.</summary>
    public static Vector2 SampleUvSkyView(float viewUp, float horizon) =>
        new(Saturate(viewUp) * 0.49f, 0.51f + Saturate(horizon) * 0.47f);

    /// <summary>
    /// Nearest-texel RGB sample from a baked RGBA8 buffer (CPU gate; GPU uses bilinear clamp).
    /// </summary>
    public static Vector3 SampleBakedRgba8(ReadOnlySpan<byte> pixels, Vector2 uv)
    {
        if (pixels.Length < ByteCount)
            throw new ArgumentException($"Atmosphere LUT sample needs {ByteCount} bytes.", nameof(pixels));

        float u = Saturate(uv.X);
        float v = Saturate(uv.Y);
        int x = Math.Clamp((int)MathF.Floor(u * (Width - 1) + 0.5f), 0, Width - 1);
        int y = Math.Clamp((int)MathF.Floor(v * (Height - 1) + 0.5f), 0, Height - 1);
        int offset = (y * Width + x) * 4;
        return new Vector3(
            pixels[offset] / 255f,
            pixels[offset + 1] / 255f,
            pixels[offset + 2] / 255f);
    }

    /// <summary>
    /// skybox.frag compose: modulate <paramref name="baseSky"/> by transmittance, then add
    /// multi-scatter and sky-view. <paramref name="sampleLut"/> returns linear RGB at a UV.
    /// </summary>
    public static Vector3 ComposeSky(
        Vector3 baseSky,
        float sunHeight,
        float viewUp,
        float daylight,
        float horizon,
        Func<Vector2, Vector3> sampleLut)
    {
        ArgumentNullException.ThrowIfNull(sampleLut);

        Vector3 transmittance = sampleLut(SampleUvTransmittance(sunHeight, viewUp));
        Vector3 multiScatter = sampleLut(SampleUvMultiScatter(daylight, viewUp));
        Vector3 skyView = sampleLut(SampleUvSkyView(viewUp, horizon));

        Vector3 sky = baseSky;
        sky *= Vector3.Lerp(new Vector3(0.82f), transmittance * 1.18f, DefaultTransmittanceMix);
        sky += multiScatter * daylight * DefaultMultiScatterScale + skyView * DefaultSkyViewScale;
        return sky;
    }

    /// <summary>
    /// Compose using a baked RGBA8 atlas. Convenience for the headless gate.
    /// </summary>
    public static Vector3 ComposeSky(
        Vector3 baseSky,
        float sunHeight,
        float viewUp,
        float daylight,
        float horizon,
        ReadOnlySpan<byte> bakedRgba8)
    {
        // Copy span — cannot capture a ref-like type in the ComposeSky sampler lambda.
        byte[] baked = bakedRgba8.ToArray();
        return ComposeSky(
            baseSky,
            sunHeight,
            viewUp,
            daylight,
            horizon,
            uv => SampleBakedRgba8(baked, uv));
    }

    /// <summary>
    /// Daylight factor matching skybox.frag: <c>saturate((sunHeight + 0.08) / 0.35)</c>.
    /// </summary>
    public static float DaylightFromSunHeight(float sunHeight) =>
        Saturate((sunHeight + 0.08f) / 0.35f);

    /// <summary>
    /// Sun height from Mesh3DState light travel direction (from sun toward scene):
    /// <c>-LightDirection.Y</c>.
    /// </summary>
    public static float SunHeightFromLightDirection(Vector3 lightDirection) =>
        -lightDirection.Y;

    private static float Saturate(float value) => Math.Clamp(value, 0f, 1f);

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
