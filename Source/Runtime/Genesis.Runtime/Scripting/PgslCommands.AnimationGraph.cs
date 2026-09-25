#nullable enable
using System;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    private static AnimationController? Graph()
    {
        PgslContext? ctx = GetContext();
        if (ctx == null) return null;
        ctx.ModelAnimationTouched = true;
        return ctx.AnimationController ??= new AnimationController(ctx.AnimationParameters);
    }

    [PgslCommand("AnimationBlendTreeCreate1D", "AnimationBlendTreeCreate1D(parameterName) -> number", "Create an instance-local 1D blend tree", "Animation")]
    public static double AnimationBlendTreeCreate1D(string parameterName)
    {
        var graph = Graph();
        if (graph == null) return -1;
        int id = graph.Trees.Count + 1;
        graph.Trees[id] = new BlendTree1D(parameterName);
        return id;
    }
    [PgslCommand("AnimationBlendTreeAddClip", "AnimationBlendTreeAddClip(treeId, clip, threshold)", "Add a threshold clip to a 1D tree", "Animation")]
    public static void AnimationBlendTreeAddClip(double treeId, string clip, double threshold)
    {
        if (Graph()?.Trees.TryGetValue((int)treeId, out var tree) == true && tree is BlendTree1D blend)
            blend.AddClip(clip, (float)threshold);
    }
    [PgslCommand("AnimationBlendTreeCreateDirect", "AnimationBlendTreeCreateDirect() -> number", "Create an instance-local direct blend tree", "Animation")]
    public static double AnimationBlendTreeCreateDirect()
    {
        var graph = Graph();
        if (graph == null) return -1;
        int id = graph.Trees.Count + 1;
        graph.Trees[id] = new DirectBlend();
        return id;
    }
    [PgslCommand("AnimationBlendTreeSetWeight", "AnimationBlendTreeSetWeight(treeId, clip, weight)", "Set a direct blend clip weight; weights are normalized", "Animation")]
    public static void AnimationBlendTreeSetWeight(double treeId, string clip, double weight)
    {
        if (Graph()?.Trees.TryGetValue((int)treeId, out var tree) == true && tree is DirectBlend blend)
            blend.SetWeight(clip, (float)weight);
    }
    [PgslCommand("AnimationStateCreate", "AnimationStateCreate(stateName, clipOrBlendTree)", "Create a state from a clip name or numeric tree ID; first state is entry", "Animation")]
    public static void AnimationStateCreate(string stateName, object clipOrBlendTree)
    {
        var graph = Graph();
        if (graph == null) return;
        string source = Convert.ToString(clipOrBlendTree, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        AnimationBlendTree node = int.TryParse(source, out int id) && graph.Trees.TryGetValue(id, out var tree)
            ? tree : new ClipNode(source);
        graph.Layers[graph.ActiveLayer].StateMachine.AddState(stateName, node);
        GetContext().ModelAnimationActive = true;
    }
    [PgslCommand("AnimationStateCreateClip", "AnimationStateCreateClip(stateName, clip, loop)", "Create a clip state, including non-looping attacks", "Animation")]
    public static void AnimationStateCreateClip(string stateName, string clip, bool loop)
    {
        var graph = Graph();
        if (graph == null) return;
        graph.Layers[graph.ActiveLayer].StateMachine.AddState(stateName, new ClipNode(clip, loop));
        GetContext().ModelAnimationActive = true;
    }
    [PgslCommand("AnimationStateAddTransition", "AnimationStateAddTransition(fromState, toState, condition, blendSeconds)", "Transition on bool, numeric comparison or AnimationFinished; * means any state", "Animation")]
    public static void AnimationStateAddTransition(string fromState, string toState, string condition, double blendSeconds)
    {
        var graph = Graph();
        if (graph == null) return;
        var machine = graph.Layers[graph.ActiveLayer].StateMachine;
        if (!machine.Contains(toState) || (fromState != "*" && !machine.Contains(fromState)))
        {
            ActiveGameContext?.Log("Animation transition ignored: create its source and destination states first.");
            return;
        }
        machine.AddTransition(fromState, toState, condition, (float)blendSeconds);
    }
    [PgslCommand("AnimationSetParameter", "AnimationSetParameter(name, value)", "Set a numeric blend/state parameter", "Animation")]
    public static void AnimationSetParameter(string name, double value) => AnimationStateSetFloat(name, value);
    [PgslCommand("AnimationGetParameter", "AnimationGetParameter(name) -> number", "Read a numeric blend/state parameter", "Animation")]
    public static double AnimationGetParameter(string name) => AnimationStateGetFloat(name);
    [PgslCommand("AnimationSetBool", "AnimationSetBool(name, value)", "Set a boolean state parameter", "Animation")]
    public static void AnimationSetBool(string name, bool value) => AnimationStateSetBool(name, value);
    [PgslCommand("AnimationGetBool", "AnimationGetBool(name) -> bool", "Read a boolean state parameter", "Animation")]
    public static bool AnimationGetBool(string name) => AnimationStateGetBool(name);
    [PgslCommand("AnimationLayerCreate", "AnimationLayerCreate(layerIndex, boneMask)", "Create/select a layer; comma-separated exact bone names, empty mask affects all bones", "Animation")]
    public static void AnimationLayerCreate(double layerIndex, string boneMask)
    {
        if (!double.IsFinite(layerIndex) || layerIndex != Math.Truncate(layerIndex) || layerIndex < 0 || layerIndex > 15)
            throw new ArgumentOutOfRangeException(nameof(layerIndex), "Layer index must be 0–15.");
        var graph = Graph();
        if (graph == null) return;
        int index = (int)layerIndex;
        if (!graph.Layers.TryGetValue(index, out var layer)) graph.Layers[index] = layer = new AnimationLayer();
        layer.BoneMask.Clear();
        foreach (string bone in boneMask.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) layer.BoneMask.Add(bone);
        graph.ActiveLayer = index;
    }
    [PgslCommand("AnimationLayerSetWeight", "AnimationLayerSetWeight(layerIndex, weight)", "Set layer influence between zero and one", "Animation")]
    public static void AnimationLayerSetWeight(double layerIndex, double weight)
    {
        if (Graph()?.Layers.TryGetValue((int)layerIndex, out var layer) == true && double.IsFinite(weight))
            layer.Weight = (float)Math.Clamp(weight, 0, 1);
    }
    [PgslCommand("AnimationLayerSetAdditive", "AnimationLayerSetAdditive(layerIndex, additive)", "Choose additive or override layer blending", "Animation")]
    public static void AnimationLayerSetAdditive(double layerIndex, bool additive)
    {
        if (Graph()?.Layers.TryGetValue((int)layerIndex, out var layer) == true)
            layer.Mode = additive ? AnimationLayerMode.Additive : AnimationLayerMode.Override;
    }
    [PgslCommand("ModelAnimationSetRootMotion", "ModelAnimationSetRootMotion(enabled)", "Extract and apply base-layer graph root movement each simulation step", "Animation")]
    public static void ModelAnimationSetRootMotion(bool enabled)
    {
        var graph = Graph();
        if (graph == null) return;
        var ctx = GetContext();
        var machine = graph.Layers[0].StateMachine;
        if (machine.CurrentState.Length == 0 && !string.IsNullOrWhiteSpace(ctx.ModelAnimationClip))
        {
            machine.AddState(ctx.ModelAnimationClip, new ClipNode(ctx.ModelAnimationClip, ctx.ModelAnimationLoop));
            machine.Seek((float)ctx.ModelAnimationTime);
        }
        graph.RootMotionEnabled = enabled;
        graph.ClearRootMotion();
    }
    [PgslCommand("ModelAnimationGetRootMotionDeltaX", "ModelAnimationGetRootMotionDeltaX() -> number", "Last local root displacement X", "Animation")]
    public static double ModelAnimationGetRootMotionDeltaX() => GetContext()?.AnimationController?.RootMotionDelta.X ?? 0;
    [PgslCommand("ModelAnimationGetRootMotionDeltaY", "ModelAnimationGetRootMotionDeltaY() -> number", "Last local root displacement Y", "Animation")]
    public static double ModelAnimationGetRootMotionDeltaY() => GetContext()?.AnimationController?.RootMotionDelta.Y ?? 0;
    [PgslCommand("ModelAnimationGetRootMotionDeltaZ", "ModelAnimationGetRootMotionDeltaZ() -> number", "Last local root displacement Z", "Animation")]
    public static double ModelAnimationGetRootMotionDeltaZ() => GetContext()?.AnimationController?.RootMotionDelta.Z ?? 0;
}
