using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using SharedCollisionShape = Genesis.Shared.ECS.Components.CollisionShape;
using SharedPhysicsMotionType = Genesis.Shared.ECS.Components.PhysicsMotionType;

namespace Genesis.Physics;

/// <summary>BepuPhysics simulation shared by a scene. Bodies are registered via ECS components.</summary>
public sealed partial class PhysicsWorld : IDisposable
{
    private readonly BufferPool _pool = new();
    private readonly Simulation _simulation;
    private readonly Dictionary<BodyHandle, int> _dynamicHandles = new();
    private readonly Dictionary<StaticHandle, int> _staticHandles = new();
    private readonly Dictionary<BodyHandle, float> _dynamicFriction = new();
    private readonly Dictionary<StaticHandle, float> _staticFriction = new();
    /// <summary>Issue 7 (declarative physics): per-collidable bounciness, mirrors <see cref="_dynamicFriction"/>/<see cref="_staticFriction"/>.</summary>
    private readonly Dictionary<BodyHandle, float> _dynamicRestitution = new();
    private readonly Dictionary<StaticHandle, float> _staticRestitution = new();
    /// <summary>Issue 7 (declarative physics): tracks <see cref="RigidBodyFlags.Sensor"/> per collidable so the narrow phase can skip collision response for them (non-solid overlap volumes).</summary>
    private readonly Dictionary<BodyHandle, bool> _dynamicSensor = new();
    private readonly Dictionary<StaticHandle, bool> _staticSensor = new();
    private readonly Dictionary<BodyHandle, (byte Layer, uint Mask)> _dynamicCollisionFilter = new();
    private readonly Dictionary<StaticHandle, (byte Layer, uint Mask)> _staticCollisionFilter = new();
    private readonly Dictionary<int, BodyBinding> _bindings = new();
    private readonly Dictionary<BodyHandle, BodyBinding> _dynamicBindingsByHandle = new();
    private readonly ThreadDispatcher _threadDispatcher;
    private readonly List<int> _staticRegistrationIds = new();
    private int _nextRegistrationId = 1;

    public Simulation Simulation => _simulation;

    public float GravityStrength { get; set; } = 1f;
    public Vector3 GravityDirection { get; set; } = new(0f, -1f, 0f);
    public float DefaultWeight { get; set; } = 1f;
    public float AirDrag { get; set; } = 0.15f;
    public float MaxVelocity { get; set; } = 45f;
    public bool AllowSleep { get; set; } = true;
    public float SleepThreshold { get; set; } = 0.25f;
    public bool EnableThreadDispatcher { get; set; } = true;
    public int DispatcherThreadCount => _threadDispatcher.ThreadCount;
    public int SolverIterations { get; }
    public float RecoveryVelocity { get; set; } = 4f;

    private PhysicsWorld(int solverIterations)
    {
        SolverIterations = solverIterations;
        _threadDispatcher = new ThreadDispatcher(Math.Max(1, Environment.ProcessorCount), 1 << 18);
        _simulation = Simulation.Create(
            _pool,
            new PhysicsNarrowPhaseCallbacks(this),
            new PhysicsPoseCallbacks(this),
            new SolveDescription(solverIterations, 2));
    }

    public static PhysicsWorld Create(PhysicsWorldAsset asset)
    {
        if (asset == null) throw new ArgumentNullException(nameof(asset));

        return new PhysicsWorld(asset.SolverIterations)
        {
            GravityStrength = asset.GravityStrength,
            GravityDirection = asset.GravityDirection,
            DefaultWeight = asset.DefaultWeight,
            AirDrag = asset.AirDrag,
            MaxVelocity = asset.MaxVelocity,
            AllowSleep = asset.AllowSleep,
            SleepThreshold = asset.SleepThreshold,
            RecoveryVelocity = asset.RecoveryVelocity,
        };
    }

    public Vector3 GetGravityAcceleration()
    {
        Vector3 dir = GravityDirection;
        if (dir.LengthSquared() < 1e-8f)
            dir = Vector3.UnitY * -1f;
        return Vector3.Normalize(dir) * (9.81f * GravityStrength);
    }

