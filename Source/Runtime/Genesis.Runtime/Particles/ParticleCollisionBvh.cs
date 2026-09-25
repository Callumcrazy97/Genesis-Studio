using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Runtime.Particles;

public readonly record struct ParticleCollisionTriangle(Vector3 A, Vector3 B, Vector3 C);

public sealed class ParticleCollisionBvhData
{
    public static ParticleCollisionBvhData Empty { get; } = new();
    public Vector4[] Nodes { get; init; } = [];
    public Vector4[] Triangles { get; init; } = [];
    public int TriangleCount => Triangles.Length / 3;
}

/// <summary>
/// Deterministic bounded stackless BVH matching the compute shader's preorder traversal.
/// A missed node jumps to the first node after its complete subtree.
/// </summary>
public static class ParticleCollisionBvh
{
    public const int MaximumTriangles = 8192;
    private const int LeafSize = 8;

    private sealed class Node
    {
        public Vector3 Min;
        public Vector3 Max;
        public int Miss;
        public int TriangleStart;
        public int TriangleCount;
    }

    public static ParticleCollisionBvhData Build(ReadOnlySpan<ParticleCollisionTriangle> source)
    {
        int count = Math.Min(source.Length, MaximumTriangles);
        if (count <= 0) return ParticleCollisionBvhData.Empty;

        ParticleCollisionTriangle[] triangles = source[..count].ToArray();
        int[] order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;

        List<Node> nodes = new(Math.Min(count * 2, 16384));
        List<ParticleCollisionTriangle> ordered = new(count);
        BuildNode(triangles, order, 0, count, nodes, ordered);

        Vector4[] packedNodes = new Vector4[nodes.Count * 3];
        for (int i = 0; i < nodes.Count; i++)
        {
            Node node = nodes[i];
            packedNodes[i * 3] = new Vector4(node.Min, node.Miss);
            packedNodes[i * 3 + 1] = new Vector4(node.Max, node.TriangleStart);
            packedNodes[i * 3 + 2] = new Vector4(node.TriangleCount, 0, 0, 0);
        }

        Vector4[] packedTriangles = new Vector4[ordered.Count * 3];
        for (int i = 0; i < ordered.Count; i++)
        {
            ParticleCollisionTriangle triangle = ordered[i];
            packedTriangles[i * 3] = new Vector4(triangle.A, 1);
            packedTriangles[i * 3 + 1] = new Vector4(triangle.B, 1);
            packedTriangles[i * 3 + 2] = new Vector4(triangle.C, 1);
        }

        return new ParticleCollisionBvhData
        {
            Nodes = packedNodes,
            Triangles = packedTriangles,
        };
    }

    private static int BuildNode(
        ParticleCollisionTriangle[] triangles,
        int[] order,
        int start,
        int count,
        List<Node> nodes,
        List<ParticleCollisionTriangle> ordered)
    {
        int nodeIndex = nodes.Count;
        var node = new Node();
        nodes.Add(node);

        Bounds(triangles, order, start, count, out node.Min, out node.Max, out Vector3 centroidMin, out Vector3 centroidMax);
        if (count <= LeafSize)
        {
            node.TriangleStart = ordered.Count;
            node.TriangleCount = count;
            for (int i = 0; i < count; i++) ordered.Add(triangles[order[start + i]]);
            node.Miss = nodeIndex + 1;
            return nodeIndex;
        }

        Vector3 extent = centroidMax - centroidMin;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        Array.Sort(order, start, count, Comparer<int>.Create((left, right) =>
        {
            float a = Axis(Centroid(triangles[left]), axis);
            float b = Axis(Centroid(triangles[right]), axis);
            int compare = a.CompareTo(b);
            return compare != 0 ? compare : left.CompareTo(right);
        }));

        int leftCount = count / 2;
        BuildNode(triangles, order, start, leftCount, nodes, ordered);
        BuildNode(triangles, order, start + leftCount, count - leftCount, nodes, ordered);
        node.Miss = nodes.Count;
        return nodeIndex;
    }

    private static void Bounds(
        ParticleCollisionTriangle[] triangles,
        int[] order,
        int start,
        int count,
        out Vector3 min,
        out Vector3 max,
        out Vector3 centroidMin,
        out Vector3 centroidMax)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        centroidMin = new Vector3(float.MaxValue);
        centroidMax = new Vector3(float.MinValue);
        for (int i = 0; i < count; i++)
        {
            ParticleCollisionTriangle triangle = triangles[order[start + i]];
            min = Vector3.Min(min, Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C)));
            max = Vector3.Max(max, Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C)));
            Vector3 centroid = Centroid(triangle);
            centroidMin = Vector3.Min(centroidMin, centroid);
            centroidMax = Vector3.Max(centroidMax, centroid);
        }
    }

    private static Vector3 Centroid(in ParticleCollisionTriangle triangle) =>
        (triangle.A + triangle.B + triangle.C) / 3f;

    private static float Axis(Vector3 value, int axis) => axis switch
    {
        1 => value.Y,
        2 => value.Z,
        _ => value.X,
    };
}
