#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Modeling;

/// <summary>
/// What a script may ask of one model in the world: where a socket or bone is, which clip to
/// play and how, whether a clip has just passed a point, and how the model is tinted or lit.
/// </summary>
/// <remarks>
/// Every method takes the world and the entity, so a behaviour can use it on itself or on any
/// other model. Nothing here changes the shared Model resource; it is all per instance.
/// </remarks>
public static class ModelInstance
{
    private static RuntimeModelAssetRegistry Assets => RuntimeModelAssetRegistry.Shared;

    private static bool TryModel(EcsWorld? world, Entity entity, out GModelAsset asset)
    {
        asset = null!;
        if (world is null || !world.IsAlive(entity) || !world.Has<ModelRendererComponent>(entity)) return false;
        string model = world.GetRef<ModelRendererComponent>(entity).ModelAsset;
        if (string.IsNullOrWhiteSpace(model)) return false;
        asset = Assets.Load(PgslCommands.ProjectPath, model);
        return asset != null;
    }

    // ── Sockets and bones ────────────────────────────────────────────────────

    /// <summary>
    /// Where one of a model's sockets is in the world right now, in the pose it is being drawn
    /// in. A bone's name works as well when the model has no socket of that name. It is the same
    /// place an Object attached to that socket is put.
    /// </summary>
    public static bool TryGetSocketWorld(EcsWorld? world, Entity entity, string socketOrBone, out Matrix4x4 socketWorld)
    {
        socketWorld = Matrix4x4.Identity;
        if (string.IsNullOrWhiteSpace(socketOrBone) || !TryModel(world, entity, out GModelAsset asset)
            || !world!.Has<TransformComponent>(entity)) return false;
        ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
        ref ModelRendererComponent model = ref world.GetRef<ModelRendererComponent>(entity);
        return ModelSocketRuntime.TryResolve(asset, socketOrBone.Trim(), ModelSocketRuntime.AnimationState(world, entity, model),
            RuntimeModelRenderSystem.TransformMatrix(transform, model), out socketWorld);
    }

    /// <summary>The world position of a socket or bone; see <see cref="TryGetSocketWorld"/>.</summary>
    public static bool TryGetSocketPosition(EcsWorld? world, Entity entity, string socketOrBone, out Vector3 position)
    {
        bool found = TryGetSocketWorld(world, entity, socketOrBone, out Matrix4x4 socketWorld);
        position = socketWorld.Translation;
        return found;
    }

    // ── Clips ────────────────────────────────────────────────────────────────

    /// <summary>A new animator plays a clip at the rate the clip was authored at.</summary>
    private static ModelAnimatorComponent NewAnimator(GModelAsset asset, string clip)
    {
        float rate = 60f;
        if (asset.Animations != null)
            foreach (GModelAnimationClip candidate in asset.Animations)
                if (string.Equals(candidate.Name, clip.Trim(), StringComparison.OrdinalIgnoreCase)) rate = MathF.Max(1f, candidate.Fps);
        return new ModelAnimatorComponent { ClipFps = rate, PlaybackSpeed = 1f, Playing = true, Loop = true };
    }

