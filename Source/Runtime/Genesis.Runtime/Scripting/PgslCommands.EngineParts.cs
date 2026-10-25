#nullable enable
using System;
using System.Numerics;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Rendering;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// The parts of the engine's areas (Documentation/Design/EngineApiAreas.md): Engine.Area.Part.Value,
// with Enabled on each part. Every value starts at the default its description gives and keeps
// what a script sets until the room or game resets it; turning a part on turns its area on
// (PgslEngineBridge). Most are the engine's existing values under one standard name each.
public static partial class PgslCommands
{
    private static SceneEnvironment? RoomLook => ActiveGameContext?.Scene?.Environment;

    private static Platform.SilkGameWindow? GameWindow => (ActiveGameContext as ProjectGameContext)?.Window;

    private static float Finite(double value, double min, double max, float fallback) =>
        double.IsFinite(value) ? (float)Math.Clamp(value, min, max) : fallback;

    // ── Engine.Display.Window, Monitor, Cursor ───────────────────────────────────────────

    [PgslCommand("X", "Engine.Display.Window.X = pixels", "Where a windowed game's window is across the desktop (its left edge); 0 where there is no window", "Display", Namespace = "Engine.Display.Window", Flat = false)]
    public static double DisplayWindowX { get => GameWindow?.Position.X ?? 0; set { if (double.IsFinite(value)) GameWindow?.SetPosition((int)value, GameWindow.Position.Y); } }

    [PgslCommand("Y", "Engine.Display.Window.Y = pixels", "Where a windowed game's window is down the desktop (its top edge); 0 where there is no window", "Display", Namespace = "Engine.Display.Window", Flat = false)]
    public static double DisplayWindowY { get => GameWindow?.Position.Y ?? 0; set { if (double.IsFinite(value)) GameWindow?.SetPosition(GameWindow.Position.X, (int)value); } }

    [PgslCommand("Resizable", "Engine.Display.Window.Resizable = true", "Whether the player can resize a windowed game's window by its edges (true unless set)", "Display", Namespace = "Engine.Display.Window", Flat = false)]
    public static bool DisplayWindowResizable { get => GameWindow?.Resizable ?? true; set { if (GameWindow is { } window) window.Resizable = value; } }

    [PgslCommand("AlwaysOnTop", "Engine.Display.Window.AlwaysOnTop = false", "Whether the window stays above other windows (false unless set)", "Display", Namespace = "Engine.Display.Window", Flat = false)]
    public static bool DisplayWindowAlwaysOnTop { get => GameWindow?.AlwaysOnTop ?? false; set { if (GameWindow is { } window) window.AlwaysOnTop = value; } }

    [PgslCommand("Maximized", "Engine.Display.Window.Maximized = true", "Whether a windowed game's window fills its monitor's working area, border and title kept (false unless set); false restores it", "Display", Namespace = "Engine.Display.Window", Flat = false)]
    public static bool DisplayWindowMaximized
    {
        get => GameWindow?.IsMaximized ?? false;
        set { if (value) GameWindow?.Maximize(); else GameWindow?.Restore(); }
    }

    [PgslCommand("Minimized", "Engine.Display.Window.Minimized = true", "Whether the window is minimised to the taskbar; false restores it", "Display", Namespace = "Engine.Display.Window", Flat = false)]
    public static bool DisplayWindowMinimized
    {
        get => ActiveGameContext?.WindowIsMinimized ?? false;
        set { if (value) GameWindow?.Minimize(); else GameWindow?.Restore(); }
    }

    [PgslCommand("Focus", "Engine.Display.Window.Focus()", "Bring the game's window to the front and give it the keyboard", "Display", Namespace = "Engine.Display.Window", Flat = false)]
    public static void DisplayWindowFocus() => GameWindow?.Focus();

    [PgslCommand("Centre", "Engine.Display.Window.Centre()", "Put a windowed game's window in the middle of its monitor", "Display", Namespace = "Engine.Display.Window", Flat = false)]
    public static void DisplayWindowCentre() => GameWindow?.Centre();

    [PgslCommand("Index", "Engine.Display.Monitor.Index = 0", "Which monitor the game is on, 0 for the first; setting it moves the window there (centred, or filling it in full screen)", "Display", Namespace = "Engine.Display.Monitor", Flat = false)]
    public static double DisplayMonitorIndex { get => GameWindow?.MonitorInfo.Index ?? 0; set { if (double.IsFinite(value)) GameWindow?.MoveToMonitor((int)Math.Max(0, value)); } }

    [PgslCommand("Count", "Engine.Display.Monitor.Count -> number", "How many monitors there are (1 where it cannot tell)", "Display", Namespace = "Engine.Display.Monitor", Flat = false)]
    public static double DisplayMonitorCount => Math.Max(1, GameWindow?.MonitorInfo.Count ?? 1);

    [PgslCommand("Width", "Engine.Display.Monitor.Width -> pixels", "The game's monitor's width in pixels (0 where there is no window)", "Display", Namespace = "Engine.Display.Monitor", Flat = false)]
    public static double DisplayMonitorWidth => GameWindow?.MonitorInfo.Width ?? 0;

    [PgslCommand("Height", "Engine.Display.Monitor.Height -> pixels", "The game's monitor's height in pixels (0 where there is no window)", "Display", Namespace = "Engine.Display.Monitor", Flat = false)]
    public static double DisplayMonitorHeight => GameWindow?.MonitorInfo.Height ?? 0;

    [PgslCommand("RefreshRate", "Engine.Display.Monitor.RefreshRate -> hertz", "How many times a second the game's monitor refreshes (0 where it cannot tell)", "Display", Namespace = "Engine.Display.Monitor", Flat = false)]
    public static double DisplayMonitorRefreshRate => GameWindow?.MonitorInfo.RefreshRate ?? 0;

    [PgslCommand("Enabled", "Engine.Display.Cursor.Enabled = true", "Whether the system pointer shows over the game (true unless set); a captured mouse stays hidden and the choice applies when it is released", "Display", Namespace = "Engine.Display.Cursor", Flat = false)]
    public static bool DisplayCursorEnabled { get => !_pointerHidden; set => WindowSetCursorVisible(value); }

    // ── Engine.Display.Resolution: render scale and dynamic resolution ───────────────────

    [PgslCommand("Scale", "Engine.Display.Resolution.Scale = 0.75", "The share of the window's size the 3D scene is drawn at, then stretched to the window with a light sharpening, 0.25 to 2 (1 unless set): 0.75 shades about 44% fewer pixels for a faster frame on a small machine, above 1 draws more for a sharper picture; the GUI and text always keep every pixel", "Display", Namespace = "Engine.Display.Resolution", Flat = false)]
    public static double DisplayResolutionScale
    {
        get => Genesis.Shared.Rendering.RenderScale.Scale;
        set => Genesis.Shared.Rendering.RenderScale.Scale = Finite(value, Genesis.Shared.Rendering.RenderScale.Smallest, Genesis.Shared.Rendering.RenderScale.Largest, 1f);
    }

    [PgslCommand("Sharpness", "Engine.Display.Resolution.Sharpness = 0.5", "How much a scene drawn smaller than the window is sharpened as it is stretched, 0 to 1 (0.5 unless set)", "Display", Namespace = "Engine.Display.Resolution", Flat = false)]
    public static double DisplayResolutionSharpness
    {
        get => Genesis.Shared.Rendering.RenderScale.Sharpness;
        set => Genesis.Shared.Rendering.RenderScale.Sharpness = Finite(value, 0, 1, 0.5f);
    }

    [PgslCommand("Current", "Engine.Display.Resolution.Current", "The share of the window's size the 3D scene was last drawn at: the Scale, or lower while dynamic resolution holds the frame rate (read)", "Display", Namespace = "Engine.Display.Resolution", Flat = false)]
    public static double DisplayResolutionCurrent => Genesis.Shared.Rendering.RenderScale.Current;

    [PgslCommand("Enabled", "Engine.Display.Resolution.Dynamic.Enabled = true", "Dynamic resolution (false unless set): the scene is drawn smaller while its frames take too long for TargetFps, and larger again when there is room, between MinimumScale and Engine.Display.Resolution.Scale", "Display", Namespace = "Engine.Display.Resolution.Dynamic", Flat = false)]
    public static bool DisplayResolutionDynamicEnabled
    {
        get => Genesis.Shared.Rendering.RenderScale.Dynamic;
        set => Genesis.Shared.Rendering.RenderScale.Dynamic = value;
    }

    [PgslCommand("TargetFps", "Engine.Display.Resolution.Dynamic.TargetFps = 60", "The frame rate dynamic resolution holds, 10 to 500 (60 unless set)", "Display", Namespace = "Engine.Display.Resolution.Dynamic", Flat = false)]
    public static double DisplayResolutionDynamicTargetFps
    {
        get => Genesis.Shared.Rendering.RenderScale.TargetFps;
        set => Genesis.Shared.Rendering.RenderScale.TargetFps = Finite(value, 10, 500, 60f);
    }

    [PgslCommand("MinimumScale", "Engine.Display.Resolution.Dynamic.MinimumScale = 0.5", "The smallest share of the window's size dynamic resolution draws the scene at, 0.25 to 1 (0.5 unless set)", "Display", Namespace = "Engine.Display.Resolution.Dynamic", Flat = false)]
    public static double DisplayResolutionDynamicMinimumScale
    {
        get => Genesis.Shared.Rendering.RenderScale.MinimumScale;
        set => Genesis.Shared.Rendering.RenderScale.MinimumScale = Finite(value, Genesis.Shared.Rendering.RenderScale.Smallest, 1, 0.5f);
    }

    // ── Engine.Camera and its parts ──────────────────────────────────────────────────────

    [PgslCommand("Fov", "Engine.Camera.Fov = degrees", "The 3D camera's vertical field of view, 1 to 179 degrees (60 unless the room sets another)", "Camera", Namespace = "Engine.Camera", Flat = false)]
    public static double CameraApiFov
    {
        get => (ActiveGameContext?.Camera?.FieldOfView ?? MathF.PI / 3f) * 180.0 / Math.PI;
        set { if (ActiveGameContext?.Camera is { } camera && double.IsFinite(value)) camera.FieldOfView = (float)(Math.Clamp(value, 1, 179) * Math.PI / 180.0); }
    }

    [PgslCommand("Near", "Engine.Camera.Near = distance", "How close to the 3D camera things are still drawn (0.1 unless the room sets another)", "Camera", Namespace = "Engine.Camera", Flat = false)]
    public static double CameraApiNear
    {
        get => ActiveGameContext?.Camera?.NearPlane ?? 0.1;
        set { if (ActiveGameContext?.Camera is { } camera && double.IsFinite(value)) camera.NearPlane = (float)Math.Clamp(value, 0.001, Math.Max(0.002, camera.FarPlane - 0.001)); }
    }

    [PgslCommand("Far", "Engine.Camera.Far = distance", "How far from the 3D camera things are still drawn (the room's distance unless set)", "Camera", Namespace = "Engine.Camera", Flat = false)]
    public static double CameraApiFar
    {
        get => ActiveGameContext?.Camera?.FarPlane ?? 1000;
        set { if (ActiveGameContext?.Camera is { } camera && double.IsFinite(value)) camera.FarPlane = (float)Math.Clamp(value, camera.NearPlane + 0.001, 1_000_000); }
    }

    [PgslCommand("Enabled", "Engine.Camera.FreeFly.Enabled = true", "Fly the 3D camera with the mouse and WASD, for looking round a level (false unless set)", "Camera", Namespace = "Engine.Camera.FreeFly", Flat = false)]
    public static bool CameraFreeFlyEnabled { get => ActiveGameContext?.Scene?.FreeFlyCamera ?? false; set => CameraSetFreeFly(value); }

    [PgslCommand("Enabled", "Engine.Camera.Orbit.Enabled = true", "This instance's third-person camera, following it on a spring arm (true once any Orbit value is set)", "Camera", Namespace = "Engine.Camera.Orbit", Flat = false)]
    public static bool CameraOrbitEnabled { get => OrbitIfAny()?.Enabled ?? false; set { if (Orbit() is { } rig) rig.Enabled = value; } }

    [PgslCommand("Distance", "Engine.Camera.Orbit.Distance = distance", "How far behind this instance the orbit camera sits, 0.05 to 1000 (7.5 unless set)", "Camera", Namespace = "Engine.Camera.Orbit", Flat = false)]
    public static double CameraOrbitDistance { get => OrbitIfAny()?.Distance ?? 7.5; set => CameraSetOrbitDistance(value); }

    [PgslCommand("Yaw", "Engine.Camera.Orbit.Yaw = degrees", "Which way round this instance the orbit camera looks (0 unless set)", "Camera", Namespace = "Engine.Camera.Orbit", Flat = false)]
    public static double CameraOrbitYaw { get => OrbitIfAny()?.Yaw ?? 0; set => CameraSetOrbitYaw(value); }

    [PgslCommand("Pitch", "Engine.Camera.Orbit.Pitch = degrees", "How far the orbit camera looks down on this instance, -25 to 75 degrees (15 unless set)", "Camera", Namespace = "Engine.Camera.Orbit", Flat = false)]
    public static double CameraOrbitPitch { get => OrbitIfAny()?.Pitch ?? 15; set => CameraSetOrbitPitch(value); }

    [PgslCommand("MouseLook", "Engine.Camera.Orbit.MouseLook = true", "Whether moving the mouse turns the orbit camera (true unless set); off when a script turns it", "Camera", Namespace = "Engine.Camera.Orbit", Flat = false)]
    public static bool CameraOrbitMouseLook { get => OrbitIfAny()?.MouseLook ?? true; set => CameraSetOrbitMouseLook(value); }

    [PgslCommand("Sensitivity", "Engine.Camera.Orbit.Sensitivity = amount", "Degrees the orbit camera turns for each pixel the mouse moves, 0.01 to 2 (0.15 unless set)", "Camera", Namespace = "Engine.Camera.Orbit", Flat = false)]
    public static double CameraOrbitSensitivity { get => OrbitIfAny()?.Sensitivity ?? 0.15; set { if (Orbit() is { } rig) rig.Sensitivity = Finite(value, 0.01, 2, 0.15f); } }

    [PgslCommand("X", "Engine.Camera.Shoulder.X = distance", "How far to the side of this instance the orbit camera looks over (1.2 unless set; negative for the left shoulder)", "Camera", Namespace = "Engine.Camera.Shoulder", Flat = false)]
    public static double CameraShoulderX { get => OrbitIfAny()?.ShoulderOffset.X ?? 1.2; set { if (Orbit() is { } rig && double.IsFinite(value)) rig.ShoulderOffset = rig.ShoulderOffset with { X = (float)value }; } }

    [PgslCommand("Y", "Engine.Camera.Shoulder.Y = height", "How high above this instance the orbit camera looks from (2.4 unless set)", "Camera", Namespace = "Engine.Camera.Shoulder", Flat = false)]
    public static double CameraShoulderY { get => OrbitIfAny()?.ShoulderOffset.Y ?? 2.4; set { if (Orbit() is { } rig && double.IsFinite(value)) rig.ShoulderOffset = rig.ShoulderOffset with { Y = (float)value }; } }

