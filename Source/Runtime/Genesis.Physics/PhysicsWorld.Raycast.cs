using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    public bool Raycast(IEcsWorld world, Vector3 origin, Vector3 direction, float maxDistance, out PhysicsRaycastHit hit,
        Entity? ignore = null)
    {
        hit = default;
        if (maxDistance <= 0f)
            return false;
        if (direction.LengthSquared() < 1e-8f)
            return false;

        direction = Vector3.Normalize(direction);

        var handler = new SimulationRayHitHandler(this, world, ignore ?? Entity.Null);
        _simulation.RayCast(origin, direction, maxDistance, ref handler, id: 0);
        if (!handler.Found)
            return false;

        hit = handler.Hit;
        return true;
    }

    public bool RaycastDown(IEcsWorld world, Vector3 origin, float maxDistance, out PhysicsRaycastHit hit, Entity? ignore = null) =>
        Raycast(world, origin, -Vector3.UnitY, maxDistance, out hit, ignore);

    public void SetLinearVelocity(IEcsWorld world, int registrationId, Vector3 velocity)
    {
        if (!TryGetDynamicHandle(registrationId, out BodyHandle handle))
            return;

        var body = _simulation.Bodies.GetBodyReference(handle);
        body.Awake = true;
        body.Velocity.Linear = velocity;
    }

    public void ApplyLinearImpulse(IEcsWorld world, int registrationId, Vector3 impulse)
    {
        if (!TryGetDynamicHandle(registrationId, out BodyHandle handle))
            return;

        var body = _simulation.Bodies.GetBodyReference(handle);
        body.Awake = true;
        body.Velocity.Linear += impulse * body.LocalInertia.InverseMass;
    }

    public void SetBodyPose(IEcsWorld world, int registrationId, Vector3 position, Quaternion orientation)
    {
        if (!TryGetDynamicHandle(registrationId, out BodyHandle handle))
            return;

        var body = _simulation.Bodies.GetBodyReference(handle);
        body.Awake = true;
        body.Pose.Position = position;
        body.Pose.Orientation = orientation;
        body.Velocity = default;

        if (TryGetBinding(registrationId, out var binding) && world.Has<Transform3DComponent>(binding.Entity))
        {
            ref var transform = ref world.GetRef<Transform3DComponent>(binding.Entity);
            transform.Position = position;
            transform.Rotation = orientation;
        }
    }

    public void ClearAngularVelocity(BodyHandle handle)
    {
        var body = _simulation.Bodies.GetBodyReference(handle);
        body.Velocity.Angular = default;
        body.Pose.Orientation = Quaternion.Normalize(body.Pose.Orientation);
    }

    public Vector3 GetBodyPosition(int registrationId)
    {
        if (!TryGetDynamicHandle(registrationId, out BodyHandle handle))
            return Vector3.Zero;

        return _simulation.Bodies.GetBodyReference(handle).Pose.Position;
    }

    public Vector3 GetLinearVelocity(int registrationId)
    {
        if (!TryGetDynamicHandle(registrationId, out BodyHandle handle))
            return Vector3.Zero;

        return _simulation.Bodies.GetBodyReference(handle).Velocity.Linear;
    }

    public bool IsGrounded(IEcsWorld world, int registrationId, float extraDistance = 0.15f)
        => TryGetGroundContact(world, registrationId, extraDistance, out _);

    public bool TryGetGroundContact(IEcsWorld world, int registrationId, float extraDistance, out PhysicsRaycastHit hit)
    {
        hit = default;
        if (!TryGetBinding(registrationId, out var binding))
            return false;

        ref var body = ref world.GetRef<RigidBodyComponent>(binding.Entity);
        Vector3 half = body.HalfExtents;
        Vector3 origin = GetBodyPosition(registrationId) - new Vector3(0f, half.Y - 0.05f, 0f);
        return RaycastDown(world, origin, extraDistance, out hit, binding.Entity);
    }

    private bool TryGetDynamicHandle(int registrationId, out BodyHandle handle)
    {
        handle = default;
        if (!TryGetBinding(registrationId, out var binding) || binding.DynamicHandle is not BodyHandle dynamic)
            return false;

        handle = dynamic;
        return true;
    }

    private struct SimulationRayHitHandler : IRayHitHandler
    {
        private readonly PhysicsWorld _physics;
        private readonly IEcsWorld _world;
        private readonly Entity _ignore;

        public PhysicsRaycastHit Hit;
        public bool Found;

        public SimulationRayHitHandler(PhysicsWorld physics, IEcsWorld world, Entity ignore)
        {
            _physics = physics;
            _world = world;
            _ignore = ignore;
            Hit = default;
            Found = false;
        }

        public bool AllowTest(CollidableReference collidable)
        {
            if (!_physics.TryGetRegistrationId(collidable, out int registrationId))
                return false;
            if (_physics.IsExternalStatic(registrationId))
                return true;
            if (!_physics.TryGetBinding(registrationId, out var binding))
                return false;
            if (binding.Entity == _ignore)
                return false;
            if (!_world.Has<RigidBodyComponent>(binding.Entity))
                return false;

            ref var body = ref _world.GetRef<RigidBodyComponent>(binding.Entity);
            return PhysicsWorld.IsEntityEnabled(_world, binding.Entity) && body.Collision;
        }

        public bool AllowTest(CollidableReference collidable, int childIndex) => AllowTest(collidable);

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            if (!_physics.TryGetRegistrationId(collidable, out int registrationId))
                return;
            bool external = _physics.IsExternalStatic(registrationId);
            Entity entity = Entity.Null;
            if (!external)
            {
                if (!_physics.TryGetBinding(registrationId, out var binding))
                    return;
                if (binding.Entity == _ignore)
                    return;
                entity = binding.Entity;
            }

            Found = true;
            maximumT = t;
            Vector3 surfaceNormal = Vector3.Dot(normal, ray.Direction) > 0f ? -normal : normal;
            Hit = new PhysicsRaycastHit
            {
                Entity = entity,
                Point = ray.Origin + ray.Direction * t,
                Normal = surfaceNormal,
                Distance = t,
                IsStatic = collidable.Mobility != CollidableMobility.Dynamic,
            };
        }
    }
}