    /// <summary>Length of one of the model's clips in seconds at the rate this instance plays it; 0 if it has none of that name.</summary>
    public static float ClipLength(EcsWorld? world, Entity entity, string clip)
    {
        if (string.IsNullOrWhiteSpace(clip) || !TryModel(world, entity, out GModelAsset asset) || asset.Animations is null) return 0f;
        float rate = world!.Has<ModelAnimatorComponent>(entity) ? world.GetRef<ModelAnimatorComponent>(entity).ClipFps : 0f;
        foreach (GModelAnimationClip candidate in asset.Animations)
        {
            if (!string.Equals(candidate.Name, clip.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (candidate.Frames is not { Count: > 0 }) return 0f;
            return candidate.Frames.Count / (rate > 0f ? rate : MathF.Max(1f, candidate.Fps));
        }

        return 0f;
    }

    /// <summary>
    /// Plays a clip on the whole model, blending from what it was playing.
    /// </summary>
    /// <param name="startSeconds">Where in the clip to begin.</param>
    /// <param name="speed">1 is as authored; a negative speed plays backwards from <paramref name="startSeconds"/>.</param>
    /// <returns>False when the model has no such clip.</returns>
    public static bool Play(EcsWorld? world, Entity entity, string clip, bool loop = true, float blendSeconds = 0.15f,
        float startSeconds = 0f, float speed = 1f)
    {
        if (ClipLength(world, entity, clip) <= 0f || !TryModel(world, entity, out GModelAsset asset)) return false;
        if (!world!.Has<ModelAnimatorComponent>(entity)) world.Set(entity, NewAnimator(asset, clip));
        ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
        bool blend = blendSeconds > 0f && !string.IsNullOrWhiteSpace(animator.ClipName);
        animator.PreviousClipName = blend ? animator.ClipName : "";
        animator.PreviousTimeSeconds = blend ? animator.TimeSeconds : 0f;
        animator.BlendDuration = blend ? blendSeconds : 0f;
        animator.BlendElapsed = 0f;
        animator.ClipName = clip.Trim();
        animator.TimeSeconds = float.IsFinite(startSeconds) ? MathF.Max(0f, startSeconds) : 0f;
        // Played once, a clip stays on its last frame: half a frame short of its length, so that
        // a clip authored to loop does not show its first frame again.
        float length = ClipLength(world, entity, clip);
        int frames = 1;
        foreach (GModelAnimationClip candidate in asset.Animations)
            if (string.Equals(candidate.Name, clip.Trim(), StringComparison.OrdinalIgnoreCase)) frames = Math.Max(1, candidate.Frames.Count);
        animator.HoldClipName = loop ? null : animator.ClipName;
        animator.HoldSeconds = loop ? 0f : MathF.Max(0.0001f, length - length / frames * 0.5f);
        if (!loop) animator.TimeSeconds = MathF.Min(animator.TimeSeconds, animator.HoldSeconds);
        animator.LastTimeSeconds = animator.TimeSeconds;
        animator.PlaybackSpeed = float.IsFinite(speed) && speed != 0f ? speed : 1f;
        animator.Loop = loop;
        animator.Playing = true;
        return true;
    }

    /// <summary>Plays a clip backwards from its end to its start: a weapon put away with the clip that draws it.</summary>
    public static bool PlayReversed(EcsWorld? world, Entity entity, string clip, float blendSeconds = 0.15f, float speed = 1f)
    {
        float length = ClipLength(world, entity, clip);
        return length > 0f && Play(world, entity, clip, loop: false, blendSeconds, startSeconds: length, speed: -MathF.Abs(speed == 0f ? 1f : speed));
    }

    /// <summary>From 0 to 1: how far through its clip the model is. A looping clip starts again at 0.</summary>
    public static float Progress(EcsWorld? world, Entity entity)
    {
        if (world is null || !world.IsAlive(entity) || !world.Has<ModelAnimatorComponent>(entity)) return 0f;
        ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
        float length = ClipLength(world, entity, animator.ClipName);
        if (length <= 0f) return 0f;
        return animator.Loop ? ((animator.TimeSeconds % length) + length) % length / length : Math.Clamp(animator.TimeSeconds / length, 0f, 1f);
    }

    /// <summary>True once a clip that does not loop has reached its end (or its start, played backwards).</summary>
    public static bool Finished(EcsWorld? world, Entity entity)
    {
        if (world is null || !world.IsAlive(entity) || !world.Has<ModelAnimatorComponent>(entity)) return false;
        ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
        if (animator.Loop) return false;
        float length = ClipLength(world, entity, animator.ClipName);
        if (animator.HoldSeconds > 0f && string.Equals(animator.HoldClipName, animator.ClipName, StringComparison.OrdinalIgnoreCase))
            length = animator.HoldSeconds;
        return length > 0f && (animator.PlaybackSpeed < 0f ? animator.TimeSeconds <= 0f : animator.TimeSeconds >= length);
    }

    /// <summary>
    /// True in the one update the model's clip passes a point (0 is its start, 1 its end): the
    /// moment a blow lands or a foot comes down. Works for a clip played backwards as well.
    /// </summary>
    public static bool Crossed(EcsWorld? world, Entity entity, float fraction)
    {
        if (world is null || !world.IsAlive(entity) || !world.Has<ModelAnimatorComponent>(entity)) return false;
        ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
        float length = ClipLength(world, entity, animator.ClipName);
        return Passed(animator.LastTimeSeconds, animator.TimeSeconds, Math.Clamp(fraction, 0f, 1f) * length, length, animator.Loop);
    }

    /// <summary>True when a clip that moved between two times passed a mark on the way, in either direction.</summary>
    public static bool Passed(float before, float now, float mark, float length, bool loop)
    {
        if (!(length > 0f) || !float.IsFinite(mark) || before == now) return false;
        if (now > before) return PgslCommands.AnimationPassed(before, now, mark, length, loop);
        mark = Math.Clamp(mark, 0f, length);
        if (!loop) return now <= mark && mark < before;
        // Backwards round a loop: the nearest time the mark comes up at or below where the clip was.
        float candidate = mark + MathF.Floor((before - mark) / length) * length;
        return candidate >= now && candidate < before;
    }

    // ── A second clip on part of the body ────────────────────────────────────

    /// <summary>
    /// Plays a clip on one bone and everything below it while the rest of the body keeps doing
    /// what it was: an attack on the spine and arms while the legs walk. It fades in, and a clip
    /// that does not loop fades out as it ends and lets go by itself.
    /// </summary>
    /// <param name="fromBone">The bone the clip takes over, with all its children ("Spine"). Empty takes the whole body.</param>
    /// <param name="speed">1 is as authored; negative plays the clip backwards from its end.</param>
    /// <returns>False when the model has no such clip or no such bone.</returns>
    /// <remarks>Applies to a model played by clip name. A model driven by an AnimationController has its own layers.</remarks>
    public static bool PlayLayer(EcsWorld? world, Entity entity, string clip, string fromBone, bool loop = false,
        float fadeSeconds = 0.12f, float speed = 1f, float startSeconds = 0f)
    {
        float length = ClipLength(world, entity, clip);
        if (length <= 0f || !TryModel(world, entity, out GModelAsset asset)) return false;
        string bone = fromBone?.Trim() ?? "";
        if (bone.Length > 0 && asset.Rig?.Bones?.Exists(candidate => string.Equals(candidate.Name, bone, StringComparison.OrdinalIgnoreCase)) != true)
            return false;
        if (!world!.Has<ModelAnimatorComponent>(entity)) world.Set(entity, NewAnimator(asset, clip));
        ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
        float rate = float.IsFinite(speed) && speed != 0f ? speed : 1f;
        animator.LayerClipName = clip.Trim();
        animator.LayerFromBone = bone;
        animator.LayerLoop = loop;
        animator.LayerSpeed = rate;
        animator.LayerLengthSeconds = length;
        animator.LayerTimeSeconds = rate < 0f && startSeconds <= 0f ? length : Math.Clamp(float.IsFinite(startSeconds) ? startSeconds : 0f, 0f, length);
        animator.LayerLastTimeSeconds = animator.LayerTimeSeconds;
        animator.LayerFadeSeconds = float.IsFinite(fadeSeconds) ? Math.Clamp(fadeSeconds, 0f, length * 0.5f) : 0f;
        animator.LayerWeight = animator.LayerFadeSeconds > 0f ? MathF.Max(animator.LayerWeight, 0.001f) : 1f;
        animator.LayerStopping = false;
        return true;
    }

    /// <summary>Lets go of the clip <see cref="PlayLayer"/> started, fading the body back to what it was doing.</summary>
    public static void StopLayer(EcsWorld? world, Entity entity, float fadeSeconds = 0.12f)
    {
        if (world is null || !world.IsAlive(entity) || !world.Has<ModelAnimatorComponent>(entity)) return;
        ref ModelAnimatorComponent animator = ref world.GetRef<ModelAnimatorComponent>(entity);
        if (string.IsNullOrEmpty(animator.LayerClipName)) return;
        if (!(fadeSeconds > 0f))
        {
            animator.ClearLayer();
            return;
        }

        animator.LayerFadeSeconds = fadeSeconds;
        animator.LayerStopping = true;
    }

    /// <summary>True while a clip started by <see cref="PlayLayer"/> still has hold of part of the body.</summary>
    public static bool LayerPlaying(EcsWorld? world, Entity entity) =>
        world is not null && world.IsAlive(entity) && world.Has<ModelAnimatorComponent>(entity)
        && !string.IsNullOrEmpty(world.GetRef<ModelAnimatorComponent>(entity).LayerClipName);

    /// <summary>As <see cref="Crossed"/>, for the clip started by <see cref="PlayLayer"/>.</summary>
    public static bool LayerCrossed(EcsWorld? world, Entity entity, float fraction)
    {
        if (!LayerPlaying(world, entity)) return false;
        ref ModelAnimatorComponent animator = ref world!.GetRef<ModelAnimatorComponent>(entity);
        float length = animator.LayerLengthSeconds;
        return Passed(animator.LayerLastTimeSeconds, animator.LayerTimeSeconds, Math.Clamp(fraction, 0f, 1f) * length, length, animator.LayerLoop);
    }

    // ── Looking and reaching ─────────────────────────────────────────────────

    /// <summary>
    /// Turns a bone (a head, "Head" unless named) to look at a world point, blending in over
    /// <paramref name="blendSeconds"/>, as far across and up or down as its limits allow, on top of
    /// whatever poses the model (a clip, a layer, an Animation Controller). It keeps looking until
    /// <see cref="ClearBoneLookAt"/>; call it again to move the point.
    /// </summary>
    /// <returns>False when the entity is gone or the bone name is empty.</returns>
    public static bool SetBoneLookAt(EcsWorld? world, Entity entity, string bone, Vector3 point, float weight = 1f,
        float blendSeconds = ModelBoneTargets.DefaultBlendSeconds, float yawLimitDegrees = ModelBoneTargets.DefaultYawLimit,
        float pitchLimitDegrees = ModelBoneTargets.DefaultPitchLimit)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return false;
        if (Aiming(world, entity, bone, look: true) is not { } aim) return false;
        aim.TargetInstance = 0;
        aim.Point = point;
        aim.Height = 0f;
        Shape(aim, weight, blendSeconds);
        Limits(aim, yawLimitDegrees, pitchLimitDegrees);
        aim.On = true;
        return true;
    }

    /// <summary>
    /// As <see cref="SetBoneLookAt(EcsWorld, Entity, string, Vector3, float, float, float, float)"/>,
    /// following another instance wherever it goes: one of its bones or sockets when
    /// <paramref name="targetBone"/> names one ("Head" to meet its eyes), otherwise its position
    /// raised by <paramref name="height"/>. Once the target is gone the bone keeps to where it was last.
    /// </summary>
    public static bool SetBoneLookAt(EcsWorld? world, Entity entity, string bone, Entity target, string targetBone = "",
        float height = 0f, float weight = 1f, float blendSeconds = ModelBoneTargets.DefaultBlendSeconds,
        float yawLimitDegrees = ModelBoneTargets.DefaultYawLimit, float pitchLimitDegrees = ModelBoneTargets.DefaultPitchLimit)
    {
        if (target.IsNull || Aiming(world, entity, bone, look: true) is not { } aim) return false;
        Follow(aim, target, targetBone, height);
        Shape(aim, weight, blendSeconds);
        Limits(aim, yawLimitDegrees, pitchLimitDegrees);
        aim.On = true;
        return true;
    }

    /// <summary>Lets a looking bone go back to its pose over <paramref name="blendSeconds"/>; an empty bone lets every one go.</summary>
    public static bool ClearBoneLookAt(EcsWorld? world, Entity entity, string bone = "", float blendSeconds = ModelBoneTargets.DefaultBlendSeconds) =>
        Release(world, entity, bone, blendSeconds, look: true);

    /// <summary>
    /// Bends the two bones above a bone (a hand: the forearm and upper arm) so that it reaches a world
    /// point, blending in over <paramref name="blendSeconds"/>, its lengths kept and the way it bends
    /// kept; a point out of reach is reached towards. On top of whatever poses the model. It keeps
    /// reaching until <see cref="ClearBoneReach"/>.
    /// </summary>
    public static bool SetBoneReach(EcsWorld? world, Entity entity, string bone, Vector3 point, float weight = 1f,
        float blendSeconds = ModelBoneTargets.DefaultBlendSeconds)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return false;
        if (Aiming(world, entity, bone, look: false) is not { } aim) return false;
        aim.TargetInstance = 0;
        aim.Point = point;
        aim.Height = 0f;
        Shape(aim, weight, blendSeconds);
        aim.On = true;
        return true;
    }