    [PgslCommand("Z", "Engine.Camera.Shoulder.Z = distance", "How far forward of this instance the orbit camera's focus is (0 unless set)", "Camera", Namespace = "Engine.Camera.Shoulder", Flat = false)]
    public static double CameraShoulderZ { get => OrbitIfAny()?.ShoulderOffset.Z ?? 0; set { if (Orbit() is { } rig && double.IsFinite(value)) rig.ShoulderOffset = rig.ShoulderOffset with { Z = (float)value }; } }

    [PgslCommand("Enabled", "Engine.Camera.Collision.Enabled = true", "Whether the orbit camera moves in front of walls between it and this instance (true unless set)", "Camera", Namespace = "Engine.Camera.Collision", Flat = false)]
    public static bool CameraCollisionEnabled { get => OrbitIfAny()?.CollisionEnabled ?? true; set => CameraSetCollisionEnabled(value); }

    [PgslCommand("Clearance", "Engine.Camera.Collision.Clearance = distance", "How far the orbit camera keeps from a wall it moves in front of, 0 to 5 (0.25 unless set)", "Camera", Namespace = "Engine.Camera.Collision", Flat = false)]
    public static double CameraCollisionClearance { get => OrbitIfAny()?.Clearance ?? 0.25; set { if (Orbit() is { } rig) rig.Clearance = Finite(value, 0, 5, 0.25f); } }

    [PgslCommand("RecoverySpeed", "Engine.Camera.Collision.RecoverySpeed = speed", "How quickly the orbit camera moves back out once the wall is gone, 0.1 to 100 (10 unless set)", "Camera", Namespace = "Engine.Camera.Collision", Flat = false)]
    public static double CameraCollisionRecoverySpeed { get => OrbitIfAny()?.RecoverySpeed ?? 10; set { if (Orbit() is { } rig) rig.RecoverySpeed = Finite(value, 0.1, 100, 10f); } }

    [PgslCommand("Start", "Engine.Camera.Shake.Start(strength, seconds)", "Shake the 3D view, dying away over the seconds given", "Camera", Namespace = "Engine.Camera.Shake", Flat = false)]
    public static void CameraShakeStart(double strength, double seconds) => CameraShake3D(strength, seconds);

    [PgslCommand("Roll", "Engine.Camera.Shake.Roll(strength, seconds, degrees)", "Shake the 3D view and tilt it by up to this many degrees, dying away over the seconds given", "Camera", Namespace = "Engine.Camera.Shake", Flat = false)]
    public static void CameraShakeRoll(double strength, double seconds, double degrees) => CameraShakeRoll3D(strength, seconds, degrees);

    [PgslCommand("Enabled", "Engine.Camera.Focus.Enabled = true", "Depth of field: what is near the focus distance stays sharp, nearer and farther things blur (false unless set)", "Camera", Namespace = "Engine.Camera.Focus", Flat = false)]
    public static bool CameraFocusEnabled { get => DepthOfField.Enabled; set => DepthOfField.Enabled = value; }

    [PgslCommand("Auto", "Engine.Camera.Focus.Auto = true", "Focus on whatever is in the middle of the screen instead of the focus distance (false unless set)", "Camera", Namespace = "Engine.Camera.Focus", Flat = false)]
    public static bool CameraFocusAuto { get => DepthOfField.Auto; set => DepthOfField.Auto = value; }

    [PgslCommand("Distance", "Engine.Camera.Focus.Distance = metres", "How far from the camera things are sharpest, 0.1 to 100000 (10 unless set)", "Camera", Namespace = "Engine.Camera.Focus", Flat = false)]
    public static double CameraFocusDistance { get => DepthOfField.Distance; set => DepthOfField.Distance = (float)value; }

    [PgslCommand("Range", "Engine.Camera.Focus.Range = metres", "How far either side of the focus distance stays sharp, 0 to 100000 (4 unless set)", "Camera", Namespace = "Engine.Camera.Focus", Flat = false)]
    public static double CameraFocusRange { get => DepthOfField.Range; set => DepthOfField.Range = (float)value; }

    [PgslCommand("Blur", "Engine.Camera.Focus.Blur = pixels", "The strongest blur, in pixels of a 1080-line picture, 0 to 32 (8 unless set)", "Camera", Namespace = "Engine.Camera.Focus", Flat = false)]
    public static double CameraFocusBlur { get => DepthOfField.Blur; set => DepthOfField.Blur = (float)value; }

    // ── Engine.Rendering's parts ─────────────────────────────────────────────────────────

    [PgslCommand("Enabled", "Engine.Rendering.Shadows.Enabled = true", "The sun's shadows (true unless the room turns them off)", "Rendering", Namespace = "Engine.Rendering.Shadows", Flat = false)]
    public static bool RenderingShadowsEnabled { get => RoomLook?.ShadowsEnabled ?? true; set { if (RoomLook is { } look) look.ShadowsEnabled = value; } }

    [PgslCommand("Strength", "Engine.Rendering.Shadows.Strength = amount", "How dark shadows are, 0 (none) to 1 (1 unless the room sets another)", "Rendering", Namespace = "Engine.Rendering.Shadows", Flat = false)]
    public static double RenderingShadowsStrength { get => RoomLook?.ShadowStrength ?? 1; set { if (RoomLook is { } look) look.ShadowStrength = Finite(value, 0, 1, 1f); } }

    [PgslCommand("Distance", "Engine.Rendering.Shadows.Distance = metres", "How far from the camera the sun's shadows reach, 0 for automatic (0 unless the room sets another)", "Rendering", Namespace = "Engine.Rendering.Shadows", Flat = false)]
    public static double RenderingShadowsDistance { get => RoomLook?.ShadowDistance ?? 0; set { if (RoomLook is { } look) look.ShadowDistance = Finite(value, 0, 20000, 0f); } }

    [PgslCommand("Bias", "Engine.Rendering.Shadows.Bias = amount", "How far shadows are pushed off the surfaces casting them, against speckled self-shadowing, 0 to 0.01 (0.00015 unless set)", "Rendering", Namespace = "Engine.Rendering.Shadows", Flat = false)]
    public static double RenderingShadowsBias { get => RoomLook?.ShadowBias ?? 0.00015; set { if (RoomLook is { } look) look.ShadowBias = Finite(value, 0, 0.01, 0.00015f); } }

    [PgslCommand("Cascades", "Engine.Rendering.Shadows.Cascades = 3", "How many shadow maps share the view, 2 or 3 (the quality tier's unless set)", "Rendering", Namespace = "Engine.Rendering.Shadows", Flat = false)]
    public static double RenderingShadowsCascades { get => CascadeCount; set { if (double.IsFinite(value)) CascadeCount = (int)Math.Round(value); } }

    [PgslCommand("Resolution", "Engine.Rendering.Shadows.Resolution = 2048", "The sun's shadow map size in pixels (the quality tier's unless set)", "Rendering", Namespace = "Engine.Rendering.Shadows", Flat = false)]
    public static double RenderingShadowsResolution { get => ShadowResolution; set { if (double.IsFinite(value)) ShadowResolution = (int)Math.Round(value); } }

    [PgslCommand("Contact", "Engine.Rendering.Shadows.Contact = true", "Small screen-space shadows where things touch (the quality tier's unless set)", "Rendering", Namespace = "Engine.Rendering.Shadows", Flat = false)]
    public static bool RenderingShadowsContact { get => ContactShadowsEnabled; set => ContactShadowsEnabled = value; }

    [PgslCommand("LampBudget", "Engine.Rendering.Shadows.LampBudget = count", "How many point and spot lights may cast shadows at once (the quality tier's unless set)", "Rendering", Namespace = "Engine.Rendering.Shadows", Flat = false)]
    public static double RenderingShadowsLampBudget { get => OmniShadowBudget; set { if (double.IsFinite(value)) OmniShadowBudget = (int)Math.Round(value); } }

    [PgslCommand("Enabled", "Engine.Rendering.Fog.Enabled = true", "Distance fog (off unless the room turns it on)", "Rendering", Namespace = "Engine.Rendering.Fog", Flat = false)]
    public static bool RenderingFogEnabled { get => RoomLook?.FogEnabled ?? false; set { if (RoomLook is { } look) look.FogEnabled = value; } }

    [PgslCommand("Start", "Engine.Rendering.Fog.Start = metres", "Where the fog begins (35 unless the room sets another)", "Rendering", Namespace = "Engine.Rendering.Fog", Flat = false)]
    public static double RenderingFogStart { get => RoomLook?.FogStart ?? 35; set { if (RoomLook is { } look) look.FogStart = Finite(value, 0, 1_000_000, 35f); } }

    [PgslCommand("End", "Engine.Rendering.Fog.End = metres", "Where the fog hides everything (120 unless the room sets another)", "Rendering", Namespace = "Engine.Rendering.Fog", Flat = false)]
    public static double RenderingFogEnd { get => RoomLook?.FogEnd ?? 120; set { if (RoomLook is { } look) look.FogEnd = Finite(value, 0.01, 1_000_000, 120f); } }

    [PgslCommand("Density", "Engine.Rendering.Fog.Density = amount", "How thick the fog is, 0 to 1 (0.012 unless the room sets another)", "Rendering", Namespace = "Engine.Rendering.Fog", Flat = false)]
    public static double RenderingFogDensity { get => RoomLook?.FogDensity ?? 0.012; set { if (RoomLook is { } look) look.FogDensity = Finite(value, 0, 1, 0.012f); } }

    [PgslCommand("HeightBase", "Engine.Rendering.Fog.HeightBase = height", "The height fog thins out above (0 unless set)", "Rendering", Namespace = "Engine.Rendering.Fog", Flat = false)]
    public static double RenderingFogHeightBase { get => RoomLook?.FogHeightBase ?? 0; set { if (RoomLook is { } look) look.FogHeightBase = Finite(value, -100000, 100000, 0f); } }

    [PgslCommand("HeightFalloff", "Engine.Rendering.Fog.HeightFalloff = amount", "How quickly fog thins with height, 0 (even) to 10 (0.35 unless set)", "Rendering", Namespace = "Engine.Rendering.Fog", Flat = false)]
    public static double RenderingFogHeightFalloff { get => RoomLook?.FogHeightFalloff ?? 0.35; set { if (RoomLook is { } look) look.FogHeightFalloff = Finite(value, 0, 10, 0.35f); } }

    [PgslCommand("Noise", "Engine.Rendering.Fog.Noise = amount", "How patchy the fog is, 0 to 1 (0.04 unless set)", "Rendering", Namespace = "Engine.Rendering.Fog", Flat = false)]
    public static double RenderingFogNoise { get => RoomLook?.FogNoiseStrength ?? 0.04; set { if (RoomLook is { } look) look.FogNoiseStrength = Finite(value, 0, 1, 0.04f); } }

    [PgslCommand("Color", "Engine.Rendering.Fog.Color = \"#a0c8f0\"", "The fog's colour as #rrggbb (the room's unless set)", "Rendering", Namespace = "Engine.Rendering.Fog", Flat = false)]
    public static string RenderingFogColor
    {
        get => HexColor(RoomLook?.FogColor ?? new Vector4(0.62f, 0.78f, 0.94f, 1f));
        set { if (RoomLook is { } look && TryHexColor(value, out Vector3 rgb)) look.FogColor = new Vector4(rgb, look.FogColor.W <= 0 ? 1f : look.FogColor.W); }
    }

    [PgslCommand("Shafts", "Engine.Rendering.Fog.Shafts = 1", "Volumetric fog and its light shafts: 0 off, 1 or 2 for more detail (the quality tier's unless set)", "Rendering", Namespace = "Engine.Rendering.Fog", Flat = false)]
    public static double RenderingFogShafts { get => VolumetricFogQuality; set { if (double.IsFinite(value)) VolumetricFogQuality = (int)Math.Clamp(Math.Round(value), 0, 2); } }

    [PgslCommand("Enabled", "Engine.Rendering.Ao.Enabled = true", "Ambient occlusion: soft darkening in corners and creases (the quality tier's unless set)", "Rendering", Namespace = "Engine.Rendering.Ao", Flat = false)]
    public static bool RenderingAoEnabled { get => GtaoEnabled; set => GtaoEnabled = value; }

    [PgslCommand("Enabled", "Engine.Rendering.Bloom.Enabled = true", "A glow round bright light (the quality tier's unless set)", "Rendering", Namespace = "Engine.Rendering.Bloom", Flat = false)]
    public static bool RenderingBloomEnabled { get => BloomEnabled; set => BloomEnabled = value; }

    [PgslCommand("Value", "Engine.Rendering.Exposure.Value = amount", "How bright the picture is made, 0.1 to 8 (1 unless set)", "Rendering", Namespace = "Engine.Rendering.Exposure", Flat = false)]
    public static double RenderingExposureValue { get => Exposure; set { if (double.IsFinite(value)) Exposure = (float)value; } }

    [PgslCommand("Auto", "Engine.Rendering.Exposure.Auto = true", "Eye adaptation: the picture brightens in the dark and darkens in bright light (off unless the room turns it on)", "Rendering", Namespace = "Engine.Rendering.Exposure", Flat = false)]
    public static bool RenderingExposureAuto { get => RenderingAutoExposureEnabled; set => RenderingAutoExposureEnabled = value; }

    [PgslCommand("Contrast", "Engine.Rendering.Grading.Contrast = amount", "The picture's contrast (1 unless set)", "Rendering", Namespace = "Engine.Rendering.Grading", Flat = false)]
    public static double RenderingGradingContrast { get => Contrast; set { if (double.IsFinite(value)) Contrast = (float)value; } }

    [PgslCommand("Saturation", "Engine.Rendering.Grading.Saturation = amount", "How colourful the picture is: 0 grey, 1 as drawn (1 unless set)", "Rendering", Namespace = "Engine.Rendering.Grading", Flat = false)]
    public static double RenderingGradingSaturation { get => Saturation; set { if (double.IsFinite(value)) Saturation = (float)value; } }

    [PgslCommand("Vignette", "Engine.Rendering.Grading.Vignette = amount", "How much the picture darkens towards its corners, 0 to 1 (0 unless set)", "Rendering", Namespace = "Engine.Rendering.Grading", Flat = false)]
    public static double RenderingGradingVignette { get => Vignette; set { if (double.IsFinite(value)) Vignette = (float)value; } }

    [PgslCommand("Mode", "Engine.Rendering.AntiAliasing.Mode = \"smaa\"", "Smoothing of jagged edges: \"off\", \"fxaa\", \"smaa\" or \"taa\" (the quality tier's unless set)", "Rendering", Namespace = "Engine.Rendering.AntiAliasing", Flat = false)]
    public static string RenderingAntiAliasingMode { get => AntiAliasing; set => AntiAliasing = value; }

