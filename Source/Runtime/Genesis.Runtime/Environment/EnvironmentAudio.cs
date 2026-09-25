using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Shared.Audio;
using Genesis.World.Navigation;
using Genesis.World.Terrain;

namespace Genesis.Runtime.Climate;

public enum EnvironmentAudioRole : byte
{
    None,
    Wind,
    Rain,
    Water,
    Fire,
    Wildlife,
    Night,
}

public readonly record struct EnvironmentAudioMix(
    float Wind,
    float Rain,
    float Water,
    float Fire,
    float Wildlife,
    float Night,
    Vector3 WaterPosition,
    Vector3 FirePosition,
    Vector3 WildlifePosition)
{
    public float Gain(EnvironmentAudioRole role) => role switch
    {
        EnvironmentAudioRole.Wind => Wind,
        EnvironmentAudioRole.Rain => Rain,
        EnvironmentAudioRole.Water => Water,
        EnvironmentAudioRole.Fire => Fire,
        EnvironmentAudioRole.Wildlife => Wildlife,
        EnvironmentAudioRole.Night => Night,
        _ => 0f,
    };

    public Vector3 Position(EnvironmentAudioRole role) => role switch
    {
        EnvironmentAudioRole.Water => WaterPosition,
        EnvironmentAudioRole.Fire => FirePosition,
        EnvironmentAudioRole.Wildlife => WildlifePosition,
        _ => default,
    };
}

/// <summary>Maps one authoritative environment/world snapshot to stable adaptive ambience gains.</summary>
public static class EnvironmentAudioMixer
{
    public static EnvironmentAudioMix Evaluate(in EnvironmentFrame frame, IWorldQuery world, Vector3 listener)
    {
        float shelter = Math.Clamp(MathF.Max(frame.LocalSample.RainOcclusion, frame.LocalSample.WindOcclusion), 0f, 1f);
        float wind = Smooth(Math.Clamp(frame.LocalWind.Length() / 12f, 0f, 1f)) * (1f - shelter * 0.58f);
        float rain = Smooth(Math.Clamp(frame.LocalRain, 0f, 1f)) * (1f - frame.LocalSample.RainOcclusion * 0.78f);
        float water = 0f, fire = 0f, wildlife = 0f;
        Vector3 waterPosition = listener, firePosition = listener, wildlifePosition = listener;
        if (world != null)
        {
            (water, waterPosition) = NearestWater(world, listener);
            (fire, firePosition) = NearestPoi(world, listener, 80f, "fire", "camp", "hearth");
            (wildlife, wildlifePosition) = NearestPoi(world, listener, 120f, "wildlife", "nest", "den", "graze");
        }
        float night = Smooth(Math.Clamp((frame.NightFactor - 0.25f) / 0.75f, 0f, 1f)) * (1f - rain * 0.55f);
        wildlife *= Math.Clamp(1f - rain * 0.72f, 0.15f, 1f);
        return new EnvironmentAudioMix(wind, rain, water, fire, wildlife, night,
            waterPosition, firePosition, wildlifePosition);
    }

    private static (float Gain, Vector3 Position) NearestWater(IWorldQuery world, Vector3 listener)
    {
        TerrainWaterDefinition nearest = null;
        float best = float.MaxValue;
        foreach (TerrainWaterDefinition water in world.Manifest.WaterBodies)
        {
            float dx = MathF.Max(0f, MathF.Abs(listener.X - water.Center.X) - water.SizeX * 0.5f);
            float dz = MathF.Max(0f, MathF.Abs(listener.Z - water.Center.Z) - water.SizeZ * 0.5f);
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance >= best) continue;
            best = distance; nearest = water;
        }
        if (nearest == null) return (0f, listener);
        float gain = Math.Clamp(1f - best / 65f, 0f, 1f);
        return (Smooth(gain), new Vector3(nearest.Center.X, nearest.SurfaceHeight, nearest.Center.Z));
    }

    private static (float Gain, Vector3 Position) NearestPoi(IWorldQuery world, Vector3 listener,
        float radius, params string[] categories)
    {
        WorldPointOfInterest nearest = world.PointsNear(listener, radius)
            .FirstOrDefault(poi => categories.Any(category => poi.Category.Contains(category, StringComparison.OrdinalIgnoreCase)));
        if (nearest == null) return (0f, listener);
        float gain = Math.Clamp(1f - Vector3.Distance(listener, nearest.Position) / radius, 0f, 1f);
        return (Smooth(gain), nearest.Position);
    }

    private static float Smooth(float value) => value * value * (3f - 2f * value);
}

/// <summary>Owns looping role channels and cross-fades them from <see cref="EnvironmentAudioMixer"/>.</summary>
public sealed class EnvironmentSoundscape : IDisposable
{
    private sealed class Layer
    {
        public int SoundId;
        public AudioChannel Channel;
        public float Gain;
        public bool Spatial;
        public float Level = 1f;
    }

    private readonly IAudioSystem _audio;
    private readonly Dictionary<EnvironmentAudioRole, Layer> _layers = new();

    public EnvironmentSoundscape(IAudioSystem audio) => _audio = audio ?? throw new ArgumentNullException(nameof(audio));

    public void Register(EnvironmentAudioRole role, string projectRelativePath, bool spatial = false, float level = 1f)
    {
        if (role == EnvironmentAudioRole.None) throw new ArgumentOutOfRangeException(nameof(role));
        int sound = _audio.LoadSound(projectRelativePath);
        if (sound == 0) return;
        if (_layers.TryGetValue(role, out Layer existing) && existing.Channel.IsValid)
            _audio.Stop(existing.Channel);
        _layers[role] = new Layer { SoundId = sound, Spatial = spatial,
            Level = float.IsFinite(level) ? Math.Clamp(level, 0f, 1f) : 1f };
    }

    public void Update(in EnvironmentFrame environment, IWorldQuery world, Vector3 listener, float deltaSeconds)
    {
        EnvironmentAudioMix mix = EnvironmentAudioMixer.Evaluate(environment, world, listener);
        float blend = 1f - MathF.Exp(-Math.Clamp(deltaSeconds, 0f, 1f) * 4.5f);
        foreach ((EnvironmentAudioRole role, Layer layer) in _layers)
        {
            float target = mix.Gain(role) * layer.Level;
            layer.Gain = float.Lerp(layer.Gain, target, blend);
            if (!layer.Channel.IsValid && target > 0.005f)
                layer.Channel = _audio.Play(layer.SoundId, 0f, 1f, loop: true);
            if (!layer.Channel.IsValid) continue;
            _audio.SetChannelVolume(layer.Channel, layer.Gain);
            if (layer.Spatial) _audio.SetChannelPosition(layer.Channel, mix.Position(role));
        }
    }

    public void Dispose()
    {
        foreach (Layer layer in _layers.Values)
            if (layer.Channel.IsValid) _audio.Stop(layer.Channel);
        _layers.Clear();
    }
}
