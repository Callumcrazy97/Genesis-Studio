#nullable enable
using System;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>Named, model-authored attachment points. Scripts decide what is attached and why.</summary>
public static partial class PgslCommands
{
    private static RuntimeModelAssetRegistry SocketModelAssets => RuntimeModelAssetRegistry.Shared;

    [PgslCommand("ModelSocketAttach", "ModelSocketAttach(childInstance, socket) -> bool",
        "Attach another live instance to one of this model's named sockets", "Models")]
    public static bool ModelSocketAttach(double childInstance, string socket) =>
        AttachToSocket(childInstance, socket, Matrix4x4.Identity);

    [PgslCommand("ModelSocketAttachOffset", "ModelSocketAttachOffset(childInstance, socket, x, y, z, pitch, yaw, roll, scale) -> bool",
        "Attach another live instance with a local position, rotation and uniform scale offset", "Models")]
    public static bool ModelSocketAttachOffset(
        double childInstance,
        string socket,
        double x,
        double y,
        double z,
        double pitch,
        double yaw,
        double roll,
        double scale)
    {
        if (!Finite3(x, y, z) || !Finite3(pitch, yaw, roll) || !double.IsFinite(scale)) return false;
        float safeScale = (float)Math.Clamp(scale, .0001, 10000);
        Matrix4x4 offset = Matrix4x4.CreateScale(safeScale)
            * Matrix4x4.CreateFromYawPitchRoll(Degrees(yaw), Degrees(pitch), Degrees(roll))
            * Matrix4x4.CreateTranslation((float)x, (float)y, (float)z);
        return AttachToSocket(childInstance, socket, offset);
    }

    [PgslCommand("ModelSocketDetach", "ModelSocketDetach(childInstance)",
        "Stop keeping another instance on this model's socket", "Models")]
    public static void ModelSocketDetach(double childInstance)
    {
        var world = ActiveGameContext?.World;
        if (world is null || !Instance(childInstance, world, out Entity child)
            || !world.Has<ModelSocketAttachmentComponent>(child)) return;
        ref ModelSocketAttachmentComponent attachment = ref world.GetRef<ModelSocketAttachmentComponent>(child);
        attachment.Enabled = false;
    }

    private static Vector3 SocketPosition(string socket, out bool found)
    {
        found = false;
        PgslContext? context = GetContext();
        var world = ActiveGameContext?.World;
        if (context is null || world is null) return Vector3.Zero;
        found = ModelInstance.TryGetSocketPosition(world, world.GetEntity(context.InstanceId), socket, out Vector3 position);
        return position;
    }

    [PgslCommand("ModelSocketExists", "ModelSocketExists(socket) -> bool",
        "True when this model has a socket, or a bone, of this name", "Models")]
    public static bool ModelSocketExists(string socket)
    {
        SocketPosition(socket, out bool found);
        return found;
    }

    [PgslCommand("ModelSocketX", "ModelSocketX(socket) -> number",
        "World X of one of this model's sockets (or bones) in the pose it is drawn in; 0 when it has none of that name", "Models")]
    public static double ModelSocketX(string socket) => SocketPosition(socket, out _).X;

    [PgslCommand("ModelSocketY", "ModelSocketY(socket) -> number", "World Y of one of this model's sockets or bones", "Models")]
    public static double ModelSocketY(string socket) => SocketPosition(socket, out _).Y;

    [PgslCommand("ModelSocketZ", "ModelSocketZ(socket) -> number", "World Z of one of this model's sockets or bones", "Models")]
    public static double ModelSocketZ(string socket) => SocketPosition(socket, out _).Z;

    private static bool AttachToSocket(double childInstance, string socket, Matrix4x4 offset)
    {
        PgslContext? context = GetContext();
        var world = ActiveGameContext?.World;
        if (context is null || world is null || string.IsNullOrWhiteSpace(socket)
            || !Instance(childInstance, world, out Entity child)) return false;
        Entity parent = world.GetEntity(context.InstanceId);
        if (!world.IsAlive(parent) || parent == child) return false;

        string model = world.Has<ModelRendererComponent>(parent)
            ? world.GetRef<ModelRendererComponent>(parent).ModelAsset
            : context.ModelAsset;
        if (string.IsNullOrWhiteSpace(model)) return false;
        GModelAsset asset = SocketModelAssets.Load(ProjectPath, model);
        string name = socket.Trim();
        if (asset.Sockets?.Exists(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) != true) return false;

        world.Set(child, new ModelSocketAttachmentComponent
        {
            Enabled = true,
            ParentEntityId = parent.Id,
            SocketName = name,
            LocalOffset = offset,
        });
        return true;
    }

    private static bool Instance(double id, Genesis.Runtime.ECS.World world, out Entity entity)
    {
        entity = Entity.Null;
        if (!double.IsFinite(id) || id < 0 || id > int.MaxValue || id != Math.Truncate(id)) return false;
        entity = world.GetEntity((int)id);
        return world.IsAlive(entity);
    }

    private static float Degrees(double value) => (float)(value * (Math.PI / 180d));
}