    [PgslCommand("Strength", "Engine.Rendering.MotionBlur.Strength = amount", "Blur along movement, 0 (off) to 1 (0 unless set)", "Rendering", Namespace = "Engine.Rendering.MotionBlur", Flat = false)]
    public static double RenderingMotionBlurStrength { get => MotionBlur; set { if (double.IsFinite(value)) MotionBlur = (float)value; } }

    [PgslCommand("Frustum", "Engine.Rendering.Culling.Frustum = true", "Skip drawing what is outside the view (true unless set)", "Rendering", Namespace = "Engine.Rendering.Culling", Flat = false)]
    public static bool RenderingCullingFrustum { get => RenderAutoState.FrustumCulling; set => RenderAutoState.FrustumCulling = value; }

    [PgslCommand("Occlusion", "Engine.Rendering.Culling.Occlusion = false", "Skip drawing what is hidden behind large things (false unless set)", "Rendering", Namespace = "Engine.Rendering.Culling", Flat = false)]
    public static bool RenderingCullingOcclusion { get => RenderAutoState.OcclusionCulling; set => RenderAutoState.OcclusionCulling = value; }

    [PgslCommand("Enabled", "Engine.Rendering.Detail.Enabled = true", "Automatic detail: models far away drawn simpler and tiny ones not at all (true unless set)", "Rendering", Namespace = "Engine.Rendering.Detail", Flat = false)]
    public static bool RenderingDetailEnabled { get => Modeling.ModelLodView.Enabled; set => Modeling.ModelLodView.Enabled = value; }

    [PgslCommand("Quality", "Engine.Rendering.Detail.Quality = amount", "How long models keep full detail as they shrink on screen, 0.05 to 8: 2 keeps it to half the size (1 unless set)", "Rendering", Namespace = "Engine.Rendering.Detail", Flat = false)]
    public static double RenderingDetailQuality { get => Modeling.ModelLodView.Quality; set => Modeling.ModelLodView.Quality = Finite(value, 0.05, 8, 1f); }

    [PgslCommand("CullSize", "Engine.Rendering.Detail.CullSize = fraction", "Models smaller than this share of the screen's height are not drawn, 0 to 0.1 (0.0025 unless set)", "Rendering", Namespace = "Engine.Rendering.Detail", Flat = false)]
    public static double RenderingDetailCullSize { get => Modeling.ModelLodView.CullSize; set => Modeling.ModelLodView.CullSize = Finite(value, 0, 0.1, 0.0025f); }

    [PgslCommand("AnimationBySize", "Engine.Rendering.Detail.AnimationBySize = true", "Figures small on screen are posed less often (true unless set)", "Rendering", Namespace = "Engine.Rendering.Detail", Flat = false)]
    public static bool RenderingDetailAnimationBySize { get => Modeling.ModelLodView.AnimationRateBySize; set => Modeling.ModelLodView.AnimationRateBySize = value; }

    // ── Engine.Rendering: what the last frame cost, for a game's own performance display or quality choice ──

    private static Genesis.Shared.Interfaces.IRenderController? GameRenderer => (ActiveGameContext as ProjectGameContext)?.Renderer;

    private static Genesis.Shared.Interfaces.RenderStats LastFrame()
    {
        try { return GameRenderer?.GetStats() ?? default; }
        catch (InvalidOperationException) { return default; }
    }

    [PgslCommand("Backend", "Engine.Rendering.Backend", "The graphics the game draws with: \"Direct3D 11\", \"Direct3D 12\", \"Vulkan\", \"OpenGL\" or \"Software\" (read)", "Rendering", Namespace = "Engine.Rendering", Flat = false)]
    public static string RenderingBackend => GameRenderer?.BackendName ?? "";

    [PgslCommand("DrawCalls", "Engine.Rendering.DrawCalls", "Draw calls the last frame made, 2D and 3D (read)", "Rendering", Namespace = "Engine.Rendering", Flat = false)]
    public static double RenderingDrawCalls => LastFrame().DrawCalls;

    [PgslCommand("Triangles", "Engine.Rendering.Triangles", "Triangles the last frame drew (read)", "Rendering", Namespace = "Engine.Rendering", Flat = false)]
    public static double RenderingTriangles => LastFrame().Triangles;

    [PgslCommand("GpuMs", "Engine.Rendering.GpuMs", "Milliseconds the graphics card spent on the last 3D scene it has finished (read; 0 where the renderer cannot tell)", "Rendering", Namespace = "Engine.Rendering", Flat = false)]
    public static double RenderingGpuMs => LastFrame().GpuMs;

    [PgslCommand("Drawn", "Engine.Rendering.Instances.Drawn", "Instances the last frame drew (read)", "Rendering", Namespace = "Engine.Rendering.Instances", Flat = false)]
    public static double RenderingInstancesDrawn => LastFrame().InstancesDrawn;

    [PgslCommand("Culled", "Engine.Rendering.Instances.Culled", "Instances the last frame left out as unseen (read)", "Rendering", Namespace = "Engine.Rendering.Instances", Flat = false)]
    public static double RenderingInstancesCulled => LastFrame().InstancesCulled;

    // ── Engine.Water.Surface: the look of every water body in the room's terrains ────────

    private static Genesis.World.Water.WaterMaterialSettings WaterLook()
    {
        Genesis.World.Water.WaterMaterialSettings? first = null;
        TerrainWaters(body => first ??= body.Material);
        return first ?? Genesis.World.Water.WaterMaterialSettings.Default;
    }

    private static void TerrainWaters(Action<Genesis.World.Water.WaterBody> visit)
    {
        if (ActiveGameContext?.Scene is not { } scene) return;
        foreach (var subsystem in scene.Subsystems)
            if (subsystem is RoomTerrainSubsystem terrains) terrains.ForEachWaterBody(visit);
    }

    // Changes one value of every water body's look, keeping the rest.
    private static void SetWaterLook(Func<Genesis.World.Water.WaterMaterialSettings, Genesis.World.Water.WaterMaterialSettings> change) =>
        TerrainWaters(body => body.Material = change(body.Material));

    private static string Hex(System.Numerics.Vector3 colour) =>
        $"#{(int)Math.Clamp(colour.X * 255f + 0.5f, 0, 255):x2}{(int)Math.Clamp(colour.Y * 255f + 0.5f, 0, 255):x2}{(int)Math.Clamp(colour.Z * 255f + 0.5f, 0, 255):x2}";

    [PgslCommand("Waves", "Engine.Water.Surface.Waves = 0.2", "How high the water's waves rise, 0 to 5 metres, on every water body of the room's terrains (each body's own unless set; 0.08 for new water)", "Water", Namespace = "Engine.Water.Surface", Flat = false)]
    public static double WaterSurfaceWaves
    {
        get => WaterLook().WaveAmplitude;
        set { if (double.IsFinite(value)) SetWaterLook(look => { look.WaveAmplitude = (float)Math.Clamp(value, 0, 5); return look; }); }
    }

    [PgslCommand("WaveSpeed", "Engine.Water.Surface.WaveSpeed = 0.6", "How fast the water's surface moves and flows, 0 to 10 (each body's own unless set; 0.35 for new water)", "Water", Namespace = "Engine.Water.Surface", Flat = false)]
    public static double WaterSurfaceWaveSpeed
    {
        get => WaterLook().FlowSpeed;
        set { if (double.IsFinite(value)) SetWaterLook(look => { look.FlowSpeed = (float)Math.Clamp(value, 0, 10); return look; }); }
    }

    [PgslCommand("FoamWidth", "Engine.Water.Surface.FoamWidth = 2", "How wide the foam is where the water meets the shore, 0 to 10 metres (each body's own unless set; 1.25 for new water)", "Water", Namespace = "Engine.Water.Surface", Flat = false)]
    public static double WaterSurfaceFoamWidth
    {
        get => WaterLook().FoamWidth;
        set { if (double.IsFinite(value)) SetWaterLook(look => { look.FoamWidth = (float)Math.Clamp(value, 0, 10); return look; }); }
    }

    [PgslCommand("Clarity", "Engine.Water.Surface.Clarity = 0.8", "How far you see into the water, 0 (opaque) to 1 (clear) (each body's own unless set; 0.5 for new water): murky after a storm, clear in a spring", "Water", Namespace = "Engine.Water.Surface", Flat = false)]
    public static double WaterSurfaceClarity
    {
        get => 1 - WaterLook().Opacity;
        set { if (double.IsFinite(value)) SetWaterLook(look => { look.Opacity = 1f - (float)Math.Clamp(value, 0, 1); return look; }); }
    }

    [PgslCommand("ShallowColor", "Engine.Water.Surface.ShallowColor = \"#2e9eb8\"", "The water's colour where it is shallow, as \"#rrggbb\" (each body's own unless set)", "Water", Namespace = "Engine.Water.Surface", Flat = false)]
    public static string WaterSurfaceShallowColor
    {
        get => Hex(WaterLook().ShallowColor);
        set { if (TryHexColor(value, out System.Numerics.Vector3 colour)) SetWaterLook(look => { look.ShallowColor = colour; return look; }); }
    }

    [PgslCommand("DeepColor", "Engine.Water.Surface.DeepColor = \"#0a2e60\"", "The water's colour where it is deep, as \"#rrggbb\" (each body's own unless set)", "Water", Namespace = "Engine.Water.Surface", Flat = false)]
    public static string WaterSurfaceDeepColor
    {
        get => Hex(WaterLook().DeepColor);
        set { if (TryHexColor(value, out System.Numerics.Vector3 colour)) SetWaterLook(look => { look.DeepColor = colour; return look; }); }
    }

    // Foam widths put by while Engine.Water.Surface.Foam is off, for when it comes back on.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Genesis.World.Water.WaterBody, System.Runtime.CompilerServices.StrongBox<float>> _foamPutBy = new();

    [PgslCommand("Enabled", "Engine.Water.Surface.Foam.Enabled = false", "Whether foam is drawn where the room's water meets the shore (true unless set); off keeps each body's foam width for when it comes back", "Water", Namespace = "Engine.Water.Surface.Foam", Flat = false)]
    public static bool WaterSurfaceFoamEnabled
    {
        get
        {
            bool on = true;
            TerrainWaters(body => on &= !_foamPutBy.TryGetValue(body, out _));
            return on;
        }
        set => TerrainWaters(body =>
        {
            Genesis.World.Water.WaterMaterialSettings look = body.Material;
            if (!value && !_foamPutBy.TryGetValue(body, out _))
            {
                _foamPutBy.Add(body, new System.Runtime.CompilerServices.StrongBox<float>(look.FoamWidth));
                look.FoamWidth = 0f;
            }
            else if (value && _foamPutBy.TryGetValue(body, out var width))
            {
                look.FoamWidth = width.Value;
                _foamPutBy.Remove(body);
            }
            body.Material = look;
        });
    }

    // The room's terrains and its physics, for the area's switch and the swimming values.
    private static RuntimeScene? WaterScene() => ActiveGameContext?.Scene;

    [PgslCommand("Enabled", "Engine.Water.Enabled = false", "Whether the room's water is there: drawn, and lifting and slowing bodies in it (true unless set); off for a drained lake or a dream", "Water", Namespace = "Engine.Water", Flat = false)]
    public static bool WaterEnabled
    {
        get => WaterScene()?.Physics?.WaterEnabled ?? true;
        set
        {
            if (WaterScene() is not { } scene) return;
            if (scene.Physics != null) scene.Physics.WaterEnabled = value;
            foreach (var subsystem in scene.Subsystems)
                if (subsystem is RoomTerrainSubsystem terrains) terrains.WaterEnabled = value;
        }
    }

    [PgslCommand("Buoyancy", "Engine.Water.Swimming.Buoyancy = 2", "How strongly the room's water lifts bodies, 0 to 10 times each water's own (1 unless set): 0 and things sink like stones, 2 for a salt lake", "Water", Namespace = "Engine.Water.Swimming", Flat = false)]
    public static double WaterSwimmingBuoyancy
    {
        get => WaterScene()?.Physics?.WaterBuoyancyScale ?? 1;
        set { if (double.IsFinite(value) && WaterScene()?.Physics is { } physics) physics.WaterBuoyancyScale = (float)Math.Clamp(value, 0, 10); }
    }

    [PgslCommand("Drag", "Engine.Water.Swimming.Drag = 3", "How strongly the room's water slows bodies moving in it, 0 to 10 times each water's own (1 unless set): more for a swamp, less for a fast river", "Water", Namespace = "Engine.Water.Swimming", Flat = false)]
    public static double WaterSwimmingDrag
    {
        get => WaterScene()?.Physics?.WaterDragScale ?? 1;
        set { if (double.IsFinite(value) && WaterScene()?.Physics is { } physics) physics.WaterDragScale = (float)Math.Clamp(value, 0, 10); }
    }

    [PgslCommand("Resolution", "Engine.Water.Reflections.Resolution = 1", "The share of the view's size the water's reflection is drawn at, 0.25 to 1 (0.5 unless set): sharper at 1, cheaper at 0.25", "Water", Namespace = "Engine.Water.Reflections", Flat = false)]
    public static double WaterReflectionsResolution
    {
        get => Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflectionResolution;
        set { if (double.IsFinite(value)) Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflectionResolution = (float)Math.Clamp(value, 0.25, 1); }
    }

    [PgslCommand("Distortion", "Engine.Water.Reflections.Distortion = 2", "How much the water's waves ripple its reflection, 0 to 5 times the usual (1 unless set): 0 for a still mirror", "Water", Namespace = "Engine.Water.Reflections", Flat = false)]
    public static double WaterReflectionsDistortion
    {
        get => Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflectionDistortion;
        set { if (double.IsFinite(value)) Genesis.Rendering.Core.EngineRenderingDefaults.WaterReflectionDistortion = (float)Math.Clamp(value, 0, 5); }
    }

    // ── Engine.Terrain: the room's ground, changed as the game plays ─────────────────────

    private static bool EditGround(RoomTerrainSubsystem.GroundEdit edit, double x, double z, double radius, double amount, int layer = 1)
    {
        if (ActiveGameContext?.Scene is not { } scene) return false;
        bool changed = false;
        foreach (var subsystem in scene.Subsystems)
            if (subsystem is RoomTerrainSubsystem terrains)
                changed |= terrains.Edit(scene, edit, (float)x, (float)z, (float)Math.Clamp(radius, 0, 10000), (float)amount, layer);
        return changed;
    }

    [PgslCommand("Raise", "Engine.Terrain.Edit.Raise(x, z, radius, metres) -> bool", "Raises the ground of the room's terrains around world x, z by up to the metres given at the centre, falling off smoothly to the radius (a mound, a bank of earth); its look, grass and collision follow at once and last while the room runs; false where no terrain is", "Terrain", Namespace = "Engine.Terrain.Edit", Flat = false)]
    public static bool TerrainEditRaise(double x, double z, double radius, double metres) =>
        EditGround(RoomTerrainSubsystem.GroundEdit.Raise, x, z, radius, metres);

