using System;
using System.Collections.Generic;
using Genesis.Runtime.Climate;
using Genesis.Runtime.Core;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Scene;
using Genesis.Shared.Audio;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project;

/// <summary>
/// Draws the weather a room's climate reports: rain, snow or hail falling around the camera,
/// lightning lighting the sky, and thunder after it.
/// </summary>
/// <remarks>
/// <para>
/// The climate already decides what the weather is, wets the ground and darkens the sky. Without
/// this a game had to notice rain for itself, place its own emitter and flash its own screen.
/// A room opts in with its "Draw rain, snow and lightning" setting, so a game that draws its own
/// weather is not given a second one.
/// </para>
/// <para>
/// Precipitation is one emitter that follows the camera, swapped when the kind of weather changes
/// and scaled with how hard it is falling. It drifts and leans with the room's wind: rain on a
/// gale comes down at a slant, snow wanders. Lightning is a strike at a random distance: the sky
/// and everything under it brighten in two quick pulses, and the thunder is played after the time
/// sound takes to cover that distance, quieter the further off it was.
/// </para>
/// </remarks>
public sealed class RoomWeatherEffectsSubsystem : ISceneSubsystem
{
    private const float SpeedOfSound = 343f;
    private const float FlashSeconds = 0.45f;

    private readonly IAudioSystem _audio;
    private readonly int _thunderSound;
    private readonly float _thunderLevel;
    private readonly Random _random;
    private readonly List<(float Delay, float Volume)> _thunder = new();
    private RuntimeScene _scene;
    private Entity _emitter = Entity.Null;
    private float _untilStrike = -1f;
    private float _flashClock = -1f;
    private float _strikeBrightness;

