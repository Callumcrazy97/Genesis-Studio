using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Rendering.Particles;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Particles;

/// <summary>
/// Converts the authored particle schema into the backend-neutral GPU protocol. The lookup table is
/// immutable until authoring data or source geometry changes; moving an emitter only replaces the
/// small parameter block.
/// </summary>
public static class ParticleGpuDefinitionBuilder
{
    private const int BaseLookupCount = GpuParticleProtocol.CurveSamples * 2;

    public static GpuParticleDefinition Build(
        ParticleConfig config,
        Matrix4x4 world,
        ReadOnlySpan<Vector3> meshSurfaceSamples = default,
        int rendererKind = 0,
        float trailDuration = 0.35f,
        float trailWidth = 1f,
        Vector3 beamEnd = default,
        float beamNoise = 0f)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        int capacity = Math.Clamp(config.MaxParticles, 1, GpuParticleProtocol.MaximumCapacity);
        Vector4[] lookup = BuildLookup(config, meshSurfaceSamples);
        GpuParticleParameters parameters = BuildParameters(
            config, world, meshSurfaceSamples.Length, rendererKind, trailDuration, trailWidth, beamEnd, beamNoise);
        return new GpuParticleDefinition
        {
            Capacity = capacity,
            Parameters = parameters,
            Lookup = lookup,
            DebugName = string.IsNullOrWhiteSpace(config.EmitterName) ? "Particles" : config.EmitterName,
            BlendMode = (int)config.BlendMode,
        };
    }

    public static GpuParticleDefinition WithWorld(GpuParticleDefinition definition, Matrix4x4 world)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            throw new ArgumentException("Particle emitter transform must be invertible.", nameof(world));
        GpuParticleParameters parameters = definition.Parameters;
        parameters.World = world;
        parameters.InverseWorld = inverse;
        return new GpuParticleDefinition
        {
            Capacity = definition.Capacity,
            Parameters = parameters,
            Lookup = definition.Lookup,
            DebugName = definition.DebugName,
            BlendMode = definition.BlendMode,
        };
    }

    public static GpuParticleParameters BuildParameters(
        ParticleConfig config,
        Matrix4x4 world,
        int meshSurfaceSampleCount = 0,
        int rendererKind = 0,
        float trailDuration = 0.35f,
        float trailWidth = 1f,
        Vector3 beamEnd = default,
        float beamNoise = 0f)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            throw new ArgumentException("Particle emitter transform must be invertible.", nameof(world));

        float collisionRadius = MathF.Max(0.001f,
            (float)Math.Max(config.StartSize, config.EndSize) * 0.5f);
        return new GpuParticleParameters
        {
            World = world,
            InverseWorld = inverse,
            Emission = new Vector4(
                (float)config.Shape,
                MathF.Max(0f, (float)config.EmitRadius),
                Math.Clamp((float)config.SpreadDegrees * (MathF.PI / 180f), 0f, MathF.PI),
                config.DownwardEmit ? 1f : 0f),
            Box = new Vector4(
                MathF.Max(0.0001f, (float)config.BoxSizeX),
                MathF.Max(0.0001f, (float)config.BoxSizeY),
                MathF.Max(0.0001f, (float)config.BoxSizeZ),
                Math.Max(0, meshSurfaceSampleCount)),
            Motion = new Vector4(
                MathF.Max(0f, (float)config.Speed),
                Math.Clamp((float)config.SpeedVariance, 0f, 1f),
                MathF.Max(0.05f, (float)config.Lifetime),
                Math.Clamp((float)config.LifetimeVariance, 0f, 0.99f)),
            Forces = new Vector4(
                (float)config.GravityX,
                (float)config.Gravity,
                (float)config.GravityZ,
                MathF.Max(0f, (float)config.Drag)),
            Wind = new Vector4(
                (float)config.WindX,
                (float)config.WindZ,
                MathF.Max(0f, (float)config.TurbulenceStrength),
                0f),
            Sizes = new Vector4(
                MathF.Max(0f, (float)config.StartSize),
                MathF.Max(0f, (float)config.EndSize),
                MathF.Max(0.0001f, (float)config.SizeXScale),
                MathF.Max(0.0001f, (float)config.SizeYScale)),
            Rotation = new Vector4(
                (float)config.RotationSpeed * (MathF.PI / 180f),
                (float)config.RotationVariance * MathF.PI,
                Math.Clamp((float)config.ColorJitter, 0f, 1f),
                MathF.Max(0f, (float)config.Emissive)),
            Collision = new Vector4(
                (float)config.CollisionMode,
                (float)config.CollisionPlaneHeight,
                Math.Clamp((float)config.CollisionBounce, 0f, 1.5f),
                collisionRadius),
            Render = new Vector4(
                Math.Clamp(rendererKind, 0, 3),
                (float)config.Alignment,
                MathF.Max(0.001f, trailDuration),
                0f),
            Flipbook = new Vector4(
                Math.Clamp(config.FlipbookColumns, 1, 64),
                Math.Clamp(config.FlipbookRows, 1, 64),
                MathF.Max(0f, (float)config.FlipbookFps),
                config.UseFlipbook ? 1f : 0f),
            // Existing authored particles are world-space: moving an emitter changes future births,
            // not particles already in flight. Options.y enables the authored collision plane.
            Options = new Vector4(
                0f,
                config.CollisionMode != ParticleCollisionMode.None ? 1f : 0f,
                MathF.Max(0.001f, trailWidth),
                config.Alignment == ParticleAlignment.Velocity ? 0.08f : 0f),
            Beam = new Vector4(beamEnd, MathF.Max(0f, beamNoise)),
            Geometry = new Vector4(BaseLookupCount, 0f, 0f, 0f),
        };
    }

    public static Vector4[] BuildLookup(ParticleConfig config, ReadOnlySpan<Vector3> meshSurfaceSamples = default)
    {
        if (config == null) throw new ArgumentNullException(nameof(config));
        int meshCount = Math.Min(meshSurfaceSamples.Length, 100_000);
        Vector4[] lookup = new Vector4[checked(BaseLookupCount + meshCount)];

        for (int i = 0; i < GpuParticleProtocol.CurveSamples; i++)
        {
            float t = i / (float)(GpuParticleProtocol.CurveSamples - 1);
            float size = config.UseCustomSizeCurve
                ? config.SizeOverLifetime?.Evaluate(t) ?? t
                : ParticleCurveMath.Evaluate(config.SizeCurve, t);
            float speed = config.UseCustomSpeedCurve
                ? config.SpeedOverLifetime?.Evaluate(t) ?? 1f
                : 1f;
            float alphaTime = config.UseCustomAlphaCurve
                ? config.AlphaOverLifetime?.Evaluate(t) ?? t
                : ParticleCurveMath.Evaluate(config.AlphaCurve, t);
            float velocity = config.UseCustomVelocityCurve
                ? config.VelocityOverLifetime?.Evaluate(t) ?? 1f
                : 1f;

            lookup[i] = new Vector4(
                Math.Clamp(size, -16f, 16f),
                Math.Clamp(speed, 0f, 16f),
                Math.Clamp(alphaTime, 0f, 1f),
                Math.Clamp(velocity, 0f, 16f));

            ParticleColor colour = EvaluateColour(config, t, alphaTime);
            lookup[GpuParticleProtocol.CurveSamples + i] = new Vector4(
                Math.Clamp(colour.R, 0f, 1f),
                Math.Clamp(colour.G, 0f, 1f),
                Math.Clamp(colour.B, 0f, 1f),
                Math.Clamp(colour.A, 0f, 1f));
        }

        for (int i = 0; i < meshCount; i++)
            lookup[BaseLookupCount + i] = new Vector4(meshSurfaceSamples[i], 1f);

        return lookup;
    }

    private static ParticleColor EvaluateColour(ParticleConfig config, float t, float alphaTime)
    {
        ParticleColor rgb = EvaluateStopsOrLegacy(config, t);
        ParticleColor alpha = MathF.Abs(alphaTime - t) < 0.00001f
            ? rgb
            : EvaluateStopsOrLegacy(config, alphaTime);
        return new ParticleColor(rgb.R, rgb.G, rgb.B, alpha.A);
    }

    private static ParticleColor EvaluateStopsOrLegacy(ParticleConfig config, float t)
    {
        List<ParticleGradientStop> stops = config.GradientStops ?? [];
        ParticleGradientStop before = null;
        ParticleGradientStop after = null;
        for (int i = 0; i < stops.Count; i++)
        {
            ParticleGradientStop stop = stops[i];
            if (stop?.Color == null) continue;
            double p = Math.Clamp(stop.Position, 0d, 1d);
            if (p <= t && (before == null || p > before.Position)) before = stop;
            if (p >= t && (after == null || p < after.Position)) after = stop;
        }
        if (before != null || after != null)
        {
            before ??= after;
            after ??= before;
            double a = Math.Clamp(before.Position, 0d, 1d);
            double b = Math.Clamp(after.Position, 0d, 1d);
            float amount = b - a <= 0.00001
                ? 0f
                : Math.Clamp((float)((t - a) / (b - a)), 0f, 1f);
            return Lerp(before.Color, after.Color, amount);
        }

        ParticleColor start = config.StartColor ?? new ParticleColor();
        ParticleColor end = config.EndColor ?? new ParticleColor(1f, 1f, 1f, 0f);
        ParticleColor middle = config.MidColor;
        float midpoint = Math.Clamp((float)config.ColorMidpoint, 0.0001f, 0.9999f);
        if (middle == null)
            return Lerp(start, end, t);
        return t <= midpoint
            ? Lerp(start, middle, t / midpoint)
            : Lerp(middle, end, (t - midpoint) / (1f - midpoint));
    }

    private static ParticleColor Lerp(ParticleColor a, ParticleColor b, float amount) => new(
        a.R + (b.R - a.R) * amount,
        a.G + (b.G - a.G) * amount,
        a.B + (b.B - a.B) * amount,
        a.A + (b.A - a.A) * amount);
}
