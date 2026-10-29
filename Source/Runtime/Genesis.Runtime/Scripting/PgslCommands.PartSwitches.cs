#nullable enable
using System;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// The Enabled of areas and parts of the engine API that had none (Documentation/Design/EngineApiAreas.md): each
// switches off what the area or part does and keeps its values for when it is switched on again. Every one is on
// unless set, so a game that never touches them runs as it did, and a new game starts with all of them on
// (ResetPartSwitches). Turning a part on turns its area on (PgslEngineBridge); an Enabled that duplicates a value
// the part already had (Sky.Sun.Visible, Room.Streaming.Remember) is the same switch under the standard's name.
public static partial class PgslCommands
{
    /// <summary>Switches back on what a game switched off, for a new play session (statics only: a room's own switches go with its scene).</summary>
    internal static void ResetPartSwitches()
    {
        _drawOn = _meshOn = _uiScaleOn = true;
        MeshLightingDefaults.ResetPartSwitches();
        if (_cullingOff) RenderingCullingEnabled = true;
    }

    // ── Engine.Game, Engine.Draw, Engine.Mesh ────────────────────────────────────────────

    [PgslCommand("Enabled", "Engine.Game.Enabled = false", "Whether the game's time runs (true unless set); off is Engine.Game.Paused: Step events and physics see no time passing, Draw events still run", "Game", Namespace = "Engine.Game", Flat = false)]
    public static bool GameApiEnabled { get => !GameApiPaused; set => GameApiPaused = !value; }

    private static bool _drawOn = true;

    [PgslCommand("Enabled", "Engine.Draw.Enabled = false", "Whether the game's drawing commands (shapes, text, images, script meshes) draw anything (true unless set); off, they do nothing, and the font, colour and the rest are kept", "Drawing 2D", Namespace = "Engine.Draw", Flat = false)]
    public static bool DrawApiEnabled { get => _drawOn; set => _drawOn = value; }

    private static bool _meshOn = true;

    [PgslCommand("Enabled", "Engine.Mesh.Enabled = false", "Whether the meshes a script builds are drawn (true unless set); off, DrawMesh3D and its kin draw nothing and the meshes are kept", "Meshes", Namespace = "Engine.Mesh", Flat = false)]
    public static bool MeshApiEnabled { get => _meshOn; set => _meshOn = value; }

    [PgslCommand("Enabled", "Engine.Mesh.Upload.Enabled = false", "Whether the upload budget is kept (true unless set); off, every script mesh goes to the graphics card the frame it changes, however long that takes", "Meshes", Namespace = "Engine.Mesh.Upload", Flat = false)]
    public static bool MeshUploadEnabledPart { get => Rendering.ScriptMeshes.UploadBudgetEnabled; set => Rendering.ScriptMeshes.UploadBudgetEnabled = value; }

    // ── Engine.Rendering's post-processing parts ─────────────────────────────────────────

    [PgslCommand("Enabled", "Engine.Rendering.Grading.Enabled = false", "Colour grading (true unless set); off, the picture is drawn with neutral contrast, saturation and vignette, and the values wait", "Rendering", Namespace = "Engine.Rendering.Grading", Flat = false)]
    public static bool RenderingGradingEnabled { get => MeshLightingDefaults.GradingEnabled; set => MeshLightingDefaults.GradingEnabled = value; }

    [PgslCommand("Enabled", "Engine.Rendering.Exposure.Enabled = false", "Exposure (true unless set); off, the picture is drawn at exposure 1 with no eye adaptation, and the value and the adaptation wait", "Rendering", Namespace = "Engine.Rendering.Exposure", Flat = false)]
    public static bool RenderingExposureEnabled { get => MeshLightingDefaults.ExposureEnabled; set => MeshLightingDefaults.ExposureEnabled = value; }

    [PgslCommand("Enabled", "Engine.Rendering.AntiAliasing.Enabled = false", "Smoothing of jagged edges (true unless set); off, edges are drawn as they are, and the mode waits", "Rendering", Namespace = "Engine.Rendering.AntiAliasing", Flat = false)]
    public static bool RenderingAntiAliasingEnabled { get => MeshLightingDefaults.AntiAliasingEnabled; set => MeshLightingDefaults.AntiAliasingEnabled = value; }

    [PgslCommand("Enabled", "Engine.Rendering.MotionBlur.Enabled = false", "Blur along movement (true unless set); off, there is none whatever its strength, which waits", "Rendering", Namespace = "Engine.Rendering.MotionBlur", Flat = false)]
    public static bool RenderingMotionBlurEnabled { get => MeshLightingDefaults.MotionBlurEnabled; set => MeshLightingDefaults.MotionBlurEnabled = value; }

    // Culling off draws everything: the four culling switches are put by and come back when it is on again.
    private static bool _cullingOff;
    private static (bool Frustum, bool Occlusion, bool Distance, bool SubPixel) _cullingPutBy;

