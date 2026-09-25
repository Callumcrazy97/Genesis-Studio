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
}
