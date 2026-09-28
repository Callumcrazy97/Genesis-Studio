using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;

namespace Genesis.Physics;

/// <summary>Solver constraints for XY motion, with rotation about Z and an optional rotation lock.</summary>
internal sealed class PlanarBodyConstraints(Simulation simulation)
{
    private readonly record struct Binding(ConstraintHandle Translation, ConstraintHandle Rotation, bool LockRotation, float Depth);
    private readonly Dictionary<BodyHandle, Binding> _bindings = new();
    private BodyHandle? _anchor;

    internal bool Contains(BodyHandle handle) => _bindings.ContainsKey(handle);

    internal void Reanchor(BodyHandle handle, float depth)
    {
        if (!_bindings.TryGetValue(handle, out Binding binding)) return;
        Remove(handle);
        Apply(handle, true, binding.LockRotation, depth);
    }

    internal void Apply(BodyHandle handle, bool enabled, bool lockRotation, float depth)
    {
        if (_bindings.TryGetValue(handle, out Binding existing))
        {
            if (enabled && existing.LockRotation == lockRotation && existing.Depth == depth) return;
            Remove(handle);
        }
        if (!enabled) return;
        _anchor ??= simulation.Bodies.Add(BodyDescription.CreateKinematic(
            new RigidPose(Vector3.Zero), default(CollidableDescription), new BodyActivityDescription(-1f)));
        BodyReference body = simulation.Bodies.GetBodyReference(handle);
        body.Awake = true;
        body.Pose.Position.Z = depth;
        body.Pose.Orientation = RotationInPlane(body.Pose.Orientation);
        body.Velocity.Linear.Z = 0;
        body.Velocity.Angular.X = body.Velocity.Angular.Y = 0;
        SpringSettings spring = new(60f, 1f);
        ConstraintHandle translation = simulation.Solver.Add(_anchor.Value, handle, new LinearAxisServo
        {
            LocalPlaneNormal = Vector3.UnitZ,
            TargetOffset = depth,
            SpringSettings = spring,
            ServoSettings = ServoSettings.Default,
        });
        ConstraintHandle rotation = lockRotation
            ? simulation.Solver.Add(handle, new OneBodyAngularServo
            {
                TargetOrientation = body.Pose.Orientation,
                SpringSettings = spring,
                ServoSettings = ServoSettings.Default,
            })
            : simulation.Solver.Add(_anchor.Value, handle, new AngularHinge
            {
                LocalHingeAxisA = Vector3.UnitZ,
                LocalHingeAxisB = Vector3.UnitZ,
                SpringSettings = spring,
            });
        _bindings.Add(handle, new Binding(translation, rotation, lockRotation, depth));
        simulation.Bodies.UpdateBounds(handle);
    }

    internal void Remove(BodyHandle handle)
    {
        if (!_bindings.Remove(handle, out Binding binding)) return;
        simulation.Solver.Remove(binding.Translation);
        simulation.Solver.Remove(binding.Rotation);
    }

    internal static Quaternion RotationInPlane(Quaternion rotation)
    {
        Quaternion planar = new(0, 0, rotation.Z, rotation.W);
        return planar.LengthSquared() > 1e-12f ? Quaternion.Normalize(planar) : Quaternion.Identity;
    }
}
