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
