using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

internal static class ModelTubeGeometry
{
    public const int Sides = 8;
    public static GModelMesh Build(IReadOnlyList<Vector3> source, float width, float taper)
    {
        if (source.Count > 512) throw new InvalidOperationException("Keep one tube to 512 drawing points or fewer.");
        if (!float.IsFinite(width) || width <= 0 || !float.IsFinite(taper) || taper < 0 || taper > .95f)
            throw new ArgumentOutOfRangeException(nameof(width), "Choose a positive width and an end taper between 0 and 95 percent.");
        List<Vector3> points = [];
        foreach (Vector3 p in source)
        {
            if (!float.IsFinite(p.LengthSquared())) throw new InvalidOperationException("The stroke contains an invalid point.");
            if (points.Count == 0 || Vector3.DistanceSquared(points[^1], p) > 1e-10f) points.Add(p);
        }
        if (points.Count < 2) throw new InvalidOperationException("Drag a stroke before releasing to create a tube.");
        float[] distances = new float[points.Count];
        for (int i = 1; i < points.Count; i++) distances[i] = distances[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        List<MeshVertex> vertices = [];
        List<ushort> indices = [];
        Vector3[] tangents = new Vector3[points.Count];
        Vector3 previousSide = Vector3.Zero;
        // Duplicate the UV seam so texturing wraps without interpolation across the whole Image.
        const int ringSize = Sides + 1;
        for (int i = 0; i < points.Count; i++)
        {
            Vector3 direction = i == 0 ? points[1] - points[0] : i == points.Count - 1 ? points[^1] - points[^2] : points[i + 1] - points[i - 1];
            if (direction.LengthSquared() < 1e-10f) direction = points[i] - points[i - 1];
            Vector3 tangent = tangents[i] = Vector3.Normalize(direction);
            Vector3 side = previousSide - tangent * Vector3.Dot(previousSide, tangent);
            if (side.LengthSquared() < 1e-8f)
            {
                Vector3 axis = Math.Abs(tangent.X) < .7f ? Vector3.UnitX : Vector3.UnitY;
                side = axis - tangent * Vector3.Dot(axis, tangent);
            }
            side = previousSide = Vector3.Normalize(side);
            Vector3 up = Vector3.Cross(tangent, side);
            float fraction = distances[i] / distances[^1], radius = width * .5f * (1 - taper * fraction);
            for (int j = 0; j <= Sides; j++)
            {
                float angle = (j == Sides ? 0 : j) * MathF.Tau / Sides;
                Vector3 normal = side * MathF.Cos(angle) + up * MathF.Sin(angle);
                vertices.Add(new() { Position = points[i] + normal * radius, Normal = normal, UV = new(j / (float)Sides, distances[i] / width), Color = Vector4.One });
            }
            if (i == 0) continue;
            for (int j = 0; j < Sides; j++)
            {
                int a = (i - 1) * ringSize + j, b = i * ringSize + j;
                Triangle(a, a + 1, b); Triangle(a + 1, b + 1, b);
            }
        }
        Cap(0, -tangents[0], true); Cap(points.Count - 1, tangents[^1], false);
        return new() { Name = "Drawn tube", Vertices = vertices.ToArray(), Indices = indices.ToArray(), SmoothShading = true };

        void Triangle(int a, int b, int c) { indices.Add(checked((ushort)a)); indices.Add(checked((ushort)b)); indices.Add(checked((ushort)c)); }
        void Cap(int ring, Vector3 normal, bool reverse)
        {
            int center = vertices.Count;
            vertices.Add(new() { Position = points[ring], Normal = normal, Color = Vector4.One, UV = new(.5f) });
            for (int j = 0; j < Sides; j++)
            {
                MeshVertex vertex = vertices[ring * ringSize + j]; vertex.Normal = normal;
                vertex.UV = new(.5f + MathF.Cos(j * MathF.Tau / Sides) * .5f, .5f + MathF.Sin(j * MathF.Tau / Sides) * .5f);
                vertices.Add(vertex);
            }
            for (int j = 0; j < Sides; j++)
            {
                int a = center + 1 + j, b = center + 1 + (j + 1) % Sides;
                Triangle(center, reverse ? b : a, reverse ? a : b);
            }
        }
    }
}