    [PgslCommand("Lower", "Engine.Terrain.Edit.Lower(x, z, radius, metres) -> bool", "Lowers the ground around world x, z by up to the metres given at the centre (a crater where a shell lands, a hole a player digs); false where no terrain is", "Terrain", Namespace = "Engine.Terrain.Edit", Flat = false)]
    public static bool TerrainEditLower(double x, double z, double radius, double metres) =>
        EditGround(RoomTerrainSubsystem.GroundEdit.Lower, x, z, radius, metres);

    [PgslCommand("Flatten", "Engine.Terrain.Edit.Flatten(x, z, radius, height) -> bool", "Levels the ground around world x, z to the world height given, blending out to the radius (a floor for a building a player puts down); false where no terrain is", "Terrain", Namespace = "Engine.Terrain.Edit", Flat = false)]
    public static bool TerrainEditFlatten(double x, double z, double radius, double height) =>
        EditGround(RoomTerrainSubsystem.GroundEdit.Flatten, x, z, radius, height);

    [PgslCommand("Smooth", "Engine.Terrain.Edit.Smooth(x, z, radius, strength) -> bool", "Softens the ground around world x, z toward its neighbours' heights, by 0 to 1 (an edge worn down); false where no terrain is", "Terrain", Namespace = "Engine.Terrain.Edit", Flat = false)]
    public static bool TerrainEditSmooth(double x, double z, double radius, double strength) =>
        EditGround(RoomTerrainSubsystem.GroundEdit.Smooth, x, z, radius, strength);

    [PgslCommand("Paint", "Engine.Terrain.Edit.Paint(x, z, radius, layer, strength) -> bool", "Paints the terrain's layer 1 to 8 around world x, z at 0 to 1 strength (scorched earth, a path worn to dirt, snow settling); false where no terrain is", "Terrain", Namespace = "Engine.Terrain.Edit", Flat = false)]
    public static bool TerrainEditPaint(double x, double z, double radius, double layer, double strength) =>
        double.IsFinite(layer) && EditGround(RoomTerrainSubsystem.GroundEdit.Paint, x, z, radius, strength, (int)Math.Round(layer));

    // The room's terrain subsystems: each value is set on all and read from the first.
    private static void ForTerrains(Action<RoomTerrainSubsystem> change)
    {
        if (ActiveGameContext?.Scene is not { } scene) return;
        foreach (var subsystem in scene.Subsystems)
            if (subsystem is RoomTerrainSubsystem terrains) change(terrains);
    }

    private static RoomTerrainSubsystem? FirstTerrains()
    {
        RoomTerrainSubsystem? first = null;
        ForTerrains(terrains => first ??= terrains);
        return first;
    }

    private static System.Numerics.Vector3 TerrainNormal(double x, double z)
    {
        System.Numerics.Vector3 normal = System.Numerics.Vector3.UnitY;
        if (double.IsFinite(x) && double.IsFinite(z)) FirstTerrains()?.TrySurface((float)x, (float)z, out _, out normal);
        return normal;
    }

    [PgslCommand("Enabled", "Engine.Terrain.Enabled = false", "Whether the room's terrains' ground and paths are drawn (true unless set); what stands on it still stands, for an x-ray or a map view", "Terrain", Namespace = "Engine.Terrain", Flat = false)]
    public static bool TerrainEnabled { get => FirstTerrains()?.GroundDrawn ?? true; set => ForTerrains(terrains => terrains.GroundDrawn = value); }

    [PgslCommand("NormalX", "Engine.Terrain.NormalX(x, z) -> number", "Which way the ground at world x, z faces, across x (with NormalY and NormalZ, a direction of length 1; straight up where no terrain is)", "Terrain", Namespace = "Engine.Terrain", Flat = false)]
    public static double TerrainNormalX(double x, double z) => TerrainNormal(x, z).X;

    [PgslCommand("NormalY", "Engine.Terrain.NormalY(x, z) -> number", "Which way the ground at world x, z faces, upward: 1 on the flat, less the steeper it is", "Terrain", Namespace = "Engine.Terrain", Flat = false)]
    public static double TerrainNormalY(double x, double z) => TerrainNormal(x, z).Y;

    [PgslCommand("NormalZ", "Engine.Terrain.NormalZ(x, z) -> number", "Which way the ground at world x, z faces, across z", "Terrain", Namespace = "Engine.Terrain", Flat = false)]
    public static double TerrainNormalZ(double x, double z) => TerrainNormal(x, z).Z;

    [PgslCommand("SlopeAt", "Engine.Terrain.SlopeAt(x, z) -> degrees", "How steep the ground at world x, z is, 0 (flat) to 90 degrees: a vehicle that cannot climb, a skier that speeds up", "Terrain", Namespace = "Engine.Terrain", Flat = false)]
    public static double TerrainSlopeAt(double x, double z) =>
        Math.Acos(Math.Clamp(TerrainNormal(x, z).Y, -1, 1)) * 180 / Math.PI;

    [PgslCommand("MaterialAt", "Engine.Terrain.MaterialAt(x, z) -> layer", "The painted layer (1 to 8) most of the ground at world x, z is, 0 where no terrain is: footstep sounds, grip, where crops grow", "Terrain", Namespace = "Engine.Terrain", Flat = false)]
    public static double TerrainMaterialAt(double x, double z) =>
        double.IsFinite(x) && double.IsFinite(z) ? FirstTerrains()?.LayerAt((float)x, (float)z) ?? 0 : 0;

    private static (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)? TerrainExtent() =>
        FirstTerrains() is { } terrains && terrains.TryGetNavigationBounds(out var min, out var max) ? (min, max) : null;

    [PgslCommand("Width", "Engine.Terrain.Width", "How wide the room's loaded terrains are together, along x, in metres (read; 0 without a terrain)", "Terrain", Namespace = "Engine.Terrain", Flat = false)]
    public static double TerrainWidth => TerrainExtent() is { } extent ? extent.Max.X - extent.Min.X : 0;

    [PgslCommand("Depth", "Engine.Terrain.Depth", "How deep the room's loaded terrains are together, along z, in metres (read; 0 without a terrain)", "Terrain", Namespace = "Engine.Terrain", Flat = false)]
    public static double TerrainDepth => TerrainExtent() is { } extent ? extent.Max.Z - extent.Min.Z : 0;

    [PgslCommand("Distance", "Engine.Terrain.Detail.Distance = 2000", "How near the camera a streamed terrain tile is read, 0 to 1000000 metres (the room's terrain distance unless set); only for a room that streams its terrains", "Terrain", Namespace = "Engine.Terrain.Detail", Flat = false)]
    public static double TerrainDetailDistance
    {
        get => FirstTerrains()?.TerrainDistance ?? 0;
        set { if (double.IsFinite(value)) ForTerrains(terrains => terrains.TerrainDistance = (float)value); }
    }

    [PgslCommand("Enabled", "Engine.Terrain.Grass.Enabled = false", "Grass grown round the camera on every terrain, or none (each terrain's own rule unless set): off for a burnt field", "Terrain", Namespace = "Engine.Terrain.Grass", Flat = false)]
    public static bool TerrainGrassEnabled
    {
        get => FirstTerrains()?.GrassOn ?? true;
        set => ForTerrains(terrains => terrains.GrassOn = value);
    }

    [PgslCommand("Density", "Engine.Terrain.Grass.Density = 0.5", "How thickly the grass grows, 0.05 to 4 times each terrain's own (1 unless set): thinner on a slow machine", "Terrain", Namespace = "Engine.Terrain.Grass", Flat = false)]
    public static double TerrainGrassDensity
    {
        get => FirstTerrains()?.GrassDensity ?? 1;
        set { if (double.IsFinite(value)) ForTerrains(terrains => terrains.GrassDensity = (float)Math.Clamp(value, 0.05, 4)); }
    }

    [PgslCommand("Distance", "Engine.Terrain.Grass.Distance = 2", "How far from the camera grass grows, 0.1 to 10 times each terrain's own (1 unless set)", "Terrain", Namespace = "Engine.Terrain.Grass", Flat = false)]
    public static double TerrainGrassDistance
    {
        get => FirstTerrains()?.GrassDistance ?? 1;
        set { if (double.IsFinite(value)) ForTerrains(terrains => terrains.GrassDistance = (float)Math.Clamp(value, 0.1, 10)); }
    }

    [PgslCommand("Height", "Engine.Terrain.Grass.Height = 1.5", "How tall the grass grows, 0.1 to 4 times each terrain's own (1 unless set): long grass after rain, cut grass in a garden", "Terrain", Namespace = "Engine.Terrain.Grass", Flat = false)]
    public static double TerrainGrassHeight
    {
        get => FirstTerrains()?.GrassHeight ?? 1;
        set { if (double.IsFinite(value)) ForTerrains(terrains => terrains.GrassHeight = (float)Math.Clamp(value, 0.1, 4)); }
    }

    [PgslCommand("Enabled", "Engine.Terrain.Scatter.Enabled = false", "Whether the terrains' scattered trees and rocks are drawn (true unless set); their collision stays", "Terrain", Namespace = "Engine.Terrain.Scatter", Flat = false)]
    public static bool TerrainScatterEnabled { get => FirstTerrains()?.ScatterDrawn ?? true; set => ForTerrains(terrains => terrains.ScatterDrawn = value); }

    [PgslCommand("Density", "Engine.Terrain.Scatter.Density = 0.5", "How many trees and rocks the terrains scatter, 0 to 4 times each layer's own (1 unless set): the copies and their collision are placed again", "Terrain", Namespace = "Engine.Terrain.Scatter", Flat = false)]
    public static double TerrainScatterDensity
    {
        get => FirstTerrains()?.ScatterDensity ?? 1;
        set { if (double.IsFinite(value) && ActiveGameContext?.Scene is { } scene) ForTerrains(terrains => terrains.SetScatterDensity(scene, (float)value)); }
    }

    [PgslCommand("Distance", "Engine.Terrain.Scatter.Distance = 0.5", "How far the scattered trees and rocks are drawn, 0.1 to 10 times each terrain's own (1 unless set)", "Terrain", Namespace = "Engine.Terrain.Scatter", Flat = false)]
    public static double TerrainScatterDistance
    {
        get => FirstTerrains()?.ScatterDistance ?? 1;
        set { if (double.IsFinite(value)) ForTerrains(terrains => terrains.ScatterDistance = (float)value); }
    }

    [PgslCommand("Shadows", "Engine.Terrain.Scatter.Shadows = false", "Whether the scattered trees and rocks cast shadows (true unless set)", "Terrain", Namespace = "Engine.Terrain.Scatter", Flat = false)]
    public static bool TerrainScatterShadows { get => FirstTerrains()?.ScatterShadows ?? true; set => ForTerrains(terrains => terrains.ScatterShadows = value); }

    [PgslCommand("Distance", "Engine.Terrain.Collision.Distance = 40", "How far round each moving body a large terrain's collision is built, 4 to 500 metres (24 unless set): more for fast vehicles", "Terrain", Namespace = "Engine.Terrain.Collision", Flat = false)]
    public static double TerrainCollisionDistance
    {
        get => FirstTerrains()?.ColliderRadiusAroundBodies ?? 24;
        set { if (double.IsFinite(value)) ForTerrains(terrains => terrains.ColliderRadiusAroundBodies = (float)Math.Clamp(value, 4, 500)); }
    }

    [PgslCommand("Quality", "Engine.Terrain.Detail.Quality = 1", "How far large terrains keep their full detail from the camera, 0.25 to 4 (1 unless set): 2 keeps it twice as far for a sharper distance, 0.5 half as far for a faster frame; lasts across rooms", "Terrain", Namespace = "Engine.Terrain.Detail", Flat = false)]
    public static double TerrainDetailQuality
    {
        get => RoomTerrainSubsystem.GameTerrainDetail;
        set
        {
            if (!double.IsFinite(value)) return;
            RoomTerrainSubsystem.GameTerrainDetail = (float)Math.Clamp(value, 0.25, 4);
            if (ActiveGameContext?.Scene is not { } scene) return;
            foreach (var subsystem in scene.Subsystems)
                if (subsystem is RoomTerrainSubsystem terrains) terrains.TerrainDetail = RoomTerrainSubsystem.GameTerrainDetail;
        }
    }

    // ── Engine.Physics.Character: this instance's character, changed as it plays ─────────

    [PgslCommand("StepHeight", "Engine.Physics.Character.StepHeight = metres", "How high a step this instance's character walks up without jumping, 0 to half its height (as made by CharacterCreate, 0.3 unless set); a crouch or a heavy load can lower it", "Physics", Namespace = "Engine.Physics.Character", Flat = false)]
    public static double PhysicsCharacterStepHeight
    {
        get => TryCharacter(out Genesis.Physics.KinematicCharacter character, out _, out _) ? character.StepHeight : 0.3;
        set
        {
            if (TryCharacter(out Genesis.Physics.KinematicCharacter character, out _, out _) && double.IsFinite(value))
                character.StepHeight = (float)Math.Clamp(value, 0, character.Height * 0.5);
        }
    }

    [PgslCommand("SlopeLimit", "Engine.Physics.Character.SlopeLimit = degrees", "The steepest slope this instance's character walks up, 0 to 89 degrees (as made by CharacterCreate, 45 unless set); steeper ground it slides from", "Physics", Namespace = "Engine.Physics.Character", Flat = false)]
    public static double PhysicsCharacterSlopeLimit
    {
        get => TryCharacter(out Genesis.Physics.KinematicCharacter character, out _, out _) ? character.MaxSlopeDegrees : 45;
        set
        {
            if (TryCharacter(out Genesis.Physics.KinematicCharacter character, out _, out _) && double.IsFinite(value))
                character.MaxSlopeDegrees = (float)Math.Clamp(value, 0, 89);
        }
    }

    [PgslCommand("Radius", "Engine.Physics.Character.Radius", "How wide this instance's character is, its capsule's radius in metres, as CharacterCreate made it (read)", "Physics", Namespace = "Engine.Physics.Character", Flat = false)]
    public static double PhysicsCharacterRadius => TryCharacter(out Genesis.Physics.KinematicCharacter character, out _, out _) ? character.Radius : 0;

    [PgslCommand("Height", "Engine.Physics.Character.Height", "How tall this instance's character is now, in metres (read; CharacterSetHeight changes it where it fits)", "Physics", Namespace = "Engine.Physics.Character", Flat = false)]
    public static double PhysicsCharacterHeight => TryCharacter(out Genesis.Physics.KinematicCharacter character, out _, out _) ? character.Height : 0;

    // ── Engine.Network.Replication: how often shared Objects are sent ───────────────────

    [PgslCommand("Rate", "Engine.Network.Replication.Rate = 30", "How many times a second the host sends shared Objects to the players, 1 to 120 (20 unless set): more is smoother and costs more of the connection", "Network", Namespace = "Engine.Network.Replication", Flat = false)]
    public static double NetworkReplicationRate
    {
        get => ActiveReplication?.SendRate ?? 20;
        set { if (ActiveReplication != null && double.IsFinite(value)) ActiveReplication.SendRate = (float)Math.Clamp(value, 1, 120); }
    }

    // ── Engine.Tile.Layer: a 2D room's tile layers by name, as the game runs ─────────────

