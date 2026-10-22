using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Physics;
using Genesis.Shared.Assets;

namespace Genesis.World.Terrain;

/// <summary>
/// Shared heightfield triangle mesh and water-volume construction used by runtime registration
/// and the Terrain Editor live collider / overlay / drop-in-water preview.
/// </summary>
public static class TerrainColliderMesh
{
    public static void Build(TerrainAsset terrain, out Vector3[] vertices, out int[] indices)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        vertices = new Vector3[terrain.ResolutionX * terrain.ResolutionZ];
        for (int z = 0; z < terrain.ResolutionZ; z++)
        for (int x = 0; x < terrain.ResolutionX; x++)
            vertices[z * terrain.ResolutionX + x] = new Vector3(
                terrain.OriginX + x * terrain.CellSize,
                terrain.GetHeight(x, z),
                terrain.OriginZ + z * terrain.CellSize);

        indices = new int[(terrain.ResolutionX - 1) * (terrain.ResolutionZ - 1) * 6];
        int index = 0;
        for (int z = 0; z < terrain.ResolutionZ - 1; z++)
        for (int x = 0; x < terrain.ResolutionX - 1; x++)
        {
            int a = z * terrain.ResolutionX + x;
            int b = a + 1;
            int c = a + terrain.ResolutionX;
            int d = c + 1;
            // Bepu uses right-handed coordinates and one-sided triangles; clockwise winding
            // as seen from above makes the terrain solid and ray-queryable from above.
            indices[index++] = a; indices[index++] = b; indices[index++] = d;
            indices[index++] = a; indices[index++] = d; indices[index++] = c;
        }
    }

    /// <summary>
    /// Triangles for one rectangle of cells. Large terrains register collision a tile at a time,
    /// near the things that can touch it, instead of one mesh for every cell they contain.
    /// </summary>
    public static void BuildRegion(TerrainAsset terrain, int startX, int startZ, int cellsX, int cellsZ,
        out Vector3[] vertices, out int[] indices)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        startX = Math.Clamp(startX, 0, terrain.ResolutionX - 1);
        startZ = Math.Clamp(startZ, 0, terrain.ResolutionZ - 1);
        cellsX = Math.Clamp(cellsX, 0, terrain.ResolutionX - 1 - startX);
        cellsZ = Math.Clamp(cellsZ, 0, terrain.ResolutionZ - 1 - startZ);
        int pointsX = cellsX + 1, pointsZ = cellsZ + 1;
        vertices = new Vector3[pointsX * pointsZ];
        for (int z = 0; z < pointsZ; z++)
        for (int x = 0; x < pointsX; x++)
            vertices[z * pointsX + x] = new Vector3(
                terrain.OriginX + (startX + x) * terrain.CellSize,
                terrain.GetHeight(startX + x, startZ + z),
                terrain.OriginZ + (startZ + z) * terrain.CellSize);

        indices = new int[cellsX * cellsZ * 6];
        int index = 0;
        for (int z = 0; z < cellsZ; z++)
        for (int x = 0; x < cellsX; x++)
        {
            int a = z * pointsX + x, b = a + 1, c = a + pointsX, d = c + 1;
            indices[index++] = a; indices[index++] = b; indices[index++] = d;
            indices[index++] = a; indices[index++] = d; indices[index++] = c;
        }
    }

    public static PhysicsWaterVolume CreateVolume(TerrainWaterDefinition definition, Matrix4x4 placement)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Normalize();
        float depth = MathF.Max(definition.PhysicsDepth, definition.SimulationDepth);
        Vector3 localMin = new(
            definition.Center.X - definition.SizeX * 0.5f,
            definition.SurfaceHeight - depth,
            definition.Center.Z - definition.SizeZ * 0.5f);
        Vector3 localMax = new(
            definition.Center.X + definition.SizeX * 0.5f,
            definition.SurfaceHeight,
            definition.Center.Z + definition.SizeZ * 0.5f);
        // A river is where its course runs, as wide as it is there, with the surface its points give.
        bool river = definition.Kind == TerrainWaterKind.River && definition.RiverPoints.Count >= 2;
        if (river)
        {
            localMin = new Vector3(float.MaxValue); localMax = new Vector3(float.MinValue);
            foreach (Water.WaterSplinePoint point in definition.RiverPoints)
            {
                Vector3 reach = new(point.Width * 0.5f, 0f, point.Width * 0.5f);
                localMin = Vector3.Min(localMin, point.Position - reach - new Vector3(0f, MathF.Max(depth, point.Depth), 0f));
                localMax = Vector3.Max(localMax, point.Position + reach);
            }
        }
        Func<float, float, float> localSurface = definition.Kind switch
        {
            TerrainWaterKind.Ocean => null,
            TerrainWaterKind.River when river => (x, z) => RiverSurface(definition.RiverPoints, x, z),
            TerrainWaterKind.Water => (x, z) => definition.ContainsHorizontal(x, z) ? definition.SurfaceHeight : float.NaN,
            _ => null,
        };
        Func<float, float, float> surfaceAt = null;
        if (localSurface != null)
        {
            // Asked in world space; the outline is the terrain's own.
            Matrix4x4 toLocal = Matrix4x4.Invert(placement, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
            surfaceAt = (x, z) =>
            {
                Vector3 local = Vector3.Transform(new Vector3(x, 0f, z), toLocal);
                float height = localSurface(local.X, local.Z);
                return float.IsFinite(height) ? Vector3.Transform(new Vector3(local.X, height, local.Z), placement).Y : float.NaN;
            };
        }
        Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 local = new(
                (corner & 1) == 0 ? localMin.X : localMax.X,
                (corner & 2) == 0 ? localMin.Y : localMax.Y,
                (corner & 4) == 0 ? localMin.Z : localMax.Z);
            Vector3 world = Vector3.Transform(local, placement);
            minimum = Vector3.Min(minimum, world);
            maximum = Vector3.Max(maximum, world);
        }

        Vector3 surfacePoint = Vector3.Transform(
            new Vector3(definition.Center.X, river ? definition.RiverPoints.Average(point => point.Position.Y) : definition.SurfaceHeight, definition.Center.Z),
            placement);
        Vector3 flowDirection = Vector3.TransformNormal(Vector3.UnitX, placement);
        if (flowDirection.LengthSquared() > 1e-6f) flowDirection = Vector3.Normalize(flowDirection);
        return new PhysicsWaterVolume
        {
            Id = definition.Id,
            Name = definition.Name,
            Minimum = minimum,
            Maximum = maximum,
            SurfaceY = surfacePoint.Y,
            Density = definition.FluidDensity,
            Buoyancy = definition.Swimmable ? definition.BuoyancyStrength : 0,
            LinearDrag = definition.PhysicsMode == WaterPhysicsMode.None ? 0 : definition.LinearDrag,
            AngularDrag = definition.Swimmable ? definition.AngularDrag : 0,
            FlowVelocity = definition.PhysicsMode == WaterPhysicsMode.None ? Vector3.Zero : flowDirection * definition.FlowSpeed,
            Swimmable = definition.Swimmable,
            Damaging = definition.PhysicsMode != WaterPhysicsMode.None && definition.Damaging,
            SurfaceAt = surfaceAt,
        };
    }

    /// <summary>A river's surface over x, z: the height along its course at the nearest point within its width, or NaN off it.</summary>
    private static float RiverSurface(List<Water.WaterSplinePoint> points, float x, float z)
    {
        Vector2 at = new(x, z);
        float best = float.MaxValue, height = float.NaN;
        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector2 a = new(points[i].Position.X, points[i].Position.Z), b = new(points[i + 1].Position.X, points[i + 1].Position.Z);
            Vector2 edge = b - a;
            float t = edge.LengthSquared() < 1e-8f ? 0f : Math.Clamp(Vector2.Dot(at - a, edge) / edge.LengthSquared(), 0f, 1f);
            float distance = Vector2.Distance(at, a + edge * t);
            float halfWidth = float.Lerp(points[i].Width, points[i + 1].Width, t) * 0.5f;
            if (distance > halfWidth || distance >= best) continue;
            best = distance;
            height = float.Lerp(points[i].Position.Y, points[i + 1].Position.Y, t);
        }
        return height;
    }

    public static int OverlayStride(TerrainAsset terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        return Math.Max(1, Math.Max(terrain.ResolutionX, terrain.ResolutionZ) / 16);
    }

    public static int CountOverlayEdges(TerrainAsset terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        int stride = OverlayStride(terrain);
        int count = 0;
        for (int z = 0; z < terrain.ResolutionZ; z += stride)
        for (int x = 0; x < terrain.ResolutionX - 1; x += stride)
            count++;
        for (int x = 0; x < terrain.ResolutionX; x += stride)
        for (int z = 0; z < terrain.ResolutionZ - 1; z += stride)
            count++;
        return count;
    }

    public static void AppendOverlayEdges(TerrainAsset terrain, List<(Vector3 A, Vector3 B)> edges)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(edges);
        int stride = OverlayStride(terrain);
        for (int z = 0; z < terrain.ResolutionZ; z += stride)
        for (int x = 0; x < terrain.ResolutionX - 1; x += stride)
        {
            int nx = Math.Min(terrain.ResolutionX - 1, x + stride);
            edges.Add((Vertex(terrain, x, z), Vertex(terrain, nx, z)));
        }

        for (int x = 0; x < terrain.ResolutionX; x += stride)
        for (int z = 0; z < terrain.ResolutionZ - 1; z += stride)
        {
            int nz = Math.Min(terrain.ResolutionZ - 1, z + stride);
            edges.Add((Vertex(terrain, x, z), Vertex(terrain, x, nz)));
        }
    }

    public static void AppendVolumeEdges(PhysicsWaterVolume volume, List<(Vector3 A, Vector3 B)> edges)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(edges);
        Vector3 min = volume.Minimum;
        Vector3 max = volume.Maximum;
        float surface = volume.SurfaceY;
        Vector3[] corners =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        int[] pairs = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < pairs.Length; i += 2)
            edges.Add((corners[pairs[i]], corners[pairs[i + 1]]));
        edges.Add((new Vector3(min.X, surface, min.Z), new Vector3(max.X, surface, min.Z)));
        edges.Add((new Vector3(max.X, surface, min.Z), new Vector3(max.X, surface, max.Z)));
        edges.Add((new Vector3(max.X, surface, max.Z), new Vector3(min.X, surface, max.Z)));
        edges.Add((new Vector3(min.X, surface, max.Z), new Vector3(min.X, surface, min.Z)));
    }

    public static bool IsTerrainSurfacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.EndsWith(".gterrain", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".nature.json", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExclusiveTerrainSurfaceChange(ProjectAssetChangeSet changes)
    {
        if (changes == null || changes.ChangedPaths == null || changes.ChangedPaths.Count == 0)
            return false;
        foreach (string path in changes.ChangedPaths)
            if (!IsTerrainSurfacePath(path))
                return false;
        return true;
    }

    private static Vector3 Vertex(TerrainAsset terrain, int x, int z) =>
        new(
            terrain.OriginX + x * terrain.CellSize,
            terrain.GetHeight(x, z),
            terrain.OriginZ + z * terrain.CellSize);
}