    public void RegisterEntity(IEcsWorld world, Entity entity, ref RigidBodyComponent body, ref Transform3DComponent transform)
    {
        if (body.RegistrationId != 0 || !body.Collision)
            return;

        if (body.Weight <= 0f)
            body.Weight = DefaultWeight;

        int registrationId = _nextRegistrationId++;
        var pose = new RigidPose(transform.Position, transform.Rotation);
        var (shape, inertia) = CreateShape(body);

        if (body.Motion == SharedPhysicsMotionType.Static)
        {
            var desc = new StaticDescription(pose, shape);
            var staticHandle = _simulation.Statics.Add(desc);
            _staticHandles[staticHandle] = registrationId;
            _staticFriction[staticHandle] = ClampFriction(body.Friction);
            _staticRestitution[staticHandle] = ClampRestitution(body.Restitution);
            _staticSensor[staticHandle] = body.IsSensor;
            _staticCollisionFilter[staticHandle] = NormalizeCollisionFilter(body.CollisionLayer, body.CollisionMask);
            _bindings[registrationId] = new BodyBinding
            {
                Entity = entity,
                StaticHandle = staticHandle,
            };
            _staticRegistrationIds.Add(registrationId);
        }
        else
        {
            var collidable = new CollidableDescription(shape, body.SpeculativeMargin);
            var activity = new BodyActivityDescription(
                AllowSleep ? MathF.Max(SleepThreshold, 1e-5f) : -1f,
                32);
            var dynamic = BodyDescription.CreateDynamic(
                pose,
                inertia,
                collidable,
                activity);
            var handle = _simulation.Bodies.Add(dynamic);
            _dynamicFriction[handle] = ClampFriction(body.Friction);
            _dynamicRestitution[handle] = ClampRestitution(body.Restitution);
            _dynamicSensor[handle] = body.IsSensor;
            _dynamicCollisionFilter[handle] = NormalizeCollisionFilter(body.CollisionLayer, body.CollisionMask);
            var binding = new BodyBinding
            {
                Entity = entity,
                DynamicHandle = handle,
                Enabled = body.Collision && IsEntityEnabled(world, entity),
                UseGravity = body.UseGravity,
                LockRotation = body.LockRotation,
                Weight = body.Weight > 0f ? body.Weight : DefaultWeight,
                // Issue 7: struct-default is 0f, but RigidBodyComponent factories now set this
                // explicitly to 1f, so 0f here only happens for hand-built components that
                // opted out of gravity entirely — treat that as "no gravity", not "normal gravity".
                GravityScale = body.GravityScale,
            };
            _bindings[registrationId] = binding;
            _dynamicHandles[handle] = registrationId;
            _dynamicBindingsByHandle[handle] = binding;
        }

        body.RegistrationId = registrationId;
    }

    private static bool IsEntityEnabled(IEcsWorld world, Entity entity)
    {
        if (!world.Has<EntityLifecycleComponent>(entity))
            return true;
        return world.GetRef<EntityLifecycleComponent>(entity).Enabled;
    }

    public void UnregisterEntity(IEcsWorld world, Entity entity, ref RigidBodyComponent body)
    {
        if (body.RegistrationId == 0)
            return;

        RemoveScriptJointsForRegistration(body.RegistrationId);

        if (!_bindings.TryGetValue(body.RegistrationId, out var binding))
        {
            body.RegistrationId = 0;
            return;
        }

        if (binding.DynamicHandle is BodyHandle dynamic)
        {
            _simulation.Bodies.Remove(dynamic);
            _dynamicHandles.Remove(dynamic);
            _dynamicBindingsByHandle.Remove(dynamic);
            _dynamicFriction.Remove(dynamic);
            _dynamicRestitution.Remove(dynamic);
            _dynamicSensor.Remove(dynamic);
            _dynamicCollisionFilter.Remove(dynamic);
        }

        if (binding.StaticHandle is StaticHandle stat)
        {
            _simulation.Statics.Remove(stat);
            _staticRegistrationIds.Remove(body.RegistrationId);
            _staticHandles.Remove(stat);
            _staticFriction.Remove(stat);
            _staticRestitution.Remove(stat);
            _staticSensor.Remove(stat);
            _staticCollisionFilter.Remove(stat);
        }

        _bindings.Remove(body.RegistrationId);
        body.RegistrationId = 0;
    }

    public void SyncTransforms(IEcsWorld world)
    {
        foreach (var pair in _dynamicBindingsByHandle)
        {
            BodyBinding binding = pair.Value;
            if (binding.DynamicHandle is not BodyHandle handle)
                continue;
            if (!binding.Enabled)
                continue;
            if (!world.Has<Transform3DComponent>(binding.Entity))
                continue;

            ref var pose = ref _simulation.Bodies.GetBodyReference(handle).Pose;
            ref var transform = ref world.GetRef<Transform3DComponent>(binding.Entity);
            // R7.12: keep the prior fixed pose for render interpolation.
            transform.PreviousPosition = transform.Position;
            transform.PreviousRotation = transform.Rotation;
            transform.PoseHistoryValid = 1;
            transform.Position = pose.Position;
            transform.Rotation = pose.Orientation;
        }
    }

