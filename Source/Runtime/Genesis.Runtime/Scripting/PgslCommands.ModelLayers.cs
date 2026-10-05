using System;
using System.Drawing;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Model layers 1-8 drawn over the world, and models in the GUI (see ModelLayers).
public static partial class PgslCommands
{
    [PgslCommand("ModelSetLayer", "ModelSetLayer(layer) -> bool",
        "Draw this instance's model in a model layer: 0 is the world; 1-8 are drawn over the world in number order, each with its own field of view and near plane, never cut by a wall and casting no shadow (a held tool, a cockpit, a compass)",
        "Models")]
    public static bool ModelSetLayer(double layer) =>
        ThisModel(out var world, out var entity) && SetModelLayer(world, entity, layer);

    [PgslCommand("InstanceSetModelLayer", "InstanceSetModelLayer(id, layer) -> bool", "Draw another instance's model in a model layer (0 = the world, 1-8)", "Models")]
    public static bool InstanceSetModelLayer(double id, double layer)
    {
        var world = ActiveGameContext?.World;
        if (world == null || !double.IsFinite(id) || id < 1 || id > int.MaxValue) return false;
        var entity = world.GetEntity((int)id);
        return world.IsAlive(entity) && world.Has<ModelRendererComponent>(entity) && SetModelLayer(world, entity, layer);
    }

    private static bool SetModelLayer(Genesis.Runtime.ECS.World world, Genesis.Shared.ECS.Entity entity, double layer)
    {
        if (!double.IsFinite(layer) || layer < 0 || layer > ModelLayers.MaxOverlay) return false;
        world.GetRef<ModelRendererComponent>(entity).ModelLayer = (int)layer;
        return true;
    }

    [PgslCommand("ModelLayerSetFov", "ModelLayerSetFov(layer, degrees)",
        "A model layer's vertical field of view (0 = the camera's), so zooming the world does not zoom the layer", "Camera")]
    public static void ModelLayerSetFov(double layer, double degrees) =>
        ModelLayers.SetFieldOfView(LayerNumber(layer), (float)degrees);

    [PgslCommand("ModelLayerSetNear", "ModelLayerSetNear(layer, distance)", "A model layer's near plane in world units (default 0.01)", "Camera")]
    public static void ModelLayerSetNear(double layer, double distance) =>
        ModelLayers.SetNearPlane(LayerNumber(layer), (float)distance);

    [PgslCommand("ModelLayerSetVisible", "ModelLayerSetVisible(layer, visible)", "Hide or show a whole model layer", "Camera")]
    public static void ModelLayerSetVisible(double layer, bool visible) =>
        ModelLayers.SetVisible(LayerNumber(layer), visible);

    // The first names: layer 1.
    [PgslCommand("ModelSetViewLayer", "ModelSetViewLayer(enabled) -> bool", "The same as ModelSetLayer(1) or ModelSetLayer(0)", "Models")]
    public static bool ModelSetViewLayer(bool enabled) => ModelSetLayer(enabled ? 1 : 0);

    [PgslCommand("InstanceSetViewLayer", "InstanceSetViewLayer(id, enabled) -> bool", "The same as InstanceSetModelLayer(id, 1) or (id, 0)", "Models")]
    public static bool InstanceSetViewLayer(double id, bool enabled) => InstanceSetModelLayer(id, enabled ? 1 : 0);

    [PgslCommand("ViewLayerSetFov", "ViewLayerSetFov(degrees)", "The same as ModelLayerSetFov(1, degrees)", "Camera")]
    public static void ViewLayerSetFov(double degrees) => ModelLayerSetFov(1, degrees);

    [PgslCommand("ViewLayerSetNear", "ViewLayerSetNear(distance)", "The same as ModelLayerSetNear(1, distance)", "Camera")]
    public static void ViewLayerSetNear(double distance) => ModelLayerSetNear(1, distance);

    private static int LayerNumber(double layer) => double.IsFinite(layer) ? (int)Math.Round(layer) : 0;

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
