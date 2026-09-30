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
        var ctx = GetContext();
        if (ctx == null) return;
        var graph = Graph();
        if (graph == null) return;
        if (graph.Layers[0].StateMachine.CurrentState.Length == 0 && !string.IsNullOrWhiteSpace(ctx.ModelAnimationClip))
        {
            graph.Layers[0].StateMachine.AddState("__current_clip", new ClipNode(ctx.ModelAnimationClip, ctx.ModelAnimationLoop));
            graph.Layers[0].StateMachine.Seek((float)ctx.ModelAnimationTime);
        }
        const double radians = Math.PI / 180;
        graph.SetBoneRotation(bone, Quaternion.CreateFromYawPitchRoll(
            (float)(yaw % 360 * radians), (float)(pitch % 360 * radians), (float)(roll % 360 * radians)));
    }

    [PgslCommand("AnimationBoneClearRotation", "AnimationBoneClearRotation(bone)",
        "Remove an instance-local bone rotation and return to the sampled animation pose", "Animation")]
    public static void AnimationBoneClearRotation(string bone) => GetContext()?.AnimationController?.BoneRotations.Remove(bone);
}