    /// <summary>As <see cref="SetBoneReach(EcsWorld, Entity, string, Vector3, float, float)"/>, to another instance (a bone or socket of it, or its position raised by <paramref name="height"/>) wherever it goes.</summary>
    public static bool SetBoneReach(EcsWorld? world, Entity entity, string bone, Entity target, string targetBone = "",
        float height = 0f, float weight = 1f, float blendSeconds = ModelBoneTargets.DefaultBlendSeconds)
    {
        if (target.IsNull || Aiming(world, entity, bone, look: false) is not { } aim) return false;
        Follow(aim, target, targetBone, height);
        Shape(aim, weight, blendSeconds);
        aim.On = true;
        return true;
    }

    /// <summary>Lets a reaching limb go back to its pose over <paramref name="blendSeconds"/>; an empty bone lets every one go.</summary>
    public static bool ClearBoneReach(EcsWorld? world, Entity entity, string bone = "", float blendSeconds = ModelBoneTargets.DefaultBlendSeconds) =>
        Release(world, entity, bone, blendSeconds, look: false);

    /// <summary>How much a bone's look turns it now, as blended in or out (0 to 1); 0 when it has none.</summary>
    public static float BoneLookAtAmount(EcsWorld? world, Entity entity, string bone) =>
        ModelBoneTargets.Of(world, entity, create: false)?.Look(bone)?.Amount ?? 0f;

