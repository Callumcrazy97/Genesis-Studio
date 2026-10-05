using System;
using System.Drawing;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// The first-person layer and models in the GUI (see ModelLayers).
public static partial class PgslCommands
{
    [PgslCommand("ModelSetViewLayer", "ModelSetViewLayer(enabled) -> bool",
        "Draw this instance's model in the first-person layer: over the world with its own field of view and near plane, never inside a wall, casting no shadow (arms, a held weapon)",
        "Models")]
    public static bool ModelSetViewLayer(bool enabled) =>
        ThisModel(out var world, out var entity) && SetViewLayer(world, entity, enabled);

    [PgslCommand("InstanceSetViewLayer", "InstanceSetViewLayer(id, enabled) -> bool", "Draw another instance's model in the first-person layer", "Models")]
    public static bool InstanceSetViewLayer(double id, bool enabled)
    {
        var world = ActiveGameContext?.World;
        if (world == null || !double.IsFinite(id) || id < 1 || id > int.MaxValue) return false;
        var entity = world.GetEntity((int)id);
        return world.IsAlive(entity) && world.Has<ModelRendererComponent>(entity) && SetViewLayer(world, entity, enabled);
    }

    private static bool SetViewLayer(Genesis.Runtime.ECS.World world, Genesis.Shared.ECS.Entity entity, bool enabled)
    {
        world.GetRef<ModelRendererComponent>(entity).ViewLayer = enabled;
        return true;
    }

    [PgslCommand("ViewLayerSetFov", "ViewLayerSetFov(degrees)",
        "The first-person layer's vertical field of view (0 = the camera's), so zooming the world does not zoom the weapon", "Camera")]
    public static void ViewLayerSetFov(double degrees) =>
        ModelLayers.FirstPersonFieldOfView = double.IsFinite(degrees) ? (float)Math.Clamp(degrees, 0, 170) : 0f;

    [PgslCommand("ViewLayerSetNear", "ViewLayerSetNear(distance)", "The first-person layer's near plane in world units (default 0.01)", "Camera")]
    public static void ViewLayerSetNear(double distance)
    {
        if (double.IsFinite(distance) && distance > 0) ModelLayers.FirstPersonNearPlane = (float)Math.Min(distance, 10);
    }

    [PgslCommand("DrawModelGui", "DrawModelGui(model, x, y, width, height, yaw, pitch, zoom)",
        "Draw a Model into a GUI rectangle (an inventory portrait), turned by yaw and pitch in degrees and framed to fit (zoom 1); layered with the other GUI drawing in call order. Draw GUI only; it shows from the next frame",
        "Drawing 2D")]
    public static void DrawModelGui(string model, double x, double y, double width, double height, double yaw, double pitch, double zoom) =>
        DrawModelGuiPose(model, x, y, width, height, yaw, pitch, zoom, string.Empty, 0);

    [PgslCommand("DrawModelGuiPose", "DrawModelGuiPose(model, x, y, width, height, yaw, pitch, zoom, clip, time)",
        "Draw a Model into a GUI rectangle posed at an animation clip's time in seconds", "Drawing 2D")]
    public static void DrawModelGuiPose(string model, double x, double y, double width, double height,
        double yaw, double pitch, double zoom, string clip, double time)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || string.IsNullOrWhiteSpace(model) || !(width >= 1) || !(height >= 1)) return;
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;
        surface.DrawModelGui(model, new RectangleF((float)x, (float)y, (float)width, (float)height),
            (float)yaw, (float)pitch, (float)zoom, clip ?? string.Empty, (float)time, (float)(GetContext()?.DrawAlpha ?? 1));
    }
}