    public void Step(IEcsWorld world, float fixedDelta)
    {
        if (fixedDelta <= 0f)
            return;

        RefreshDynamicBindingCache(world);
        if (EnableThreadDispatcher)
            _simulation.Timestep(fixedDelta, _threadDispatcher);
        else
            _simulation.Timestep(fixedDelta);
    }

    /// <summary>
    /// R7.12: run <see cref="Step"/> on the physics motorway worker when provided; otherwise
    /// step on the calling thread (headless / tests).
    /// </summary>
    public void StepOnMotorway(IEcsWorld world, float fixedDelta, PhysicsMotorway? motorway)
    {
        if (motorway is null)
        {
            Step(world, fixedDelta);
            return;
        }

        motorway.Run(() => Step(world, fixedDelta));
    }

    private void RefreshDynamicBindingCache(IEcsWorld world)
    {
        foreach (var pair in _dynamicBindingsByHandle)
        {
            BodyBinding binding = pair.Value;
            if (!world.Has<RigidBodyComponent>(binding.Entity))
            {
                binding.Enabled = false;
                continue;
            }

            ref var body = ref world.GetRef<RigidBodyComponent>(binding.Entity);
            binding.Enabled = body.Collision && IsEntityEnabled(world, binding.Entity);
            binding.UseGravity = body.UseGravity;
            binding.LockRotation = body.LockRotation;
            binding.Weight = body.Weight > 0f ? body.Weight : DefaultWeight;
            binding.GravityScale = body.GravityScale;

            if (body.Weight <= 0f)
                body.Weight = DefaultWeight;
        }
    }

