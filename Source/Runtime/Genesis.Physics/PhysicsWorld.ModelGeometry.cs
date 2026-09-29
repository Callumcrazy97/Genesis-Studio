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
        Vector3[] vertices = Array.ConvertAll(geometry.Vertices, vertex => vertex * scale);
        foreach (Vector3 vertex in vertices)
            if (!float.IsFinite(vertex.LengthSquared())) throw new InvalidDataException("Model collider positions must be finite.");
        if (body.Shape == SharedCollisionShape.ConvexHull)
        {
            ConvexHullHelper.CreateShape(vertices.AsSpan(), _pool, out Vector3 center, out ConvexHull hull);
            if (hull.Points.Length == 0) throw new InvalidDataException("Convex Hull collider needs a model with nonzero volume.");
            return (_simulation.Shapes.Add(hull), hull.ComputeInertia(MathF.Max(.001f, body.Mass)), center);
        }
        if (geometry.Indices.Length % 3 != 0) throw new InvalidDataException("Model collider indices must describe complete triangles.");
        foreach (int index in geometry.Indices)
            if ((uint)index >= vertices.Length) throw new InvalidDataException("Model collider has an invalid triangle index.");
        _pool.Take<Triangle>(geometry.Indices.Length / 3, out var triangles);
        for (int index = 0; index < triangles.Length; index++)
            triangles[index] = new Triangle(vertices[geometry.Indices[index * 3]], vertices[geometry.Indices[index * 3 + 1]], vertices[geometry.Indices[index * 3 + 2]]);
        Mesh mesh = new(triangles, Vector3.One, _pool);
        Vector3 meshCenter = Vector3.Zero;
        BodyInertia inertia = body.Motion == Genesis.Shared.ECS.Components.PhysicsMotionType.Dynamic
            ? mesh.ComputeOpenInertia(MathF.Max(.001f, body.Mass), out meshCenter) : default;
        return (_simulation.Shapes.Add(mesh), inertia, meshCenter);
    }
}
