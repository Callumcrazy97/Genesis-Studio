using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Genesis.Rendering.Core;
using Genesis.Rendering.Viewport;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

public enum EditorCameraControlMethod { Orbit, Free }

/// <summary>Exact view/projection supplied by an authored game camera.</summary>
public readonly record struct EditorCameraOverride(
    Matrix4x4 View,
    Matrix4x4 Projection,
    Vector3 Eye,
    Vector3 Forward);

/// <summary>Orbit camera shared by all 3D editor viewports.</summary>
public sealed class OrbitCameraRig
{
    public Vector3 Target = new(0f, 0f, 0f);
    public float Yaw = 0.65f;
    public float Pitch = -0.52f;
    public float Distance = 26f;
    public float MaximumDistance { get; set; } = 1600f;

    public Vector3 Eye
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            Vector3 forward = new(cp * MathF.Sin(Yaw), MathF.Sin(Pitch), cp * MathF.Cos(Yaw));
            return Target - forward * Distance;
        }
    }

    public Vector3 Forward => Vector3.Normalize(Target - Eye);

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitY);

    public void Orbit(float dxPixels, float dyPixels)
    {
        Yaw -= dxPixels * 0.0085f;
        Pitch = Math.Clamp(Pitch - dyPixels * 0.0085f, -1.45f, 1.45f);
    }

    public void Pan(float dxPixels, float dyPixels)
    {
        Vector3 forward = Forward;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Cross(right, forward);
        float scale = Distance * 0.0018f;
        Target += (-right * dxPixels + up * dyPixels) * scale;
    }

    public void Zoom(float wheelTicks)
    {
        Distance = Math.Clamp(Distance * MathF.Pow(0.88f, wheelTicks), 1.2f, Math.Max(1.2f, MaximumDistance));
    }

    public void Look(float dxPixels, float dyPixels)
    {
        Vector3 eye = Eye;
        Orbit(dxPixels, dyPixels);
        Target = eye + Forward * Distance;
    }

    public void Travel(float wheelTicks) => Target += Forward * wheelTicks * MathF.Max(.25f, Distance * .08f);

    public void CopyFrom(OrbitCameraRig other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Target = other.Target;
        Yaw = other.Yaw;
        Pitch = other.Pitch;
        Distance = other.Distance;
        MaximumDistance = other.MaximumDistance;
    }

    public void LookAt(Vector3 target) => Target = target;
}

/// <summary>
/// Hosted D3D11 viewport with the shared editor lighting, orbit camera, screen/world helpers,
/// and readback capture. Editors subscribe <see cref="DrawScene"/> (3D submit phase) and
/// <see cref="DrawOverlay"/> (2D sprite overlay in swap-chain pixel space).
/// </summary>
public sealed partial class EditorViewport3D : Panel
{
    private readonly D3DViewportControl _viewport;
    private bool _orbiting;
    private bool _panning;
    private Point _lastMouse;
    private long _lastNavigationFrame;
    private MeshHandle _floorMesh;
    private MeshHandle _floorPlainMesh;

    public EditorFloorStyle FloorStyle { get; set; } = EditorFloorStyle.Checkerboard;
    /// <summary>Preview-only ground elevation; authored geometry and pivots remain unchanged.</summary>
    public float FloorHeight { get; set; }

    private const int FloorTilesPerAxis = 80;
    private const float FloorTileSize = 4f;
    private const float FloorWorldSize = FloorTilesPerAxis * FloorTileSize;
    private const float FloorPatternPeriod = FloorTileSize * 2f;

