using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using Genesis.Shared.ECS;

namespace Genesis.Physics;

public readonly record struct PhysicsDebugLine(Vector3 Start, Vector3 End);
public sealed record PhysicsDebugCollider(int RegistrationId, Entity Entity, string Label, bool IsStatic, bool IsSensor,
    IReadOnlyList<PhysicsDebugLine> Lines, bool DetailLimited);
public readonly record struct PhysicsDebugContact(Vector3 Position, Vector3 Normal, float Depth);

public sealed partial class PhysicsWorld
{
    private readonly ConcurrentDictionary<(int A, int B), PhysicsDebugContact> _debugContacts = new();
    public bool CaptureDebugContacts { get; set; }
    /// <summary>One actual narrow-phase contact per touching pair. Read after the fixed step completes.</summary>
    public IReadOnlyList<PhysicsDebugContact> DebugContacts => CaptureDebugContacts ? _debugContacts.Values.ToArray() : Array.Empty<PhysicsDebugContact>();

    internal void RecordDebugContact(CollidablePair pair, Vector3 offset, Vector3 normal, float depth)
    {
        if (!CaptureDebugContacts || !TryGetRegistrationId(pair.A, out int a) || !TryGetRegistrationId(pair.B, out int b)) return;
        Vector3 origin = pair.A.Mobility == CollidableMobility.Static ? _simulation.Statics[pair.A.StaticHandle].Pose.Position
            : _simulation.Bodies.GetBodyReference(pair.A.BodyHandle).Pose.Position;
        _debugContacts[a < b ? (a, b) : (b, a)] = new(origin + offset, normal, depth);
    }

    private void FinishDebugContacts()
    {
        foreach (var pair in _debugContacts.Keys)
            if (!CaptureDebugContacts || !_contacts.ContainsKey(pair)) _debugContacts.TryRemove(pair, out _);
    }

