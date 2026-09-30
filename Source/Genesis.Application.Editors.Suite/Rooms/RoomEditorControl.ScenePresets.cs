using Genesis.Application.Core.UI;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    /// <summary>One-click moods for a 3D Room: sky, time of day, weather and clouds together.</summary>
    /// <remarks>
    /// The Sky page exposes more than a dozen separate environment fields (clock, time scale,
    /// atmosphere, weather, automatic weather, clouds, haze…). Getting "a sunny afternoon" meant
    /// knowing which five of them to change. A preset writes them all as one undoable edit; the
    /// fields below stay available for fine-tuning. Presets hold the clock still (time scale 0) so
    /// the Room looks in play the way it looked when it was chosen.
    /// </remarks>
    private sealed record ScenePreset(
        string Id,
        string Title,
        string Description,
        string Glyph,
        string Swatch,
        Action<RoomEnvironment> Apply);

    private static readonly ScenePreset[] ScenePresets =
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

    private StarterGallery? _scenePresets;

    /// <summary>The Sky page's scene preset cards.</summary>
    public StarterGallery? ScenePresetGallery => _scenePresets;

    private void AddScenePresets(System.Windows.Forms.Panel skybox)
    {
        _scenePresets = new StarterGallery("RoomScenePresets")
        {
            Compact = true,
            Dock = System.Windows.Forms.DockStyle.Top,
            FitsContent = true,
            Subheading = "One click sets the sky, time of day and weather. Fine-tune them below.",
        };
        _scenePresets.SetItems(ScenePresets.Select(preset => new StarterItem(preset.Id, preset.Title, preset.Description)
        {
            Glyph = preset.Glyph,
            Swatch = UiTokens.FromHex(preset.Swatch),
        }));
        _scenePresets.ItemChosen += (_, item) => ApplyScenePreset(item.Id);
        skybox.Controls.Add(_scenePresets);

        // Top-docked siblings stack in reverse z-order: sending the gallery to the back docks it
        // first, above the Sky and time, Weather and Clouds groups.
        _scenePresets.SendToBack();
    }

    /// <summary>Applies a scene preset as one undoable edit. Returns false for an unknown id.</summary>
    public bool ApplyScenePreset(string id)
    {
        ScenePreset? preset = ScenePresets.FirstOrDefault(candidate => candidate.Id == id);
        if (preset is null) return false;
        RoomEnvironment previous = CloneEnvironment(_room.Environment);
        RoomEnvironment next = CloneEnvironment(_room.Environment);
        preset.Apply(next);
        ApplyEnvironment(next);
        PushEdit("Scene: " + preset.Title, () => ApplyEnvironment(next), () => ApplyEnvironment(previous));
        if (_scenePresets is not null) _scenePresets.SelectedId = id;
        return true;
    }

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
