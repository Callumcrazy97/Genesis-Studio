using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling;

/// <summary>Local skin weights propagated along welded mesh edges, never across empty space.</summary>
internal static class GModelSurfaceBinding
{
    public static void Bind(GModelAsset asset, System.Threading.CancellationToken cancellationToken = default)
    {
        if (!asset.Rig.Bones.Any(b => b.Deforms)) throw new InvalidOperationException("The rig has no deforming bones.");
        var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, asset.Rig.Bones.Select(b => b.BindLocal).ToArray());
        float size = Math.Max(.0001f, asset.Bounds.Size.Length());
        float weld = size * 1e-6f;
        var lookup = new Dictionary<(long, long, long), int>();
        var positions = new List<Vector3>();
        var maps = new List<int[]>();
        // Weld only coincident positions. In particular, do not bridge nearby opposing limbs.
        foreach (var mesh in asset.Meshes)
        {
            var map = new int[mesh.Vertices.Length];
            for (int i = 0; i < map.Length; i++)
            {
                if ((i & 511) == 0) cancellationToken.ThrowIfCancellationRequested();
                var p = mesh.Vertices[i].Position;
                var key = ((long)Math.Round(p.X / weld), (long)Math.Round(p.Y / weld), (long)Math.Round(p.Z / weld));
                if (!lookup.TryGetValue(key, out int index)) { index = positions.Count; lookup.Add(key, index); positions.Add(p); }
                map[i] = index;
            }
            maps.Add(map);
        }
        int count = positions.Count;
        if (count == 0) return;
        var edges = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        for (int m = 0; m < asset.Meshes.Count; m++)
        {
            var indices = asset.Meshes[m].Indices; var map = maps[m];
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                if (indices[t] >= map.Length || indices[t + 1] >= map.Length || indices[t + 2] >= map.Length) continue;
                int a = map[indices[t]], b = map[indices[t + 1]], c = map[indices[t + 2]];
                Connect(a, b); Connect(b, c); Connect(c, a);
            }
        }
        var neighbours = edges.Select(e => e.Order().ToArray()).ToArray();
        var nearest = new float[count]; Array.Fill(nearest, float.PositiveInfinity);
        var owners = new int[count];
        var radii = new float[worlds.Length];
        for (int bone = 0; bone < worlds.Length; bone++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!asset.Rig.Bones[bone].Deforms) continue;
            int parent = asset.Rig.Bones[bone].ParentIndex;
            var tail = worlds[bone].Translation;
            var head = parent >= 0 && asset.Rig.Bones[parent].Deforms ? worlds[parent].Translation : tail;
            radii[bone] = Math.Max(size * .015f, Math.Max(Vector3.Distance(head, tail) * .2f,
                Math.Max(asset.Rig.Bones[bone].JointRadius, parent >= 0 ? asset.Rig.Bones[parent].JointRadius : 0)));
            for (int v = 0; v < count; v++)
            {
                float d = SegmentDistance(positions[v], head, tail);
                if (d < nearest[v]) { nearest[v] = d; owners[v] = bone; }
            }
        }

        int influences = asset.SkinBindingMode == GModelSkinBindingMode.Rigid1 ? 1 : asset.SkinBindingMode == GModelSkinBindingMode.Balanced2 ? 2 : 4;
        var weights = new Vector4[count]; var joints = new Vector4[count];
        var distance = new float[count];
        var queue = new PriorityQueue<int, float>();
        // Each field starts in its nearest-bone region. Dijkstra spreads it over the actual
        // surface; disconnected clothing islands keep their own nearest-bone regions.
        // Only one distance field is retained at a time: storage is O(vertices + edges).
        for (int bone = 0; bone < worlds.Length; bone++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!asset.Rig.Bones[bone].Deforms) continue;
            Array.Fill(distance, float.PositiveInfinity); queue.Clear();
            for (int v = 0; v < count; v++)
                if (owners[v] == bone) { distance[v] = nearest[v]; queue.Enqueue(v, nearest[v]); }
            float radius = radii[bone];
            while (queue.TryDequeue(out int v, out float d))
            {
                if ((v & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (d > distance[v]) continue;
                foreach (int other in neighbours[v])
                {
                    float next = d + Vector3.Distance(positions[v], positions[other]);
                    if (next >= distance[other] || next > nearest[other] + radius * 2) continue;
                    distance[other] = next; queue.Enqueue(other, next);
                }
            }
            for (int v = 0; v < count; v++)
            {
                if (!float.IsFinite(distance[v])) continue;
                float delta = Math.Max(0, distance[v] - nearest[v]) / radius;
                float weight = MathF.Exp(-2 * delta * delta);
                if (weight < .001f) continue;
                for (int slot = 0; slot < influences; slot++)
                {
                    if (weight <= weights[v][slot]) continue;
                    for (int j = influences - 1; j > slot; j--) { weights[v][j] = weights[v][j - 1]; joints[v][j] = joints[v][j - 1]; }
                    weights[v][slot] = weight; joints[v][slot] = bone; break;
                }
            }
        }
        for (int v = 0; v < count; v++) weights[v] /= weights[v].X + weights[v].Y + weights[v].Z + weights[v].W;
        for (int m = 0; m < asset.Meshes.Count; m++)
        {
            var mesh = asset.Meshes[m]; var map = maps[m];
            mesh.SkinnedVertices = new SkinnedMeshVertex[mesh.Vertices.Length];
            for (int i = 0; i < map.Length; i++)
            {
                var v = mesh.Vertices[i]; int index = map[i];
                mesh.SkinnedVertices[i] = new() { Position = v.Position, Normal = v.Normal, Color = v.Color, UV = v.UV,
                    JointWeights = weights[index], JointIndices = joints[index] };
            }
            mesh.IsSkinned = map.Length > 0;
        }
        asset.Metadata["genesis.skin.binding"] = "surface-geodesic-1";

        void Connect(int a, int b) { if (a != b) { edges[a].Add(b); edges[b].Add(a); } }
    }

    private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() > 1e-12f ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
        return Vector3.Distance(p, a + t * ab);
    }
}
