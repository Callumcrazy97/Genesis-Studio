using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;

namespace Genesis.Physics;

internal struct PhysicsNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    private readonly PhysicsWorld _world;

    public PhysicsNarrowPhaseCallbacks(PhysicsWorld world) => _world = world;

    public void Initialize(Simulation simulation) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) =>
        _world.ShouldCollide(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        if (_world.IsCollidableSensor(pair.A) || _world.IsCollidableSensor(pair.B))
        {
            pairMaterial = default;
            return false;
        }
        float fa = _world.GetCollidableFriction(pair.A);
        float fb = _world.GetCollidableFriction(pair.B);
        pairMaterial.FrictionCoefficient = MathF.Sqrt(fa * fb);

        // Issue 7 (declarative physics): approximate a restitution coefficient by raising the
        // contact's allowed recovery velocity — Bepu v2 has no first-class "bounciness" knob, so
        // a 0 restitution pair behaves exactly as before (RecoveryVelocity, unchanged) and higher
        // restitution pairs get pushed apart faster on resolution, which reads as a bounce.
        float ra = _world.GetCollidableRestitution(pair.A);
        float rb = _world.GetCollidableRestitution(pair.B);
        float restitution = MathF.Max(ra, rb);
        pairMaterial.MaximumRecoveryVelocity = _world.RecoveryVelocity * (1f + restitution * 4f);
        pairMaterial.SpringSettings = new SpringSettings(30, 1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) =>
        true;

    public void Dispose() { }
}

internal struct PhysicsPoseCallbacks : IPoseIntegratorCallbacks
{
    private readonly PhysicsWorld _world;
    private Simulation _simulation = null!;
    private Vector3 _gravity;
    private float _airDrag;
    private float _maxVelocity;
    private float _maxVelocitySq;

    public PhysicsPoseCallbacks(PhysicsWorld world) => _world = world;

    public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public bool AllowSubstepsForUnconstrainedBodies => false;
    public bool IntegrateVelocityForKinematics => false;

    public void Initialize(Simulation simulation) => _simulation = simulation;

    public void PrepareForIntegration(float dt)
    {
        _gravity = _world.GetGravityAcceleration();
        _airDrag = MathF.Max(0f, _world.AirDrag);
        _maxVelocity = MathF.Max(0f, _world.MaxVelocity);
        _maxVelocitySq = _maxVelocity * _maxVelocity;
    }

    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
        ref BodyVelocityWide velocity)
    {
        ref var activeSet = ref _simulation.Bodies.ActiveSet;
        for (int lane = 0; lane < Vector<int>.Count; lane++)
        {
            if (GetIntLane(in integrationMask, lane) == 0)
                continue;

            int bodyIndex = GetIntLane(in bodyIndices, lane);
            BodyHandle handle = activeSet.IndexToHandle[bodyIndex];
            if (!_world.TryGetCachedDynamicBinding(handle, out var binding) || !binding.Enabled)
                continue;

            float laneDt = GetFloatLane(in dt, lane);
            Vector3 linear = new(
                GetFloatLane(in velocity.Linear.X, lane),
                GetFloatLane(in velocity.Linear.Y, lane),
                GetFloatLane(in velocity.Linear.Z, lane));

            if (binding.UseGravity)
                // Gravity is acceleration and therefore independent of mass/weight. Weight only
                // affects authored drag/inertia behavior; multiplying gravity by it made a 75 kg
                // character accelerate downward at 735 m/s² and defeated jumping/buoyancy.
                linear += _gravity * binding.GravityScale * laneDt;

            if (_airDrag > 0f)
            {
                float speed = linear.Length();
                if (speed > 1e-4f)
                {
                    float drag = _airDrag * laneDt / MathF.Max(MathF.Sqrt(MathF.Max(binding.Weight, 0f)), 0.25f);
                    linear -= Vector3.Normalize(linear) * MathF.Min(speed, drag * speed);
                }
            }

            if (_maxVelocity > 0f && linear.LengthSquared() > _maxVelocitySq)
                linear = Vector3.Normalize(linear) * _maxVelocity;

            SetFloatLane(ref velocity.Linear.X, lane, linear.X);
            SetFloatLane(ref velocity.Linear.Y, lane, linear.Y);
            SetFloatLane(ref velocity.Linear.Z, lane, linear.Z);

            if (binding.LockRotation)
            {
                SetFloatLane(ref velocity.Angular.X, lane, 0f);
                SetFloatLane(ref velocity.Angular.Y, lane, 0f);
                SetFloatLane(ref velocity.Angular.Z, lane, 0f);
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
