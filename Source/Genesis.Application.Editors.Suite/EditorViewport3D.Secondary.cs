using System.Diagnostics;
using System.Numerics;

namespace Genesis.Application.Editors.Suite;

public sealed partial class EditorViewport3D
{
    private EditorSecondaryViewportHost? _secondaryHost;
    private EditorCameraSlot? _secondarySlot;
    private readonly Stopwatch _secondaryMotionClock = new();

    /// <summary>Optional world point for Look-at / follow / orbit-target commands.</summary>
    public Func<Vector3?>? SelectionWorldPoint { get; set; }

    public bool HasSecondaryCamera => _secondarySlot is not null && _secondaryHost is { Visible: true };

    public EditorCameraSlot? SecondaryCamera => _secondarySlot;

    public EditorViewport3D? SecondaryViewport => _secondaryHost?.Viewport;

    /// <summary>Creates or updates the top-right inset from the current editor orbit view.</summary>
    public void PinSecondaryFromCurrentView(string? label = null)
    {
        EditorCameraSlot slot = EnsureSecondarySlot(EditorCameraSlotKind.PinnedSecondary, label ?? "Pinned view");
        slot.CopyViewFrom(this);
        ShowSecondaryInset();
    }

    /// <summary>Keeps the existing inset, but snaps it to the current editor camera.</summary>
    public void MoveSecondaryToCurrentView()
    {
        if (_secondarySlot is null)
        {
            PinSecondaryFromCurrentView();
            return;
        }

        _secondarySlot.CopyViewFrom(this);
        _secondarySlot.Kind = EditorCameraSlotKind.PinnedSecondary;
        _secondaryHost?.SetCaption(_secondarySlot.Label);
        _secondaryHost?.Viewport.Host.Invalidate();
    }

    /// <summary>Shows an authored/game camera in the inset without replacing the editor orbit rig.</summary>
    public void ShowAuthoredSecondaryCamera(Func<EditorCameraOverride> overrideFactory, string label = "Game camera")
    {
        ArgumentNullException.ThrowIfNull(overrideFactory);
        EditorCameraSlot slot = EnsureSecondarySlot(EditorCameraSlotKind.AuthoredGame, label);
        slot.OverrideFactory = overrideFactory;
        slot.Motion = EditorCameraMotionMode.Pinned;
        ShowSecondaryInset();
    }

    public void LookSecondaryAtSelection()
    {
        if (SelectionWorldPoint?.Invoke() is not Vector3 point)
        {
            return;
        }

        if (_secondarySlot is null)
        {
            PinSecondaryFromCurrentView();
        }

        if (_secondarySlot is null)
        {
            return;
        }

        _secondarySlot.OrbitPoint = point;
        if (Mode2D)
        {
            _secondarySlot.Camera2DX = point.X;
            _secondarySlot.Camera2DY = point.Y;
            ApplySlotViewToSecondary();
        }
        else
        {
            _secondarySlot.Camera.LookAt(point);
        }

        _secondaryHost?.Viewport.Host.Invalidate();
    }

    public void SetPrimaryOrbitTargetFromSelection()
    {
        if (SelectionWorldPoint?.Invoke() is not Vector3 point)
        {
            return;
        }

        if (Mode2D)
        {
            Camera2DX = point.X;
            Camera2DY = point.Y;
        }
        else
        {
            Camera.LookAt(point);
        }

        Host.Invalidate();
    }

    public void SetSecondaryMotion(EditorCameraMotionMode motion)
    {
        if (_secondarySlot is null)
        {
            return;
        }

        _secondarySlot.Motion = motion;
        if (motion == EditorCameraMotionMode.OrbitPoint && SelectionWorldPoint?.Invoke() is Vector3 point)
        {
            _secondarySlot.OrbitPoint = point;
            if (Mode2D)
            {
                float dx = _secondarySlot.Camera2DX - point.X;
                float dy = _secondarySlot.Camera2DY - point.Y;
                _secondarySlot.OrbitRadius2D = MathF.Max(32f, MathF.Sqrt(dx * dx + dy * dy));
                _secondarySlot.OrbitAngle2D = MathF.Atan2(dy, dx);
            }
        }

        if (!_secondaryMotionClock.IsRunning)
        {
            _secondaryMotionClock.Restart();
        }
    }

