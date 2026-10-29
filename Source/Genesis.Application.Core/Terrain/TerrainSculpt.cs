using System.Numerics;
using Genesis.World.Terrain;

namespace Genesis.Application.Core.Terrain;

/// <summary>The Terrain editor's sculpt and paint brushes.</summary>
public enum TerrainSculptBrush
{
    Raise,
    Lower,
    Smooth,
    Flatten,
    Paint,
    Erase,
    InFill,
    /// <summary>Cuts the ground away: the cells under the brush are not drawn, solid or walkable.</summary>
    Hole,
    /// <summary>Puts cut-away ground back.</summary>
    FillHole,
}

/// <summary>How one dab of a brush behaves.</summary>
public sealed class TerrainSculptDab
{
    /// <summary>Radius in world units.</summary>
    public float Radius { get; init; } = 8f;

    /// <summary>0..1: how far each dab moves the ground (or paint) toward its target.</summary>
    public float Strength { get; init; } = 0.5f;

    /// <summary>"Smooth", "Linear" or "Hard".</summary>
    public string Falloff { get; init; } = "Smooth";

    /// <summary>The paint layer (0-based) the Paint brush lays down.</summary>
    public int Layer { get; init; }

    /// <summary>The height Flatten levels to: the ground where the stroke began.</summary>
    public float FlattenTarget { get; init; }

    /// <summary>A polygon (x, z) the brush stays inside; fewer than three points means anywhere.</summary>
    public IReadOnlyList<Vector2>? Selection { get; init; }

    /// <summary>The heights Erase restores (the terrain as it was opened); Erase does nothing without them.</summary>
    public ushort[]? EraseBaseline { get; init; }
}

