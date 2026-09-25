using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Rendering.Particles;

namespace Genesis.Runtime.Particles;

/// <summary>
/// Converts the ordinary authored ParticleConfig into the backend-neutral GPU particle protocol.
/// This is the only config-to-GPU mapping used by Studio preview and the Player.
/// </summary>
public static class GpuParticleDefinitionBuilder
{
    public static GpuParticleDefinition Build(
        ParticleConfig config,
        Matrix4x4 world,
        ReadOnlySpan<Vector3> meshSurfaceSamples = default,
        ReadOnlySpan<Vector3> collisionTriangleVertices = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            throw new ArgumentException("Particle emitter transform must be invertible.", nameof(world));
        if (collisionTriangleVertices.Length % 3 != 0)
            throw new ArgumentException("Collision geometry must contain complete triangles.", nameof(collisionTriangleVertices));

        List<Vector4> lookup = new(GpuParticleProtocol.CurveSamples * 2
            + meshSurfaceSamples.Length + collisionTriangleVertices.Length + 3);

        BuildCurveTable(config, lookup);
        BuildColourTable(config, lookup);

        int meshOffset = lookup.Count;
        int meshCount = Math.Min(meshSurfaceSamples.Length, 100_000);
        for (int i = 0; i < meshCount; i++)
            lookup.Add(new Vector4(meshSurfaceSamples[i], 0f));

        int collisionRoot = 0;
        int collisionNodeCount = 0;
        int collisionTrianglesOffset = 0;
        int triangleCount = collisionTriangleVertices.Length / 3;
        if (triangleCount > 0)
        {
            collisionRoot = lookup.Count;
            collisionNodeCount = 1;
            collisionTrianglesOffset = collisionRoot + 3;

            Vector3 lo = new(float.PositiveInfinity);
            Vector3 hi = new(float.NegativeInfinity);
            for (int i = 0; i < collisionTriangleVertices.Length; i++)
            {
                lo = Vector3.Min(lo, collisionTriangleVertices[i]);
                hi = Vector3.Max(hi, collisionTriangleVertices[i]);
            }

            // One conservative root leaf. It is deliberately exact rather than truncating
            // geometry; a later acceleration-structure upgrade can subdivide this representation
            // without changing the shader protocol.
            lookup.Add(new Vector4(lo, 1f));              // miss -> node index 1 => end
            lookup.Add(new Vector4(hi, 0f));              // first triangle index
            lookup.Add(new Vector4(triangleCount, 0f, 0f, 0f));
            for (int i = 0; i < collisionTriangleVertices.Length; i++)
                lookup.Add(new Vector4(collisionTriangleVertices[i], 0f));
        }

        float collisionMode = config.CollisionMode switch
        {
            ParticleCollisionMode.Bounce => 1f,
            ParticleCollisionMode.Die => 2f,
            ParticleCollisionMode.Stick => 3f,
            _ => 0f,
        };

        var parameters = new GpuParticleParameters
        {
            World = world,
            InverseWorld = inverse,
            Emission = new Vector4(
                (float)config.Shape,
                (float)Math.Max(0d, config.EmitRadius),
                DegreesToRadians((float)config.SpreadDegrees),
                config.DownwardEmit ? 1f : 0f),
            Box = new Vector4(
                (float)Math.Max(0d, config.BoxSizeX),
                (float)Math.Max(0d, config.BoxSizeY),
                (float)Math.Max(0d, config.BoxSizeZ),
                meshCount),
            Motion = new Vector4(
                (float)Math.Max(0d, config.Speed),
                (float)Math.Clamp(config.SpeedVariance, 0d, 1d),
                (float)Math.Max(0.05d, config.Lifetime),
                (float)Math.Clamp(config.LifetimeVariance, 0d, 0.99d)),
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
                (float)Math.Max(0.001d, config.SizeXScale),
                (float)Math.Max(0.001d, config.SizeYScale)),
            Rotation = new Vector4(
                DegreesToRadians((float)config.RotationSpeed),
                (float)Math.Clamp(config.RotationVariance, 0d, 4d) * MathF.PI,
                (float)Math.Clamp(config.ColorJitter, 0d, 1d),
                (float)Math.Max(0d, config.Emissive) - 1f),
            Collision = new Vector4(
                collisionMode,
                (float)config.CollisionPlaneHeight,
                (float)Math.Clamp(config.CollisionBounce, 0d, 1.5d),
                MathF.Max(0.001f, (float)Math.Min(config.StartSize, Math.Max(config.EndSize, 0.001)) * 0.25f)),
            Render = new Vector4(
                (float)config.RendererKind,
                (float)config.Alignment,
                (float)Math.Max(0.01d, config.TrailDuration),
                (float)Math.Max(0.001d, config.RibbonMaxSegmentLength)),
            Flipbook = new Vector4(
                Math.Clamp(config.FlipbookColumns, 1, 64),
                Math.Clamp(config.FlipbookRows, 1, 64),
                (float)Math.Max(0d, config.FlipbookFps),
                config.UseFlipbook ? 1f : 0f),
            Options = new Vector4(
                config.SimulationSpace == ParticleSimulationSpace.Local ? 1f : 0f,
                config.CollisionMode != ParticleCollisionMode.None ? 1f : 0f,
                (float)Math.Max(0.001d, config.TrailWidth),
                (float)Math.Max(0d, config.VelocityStretch)),
            Beam = new Vector4(
                (float)config.BeamEndX,
                (float)config.BeamEndY,
                (float)config.BeamEndZ,
                (float)Math.Max(0d, config.BeamNoise)),
            Geometry = new Vector4(
                meshOffset,
                collisionRoot,
                collisionNodeCount,
                collisionTrianglesOffset),
        };

