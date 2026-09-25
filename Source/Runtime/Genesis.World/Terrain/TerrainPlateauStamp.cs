using System;
using System.Numerics;

namespace Genesis.World.Terrain;

/// <summary>
/// Deterministic, bounded terrain grading for buildable clearings. Unlike a flatten brush this
/// preserves a fixed inner platform and blends it into the source terrain across an outer band;
/// it deliberately leaves splat weights untouched so grassy town pads remain grassy.
/// </summary>
public sealed class TerrainPlateauStamp
{
    public TerrainPlateauStamp(Vector2 center, float targetHeight, float innerRadius, float outerRadius)
    {
        if (!float.IsFinite(targetHeight)) throw new ArgumentOutOfRangeException(nameof(targetHeight));
        if (!float.IsFinite(innerRadius) || innerRadius < 0f) throw new ArgumentOutOfRangeException(nameof(innerRadius));
        if (!float.IsFinite(outerRadius) || outerRadius <= innerRadius) throw new ArgumentOutOfRangeException(nameof(outerRadius));
        Center = center;
        TargetHeight = targetHeight;
        InnerRadius = innerRadius;
        OuterRadius = outerRadius;
    }

    public Vector2 Center { get; }
    public float TargetHeight { get; }
    public float InnerRadius { get; }
    public float OuterRadius { get; }

    public float Apply(float worldX, float worldZ, float naturalHeight)
    {
        float influence = Influence(worldX, worldZ);
        return naturalHeight + (TargetHeight - naturalHeight) * influence;
    }

    public float Influence(float worldX, float worldZ)
    {
        float distance = Vector2.Distance(new Vector2(worldX, worldZ), Center);
        if (distance <= InnerRadius) return 1f;
        if (distance >= OuterRadius) return 0f;
        float t = (distance - InnerRadius) / (OuterRadius - InnerRadius);
        return 1f - t * t * (3f - 2f * t);
    }

    public void ApplyTo(TerrainAsset terrain)
    {
        if (terrain == null) throw new ArgumentNullException(nameof(terrain));
        int minX = Math.Max(0, (int)MathF.Floor((Center.X - OuterRadius - terrain.OriginX) / terrain.CellSize));
        int maxX = Math.Min(terrain.ResolutionX - 1, (int)MathF.Ceiling((Center.X + OuterRadius - terrain.OriginX) / terrain.CellSize));
        int minZ = Math.Max(0, (int)MathF.Floor((Center.Y - OuterRadius - terrain.OriginZ) / terrain.CellSize));
        int maxZ = Math.Min(terrain.ResolutionZ - 1, (int)MathF.Ceiling((Center.Y + OuterRadius - terrain.OriginZ) / terrain.CellSize));
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            float worldX = terrain.OriginX + x * terrain.CellSize;
            float worldZ = terrain.OriginZ + z * terrain.CellSize;
            terrain.SetHeight(x, z, Apply(worldX, worldZ, terrain.GetHeight(x, z)));
        }
    }
}
