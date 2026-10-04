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
