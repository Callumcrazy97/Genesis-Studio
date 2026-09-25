using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Genesis.Rendering.Core;

/// <summary>
/// Engine-level fog defaults shared by Studio previews and the Player. Start/end remain the
/// persisted/runtime representation for backward compatibility. Designer UI exposes the same pair
/// as Depth + Thickness: Depth == Start and Thickness == End - Start.
/// </summary>
public readonly record struct EngineFogDefaults(
    bool Enabled,
    Vector4 Color,
    float Start,
    float End)
{
    public float Depth => Start;
    public float Thickness => Math.Max(0.001f, End - Start);
    public float Alpha => Math.Clamp(Color.W, 0f, 1f);
}

public static class EngineRenderingDefaults
{
    public const string WaterReflectionsEnvironmentVariable = "GENESIS_WATER_REFLECTIONS";
    /// <summary>Optional half-resolution planar water pass; software retains the analytic sky fallback.</summary>
    public static bool WaterReflections { get; set; }
    public const string FogEnabledEnvironmentVariable = "GENESIS_FOG_ENABLED";
    public const string FogColorEnvironmentVariable = "GENESIS_FOG_COLOR";
    public const string FogStartEnvironmentVariable = "GENESIS_FOG_START";
    public const string FogEndEnvironmentVariable = "GENESIS_FOG_END";
    public const string FogAlphaEnvironmentVariable = "GENESIS_FOG_ALPHA";

    /// <summary>Set by F5 from the project's own settings, read by the player at startup.</summary>
    public const string AllowEscapeEnvironmentVariable = "GENESIS_ALLOW_ESCAPE";

    private static EngineFogDefaults _fog = new(
        false,
        new Vector4(0.62f, 0.78f, 0.94f, 1f),
        35f,
        120f);

    private static bool _environmentRead;

    public static event Action Changed;
    public static EngineFogDefaults Fog => _fog;

    /// <summary>Legacy start/end configuration retained for existing callers and saved settings.</summary>
    public static void ConfigureFog(bool enabled, string colorHex, float start, float end)
        => ConfigureFog(enabled, colorHex, start, end, _fog.Alpha);

    public static void ConfigureFog(bool enabled, string colorHex, float start, float end, float alpha)
    {
        float safeStart = start;
        float safeEnd = Math.Max(safeStart + 0.001f, end);
        Vector4 rgb = ParseHexColor(colorHex, _fog.Color);
        Vector4 color = new(rgb.X, rgb.Y, rgb.Z, Math.Clamp(alpha, 0f, 1f));
        EngineFogDefaults next = new(enabled, color, safeStart, safeEnd);
        if (next.Equals(_fog)) return;
        _fog = next;
        Changed?.Invoke();
    }

    /// <summary>Preferred designer-facing configuration: depth + transition thickness + max alpha.</summary>
    public static void ConfigureFogDepth(bool enabled, string colorHex, float depth, float thickness, float alpha)
        => ConfigureFog(enabled, colorHex, depth, depth + Math.Max(0.001f, thickness), alpha);

    public static void ApplyEnvironmentOverrides()
    {
        if (_environmentRead) return;
        _environmentRead = true;
        string reflections = Environment.GetEnvironmentVariable(WaterReflectionsEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(reflections))
            WaterReflections = reflections == "1" || bool.TryParse(reflections, out bool enabledReflections) && enabledReflections;

        bool enabled = _fog.Enabled;
        string color = ToHexColor(_fog.Color);
        float start = _fog.Start;
        float end = _fog.End;
        float alpha = _fog.Alpha;

        string enabledValue = Environment.GetEnvironmentVariable(FogEnabledEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(enabledValue))
            enabled = enabledValue == "1" || bool.TryParse(enabledValue, out bool parsed) && parsed;

        string colorValue = Environment.GetEnvironmentVariable(FogColorEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(colorValue)) color = colorValue;

        TryEnvironmentFloat(FogStartEnvironmentVariable, ref start);
        TryEnvironmentFloat(FogEndEnvironmentVariable, ref end);
        TryEnvironmentFloat(FogAlphaEnvironmentVariable, ref alpha);
        ConfigureFog(enabled, color, start, end, alpha);
    }

    public static Dictionary<string, string> BuildEnvironment(RenderBackendOption backend)
        => BuildEnvironment(backend, _fog.Enabled, ToHexColor(_fog.Color), _fog.Start, _fog.End, _fog.Alpha);

    public static Dictionary<string, string> BuildEnvironment(
        RenderBackendOption backend,
        bool fogEnabled,
        string fogColorHex,
        float fogStart,
        float fogEnd)
        => BuildEnvironment(backend, fogEnabled, fogColorHex, fogStart, fogEnd, _fog.Alpha);

    public static Dictionary<string, string> BuildEnvironment(
        RenderBackendOption backend,
        bool fogEnabled,
        string fogColorHex,
        float fogStart,
        float fogEnd,
        float fogAlpha)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [RenderBackendSelection.EnvironmentVariable] = RenderBackendSelection.ToEnvironmentValue(backend),
            [WaterReflectionsEnvironmentVariable] = WaterReflections ? "1" : "0",
            [FogEnabledEnvironmentVariable] = fogEnabled ? "1" : "0",
            [FogColorEnvironmentVariable] = NormalizeHex(fogColorHex),
            [FogStartEnvironmentVariable] = fogStart.ToString(CultureInfo.InvariantCulture),
            [FogEndEnvironmentVariable] = fogEnd.ToString(CultureInfo.InvariantCulture),
            [FogAlphaEnvironmentVariable] = Math.Clamp(fogAlpha, 0f, 1f).ToString(CultureInfo.InvariantCulture),
        };
    }

    public static string ToHexColor(Vector4 color)
    {
        int r = (int)MathF.Round(Math.Clamp(color.X, 0f, 1f) * 255f);
        int g = (int)MathF.Round(Math.Clamp(color.Y, 0f, 1f) * 255f);
        int b = (int)MathF.Round(Math.Clamp(color.Z, 0f, 1f) * 255f);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static Vector4 ParseHexColor(string value, Vector4 fallback)
    {
        string hex = NormalizeHex(value);
        if (hex.Length != 7) return fallback;
        if (!byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            return fallback;
        return new Vector4(r / 255f, g / 255f, b / 255f, fallback.W);
    }

    private static string NormalizeHex(string value)
    {
        string hex = string.IsNullOrWhiteSpace(value) ? "#9EC7F0" : value.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        return hex.ToUpperInvariant();
    }

    private static void TryEnvironmentFloat(string name, ref float value)
    {
        string text = Environment.GetEnvironmentVariable(name);
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            value = parsed;
    }
}
