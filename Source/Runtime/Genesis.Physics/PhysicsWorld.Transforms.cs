using System.Numerics;
using BepuPhysics;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    /// <summary>Synchronize an authored pose/size without discarding a dynamic body's velocity.</summary>
    public void SynchronizeEntityTransform(IEcsWorld world, Entity entity, Vector3 sizeRatio)
    {
        if (!world.Has<RigidBodyComponent>(entity) || !world.Has<Transform3DComponent>(entity)) return;
        ref var body = ref world.GetRef<RigidBodyComponent>(entity);
        ref var transform = ref world.GetRef<Transform3DComponent>(entity);
        bool dynamic = TryGetDynamicHandle(body.RegistrationId, out BodyHandle handle);
        BodyVelocity velocity = dynamic ? _simulation.Bodies.GetBodyReference(handle).Velocity : default;
        if (dynamic && sizeRatio == Vector3.One)
        {
            SetBodyPose(world, body.RegistrationId, transform.Position, transform.Rotation);
            _simulation.Bodies.GetBodyReference(handle).Velocity = velocity;
            _simulation.Bodies.UpdateBounds(handle);
            return;
        }
        bool registered = body.RegistrationId != 0;
        if (registered) UnregisterEntity(world, entity, ref body);
        body.Size *= body.Shape switch
        {
            Genesis.Shared.ECS.Components.CollisionShape.Sphere => new Vector3(System.MathF.Max(sizeRatio.X, System.MathF.Max(sizeRatio.Y, sizeRatio.Z))),
            Genesis.Shared.ECS.Components.CollisionShape.Capsule or Genesis.Shared.ECS.Components.CollisionShape.Cylinder => new Vector3(System.MathF.Max(sizeRatio.X, sizeRatio.Z), sizeRatio.Y, 1),
            _ => sizeRatio,
        };
        if (registered)
        {
            RegisterEntity(world, entity, ref body, ref transform);
            if (TryGetDynamicHandle(body.RegistrationId, out handle)) _simulation.Bodies.GetBodyReference(handle).Velocity = velocity;
        }
    }
}

