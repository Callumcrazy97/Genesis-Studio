using System;
using System.Numerics;
using Genesis.Physics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using SharedPhysicsMotionType = Genesis.Shared.ECS.Components.PhysicsMotionType;

namespace Genesis.Runtime.Systems
{
    /// <summary>Grab, hold, and throw physics objects with the mouse while look is captured.</summary>
    public sealed class PhysicsGrabSystem : IFixedEcsSystem, IEcsSystem
    {
        private readonly RuntimeScene _scene;

        public float Reach { get; set; } = 8f;
        public float HoldDistance { get; set; } = 3.5f;
        public float ThrowBaseSpeed { get; set; } = 12f;
        public float ThrowDragScale { get; set; } = 0.02f;
        public float HandVelocityScale { get; set; } = 1.25f;

        private Entity _heldEntity;
        private int _heldRegistrationId;
        private Vector3 _lastHoldPoint;
        private Vector3 _handVelocity;
        private Vector2 _dragLookAccum;

        public PhysicsGrabSystem(RuntimeScene scene) => _scene = scene;

        public int FixedPriority => 105;

        public int Priority => 20;

        public void Update(IEcsWorld world, float dt)
        {
            InputState input = _scene.Input;
            if (input == null || _scene.Physics == null)
                return;

            if (input.WasPressed(MouseButton.Left))
                TryGrab(world);

            if (_heldRegistrationId != 0 && input.IsDown(MouseButton.Left))
                _dragLookAccum += input.LookDelta;
        }

        public void FixedUpdate(IEcsWorld world, float fixedDelta)
        {
            InputState input = _scene.Input;
            PhysicsWorld physics = _scene.Physics;
            if (input == null || physics == null || _heldRegistrationId == 0)
                return;

            var camera = _scene.Camera3D;
            Vector3 holdPoint = camera.Position + camera.Forward * HoldDistance;

            if (!input.IsDown(MouseButton.Left))
            {
                Release(physics, world, camera);
                return;
            }

            if (_lastHoldPoint != Vector3.Zero && fixedDelta > 0f)
                _handVelocity = (holdPoint - _lastHoldPoint) / fixedDelta;

            _lastHoldPoint = holdPoint;

            Quaternion orientation = Quaternion.Identity;
            if (! _heldEntity.IsNull && world.Has<Transform3DComponent>(_heldEntity))
                orientation = world.GetRef<Transform3DComponent>(_heldEntity).Rotation;

            physics.SetBodyPose(world, _heldRegistrationId, holdPoint, orientation);
        }

        private void TryGrab(IEcsWorld world)
        {
            PhysicsWorld physics = _scene.Physics;
            if (physics == null)
                return;

            Entity playerEntity = default;
            int playerRegistrationId = 0;
            world.Query<PhysicsPlayerTag, RigidBodyComponent>((entity, ref _, ref body) =>
            {
                if (!IsEnabled(world, entity) || body.RegistrationId == 0)
                    return;
                playerEntity = entity;
                playerRegistrationId = body.RegistrationId;
            });

            var camera = _scene.Camera3D;
            if (!physics.Raycast(world, camera.Position, camera.Forward, Reach, out var hit, playerEntity))
                return;

            if (hit.Entity.IsNull || hit.Entity == playerEntity)
                return;

            ref var body = ref world.GetRef<RigidBodyComponent>(hit.Entity);
            if (body.Motion != SharedPhysicsMotionType.Dynamic || body.RegistrationId == 0)
                return;

            _heldEntity = hit.Entity;
            _heldRegistrationId = body.RegistrationId;
            _dragLookAccum = Vector2.Zero;
            _handVelocity = Vector3.Zero;
            _lastHoldPoint = camera.Position + camera.Forward * HoldDistance;
        }

        private void Release(PhysicsWorld physics, IEcsWorld world, Camera3D camera)
        {
            Vector3 releaseVelocity = _handVelocity * HandVelocityScale;

            if (_dragLookAccum.LengthSquared() > 1f)
            {
                Vector3 throwDir = camera.Forward;
                throwDir += camera.Right * (_dragLookAccum.X * ThrowDragScale);
                throwDir += Vector3.UnitY * (-_dragLookAccum.Y * ThrowDragScale);
                if (throwDir.LengthSquared() > 1e-4f)
                    throwDir = Vector3.Normalize(throwDir);

                float dragSpeed = _dragLookAccum.Length() * ThrowDragScale;
                releaseVelocity += throwDir * (ThrowBaseSpeed + dragSpeed);
            }

            float weight = DefaultWeight(world, _heldEntity, physics);
            releaseVelocity /= MathF.Max(MathF.Sqrt(weight), 0.5f);

            if (physics.MaxVelocity > 0f)
            {
                float cap = physics.MaxVelocity * 0.85f;
                float speed = releaseVelocity.Length();
                if (speed > cap)
                    releaseVelocity = releaseVelocity / speed * cap;
            }

            physics.SetLinearVelocity(world, _heldRegistrationId, releaseVelocity);

            _heldEntity = default;
            _heldRegistrationId = 0;
            _dragLookAccum = Vector2.Zero;
            _handVelocity = Vector3.Zero;
            _lastHoldPoint = Vector3.Zero;
        }

        private static float DefaultWeight(IEcsWorld world, Entity entity, PhysicsWorld physics)
        {
            if (entity.IsNull || !world.Has<RigidBodyComponent>(entity))
                return physics.DefaultWeight;
            float weight = world.GetRef<RigidBodyComponent>(entity).Weight;
            return weight > 0f ? weight : physics.DefaultWeight;
        }

        private static bool IsEnabled(IEcsWorld world, Entity entity)
        {
            if (!world.Has<EntityLifecycleComponent>(entity))
                return true;
            return world.GetRef<EntityLifecycleComponent>(entity).Enabled;
        }
    }
}
