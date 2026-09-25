using System;
using System.Numerics;

namespace Genesis.World.Terrain;

/// <summary>
/// Deterministic, bounded, subtractive terrain stamp for authored lakes and ponds.
/// Ported from the Aetherforge settlement integration so Genesis terrain tools can guarantee a
/// water-bearing basin instead of depending on an accidental depression in the source heightmap.
/// </summary>
public sealed class TerrainBasinStamp
{
    public TerrainBasinStamp(Vector2 center, float surfaceHeight, float radius, float maximumDepth, float outerBlend)
    {
        if (!float.IsFinite(surfaceHeight)) throw new ArgumentOutOfRangeException(nameof(surfaceHeight));
        if (!float.IsFinite(radius) || radius <= 1f) throw new ArgumentOutOfRangeException(nameof(radius));
        if (!float.IsFinite(maximumDepth) || maximumDepth <= 0f) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (!float.IsFinite(outerBlend) || outerBlend <= 0f) throw new ArgumentOutOfRangeException(nameof(outerBlend));
        Center = center;
        SurfaceHeight = surfaceHeight;
        Radius = radius;
        MaximumDepth = maximumDepth;
        OuterBlend = outerBlend;
    }

    public Vector2 Center { get; }
    public float SurfaceHeight { get; }
    public float Radius { get; }
    public float MaximumDepth { get; }
    public float OuterBlend { get; }
    public float InfluenceRadius => Radius + OuterBlend;

    public float Apply(float worldX, float worldZ, float naturalHeight)
    {
        float distance = Vector2.Distance(new Vector2(worldX, worldZ), Center);
        if (distance >= InfluenceRadius) return naturalHeight;
        float radial = Math.Clamp(distance / Radius, 0f, 1f);
        float bowl = SurfaceHeight - MaximumDepth * (1f - radial * radial);
        if (distance <= Radius) return MathF.Min(naturalHeight, bowl);
        float blend = SmoothStep(Radius, InfluenceRadius, distance);
        return MathF.Min(naturalHeight, bowl + (naturalHeight - bowl) * blend);
    }

    public float Influence(float worldX, float worldZ)
    {
        float distance = Vector2.Distance(new Vector2(worldX, worldZ), Center);
        if (distance <= Radius) return 1f;
        if (distance >= InfluenceRadius) return 0f;
        return 1f - SmoothStep(Radius, InfluenceRadius, distance);
    }

    public void ApplyTo(TerrainAsset terrain)
    {
        if (terrain == null) throw new ArgumentNullException(nameof(terrain));
        int minX = Math.Max(0, (int)MathF.Floor((Center.X - InfluenceRadius - terrain.OriginX) / terrain.CellSize));
        int maxX = Math.Min(terrain.ResolutionX - 1, (int)MathF.Ceiling((Center.X + InfluenceRadius - terrain.OriginX) / terrain.CellSize));
        int minZ = Math.Max(0, (int)MathF.Floor((Center.Y - InfluenceRadius - terrain.OriginZ) / terrain.CellSize));
        int maxZ = Math.Min(terrain.ResolutionZ - 1, (int)MathF.Ceiling((Center.Y + InfluenceRadius - terrain.OriginZ) / terrain.CellSize));
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            float worldX = terrain.OriginX + x * terrain.CellSize;
            float worldZ = terrain.OriginZ + z * terrain.CellSize;
            float current = terrain.GetHeight(x, z);
            terrain.SetHeight(x, z, Apply(worldX, worldZ, current));
        }
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / MathF.Max(0.001f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
