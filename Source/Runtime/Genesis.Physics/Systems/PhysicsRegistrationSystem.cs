using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Physics.Systems
{
    /// <summary>
    /// Registers rigid bodies with the Bepu simulation during fixed update.
    /// Only entities with <see cref="RigidBodyComponent.Collision"/> enabled are simulated —
    /// untagged objects never receive physics unless explicitly configured.
    /// </summary>
    public sealed class PhysicsRegistrationSystem : IFixedEcsSystem
    {
        private readonly PhysicsWorld _physics;

        public PhysicsRegistrationSystem(PhysicsWorld physics) => _physics = physics;

        public int FixedPriority => 80;

        public void FixedUpdate(IEcsWorld world, float fixedDelta)
        {
            world.Query<RigidBodyComponent, Transform3DComponent, EntityLifecycleComponent>(
                (entity, ref rigid, ref transform, ref lifecycle) =>
            {
                if (rigid.RegistrationId != 0 || !lifecycle.Enabled || !rigid.Collision)
                    return;

                _physics.RegisterEntity(world, entity, ref rigid, ref transform);
            });
        }
    }
}