    /// <summary>Wire geometry of registered Bepu shapes, including recentered meshes/hulls and scene-owned terrain.
    /// Read while simulation is stopped or after a completed fixed step. Dense shapes are sampled to a bounded line budget.</summary>
    public IReadOnlyList<PhysicsDebugCollider> CaptureColliderDebug(int maximumLines = 12000)
    {
        if (maximumLines < 12) throw new ArgumentOutOfRangeException(nameof(maximumLines));
        List<PhysicsDebugCollider> result = new();
        int remaining = maximumLines;
        foreach (var pair in _bindings.OrderBy(p => p.Key))
        {
            BodyBinding binding = pair.Value;
            if (!binding.Enabled || remaining < 12) continue;
            bool isStatic = binding.StaticHandle.HasValue;
            RigidPose pose = isStatic ? _simulation.Statics[binding.StaticHandle!.Value].Pose
                : _simulation.Bodies.GetBodyReference(binding.DynamicHandle!.Value).Pose;
            bool sensor = isStatic ? _staticSensor.GetValueOrDefault(binding.StaticHandle!.Value) : _dynamicSensor.GetValueOrDefault(binding.DynamicHandle!.Value);
            Add(pair.Key, binding.Entity, "Body " + pair.Key, binding.Shape, pose, isStatic, sensor);
        }
        foreach (var pair in _externalStatics.OrderBy(p => p.Key))
        {
            if (remaining < 12) break;
            Add(pair.Key, Entity.Null, pair.Value.Label, pair.Value.Shape, _simulation.Statics[pair.Value.Handle].Pose, true, false);
        }
        return result;

        void Add(int id, Entity entity, string label, TypedIndex shape, RigidPose pose, bool isStatic, bool sensor)
        {
            List<PhysicsDebugLine> lines = new(); bool limited = false;
            int budget = remaining;
            Vector3 World(Vector3 p) => Vector3.Transform(p, pose.Orientation) + pose.Position;
            void Line(Vector3 a, Vector3 b)
            {
                if (lines.Count >= budget) { limited = true; return; }
                lines.Add(new(World(a), World(b)));
            }
            void Circle(float radius, Vector3 center, Vector3 u, Vector3 v)
            {
                for (int i = 0; i < 32; i++)
                {
                    float a = i * MathF.Tau / 32, b = (i + 1) * MathF.Tau / 32;
                    Line(center + radius * (u * MathF.Cos(a) + v * MathF.Sin(a)), center + radius * (u * MathF.Cos(b) + v * MathF.Sin(b)));
                }
            }
            if (shape.Type == Box.Id)
            {
                Box box = _simulation.Shapes.GetShape<Box>(shape.Index);
                Vector3[] corners = new Vector3[8];
                for (int i = 0; i < 8; i++) corners[i] = new((i & 1) == 0 ? -box.HalfWidth : box.HalfWidth,
                    (i & 2) == 0 ? -box.HalfHeight : box.HalfHeight, (i & 4) == 0 ? -box.HalfLength : box.HalfLength);
                for (int i = 0; i < 8; i++) for (int bit = 1; bit <= 4; bit *= 2) if ((i & bit) == 0) Line(corners[i], corners[i | bit]);
            }
            else if (shape.Type == Sphere.Id)
            {
                float radius = _simulation.Shapes.GetShape<Sphere>(shape.Index).Radius;
                Circle(radius, Vector3.Zero, Vector3.UnitX, Vector3.UnitY); Circle(radius, Vector3.Zero, Vector3.UnitY, Vector3.UnitZ); Circle(radius, Vector3.Zero, Vector3.UnitX, Vector3.UnitZ);
            }
            else if (shape.Type == Capsule.Id || shape.Type == Cylinder.Id)
            {
                float radius, half;
                bool capsule = shape.Type == Capsule.Id;
                if (capsule) { Capsule body = _simulation.Shapes.GetShape<Capsule>(shape.Index); radius = body.Radius; half = body.HalfLength; }
                else { Cylinder body = _simulation.Shapes.GetShape<Cylinder>(shape.Index); radius = body.Radius; half = body.HalfLength; }
                Circle(radius, new(0, half, 0), Vector3.UnitX, Vector3.UnitZ); Circle(radius, new(0, -half, 0), Vector3.UnitX, Vector3.UnitZ);
                if (capsule)
                {
                    foreach (Vector3 horizontal in new[] { Vector3.UnitX, Vector3.UnitZ })
                        for (int i = 0; i < 32; i++)
                        {
                            Vector3 Point(int at)
                            {
                                float angle = at * MathF.Tau / 32, y = MathF.Cos(angle);
                                return horizontal * (MathF.Sin(angle) * radius) + Vector3.UnitY * (y * radius + (y >= 0 ? half : -half));
                            }
                            Line(Point(i), Point(i + 1));
                        }
                }
                else foreach (Vector3 side in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
                    Line(side * radius + Vector3.UnitY * half, side * radius - Vector3.UnitY * half);
            }
            else if (shape.Type == Mesh.Id)
            {
                Mesh mesh = _simulation.Shapes.GetShape<Mesh>(shape.Index);
                int stride = Math.Max(1, (int)Math.Ceiling(mesh.Triangles.Length * 3d / Math.Max(1, budget)));
                limited = stride > 1;
                for (int i = 0; i < mesh.Triangles.Length; i += stride)
                {
                    Triangle triangle = mesh.Triangles[i]; Vector3 a = triangle.A * mesh.Scale, b = triangle.B * mesh.Scale, c = triangle.C * mesh.Scale;
                    Line(a, b); Line(b, c); Line(c, a);
                }
            }
            else if (shape.Type == ConvexHull.Id)
            {
                ConvexHull hull = _simulation.Shapes.GetShape<ConvexHull>(shape.Index);
                for (int face = 0; face < hull.FaceToVertexIndicesStart.Length && lines.Count < budget; face++)
                {
                    hull.GetVertexIndicesForFace(face, out var points);
                    for (int i = 0; i < points.Length; i++)
                    {
                        hull.GetPoint(points[i], out Vector3 a); hull.GetPoint(points[(i + 1) % points.Length], out Vector3 b); Line(a, b);
                    }
                }
                limited = lines.Count >= budget;
            }
            remaining -= lines.Count;
            result.Add(new(id, entity, label, isStatic, sensor, lines, limited));
        }
    }
}
