using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    /// <summary>Applies authored buoyancy, drag and flow to all registered dynamic ECS bodies.</summary>
    public void ApplyWater(IEcsWorld world, IReadOnlyList<PhysicsWaterVolume> volumes, float dt)
    {
        if (world == null || volumes == null || volumes.Count == 0 || dt <= 0f)
            return;
        Vector3 gravity = GetGravityAcceleration();
        foreach ((int _, BodyBinding binding) in EnumerateDynamicBindings())
        {
            if (binding.DynamicHandle is not BepuPhysics.BodyHandle handle || !binding.Enabled ||
                !world.Has<RigidBodyComponent>(binding.Entity))
                continue;
            BepuPhysics.BodyReference reference = _simulation.Bodies.GetBodyReference(handle);
            ref RigidBodyComponent rigid = ref world.GetRef<RigidBodyComponent>(binding.Entity);
            Vector3 position = reference.Pose.Position;
            PhysicsWaterVolume? selected = null;
            float fraction = 0f;
            foreach (PhysicsWaterVolume volume in volumes)
            {
                float candidate = volume.SubmergedFraction(position, rigid.HalfExtents);
                if (candidate > fraction) { selected = volume; fraction = candidate; }
            }
            if (selected == null || fraction <= 0f) continue;

            float drag = MathF.Exp(-MathF.Max(0f, selected.LinearDrag) * fraction * dt);
            Vector3 velocity = reference.Velocity.Linear * drag;
            float densityScale = MathF.Max(0f, selected.Density) / 1000f;
            velocity += -gravity * MathF.Max(0f, selected.Buoyancy) * densityScale * fraction * dt;
            velocity += (selected.FlowVelocity - velocity) * Math.Clamp(fraction * dt, 0f, 1f);
            reference.Velocity.Linear = velocity;
            reference.Velocity.Angular *= MathF.Exp(-MathF.Max(0f, selected.AngularDrag) * fraction * dt);
            reference.Awake = true;
        }
    }
}
