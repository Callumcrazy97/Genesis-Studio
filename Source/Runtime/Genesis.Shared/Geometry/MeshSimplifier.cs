using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Shared.Geometry
{
    /// <summary>
    /// Reduces a triangle mesh to fewer triangles while keeping its shape, by repeatedly collapsing
    /// the edge whose removal changes the surface least (quadric error metric).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An edge is collapsed by moving one end onto the other, so every vertex of the result is a
    /// vertex of the original. Nothing is interpolated: normals, texture coordinates, colours and
    /// skin weights stay exactly as authored, and the same code serves any vertex format. The
    /// result is a new index list into the original vertices.
    /// </para>
    /// <para>
    /// Vertices that share a position (the two sides of a texture seam or a hard edge) are moved
    /// together, so simplifying never opens a crack along a seam. Open borders are held in place.
    /// </para>
    /// </remarks>
    public static class MeshSimplifier
    {
        private struct Quadric
        {
            public double A00, A01, A02, A11, A12, A22, B0, B1, B2, C;
            /// <summary>Total area of the faces summed in, so an error can be read as a distance.</summary>
            public double Area;

            public static Quadric FromPlane(Vector3 normal, Vector3 point, double weight)
            {
                double a = normal.X, b = normal.Y, c = normal.Z;
                double d = -(a * point.X + b * point.Y + c * point.Z);
                return new Quadric
                {
                    A00 = a * a * weight, A01 = a * b * weight, A02 = a * c * weight,
                    A11 = b * b * weight, A12 = b * c * weight, A22 = c * c * weight,
                    B0 = a * d * weight, B1 = b * d * weight, B2 = c * d * weight, C = d * d * weight,
                    Area = weight,
                };
            }

            public void Add(in Quadric other)
            {
                A00 += other.A00; A01 += other.A01; A02 += other.A02; A11 += other.A11; A12 += other.A12; A22 += other.A22;
                B0 += other.B0; B1 += other.B1; B2 += other.B2; C += other.C;
                Area += other.Area;
            }

            public readonly double Error(Vector3 p)
            {
                double x = p.X, y = p.Y, z = p.Z;
                return A00 * x * x + 2 * A01 * x * y + 2 * A02 * x * z + A11 * y * y + 2 * A12 * y * z + A22 * z * z
                    + 2 * (B0 * x + B1 * y + B2 * z) + C;
            }
        }

        private struct Triangle
        {
            public int C0, C1, C2;   // position clusters
            public int V0, V1, V2;   // original vertices (the corner's attributes)
            public bool Alive;
        }

        /// <summary>
        /// Simplifies a mesh to at most <paramref name="targetTriangles"/> triangles.
        /// </summary>
        /// <param name="positions">Position of every vertex.</param>
        /// <param name="indices">Triangle list into <paramref name="positions"/>.</param>
        /// <param name="targetTriangles">Stop once this few triangles remain.</param>
        /// <param name="maximumError">
        /// Also stop when the next collapse would move the surface further than this, in the mesh's
        /// units. Use <see cref="float.MaxValue"/> to reach the target whatever it costs.
        /// </param>
        /// <returns>A triangle list into the original vertices.</returns>
        public static int[] Simplify(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, int targetTriangles,
            float maximumError = float.MaxValue)
        {
            int triangleCount = indices.Length / 3;
            if (triangleCount == 0) return Array.Empty<int>();
            targetTriangles = Math.Max(0, targetTriangles);

            // Vertices at the same position form one cluster and move as one.
            int[] cluster = new int[positions.Length];
            var clusterOf = new Dictionary<(int, int, int), int>(positions.Length);
            var clusterPosition = new List<Vector3>(positions.Length);
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = 0; i < positions.Length; i++) { min = Vector3.Min(min, positions[i]); max = Vector3.Max(max, positions[i]); }
            float extent = MathF.Max(1e-6f, Vector3.Distance(min, max));
            float quantum = extent * 1e-5f;
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 p = positions[i];
                var key = ((int)MathF.Round(p.X / quantum), (int)MathF.Round(p.Y / quantum), (int)MathF.Round(p.Z / quantum));
                if (!clusterOf.TryGetValue(key, out int id))
                {
                    id = clusterPosition.Count;
                    clusterOf[key] = id;
                    clusterPosition.Add(p);
                }

                cluster[i] = id;
            }

            int clusters = clusterPosition.Count;
            var triangles = new Triangle[triangleCount];
            var incident = new List<int>[clusters];
            for (int i = 0; i < clusters; i++) incident[i] = new List<int>(6);
            var quadrics = new Quadric[clusters];
            int alive = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                int v0 = indices[t * 3], v1 = indices[t * 3 + 1], v2 = indices[t * 3 + 2];
                int c0 = cluster[v0], c1 = cluster[v1], c2 = cluster[v2];
                triangles[t] = new Triangle { C0 = c0, C1 = c1, C2 = c2, V0 = v0, V1 = v1, V2 = v2 };
                if (c0 == c1 || c1 == c2 || c0 == c2) continue;
                Vector3 normal = Vector3.Cross(clusterPosition[c1] - clusterPosition[c0], clusterPosition[c2] - clusterPosition[c0]);
                float area = normal.Length();
                if (area < 1e-20f) continue;
                triangles[t].Alive = true;
                alive++;
                Quadric plane = Quadric.FromPlane(normal / area, clusterPosition[c0], area);
                quadrics[c0].Add(plane); quadrics[c1].Add(plane); quadrics[c2].Add(plane);
                incident[c0].Add(t); incident[c1].Add(t); incident[c2].Add(t);
            }

            if (alive <= targetTriangles) return Emit(triangles);

            // Open borders: an edge used by a single triangle. A plane through the edge, at right
            // angles to that triangle, makes moving the border expensive.
            var edgeUse = new Dictionary<(int, int), (int Count, int Triangle)>(alive * 2);
            void CountEdge(int a, int b, int t)
            {
                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.TryGetValue(key, out var use) ? (use.Count + 1, use.Triangle) : (1, t);
            }

            for (int t = 0; t < triangleCount; t++)
            {
                if (!triangles[t].Alive) continue;
                CountEdge(triangles[t].C0, triangles[t].C1, t);
                CountEdge(triangles[t].C1, triangles[t].C2, t);
                CountEdge(triangles[t].C2, triangles[t].C0, t);
            }

            var isBorder = new bool[clusters];
            foreach (KeyValuePair<(int, int), (int Count, int Triangle)> edge in edgeUse)
            {
                if (edge.Value.Count != 1) continue;
                (int a, int b) = edge.Key;
                ref Triangle tri = ref triangles[edge.Value.Triangle];
                Vector3 faceNormal = Vector3.Cross(clusterPosition[tri.C1] - clusterPosition[tri.C0], clusterPosition[tri.C2] - clusterPosition[tri.C0]);
                Vector3 along = clusterPosition[b] - clusterPosition[a];
                Vector3 side = Vector3.Cross(along, faceNormal);
                float length = side.Length();
                if (length < 1e-20f) continue;
                Quadric wall = Quadric.FromPlane(side / length, clusterPosition[a], along.LengthSquared() * 40.0);
                wall.Area = 0;
                quadrics[a].Add(wall); quadrics[b].Add(wall);
                isBorder[a] = isBorder[b] = true;
            }

            double errorLimit = maximumError >= float.MaxValue ? double.MaxValue : (double)maximumError * maximumError;
            var version = new int[clusters];
            var dead = new bool[clusters];
            var queue = new PriorityQueue<(int From, int To, int FromVersion, int ToVersion), double>(edgeUse.Count * 2);
            void Offer(int from, int to)
            {
                if (from == to || dead[from] || dead[to]) return;
                // A border vertex may slide along the border but not leave it for the interior.
                if (isBorder[from] && !isBorder[to]) return;
                Quadric sum = quadrics[from];
                sum.Add(quadrics[to]);
                double cost = Math.Max(0.0, sum.Error(clusterPosition[to]));
                // The area-weighted mean of the squared distances to the faces this vertex stands for.
                if (sum.Area > 0 && cost / sum.Area > errorLimit) return;
                queue.Enqueue((from, to, version[from], version[to]), cost);
            }

            foreach ((int a, int b) in edgeUse.Keys) { Offer(a, b); Offer(b, a); }

            var neighbours = new HashSet<int>();
            while (alive > targetTriangles && queue.TryDequeue(out var edge, out double cost))
            {
                (int from, int to, int fromVersion, int toVersion) = edge;
                if (dead[from] || dead[to] || version[from] != fromVersion || version[to] != toVersion) continue;

                // Refuse a collapse that would fold a neighbouring triangle over.
                Vector3 target = clusterPosition[to];
                bool flips = false, shared = false;
                foreach (int t in incident[from])
                {
                    ref Triangle tri = ref triangles[t];
                    if (!tri.Alive) continue;
                    if (tri.C0 == to || tri.C1 == to || tri.C2 == to) { shared = true; continue; }
                    Vector3 p0 = clusterPosition[tri.C0], p1 = clusterPosition[tri.C1], p2 = clusterPosition[tri.C2];
                    Vector3 before = Vector3.Cross(p1 - p0, p2 - p0);
                    if (tri.C0 == from) p0 = target; else if (tri.C1 == from) p1 = target; else p2 = target;
                    Vector3 after = Vector3.Cross(p1 - p0, p2 - p0);
                    float afterLength = after.Length(), beforeLength = before.Length();
                    if (afterLength < 1e-20f || Vector3.Dot(before, after) < 0.15f * beforeLength * afterLength) { flips = true; break; }
                }

                if (flips || !shared) continue;

                // The corner attributes a moved triangle takes: the far end's vertex as used by a
                // triangle that touched both ends, so it stays on the same side of any seam.
                int replacement = -1;
                foreach (int t in incident[from])
                {
                    ref Triangle tri = ref triangles[t];
                    if (!tri.Alive) continue;
                    if (tri.C0 == to) { replacement = tri.V0; break; }
                    if (tri.C1 == to) { replacement = tri.V1; break; }
                    if (tri.C2 == to) { replacement = tri.V2; break; }
                }

                neighbours.Clear();
                foreach (int t in incident[from])
                {
                    ref Triangle tri = ref triangles[t];
                    if (!tri.Alive) continue;
                    if (tri.C0 == to || tri.C1 == to || tri.C2 == to)
                    {
                        tri.Alive = false;
                        alive--;
                        continue;
                    }

                    if (tri.C0 == from) { tri.C0 = to; tri.V0 = replacement; }
                    else if (tri.C1 == from) { tri.C1 = to; tri.V1 = replacement; }
                    else { tri.C2 = to; tri.V2 = replacement; }
                    incident[to].Add(t);
                    neighbours.Add(tri.C0); neighbours.Add(tri.C1); neighbours.Add(tri.C2);
                }

                foreach (int t in incident[to])
                {
                    ref Triangle tri = ref triangles[t];
                    if (!tri.Alive) continue;
                    neighbours.Add(tri.C0); neighbours.Add(tri.C1); neighbours.Add(tri.C2);
                }

                incident[to].RemoveAll(t => !triangles[t].Alive);
                incident[from].Clear();
                dead[from] = true;
                quadrics[to].Add(quadrics[from]);
                version[to]++;
                neighbours.Remove(to);
                foreach (int other in neighbours) { Offer(to, other); Offer(other, to); }
            }

            return Emit(triangles);
        }

        private static int[] Emit(Triangle[] triangles)
        {
            int count = 0;
            foreach (Triangle triangle in triangles) if (triangle.Alive) count++;
            int[] result = new int[count * 3];
            int at = 0;
            foreach (Triangle triangle in triangles)
            {
                if (!triangle.Alive) continue;
                result[at++] = triangle.V0; result[at++] = triangle.V1; result[at++] = triangle.V2;
            }

            return result;
        }

        /// <summary>
        /// Drops the vertices a triangle list no longer uses and renumbers the list to match.
        /// </summary>
        /// <returns>False when more than 65,536 vertices are still used, which one mesh cannot hold.</returns>
        public static bool Compact<TVertex>(ReadOnlySpan<TVertex> vertices, ReadOnlySpan<int> indices,
            out TVertex[] compactVertices, out ushort[] compactIndices)
        {
            int[] remap = new int[vertices.Length];
            Array.Fill(remap, -1);
            var kept = new List<TVertex>(Math.Min(vertices.Length, indices.Length));
            compactIndices = new ushort[indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                int source = indices[i];
                if (remap[source] < 0)
                {
                    if (kept.Count > ushort.MaxValue)
                    {
                        compactVertices = Array.Empty<TVertex>();
                        compactIndices = Array.Empty<ushort>();
                        return false;
                    }

                    remap[source] = kept.Count;
                    kept.Add(vertices[source]);
                }

                compactIndices[i] = (ushort)remap[source];
            }

            compactVertices = kept.ToArray();
            return true;
        }
    }
}
