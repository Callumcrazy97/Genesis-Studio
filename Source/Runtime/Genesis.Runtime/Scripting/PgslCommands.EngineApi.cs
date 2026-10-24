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

    // What scripts set is kept here too, so it reads back without a sound device (a test, a server).
    private static string _acoustics = "outdoors";
    private static double _acousticsAmount = .5, _musicMuffle;
    private static readonly string[] AcousticsNames = { "outdoors", "room", "hall", "cave" };

    [PgslCommand("Acoustics", "Engine.Audio.Acoustics = \"cave\"",
        "How the place sounds for sound effects: \"outdoors\" (no echo), \"room\", \"hall\" or \"cave\". Each room starts outdoors unless its settings choose another",
        "Audio", Namespace = "Engine.Audio", Flat = false)]
    public static string AudioApiAcoustics
    {
        get => _acoustics;
        set
        {
            string name = value?.Trim().ToLowerInvariant() ?? "";
            if (Array.IndexOf(AcousticsNames, name) < 0) return;
            _acoustics = name;
            if (ActiveGameContext?.Audio is { } audio) audio.Acoustics = name;
        }
    }

    [PgslCommand("AcousticsAmount", "Engine.Audio.AcousticsAmount = amount", "How much of the acoustics is heard, 0 (none) to 1 (0.5 unless set)", "Audio", Namespace = "Engine.Audio", Flat = false)]
    public static double AudioApiAcousticsAmount
    {
        get => _acousticsAmount;
        set
        {
            if (!double.IsFinite(value)) return;
            _acousticsAmount = Math.Clamp(value, 0, 1);
            if (ActiveGameContext?.Audio is { } audio) audio.AcousticsAmount = (float)_acousticsAmount;
        }
    }

    [PgslCommand("MusicMuffle", "Engine.Audio.MusicMuffle = amount", "How muffled the music is, 0 (clear) to 1 (heard through a wall): for a pause menu, underwater or a dream", "Audio", Namespace = "Engine.Audio", Flat = false)]
    public static double AudioApiMusicMuffle
    {
        get => _musicMuffle;
        set
        {
            if (!double.IsFinite(value)) return;
            _musicMuffle = Math.Clamp(value, 0, 1);
            if (ActiveGameContext?.Audio is { } audio) audio.MusicMuffle = (float)_musicMuffle;
        }
    }

    /// <summary>A room starts outdoors: its own engine settings may then choose other acoustics.</summary>
    internal static void ResetRoomAcoustics() => AudioApiAcoustics = "outdoors";

    // ── Engine.Lighting, and its parts Bounce, Reflections and Probe ─────────────────────
    // Each part has Enabled; turning a part on turns Engine.Lighting on (PgslEngineBridge), and
    // turning Engine.Lighting off turns the room's 3D lighting off with every part in it.

    // Kept here too, so it reads back where there is no game (a test, an editor's sandbox).
    private static bool _lightingEnabled = true;

    [PgslCommand("Enabled", "Engine.Lighting.Enabled = true",
        "The room's 3D lighting (the sun, lamps, shadows and every part of Engine.Lighting): off draws everything in its own colours. Turning a part on (Engine.Lighting.Bounce.Enabled = true) turns this on too",
        "Lighting", Namespace = "Engine.Lighting", Flat = false)]
    public static bool LightingApiEnabled
    {
        get => ActiveGameContext?.Scene?.Environment is { } environment ? environment.LightingEnabled : _lightingEnabled;
        set
        {
            _lightingEnabled = value;
            if (ActiveGameContext?.Scene?.Environment is { } environment) environment.LightingEnabled = value;
        }
    }

    [PgslCommand("Enabled", "Engine.Lighting.Bounce.Enabled = true",
        "Bounced light: surfaces are lit by the light around them (the sky where nothing is in the way, the lit walls and ground where something is) instead of the room's two flat ambient colours. On in each room unless turned off",
        "Lighting", Namespace = "Engine.Lighting.Bounce", Flat = false)]
    public static bool LightingApiBounceEnabled
    {
        get => Genesis.Shared.Rendering.LightProbes.BouncedLight;
        set => Genesis.Shared.Rendering.LightProbes.BouncedLight = value;
    }

    [PgslCommand("Strength", "Engine.Lighting.Bounce.Strength = amount",
        "How much the bounced light takes the place of the room's flat ambient colours, 0 to 1 (1 unless set)",
        "Lighting", Namespace = "Engine.Lighting.Bounce", Flat = false)]
    public static double LightingApiBounceStrength
    {
        get => Genesis.Shared.Rendering.LightProbes.BouncedLightStrength;
        set
        {
            if (double.IsFinite(value)) Genesis.Shared.Rendering.LightProbes.BouncedLightStrength = (float)value;
        }
    }

    [PgslCommand("Enabled", "Engine.Lighting.Reflections.Enabled = true",
        "Reflections: smooth and metal surfaces mirror what is around them, rough ones show it blurred. On in each room unless its settings or a script turn it off",
        "Lighting", Namespace = "Engine.Lighting.Reflections", Flat = false)]
    public static bool LightingApiReflectionsEnabled
    {
        get => Genesis.Shared.Rendering.LightProbes.Reflections;
        set => Genesis.Shared.Rendering.LightProbes.Reflections = value;
    }

    [PgslCommand("Strength", "Engine.Lighting.Reflections.Strength = amount",
        "How strongly surfaces reflect what is around them, 0 to 4: 1 is physically balanced (unless the room set its own environment reflection)",
        "Lighting", Namespace = "Engine.Lighting.Reflections", Flat = false)]
    public static double LightingApiReflectionsStrength
    {
        get => Genesis.Shared.Rendering.LightProbes.ReflectionStrength;
        set
        {
            if (double.IsFinite(value)) Genesis.Shared.Rendering.LightProbes.ReflectionStrength = (float)value;
        }
    }

    [PgslCommand("Quality", "Engine.Lighting.Probe.Quality = 2",
        "The detail of the light probe reflections and bounced light come from, 1 (32 pixels a side) to 3 (128); 2 unless set",
        "Lighting", Namespace = "Engine.Lighting.Probe", Flat = false)]
    public static double LightingApiProbeQuality
    {
        get => Genesis.Shared.Rendering.LightProbes.ReflectionQuality;
        set
        {
            if (double.IsFinite(value)) Genesis.Shared.Rendering.LightProbes.ReflectionQuality = (int)Math.Round(value);
        }
    }

    [PgslCommand("Distance", "Engine.Lighting.Probe.Distance = metres",
        "How far the camera travels from the last capture before the light probe is captured again, 5 to 1000 metres (40 unless set)",
        "Lighting", Namespace = "Engine.Lighting.Probe", Flat = false)]
    public static double LightingApiProbeDistance
    {
        get => Genesis.Shared.Rendering.LightProbes.RecaptureDistance;
        set
        {
            if (double.IsFinite(value)) Genesis.Shared.Rendering.LightProbes.RecaptureDistance = (float)value;
        }
    }

    [PgslCommand("Radius", "Engine.Lighting.Probe.Radius = metres",
        "What lies within this distance of a capture is drawn into it wherever the camera looks, 10 to 2000 metres (80 unless set)",
        "Lighting", Namespace = "Engine.Lighting.Probe", Flat = false)]
    public static double LightingApiProbeRadius
    {
        get => Genesis.Shared.Rendering.LightProbes.CaptureRadius;
        set
        {
            if (double.IsFinite(value)) Genesis.Shared.Rendering.LightProbes.CaptureRadius = (float)value;
        }
    }

    [PgslCommand("Recapture", "Engine.Lighting.Probe.Recapture()",
        "Captures what is around the camera again at the next frame, for reflections and bounced light: after a big change to the scene, such as a door opening or a building placed",
        "Lighting", Namespace = "Engine.Lighting.Probe", Flat = false)]
    public static void LightingApiProbeRecapture() => Genesis.Shared.Rendering.LightProbes.Recapture();

    // ── Engine.Game ──────────────────────────────────────────────────────────────────────

    [PgslCommand("Threads", "Engine.Game.Threads = threads",
        "How many of the computer's threads the engine shares its frame work between (culling, terrain, generation): reads the number in use; 0 chooses automatically (every core but two, the default), 1 keeps it all on the game's thread. Lasts until the game ends",
        "Game", Namespace = "Engine.Game", Flat = false)]
    public static double GameApiThreads
    {
        get => Genesis.Shared.Threading.EngineJobs.Threads;
        set
        {
            if (double.IsFinite(value)) Genesis.Shared.Threading.EngineJobs.ThreadLimit = (int)Math.Max(0, Math.Round(value));
        }
    }

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
