#nullable enable
using System;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Cameras;

public struct ThirdPersonCameraComponent : IComponent
{
    public ThirdPersonCamera Rig;
}

/// <summary>Angles are authored in degrees. Conversion to the engine's radian camera happens once.</summary>
public sealed class ThirdPersonCamera
{
    public float Distance { get; set; } = 7.5f;
    public float Yaw { get; set; }
    public float Pitch { get; set; } = 15;
    public Vector3 ShoulderOffset { get; set; } = new(1.2f, 2.4f, 0);
    public bool CollisionEnabled { get; set; } = true;
    public bool MouseLook { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public float Sensitivity { get; set; } = .15f;
    public float Clearance { get; set; } = .25f;
    public float RecoverySpeed { get; set; } = 10;
    public float CurrentDistance { get; private set; } = -1;
    public Vector3 Position { get; private set; }

    public void Update(Vector3 target, Vector2 look, float dt, Func<Vector3, Vector3, float, float>? raycast = null)
    {
        if (!Enabled || !float.IsFinite(dt) || dt < 0) return;
        if (MouseLook) { Yaw -= look.X * Sensitivity; Pitch -= look.Y * Sensitivity; }
        Yaw = float.IsFinite(Yaw) ? Yaw % 360 : 0;
        Pitch = float.IsFinite(Pitch) ? Math.Clamp(Pitch, -25, 75) : 15;
        float yaw = Yaw * MathF.PI / 180, pitch = Pitch * MathF.PI / 180;
        Vector3 forward = Conventions.DirectionFromYawPitch(yaw, pitch);
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Conventions.DirectionFromYawPitch(yaw, 0)));
        Vector3 head = target + Vector3.UnitY * ShoulderOffset.Y;
        Vector3 pivot = head + right * ShoulderOffset.X + Conventions.DirectionFromYawPitch(yaw, 0) * ShoulderOffset.Z;
        // Also constrain the shoulder offset, otherwise the boom could begin through a wall.
        if (CollisionEnabled && raycast != null)
        {
            Vector3 shoulder = pivot - head;
            float length = shoulder.Length();
            if (length > 1e-5f)
            {
                float hit = raycast(head, shoulder / length, length);
                if (hit >= 0) pivot = head + shoulder / length * MathF.Max(0, hit - Clearance);
            }
        }
        float desired = float.IsFinite(Distance) ? Math.Clamp(Distance, .05f, 1000) : 7.5f;
        float safe = desired;
        if (CollisionEnabled && raycast != null)
        {
            // A center ray plus near-plane probes protects the camera's visible corners.
            Vector3 up = Vector3.Normalize(Vector3.Cross(forward, right));
            foreach (Vector3 offset in new[] { Vector3.Zero, right * Clearance, -right * Clearance, up * Clearance, -up * Clearance })
            {
                float hit = raycast(pivot + offset, -forward, desired);
                if (hit >= 0) safe = MathF.Min(safe, MathF.Max(0, hit - Clearance));
            }
        }
        if (CurrentDistance < 0 || safe < CurrentDistance) CurrentDistance = safe;
        else CurrentDistance += (safe - CurrentDistance) * (1 - MathF.Exp(-MathF.Max(0, RecoverySpeed) * dt));
        Position = pivot - forward * CurrentDistance;
    }

    public static void UpdateScene(RuntimeScene scene, float dt)
    {
        bool chosen = false;
        scene.World.Query<TransformComponent, ThirdPersonCameraComponent>((entity, ref transform, ref component) =>
        {
            if (chosen || component.Rig is not { Enabled: true } rig) return;
            chosen = true;
            rig.Update(new(transform.X, transform.Y, transform.Z), scene.Input?.LookDelta ?? Vector2.Zero, dt,
                (origin, direction, length) => scene.Physics?.Raycast(scene.World, origin, direction, length,
                    out var hit, entity) == true ? hit.Distance : -1);
            scene.Camera3D.Position = rig.Position;
            scene.Camera3D.Yaw = rig.Yaw * MathF.PI / 180;
            scene.Camera3D.Pitch = rig.Pitch * MathF.PI / 180;
        });
    }
}