    public void ClearSecondaryCamera()
    {
        if (_secondaryHost is not null)
        {
            _secondaryHost.Viewport.ViewChanged -= OnSecondaryViewChanged;
            _secondaryHost.CloseRequested -= OnSecondaryCloseRequested;
            Controls.Remove(_secondaryHost);
            _secondaryHost.Dispose();
            _secondaryHost = null;
        }

        _secondarySlot = null;
        _secondaryMotionClock.Reset();
    }

    private EditorCameraSlot EnsureSecondarySlot(EditorCameraSlotKind kind, string label)
    {
        _secondarySlot ??= new EditorCameraSlot();
        _secondarySlot.Kind = kind;
        _secondarySlot.Label = label;
        return _secondarySlot;
    }

    private void ShowSecondaryInset()
    {
        if (_secondaryHost is null)
        {
            _secondaryHost = new EditorSecondaryViewportHost();
            _secondaryHost.Viewport.ViewSettings = ViewSettings;
            _secondaryHost.CloseRequested += OnSecondaryCloseRequested;
            _secondaryHost.Viewport.SceneStateFactory = () =>
            {
                var state = SceneStateFactory?.Invoke() ?? EditorSceneLighting.Create(showFloor: true);
                _secondaryHost.Viewport.FloorStyle = FloorStyle;
                _secondaryHost.Viewport.FloorHeight = FloorHeight;
                return state;
            };
            _secondaryHost.Viewport.CameraOverrideFactory = BuildSecondaryOverride;
            _secondaryHost.Viewport.ViewChanged += OnSecondaryViewChanged;
            _secondaryHost.Viewport.DrawScene += renderer =>
            {
                ApplySecondaryMotion();
                DrawScene?.Invoke(renderer);
            };
            _secondaryHost.Viewport.DrawScene2D += renderer =>
            {
                ApplySecondaryMotion();
                DrawScene2D?.Invoke(renderer);
            };
            _secondaryHost.Viewport.Background2D = Background2D;
            Controls.Add(_secondaryHost);
            SizeChanged -= OnSecondaryHostResized;
            SizeChanged += OnSecondaryHostResized;
        }

        _secondaryHost.Viewport.Mode2D = Mode2D;
        bool authored = _secondarySlot?.Kind == EditorCameraSlotKind.AuthoredGame
            || _secondarySlot?.OverrideFactory is not null;
        _secondaryHost.Viewport.NavigationEnabled = !authored;
        _secondaryHost.Viewport.LookWithLeftButton = !authored;
        _secondaryHost.Viewport.FieldOfViewDegrees = _secondarySlot?.FieldOfViewDegrees ?? FieldOfViewDegrees;
        _secondaryHost.Viewport.NearPlane = _secondarySlot?.NearPlane ?? NearPlane;
        _secondaryHost.Viewport.FarPlane = _secondarySlot?.FarPlane ?? FarPlane;
        ApplySlotViewToSecondary();
        _secondaryHost.SetCaption(_secondarySlot?.Label ?? "Second camera");
        _secondaryHost.Visible = true;
        _secondaryHost.LayoutIn(this);
        if (!_secondaryMotionClock.IsRunning)
        {
            _secondaryMotionClock.Restart();
        }
    }

    private EditorCameraOverride BuildSecondaryOverride()
    {
        if (_secondarySlot?.OverrideFactory?.Invoke() is EditorCameraOverride authored)
        {
            return authored;
        }

        OrbitCameraRig camera = _secondarySlot?.Camera ?? Camera;
        float width = Math.Max(1, _secondaryHost?.Viewport.SurfaceWidth ?? 1);
        float height = Math.Max(1, _secondaryHost?.Viewport.SurfaceHeight ?? 1);
        return new EditorCameraOverride(
            camera.View,
            Matrix4x4.CreatePerspectiveFieldOfView(
                (_secondarySlot?.FieldOfViewDegrees ?? FieldOfViewDegrees) * MathF.PI / 180f,
                width / height,
                _secondarySlot?.NearPlane ?? NearPlane,
                _secondarySlot?.FarPlane ?? FarPlane),
            camera.Eye,
            camera.Forward);
    }

