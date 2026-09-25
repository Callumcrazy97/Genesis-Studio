using System;
using System.Numerics;
using Genesis.Physics.Buoyancy;

namespace Genesis.World.Water;

/// <summary>
/// Links authored <see cref="WaterBody"/> assets to runtime <see cref="BuoyancyVolume"/> instances.
/// </summary>
public static class WaterBodyPhysicsBridge
{
    public static BuoyancyVolume Register(WaterBody body, WaterPhysicsIntegration integration, bool lava = false)
    {
        if (body == null)
            throw new ArgumentNullException(nameof(body));
        if (integration == null)
            throw new ArgumentNullException(nameof(integration));

        Bounds2D bounds = ResolveBounds(body);
        var volume = new BuoyancyVolume
        {
            Name = body.Name,
            SurfaceY = body.SurfaceY,
            BottomY = lava ? body.SurfaceY - 4f : float.NegativeInfinity,
            HorizontalBounds = bounds,
            FluidDensity = lava ? 2800f : 1000f,
            LinearDrag = lava ? 5.5f : 2.5f,
            AngularDrag = lava ? 2f : 1f,
            BuoyancyStrength = lava ? 0.75f : 1f,
        };

        if (body.Kind == WaterBodyKind.River && body.SplinePoints is { Length: >= 2 })
        {
            var flow = new FlowField
            {
                Mode = FlowMode.Spline,
                SplineRadius = MathF.Max(1f, body.RiverWidth * 0.5f),
            };
            foreach (Vector3 pt in body.SplinePoints)
                flow.SplinePoints.Add(new FlowSplinePoint(pt, 2.5f));
            volume.FlowField = flow;
        }

        return integration.AddVolume(volume);
    }

    private static Bounds2D ResolveBounds(WaterBody body)
    {
        return body.Kind switch
        {
            WaterBodyKind.Lake or WaterBodyKind.Ocean or WaterBodyKind.Reservoir => new Bounds2D(
                body.Center.X - body.SizeX * 0.5f,
                body.Center.Z - body.SizeZ * 0.5f,
                body.Center.X + body.SizeX * 0.5f,
                body.Center.Z + body.SizeZ * 0.5f),
            WaterBodyKind.River when body.SplinePoints is { Length: > 0 } => ResolveRiverBounds(body),
            _ => Bounds2D.Infinite,
        };
    }

    private static Bounds2D ResolveRiverBounds(WaterBody body)
    {
        float minimumX = float.MaxValue, minimumZ = float.MaxValue;
        float maximumX = float.MinValue, maximumZ = float.MinValue;
        for (int i = 0; i < body.SplinePoints.Length; i++)
        {
            Vector3 point = body.SplinePoints[i];
            float halfWidth = body.SplineWidths is { Length: > 0 }
                ? body.SplineWidths[Math.Min(i, body.SplineWidths.Length - 1)] * .5f
                : body.RiverWidth * .5f;
            minimumX = MathF.Min(minimumX, point.X - halfWidth);
            maximumX = MathF.Max(maximumX, point.X + halfWidth);
            minimumZ = MathF.Min(minimumZ, point.Z - halfWidth);
            maximumZ = MathF.Max(maximumZ, point.Z + halfWidth);
        }
        return new Bounds2D(minimumX, minimumZ, maximumX, maximumZ);
    }
}
