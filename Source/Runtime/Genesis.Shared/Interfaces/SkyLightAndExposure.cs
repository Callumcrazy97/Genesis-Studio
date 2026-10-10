using System;

namespace Genesis.Shared.Interfaces;

/// <summary>
/// How the sky lights the surfaces the sun does not reach (<see cref="Mesh3DState.SkyLightMode"/>).
/// </summary>
public static class SkyLightModes
{
    /// <summary>The ambient colour as the room, sky or script gives it (the zenith colour under a dynamic sky). The default.</summary>
    public const int Zenith = 0;

    /// <summary>
    /// The light of the whole sky dome as the engine draws it (bright horizon and blue zenith) and of
    /// the sunlit ground below, worked out whenever the sky changes.
    /// </summary>
    public const int Hemisphere = 1;

    /// <summary>"zenith" or "hemisphere" (any case); false for anything else.</summary>
    public static bool TryParse(string name, out int mode)
    {
        switch ((name ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "zenith": case "off": case "legacy": mode = Zenith; return true;
            case "hemisphere": case "sky": case "dome": mode = Hemisphere; return true;
            default: mode = Zenith; return false;
        }
    }

    /// <summary>The mode's name as a room file writes it.</summary>
    public static string Name(int mode) => mode == Hemisphere ? "hemisphere" : "zenith";
}

/// <summary>Defaults of the hemisphere sky light.</summary>
public static class SkyLightDefaults
{
    /// <summary>The sky as drawn.</summary>
    public const float Strength = 1f;

    /// <summary>
    /// Four tenths of the sky's colour. The light of a real clear sky is much paler than its blue
    /// (much of it comes from the bright haze near the horizon); at 0.4 the sunlit ground of the
    /// engine's clear day comes out within a few percent of neutral in either colour pipeline,
    /// rather than blue.
    /// </summary>
    public const float Saturation = 0.4f;

    public static float ClampStrength(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 16f) : Strength;
    public static float ClampSaturation(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 2f) : Saturation;
    public static float ClampTint(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 4f) : 1f;
}

/// <summary>Defaults and limits of eye adaptation (auto exposure).</summary>
public static class AutoExposureDefaults
{
    /// <summary>Mid grey: an average scene is shown as a mid grey surface would be.</summary>
    public const float Key = 0.18f;

    /// <summary>Stepping out into daylight: the picture darkens most of the way in this many seconds.</summary>
    public const float DarkenSeconds = 0.8f;

    /// <summary>Stepping into the dark: the picture brightens most of the way in this many seconds.</summary>
    public const float BrightenSeconds = 2.5f;

    /// <summary>At most four stops darker (a sixteenth).</summary>
    public const float MinEv = -4f;

    /// <summary>At most four stops brighter (sixteen times).</summary>
    public const float MaxEv = 4f;

    /// <summary>The centre of the picture counts somewhat more than its edges.</summary>
    public const float CenterWeight = 0.5f;

    public static float ClampKey(float value) => float.IsFinite(value) && value > 0f ? Math.Clamp(value, 0.01f, 4f) : Key;
    public static float ClampSeconds(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 600f) : DarkenSeconds;
    public static float ClampEv(float value) => float.IsFinite(value) ? Math.Clamp(value, -16f, 16f) : 0f;
    public static float ClampCenterWeight(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : CenterWeight;
}
