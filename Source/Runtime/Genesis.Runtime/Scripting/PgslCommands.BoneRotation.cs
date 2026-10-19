using System;
using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    [PgslCommand("AnimationBoneSetRotation", "AnimationBoneSetRotation(bone, pitch, yaw, roll)",
        "Apply an instance-local bone rotation in degrees after animation blending; useful for gaze or aim", "Animation")]
    public static void AnimationBoneSetRotation(string bone, double pitch, double yaw, double roll)
    {
        if (!double.IsFinite(pitch) || !double.IsFinite(yaw) || !double.IsFinite(roll))
            throw new ArgumentException("Bone angles must be finite.");
        var graph = BoneGraph();
        if (graph == null) return;
        const double radians = Math.PI / 180;
        graph.SetBoneRotation(bone, Quaternion.CreateFromYawPitchRoll(
            (float)(yaw % 360 * radians), (float)(pitch % 360 * radians), (float)(roll % 360 * radians)));
    }

    [PgslCommand("AnimationBoneClearRotation", "AnimationBoneClearRotation(bone)",
        "Remove an instance-local bone rotation and return to the sampled animation pose", "Animation")]
    public static void AnimationBoneClearRotation(string bone) => GetContext()?.AnimationController?.BoneRotations.Remove(bone);

    [PgslCommand("AnimationBoneSetTranslation", "AnimationBoneSetTranslation(bone, x, y, z)",
        "Move a bone by a local offset (in its parent's space, model units) after animation blending, as AnimationBoneSetRotation turns it", "Animation")]
    public static void AnimationBoneSetTranslation(string bone, double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            throw new ArgumentException("A bone offset must be finite.");
        BoneGraph()?.SetBoneTranslation(bone, new Vector3((float)x, (float)y, (float)z));
    }

    [PgslCommand("AnimationBoneClearTranslation", "AnimationBoneClearTranslation(bone)",
        "Remove an instance-local bone offset and return to the sampled animation pose", "Animation")]
    public static void AnimationBoneClearTranslation(string bone) => GetContext()?.AnimationController?.BoneTranslations.Remove(bone);

    [PgslCommand("AnimationBoneLookAt", "AnimationBoneLookAt(bone, x, y, z, weight?, maxDegrees?)",
        "Turn a bone (a head, a neck) to face a point in the world after animation blending, as far as maxDegrees (default 70) and weight (0 to 1, default 1) allow; call it each frame as the point moves. The model is taken to face +Z in its own space unless AnimationSetLookForward says otherwise", "Animation")]
    public static void AnimationBoneLookAt(string bone, double x, double y, double z, double weight = 1, double maxDegrees = 70)
    {
        if (!Finite3(x, y, z) || !double.IsFinite(weight) || !double.IsFinite(maxDegrees))
            throw new ArgumentException("A look-at point, weight and limit must be finite.");
        if (string.IsNullOrWhiteSpace(bone) || !TryModelPoint(x, y, z, out Vector3 point)) return;
        BoneGraph()?.SetBoneLookAt(bone.Trim(), point, (float)weight, (float)maxDegrees);
    }

    [PgslCommand("AnimationBoneReach", "AnimationBoneReach(bone, x, y, z, weight?)",
        "Bend the two bones above a bone so it reaches a point in the world after animation blending: a hand to a door handle, a foot onto a step. Lengths stay as they are, the limb keeps the way it bends, and a point out of reach is reached towards. Weight 0 to 1 (default 1); call it each frame as the point moves", "Animation")]
    public static void AnimationBoneReach(string bone, double x, double y, double z, double weight = 1)
    {
        if (!Finite3(x, y, z) || !double.IsFinite(weight)) throw new ArgumentException("A reach point and weight must be finite.");
        if (string.IsNullOrWhiteSpace(bone) || !TryModelPoint(x, y, z, out Vector3 point)) return;
        BoneGraph()?.SetBoneReach(bone.Trim(), point, (float)weight);
    }

    [PgslCommand("AnimationBoneClearReach", "AnimationBoneClearReach(bone)", "Stop a bone reaching for a point; its limb follows its animation again", "Animation")]
    public static void AnimationBoneClearReach(string bone) => GetContext()?.AnimationController?.BoneReaches.Remove(bone ?? "");

    // A point in the world in the instance's model's own space, the space its bones are in: the
    // inverse of the draw's transform with the model's pivot.
    private static bool TryModelPoint(double x, double y, double z, out Vector3 point)
    {
        point = default;
        var context = GetContext();
        var world = ActiveGameContext?.World;
        if (context == null || world == null) return false;
        var entity = world.GetEntity(context.InstanceId);
        if (!world.IsAlive(entity) || !world.Has<Genesis.Runtime.ECS.Components.ModelRendererComponent>(entity)
            || !world.Has<Genesis.Runtime.ECS.Components.TransformComponent>(entity)) return false;
        ref var model = ref world.GetRef<Genesis.Runtime.ECS.Components.ModelRendererComponent>(entity);
        ref var transform = ref world.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
        Vector3 pivot = SocketModelAssets.Load(ProjectPath, model.ModelAsset)?.Pivot?.Position ?? Vector3.Zero;
        Matrix4x4 placed = Matrix4x4.CreateTranslation(-pivot) * RuntimeModelRenderSystem.TransformMatrix(transform, model);
        if (!Matrix4x4.Invert(placed, out Matrix4x4 inverse)) return false;
        point = Vector3.Transform(new Vector3((float)x, (float)y, (float)z), inverse);
        return true;
    }

    [PgslCommand("AnimationBoneClearLookAt", "AnimationBoneClearLookAt(bone)", "Stop a bone looking at a point; it follows its animation again", "Animation")]
    public static void AnimationBoneClearLookAt(string bone) => GetContext()?.AnimationController?.BoneLookAts.Remove(bone ?? "");

    [PgslCommand("AnimationSetLookForward", "AnimationSetLookForward(x, y, z)",
        "Which way the model faces in its own space (default 0, 0, 1), for a model built facing another way; AnimationBoneLookAt measures turns from it", "Animation")]
    public static void AnimationSetLookForward(double x, double y, double z)
    {
        if (!Finite3(x, y, z) || x * x + y * y + z * z < 1e-12) return;
        var graph = BoneGraph();
        if (graph != null) graph.LookForward = Vector3.Normalize(new Vector3((float)x, (float)y, (float)z));
    }
    // The instance's animation graph, keeping the clip it was already playing as its current state.
    private static AnimationController BoneGraph()
    {
        var ctx = GetContext();
        if (ctx == null) return null;
        var graph = Graph();
        if (graph == null) return null;
        if (graph.Layers[0].StateMachine.CurrentState.Length == 0 && !string.IsNullOrWhiteSpace(ctx.ModelAnimationClip))
        {
            graph.Layers[0].StateMachine.AddState("__current_clip", new ClipNode(ctx.ModelAnimationClip, ctx.ModelAnimationLoop));
            graph.Layers[0].StateMachine.Seek((float)ctx.ModelAnimationTime);
        }
        return graph;
    }
}
