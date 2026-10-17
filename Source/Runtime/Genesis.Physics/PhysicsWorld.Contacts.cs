using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using Genesis.Shared.ECS;

namespace Genesis.Physics;

public enum PhysicsContactPhase { Enter, Stay, Exit }
public readonly record struct PhysicsContactChange(Entity A, Entity B, PhysicsContactPhase Phase);

public sealed partial class PhysicsWorld
{
    private readonly ConcurrentDictionary<(int A, int B), byte> _reportedContacts = new();
    private readonly Dictionary<(int A, int B), (Entity A, Entity B)> _contacts = new();
    private readonly List<PhysicsContactChange> _contactChanges = new();

    /// <summary>Actual narrow-phase contacts from the completed fixed step. Read only after
    /// Step/StepOnMotorway returns; callbacks on simulation workers never execute gameplay code.</summary>
    public IReadOnlyList<PhysicsContactChange> ContactChanges => _contactChanges;

    internal void RecordContact(CollidablePair pair)
    {
        if (!TryGetRegistrationId(pair.A, out int a) || !TryGetRegistrationId(pair.B, out int b)) return;
        _reportedContacts.TryAdd(a < b ? (a, b) : (b, a), 0);
    }

    private readonly List<(int A, int B)> _sortedContacts = new();
    private readonly List<KeyValuePair<int, BodyBinding>> _missingBindings = new();

    private void FinishContacts(IEcsWorld world)
    {
        _contactChanges.Clear();
        // Bepu stops running narrow phase for sleeping pairs. Keep their existing contact until
        // a participant wakes, is disabled/removed, or a static surface changes.
        foreach (var pair in _contacts)
            if (!_reportedContacts.ContainsKey(pair.Key) && ContactFiltersMatch(pair.Key.A, pair.Key.B, world)
                && SleepingOrStatic(pair.Key.A, world) && SleepingOrStatic(pair.Key.B, world))
                _reportedContacts.TryAdd(pair.Key, 0);
        foreach (var pair in _contacts)
            if (!_reportedContacts.ContainsKey(pair.Key))
                _contactChanges.Add(new(pair.Value.A, pair.Value.B, PhysicsContactPhase.Exit));
        // A reused sorted list: OrderBy/ThenBy and ConcurrentDictionary.Keys allocated on every step.
        _sortedContacts.Clear();
        foreach (var reported in _reportedContacts) _sortedContacts.Add(reported.Key);
        _sortedContacts.Sort(static (x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B.CompareTo(y.B));
        foreach (var pair in _sortedContacts)
        {
            Entity a = _bindings.TryGetValue(pair.A, out BodyBinding? bindingA) ? bindingA.Entity : Entity.Null;
            Entity b = _bindings.TryGetValue(pair.B, out BodyBinding? bindingB) ? bindingB.Entity : Entity.Null;
            if (a == Entity.Null && b == Entity.Null) continue;
            _contactChanges.Add(new(a, b, _contacts.ContainsKey(pair) ? PhysicsContactPhase.Stay : PhysicsContactPhase.Enter));
        }
        _contacts.Clear();
        foreach (var pair in _sortedContacts)
        {
            Entity a = _bindings.TryGetValue(pair.A, out BodyBinding? bindingA) ? bindingA.Entity : Entity.Null;
            Entity b = _bindings.TryGetValue(pair.B, out BodyBinding? bindingB) ? bindingB.Entity : Entity.Null;
            if (a != Entity.Null || b != Entity.Null) _contacts[pair] = (a, b);
        }
        FinishDebugContacts();
    }

    private bool SleepingOrStatic(int registration, IEcsWorld world)
    {
        if (!_bindings.TryGetValue(registration, out BodyBinding? binding)) return IsExternalStatic(registration);
        if (!world.IsAlive(binding.Entity) || !world.Has<Genesis.Shared.ECS.Components.RigidBodyComponent>(binding.Entity)
            || !world.GetRef<Genesis.Shared.ECS.Components.RigidBodyComponent>(binding.Entity).Collision
            || !IsEntityEnabled(world, binding.Entity)) return false;
        return binding.StaticHandle.HasValue || binding.DynamicHandle is { } handle && !_simulation.Bodies.GetBodyReference(handle).Awake;
    }

    private bool CollidableEnabled(CollidableReference collidable) =>
        !TryGetRegistrationId(collidable, out int id) || !_bindings.TryGetValue(id, out BodyBinding? binding) || binding.Enabled;

    private bool ContactFiltersMatch(int a, int b, IEcsWorld world)
    {
        (byte Layer, uint Mask) Filter(int id)
        {
            if (!_bindings.TryGetValue(id, out BodyBinding? binding) || !world.IsAlive(binding.Entity)
                || !world.Has<Genesis.Shared.ECS.Components.RigidBodyComponent>(binding.Entity)) return (0, 0x7Fu);
            ref var body = ref world.GetRef<Genesis.Shared.ECS.Components.RigidBodyComponent>(binding.Entity);
            return NormalizeCollisionFilter(body.CollisionLayer, body.CollisionMask);
        }
        var first = Filter(a); var second = Filter(b);
        return (first.Mask & (1u << second.Layer)) != 0 && (second.Mask & (1u << first.Layer)) != 0;
    }

    private void RemoveMissingBodyBindings(IEcsWorld world)
    {
        // Collected into a reused list only when a binding is gone, not copied on every step.
        _missingBindings.Clear();
        foreach (var binding in _bindings)
            if (!world.IsAlive(binding.Value.Entity) || !world.Has<Genesis.Shared.ECS.Components.RigidBodyComponent>(binding.Value.Entity))
                _missingBindings.Add(binding);
        foreach (var pair in _missingBindings)
        {
            var removed = new Genesis.Shared.ECS.Components.RigidBodyComponent { RegistrationId = pair.Key };
            UnregisterEntity(world, pair.Value.Entity, ref removed);
        }
    }

    private void RefreshContactSettings(IEcsWorld world)
    {
        foreach (var binding in _bindings.Values)
        {
            ref var body = ref world.GetRef<Genesis.Shared.ECS.Components.RigidBodyComponent>(binding.Entity);
            binding.Enabled = body.Collision && IsEntityEnabled(world, binding.Entity);
            if (binding.DynamicHandle is { } dynamic)
            {
                _dynamicSensor[dynamic] = body.IsSensor;
                _dynamicFriction[dynamic] = ClampFriction(body.Friction);
                _dynamicRestitution[dynamic] = ClampRestitution(body.Restitution);
                _dynamicCollisionFilter[dynamic] = NormalizeCollisionFilter(body.CollisionLayer, body.CollisionMask);
            }
            else if (binding.StaticHandle is { } stat)
            {
                _staticSensor[stat] = body.IsSensor;
                _staticFriction[stat] = ClampFriction(body.Friction);
                _staticRestitution[stat] = ClampRestitution(body.Restitution);
                _staticCollisionFilter[stat] = NormalizeCollisionFilter(body.CollisionLayer, body.CollisionMask);
            }
        }
    }
}