    internal float GetCollidableFriction(CollidableReference collidable)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic)
        {
            if (_dynamicFriction.TryGetValue(collidable.BodyHandle, out float friction))
                return friction;
            return 0.9f;
        }

        if (_staticFriction.TryGetValue(collidable.StaticHandle, out float staticFriction))
            return staticFriction;

        return 0.9f;
    }

    private static float ClampFriction(float friction)
    {
        if (float.IsNaN(friction) || float.IsInfinity(friction))
            return 0.9f;
        return Math.Clamp(friction, 0.01f, 4f);
    }

    /// <summary>
    /// Issue 7 (declarative physics): bounciness, looked up per-pair in
    /// <see cref="PhysicsNarrowPhaseCallbacks.ConfigureContactManifold{TManifold}"/> the same way
    /// friction is, and used to scale up the contact's recovery velocity as a cheap approximation
    /// of a restitution coefficient (Bepu v2 has no first-class restitution model).
    /// </summary>
    internal float GetCollidableRestitution(CollidableReference collidable)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic)
        {
            if (_dynamicRestitution.TryGetValue(collidable.BodyHandle, out float restitution))
                return restitution;
            return 0f;
        }

        if (_staticRestitution.TryGetValue(collidable.StaticHandle, out float staticRestitution))
            return staticRestitution;

        return 0f;
    }

    private static float ClampRestitution(float restitution)
    {
        if (float.IsNaN(restitution) || float.IsInfinity(restitution))
            return 0f;
        return Math.Clamp(restitution, 0f, 1f);
    }

    /// <summary>
    /// Issue 7 (declarative physics): true if this collidable was registered with
    /// <see cref="RigidBodyFlags.Sensor"/> set (<c>PhysicsType = "Trigger"</c>/<c>"Sensor"</c>
    /// authoring) — used to suppress collision response while still letting the body exist for
    /// future overlap-event work.
    /// </summary>
    internal bool IsCollidableSensor(CollidableReference collidable)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic)
            return _dynamicSensor.TryGetValue(collidable.BodyHandle, out bool sensor) && sensor;

        return _staticSensor.TryGetValue(collidable.StaticHandle, out bool staticSensor) && staticSensor;
    }

    internal bool ShouldCollide(CollidableReference a, CollidableReference b)
    {
        (byte Layer, uint Mask) filterA = GetCollisionFilter(a);
        (byte Layer, uint Mask) filterB = GetCollisionFilter(b);
        return (filterA.Mask & (1u << filterB.Layer)) != 0 &&
               (filterB.Mask & (1u << filterA.Layer)) != 0;
    }

    private (byte Layer, uint Mask) GetCollisionFilter(CollidableReference collidable)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic &&
            _dynamicCollisionFilter.TryGetValue(collidable.BodyHandle, out var dynamicFilter))
            return dynamicFilter;
        if (collidable.Mobility != CollidableMobility.Dynamic &&
            _staticCollisionFilter.TryGetValue(collidable.StaticHandle, out var staticFilter))
            return staticFilter;
        return (0, 0x7Fu);
    }

    private static (byte Layer, uint Mask) NormalizeCollisionFilter(byte layer, uint mask) =>
        ((byte)Math.Clamp(layer, (byte)0, (byte)6), mask == 0 ? 0x7Fu : mask & 0x7Fu);

    /// <summary>
    /// Issue 7 (declarative physics): direct Bepu body access for systems that need to nudge
    /// velocity outside the normal pose-integration pipeline (e.g. <see cref="Systems.BuoyancySystem"/>).
    /// </summary>
    internal bool TryGetDynamicBodyReference(int registrationId, out BodyReference bodyReference)
    {
        if (_bindings.TryGetValue(registrationId, out BodyBinding? binding) && binding.DynamicHandle is BodyHandle handle)
        {
            bodyReference = _simulation.Bodies.GetBodyReference(handle);
            return true;
        }

        bodyReference = default;
        return false;
    }

    internal bool TryGetCachedDynamicBinding(BodyHandle handle, out BodyBinding binding)
    {
        if (_dynamicBindingsByHandle.TryGetValue(handle, out binding!))
            return true;
        binding = null!;
        return false;
    }

    private static (TypedIndex Shape, BodyInertia Inertia) CreateShape(RigidBodyComponent body, Simulation simulation)
    {
        return body.Shape switch
        {
            SharedCollisionShape.Box => CreateBox(body, simulation),
            SharedCollisionShape.Sphere => CreateSphere(body, simulation),
            SharedCollisionShape.Capsule or SharedCollisionShape.Cylinder => CreateCapsuleShape(body, simulation),
            _ => CreateBox(body, simulation),
        };
    }

    private static (TypedIndex, BodyInertia) CreateBox(RigidBodyComponent body, Simulation simulation)
    {
        var box = new Box(body.Size.X * 2f, body.Size.Y * 2f, body.Size.Z * 2f);
        return (simulation.Shapes.Add(box), box.ComputeInertia(body.Mass));
    }

    private static (TypedIndex, BodyInertia) CreateSphere(RigidBodyComponent body, Simulation simulation)
    {
        var sphere = new Sphere(body.Size.X);
        return (simulation.Shapes.Add(sphere), sphere.ComputeInertia(body.Mass));
    }

    private static (TypedIndex, BodyInertia) CreateCapsuleShape(RigidBodyComponent body, Simulation simulation)
    {
        var capsule = CreateCapsule(body.Size.X, body.Size.Y);
        return (simulation.Shapes.Add(capsule), capsule.ComputeInertia(body.Mass));
    }

    private (TypedIndex Shape, BodyInertia Inertia) CreateShape(RigidBodyComponent body) =>
        CreateShape(body, _simulation);

    private static Capsule CreateCapsule(float radius, float halfHeight)
    {
        float length = MathF.Max(0.01f, halfHeight * 2f - radius * 2f);
        return new Capsule(radius, length);
    }

    internal bool TryGetBinding(int registrationId, out BodyBinding binding)
    {
        if (_bindings.TryGetValue(registrationId, out binding!))
            return true;
        binding = null!;
        return false;
    }

    internal IEnumerable<(int RegistrationId, BodyBinding Binding)> EnumerateDynamicBindings()
    {
        foreach (var pair in _dynamicHandles)
        {
            if (_bindings.TryGetValue(pair.Value, out var binding))
                yield return (pair.Value, binding);
        }
    }

    internal IReadOnlyList<int> StaticRegistrationIds => _staticRegistrationIds;

    internal bool TryGetRegistrationId(CollidableReference collidable, out int registrationId)
    {
        if (collidable.Mobility == CollidableMobility.Dynamic)
            return _dynamicHandles.TryGetValue(collidable.BodyHandle, out registrationId);
        return _staticHandles.TryGetValue(collidable.StaticHandle, out registrationId);
    }

    public void Dispose()
    {
        ClearExternalStatics();
        _simulation.Dispose();
        _threadDispatcher.Dispose();
        _pool.Clear();
        _dynamicHandles.Clear();
        _dynamicBindingsByHandle.Clear();
        _staticHandles.Clear();
        _bindings.Clear();
        _staticRegistrationIds.Clear();
    }

    internal sealed class BodyBinding
    {
        public Entity Entity;
        public BodyHandle? DynamicHandle;
        public StaticHandle? StaticHandle;
        public bool Enabled;
        public bool UseGravity;
        public bool LockRotation;
        public float Weight;
        /// <summary>Issue 7 (declarative physics): per-body multiplier on world gravity.</summary>
        public float GravityScale;
    }
}
