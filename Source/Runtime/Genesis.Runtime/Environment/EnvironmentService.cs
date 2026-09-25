using System;
using System.Numerics;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Scene;

namespace Genesis.Runtime.Climate;

public enum WeatherKind
{
    Clear,
    Overcast,
    Wind,
    Rain,
    Thunderstorm,
    Snow,
    Hail,
    Fog,
    ColdSnap,
    Heatwave,
}

/// <summary>Interpolated weather values consumed by rendering, particles, audio, and gameplay.</summary>
public readonly record struct WeatherState(
    WeatherKind Kind,
    float CloudCover,
    float Rain,
    float Snow,
    float Hail,
    Vector3 WindDirection,
    float WindSpeed,
    float TemperatureC,
    float FogDensity,
    float Lightning)
{
    public float Precipitation => Math.Clamp(Rain + Snow + Hail, 0f, 1f);

    internal static WeatherState Blend(in WeatherState from, in WeatherState to, float amount)
    {
        float t = Math.Clamp(amount, 0f, 1f);
        Vector3 wind = Vector3.Lerp(from.WindDirection, to.WindDirection, t);
        if (wind.LengthSquared() > 1e-6f) wind = Vector3.Normalize(wind);
        return new WeatherState(
            t < 0.5f ? from.Kind : to.Kind,
            Lerp(from.CloudCover, to.CloudCover, t),
            Lerp(from.Rain, to.Rain, t),
            Lerp(from.Snow, to.Snow, t),
            Lerp(from.Hail, to.Hail, t),
            wind,
            Lerp(from.WindSpeed, to.WindSpeed, t),
            Lerp(from.TemperatureC, to.TemperatureC, t),
            Lerp(from.FogDensity, to.FogDensity, t),
            Lerp(from.Lightning, to.Lightning, t));
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>Local shelter/altitude information supplied by the active world implementation.</summary>
public readonly record struct EnvironmentLocalSample(
    float RainOcclusion,
    float WindOcclusion,
    float AltitudeMeters,
    float WaterDepth,
    bool IsIndoors)
{
    public static EnvironmentLocalSample Open(Vector3 observer) =>
        new(0f, 0f, observer.Y, 0f, false);
}

public interface IEnvironmentQuery
{
    EnvironmentLocalSample Sample(Vector3 observerPosition, Vector3 windDirection);
}

public sealed class OpenEnvironmentQuery : IEnvironmentQuery
{
    public static OpenEnvironmentQuery Instance { get; } = new();
    private OpenEnvironmentQuery() { }
    public EnvironmentLocalSample Sample(Vector3 observerPosition, Vector3 windDirection) =>
        EnvironmentLocalSample.Open(observerPosition);
}

public sealed class EnvironmentOptions
{
    public int Seed { get; set; } = 1337;
    public float StartTimeHours { get; set; } = 7.5f;
    /// <summary>Simulated seconds advanced by one real second; 24 gives one day per real hour.</summary>
    public float TimeScale { get; set; } = 24f;
    public int DayOfYear { get; set; } = 215;
    public float LatitudeDegrees { get; set; } = 53.9f;
    public bool Paused { get; set; }
    public bool AutomaticWeather { get; set; } = true;
    public bool UseEpisodicWeather { get; set; }
    /// <summary>Episode-clock multiplier, separate from celestial time scale.</summary>
    public float WeatherTimeScale { get; set; } = 1f;
    public float BaseTemperatureC { get; set; } = 18f;

    public EnvironmentOptions Clone() => (EnvironmentOptions)MemberwiseClone();
    internal void Validate()
    {
        if (!float.IsFinite(StartTimeHours) || !float.IsFinite(TimeScale) || TimeScale < 0 ||
            !float.IsFinite(LatitudeDegrees) || !float.IsFinite(WeatherTimeScale) || WeatherTimeScale < 0 ||
            !float.IsFinite(BaseTemperatureC))
            throw new ArgumentException("Environment options must contain finite times, coordinates and temperatures.");
    }
}

public readonly record struct EnvironmentPersistenceState(
    double ElapsedRealSeconds,
    WeatherKind ActiveWeather,
    float GroundWetness,
    float SnowAccumulation,
    bool AutomaticWeather);

/// <summary>One authoritative snapshot shared by lighting, weather particles, audio, and gameplay.</summary>
public readonly record struct EnvironmentFrame(
    double ElapsedRealSeconds,
    int DayIndex,
    float TimeOfDayHours,
    Vector3 SunDirection,
    Vector3 MoonDirection,
    float NightFactor,
    /// <summary>AF2.5 lunar illumination 0..1 (synodic cycle from day-of-year).</summary>
    float MoonPhase,
    WeatherState Weather,
    Vector3 LocalWind,
    float LocalRain,
    float LocalSnow,
    float LocalHail,
    float LocalTemperatureC,
    float GroundWetness,
    float SnowAccumulation,
    EnvironmentLocalSample LocalSample,
    Vector4 BackgroundColor,
    Vector4 FogColor,
    Vector3 AmbientSky,
    Vector3 AmbientGround)
{
    public bool IsNight => NightFactor >= 0.5f;
    public bool IsPrecipitating => LocalRain + LocalSnow + LocalHail > 0.01f;
}

/// <summary>
/// Deterministic environment service adapted from Aetherforge's authoritative environment-frame
/// pattern. Genesis owns the implementation and connects it directly to its scene and particle
/// contracts, with no dependency on the Aetherforge repository or its imported showcase code.
/// </summary>
public sealed class EnvironmentService
{
    private readonly IEnvironmentQuery _query;
    private double _elapsedRealSeconds;
    private double _simulatedSeconds;
    private WeatherState _weather;
    private WeatherState _transitionFrom;
    private WeatherState _transitionTarget;
    private float _transitionElapsed;
    private float _transitionDuration;
    private int _scheduleSlot = int.MinValue;
    private float _groundWetness;
    private float _snowAccumulation;

    public EnvironmentService(EnvironmentOptions options = null, IEnvironmentQuery query = null)
    {
        Options = (options ?? new EnvironmentOptions()).Clone();
        Options.Validate();
        Options.DayOfYear = Math.Clamp(Options.DayOfYear, 1, 366);
        Options.LatitudeDegrees = Math.Clamp(Options.LatitudeDegrees, -89f, 89f);
        _query = query ?? OpenEnvironmentQuery.Instance;
        _simulatedSeconds = WrapHours(Options.StartTimeHours) * 3600.0;
        Episodes = new WeatherSystem(Options.Seed);
        _weather = Profile(WeatherKind.Clear, Options.Seed);
        _transitionFrom = _transitionTarget = _weather;
        Current = BuildFrame(Vector3.Zero);
    }

    public WeatherSystem Episodes { get; }
    public EnvironmentOptions Options { get; }
    public EnvironmentFrame Current { get; private set; }
    public double ElapsedRealSeconds => _elapsedRealSeconds;
    public int DayIndex => (int)Math.Floor(_simulatedSeconds / 86_400.0);
    public float TimeOfDayHours => WrapHours((float)(_simulatedSeconds / 3600.0));
    public WeatherKind ActiveWeather => Options.UseEpisodicWeather && Options.AutomaticWeather ? _weather.Kind : _transitionTarget.Kind;

    public void SetTimeOfDay(float hours)
    {
        if (!float.IsFinite(hours)) throw new ArgumentOutOfRangeException(nameof(hours));
        double dayStart = Math.Floor(_simulatedSeconds / 86_400.0) * 86_400.0;
        _simulatedSeconds = dayStart + WrapHours(hours) * 3600.0;
        Current = BuildFrame(Vector3.Zero);
    }

    public void SetWeather(WeatherKind kind, float transitionSeconds = 20f)
    {
        if (!Enum.IsDefined(kind) || !float.IsFinite(transitionSeconds) || transitionSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(kind));
        Options.AutomaticWeather = false;
        BeginWeatherTransition(kind, transitionSeconds);
    }

    public void ResumeAutomaticWeather()
    {
        Options.AutomaticWeather = true;
        _scheduleSlot = int.MinValue;
    }

    public EnvironmentFrame Update(float deltaSeconds, Vector3 observerPosition)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0 ||
            !float.IsFinite(observerPosition.X) || !float.IsFinite(observerPosition.Y) || !float.IsFinite(observerPosition.Z))
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        Options.Validate();
        float dt = Math.Clamp(deltaSeconds, 0f, 1f);
        _elapsedRealSeconds += dt;
        if (!Options.Paused)
            _simulatedSeconds += dt * Math.Clamp(Options.TimeScale, 0f, 100_000f);

        if (Options.UseEpisodicWeather && Options.AutomaticWeather)
        {
            if (!Options.Paused) Episodes.Advance(dt * Math.Clamp(Options.WeatherTimeScale, 0f, 100_000f));
            _weather = Episodes.ToClimateState(Options.BaseTemperatureC);
        }
        else
        {
            if (Options.AutomaticWeather) UpdateSchedule();
            UpdateWeatherTransition(dt);
        }

        EnvironmentLocalSample local = _query.Sample(observerPosition, _weather.WindDirection);
        float rainOcclusion = Math.Clamp(local.RainOcclusion, 0f, 1f);
        float windOcclusion = Math.Clamp(local.WindOcclusion, 0f, 1f);
        float localRain = _weather.Rain * (1f - rainOcclusion);
        float localSnow = _weather.Snow * (1f - rainOcclusion);
        float localHail = _weather.Hail * (1f - rainOcclusion);
        Vector3 localWind = _weather.WindDirection * _weather.WindSpeed * (1f - windOcclusion);
        float temperature = _weather.TemperatureC - MathF.Max(0f, local.AltitudeMeters) * 0.0065f;
        float precipitation = Math.Clamp(localRain + localSnow + localHail, 0f, 1f);
        float drying = MathF.Max(0f, temperature) * 0.0003f + localWind.Length() * 0.00015f;
        _groundWetness = Math.Clamp(_groundWetness + precipitation * dt * 0.035f - drying * dt, 0f, 1f);
        float melt = MathF.Max(0f, temperature) * dt * 0.0007f;
        _snowAccumulation = Math.Clamp(_snowAccumulation + localSnow * dt * 0.012f - melt, 0f, 1f);

        Current = BuildFrame(observerPosition, local, localWind, localRain, localSnow, localHail, temperature);
        return Current;
    }

    public EnvironmentFullState CaptureFullState() => new(1, _elapsedRealSeconds, _simulatedSeconds,
        _weather, _transitionFrom, _transitionTarget, _transitionElapsed, _transitionDuration, _scheduleSlot,
        _groundWetness, _snowAccumulation, Options.Clone(), Episodes.CaptureState());

    public void RestoreFullState(EnvironmentFullState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != 1 || state.Options == null || !double.IsFinite(state.ElapsedSeconds) || state.ElapsedSeconds < 0 ||
            !double.IsFinite(state.SimulatedSeconds) || state.SimulatedSeconds < 0 ||
            !float.IsFinite(state.TransitionElapsed) || state.TransitionElapsed < 0 ||
            !float.IsFinite(state.TransitionDuration) || state.TransitionDuration < state.TransitionElapsed ||
            !float.IsFinite(state.Wetness) || state.Wetness < 0 || state.Wetness > 1 ||
            !float.IsFinite(state.Snow) || state.Snow < 0 || state.Snow > 1 ||
            !ValidWeather(state.Weather) || !ValidWeather(state.From) || !ValidWeather(state.Target))
            throw new ArgumentException("Invalid environment state.", nameof(state));
        state.Options.Validate();
        Episodes.RestoreState(state.Episodes);
        _elapsedRealSeconds=state.ElapsedSeconds; _simulatedSeconds=state.SimulatedSeconds; _weather=state.Weather;
        _transitionFrom=state.From; _transitionTarget=state.Target; _transitionElapsed=state.TransitionElapsed;
        _transitionDuration=state.TransitionDuration; _scheduleSlot=state.ScheduleSlot;
        _groundWetness=Math.Clamp(state.Wetness,0,1); _snowAccumulation=Math.Clamp(state.Snow,0,1);
        Options.Seed=state.Options.Seed; Options.StartTimeHours=state.Options.StartTimeHours;
        Options.TimeScale=state.Options.TimeScale; Options.DayOfYear=state.Options.DayOfYear;
        Options.LatitudeDegrees=state.Options.LatitudeDegrees; Options.Paused=state.Options.Paused;
        Options.AutomaticWeather=state.Options.AutomaticWeather; Options.UseEpisodicWeather=state.Options.UseEpisodicWeather;
        Options.WeatherTimeScale=state.Options.WeatherTimeScale; Options.BaseTemperatureC=state.Options.BaseTemperatureC;
        Current=BuildFrame(Vector3.Zero);
    }

    private static bool ValidWeather(WeatherState state) => Enum.IsDefined(state.Kind) &&
        float.IsFinite(state.CloudCover) && state.CloudCover >= 0 && state.CloudCover <= 1 &&
        float.IsFinite(state.Rain) && state.Rain >= 0 && state.Rain <= 1 &&
        float.IsFinite(state.Snow) && state.Snow >= 0 && state.Snow <= 1 &&
        float.IsFinite(state.Hail) && state.Hail >= 0 && state.Hail <= 1 &&
        float.IsFinite(state.WindDirection.X) && float.IsFinite(state.WindDirection.Y) && float.IsFinite(state.WindDirection.Z) &&
        float.IsFinite(state.WindSpeed) && state.WindSpeed >= 0 && float.IsFinite(state.TemperatureC) &&
        float.IsFinite(state.FogDensity) && state.FogDensity >= 0 && float.IsFinite(state.Lightning);

    public EnvironmentPersistenceState CaptureState() => new(
        _elapsedRealSeconds, ActiveWeather, _groundWetness, _snowAccumulation, Options.AutomaticWeather);

    public void RestoreState(in EnvironmentPersistenceState state)
    {
        _elapsedRealSeconds = Math.Max(0.0, state.ElapsedRealSeconds);
        _simulatedSeconds = WrapHours(Options.StartTimeHours) * 3600.0
            + _elapsedRealSeconds * Math.Clamp(Options.TimeScale, 0f, 100_000f);
        _groundWetness = Math.Clamp(state.GroundWetness, 0f, 1f);
        _snowAccumulation = Math.Clamp(state.SnowAccumulation, 0f, 1f);
        Options.AutomaticWeather = state.AutomaticWeather;
        _weather = Profile(state.ActiveWeather, Options.Seed + DayIndex);
        _transitionFrom = _transitionTarget = _weather;
        _transitionElapsed = _transitionDuration = 0f;
        _scheduleSlot = int.MinValue;
        Current = BuildFrame(Vector3.Zero);
    }

    /// <summary>Applies this frame's sky, celestial light, ambient, and fog to a render scene.</summary>
    public void ApplyTo(SceneEnvironment scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        EnvironmentFrame frame = Current;
        scene.BackgroundColor = frame.BackgroundColor;
        scene.FogColor = frame.FogColor;
        scene.AmbientColor = new Vector4(frame.AmbientSky, 1f);
        scene.AmbientGroundColor = new Vector4(frame.AmbientGround, 1f);
        scene.SunEnabled = true;
        scene.SunIntensity = MathF.Max(0.06f, 1.15f * (1f - frame.NightFactor) + 0.09f * frame.NightFactor);
        Vector3 primary = Vector3.Lerp(frame.SunDirection, frame.MoonDirection, frame.NightFactor);
        if (primary.LengthSquared() < 1e-6f) primary = -Vector3.UnitY;
        primary = Vector3.Normalize(primary);
        scene.SunYawDegrees = float.RadiansToDegrees(MathF.Atan2(primary.X, primary.Z));
        scene.SunPitchDegrees = float.RadiansToDegrees(MathF.Asin(Math.Clamp(-primary.Y, -1f, 1f)));
        scene.SunColor = Vector4.Lerp(
            new Vector4(1f, 0.78f, 0.55f, 1f),
            new Vector4(0.58f, 0.67f, 1f, 1f),
            frame.NightFactor);
        scene.FogEnabled = frame.Weather.FogDensity > 0.01f || frame.Weather.CloudCover > 0.78f;
        scene.FogDensity = MathF.Max(0.004f, frame.Weather.FogDensity);
        scene.FogAerialBlend = 0.35f + frame.Weather.CloudCover * 0.35f;
    }

    /// <summary>Builds the runtime precipitation emitter matching the current local frame.</summary>
    public ParticleConfig CreatePrecipitationConfig()
    {
        EnvironmentFrame frame = Current;
        if (frame.LocalSnow > 0.01f)
        {
            ParticleConfig snow = ParticlePresets.Snow();
            snow.EmitRate *= Math.Clamp(frame.LocalSnow, 0.08f, 1f);
            snow.WindX = frame.LocalWind.X;
            snow.WindZ = frame.LocalWind.Z;
            return snow;
        }

        if (frame.LocalHail > 0.01f)
        {
            ParticleConfig hail = ParticlePresets.Rain();
            hail.EmitRate *= Math.Clamp(frame.LocalHail, 0.1f, 1f) * 0.55f;
            hail.StartSize = hail.EndSize = 0.12;
            hail.SizeXScale = hail.SizeYScale = 1.0;
            hail.StartColor = new ParticleColor(0.86f, 0.91f, 1f, 0.9f);
            hail.EndColor = new ParticleColor(0.72f, 0.8f, 0.95f, 0.2f);
            hail.WindX = frame.LocalWind.X;
            hail.WindZ = frame.LocalWind.Z;
            return hail;
        }

        if (frame.LocalRain > 0.01f)
        {
            ParticleConfig rain = frame.LocalRain < 0.38f
                ? ParticlePresets.RainDrizzle()
                : frame.LocalRain > 0.78f ? ParticlePresets.RainHeavy() : ParticlePresets.Rain();
            rain.EmitRate *= Math.Clamp(frame.LocalRain, 0.08f, 1f);
            rain.WindX = frame.LocalWind.X;
            rain.WindZ = frame.LocalWind.Z;
            return rain;
        }

        return null;
    }

    private void UpdateSchedule()
    {
        int slot = DayIndex * 12 + (int)(TimeOfDayHours / 2f);
        if (slot == _scheduleSlot) return;
        _scheduleSlot = slot;
        uint hash = Hash((uint)Options.Seed ^ (uint)(slot * 747_796_405));
        WeatherKind[] choices =
        [
            WeatherKind.Clear, WeatherKind.Clear, WeatherKind.Overcast, WeatherKind.Wind,
            WeatherKind.Rain, WeatherKind.Rain, WeatherKind.Fog, WeatherKind.Thunderstorm,
            WeatherKind.Snow, WeatherKind.Hail,
        ];
        BeginWeatherTransition(choices[hash % (uint)choices.Length], 25f + hash % 50u);
    }

    private void BeginWeatherTransition(WeatherKind kind, float seconds)
    {
        _transitionFrom = _weather;
        _transitionTarget = Profile(kind, Options.Seed + DayIndex * 37 + _scheduleSlot);
        _transitionElapsed = 0f;
        _transitionDuration = MathF.Max(0.001f, seconds);
        if (seconds <= 0.001f)
        {
            _weather = _transitionTarget;
            _transitionElapsed = _transitionDuration;
        }
    }

    private void UpdateWeatherTransition(float dt)
    {
        if (_transitionElapsed >= _transitionDuration) return;
        _transitionElapsed = MathF.Min(_transitionDuration, _transitionElapsed + dt);
        float t = _transitionElapsed / _transitionDuration;
        t = t * t * (3f - 2f * t);
        _weather = WeatherState.Blend(_transitionFrom, _transitionTarget, t);
    }

    private EnvironmentFrame BuildFrame(Vector3 observer) => BuildFrame(
        observer,
        EnvironmentLocalSample.Open(observer),
        _weather.WindDirection * _weather.WindSpeed,
        _weather.Rain,
        _weather.Snow,
        _weather.Hail,
        _weather.TemperatureC);

    private EnvironmentFrame BuildFrame(
        Vector3 observer,
        EnvironmentLocalSample local,
        Vector3 localWind,
        float localRain,
        float localSnow,
        float localHail,
        float temperature)
    {
        _ = observer;
        (Vector3 sun, Vector3 moon, float night) = CelestialDirections(
            TimeOfDayHours, Options.DayOfYear, Options.LatitudeDegrees);
        float moonPhase = MoonPhaseFromDayOfYear(Options.DayOfYear);
        (Vector4 background, Vector4 fog, Vector3 ambientSky, Vector3 ambientGround) =
            SkyPalette(TimeOfDayHours, night, _weather.CloudCover, _weather.FogDensity);
        return new EnvironmentFrame(
            _elapsedRealSeconds, DayIndex, TimeOfDayHours, sun, moon, night, moonPhase, _weather,
            localWind, localRain, localSnow, localHail, temperature, _groundWetness,
            _snowAccumulation, local, background, fog, ambientSky, ambientGround);
    }

    private static (Vector3 Sun, Vector3 Moon, float Night) CelestialDirections(
        float hours, int dayOfYear, float latitudeDegrees)
    {
        float latitude = float.DegreesToRadians(latitudeDegrees);
        float declination = float.DegreesToRadians(23.44f)
            * MathF.Sin(MathF.Tau * (284f + dayOfYear) / 365f);
        float hourAngle = float.DegreesToRadians((hours - 12f) * 15f);
        float sinElevation = MathF.Sin(latitude) * MathF.Sin(declination)
            + MathF.Cos(latitude) * MathF.Cos(declination) * MathF.Cos(hourAngle);
        float elevation = MathF.Asin(Math.Clamp(sinElevation, -1f, 1f));
        float azimuth = MathF.Atan2(
            -MathF.Sin(hourAngle) * MathF.Cos(declination),
            MathF.Sin(declination) * MathF.Cos(latitude)
                - MathF.Cos(declination) * MathF.Sin(latitude) * MathF.Cos(hourAngle));
        Vector3 sunPosition = Vector3.Normalize(new Vector3(
            MathF.Sin(azimuth) * MathF.Cos(elevation),
            MathF.Sin(elevation),
            MathF.Cos(azimuth) * MathF.Cos(elevation)));
        // AF2.5: cheap lunar orbit offset beyond pure anti-sun (SkyForge CelestialSystem).
        float moonOrbit = (dayOfYear * 0.22997f + hours / 24f) * MathF.Tau;
        Vector3 towardMoon = Vector3.Normalize(new Vector3(
            -sunPosition.X + 0.22f * MathF.Sin(moonOrbit),
            -sunPosition.Y + 0.16f * MathF.Sin(moonOrbit * 0.83f),
            -sunPosition.Z + 0.22f * MathF.Cos(moonOrbit)));
        float daylight = SmoothStep(-0.12f, 0.08f, elevation);
        // Sun/Moon directions use light-travel convention (from light toward the scene).
        return (-sunPosition, -towardMoon, 1f - daylight);
    }

    /// <summary>
    /// Synodic illumination 0..1: <c>0.5 − 0.5·cos(2π · frac(day / 29.5306))</c>.
    /// </summary>
    private static float MoonPhaseFromDayOfYear(int dayOfYear)
    {
        const float synodic = 29.5306f;
        float cycle = MathF.Abs(dayOfYear) % synodic / synodic;
        return 0.5f - 0.5f * MathF.Cos(cycle * MathF.Tau);
    }

    private static (Vector4 Background, Vector4 Fog, Vector3 Sky, Vector3 Ground) SkyPalette(
        float hours, float night, float cloud, float fogDensity)
    {
        Vector3 dayZenith = new(0.25f, 0.46f, 0.78f);
        Vector3 dayHorizon = new(0.62f, 0.72f, 0.84f);
        Vector3 nightZenith = new(0.012f, 0.02f, 0.07f);
        Vector3 nightHorizon = new(0.045f, 0.06f, 0.12f);
        float dawn = MathF.Max(
            MathF.Exp(-MathF.Pow((hours - 6f) / 1.35f, 2f)),
            MathF.Exp(-MathF.Pow((hours - 18f) / 1.35f, 2f)));
        Vector3 warm = new(0.92f, 0.38f, 0.16f);
        Vector3 zenith = Vector3.Lerp(dayZenith, nightZenith, night);
        Vector3 horizon = Vector3.Lerp(dayHorizon, nightHorizon, night);
        horizon = Vector3.Lerp(horizon, warm, dawn * (1f - cloud) * 0.65f);
        Vector3 overcast = new(0.34f, 0.37f, 0.42f);
        zenith = Vector3.Lerp(zenith, overcast * 0.82f, cloud * 0.72f);
        horizon = Vector3.Lerp(horizon, overcast, cloud * 0.68f);
        Vector3 fog = Vector3.Lerp(horizon, overcast, Math.Clamp(fogDensity * 8f, 0f, 0.75f));
        float ambient = 0.33f * (1f - night) + 0.075f * night;
        return (new Vector4(horizon, 1f), new Vector4(fog, 1f),
            zenith * ambient, new Vector3(0.24f, 0.21f, 0.18f) * ambient);
    }

    private static WeatherState Profile(WeatherKind kind, int seed)
    {
        uint hash = Hash((uint)seed ^ (uint)kind * 2_246_822_519u);
        float angle = hash / (float)uint.MaxValue * MathF.Tau;
        Vector3 wind = Vector3.Normalize(new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)));
        return kind switch
        {
            WeatherKind.Clear => new(kind, 0.08f, 0f, 0f, 0f, wind, 1.2f, 18f, 0f, 0f),
            WeatherKind.Overcast => new(kind, 0.88f, 0f, 0f, 0f, wind, 2.6f, 13f, 0.025f, 0f),
            WeatherKind.Wind => new(kind, 0.32f, 0f, 0f, 0f, wind, 8.5f, 16f, 0.005f, 0f),
            WeatherKind.Rain => new(kind, 0.94f, 0.72f, 0f, 0f, wind, 5.2f, 11f, 0.045f, 0f),
            WeatherKind.Thunderstorm => new(kind, 1f, 1f, 0f, 0.05f, wind, 11f, 9f, 0.075f, 0.85f),
            WeatherKind.Snow => new(kind, 0.91f, 0f, 0.78f, 0f, wind, 3.8f, -5f, 0.055f, 0f),
            WeatherKind.Hail => new(kind, 0.98f, 0.25f, 0f, 0.82f, wind, 9f, 2f, 0.055f, 0.25f),
            WeatherKind.Fog => new(kind, 0.72f, 0f, 0f, 0f, wind, 0.8f, 8f, 0.16f, 0f),
            WeatherKind.ColdSnap => new(kind, .45f, 0, 0, 0, wind, 3.6f, -3, .02f, 0),
            WeatherKind.Heatwave => new(kind, .04f, 0, 0, 0, wind, .24f, 30, .003f, 0),
            _ => new(kind, 0f, 0f, 0f, 0f, wind, 0f, 15f, 0f, 0f),
        };
    }

    private static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 2_246_822_519u;
        value ^= value >> 13;
        value *= 3_266_489_917u;
        return value ^ (value >> 16);
    }

    private static float SmoothStep(float min, float max, float value)
    {
        float t = Math.Clamp((value - min) / MathF.Max(0.0001f, max - min), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float WrapHours(float hours)
    {
        hours %= 24f;
        return hours < 0f ? hours + 24f : hours;
    }
}
