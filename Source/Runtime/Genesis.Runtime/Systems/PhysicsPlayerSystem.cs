using System;
using System.Numerics;
using Genesis.Physics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using SharedCollisionShape = Genesis.Shared.ECS.Components.CollisionShape;
using SharedPhysicsMotionType = Genesis.Shared.ECS.Components.PhysicsMotionType;

namespace Genesis.Runtime.Systems
{
    /// <summary>Physics-driven first-person controller: WASD movement, mouse look, jump, and camera follow.</summary>
    public sealed class PhysicsPlayerSystem : IFixedEcsSystem, IEcsSystem
    {
        private readonly RuntimeScene _scene;

        public float MoveSpeed { get; set; } = 8f;
        public float JumpSpeed { get; set; } = 6.5f;
        public float LookSensitivity { get; set; } = 0.005f;
        public float EyeHeight { get; set; } = 1.55f;

        public PhysicsPlayerSystem(RuntimeScene scene) => _scene = scene;

        public int FixedPriority => 95;

        public int Priority => 10;

        public void FixedUpdate(IEcsWorld world, float fixedDelta)
        {
            InputState input = _scene.Input;
            PhysicsWorld physics = _scene.Physics;
            if (input == null || physics == null)
                return;

            var camera = _scene.Camera3D;

            world.Query<PhysicsPlayerTag, RigidBodyComponent, Transform3DComponent>(
                (entity, ref _, ref body, ref transform) =>
            {
                if (!IsEnabled(world, entity) || body.RegistrationId == 0 || body.Motion != SharedPhysicsMotionType.Dynamic)
                    return;

                CharacterMotorComponent motor = world.Has<CharacterMotorComponent>(entity)
                    ? world.GetRef<CharacterMotorComponent>(entity)
                    : CharacterMotorComponent.Default;
                PhysicsWaterVolume water = _scene.FindWater(transform.Position);
                float submerged = water?.SubmergedFraction(transform.Position, body.HalfExtents) ?? 0f;
                bool swimming = water?.Swimmable == true && submerged >= 0.2f;
                bool jumpOut = swimming && motor.WasInWater && input.WasPressed(Key.Space)
                    && submerged < 0.72f;
                if (jumpOut) swimming = false;
                bool grounded = physics.TryGetGroundContact(world, body.RegistrationId, motor.StepHeight + 0.12f, out PhysicsRaycastHit ground);
                float slopeCos = MathF.Cos(Math.Clamp(motor.MaximumSlopeDegrees, 0f, 89f) * MathF.PI / 180f);
                bool walkable = grounded && ground.Normal.Y >= slopeCos;
                Vector3 velocity = physics.GetLinearVelocity(body.RegistrationId);

                Vector3 wish = Vector3.Zero;
                if (input.IsDown(Key.W)) wish += camera.Forward;
                if (input.IsDown(Key.S)) wish -= camera.Forward;
                if (input.IsDown(Key.D)) wish += camera.Right;
                if (input.IsDown(Key.A)) wish -= camera.Right;
                wish.Y = 0f;
                if (wish != Vector3.Zero)
                    wish = Vector3.Normalize(wish);

                float speed = MathF.Max(0f, motor.WalkSpeed) * (input.IsDown(Key.Shift) ? MathF.Max(1f, motor.SprintMultiplier) : 1f);
                if (swimming)
                {
                    Vector3 swimWish = wish;
                    if (input.IsDown(Key.Space)) swimWish += Vector3.UnitY;
                    if (input.IsDown(Key.Control)) swimWish -= Vector3.UnitY;
                    if (swimWish.LengthSquared() > 1f) swimWish = Vector3.Normalize(swimWish);
                    Vector3 target = new(swimWish.X * motor.SwimSpeed, swimWish.Y * motor.SwimVerticalSpeed, swimWish.Z * motor.SwimSpeed);
                    velocity = MoveTowards(velocity, target, motor.SwimAcceleration * fixedDelta);
                    velocity *= MathF.Exp(-MathF.Max(0f, motor.SwimDrag) * fixedDelta * 0.25f);
                    if (transform.Position.Y >= water.SurfaceY - MathF.Max(0f, motor.SurfaceOffset) && velocity.Y > 0f)
                        velocity.Y = 0f;
                    motor.State = CharacterMotorState.Swimming;
                }
                else
                {
                    float acceleration = walkable ? motor.GroundAcceleration : motor.AirAcceleration;
                    Vector2 horizontal = MoveTowards(new Vector2(velocity.X, velocity.Z), new Vector2(wish.X, wish.Z) * speed, acceleration * fixedDelta);
                    velocity.X = horizontal.X; velocity.Z = horizontal.Y;
                    if (walkable)
                    {
                        SnapToWalkableSurface(world, physics, entity, body, transform, wish, motor, slopeCos, ref velocity);
                        if (input.WasPressed(Key.Space) && motor.JumpSpeed > 0f)
                            velocity.Y = motor.JumpSpeed;
                        else if (velocity.Y < 0f)
                            velocity.Y = 0f;
                        motor.State = CharacterMotorState.Grounded;
                    }
                    else
                    {
                        if (jumpOut)
                            velocity.Y = MathF.Max(velocity.Y, motor.JumpSpeed);
                        if (grounded)
                        {
                            Vector3 downhill = Vector3.Normalize(new Vector3(ground.Normal.X, 0f, ground.Normal.Z));
                            velocity += downhill * 9.81f * fixedDelta;
                        }
                        motor.State = CharacterMotorState.Falling;
                    }
                }

                motor.EnteredWater = swimming && !motor.WasInWater;
                motor.ExitedWater = !swimming && motor.WasInWater;
                motor.WasInWater = swimming;
                motor.SubmergedFraction = submerged;
                world.Set(entity, motor);

                physics.SetLinearVelocity(world, body.RegistrationId, velocity);
            });
        }

