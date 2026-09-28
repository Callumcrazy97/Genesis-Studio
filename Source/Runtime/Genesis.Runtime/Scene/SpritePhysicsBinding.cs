using System;
using System.Drawing;
using System.Linq;
using System.Numerics;
using Genesis.Physics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Spatial;
using Genesis.Shared.Assets;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Newtonsoft.Json.Linq;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scene;

/// <summary>Maps an authored sprite pivot in pixels to a planar solver body in metres.</summary>
public struct SpritePhysicsBindingComponent : IComponent
{
    public Vector2 CentreOffset;
}

public static class SpritePhysicsBinding
{
    public const float PixelsPerMetre = 32f;

    public static bool UsesSavedTwoDAsset(string project, JObject document)
    {
        if (document is null || (bool?)document["solid"] == false || document["components"] is JArray components
            && components.OfType<JObject>().Any(component => (string)component["type"] == "PhysicsComponent" && (bool?)component["enabled"] == false)) return false;
        string path = ResourceNames.Resolve(project, (string)document?["physics"], ResourceType.Physics);
        return !string.IsNullOrWhiteSpace(path) && PhysicsSceneConfig.LoadFromFile(path).Dimension == PhysicsDimension.TwoD;
    }

    public static void EnsureScene(RuntimeScene scene, RoomAsset room, string project)
    {
        if (scene.IsSpritePhysicsScene) return;
        scene.IsSpritePhysicsScene = true;
        scene.Physics ??= PhysicsWorld.Create(new PhysicsWorldAsset());
        if (room is null) return;
        RoomSceneBuilder.ApplySceneSettings(scene, room);
        foreach (RectangleF bounds in RoomTileCollisionMap.Get(room, project).Solids)
            scene.Physics.RegisterStaticBox(ToPhysics(new Vector3(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2, 0)),
                new Vector3(bounds.Width / (2 * PixelsPerMetre), bounds.Height / (2 * PixelsPerMetre), .5f), "Room tile");
    }

    public static bool Attach(EcsWorld world, Entity entity, string project, JObject document, TransformComponent transform)
    {
        if (!UsesSavedTwoDAsset(project, document)) return false;
        string name = (string)document["physics"];
        string path = ResourceNames.Resolve(project, name, ResourceType.Physics);
        if (string.IsNullOrWhiteSpace(path) || (bool?)document["solid"] == false) return false;
        PhysicsSceneConfig asset = PhysicsSceneConfig.LoadFromFile(path);
        if (asset.Dimension != PhysicsDimension.TwoD) return false;
        ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry draw);
        var image = SpriteCollisionBounds.Load(project, draw?.Image);
        RectangleF bounds = SpriteCollisionBounds.Resolve(image, 0, 0, 0, transform.ScaleX, transform.ScaleY);
        Vector3 size = SizeFor(asset, bounds);
        if (!PhysicsDeclarativeBinding.TryBuildRigidBody(name, null, null, project, size, out RigidBodyComponent body, out _)) return false;
        body.Mass = MassFor(asset, size);
        SpritePhysicsBindingComponent binding = new() { CentreOffset = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2) };
        world.Set(entity, binding);
        world.Set(entity, body);
        world.Set(entity, Pose(binding, transform));
        return true;
    }

    public static Vector3 SizeFor(PhysicsSceneConfig asset, RectangleF bounds)
    {
        Vector3 size = new(MathF.Max(.01f, bounds.Width / (2 * PixelsPerMetre)), MathF.Max(.01f, bounds.Height / (2 * PixelsPerMetre)), .5f);
        if (asset.Shape is PhysicsBodyShape.Sphere or PhysicsBodyShape.Capsule or PhysicsBodyShape.Cylinder) size.X = MathF.Min(size.X, size.Y);
        return size;
    }

    public static float MassFor(PhysicsSceneConfig asset, Vector3 size)
    {
        float area = asset.Shape switch
        {
            PhysicsBodyShape.Sphere => MathF.PI * size.X * size.X,
            PhysicsBodyShape.Capsule or PhysicsBodyShape.Cylinder => MathF.PI * size.X * size.X + 4 * size.X * MathF.Max(0, size.Y - size.X),
            _ => size.X * size.Y * 4,
        };
        return MathF.Max(.001f, (float)asset.Density * area);
    }

    public static Transform3DComponent Pose(SpritePhysicsBindingComponent binding, TransformComponent sprite)
    {
        Vector2 offset = Vector2.Transform(binding.CentreOffset, Matrix3x2.CreateRotation(sprite.Rotation * MathF.PI / 180));
        return new Transform3DComponent
        {
            Position = ToPhysics(new Vector3(sprite.X + offset.X, sprite.Y + offset.Y, 0)),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -sprite.Rotation * MathF.PI / 180), Scale = Vector3.One,
        };
    }

    public static void SyncSprite(SpritePhysicsBindingComponent binding, Transform3DComponent pose, ref TransformComponent sprite)
    {
        float angle = -MathF.Atan2(2 * (pose.Rotation.W * pose.Rotation.Z + pose.Rotation.X * pose.Rotation.Y),
            1 - 2 * (pose.Rotation.Y * pose.Rotation.Y + pose.Rotation.Z * pose.Rotation.Z));
        Vector2 offset = Vector2.Transform(binding.CentreOffset, Matrix3x2.CreateRotation(angle));
        Vector3 centre = ToPixels(pose.Position);
        sprite.X = centre.X - offset.X; sprite.Y = centre.Y - offset.Y;
        sprite.Rotation = sprite.RotationZ = angle * 180 / MathF.PI;
    }

    public static Vector3 ToPhysics(Vector3 pixels) => new(pixels.X / PixelsPerMetre, -pixels.Y / PixelsPerMetre, 0);
    public static Vector3 ToPixels(Vector3 metres) => new(metres.X * PixelsPerMetre, -metres.Y * PixelsPerMetre, 0);
    public static Vector3 ToPhysics(EcsWorld world, int entityId, Vector3 vector) => world.Has<SpritePhysicsBindingComponent>(world.GetEntity(entityId)) ? ToPhysics(vector) : vector;
    public static Vector3 ToPixels(EcsWorld world, int entityId, Vector3 vector) => world.Has<SpritePhysicsBindingComponent>(world.GetEntity(entityId)) ? ToPixels(vector) : vector;
}
