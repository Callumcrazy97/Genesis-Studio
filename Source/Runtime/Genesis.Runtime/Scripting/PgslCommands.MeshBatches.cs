using System;
using System.Collections.Generic;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// A mesh's faces or vertices added from lists in one call. A worker job cannot build a mesh (meshes
// belong to the game's thread), but it can work out a chunk's faces into a list; the game's thread
// then adds them all with one command instead of thousands of calls. Each entry is added exactly as
// the single command would add it.
public static partial class PgslCommands
{
    private const int QuadNumbers = 36;
    private const int VertexNumbers = 12;

    [PgslCommand("MeshAddQuadsFromList", "MeshAddQuadsFromList(mesh, list) -> quads added",
        "Add many quads in one call from a list holding 36 numbers for each, in MeshAddQuadColors' order (four corners, normal, u0 v0 u1 v1, four colours with alpha, flip); stops when the mesh is full",
        "Meshes")]
    public static double MeshAddQuadsFromList(double mesh, double list)
    {
        List<object> entries = Resolve<List<object>>("list", list);
        if (entries is null || !ScriptMeshes.Exists((int)mesh)) return 0;
        Span<double> q = stackalloc double[QuadNumbers];
        int added = 0;
        for (int at = 0; at + QuadNumbers <= entries.Count; at += QuadNumbers)
        {
            for (int k = 0; k < QuadNumbers; k++) q[k] = AsNumber(entries[at + k]);
            double index = MeshAddQuadColors(mesh, q[0], q[1], q[2], q[3], q[4], q[5], q[6], q[7], q[8], q[9], q[10], q[11],
                q[12], q[13], q[14], q[15], q[16], q[17], q[18],
                q[19], q[20], q[21], q[22], q[23], q[24], q[25], q[26], q[27], q[28], q[29], q[30], q[31], q[32], q[33], q[34], q[35]);
            if (index >= 0) added++;
            else if (ScriptMeshes.Overflowed((int)mesh)) break;
        }
        return added;
    }

    [PgslCommand("MeshAddVerticesFromList", "MeshAddVerticesFromList(mesh, vertices, triangles) -> vertices added",
        "Add many vertices and triangles in one call: vertices holds 12 numbers for each (MeshAddVertex's order), triangles three indices for each, counted from the first vertex this call adds; stops when the mesh is full",
        "Meshes")]
    public static double MeshAddVerticesFromList(double mesh, double vertices, double triangles)
    {
        List<object> points = Resolve<List<object>>("list", vertices);
        List<object> corners = Resolve<List<object>>("list", triangles);
        if (points is null || !ScriptMeshes.Exists((int)mesh)) return 0;
        Span<double> v = stackalloc double[VertexNumbers];
        // Where each listed vertex went in the mesh (-1 when it could not be added: not a number,
        // or the mesh was full); a triangle using one that was not added is skipped.
        int[] placed = new int[points.Count / VertexNumbers];
        int added = 0;
        for (int i = 0; i < placed.Length; i++)
        {
            for (int k = 0; k < VertexNumbers; k++) v[k] = AsNumber(points[(i * VertexNumbers) + k]);
            double index = MeshAddVertex(mesh, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11]);
            placed[i] = (int)index;
            if (index >= 0) added++;
        }
        if (corners is not null)
        {
            for (int at = 0; at + 3 <= corners.Count; at += 3)
            {
                int a = Placed(corners[at]), b = Placed(corners[at + 1]), c = Placed(corners[at + 2]);
                if (a >= 0 && b >= 0 && c >= 0) MeshAddTriangle(mesh, a, b, c);
            }
        }
        return added;

        int Placed(object corner)
        {
            double at = AsNumber(corner);
            return at >= 0 && at < placed.Length ? placed[(int)at] : -1;
        }
    }
}
