using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Meshes a script builds and changes (a voxel chunk, a procedural rock, a trail): vertices and
    /// triangles added by PGSL, uploaded when next drawn after a change. One draw of a chunk replaces
    /// thousands of instances. A mesh holds up to 65535 vertices. Its storage is pooled
    /// (<see cref="MeshBuffers"/>), and a worker job can build a whole mesh and hand it over finished
    /// (<see cref="TakeBuild"/>).
    /// </summary>
    public static class ScriptMeshes
    {
        public const int MaxVertices = 65535;

        private sealed class Builder
        {
            public MeshBuffers Buffers = new();
            public MeshHandle Handle = MeshHandle.Invalid;
            public IRenderController Owner;
            public bool Dirty = true;
            public int Uploads;
        }

        private static readonly Dictionary<int, Builder> Meshes = new();
        private static int _next;

        // Builders of destroyed meshes, kept with their storage for the next MeshCreate: a game that
        // streams chunks makes and frees meshes of much the same size all the time, and growing a
        // new 12,000-vertex list from nothing allocated about 1.6 MB (most of it on the large
        // object heap) for every chunk.
        private static readonly Stack<Builder> Spare = new();
        private static long _spareBytes;

        /// <summary>The most vertex and index storage kept for reuse by meshes made later.</summary>
        public const long MaxSpareBytes = 48L * 1024 * 1024;

        public static int Create()
        {
            lock (Meshes)
            {
                int id = ++_next;
                Builder mesh;
                if (Spare.Count > 0)
                {
                    mesh = Spare.Pop();
                    _spareBytes -= mesh.Buffers.Bytes;
                }
                else mesh = new Builder();
                Meshes[id] = mesh;
                return id;
            }
        }

        public static bool Exists(int id) { lock (Meshes) return Meshes.ContainsKey(id); }

        public static void Clear(int id)
        {
            if (!TryGet(id, out Builder mesh)) return;
            mesh.Buffers.Clear();
            mesh.Dirty = true;
        }

        /// <summary>
        /// Frees a mesh: the renderer's copy (its GPU buffers, or the previous build's while a
        /// rebuild waits for its upload turn) is released now and its own storage is kept for a
        /// mesh made later.
        /// </summary>
        public static void Destroy(int id)
        {
            Builder mesh;
            lock (Meshes)
            {
                if (!Meshes.Remove(id, out mesh)) return;
            }
            if (mesh.Handle.IsValid) mesh.Owner?.ReleaseMesh(mesh.Handle);
            Recycle(mesh);
        }

        private static void Recycle(Builder mesh)
        {
            mesh.Buffers.Clear();
            mesh.Handle = MeshHandle.Invalid;
            mesh.Owner = null;
            mesh.Dirty = true;
            long bytes = mesh.Buffers.Bytes;
            lock (Meshes)
            {
                if (_spareBytes + bytes <= MaxSpareBytes)
                {
                    Spare.Push(mesh);
                    _spareBytes += bytes;
                    return;
                }
            }
            // Not kept whole: its arrays still go back to the shared pool.
            mesh.Buffers.Release();
        }

        /// <summary>Makes room in a mesh for this many more vertices and indices (at most a full mesh).</summary>
        internal static void Reserve(int id, int vertices, int indices)
        {
            if (TryGet(id, out Builder mesh)) mesh.Buffers.Reserve(vertices, indices);
        }

        /// <summary>Storage kept for reuse, in bytes (for checks).</summary>
        internal static long SpareBytes { get { lock (Meshes) return _spareBytes; } }

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
                Spare.Clear();
                _spareBytes = 0;
            }
            foreach (Builder mesh in all)
                if (mesh.Handle.IsValid) mesh.Owner?.ReleaseMesh(mesh.Handle);
        }

        /// <summary>
        /// A mesh's storage to add to directly (the batch commands), marked changed; null when the
        /// mesh is unknown. Game thread only.
        /// </summary>
        internal static MeshBuffers BuffersToChange(int id)
        {
            if (!TryGet(id, out Builder mesh)) return null;
            mesh.Dirty = true;
            return mesh.Buffers;
        }

        /// <summary>
        /// A mesh built elsewhere (a worker job) becomes this mesh's content as it is: its arrays are
        /// taken, not copied, and the mesh's previous storage goes back to the pool. Uploaded when next
        /// drawn, within the frame's upload budget; the previous build draws until then. False (and the
        /// build released) when the mesh no longer exists.
        /// </summary>
        public static bool TakeBuild(int id, MeshBuffers built)
        {
            if (built is null) return false;
            if (!TryGet(id, out Builder mesh)) { built.Release(); return false; }
            MeshBuffers previous = mesh.Buffers;
            mesh.Buffers = built;
            mesh.Dirty = true;
            previous.Release();
            return true;
        }

        /// <summary>Adds a vertex and returns its index; -1 when the mesh is full or unknown.</summary>
        public static int AddVertex(int id, Vector3 position, Vector3 normal, Vector2 uv, Vector4 colour) =>
            BuffersToChange(id)?.AddVertex(position, normal, uv, colour) ?? -1;

        public static bool AddTriangle(int id, int a, int b, int c) =>
            BuffersToChange(id)?.AddTriangle(a, b, c) ?? false;

        /// <summary>Face bits for <see cref="AddCube(int, Vector3, float, int, Vector4, Vector4)"/>.</summary>
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
        public static int AddCube(int id, Vector3 centre, float size, int faces, Vector4 colour, Vector4 top, Vector4 side, Vector4 bottom) =>
            BuffersToChange(id)?.AddCube(centre, size, faces, colour, top, side, bottom) ?? 0;

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
            Vector4 c0, Vector4 c1, Vector4 c2, Vector4 c3, bool flip) =>
            BuffersToChange(id)?.AddQuad(p0, p1, p2, p3, normal, uv, c0, c1, c2, c3, flip) ?? -1;

        public static int VertexCount(int id) => TryGet(id, out Builder mesh) ? mesh.Buffers.VertexCount : 0;

        public static int TriangleCount(int id) => TryGet(id, out Builder mesh) ? mesh.Buffers.IndexCount / 3 : 0;

        public static bool Overflowed(int id) => TryGet(id, out Builder mesh) && mesh.Buffers.Overflowed;

        /// <summary>A copy of the mesh's vertices and indices, for checks.</summary>
        internal static bool TryGetVertices(int id, out MeshVertex[] vertices, out ushort[] indices)
        {
            vertices = [];
            indices = [];
            if (!TryGet(id, out Builder mesh)) return false;
            vertices = mesh.Buffers.VertexSpan.ToArray();
            indices = mesh.Buffers.IndexSpan.ToArray();
            return true;
        }

        /// <summary>The mesh's triangles for a collider (positions and indices).</summary>
        public static bool TryGetGeometry(int id, out Vector3[] positions, out int[] indices)
        {
            positions = [];
            indices = [];
            if (!TryGet(id, out Builder mesh) || mesh.Buffers.IndexCount < 3) return false;
            MeshBuffers buffers = mesh.Buffers;
            positions = new Vector3[buffers.VertexCount];
            for (int i = 0; i < positions.Length; i++) positions[i] = buffers.Vertices[i].Position;
            indices = new int[buffers.IndexCount];
            // Render triangles turn one way, physics wants the other (as ModelColliderBinding).
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                indices[i] = buffers.Indices[i];
                indices[i + 1] = buffers.Indices[i + 2];
                indices[i + 2] = buffers.Indices[i + 1];
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
            TryGet(id, out Builder mesh) && !mesh.Dirty && (mesh.Handle.IsValid || mesh.Buffers.IndexCount < 3);

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
            if (mesh.Buffers.IndexCount >= 3)
                mesh.Handle = renderer.RegisterMesh(mesh.Buffers.VertexSpan, mesh.Buffers.IndexSpan);
            mesh.Uploads++;
            _frameUploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            _frameUploads++;
            return mesh.Handle;
        }

        /// <summary>How many times the mesh has gone to a renderer; a change means a new build is drawn.</summary>
        public static int UploadCount(int id) => TryGet(id, out Builder mesh) ? mesh.Uploads : 0;

        /// <summary>The box around the mesh's vertices (its own space); false when it has none.</summary>
        public static bool TryGetBounds(int id, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.Zero;
            if (!TryGet(id, out Builder mesh) || mesh.Buffers.VertexCount == 0) return false;
            ReadOnlySpan<MeshVertex> vertices = mesh.Buffers.VertexSpan;
            min = max = vertices[0].Position;
            for (int i = 1; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i].Position);
                max = Vector3.Max(max, vertices[i].Position);
            }
            return true;
        }

        private static bool TryGet(int id, out Builder mesh)
        {
            lock (Meshes) return Meshes.TryGetValue(id, out mesh);
        }
    }
}
