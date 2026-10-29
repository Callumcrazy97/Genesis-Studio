using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Physics;

public sealed partial class PhysicsWorld
{
    /// <summary>Whether bodies float in and are slowed by the room's water (Engine.Water.Enabled; true unless set).</summary>
    public bool WaterEnabled { get; set; } = true;

    /// <summary>Whether water acts on bodies' swimming at all (Engine.Water.Swimming.Enabled; true unless set); off, it neither lifts nor slows them and the scales wait.</summary>
    public bool WaterSwimmingEnabled { get; set; } = true;

    /// <summary>How strongly water lifts bodies, times each water's own (Engine.Water.Swimming.Buoyancy; 1 unless set).</summary>
    public float WaterBuoyancyScale { get; set; } = 1f;

    /// <summary>How strongly water slows bodies, times each water's own (Engine.Water.Swimming.Drag; 1 unless set).</summary>
    public float WaterDragScale { get; set; } = 1f;

    /// <summary>Applies authored buoyancy, drag and flow to all registered dynamic ECS bodies.</summary>
    public void ApplyWater(IEcsWorld world, IReadOnlyList<PhysicsWaterVolume> volumes, float dt)
    {
        if (!WaterEnabled || !WaterSwimmingEnabled || world == null || volumes == null || volumes.Count == 0 || dt <= 0f)
            return;
        float lift = MathF.Max(0f, WaterBuoyancyScale), slow = MathF.Max(0f, WaterDragScale);
        Vector3 gravity = GetActiveGravityAcceleration();
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
            // By index: foreach over IReadOnlyList boxed an enumerator per body per step.
            for (int v = 0; v < volumes.Count; v++)
            {
                PhysicsWaterVolume volume = volumes[v];
                float candidate = volume.SubmergedFraction(position, rigid.HalfExtents);
                if (candidate > fraction) { selected = volume; fraction = candidate; }
            }
            if (selected == null || fraction <= 0f) continue;

            float drag = MathF.Exp(-MathF.Max(0f, selected.LinearDrag) * slow * fraction * dt);
            Vector3 velocity = reference.Velocity.Linear * drag;
            float densityScale = MathF.Max(0f, selected.Density) / 1000f;
            velocity += -gravity * MathF.Max(0f, selected.Buoyancy) * lift * densityScale * fraction * dt;
            velocity += (selected.FlowVelocity - velocity) * Math.Clamp(fraction * dt, 0f, 1f);
            reference.Velocity.Linear = velocity;
            reference.Velocity.Angular *= MathF.Exp(-MathF.Max(0f, selected.AngularDrag) * slow * fraction * dt);
            reference.Awake = true;
        }
    }
}
