#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    [PgslCommand("ModelSetMaterialTint", "ModelSetMaterialTint(material, r, g, b) -> bool",
        "Set an instance-only multiplier on an authored model material; the shared resource is unchanged", "Models")]
    public static bool ModelSetMaterialTint(string material, double r, double g, double b)
    {
        var context = GetContext();
        var world = ActiveGameContext?.World;
        if (context == null || world == null || !Finite3(r, g, b) || r < 0 || g < 0 || b < 0
            || string.IsNullOrWhiteSpace(material)) return false;
        var entity = world.GetEntity(context.InstanceId);
        if (!world.IsAlive(entity) || !world.Has<ModelRendererComponent>(entity)) return false;
        ref var component = ref world.GetRef<ModelRendererComponent>(entity);
        var asset = SocketModelAssets.Load(ProjectPath, component.ModelAsset);
        string name = material.Trim();
        if (!asset.Materials.Exists(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
        component.MaterialTints ??= new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        component.MaterialTints[name] = new((float)Math.Min(r, 16), (float)Math.Min(g, 16), (float)Math.Min(b, 16), 1);
        return true;
    }

    private static bool ThisModel(out Genesis.Runtime.ECS.World world, out Genesis.Shared.ECS.Entity entity)
    {
        entity = Genesis.Shared.ECS.Entity.Null;
        world = ActiveGameContext?.World!;
        var context = GetContext();
        if (context == null || world == null) return false;
        entity = world.GetEntity(context.InstanceId);
        return world.IsAlive(entity) && world.Has<ModelRendererComponent>(entity);
    }

    [PgslCommand("ModelSetTint", "ModelSetTint(r, g, b, a) -> bool",
        "Multiply the whole model's colour on this instance; an alpha below 1 fades it. 1, 1, 1, 1 is as authored", "Models")]
    public static bool ModelSetTint(double r, double g, double b, double a) =>
        Finite3(r, g, b) && double.IsFinite(a) && ThisModel(out var world, out var entity)
        && Genesis.Runtime.Modeling.ModelInstance.SetTint(world, entity, new Vector4((float)r, (float)g, (float)b, (float)a));

    [PgslCommand("ModelSetGlow", "ModelSetGlow(amount) -> bool",
        "Add the model's own colours on top of its lighting: 0 none, 1 fully self-lit. Raise it briefly for a hit flash", "Models")]
    public static bool ModelSetGlow(double amount) =>
        ThisModel(out var world, out var entity) && Genesis.Runtime.Modeling.ModelInstance.SetGlow(world, entity, (float)amount);

    [PgslCommand("ModelSetEmissionScale", "ModelSetEmissionScale(scale) -> bool",
        "Scale the light this model's materials give off: 0 puts its lamps out, 1 is as authored", "Models")]
    public static bool ModelSetEmissionScale(double scale) =>
        ThisModel(out var world, out var entity) && Genesis.Runtime.Modeling.ModelInstance.SetEmissionScale(world, entity, (float)scale);

    [PgslCommand("ModelSetMaterialEmission", "ModelSetMaterialEmission(material, strength) -> bool",
        "Make one of this model's materials give off light of this strength (window glass at night); a negative strength gives it back its authored light", "Models")]
    public static bool ModelSetMaterialEmission(string material, double strength) =>
        ThisModel(out var world, out var entity)
        && Genesis.Runtime.Modeling.ModelInstance.SetMaterialEmission(world, entity, material, (float)strength);

    [PgslCommand("ModelSetMaterialEmissionColor", "ModelSetMaterialEmissionColor(material, strength, r, g, b) -> bool",
        "Make one of this model's materials give off light of this strength in this colour (window glass glowing warm at night); a negative strength gives it back its authored light and colour", "Models")]
    public static bool ModelSetMaterialEmissionColor(string material, double strength, double r, double g, double b) =>
        Finite3(r, g, b) && ThisModel(out var world, out var entity)
        && Genesis.Runtime.Modeling.ModelInstance.SetMaterialEmission(world, entity, material, (float)strength,
            new Vector3((float)r, (float)g, (float)b));

    [PgslCommand("ModelSetMeshVisible", "ModelSetMeshVisible(mesh, visible) -> bool",
        "Show or hide a named mesh on this instance without changing the shared model", "Models")]
    public static bool ModelSetMeshVisible(string mesh, bool visible)
    {
        var context = GetContext();
        var world = ActiveGameContext?.World;
        if (context == null || world == null || string.IsNullOrWhiteSpace(mesh)) return false;
        var entity = world.GetEntity(context.InstanceId);
        if (!world.IsAlive(entity) || !world.Has<ModelRendererComponent>(entity)) return false;
        ref var component = ref world.GetRef<ModelRendererComponent>(entity);
        var asset = SocketModelAssets.Load(ProjectPath, component.ModelAsset);
        string name = mesh.Trim();
        if (!asset.Meshes.Exists(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
        component.HiddenMeshes ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (visible) component.HiddenMeshes.Remove(name); else component.HiddenMeshes.Add(name);
        return true;
    }
}
