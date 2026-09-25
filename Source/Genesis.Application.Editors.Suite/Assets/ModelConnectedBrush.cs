using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Edits positional welds, retaining UV/normal splits and 16-bit mesh chunks.</summary>
internal static class ModelConnectedBrush
{
    public static int Apply(MeshVertex[][] meshes, ushort[][] triangles, ModelBrushKind kind,
        Vector3 centre, float radius, float strength, Vector4 colour, Func<int, int, bool> allowed)
    {
        if (radius <= 0 || !float.IsFinite(radius)) return 0;
        var map = new Dictionary<Vector3, int>();
        var positions = new List<Vector3>(); var normals = new List<Vector3>();
        var enabled = new List<bool>(); var refs = new int[meshes.Length][];
        for (int m = 0; m < meshes.Length; m++)
        {
            refs[m] = new int[meshes[m].Length];
            for (int v = 0; v < meshes[m].Length; v++)
            {
                var vertex = meshes[m][v];
                if (!map.TryGetValue(vertex.Position, out int group))
                { group = positions.Count; map.Add(vertex.Position, group); positions.Add(vertex.Position); normals.Add(Vector3.Zero); enabled.Add(false); }
                refs[m][v] = group; normals[group] += vertex.Normal; enabled[group] |= allowed(m, v);
            }
        }
        var neighbours = new HashSet<int>[positions.Count];
        if (kind == ModelBrushKind.Smooth)
        {
            for (int i = 0; i < neighbours.Length; i++) neighbours[i] = [];
            for (int m = 0; m < meshes.Length; m++)
            for (int i = 0; i + 2 < triangles[m].Length; i += 3)
            {
                int a = refs[m][triangles[m][i]], b = refs[m][triangles[m][i + 1]], c = refs[m][triangles[m][i + 2]];
                neighbours[a].UnionWith([b,c]); neighbours[b].UnionWith([a,c]); neighbours[c].UnionWith([a,b]);
            }
        }
        var output = positions.ToArray(); var amounts = new float[positions.Count]; int affected = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            if (!enabled[i]) continue;
            float t = 1 - Math.Clamp(Vector3.Distance(positions[i], centre) / radius, 0, 1);
            float amount = Math.Clamp(strength, 0, 1) * t * t * (3 - 2 * t);
            if (amount <= 0) continue;
            amounts[i] = amount; affected++;
            // Radius-relative displacement avoids explosive strokes on small imports.
            if (kind is ModelBrushKind.Draw or ModelBrushKind.Carve && normals[i].LengthSquared() > 1e-12f)
                output[i] += Vector3.Normalize(normals[i]) * (amount * radius * .15f * (kind == ModelBrushKind.Carve ? -1 : 1));
            else if (kind == ModelBrushKind.Smooth && neighbours[i].Count > 0)
                output[i] = Vector3.Lerp(positions[i], neighbours[i].Aggregate(Vector3.Zero, (sum, n) => sum + positions[n]) / neighbours[i].Count, amount * .5f);
        }
        for (int m = 0; m < meshes.Length; m++)
        for (int v = 0; v < meshes[m].Length; v++)
        {
            int group = refs[m][v];
            if (kind == ModelBrushKind.Paint)
            { if (allowed(m, v)) meshes[m][v].Color = Vector4.Lerp(meshes[m][v].Color, colour, amounts[group]); }
            else meshes[m][v].Position = output[group];
        }
        if (kind != ModelBrushKind.Paint && affected > 0)
        {
            // Rebuild normals within each original smoothing group, across chunk seams.
            var sums = new Dictionary<(int Weld, Vector3 Normal), Vector3>();
            for (int m = 0; m < meshes.Length; m++)
            for (int i = 0; i + 2 < triangles[m].Length; i += 3)
            {
                int a = triangles[m][i], b = triangles[m][i+1], c = triangles[m][i+2];
                Vector3 normal = Vector3.Cross(meshes[m][b].Position - meshes[m][a].Position, meshes[m][c].Position - meshes[m][a].Position);
                foreach (int v in new[] {a,b,c}) { var key = (refs[m][v], meshes[m][v].Normal); sums[key] = sums.GetValueOrDefault(key) + normal; }
            }
            for (int m = 0; m < meshes.Length; m++)
            for (int v = 0; v < meshes[m].Length; v++)
                if (sums.TryGetValue((refs[m][v], meshes[m][v].Normal), out var normal) && normal.LengthSquared() > 1e-12f)
                    meshes[m][v].Normal = Vector3.Normalize(normal);
        }
        return affected;
    }
}