    private static Rendering.RoomRenderSubsystem.TileLayerLook? TileLayerLook(string layer, bool make)
    {
        if (string.IsNullOrWhiteSpace(layer) || ViewRenderer() is not { } renderer || !renderer.HasTileLayer(layer)) return null;
        return make ? renderer.LookOfTileLayer(layer.Trim()) : renderer.PeekTileLayer(layer.Trim()) ?? new Rendering.RoomRenderSubsystem.TileLayerLook();
    }

    [PgslCommand("Visible", "Engine.Tile.Layer.Visible(\"Layer\") -> bool", "Whether the room's tile layer of this name is drawn (true unless set; false for a name the room has no layer of)", "Tiles", Namespace = "Engine.Tile.Layer", Flat = false)]
    public static bool TileLayerVisible(string layer) => TileLayerLook(layer, make: false)?.Visible ?? false;

    [PgslCommand("SetVisible", "Engine.Tile.Layer.SetVisible(\"Layer\", visible) -> bool", "Shows or hides a tile layer of the room (a secret wall, a lit and an unlit version of a room); false for a name the room has no layer of", "Tiles", Namespace = "Engine.Tile.Layer", Flat = false)]
    public static bool TileLayerSetVisible(string layer, bool visible)
    {
        if (TileLayerLook(layer, make: true) is not { } look) return false;
        look.Visible = visible;
        return true;
    }

    [PgslCommand("Alpha", "Engine.Tile.Layer.Alpha(\"Layer\") -> number", "How opaque the tile layer is, 0 to 1 (1 unless set)", "Tiles", Namespace = "Engine.Tile.Layer", Flat = false)]
    public static double TileLayerAlpha(string layer) => TileLayerLook(layer, make: false)?.Alpha ?? 0;

    [PgslCommand("SetAlpha", "Engine.Tile.Layer.SetAlpha(\"Layer\", alpha) -> bool", "Makes a tile layer see-through, 0 to 1 (a roof fading as the player walks under it); false for a name the room has no layer of", "Tiles", Namespace = "Engine.Tile.Layer", Flat = false)]
    public static bool TileLayerSetAlpha(string layer, double alpha)
    {
        if (TileLayerLook(layer, make: true) is not { } look) return false;
        look.Alpha = Finite(alpha, 0, 1, 1f);
        return true;
    }

    [PgslCommand("OffsetX", "Engine.Tile.Layer.OffsetX(\"Layer\") -> number", "How far the tile layer is drawn moved across, in pixels (0 unless set)", "Tiles", Namespace = "Engine.Tile.Layer", Flat = false)]
    public static double TileLayerOffsetX(string layer) => TileLayerLook(layer, make: false)?.OffsetX ?? 0;

    [PgslCommand("OffsetY", "Engine.Tile.Layer.OffsetY(\"Layer\") -> number", "How far the tile layer is drawn moved down, in pixels (0 unless set)", "Tiles", Namespace = "Engine.Tile.Layer", Flat = false)]
    public static double TileLayerOffsetY(string layer) => TileLayerLook(layer, make: false)?.OffsetY ?? 0;

    [PgslCommand("SetOffset", "Engine.Tile.Layer.SetOffset(\"Layer\", x, y) -> bool", "Draws a tile layer moved by x and y pixels (a background scrolled slower than the view for parallax); what is solid stays where it was painted; false for a name the room has no layer of", "Tiles", Namespace = "Engine.Tile.Layer", Flat = false)]
    public static bool TileLayerSetOffset(string layer, double x, double y)
    {
        if (TileLayerLook(layer, make: true) is not { } look || !double.IsFinite(x) || !double.IsFinite(y)) return false;
        look.OffsetX = (float)x;
        look.OffsetY = (float)y;
        return true;
    }

    // ── Engine.Instance.Update: how far from the camera instances still take their Step ──

    [PgslCommand("Enabled", "Engine.Instance.Enabled = false", "Whether this instance's Step events run (true unless set); off, it is frozen where it is, still drawn and still solid, until another instance turns it on with `with`: a statue spell, an enemy held by a cutscene", "Instances", Namespace = "Engine.Instance", Flat = false)]
    public static bool InstanceEnabled
    {
        get
        {
            var world = ActiveGameContext?.World;
            int id = GetContext()?.InstanceId ?? 0;
            if (world == null || id <= 0) return true;
            var entity = world.GetEntity(id);
            return !world.IsAlive(entity) || !world.Has<Genesis.Runtime.ECS.Components.InstanceFrozenComponent>(entity)
                || !world.GetRef<Genesis.Runtime.ECS.Components.InstanceFrozenComponent>(entity).Frozen;
        }
        set
        {
            var world = ActiveGameContext?.World;
            int id = GetContext()?.InstanceId ?? 0;
            if (world == null || id <= 0) return;
            var entity = world.GetEntity(id);
            if (!world.IsAlive(entity) || value && !world.Has<Genesis.Runtime.ECS.Components.InstanceFrozenComponent>(entity)) return;
            if (!value) Genesis.Runtime.ECS.Components.InstanceFrozenComponent.Any = true;
            world.Set(entity, new Genesis.Runtime.ECS.Components.InstanceFrozenComponent { Frozen = !value });
        }
    }

    [PgslCommand("Distance", "Engine.Instance.Update.Distance = metres", "Instances farther than this from the camera skip their Step events until it comes near, 0 to 100000 metres; 0 updates every instance however far (the room's simulation distance unless set)", "Instances", Namespace = "Engine.Instance.Update", Flat = false)]
    public static double InstanceUpdateDistance
    {
        get => RoomLook?.SimulationDistance ?? 0;
        set { if (RoomLook is { } look) look.SimulationDistance = Finite(value, 0, 100000, 0f); }
    }

    // ── Engine.Lighting's Sun, Ambient, Toon and Glow ────────────────────────────────────

    [PgslCommand("Intensity", "Engine.Lighting.Sun.Intensity = amount", "How bright the sunlight is, 0 to 20 (1.1 unless the room sets another)", "Lighting", Namespace = "Engine.Lighting.Sun", Flat = false)]
    public static double LightingSunIntensity { get => RoomLook?.SunIntensity ?? 1.1; set { if (RoomLook is { } look) look.SunIntensity = Finite(value, 0, 20, 1.1f); } }

    [PgslCommand("Yaw", "Engine.Lighting.Sun.Yaw = degrees", "Which way the sunlight comes from round the horizon (135 unless the room sets another)", "Lighting", Namespace = "Engine.Lighting.Sun", Flat = false)]
    public static double LightingSunYaw
    {
        get => RoomLook?.SunYawDegrees ?? 135;
        set { if (RoomLook is { } look && double.IsFinite(value)) { look.SunYawDegrees = (float)(value % 360); look.LightDirectionOverride = null; } }
    }

    [PgslCommand("Pitch", "Engine.Lighting.Sun.Pitch = degrees", "How high the sun is, -90 to 90 degrees (42 unless the room sets another)", "Lighting", Namespace = "Engine.Lighting.Sun", Flat = false)]
    public static double LightingSunPitch
    {
        get => RoomLook?.SunPitchDegrees ?? 42;
        set { if (RoomLook is { } look && double.IsFinite(value)) { look.SunPitchDegrees = (float)Math.Clamp(value, -90, 90); look.LightDirectionOverride = null; } }
    }

    [PgslCommand("Color", "Engine.Lighting.Sun.Color = \"#fff2d1\"", "The sunlight's colour as #rrggbb (warm white unless the room sets another)", "Lighting", Namespace = "Engine.Lighting.Sun", Flat = false)]
    public static string LightingSunColor
    {
        get => HexColor(RoomLook?.SunColor ?? new Vector4(1f, 0.95f, 0.82f, 1f));
        set { if (RoomLook is { } look && TryHexColor(value, out Vector3 rgb)) look.SunColor = new Vector4(rgb, 1f); }
    }

    [PgslCommand("Sky", "Engine.Lighting.Ambient.Sky = \"#4c5462\"", "The light from above where no sun reaches, as #rrggbb (the sky's unless set)", "Lighting", Namespace = "Engine.Lighting.Ambient", Flat = false)]
    public static string LightingAmbientSky
    {
        get => RoomLook is { } look ? HexColor(look.AmbientOverrideColor is Vector3 sky ? new Vector4(sky, 1f) : look.AmbientColor) : "#4c5462";
        set { if (RoomLook is { } look && TryHexColor(value, out Vector3 rgb)) look.AmbientOverrideColor = rgb; }
    }

    [PgslCommand("Ground", "Engine.Lighting.Ambient.Ground = \"#1f1f21\"", "The light from below where no sun reaches, as #rrggbb (the sky's unless set)", "Lighting", Namespace = "Engine.Lighting.Ambient", Flat = false)]
    public static string LightingAmbientGround
    {
        get => HexColor(RoomLook?.AmbientGroundColor ?? new Vector4(0.12f, 0.12f, 0.13f, 1f));
        set { if (RoomLook is { } look && TryHexColor(value, out Vector3 rgb)) look.AmbientGroundColor = new Vector4(rgb, 1f); }
    }

    [PgslCommand("Mode", "Engine.Lighting.Ambient.Mode = \"hemisphere\"", "\"zenith\" (the two colours above) or \"hemisphere\" (the drawn sky's own light) (the room's unless set)", "Lighting", Namespace = "Engine.Lighting.Ambient", Flat = false)]
    public static string LightingAmbientMode { get => SkyAmbientMode; set => SkyAmbientMode = value; }

    [PgslCommand("Strength", "Engine.Lighting.Ambient.Strength = amount", "How strong the hemisphere sky light is (1 unless set)", "Lighting", Namespace = "Engine.Lighting.Ambient", Flat = false)]
    public static double LightingAmbientStrength { get => SkyAmbientStrength; set { if (double.IsFinite(value)) SkyAmbientStrength = (float)value; } }

    [PgslCommand("Enabled", "Engine.Lighting.Toon.Enabled = true", "Stylized (toon) lighting: stepped shading, a rim and its own highlight (off unless the room turns it on)", "Lighting", Namespace = "Engine.Lighting.Toon", Flat = false)]
    public static bool LightingToonEnabled { get => RoomLook?.StylizedEnabled ?? false; set { if (RoomLook is { } look) look.StylizedEnabled = value; } }

    [PgslCommand("Steps", "Engine.Lighting.Toon.Steps = count", "How many bands the shading is cut into, 1 (smooth) to 8 (1 unless set)", "Lighting", Namespace = "Engine.Lighting.Toon", Flat = false)]
    public static double LightingToonSteps { get => RoomLook?.StylizedToonSteps ?? 1; set { if (RoomLook is { } look) look.StylizedToonSteps = Finite(Math.Round(value), 1, 8, 1f); } }

    [PgslCommand("Wrap", "Engine.Lighting.Toon.Wrap = amount", "How far light wraps round the shaded side, 0 to 1 (0.5 unless set)", "Lighting", Namespace = "Engine.Lighting.Toon", Flat = false)]
    public static double LightingToonWrap { get => RoomLook?.StylizedDiffuseWrap ?? 0.5; set { if (RoomLook is { } look) look.StylizedDiffuseWrap = Finite(value, 0, 1, 0.5f); } }

    [PgslCommand("Specular", "Engine.Lighting.Toon.Specular = amount", "The strength of the toon highlight, 0 to 4 (0.2 unless set)", "Lighting", Namespace = "Engine.Lighting.Toon", Flat = false)]
    public static double LightingToonSpecular { get => RoomLook?.StylizedSpecularStrength ?? 0.2; set { if (RoomLook is { } look) look.StylizedSpecularStrength = Finite(value, 0, 4, 0.2f); } }

    [PgslCommand("Rim", "Engine.Lighting.Toon.Rim = amount", "The strength of the light round silhouettes, 0 to 4 (0.18 unless set)", "Lighting", Namespace = "Engine.Lighting.Toon", Flat = false)]
    public static double LightingToonRim { get => RoomLook?.StylizedRimStrength ?? 0.18; set { if (RoomLook is { } look) look.StylizedRimStrength = Finite(value, 0, 4, 0.18f); } }

    [PgslCommand("Saturation", "Engine.Lighting.Toon.Saturation = amount", "How colourful toon-lit surfaces are, 0.5 to 2 (1 unless set)", "Lighting", Namespace = "Engine.Lighting.Toon", Flat = false)]
    public static double LightingToonSaturation { get => RoomLook?.StylizedSaturation ?? 1; set { if (RoomLook is { } look) look.StylizedSaturation = Finite(value, 0.5, 2, 1f); } }

    [PgslCommand("Strength", "Engine.Lighting.Glow.Strength = amount", "How much every surface glows in its own colours, 0 to 4 (0 unless the room sets another)", "Lighting", Namespace = "Engine.Lighting.Glow", Flat = false)]
    public static double LightingGlowStrength { get => RoomLook?.GlobalEmissive ?? 0; set { if (RoomLook is { } look) look.GlobalEmissive = Finite(value, 0, 4, 0f); } }

    // ── Engine.Sky's parts ───────────────────────────────────────────────────────────────

    [PgslCommand("OfDay", "Engine.Sky.Time.OfDay = hours", "The time of day, 0 to 24 hours: moves the sun and moon (the room's unless set)", "Sky", Namespace = "Engine.Sky.Time", Flat = false)]
    public static double SkyTimeOfDayPart { get => SkyTimeOfDay; set { if (double.IsFinite(value)) SkyTimeOfDay = (float)value; } }

    [PgslCommand("DayOfYear", "Engine.Sky.Time.DayOfYear = day", "The day of the year, 1 to 365: the sun's height through the seasons (the room's unless set)", "Sky", Namespace = "Engine.Sky.Time", Flat = false)]
    public static double SkyTimeDayOfYear { get => SkyDayOfYear; set { if (double.IsFinite(value)) SkyDayOfYear = (float)value; } }

    [PgslCommand("Latitude", "Engine.Sky.Time.Latitude = degrees", "How far north (positive) or south the world is, for the sun's path (the room's unless set)", "Sky", Namespace = "Engine.Sky.Time", Flat = false)]
    public static double SkyTimeLatitude { get => SkyLatitude; set { if (double.IsFinite(value)) SkyLatitude = (float)value; } }

    [PgslCommand("Enabled", "Engine.Sky.Clouds.Enabled = true", "Volumetric clouds (the room's unless set)", "Sky", Namespace = "Engine.Sky.Clouds", Flat = false)]
    public static bool SkyCloudsEnabled { get => SkyRaymarchedCloudsEnabled; set => SkyRaymarchedCloudsEnabled = value; }

    [PgslCommand("Coverage", "Engine.Sky.Clouds.Coverage = amount", "How much of the sky is cloud, 0 to 1 (the room's unless set)", "Sky", Namespace = "Engine.Sky.Clouds", Flat = false)]
    public static double SkyCloudsCoverage { get => SkyCoverage; set { if (double.IsFinite(value)) SkyCoverage = (float)value; } }

