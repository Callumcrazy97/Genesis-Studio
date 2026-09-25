using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;

namespace Genesis.Physics;

internal struct SandboxNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    private readonly SandboxPhysicsWorld _world;

    public SandboxNarrowPhaseCallbacks(SandboxPhysicsWorld world) => _world = world;

    public void Initialize(Simulation simulation) { }

    public void Dispose() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) =>
        (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic) &&
        _world.ShouldCollide(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = MathF.Max(0f, _world.FrictionCoefficient);
        pairMaterial.MaximumRecoveryVelocity = MathF.Max(0.1f, 1f + _world.Restitution * 9f);
        pairMaterial.SpringSettings = new SpringSettings(30, 1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) =>
        true;
}

internal struct SandboxPoseCallbacks : IPoseIntegratorCallbacks
{
    private readonly SandboxPhysicsWorld _world;
    private Vector3 _gravity;
    private float _maxVelocity;
    private float _maxVelocitySq;

    public SandboxPoseCallbacks(SandboxPhysicsWorld world) => _world = world;

    public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public bool AllowSubstepsForUnconstrainedBodies => false;
    public bool IntegrateVelocityForKinematics => false;

    public void Initialize(Simulation simulation) { }
    public void PrepareForIntegration(float dt)
    {
        _gravity = _world.Gravity;
        _maxVelocity = MathF.Max(0f, _world.MaxVelocity);
        _maxVelocitySq = _maxVelocity * _maxVelocity;
    }

    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
        ref BodyVelocityWide velocity)
    {
        velocity.Linear.X += new Vector<float>(_gravity.X) * dt;
        velocity.Linear.Y += new Vector<float>(_gravity.Y) * dt;
        velocity.Linear.Z += new Vector<float>(_gravity.Z) * dt;
        Vector<float> linearDrag = Vector.Max(Vector<float>.Zero, new Vector<float>(1f) - new Vector<float>(_world.LinearDamping) * dt);
        Vector<float> angularDrag = Vector.Max(Vector<float>.Zero, new Vector<float>(1f) - new Vector<float>(_world.AngularDamping) * dt);
        velocity.Linear.X *= linearDrag;
        velocity.Linear.Y *= linearDrag;
        velocity.Linear.Z *= linearDrag;
        velocity.Angular.X *= angularDrag;
        velocity.Angular.Y *= angularDrag;
        velocity.Angular.Z *= angularDrag;

        if (_maxVelocity <= 0f)
            return;

        for (int lane = 0; lane < Vector<int>.Count; lane++)
        {
            if (GetIntLane(in integrationMask, lane) == 0)
                continue;

            Vector3 linear = new(
                GetFloatLane(in velocity.Linear.X, lane),
                GetFloatLane(in velocity.Linear.Y, lane),
                GetFloatLane(in velocity.Linear.Z, lane));

            if (linear.LengthSquared() > _maxVelocitySq)
            {
                linear = Vector3.Normalize(linear) * _maxVelocity;
                SetFloatLane(ref velocity.Linear.X, lane, linear.X);
                SetFloatLane(ref velocity.Linear.Y, lane, linear.Y);
                SetFloatLane(ref velocity.Linear.Z, lane, linear.Z);
            }
        }
    }

    public void Dispose() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetIntLane(in Vector<int> vector, int lane) =>
        Unsafe.Add(ref Unsafe.As<Vector<int>, int>(ref Unsafe.AsRef(in vector)), lane);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float GetFloatLane(in Vector<float> vector, int lane) =>
        Unsafe.Add(ref Unsafe.As<Vector<float>, float>(ref Unsafe.AsRef(in vector)), lane);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetFloatLane(ref Vector<float> vector, int lane, float value) =>
        Unsafe.Add(ref Unsafe.As<Vector<float>, float>(ref vector), lane) = value;
}
