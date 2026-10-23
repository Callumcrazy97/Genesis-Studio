using System;
using System.Numerics;
using BepuPhysics;

namespace Genesis.Physics;

// What a script changes on a body while the game runs (Engine.Physics.Mass and the rest). Friction,
// bounciness, damping, gravity and the flags are read from the body's component every step; these
// are the parts that live in the solver.
public sealed partial class PhysicsWorld
{
    /// <summary>
    /// A moving body's mass, changed at once: its resistance to turning scales with it. A fixed or
    /// kinematic body has none to change.
    /// </summary>
    public void SetMass(int registrationId, float mass)
    {
        if (!float.IsFinite(mass) || mass <= 0f || !TryGetDynamicHandle(registrationId, out BodyHandle handle)) return;
        BodyReference body = _simulation.Bodies.GetBodyReference(handle);
        ref BodyInertia inertia = ref body.LocalInertia;
        if (inertia.InverseMass <= 0f) return;
        // The same shape at another mass: every term of the inverse tensor is the old mass over the new.
        float ratio = 1f / (inertia.InverseMass * mass);
        inertia.InverseInertiaTensor.XX *= ratio;
        inertia.InverseInertiaTensor.YX *= ratio;
        inertia.InverseInertiaTensor.YY *= ratio;
        inertia.InverseInertiaTensor.ZX *= ratio;
        inertia.InverseInertiaTensor.ZY *= ratio;
        inertia.InverseInertiaTensor.ZZ *= ratio;
        inertia.InverseMass = 1f / mass;
        body.Awake = true;
    }

    /// <summary>How fast a moving body turns, radians a second about each axis (zero for others).</summary>
    public Vector3 GetAngularVelocity(int registrationId) =>
        TryGetDynamicHandle(registrationId, out BodyHandle handle) ? _simulation.Bodies.GetBodyReference(handle).Velocity.Angular : Vector3.Zero;

    /// <summary>Sets how fast a moving body turns; a 2D body turns only about Z.</summary>
    public void SetAngularVelocity(int registrationId, Vector3 velocity)
    {
        if (!TryGetDynamicHandle(registrationId, out BodyHandle handle)) return;
        BodyReference body = _simulation.Bodies.GetBodyReference(handle);
        body.Awake = true;
        if (_planarConstraints.Contains(handle)) velocity.X = velocity.Y = 0;
        body.Velocity.Angular = velocity;
    }

    /// <summary>Whether a moving body is being simulated (false once it has come to rest and sleeps).</summary>
    public bool IsAwake(int registrationId) =>
        TryGetDynamicHandle(registrationId, out BodyHandle handle) && _simulation.Bodies.GetBodyReference(handle).Awake;

    /// <summary>Wakes a moving body, or puts it to sleep until something touches it.</summary>
    public void SetAwake(int registrationId, bool awake)
    {
        if (!TryGetDynamicHandle(registrationId, out BodyHandle handle)) return;
        BodyReference body = _simulation.Bodies.GetBodyReference(handle);
        body.Awake = awake;
    }
}
