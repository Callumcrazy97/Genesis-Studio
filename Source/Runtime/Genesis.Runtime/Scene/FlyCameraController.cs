using System;
using System.Numerics;
using Genesis.Runtime.Core;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;

namespace Genesis.Runtime.Scene
{
    /// <summary>WASD + mouse-look controller for a <see cref="Camera3D"/>.</summary>
    public sealed class FlyCameraController
    {
        public Camera3D Camera { get; }
        public float MoveSpeed { get; set; } = 12f;
        public float FastMultiplier { get; set; } = 3f;
        public float LookSensitivity { get; set; } = 0.005f;

        public FlyCameraController(Camera3D camera) => Camera = camera;

        public void Update(InputState input, float deltaTime, bool allowMovement = true)
        {
            if (input == null) return;

            Camera.ApplyLook(input.LookDelta, LookSensitivity);

            if (!allowMovement) return;

            float speed = MoveSpeed * (input.IsDown(Key.Shift) ? FastMultiplier : 1f) * deltaTime;
            Vector3 move = Vector3.Zero;
            if (input.IsDown(Key.W)) move += Camera.Forward;
            if (input.IsDown(Key.S)) move -= Camera.Forward;
            if (input.IsDown(Key.D)) move += Camera.Right;
            if (input.IsDown(Key.A)) move -= Camera.Right;
            if (input.IsDown(Key.Space)) move += Vector3.UnitY;
            if (input.IsDown(Key.Control)) move -= Vector3.UnitY;

            if (move != Vector3.Zero)
                Camera.Position += move * speed;
        }
    }
}
