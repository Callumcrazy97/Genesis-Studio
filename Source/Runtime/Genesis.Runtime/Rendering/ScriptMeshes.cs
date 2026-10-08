using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Meshes a script builds and changes (a voxel chunk, a procedural rock, a trail): vertices and
    /// triangles added by PGSL, uploaded when next drawn after a change. One draw of a chunk replaces
    /// thousands of instances. A mesh holds up to 65535 vertices.
    /// </summary>
    public static class ScriptMeshes
    {
        public const int MaxVertices = 65535;

        private sealed class Builder
        {
            public readonly List<MeshVertex> Vertices = new();
            public readonly List<ushort> Indices = new();
            public MeshHandle Handle = MeshHandle.Invalid;
            public IRenderController Owner;
            public bool Dirty = true;
            public bool Overflowed;
        }

        private static readonly Dictionary<int, Builder> Meshes = new();
        private static int _next;

        public static int Create()
        {
            lock (Meshes)
            {
                int id = ++_next;
                Meshes[id] = new Builder();
                return id;
            }
        }

        public static bool Exists(int id) { lock (Meshes) return Meshes.ContainsKey(id); }

        public static void Clear(int id)
        {
            if (!TryGet(id, out Builder mesh)) return;
            mesh.Vertices.Clear();
            mesh.Indices.Clear();
            mesh.Overflowed = false;
            mesh.Dirty = true;
        }

        public static void Destroy(int id)
        {
            Builder mesh;
            lock (Meshes)
            {
                if (!Meshes.Remove(id, out mesh)) return;
            }
            if (mesh.Handle.IsValid) mesh.Owner?.ReleaseMesh(mesh.Handle);
        }

        /// <summary>Forgets every script mesh (a new game); the renderer's copies go with it.</summary>
        public static void Reset()
        {
            UploadBudgetMilliseconds = DefaultUploadBudgetMilliseconds;
            BeginFrame();
            List<Builder> all;
            lock (Meshes)
            {
                all = new List<Builder>(Meshes.Values);
                Meshes.Clear();
            }
            foreach (Builder mesh in all)
                if (mesh.Handle.IsValid) mesh.Owner?.ReleaseMesh(mesh.Handle);
        }

        /// <summary>Adds a vertex and returns its index; -1 when the mesh is full or unknown.</summary>
        public static int AddVertex(int id, Vector3 position, Vector3 normal, Vector2 uv, Vector4 colour)
        {
            if (!TryGet(id, out Builder mesh)) return -1;
            if (mesh.Vertices.Count >= MaxVertices) { mesh.Overflowed = true; return -1; }
            mesh.Vertices.Add(new MeshVertex
            {
                Position = position,
                Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY,
                UV = uv,
                Color = colour,
            });
            mesh.Dirty = true;
            return mesh.Vertices.Count - 1;
        }

        public static bool AddTriangle(int id, int a, int b, int c)
        {
            if (!TryGet(id, out Builder mesh)) return false;
            int count = mesh.Vertices.Count;
            if ((uint)a >= (uint)count || (uint)b >= (uint)count || (uint)c >= (uint)count) return false;
            mesh.Indices.Add((ushort)a);
            mesh.Indices.Add((ushort)b);
            mesh.Indices.Add((ushort)c);
            mesh.Dirty = true;
            return true;
        }

        /// <summary>Face bits for <see cref="AddCube"/>.</summary>
        public const int PositiveX = 1, NegativeX = 2, PositiveY = 4, NegativeY = 8, PositiveZ = 16, NegativeZ = 32, AllFaces = 63;

        /// <summary>
        /// A cube's chosen faces (a voxel's faces that touch air), centred on a point, each with the
        /// same texture rectangle of an atlas. Returns the faces added.
        /// </summary>
        public static int AddCube(int id, Vector3 centre, float size, int faces, Vector4 colour, Vector4 uv) =>
            AddCube(id, centre, size, faces, colour, uv, uv, uv);

        /// <summary>
        /// The same with a tile of its own for the top (+Y), the four sides and the bottom (-Y): a
        /// grass block, a log, a crate.
        /// </summary>
        public static int AddCube(int id, Vector3 centre, float size, int faces, Vector4 colour, Vector4 top, Vector4 side, Vector4 bottom)
        {
            if (!TryGet(id, out Builder mesh)) return 0;
            float h = size * 0.5f;
            int added = 0;
            void Face(int bit, Vector3 normal, Vector3 up, Vector3 right)
            {
                if ((faces & bit) == 0) return;
                Vector4 uv = bit == PositiveY ? top : bit == NegativeY ? bottom : side;
                if (mesh.Vertices.Count + 4 > MaxVertices) { mesh.Overflowed = true; return; }
                Vector3 c = centre + normal * h;
                ushort start = (ushort)mesh.Vertices.Count;
                mesh.Vertices.Add(new MeshVertex { Position = c - right + up, Normal = normal, Color = colour, UV = new Vector2(uv.X, uv.Y) });
                mesh.Vertices.Add(new MeshVertex { Position = c + right + up, Normal = normal, Color = colour, UV = new Vector2(uv.Z, uv.Y) });
                mesh.Vertices.Add(new MeshVertex { Position = c + right - up, Normal = normal, Color = colour, UV = new Vector2(uv.Z, uv.W) });
                mesh.Vertices.Add(new MeshVertex { Position = c - right - up, Normal = normal, Color = colour, UV = new Vector2(uv.X, uv.W) });
                mesh.Indices.Add(start); mesh.Indices.Add((ushort)(start + 1)); mesh.Indices.Add((ushort)(start + 2));
                mesh.Indices.Add(start); mesh.Indices.Add((ushort)(start + 2)); mesh.Indices.Add((ushort)(start + 3));
                added++;
            }
            // The same face layout as the engine's own cube (MeshGeometry.BuildCube).
            Face(PositiveY, Vector3.UnitY, new Vector3(0, 0, h), new Vector3(h, 0, 0));
            Face(NegativeY, -Vector3.UnitY, new Vector3(0, 0, -h), new Vector3(h, 0, 0));
            Face(PositiveX, Vector3.UnitX, new Vector3(0, h, 0), new Vector3(0, 0, h));
            Face(NegativeX, -Vector3.UnitX, new Vector3(0, h, 0), new Vector3(0, 0, -h));
            Face(PositiveZ, Vector3.UnitZ, new Vector3(0, h, 0), new Vector3(-h, 0, 0));
            Face(NegativeZ, -Vector3.UnitZ, new Vector3(0, h, 0), new Vector3(h, 0, 0));
            if (added > 0) mesh.Dirty = true;
            return added;
        }

        /// <summary>
        /// A four-cornered face in one call: corners in order around it, turning the way
        /// <see cref="AddTriangle"/> expects, all with one normal and colour, the texture rectangle
        /// u0, v0 (first corner) to u1, v1 (third corner). Returns the first corner's index, -1 when full.
        /// </summary>
        public static int AddQuad(int id, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal, Vector4 uv, Vector4 colour) =>
            AddQuad(id, p0, p1, p2, p3, normal, uv, colour, colour, colour, colour, flip: false);

        /// <summary>
        /// The same with a colour for each corner (baked light, ambient occlusion, gradients).
        /// <paramref name="flip"/> splits the quad along the other diagonal, (1,2,3) and (1,3,0), so
        /// corner colours blend evenly whichever way they differ.
        /// </summary>
        public static int AddQuad(int id, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal, Vector4 uv,
            Vector4 c0, Vector4 c1, Vector4 c2, Vector4 c3, bool flip)
        {
            if (!TryGet(id, out Builder mesh)) return -1;
            if (mesh.Vertices.Count + 4 > MaxVertices) { mesh.Overflowed = true; return -1; }
            Vector3 n = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            int start = mesh.Vertices.Count;
            mesh.Vertices.Add(new MeshVertex { Position = p0, Normal = n, Color = c0, UV = new Vector2(uv.X, uv.Y) });
            mesh.Vertices.Add(new MeshVertex { Position = p1, Normal = n, Color = c1, UV = new Vector2(uv.Z, uv.Y) });
            mesh.Vertices.Add(new MeshVertex { Position = p2, Normal = n, Color = c2, UV = new Vector2(uv.Z, uv.W) });
            mesh.Vertices.Add(new MeshVertex { Position = p3, Normal = n, Color = c3, UV = new Vector2(uv.X, uv.W) });
            ushort a = (ushort)start, b = (ushort)(start + 1), c = (ushort)(start + 2), d = (ushort)(start + 3);
            if (flip)
            {
                mesh.Indices.Add(b); mesh.Indices.Add(c); mesh.Indices.Add(d);
                mesh.Indices.Add(b); mesh.Indices.Add(d); mesh.Indices.Add(a);
            }
            else
            {
                mesh.Indices.Add(a); mesh.Indices.Add(b); mesh.Indices.Add(c);
                mesh.Indices.Add(a); mesh.Indices.Add(c); mesh.Indices.Add(d);
            }
            mesh.Dirty = true;
            return start;
        }

        public static int VertexCount(int id) => TryGet(id, out Builder mesh) ? mesh.Vertices.Count : 0;

        public static int TriangleCount(int id) => TryGet(id, out Builder mesh) ? mesh.Indices.Count / 3 : 0;

        public static bool Overflowed(int id) => TryGet(id, out Builder mesh) && mesh.Overflowed;

        /// <summary>The mesh's triangles for a collider (positions and indices).</summary>
        public static bool TryGetGeometry(int id, out Vector3[] positions, out int[] indices)
        {
            positions = Array.Empty<Vector3>();
            indices = Array.Empty<int>();
            if (!TryGet(id, out Builder mesh) || mesh.Indices.Count < 3) return false;
            positions = new Vector3[mesh.Vertices.Count];
            for (int i = 0; i < positions.Length; i++) positions[i] = mesh.Vertices[i].Position;
            indices = new int[mesh.Indices.Count];
            // Render triangles turn one way, physics wants the other (as ModelColliderBinding).
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                indices[i] = mesh.Indices[i];
                indices[i + 1] = mesh.Indices[i + 2];
                indices[i + 2] = mesh.Indices[i + 1];
            }
            return true;
        }

        /// <summary>
        /// Milliseconds a frame may spend sending new or changed script meshes to the GPU (0 = no
        /// limit). At least one goes every frame; the rest go on later frames, a changed mesh drawing
        /// its previous build meanwhile and a new one appearing when its turn comes, so building
        /// many meshes at once never stalls a frame.
        /// </summary>
        public static double UploadBudgetMilliseconds { get; set; } = DefaultUploadBudgetMilliseconds;
        public const double DefaultUploadBudgetMilliseconds = 4;

        private static long _frameUploadTicks;
        private static int _frameUploads;

        /// <summary>Starts a frame's upload budget; the host calls it before the frame's Draw events.</summary>
        public static void BeginFrame()
        {
            _frameUploadTicks = 0;
            _frameUploads = 0;
        }

        /// <summary>Whether a mesh's current build is on the GPU (false while it waits for its turn).</summary>
        public static bool IsUploaded(int id) =>
            TryGet(id, out Builder mesh) && !mesh.Dirty && (mesh.Handle.IsValid || mesh.Indices.Count < 3);

        /// <summary>The renderer's mesh for a script mesh, uploaded again if it changed since (within the frame's budget).</summary>
        public static MeshHandle Resolve(int id, IRenderController renderer)
        {
            if (renderer == null || !TryGet(id, out Builder mesh)) return MeshHandle.Invalid;
            bool sameOwner = ReferenceEquals(mesh.Owner, renderer);
            if (!mesh.Dirty && mesh.Handle.IsValid && sameOwner) return mesh.Handle;
            if (UploadBudgetMilliseconds > 0 && _frameUploads > 0
                && _frameUploadTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= UploadBudgetMilliseconds)
                return mesh.Handle.IsValid && sameOwner ? mesh.Handle : MeshHandle.Invalid;

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (mesh.Handle.IsValid) mesh.Owner?.ReleaseMesh(mesh.Handle);
            mesh.Handle = MeshHandle.Invalid;
            mesh.Owner = renderer;
            mesh.Dirty = false;
            if (mesh.Indices.Count >= 3)
                mesh.Handle = renderer.RegisterMesh(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mesh.Vertices),
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(mesh.Indices));
            _frameUploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            _frameUploads++;
            return mesh.Handle;
        }

        private static bool TryGet(int id, out Builder mesh)
        {
            lock (Meshes) return Meshes.TryGetValue(id, out mesh);
        }
    }
}
