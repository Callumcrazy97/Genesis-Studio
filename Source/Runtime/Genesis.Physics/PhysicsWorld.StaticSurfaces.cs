using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    private sealed class ExternalStatic
    {
        public StaticHandle Handle;
        public TypedIndex Shape;
        public string Label = string.Empty;
        /// <summary>The pool a prepared mesh was built in; null for shapes made from the simulation's own pool.</summary>
        public BufferPool? Pool;
    }

    /// <summary>
    /// A triangle mesh that is ready to be made solid: its triangles are copied and the tree the
    /// physics engine searches them with is built. Building that tree is nearly all the cost of a
    /// triangle-mesh collider, and it needs nothing from the physics world, so it can be done on a
    /// worker thread. Registering a prepared mesh then costs the game's thread next to nothing.
    /// Dispose one that is never registered.
    /// </summary>
    public sealed class PreparedStaticMesh : IDisposable
    {
        internal Mesh Mesh;
        // A buffer pool serves one thread at a time, and the simulation's pool belongs to the
        // thread that steps it; a mesh built elsewhere has a small pool of its own.
        internal BufferPool? Pool;

        /// <summary>How many triangles the mesh has.</summary>
        public int TriangleCount { get; internal init; }

        /// <summary>
        /// The physics engine's stored form of this mesh: its triangles, scale and search tree.
        /// Two meshes with the same bytes collide identically. Not valid once registered or disposed.
        /// </summary>
        public byte[] ToBytes()
        {
            if (Pool == null) throw new ObjectDisposedException(nameof(PreparedStaticMesh), "The prepared mesh was disposed or is already registered.");
            byte[] bytes = new byte[Mesh.GetSerializedByteCount()];
            Mesh.Serialize(bytes);
            return bytes;
        }

        public void Dispose()
        {
            BufferPool? pool = System.Threading.Interlocked.Exchange(ref Pool, null);
            if (pool == null) return;
            pool.Clear();
            GC.SuppressFinalize(this);
        }

        ~PreparedStaticMesh() => System.Threading.Interlocked.Exchange(ref Pool, null)?.Clear();
    }

    /// <summary>
    /// Copies a triangle mesh and builds its search tree. Safe to call from any thread, with or
    /// without a physics world: the result is handed to <see cref="RegisterStaticTriangleMesh(PreparedStaticMesh, Vector3, Quaternion, string, float, float)"/>.
    /// </summary>
    public static PreparedStaticMesh PrepareStaticTriangleMesh(
        IReadOnlyList<Vector3> vertices, IReadOnlyList<int> indices, Vector3 scale)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count < 3 || indices.Count % 3 != 0)
            throw new ArgumentException("Triangle indices must be a non-empty multiple of three.", nameof(indices));

        var pool = new BufferPool();
        try
        {
            pool.Take<Triangle>(indices.Count / 3, out Buffer<Triangle> triangles);
            for (int i = 0; i < triangles.Length; i++)
            {
                int offset = i * 3;
                triangles[i] = new Triangle(
                    vertices[indices[offset]],
                    vertices[indices[offset + 1]],
                    vertices[indices[offset + 2]]);
            }

            var mesh = new Mesh(triangles, new Vector3(
                MathF.Max(MathF.Abs(scale.X), 1e-5f),
                MathF.Max(MathF.Abs(scale.Y), 1e-5f),
                MathF.Max(MathF.Abs(scale.Z), 1e-5f)), pool);
            return new PreparedStaticMesh { Mesh = mesh, Pool = pool, TriangleCount = triangles.Length };
        }
        catch
        {
            pool.Clear();
            throw;
        }
    }

    /// <summary>Makes a prepared triangle mesh solid. The mesh belongs to this world from here on.</summary>
    public int RegisterStaticTriangleMesh(
        PreparedStaticMesh prepared,
        Vector3 position,
        Quaternion orientation,
        string label,
        float friction = 0.9f,
        float restitution = 0f)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        BufferPool pool = System.Threading.Interlocked.Exchange(ref prepared.Pool, null)
            ?? throw new ObjectDisposedException(nameof(PreparedStaticMesh), "The prepared mesh was disposed or is already registered.");
        GC.SuppressFinalize(prepared);
        TypedIndex shape = _simulation.Shapes.Add(prepared.Mesh);
        StaticHandle handle = _simulation.Statics.Add(new StaticDescription(position, orientation, shape));
        int registrationId = _nextRegistrationId++;
        _staticHandles[handle] = registrationId;
        _staticFriction[handle] = ClampFriction(friction);
        _staticRestitution[handle] = ClampRestitution(restitution);
        _staticSensor[handle] = false;
        _staticCollisionFilter[handle] = (0, 0x7Fu);
        _externalStatics[registrationId] = new ExternalStatic
        {
            Handle = handle, Shape = shape, Label = label ?? string.Empty, Pool = pool,
        };
        return registrationId;
    }

    private readonly Dictionary<int, ExternalStatic> _externalStatics = new();

    /// <summary>Scene-owned collision geometry, without creating gameplay instances for tiles.</summary>
    public int RegisterStaticBox(Vector3 position, Vector3 halfExtents, string label, float friction = .9f)
    {
        if (halfExtents.X <= 0 || halfExtents.Y <= 0 || halfExtents.Z <= 0
            || !float.IsFinite(halfExtents.LengthSquared()) || !float.IsFinite(position.LengthSquared()))
            throw new ArgumentException("Static box position and extents must be finite and positive.");
        TypedIndex shape = _simulation.Shapes.Add(new Box(halfExtents.X * 2, halfExtents.Y * 2, halfExtents.Z * 2));
        StaticHandle handle = _simulation.Statics.Add(new StaticDescription(position, Quaternion.Identity, shape));
        int registrationId = _nextRegistrationId++;
        _staticHandles[handle] = registrationId;
        _staticFriction[handle] = ClampFriction(friction);
        _staticRestitution[handle] = 0;
        _staticSensor[handle] = false;
        _staticCollisionFilter[handle] = (0, 0x7Fu);
        _externalStatics[registrationId] = new ExternalStatic { Handle = handle, Shape = shape, Label = label ?? string.Empty };
        return registrationId;
    }

    /// <summary>Registers a static triangle mesh owned by a scene subsystem.</summary>
    public int RegisterStaticTriangleMesh(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<int> indices,
        Vector3 scale,
        Vector3 position,
        Quaternion orientation,
        string label,
        float friction = 0.9f,
        float restitution = 0f)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count < 3 || indices.Count % 3 != 0)
            throw new ArgumentException("Triangle indices must be a non-empty multiple of three.", nameof(indices));

        _pool.Take<Triangle>(indices.Count / 3, out Buffer<Triangle> triangles);
        for (int i = 0; i < triangles.Length; i++)
        {
            int offset = i * 3;
            triangles[i] = new Triangle(
                vertices[indices[offset]],
                vertices[indices[offset + 1]],
                vertices[indices[offset + 2]]);
        }

        var mesh = new Mesh(triangles, new Vector3(
            MathF.Max(MathF.Abs(scale.X), 1e-5f),
            MathF.Max(MathF.Abs(scale.Y), 1e-5f),
            MathF.Max(MathF.Abs(scale.Z), 1e-5f)), _pool);
        TypedIndex shape = _simulation.Shapes.Add(mesh);
        StaticHandle handle = _simulation.Statics.Add(new StaticDescription(position, orientation, shape));
        int registrationId = _nextRegistrationId++;
        _staticHandles[handle] = registrationId;
        _staticFriction[handle] = ClampFriction(friction);
        _staticRestitution[handle] = ClampRestitution(restitution);
        _staticSensor[handle] = false;
        _staticCollisionFilter[handle] = (0, 0x7Fu);
        _externalStatics[registrationId] = new ExternalStatic { Handle = handle, Shape = shape, Label = label ?? string.Empty };
        return registrationId;
    }

    public bool UnregisterStaticSurface(int registrationId)
    {
        if (!_externalStatics.Remove(registrationId, out ExternalStatic? surface))
            return false;
        _simulation.Statics.Remove(surface.Handle);
        _simulation.Shapes.RemoveAndDispose(surface.Shape, surface.Pool ?? _pool);
        surface.Pool?.Clear();
        _staticHandles.Remove(surface.Handle);
        _staticFriction.Remove(surface.Handle);
        _staticRestitution.Remove(surface.Handle);
        _staticSensor.Remove(surface.Handle);
        _staticCollisionFilter.Remove(surface.Handle);
        return true;
    }

    public int ExternalStaticCount => _externalStatics.Count;
    internal bool IsExternalStatic(int registrationId) => _externalStatics.ContainsKey(registrationId);

    private void ClearExternalStatics()
    {
        foreach (int id in new List<int>(_externalStatics.Keys))
            UnregisterStaticSurface(id);
    }
}
