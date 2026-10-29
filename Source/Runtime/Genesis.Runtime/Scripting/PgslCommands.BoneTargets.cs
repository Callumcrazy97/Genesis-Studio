#nullable enable
using System;
using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Looks and reaches (Engine.Animation.LookAt, Engine.Animation.Reach): this instance's head turns to
// a point or another instance within its limits, a hand reaches one, each blending in and out, on
// top of whatever animates the model. They keep going until stopped; there is no need to call them
// every frame. (AnimationBoneLookAt and AnimationBoneReach, which take a point each frame on an
// Animation Controller's bones, are unchanged.)
public static partial class PgslCommands
{
    private static bool ThisInstance(out Genesis.Runtime.ECS.World world, out Entity entity)
    {
        entity = Entity.Null;
        world = ActiveGameContext?.World!;
        var context = GetContext();
        if (context == null || world == null) return false;
        entity = world.GetEntity(context.InstanceId);
        return world.IsAlive(entity);
    }

    // Reading never gives an instance looks or reaches; setting one does.
    private static ModelBoneTargets? AimsOf(bool create) =>
        ThisInstance(out var world, out var entity) ? ModelBoneTargets.Of(world, entity, create) : null;

    private static ModelBoneTargets.Aim? LookAim(bool create) => AimsOf(create) is { } aims ? aims.Look(aims.LookBone, create) : null;

    private static ModelBoneTargets.Aim? ReachAim(bool create) => AimsOf(create) is { } aims ? aims.Reach(aims.ReachBone, create) : null;

    private static ModelBoneTargets.Aim? ReachAimOf(string bone, bool create) => AimsOf(create)?.Reach(bone, create);

    private static bool ThisHasModel() =>
        ThisInstance(out var world, out var entity) && world.Has<Genesis.Runtime.ECS.Components.ModelRendererComponent>(entity);

    // Aims at a point or an instance and turns it on; a weight or time out of range keeps the aim's own.
    private static bool AimAt(ModelBoneTargets.Aim? aim, Vector3? point, double instance, string? targetBone, double weight, double seconds)
    {
        if (aim == null) return false;
        if (point is { } at)
        {
            aim.TargetInstance = 0;
            aim.Point = at;
        }
        else
        {
            if (!double.IsFinite(instance) || instance < 1 || instance > int.MaxValue) return false;
            aim.TargetInstance = (int)instance;
            aim.TargetBone = targetBone?.Trim() ?? "";
        }
        if (double.IsFinite(weight) && weight >= 0) aim.Weight = (float)Math.Min(weight, 1);
        if (double.IsFinite(seconds) && seconds >= 0) aim.BlendSeconds = (float)Math.Min(seconds, 10);
        aim.On = true;
        return true;
    }

    private static void AimStop(ModelBoneTargets.Aim? aim, double seconds)
    {
        if (aim == null) return;
        if (double.IsFinite(seconds) && seconds >= 0) aim.BlendSeconds = (float)Math.Min(seconds, 10);
        aim.On = false;
    }

    // ── The flat commands ────────────────────────────────────────────────────────────────

    [PgslCommand("AnimationLookAtPoint", "AnimationLookAtPoint(x, y, z, weight?, seconds?) -> bool",
        "This instance's head (Engine.Animation.LookAt.Bone, \"Head\" unless set) turns to look at a world point, blending in over the seconds given (its BlendTime unless given), as far as its yaw and pitch limits allow, on top of its animation; it keeps looking until AnimationLookStop. False when this instance has no model", "Animation")]
    public static bool AnimationLookAtPoint(double x, double y, double z, double weight = 1, double seconds = -1) =>
        ThisHasModel() && Finite3(x, y, z) && AimAt(LookAim(create: true), new Vector3((float)x, (float)y, (float)z), 0, null, weight, seconds);