    [PgslCommand("Density", "Engine.Sky.Clouds.Density = amount", "How thick the clouds are (the room's unless set)", "Sky", Namespace = "Engine.Sky.Clouds", Flat = false)]
    public static double SkyCloudsDensity { get => SkyDensity; set { if (double.IsFinite(value)) SkyDensity = (float)value; } }

    [PgslCommand("Altitude", "Engine.Sky.Clouds.Altitude = metres", "How high the cloud layer starts (the room's unless set)", "Sky", Namespace = "Engine.Sky.Clouds", Flat = false)]
    public static double SkyCloudsAltitude { get => SkyAltitude; set { if (double.IsFinite(value)) SkyAltitude = (float)value; } }

    [PgslCommand("Thickness", "Engine.Sky.Clouds.Thickness = metres", "How deep the cloud layer is (the room's unless set)", "Sky", Namespace = "Engine.Sky.Clouds", Flat = false)]
    public static double SkyCloudsThickness { get => SkyThickness; set { if (double.IsFinite(value)) SkyThickness = (float)value; } }

    [PgslCommand("Quality", "Engine.Sky.Clouds.Quality = 1", "The clouds' detail (the quality tier's unless set)", "Sky", Namespace = "Engine.Sky.Clouds", Flat = false)]
    public static double SkyCloudsQuality { get => CloudQuality; set { if (double.IsFinite(value)) CloudQuality = (int)Math.Round(value); } }

    [PgslCommand("Visible", "Engine.Sky.Sun.Visible = true", "Whether the sun's disc is drawn in the sky (true unless set)", "Sky", Namespace = "Engine.Sky.Sun", Flat = false)]
    public static bool SkySunVisible { get => SkySunDiscVisible; set => SkySunDiscVisible = value; }

    [PgslCommand("Size", "Engine.Sky.Sun.Size = amount", "How large the sun's disc is drawn (1 unless set)", "Sky", Namespace = "Engine.Sky.Sun", Flat = false)]
    public static double SkySunSizePart { get => SkySunSize; set { if (double.IsFinite(value)) SkySunSize = (float)value; } }

    [PgslCommand("Visible", "Engine.Sky.Moon.Visible = true", "Whether the moon's disc is drawn in the sky (true unless set)", "Sky", Namespace = "Engine.Sky.Moon", Flat = false)]
    public static bool SkyMoonVisible { get => SkyMoonDiscVisible; set => SkyMoonDiscVisible = value; }

    [PgslCommand("Enabled", "Engine.Sky.Stars.Enabled = true", "Stars and the moon's glow at night (the quality tier's unless set)", "Sky", Namespace = "Engine.Sky.Stars", Flat = false)]
    public static bool SkyStarsEnabled { get => CelestialExtrasEnabled; set => CelestialExtrasEnabled = value; }

    // ── Engine.Sky.Time's clock and Engine.Weather ───────────────────────────────────────

    private static Genesis.Runtime.Climate.EnvironmentService? Climate => ActiveGameContext?.Scene?.Climate;

    [PgslCommand("Speed", "Engine.Sky.Time.Speed = multiplier", "How much faster than real time the day goes, 0 to 100000 (24 unless the room sets another: a day an hour)", "Sky", Namespace = "Engine.Sky.Time", Flat = false)]
    public static double SkyTimeSpeed
    {
        get => Climate?.Options.TimeScale ?? 24;
        set { if (Climate is { } climate) climate.Options.TimeScale = Finite(value, 0, 100000, 24f); }
    }

    [PgslCommand("Paused", "Engine.Sky.Time.Paused = true", "Stop the time of day where it is (false unless set)", "Sky", Namespace = "Engine.Sky.Time", Flat = false)]
    public static bool SkyTimePaused
    {
        get => Climate?.Options.Paused ?? false;
        set { if (Climate is { } climate) climate.Options.Paused = value; }
    }

    private static double _weatherTransition = 20;

    [PgslCommand("Preset", "Engine.Weather.Preset = \"rain\"", "The weather: \"clear\", \"overcast\", \"wind\", \"rain\", \"thunderstorm\", \"snow\", \"hail\", \"fog\" or \"coldsnap\"; setting it changes over Engine.Weather.Transition seconds and stops automatic weather (\"clear\" unless the room sets another)", "Weather", Namespace = "Engine.Weather", Flat = false)]
    public static string WeatherPreset
    {
        get => (Climate?.ActiveWeather ?? Genesis.Runtime.Climate.WeatherKind.Clear).ToString().ToLowerInvariant();
        set
        {
            if (Climate is { } climate && Enum.TryParse(value?.Trim(), true, out Genesis.Runtime.Climate.WeatherKind kind))
                climate.SetWeather(kind, (float)_weatherTransition);
        }
    }

    [PgslCommand("Transition", "Engine.Weather.Transition = seconds", "How long a change of weather takes, 0 to 600 seconds (20 unless set)", "Weather", Namespace = "Engine.Weather", Flat = false)]
    public static double WeatherTransition { get => _weatherTransition; set => _weatherTransition = Finite(value, 0, 600, 20f); }

    [PgslCommand("Auto", "Engine.Weather.Auto = true", "The weather changes by itself through the day (the room's choice unless set); setting a preset turns it off", "Weather", Namespace = "Engine.Weather", Flat = false)]
    public static bool WeatherAuto
    {
        get => Climate?.Options.AutomaticWeather ?? false;
        set
        {
            if (Climate is not { } climate) return;
            if (value) climate.ResumeAutomaticWeather();
            else climate.Options.AutomaticWeather = false;
        }
    }

    // Wind, rain, snow, lightning and temperature follow the weather until a script sets one; it
    // then holds there whatever the weather does, until Engine.Weather.Release() or the next room.
    // A part turned off holds its amount at 0 and keeps the value set for when it is turned on.
    private static Genesis.Runtime.Climate.WeatherOverrides? WeatherHeld => Climate?.Overrides;

    [PgslCommand("Release", "Engine.Weather.Release()", "The wind, rain, snow, lightning and temperature a script set follow the weather again, and every weather part is on", "Weather", Namespace = "Engine.Weather", Flat = false)]
    public static void WeatherRelease() => WeatherHeld?.Clear();

    [PgslCommand("Enabled", "Engine.Weather.Wind.Enabled = false", "Wind (true unless set); off, the weather's wind is still whatever the weather", "Weather", Namespace = "Engine.Weather.Wind", Flat = false)]
    public static bool WeatherWindEnabled
    {
        get => WeatherHeld?.WindEnabled ?? true;
        set { if (WeatherHeld is { } held) held.WindEnabled = value; }
    }

    [PgslCommand("Strength", "Engine.Weather.Wind.Strength = metresPerSecond", "The wind's speed, 0 to 60 metres a second (the weather's unless set: 1.2 in clear weather, 11 in a thunderstorm)", "Weather", Namespace = "Engine.Weather.Wind", Flat = false)]
    public static double WeatherWindStrength
    {
        get => WeatherHeld?.WindSpeed ?? Climate?.OwnWeather.WindSpeed ?? 1.2;
        set { if (WeatherHeld is { } held) held.WindSpeed = Finite(value, 0, 60, 1.2f); }
    }

    [PgslCommand("Direction", "Engine.Weather.Wind.Direction = degrees", "The way the wind blows towards, 0 to 360 degrees: 0 along +Z, 90 along +X (the weather's unless set)", "Weather", Namespace = "Engine.Weather.Wind", Flat = false)]
    public static double WeatherWindDirection
    {
        get
        {
            if (WeatherHeld?.WindDirectionDegrees is { } held) return held;
            System.Numerics.Vector3 wind = Climate?.OwnWeather.WindDirection ?? System.Numerics.Vector3.UnitZ;
            double degrees = Math.Atan2(wind.X, wind.Z) * 180.0 / Math.PI;
            return degrees < 0 ? degrees + 360 : degrees;
        }
        set { if (WeatherHeld is { } held && double.IsFinite(value)) held.WindDirectionDegrees = (float)(((value % 360) + 360) % 360); }
    }

    [PgslCommand("Enabled", "Engine.Weather.Rain.Enabled = false", "Rain (true unless set); off, no rain falls whatever the weather", "Weather", Namespace = "Engine.Weather.Rain", Flat = false)]
    public static bool WeatherRainEnabled
    {
        get => WeatherHeld?.RainEnabled ?? true;
        set { if (WeatherHeld is { } held) held.RainEnabled = value; }
    }

    [PgslCommand("Amount", "Engine.Weather.Rain.Amount = 0.5", "How hard it rains, 0 to 1 (the weather's unless set: 0 in clear weather, 0.72 in rain)", "Weather", Namespace = "Engine.Weather.Rain", Flat = false)]
    public static double WeatherRainAmount
    {
        get => WeatherHeld?.Rain ?? Climate?.OwnWeather.Rain ?? 0;
        set { if (WeatherHeld is { } held) held.Rain = Finite(value, 0, 1, 0f); }
    }

    [PgslCommand("Wetness", "Engine.Weather.Rain.Wetness = 1", "How wet the ground and surfaces are, 0 to 1 (0 unless set); rain wets them and warmth and wind dry them from there", "Weather", Namespace = "Engine.Weather.Rain", Flat = false)]
    public static double WeatherRainWetness
    {
        get => Climate?.GroundWetness ?? 0;
        set { if (Climate is { } climate) climate.GroundWetness = Finite(value, 0, 1, 0f); }
    }

    [PgslCommand("Enabled", "Engine.Weather.Snow.Enabled = false", "Snow (true unless set); off, no snow falls whatever the weather", "Weather", Namespace = "Engine.Weather.Snow", Flat = false)]
    public static bool WeatherSnowEnabled
    {
        get => WeatherHeld?.SnowEnabled ?? true;
        set { if (WeatherHeld is { } held) held.SnowEnabled = value; }
    }

    [PgslCommand("Amount", "Engine.Weather.Snow.Amount = 0.5", "How hard it snows, 0 to 1 (the weather's unless set: 0.78 in snow)", "Weather", Namespace = "Engine.Weather.Snow", Flat = false)]
    public static double WeatherSnowAmount
    {
        get => WeatherHeld?.Snow ?? Climate?.OwnWeather.Snow ?? 0;
        set { if (WeatherHeld is { } held) held.Snow = Finite(value, 0, 1, 0f); }
    }

    [PgslCommand("Cover", "Engine.Weather.Snow.Cover = 1", "How much snow lies on the ground and surfaces, 0 to 1 (0 unless set); snow builds it and warmth melts it from there", "Weather", Namespace = "Engine.Weather.Snow", Flat = false)]
    public static double WeatherSnowCover
    {
        get => Climate?.SnowCover ?? 0;
        set { if (Climate is { } climate) climate.SnowCover = Finite(value, 0, 1, 0f); }
    }

    [PgslCommand("Enabled", "Engine.Weather.Lightning.Enabled = false", "Lightning in storms (true unless set); a script's own strikes still happen when off", "Weather", Namespace = "Engine.Weather.Lightning", Flat = false)]
    public static bool WeatherLightningEnabled
    {
        get => WeatherHeld?.LightningEnabled ?? true;
        set { if (WeatherHeld is { } held) held.LightningEnabled = value; }
    }

    [PgslCommand("Amount", "Engine.Weather.Lightning.Amount = 0.5", "How often lightning strikes, 0 to 1 (the weather's unless set: 0.85 in a thunderstorm, 0 otherwise)", "Weather", Namespace = "Engine.Weather.Lightning", Flat = false)]
    public static double WeatherLightningAmount
    {
        get => WeatherHeld?.Lightning ?? Climate?.OwnWeather.Lightning ?? 0;
        set { if (WeatherHeld is { } held) held.Lightning = Finite(value, 0, 1, 0f); }
    }

    [PgslCommand("Celsius", "Engine.Weather.Temperature.Celsius = -5", "The air's temperature at sea level, -60 to 60 degrees (the weather's unless set: 18 in clear weather); below 0 snow lies and above it melts", "Weather", Namespace = "Engine.Weather.Temperature", Flat = false)]
    public static double WeatherTemperatureCelsius
    {
        get => WeatherHeld?.TemperatureC ?? Climate?.OwnWeather.TemperatureC ?? 18;
        set { if (WeatherHeld is { } held) held.TemperatureC = Finite(value, -60, 60, 18f); }
    }

    // ── Engine.Audio's parts ─────────────────────────────────────────────────────────────

    // A turned-off bus keeps the volume it had, for when it is turned on again.
    private static double _audioVolumeBeforeOff = 1, _musicVolumeBeforeOff = 1, _effectsVolumeBeforeOff = 1;
    private static bool _audioOff, _musicOff, _effectsOff;
    private static string _acousticsBeforeOff = "room";

    [PgslCommand("Enabled", "Engine.Audio.Enabled = true", "All of the game's sound (true unless set); off keeps every volume as it was for when it comes back", "Audio", Namespace = "Engine.Audio", Flat = false)]
    public static bool AudioApiEnabled
    {
        get => !_audioOff;
        set
        {
            if (value == !_audioOff) return;
            if (!value) { _audioVolumeBeforeOff = GetMasterVolume(); SetMasterVolume(0); }
            else SetMasterVolume(_audioVolumeBeforeOff);
            _audioOff = !value;
        }
    }

    [PgslCommand("Enabled", "Engine.Audio.Music.Enabled = true", "The music bus (true unless set); off keeps its volume for when it comes back", "Audio", Namespace = "Engine.Audio.Music", Flat = false)]
    public static bool AudioMusicEnabled
    {
        get => !_musicOff;
        set
        {
            if (value == !_musicOff) return;
            if (!value) { _musicVolumeBeforeOff = GetBusVolume("music"); SetBusVolume("music", 0); }
            else SetBusVolume("music", _musicVolumeBeforeOff);
            _musicOff = !value;
        }
    }

    [PgslCommand("Volume", "Engine.Audio.Music.Volume = volume", "The music bus's volume, 0 to 1 (1 unless set)", "Audio", Namespace = "Engine.Audio.Music", Flat = false)]
    public static double AudioMusicVolume { get => GetBusVolume("music"); set => SetBusVolume("music", value); }

    [PgslCommand("Track", "Engine.Audio.Music.Track = \"Theme\"", "The music playing (\"\" for none): setting it cross-fades to that track over a second, \"\" fades it out", "Audio", Namespace = "Engine.Audio.Music", Flat = false)]
    public static string AudioMusicTrack { get => AudioApiMusic; set => AudioApiMusic = value; }

    [PgslCommand("Muffle", "Engine.Audio.Music.Muffle = amount", "How muffled the music is, 0 (clear) to 1 (heard through a wall); (0 unless set)", "Audio", Namespace = "Engine.Audio.Music", Flat = false)]
    public static double AudioMusicMufflePart { get => AudioApiMusicMuffle; set => AudioApiMusicMuffle = value; }

    [PgslCommand("Enabled", "Engine.Audio.Effects.Enabled = true", "The sound-effects bus (true unless set); off keeps its volume for when it comes back", "Audio", Namespace = "Engine.Audio.Effects", Flat = false)]
    public static bool AudioEffectsEnabled
    {
        get => !_effectsOff;
        set
        {
            if (value == !_effectsOff) return;
            if (!value) { _effectsVolumeBeforeOff = GetBusVolume("sfx"); SetBusVolume("sfx", 0); }
            else SetBusVolume("sfx", _effectsVolumeBeforeOff);
            _effectsOff = !value;
        }
    }

