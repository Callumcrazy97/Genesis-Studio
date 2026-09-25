#nullable enable
using System;
using System.Collections.Generic;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>Data-driven model morph controls. Target names are supplied by the imported model.</summary>
public static partial class PgslCommands
{
    [PgslCommand("ModelMorphSetWeight", "ModelMorphSetWeight(target, weight)",
        "Set a named morph-target weight on this model instance", "Models")]
    public static void ModelMorphSetWeight(string target, double weight)
    {
        if (!TryMorph(out var world, out Entity entity, out ModelMorphComponent component)
            || string.IsNullOrWhiteSpace(target) || !double.IsFinite(weight)) return;

        component.Enabled = true;
        component.Weights ??= new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        component.Weights[target.Trim()] = (float)Math.Clamp(weight, -8d, 8d);
        world.Set(entity, component);
    }

    [PgslCommand("ModelMorphGetWeight", "ModelMorphGetWeight(target) -> number",
        "Read a named morph-target weight from this model instance", "Models")]
    public static double ModelMorphGetWeight(string target)
    {
        if (!TryMorph(out _, out _, out ModelMorphComponent component)
            || !component.Enabled || component.Weights is null || string.IsNullOrWhiteSpace(target)) return 0;
        return component.Weights.TryGetValue(target.Trim(), out float value) ? value : 0;
    }

    [PgslCommand("ModelMorphReset", "ModelMorphReset(target)",
        "Remove this instance's override for one named morph target", "Models")]
    public static void ModelMorphReset(string target)
    {
        if (!TryMorph(out var world, out Entity entity, out ModelMorphComponent component)
            || component.Weights is null || string.IsNullOrWhiteSpace(target)) return;
        component.Weights.Remove(target.Trim());
        world.Set(entity, component);
    }

    [PgslCommand("ModelMorphResetAll", "ModelMorphResetAll()",
        "Remove every per-instance morph-target override", "Models")]
    public static void ModelMorphResetAll()
    {
        if (!TryMorph(out var world, out Entity entity, out ModelMorphComponent component)) return;
        component.Weights?.Clear();
        world.Set(entity, component);
    }

    [PgslCommand("ModelMorphSetEnabled", "ModelMorphSetEnabled(enabled)",
        "Enable or disable all per-instance morph-target overrides", "Models")]
    public static void ModelMorphSetEnabled(bool enabled)
    {
        if (!TryMorph(out var world, out Entity entity, out ModelMorphComponent component)) return;
        component.Enabled = enabled;
        world.Set(entity, component);
    }

    private static bool TryMorph(
        out Genesis.Runtime.ECS.World world,
        out Entity entity,
        out ModelMorphComponent component)
    {
        world = ActiveGameContext?.World!;
        PgslContext? context = GetContext();
        entity = world is null || context is null ? Entity.Null : world.GetEntity(context.InstanceId);
        if (world is null || !world.IsAlive(entity))
        {
            component = default;
            return false;
        }

        component = world.Has<ModelMorphComponent>(entity)
            ? world.GetRef<ModelMorphComponent>(entity)
            : new ModelMorphComponent
            {
                Enabled = true,
                Weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase),
            };
        return true;
    }
}