    public EditorViewport3D()
    {
        BackColor = EditorChrome.Canvas;
        Dock = DockStyle.Fill;
        _viewport = new D3DViewportControl
        {
            Dock = DockStyle.Fill,
            DriveMode = ViewportDriveMode.Timer,
            TargetFps = 60,
        };
        _viewport.OnRender += OnViewportRender;
        _viewport.OnPostFrame += OnViewportPostFrame;
        Controls.Add(_viewport);

        _viewport.MouseDown += OnViewportMouseDown;
        _viewport.MouseMove += OnViewportMouseMove;
        _viewport.MouseUp += OnViewportMouseUp;
        _viewport.MouseCaptureChanged += (_, _) =>
        {
            if (!_viewport.Capture) { _orbiting = false; _panning = false; }
        };
        _viewport.MouseWheel += OnViewportMouseWheel;
    }

    public OrbitCameraRig Camera { get; } = new();

    public D3DViewportControl Host => _viewport;

    /// <summary>Per-viewport backend for Help → Commands Visual Test. Null uses Studio's preference.</summary>
    public RenderBackendOption? BackendOverride
    {
        get => _viewport.BackendOverride;
        set => _viewport.BackendOverride = value;
    }

    // ── 2D mode ──────────────────────────────────────────────────────────────────

    /// <summary>When true the viewport renders through <see cref="DrawScene2D"/> with the
    /// orthographic sprite camera instead of the 3D pipeline.</summary>
    public bool Mode2D { get; set; }

    /// <summary>2D camera centre in world units (pixels).</summary>
    public float Camera2DX { get; set; }

    public float Camera2DY { get; set; }

    public float Zoom2D { get; set; } = 1f;

    /// <summary>2D submit phase — camera already applied; world units are pixels.</summary>
    public event Action<IRenderController>? DrawScene2D;

    /// <summary>2D background clear colour (room background).</summary>
    public Func<(float R, float G, float B)>? Background2D { get; set; }

    public Vector2 SurfaceToWorld2D(PointF surfacePoint) => new(
        Camera2DX + (surfacePoint.X - SurfaceWidth * 0.5f) / Zoom2D,
        Camera2DY + (surfacePoint.Y - SurfaceHeight * 0.5f) / Zoom2D);

    public Vector2 World2DToSurface(Vector2 world) => new(
        (world.X - Camera2DX) * Zoom2D + SurfaceWidth * 0.5f,
        (world.Y - Camera2DY) * Zoom2D + SurfaceHeight * 0.5f);

    public Vector2 ControlToWorld2D(Point clientPoint)
    {
        PointF surface = ControlToSurface(clientPoint);
        return SurfaceToWorld2D(surface);
    }

    public float FieldOfViewDegrees { get; set; } = 60f;

    public float NearPlane { get; set; } = 0.1f;

    public float FarPlane { get; set; } = 900f;

    /// <summary>
    /// Optional authored camera matrices. Room Game Camera preview uses the runtime camera math
    /// through this hook instead of approximating it with the editor orbit rig.
    /// </summary>
    public Func<EditorCameraOverride>? CameraOverrideFactory { get; set; }

    /// <summary>When false, camera mouse navigation is suspended (a tool drag owns the mouse).</summary>
    public bool NavigationEnabled { get; set; } = true;
    public EditorCameraControlMethod ControlMethod { get; set; } = EditorCameraControlMethod.Orbit;
    public bool MiddleButtonPans { get; set; }
    public Func<Point, int, Keys, bool>? WheelInputHandler { get; set; }

    /// <summary>
    /// When true, left-drag orbits/looks this camera. Used by the second-camera inset so a click-drag
    /// looks left/right without using the main view's RMB orbit.
    /// </summary>
    public bool LookWithLeftButton { get; set; }

    /// <summary>Raised after orbit, pan, or zoom so a pinned secondary slot can copy this view.</summary>
    public event Action? ViewChanged;

    /// <summary>3D submit phase — camera and lighting state are already applied.</summary>
    public event Action<IRenderController>? DrawScene;

    /// <summary>2D overlay phase in swap-chain pixel coordinates (gizmos, HUD).</summary>
    public event Action<IRenderController>? DrawOverlay;

