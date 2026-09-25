using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>What a surface brush stroke does to the mesh.</summary>
public enum ModelBrushKind
{
    /// <summary>Push vertices out along their normal.</summary>
    Draw,
    /// <summary>Pull vertices in along their normal.</summary>
    Carve,
    /// <summary>Relax vertices toward their neighbourhood average.</summary>
    Smooth,
    /// <summary>Paint vertex colour (surface colouring without needing UVs).</summary>
    Paint,
}

/// <summary>
/// Sculpting and painting directly on the model surface: a camera ray is intersected with the mesh
/// and vertices within the brush radius are displaced (sculpt) or tinted (paint) with smoothstep
/// falloff — the same brush feel as the Terrain editor, lifted from a 2D heightfield to a 3D surface.
/// </summary>
public static class ModelSurfaceBrush
{
    /// <summary>Möller–Trumbore ray/mesh intersection; returns the nearest hit point.</summary>
    public static bool Raycast(
        MeshVertex[] vertices, ushort[] indices, Vector3 origin, Vector3 direction, out Vector3 hit)
    {
        hit = Vector3.Zero;
        float best = float.MaxValue;
        bool found = false;

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 a = vertices[indices[i]].Position;
            Vector3 b = vertices[indices[i + 1]].Position;
            Vector3 c = vertices[indices[i + 2]].Position;

            Vector3 e1 = b - a;
            Vector3 e2 = c - a;
            Vector3 p = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-7f) continue;      // ray parallel to the triangle

            float invDet = 1f / det;
            Vector3 t = origin - a;
            float u = Vector3.Dot(t, p) * invDet;
            if (u < 0f || u > 1f) continue;

            Vector3 q = Vector3.Cross(t, e1);
            float v = Vector3.Dot(direction, q) * invDet;
            if (v < 0f || u + v > 1f) continue;

            float distance = Vector3.Dot(e2, q) * invDet;
            if (distance > 0.0001f && distance < best)
            {
                best = distance;
                found = true;
            }
        }

        if (found)
        {
            hit = origin + direction * best;
        }

        return found;
    }

    /// <summary>
    /// Applies one brush dab centred on <paramref name="center"/>. Returns the number of vertices
    /// affected so callers can skip a mesh re-upload when a stroke misses entirely.
    /// </summary>
    public static int Apply(
        MeshVertex[] vertices,
        ushort[] indices,
        ModelBrushKind kind,
        Vector3 center,
        float radius,
        float strength,
        Vector4 paintColor)
    {
        if (radius <= 0f) return 0;
        float radiusSq = radius * radius;
        int affected = 0;

        // Smooth needs neighbour averages, so gather them before moving anything.
        Vector3[]? neighbourAverage = kind == ModelBrushKind.Smooth
            ? BuildNeighbourAverages(vertices, indices)
            : null;

        for (int i = 0; i < vertices.Length; i++)
        {
            float distanceSq = Vector3.DistanceSquared(vertices[i].Position, center);
            if (distanceSq > radiusSq) continue;

            float falloff = Falloff(MathF.Sqrt(distanceSq) / radius);
            if (falloff <= 0f) continue;
            float amount = strength * falloff;
            affected++;

            switch (kind)
            {
                case ModelBrushKind.Draw:
                    vertices[i].Position += vertices[i].Normal * amount;
                    break;
                case ModelBrushKind.Carve:
                    vertices[i].Position -= vertices[i].Normal * amount;
                    break;
                case ModelBrushKind.Smooth when neighbourAverage is not null:
                    vertices[i].Position = Vector3.Lerp(
                        vertices[i].Position, neighbourAverage[i], Math.Clamp(amount, 0f, 1f));
                    break;
                case ModelBrushKind.Paint:
                    vertices[i].Color = Vector4.Lerp(
                        vertices[i].Color, paintColor, Math.Clamp(amount, 0f, 1f));
                    break;
            }
        }

        return affected;
    }

    /// <summary>Smoothstep falloff — flat in the middle of the brush, feathered at the rim.</summary>
    private static float Falloff(float normalizedDistance)
    {
        float t = 1f - Math.Clamp(normalizedDistance, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Vector3[] BuildNeighbourAverages(MeshVertex[] vertices, ushort[] indices)
    {
        Vector3[] sum = new Vector3[vertices.Length];
        int[] count = new int[vertices.Length];

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Accumulate(indices[i], indices[i + 1]);
            Accumulate(indices[i + 1], indices[i + 2]);
            Accumulate(indices[i + 2], indices[i]);
        }

        Vector3[] average = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            average[i] = count[i] > 0 ? sum[i] / count[i] : vertices[i].Position;
        }

        return average;

        void Accumulate(int a, int b)
        {
            sum[a] += vertices[b].Position;
            count[a]++;
            sum[b] += vertices[a].Position;
            count[b]++;
        }
    }
}