    /// <summary>How much a bone's reach bends its limb now (0 to 1); 0 when it has none.</summary>
    public static float BoneReachAmount(EcsWorld? world, Entity entity, string bone) =>
        ModelBoneTargets.Of(world, entity, create: false)?.Reach(bone)?.Amount ?? 0f;

    /// <summary>Which way the model faces in its own space (+Z unless set), for a model built facing another way; looks are measured from it.</summary>
    public static bool SetLookForward(EcsWorld? world, Entity entity, Vector3 forward)
    {
        if (!float.IsFinite(forward.LengthSquared()) || forward.LengthSquared() < 1e-12f
            || ModelBoneTargets.Of(world, entity, create: true) is not { } targets) return false;
        targets.Forward = Vector3.Normalize(forward);
        return true;
    }

    /// <summary>The instance's looks and reaches, for reading them or setting them up in detail; null when it has none.</summary>
    public static ModelBoneTargets? BoneTargets(EcsWorld? world, Entity entity) => ModelBoneTargets.Of(world, entity, create: false);

    private static ModelBoneTargets.Aim? Aiming(EcsWorld? world, Entity entity, string bone, bool look)
    {
        if (string.IsNullOrWhiteSpace(bone) || ModelBoneTargets.Of(world, entity, create: true) is not { } targets) return null;
        return look ? targets.Look(bone, create: true) : targets.Reach(bone, create: true);
    }

