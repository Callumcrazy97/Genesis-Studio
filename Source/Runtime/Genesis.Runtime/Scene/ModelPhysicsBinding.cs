using System;
using System.IO;
using System.Numerics;
using Genesis.Physics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Newtonsoft.Json.Linq;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scene;

/// <summary>A saved 3D Physics resource supplies body settings; the linked Model supplies geometry.
/// Room gravity remains owned by the Room. Neither resource is changed when an instance is placed.</summary>
public static class ModelPhysicsBinding
{
    public static bool Attach(EcsWorld world, Entity entity, string project, JObject document, TransformComponent transform)
    {
        string name = (string)document["physics"];
        string path = ResourceNames.Resolve(project, name, ResourceType.Physics);
        if (string.IsNullOrWhiteSpace(path)) return false; // Preserve legacy gameplay presets.
        PhysicsSceneConfig asset = PhysicsSceneConfig.LoadFromFile(path);
        if (asset.Dimension != PhysicsDimension.ThreeD)
            throw new InvalidDataException("A 3D Object requires a 3D Physics resource. Choose 3D bodies in Physics Quick setup.");
        ModelRendererComponent renderer = world.Has<ModelRendererComponent>(entity) ? world.GetRef<ModelRendererComponent>(entity) : default;
        GModelAsset model = string.IsNullOrWhiteSpace(renderer.ModelAsset) ? null : RuntimeModelAssetRegistry.Shared.Load(project, renderer.ModelAsset);
        if (model != null && !model.HasRenderableMeshes) throw new InvalidDataException("The Physics Object's Model has no saved geometry.");
        model?.RecalculateBounds();
        Vector3 scale = new Vector3(SafeScale(transform.ScaleX), SafeScale(transform.ScaleY), SafeScale(transform.ScaleZ))
            * new Vector3(SafeScale(renderer.ScaleX), SafeScale(renderer.ScaleY), SafeScale(renderer.ScaleZ));
        Vector3 half = Vector3.Max((model?.Bounds.Size ?? Vector3.One) * Vector3.Abs(scale) * .5f, new Vector3(.01f));
        Vector3 size = asset.Shape switch
        {
            PhysicsBodyShape.Sphere => new Vector3(MathF.Max(half.X, MathF.Max(half.Y, half.Z))),
            PhysicsBodyShape.Capsule or PhysicsBodyShape.Cylinder => new Vector3(MathF.Max(half.X, half.Z), half.Y, 0),
            _ => half,
        };
        PhysicsDeclarativeBinding.TryBuildRigidBody(name, null, null, project, size, out RigidBodyComponent body, out _);
        float volume;
        if (body.Shape == Genesis.Shared.ECS.Components.CollisionShape.Mesh)
        {
            if (model == null) throw new InvalidDataException("A Mesh Physics body requires a saved Model on the Object.");
            ModelColliderBinding.AttachGeometry(world, entity, model, scale);
            volume = ModelColliderBinding.ClosedVolume(world.GetRef<MeshColliderComponent>(entity));
            // Open surfaces have no enclosed volume; their fitted bounds provide a stable mass.
            if (volume < .000001f) volume = half.X * half.Y * half.Z * 8;
        }
        else
        {
            body.LocalOffset = model == null ? Vector3.Zero : (model.Bounds.Center - (model.Pivot?.Position ?? Vector3.Zero)) * scale;
            volume = asset.Shape switch
            {
                PhysicsBodyShape.Sphere => 4f / 3 * MathF.PI * size.X * size.X * size.X,
                PhysicsBodyShape.Cylinder => MathF.PI * size.X * size.X * size.Y * 2,
                PhysicsBodyShape.Capsule => MathF.PI * size.X * size.X * MathF.Max(.01f, size.Y * 2 - size.X * 2)
                    + 4f / 3 * MathF.PI * size.X * size.X * size.X,
                _ => size.X * size.Y * size.Z * 8,
            };
        }
        body.Mass = MathF.Max(.001f, (float)asset.Density * volume);
        world.Set(entity, body);
        return true;
    }

    private static float SafeScale(float value) => float.IsFinite(value) && MathF.Abs(value) > .000001f ? value : 1;
}