    [PgslCommand("AnimationLookAtInstance", "AnimationLookAtInstance(instance, targetBone?, weight?, seconds?) -> bool",
        "This instance's head follows another instance with its eyes wherever it goes: one of its bones or sockets (\"Head\" to meet its eyes), or its position raised by Engine.Animation.LookAt.Height; once it is gone, where it was last. False when this instance has no model", "Animation")]
    public static bool AnimationLookAtInstance(double instance, string targetBone = "", double weight = 1, double seconds = -1) =>
        ThisHasModel() && AimAt(LookAim(create: true), null, instance, targetBone, weight, seconds);

    [PgslCommand("AnimationLookStop", "AnimationLookStop(seconds?)", "This instance's head goes back to its animation over the seconds given (its BlendTime unless given)", "Animation")]
    public static void AnimationLookStop(double seconds = -1) => AimStop(LookAim(create: false), seconds);

    [PgslCommand("AnimationSetLookLimits", "AnimationSetLookLimits(yawDegrees, pitchDegrees)", "How far this instance's head may turn across (0 to 180 degrees, 70 unless set) and up or down (0 to 89, 40 unless set) from the way its animation faces it", "Animation")]
    public static void AnimationSetLookLimits(double yawDegrees, double pitchDegrees)
    {
        if (LookAim(create: true) is not { } aim) return;
        aim.YawLimit = Finite(yawDegrees, 0, 180, aim.YawLimit);
        aim.PitchLimit = Finite(pitchDegrees, 0, 89, aim.PitchLimit);
    }

    [PgslCommand("AnimationReachToPoint", "AnimationReachToPoint(bone, x, y, z, weight?, seconds?) -> bool",
        "This instance's limb ending in the bone (a hand: \"RightHand\") bends to bring it to a world point, blending in over the seconds given (0.25 unless given), its lengths and the way it bends kept; a point out of reach is reached towards. On top of its animation, until AnimationReachStop. False when this instance has no model", "Animation")]
    public static bool AnimationReachToPoint(string bone, double x, double y, double z, double weight = 1, double seconds = -1) =>
        ThisHasModel() && Finite3(x, y, z) && AimAt(ReachAimOf(bone, create: true), new Vector3((float)x, (float)y, (float)z), 0, null, weight, seconds);

    [PgslCommand("AnimationReachToInstance", "AnimationReachToInstance(bone, instance, targetBone?, weight?, seconds?) -> bool",
        "This instance's limb ending in the bone reaches another instance wherever it goes: one of its bones or sockets (a door's \"Handle\"), or its position raised by Engine.Animation.Reach.Height. False when this instance has no model", "Animation")]
    public static bool AnimationReachToInstance(string bone, double instance, string targetBone = "", double weight = 1, double seconds = -1) =>
        ThisHasModel() && AimAt(ReachAimOf(bone, create: true), null, instance, targetBone, weight, seconds);

    [PgslCommand("AnimationReachStop", "AnimationReachStop(bone?, seconds?)", "This instance's limb ending in the bone goes back to its animation over the seconds given (its blend time unless given); no bone, or \"\", lets every reach go", "Animation")]
    public static void AnimationReachStop(string bone = "", double seconds = -1)
    {
        if (string.IsNullOrWhiteSpace(bone))
        {
            AimsOf(create: false)?.StopAll(looks: false, double.IsFinite(seconds) && seconds >= 0 ? (float)Math.Min(seconds, 10) : -1f);
            return;
        }
        AimStop(ReachAimOf(bone, create: false), seconds);
    }

    [PgslCommand("AnimationLookAmount", "AnimationLookAmount() -> number", "How much this instance's head is turned to its target now, as blended in or out (0 to 1)", "Animation")]
    public static double AnimationLookAmount() => LookAim(create: false)?.Amount ?? 0;

    [PgslCommand("AnimationReachAmount", "AnimationReachAmount(bone) -> number", "How much this instance's limb ending in the bone reaches now, as blended in or out (0 to 1)", "Animation")]
    public static double AnimationReachAmount(string bone) => ReachAimOf(bone, create: false)?.Amount ?? 0;

    // ── Engine.Animation.LookAt: this instance's look (of the bone named in Bone) ─────────