    private static void Follow(ModelBoneTargets.Aim aim, Entity target, string targetBone, float height)
    {
        aim.TargetInstance = target.Id;
        aim.TargetBone = targetBone?.Trim() ?? "";
        aim.Height = float.IsFinite(height) ? Math.Clamp(height, -1000f, 1000f) : 0f;
    }

    private static void Shape(ModelBoneTargets.Aim aim, float weight, float blendSeconds)
    {
        aim.Weight = float.IsFinite(weight) ? Math.Clamp(weight, 0f, 1f) : 1f;
        aim.BlendSeconds = float.IsFinite(blendSeconds) ? Math.Clamp(blendSeconds, 0f, 10f) : ModelBoneTargets.DefaultBlendSeconds;
    }

    private static void Limits(ModelBoneTargets.Aim aim, float yawDegrees, float pitchDegrees)
    {
        aim.YawLimit = float.IsFinite(yawDegrees) ? Math.Clamp(yawDegrees, 0f, 180f) : ModelBoneTargets.DefaultYawLimit;
        aim.PitchLimit = float.IsFinite(pitchDegrees) ? Math.Clamp(pitchDegrees, 0f, 89f) : ModelBoneTargets.DefaultPitchLimit;
    }

    private static bool Release(EcsWorld? world, Entity entity, string bone, float blendSeconds, bool look)
    {
        if (ModelBoneTargets.Of(world, entity, create: false) is not { } targets) return false;
        float seconds = float.IsFinite(blendSeconds) ? Math.Clamp(blendSeconds, 0f, 10f) : ModelBoneTargets.DefaultBlendSeconds;
        if (string.IsNullOrWhiteSpace(bone))
        {
            targets.StopAll(look, seconds);
            return true;
        }
        if ((look ? targets.Look(bone) : targets.Reach(bone)) is not { } aim) return false;
        aim.BlendSeconds = seconds;
        aim.On = false;
        return true;
    }

    // ── Colour and light ─────────────────────────────────────────────────────

