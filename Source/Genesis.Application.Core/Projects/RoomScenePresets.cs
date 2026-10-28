using Genesis.Application.Core.UI;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Core.Projects;

/// <summary>A one-click mood for a 3D Room: sky, time of day, weather and clouds together.</summary>
public sealed record RoomScenePreset(
    string Id,
    string Title,
    string Description,
    string Glyph,
    string Swatch,
    Action<RoomEnvironment> Apply);

/// <summary>
/// The scene presets the Room editor's Sky page offers and automation's room.create and
/// room.setEnvironment take: one list, so a card and a command give the same room.
/// </summary>
/// <remarks>
/// The Sky page exposes more than a dozen separate environment fields (clock, time scale,
/// atmosphere, weather, automatic weather, clouds, haze…). Getting "a sunny afternoon" meant
/// knowing which five of them to change. A preset writes them all; the fields stay available for
/// fine-tuning. Presets hold the clock still (time scale 0) so the Room looks in play the way it
/// looked when it was chosen.
/// </remarks>
public static class RoomScenePresets
{
    /// <summary>What a new 3D room made by automation starts with, so it has a sky rather than a dark void.</summary>
    public const string DefaultOutdoor = "SunnyDay";

    public static IReadOnlyList<RoomScenePreset> All { get; } =
    [
        new("SunnyDay", "Sunny day", "Midday sun, a few clouds, clear air.", UiGlyphs.Sun, "#F2C14B", environment =>
            Sky(environment, 12.5f, WeatherKind.Clear, AtmospherePreset.ClearDay, coverage: 0.6f, haze: 0.12f)),
        new("GoldenHour", "Golden hour", "Low, warm evening light and long shadows.", UiGlyphs.Sunset, "#F28C4B", environment =>
            Sky(environment, 18.6f, WeatherKind.Clear, AtmospherePreset.GoldenHour, coverage: 0.8f, haze: 0.25f)),
        new("Overcast", "Overcast", "Soft, even light under a grey sky.", UiGlyphs.Cloud, "#8FA3B8", environment =>
            Sky(environment, 13f, WeatherKind.Overcast, AtmospherePreset.Overcast, coverage: 1.6f, haze: 0.3f)),
        new("Stormy", "Stormy", "Dark clouds, rain and thunder.", UiGlyphs.Lightning, "#6C7A99", environment =>
            Sky(environment, 16f, WeatherKind.Thunderstorm, AtmospherePreset.Storm, coverage: 2f, haze: 0.35f)),
        new("Snowy", "Snowy", "Falling snow and cold, hazy light.", UiGlyphs.Snow, "#CFE3F2", environment =>
            Sky(environment, 11f, WeatherKind.Snow, AtmospherePreset.Overcast, coverage: 1.4f, haze: 0.3f)),
        new("Night", "Night", "Moonlight and stars.", UiGlyphs.Moon, "#3F4C8C", environment =>
            Sky(environment, 23.5f, WeatherKind.Clear, AtmospherePreset.Night, coverage: 0.6f, haze: 0.1f)),
        new("Alien", "Alien world", "A strange sky for another planet.", UiGlyphs.Globe, "#B07CF2", environment =>
            Sky(environment, 15f, WeatherKind.Clear, AtmospherePreset.Alien, coverage: 0.9f, haze: 0.2f)),
        new("Indoor", "Indoor", "No sky or weather: lights and the background colour only.", UiGlyphs.Home, "#9DA6BB", environment =>
        {
            environment.DynamicSky = false;
            environment.AutomaticWeather = false;
            environment.Weather = WeatherKind.Clear.ToString();
            environment.VolumetricClouds = false;
            environment.TimeScale = 0f;
        }),
    ];

    /// <summary>The preset with this id (any case), or null.</summary>
    public static RoomScenePreset? Find(string? id) =>
        All.FirstOrDefault(preset => preset.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static void Sky(RoomEnvironment environment, float hours, WeatherKind weather, AtmospherePreset atmosphere,
        float coverage, float haze)
    {
        environment.DynamicSky = true;
        environment.AutomaticWeather = false;
        environment.TimeOfDayHours = hours;
        environment.TimeScale = 0f;
        environment.Weather = weather.ToString();
        environment.AtmospherePreset = atmosphere.ToString();
        environment.VolumetricClouds = true;
        environment.CloudCoverageScale = coverage;
        environment.AtmosphericHaze = haze;
    }
}