        private static Vector3 MoveTowards(Vector3 current, Vector3 target, float maximumDelta)
        {
            Vector3 delta = target - current;
            float length = delta.Length();
            return length <= maximumDelta || length < 1e-6f ? target : current + delta / length * maximumDelta;
        }

        private static Vector2 MoveTowards(Vector2 current, Vector2 target, float maximumDelta)
        {
            Vector2 delta = target - current;
            float length = delta.Length();
            return length <= maximumDelta || length < 1e-6f ? target : current + delta / length * maximumDelta;
        }

        private static void SnapToWalkableSurface(
            IEcsWorld world,
            PhysicsWorld physics,
            Entity entity,
            RigidBodyComponent body,
            Transform3DComponent transform,
            Vector3 wish,
            CharacterMotorComponent motor,
            float slopeCos,
            ref Vector3 velocity)
        {
            if (wish == Vector3.Zero || motor.StepHeight <= 0f || velocity.Y > 0.1f) return;
            Vector3 current = physics.GetBodyPosition(body.RegistrationId);
            float reach = MathF.Max(body.HalfExtents.X, body.HalfExtents.Z) + 0.12f;
            Vector3 probe = current + wish * reach + Vector3.UnitY * motor.StepHeight;
            if (!physics.RaycastDown(world, probe, motor.StepHeight * 2f + 0.2f, out PhysicsRaycastHit step, entity)) return;
            if (step.Normal.Y < slopeCos) return;
            float targetY = step.Point.Y + body.HalfExtents.Y;
            float delta = targetY - current.Y;
            if (delta < -motor.StepHeight || delta > motor.StepHeight) return;
            physics.SetBodyPose(world, body.RegistrationId, new Vector3(current.X, targetY, current.Z), transform.Rotation);
            velocity.Y = 0f;
        }

        private bool _isThirdPerson = true;

        public void Update(IEcsWorld world, float dt)
        {
            InputState input = _scene.Input;
            if (input == null)
                return;

            if (input.WasPressed(Key.V))
                _isThirdPerson = !_isThirdPerson;

            var camera = _scene.Camera3D;
            camera.ApplyLook(input.LookDelta, LookSensitivity);

            world.Query<PhysicsPlayerTag, RigidBodyComponent, Transform3DComponent>(
                (entity, ref _, ref body, ref transform) =>
            {
                if (!IsEnabled(world, entity))
                    return;

                float eyeOffset = body.Shape == SharedCollisionShape.Capsule
                    ? body.Size.Y + body.Size.X * 0.25f
                    : EyeHeight - body.HalfExtents.Y;
                
                Vector3 basePos = transform.Position + new Vector3(0f, eyeOffset, 0f);
                if (_isThirdPerson)
                {
                    // Move the camera back 10 units for 3rd person.
                    Vector3 camPos = basePos - camera.Forward * 10f;
                    // Prevent camera from clipping through the floor when looking up
                    camPos.Y = MathF.Max(camPos.Y, transform.Position.Y + 0.5f);
                    camera.Position = camPos;
                }
                else
                {
                    camera.Position = basePos;
                }
            });
        }

        private static bool IsEnabled(IEcsWorld world, Entity entity)
        {
            if (!world.Has<EntityLifecycleComponent>(entity))
                return true;
            return world.GetRef<EntityLifecycleComponent>(entity).Enabled;
        }
    }
}