    [PgslCommand("Enabled", "Engine.Rendering.Culling.Enabled = false", "Skipping what is not seen (true unless set); off, everything is drawn, outside the view, behind large things, far away or too small to see, and Frustum and Occlusion wait", "Rendering", Namespace = "Engine.Rendering.Culling", Flat = false)]
    public static bool RenderingCullingEnabled
    {
        get => !_cullingOff;
        set
        {
            if (value == !_cullingOff) return;
            if (!value)
            {
                _cullingPutBy = (RenderAutoState.FrustumCulling, RenderAutoState.OcclusionCulling, RenderAutoState.DistanceCulling, RenderAutoState.SubPixelCulling);
                RenderAutoState.FrustumCulling = RenderAutoState.OcclusionCulling = RenderAutoState.DistanceCulling = RenderAutoState.SubPixelCulling = false;
            }
            else
            {
                (RenderAutoState.FrustumCulling, RenderAutoState.OcclusionCulling, RenderAutoState.DistanceCulling, RenderAutoState.SubPixelCulling) = _cullingPutBy;
            }
            _cullingOff = !value;
        }
    }

    [PgslCommand("Enabled", "Engine.Display.Resolution.Enabled = false", "Drawing the 3D scene at a share of the window's size (true unless set); off, it is drawn at the window's own size, and the scale, the sharpening and the dynamic resolution wait", "Display", Namespace = "Engine.Display.Resolution", Flat = false)]
    public static bool DisplayResolutionEnabled { get => RenderScale.Enabled; set => RenderScale.Enabled = value; }

    // ── Engine.Lighting's Sun, Ambient and Glow; the room's simulation distance ──────────

    [PgslCommand("Enabled", "Engine.Lighting.Sun.Enabled = false", "The sun's light (true unless set); off, nothing is lit by the sun (its disc is Engine.Sky.Sun.Visible), and the intensity, direction and colour wait", "Lighting", Namespace = "Engine.Lighting.Sun", Flat = false)]
    public static bool LightingSunEnabled { get => RoomLook?.SunLightEnabled ?? true; set { if (RoomLook is { } look) look.SunLightEnabled = value; } }

    [PgslCommand("Enabled", "Engine.Lighting.Ambient.Enabled = false", "The light from the sky and the ground where no sun reaches (true unless set); off, what the sun does not reach is dark, and the colours, mode and strength wait", "Lighting", Namespace = "Engine.Lighting.Ambient", Flat = false)]
    public static bool LightingAmbientEnabled { get => RoomLook?.AmbientLightEnabled ?? true; set { if (RoomLook is { } look) look.AmbientLightEnabled = value; } }

    [PgslCommand("Enabled", "Engine.Lighting.Glow.Enabled = false", "The glow of every surface in its own colours (true unless set); off, there is none, and the strength waits", "Lighting", Namespace = "Engine.Lighting.Glow", Flat = false)]
    public static bool LightingGlowEnabled { get => RoomLook?.GlowEnabled ?? true; set { if (RoomLook is { } look) look.GlowEnabled = value; } }

    [PgslCommand("Enabled", "Engine.Instance.Update.Enabled = false", "Whether instances far from the camera skip their Step events (true unless set); off, every instance steps however far it is, and the distance waits", "Instances", Namespace = "Engine.Instance.Update", Flat = false)]
    public static bool InstanceUpdateEnabled { get => RoomLook?.SimulationDistanceEnabled ?? true; set { if (RoomLook is { } look) look.SimulationDistanceEnabled = value; } }

    // ── Engine.Camera.Shoulder, Engine.View.Camera ───────────────────────────────────────

    [PgslCommand("Enabled", "Engine.Camera.Shoulder.Enabled = false", "Whether the orbit camera looks over this instance's shoulder (true unless set); off, it circles the instance itself, and the offsets wait", "Camera", Namespace = "Engine.Camera.Shoulder", Flat = false)]
    public static bool CameraShoulderEnabled { get => OrbitIfAny()?.ShoulderEnabled ?? true; set { if (Orbit() is { } rig) rig.ShoulderEnabled = value; } }

    [PgslCommand("Enabled", "Engine.View.Camera.Enabled = false", "Whether the active view is drawn (true unless set); off, its part of the window is left undrawn: ViewSetActive chooses which view this is", "Views", Namespace = "Engine.View.Camera", Flat = false)]
    public static bool ViewCameraEnabled { get => ViewGetEnabled(ViewGetActive()); set => ViewSetEnabled(ViewGetActive(), value); }

    // ── Engine.Sky's Time, Sun and Moon; Engine.Weather ──────────────────────────────────

    [PgslCommand("Enabled", "Engine.Sky.Time.Enabled = false", "Whether the time of day runs (true unless set); off holds it where it is, as Engine.Sky.Time.Paused does", "Sky", Namespace = "Engine.Sky.Time", Flat = false)]
    public static bool SkyTimeEnabled { get => !SkyTimePaused; set => SkyTimePaused = !value; }

