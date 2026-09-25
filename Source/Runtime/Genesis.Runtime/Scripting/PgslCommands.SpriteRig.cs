#nullable enable
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Imaging;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    private sealed class RigError { public string Message = ""; }
    private static readonly ConditionalWeakTable<PgslContext, RigError> RigErrors = new();
    private static Entity RigCaller => ActiveGameContext?.World?.GetEntity(GetContext()?.InstanceId ?? -1) ?? Entity.Null;
    private static PixelRigPlayer? CallerRig => SpriteRigRuntime.Get(ActiveGameContext?.World, RigCaller);
    private static void SetRigError(string message)
    {
        PgslContext? context = GetContext();
        if (context is not null) RigErrors.GetValue(context, _ => new RigError()).Message = message;
    }
    private static bool RigResult(bool success, string error)
    { SetRigError(success ? "" : error); return success; }

    [PgslCommand("SpriteRigBind", "SpriteRigBind(image,rig?) -> bool", "Bind an Image Editor pixel rig to the calling live object; preserves other instances and source artwork", "Sprite rig")]
    public static bool SpriteRigBind(string image, string rig = "")
    {
        if (ActiveGameContext?.World is not { } world || GetContext() is not { } context)
            return RigResult(false, "SpriteRigBind requires a live object event.");
        bool success = SpriteRigRuntime.Bind(world, RigCaller, ProjectPath, image, rig, out string error);
        if (success) context.SpriteIndex = image; // Collision metadata remains the authored image mask.
        return RigResult(success, error);
    }

    [PgslCommand("SpriteRigClear", "SpriteRigClear()", "Release this object's rig and resume normal sprite-frame drawing", "Sprite rig")]
    public static void SpriteRigClear() { SpriteRigRuntime.Clear(ActiveGameContext?.World, RigCaller); SetRigError(""); }

    [PgslCommand("SpriteRigIsBound", "SpriteRigIsBound() -> bool", "Whether this object has a live pixel-rig player", "Sprite rig")]
    public static bool SpriteRigIsBound() => CallerRig is not null;

    [PgslCommand("SpriteRigError", "SpriteRigError() -> string", "Last rig API error for this object; empty after a successful operation", "Sprite rig")]
    public static string SpriteRigError() => GetContext() is { } ctx && RigErrors.TryGetValue(ctx, out RigError? error) ? error.Message : "";

    [PgslCommand("SpriteRigPose", "SpriteRigPose(pose) -> bool", "Select a saved Image Editor pose by name or id, without baking or editing the source image", "Sprite rig")]
    public static bool SpriteRigPose(string pose) => RigResult(CallerRig?.SetPose(pose) == true, "Bind a rig containing this pose first.");

    [PgslCommand("SpriteRigBoneRotate", "SpriteRigBoneRotate(bone,degrees,followChildren?) -> bool", "Set an absolute local bone rotation offset from the sampled pose; shared joints remain connected", "Sprite rig")]
    public static bool SpriteRigBoneRotate(string bone, double degrees, bool followChildren = true) =>
        RigResult(CallerRig?.RotateBone(bone, degrees, followChildren) == true, "Bind a rig containing this bone and use a finite angle.");

    [PgslCommand("SpriteRigAimLocal", "SpriteRigAimLocal(bone,x,y,limit?,followChildren?) -> bool", "Aim a bone at image-local pixels, clamped around its authored direction", "Sprite rig")]
    public static bool SpriteRigAimLocal(string bone, double x, double y, double limit = 35, bool followChildren = true) =>
        RigResult(CallerRig?.AimBone(bone, x, y, limit, followChildren) == true, "A bound bone and finite local aim target are required.");

    [PgslCommand("SpriteRigAim", "SpriteRigAim(bone,worldX,worldY,limit?,followChildren?) -> bool", "Aim in room coordinates, respecting this object's origin, rotation and mirrored scale", "Sprite rig")]
    public static bool SpriteRigAim(string bone, double worldX, double worldY, double limit = 35, bool followChildren = true)
    {
        PgslContext? context = GetContext();
        var world = ActiveGameContext?.World;
        Entity entity = RigCaller;
        if (context is null || world is null || !world.IsAlive(entity) || !world.Has<PixelRigSpriteComponent>(entity)
            || !double.IsFinite(worldX) || !double.IsFinite(worldY)) return RigResult(false, "A bound live object and finite world target are required.");
        PixelRigSprite? binding = world.GetRef<PixelRigSpriteComponent>(entity).Binding;
        if (binding is null || !double.IsFinite(context.ImageAngle) || !double.IsFinite(context.ImageXScale)
            || !double.IsFinite(context.ImageYScale) || Math.Abs(context.ImageXScale) < .000001 || Math.Abs(context.ImageYScale) < .000001)
            return RigResult(false, "Cannot aim a missing rig or a zero/non-finite sprite scale.");
        Vector2 delta = Vector2.Transform(new Vector2((float)(worldX-context.X),(float)(worldY-context.Y)),
            Matrix3x2.CreateRotation((float)(-context.ImageAngle*Math.PI/180)));
        return SpriteRigAimLocal(bone, delta.X/context.ImageXScale+binding.OriginX,
            delta.Y/context.ImageYScale+binding.OriginY, limit, followChildren);
    }

    [PgslCommand("SpriteRigJointMove", "SpriteRigJointMove(joint,x,y) -> bool", "Move an unpinned joint in image-local pixels using the editor's connected-joint solver", "Sprite rig")]
    public static bool SpriteRigJointMove(string joint, double x, double y) =>
        RigResult(CallerRig?.MoveJoint(joint, x, y) == true, "Bind an unpinned joint and supply finite local coordinates.");

    [PgslCommand("SpriteRigInfill", "SpriteRigInfill(enabled) -> bool", "Enable the editor's source-alpha-aware joint-gap infill; never invents new artwork", "Sprite rig")]
    public static bool SpriteRigInfill(bool enabled)
    {
        if (CallerRig is not { } rig) return RigResult(false, "Bind a pixel rig first.");
        rig.FillJointGaps = enabled; return RigResult(true, "");
    }

    [PgslCommand("SpriteRigPlay", "SpriteRigPlay(animation,speed?,loop?,restart?) -> bool", "Play saved rig keyframes at authored FPS; repeat calls do not restart unless requested; loop -1 uses authored setting", "Sprite rig")]
    public static bool SpriteRigPlay(string animation, double speed = 1, int loop = -1, bool restart = false) =>
        RigResult(CallerRig?.Play(animation, speed, loop < 0 ? null : loop != 0, restart) == true, "Bind a rig containing keyed animation; speed must be >0 and <=16.");

    [PgslCommand("SpriteRigStop", "SpriteRigStop()", "Pause rig playback at its current pose", "Sprite rig")]
    public static void SpriteRigStop() => CallerRig?.Stop();

    [PgslCommand("SpriteRigFrame", "SpriteRigFrame() -> number", "Current authored rig animation frame (1-based)", "Sprite rig")]
    public static double SpriteRigFrame() => CallerRig?.Frame ?? 0;

    [PgslCommand("SpriteRigSeek", "SpriteRigSeek(frame) -> bool", "Seek in the current rig animation (1-based)", "Sprite rig")]
    public static bool SpriteRigSeek(int frame) => RigResult(CallerRig?.Seek(frame) == true, "Start a rig animation and provide a positive frame first.");

    [PgslCommand("SpriteRigReset", "SpriteRigReset()", "Return to the saved bind pose and clear procedural controls", "Sprite rig")]
    public static void SpriteRigReset() => CallerRig?.Reset();

    [PgslCommand("SpriteRigClearControls", "SpriteRigClearControls()", "Clear aim/joint overrides while retaining the authored animation or selected pose", "Sprite rig")]
    public static void SpriteRigClearControls() => CallerRig?.ClearControls();
}