    [PgslCommand("Enabled", "Engine.Animation.LookAt.Enabled = true", "Whether this instance's head (the Bone) looks at its target, blending in, or goes back to its animation, blending out, over BlendTime (false unless set)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static bool AnimationLookAtEnabled { get => LookAim(create: false)?.On ?? false; set { if (LookAim(create: true) is { } aim) aim.On = value; } }

    [PgslCommand("Bone", "Engine.Animation.LookAt.Bone = \"Neck\"", "The bone that looks (\"Head\" unless set); the other LookAt values belong to its look. A rig's prefix may be left off (\"Head\" finds \"mixamorig:Head\")", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static string AnimationLookAtBone
    {
        get => AimsOf(create: false)?.LookBone ?? ModelBoneTargets.DefaultLookBone;
        set { if (!string.IsNullOrWhiteSpace(value) && AimsOf(create: true) is { } aims) aims.LookBone = value.Trim(); }
    }

    [PgslCommand("Target", "Engine.Animation.LookAt.Target = other.id", "The instance looked at, wherever it goes (0, unless set, looks at the point X, Y, Z)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtTarget
    {
        get => LookAim(create: false)?.TargetInstance ?? 0;
        set { if (double.IsFinite(value) && LookAim(create: true) is { } aim) aim.TargetInstance = (int)Math.Clamp(value, 0, int.MaxValue); }
    }

    [PgslCommand("TargetBone", "Engine.Animation.LookAt.TargetBone = \"Head\"", "A bone or socket of the instance looked at (\"\" unless set: its position)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static string AnimationLookAtTargetBone { get => LookAim(create: false)?.TargetBone ?? ""; set { if (LookAim(create: true) is { } aim) aim.TargetBone = value?.Trim() ?? ""; } }

    [PgslCommand("Height", "Engine.Animation.LookAt.Height = 1.6", "Metres above the point or instance looked at, -1000 to 1000 (0 unless set): eye height on an instance with no head", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtHeight { get => LookAim(create: false)?.Height ?? 0; set { if (LookAim(create: true) is { } aim) aim.Height = Finite(value, -1000, 1000, aim.Height); } }

    [PgslCommand("X", "Engine.Animation.LookAt.X = x", "The world point looked at when there is no Target, across (0 unless set)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtX { get => LookAim(create: false)?.Point.X ?? 0; set { if (double.IsFinite(value) && LookAim(create: true) is { } aim) aim.Point = aim.Point with { X = (float)value }; } }

    [PgslCommand("Y", "Engine.Animation.LookAt.Y = y", "The world point looked at when there is no Target, up (0 unless set)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtY { get => LookAim(create: false)?.Point.Y ?? 0; set { if (double.IsFinite(value) && LookAim(create: true) is { } aim) aim.Point = aim.Point with { Y = (float)value }; } }

    [PgslCommand("Z", "Engine.Animation.LookAt.Z = z", "The world point looked at when there is no Target, along z (0 unless set)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtZ { get => LookAim(create: false)?.Point.Z ?? 0; set { if (double.IsFinite(value) && LookAim(create: true) is { } aim) aim.Point = aim.Point with { Z = (float)value }; } }

    [PgslCommand("Weight", "Engine.Animation.LookAt.Weight = 0.5", "How much of the way the head turns to its target once blended in, 0 to 1 (1 unless set)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtWeight { get => LookAim(create: false)?.Weight ?? 1; set { if (LookAim(create: true) is { } aim) aim.Weight = Finite(value, 0, 1, aim.Weight); } }

    [PgslCommand("BlendTime", "Engine.Animation.LookAt.BlendTime = seconds", "How long the look takes to blend in, out or to a new weight, 0 to 10 seconds (0.25 unless set)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtBlendTime
    {
        get => LookAim(create: false)?.BlendSeconds ?? ModelBoneTargets.DefaultBlendSeconds;
        set { if (LookAim(create: true) is { } aim) aim.BlendSeconds = Finite(value, 0, 10, aim.BlendSeconds); }
    }

    [PgslCommand("YawLimit", "Engine.Animation.LookAt.YawLimit = degrees", "How far the head may turn across from the way its animation faces it, 0 to 180 degrees (70 unless set)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtYawLimit
    {
        get => LookAim(create: false)?.YawLimit ?? ModelBoneTargets.DefaultYawLimit;
        set { if (LookAim(create: true) is { } aim) aim.YawLimit = Finite(value, 0, 180, aim.YawLimit); }
    }

    [PgslCommand("PitchLimit", "Engine.Animation.LookAt.PitchLimit = degrees", "How far the head may turn up or down from the way its animation faces it, 0 to 89 degrees (40 unless set)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtPitchLimit
    {
        get => LookAim(create: false)?.PitchLimit ?? ModelBoneTargets.DefaultPitchLimit;
        set { if (LookAim(create: true) is { } aim) aim.PitchLimit = Finite(value, 0, 89, aim.PitchLimit); }
    }

    [PgslCommand("Amount", "Engine.Animation.LookAt.Amount", "How much the head is turned to its target now, as blended in or out, 0 to 1 (read)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static double AnimationLookAtAmount => LookAim(create: false)?.Amount ?? 0;

    [PgslCommand("At", "Engine.Animation.LookAt.At(x, y, z) -> bool", "Look at a world point (and turn the look on); false when this instance has no model", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static bool AnimationLookAtAt(double x, double y, double z) =>
        ThisHasModel() && Finite3(x, y, z) && AimAt(LookAim(create: true), new Vector3((float)x, (float)y, (float)z), 0, null, -1, -1);

    [PgslCommand("Follow", "Engine.Animation.LookAt.Follow(instance, targetBone?) -> bool", "Look at another instance wherever it goes, at one of its bones or sockets when named (and turn the look on)", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static bool AnimationLookAtFollow(double instance, string targetBone = "") =>
        ThisHasModel() && AimAt(LookAim(create: true), null, instance, targetBone, -1, -1);

    [PgslCommand("Stop", "Engine.Animation.LookAt.Stop()", "Let the head go back to its animation over BlendTime", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static void AnimationLookAtStop() => AimStop(LookAim(create: false), -1);

    [PgslCommand("SetForward", "Engine.Animation.LookAt.SetForward(x, y, z)", "Which way the model faces in its own space (0, 0, 1 unless set), for a model built facing another way; looks are measured from it", "Animation", Namespace = "Engine.Animation.LookAt", Flat = false)]
    public static void AnimationLookAtSetForward(double x, double y, double z)
    {
        if (Finite3(x, y, z) && ThisInstance(out var world, out var entity)) ModelInstance.SetLookForward(world, entity, new Vector3((float)x, (float)y, (float)z));
    }

    // ── Engine.Animation.Reach: this instance's reach (of the bone named in Bone) ─────────

    [PgslCommand("Enabled", "Engine.Animation.Reach.Enabled = true", "Whether this instance's limb ending in the Bone reaches its target, blending in, or goes back to its animation, blending out, over BlendTime (false unless set)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static bool AnimationReachEnabled { get => ReachAim(create: false)?.On ?? false; set { if (ReachAim(create: true) is { } aim) aim.On = value; } }

    [PgslCommand("Bone", "Engine.Animation.Reach.Bone = \"LeftHand\"", "The bone at the end of the limb that reaches, with the two above it bending (\"RightHand\" unless set); the other Reach values belong to its reach. A rig's prefix may be left off", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static string AnimationReachBone
    {
        get => AimsOf(create: false)?.ReachBone ?? ModelBoneTargets.DefaultReachBone;
        set { if (!string.IsNullOrWhiteSpace(value) && AimsOf(create: true) is { } aims) aims.ReachBone = value.Trim(); }
    }

    [PgslCommand("Target", "Engine.Animation.Reach.Target = other.id", "The instance reached for, wherever it goes (0, unless set, reaches the point X, Y, Z)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static double AnimationReachTarget
    {
        get => ReachAim(create: false)?.TargetInstance ?? 0;
        set { if (double.IsFinite(value) && ReachAim(create: true) is { } aim) aim.TargetInstance = (int)Math.Clamp(value, 0, int.MaxValue); }
    }

    [PgslCommand("TargetBone", "Engine.Animation.Reach.TargetBone = \"Handle\"", "A bone or socket of the instance reached for (\"\" unless set: its position)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static string AnimationReachTargetBone { get => ReachAim(create: false)?.TargetBone ?? ""; set { if (ReachAim(create: true) is { } aim) aim.TargetBone = value?.Trim() ?? ""; } }

    [PgslCommand("Height", "Engine.Animation.Reach.Height = 1", "Metres above the point or instance reached for, -1000 to 1000 (0 unless set)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static double AnimationReachHeight { get => ReachAim(create: false)?.Height ?? 0; set { if (ReachAim(create: true) is { } aim) aim.Height = Finite(value, -1000, 1000, aim.Height); } }

    [PgslCommand("X", "Engine.Animation.Reach.X = x", "The world point reached for when there is no Target, across (0 unless set)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static double AnimationReachX { get => ReachAim(create: false)?.Point.X ?? 0; set { if (double.IsFinite(value) && ReachAim(create: true) is { } aim) aim.Point = aim.Point with { X = (float)value }; } }

    [PgslCommand("Y", "Engine.Animation.Reach.Y = y", "The world point reached for when there is no Target, up (0 unless set)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static double AnimationReachY { get => ReachAim(create: false)?.Point.Y ?? 0; set { if (double.IsFinite(value) && ReachAim(create: true) is { } aim) aim.Point = aim.Point with { Y = (float)value }; } }

    [PgslCommand("Z", "Engine.Animation.Reach.Z = z", "The world point reached for when there is no Target, along z (0 unless set)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static double AnimationReachZ { get => ReachAim(create: false)?.Point.Z ?? 0; set { if (double.IsFinite(value) && ReachAim(create: true) is { } aim) aim.Point = aim.Point with { Z = (float)value }; } }

    [PgslCommand("Weight", "Engine.Animation.Reach.Weight = 0.5", "How much of the way the limb reaches once blended in, 0 to 1 (1 unless set)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static double AnimationReachWeight { get => ReachAim(create: false)?.Weight ?? 1; set { if (ReachAim(create: true) is { } aim) aim.Weight = Finite(value, 0, 1, aim.Weight); } }

    [PgslCommand("BlendTime", "Engine.Animation.Reach.BlendTime = seconds", "How long the reach takes to blend in, out or to a new weight, 0 to 10 seconds (0.25 unless set)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static double AnimationReachBlendTime
    {
        get => ReachAim(create: false)?.BlendSeconds ?? ModelBoneTargets.DefaultBlendSeconds;
        set { if (ReachAim(create: true) is { } aim) aim.BlendSeconds = Finite(value, 0, 10, aim.BlendSeconds); }
    }

    [PgslCommand("Amount", "Engine.Animation.Reach.Amount", "How much the limb reaches now, as blended in or out, 0 to 1 (read)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static double AnimationReachAmountNow => ReachAim(create: false)?.Amount ?? 0;

    [PgslCommand("At", "Engine.Animation.Reach.At(x, y, z) -> bool", "Reach for a world point (and turn the reach on); false when this instance has no model", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static bool AnimationReachAt(double x, double y, double z) =>
        ThisHasModel() && Finite3(x, y, z) && AimAt(ReachAim(create: true), new Vector3((float)x, (float)y, (float)z), 0, null, -1, -1);

    [PgslCommand("Follow", "Engine.Animation.Reach.Follow(instance, targetBone?) -> bool", "Reach for another instance wherever it goes, for one of its bones or sockets when named (and turn the reach on)", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static bool AnimationReachFollow(double instance, string targetBone = "") =>
        ThisHasModel() && AimAt(ReachAim(create: true), null, instance, targetBone, -1, -1);

    [PgslCommand("Stop", "Engine.Animation.Reach.Stop()", "Let the limb go back to its animation over BlendTime", "Animation", Namespace = "Engine.Animation.Reach", Flat = false)]
    public static void AnimationReachStopNow() => AimStop(ReachAim(create: false), -1);
}
