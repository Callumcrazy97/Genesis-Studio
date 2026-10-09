using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using Newtonsoft.Json.Linq;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Particles;

/// <summary>
/// Plays a Particle resource once at a place and removes it when it has played out: sparks from
/// a blade, dust from a footfall. Nothing has to be kept or destroyed by the script that asked.
/// </summary>
public static class ParticleBursts
{
    /// <summary>How many bursts may be alive at once in a world unless a game says otherwise.</summary>
    public const int DefaultLimit = 256;

    /// <summary>Batched bursts waiting for the next update; more are refused (nothing is taking them).</summary>
    public const int MaxQueuedBatched = 4096;

    private static int _limit = DefaultLimit;

    /// <summary>
    /// The most bursts (<see cref="Play"/>) alive at once in a world: playing one more removes the
    /// oldest. 0 is no limit. Emitters placed on Objects are never counted or removed.
    /// </summary>
    public static int Limit
    {
        get => _limit;
        set => _limit = System.Math.Max(0, value);
    }

    /// <summary>Puts the limit back to <see cref="DefaultLimit"/> (a new game).</summary>
    public static void ResetLimit() => _limit = DefaultLimit;

    /// <summary>A burst asked for with <see cref="PlayBatched"/>, waiting for the next update.</summary>
    public readonly record struct BatchedBurst(string Asset, Vector3 Position, float Scale);

    private sealed class Ledger
    {
        // The world's bursts, oldest first (some may have played out and gone already).
        public readonly Queue<Entity> Live = new();
        public readonly List<BatchedBurst> Batched = new();
        public int CompactAt = 64;
    }

    private static readonly ConditionalWeakTable<EcsWorld, Ledger> Ledgers = new();

    /// <param name="emitSeconds">
    /// For an effect that emits continuously, how long it emits before it is left to die away
    /// (a quarter of a second when 0). An effect that is a single burst ignores it.
    /// </param>
    /// <returns>The emitter, which removes itself; <see cref="Entity.Null"/> when nothing could be made.</returns>
    public static Entity Play(EcsWorld world, string asset, Vector3 position, float scale = 1f, float emitSeconds = 0f)
    {
        if (world == null || string.IsNullOrWhiteSpace(asset)) return Entity.Null;
        float size = float.IsFinite(scale) && scale > 0f ? scale : 1f;
        var prefab = new JObject
        {
            ["dimension"] = "ThreeD",
            ["components"] = new JArray(
                new JObject
                {
                    ["type"] = "TransformComponent",
                    ["props"] = new JObject { ["Position"] = new JArray(position.X, position.Y, position.Z), ["Scale"] = new JArray(size, size, size) },
                },
                new JObject
                {
                    ["type"] = "ParticleComponent",
                    ["props"] = new JObject { ["Asset"] = asset, ["Emitting"] = true, ["FollowEntity"] = true, ["RateScale"] = 1 },
                }),
        };
        Entity entity = Scene.PrefabSpawner.Spawn(world, prefab);
        if (entity.IsNull || !world.Has<ParticleComponent>(entity)) return Entity.Null;
        ref ParticleComponent particles = ref world.GetRef<ParticleComponent>(entity);
        particles.RemoveWhenDone = true;
        particles.EmitSeconds = float.IsFinite(emitSeconds) ? System.Math.Clamp(emitSeconds, 0f, 60f) : 0f;
        Track(world, entity);
        return entity;
    }

    /// <summary>
    /// Asks for a burst that shares one emitter with every other batched burst of the same
    /// resource: the effect's composition update births its particles where asked, and hundreds of
    /// them cost one simulation and one draw a frame. An effect that cannot share (one that loops,
    /// follows the camera, simulates in its own space, links emitters or lights the scene), or a
    /// renderer without GPU particles, plays it as <see cref="Play"/> instead.
    /// </summary>
    /// <returns>False when nothing was asked for (no effect named, or the queue is full).</returns>
    public static bool PlayBatched(EcsWorld world, string asset, Vector3 position, float scale = 1f)
    {
        if (world == null || string.IsNullOrWhiteSpace(asset)
            || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return false;
        Ledger ledger = Ledgers.GetOrCreateValue(world);
        if (ledger.Batched.Count >= MaxQueuedBatched) return false;
        ledger.Batched.Add(new BatchedBurst(asset, position, float.IsFinite(scale) && scale > 0f ? scale : 1f));
        return true;
    }

    /// <summary>Moves the world's waiting batched bursts into <paramref name="into"/>.</summary>
    public static void TakeBatched(EcsWorld world, List<BatchedBurst> into)
    {
        if (world == null || into == null || !Ledgers.TryGetValue(world, out Ledger ledger) || ledger.Batched.Count == 0) return;
        into.AddRange(ledger.Batched);
        ledger.Batched.Clear();
    }

    /// <summary>Bursts made with <see cref="Play"/> still alive in a world.</summary>
    public static int LiveCount(EcsWorld world)
    {
        if (world == null || !Ledgers.TryGetValue(world, out Ledger ledger)) return 0;
        Compact(world, ledger.Live);
        return ledger.Live.Count;
    }

    private static void Track(EcsWorld world, Entity entity)
    {
        Ledger ledger = Ledgers.GetOrCreateValue(world);
        Queue<Entity> live = ledger.Live;
        live.Enqueue(entity);
        int limit = _limit;
        // Bursts that have played out leave on their own; forget them now and then (and whenever
        // the limit is reached) so the ledger stays the size of what is alive.
        if (live.Count >= ledger.CompactAt || (limit > 0 && live.Count > limit))
        {
            Compact(world, live);
            ledger.CompactAt = System.Math.Max(64, live.Count * 2);
        }
        if (limit <= 0) return;
        while (live.Count > limit)
        {
            Entity oldest = live.Dequeue();
            if (world.IsAlive(oldest)) world.DestroyEntity(oldest);
        }
    }

    private static void Compact(EcsWorld world, Queue<Entity> live)
    {
        int count = live.Count;
        for (int i = 0; i < count; i++)
        {
            Entity entity = live.Dequeue();
            if (world.IsAlive(entity) && world.Has<ParticleComponent>(entity)) live.Enqueue(entity);
        }
    }
}
