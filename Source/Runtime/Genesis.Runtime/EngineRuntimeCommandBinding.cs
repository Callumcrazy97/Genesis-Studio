using System;
using System.Numerics;
using Genesis.Runtime.Scene;
using Genesis.Shared.Commands;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime;

/// <summary>
/// Turns stateful <c>Engine.*</c> PGSL commands into live window, camera and renderer state.
/// Keeping the subscriptions together makes a catalogued command's runtime consumer auditable
/// and prevents static event handlers leaking between player sessions.
/// </summary>
public sealed class EngineRuntimeCommandBinding : IDisposable
{
    private readonly RuntimeScene _scene;
    private readonly IGameWindow _window;
    private readonly RendererOptions _options;
    private readonly IRenderController _renderer;
    private bool _disposed;

    public RenderDebugView DebugView { get; private set; } = RenderDebugView.Shaded;

    public EngineRuntimeCommandBinding(
        RuntimeScene scene,
        IGameWindow window,
        RendererOptions options,
        IRenderController renderer = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _renderer = renderer;

        Engine.FogChanged += OnFogChanged;
        Engine.FogStartChanged += OnFogStartChanged;
        Engine.FogEndChanged += OnFogEndChanged;
        Engine.FogColourChanged += OnFogColourChanged;
        Engine.FogDensityChanged += OnFogDensityChanged;

        Engine.VsyncChanged += OnVsyncChanged;
        Engine.FpsTargetChanged += OnFpsTargetChanged;
        Engine.WindowModeChanged += OnWindowModeChanged;
        Engine.WindowTitleChanged += OnWindowTitleChanged;
        Engine.WindowSizeChanged += OnWindowSizeChanged;

        Engine.CameraFovChanged += OnCameraFovChanged;
        Engine.CameraNearPlaneChanged += OnCameraNearPlaneChanged;
        Engine.CameraFarPlaneChanged += OnCameraFarPlaneChanged;
        Engine.CameraPositionChanged += OnCameraPositionChanged;
        Engine.CameraTargetChanged += OnCameraTargetChanged;
        Engine.Camera2DRegistryChanged += OnCamera2DRegistryChanged;
        Engine.Camera3DRegistryChanged += OnCamera3DRegistryChanged;

        Engine.LightingChanged += OnLightingChanged;
        Engine.ShadowsChanged += OnShadowsChanged;
        Engine.ShadowStrengthChanged += OnShadowStrengthChanged;
        Engine.AmbientLightChanged += OnAmbientLightChanged;
        Engine.GlobalLightColorChanged += OnGlobalLightColorChanged;
        Engine.GlobalLightDirectionChanged += OnGlobalLightDirectionChanged;
        Engine.CullingChanged += OnCullingChanged;
        Engine.FrustumCullingChanged += OnFrustumCullingChanged;
        Engine.DebugViewChanged += OnDebugViewChanged;
        Engine.GpuFunctionChanged += OnGpuFunctionChanged;

        Engine.RectDrawSubmitted += OnRectDrawSubmitted;
        Engine.LineDrawSubmitted += OnLineDrawSubmitted;
        Engine.TextDrawSubmitted += OnTextDrawSubmitted;
    }

    private void OnVsyncChanged(bool enabled) => _window.VSync = enabled;

    private void OnFpsTargetChanged(int target) => _window.TargetFps = Math.Max(0, target);

    private void OnWindowModeChanged(string mode)
    {
        _window.Mode = mode?.Trim().ToLowerInvariant() switch
        {
            "fullscreen" => WindowMode.Fullscreen,
            "borderless" => WindowMode.Borderless,
            _ => WindowMode.Windowed,
        };
    }

    private void OnWindowTitleChanged(string title)
    {
        if (!string.IsNullOrEmpty(title)) _window.Title = title;
    }

    private void OnWindowSizeChanged(int width, int height) => _window.SetSize(width, height);

    private void OnCameraFovChanged(float fovDegrees) =>
        _scene.Camera3D.FieldOfView = Math.Clamp(fovDegrees, 1f, 179f) * (MathF.PI / 180f);

    private void OnCameraNearPlaneChanged(float nearPlane)
    {
        _scene.Camera3D.NearPlane = Math.Clamp(nearPlane, 0.001f, _scene.Camera3D.FarPlane - 0.001f);
    }