    public RoomWeatherEffectsSubsystem(RoomEnvironment environment, IAudioSystem audio)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _audio = audio ?? NullAudioSystem.Instance;
        _random = new Random(environment.ClimateSeed);
        RoomSoundscapeLevels levels = (environment.SoundscapeLevels ?? new()).Clone();
        _thunderLevel = levels.Master * levels.Thunder;
        _thunderSound = string.IsNullOrWhiteSpace(environment.ThunderAudio) ? 0 : _audio.LoadSound(environment.ThunderAudio);
    }

    /// <summary>True for a 3D room with a dynamic sky that asks the engine to draw its weather.</summary>
    public static bool ShouldRegister(RoomAsset room) =>
        room is { Dimension: RoomDimension.ThreeD, Environment: { DynamicSky: true, WeatherEffects: true } };

    /// <summary>The built-in effect falling now ("builtin://Rain", "builtin://Snow"), or empty when dry.</summary>
    public string Precipitation { get; private set; } = "";

    /// <summary>How hard it is falling against the effect's own rate.</summary>
    public float PrecipitationRate { get; private set; }

    /// <summary>The wind the precipitation is drifting on, in metres a second along X and Z.</summary>
    public System.Numerics.Vector2 PrecipitationWind { get; private set; }

    /// <summary>The most wind rain is carried on, in metres a second: at 9 m/s of fall this is a slant of about forty degrees.</summary>
    public const float RainWindLimit = 7.5f;

    /// <summary>The most wind snow is carried on, in metres a second. A flake is in the air for seconds, so it travels far on little.</summary>
    public const float SnowWindLimit = 2.5f;

    /// <summary>The light a lightning strike is adding right now, from 0 to 1.</summary>
    public float Flash { get; private set; }

    /// <summary>Lightning strikes since the room began.</summary>
    public int Strikes { get; private set; }

    /// <summary>Thunderclaps played since the room began.</summary>
    public int Thunderclaps { get; private set; }

    public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

    public void Update(RuntimeScene scene, GameTime time)
    {
        _scene = scene;
        EnvironmentService climate = scene.Climate;
        if (climate == null)
        {
            RemoveEmitter(scene);
            SetFlash(scene, 0f);
            return;
        }

        EnvironmentFrame frame = climate.Current;
        UpdatePrecipitation(scene, frame);
        UpdateLightning(scene, frame, MathF.Max(0f, time.Delta));
    }

    private void UpdatePrecipitation(RuntimeScene scene, in EnvironmentFrame frame)
    {
        // Snow first; then whichever of hail and rain is falling harder (a thunderstorm carries a
        // little hail in a great deal of rain, and must look like the rain); and a drizzle, a
        // shower or a downpour by how hard the rain falls.
        string asset = "";
        float rate = 0f;
        if (frame.LocalSnow > 0.01f)
        {
            asset = "builtin://Snow";
            rate = Math.Clamp(frame.LocalSnow, 0.08f, 1f);
        }
        else if (frame.LocalHail > 0.01f && frame.LocalHail >= frame.LocalRain)
        {
            asset = "builtin://Rain";
            rate = Math.Clamp(frame.LocalHail, 0.1f, 1f) * 0.55f;
        }
        else if (frame.LocalRain > 0.01f)
        {
            asset = "builtin://Rain";
            float kind = frame.LocalRain < 0.38f ? 0.45f : frame.LocalRain > 0.78f ? 1.35f : 1f;
            rate = Math.Clamp(frame.LocalRain, 0.08f, 1f) * kind;
        }

        if (!string.Equals(asset, Precipitation, StringComparison.Ordinal))
        {
            RemoveEmitter(scene);
            Precipitation = asset;
            if (asset.Length > 0)
            {
                _emitter = scene.World.CreateEntity();
                scene.World.Set(_emitter, new TransformComponent { ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f });
                scene.World.Set(_emitter, new ParticleComponent
                {
                    Asset = asset, ParticleTypeId = -1, RateScale = MathF.Max(0.01f, rate), FollowEntity = true, Emitting = true,
                });
            }
        }

        PrecipitationRate = rate;
        // The same wind the climate reports, held to a speed the effect still looks right at.
        var wind = new System.Numerics.Vector2(frame.LocalWind.X, frame.LocalWind.Z);
        float limit = asset == "builtin://Snow" ? SnowWindLimit : RainWindLimit;
        float speed = wind.Length();
        if (!float.IsFinite(speed)) wind = default;
        else if (speed > limit) wind *= limit / speed;
        PrecipitationWind = asset.Length > 0 ? wind : default;
        if (_emitter.IsNull) return;
        if (!scene.World.IsAlive(_emitter) || !scene.World.Has<ParticleComponent>(_emitter))
        {
            // Something removed it; make it again on the next update.
            _emitter = Entity.Null;
            Precipitation = "";
            return;
        }

        ref ParticleComponent falling = ref scene.World.GetRef<ParticleComponent>(_emitter);
        falling.RateScale = MathF.Max(0.01f, rate);
        falling.Wind = PrecipitationWind;
    }

    private void UpdateLightning(RuntimeScene scene, in EnvironmentFrame frame, float delta)
    {
        float lightning = Math.Clamp(frame.Weather.Lightning, 0f, 1f);
        if (lightning > 0.02f)
        {
            // A storm at full strength strikes every five to thirteen seconds; a weaker one waits longer.
            if (_untilStrike < 0f) _untilStrike = NextGap(lightning) * 0.4f;
            _untilStrike -= delta;
            if (_untilStrike <= 0f)
            {
                Strike();
                _untilStrike = NextGap(lightning);
            }
        }
        else
        {
            _untilStrike = -1f;
        }

        if (_flashClock >= 0f)
        {
            _flashClock += delta;
            if (_flashClock >= FlashSeconds) _flashClock = -1f;
        }

        SetFlash(scene, _flashClock < 0f ? 0f : Envelope(_flashClock) * _strikeBrightness);

        for (int i = _thunder.Count - 1; i >= 0; i--)
        {
            (float delay, float volume) = _thunder[i];
            delay -= delta;
            if (delay > 0f)
            {
                _thunder[i] = (delay, volume);
                continue;
            }

            _thunder.RemoveAt(i);
            Thunderclaps++;
            if (_thunderSound != 0) _audio.Play(_thunderSound, Math.Clamp(volume * _thunderLevel, 0f, 1f), 0.9f + (float)_random.NextDouble() * 0.2f);
        }
    }

    /// <summary>Lightning now, whatever the weather: for a scripted storm, a spell, a cutscene.</summary>
    /// <param name="distanceMetres">How far away it strikes; the thunder follows after the sound has travelled that far.</param>
    public void Strike(float distanceMetres = -1f)
    {
        float distance = distanceMetres >= 0f && float.IsFinite(distanceMetres)
            ? distanceMetres
            : 250f + (float)(_random.NextDouble() * _random.NextDouble()) * 5500f;
        Strikes++;
        _flashClock = 0f;
        // A strike overhead is blinding; one on the horizon lights the cloud.
        _strikeBrightness = Math.Clamp(1.05f - distance / 9000f, 0.35f, 1f);
        _thunder.Add((Math.Clamp(distance / SpeedOfSound, 0.15f, 12f), Math.Clamp(1.1f - distance / 6500f, 0.12f, 1f)));
    }

    private float NextGap(float lightning) => (5f + (float)_random.NextDouble() * 8f) / MathF.Max(0.15f, lightning);

    /// <summary>Two pulses: the stroke, a flicker, and the glow dying away.</summary>
    private static float Envelope(float seconds)
    {
        if (seconds < 0.05f) return 1f;
        if (seconds < 0.12f) return float.Lerp(1f, 0.15f, (seconds - 0.05f) / 0.07f);
        if (seconds < 0.16f) return 0.75f;
        return seconds < FlashSeconds ? float.Lerp(0.75f, 0f, (seconds - 0.16f) / (FlashSeconds - 0.16f)) : 0f;
    }

    private void SetFlash(RuntimeScene scene, float flash)
    {
        Flash = flash;
        scene.Environment.LightningFlash = flash;
    }

    private void RemoveEmitter(RuntimeScene scene)
    {
        if (!_emitter.IsNull && scene?.World != null && scene.World.IsAlive(_emitter)) scene.World.DestroyEntity(_emitter);
        _emitter = Entity.Null;
        Precipitation = "";
        PrecipitationRate = 0f;
        PrecipitationWind = default;
    }

    public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer) { }

    public void Dispose()
    {
        if (_scene != null)
        {
            RemoveEmitter(_scene);
            _scene.Environment.LightningFlash = 0f;
        }

        _thunder.Clear();
    }
}
