using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Constraints;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    private sealed record ScriptJoint(ConstraintHandle Handle, int RegistrationA, int RegistrationB)
    {
        /// <summary>The limits and hinges added to the joint, removed with it.</summary>
        public List<ConstraintHandle> Limits { get; } = new();
    }

    private readonly Dictionary<int, ScriptJoint> _scriptJoints = new();
    private int _nextScriptJointId = 1;

    /// <summary>Creates a freely rotating point joint at a world-space anchor.</summary>
    public int CreateBallJoint(
        int registrationA,
        int registrationB,
        Vector3 worldAnchor,
        float frequency = 30f,
        float dampingRatio = 1f)
    {
        if (!TryGetDynamicHandle(registrationA, out BodyHandle handleA)
            || !TryGetDynamicHandle(registrationB, out BodyHandle handleB)
            || handleA == handleB) return 0;

        var bodyA = _simulation.Bodies.GetBodyReference(handleA);
        var bodyB = _simulation.Bodies.GetBodyReference(handleB);
        Vector3 localA = Vector3.Transform(
            worldAnchor - bodyA.Pose.Position,
            Quaternion.Conjugate(bodyA.Pose.Orientation));
        Vector3 localB = Vector3.Transform(
            worldAnchor - bodyB.Pose.Position,
            Quaternion.Conjugate(bodyB.Pose.Orientation));
        BallSocket description = new()
        {
            LocalOffsetA = localA,
            LocalOffsetB = localB,
            SpringSettings = new SpringSettings(
                Math.Clamp(frequency, 0.01f, 120f),
                Math.Clamp(dampingRatio, 0f, 4f)),
        };
        ConstraintHandle constraint = _simulation.Solver.Add(handleA, handleB, description);
        int id = _nextScriptJointId++;
        _scriptJoints[id] = new ScriptJoint(constraint, registrationA, registrationB);
        return id;
    }

    public bool DestroyScriptJoint(int jointId)
    {
        if (!_scriptJoints.Remove(jointId, out ScriptJoint? joint)) return false;
        foreach (ConstraintHandle limit in joint.Limits) _simulation.Solver.Remove(limit);
        _simulation.Solver.Remove(joint.Handle);
        return true;
    }

    /// <summary>
    /// Keeps the second body's <paramref name="worldAxis"/> within <paramref name="maxDegrees"/> of
    /// the first's, the axis taken in each body as they are now: a shoulder or a hip, the axis
    /// along the limb.
    /// </summary>
    public bool LimitScriptJointSwing(int jointId, Vector3 worldAxis, float maxDegrees)
    {
        if (!TryJointBodies(jointId, out ScriptJoint? joint, out BodyReference a, out BodyReference b) || !TryAxis(worldAxis, out Vector3 axis)
            || !float.IsFinite(maxDegrees)) return false;
        SwingLimit limit = new()
        {
            AxisLocalA = Local(a, axis),
            AxisLocalB = Local(b, axis),
            SpringSettings = new SpringSettings(30f, 1f),
        };
        limit.MaximumSwingAngle = Math.Clamp(maxDegrees, 0f, 180f) * MathF.PI / 180f;
        joint!.Limits.Add(_simulation.Solver.Add(a.Handle, b.Handle, limit));
        return true;
    }

    /// <summary>
    /// Lets the second body turn about <paramref name="worldAxis"/>, relative to the first, only
    /// between <paramref name="minDegrees"/> and <paramref name="maxDegrees"/> from how they are
    /// now: a forearm's twist.
    /// </summary>
    public bool LimitScriptJointTwist(int jointId, Vector3 worldAxis, float minDegrees, float maxDegrees)
    {
        if (!TryJointBodies(jointId, out ScriptJoint? joint, out BodyReference a, out BodyReference b) || !TryAxis(worldAxis, out Vector3 axis)
            || !TryRange(minDegrees, maxDegrees, out float min, out float max)) return false;
        joint!.Limits.Add(_simulation.Solver.Add(a.Handle, b.Handle, Twist(a, b, axis, min, max)));
        return true;
    }

    /// <summary>
    /// Lets the joint turn only about <paramref name="worldAxis"/>, between
    /// <paramref name="minDegrees"/> and <paramref name="maxDegrees"/> from how the bodies are now:
    /// a knee, an elbow, a door.
    /// </summary>
    public bool HingeScriptJoint(int jointId, Vector3 worldAxis, float minDegrees, float maxDegrees)
    {
        if (!TryJointBodies(jointId, out ScriptJoint? joint, out BodyReference a, out BodyReference b) || !TryAxis(worldAxis, out Vector3 axis)
            || !TryRange(minDegrees, maxDegrees, out float min, out float max)) return false;
        AngularHinge hinge = new()
        {
            LocalHingeAxisA = Local(a, axis),
            LocalHingeAxisB = Local(b, axis),
            SpringSettings = new SpringSettings(30f, 1f),
        };
        joint!.Limits.Add(_simulation.Solver.Add(a.Handle, b.Handle, hinge));
        joint.Limits.Add(_simulation.Solver.Add(a.Handle, b.Handle, Twist(a, b, axis, min, max)));
        return true;
    }

    private bool TryJointBodies(int jointId, out ScriptJoint? joint, out BodyReference a, out BodyReference b)
    {
        a = default;
        b = default;
        if (!_scriptJoints.TryGetValue(jointId, out joint)
            || !TryGetDynamicHandle(joint.RegistrationA, out BodyHandle handleA)
            || !TryGetDynamicHandle(joint.RegistrationB, out BodyHandle handleB)) return false;
        a = _simulation.Bodies.GetBodyReference(handleA);
        b = _simulation.Bodies.GetBodyReference(handleB);
        return true;
    }

    private static bool TryAxis(Vector3 worldAxis, out Vector3 axis)
    {
        axis = default;
        if (!float.IsFinite(worldAxis.X) || !float.IsFinite(worldAxis.Y) || !float.IsFinite(worldAxis.Z)
            || worldAxis.LengthSquared() < 1e-10f) return false;
        axis = Vector3.Normalize(worldAxis);
        return true;
    }

    private static bool TryRange(float minDegrees, float maxDegrees, out float min, out float max)
    {
        min = max = 0f;
        if (!float.IsFinite(minDegrees) || !float.IsFinite(maxDegrees)) return false;
        min = Math.Clamp(Math.Min(minDegrees, maxDegrees), -180f, 180f) * MathF.PI / 180f;
        max = Math.Clamp(Math.Max(minDegrees, maxDegrees), -180f, 180f) * MathF.PI / 180f;
        return true;
    }

    // A world direction in a body's own space, as the body is now.
    private static Vector3 Local(BodyReference body, Vector3 worldAxis) =>
        Vector3.Transform(worldAxis, Quaternion.Conjugate(body.Pose.Orientation));

    // A twist limit about a world axis, measured from the bodies' turn now: each body's basis is
    // the same world basis (its Z the axis) in its own space.
    private static TwistLimit Twist(BodyReference a, BodyReference b, Vector3 axis, float min, float max)
    {
        Quaternion basis = RotationFromZ(axis);
        return new TwistLimit
        {
            LocalBasisA = Quaternion.Normalize(Quaternion.Conjugate(a.Pose.Orientation) * basis),
            LocalBasisB = Quaternion.Normalize(Quaternion.Conjugate(b.Pose.Orientation) * basis),
            MinimumAngle = min,
            MaximumAngle = max,
            SpringSettings = new SpringSettings(30f, 1f),
        };
    }

    // The shortest turn taking +Z to a unit direction.
    private static Quaternion RotationFromZ(Vector3 axis)
    {
        float dot = Vector3.Dot(Vector3.UnitZ, axis);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitZ, axis), 1f + dot));
    }

    private void RemoveScriptJointsForRegistration(int registrationId)
    {
        foreach (int id in _scriptJoints
                     .Where(pair => pair.Value.RegistrationA == registrationId
                                    || pair.Value.RegistrationB == registrationId)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            DestroyScriptJoint(id);
        }
    }
}
