namespace Genesis.Runtime.Climate;
/// <summary>Exact resumable environment state. Legacy EnvironmentPersistenceState remains supported.</summary>
public sealed record EnvironmentFullState(int Version, double ElapsedSeconds, double SimulatedSeconds,
    WeatherState Weather, WeatherState From, WeatherState Target, float TransitionElapsed, float TransitionDuration,
    int ScheduleSlot, float Wetness, float Snow, EnvironmentOptions Options, WeatherSystemState Episodes);
