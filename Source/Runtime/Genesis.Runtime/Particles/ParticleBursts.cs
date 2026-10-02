using System.Numerics;
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
        return entity;
    }
}
