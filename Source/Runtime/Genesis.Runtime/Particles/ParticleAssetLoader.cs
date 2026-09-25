#nullable enable annotations
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace Genesis.Runtime.Particles;

/// <summary>Loads the canonical particle document shared by the Particle Editor and runtime.</summary>
public static class ParticleAssetLoader
{
    private const string BuiltInPrefix = "builtin://";
    private sealed record CacheEntry(long WriteTicks, ParticleConfig Config);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ParticleConfig Load(string projectPath, string asset)
    {
        if (TryBuiltIn(asset, out string preset))
            return ParticlePresets.Create(preset);
        string path = Resolve(projectPath, asset);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"Particle asset '{asset}' could not be resolved.", path);

        long writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
        if (Cache.TryGetValue(path, out CacheEntry? cached) && cached.WriteTicks == writeTicks)
            return cached.Config.Clone();

        ParticleConfig config = JsonSerializer.Deserialize<ParticleConfig>(File.ReadAllText(path), JsonOptions)
            ?? new ParticleConfig();
        Normalise(config);
        Cache[path] = new CacheEntry(writeTicks, config.Clone());
        return config;
    }

    public static string Resolve(string projectPath, string asset)
    {
        if (TryBuiltIn(asset, out string preset)) return BuiltInPrefix + preset;
        return ResourceNames.Resolve(projectPath, asset, ResourceType.Particle);
    }

    private static bool TryBuiltIn(string asset, out string preset)
    {
        preset = string.Empty;
        if (string.IsNullOrWhiteSpace(asset) || !asset.StartsWith(BuiltInPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        string requested = asset[BuiltInPrefix.Length..].Replace('-', ' ').Trim();
        preset = Array.Find(ParticlePresets.Names, name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        return preset.Length > 0;
    }

    public static void ClearCache() => Cache.Clear();

    /// <summary>Returns the root emitter followed by enabled additional emitters.</summary>
    public static IReadOnlyList<(string Id, string Name, ParticleConfig Config)> EnumerateEnabledEmitters(ParticleConfig effect)
    {
        var result = new List<(string, string, ParticleConfig)>();
        if (effect == null) return result;
        if (effect.EmitterEnabled)
            result.Add((effect.EmitterId ?? "primary", effect.EmitterName ?? "Primary", effect.Clone(includeEmitters: false)));
        foreach (ParticleEmitterLayer layer in effect.Emitters ?? [])
        {
            if (layer?.Enabled != true || layer.Config == null) continue;
            ParticleConfig config = layer.Config.Clone(includeEmitters: false);
            Normalise(config);
            result.Add((layer.Id ?? Guid.NewGuid().ToString("N"), layer.Name ?? "Emitter", config));
        }
        return result;
    }

    private static void Normalise(ParticleConfig config)
    {
        config.MaxParticles = Math.Clamp(config.MaxParticles, 1, 100_000);
        config.EmitRate = Math.Clamp(config.EmitRate, 0d, 100_000d);
        config.BurstCount = Math.Clamp(config.BurstCount, 0, 100_000);
        config.Lifetime = Math.Clamp(config.Lifetime, 0.01d, 3600d);
        config.Duration = Math.Clamp(config.Duration, 0.05d, 3600d);
        config.FlipbookColumns = Math.Clamp(config.FlipbookColumns, 1, 64);
        config.FlipbookRows = Math.Clamp(config.FlipbookRows, 1, 64);
        config.FlipbookFps = Math.Clamp(config.FlipbookFps, 0d, 1000d);
        config.TrailDuration = Math.Clamp(config.TrailDuration, 0.001d, 3600d);
        config.TrailWidth = Math.Clamp(config.TrailWidth, 0.001d, 10000d);
        config.RibbonMaxSegmentLength = Math.Clamp(config.RibbonMaxSegmentLength, 0.001d, 10000d);
        config.VelocityStretch = Math.Clamp(config.VelocityStretch, 0d, 1000d);
        config.BeamNoise = Math.Clamp(config.BeamNoise, 0d, 10000d);
        config.EventProbability = Math.Clamp(config.EventProbability, 0d, 1d);
        config.EventSpawnCount = Math.Clamp(config.EventSpawnCount, 1, 32);
        config.EventInheritVelocity = Math.Clamp(config.EventInheritVelocity, 0d, 4d);
        config.BoundsSizeX = Math.Max(0.001d, config.BoundsSizeX);
        config.BoundsSizeY = Math.Max(0.001d, config.BoundsSizeY);
        config.BoundsSizeZ = Math.Max(0.001d, config.BoundsSizeZ);
        config.CollisionRadius = Math.Clamp(config.CollisionRadius, 0d, 1000d);
        config.ParentEmitterId ??= string.Empty;
        config.BoxSizeX = Math.Max(0.001d, config.BoxSizeX);
        config.BoxSizeY = Math.Max(0.001d, config.BoxSizeY);
        config.BoxSizeZ = Math.Max(0.001d, config.BoxSizeZ);
        config.StartColor ??= new ParticleColor();
        config.EndColor ??= new ParticleColor(1f, 1f, 1f, 0f);
        config.TexturePath ??= string.Empty;
        config.MeshSurfaceAsset ??= string.Empty;
        config.MeshParticleAsset ??= string.Empty;
        config.BackdropSprite ??= string.Empty;
        config.PreviewTargetAsset ??= string.Empty;
        config.Notes ??= string.Empty;
        config.Script ??= string.Empty;
        config.EffectName ??= "Particle Effect";
        config.EmitterId ??= "primary";
        config.EmitterName ??= "Primary";
        config.Emitters ??= [];
        config.Light ??= new ParticleLightConfig();
        config.Light.Color ??= new ParticleColor(1f, 0.48f, 0.12f, 1f);
        config.SizeOverLifetime ??= new ParticleBezierCurve();
        config.SpeedOverLifetime ??= new ParticleBezierCurve();
        config.AlphaOverLifetime ??= new ParticleBezierCurve();
        config.VelocityOverLifetime ??= new ParticleBezierCurve();
        config.GradientStops ??= [];
        config.GradientStops.RemoveAll(stop => stop == null);
        foreach (ParticleGradientStop stop in config.GradientStops)
        {
            stop.Position = Math.Clamp(stop.Position, 0d, 1d);
            stop.Color ??= new ParticleColor();
        }
        config.GradientStops.Sort((left, right) => left.Position.CompareTo(right.Position));
        foreach (ParticleEmitterLayer layer in config.Emitters)
        {
            if (layer == null) continue;
            layer.Id ??= Guid.NewGuid().ToString("N");
            layer.Name ??= "Emitter";
            layer.Config ??= new ParticleConfig();
            Normalise(layer.Config);
            layer.Config.Emitters = [];
        }
    }
}
