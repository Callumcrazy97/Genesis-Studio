using System;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Audio;

namespace Genesis.Runtime.Project;

/// <summary>Room-authored adaptive ambience driven by the same climate/world snapshot as gameplay.</summary>
public sealed class RoomEnvironmentAudioSubsystem : ISceneSubsystem
{
    private readonly EnvironmentSoundscape _soundscape;
    private readonly RoomEnvironment _environment;
    private EnvironmentService _audioClimate;

    public RoomEnvironmentAudioSubsystem(RoomEnvironment environment, IGameContext game)
        : this(environment, game?.Audio ?? throw new ArgumentNullException(nameof(game))) { }

    public RoomEnvironmentAudioSubsystem(RoomEnvironment environment, IAudioSystem audio)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _soundscape = new EnvironmentSoundscape(audio);
        _environment = environment;
        RoomSoundscapeLevels levels = (environment.SoundscapeLevels ?? new()).Clone();
        Register(EnvironmentAudioRole.Wind, environment.WindAudio, levels.Master * levels.Wind);
        Register(EnvironmentAudioRole.Rain, environment.RainAudio, levels.Master * levels.Rain);
        Register(EnvironmentAudioRole.Water, environment.WaterAudio, levels.Master * levels.Water, spatial: true);
        Register(EnvironmentAudioRole.Fire, environment.FireAudio, levels.Master * levels.Fire, spatial: true);
        Register(EnvironmentAudioRole.Wildlife, environment.WildlifeAudio, levels.Master * levels.Wildlife, spatial: true);
        Register(EnvironmentAudioRole.Night, environment.NightAudio, levels.Master * levels.Night);
    }

    public static bool ShouldRegister(RoomEnvironment environment) => environment != null &&
        (!string.IsNullOrWhiteSpace(environment.WindAudio) || !string.IsNullOrWhiteSpace(environment.RainAudio) ||
         !string.IsNullOrWhiteSpace(environment.WaterAudio) || !string.IsNullOrWhiteSpace(environment.FireAudio) ||
         !string.IsNullOrWhiteSpace(environment.WildlifeAudio) || !string.IsNullOrWhiteSpace(environment.NightAudio));

    public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

    public void Update(RuntimeScene scene, GameTime time)
    {
        EnvironmentFrame frame;
        if (scene.Climate != null) frame = scene.Climate.Current;
        else
        {
            if (_audioClimate == null)
            {
                _audioClimate = scene.CreateEnvironmentService(new EnvironmentOptions
                {
                    Seed = _environment.ClimateSeed, StartTimeHours = _environment.TimeOfDayHours,
                    TimeScale = _environment.TimeScale, DayOfYear = _environment.DayOfYear,
                    LatitudeDegrees = _environment.LatitudeDegrees, AutomaticWeather = _environment.AutomaticWeather,
                });
                _audioClimate.SetWeather(Enum.TryParse(_environment.Weather, true, out WeatherKind kind) ? kind : WeatherKind.Clear, 0f);
                if (_environment.AutomaticWeather) _audioClimate.ResumeAutomaticWeather();
            }
            frame = _audioClimate.Update(time.Delta, scene.Camera3D.Position);
        }
        _soundscape.Update(frame, scene.WorldQuery, scene.Camera3D.Position, time.Delta);
    }

    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }
    public void Dispose() => _soundscape.Dispose();

    private void Register(EnvironmentAudioRole role, string path, float level, bool spatial = false)
    {
        if (!string.IsNullOrWhiteSpace(path)) _soundscape.Register(role, path, spatial, level);
    }
}
