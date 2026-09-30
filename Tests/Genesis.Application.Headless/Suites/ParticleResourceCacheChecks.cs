using System.Collections;
using System.Reflection;
using Genesis.Runtime;
using Genesis.Runtime.Core;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

internal static class ParticleResourceCacheChecks
{
    public static void Run(string workspace, Action<bool, string> check)
    {
        string root = Path.Combine(workspace, "ParticleFreshness");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        string first = Path.Combine(root, "Assets", "First.particle.json");
        string second = Path.Combine(root, "Assets", "Second.particle.json");
        File.WriteAllText(first, "{\"emitRate\":2}");
        File.WriteAllText(second, "{\"emitRate\":12}");
        ResourceCatalog.Invalidate(root);
        using var scene = new RuntimeScene("Particle resource refresh");
        using var composition = new ObjectCompositionSubsystem(root);
        var entity = scene.World.CreateEntity();
        scene.World.Set(entity, new TransformComponent { ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
        scene.World.Set(entity, new ParticleComponent { Asset = "Assets/First.particle.json", Emitting = true, RateScale = 1 });
        var time = new GameTime();
        void Update() { time.Advance(.016f); composition.Update(scene, time); }
        ParticleConfig Config()
        {
            var states = (IDictionary)typeof(ObjectCompositionSubsystem).GetField("_particles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(composition)!;
            object state = states[entity.Id]!;
            var layers = (IList)state.GetType().GetField("Layers")!.GetValue(state)!;
            object layer = layers[0]!;
            return (ParticleConfig)layer.GetType().GetField("Config")!.GetValue(layer)!;
        }
        Update();
        var original = Config();
        scene.World.GetRef<ParticleComponent>(entity).RateScale = .5f;
        Update();
        check(ReferenceEquals(original, Config()) && Config().EmitRate == 1, "A cached emitter delayed a gameplay rate change.");
        File.WriteAllText(first, "{\"emitRate\":8}");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddSeconds(1));
        Thread.Sleep(330);
        Update();
        check(!ReferenceEquals(original, Config()) && Config().EmitRate == 4, "External particle edit did not reload after bounded freshness.");
        scene.World.GetRef<ParticleComponent>(entity).Asset = "Assets/Second.particle.json";
        Update();
        check(Config().EmitRate == 6, "Changing the referenced particle was delayed by the old cache interval.");
    }
}
