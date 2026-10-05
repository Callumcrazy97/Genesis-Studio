using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    private const uint PreparedMeshMagic = 0x314D4347;   // "GCM1"
    private const int PreparedMeshVersion = 1;
    // Magic, version, triangle count, the payload's length and its hash.
    private const int PreparedMeshHeaderBytes = 4 + 4 + 4 + 8 + 32;

    /// <summary>Set false to read and write no prepared meshes on disk. Also off when GENESIS_COLLIDER_CACHE=0.</summary>
    public static bool PreparedMeshCacheEnabled { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("GENESIS_COLLIDER_CACHE"), "0", StringComparison.Ordinal);

    /// <summary>Prepared meshes read from disk instead of built since the process started.</summary>
    public static int PreparedMeshCacheHits => _preparedMeshCacheHits;

    /// <summary>Prepared meshes written to disk since the process started.</summary>
    public static int PreparedMeshCacheWrites => _preparedMeshCacheWrites;

    private static int _preparedMeshCacheHits, _preparedMeshCacheWrites;

    /// <summary>
    /// Like <see cref="PrepareStaticTriangleMesh(IReadOnlyList{Vector3}, IReadOnlyList{int}, Vector3)"/>,
    /// but keeps the prepared mesh in <paramref name="cacheDirectory"/> and reads it back when the
    /// same triangles at the same scale are prepared again.
    /// </summary>
    /// <remarks>
    /// Building the search tree over a large terrain's triangles takes seconds on one thread, and a
    /// room's loading screen waited for it on every run. An entry is named by a hash of the
    /// triangles and the scale themselves, so a changed terrain can never find an old tree, and it
    /// is the physics engine's own copy of the finished mesh, so what is read back collides exactly
    /// as what was built. A damaged entry fails its hash and is built again. Only arrays are cached;
    /// any other list is prepared the ordinary way.
    /// </remarks>
    public static PreparedStaticMesh PrepareStaticTriangleMesh(
        IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, Vector3 scale, string? cacheDirectory)
    {
        if (!PreparedMeshCacheEnabled || string.IsNullOrWhiteSpace(cacheDirectory)
            || vertices is not Vector3[] vertexArray || indices is not int[] indexArray
            || indexArray.Length < 3 || indexArray.Length % 3 != 0)
            return PrepareStaticTriangleMesh(vertices, indices, scale);

        string path = Path.Combine(cacheDirectory, PreparedMeshKey(vertexArray, indexArray, scale) + ".gcm");
        int triangleCount = indexArray.Length / 3;
        if (TryReadPreparedMesh(path, triangleCount, out PreparedStaticMesh? cached))
        {
            Interlocked.Increment(ref _preparedMeshCacheHits);
            return cached!;
        }

        PreparedStaticMesh prepared = PrepareStaticTriangleMesh(vertices, indices, scale);
        byte[]? entry = null;
        try
        {
            // Copied out now, before the mesh can be handed to a simulation; written on a worker.
            int length = prepared.Mesh.GetSerializedByteCount();
            entry = new byte[PreparedMeshHeaderBytes + length];
            prepared.Mesh.Serialize(entry.AsSpan(PreparedMeshHeaderBytes));
            Span<byte> header = entry.AsSpan(0, PreparedMeshHeaderBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header, PreparedMeshMagic);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], PreparedMeshVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], triangleCount);
            BinaryPrimitives.WriteInt64LittleEndian(header[12..], length);
            SHA256.HashData(entry.AsSpan(PreparedMeshHeaderBytes), header.Slice(20, 32));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            entry = null;
        }

        if (entry != null) Task.Run(() => WritePreparedMesh(path, entry));
        return prepared;
    }

    private static string PreparedMeshKey(Vector3[] vertices, int[] indices, Vector3 scale)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> head = stackalloc byte[4 + 4 + 4 + 12];
        BinaryPrimitives.WriteInt32LittleEndian(head, PreparedMeshVersion);
        BinaryPrimitives.WriteInt32LittleEndian(head[4..], vertices.Length);
        BinaryPrimitives.WriteInt32LittleEndian(head[8..], indices.Length);
        BinaryPrimitives.WriteSingleLittleEndian(head[12..], scale.X);
        BinaryPrimitives.WriteSingleLittleEndian(head[16..], scale.Y);
        BinaryPrimitives.WriteSingleLittleEndian(head[20..], scale.Z);
        hash.AppendData(head);
        hash.AppendData(MemoryMarshal.AsBytes(vertices.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(indices.AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset(), 0, 16).ToLowerInvariant();
    }

    private static bool TryReadPreparedMesh(string path, int triangleCount, out PreparedStaticMesh? prepared)
    {
        prepared = null;
        BufferPool? pool = null;
        try
        {
            if (!File.Exists(path)) return false;
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length <= PreparedMeshHeaderBytes) return false;
            ReadOnlySpan<byte> header = bytes.AsSpan(0, PreparedMeshHeaderBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != PreparedMeshMagic
                || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != PreparedMeshVersion
                || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != triangleCount
                || BinaryPrimitives.ReadInt64LittleEndian(header[12..]) != bytes.Length - PreparedMeshHeaderBytes)
                return false;
            // The engine copies the stored mesh without checking it, so a damaged file must never reach it.
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(bytes.AsSpan(PreparedMeshHeaderBytes), hash);
            if (!hash.SequenceEqual(header.Slice(20, 32))) return false;

            pool = new BufferPool();
            var mesh = new Mesh(bytes.AsSpan(PreparedMeshHeaderBytes), pool);
            if (mesh.Triangles.Length != triangleCount)
            {
                pool.Clear();
                return false;
            }

            prepared = new PreparedStaticMesh { Mesh = mesh, Pool = pool, TriangleCount = triangleCount };
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or OutOfMemoryException or IndexOutOfRangeException)
        {
            pool?.Clear();
            prepared = null;
            return false;
        }
    }

    private static void WritePreparedMesh(string path, byte[] entry)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, entry);
            File.Move(temporary, path, overwrite: true);
            Interlocked.Increment(ref _preparedMeshCacheWrites);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
