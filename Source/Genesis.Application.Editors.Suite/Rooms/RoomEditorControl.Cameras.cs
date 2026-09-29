using System.Numerics;
using Genesis.Runtime.Scene;
using Genesis.Shared.Commands;
using Genesis.Runtime.Core;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private bool _extraCameraPreview;
    public IReadOnlyList<string> CameraListKeys => _sceneViewBox.Items.OfType<SceneViewChoice>().Select(choice => choice.NodeId).ToArray();

    public bool LookThroughCamera(string key)
    {
        RefreshSceneViews();
        for (int i = 0; i < _sceneViewBox.Items.Count; i++)
            if (_sceneViewBox.Items[i] is SceneViewChoice choice && choice.NodeId == key)
            {
                _sceneViewBox.SelectedIndex = i; ApplySceneView(); SyncWorkspaceSceneCameras(); return true;
            }
        return false;
    }

    private void AddExtraCameraChoices()
    {
        if (_viewport.HasSecondaryCamera && _viewport.SecondaryCamera?.Kind == EditorCameraSlotKind.PinnedSecondary)
            _sceneViewBox.Items.Add(new SceneViewChoice("@pinned", "Pinned editor view", CameraChoiceKind.Pinned));
        for (int i = 0; i < _room.Viewports.Count; i++)
        {
            bool live = _room.Dimension == RoomDimension.ThreeD ? Engine.TryGetCamera3DPose(i, out _, out _, out _, out _, out _, out _, out _)
                : Engine.TryGetCamera2DPose(i, out _, out _, out _, out _, out _, out _);
            _sceneViewBox.Items.Add(new SceneViewChoice("@view:" + i, $"View {i + 1}" + (live ? " · live ID " + i : "")
                + (_room.Viewports[i].Enabled ? "" : " (disabled)"), CameraChoiceKind.Viewport, i));
        }
        for (int i = _room.Viewports.Count; i < RoomAsset.MaxViewports; i++)
        {
            bool exists = _room.Dimension == RoomDimension.ThreeD
                ? Engine.TryGetCamera3DPose(i, out _, out _, out _, out _, out _, out _, out _)
                : Engine.TryGetCamera2DPose(i, out _, out _, out _, out _, out _, out _);
            if (exists) _sceneViewBox.Items.Add(new SceneViewChoice("@camera:" + i, "Live camera ID " + i, CameraChoiceKind.Registry, i));
        }
    }

    private bool ApplyExtraCameraView(SceneViewChoice choice)
    {
        if (!TryResolveExtraCamera(choice, _viewport, out EditorCameraOverride? camera, out Vector2 position, out float zoom)) return false;
        _cameraBookmark ??= new CameraBookmark(_viewport.Camera2DX, _viewport.Camera2DY, _viewport.Zoom2D,
            _viewport.Camera.Target, _viewport.Camera.Yaw, _viewport.Camera.Pitch, _viewport.Camera.Distance,
            _viewport.FieldOfViewDegrees, _viewport.NearPlane, _viewport.FarPlane);
        _extraCameraPreview = true; GameCameraPreviewState = null;
        _viewport.CameraOverrideFactory = camera.HasValue ? () => ResolveExtraOverride(choice, _viewport, camera.Value) : null;
        if (!camera.HasValue) { _viewport.Camera2DX = position.X; _viewport.Camera2DY = position.Y; _viewport.Zoom2D = zoom; }
        _viewport.NavigationEnabled = false; _viewport.Invalidate();
        UpdateStatus($"Looking through {choice.Label}. Choose Scene to return to editing.");
        return true;
    }

    private EditorCameraOverride ResolveExtraOverride(SceneViewChoice choice, EditorViewport3D surface, EditorCameraOverride fallback)
        => TryResolveExtraCamera(choice, surface, out EditorCameraOverride? camera, out _, out _) ? camera ?? fallback : fallback;

    private bool TryResolveExtraCamera(SceneViewChoice choice, EditorViewport3D surface,
        out EditorCameraOverride? camera, out Vector2 position, out float zoom)
    {
        camera = null; position = Vector2.Zero; zoom = 1;
        float aspect = surface.SurfaceWidth / (float)Math.Max(1, surface.SurfaceHeight);
        if (choice.Kind == CameraChoiceKind.Pinned)
        {
            EditorCameraSlot? slot = _viewport.SecondaryCamera;
            if (!_viewport.HasSecondaryCamera || slot?.Kind != EditorCameraSlotKind.PinnedSecondary) return false;
            if (_room.Dimension == RoomDimension.TwoD) { position = new(slot.Camera2DX, slot.Camera2DY); zoom = slot.Zoom2D; }
            else camera = new EditorCameraOverride(slot.Camera.View, Matrix4x4.CreatePerspectiveFieldOfView(slot.FieldOfViewDegrees * MathF.PI / 180, aspect, slot.NearPlane, slot.FarPlane), slot.Camera.Eye, slot.Camera.Forward);
            return true;
        }
        if (choice.Kind == CameraChoiceKind.Viewport)
        {
            if (choice.Slot < 0 || choice.Slot >= _room.Viewports.Count) return false;
            RoomViewport view = _room.Viewports[choice.Slot];
            if (_room.Dimension == RoomDimension.TwoD)
            {
                position = new(view.SourceX + view.SourceWidth * .5f, view.SourceY + view.SourceHeight * .5f);
                zoom = Math.Clamp(surface.SurfaceWidth / Math.Max(1, view.SourceWidth), .05f, 40); return true;
            }
            ResolveViewportFrustumPose(choice.Slot, view, out Vector3 eye, out Vector3 forward, out float fov, out float near, out float far, out _);
            camera = Override(eye, forward, fov, near, far); return true;
        }
        if (choice.Kind != CameraChoiceKind.Registry) return false;
        if (_room.Dimension == RoomDimension.TwoD)
        {
            if (!Engine.TryGetCamera2DPose(choice.Slot, out float x, out float y, out float left, out float right, out float top, out float bottom)) return false;
            position = new(x + (left + right) * .5f, y + (top + bottom) * .5f);
            zoom = Math.Clamp(surface.SurfaceWidth / Math.Max(1, right - left), .05f, 40); return true;
        }
        if (!Engine.TryGetCamera3DPose(choice.Slot, out Vector3 location, out float yaw, out float pitch, out float field, out _, out float nearZ, out float farZ)) return false;
        camera = Override(location, MathUtil.DirectionFromYawPitch(yaw * MathUtil.DegToRad, pitch * MathUtil.DegToRad), field, nearZ, farZ); return true;

        EditorCameraOverride Override(Vector3 eye, Vector3 forward, float fov, float near, float far)
            => new(MathUtil.LookAtLH(eye, eye + forward, Vector3.UnitY), MathUtil.PerspectiveFovLH(fov * MathF.PI / 180, aspect, near, far), eye, forward);
    }

    public void PreviewListedCamera()
    {
        if (_sceneViewBox.SelectedItem is not SceneViewChoice choice) return;
        if (choice.Kind is CameraChoiceKind.Editor or CameraChoiceKind.Pinned)
        {
            if (choice.Kind == CameraChoiceKind.Editor) _viewport.PinSecondaryFromCurrentView("Pinned editor view");
            RefreshSceneViews(); return;
        }
        if (choice.Kind == CameraChoiceKind.Authored) { ShowGameCameraInInset(); return; }
        if (choice.Kind == CameraChoiceKind.Viewport && !_room.Viewports[choice.Slot].Enabled)
        { UpdateStatus("Enable this View in Camera views before previewing its inset."); return; }
        EditorViewport3D surface = _viewport.SecondaryViewport ?? _viewport;
        if (!TryResolveExtraCamera(choice, surface, out EditorCameraOverride? camera, out Vector2 position, out float zoom)) return;
        if (camera.HasValue)
            _viewport.ShowAuthoredSecondaryCamera(() => ResolveExtraOverride(choice, _viewport.SecondaryViewport ?? _viewport, camera.Value), choice.Label);
        else
        {
            _viewport.ShowAuthoredSecondaryCamera2D(() =>
            {
                TryResolveExtraCamera(choice, _viewport.SecondaryViewport ?? _viewport, out _, out Vector2 current, out float currentZoom);
                return (current.X, current.Y, currentZoom);
            }, choice.Label);
        }
        RefreshSceneViews();
    }

    public void SelectListedCamera()
    {
        if (_sceneViewBox.SelectedItem is not SceneViewChoice choice) return;
        if (choice.Kind == CameraChoiceKind.Authored) { if (SceneViewNode is { } node) Select(node); }
        else if (choice.Kind == CameraChoiceKind.Viewport) { _navigation.SetSection(RoomNavSection.Views); SelectViewport(choice.Slot); }
        else UpdateStatus("This camera is a preview. Authored cameras select their Room Object; Views select their saved settings.");
    }
}
