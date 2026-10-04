using System;
using System.IO;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using SharedCollisionShape = Genesis.Shared.ECS.Components.CollisionShape;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    // One triangle mesh (its triangles and its tree) per model geometry, shared by every instance:
    // an instance's shape is a copy of the mesh with its own scale. 241 buildings once built 241
    // copies of their triangles and ran the pool out of memory.
    private sealed class SharedMesh
    {
        public Mesh Mesh;
        public int Users;
    }

    private readonly System.Collections.Generic.Dictionary<GeometryKey, SharedMesh> _sharedMeshes = new();
    private readonly System.Collections.Generic.Dictionary<uint, GeometryKey> _sharedMeshShapes = new();

    private readonly struct GeometryKey : IEquatable<GeometryKey>
    {
        public readonly Vector3[] Vertices;
        public readonly int[] Indices;
        public GeometryKey(Vector3[] vertices, int[] indices) { Vertices = vertices; Indices = indices; }
        public bool Equals(GeometryKey other) => ReferenceEquals(Vertices, other.Vertices) && ReferenceEquals(Indices, other.Indices);
        public override bool Equals(object? obj) => obj is GeometryKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Vertices),
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Indices));
    }

    /// <summary>Instances sharing model collision meshes, and the meshes they share (for diagnostics and tests).</summary>
    public (int SharedMeshes, int Instances) SharedMeshCounts()
    {
        int instances = 0;
        foreach (SharedMesh shared in _sharedMeshes.Values) instances += shared.Users;
        return (_sharedMeshes.Count, instances);
    }

    /// <summary>Removes a body's shape; a shared mesh is freed when its last instance goes.</summary>
    private void ReleaseShape(TypedIndex shape)
    {
        if (_sharedMeshShapes.Remove(shape.Packed, out GeometryKey key) && _sharedMeshes.TryGetValue(key, out SharedMesh? shared))
        {
            _simulation.Shapes.Remove(shape);
            if (--shared.Users <= 0)
            {
                shared.Mesh.Dispose(_pool);
                _sharedMeshes.Remove(key);
            }
            return;
        }
        _simulation.Shapes.RemoveAndDispose(shape, _pool);
    }

    private (TypedIndex Shape, BodyInertia Inertia, Vector3 Center) CreateAuthoredShape(IEcsWorld world, Entity entity, RigidBodyComponent body)
    {
        if (body.Shape is not (SharedCollisionShape.Mesh or SharedCollisionShape.ConvexHull))
        {
            var primitive = CreateShape(body);
            return (primitive.Shape, primitive.Inertia, Vector3.Zero);
        }
        if (!world.Has<MeshColliderComponent>(entity))
            throw new InvalidDataException("A Mesh or Convex Hull collider requires saved model geometry.");
        MeshColliderComponent geometry = world.GetRef<MeshColliderComponent>(entity);
        if (geometry.Vertices is not { Length: >= 3 } || geometry.Indices is not { Length: >= 3 })
            throw new InvalidDataException("The model collider has no triangles.");
        Vector3 scale = geometry.Scale == Vector3.Zero ? Vector3.One : geometry.Scale;
        // A mirrored instance bakes its own copy below; any other shares its model's mesh.
        if (body.Shape == SharedCollisionShape.Mesh && scale.X > 0f && scale.Y > 0f && scale.Z > 0f
            && float.IsFinite(scale.X) && float.IsFinite(scale.Y) && float.IsFinite(scale.Z))
        {
            var key = new GeometryKey(geometry.Vertices, geometry.Indices);
            if (!_sharedMeshes.TryGetValue(key, out SharedMesh? shared))
            {
                shared = new SharedMesh { Mesh = BuildMesh(geometry.Vertices, geometry.Indices) };
                _sharedMeshes[key] = shared;
            }
            Mesh instance = shared.Mesh;
            instance.Scale = scale;
            Vector3 sharedCenter = Vector3.Zero;
            BodyInertia sharedInertia = body.Motion == Genesis.Shared.ECS.Components.PhysicsMotionType.Dynamic
                ? instance.ComputeOpenInertia(MathF.Max(.001f, body.Mass), out sharedCenter) : default;
            TypedIndex index = _simulation.Shapes.Add(instance);
            shared.Users++;
            _sharedMeshShapes[index.Packed] = key;
            return (index, sharedInertia, sharedCenter);
        }
        Vector3[] vertices = Array.ConvertAll(geometry.Vertices, vertex => vertex * scale);
        foreach (Vector3 vertex in vertices)
            if (!float.IsFinite(vertex.LengthSquared())) throw new InvalidDataException("Model collider positions must be finite.");
        if (body.Shape == SharedCollisionShape.ConvexHull)
        {
            ConvexHullHelper.CreateShape(vertices.AsSpan(), _pool, out Vector3 center, out ConvexHull hull);
            if (hull.Points.Length == 0) throw new InvalidDataException("Convex Hull collider needs a model with nonzero volume.");
            return (_simulation.Shapes.Add(hull), hull.ComputeInertia(MathF.Max(.001f, body.Mass)), center);
        }
        Mesh mesh = BuildMesh(vertices, geometry.Indices);
        Vector3 meshCenter = Vector3.Zero;
        BodyInertia inertia = body.Motion == Genesis.Shared.ECS.Components.PhysicsMotionType.Dynamic
            ? mesh.ComputeOpenInertia(MathF.Max(.001f, body.Mass), out meshCenter) : default;
        return (_simulation.Shapes.Add(mesh), inertia, meshCenter);
    }

    private Mesh BuildMesh(Vector3[] vertices, int[] indices)
    {
        foreach (Vector3 vertex in vertices)
            if (!float.IsFinite(vertex.LengthSquared())) throw new InvalidDataException("Model collider positions must be finite.");
        if (indices.Length % 3 != 0) throw new InvalidDataException("Model collider indices must describe complete triangles.");
        foreach (int index in indices)
            if ((uint)index >= vertices.Length) throw new InvalidDataException("Model collider has an invalid triangle index.");
        _pool.Take<Triangle>(indices.Length / 3, out var triangles);
        for (int index = 0; index < triangles.Length; index++)
            triangles[index] = new Triangle(vertices[indices[index * 3]], vertices[indices[index * 3 + 1]], vertices[indices[index * 3 + 2]]);
        return new Mesh(triangles, Vector3.One, _pool);
    }
}
