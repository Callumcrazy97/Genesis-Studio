#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// The values of the standard API (PgslApi) that have no Get/Set pair of commands to make them from:
/// the current instance's model, image and transform, the audio buses and the camera's position,
/// each read and set like a variable (<c>Engine.Model.Glow = 2;</c>, <c>Engine.Audio.MusicVolume -= 0.1;</c>).
/// An instance's value is the current instance's: its own in its events, each target's inside <c>with</c>.
/// </summary>
public static partial class PgslCommands
{
    // ── Engine.Model: the current instance's model ───────────────────────────────────────────

    [PgslCommand("CastShadows", "Engine.Model.CastShadows = true", "Whether this instance's model casts a shadow", "Models", Namespace = "Engine.Model", Flat = false)]
    public static bool ModelApiCastShadows
    {
        get => ThisModel(out var world, out var entity) && world.GetRef<ModelRendererComponent>(entity).CastShadows;
        set => ModelSetCastShadows(value);
    }

    [PgslCommand("ReceiveShadows", "Engine.Model.ReceiveShadows = true", "Whether shadows fall on this instance's model", "Models", Namespace = "Engine.Model", Flat = false)]
    public static bool ModelApiReceiveShadows
    {
        get => ThisModel(out var world, out var entity) && world.GetRef<ModelRendererComponent>(entity).ReceiveShadows;
        set { if (ThisModel(out var world, out var entity)) world.GetRef<ModelRendererComponent>(entity).ReceiveShadows = value; }
    }

    [PgslCommand("Glow", "Engine.Model.Glow = amount", "How brightly this instance's model glows (0 none to 32)", "Models", Namespace = "Engine.Model", Flat = false)]
    public static double ModelApiGlow
    {
        get => ThisModel(out var world, out var entity) ? world.GetRef<ModelRendererComponent>(entity).Glow : 0;
        set => ModelSetGlow(value);
    }

    [PgslCommand("EmissionScale", "Engine.Model.EmissionScale = scale", "The light this model's materials give off: 0 puts its lamps out, 1 is as authored", "Models", Namespace = "Engine.Model", Flat = false)]
    public static double ModelApiEmissionScale
    {
        get => ThisModel(out var world, out var entity) ? world.GetRef<ModelRendererComponent>(entity).EmissionScale ?? 1 : 0;
        set => ModelSetEmissionScale(value);
    }

    [PgslCommand("Layer", "Engine.Model.Layer = layer", "The model layer this instance's model is drawn in (0 = the world, 1-8 drawn over it)", "Models", Namespace = "Engine.Model", Flat = false)]
    public static double ModelApiLayer
    {
        get => ThisModel(out var world, out var entity) ? world.GetRef<ModelRendererComponent>(entity).ModelLayer : 0;
        set => ModelSetLayer(value);
    }

    [PgslCommand("ViewLayer", "Engine.Model.ViewLayer = true", "Whether this instance's model is drawn in the view layer (layer 1: a first-person weapon)", "Models", Namespace = "Engine.Model", Flat = false)]
    public static bool ModelApiViewLayer
    {
        get => ThisModel(out var world, out var entity) && world.GetRef<ModelRendererComponent>(entity).ModelLayer == 1;
        set => ModelSetViewLayer(value);
    }

    [PgslCommand("ScaleX", "Engine.Model.ScaleX = scale", "The model's own scale along X, on top of the instance's", "Models", Namespace = "Engine.Model", Flat = false)]
    public static double ModelApiScaleX { get => ModelScale(0); set => SetModelScale(0, value); }

    [PgslCommand("ScaleY", "Engine.Model.ScaleY = scale", "The model's own scale along Y, on top of the instance's", "Models", Namespace = "Engine.Model", Flat = false)]
    public static double ModelApiScaleY { get => ModelScale(1); set => SetModelScale(1, value); }

    [PgslCommand("ScaleZ", "Engine.Model.ScaleZ = scale", "The model's own scale along Z, on top of the instance's", "Models", Namespace = "Engine.Model", Flat = false)]
    public static double ModelApiScaleZ { get => ModelScale(2); set => SetModelScale(2, value); }

    private static double ModelScale(int axis)
    {
        if (!ThisModel(out var world, out var entity)) return 0;
        ModelRendererComponent model = world.GetRef<ModelRendererComponent>(entity);
        float scale = axis == 0 ? model.ScaleX : axis == 1 ? model.ScaleY : model.ScaleZ;
        return scale == 0 ? 1 : scale;
    }

