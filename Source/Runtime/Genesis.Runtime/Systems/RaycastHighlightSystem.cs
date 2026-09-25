using System.Numerics;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Runtime.Systems
{
    /// <summary>Highlights the mesh the camera is looking at using a physics raycast.</summary>
    public sealed class RaycastHighlightSystem : IEcsSystem
    {
        private readonly RuntimeScene _scene;

        public float MaxDistance { get; set; } = 8f;
        public Vector4 HighlightTint { get; set; } = new(1.35f, 1.25f, 0.55f, 1f);

        private Entity _highlightedEntity;
        private Vector4 _savedTint = Vector4.One;

        public RaycastHighlightSystem(RuntimeScene scene) => _scene = scene;

        public int Priority => 250;

        public void Update(IEcsWorld world, float dt)
        {
            var physics = _scene.Physics;
            if (physics == null)
            {
                ClearHighlight(world);
                return;
            }

            Entity ignore = default;
            world.Query<RaycastHighlightTag, PhysicsPlayerTag, RigidBodyComponent>(
                (entity, ref _, ref __, ref body) =>
            {
                if (!IsEnabled(world, entity) || body.RegistrationId == 0)
                    return;
                ignore = entity;
            });

            var camera = _scene.Camera3D;
            ClearHighlight(world);

            if (!physics.Raycast(world, camera.Position, camera.Forward, MaxDistance, out var hit, ignore))
                return;

            if (hit.Entity.IsNull || !world.Has<MeshDrawComponent>(hit.Entity))
                return;

            _highlightedEntity = hit.Entity;
            ref var mesh = ref world.GetRef<MeshDrawComponent>(hit.Entity);
            _savedTint = mesh.Tint;
            mesh.Tint = HighlightTint;
        }

        private void ClearHighlight(IEcsWorld world)
        {
            if (_highlightedEntity.IsNull || !world.IsAlive(_highlightedEntity))
            {
                _highlightedEntity = default;
                return;
            }

            if (world.Has<MeshDrawComponent>(_highlightedEntity))
            {
                ref var mesh = ref world.GetRef<MeshDrawComponent>(_highlightedEntity);
                mesh.Tint = _savedTint;
            }

            _highlightedEntity = default;
        }

        private static bool IsEnabled(IEcsWorld world, Entity entity)
        {
            if (!world.Has<EntityLifecycleComponent>(entity))
                return true;
            return world.GetRef<EntityLifecycleComponent>(entity).Enabled;
        }
    }
}