    private void ApplySecondaryMotion()
    {
        if (_secondarySlot is null)
        {
            return;
        }

        float dt = (float)_secondaryMotionClock.Elapsed.TotalSeconds;
        _secondaryMotionClock.Restart();
        dt = Math.Clamp(dt, 0f, 0.05f);

        if (Mode2D)
        {
            ApplySecondaryMotion2D(dt);
            ApplySlotViewToSecondary();
            return;
        }

        switch (_secondarySlot.Motion)
        {
            case EditorCameraMotionMode.FollowSelection:
                if (SelectionWorldPoint?.Invoke() is Vector3 follow)
                {
                    _secondarySlot.Camera.LookAt(follow);
                }

                break;
            case EditorCameraMotionMode.OrbitPoint:
                _secondarySlot.Camera.Target = _secondarySlot.OrbitPoint;
                _secondarySlot.Camera.Yaw += _secondarySlot.OrbitSpeedDegreesPerSecond * (MathF.PI / 180f) * dt;
                break;
        }

        ApplySlotViewToSecondary();
    }

    private void ApplySecondaryMotion2D(float dt)
    {
        if (_secondarySlot is null)
        {
            return;
        }

        switch (_secondarySlot.Motion)
        {
            case EditorCameraMotionMode.FollowSelection:
                if (SelectionWorldPoint?.Invoke() is Vector3 follow)
                {
                    _secondarySlot.Camera2DX = follow.X;
                    _secondarySlot.Camera2DY = follow.Y;
                    _secondarySlot.OrbitPoint = follow;
                }

                break;
            case EditorCameraMotionMode.OrbitPoint:
                _secondarySlot.OrbitAngle2D +=
                    _secondarySlot.OrbitSpeedDegreesPerSecond * (MathF.PI / 180f) * dt;
                _secondarySlot.Camera2DX = _secondarySlot.OrbitPoint.X
                    + MathF.Cos(_secondarySlot.OrbitAngle2D) * _secondarySlot.OrbitRadius2D;
                _secondarySlot.Camera2DY = _secondarySlot.OrbitPoint.Y
                    + MathF.Sin(_secondarySlot.OrbitAngle2D) * _secondarySlot.OrbitRadius2D;
                break;
        }
    }

    private void OnSecondaryViewChanged()
    {
        if (_secondarySlot is null || _secondaryHost is null || _secondarySlot.OverrideFactory is not null)
        {
            return;
        }

        EditorViewport3D feed = _secondaryHost.Viewport;
        _secondarySlot.Camera.CopyFrom(feed.Camera);
        _secondarySlot.Camera2DX = feed.Camera2DX;
        _secondarySlot.Camera2DY = feed.Camera2DY;
        _secondarySlot.Zoom2D = feed.Zoom2D;
        _secondarySlot.Motion = EditorCameraMotionMode.Pinned;
    }

    private void ApplySlotViewToSecondary()
    {
        if (_secondaryHost is null || _secondarySlot is null)
        {
            return;
        }

        EditorViewport3D feed = _secondaryHost.Viewport;
        feed.Camera.CopyFrom(_secondarySlot.Camera);
        feed.Camera2DX = _secondarySlot.Camera2DX;
        feed.Camera2DY = _secondarySlot.Camera2DY;
        feed.Zoom2D = _secondarySlot.Zoom2D;
        feed.FieldOfViewDegrees = _secondarySlot.FieldOfViewDegrees;
        feed.NearPlane = _secondarySlot.NearPlane;
        feed.FarPlane = _secondarySlot.FarPlane;
    }

    private void OnSecondaryHostResized(object? sender, EventArgs e) => _secondaryHost?.LayoutIn(this);

    private void OnSecondaryCloseRequested(object? sender, EventArgs e) => ClearSecondaryCamera();
}