    /// <summary>Base scene state factory; editors replace to customise (fog, floor…).</summary>
    public Func<Mesh3DState>? SceneStateFactory { get; set; }
    public Editor3DViewSettings ViewSettings { get; private set; } = new();
    public Mesh3DState InspectionState => ViewSettings.Apply(SceneStateFactory?.Invoke() ?? EditorSceneLighting.Create(showFloor: true));

    public int SurfaceWidth => Math.Max(1, _viewport.RenderWidth);

    public int SurfaceHeight => Math.Max(1, _viewport.RenderHeight);

    public Matrix4x4 ViewMatrix { get; private set; } = Matrix4x4.Identity;

    public Matrix4x4 ProjectionMatrix { get; private set; } = Matrix4x4.Identity;

    /// <summary>Maps a control-space point to swap-chain pixel space (fixed 1280×720 buffer).</summary>
    public PointF ControlToSurface(Point clientPoint)
    {
        float sx = SurfaceWidth / (float)Math.Max(1, _viewport.ClientWidth);
        float sy = SurfaceHeight / (float)Math.Max(1, _viewport.ClientHeight);
        return new PointF(clientPoint.X * sx, clientPoint.Y * sy);
    }

    /// <summary>Maps swap-chain pixel coordinates back to viewport client coordinates.</summary>
    public PointF SurfaceToControl(PointF surfacePoint)
    {
        float sx = _viewport.ClientWidth / (float)SurfaceWidth;
        float sy = _viewport.ClientHeight / (float)SurfaceHeight;
        return new PointF(surfacePoint.X * sx, surfacePoint.Y * sy);
    }

