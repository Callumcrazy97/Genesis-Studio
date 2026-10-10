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

    [PgslCommand("ModelNodeSetRotation", "ModelNodeSetRotation(node, pitch, yaw, roll) -> bool",
        "Turn one of this instance's model nodes (a propeller, a gun's bolt) by degrees from its authored pose; the nodes below it follow. False when the model has no node of that name",
        "Models")]
    public static bool ModelNodeSetRotation(string node, double pitch, double yaw, double roll)
    {
        if (!Finite3(pitch, yaw, roll) || !ThisModelNode(node, out var pose, out var poses, out string name)) return false;
        const double radians = Math.PI / 180;
        pose.Rotation = Quaternion.CreateFromYawPitchRoll(
            (float)(yaw % 360 * radians), (float)(pitch % 360 * radians), (float)(roll % 360 * radians));
        poses[name] = pose;
        return true;
    }

    [PgslCommand("ModelNodeSetTranslation", "ModelNodeSetTranslation(node, x, y, z) -> bool",
        "Move one of this instance's model nodes (a slide, a magazine) by an offset in its parent's space, model units", "Models")]
    public static bool ModelNodeSetTranslation(string node, double x, double y, double z)
    {
        if (!Finite3(x, y, z) || !ThisModelNode(node, out var pose, out var poses, out string name)) return false;
        pose.Translation = new Vector3((float)x, (float)y, (float)z);
        poses[name] = pose;
        return true;
    }

    [PgslCommand("ModelNodeClear", "ModelNodeClear(node)", "Return one of this instance's model nodes to its authored pose", "Models")]
    public static void ModelNodeClear(string node)
    {
        if (NodeModel(out var world, out var entity) && !string.IsNullOrWhiteSpace(node))
            world.GetRef<ModelRendererComponent>(entity).NodePoses?.Remove(node.Trim());
    }

    [PgslCommand("InstanceModelNodeSetRotation", "InstanceModelNodeSetRotation(id, node, pitch, yaw, roll) -> bool",
        "Turn one of another instance's model nodes by degrees from its authored pose", "Models")]
    public static bool InstanceModelNodeSetRotation(double id, string node, double pitch, double yaw, double roll) =>
        ForNodeTarget(id, () => ModelNodeSetRotation(node, pitch, yaw, roll));

    [PgslCommand("InstanceModelNodeSetTranslation", "InstanceModelNodeSetTranslation(id, node, x, y, z) -> bool",
        "Move one of another instance's model nodes by an offset in its parent's space", "Models")]
    public static bool InstanceModelNodeSetTranslation(double id, string node, double x, double y, double z) =>
        ForNodeTarget(id, () => ModelNodeSetTranslation(node, x, y, z));

    [PgslCommand("InstanceModelNodeClear", "InstanceModelNodeClear(id, node)", "Return one of another instance's model nodes to its authored pose", "Models")]
    public static void InstanceModelNodeClear(double id, string node) =>
        ForNodeTarget(id, () => { ModelNodeClear(node); return true; });

    // The instance the node commands act on when given an id (0: the calling instance).
    [ThreadStatic] private static int _nodeTarget;

    private static bool ForNodeTarget(double id, Func<bool> action)
    {
        if (!double.IsFinite(id) || id < 1 || id > int.MaxValue) return false;
        int previous = _nodeTarget;
        _nodeTarget = (int)id;
        try { return action(); }
        finally { _nodeTarget = previous; }
    }

    private static bool NodeModel(out Genesis.Runtime.ECS.World world, out Genesis.Shared.ECS.Entity entity)
    {
        if (_nodeTarget <= 0) return ThisModel(out world, out entity);
        world = ActiveGameContext?.World!;
        entity = world != null ? world.GetEntity(_nodeTarget) : Genesis.Shared.ECS.Entity.Null;
        return world != null && world.IsAlive(entity) && world.Has<ModelRendererComponent>(entity);
    }

    private static bool ThisModelNode(string node, out Genesis.Runtime.Modeling.ModelNodePose pose,
        out Dictionary<string, Genesis.Runtime.Modeling.ModelNodePose> poses, out string name)
    {
        pose = Genesis.Runtime.Modeling.ModelNodePose.Identity;
        poses = null!;
        name = node?.Trim() ?? string.Empty;
        if (name.Length == 0 || !NodeModel(out var world, out var entity)) return false;
        ref var component = ref world.GetRef<ModelRendererComponent>(entity);
        var asset = SocketModelAssets.Load(ProjectPath, component.ModelAsset);
        string wanted = name;
        if (asset?.Nodes == null || !asset.Nodes.Exists(n => string.Equals(n.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            return false;
        component.NodePoses ??= new Dictionary<string, Genesis.Runtime.Modeling.ModelNodePose>(StringComparer.OrdinalIgnoreCase);
        poses = component.NodePoses;
        if (poses.TryGetValue(name, out var existing)) pose = existing;
        return true;
    }

    [PgslCommand("ModelSetCastShadows", "ModelSetCastShadows(enabled) -> bool",
        "Whether this instance's model casts a shadow: a first-person weapon or arms should not", "Models")]
    public static bool ModelSetCastShadows(bool enabled) =>
        ThisModel(out var world, out var entity) && SetCastShadows(world, entity, enabled);

    [PgslCommand("InstanceSetCastShadows", "InstanceSetCastShadows(id, enabled) -> bool",
        "Whether another instance's model casts a shadow", "Models")]
    public static bool InstanceSetCastShadows(double id, bool enabled)
    {
        var world = ActiveGameContext?.World;
        if (world == null || !double.IsFinite(id) || id < 1 || id > int.MaxValue) return false;
        var entity = world.GetEntity((int)id);
        return world.IsAlive(entity) && world.Has<ModelRendererComponent>(entity) && SetCastShadows(world, entity, enabled);
    }

    private static bool SetCastShadows(Genesis.Runtime.ECS.World world, Genesis.Shared.ECS.Entity entity, bool enabled)
    {
        world.GetRef<ModelRendererComponent>(entity).CastShadows = enabled;
        return true;
    }

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

    [PgslCommand("ModelSetMaterialTexture", "ModelSetMaterialTexture(material, slot, image) -> bool",
        "Give one of this model's materials another Image on this instance only (each weapon its own camo). Slot: albedo, normal, orm, emission, or a texture the material's Shader declares. An empty image gives the slot back its own",
        "Models")]
    public static bool ModelSetMaterialTexture(string material, string slot, string image) =>
        ThisModel(out var world, out var entity)
        && Genesis.Runtime.Modeling.ModelInstance.SetMaterialTexture(world, entity, material, slot, image);

    [PgslCommand("InstanceSetMaterialTexture", "InstanceSetMaterialTexture(id, material, slot, image) -> bool",
        "Give one of another instance's model materials another Image on that instance only", "Models")]
    public static bool InstanceSetMaterialTexture(double id, string material, string slot, string image)
    {
        var world = ActiveGameContext?.World;
        if (world == null || !double.IsFinite(id) || id < 1 || id > int.MaxValue) return false;
        var entity = world.GetEntity((int)id);
        return Genesis.Runtime.Modeling.ModelInstance.SetMaterialTexture(world, entity, material, slot, image);
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