    /// <summary>Multiplies the whole model's colour: (1, 0.4, 0.4, 1) reddens it, an alpha below 1 fades it. (1, 1, 1, 1) is as authored.</summary>
    public static bool SetTint(EcsWorld? world, Entity entity, Vector4 tint)
    {
        if (world is null || !world.IsAlive(entity) || !world.Has<ModelRendererComponent>(entity)) return false;
        Vector4 safe = new(Channel(tint.X), Channel(tint.Y), Channel(tint.Z), Math.Clamp(float.IsFinite(tint.W) ? tint.W : 1f, 0f, 1f));
        world.GetRef<ModelRendererComponent>(entity).Tint = safe == Vector4.One ? null : safe;
        return true;

        static float Channel(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 16f) : 1f;
    }

    /// <summary>
    /// Adds the model's own colours on top of its lighting: 0 is none, 1 is fully self-lit, more
    /// is brighter still. Raise it for a few frames for a hit flash, or hold it for a selection glow.
    /// </summary>
    public static bool SetGlow(EcsWorld? world, Entity entity, float amount)
    {
        if (world is null || !world.IsAlive(entity) || !world.Has<ModelRendererComponent>(entity)) return false;
        world.GetRef<ModelRendererComponent>(entity).Glow = float.IsFinite(amount) ? Math.Clamp(amount, 0f, 32f) : 0f;
        return true;
    }

    /// <summary>
    /// Scales the light the model's materials give off as authored: 0 puts its lamps out, 1 is as
    /// authored, 2 doubles them. Fade it with the time of day to light windows at dusk.
    /// </summary>
    public static bool SetEmissionScale(EcsWorld? world, Entity entity, float scale)
    {
        if (world is null || !world.IsAlive(entity) || !world.Has<ModelRendererComponent>(entity)) return false;
        float safe = float.IsFinite(scale) ? Math.Clamp(scale, 0f, 64f) : 1f;
        world.GetRef<ModelRendererComponent>(entity).EmissionScale = safe == 1f ? null : safe;
        return true;
    }

    /// <summary>
    /// Makes one of the model's materials give off light of this strength whatever it was authored
    /// with: window glass that was dark by day. A negative strength gives the material back its
    /// authored light.
    /// </summary>
    /// <returns>False when the model has no material of that name.</returns>
    public static bool SetMaterialEmission(EcsWorld? world, Entity entity, string material, float strength)
    {
        if (string.IsNullOrWhiteSpace(material) || !TryModel(world, entity, out GModelAsset asset)) return false;
        string name = material.Trim();
        if (asset.Materials?.Exists(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) != true) return false;
        ref ModelRendererComponent model = ref world!.GetRef<ModelRendererComponent>(entity);
        if (!float.IsFinite(strength) || strength < 0f)
        {
            model.MaterialEmission?.Remove(name);
            return true;
        }

        model.MaterialEmission ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        model.MaterialEmission[name] = MathF.Min(strength, 64f);
        return true;
    }

    /// <summary>
    /// As <see cref="SetMaterialEmission(EcsWorld, Entity, string, float)"/>, in a colour: window
    /// glass that glows warm at night whatever colour the glass is by day. The material gives off
    /// its own colour multiplied by this one, so the colour is also a tint on the material while
    /// it is set. A negative strength gives the material back its authored light and colour.
    /// </summary>
    /// <returns>False when the model has no material of that name.</returns>
    public static bool SetMaterialEmission(EcsWorld? world, Entity entity, string material, float strength, Vector3 colour)
    {
        if (!SetMaterialEmission(world, entity, material, strength)) return false;
        bool lit = float.IsFinite(strength) && strength >= 0f;
        return SetMaterialTint(world, entity, material, lit ? colour : null);
    }

    /// <summary>
    /// Gives the light one of the model's materials gives off a colour on this instance only: it
    /// multiplies the material's emission image and its own glow, and leaves the surface's colour
    /// and a whole-model glow alone (one grey glow pattern in any colour). White is as the
    /// material is authored; null gives the material back its own emission colour.
    /// </summary>
    /// <returns>False when the model has no material of that name.</returns>
    public static bool SetMaterialEmissionTint(EcsWorld? world, Entity entity, string material, Vector3? colour)
    {
        if (string.IsNullOrWhiteSpace(material) || !TryModel(world, entity, out GModelAsset asset)) return false;
        string name = material.Trim();
        if (asset.Materials?.Exists(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) != true) return false;
        ref ModelRendererComponent model = ref world!.GetRef<ModelRendererComponent>(entity);
        if (colour is not Vector3 tint || !float.IsFinite(tint.LengthSquared()))
        {
            model.MaterialEmissionTints?.Remove(name);
            return true;
        }

        model.MaterialEmissionTints ??= new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        model.MaterialEmissionTints[name] = Vector3.Clamp(tint, Vector3.Zero, new Vector3(16f));
        return true;
    }

