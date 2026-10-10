using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scene;

/// <summary>A place colliders are kept around, and how far.</summary>
public readonly record struct CollisionFocus(Vector3 Position, float Radius);

/// <summary>
/// Instances a script has asked colliders to follow (<c>PhysicsAddCollisionFocus</c>).
/// </summary>
/// <remarks>
/// A large world makes some colliders only near what can touch them: streamed scenery, a large
/// terrain's tiles and solid scatter exist near the camera and near moving bodies. Something a
/// script moves without a body of its own (a bot under <c>CharacterMove</c>, a vehicle steered by
/// rays) can be named here, and those colliders are then kept within its radius as well, however
/// far it is from the camera. Each world keeps its own list; an instance that is destroyed drops
/// out of it.
/// </remarks>
public static class CollisionFoci
{
    /// <summary>The largest radius one focus may ask for (metres).</summary>
    public const float MaximumRadius = 1000f;

    /// <summary>The most foci one world keeps.</summary>
    public const int MaximumCount = 1024;

    private static readonly ConditionalWeakTable<EcsWorld, Dictionary<Entity, float>> Sets = new();

    /// <summary>Keeps colliders within <paramref name="radius"/> metres of an instance. False when it cannot be added.</summary>
    public static bool Add(EcsWorld world, Entity entity, float radius)
    {
        if (world == null || entity.IsNull || !world.IsAlive(entity) || !float.IsFinite(radius) || radius <= 0f) return false;
        Dictionary<Entity, float> set = Sets.GetOrCreateValue(world);
        if (!set.ContainsKey(entity) && set.Count >= MaximumCount)
        {
            DropGone(world, set);
            if (set.Count >= MaximumCount) return false;
        }

        set[entity] = MathF.Min(radius, MaximumRadius);
        return true;
    }

    /// <summary>Stops keeping colliders around an instance. False when it was not a focus.</summary>
    public static bool Remove(EcsWorld world, Entity entity) =>
        world != null && Sets.TryGetValue(world, out Dictionary<Entity, float> set) && set.Remove(entity);

    /// <summary>Foci a world holds now (instances destroyed since the last use may still be counted).</summary>
    public static int Count(EcsWorld world) =>
        world != null && Sets.TryGetValue(world, out Dictionary<Entity, float> set) ? set.Count : 0;

    /// <summary>
    /// Adds where each focus stands now, with its radius, up to <paramref name="limit"/> entries in
    /// <paramref name="into"/>. A focus whose instance has been destroyed is dropped.
    /// </summary>
    public static void Collect(EcsWorld world, List<CollisionFocus> into, int limit)
    {
        if (world == null || into == null || !Sets.TryGetValue(world, out Dictionary<Entity, float> set) || set.Count == 0) return;
        bool dropped = false;
        foreach (KeyValuePair<Entity, float> focus in set)
        {
            if (!world.IsAlive(focus.Key))
            {
                dropped = true;
                continue;
            }

            if (into.Count >= limit) continue;
            if (TryPosition(world, focus.Key, out Vector3 position)) into.Add(new CollisionFocus(position, focus.Value));
        }

        if (dropped) DropGone(world, set);
    }

    /// <summary>
    /// Adds the feet of every script character (<c>CharacterCreate</c>) with <paramref name="radius"/>,
    /// up to <paramref name="limit"/> entries in <paramref name="into"/>.
    /// </summary>
    public static void CollectCharacters(EcsWorld world, List<CollisionFocus> into, float radius, int limit)
    {
        if (world == null || into == null || !float.IsFinite(radius) || radius <= 0f) return;
        Genesis.Runtime.Scripting.PgslCommands.CollectCharacterFeet(world, into, radius, limit);
    }

    // A character's feet when it has one (the script may not have copied them to the instance
    // yet), otherwise the instance's own position.
    private static bool TryPosition(EcsWorld world, Entity entity, out Vector3 position)
    {
        if (Genesis.Runtime.Scripting.PgslCommands.TryCharacterFeet(world, entity, out position)) return Finite(position);
        if (world.Has<Genesis.Runtime.ECS.Components.TransformComponent>(entity))
        {
            ref Genesis.Runtime.ECS.Components.TransformComponent transform = ref world.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
            position = new Vector3(transform.X, transform.Y, transform.Z);
            return Finite(position);
        }

        if (world.Has<Transform3DComponent>(entity))
        {
            position = world.GetRef<Transform3DComponent>(entity).Position;
            return Finite(position);
        }

        position = default;
        return false;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void DropGone(EcsWorld world, Dictionary<Entity, float> set)
    {
        List<Entity> gone = null;
        foreach (Entity entity in set.Keys)
            if (!world.IsAlive(entity)) (gone ??= new List<Entity>()).Add(entity);
        if (gone == null) return;
        foreach (Entity entity in gone) set.Remove(entity);
    }
}
