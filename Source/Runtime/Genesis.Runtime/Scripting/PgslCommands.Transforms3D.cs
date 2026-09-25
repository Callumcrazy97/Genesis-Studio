#nullable enable
using System;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    [PgslCommand("InstanceSetRotation3D", "InstanceSetRotation3D(instanceId, pitch, yaw, roll)", "Set persistent Euler rotation in degrees and synchronize the physics pose", "Instances")]
    public static void InstanceSetRotation3D(double instanceId, double pitch, double yaw, double roll)
    {
        if (!Finite3(pitch, yaw, roll) || !TryTransform(instanceId, out var entity, out var world)) return;
        ref var t = ref world.GetRef<TransformComponent>(entity);
        t.RotationX = (float)pitch; t.RotationY = (float)yaw; t.RotationZ = t.Rotation = (float)roll;
        if (GetContext() is { } ctx && ctx.InstanceId == entity.Id) ctx.ImageAngle = roll;
        SynchronizeTransform(world, entity, Vector3.One);
    }
    [PgslCommand("InstanceGetRotationPitch", "InstanceGetRotationPitch(instanceId) -> number", "Read persistent pitch in degrees", "Instances")]
    public static double InstanceGetRotationPitch(double instanceId) => TryTransform(instanceId, out var e, out var w) ? w.GetRef<TransformComponent>(e).RotationX : 0;
    [PgslCommand("InstanceGetRotationYaw", "InstanceGetRotationYaw(instanceId) -> number", "Read persistent yaw in degrees", "Instances")]
    public static double InstanceGetRotationYaw(double instanceId) => TryTransform(instanceId, out var e, out var w) ? w.GetRef<TransformComponent>(e).RotationY : 0;
    [PgslCommand("InstanceGetRotationRoll", "InstanceGetRotationRoll(instanceId) -> number", "Read persistent roll in degrees", "Instances")]
    public static double InstanceGetRotationRoll(double instanceId) => TryTransform(instanceId, out var e, out var w) ? w.GetRef<TransformComponent>(e).RotationZ : 0;
    [PgslCommand("InstanceSetScale3D", "InstanceSetScale3D(instanceId, sx, sy, sz)", "Set nonzero persistent XYZ scale and resize registered physics colliders", "Instances")]
    public static void InstanceSetScale3D(double instanceId, double sx, double sy, double sz)
    {
        if (!Finite3(sx, sy, sz) || Math.Abs(sx) < 1e-6 || Math.Abs(sy) < 1e-6 || Math.Abs(sz) < 1e-6
            || !TryTransform(instanceId, out var e, out var w)) return;
        ref var t = ref w.GetRef<TransformComponent>(e);
        Vector3 ratio = new((float)sx / UnitScale(t.ScaleX), (float)sy / UnitScale(t.ScaleY), (float)sz / UnitScale(t.ScaleZ));
        t.ScaleX = (float)sx; t.ScaleY = (float)sy; t.ScaleZ = (float)sz;
        if (GetContext() is { } ctx && ctx.InstanceId == e.Id) { ctx.ImageXScale = sx; ctx.ImageYScale = sy; }
        SynchronizeTransform(w, e, Vector3.Abs(ratio));
    }
    [PgslCommand("InstanceGetScaleX", "InstanceGetScaleX(instanceId) -> number", "Read persistent X scale", "Instances")]
    public static double InstanceGetScaleX(double instanceId) => TryTransform(instanceId, out var e, out var w) ? UnitScale(w.GetRef<TransformComponent>(e).ScaleX) : 0;
    [PgslCommand("InstanceGetScaleY", "InstanceGetScaleY(instanceId) -> number", "Read persistent Y scale", "Instances")]
    public static double InstanceGetScaleY(double instanceId) => TryTransform(instanceId, out var e, out var w) ? UnitScale(w.GetRef<TransformComponent>(e).ScaleY) : 0;
    [PgslCommand("InstanceGetScaleZ", "InstanceGetScaleZ(instanceId) -> number", "Read persistent Z scale", "Instances")]
    public static double InstanceGetScaleZ(double instanceId) => TryTransform(instanceId, out var e, out var w) ? UnitScale(w.GetRef<TransformComponent>(e).ScaleZ) : 0;

    private static bool Finite3(double x, double y, double z) => float.IsFinite((float)x) && float.IsFinite((float)y) && float.IsFinite((float)z);
    private static float UnitScale(float scale) => scale == 0 ? 1 : scale;

    internal static void SynchronizeTransform(EcsWorld world, Entity entity, Vector3 sizeRatio)
    {
        ref var t = ref world.GetRef<TransformComponent>(entity);
        if (!world.Has<Transform3DComponent>(entity))
        {
            if (!world.Has<RigidBodyComponent>(entity)) return;
            world.Set(entity, Transform3DComponent.Default);
        }
        ref var pose = ref world.GetRef<Transform3DComponent>(entity);
        pose.Position = new(t.X, t.Y, t.Z);
        pose.Rotation = Quaternion.CreateFromYawPitchRoll(t.RotationY * MathF.PI / 180,
            t.RotationX * MathF.PI / 180, t.RotationZ * MathF.PI / 180);
        pose.Scale = new(UnitScale(t.ScaleX), UnitScale(t.ScaleY), UnitScale(t.ScaleZ));
        pose.PoseHistoryValid = 0;
        PhysicsWorld?.SynchronizeEntityTransform(world, entity, sizeRatio);
    }

    internal static void ApplyAnimationRootMotion(EcsWorld world, Entity entity, AnimationController controller)
    {
        ref var t = ref world.GetRef<TransformComponent>(entity);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(t.RotationY * MathF.PI / 180,
            t.RotationX * MathF.PI / 180, t.RotationZ * MathF.PI / 180);
        Vector3 scale = new(UnitScale(t.ScaleX), UnitScale(t.ScaleY), UnitScale(t.ScaleZ));
        if (world.Has<ModelRendererComponent>(entity))
        {
            var model = world.GetRef<ModelRendererComponent>(entity);
            scale *= new Vector3(UnitScale(model.ScaleX), UnitScale(model.ScaleY), UnitScale(model.ScaleZ));
        }
        Vector3 delta = Vector3.Transform(controller.RootMotionDelta * scale, rotation);
        t.X += delta.X; t.Y += delta.Y; t.Z += delta.Z;
        Quaternion q = Quaternion.Normalize(rotation * controller.RootMotionRotation);
        t.RotationX = MathF.Asin(Math.Clamp(2 * (q.W * q.X - q.Y * q.Z), -1, 1)) * 180 / MathF.PI;
        t.RotationY = MathF.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y)) * 180 / MathF.PI;
        t.RotationZ = t.Rotation = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z)) * 180 / MathF.PI;
        SynchronizeTransform(world, entity, Vector3.One);
    }
}
