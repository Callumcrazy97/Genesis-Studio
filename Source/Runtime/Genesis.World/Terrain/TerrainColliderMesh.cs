using System;
using System.Collections.Generic;
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

    public static PhysicsWaterVolume CreateVolume(TerrainWaterDefinition definition, Matrix4x4 placement)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Normalize();
        Vector3 localMin = new(
            definition.Center.X - definition.SizeX * 0.5f,
            definition.SurfaceHeight - MathF.Max(definition.PhysicsDepth, definition.SimulationDepth),
            definition.Center.Z - definition.SizeZ * 0.5f);
        Vector3 localMax = new(
            definition.Center.X + definition.SizeX * 0.5f,
            definition.SurfaceHeight,
            definition.Center.Z + definition.SizeZ * 0.5f);
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
            new Vector3(definition.Center.X, definition.SurfaceHeight, definition.Center.Z),
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
        };
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