    /// <summary>Builds a world-space picking ray through a control-space point.</summary>
    public (Vector3 Origin, Vector3 Direction) PickRay(Point clientPoint)
    {
        PointF surface = ControlToSurface(clientPoint);
        float ndcX = surface.X / SurfaceWidth * 2f - 1f;
        float ndcY = 1f - surface.Y / SurfaceHeight * 2f;

        Matrix4x4.Invert(ViewMatrix * ProjectionMatrix, out Matrix4x4 invViewProj);
        Vector4 nearPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), invViewProj);
        Vector4 farPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), invViewProj);
        Vector3 near = new Vector3(nearPoint.X, nearPoint.Y, nearPoint.Z) / nearPoint.W;
        Vector3 far = new Vector3(farPoint.X, farPoint.Y, farPoint.Z) / farPoint.W;
        return (near, Vector3.Normalize(far - near));
    }

    /// <summary>Projects a world position to swap-chain pixel space; Z holds NDC depth.</summary>
    public Vector3 WorldToSurface(Vector3 world)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), ViewMatrix * ProjectionMatrix);
        if (MathF.Abs(clip.W) < 1e-6f)
        {
            return new Vector3(-10000f, -10000f, 2f);
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        float ndcZ = clip.Z / clip.W;
        return new Vector3(
            (ndcX * 0.5f + 0.5f) * SurfaceWidth,
            (0.5f - ndcY * 0.5f) * SurfaceHeight,
            ndcZ);
    }

    /// <summary>Intersects the pick ray with a horizontal plane (y = planeY).</summary>
    public bool RayToGround(Point clientPoint, float planeY, out Vector3 hit)
    {
        (Vector3 origin, Vector3 direction) = PickRay(clientPoint);
        hit = default;
        if (MathF.Abs(direction.Y) < 1e-6f)
        {
            return false;
        }

        float t = (planeY - origin.Y) / direction.Y;
        if (t <= 0f)
        {
            return false;
        }

        hit = origin + direction * t;
        return true;
    }

    public Bitmap? CaptureFrame(int settleFrames = 3) => _viewport.ReadbackFrameToBitmap(settleFrames);

    /// <summary>
    /// Ground reference plate with counter-clockwise triangles viewed from above.
    /// Triangle cross products agree with the upward vertex normals and shared mesh defaults.
    /// </summary>
    private void EnsureFloorMesh(IRenderController renderer)
    {
        if (!_floorMesh.IsValid)
        {
            BuildStableCheckerFloor(out MeshVertex[] vertices, out ushort[] indices);
            _floorMesh = renderer.RegisterMesh(vertices, indices);
        }

        if (!_floorPlainMesh.IsValid)
        {
            BuildPlainFloor(out MeshVertex[] vertices, out ushort[] indices);
            _floorPlainMesh = renderer.RegisterMesh(vertices, indices);
        }
    }

    private static void BuildStableCheckerFloor(out MeshVertex[] vertices, out ushort[] indices)
    {
        vertices = new MeshVertex[FloorTilesPerAxis * FloorTilesPerAxis * 4];
        indices = new ushort[FloorTilesPerAxis * FloorTilesPerAxis * 6];
        Vector4 light = new(0.92f, 0.93f, 0.96f, 1f);
        Vector4 dark = new(0.74f, 0.76f, 0.81f, 1f);
        float origin = -FloorWorldSize * 0.5f;

        int vi = 0;
        int ii = 0;
        for (int z = 0; z < FloorTilesPerAxis; z++)
        for (int x = 0; x < FloorTilesPerAxis; x++)
        {
            float x0 = origin + x * FloorTileSize;
            float z0 = origin + z * FloorTileSize;
            float x1 = x0 + FloorTileSize;
            float z1 = z0 + FloorTileSize;
            Vector4 color = ((x + z) & 1) == 0 ? light : dark;

            vertices[vi + 0] = FloorVertex(x0, z0, color, 0f, 0f);
            vertices[vi + 1] = FloorVertex(x1, z0, color, 1f, 0f);
            vertices[vi + 2] = FloorVertex(x1, z1, color, 1f, 1f);
            vertices[vi + 3] = FloorVertex(x0, z1, color, 0f, 1f);
            indices[ii + 0] = (ushort)(vi + 0);
            indices[ii + 1] = (ushort)(vi + 2);
            indices[ii + 2] = (ushort)(vi + 1);
            indices[ii + 3] = (ushort)(vi + 0);
            indices[ii + 4] = (ushort)(vi + 3);
            indices[ii + 5] = (ushort)(vi + 2);
            vi += 4;
            ii += 6;
        }
    }

    private static void BuildPlainFloor(out MeshVertex[] vertices, out ushort[] indices)
    {
        float half = FloorWorldSize * 0.5f;
        vertices =
        [
            FloorVertex(-half, -half, Vector4.One, 0f, 0f),
            FloorVertex(half, -half, Vector4.One, 1f, 0f),
            FloorVertex(half, half, Vector4.One, 1f, 1f),
            FloorVertex(-half, half, Vector4.One, 0f, 1f),
        ];
        indices = [0, 2, 1, 0, 3, 2];
    }

    private static MeshVertex FloorVertex(float x, float z, Vector4 color, float u, float v) => new()
    {
        Position = new Vector3(x, 0f, z),
        Normal = Vector3.UnitY,
        Color = color,
        UV = new Vector2(u, v),
    };

    private static float SnapFloorTranslation(float value) =>
        MathF.Floor(value / FloorPatternPeriod) * FloorPatternPeriod;

    private void OnViewportRender(IRenderController renderer)
    {
        int width = Math.Max(1, renderer.PixelWidth);
        int height = Math.Max(1, renderer.PixelHeight);

        if (Mode2D)
        {
            renderer.Set3DFrameActive(false);
            renderer.SetViewport(0, 0, width, height);

            EngineFogDefaults fog = EngineRenderingDefaults.Fog;
            RoomFogState spriteFog = RoomFogState.Create(fog.Enabled, fog.Color, fog.Start, fog.End);
            renderer.SetRoomFog(spriteFog);

            (float r, float g, float b) = Background2D?.Invoke() ?? (0.07f, 0.09f, 0.14f);
            Vector4 foggedBackground = spriteFog.ApplyToBackground(new Vector4(r, g, b, 1f));
            renderer.Clear(foggedBackground.X, foggedBackground.Y, foggedBackground.Z, foggedBackground.W);
            renderer.SetCamera2D(Camera2DX, Camera2DY, Zoom2D, 0f);
            DrawScene2D?.Invoke(renderer);
            return;
        }

        // 3D fog is handled by Mesh3DState/ForwardRenderer, never by the sprite layer.
        renderer.SetRoomFog(RoomFogState.Disabled);

        EditorCameraOverride? cameraOverride = CameraOverrideFactory?.Invoke();
        Vector3 cameraForward = cameraOverride?.Forward ?? Camera.Forward;
        Mesh3DState state = InspectionState;
        state.CameraForward = cameraForward;
        state.CameraFarPlane = FarPlane;

        // The renderer draws its OWN builtin floor automatically at end-of-frame whenever
        // ShowFloor is true (independent of anything submitted via DrawMesh), using a fixed
        // 500-unit/500-tile mesh baked once in RegisterBuiltinMeshes. That mesh's winding is
        // now fixed (see BuildFloor), so leaving ShowFloor on here would render a SECOND,
        // fine-grained floor on top of ours that we have no size/tile control over — every
        // earlier attempt to tune the checker via our own mesh had no visible effect because
        // this hidden second floor was what actually filled the frame. Request our own
        // coarse-tile plate instead and suppress the renderer's builtin one.
        bool wantsFloor = state.ShowFloor && FloorStyle is EditorFloorStyle.Checkerboard or EditorFloorStyle.Plain;
        state.ShowFloor = false;

        renderer.Set3DFrameActive(true);
        renderer.SetViewport(0, 0, width, height);
        Vector3 background = state.BackgroundColor;
        renderer.Clear(background.X, background.Y, background.Z, 1f);

        ViewMatrix = cameraOverride?.View ?? Camera.View;
        ProjectionMatrix = cameraOverride?.Projection ?? Matrix4x4.CreatePerspectiveFieldOfView(
            FieldOfViewDegrees * MathF.PI / 180f,
            width / (float)height,
            NearPlane,
            FarPlane);
        renderer.SetCamera3D(ViewMatrix, ProjectionMatrix);
        renderer.SetMesh3DState(state);

        if (wantsFloor)
        {
            EnsureFloorMesh(renderer);
            Vector3 eye = cameraOverride?.Eye ?? Camera.Eye;
            // Generous margin over our largest editor orbit distance (~30 units) without
            // extending so far toward the horizon that the checker overwhelms the scene.
            Vector3 floorTranslation = state.FloorFollowsCamera
                ? new Vector3(SnapFloorTranslation(eye.X), FloorHeight, SnapFloorTranslation(eye.Z))
                : new Vector3(0, FloorHeight, 0);
            renderer.DrawMesh(new MeshDrawCall
            {
                Mesh = FloorStyle == EditorFloorStyle.Plain ? _floorPlainMesh : _floorMesh,
                World = Matrix4x4.CreateTranslation(floorTranslation),
                Tint = new RenderColor(state.FloorColor.X, state.FloorColor.Y, state.FloorColor.Z),
                Alpha = 1f,
                Flags = MeshDrawFlags.NoShadow | MeshDrawFlags.EditorReference,
            });
        }

        DrawScene?.Invoke(renderer);
    }

    private void OnViewportPostFrame(IRenderController renderer)
    {
        if (DrawOverlay is null)
        {
            return;
        }

        // Overlay sprites are submitted after EndFrame and flushed with the centred
        // pixel-space camera, so gizmos/HUD stay in swap-chain pixel coordinates in
        // both 2D and 3D modes regardless of the world camera used by the scene pass.
        DrawOverlay.Invoke(renderer);
        renderer.FlushOverlaySprites();
    }

    private void OnViewportMouseDown(object? sender, MouseEventArgs e)
        => BeginNavigationDrag(e.Location, e.Button, ModifierKeys);

    public void BeginNavigationDrag(Point location, MouseButtons button, Keys modifiers)
    {
        _lastMouse = location;
        if (_viewport.CanFocus) _viewport.Focus();
        if (!NavigationEnabled)
        {
            return;
        }

        if (LookWithLeftButton && button == MouseButtons.Left)
        {
            _orbiting = true;
            _panning = false;
            return;
        }

        if (button == MouseButtons.Middle || button == MouseButtons.Right)
        {
            _viewport.Capture = true;
            bool pan = MiddleButtonPans ? button == MouseButtons.Middle : (modifiers & Keys.Shift) != 0;
            _orbiting = !pan;
            _panning = pan;
        }
    }

    private void OnViewportMouseMove(object? sender, MouseEventArgs e)
        => MoveNavigationDrag(e.Location);

    public void MoveNavigationDrag(Point location)
    {
        int dx = location.X - _lastMouse.X;
        int dy = location.Y - _lastMouse.Y;
        _lastMouse = location;
        if (!NavigationEnabled) return;
        if (Mode2D)
        {
            if (_orbiting || _panning)
            {
                float scale = SurfaceWidth / (float)Math.Max(1, _viewport.ClientWidth);
                Camera2DX -= dx * scale / Zoom2D;
                Camera2DY -= dy * scale / Zoom2D;
                ViewChanged?.Invoke();
                RenderNavigationFrame();
            }

            return;
        }

        if (_orbiting)
        {
            if (ControlMethod == EditorCameraControlMethod.Free) Camera.Look(dx, dy);
            else Camera.Orbit(dx, dy);
            ViewChanged?.Invoke();
            RenderNavigationFrame();
        }
        else if (_panning)
        {
            Camera.Pan(dx, dy);
            ViewChanged?.Invoke();
            RenderNavigationFrame();
        }
    }

    private void OnViewportMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button is MouseButtons.Middle or MouseButtons.Right or MouseButtons.Left)
        {
            _orbiting = false;
            _panning = false;
            _viewport.Capture = false;
        }
    }

    private void OnViewportMouseWheel(object? sender, MouseEventArgs e)
        => NavigateWheel(e.Location, e.Delta, ModifierKeys);

    public void NavigateWheel(Point location, int delta, Keys modifiers)
    {
        if (!NavigationEnabled)
        {
            return;
        }

        if (WheelInputHandler?.Invoke(location, delta, modifiers) == true) return;
        if (Mode2D)
        {
            Vector2 anchor = ControlToWorld2D(location);
            float factor = delta > 0 ? 1.15f : 1f / 1.15f;
            Zoom2D = Math.Clamp(Zoom2D * factor, 0.05f, 24f);
            Vector2 after = ControlToWorld2D(location);
            Camera2DX += anchor.X - after.X;
            Camera2DY += anchor.Y - after.Y;
            ViewChanged?.Invoke();
            RenderNavigationFrame();
            return;
        }

        if (ControlMethod == EditorCameraControlMethod.Free) Camera.Travel(delta / 120f);
        else Camera.Zoom(delta / 120f);
        ViewChanged?.Invoke();
        RenderNavigationFrame();
    }

    private void RenderNavigationFrame()
    {
        // WM_TIMER is lower priority than mouse input. Present while dragging instead of
        // waiting for the input queue to become idle at mouse release.
        long now = Environment.TickCount64;
        if (now - _lastNavigationFrame < 16 || !_viewport.IsHandleCreated) return;
        _lastNavigationFrame = now;
        _viewport.RenderFrame();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearSecondaryCamera();
        }

        base.Dispose(disposing);
    }
}
