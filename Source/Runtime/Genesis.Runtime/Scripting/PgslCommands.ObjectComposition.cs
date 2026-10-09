using System;
using Genesis.Shared.Scripting;
using Newtonsoft.Json.Linq;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    [PgslCommand("SpawnParticleEmitter", "SpawnParticleEmitter(particle, x, y, z, scale) -> id", "Spawn a Particle resource through the runtime Object component system", "Particles")]
    public static int SpawnParticleEmitter(string particle, double x, double y, double z, double scale = 1)
    {
        if (ActiveGameContext?.World is not { } world || string.IsNullOrWhiteSpace(particle)) return -1;
        var prefab = new JObject { ["dimension"] = "ThreeD", ["components"] = new JArray(
            new JObject { ["type"] = "TransformComponent", ["props"] = new JObject { ["Position"] = new JArray(x, y, z), ["Scale"] = new JArray(scale, scale, scale) } },
            new JObject { ["type"] = "ParticleComponent", ["props"] = new JObject { ["Asset"] = particle, ["Emitting"] = true, ["FollowEntity"] = true, ["RateScale"] = 1 } }) };
        return Scene.PrefabSpawner.Spawn(world, prefab).Id;
    }

    [PgslCommand("ParticleBurstAt", "ParticleBurstAt(particle, x, y, z, scale, seconds) -> id",
        "Play a Particle resource once at a place; it removes itself when it has played out. Seconds is how long a continuous effect emits", "Particles")]
    public static int ParticleBurstAt(string particle, double x, double y, double z, double scale = 1, double seconds = 0)
    {
        if (ActiveGameContext?.World is not { } world) return -1;
        var entity = Genesis.Runtime.Particles.ParticleBursts.Play(world, particle,
            new System.Numerics.Vector3((float)x, (float)y, (float)z), (float)scale, (float)seconds);
        return entity.IsNull ? -1 : entity.Id;
    }

    [PgslCommand("ParticleBurstBatched", "ParticleBurstBatched(particle, x, y, z, scale) -> bool",
        "Play a Particle resource once at a place like ParticleBurstAt, but every batched burst of that resource shares one emitter: hundreds of them (falling leaves, sparks) cost one simulation and one draw a frame. "
        + "No id is returned. An effect that loops, follows the camera, simulates in its own space, links emitters or lights the scene, and the Software renderer, play it as ParticleBurstAt", "Particles")]
    public static bool ParticleBurstBatched(string particle, double x, double y, double z, double scale = 1)
    {
        if (ActiveGameContext?.World is not { } world || !double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return false;
        return Genesis.Runtime.Particles.ParticleBursts.PlayBatched(world, particle,
            new System.Numerics.Vector3((float)x, (float)y, (float)z), double.IsFinite(scale) ? (float)scale : 1f);
    }

    [PgslCommand("ParticleSetBurstLimit", "ParticleSetBurstLimit(count)",
        "The most ParticleBurstAt bursts alive at once (default 256; 0 = no limit): one more removes the oldest. Emitters placed on Objects are never removed", "Particles")]
    public static void ParticleSetBurstLimit(double count) =>
        Genesis.Runtime.Particles.ParticleBursts.Limit = double.IsFinite(count)
            ? (int)Math.Clamp(count, 0, 100_000)
            : Genesis.Runtime.Particles.ParticleBursts.DefaultLimit;

    [PgslCommand("ParticleBurstLimit", "ParticleBurstLimit() -> number", "The most ParticleBurstAt bursts alive at once (0 = no limit)", "Particles")]
    public static double ParticleBurstLimit() => Genesis.Runtime.Particles.ParticleBursts.Limit;

    [PgslCommand("ParticleBurstCount", "ParticleBurstCount() -> number", "ParticleBurstAt bursts alive now", "Particles")]
    public static double ParticleBurstCount() =>
        ActiveGameContext?.World is { } world ? Genesis.Runtime.Particles.ParticleBursts.LiveCount(world) : 0;
}
