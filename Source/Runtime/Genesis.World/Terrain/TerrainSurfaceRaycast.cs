using System;
using System.Numerics;

namespace Genesis.World.Terrain;

public readonly record struct TerrainSurfaceHit(Vector3 Position, Vector3 Normal, float Distance);

/// <summary>Shared bounded heightfield ray traversal for authored terrain in Studio and runtime.</summary>
public static class TerrainSurfaceRaycast
{
    public static bool Raycast(TerrainAsset terrain, Matrix4x4 world, Vector3 origin, Vector3 direction,
        out TerrainSurfaceHit hit, float maximumDistance = float.MaxValue)
    {
        hit = default;
        if (terrain.ResolutionX < 2 || terrain.ResolutionZ < 2 || terrain.CellSize <= 0
            || !Finite(origin) || !Finite(direction) || direction.LengthSquared() < 1e-12f
            || !Matrix4x4.Invert(world, out Matrix4x4 inverse)) return false;
        direction = Vector3.Normalize(direction);
        Vector3 localOrigin = Vector3.Transform(origin, inverse);
        // Keeping this vector unnormalised preserves distance along the world ray under scaling.
        Vector3 localDirection = Vector3.TransformNormal(direction, inverse);
        Vector3 minimum = new(terrain.OriginX, terrain.MinHeight - .001f, terrain.OriginZ);
        Vector3 maximum = new(terrain.OriginX + (terrain.ResolutionX - 1) * terrain.CellSize,
            terrain.MaxHeight + .001f, terrain.OriginZ + (terrain.ResolutionZ - 1) * terrain.CellSize);
        float enter = 0, leave = maximumDistance;
        if (!Clip(localOrigin.X, localDirection.X, minimum.X, maximum.X, ref enter, ref leave)
            || !Clip(localOrigin.Y, localDirection.Y, minimum.Y, maximum.Y, ref enter, ref leave)
            || !Clip(localOrigin.Z, localDirection.Z, minimum.Z, maximum.Z, ref enter, ref leave)) return false;

        Vector3 first = localOrigin + localDirection * enter;
        int x = Math.Clamp((int)MathF.Floor((first.X - terrain.OriginX) / terrain.CellSize), 0, terrain.ResolutionX - 2);
        int z = Math.Clamp((int)MathF.Floor((first.Z - terrain.OriginZ) / terrain.CellSize), 0, terrain.ResolutionZ - 2);
        int stepX = Math.Sign(localDirection.X), stepZ = Math.Sign(localDirection.Z);
        float deltaX = stepX == 0 ? float.PositiveInfinity : terrain.CellSize / MathF.Abs(localDirection.X);
        float deltaZ = stepZ == 0 ? float.PositiveInfinity : terrain.CellSize / MathF.Abs(localDirection.Z);
        float nextX = stepX == 0 ? float.PositiveInfinity
            : (terrain.OriginX + (x + (stepX > 0 ? 1 : 0)) * terrain.CellSize - localOrigin.X) / localDirection.X;
        float nextZ = stepZ == 0 ? float.PositiveInfinity
            : (terrain.OriginZ + (z + (stepZ > 0 ? 1 : 0)) * terrain.CellSize - localOrigin.Z) / localDirection.Z;

        // A ray crosses at most this many cells. No full mesh scan or GPU readback per pointer move.
        for (int remaining = terrain.ResolutionX + terrain.ResolutionZ + 2; remaining > 0; remaining--)
        {
            float cellExit = MathF.Min(leave, MathF.Min(nextX, nextZ));
            Vector3 a = Vertex(terrain, x, z), b = Vertex(terrain, x + 1, z);
            Vector3 c = Vertex(terrain, x + 1, z + 1), d = Vertex(terrain, x, z + 1);
            float nearest = float.PositiveInfinity;
            Vector3 normal = default;
            // Match TerrainMeshBuilder's a,c,b / a,d,c diagonal and outward winding exactly.
            Test(a, c, b);
            Test(a, d, c);
            if (float.IsFinite(nearest))
            {
                normal = Vector3.TransformNormal(normal, Matrix4x4.Transpose(inverse));
                if (normal.LengthSquared() < 1e-12f) return false;
                hit = new TerrainSurfaceHit(origin + direction * nearest, Vector3.Normalize(normal), nearest);
                return true;
            }
            if (cellExit >= leave || (stepX == 0 && stepZ == 0)) break;
            bool crossX = nextX <= nextZ, crossZ = nextZ <= nextX;
            if (crossX) { x += stepX; nextX += deltaX; }
            if (crossZ) { z += stepZ; nextZ += deltaZ; }
            if (x < 0 || x >= terrain.ResolutionX - 1 || z < 0 || z >= terrain.ResolutionZ - 1) break;
            enter = cellExit;

            void Test(Vector3 v0, Vector3 v1, Vector3 v2)
            {
                if (Triangle(localOrigin, localDirection, v0, v1, v2, out float distance)
                    && distance >= enter - .0001f && distance <= cellExit + .0001f && distance < nearest)
                {
                    nearest = MathF.Max(0, distance);
                    normal = Vector3.Cross(v1 - v0, v2 - v0);
                }
            }
        }
        return false;
    }

    private static Vector3 Vertex(TerrainAsset terrain, int x, int z) =>
        new(terrain.OriginX + x * terrain.CellSize, terrain.GetHeight(x, z), terrain.OriginZ + z * terrain.CellSize);

    private static bool Clip(float origin, float direction, float min, float max, ref float enter, ref float leave)
    {
        if (MathF.Abs(direction) < 1e-12f) return origin >= min && origin <= max;
        float a = (min - origin) / direction, b = (max - origin) / direction;
        if (a > b) (a, b) = (b, a);
        enter = MathF.Max(enter, a); leave = MathF.Min(leave, b);
        return enter <= leave;
    }

    private static bool Triangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0;
        Vector3 edge1 = b - a, edge2 = c - a, p = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < 1e-10f) return false;
        float inverse = 1 / determinant;
        Vector3 offset = origin - a;
        float u = Vector3.Dot(offset, p) * inverse;
        if (u < -.00001f || u > 1.00001f) return false;
        Vector3 q = Vector3.Cross(offset, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < -.00001f || u + v > 1.00001f) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= -.0001f;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