    [PgslCommand("Volume", "Engine.Audio.Effects.Volume = volume", "The sound-effects bus's volume, 0 to 1 (1 unless set)", "Audio", Namespace = "Engine.Audio.Effects", Flat = false)]
    public static double AudioEffectsVolume { get => GetBusVolume("sfx"); set => SetBusVolume("sfx", value); }

    [PgslCommand("Enabled", "Engine.Audio.Acoustics.Enabled = true", "Echo for sound effects: off is outdoors, on brings back the last place chosen (\"room\" unless one was)", "Audio", Namespace = "Engine.Audio.Acoustics", Flat = false)]
    public static bool AudioAcousticsEnabled
    {
        get => _acoustics != "outdoors";
        set
        {
            if (value == (_acoustics != "outdoors")) return;
            if (!value) { _acousticsBeforeOff = _acoustics; AudioApiAcoustics = "outdoors"; }
            else AudioApiAcoustics = _acousticsBeforeOff == "outdoors" ? "room" : _acousticsBeforeOff;
        }
    }

    [PgslCommand("Place", "Engine.Audio.Acoustics.Place = \"cave\"", "How the place sounds: \"outdoors\" (no echo, unless the room chooses another), \"room\", \"hall\" or \"cave\"", "Audio", Namespace = "Engine.Audio.Acoustics", Flat = false)]
    public static string AudioAcousticsPlace { get => AudioApiAcoustics; set => AudioApiAcoustics = value; }

    [PgslCommand("Amount", "Engine.Audio.Acoustics.Amount = amount", "How much of the echo is heard, 0 to 1 (0.5 unless set)", "Audio", Namespace = "Engine.Audio.Acoustics", Flat = false)]
    public static double AudioAcousticsAmountPart { get => AudioApiAcousticsAmount; set => AudioApiAcousticsAmount = value; }

    [PgslCommand("FollowCamera", "Engine.Audio.Listener.FollowCamera = true", "Positioned sounds are heard from the 3D camera (true unless set); false keeps the listener where SetAudioListener put it", "Audio", Namespace = "Engine.Audio.Listener", Flat = false)]
    public static bool AudioListenerFollowsCameraPart { get => !AudioListenerManual; set => AudioListenerManual = !value; }

    // ── Engine.Physics' World, Gravity and Sleep ─────────────────────────────────────────

    [PgslCommand("AirDrag", "Engine.Physics.World.AirDrag = amount", "How much the air slows every body, 0 to 1 (the room's unless set)", "Physics", Namespace = "Engine.Physics.World", Flat = false)]
    public static double PhysicsWorldAirDrag { get => PhysicsGetAirDrag(); set => PhysicsSetAirDrag(value); }

    [PgslCommand("MaxVelocity", "Engine.Physics.World.MaxVelocity = speed", "The fastest any body may move, metres a second (the room's unless set)", "Physics", Namespace = "Engine.Physics.World", Flat = false)]
    public static double PhysicsWorldMaxVelocity { get => PhysicsGetMaxVelocity(); set => PhysicsSetMaxVelocity(value); }

    [PgslCommand("RecoveryVelocity", "Engine.Physics.World.RecoveryVelocity = speed", "How fast bodies pushed into each other are moved apart (the room's unless set)", "Physics", Namespace = "Engine.Physics.World", Flat = false)]
    public static double PhysicsWorldRecoveryVelocityPart { get => PhysicsWorldRecoveryVelocity; set => PhysicsWorldRecoveryVelocity = value; }

    [PgslCommand("Strength", "Engine.Physics.Gravity.Strength = metres", "How strongly everything falls, metres a second squared (9.81 unless the room sets another)", "Physics", Namespace = "Engine.Physics.Gravity", Flat = false)]
    public static double PhysicsGravityStrength { get => PhysicsGetGravityStrength(); set => PhysicsSetGravityStrength(value); }

    [PgslCommand("X", "Engine.Physics.Gravity.X = amount", "Gravity along X (0 unless set)", "Physics", Namespace = "Engine.Physics.Gravity", Flat = false)]
    public static double PhysicsGravityXPart { get => PhysicsGetGravityX(); set => PhysicsSetGravityDirection(value, PhysicsGetGravityY(), PhysicsGetGravityZ()); }

    [PgslCommand("Y", "Engine.Physics.Gravity.Y = amount", "Gravity along Y (-9.81 unless set: down)", "Physics", Namespace = "Engine.Physics.Gravity", Flat = false)]
    public static double PhysicsGravityYPart { get => PhysicsGetGravityY(); set => PhysicsSetGravityDirection(PhysicsGetGravityX(), value, PhysicsGetGravityZ()); }

    [PgslCommand("Z", "Engine.Physics.Gravity.Z = amount", "Gravity along Z (0 unless set)", "Physics", Namespace = "Engine.Physics.Gravity", Flat = false)]
    public static double PhysicsGravityZPart { get => PhysicsGetGravityZ(); set => PhysicsSetGravityDirection(PhysicsGetGravityX(), PhysicsGetGravityY(), value); }

    [PgslCommand("Enabled", "Engine.Physics.Sleep.Enabled = true", "Bodies at rest stop being simulated until something touches them (true unless set)", "Physics", Namespace = "Engine.Physics.Sleep", Flat = false)]
    public static bool PhysicsSleepEnabled { get => PhysicsGetAllowSleep(); set => PhysicsSetAllowSleep(value); }

    [PgslCommand("Threshold", "Engine.Physics.Sleep.Threshold = speed", "How slowly a body must move to fall asleep (the engine's unless set)", "Physics", Namespace = "Engine.Physics.Sleep", Flat = false)]
    public static double PhysicsSleepThresholdPart { get => PhysicsWorldSleepThreshold; set => PhysicsWorldSleepThreshold = value; }

    // ── Engine.Input's Mouse and Gamepad ─────────────────────────────────────────────────

    [PgslCommand("Captured", "Engine.Input.Mouse.Captured = true", "The mouse is locked to the window and hidden, for mouse look (false unless set)", "Input", Namespace = "Engine.Input.Mouse", Flat = false)]
    public static bool InputMouseCaptured { get => ActiveGameContext?.MouseCaptured ?? false; set => SetMouseCaptured(value); }

    [PgslCommand("DeadZone", "Engine.Input.Gamepad.DeadZone = amount", "How far both sticks must move before they count, 0 to 0.9 (the engine's unless set)", "Input", Namespace = "Engine.Input.Gamepad", Flat = false)]
    public static double InputGamepadDeadZone
    {
        get => GamepadGetDeadZone("left");
        set { GamepadSetDeadZone("left", value); GamepadSetDeadZone("right", value); }
    }

    [PgslCommand("KeyboardEmulation", "Engine.Input.Gamepad.KeyboardEmulation = true", "A controller also presses the keys a keyboard game listens for (arrows, Space, Enter, Escape) (true unless set)", "Input", Namespace = "Engine.Input.Gamepad", Flat = false)]
    public static bool InputGamepadKeyboardEmulation { get => ActiveGameContext?.Input?.GamepadEmulatesKeyboard ?? true; set => GamepadKeyboardEmulation(value); }

    // ── The player's own input settings (an options screen's), applied for the engine's cameras and every script ──

    [PgslCommand("Sensitivity", "Engine.Input.Mouse.Sensitivity = 1.5", "How far the view turns for a mouse movement, 0.05 to 10 (1 unless set); the engine's cameras and Engine.Input.Mouse.LookDelta both follow it", "Input", Namespace = "Engine.Input.Mouse", Flat = false)]
    public static double InputMouseSensitivity
    {
        get => Input.InputSettings.LookSensitivity;
        set => Input.InputSettings.LookSensitivity = Finite(value, 0.05, 10, 1f);
    }

    [PgslCommand("InvertY", "Engine.Input.Mouse.InvertY = true", "Moving the mouse up looks down, for players who prefer it (false unless set)", "Input", Namespace = "Engine.Input.Mouse", Flat = false)]
    public static bool InputMouseInvertY
    {
        get => Input.InputSettings.InvertLookY;
        set => Input.InputSettings.InvertLookY = value;
    }

    [PgslCommand("VibrationStrength", "Engine.Input.Gamepad.VibrationStrength = 0.5", "How strongly controllers vibrate when the game runs their motors, 0 (never) to 1 (as asked) (1 unless set)", "Input", Namespace = "Engine.Input.Gamepad", Flat = false)]
    public static double InputGamepadVibrationStrength
    {
        get => Input.InputSettings.VibrationStrength;
        set => Input.InputSettings.VibrationStrength = Finite(value, 0, 1, 1f);
    }

    [PgslCommand("Zoom", "Engine.View.Camera.Zoom = amount", "How far the active view is zoomed in, 0.01 to 100 (1 unless set)", "Views", Namespace = "Engine.View.Camera", Flat = false)]
    public static double ViewCameraZoom { get => ViewGetZoom(ViewGetActive()); set => ViewSetZoom(ViewGetActive(), value); }

    // ── Engine.Game ──────────────────────────────────────────────────────────────────────

    // The project file is read once per project, not on every read.
    private static (string Project, string Name, string Version) _gameInfo;

    private static (string Name, string Version) GameInfo()
    {
        string project = ProjectPath ?? "";
        if (!string.Equals(_gameInfo.Project, project, StringComparison.OrdinalIgnoreCase) || _gameInfo.Version == null)
        {
            (string name, string version) = ProjectPaths.ReadGameInfo(project);
            _gameInfo = (project, name, version.Length > 0 ? version : "1.0");
        }
        return (_gameInfo.Name, _gameInfo.Version);
    }

    [PgslCommand("Name", "Engine.Game.Name -> text", "The game's name, from its project", "Game", Namespace = "Engine.Game", Flat = false)]
    public static string GameApiName => GameInfo().Name;

    [PgslCommand("Version", "Engine.Game.Version -> text", "The game's version, from its project (\"1.0\" unless the project sets one): for a title or about screen; it is in crash reports too", "Game", Namespace = "Engine.Game", Flat = false)]
    public static string GameApiVersion => GameInfo().Version;

    private static double _speedBeforePause = 1;

    [PgslCommand("Paused", "Engine.Game.Paused = true", "Stop the game's time (Step events and physics see no time passing; Draw events still run) and start it again at the speed it had (false unless set)", "Game", Namespace = "Engine.Game", Flat = false)]
    public static bool GameApiPaused
    {
        get => GameGetSpeed() == 0;
        set
        {
            if (value == (GameGetSpeed() == 0)) return;
            if (value) { _speedBeforePause = GameGetSpeed(); GameSetSpeed(0); }
            else GameSetSpeed(_speedBeforePause > 0 ? _speedBeforePause : 1);
        }
    }

    // ── Engine.Model's parts (this instance's model) ─────────────────────────────────────

    [PgslCommand("Cast", "Engine.Model.Shadows.Cast = true", "Whether this instance's model casts a shadow (true unless set)", "Models", Namespace = "Engine.Model.Shadows", Flat = false)]
    public static bool ModelShadowsCast { get => ModelApiCastShadows; set => ModelApiCastShadows = value; }

    [PgslCommand("Receive", "Engine.Model.Shadows.Receive = true", "Whether shadows fall on this instance's model (true unless set)", "Models", Namespace = "Engine.Model.Shadows", Flat = false)]
    public static bool ModelShadowsReceive { get => ModelApiReceiveShadows; set => ModelApiReceiveShadows = value; }

    [PgslCommand("Strength", "Engine.Model.Glow.Strength = amount", "How brightly this instance's model glows in its own colours, 0 to 32 (0 unless set)", "Models", Namespace = "Engine.Model.Glow", Flat = false)]
    public static double ModelGlowStrength { get => ModelApiGlow; set => ModelApiGlow = value; }

    [PgslCommand("EmissionScale", "Engine.Model.Glow.EmissionScale = scale", "The light this model's materials give off: 0 puts its lamps out, 1 is as authored (1 unless set)", "Models", Namespace = "Engine.Model.Glow", Flat = false)]
    public static double ModelGlowEmissionScale { get => ModelApiEmissionScale; set => ModelApiEmissionScale = value; }

    // ── Smaller parts: room changes, debug keys, scripts, bursts, uploads, water, tiles, root motion ──
    // Values the engine only lets a script set are kept here too, starting at the engine's default.

    [PgslCommand("Fade", "Engine.Room.Change.Fade = seconds", "How long the cover takes to fade from a room just shown, 0 to 5 seconds (0.2 unless set; 0 lifts it at once)", "Rooms", Namespace = "Engine.Room.Change", Flat = false)]
    public static double RoomChangeFadePart { get => Project.RoomChangeScreen.FadeSeconds; set => RoomChangeFade(value); }

    [PgslCommand("MinimumTime", "Engine.Room.Change.MinimumTime = seconds", "The least time a room change's cover stays up, so a loading screen can be read, 0 to 60 seconds (0 unless set)", "Rooms", Namespace = "Engine.Room.Change", Flat = false)]
    public static double RoomChangeMinimumTimePart { get => Project.RoomChangeScreen.MinimumSeconds; set => RoomChangeMinimumTime(value); }

    [PgslCommand("ProgressBar", "Engine.Room.Change.ProgressBar = true", "Whether the engine draws its progress bar and text on a room change's cover (true unless set)", "Rooms", Namespace = "Engine.Room.Change", Flat = false)]
    public static bool RoomChangeProgressBarPart { get => Project.RoomChangeScreen.ShowProgress; set => RoomChangeProgressBar(value); }

    [PgslCommand("Text", "Engine.Room.Change.Text = \"Loading\"", "The words above the progress bar during a room change (\"Loading\" unless set; \"\" for none)", "Rooms", Namespace = "Engine.Room.Change", Flat = false)]
    public static string RoomChangeTextPart { get => Project.RoomChangeScreen.Text; set => RoomChangeText(value); }

    [PgslCommand("Remember", "Engine.Room.Streaming.Remember = true", "Streamed Objects (\"streamable\": true) come back where they were with their variables, and Objects or scenery a script destroyed stay gone, for the rest of the game, room by room (true unless set)", "Rooms", Namespace = "Engine.Room.Streaming", Flat = false)]
    public static bool RoomStreamingRemember
    {
        get => Genesis.Runtime.Scene.RoomSceneryStreamer.Remember;
        set => Genesis.Runtime.Scene.RoomSceneryStreamer.Remember = value;
    }

    [PgslCommand("Forget", "Engine.Room.Streaming.Forget()", "Forgets what every streamed Object was like and which were destroyed: each comes back as its room first made it (a new day's foes, a reset dungeon)", "Rooms", Namespace = "Engine.Room.Streaming", Flat = false)]
    public static void RoomStreamingForget() => Genesis.Runtime.Scene.RoomSceneryStreamer.ForgetAll();

