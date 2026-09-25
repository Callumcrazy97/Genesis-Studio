using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Rendering.Particles;

namespace Genesis.Runtime.Particles;

public static class ParticleGpuDefinitionBuilder
{
    public static Matrix4x4 EmitterWorld(ParticleConfig config, Vector3 origin)
    {
        ArgumentNullException.ThrowIfNull(config);
        Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(
            (float)(config.EmitterYaw * Math.PI / 180.0),
            (float)(config.EmitterPitch * Math.PI / 180.0),
            (float)(config.EmitterRoll * Math.PI / 180.0));
        Vector3 translation = origin + new Vector3(
            (float)config.EmitterOffsetX,
            (float)config.EmitterOffsetY,
            (float)config.EmitterOffsetZ);
        return rotation * Matrix4x4.CreateTranslation(translation);
    }

    public static GpuParticleDefinition Build(
        ParticleConfig config,
        Matrix4x4 world,
        ReadOnlySpan<Vector3> meshSurfaceSamples = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            inverse = Matrix4x4.Identity;

        int sampleCount = Math.Min(meshSurfaceSamples.Length, 65_536);
        Vector4[] lookup = new Vector4[GpuParticleProtocol.CurveSamples * 2 + sampleCount];
        for (int i = 0; i < GpuParticleProtocol.CurveSamples; i++)
        {
            float t = i / (float)(GpuParticleProtocol.CurveSamples - 1);
            float size = config.UseCustomSizeCurve
                ? config.SizeOverLifetime?.Evaluate(t) ?? t
                : ParticleCurveMath.Evaluate(config.SizeCurve, t);
            float speed = config.UseCustomSpeedCurve
                ? MathF.Max(0.001f, config.SpeedOverLifetime?.Evaluate(t) ?? 1f)
                : 1f;
            float alpha = config.UseCustomAlphaCurve
                ? Math.Clamp(config.AlphaOverLifetime?.Evaluate(t) ?? 1f, 0f, 1f)
                : ParticleCurveMath.Evaluate(config.AlphaCurve, t);
            float velocity = config.UseCustomVelocityCurve
                ? MathF.Max(0f, config.VelocityOverLifetime?.Evaluate(t) ?? 1f)
                : 1f;
            lookup[i] = new Vector4(size, speed, alpha, velocity);

            ParticleColor colour = SampleGradient(config, t);
            lookup[GpuParticleProtocol.CurveSamples + i] =
                new Vector4(colour.R, colour.G, colour.B, colour.A * alpha);
        }
        for (int i = 0; i < sampleCount; i++)
            lookup[GpuParticleProtocol.CurveSamples * 2 + i] = new Vector4(meshSurfaceSamples[i], 1f);

        var parameters = new GpuParticleParameters
        {
            World = world,
            InverseWorld = inverse,
            Emission = new Vector4(
                (float)config.Shape,
                (float)Math.Max(0d, config.EmitRadius),
                (float)(config.SpreadDegrees * Math.PI / 180.0),
                config.DownwardEmit ? 1f : 0f),
            Box = new Vector4(
                (float)Math.Max(.001, config.BoxSizeX),
                (float)Math.Max(.001, config.BoxSizeY),
                (float)Math.Max(.001, config.BoxSizeZ),
                sampleCount),
            Motion = new Vector4(
                (float)Math.Max(0d, config.Speed),
                (float)Math.Clamp(config.SpeedVariance, 0d, 1d),
                (float)Math.Max(.01, config.Lifetime),
                (float)Math.Clamp(config.LifetimeVariance, 0d, .99d)),
            Forces = new Vector4(
                (float)config.GravityX,
                (float)config.Gravity,
                (float)config.GravityZ,
                (float)Math.Max(0d, config.Drag)),
            Wind = new Vector4(
                (float)config.WindX,
                (float)config.WindZ,
                (float)Math.Max(0d, config.TurbulenceStrength),
                0f),
            Sizes = new Vector4(
                (float)Math.Max(0d, config.StartSize),
                (float)Math.Max(0d, config.EndSize),
                (float)Math.Max(.001, config.SizeXScale),
                (float)Math.Max(.001, config.SizeYScale)),
            Rotation = new Vector4(
                (float)(config.RotationSpeed * Math.PI / 180.0),
                (float)(Math.Abs(config.RotationVariance) * Math.PI / 180.0),
                (float)Math.Clamp(config.ColorJitter, 0d, 1d),
                (float)Math.Max(0d, config.Emissive)),
            Collision = new Vector4(
                (float)config.CollisionMode,
                (float)config.CollisionPlaneHeight,
                (float)Math.Clamp(config.CollisionBounce, 0d, 1.5d),
                (float)Math.Max(0d, config.CollisionRadius)),
            Render = new Vector4(
                (float)config.RendererMode,
                (float)config.Alignment,
                (float)Math.Max(.001, config.TrailDuration),
                (float)Math.Max(.001, config.RibbonMaxSegmentLength)),
            Flipbook = new Vector4(
                Math.Clamp(config.FlipbookColumns, 1, 64),
                Math.Clamp(config.FlipbookRows, 1, 64),
                (float)Math.Max(0d, config.FlipbookFps),
                config.UseFlipbook ? 1f : 0f),
            Options = new Vector4(
                config.LocalSpace ? 1f : 0f,
                config.CollisionMode != ParticleCollisionMode.None ? 1f : 0f,
                (float)Math.Max(.001, config.TrailWidth),
                (float)Math.Max(0d, config.VelocityStretch)),
            Beam = new Vector4(
                (float)config.BeamEndX,
                (float)config.BeamEndY,
                (float)config.BeamEndZ,
                (float)Math.Max(0d, config.BeamNoise)),
            Geometry = new Vector4(GpuParticleProtocol.CurveSamples * 2, 0, 0, 0),
        };

        return new GpuParticleDefinition
        {
            Capacity = Math.Clamp(config.MaxParticles, 1, GpuParticleProtocol.MaximumCapacity),
            Parameters = parameters,
            Lookup = lookup,
            DebugName = string.IsNullOrWhiteSpace(config.EmitterName) ? "Particles" : config.EmitterName,
            BlendMode = (int)config.BlendMode,
        };
    }

