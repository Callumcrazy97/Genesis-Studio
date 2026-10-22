using System;
using System.Collections.Generic;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// A mesh's faces or vertices added from lists in one call, on the game's thread or in a worker job
// building a mesh lent to it (JobScriptMesh). Each entry is added exactly as the single command would
// add it. The number-list forms (NumList*) read their numbers without unboxing and take a range.
public static partial class PgslCommands
{
    private const int QuadNumbers = 36;
    private const int VertexNumbers = 12;

    [PgslCommand("MeshAddQuadsFromList", "MeshAddQuadsFromList(mesh, list) -> quads added",
        "Add many quads in one call from a list holding 36 numbers for each, in MeshAddQuadColors' order (four corners, normal, u0 v0 u1 v1, four colours with alpha, flip); stops when the mesh is full",
        "Meshes")]
    public static double MeshAddQuadsFromList(double mesh, double list)
    {
        List<object> entries = ResolveRead<List<object>>("list", list);
        if (entries is null || MeshTarget(mesh) is not { } target) return 0;
        // Room for every quad at once, rather than the mesh's storage growing step by step.
        int quads = entries.Count / QuadNumbers;
        target.Reserve(quads * 4, quads * 6);
        Span<double> q = stackalloc double[QuadNumbers];
        int added = 0;
        for (int at = 0; at + QuadNumbers <= entries.Count; at += QuadNumbers)
        {
            for (int k = 0; k < QuadNumbers; k++) q[k] = AsNumber(entries[at + k]);
            if (AddQuadNumbers(target, q) >= 0) added++;
            else if (target.Overflowed) break;
        }
        return added;
    }

    [PgslCommand("MeshAddQuadsFromNumList", "MeshAddQuadsFromNumList(mesh, numList, firstQuad, quadCount) -> quads added",
        "Add quads from a number list (36 numbers each, MeshAddQuadColors' order), quadCount of them from firstQuad on; read without unboxing, so a chunk's faces go in at array speed. Stops when the mesh is full",
        "Meshes")]
    public static double MeshAddQuadsFromNumList(double mesh, double numList, double firstQuad, double quadCount)
    {
        PgslNumberList numbers = NumRead(numList);
        if (numbers is null || MeshTarget(mesh) is not { } target) return 0;
        if (!NumRange(numbers.Count / QuadNumbers, firstQuad, quadCount, out int first, out int count)) return 0;
        target.Reserve(count * 4, count * 6);
        ReadOnlySpan<double> all = numbers.Items.AsSpan(first * QuadNumbers, count * QuadNumbers);
        int added = 0;
        for (int at = 0; at < all.Length; at += QuadNumbers)
        {
            if (AddQuadNumbers(target, all.Slice(at, QuadNumbers)) >= 0) added++;
            else if (target.Overflowed) break;
        }
        return added;
    }

    [PgslCommand("MeshAddVerticesFromList", "MeshAddVerticesFromList(mesh, vertices, triangles) -> vertices added",
        "Add many vertices and triangles in one call: vertices holds 12 numbers for each (MeshAddVertex's order), triangles three indices for each, counted from the first vertex this call adds; stops when the mesh is full",
        "Meshes")]
    public static double MeshAddVerticesFromList(double mesh, double vertices, double triangles)
    {
        List<object> points = ResolveRead<List<object>>("list", vertices);
        List<object> corners = ResolveRead<List<object>>("list", triangles);
        if (points is null || MeshTarget(mesh) is not { } target) return 0;
        int count = points.Count / VertexNumbers;
        int cornerCount = corners?.Count ?? 0;
        return AddVertexNumbers(target, count, cornerCount,
            (i, k) => AsNumber(points[(i * VertexNumbers) + k]), at => AsNumber(corners[at]));
    }

    [PgslCommand("MeshAddVerticesFromNumList", "MeshAddVerticesFromNumList(mesh, vertices, triangles) -> vertices added",
        "MeshAddVerticesFromList from number lists: 12 numbers a vertex, three indices a triangle (counted from the first vertex this call adds); stops when the mesh is full",
        "Meshes")]
    public static double MeshAddVerticesFromNumList(double mesh, double vertices, double triangles)
    {
        PgslNumberList points = NumRead(vertices);
        PgslNumberList corners = NumRead(triangles);
        if (points is null || MeshTarget(mesh) is not { } target) return 0;
        double[] p = points.Items, c = corners?.Items;
        return AddVertexNumbers(target, points.Count / VertexNumbers, corners?.Count ?? 0,
            (i, k) => p[(i * VertexNumbers) + k], at => c[at]);
    }

    // Vertices then triangles: where each listed vertex went in the mesh (-1 when it could not be
    // added: not a number, or the mesh was full); a triangle using one that was not added is skipped.
    // Rented, not allocated: a game calls this for every chunk it builds.
    private static double AddVertexNumbers(MeshBuffers target, int count, int cornerCount, Func<int, int, double> vertex, Func<int, double> corner)
    {
        int[] placed = System.Buffers.ArrayPool<int>.Shared.Rent(Math.Max(1, count));
        try
        {
            target.Reserve(count, cornerCount);
            Span<double> v = stackalloc double[VertexNumbers];
            int added = 0;
            for (int i = 0; i < count; i++)
            {
                for (int k = 0; k < VertexNumbers; k++) v[k] = vertex(i, k);
                bool finite = true;
                for (int k = 0; k < 8; k++) finite &= double.IsFinite(v[k]);
                int index = finite
                    ? target.AddVertex(new System.Numerics.Vector3((float)v[0], (float)v[1], (float)v[2]), new System.Numerics.Vector3((float)v[3], (float)v[4], (float)v[5]),
                        new System.Numerics.Vector2((float)v[6], (float)v[7]), Colour(v[8], v[9], v[10], v[11]))
                    : -1;
                placed[i] = index;
                if (index >= 0) added++;
            }
            for (int at = 0; at + 3 <= cornerCount; at += 3)
            {
                int a = Placed(corner(at)), b = Placed(corner(at + 1)), c = Placed(corner(at + 2));
                if (a >= 0 && b >= 0 && c >= 0) target.AddTriangle(a, b, c);
            }
            return added;

            int Placed(double at) => at >= 0 && at < count ? placed[(int)at] : -1;
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(placed);
        }
    }
}
