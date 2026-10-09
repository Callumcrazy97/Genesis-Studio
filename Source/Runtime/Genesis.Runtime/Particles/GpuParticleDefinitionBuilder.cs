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
    /// <summary>Reused between builds on a thread: a running game rebuilds every emitter's definition each frame.</summary>
    [ThreadStatic] private static List<Vector4> t_lookup;
    [ThreadStatic] private static List<(double Position, Vector4 Color)> t_stops;

    /// <param name="previousLookup">
    /// The lookup table of this emitter's last definition. When the new table holds the same
    /// values the same array is used again, so an emitter whose curves and colours have not
    /// changed allocates no table.
    /// </param>
    public static GpuParticleDefinition Build(
        ParticleConfig config,
        Matrix4x4 world,
        ReadOnlySpan<Vector3> meshSurfaceSamples = default,
        ReadOnlySpan<Vector3> collisionTriangleVertices = default,
        Vector4[] previousLookup = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            throw new ArgumentException("Particle emitter transform must be invertible.", nameof(world));
        if (collisionTriangleVertices.Length % 3 != 0)
            throw new ArgumentException("Collision geometry must contain complete triangles.", nameof(collisionTriangleVertices));

        List<Vector4> lookup = t_lookup ??= new List<Vector4>(GpuParticleProtocol.CurveSamples * 2 + 3);
        lookup.Clear();
        lookup.EnsureCapacity(GpuParticleProtocol.CurveSamples * 2
            + meshSurfaceSamples.Length + collisionTriangleVertices.Length + 3);

        BuildCurveTable(config, lookup);
        BuildColourTable(config, lookup);

        int meshOffset = lookup.Count;
        int meshCount = Math.Min(meshSurfaceSamples.Length, 100_000);
        for (int i = 0; i < meshCount; i++)
        {
            Vector3 sample = meshSurfaceSamples[i];
            if (config.IsPlanar2D) sample = new Vector3(sample.X, sample.Y, 0) * Particle2DLayout.PixelsPerUnit;
            lookup.Add(new Vector4(sample, 0f));
        }

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

        GpuParticleParameters parameters = BuildParameters(config, world, inverse, meshCount,
            new Vector4(meshOffset, collisionRoot, collisionNodeCount, collisionTrianglesOffset));

        ComputeBounds(config, world, out Vector3 boundsCenter, out float boundsRadius);
        ReadOnlySpan<Vector4> built = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(lookup);
        Vector4[] table = previousLookup != null && built.SequenceEqual(previousLookup) ? previousLookup : built.ToArray();
        if (lookup.Capacity > 65_536) { lookup.Clear(); lookup.Capacity = GpuParticleProtocol.CurveSamples * 2 + 3; }
        return new GpuParticleDefinition
        {
            Capacity = Math.Clamp(config.MaxParticles, 1, GpuParticleProtocol.MaximumCapacity),
            Parameters = parameters,
            Lookup = table,
            DebugName = string.IsNullOrWhiteSpace(config.EmitterName) ? "Particles" : config.EmitterName,
            BoundsCenter = boundsCenter,
            BoundsRadius = boundsRadius,
            BlendMode = (int)config.BlendMode,
            PointSampling = config.PixelSampling,
        };
    }

    /// <summary>
    /// The same emitter somewhere else, or with its wind changed: what depends on the transform
    /// and the emitter's own values is worked out again, while the curve and colour table, the
    /// capacity and the culling of <paramref name="previous"/> are kept (no table is built or
    /// allocated). Only for a definition built from this same <paramref name="config"/>, whose
    /// curves, colours and samples have not changed since.
    /// </summary>
    public static GpuParticleDefinition Move(GpuParticleDefinition previous, ParticleConfig config, Matrix4x4 world)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(config);
        if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
            throw new ArgumentException("Particle emitter transform must be invertible.", nameof(world));
        GpuParticleParameters kept = previous.Parameters;
        Vector3 boundsCenter = world.Translation;
        float boundsRadius = 0f;
        if (previous.BoundsRadius > 0f) ComputeBounds(config, world, out boundsCenter, out boundsRadius);
        return new GpuParticleDefinition
        {
            Capacity = previous.Capacity,
            Parameters = BuildParameters(config, world, inverse, kept.Box.W, kept.Geometry),
            Lookup = previous.Lookup,
            DebugName = previous.DebugName,
            BoundsCenter = boundsCenter,
            BoundsRadius = boundsRadius,
            BlendMode = (int)config.BlendMode,
            PointSampling = config.PixelSampling,
        };
    }

    /// <summary>
    /// A copy with another capacity that is never culled by its bounds: one emitter shared by
    /// bursts all over the world has no single place to be seen from.
    /// </summary>
    public static GpuParticleDefinition Unbounded(GpuParticleDefinition definition, int capacity)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new GpuParticleDefinition
        {
            Capacity = Math.Clamp(capacity, 1, GpuParticleProtocol.MaximumCapacity),
            Parameters = definition.Parameters,
            Lookup = definition.Lookup,
            DebugName = definition.DebugName,
            BoundsCenter = definition.BoundsCenter,
            BoundsRadius = 0f,
            BlendMode = definition.BlendMode,
            PointSampling = definition.PointSampling,
        };
    }

    private static GpuParticleParameters BuildParameters(ParticleConfig config, Matrix4x4 world, Matrix4x4 inverse,
        float meshCount, Vector4 geometry)
    {
        float collisionMode = config.CollisionMode switch
        {
            ParticleCollisionMode.Bounce => 1f,
            ParticleCollisionMode.Die => 2f,
            ParticleCollisionMode.Stick => 3f,
            _ => 0f,
        };

        return new GpuParticleParameters
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
                config.IsPlanar2D ? 1f : 0f),
            Sizes = new Vector4(
                (float)Math.Max(0d, config.StartSize),
                (float)Math.Max(0d, config.EndSize),
                (float)Math.Max(0.001d, config.SizeXScale) * (config.IsPlanar2D ? new Vector2(world.M11, world.M12).Length() : 1),
                (float)Math.Max(0.001d, config.SizeYScale) * (config.IsPlanar2D ? new Vector2(world.M21, world.M22).Length() : 1)),
            Rotation = new Vector4(
                DegreesToRadians((float)config.RotationSpeed),
                (float)Math.Clamp(config.RotationVariance, 0d, 4d) * MathF.PI,
                (float)Math.Clamp(config.ColorJitter, 0d, 1d),
                (float)Math.Max(0d, config.Emissive) - 1f),
            Collision = new Vector4(
                collisionMode,
                config.IsPlanar2D
                    ? Vector3.Transform(new Vector3(0, (float)config.CollisionPlaneHeight, 0), world).Y
                    : (float)config.CollisionPlaneHeight,
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
                // 1 plays the sheet; 2 also starts each particle on a random frame.
                config.UseFlipbook ? (config.FlipbookRandomStart ? 2f : 1f) : 0f),
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
            Geometry = geometry,
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
        // The stops, in position order (a stable sort, as OrderBy was), read without copying the
        // authored colours: this runs for every emitter on every frame of a game.
        List<(double Position, Vector4 Color)> stops = t_stops ??= new List<(double Position, Vector4 Color)>();
        stops.Clear();
        if (config.GradientStops != null)
        {
            foreach (ParticleGradientStop stop in config.GradientStops)
            {
                if (stop?.Color is null) continue;
                int at = stops.Count;
                while (at > 0 && Comparer<double>.Default.Compare(stops[at - 1].Position, stop.Position) > 0) at--;
                stops.Insert(at, (stop.Position, ToVector(stop.Color)));
            }
        }

        if (stops.Count < 2)
        {
            stops.Clear();
            stops.Add((0, config.StartColor is null ? Vector4.One : ToVector(config.StartColor)));
            stops.Add((1, config.EndColor is null ? new Vector4(1, 1, 1, 0) : ToVector(config.EndColor)));
            if (config.MidColor is not null)
                stops.Insert(1, (Math.Clamp(config.ColorMidpoint, 0d, 1d), ToVector(config.MidColor)));
        }

        for (int i = 0; i < GpuParticleProtocol.CurveSamples; i++)
        {
            float t = i / (float)(GpuParticleProtocol.CurveSamples - 1);
            Vector4 colour = SampleGradient(stops, t);
            float alphaT = config.UseCustomAlphaCurve
                ? Math.Clamp(config.AlphaOverLifetime?.Evaluate(t) ?? 1f, 0f, 1f)
                : ParticleCurveMath.Evaluate(config.AlphaCurve, t);
            float legacyAlpha = SampleGradient(stops, alphaT).W;
            output.Add(new Vector4(colour.X, colour.Y, colour.Z, Math.Clamp(legacyAlpha, 0f, 1f)));
        }
    }

    private static Vector4 ToVector(ParticleColor colour) => new(colour.R, colour.G, colour.B, colour.A);

    private static Vector4 SampleGradient(List<(double Position, Vector4 Color)> stops, float time)
    {
        double t = Math.Clamp(time, 0f, 1f);
        if (stops.Count == 0) return Vector4.One;
        if (t <= stops[0].Position) return stops[0].Color;
        if (t >= stops[^1].Position) return stops[^1].Color;

        for (int i = 1; i < stops.Count; i++)
        {
            if (t > stops[i].Position) continue;
            (double leftPosition, Vector4 left) = stops[i - 1];
            (double rightPosition, Vector4 right) = stops[i];
            double span = Math.Max(1e-9, rightPosition - leftPosition);
            float amount = (float)Math.Clamp((t - leftPosition) / span, 0d, 1d);
            return new Vector4(
                Lerp(left.X, right.X, amount),
                Lerp(left.Y, right.Y, amount),
                Lerp(left.Z, right.Z, amount),
                Lerp(left.W, right.W, amount));
        }

        return stops[^1].Color;
    }

    private static void ComputeBounds(ParticleConfig config, Matrix4x4 world, out Vector3 center, out float radius)
    {
        if (config.BoundsMode == ParticleBoundsMode.Custom)
        {
            Vector3 localCenter = new((float)config.BoundsCenterX, (float)config.BoundsCenterY, (float)config.BoundsCenterZ);
            Vector3 half = new(
                (float)Math.Max(0.01d, config.BoundsSizeX) * 0.5f,
                (float)Math.Max(0.01d, config.BoundsSizeY) * 0.5f,
                (float)Math.Max(0.01d, config.BoundsSizeZ) * 0.5f);
            center = Vector3.Transform(localCenter, world);
            float scale = LargestScale(world);
            radius = MathF.Max(0.01f, half.Length() * scale);
            return;
        }

        float shapeRadius = config.Shape switch
        {
            ParticleEmitShape.Box => new Vector3(
                (float)config.BoxSizeX, (float)config.BoxSizeY, (float)config.BoxSizeZ).Length() * 0.5f,
            ParticleEmitShape.Disc or ParticleEmitShape.Ring => (float)Math.Max(0d, config.EmitRadius),
            _ => 0f,
        };
        float maxLife = (float)Math.Max(0.05d, config.Lifetime * (1d + Math.Clamp(config.LifetimeVariance, 0d, 0.99d)));
        float maxSpeed = (float)Math.Max(0d, config.Speed * (1d + Math.Clamp(config.SpeedVariance, 0d, 1d)));
        float drift = new Vector2((float)config.WindX, (float)config.WindZ).Length() * maxLife;
        float motion = maxSpeed * maxLife + drift
            + (float)Math.Max(0d, config.TurbulenceStrength) * maxLife * maxLife * 0.5f;
        float size = (float)Math.Max(config.StartSize, config.EndSize)
            * (float)Math.Max(config.SizeXScale, config.SizeYScale);
        float trail = config.RendererKind == ParticleRendererKind.Trail
            ? maxSpeed * (float)Math.Max(0d, config.TrailDuration)
            : 0f;
        float localRadius = MathF.Max(0.25f, shapeRadius + motion + size + trail);
        center = Vector3.Transform(Vector3.Zero, world);
        radius = localRadius * LargestScale(world);
    }

    private static float LargestScale(Matrix4x4 world) => MathF.Max(
        MathF.Max(
            new Vector3(world.M11, world.M12, world.M13).Length(),
            new Vector3(world.M21, world.M22, world.M23).Length()),
        MathF.Max(0.0001f, new Vector3(world.M31, world.M32, world.M33).Length()));

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
}
