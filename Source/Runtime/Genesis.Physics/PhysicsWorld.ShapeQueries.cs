using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Physics;

// Shape queries for script-driven movers: how far a sphere or an upright capsule travels before it
// touches something, and whether a capsule fits where it is. A rounded foot asking where it rests
// finds kerbs, edges and thin poles that rays between samples miss.
public sealed partial class PhysicsWorld
{
    /// <summary>The first thing a sphere moving from <paramref name="origin"/> touches; distance 0 when it starts touching.</summary>
    public bool SphereCast(IEcsWorld world, Vector3 origin, float radius, Vector3 direction, float maxDistance,
        out PhysicsRaycastHit hit, Entity? ignore = null)
    {
        hit = default;
        if (!(radius > 0f) || !float.IsFinite(radius)) return false;
        return Sweep(world, new Sphere(radius), origin, direction, maxDistance, out hit, ignore ?? Entity.Null);
    }

    /// <summary>
    /// As <see cref="SphereCast"/> for an upright capsule centred on <paramref name="centre"/>,
    /// <paramref name="height"/> tall end to end.
    /// </summary>
    public bool CapsuleCast(IEcsWorld world, Vector3 centre, float radius, float height, Vector3 direction, float maxDistance,
        out PhysicsRaycastHit hit, Entity? ignore = null)
    {
        hit = default;
        if (!(radius > 0f) || !float.IsFinite(radius) || !float.IsFinite(height)) return false;
        var capsule = new Capsule(radius, MathF.Max(0f, height - radius * 2f));
        return Sweep(world, capsule, centre, direction, maxDistance, out hit, ignore ?? Entity.Null);
    }

    /// <summary>Whether an upright capsule at <paramref name="centre"/> overlaps or touches anything.</summary>
    public bool CapsuleOverlaps(IEcsWorld world, Vector3 centre, float radius, float height, out PhysicsRaycastHit hit,
        Entity? ignore = null) =>
        CapsuleCast(world, centre, radius, height, Vector3.UnitY, 1e-4f, out hit, ignore);

    /// <summary>
    /// How much further than asked a shape is swept. Bepu misses a mesh's triangle near the end of
    /// a short sweep: a sphere 0.1 m from a Mesh collider's face was not found by any sweep shorter
    /// than 0.32 m, whatever the triangle's size, the instance's scale or the sphere's radius (boxes
    /// were always found). A character moving a few centimetres a frame met a building or terrain
    /// only once inside it, and was then held there by the overlap. Sweeping 0.5 m further and
    /// keeping only hits within the distance asked found every face (measured margin needed: 0.25 m).
    /// </summary>
    private const float SweepReach = 0.5f;

    private bool Sweep<TShape>(IEcsWorld world, TShape shape, Vector3 origin, Vector3 direction, float maxDistance,
        out PhysicsRaycastHit hit, Entity ignore) where TShape : unmanaged, IConvexShape
    {
        hit = default;
        if (!(maxDistance > 0f) || !float.IsFinite(maxDistance) || direction.LengthSquared() < 1e-8f
            || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            return false;
        direction = Vector3.Normalize(direction);
        var handler = new SweepHitHandler(this, world, ignore, origin, direction);
        var pose = new RigidPose(origin);
        var velocity = new BodyVelocity(direction);
        _simulation.Sweep(shape, pose, velocity, maxDistance + SweepReach, _pool, ref handler);
        // The handler keeps the nearest hit; one only in the extra reach is no hit at all.
        if (!handler.Found || handler.Hit.Distance > maxDistance) return false;
        hit = handler.Hit;
        return true;
    }

    // The same bodies a ray may hit: enabled, colliding, and not the one asking.
    private bool AllowsQuery(IEcsWorld world, CollidableReference collidable, Entity ignore)
    {
        if (!TryGetRegistrationId(collidable, out int registrationId)) return false;
        if (IsExternalStatic(registrationId)) return true;
        if (!TryGetBinding(registrationId, out var binding) || binding.Entity == ignore) return false;
        if (!world.Has<RigidBodyComponent>(binding.Entity)) return false;
        return IsEntityEnabled(world, binding.Entity) && world.GetRef<RigidBodyComponent>(binding.Entity).Collision;
    }

    private bool TryQueryEntity(CollidableReference collidable, Entity ignore, out Entity entity)
    {
        entity = Entity.Null;
        if (!TryGetRegistrationId(collidable, out int registrationId)) return false;
        if (IsExternalStatic(registrationId)) return true;
        if (!TryGetBinding(registrationId, out var binding) || binding.Entity == ignore) return false;
        entity = binding.Entity;
        return true;
    }

    private struct SweepHitHandler : ISweepHitHandler
    {
        private readonly PhysicsWorld _physics;
        private readonly IEcsWorld _world;
        private readonly Entity _ignore;
        private readonly Vector3 _origin, _direction;

        public PhysicsRaycastHit Hit;
        public bool Found;

        public SweepHitHandler(PhysicsWorld physics, IEcsWorld world, Entity ignore, Vector3 origin, Vector3 direction)
        {
            _physics = physics;
            _world = world;
            _ignore = ignore;
            _origin = origin;
            _direction = direction;
            Hit = default;
            Found = false;
        }

        public bool AllowTest(CollidableReference collidable) => _physics.AllowsQuery(_world, collidable, _ignore);

        public bool AllowTest(CollidableReference collidable, int child) => AllowTest(collidable);

        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal, CollidableReference collidable)
        {
            if (Found && t >= Hit.Distance) return;
            if (!_physics.TryQueryEntity(collidable, _ignore, out Entity entity)) return;
            Found = true;
            maximumT = t;
            Vector3 normal = hitNormal.LengthSquared() > 1e-12f ? Vector3.Normalize(hitNormal) : -_direction;
            if (Vector3.Dot(normal, _direction) > 0f) normal = -normal;
            Hit = new PhysicsRaycastHit
            {
                Entity = entity,
                Point = hitLocation,
                Normal = normal,
                Distance = t,
                IsStatic = collidable.Mobility == CollidableMobility.Static,
            };
        }

        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        {
            if (!_physics.TryQueryEntity(collidable, _ignore, out Entity entity)) return;
            Found = true;
            maximumT = 0f;
            Hit = new PhysicsRaycastHit
            {
                Entity = entity,
                Point = _origin,
                Normal = -_direction,
                Distance = 0f,
                IsStatic = collidable.Mobility == CollidableMobility.Static,
            };
        }
    }
}
