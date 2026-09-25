using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling
{
    /// <summary>
    /// Canonical mesh production operations shared by Studio authoring and acceptance/runtime
    /// validation. Every operation mutates the same <see cref="GModelMesh"/> that gameplay loads.
    /// </summary>
    public static class GModelProductionTools
    {
        public static void EnsureTriangleMaterials(GModelMesh mesh)
        {
            int triangles = (mesh?.Indices?.Length ?? 0) / 3;
            if (mesh == null || mesh.TriangleMaterialIndices?.Length == triangles) return;
            int[] slots = new int[triangles];
            Array.Fill(slots, Math.Max(0, mesh.MaterialIndex));
            mesh.TriangleMaterialIndices = slots;
        }

        public static void AssignMaterial(GModelMesh mesh, IEnumerable<int> triangles, int materialIndex)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            EnsureTriangleMaterials(mesh);
            foreach (int triangle in triangles?.Distinct() ?? Enumerable.Empty<int>())
            {
                if ((uint)triangle < (uint)mesh.TriangleMaterialIndices.Length)
                    mesh.TriangleMaterialIndices[triangle] = Math.Max(0, materialIndex);
            }
        }

        public static void ApplyUvProjection(GModelMesh mesh, GModelUvProjection projection)
        {
            if (mesh == null || mesh.IsSkinned || mesh.Vertices.Length == 0) return;
            MeshVertex[] vertices = mesh.Vertices;
            Bounds(vertices, out Vector3 min, out Vector3 max);
            Vector3 size = Vector3.Max(max - min, new Vector3(1e-5f));
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = (vertices[i].Position - min) / size;
                Vector3 centered = vertices[i].Position - (min + max) * 0.5f;
                Vector2 uv = projection switch
                {
                    GModelUvProjection.PlanarTop => new Vector2(p.X, 1f - p.Z),
                    GModelUvProjection.PlanarFront => new Vector2(p.X, 1f - p.Y),
                    GModelUvProjection.Cylindrical => new Vector2(
                        0.5f + MathF.Atan2(centered.Z, centered.X) / (MathF.PI * 2f), 1f - p.Y),
                    GModelUvProjection.Spherical => SphericalUv(centered),
                    GModelUvProjection.Box => BoxUv(vertices[i].Normal, p),
                    _ => vertices[i].UV,
                };
                vertices[i].UV = uv;
            }
            mesh.UvProjection = projection;
            mesh.UvOverride = projection != GModelUvProjection.Source;
        }

        public static void RecomputeNormalsAndTangents(GModelMesh mesh)
        {
            if (mesh == null || mesh.IsSkinned || mesh.Vertices.Length == 0) return;
            MeshVertex[] vertices = mesh.Vertices;
            Vector3[] normals = new Vector3[vertices.Length];
            Vector3[] tan1 = new Vector3[vertices.Length];
            Vector3[] tan2 = new Vector3[vertices.Length];
            for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                int ia = mesh.Indices[i], ib = mesh.Indices[i + 1], ic = mesh.Indices[i + 2];
                if ((uint)ia >= vertices.Length || (uint)ib >= vertices.Length || (uint)ic >= vertices.Length) continue;
                Vector3 a = vertices[ia].Position, b = vertices[ib].Position, c = vertices[ic].Position;
                Vector3 edge1 = b - a, edge2 = c - a;
                Vector3 normal = Vector3.Cross(edge1, edge2);
                if (normal.LengthSquared() > 1e-12f)
                {
                    normals[ia] += normal; normals[ib] += normal; normals[ic] += normal;
                }

                Vector2 duv1 = vertices[ib].UV - vertices[ia].UV;
                Vector2 duv2 = vertices[ic].UV - vertices[ia].UV;
                float divisor = duv1.X * duv2.Y - duv1.Y * duv2.X;
                if (MathF.Abs(divisor) <= 1e-8f) continue;
                float r = 1f / divisor;
                Vector3 tangent = (edge1 * duv2.Y - edge2 * duv1.Y) * r;
                Vector3 bitangent = (edge2 * duv1.X - edge1 * duv2.X) * r;
                tan1[ia] += tangent; tan1[ib] += tangent; tan1[ic] += tangent;
                tan2[ia] += bitangent; tan2[ib] += bitangent; tan2[ic] += bitangent;
            }

            Vector4[] tangents = new Vector4[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 n = SafeNormal(normals[i], vertices[i].Normal);
                Vector3 t = tan1[i] - n * Vector3.Dot(n, tan1[i]);
                t = SafeNormal(t, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
                float w = Vector3.Dot(Vector3.Cross(n, t), tan2[i]) < 0f ? -1f : 1f;
                vertices[i].Normal = n;
                tangents[i] = new Vector4(t, w);
            }
            mesh.Tangents = tangents;
        }

        public static IReadOnlyList<int> DeleteFaces(GModelMesh mesh, IEnumerable<int> triangles)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            HashSet<int> removed = new(triangles ?? Enumerable.Empty<int>());
            EnsureTriangleMaterials(mesh);
            List<ushort> indices = new(mesh.Indices.Length);
            List<int> materials = new(mesh.TriangleMaterialIndices.Length);
            List<int> keptOriginal = new();
            int triangleCount = mesh.Indices.Length / 3;
            for (int triangle = 0; triangle < triangleCount; triangle++)
            {
                if (removed.Contains(triangle)) continue;
                indices.Add(mesh.Indices[triangle * 3]);
                indices.Add(mesh.Indices[triangle * 3 + 1]);
                indices.Add(mesh.Indices[triangle * 3 + 2]);
                materials.Add(mesh.TriangleMaterialIndices[triangle]);
                keptOriginal.Add(triangle);
            }
            mesh.Indices = indices.ToArray();
            mesh.TriangleMaterialIndices = materials.ToArray();
            RemapFaceGroups(mesh, keptOriginal);
            RecomputeNormalsAndTangents(mesh);
            return keptOriginal;
        }

        public static int MoveVertices(GModelMesh mesh, IEnumerable<int> vertexIndices, Vector3 delta)
        {
            if (mesh == null || mesh.IsSkinned || delta.LengthSquared() <= 1e-12f) return 0;
            int moved = 0;
            foreach (int index in vertexIndices?.Distinct() ?? Enumerable.Empty<int>())
            {
                if ((uint)index >= mesh.Vertices.Length) continue;
                mesh.Vertices[index].Position += delta;
                moved++;
            }
            if (moved > 0) RecomputeNormalsAndTangents(mesh);
            return moved;
        }

        public static IReadOnlyList<int> SplitEdges(
            GModelMesh mesh,
            IEnumerable<(ushort A, ushort B)> edges)
        {
            if (mesh == null || mesh.IsSkinned) return Array.Empty<int>();
            HashSet<(ushort A, ushort B)> selected = new((edges ?? Enumerable.Empty<(ushort, ushort)>())
                .Select(edge => NormalizeEdge(edge.A, edge.B)));
            if (selected.Count == 0) return Array.Empty<int>();
            EnsureTriangleMaterials(mesh);
            List<MeshVertex> vertices = new(mesh.Vertices);
            List<ushort> indices = new(mesh.Indices.Length + selected.Count * 3);
            List<int> materials = new(mesh.TriangleMaterialIndices.Length + selected.Count);
            Dictionary<(ushort A, ushort B), ushort> midpoints = new();
            int triangles = mesh.Indices.Length / 3;
            for (int triangle = 0; triangle < triangles; triangle++)
            {
                ushort a = mesh.Indices[triangle * 3], b = mesh.Indices[triangle * 3 + 1], c = mesh.Indices[triangle * 3 + 2];
                int material = mesh.TriangleMaterialIndices[triangle];
                if (selected.Contains(NormalizeEdge(a, b)))
                {
                    ushort midpoint = Midpoint(vertices, a, b, midpoints);
                    AddTriangle(indices, materials, a, midpoint, c, material);
                    AddTriangle(indices, materials, midpoint, b, c, material);
                }
                else if (selected.Contains(NormalizeEdge(b, c)))
                {
                    ushort midpoint = Midpoint(vertices, b, c, midpoints);
                    AddTriangle(indices, materials, a, b, midpoint, material);
                    AddTriangle(indices, materials, a, midpoint, c, material);
                }
                else if (selected.Contains(NormalizeEdge(c, a)))
                {
                    ushort midpoint = Midpoint(vertices, c, a, midpoints);
                    AddTriangle(indices, materials, a, b, midpoint, material);
                    AddTriangle(indices, materials, midpoint, b, c, material);
                }
                else
                {
                    AddTriangle(indices, materials, a, b, c, material);
                }
            }
            mesh.Vertices = vertices.ToArray();
            mesh.Indices = indices.ToArray();
            mesh.TriangleMaterialIndices = materials.ToArray();
            mesh.FaceGroups.Clear();
            RecomputeNormalsAndTangents(mesh);
            return midpoints.Values.Select(value => (int)value).ToArray();
        }

        public static IReadOnlyList<int> ExtrudeFaces(GModelMesh mesh, IEnumerable<int> triangles, float distance)
        {
            if (mesh == null || mesh.IsSkinned) return Array.Empty<int>();
            HashSet<int> selected = new(triangles ?? Enumerable.Empty<int>());
            EnsureTriangleMaterials(mesh);
            List<MeshVertex> vertices = new(mesh.Vertices);
            List<ushort> indices = new();
            List<int> materials = new();
            List<int> topFaces = new();
            int originalTriangles = mesh.Indices.Length / 3;
            for (int triangle = 0; triangle < originalTriangles; triangle++)
            {
                int baseIndex = triangle * 3;
                ushort ia = mesh.Indices[baseIndex], ib = mesh.Indices[baseIndex + 1], ic = mesh.Indices[baseIndex + 2];
                int material = mesh.TriangleMaterialIndices[triangle];
                if (!selected.Contains(triangle))
                {
                    AddTriangle(indices, materials, ia, ib, ic, material);
                    continue;
                }

                Vector3 normal = SafeNormal(Vector3.Cross(
                    vertices[ib].Position - vertices[ia].Position,
                    vertices[ic].Position - vertices[ia].Position), Vector3.UnitY);
                ushort na = AppendOffset(vertices, vertices[ia], normal * distance);
                ushort nb = AppendOffset(vertices, vertices[ib], normal * distance);
                ushort nc = AppendOffset(vertices, vertices[ic], normal * distance);
                int top = materials.Count;
                AddTriangle(indices, materials, na, nb, nc, material);
                topFaces.Add(top);
                AddQuad(indices, materials, ia, ib, nb, na, material);
                AddQuad(indices, materials, ib, ic, nc, nb, material);
                AddQuad(indices, materials, ic, ia, na, nc, material);
            }
            mesh.Vertices = vertices.ToArray();
            mesh.Indices = indices.ToArray();
            mesh.TriangleMaterialIndices = materials.ToArray();
            mesh.FaceGroups.Clear();
            RecomputeNormalsAndTangents(mesh);
            return topFaces;
        }

        public static IReadOnlyList<int> AppendRibbon(
            GModelMesh mesh,
            IReadOnlyList<Vector3> points,
            float width,
            int materialIndex)
        {
            if (mesh == null || mesh.IsSkinned || points == null || points.Count < 2) return Array.Empty<int>();
            EnsureTriangleMaterials(mesh);
            List<MeshVertex> vertices = new(mesh.Vertices);
            List<ushort> indices = new(mesh.Indices);
            List<int> materials = new(mesh.TriangleMaterialIndices);
            List<int> faces = new();
            ushort[] left = new ushort[points.Count];
            ushort[] right = new ushort[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 direction = points[Math.Min(i + 1, points.Count - 1)] - points[Math.Max(0, i - 1)];
                Vector3 side = SafeNormal(Vector3.Cross(Vector3.UnitY, direction), Vector3.UnitX) * (width * 0.5f);
                float v = i / (float)(points.Count - 1);
                left[i] = Append(vertices, points[i] - side, new Vector2(0f, v));
                right[i] = Append(vertices, points[i] + side, new Vector2(1f, v));
            }
            for (int i = 0; i < points.Count - 1; i++)
            {
                faces.Add(materials.Count);
                AddTriangle(indices, materials, left[i], right[i], right[i + 1], materialIndex);
                faces.Add(materials.Count);
                AddTriangle(indices, materials, left[i], right[i + 1], left[i + 1], materialIndex);
            }
            mesh.Vertices = vertices.ToArray();
            mesh.Indices = indices.ToArray();
            mesh.TriangleMaterialIndices = materials.ToArray();
            RecomputeNormalsAndTangents(mesh);
            return faces;
        }

        public static GModelMesh GenerateLod(GModelMesh source, int level, float triangleRatio)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            int triangles = source.Indices.Length / 3;
            int target = Math.Clamp((int)MathF.Round(triangles * triangleRatio), 1, Math.Max(1, triangles));
            EnsureTriangleMaterials(source);
            List<ushort> indices = new(target * 3);
            List<int> materials = new(target);
            for (int output = 0; output < target; output++)
            {
                int triangle = Math.Min(triangles - 1, (int)((long)output * triangles / target));
                indices.Add(source.Indices[triangle * 3]);
                indices.Add(source.Indices[triangle * 3 + 1]);
                indices.Add(source.Indices[triangle * 3 + 2]);
                materials.Add(source.TriangleMaterialIndices[triangle]);
            }
            return new GModelMesh
            {
                Name = $"{source.Name} LOD{level}",
                SourceNodeIndex = source.SourceNodeIndex,
                MaterialIndex = source.MaterialIndex,
                Vertices = (MeshVertex[])source.Vertices.Clone(),
                SkinnedVertices = (SkinnedMeshVertex[])source.SkinnedVertices.Clone(),
                Indices = indices.ToArray(),
                TriangleMaterialIndices = materials.ToArray(),
                Tangents = (Vector4[])source.Tangents.Clone(),
                IsSkinned = source.IsSkinned,
                Lod = Math.Max(1, level),
                SourceUvProtected = source.SourceUvProtected,
                SourceUVs = (Vector2[])source.SourceUVs.Clone(),
                UvOverride = source.UvOverride,
                UvProjection = source.UvProjection,
                Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase),
            };
        }

        public static GModelCollider FitCollider(GModelAsset asset, GModelColliderShape shape)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            asset.RecalculateBounds();
            Vector3 size = Vector3.Max(asset.Bounds.Size * 0.5f, new Vector3(0.01f));
            if (shape == GModelColliderShape.Sphere)
                size = new Vector3(MathF.Max(size.X, MathF.Max(size.Y, size.Z)));
            else if (shape is GModelColliderShape.Capsule or GModelColliderShape.Cylinder)
                size = new Vector3(MathF.Max(size.X, size.Z), size.Y, 0f);
            return new GModelCollider { Shape = shape, Center = asset.Bounds.Center, Size = size };
        }

        public static bool TryCreateRigidBody(GModelAsset asset, out RigidBodyComponent body)
        {
            body = default;
            GModelCollider collider = asset?.Colliders?.FirstOrDefault();
            if (collider == null) return false;
            body = new RigidBodyComponent
            {
                Shape = collider.Shape switch
                {
                    GModelColliderShape.Sphere => CollisionShape.Sphere,
                    GModelColliderShape.Capsule => CollisionShape.Capsule,
                    GModelColliderShape.Cylinder => CollisionShape.Cylinder,
                    GModelColliderShape.ConvexHull => CollisionShape.ConvexHull,
                    GModelColliderShape.Mesh => CollisionShape.Mesh,
                    _ => CollisionShape.Box,
                },
                Motion = collider.Motion switch
                {
                    GModelColliderMotion.Dynamic => PhysicsMotionType.Dynamic,
                    GModelColliderMotion.Kinematic => PhysicsMotionType.Kinematic,
                    _ => PhysicsMotionType.Static,
                },
                Size = Vector3.Max(collider.Size, new Vector3(0.01f)),
                Mass = MathF.Max(0f, collider.Mass),
                Weight = MathF.Max(0f, collider.Mass),
                Friction = Math.Clamp(collider.Friction, 0f, 1f),
                Restitution = Math.Clamp(collider.Restitution, 0f, 1f),
                SpeculativeMargin = 0.06f,
                GravityScale = 1f,
                Flags = RigidBodyFlags.Collision
                    | (collider.Motion == GModelColliderMotion.Dynamic ? RigidBodyFlags.UseGravity : RigidBodyFlags.None)
                    | (collider.IsTrigger ? RigidBodyFlags.Sensor : RigidBodyFlags.None),
            };
            return true;
        }

        private static Vector2 SphericalUv(Vector3 p)
        {
            Vector3 n = SafeNormal(p, Vector3.UnitY);
            return new Vector2(0.5f + MathF.Atan2(n.Z, n.X) / (MathF.PI * 2f), 0.5f - MathF.Asin(n.Y) / MathF.PI);
        }

        private static Vector2 BoxUv(Vector3 normal, Vector3 p)
        {
            Vector3 n = Vector3.Abs(normal);
            return n.Y >= n.X && n.Y >= n.Z ? new Vector2(p.X, 1f - p.Z)
                : n.X >= n.Z ? new Vector2(p.Z, 1f - p.Y)
                : new Vector2(p.X, 1f - p.Y);
        }

        private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
            => value.LengthSquared() > 1e-12f && float.IsFinite(value.X + value.Y + value.Z)
                ? Vector3.Normalize(value)
                : Vector3.Normalize(fallback);

        private static void Bounds(IReadOnlyList<MeshVertex> vertices, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
            foreach (MeshVertex vertex in vertices)
            {
                min = Vector3.Min(min, vertex.Position); max = Vector3.Max(max, vertex.Position);
            }
        }

        private static ushort AppendOffset(List<MeshVertex> vertices, MeshVertex source, Vector3 offset)
        {
            if (vertices.Count >= ushort.MaxValue) throw new InvalidOperationException("The authored mesh exceeds 65,535 vertices.");
            source.Position += offset;
            vertices.Add(source);
            return (ushort)(vertices.Count - 1);
        }

        private static ushort Midpoint(
            List<MeshVertex> vertices,
            ushort a,
            ushort b,
            Dictionary<(ushort A, ushort B), ushort> midpoints)
        {
            (ushort A, ushort B) edge = NormalizeEdge(a, b);
            if (midpoints.TryGetValue(edge, out ushort existing)) return existing;
            if (vertices.Count >= ushort.MaxValue) throw new InvalidOperationException("The authored mesh exceeds 65,535 vertices.");
            MeshVertex left = vertices[a], right = vertices[b];
            MeshVertex midpoint = new()
            {
                Position = (left.Position + right.Position) * 0.5f,
                Normal = SafeNormal(left.Normal + right.Normal, left.Normal),
                Color = (left.Color + right.Color) * 0.5f,
                UV = (left.UV + right.UV) * 0.5f,
            };
            vertices.Add(midpoint);
            ushort index = (ushort)(vertices.Count - 1);
            midpoints.Add(edge, index);
            return index;
        }

        private static (ushort A, ushort B) NormalizeEdge(ushort a, ushort b)
            => a <= b ? (a, b) : (b, a);

        private static ushort Append(List<MeshVertex> vertices, Vector3 position, Vector2 uv)
        {
            if (vertices.Count >= ushort.MaxValue) throw new InvalidOperationException("The authored mesh exceeds 65,535 vertices.");
            vertices.Add(new MeshVertex { Position = position, Normal = Vector3.UnitY, Color = Vector4.One, UV = uv });
            return (ushort)(vertices.Count - 1);
        }

        private static void AddTriangle(List<ushort> indices, List<int> materials, ushort a, ushort b, ushort c, int material)
        {
            indices.Add(a); indices.Add(b); indices.Add(c); materials.Add(Math.Max(0, material));
        }

        private static void AddQuad(List<ushort> indices, List<int> materials, ushort a, ushort b, ushort c, ushort d, int material)
        {
            AddTriangle(indices, materials, a, b, c, material);
            AddTriangle(indices, materials, a, c, d, material);
        }

        private static void RemapFaceGroups(GModelMesh mesh, IReadOnlyList<int> keptOriginal)
        {
            Dictionary<int, int> remap = keptOriginal.Select((original, current) => (original, current))
                .ToDictionary(pair => pair.original, pair => pair.current);
            foreach (GModelFaceGroup group in mesh.FaceGroups)
                group.TriangleIndices = group.TriangleIndices.Where(remap.ContainsKey).Select(face => remap[face]).ToList();
            mesh.FaceGroups.RemoveAll(group => group.TriangleIndices.Count == 0);
        }
    }
}
