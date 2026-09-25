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

    private static AtmosphereOptions ResolveAtmosphere() => ActiveSkyScene?.Atmosphere?.Options;

    private static EnvironmentOptions ResolveClimate() => ActiveSkyScene?.Climate?.Options;
}
