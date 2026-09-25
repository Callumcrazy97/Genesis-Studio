using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Rest-mesh intersections for placing a skeleton inside the clicked body part.</summary>
public sealed class ModelRigPlacementSurface
{
    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, int Part);
    private readonly record struct Crossing(float Distance, int Part);
    private readonly Triangle[] _triangles;

    public ModelRigPlacementSurface(GModelAsset asset)
    {
        // Weld positions for connectivity only. UV seams and renderer chunks must not split
        // the front and back of a limb into different parts; the source mesh is untouched.
        var positions = new Dictionary<Vector3, int>();
        var parents = new List<int>();
        var triangles = new List<Triangle>();
        foreach (var mesh in asset.Meshes)
        {
            int count = mesh.Vertices.Length > 0 ? mesh.Vertices.Length : mesh.SkinnedVertices.Length;
            var ids = new int[count];
            for (int i = 0; i < count; i++) ids[i] = Vertex(Position(i));
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
                if (a >= count || b >= count || c >= count) continue;
                Join(ids[a], ids[b]); Join(ids[a], ids[c]);
                triangles.Add(new(Position(a), Position(b), Position(c), ids[a]));
            }
            Vector3 Position(int i) => mesh.Vertices.Length > 0
                ? mesh.Vertices[i].Position : mesh.SkinnedVertices[i].Position;
        }
        _triangles = triangles.Select(t => t with { Part = Root(t.Part) }).ToArray();

        int Vertex(Vector3 position)
        {
            if (positions.TryGetValue(position, out int id)) return id;
            id = parents.Count; positions.Add(position, id); parents.Add(id); return id;
        }
        int Root(int id)
        {
            while (parents[id] != id) { parents[id] = parents[parents[id]]; id = parents[id]; }
            return id;
        }
        void Join(int a, int b) { a = Root(a); b = Root(b); if (a != b) parents[b] = a; }
    }

    /// <summary>
    /// Returns model coordinates midway between the nearest distinct crossings of the hit part.
    /// A single-sided/open part falls back to its surface; empty space is never a placement plane.
    /// </summary>
    public bool TryPick(Vector3 worldOrigin, Vector3 worldDirection, Matrix4x4 modelWorld, out Vector3 center)
    {
        center = default;
        if (!Matrix4x4.Invert(modelWorld, out var inverse)) return false;
        var origin = Vector3.Transform(worldOrigin, inverse);
        var direction = Vector3.TransformNormal(worldDirection, inverse);
        float length = direction.Length();
        if (!float.IsFinite(length) || length < 1e-10f) return false;
        direction /= length;
        var hits = new List<Crossing>();
        foreach (var triangle in _triangles)
        {
            var edge1 = triangle.B - triangle.A;
            var edge2 = triangle.C - triangle.A;
            var p = Vector3.Cross(direction, edge2);
            float determinant = Vector3.Dot(edge1, p);
            float area = Vector3.Cross(edge1, edge2).Length();
            if (area == 0 || MathF.Abs(determinant) <= area * 1e-7f) continue;
            var offset = origin - triangle.A;
            float u = Vector3.Dot(offset, p) / determinant;
            if (u < -1e-6f || u > 1 + 1e-6f) continue;
            var q = Vector3.Cross(offset, edge1);
            float v = Vector3.Dot(direction, q) / determinant;
            if (v < -1e-6f || u + v > 1 + 1e-6f) continue;
            float distance = Vector3.Dot(edge2, q) / determinant;
            if (float.IsFinite(distance) && distance > 0) hits.Add(new(distance, triangle.Part));
        }
        if (hits.Count == 0) return false;
        hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var entry = hits[0];
        float depth = entry.Distance;
        // Coincident faces and hits on a shared triangle edge are one boundary, not thickness.
        float tolerance = Math.Max(1e-7f, Math.Abs(entry.Distance) * 2e-6f);
        foreach (var exit in hits)
        {
            if (exit.Part != entry.Part || exit.Distance - entry.Distance <= tolerance) continue;
            depth = (entry.Distance + exit.Distance) * .5f;
            break;
        }
        center = origin + direction * depth;
        return true;
    }
}