    private static void SetModelScale(int axis, double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) < 1e-6 || !ThisModel(out var world, out var entity)) return;
        ref ModelRendererComponent model = ref world.GetRef<ModelRendererComponent>(entity);
        // A scale never set is 1 on every axis, not 0 on the other two.
        if (model.ScaleX == 0 && model.ScaleY == 0 && model.ScaleZ == 0) model.ScaleX = model.ScaleY = model.ScaleZ = 1;
        if (axis == 0) model.ScaleX = (float)value; else if (axis == 1) model.ScaleY = (float)value; else model.ScaleZ = (float)value;
    }

    // ── Engine.Image: the current instance's image ───────────────────────────────────────────

    [PgslCommand("Alpha", "Engine.Image.Alpha = alpha", "How opaque this instance's image is (0 invisible to 1)", "Sprites", Namespace = "Engine.Image", Flat = false)]
    public static double ImageApiAlpha
    {
        get => GetContext()?.ImageAlpha ?? 0;
        set => SpriteSetAlpha(value);
    }

    [PgslCommand("ScaleX", "Engine.Image.ScaleX = scale", "This instance's image scale across (negative flips it)", "Sprites", Namespace = "Engine.Image", Flat = false)]
    public static double ImageApiScaleX
    {
        get => GetContext()?.ImageXScale ?? 0;
        set { if (GetContext() is { } ctx && double.IsFinite(value)) ctx.ImageXScale = value; }
    }

    [PgslCommand("ScaleY", "Engine.Image.ScaleY = scale", "This instance's image scale down (negative flips it)", "Sprites", Namespace = "Engine.Image", Flat = false)]
    public static double ImageApiScaleY
    {
        get => GetContext()?.ImageYScale ?? 0;
        set { if (GetContext() is { } ctx && double.IsFinite(value)) ctx.ImageYScale = value; }
    }

    [PgslCommand("BlendR", "Engine.Image.BlendR = red", "The red of this instance's image tint (0-255; 255 leaves it as drawn)", "Sprites", Namespace = "Engine.Image", Flat = false)]
    public static double ImageApiBlendR { get => Blend().R; set => SetBlend(0, value); }

    [PgslCommand("BlendG", "Engine.Image.BlendG = green", "The green of this instance's image tint (0-255)", "Sprites", Namespace = "Engine.Image", Flat = false)]
    public static double ImageApiBlendG { get => Blend().G; set => SetBlend(1, value); }

    [PgslCommand("BlendB", "Engine.Image.BlendB = blue", "The blue of this instance's image tint (0-255)", "Sprites", Namespace = "Engine.Image", Flat = false)]
    public static double ImageApiBlendB { get => Blend().B; set => SetBlend(2, value); }

    [PgslCommand("Width", "Engine.Image.Width", "Width in pixels of a frame of this instance's image (0 for none)", "Sprites", Namespace = "Engine.Image", Flat = false)]
    public static double ImageApiWidth => SpriteWidth(SpriteGet());

    [PgslCommand("Height", "Engine.Image.Height", "Height in pixels of a frame of this instance's image (0 for none)", "Sprites", Namespace = "Engine.Image", Flat = false)]
    public static double ImageApiHeight => SpriteHeight(SpriteGet());

    private static System.Drawing.Color Blend() => GetContext()?.ImageBlend ?? System.Drawing.Color.White;

    private static void SetBlend(int channel, double value)
    {
        if (!double.IsFinite(value) || GetContext() is not { } ctx) return;
        System.Drawing.Color blend = ctx.ImageBlend;
        int v = (int)Math.Clamp(value, 0, 255);
        SpriteSetBlend(channel == 0 ? v : blend.R, channel == 1 ? v : blend.G, channel == 2 ? v : blend.B);
    }

    // ── Engine.Audio ──────────────────────────────────────────────────────────────────────

    [PgslCommand("MusicVolume", "Engine.Audio.MusicVolume = volume", "The volume of the music bus (0-1)", "Audio", Namespace = "Engine.Audio", Flat = false)]
    public static double AudioApiMusicVolume { get => GetBusVolume("music"); set => SetBusVolume("music", value); }

    [PgslCommand("SfxVolume", "Engine.Audio.SfxVolume = volume", "The volume of the sound-effects bus (0-1)", "Audio", Namespace = "Engine.Audio", Flat = false)]
    public static double AudioApiSfxVolume { get => GetBusVolume("sfx"); set => SetBusVolume("sfx", value); }

    [PgslCommand("Music", "Engine.Audio.Music = \"Theme\"", "The music track playing (\"\" for none): setting it cross-fades to that track over a second, \"\" fades it out", "Audio", Namespace = "Engine.Audio", Flat = false)]
    public static string AudioApiMusic
    {
        get => MusicCurrent();
        set
        {
            if (string.IsNullOrWhiteSpace(value)) MusicStop();
            else MusicPlay(value);
        }
    }

    // ── Engine.Display and Engine.Draw ───────────────────────────────────────────────────

    [PgslCommand("Width", "Engine.Display.Width = pixels", "The window's width (setting it resizes the window, as WindowSetSize does)", "Display", Namespace = "Engine.Display", Flat = false)]
    public static double DisplayApiWidth { get => WindowGetWidth(); set => WindowSetSize(value, WindowGetHeight()); }

    [PgslCommand("Height", "Engine.Display.Height = pixels", "The window's height (setting it resizes the window)", "Display", Namespace = "Engine.Display", Flat = false)]
    public static double DisplayApiHeight { get => WindowGetHeight(); set => WindowSetSize(WindowGetWidth(), value); }

    [PgslCommand("OffsetX", "Engine.Draw.OffsetX = pixels", "How far across what is drawn next is moved (as DrawSetOffset)", "Drawing 2D", Namespace = "Engine.Draw", Flat = false)]
    public static double DrawApiOffsetX { get => DrawGetOffsetX(); set => DrawSetOffset(value, DrawGetOffsetY()); }

    [PgslCommand("OffsetY", "Engine.Draw.OffsetY = pixels", "How far down what is drawn next is moved (as DrawSetOffset)", "Drawing 2D", Namespace = "Engine.Draw", Flat = false)]
    public static double DrawApiOffsetY { get => DrawGetOffsetY(); set => DrawSetOffset(DrawGetOffsetX(), value); }

    // ── Engine.Camera ────────────────────────────────────────────────────────────────────

    [PgslCommand("X", "Engine.Camera.X = x", "The play camera's position across", "Camera", Namespace = "Engine.Camera", Flat = false)]
    public static double CameraApiX { get => GetCameraX(); set => SetCameraPosition(value, GetCameraY(), GetCameraZ()); }

    [PgslCommand("Y", "Engine.Camera.Y = y", "The play camera's height", "Camera", Namespace = "Engine.Camera", Flat = false)]
    public static double CameraApiY { get => GetCameraY(); set => SetCameraPosition(GetCameraX(), value, GetCameraZ()); }

    [PgslCommand("Z", "Engine.Camera.Z = z", "The play camera's position along Z", "Camera", Namespace = "Engine.Camera", Flat = false)]
    public static double CameraApiZ { get => GetCameraZ(); set => SetCameraPosition(GetCameraX(), GetCameraY(), value); }

    // ── Engine.Instance: the current instance in 3D ──────────────────────────────────────────

    [PgslCommand("Pitch", "Engine.Instance.Pitch = degrees", "This instance's rotation about X, in degrees (its body turns with it)", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static double InstanceApiPitch { get => InstanceGetRotationPitch(SelfId()); set => SetRotationAxis(0, value); }

    [PgslCommand("Yaw", "Engine.Instance.Yaw = degrees", "This instance's rotation about Y (its heading), in degrees", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static double InstanceApiYaw { get => InstanceGetRotationYaw(SelfId()); set => SetRotationAxis(1, value); }

    [PgslCommand("Roll", "Engine.Instance.Roll = degrees", "This instance's rotation about Z, in degrees (image_angle in 2D)", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static double InstanceApiRoll { get => InstanceGetRotationRoll(SelfId()); set => SetRotationAxis(2, value); }

    [PgslCommand("ScaleX", "Engine.Instance.ScaleX = scale", "This instance's scale along X (its body is resized with it)", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static double InstanceApiScaleX { get => InstanceGetScaleX(SelfId()); set => SetScaleAxis(0, value); }

    [PgslCommand("ScaleY", "Engine.Instance.ScaleY = scale", "This instance's scale along Y", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static double InstanceApiScaleY { get => InstanceGetScaleY(SelfId()); set => SetScaleAxis(1, value); }

    [PgslCommand("ScaleZ", "Engine.Instance.ScaleZ = scale", "This instance's scale along Z", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static double InstanceApiScaleZ { get => InstanceGetScaleZ(SelfId()); set => SetScaleAxis(2, value); }

    // ── Engine.Instance: parts (Objects inside Objects) ────────────────────────────────────────

    [PgslCommand("Parent", "Engine.Instance.Parent = id", "The instance this one is kept on (an Object's part, or attached by Attach); 0 for none. Setting it keeps it on that instance where it is now, 0 lets it go", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static double InstanceApiParent
    {
        get
        {
            var world = ActiveGameContext?.World;
            if (world == null || !Instance(SelfId(), world, out Entity self) || !world.Has<ModelSocketAttachmentComponent>(self)) return 0;
            ModelSocketAttachmentComponent link = world.GetRef<ModelSocketAttachmentComponent>(self);
            return link.Enabled && world.IsAlive(world.GetEntity(link.ParentEntityId)) ? link.ParentEntityId : 0;
        }
        set
        {
            var world = ActiveGameContext?.World;
            if (world == null || !Instance(SelfId(), world, out Entity self)) return;
            if (value <= 0 || !Instance(value, world, out Entity parent))
            {
                InstanceApiDetach();
                return;
            }
            // Kept where it is now: its offset is where it stands seen from the new parent.
            bool threeD = world.Has<Transform3DComponent>(parent);
            Matrix4x4 here = ModelSocketRuntime.InstanceMatrix(world.GetRef<TransformComponent>(self), threeD);
            if (!Matrix4x4.Invert(ModelSocketRuntime.InstanceMatrix(world.GetRef<TransformComponent>(parent), threeD), out Matrix4x4 inverse)) return;
            ModelSocketRuntime.Attach(world, self, parent, here * inverse, part: false);
        }
    }

    [PgslCommand("ChildCount", "Engine.Instance.ChildCount", "How many instances are kept on this one (its parts, and what was attached to it)", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static double InstanceApiChildCount => Children().Count;

    [PgslCommand("Child", "Engine.Instance.Child(index) -> id", "The id of one of the instances kept on this one, from 0 in the order they were made; 0 past the end", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static double InstanceApiChild(double index)
    {
        List<int> children = Children();
        return double.IsFinite(index) && index >= 0 && index < children.Count ? children[(int)index] : 0;
    }

    [PgslCommand("Attach", "Engine.Instance.Attach(id, x, y, z) -> bool", "Keep another instance on this one, x, y and z from it, turning and moving with it", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static bool InstanceApiAttach(double id, double x, double y, double z)
    {
        var world = ActiveGameContext?.World;
        if (world == null || !Finite3(x, y, z) || !Instance(SelfId(), world, out Entity self) || !Instance(id, world, out Entity child)) return false;
        return ModelSocketRuntime.Attach(world, child, self, ModelSocketRuntime.Offset((float)x, (float)y, (float)z, 0, 0, 0, 1), part: false);
    }

    [PgslCommand("Detach", "Engine.Instance.Detach() -> bool", "Let this instance go from the one it is kept on (it stays where it is); false when it was on none", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static bool InstanceApiDetach()
    {
        var world = ActiveGameContext?.World;
        if (world == null || !Instance(SelfId(), world, out Entity self) || !world.Has<ModelSocketAttachmentComponent>(self)) return false;
        ref ModelSocketAttachmentComponent link = ref world.GetRef<ModelSocketAttachmentComponent>(self);
        bool was = link.Enabled;
        link.Enabled = false;
        link.Part = false;
        return was;
    }

    // The instances kept on the current one, in the order they were made (entity id).
    private static List<int> Children()
    {
        var children = new List<int>();
        var world = ActiveGameContext?.World;
        if (world == null || !Instance(SelfId(), world, out Entity self)) return children;
        foreach (var item in world.Query<ModelSocketAttachmentComponent>())
            if (item.C1.Enabled && item.C1.ParentEntityId == self.Id && world.IsAlive(item.Entity)) children.Add(item.Entity.Id);
        children.Sort();
        return children;
    }

    private static void SetRotationAxis(int axis, double value)
    {
        double self = SelfId();
        double pitch = InstanceGetRotationPitch(self), yaw = InstanceGetRotationYaw(self), roll = InstanceGetRotationRoll(self);
        if (axis == 0) pitch = value; else if (axis == 1) yaw = value; else roll = value;
        InstanceSetRotation3D(self, pitch, yaw, roll);
    }

    private static void SetScaleAxis(int axis, double value)
    {
        double self = SelfId();
        double x = InstanceGetScaleX(self), y = InstanceGetScaleY(self), z = InstanceGetScaleZ(self);
        if (axis == 0) x = value; else if (axis == 1) y = value; else z = value;
        InstanceSetScale3D(self, x, y, z);
    }
}
