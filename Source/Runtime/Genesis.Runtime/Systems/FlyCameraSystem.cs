using System.Numerics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Runtime.Systems
{
    /// <summary>
    /// WASD + mouse-look controller for Camera3D when no physics player entities exist. It stands
    /// aside once anything else moves the camera (a script's SetCameraPosition every Step), so the
    /// camera is not moved twice a frame; RuntimeScene.FreeFlyCamera turns it off or keeps it on.
    /// </summary>
    public sealed class FlyCameraSystem : IEcsSystem
    {
        private readonly RuntimeScene _scene;
        private int _generation = -1;
        private bool _haveLeft, _otherOwnsCamera;
        private Vector3 _leftPosition;
        private float _leftYaw, _leftPitch;

        public float MoveSpeed { get; set; } = 12f;
        public float FastMultiplier { get; set; } = 3f;
        public float LookSensitivity { get; set; } = 0.005f;

        public FlyCameraSystem(RuntimeScene scene) => _scene = scene;

        public int Priority => 5;

        public void Update(IEcsWorld world, float dt)
        {
            if (_generation != _scene.RoomGeneration)
            {
                _generation = _scene.RoomGeneration;
                _haveLeft = _otherOwnsCamera = false;
            }
            if (_scene.HostControlsFlyCamera || _scene.FreeFlyCamera == false || HasPhysicsPlayer(world))
            {
                _haveLeft = false;
                return;
            }

            InputState input = _scene.Input;
            if (input == null)
                return;

            var camera = _scene.Camera3D;
            if (_scene.FreeFlyCamera != true)
            {
                // Moved since this system last left it: a script (or another system) owns the camera.
                if (_otherOwnsCamera) return;
                if (_haveLeft && (camera.Position != _leftPosition || camera.Yaw != _leftYaw || camera.Pitch != _leftPitch))
                {
                    _otherOwnsCamera = true;
                    return;
                }
            }

            camera.ApplyLook(input.LookDelta, LookSensitivity);
            if (AnyMovementKey(input))
            {
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

            _leftPosition = camera.Position;
            _leftYaw = camera.Yaw;
            _leftPitch = camera.Pitch;
            _haveLeft = true;
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
