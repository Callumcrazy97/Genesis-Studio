#nullable enable
using System;
using System.Numerics;
using Genesis.Runtime.Cameras;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;
public static partial class PgslCommands
{
    private static ThirdPersonCamera? Orbit()
    {
        var ctx = GetContext();
        var world = ActiveGameContext?.World;
        if (ctx == null || world == null) return null;
        var entity = world.GetEntity(ctx.InstanceId);
        if (!world.IsAlive(entity)) return null;
        if (!world.Has<ThirdPersonCameraComponent>(entity))
            world.Set(entity, new ThirdPersonCameraComponent { Rig = new ThirdPersonCamera() });
        return world.GetRef<ThirdPersonCameraComponent>(entity).Rig;
    }
    [PgslCommand("CameraSetOrbitDistance", "CameraSetOrbitDistance(distance)", "Attach/configure this instance's third-person spring arm", "Camera")]
    public static void CameraSetOrbitDistance(double distance) { if (Orbit() is { } rig && double.IsFinite(distance)) rig.Distance = (float)Math.Clamp(distance, .05, 1000); }
    [PgslCommand("CameraSetOrbitPitch", "CameraSetOrbitPitch(pitch)", "Set orbit pitch in degrees, clamped -25 to 75", "Camera")]
    public static void CameraSetOrbitPitch(double pitch) { if (Orbit() is { } rig && double.IsFinite(pitch)) rig.Pitch = (float)Math.Clamp(pitch, -25, 75); }
    [PgslCommand("CameraSetOrbitYaw", "CameraSetOrbitYaw(yaw)", "Set orbit yaw in degrees", "Camera")]
    public static void CameraSetOrbitYaw(double yaw) { if (Orbit() is { } rig && double.IsFinite(yaw)) rig.Yaw = (float)(yaw % 360); }
    [PgslCommand("CameraSetShoulderOffset", "CameraSetShoulderOffset(x, y, z)", "Set horizontal shoulder, height and forward offsets", "Camera")]
    public static void CameraSetShoulderOffset(double x, double y, double z) { if (Orbit() is { } rig && Finite3(x, y, z)) rig.ShoulderOffset = new Vector3((float)x, (float)y, (float)z); }
    [PgslCommand("CameraSetCollisionEnabled", "CameraSetCollisionEnabled(enabled)", "Enable camera occlusion avoidance", "Camera")]
    public static void CameraSetCollisionEnabled(bool enabled) { if (Orbit() is { } rig) rig.CollisionEnabled = enabled; }
    [PgslCommand("CameraSetOrbitMouseLook", "CameraSetOrbitMouseLook(enabled)", "Enable built-in mouse orbit; disable when scripting yaw/pitch from input", "Camera")]
    public static void CameraSetOrbitMouseLook(bool enabled) { if (Orbit() is { } rig) rig.MouseLook = enabled; }
    [PgslCommand("CameraSetOrbitEnabled", "CameraSetOrbitEnabled(enabled)", "Enable/disable this instance's orbit camera", "Camera")]
    public static void CameraSetOrbitEnabled(bool enabled) { if (Orbit() is { } rig) rig.Enabled = enabled; }
}

