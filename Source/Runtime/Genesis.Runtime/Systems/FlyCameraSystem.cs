using System.Numerics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Runtime.Systems
{
    /// <summary>WASD + mouse-look controller for Camera3D when no physics player entities exist.</summary>
    public sealed class FlyCameraSystem : IEcsSystem
    {
        private readonly RuntimeScene _scene;

        public float MoveSpeed { get; set; } = 12f;
        public float FastMultiplier { get; set; } = 3f;
        public float LookSensitivity { get; set; } = 0.005f;

        public FlyCameraSystem(RuntimeScene scene) => _scene = scene;

        public int Priority => 5;

        public void Update(IEcsWorld world, float dt)
        {
            if (_scene.HostControlsFlyCamera || HasPhysicsPlayer(world))
                return;

            InputState input = _scene.Input;
            if (input == null)
                return;

            var camera = _scene.Camera3D;
            camera.ApplyLook(input.LookDelta, LookSensitivity);

            bool allowMovement = AnyMovementKey(input);
            if (!allowMovement)
                return;

            float speed = MoveSpeed * (input.IsDown(Key.Shift) ? FastMultiplier : 1f) * dt;
            Vector3 move = Vector3.Zero;
            if (input.IsDown(Key.W)) move += camera.Forward;
            if (input.IsDown(Key.S)) move -= camera.Forward;
            if (input.IsDown(Key.D)) move += camera.Right;
            if (input.IsDown(Key.A)) move -= camera.Right;
            if (input.IsDown(Key.Space)) move += Vector3.UnitY;
            if (input.IsDown(Key.Control)) move -= Vector3.UnitY;

            if (move != Vector3.Zero)
                camera.Position += move * speed;
        }

        private static bool HasPhysicsPlayer(IEcsWorld world)
        {
            bool found = false;
            world.Query<PhysicsPlayerTag, EntityLifecycleComponent>((entity, ref _, ref lifecycle) =>
            {
                if (lifecycle.Enabled)
                    found = true;
            });
            return found;
        }

        private static bool AnyMovementKey(InputState input) =>
            input.IsDown(Key.W) || input.IsDown(Key.A) || input.IsDown(Key.S) || input.IsDown(Key.D)
            || input.IsDown(Key.Space) || input.IsDown(Key.Control);
    }
}