        return new GpuParticleDefinition
        {
            Capacity = Math.Clamp(config.MaxParticles, 1, GpuParticleProtocol.MaximumCapacity),
            Parameters = parameters,
            Lookup = lookup.ToArray(),
            DebugName = string.IsNullOrWhiteSpace(config.EmitterName) ? "Particles" : config.EmitterName,
            BlendMode = (int)config.BlendMode,
        };
    }

    private static void BuildCurveTable(ParticleConfig config, List<Vector4> output)
    {
        for (int i = 0; i < GpuParticleProtocol.CurveSamples; i++)
        {
            float t = i / (float)(GpuParticleProtocol.CurveSamples - 1);
            float size = config.UseCustomSizeCurve
                ? config.SizeOverLifetime?.Evaluate(t) ?? t
                : ParticleCurveMath.Evaluate(config.SizeCurve, t);
            float speed = config.UseCustomSpeedCurve
                ? config.SpeedOverLifetime?.Evaluate(t) ?? 1f
                : 1f;
            float alpha = config.UseCustomAlphaCurve
                ? config.AlphaOverLifetime?.Evaluate(t) ?? 1f
                : 1f;
            float velocity = config.UseCustomVelocityCurve
                ? config.VelocityOverLifetime?.Evaluate(t) ?? 1f
                : 1f;
            output.Add(new Vector4(
                Math.Clamp(size, -16f, 16f),
                Math.Clamp(speed, 0f, 16f),
                Math.Clamp(alpha, 0f, 16f),
                Math.Clamp(velocity, 0f, 16f)));
        }
    }

    private static void BuildColourTable(ParticleConfig config, List<Vector4> output)
    {
        List<ParticleGradientStop> stops = (config.GradientStops ?? [])
            .Where(stop => stop?.Color is not null)
            .OrderBy(stop => stop.Position)
            .Select(stop => stop.Clone())
            .ToList();

        if (stops.Count < 2)
        {
            stops =
            [
                new ParticleGradientStop { Position = 0, Color = config.StartColor?.Clone() ?? new ParticleColor() },
                new ParticleGradientStop { Position = 1, Color = config.EndColor?.Clone() ?? new ParticleColor(1,1,1,0) },
            ];
            if (config.MidColor is not null)
                stops.Insert(1, new ParticleGradientStop
                {
                    Position = Math.Clamp(config.ColorMidpoint, 0d, 1d),
                    Color = config.MidColor.Clone(),
                });
        }

        for (int i = 0; i < GpuParticleProtocol.CurveSamples; i++)
        {
            float t = i / (float)(GpuParticleProtocol.CurveSamples - 1);
            ParticleColor colour = SampleGradient(stops, t);
            float alphaT = config.UseCustomAlphaCurve
                ? Math.Clamp(config.AlphaOverLifetime?.Evaluate(t) ?? 1f, 0f, 1f)
                : ParticleCurveMath.Evaluate(config.AlphaCurve, t);
            float legacyAlpha = SampleGradient(stops, alphaT).A;
            colour.A = Math.Clamp(legacyAlpha, 0f, 1f);
            output.Add(new Vector4(colour.R, colour.G, colour.B, colour.A));
        }
    }

    private static ParticleColor SampleGradient(IReadOnlyList<ParticleGradientStop> stops, float time)
    {
        double t = Math.Clamp(time, 0f, 1f);
        if (stops.Count == 0) return new ParticleColor();
        if (t <= stops[0].Position) return stops[0].Color.Clone();
        if (t >= stops[^1].Position) return stops[^1].Color.Clone();

        for (int i = 1; i < stops.Count; i++)
        {
            if (t > stops[i].Position) continue;
            ParticleGradientStop left = stops[i - 1];
            ParticleGradientStop right = stops[i];
            double span = Math.Max(1e-9, right.Position - left.Position);
            float amount = (float)Math.Clamp((t - left.Position) / span, 0d, 1d);
            return new ParticleColor(
                Lerp(left.Color.R, right.Color.R, amount),
                Lerp(left.Color.G, right.Color.G, amount),
                Lerp(left.Color.B, right.Color.B, amount),
                Lerp(left.Color.A, right.Color.A, amount));
        }

        return stops[^1].Color.Clone();
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
}
