using System.Numerics;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private EnvironmentService? _environmentPreviewClimate;
    private AtmosphereService? _environmentPreviewAtmosphere;
    private readonly WeatherMapService _environmentPreviewWeatherMap = new();
    private EnvironmentPreviewSettings? _environmentPreviewSettings;
    private Vector3 _environmentPreviewObserver;
    private float _environmentPreviewRoomTime;
    private byte[] _environmentPreviewWeatherPixels = [];

    public EnvironmentFrame? EnvironmentPreviewFrame => _environmentPreviewClimate?.Current;
    public AtmosphereFrame? AtmospherePreviewFrame => _environmentPreviewAtmosphere?.Current;
    public int EnvironmentPreviewEvaluationCount { get; private set; }
    public int EnvironmentWeatherMapGenerationCount { get; private set; }
    public int SubmittedEnvironmentCloudCount { get; private set; }
    public bool SubmittedEnvironmentWeatherMap { get; private set; }

    private readonly record struct EnvironmentPreviewSettings(
        int Seed, float Hours, float TimeScale, int Day, float Latitude, WeatherKind Weather,
        bool AutomaticWeather, AtmospherePreset Preset, bool Clouds, int Quality,
        float BaseHeight, float Thickness, float Coverage, float Haze)
    {
        public static EnvironmentPreviewSettings From(RoomEnvironment source) => new(
            source.ClimateSeed, source.TimeOfDayHours, source.TimeScale,
            (int)SkyAuthoringDefaults.ClampDayOfYear(source.DayOfYear),
            SkyAuthoringDefaults.ClampLatitude(source.LatitudeDegrees),
            Enum.TryParse(source.Weather, true, out WeatherKind weather) ? weather : WeatherKind.Clear,
            source.AutomaticWeather,
            Enum.TryParse(source.AtmospherePreset, true, out AtmospherePreset preset) ? preset : AtmospherePreset.Natural,
            source.VolumetricClouds, Math.Clamp(source.CloudQuality, 0, 3),
            SkyAuthoringDefaults.ClampBaseHeight(source.CloudBaseHeight),
            SkyAuthoringDefaults.ClampThickness(source.CloudThickness),
            SkyAuthoringDefaults.ClampCoverageScale(source.CloudCoverageScale),
            Math.Clamp(source.AtmosphericHaze, 0f, 1f));
    }

    private void UpdateRoomEnvironmentPreview()
    {
        if (_room.Environment is not { DynamicSky: true } environment)
        {
            _environmentPreviewClimate = null;
            _environmentPreviewAtmosphere = null;
            _environmentPreviewSettings = null;
            SubmittedEnvironmentCloudCount = 0;
            SubmittedEnvironmentWeatherMap = false;
            return;
        }

        EnvironmentPreviewSettings settings = EnvironmentPreviewSettings.From(environment);
        Vector3 observer = _viewport.CameraOverrideFactory?.Invoke().Eye ?? _viewport.Camera.Eye;
        bool changed = settings != _environmentPreviewSettings;
        float delta = _roomTime - _environmentPreviewRoomTime;
        bool rewind = delta < 0f;
        if (!changed && delta == 0f && observer == _environmentPreviewObserver) return;

        bool restart = _environmentPreviewClimate is null || _environmentPreviewSettings?.Seed != settings.Seed || rewind;
        if (restart)
        {
            _environmentPreviewClimate = new EnvironmentService(new EnvironmentOptions
            {
                Seed = settings.Seed, StartTimeHours = settings.Hours, TimeScale = settings.TimeScale,
                DayOfYear = settings.Day, LatitudeDegrees = settings.Latitude,
                AutomaticWeather = settings.AutomaticWeather,
            });
            if (!settings.AutomaticWeather) _environmentPreviewClimate.SetWeather(settings.Weather, 0f);
            // Rewinding transport returns to the authored start. Opening/changing the room
            // starts its sky at that start time, regardless of the previous room's clock.
            delta = rewind ? MathF.Max(0f, _roomTime) : 0f;
        }

        EnvironmentService climate = _environmentPreviewClimate!;
        if (changed && !restart)
        {
            climate.Options.StartTimeHours = settings.Hours;
            climate.Options.TimeScale = settings.TimeScale;
            climate.Options.DayOfYear = settings.Day;
            climate.Options.LatitudeDegrees = settings.Latitude;
            if (_environmentPreviewSettings?.Hours != settings.Hours) climate.SetTimeOfDay(settings.Hours);
            if (_environmentPreviewSettings?.AutomaticWeather != settings.AutomaticWeather
                || _environmentPreviewSettings?.Weather != settings.Weather)
            {
                if (settings.AutomaticWeather) climate.ResumeAutomaticWeather();
                else climate.SetWeather(settings.Weather, 0f);
            }
        }

        // Paused repaint and camera orbit use dt=0; they never advance the climate's clock.
        // Explicit large transport seeks restore a snapshot rather than iterating thousands of frames.
        if (delta > 8f)
        {
            EnvironmentPersistenceState previous = climate.CaptureState();
            climate.RestoreState(previous with { ElapsedRealSeconds = previous.ElapsedRealSeconds + delta });
            delta = 0f;
        }
        while (delta > 1f)
        {
            climate.Update(1f, observer);
            delta -= 1f;
        }
        EnvironmentFrame frame = climate.Update(MathF.Max(0f, delta), observer);
        _environmentPreviewAtmosphere ??= new AtmosphereService();
        AtmosphereOptions options = _environmentPreviewAtmosphere.Options;
        options.Preset = settings.Preset;
        options.VolumetricClouds = settings.Clouds;
        options.CloudQuality = settings.Quality;
        options.CloudBaseHeight = settings.BaseHeight;
        options.CloudThickness = settings.Thickness;
        options.CloudCoverageScale = settings.Coverage;
        options.CloudDensityScale = SkyAuthoringDefaults.CloudDensityScale;
        options.Haze = settings.Haze;
        options.Seed = settings.Seed;
        _environmentPreviewAtmosphere.Update(frame, observer);
        EnvironmentPreviewEvaluationCount++;

        bool mapRegionChanged = _environmentPreviewWeatherMap.ConfigureViewCoverage(observer, settings.BaseHeight, settings.Thickness);
        if (changed || restart || mapRegionChanged || _roomTime != _environmentPreviewRoomTime)
        {
            _environmentPreviewWeatherMap.Update(frame, settings.Seed);
            int bytes = _environmentPreviewWeatherMap.Resolution * _environmentPreviewWeatherMap.Resolution * 4;
            if (_environmentPreviewWeatherPixels.Length != bytes) _environmentPreviewWeatherPixels = new byte[bytes];
            _environmentPreviewWeatherMap.TryCopyRgba8(_environmentPreviewWeatherPixels, out _, out _, out _);
            EnvironmentWeatherMapGenerationCount++;
        }
        _environmentPreviewRoomTime = _roomTime;
        _environmentPreviewObserver = observer;
        _environmentPreviewSettings = settings;
    }

    private void ApplyRoomEnvironmentPreview(ref Mesh3DState state)
    {
        UpdateRoomEnvironmentPreview();
        EnvironmentMapper.StampClimateAtmosphere(ref state, _environmentPreviewClimate!, _environmentPreviewAtmosphere!);
    }

    private void SubmitRoomEnvironment(IRenderController renderer)
    {
        SubmittedEnvironmentCloudCount = 0;
        SubmittedEnvironmentWeatherMap = false;
        if (_room.Environment is not { DynamicSky: true }
            || _environmentPreviewAtmosphere is not { } atmosphere) return;
        // Use the snapshot that produced this frame's lighting state. The same cloud
        // volumes and weather-map contract are submitted by RuntimeScene for F5.
        if (atmosphere.Current.CloudVolumes is { } clouds)
        {
            foreach (FogVolume cloud in clouds) renderer.AddFogVolume(cloud);
            SubmittedEnvironmentCloudCount = clouds.Count;
        }
        if (atmosphere.Options.VolumetricClouds && _environmentPreviewWeatherPixels.Length > 0)
        {
            int resolution = _environmentPreviewWeatherMap.Resolution;
            renderer.SetWeatherMapRgba8(_environmentPreviewWeatherPixels, resolution, resolution,
                _environmentPreviewWeatherMap.WorldHalfExtent, _environmentPreviewWeatherMap.WorldCenter);
            SubmittedEnvironmentWeatherMap = true;
        }
    }
}