    public static GpuParticleDefinition WithWorld(GpuParticleDefinition definition, Matrix4x4 world)
    {
        ArgumentNullException.ThrowIfNull(definition);
        GpuParticleParameters parameters = definition.Parameters;
        parameters.World = world;
        parameters.InverseWorld = Matrix4x4.Invert(world, out Matrix4x4 inverse)
            ? inverse
            : Matrix4x4.Identity;
        return new GpuParticleDefinition
        {
            Capacity = definition.Capacity,
            Parameters = parameters,
            Lookup = definition.Lookup,
            DebugName = definition.DebugName,
            BlendMode = definition.BlendMode,
        };
    }

    private static ParticleColor SampleGradient(ParticleConfig config, float time)
    {
        float t = Math.Clamp(time, 0f, 1f);
        List<ParticleGradientStop> stops = config.GradientStops ?? [];
        if (stops.Count >= 2)
        {
            ParticleGradientStop left = stops[0];
            ParticleGradientStop right = stops[^1];
            for (int i = 1; i < stops.Count; i++)
            {
                if (t > stops[i].Position) continue;
                left = stops[i - 1];
                right = stops[i];
                break;
            }
            float width = (float)Math.Max(1e-6, right.Position - left.Position);
            float u = Math.Clamp((t - (float)left.Position) / width, 0f, 1f);
            return Lerp(left.Color ?? new ParticleColor(), right.Color ?? new ParticleColor(), u);
        }

        ParticleColor start = config.StartColor ?? new ParticleColor();
        ParticleColor end = config.EndColor ?? new ParticleColor(1, 1, 1, 0);
        if (config.MidColor is null)
            return Lerp(start, end, t);
        float middle = (float)Math.Clamp(config.ColorMidpoint, .001, .999);
        return t <= middle
            ? Lerp(start, config.MidColor, t / middle)
            : Lerp(config.MidColor, end, (t - middle) / (1f - middle));
    }

    private static ParticleColor Lerp(ParticleColor a, ParticleColor b, float t) => new(
        a.R + (b.R - a.R) * t,
        a.G + (b.G - a.G) * t,
        a.B + (b.B - a.B) * t,
        a.A + (b.A - a.A) * t);
}
