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
