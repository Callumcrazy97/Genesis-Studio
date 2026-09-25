#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Modeling;

/// <summary>Resolves and applies named model attachment sockets without assigning gameplay meaning.</summary>
public static class ModelSocketRuntime
{
    public static void Update(EcsWorld world, string projectPath, RuntimeModelAssetRegistry assets)
    {
        if (world is null || assets is null) return;
        world.Query<TransformComponent, ModelSocketAttachmentComponent>((Entity entity, ref TransformComponent child,
            ref ModelSocketAttachmentComponent attachment) =>
        {
            if (!attachment.Enabled || attachment.ParentEntityId < 0 || attachment.ParentEntityId == entity.Id
                || string.IsNullOrWhiteSpace(attachment.SocketName)) return;
            Entity parent = world.GetEntity(attachment.ParentEntityId);
            if (!world.IsAlive(parent) || !world.Has<TransformComponent>(parent)
                || !world.Has<ModelRendererComponent>(parent)) return;

            ref TransformComponent parentTransform = ref world.GetRef<TransformComponent>(parent);
            ref ModelRendererComponent parentModel = ref world.GetRef<ModelRendererComponent>(parent);
            if (string.IsNullOrWhiteSpace(parentModel.ModelAsset)) return;
            GModelAsset asset = assets.Load(projectPath, parentModel.ModelAsset);
            RuntimeModelAnimationState animation = AnimationState(world, parent, parentModel);
            Matrix4x4 parentWorld = RuntimeModelRenderSystem.TransformMatrix(parentTransform, parentModel);
            Matrix4x4 offset = IsUsable(attachment.LocalOffset) ? attachment.LocalOffset : Matrix4x4.Identity;
            if (!TryResolve(asset, attachment.SocketName, animation, parentWorld, out Matrix4x4 resolved)) return;
            resolved = offset * resolved;
            if (!Matrix4x4.Decompose(resolved, out Vector3 scale, out Quaternion rotation, out Vector3 position)) return;

            Vector3 euler = EulerDegrees(rotation);
            child.X = position.X;
            child.Y = position.Y;
            child.Z = position.Z;
            child.ScaleX = SafeScale(scale.X);
            child.ScaleY = SafeScale(scale.Y);
            child.ScaleZ = SafeScale(scale.Z);
            child.Rotation = euler.Z;
            child.RotationX = euler.X;
            child.RotationY = euler.Y;
            child.RotationZ = euler.Z;
            Genesis.Runtime.Scripting.PgslCommands.SynchronizeTransform(world, entity, Vector3.One);
        });
    }

    public static bool TryResolve(
        GModelAsset asset,
        string socketName,
        RuntimeModelAnimationState animation,
        Matrix4x4 modelWorld,
        out Matrix4x4 socketWorld)
    {
        socketWorld = Matrix4x4.Identity;
        if (asset is null) return false;
        GModelSocket? socket = asset.Sockets?.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, socketName, StringComparison.OrdinalIgnoreCase));
        if (socket is null) return false;

        Matrix4x4 anchor = Matrix4x4.Identity;
        var bones = asset.Rig?.Bones;
        if (socket.BoneIndex >= 0 && bones is { Count: > 0 }
            && socket.BoneIndex < bones.Count)
        {
            Matrix4x4[] local = GModelPrimitiveFactory.EvaluateAnimatedLocals(asset, animation);
            Matrix4x4[] transforms = GModelPrimitiveFactory.ComputeWorldTransforms(bones, local);
            anchor = socket.BoneIndex < transforms.Length ? transforms[socket.BoneIndex] : Matrix4x4.Identity;
        }
        else
        {
            var nodes = asset.Nodes;
            if (socket.NodeIndex >= 0 && nodes is { Count: > 0 } && socket.NodeIndex < nodes.Count)
                anchor = NodeWorld(nodes, socket.NodeIndex);
        }

        socketWorld = socket.LocalTransform * anchor * modelWorld;
        return IsUsable(socketWorld);
    }

    private static RuntimeModelAnimationState AnimationState(EcsWorld world, Entity entity, ModelRendererComponent model)
    {
        if (!world.Has<ModelAnimatorComponent>(entity)) return default;
        ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
        float blend = animator.BlendDuration <= 0f
            ? 1f
            : Math.Clamp(animator.BlendElapsed / animator.BlendDuration, 0f, 1f);
        return new RuntimeModelAnimationState(
            animator.ClipName,
            animator.TimeSeconds,
            animator.ClipFps,
            animator.Loop,
            previousClipName: animator.PreviousClipName,
            previousTimeSeconds: animator.PreviousTimeSeconds,
            blendFactor: blend,
            preserveRootTransform: model.KeepPreviousTransform,
            controller: animator.Controller);
    }

    private static Matrix4x4 NodeWorld(System.Collections.Generic.IReadOnlyList<GModelNode> nodes, int index)
    {
        Matrix4x4 result = Matrix4x4.Identity;
        Span<int> chain = stackalloc int[128];
        int count = 0;
        int current = index;
        while (current >= 0 && current < nodes.Count && count < chain.Length)
        {
            bool repeated = false;
            for (int i = 0; i < count; i++) repeated |= chain[i] == current;
            if (repeated) break;
            chain[count++] = current;
            current = nodes[current].ParentIndex;
        }
        for (int i = 0; i < count; i++) result *= nodes[chain[i]].LocalTransform;
        return result;
    }

    private static Vector3 EulerDegrees(Quaternion value)
    {
        value = Quaternion.Normalize(value);
        float pitchSin = 2f * (value.W * value.X - value.Y * value.Z);
        float pitch = MathF.Abs(pitchSin) >= 1f ? MathF.CopySign(MathF.PI / 2f, pitchSin) : MathF.Asin(pitchSin);
        float yaw = MathF.Atan2(2f * (value.W * value.Y + value.Z * value.X),
            1f - 2f * (value.X * value.X + value.Y * value.Y));
        float roll = MathF.Atan2(2f * (value.W * value.Z + value.X * value.Y),
            1f - 2f * (value.X * value.X + value.Z * value.Z));
        const float degrees = 180f / MathF.PI;
        return new Vector3(pitch * degrees, yaw * degrees, roll * degrees);
    }

    private static bool IsUsable(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M22) && float.IsFinite(value.M33)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43)
        && MathF.Abs(value.M44) > 1e-8f;

    private static float SafeScale(float value) => float.IsFinite(value) && MathF.Abs(value) > 1e-6f ? value : 1f;
}