    private void OnCameraFarPlaneChanged(float farPlane)
    {
        _scene.Camera3D.FarPlane = Math.Max(_scene.Camera3D.NearPlane + 0.001f, farPlane);
    }

    private void OnCameraPositionChanged(Vector3 position) => _scene.Camera3D.Position = position;

    private void OnCameraTargetChanged(Vector3 target)
    {
        Vector3 delta = target - _scene.Camera3D.Position;
        if (delta.LengthSquared() < 0.000001f) return;
        Vector3 direction = Vector3.Normalize(delta);
        _scene.Camera3D.Yaw = MathF.Atan2(direction.X, -direction.Z);
        _scene.Camera3D.Pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
    }

    private void OnCamera3DRegistryChanged(int id)
    {
        if (!Engine.TryGetCamera3DPose(
                id,
                out Vector3 position,
                out float yawDegrees,
                out float pitchDegrees,
                out float fovDegrees,
                out float aspectRatio,
                out float nearZ,
                out float farZ))
        {
            return;
        }

        // Indexed cameras share Room viewport slot ids. Persist pose onto the matching slot when a
        // live room is bound; the play camera only follows the active id.
        Scripting.PgslCommands.SyncEngineCamera3DToViewport(
            id, position, fovDegrees, nearZ, farZ, activate: id == Engine.ActiveCamera3DId);

        if (id != Engine.ActiveCamera3DId) return;

        _scene.Camera3D.Position = position;
        _scene.Camera3D.Yaw = yawDegrees * (MathF.PI / 180f);
        _scene.Camera3D.Pitch = pitchDegrees * (MathF.PI / 180f);
        _scene.Camera3D.FieldOfView = Math.Clamp(fovDegrees, 1f, 179f) * (MathF.PI / 180f);
        _scene.Camera3D.AspectRatio = Math.Max(0.0001f, aspectRatio);
        _scene.Camera3D.NearPlane = Math.Clamp(nearZ, 0.001f, farZ - 0.001f);
        _scene.Camera3D.FarPlane = Math.Max(_scene.Camera3D.NearPlane + 0.001f, farZ);
    }

    private void OnCamera2DRegistryChanged(int id)
    {
        if (!Engine.TryGetCamera2DPose(
                id, out float x, out float y, out float left, out float right, out float top, out float bottom))
        {
            return;
        }

        Scripting.PgslCommands.SyncEngineCamera2DToViewport(
            id, x, y, left, right, top, bottom, activate: id == Engine.ActiveCamera2DId);
    }

    private void OnLightingChanged(bool enabled) => _scene.Environment.LightingEnabled = enabled;

    private void OnShadowsChanged(bool enabled) => _scene.Environment.ShadowsEnabled = enabled;

    private void OnShadowStrengthChanged(float strength) =>
        _scene.Environment.ShadowStrength = Math.Clamp(strength, 0f, 1f);

    private void OnAmbientLightChanged(Vector3 color) =>
        _scene.Environment.AmbientOverrideColor = ClampColor(color);

    private void OnGlobalLightColorChanged(Vector3 color) =>
        _scene.Environment.SunColor = new Vector4(ClampColor(color), 1f);

    private void OnGlobalLightDirectionChanged(Vector3 direction)
    {
        if (direction.LengthSquared() < 0.000001f) return;
        _scene.Environment.LightDirectionOverride = Vector3.Normalize(direction);
    }

