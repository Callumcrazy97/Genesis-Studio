using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// A script mesh's vertices and triangles in pooled arrays. The game's meshes keep theirs in one
    /// (<see cref="ScriptMeshes"/>); a worker job builds a chunk into one of its own, and the game takes
    /// the finished arrays as they are (no copy) and gives the old ones back to the pool.
    /// </summary>
    public sealed class MeshBuffers
    {
        public MeshVertex[] Vertices = [];
        public int VertexCount;
        public ushort[] Indices = [];
        public int IndexCount;
        public bool Overflowed;

        public ReadOnlySpan<MeshVertex> VertexSpan => Vertices.AsSpan(0, VertexCount);
        public ReadOnlySpan<ushort> IndexSpan => Indices.AsSpan(0, IndexCount);

        /// <summary>Storage held, in bytes.</summary>
        public long Bytes => ((long)Vertices.Length * Unsafe.SizeOf<MeshVertex>()) + ((long)Indices.Length * sizeof(ushort));

        public void Clear()
        {
            VertexCount = 0;
            IndexCount = 0;
            Overflowed = false;
        }

        /// <summary>Room for this many more vertices and indices (at most a full mesh).</summary>
        public void Reserve(int vertices, int indices)
        {
            if (vertices > 0) GrowVertices(Math.Min(ScriptMeshes.MaxVertices, VertexCount + vertices));
            if (indices > 0) GrowIndices(Math.Min(ScriptMeshes.MaxVertices * 6, IndexCount + indices));
        }

        /// <summary>Gives the arrays back to the pool; the buffers are empty afterwards.</summary>
        public void Release()
        {
            MeshArrayPool.Return(Vertices);
            MeshArrayPool.Return(Indices);
            Vertices = [];
            Indices = [];
            Clear();
        }

        private void GrowVertices(int wanted)
        {
            if (wanted <= Vertices.Length) return;
            MeshVertex[] bigger = MeshArrayPool.Rent<MeshVertex>(Math.Max(wanted, Math.Max(256, Vertices.Length * 2)));
            Array.Copy(Vertices, bigger, VertexCount);
            MeshArrayPool.Return(Vertices);
            Vertices = bigger;
        }

        private void GrowIndices(int wanted)
        {
            if (wanted <= Indices.Length) return;
            ushort[] bigger = MeshArrayPool.Rent<ushort>(Math.Max(wanted, Math.Max(384, Indices.Length * 2)));
            Array.Copy(Indices, bigger, IndexCount);
            MeshArrayPool.Return(Indices);
            Indices = bigger;
        }

        private void PushIndex(ushort index)
        {
            if (IndexCount == Indices.Length) GrowIndices(IndexCount + 1);
            Indices[IndexCount++] = index;
        }

        private void PushVertex(in MeshVertex vertex)
        {
            if (VertexCount == Vertices.Length) GrowVertices(VertexCount + 1);
            Vertices[VertexCount++] = vertex;
        }

        /// <summary>Adds a vertex and returns its index; -1 when the mesh is full.</summary>
        public int AddVertex(Vector3 position, Vector3 normal, Vector2 uv, Vector4 colour)
        {
            if (VertexCount >= ScriptMeshes.MaxVertices) { Overflowed = true; return -1; }
            PushVertex(new MeshVertex
            {
                Position = position,
                Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY,
                UV = uv,
                Color = colour,
            });
            return VertexCount - 1;
        }

        public bool AddTriangle(int a, int b, int c)
        {
            if ((uint)a >= (uint)VertexCount || (uint)b >= (uint)VertexCount || (uint)c >= (uint)VertexCount) return false;
            if (IndexCount + 3 > Indices.Length) GrowIndices(IndexCount + 3);
            Indices[IndexCount++] = (ushort)a;
            Indices[IndexCount++] = (ushort)b;
            Indices[IndexCount++] = (ushort)c;
            return true;
        }

        /// <summary>A cube's chosen faces (see <see cref="ScriptMeshes.AllFaces"/>), each with its tile; returns the faces added.</summary>
        public int AddCube(Vector3 centre, float size, int faces, Vector4 colour, Vector4 top, Vector4 side, Vector4 bottom)
        {
            float h = size * 0.5f;
            int added = 0;
            // The same face layout as the engine's own cube (MeshGeometry.BuildCube).
            added += Face(ScriptMeshes.PositiveY, Vector3.UnitY, new Vector3(0, 0, h), new Vector3(h, 0, 0));
            added += Face(ScriptMeshes.NegativeY, -Vector3.UnitY, new Vector3(0, 0, -h), new Vector3(h, 0, 0));
            added += Face(ScriptMeshes.PositiveX, Vector3.UnitX, new Vector3(0, h, 0), new Vector3(0, 0, h));
            added += Face(ScriptMeshes.NegativeX, -Vector3.UnitX, new Vector3(0, h, 0), new Vector3(0, 0, -h));
            added += Face(ScriptMeshes.PositiveZ, Vector3.UnitZ, new Vector3(0, h, 0), new Vector3(-h, 0, 0));
            added += Face(ScriptMeshes.NegativeZ, -Vector3.UnitZ, new Vector3(0, h, 0), new Vector3(h, 0, 0));
            return added;

            int Face(int bit, Vector3 normal, Vector3 up, Vector3 right)
            {
                if ((faces & bit) == 0) return 0;
                Vector4 uv = bit == ScriptMeshes.PositiveY ? top : bit == ScriptMeshes.NegativeY ? bottom : side;
                if (VertexCount + 4 > ScriptMeshes.MaxVertices) { Overflowed = true; return 0; }
                Vector3 c = centre + (normal * h);
                ushort start = (ushort)VertexCount;
                PushVertex(new MeshVertex { Position = c - right + up, Normal = normal, Color = colour, UV = new Vector2(uv.X, uv.Y) });
                PushVertex(new MeshVertex { Position = c + right + up, Normal = normal, Color = colour, UV = new Vector2(uv.Z, uv.Y) });
                PushVertex(new MeshVertex { Position = c + right - up, Normal = normal, Color = colour, UV = new Vector2(uv.Z, uv.W) });
                PushVertex(new MeshVertex { Position = c - right - up, Normal = normal, Color = colour, UV = new Vector2(uv.X, uv.W) });
                PushIndex(start); PushIndex((ushort)(start + 1)); PushIndex((ushort)(start + 2));
                PushIndex(start); PushIndex((ushort)(start + 2)); PushIndex((ushort)(start + 3));
                return 1;
            }
        }

        /// <summary>
        /// A four-cornered face with a colour for each corner; <paramref name="flip"/> splits it along
        /// the other diagonal. Returns the first corner's index, -1 when full.
        /// </summary>
        public int AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal, Vector4 uv,
            Vector4 c0, Vector4 c1, Vector4 c2, Vector4 c3, bool flip)
        {
            if (VertexCount + 4 > ScriptMeshes.MaxVertices) { Overflowed = true; return -1; }
            Vector3 n = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
            int start = VertexCount;
            if (VertexCount + 4 > Vertices.Length) GrowVertices(VertexCount + 4);
            Vertices[VertexCount++] = new MeshVertex { Position = p0, Normal = n, Color = c0, UV = new Vector2(uv.X, uv.Y) };
            Vertices[VertexCount++] = new MeshVertex { Position = p1, Normal = n, Color = c1, UV = new Vector2(uv.Z, uv.Y) };
            Vertices[VertexCount++] = new MeshVertex { Position = p2, Normal = n, Color = c2, UV = new Vector2(uv.Z, uv.W) };
            Vertices[VertexCount++] = new MeshVertex { Position = p3, Normal = n, Color = c3, UV = new Vector2(uv.X, uv.W) };
            ushort a = (ushort)start, b = (ushort)(start + 1), c = (ushort)(start + 2), d = (ushort)(start + 3);
            if (IndexCount + 6 > Indices.Length) GrowIndices(IndexCount + 6);
            if (flip)
            {
                Indices[IndexCount++] = b; Indices[IndexCount++] = c; Indices[IndexCount++] = d;
                Indices[IndexCount++] = b; Indices[IndexCount++] = d; Indices[IndexCount++] = a;
            }
            else
            {
                Indices[IndexCount++] = a; Indices[IndexCount++] = b; Indices[IndexCount++] = c;
                Indices[IndexCount++] = a; Indices[IndexCount++] = c; Indices[IndexCount++] = d;
            }
            return start;
        }
    }

    /// <summary>
    /// Vertex and index arrays kept for reuse by size (powers of two), shared by the game's thread and
    /// worker jobs; at most <see cref="MaxBytes"/> are kept, the rest are left to the collector.
    /// </summary>
    public static class MeshArrayPool
    {
        public const long MaxBytes = 96L * 1024 * 1024;

        private static class Buckets<T>
        {
            public static readonly ConcurrentBag<T[]>[] Bags = CreateBags();

            private static ConcurrentBag<T[]>[] CreateBags()
            {
                var bags = new ConcurrentBag<T[]>[24];
                for (int i = 0; i < bags.Length; i++) bags[i] = [];
                return bags;
            }
        }

        private static long _held;
        private static long _rents, _reused;

        /// <summary>Bytes held for reuse now.</summary>
        public static long HeldBytes => Interlocked.Read(ref _held);
        /// <summary>Rents so far, and how many of them reused a pooled array (for checks and speed reports).</summary>
        public static long Rents => Interlocked.Read(ref _rents);
        public static long Reused => Interlocked.Read(ref _reused);

        private static int Bucket(int length) => length <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(length - 1));

        public static T[] Rent<T>(int minimum)
        {
            Interlocked.Increment(ref _rents);
            int bucket = Bucket(Math.Max(1, minimum));
            if (bucket < Buckets<T>.Bags.Length && Buckets<T>.Bags[bucket].TryTake(out T[] array))
            {
                Interlocked.Increment(ref _reused);
                Interlocked.Add(ref _held, -((long)array.Length * Unsafe.SizeOf<T>()));
                return array;
            }
            return new T[bucket < 31 ? 1 << bucket : minimum];
        }

        public static void Return<T>(T[] array)
        {
            if (array is null || array.Length == 0) return;
            int bucket = Bucket(array.Length);
            // Only exact powers of two go back, so every array in a bucket is that bucket's size.
            if (bucket >= Buckets<T>.Bags.Length || array.Length != 1 << bucket) return;
            long bytes = (long)array.Length * Unsafe.SizeOf<T>();
            if (Interlocked.Read(ref _held) + bytes > MaxBytes) return;
            Interlocked.Add(ref _held, bytes);
            Buckets<T>.Bags[bucket].Add(array);
        }
    }
}
