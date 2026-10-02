using System;
using System.Globalization;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Model playback and animation-state commands shared by handwritten PGSL and Object Builder
/// actions. Sprite playback remains frame based; model transitions blend skeletal poses in the
/// renderer through <c>ModelAnimatorComponent</c>.
/// </summary>
public static partial class PgslCommands
{
    [PgslCommand("ModelSet", "ModelSet(model)", "Assign a 3D model to this instance", "Models")]
    public static void ModelSet(string model)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        ctx.ModelAsset = model?.Trim() ?? string.Empty;
        ctx.ModelBindingTouched = true;
    }

    [PgslCommand("ModelGet", "ModelGet() -> string", "Get this instance's assigned 3D model", "Models")]
    public static string ModelGet() => GetContext()?.ModelAsset ?? string.Empty;

    [PgslCommand("ModelAnimationPlay", "ModelAnimationPlay(clip, loop, blendSeconds)",
        "Play a skeletal clip and blend from the current pose", "Animation")]
    public static void ModelAnimationPlay(string clip, bool loop, double blendSeconds)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;

        string next = clip?.Trim() ?? string.Empty;
        // Explicit clip playback hands control back from a graph to the established clip player.
        ctx.AnimationController = null;
        double duration = Math.Max(0, blendSeconds);
        bool changing = !string.Equals(ctx.ModelAnimationClip, next, StringComparison.OrdinalIgnoreCase);
        if (changing && duration > 0 && !string.IsNullOrWhiteSpace(ctx.ModelAnimationClip))
        {
            ctx.ModelAnimationPreviousClip = ctx.ModelAnimationClip;
            ctx.ModelAnimationPreviousTime = ctx.ModelAnimationTime;
            ctx.ModelAnimationBlendDuration = duration;
            ctx.ModelAnimationBlendElapsed = 0;
        }
        else if (changing)
        {
            ctx.ModelAnimationPreviousClip = string.Empty;
            ctx.ModelAnimationPreviousTime = 0;
            ctx.ModelAnimationBlendDuration = 0;
            ctx.ModelAnimationBlendElapsed = 0;
        }

        if (changing) ctx.ModelAnimationTime = 0;
        ctx.ModelAnimationClip = next;
        ctx.ModelAnimationLoop = loop;
        ctx.ModelAnimationActive = next.Length > 0;
        if (ctx.ModelAnimationSpeed == 0) ctx.ModelAnimationSpeed = 1;
        ctx.ModelAnimationTouched = true;
    }

    [PgslCommand("ModelAnimationStop", "ModelAnimationStop()", "Pause the active model clip", "Animation")]
    public static void ModelAnimationStop()
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        ctx.ModelAnimationActive = false;
        ctx.ModelAnimationTouched = true;
    }

    [PgslCommand("ModelAnimationIsPlaying", "ModelAnimationIsPlaying() -> bool",
        "True while a model clip is playing", "Animation")]
    public static bool ModelAnimationIsPlaying() => GetContext()?.ModelAnimationActive ?? false;

    [PgslCommand("ModelAnimationGetClip", "ModelAnimationGetClip() -> string",
        "Get the active model clip", "Animation")]
    public static string ModelAnimationGetClip() => GetContext()?.ModelAnimationClip ?? string.Empty;

    [PgslCommand("ModelAnimationSetSpeed", "ModelAnimationSetSpeed(speed)",
        "Set the model clip playback multiplier", "Animation")]
    public static void ModelAnimationSetSpeed(double speed)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        ctx.ModelAnimationSpeed = speed;
        ctx.ModelAnimationTouched = true;
    }

    [PgslCommand("ModelAnimationGetSpeed", "ModelAnimationGetSpeed() -> number",
        "Get the model clip playback multiplier", "Animation")]
    public static double ModelAnimationGetSpeed() => GetContext()?.ModelAnimationSpeed ?? 0;

    /// <summary>Length in seconds of one of the instance's clips at the rate it is played, or 0.</summary>
    private static double ModelClipSeconds(PgslContext ctx, string clipName)
    {
        if (ctx is null || string.IsNullOrWhiteSpace(ctx.ModelAsset) || string.IsNullOrWhiteSpace(clipName)) return 0;
        Genesis.Runtime.Modeling.GModelAsset asset = Genesis.Runtime.Modeling.RuntimeModelAssetRegistry.Shared.Load(ProjectPath, ctx.ModelAsset);
        if (asset?.Animations is null) return 0;
        foreach (Genesis.Runtime.Modeling.GModelAnimationClip clip in asset.Animations)
        {
            if (!string.Equals(clip.Name, clipName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (clip.Frames is not { Count: > 0 }) return 0;
            double fps = ctx.ModelAnimationFps > 0 ? ctx.ModelAnimationFps : Math.Max(1.0, clip.Fps);
            return clip.Frames.Count / fps;
        }

        return 0;
    }

    /// <summary>
    /// True when a clip that moved from <paramref name="before"/> to <paramref name="now"/> seconds
    /// passed <paramref name="mark"/> seconds on the way. A looping clip passes each mark once
    /// every time round.
    /// </summary>
    public static bool AnimationPassed(double before, double now, double mark, double length, bool loop)
    {
        if (!(now > before) || !(length > 0) || !double.IsFinite(mark)) return false;
        mark = Math.Clamp(mark, 0, length);
        if (!loop) return before <= mark && mark < now;
        // The first time round at or after where the clip was.
        double turn = Math.Max(0, Math.Ceiling((before - mark) / length));
        return mark + turn * length < now;
    }

    [PgslCommand("ModelAnimationGetTime", "ModelAnimationGetTime() -> number",
        "Seconds the active model clip has been playing", "Animation")]
    public static double ModelAnimationGetTime() => GetContext()?.ModelAnimationTime ?? 0;

    [PgslCommand("ModelAnimationGetLength", "ModelAnimationGetLength(clip) -> number",
        "Length of one of this model's clips in seconds at the rate it is played; 0 if the model has no such clip", "Animation")]
    public static double ModelAnimationGetLength(string clip) => ModelClipSeconds(GetContext(), clip);

    [PgslCommand("ModelAnimationGetProgress", "ModelAnimationGetProgress() -> number",
        "From 0 to 1: how far through the active model clip; a looping clip starts again at 0", "Animation")]
    public static double ModelAnimationGetProgress()
    {
        PgslContext ctx = GetContext();
        double length = ModelClipSeconds(ctx, ctx?.ModelAnimationClip);
        if (length <= 0) return 0;
        double time = ctx.ModelAnimationTime;
        return ctx.ModelAnimationLoop ? time % length / length : Math.Clamp(time / length, 0, 1);
    }

    [PgslCommand("ModelAnimationCrossed", "ModelAnimationCrossed(fraction) -> bool",
        "True in the one frame the active model clip passes this point (0 = its start, 1 = its end): the moment a blow lands or a foot comes down", "Animation")]
    public static bool ModelAnimationCrossed(double fraction)
    {
        PgslContext ctx = GetContext();
        if (ctx is null || !ctx.ModelAnimationActive) return false;
        // A clip that did not move this frame (paused, or the game held still) passes nothing.
        if (ctx.ModelAnimationAdvancedFrame != (ActiveGameContext?.FrameCount ?? 0)) return false;
        double length = ModelClipSeconds(ctx, ctx.ModelAnimationClip);
        return AnimationPassed(ctx.ModelAnimationLastTime, ctx.ModelAnimationTime, Math.Clamp(fraction, 0, 1) * length, length,
            ctx.ModelAnimationLoop);
    }

    [PgslCommand("KeepPreviousTransform", "Engine.Rendering.Models.KeepPreviousTransform",
        "Keep entity position, rotation and proportional model scale when a model or animation changes",
        "Engine · Models", Namespace = "Engine.Rendering.Models")]
    public static bool ModelKeepPreviousTransform
    {
        get => GetContext()?.ModelKeepPreviousTransform ?? false;
        set
        {
            PgslContext ctx = GetContext();
            if (ctx is null) return;
            ctx.ModelKeepPreviousTransform = value;
            ctx.ModelTransformPolicyTouched = true;
        }
    }

    [PgslCommand("ModelAnimationSetTime", "ModelAnimationSetTime(seconds)",
        "Seek the active model clip", "Animation")]
    public static void ModelAnimationSetTime(double seconds)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        if (!double.IsFinite(seconds)) return;
        ctx.ModelAnimationTime = Math.Max(0, seconds);
        if (ctx.AnimationController is { } graph)
        {
            foreach (var layer in graph.Layers.Values) layer.StateMachine.Seek((float)ctx.ModelAnimationTime);
            graph.ClearRootMotion();
        }
        ctx.ModelAnimationTouched = true;
    }

    [PgslCommand("ModelAnimationSetLoop", "ModelAnimationSetLoop(loop)",
        "Set whether the active model clip repeats", "Animation")]
    public static void ModelAnimationSetLoop(bool loop)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        ctx.ModelAnimationLoop = loop;
        ctx.ModelAnimationTouched = true;
    }

    [PgslCommand("AnimationStatePlay", "AnimationStatePlay(state, loop, blendSeconds)",
        "Play an animation state on the assigned sprite or model", "Animation State")]
    public static void AnimationStatePlay(string state, bool loop, double blendSeconds)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        if (ctx.AnimationController is { } controller
            && controller.Layers[controller.ActiveLayer].StateMachine.Contains(state))
        {
            var machine = controller.Layers[controller.ActiveLayer].StateMachine;
            if (!string.Equals(machine.CurrentState, state, StringComparison.OrdinalIgnoreCase))
                machine.Play(state, (float)Math.Max(0, double.IsFinite(blendSeconds) ? blendSeconds : 0));
            ctx.ModelAnimationActive = true;
            ctx.ModelAnimationTouched = true;
            return;
        }
        if (!string.IsNullOrWhiteSpace(ctx.ModelAsset))
            ModelAnimationPlay(state, loop, blendSeconds);
        else
        {
            double duration = Math.Max(0, blendSeconds);
            bool changing = !string.Equals(ctx.SpriteAnimationTag, state?.Trim(), StringComparison.OrdinalIgnoreCase);
            if (duration > 0 && ctx.SpriteAnimationActive && changing)
            {
                ctx.SpriteTransitionPreviousImage = ctx.SpriteIndex ?? string.Empty;
                ctx.SpriteTransitionPreviousFrame = ctx.ImageIndex;
                ctx.SpriteTransitionDuration = duration;
                ctx.SpriteTransitionElapsed = 0;
                ctx.SpriteTransitionTouched = true;
            }
            AnimationPlay(state, loop);
        }
    }

    [PgslCommand("AnimationStateStop", "AnimationStateStop()",
        "Pause the assigned sprite or model animation", "Animation State")]
    public static void AnimationStateStop()
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        if (!string.IsNullOrWhiteSpace(ctx.ModelAsset)) ModelAnimationStop();
        else AnimationStop();
    }

    [PgslCommand("AnimationStateSetSpeed", "AnimationStateSetSpeed(speed)",
        "Set playback speed for the assigned sprite or model animation", "Animation State")]
    public static void AnimationStateSetSpeed(double speed)
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return;
        if (!string.IsNullOrWhiteSpace(ctx.ModelAsset)) ModelAnimationSetSpeed(speed);
        else AnimationSetSpeed(speed);
    }

    [PgslCommand("AnimationStateHasFinished", "AnimationStateHasFinished() -> bool",
        "True when the current non-looping sprite or model state has completed", "Animation State")]
    public static bool AnimationStateHasFinished()
    {
        PgslContext ctx = GetContext();
        if (ctx is null) return true;
        if (ctx.AnimationController is { } graph)
            return graph.ActiveStateHasFinished;
        return !string.IsNullOrWhiteSpace(ctx.ModelAsset)
            ? !ctx.ModelAnimationActive
            : !ctx.SpriteAnimationActive;
    }

    [PgslCommand("AnimationStateSetFloat", "AnimationStateSetFloat(name, value)",
        "Set a numeric animation-controller parameter", "Animation State")]
    public static void AnimationStateSetFloat(string name, double value) =>
        SetAnimationParameter(name, value);

    [PgslCommand("AnimationStateGetFloat", "AnimationStateGetFloat(name) -> number",
        "Get a numeric animation-controller parameter", "Animation State")]
    public static double AnimationStateGetFloat(string name)
    {
        object value = GetAnimationParameter(name);
        if (value is null) return 0;
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    [PgslCommand("AnimationStateSetBool", "AnimationStateSetBool(name, value)",
        "Set a boolean animation-controller parameter", "Animation State")]
    public static void AnimationStateSetBool(string name, bool value) =>
        SetAnimationParameter(name, value);

    [PgslCommand("AnimationStateGetBool", "AnimationStateGetBool(name) -> bool",
        "Get a boolean animation-controller parameter", "Animation State")]
    public static bool AnimationStateGetBool(string name)
    {
        object value = GetAnimationParameter(name);
        if (value is bool result) return result;
        if (value is string text && bool.TryParse(text, out result)) return result;
        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0; }
        catch { return false; }
    }

    [PgslCommand("AnimationStateSetText", "AnimationStateSetText(name, value)",
        "Set a text animation-controller parameter", "Animation State")]
    public static void AnimationStateSetText(string name, string value) =>
        SetAnimationParameter(name, value ?? string.Empty);

    [PgslCommand("AnimationStateGetText", "AnimationStateGetText(name) -> string",
        "Get a text animation-controller parameter", "Animation State")]
    public static string AnimationStateGetText(string name) =>
        Convert.ToString(GetAnimationParameter(name), CultureInfo.InvariantCulture) ?? string.Empty;

    [PgslCommand("AnimationStateSetTrigger", "AnimationStateSetTrigger(name)",
        "Raise a one-shot animation-controller trigger", "Animation State")]
    public static void AnimationStateSetTrigger(string name) => SetAnimationParameter(name, true);

    [PgslCommand("AnimationStateConsumeTrigger", "AnimationStateConsumeTrigger(name) -> bool",
        "Read and clear a one-shot animation-controller trigger", "Animation State")]
    public static bool AnimationStateConsumeTrigger(string name)
    {
        PgslContext ctx = GetContext();
        string key = name?.Trim() ?? string.Empty;
        if (ctx is null || key.Length == 0 || !ctx.AnimationParameters.TryGetValue(key, out object value))
            return false;
        bool raised = value is bool flag ? flag : AnimationStateGetBool(key);
        ctx.AnimationParameters[key] = false;
        return raised;
    }

    private static void SetAnimationParameter(string name, object value)
    {
        PgslContext ctx = GetContext();
        string key = name?.Trim() ?? string.Empty;
        if (ctx is null || key.Length == 0) return;
        ctx.AnimationParameters[key] = value;
    }

    private static object GetAnimationParameter(string name)
    {
        PgslContext ctx = GetContext();
        string key = name?.Trim() ?? string.Empty;
        return ctx is not null && key.Length > 0 && ctx.AnimationParameters.TryGetValue(key, out object value)
            ? value
            : null;
    }
}
