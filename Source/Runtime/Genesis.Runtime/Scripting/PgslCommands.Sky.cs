using System;
using Genesis.Runtime.Climate;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// AF2.6 <c>Engine.Sky</c> authoring. Mutates active <see cref="RuntimeScene"/> Climate /
/// Atmosphere options when a game is bound; otherwise updates <see cref="SkyAuthoringDefaults"/>
/// so headless gates and Mesh3DState stamps still round-trip without a live Player.
/// </summary>
public static partial class PgslCommands
{
    private static RuntimeScene ActiveSkyScene => ActiveGameContext?.Scene;

    [PgslCommand("Coverage", "Engine.Sky.Coverage",
        "Cloud coverage scale (weather map × FogVolumes)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyCoverage
    {
        get => ResolveAtmosphere()?.CloudCoverageScale ?? SkyAuthoringDefaults.CloudCoverageScale;
        set
        {
            float clamped = SkyAuthoringDefaults.ClampCoverageScale(value);
            SkyAuthoringDefaults.CloudCoverageScale = clamped;
            AtmosphereOptions atmosphere = ResolveAtmosphere();
            if (atmosphere != null)
                atmosphere.CloudCoverageScale = clamped;
        }
    }

    [PgslCommand("Density", "Engine.Sky.Density",
        "Cloud density scale (raymarch intensity multiplier)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyDensity
    {
        get => ResolveAtmosphere()?.CloudDensityScale ?? SkyAuthoringDefaults.CloudDensityScale;
        set
        {
            float clamped = SkyAuthoringDefaults.ClampDensityScale(value);
            SkyAuthoringDefaults.CloudDensityScale = clamped;
            AtmosphereOptions atmosphere = ResolveAtmosphere();
            if (atmosphere != null)
                atmosphere.CloudDensityScale = clamped;
        }
    }

    [PgslCommand("SunSize", "Engine.Sky.SunSize",
        "How large the sun's disc is drawn against its usual size (0.25 to 8)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkySunSize
    {
        get => ResolveAtmosphere()?.SunDiscScale ?? 1f;
        set
        {
            AtmosphereOptions atmosphere = ResolveAtmosphere();
            if (atmosphere != null && float.IsFinite(value)) atmosphere.SunDiscScale = Math.Clamp(value, 0.25f, 8f);
        }
    }

    [PgslCommand("Altitude", "Engine.Sky.Altitude",
        "Cloud slab base height in metres", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyAltitude
    {
        get => ResolveAtmosphere()?.CloudBaseHeight ?? SkyAuthoringDefaults.CloudBaseHeight;
        set
        {
            float clamped = SkyAuthoringDefaults.ClampBaseHeight(value);
            SkyAuthoringDefaults.CloudBaseHeight = clamped;
            AtmosphereOptions atmosphere = ResolveAtmosphere();
            if (atmosphere != null)
                atmosphere.CloudBaseHeight = clamped;
        }
    }

    [PgslCommand("Thickness", "Engine.Sky.Thickness",
        "Cloud slab thickness in metres", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyThickness
    {
        get => ResolveAtmosphere()?.CloudThickness ?? SkyAuthoringDefaults.CloudThickness;
        set
        {
            float clamped = SkyAuthoringDefaults.ClampThickness(value);
            SkyAuthoringDefaults.CloudThickness = clamped;
            AtmosphereOptions atmosphere = ResolveAtmosphere();
            if (atmosphere != null)
                atmosphere.CloudThickness = clamped;
        }
    }

    [PgslCommand("Quality", "Engine.Sky.Quality",
        "Cloud quality 0..3 (alias Engine.Rendering.CloudQuality)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static int SkyQuality
    {
        get => CloudQuality;
        set => CloudQuality = value;
    }

    [PgslCommand("Latitude", "Engine.Sky.Latitude",
        "Observer latitude in degrees (celestial extras)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyLatitude
    {
        get => ResolveClimate()?.LatitudeDegrees ?? SkyAuthoringDefaults.LatitudeDegrees;
        set
        {
            float clamped = SkyAuthoringDefaults.ClampLatitude(value);
            SkyAuthoringDefaults.LatitudeDegrees = clamped;
            EnvironmentOptions climate = ResolveClimate();
            if (climate != null)
                climate.LatitudeDegrees = clamped;
        }
    }

    [PgslCommand("DayOfYear", "Engine.Sky.DayOfYear",
        "Calendar day-of-year (celestial extras / moon phase)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyDayOfYear
    {
        get => ResolveClimate()?.DayOfYear ?? SkyAuthoringDefaults.DayOfYear;
        set
        {
            float clamped = SkyAuthoringDefaults.ClampDayOfYear(value);
            SkyAuthoringDefaults.DayOfYear = clamped;
            EnvironmentOptions climate = ResolveClimate();
            if (climate != null)
                climate.DayOfYear = (int)MathF.Round(clamped);
        }
    }

    [PgslCommand("RaymarchedCloudsEnabled", "Engine.Sky.RaymarchedCloudsEnabled",
        "Enable raymarched clouds (alias Engine.Rendering)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static bool SkyRaymarchedCloudsEnabled
    {
        get => RaymarchedCloudsEnabled;
        set => RaymarchedCloudsEnabled = value;
    }

    [PgslCommand("TimeOfDay", "Engine.Sky.TimeOfDay",
        "Time of day in hours (0..24)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyTimeOfDay
    {
        get
        {
            EnvironmentService climate = ActiveSkyScene?.Climate;
            return climate != null ? climate.TimeOfDayHours : SkyAuthoringDefaults.TimeOfDayHours;
        }
        set
        {
            float hours = value;
            while (hours < 0f) hours += 24f;
            while (hours >= 24f) hours -= 24f;
            SkyAuthoringDefaults.TimeOfDayHours = hours;
            EnvironmentService climate = ActiveSkyScene?.Climate;
            climate?.SetTimeOfDay(hours);
        }
    }

    [PgslCommand("SetCloudCoverage", "Engine.Sky.SetCloudCoverage",
        "Set cloud coverage scale (alias Coverage)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static void SetCloudCoverage(float coverage) => SkyCoverage = coverage;

    [PgslCommand("SetLatitude", "Engine.Sky.SetLatitude",
        "Set observer latitude in degrees (alias Latitude)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static void SetLatitude(float latitudeDegrees) => SkyLatitude = latitudeDegrees;

    [PgslCommand("SetTimeOfDay", "Engine.Sky.SetTimeOfDay",
        "Set time of day in hours (alias TimeOfDay)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static void SetTimeOfDay(float hours) => SkyTimeOfDay = hours;

    [PgslCommand("Haze", "Engine.Sky.Haze",
        "Atmospheric haze of the room's sky, 0 to 1, as the Room's Haze; changes with the hour for a hazy morning", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyHaze
    {
        get => ResolveAtmosphere()?.Haze ?? 0f;
        set
        {
            AtmosphereOptions atmosphere = ResolveAtmosphere();
            if (atmosphere != null && float.IsFinite(value)) atmosphere.Haze = Math.Clamp(value, 0f, 1f);
        }
    }

    [PgslCommand("Visibility", "Engine.Sky.Visibility",
        "How far one can see through the atmosphere's fog, in metres (0 = the preset's own)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyVisibility
    {
        get => ResolveAtmosphere()?.VisibilityMetres ?? 0f;
        set
        {
            AtmosphereOptions atmosphere = ResolveAtmosphere();
            if (atmosphere != null && float.IsFinite(value)) atmosphere.VisibilityMetres = Math.Clamp(value, 0f, 1_000_000f);
        }
    }

    [PgslCommand("FogScale", "Engine.Sky.FogScale",
        "Scales the fog the weather brings, 0 to 4 (1 = as the weather has it)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyFogScale
    {
        get => ResolveAtmosphere()?.WeatherFogScale ?? 1f;
        set
        {
            AtmosphereOptions atmosphere = ResolveAtmosphere();
            if (atmosphere != null && float.IsFinite(value)) atmosphere.WeatherFogScale = Math.Clamp(value, 0f, 4f);
        }
    }

    [PgslCommand("AmbientScale", "Engine.Sky.AmbientScale",
        "Scales the sky's ambient light, 0 to 8 (1 = as the preset has it)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static float SkyAmbientScale
    {
        get => ResolveAtmosphere()?.AmbientScale ?? 1f;
        set
        {
            AtmosphereOptions atmosphere = ResolveAtmosphere();
            if (atmosphere != null && float.IsFinite(value)) atmosphere.AmbientScale = Math.Clamp(value, 0f, 8f);
        }
    }

    [PgslCommand("SetHaze", "Engine.Sky.SetHaze", "Set the atmospheric haze, 0 to 1 (alias Haze)", "Engine · Sky",
        Namespace = "Engine.Sky")]
    public static void SetHaze(float amount) => SkyHaze = amount;

    [PgslCommand("SetAtmospherePreset", "Engine.Sky.SetAtmospherePreset",
        "Switch the room's atmosphere: Natural, ClearDay, GoldenHour, Overcast, Storm, Night or Alien. False for an unknown name",
        "Engine · Sky", Namespace = "Engine.Sky")]
    public static bool SetAtmospherePreset(string preset)
    {
        AtmosphereOptions atmosphere = ResolveAtmosphere();
        string name = (preset ?? string.Empty).Replace(" ", string.Empty);
        if (atmosphere == null || int.TryParse(name, out _) || !Enum.TryParse(name, true, out AtmospherePreset parsed)
            || !Enum.IsDefined(parsed)) return false;
        atmosphere.Preset = parsed;
        return true;
    }

    [PgslCommand("AtmospherePreset", "Engine.Sky.AtmospherePreset", "The room's atmosphere preset by name (empty with no sky)",
        "Engine · Sky", Namespace = "Engine.Sky")]
    public static string SkyAtmospherePreset => ResolveAtmosphere()?.Preset.ToString() ?? string.Empty;

    private static AtmosphereOptions ResolveAtmosphere() => ActiveSkyScene?.Atmosphere?.Options;

    private static EnvironmentOptions ResolveClimate() => ActiveSkyScene?.Climate?.Options;
}