    private void OnCullingChanged(string windingOrder, string faceMode)
    {
        _options.FrontCounterClockwise = string.Equals(
            windingOrder,
            "CCW",
            StringComparison.OrdinalIgnoreCase);

        bool disabled = string.Equals(faceMode, "Off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(faceMode, "None", StringComparison.OrdinalIgnoreCase);
        _options.FaceCulling = !disabled;
        _options.CullFrontFaces = string.Equals(faceMode, "Front", StringComparison.OrdinalIgnoreCase);
    }

    private void OnFrustumCullingChanged(bool enabled) => _options.FrustumCulling = enabled;

    private void OnDebugViewChanged(RenderDebugView view) => DebugView = view;

    private void OnGpuFunctionChanged(string function, bool enabled)
    {
        if (function.Equals("Shadows", StringComparison.OrdinalIgnoreCase))
            _scene.Environment.ShadowsEnabled = enabled;
        else if (function.Equals("Fog", StringComparison.OrdinalIgnoreCase))
            _scene.Environment.FogEnabled = enabled;
        else if (function.Equals("Lighting", StringComparison.OrdinalIgnoreCase))
            _scene.Environment.LightingEnabled = enabled;
    }

    private void OnFogChanged(bool enabled) => _scene.Environment.FogEnabled = enabled;

    private void OnFogStartChanged(float depth)
    {
        _scene.Environment.FogStart = depth;
        if (_scene.Environment.FogEnd <= depth) _scene.Environment.FogEnd = depth + 0.001f;
    }

    private void OnFogEndChanged(float endDepth) =>
        _scene.Environment.FogEnd = Math.Max(_scene.Environment.FogStart + 0.001f, endDepth);

    private void OnFogColourChanged(Vector4 color) => _scene.Environment.FogColor = color;

    private void OnFogDensityChanged(float density) => _scene.Environment.FogDensity = Math.Max(0f, density);

    private void OnRectDrawSubmitted(Engine.RectDrawSpec spec) => _renderer?.DrawRect(
        spec.X,
        spec.Y,
        spec.W,
        spec.H,
        new RenderColor(spec.R, spec.G, spec.B, spec.A),
        spec.Filled);

    private void OnLineDrawSubmitted(Engine.LineDrawSpec spec) => _renderer?.DrawLine(
        spec.X1,
        spec.Y1,
        spec.X2,
        spec.Y2,
        new RenderColor(spec.R, spec.G, spec.B, spec.A));

    private void OnTextDrawSubmitted(Engine.TextDrawSpec spec) => _renderer?.DrawText(
        spec.Text,
        spec.X,
        spec.Y,
        spec.Size,
        new RenderColor(spec.R, spec.G, spec.B, spec.A));

    /// <summary>
    /// Submits persistent command-owned render state after BeginFrame. The forward renderer clears
    /// fog volumes per frame, so a live PGSL volume must be re-added every frame rather than only
    /// when its authoring command changes.
    /// </summary>
    public void ApplyFrameRenderState()
    {
        if (_renderer == null) return;
        foreach (FogVolume volume in Engine.GetScriptedFogVolumes().Values)
            _renderer.AddFogVolume(volume);
    }

    private static Vector3 ClampColor(Vector3 color) => Vector3.Clamp(color, Vector3.Zero, Vector3.One);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Engine.FogChanged -= OnFogChanged;
        Engine.FogStartChanged -= OnFogStartChanged;
        Engine.FogEndChanged -= OnFogEndChanged;
        Engine.FogColourChanged -= OnFogColourChanged;
        Engine.FogDensityChanged -= OnFogDensityChanged;

        Engine.VsyncChanged -= OnVsyncChanged;
        Engine.FpsTargetChanged -= OnFpsTargetChanged;
        Engine.WindowModeChanged -= OnWindowModeChanged;
        Engine.WindowTitleChanged -= OnWindowTitleChanged;
        Engine.WindowSizeChanged -= OnWindowSizeChanged;

        Engine.CameraFovChanged -= OnCameraFovChanged;
        Engine.CameraNearPlaneChanged -= OnCameraNearPlaneChanged;
        Engine.CameraFarPlaneChanged -= OnCameraFarPlaneChanged;
        Engine.CameraPositionChanged -= OnCameraPositionChanged;
        Engine.CameraTargetChanged -= OnCameraTargetChanged;
        Engine.Camera2DRegistryChanged -= OnCamera2DRegistryChanged;
        Engine.Camera3DRegistryChanged -= OnCamera3DRegistryChanged;

        Engine.LightingChanged -= OnLightingChanged;
        Engine.ShadowsChanged -= OnShadowsChanged;
        Engine.ShadowStrengthChanged -= OnShadowStrengthChanged;
        Engine.AmbientLightChanged -= OnAmbientLightChanged;
        Engine.GlobalLightColorChanged -= OnGlobalLightColorChanged;
        Engine.GlobalLightDirectionChanged -= OnGlobalLightDirectionChanged;
        Engine.CullingChanged -= OnCullingChanged;
        Engine.FrustumCullingChanged -= OnFrustumCullingChanged;
        Engine.DebugViewChanged -= OnDebugViewChanged;
        Engine.GpuFunctionChanged -= OnGpuFunctionChanged;

        Engine.RectDrawSubmitted -= OnRectDrawSubmitted;
        Engine.LineDrawSubmitted -= OnLineDrawSubmitted;
        Engine.TextDrawSubmitted -= OnTextDrawSubmitted;
    }
}
