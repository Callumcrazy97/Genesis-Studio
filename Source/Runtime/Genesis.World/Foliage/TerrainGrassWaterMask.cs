using System;
using System.Collections.Generic;
using Genesis.World.Terrain;
using Genesis.World.Water;

namespace Genesis.World.Foliage;

/// <summary>
/// Keeps grass grown around the camera out of a terrain's authored water, as the scattered foliage
/// is kept out of it. Footprints are decoded once, and each cell only tests the water bodies whose
/// bounds reach it, so a cell far from water pays nothing and no test allocates.
/// </summary>
internal sealed class TerrainGrassWaterMask
{
    private sealed class Body
    {
        public TerrainWaterDefinition Water;
        public byte[] Footprint;
        public float MinX, MinZ, MaxX, MaxZ;
    }

    private readonly List<Body> _bodies = new();
    private readonly List<Body> _near = new();
    private readonly float _padding;

    public TerrainGrassWaterMask(IReadOnlyList<TerrainWaterDefinition> waters, float padding)
    {
        _padding = MathF.Max(0f, padding);
        if (waters == null) return;
        foreach (TerrainWaterDefinition water in waters)
        {
            if (water == null) continue;
            var body = new Body { Water = water };
            if (water.HasFootprint) body.Footprint = TerrainWaterDefinition.UnpackBits(water.Footprint);
            Bounds(water, body.Footprint != null, out body.MinX, out body.MinZ, out body.MaxX, out body.MaxZ);
            body.MinX -= _padding; body.MinZ -= _padding; body.MaxX += _padding; body.MaxZ += _padding;
            _bodies.Add(body);
        }
    }

    public bool IsEmpty => _bodies.Count == 0;

    /// <summary>Chooses the water bodies that reach the rectangle about to be grown, in terrain space.</summary>
    public void BeginCell(float minX, float minZ, float maxX, float maxZ)
    {
        _near.Clear();
        foreach (Body body in _bodies)
            if (body.MaxX >= minX && body.MinX <= maxX && body.MaxZ >= minZ && body.MinZ <= maxZ) _near.Add(body);
    }

    /// <summary>True when the point, in terrain space, is in or beside water chosen by <see cref="BeginCell"/>.</summary>
    public bool Excludes(float x, float z)
    {
        for (int i = 0; i < _near.Count; i++)
        {
            Body body = _near[i];
            if (x < body.MinX || x > body.MaxX || z < body.MinZ || z > body.MaxZ) continue;
            if (body.Footprint != null ? FootprintContains(body, x, z) : body.Water.ContainsHorizontal(x, z, _padding)) return true;
        }

        return false;
    }

    /// <summary>The same test as the water's own, on footprint bits decoded once.</summary>
    private bool FootprintContains(Body body, float x, float z)
    {
        TerrainWaterDefinition water = body.Water;
        float cell = water.FootprintCellSize;
        int cx = (int)MathF.Floor((x - water.FootprintOriginX) / cell);
        int cz = (int)MathF.Floor((z - water.FootprintOriginZ) / cell);
        if (_padding <= 0f)
            return TerrainWaterDefinition.CellMarked(body.Footprint, water.FootprintWidth, water.FootprintHeight, cx, cz);

        int padCells = (int)MathF.Ceiling(_padding / cell) + 1;
        float padSq = _padding * _padding;
        for (int iz = cz - padCells; iz <= cz + padCells; iz++)
        for (int ix = cx - padCells; ix <= cx + padCells; ix++)
        {
            if (!TerrainWaterDefinition.CellMarked(body.Footprint, water.FootprintWidth, water.FootprintHeight, ix, iz)) continue;
            float x0 = water.FootprintOriginX + ix * cell, z0 = water.FootprintOriginZ + iz * cell;
            float dx = x - Math.Clamp(x, x0, x0 + cell), dz = z - Math.Clamp(z, z0, z0 + cell);
            if (dx * dx + dz * dz <= padSq) return true;
        }

        return false;
    }

    /// <summary>A rectangle holding everything the water's own containment test can accept, before padding.</summary>
    private static void Bounds(TerrainWaterDefinition water, bool footprint, out float minX, out float minZ, out float maxX, out float maxZ)
    {
        if (footprint)
        {
            minX = water.FootprintOriginX;
            minZ = water.FootprintOriginZ;
            maxX = minX + water.FootprintWidth * water.FootprintCellSize;
            maxZ = minZ + water.FootprintHeight * water.FootprintCellSize;
            return;
        }

        if (!water.HasFootprint && water.Kind == TerrainWaterKind.River && water.RiverPoints is { Count: >= 2 })
        {
            minX = minZ = float.MaxValue;
            maxX = maxZ = float.MinValue;
            foreach (WaterSplinePoint point in water.RiverPoints)
            {
                float reach = point.Width * 0.5f;
                minX = MathF.Min(minX, point.Position.X - reach); maxX = MathF.Max(maxX, point.Position.X + reach);
                minZ = MathF.Min(minZ, point.Position.Z - reach); maxZ = MathF.Max(maxZ, point.Position.Z + reach);
            }

            return;
        }

        // Ellipses and waterfalls both fit in the box of their size.
        float halfX = MathF.Abs(water.SizeX) * 0.5f, halfZ = MathF.Abs(water.SizeZ) * 0.5f;
        minX = water.Center.X - halfX; maxX = water.Center.X + halfX;
        minZ = water.Center.Z - halfZ; maxZ = water.Center.Z + halfZ;
    }
}