    [PgslCommand("Save", "Engine.Room.Streaming.Save() -> text", "What every room remembers, as text for a save slot (SaveSlotWrite): each streamed Object and each Object of a persistent room where it was with its variables, what scripts destroyed, and what scripts made in persistent rooms; the room the game is in is taken as it is now", "Rooms", Namespace = "Engine.Room.Streaming", Flat = false)]
    public static string RoomStreamingSave()
    {
        if (ActiveGameContext is Project.ProjectGameContext { Scene: { } scene } game)
        {
            if (game.Room is { Settings.Persistent: true } room)
                Genesis.Runtime.Scene.RoomMemory.Leave(scene.World, room.Name, entity => game.ScriptHost?.FindBehaviorForEntity(entity) as PgslBehavior);
            foreach (var subsystem in scene.Subsystems)
                if (subsystem is Genesis.Runtime.Scene.RoomSceneryStreamer streamer) streamer.RememberLoaded(scene);
        }
        return Genesis.Runtime.Scene.RoomMemory.Write();
    }

    [PgslCommand("Load", "Engine.Room.Streaming.Load(text) -> bool", "Puts back what Engine.Room.Streaming.Save gave (from SaveSlotRead): each room comes back as saved the next time the game enters it, so change to the saved room after loading; false when the text is not such a save", "Rooms", Namespace = "Engine.Room.Streaming", Flat = false)]
    public static bool RoomStreamingLoad(string text) => Genesis.Runtime.Scene.RoomMemory.Read(text);

    [PgslCommand("Remembered", "Engine.Room.Streaming.Remembered", "How many streamed Objects and destroyed things are remembered now, in every room (read)", "Rooms", Namespace = "Engine.Room.Streaming", Flat = false)]
    public static double RoomStreamingRemembered => Genesis.Runtime.Scene.RoomSceneryStreamer.RememberedCount;

    [PgslCommand("Enabled", "Engine.Debug.Keys.Enabled = true", "The engine's debug keys (F6 debug screen, F12 screenshot...) in this run (true in Studio's Run, false in an exported game, unless set)", "Debug", Namespace = "Engine.Debug.Keys", Flat = false)]
    public static bool DebugKeysEnabledPart { get => DebugKeysEnabled(); set => DebugKeysSetEnabled(value); }

    [PgslCommand("InstructionLimit", "Engine.Game.Scripts.InstructionLimit = count", "Instructions each event body and function call may run before it is stopped as a hang (100000 unless set; up to 1000000000)", "Game", Namespace = "Engine.Game.Scripts", Flat = false)]
    public static double GameScriptsInstructionLimit { get => VM.PgslVm.CallInstructionLimit; set => ScriptInstructionLimit(value); }

    private static bool _scriptProfile;

    [PgslCommand("Profile", "Engine.Game.Scripts.Profile = true", "Time every event and function, for Engine.Game.Scripts.Report() (false unless set)", "Game", Namespace = "Engine.Game.Scripts", Flat = false)]
    public static bool GameScriptsProfile { get => _scriptProfile; set { _scriptProfile = value; ScriptProfileEnable(value); } }

    private static double _burstLimit = 256;

    [PgslCommand("Limit", "Engine.Particle.Bursts.Limit = count", "How many particle bursts may be alive at once, the oldest ending first (256 unless set)", "Particles", Namespace = "Engine.Particle.Bursts", Flat = false)]
    public static double ParticleBurstsLimit { get => _burstLimit; set { if (double.IsFinite(value)) { _burstLimit = Math.Clamp(Math.Round(value), 1, 100000); ParticleSetBurstLimit(_burstLimit); } } }

    [PgslCommand("Quality", "Engine.Particle.Quality = 0.5", "How many particles every effect makes, 0.1 to 1 of what it was authored with (1 unless set): fewer on a slow machine, each effect still recognisable; a one-off burst keeps at least one", "Particles", Namespace = "Engine.Particle", Flat = false)]
    public static double ParticleQualityPart
    {
        get => Genesis.Runtime.ECS.Components.ParticleQuality.Amount;
        set { if (double.IsFinite(value)) Genesis.Runtime.ECS.Components.ParticleQuality.Amount = (float)value; }
    }

    [PgslCommand("Enabled", "Engine.Particle.Enabled = false", "Whether particle effects run and are drawn (true unless set); off, every effect stops where it is and carries on when back on: a photo mode, a pause screen", "Particles", Namespace = "Engine.Particle", Flat = false)]
    public static bool ParticleEnabledPart { get => Genesis.Runtime.ECS.Components.ParticleQuality.Enabled; set => Genesis.Runtime.ECS.Components.ParticleQuality.Enabled = value; }

    [PgslCommand("Enabled", "Engine.Particle.Bursts.Enabled = false", "Whether new one-off bursts start, from ParticleBurstAt, batched bursts and the engine's own (true unless set); those playing finish", "Particles", Namespace = "Engine.Particle.Bursts", Flat = false)]
    public static bool ParticleBurstsEnabled { get => Genesis.Runtime.ECS.Components.ParticleQuality.Bursts; set => Genesis.Runtime.ECS.Components.ParticleQuality.Bursts = value; }

    [PgslCommand("Enabled", "Engine.Particle.Particles2D.Enabled = false", "Whether a 2D room's particles (Particle2DBurst, Particle2DFlow) start and are drawn (true unless set); its lights and darkness stay", "Particles", Namespace = "Engine.Particle.Particles2D", Flat = false)]
    public static bool ParticleParticles2DEnabled { get => Genesis.Runtime.ECS.Components.ParticleQuality.Particles2D; set => Genesis.Runtime.ECS.Components.ParticleQuality.Particles2D = value; }

    [PgslCommand("Enabled", "Engine.Particle.Gpu.Collision.Enabled = false", "Whether effects' particles bounce off, stick to or die on the ground and geometry as each effect was made to (true unless set); off, they pass through, for a cheaper frame", "Particles", Namespace = "Engine.Particle.Gpu.Collision", Flat = false)]
    public static bool ParticleGpuCollisionEnabled { get => Genesis.Runtime.ECS.Components.ParticleQuality.Collisions; set => Genesis.Runtime.ECS.Components.ParticleQuality.Collisions = value; }

    // ── Engine.Particle.Emitter: this instance's attached particles, changed as it plays ──

    private static bool TryEmitter(out Genesis.Runtime.ECS.World? world, out Genesis.Shared.ECS.Entity entity)
    {
        world = ActiveGameContext?.World;
        entity = default;
        int instanceId = GetContext()?.InstanceId ?? 0;
        if (world == null || instanceId <= 0) return false;
        entity = world.GetEntity(instanceId);
        return !entity.IsNull && world.Has<Genesis.Runtime.ECS.Components.ParticleComponent>(entity);
    }

    private static Genesis.Runtime.ECS.Components.ParticleLook EmitterLook() =>
        TryEmitter(out var world, out var entity)
            ? world!.GetRef<Genesis.Runtime.ECS.Components.ParticleComponent>(entity).Look ?? Genesis.Runtime.ECS.Components.ParticleLook.AsAuthored
            : Genesis.Runtime.ECS.Components.ParticleLook.AsAuthored;

    // Changes one value of the emitter's look, keeping the rest; back to null once it is as authored.
    private static void SetEmitterLook(Func<Genesis.Runtime.ECS.Components.ParticleLook, Genesis.Runtime.ECS.Components.ParticleLook> change)
    {
        if (!TryEmitter(out var world, out var entity)) return;
        ref var particles = ref world!.GetRef<Genesis.Runtime.ECS.Components.ParticleComponent>(entity);
        var look = change(particles.Look ?? Genesis.Runtime.ECS.Components.ParticleLook.AsAuthored);
        particles.Look = look == Genesis.Runtime.ECS.Components.ParticleLook.AsAuthored ? null : look;
    }

    [PgslCommand("Enabled", "Engine.Particle.Emitter.Enabled = false", "Whether this instance's attached particles are emitting (as its Object has them unless set); those already out live their lives", "Particles", Namespace = "Engine.Particle.Emitter", Flat = false)]
    public static bool ParticleEmitterEnabled { get => ParticleAttachedGetEmitting(); set => ParticleAttachedSetEmitting(value); }

    [PgslCommand("Rate", "Engine.Particle.Emitter.Rate = 40", "Particles this instance's attached effect makes a second, 0 to 10000 (the effect's own unless set)", "Particles", Namespace = "Engine.Particle.Emitter", Flat = false)]
    public static double ParticleEmitterRate { get => ParticleAttachedGetRate(); set { if (double.IsFinite(value)) ParticleAttachedSetRate(value); } }

    [PgslCommand("Size", "Engine.Particle.Emitter.Size = 2", "How big this instance's particles are, 0 to 10 times the effect's own (1 unless set): a fire that grows as it catches", "Particles", Namespace = "Engine.Particle.Emitter", Flat = false)]
    public static double ParticleEmitterSize
    {
        get => EmitterLook().Size;
        set { if (double.IsFinite(value)) SetEmitterLook(look => look with { Size = (float)Math.Clamp(value, 0, 10) }); }
    }

    [PgslCommand("Speed", "Engine.Particle.Emitter.Speed = 1.5", "How fast this instance's particles leave, 0 to 10 times the effect's own (1 unless set): an engine's exhaust at full throttle", "Particles", Namespace = "Engine.Particle.Emitter", Flat = false)]
    public static double ParticleEmitterSpeed
    {
        get => EmitterLook().Speed;
        set { if (double.IsFinite(value)) SetEmitterLook(look => look with { Speed = (float)Math.Clamp(value, 0, 10) }); }
    }

    [PgslCommand("Life", "Engine.Particle.Emitter.Life = 0.5", "How long this instance's particles live, 0.05 to 10 times the effect's own (1 unless set): smoke that lingers in still air", "Particles", Namespace = "Engine.Particle.Emitter", Flat = false)]
    public static double ParticleEmitterLife
    {
        get => EmitterLook().Life;
        set { if (double.IsFinite(value)) SetEmitterLook(look => look with { Life = (float)Math.Clamp(value, 0.05, 10) }); }
    }

    [PgslCommand("Gravity", "Engine.Particle.Emitter.Gravity = 0", "How strongly gravity pulls this instance's particles, -10 to 10 times the effect's own (1 unless set): 0 for sparks in space, below 0 to turn it around", "Particles", Namespace = "Engine.Particle.Emitter", Flat = false)]
    public static double ParticleEmitterGravity
    {
        get => EmitterLook().Gravity;
        set { if (double.IsFinite(value)) SetEmitterLook(look => look with { Gravity = (float)Math.Clamp(value, -10, 10) }); }
    }

    [PgslCommand("Color", "Engine.Particle.Emitter.Color = \"#80c0ff\"", "A colour this instance's particles' colours are multiplied by, as \"#rrggbb\" (#ffffff unless set): a magic effect in the caster's colour", "Particles", Namespace = "Engine.Particle.Emitter", Flat = false)]
    public static string ParticleEmitterColor
    {
        get => Hex(EmitterLook().Tint);
        set { if (TryHexColor(value, out Vector3 tint)) SetEmitterLook(look => look with { Tint = tint }); }
    }

    // ── Engine.Ui: every User Interface resource the game draws ──────────────────────────

    private static bool _uiEnabled = true, _uiScaleAuto = true, _uiFocusNavigation = true;
    private static float _uiScale = 1f;

    internal static void ResetUiParts()
    {
        _uiEnabled = true; _uiScaleAuto = true; _uiFocusNavigation = true; _uiScale = 1f;
    }

    [PgslCommand("Enabled", "Engine.Ui.Enabled = false", "Whether User Interface resources are drawn and answer the pointer, keys and controllers (true unless set): off for a photo mode or a cutscene, everything in them kept as it was", "User Interface", Namespace = "Engine.Ui", Flat = false)]
    public static bool UiEnabledPart { get => _uiEnabled; set => _uiEnabled = value; }

    [PgslCommand("Value", "Engine.Ui.Scale.Value = 1.25", "How large User Interfaces are drawn, 0.25 to 4 times (1 unless set): a player's text-size setting; what is anchored to a side of the window stays there", "User Interface", Namespace = "Engine.Ui.Scale", Flat = false)]
    public static double UiScaleValue { get => _uiScale; set { if (double.IsFinite(value)) _uiScale = (float)Math.Clamp(value, 0.25, 4); } }

    [PgslCommand("Enabled", "Engine.Ui.Scale.Auto.Enabled = false", "Whether User Interfaces stretch to fill the window (true unless set); off, they are drawn at their design pixels, times Engine.Ui.Scale.Value, anchored to the window's sides, crisp on a wide or large screen", "User Interface", Namespace = "Engine.Ui.Scale.Auto", Flat = false)]
    public static bool UiScaleAutoEnabled { get => _uiScaleAuto; set => _uiScaleAuto = value; }

    [PgslCommand("Enabled", "Engine.Ui.Focus.Navigation.Enabled = false", "Whether the arrow keys, a controller's d-pad and left stick move the focus between a User Interface's elements and Enter, Space or A press them (true unless set); off, a game moves the focus itself", "User Interface", Namespace = "Engine.Ui.Focus.Navigation", Flat = false)]
    public static bool UiFocusNavigationEnabled { get => _uiFocusNavigation; set => _uiFocusNavigation = value; }

    [PgslCommand("Budget", "Engine.Mesh.Upload.Budget = milliseconds", "How long a frame may spend sending script meshes to the graphics card before the rest wait for the next (4 unless set)", "Meshes", Namespace = "Engine.Mesh.Upload", Flat = false)]
    public static double MeshUploadBudgetPart { get => Rendering.ScriptMeshes.UploadBudgetMilliseconds; set => MeshSetUploadBudget(value); }

    [PgslCommand("Enabled", "Engine.Water.Reflections.Enabled = true", "Water mirrors the scene above it (off unless set; a half-resolution pass)", "Water", Namespace = "Engine.Water.Reflections", Flat = false)]
    public static bool WaterReflectionsEnabled { get => WaterReflections; set => WaterReflections = value; }

    [PgslCommand("Enabled", "Engine.Tile.Autotile.Enabled = true", "Painting and erasing tiles from a script picks each tile from its neighbours on an autotiled tile set (true unless set)", "Tiles", Namespace = "Engine.Tile.Autotile", Flat = false)]
    public static bool TileAutotileEnabled { get => TileApiAutotile; set => TileApiAutotile = value; }

    // ── Colours as #rrggbb ───────────────────────────────────────────────────────────────

    private static string HexColor(Vector4 color) =>
        "#" + Channel(color.X) + Channel(color.Y) + Channel(color.Z);

    private static string Channel(float value) => ((int)Math.Round(Math.Clamp(value, 0f, 1f) * 255)).ToString("x2");

    private static bool TryHexColor(string? text, out Vector3 rgb)
    {
        rgb = default;
        string hex = text?.Trim().TrimStart('#') ?? "";
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int value)) return false;
        rgb = new Vector3((value >> 16 & 255) / 255f, (value >> 8 & 255) / 255f, (value & 255) / 255f);
        return true;
    }
}
