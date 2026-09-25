using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Constraints;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    private sealed record ScriptJoint(ConstraintHandle Handle, int RegistrationA, int RegistrationB);

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
        _simulation.Solver.Remove(joint.Handle);
        return true;
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