/// <summary>
/// The sculpt and paint brushes, shared by the Terrain editor's mouse and the automation commands so
/// a stroke made either way moves the ground the same.
/// </summary>
public static class TerrainSculpt
{
    /// <summary>Applies one dab at a world position; returns the cells it covered.</summary>
    public static (int MinX, int MaxX, int MinZ, int MaxZ) Apply(TerrainAsset terrain, TerrainSculptBrush brush, float worldX, float worldZ, TerrainSculptDab dab)
    {
        float radius = dab.Radius, strength = dab.Strength;
        var bounds = CellBounds(terrain, worldX, worldZ, radius);
        IReadOnlyList<Vector2> selection = dab.Selection ?? [];
        if (brush is TerrainSculptBrush.Hole or TerrainSculptBrush.FillHole)
        {
            // Whole cells, cut or filled where their middle lies within the brush (and the selection).
            for (int z = bounds.MinZ; z < Math.Min(bounds.MaxZ + 1, terrain.CellsZ); z++)
            for (int x = bounds.MinX; x < Math.Min(bounds.MaxX + 1, terrain.CellsX); x++)
            {
                float wx = terrain.OriginX + (x + 0.5f) * terrain.CellSize, wz = terrain.OriginZ + (z + 0.5f) * terrain.CellSize;
                if (Vector2.Distance(new(wx, wz), new(worldX, worldZ)) > radius
                    || (selection.Count >= 3 && !InsidePolygon(selection, wx, wz))) continue;
                terrain.SetHole(x, z, brush == TerrainSculptBrush.Hole);
            }

            return bounds;
        }

        ushort[] baseline = dab.EraseBaseline ?? [];
        // Smooth reads a snapshot of the brush footprint plus a one-sample border, not a copy of
        // the entire heightfield per dab.
        int windowMinX = Math.Max(0, bounds.MinX - 1), windowMinZ = Math.Max(0, bounds.MinZ - 1);
        int windowMaxX = Math.Min(terrain.ResolutionX - 1, bounds.MaxX + 1), windowMaxZ = Math.Min(terrain.ResolutionZ - 1, bounds.MaxZ + 1);
        int windowWidth = Math.Max(0, windowMaxX - windowMinX + 1);
        float[]? smooth = brush == TerrainSculptBrush.Smooth ? new float[windowWidth * Math.Max(0, windowMaxZ - windowMinZ + 1)] : null;
        if (smooth is not null) for (int z = windowMinZ; z <= windowMaxZ; z++) for (int x = windowMinX; x <= windowMaxX; x++) smooth[(z - windowMinZ) * windowWidth + (x - windowMinX)] = terrain.GetHeight(x, z);
        float rim = 0;
        if (brush == TerrainSculptBrush.InFill) for (int i = 0; i < 16; i++) rim += terrain.SampleHeight(worldX + MathF.Cos(i * MathF.Tau / 16) * radius, worldZ + MathF.Sin(i * MathF.Tau / 16) * radius) / 16;
        for (int z = bounds.MinZ; z <= bounds.MaxZ; z++) for (int x = bounds.MinX; x <= bounds.MaxX; x++)
        {
            float wx = terrain.OriginX + x * terrain.CellSize, wz = terrain.OriginZ + z * terrain.CellSize;
            float distance = Vector2.Distance(new(wx, wz), new(worldX, worldZ));
            if (distance > radius || (selection.Count >= 3 && !InsidePolygon(selection, wx, wz))) continue;
            float blend = Math.Clamp(strength * Falloff(dab.Falloff, distance, radius), 0, 1);
            int at = z * terrain.ResolutionX + x;
            if (brush == TerrainSculptBrush.Paint)
            {
                terrain.BlendLayerAt(at, Math.Clamp(dab.Layer, 0, TerrainAsset.MaximumPaintLayers - 1), blend);
                continue;
            }
            float current = terrain.GetHeight(x, z), target = current;
            if (brush == TerrainSculptBrush.Raise) target += 1.6f;
            if (brush == TerrainSculptBrush.Lower) target -= 1.6f;
            if (brush == TerrainSculptBrush.Flatten) target = dab.FlattenTarget;
            if (brush == TerrainSculptBrush.InFill) target = Math.Max(current, rim);
            if (brush == TerrainSculptBrush.Erase && baseline.Length == terrain.HeightsData.Length) target = terrain.MinHeight + baseline[at] / (float)ushort.MaxValue * (terrain.MaxHeight - terrain.MinHeight);
            if (smooth is not null)
            {
                float sum = 0; int count = 0;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++) { int sx = Math.Clamp(x + dx, windowMinX, windowMaxX), sz = Math.Clamp(z + dz, windowMinZ, windowMaxZ); sum += smooth[(sz - windowMinZ) * windowWidth + (sx - windowMinX)]; count++; }
                target = sum / count;
            }
            terrain.SetHeight(x, z, float.Lerp(current, target, blend));
        }
        return bounds;
    }

    public static float Falloff(string falloff, float distance, float radius)
    {
        float t = Math.Clamp(1 - distance / Math.Max(.001f, radius), 0, 1);
        return falloff == "Hard" ? 1 : falloff == "Linear" ? t : t * t * (3 - 2 * t);
    }

    public static (int MinX, int MaxX, int MinZ, int MaxZ) CellBounds(TerrainAsset terrain, float x, float z, float radius) => (
        Math.Max(0, (int)MathF.Floor((x - radius - terrain.OriginX) / terrain.CellSize)),
        Math.Min(terrain.ResolutionX - 1, (int)MathF.Ceiling((x + radius - terrain.OriginX) / terrain.CellSize)),
        Math.Max(0, (int)MathF.Floor((z - radius - terrain.OriginZ) / terrain.CellSize)),
        Math.Min(terrain.ResolutionZ - 1, (int)MathF.Ceiling((z + radius - terrain.OriginZ) / terrain.CellSize)));

    public static bool InsidePolygon(IReadOnlyList<Vector2> points, float x, float z)
    {
        bool inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[i]; var b = points[j];
            if ((a.Y > z) != (b.Y > z) && x < (b.X - a.X) * (z - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