    /// <summary>
    /// Multiplies one of the model's materials by a colour on this instance only; the shared
    /// model is unchanged. Parts above 1 brighten. Null gives the material back its own colour.
    /// </summary>
    /// <returns>False when the model has no material of that name.</returns>
    public static bool SetMaterialTint(EcsWorld? world, Entity entity, string material, Vector3? tint)
    {
        if (string.IsNullOrWhiteSpace(material) || !TryModel(world, entity, out GModelAsset asset)) return false;
        string name = material.Trim();
        if (asset.Materials?.Exists(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) != true) return false;
        ref ModelRendererComponent model = ref world!.GetRef<ModelRendererComponent>(entity);
        if (tint is not Vector3 colour || !float.IsFinite(colour.LengthSquared()))
        {
            model.MaterialTints?.Remove(name);
            return true;
        }

        model.MaterialTints ??= new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        model.MaterialTints[name] = new Vector4(Vector3.Clamp(colour, Vector3.Zero, new Vector3(16f)), 1f);
        return true;
    }

    /// <summary>
    /// Gives one of the model's materials another image on this instance only: every weapon on a
    /// map its own camo from one model. <paramref name="slot"/> is <c>albedo</c>, <c>normal</c>,
    /// <c>orm</c> or <c>emission</c> for the material's own maps, or the name of a texture the
    /// material's Shader declares (its <c>Texture2D</c> at t17-t23). An empty image gives the slot
    /// back its authored image.
    /// </summary>
    /// <returns>False when the model has no material of that name or the slot is empty.</returns>
    public static bool SetMaterialTexture(EcsWorld? world, Entity entity, string material, string slot, string? image)
    {
        if (string.IsNullOrWhiteSpace(material) || string.IsNullOrWhiteSpace(slot)
            || !TryModel(world, entity, out GModelAsset asset)) return false;
        string name = material.Trim();
        if (asset.Materials?.Exists(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) != true) return false;
        string key = MaterialTextureSlots.Normalize(slot);
        ref ModelRendererComponent model = ref world!.GetRef<ModelRendererComponent>(entity);
        if (string.IsNullOrWhiteSpace(image))
        {
            if (model.MaterialTextures?.TryGetValue(name, out Dictionary<string, string>? slots) == true)
            {
                slots.Remove(key);
                if (slots.Count == 0) model.MaterialTextures.Remove(name);
            }
            return true;
        }

        model.MaterialTextures ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!model.MaterialTextures.TryGetValue(name, out Dictionary<string, string>? textures))
            model.MaterialTextures[name] = textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        textures[key] = image.Trim();
        return true;
    }
}

/// <summary>The engine's own material map slots, as <see cref="ModelInstance.SetMaterialTexture"/> names them.</summary>
public static class MaterialTextureSlots
{
    public const string Albedo = "albedo";
    public const string Normal = "normal";
    public const string Orm = "orm";
    public const string Emission = "emission";

    /// <summary>The engine slot a name means (any case, a few spellings), or the name itself for a shader's texture.</summary>
    public static string Normalize(string slot)
    {
        string trimmed = slot?.Trim() ?? string.Empty;
        return trimmed.ToLowerInvariant() switch
        {
            "albedo" or "base" or "basecolor" or "base colour" or "base color" or "color" or "colour" => Albedo,
            "normal" or "normalmap" => Normal,
            "orm" or "metallicroughness" or "metallic-roughness" => Orm,
            "emission" or "emissive" => Emission,
            _ => trimmed,
        };
    }

    /// <summary>True for the four slots the engine's own materials read.</summary>
    public static bool IsEngineSlot(string slot) =>
        string.Equals(slot, Albedo, StringComparison.Ordinal) || string.Equals(slot, Normal, StringComparison.Ordinal)
        || string.Equals(slot, Orm, StringComparison.Ordinal) || string.Equals(slot, Emission, StringComparison.Ordinal);
}