    [PgslCommand("Enabled", "Engine.Sky.Sun.Enabled = false", "Whether the sun's disc is in the sky (true unless set): the same switch as Engine.Sky.Sun.Visible", "Sky", Namespace = "Engine.Sky.Sun", Flat = false)]
    public static bool SkySunEnabled { get => SkySunDiscVisible; set => SkySunDiscVisible = value; }

    [PgslCommand("Enabled", "Engine.Sky.Moon.Enabled = false", "Whether the moon's disc is in the sky (true unless set): the same switch as Engine.Sky.Moon.Visible", "Sky", Namespace = "Engine.Sky.Moon", Flat = false)]
    public static bool SkyMoonEnabled { get => SkyMoonDiscVisible; set => SkyMoonDiscVisible = value; }

    [PgslCommand("Enabled", "Engine.Weather.Enabled = false", "Whether the weather acts (true unless set); off, there is no wind, rain, snow or lightning whatever the weather is, and each part keeps its own setting for when it comes back", "Weather", Namespace = "Engine.Weather", Flat = false)]
    public static bool WeatherApiEnabled { get => WeatherHeld?.Active ?? true; set { if (WeatherHeld is { } held) held.Active = value; } }

    // ── Engine.Physics' World and Gravity; Engine.Water.Swimming ─────────────────────────

    [PgslCommand("Enabled", "Engine.Physics.World.Enabled = false", "Whether the physics simulation steps (true unless set); off, every body holds where it is, and the world's values wait", "Physics", Namespace = "Engine.Physics.World", Flat = false)]
    public static bool PhysicsWorldEnabled { get => ActiveGameContext?.Scene?.Physics?.SimulationEnabled ?? true; set { if (ActiveGameContext?.Scene?.Physics is { } physics) physics.SimulationEnabled = value; } }

    [PgslCommand("Enabled", "Engine.Physics.Gravity.Enabled = false", "Whether gravity pulls on bodies (true unless set); off, nothing falls, and the strength and direction wait", "Physics", Namespace = "Engine.Physics.Gravity", Flat = false)]
    public static bool PhysicsGravityEnabled { get => ActiveGameContext?.Scene?.Physics?.GravityEnabled ?? true; set { if (ActiveGameContext?.Scene?.Physics is { } physics) physics.GravityEnabled = value; } }

    [PgslCommand("Enabled", "Engine.Water.Swimming.Enabled = false", "Whether the room's water lifts and slows bodies in it (true unless set); off, they pass through it as through air (the water is still drawn: Engine.Water.Enabled), and the buoyancy and drag wait", "Water", Namespace = "Engine.Water.Swimming", Flat = false)]
    public static bool WaterSwimmingEnabled { get => ActiveGameContext?.Scene?.Physics?.WaterSwimmingEnabled ?? true; set { if (ActiveGameContext?.Scene?.Physics is { } physics) physics.WaterSwimmingEnabled = value; } }

    // ── Engine.Audio.Listener, Engine.Room.Change, Engine.Room.Streaming, Engine.Ui.Scale ─

    [PgslCommand("Enabled", "Engine.Audio.Listener.Enabled = false", "Whether positioned sounds are heard from the 3D camera (true unless set): the same switch as Engine.Audio.Listener.FollowCamera, false keeping the listener where SetAudioListener put it", "Audio", Namespace = "Engine.Audio.Listener", Flat = false)]
    public static bool AudioListenerEnabled { get => AudioListenerFollowsCameraPart; set => AudioListenerFollowsCameraPart = value; }

    [PgslCommand("Enabled", "Engine.Room.Change.Enabled = false", "Whether a room change is spread over frames behind a cover (true unless set); off, the room changes in one step with no cover or loading screen, and the fade, minimum time and words wait", "Rooms", Namespace = "Engine.Room.Change", Flat = false)]
    public static bool RoomChangeEnabledPart { get => Genesis.Runtime.Project.RoomChangeScreen.Enabled; set => Genesis.Runtime.Project.RoomChangeScreen.Enabled = value; }

    [PgslCommand("Enabled", "Engine.Room.Streaming.Enabled = false", "Whether streamed Objects and what a script destroyed are remembered room by room (true unless set): the same switch as Engine.Room.Streaming.Remember", "Rooms", Namespace = "Engine.Room.Streaming", Flat = false)]
    public static bool RoomStreamingEnabled { get => RoomStreamingRemember; set => RoomStreamingRemember = value; }

    private static bool _uiScaleOn = true;

    [PgslCommand("Enabled", "Engine.Ui.Scale.Enabled = false", "Whether User Interfaces are scaled by Engine.Ui.Scale.Value (true unless set); off, they are drawn at their own size, and the value waits", "User Interface", Namespace = "Engine.Ui.Scale", Flat = false)]
    public static bool UiScaleEnabledPart { get => _uiScaleOn; set => _uiScaleOn = value; }
}
