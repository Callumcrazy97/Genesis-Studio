using System;
using System.Numerics;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>Live PGSL control of the room's authored viewport slots and 3D play camera.</summary>
public static partial class PgslCommands
{
    private static int _activeView;

    private static RoomViewport View(double view) =>
        TryView(view, out RoomViewport viewport, out _) ? viewport : null;

    private static bool TryView(double view, out RoomViewport viewport, out int index)
    {
        index = (int)view;
        viewport = null;
        RoomAsset room = ActiveGameContext?.Room;
        if (room?.Viewports == null || index < 0 || index >= room.Viewports.Count) return false;
        viewport = room.Viewports[index];
        return viewport != null;
    }

    private static RoomRenderSubsystem ViewRenderer()
    {
        if (ActiveGameContext?.Scene == null) return null;
        foreach (var subsystem in ActiveGameContext.Scene.Subsystems)
            if (subsystem is RoomRenderSubsystem renderer) return renderer;
        return null;
    }

    private static Vector3 ViewPosition(int index, RoomViewport viewport) =>
        ViewRenderer()?.GetViewportPosition(index)
        ?? new Vector3(viewport.SourceX, viewport.SourceY, viewport.SourceZ);

    private static void SetViewPosition(int index, RoomViewport viewport, Vector3 position)
    {
        viewport.SourceX = position.X;
        viewport.SourceY = position.Y;
        viewport.SourceZ = position.Z;
        ViewRenderer()?.SetViewportPosition(index, position);
        if (ActiveGameContext?.Room?.Dimension == RoomDimension.ThreeD
            && index == _activeView
            && ActiveGameContext.Camera != null)
        {
            ActiveGameContext.Camera.Position = position;
        }
    }

    [PgslCommand("ViewGetCount", "ViewGetCount() -> number", "Number of viewport slots in the current room", "Views")]
    public static double ViewGetCount() => ActiveGameContext?.Room?.Viewports?.Count ?? 0;

    [PgslCommand("ViewGetActive", "ViewGetActive() -> number", "Currently active viewport slot", "Views")]
    public static double ViewGetActive() => _activeView;

    [PgslCommand("ViewSetActive", "ViewSetActive(view)", "Choose the viewport that drives 3D camera commands", "Views")]
    public static void ViewSetActive(double view)
    {
        if (!TryView(view, out RoomViewport viewport, out int index)) return;
        _activeView = index;
        viewport.Enabled = true;
        if (ActiveGameContext?.Room?.Dimension == RoomDimension.ThreeD && ActiveGameContext.Camera != null)
            ActiveGameContext.Camera.Position = ViewPosition(index, viewport);
    }

    /// <summary>
    /// CAM-1: map <c>Engine.Camera3D*</c> ids onto Room viewport slots so PGSL View* and the
    /// indexed Engine registry share one schema. No-ops when no room is bound (headless binding tests).
    /// </summary>
    public static void SyncEngineCamera3DToViewport(
        int id,
        Vector3 position,
        float fovDegrees,
        float nearZ,
        float farZ,
        bool activate)
    {
        if (!TryView(id, out RoomViewport viewport, out int index)) return;
        viewport.Enabled = true;
        viewport.SourceX = position.X;
        viewport.SourceY = position.Y;
        viewport.SourceZ = position.Z;
        viewport.FieldOfView = Math.Clamp(fovDegrees, 1f, 179f);
        viewport.FrustumNear = Math.Max(0.001f, nearZ);
        viewport.FrustumFar = Math.Max(viewport.FrustumNear + 0.001f, farZ);
        ViewRenderer()?.SetViewportPosition(index, position);
        if (activate) _activeView = index;
    }

    /// <summary>
    /// CAM-1: map <c>Engine.Camera2D*</c> ids onto Room viewport slots (source centre + bounds).
    /// </summary>
    public static void SyncEngineCamera2DToViewport(
        int id,
        float x,
        float y,
        float left,
        float right,
        float top,
        float bottom,
        bool activate)
    {
        if (!TryView(id, out RoomViewport viewport, out int index)) return;
        viewport.Enabled = true;
        viewport.SourceX = x;
        viewport.SourceY = y;
        float width = Math.Abs(right - left);
        float height = Math.Abs(bottom - top);
        if (width > 0.001f) viewport.SourceWidth = width;
        if (height > 0.001f) viewport.SourceHeight = height;
        ViewRenderer()?.SetViewportPosition(index, new Vector3(x, y, viewport.SourceZ));
        if (activate) _activeView = index;
    }

    [PgslCommand("ViewGetEnabled", "ViewGetEnabled(view) -> bool", "Whether a viewport is rendered", "Views")]
    public static bool ViewGetEnabled(double view) => View(view)?.Enabled ?? false;

    [PgslCommand("ViewSetEnabled", "ViewSetEnabled(view, enabled)", "Enable or disable a viewport", "Views")]
    public static void ViewSetEnabled(double view, bool enabled)
    {
        RoomViewport viewport = View(view);
        if (viewport != null) viewport.Enabled = enabled;
    }

    [PgslCommand("ViewGetPosX", "ViewGetPosX(view) -> number", "Live viewport source X", "Views")]
    public static double ViewGetPosX(double view) => TryView(view, out RoomViewport v, out int i) ? ViewPosition(i, v).X : 0;
    [PgslCommand("ViewGetPosY", "ViewGetPosY(view) -> number", "Live viewport source Y", "Views")]
    public static double ViewGetPosY(double view) => TryView(view, out RoomViewport v, out int i) ? ViewPosition(i, v).Y : 0;
    [PgslCommand("ViewGetPosZ", "ViewGetPosZ(view) -> number", "Live viewport source Z", "Views")]
    public static double ViewGetPosZ(double view) => TryView(view, out RoomViewport v, out int i) ? ViewPosition(i, v).Z : 0;

    [PgslCommand("ViewSetPosX", "ViewSetPosX(view, x)", "Set live viewport source X", "Views")]
    public static void ViewSetPosX(double view, double x)
    {
        if (!TryView(view, out RoomViewport v, out int i)) return;
        Vector3 p = ViewPosition(i, v); p.X = (float)x; SetViewPosition(i, v, p);
    }
    [PgslCommand("ViewSetPosY", "ViewSetPosY(view, y)", "Set live viewport source Y", "Views")]
    public static void ViewSetPosY(double view, double y)
    {
        if (!TryView(view, out RoomViewport v, out int i)) return;
        Vector3 p = ViewPosition(i, v); p.Y = (float)y; SetViewPosition(i, v, p);
    }
    [PgslCommand("ViewSetPosZ", "ViewSetPosZ(view, z)", "Set live viewport source Z", "Views")]
    public static void ViewSetPosZ(double view, double z)
    {
        if (!TryView(view, out RoomViewport v, out int i)) return;
        Vector3 p = ViewPosition(i, v); p.Z = (float)z; SetViewPosition(i, v, p);
    }

    [PgslCommand("ViewGetBoundsW", "ViewGetBoundsW(view) -> number", "Visible world width", "Views")]
    public static double ViewGetBoundsW(double view) => View(view)?.SourceWidth ?? 0;
    [PgslCommand("ViewGetBoundsH", "ViewGetBoundsH(view) -> number", "Visible world height", "Views")]
    public static double ViewGetBoundsH(double view) => View(view)?.SourceHeight ?? 0;
    [PgslCommand("ViewSetBoundsW", "ViewSetBoundsW(view, width)", "Set visible world width", "Views")]
    public static void ViewSetBoundsW(double view, double width) { RoomViewport v = View(view); if (v != null) v.SourceWidth = Math.Max(1f, (float)width); }
    [PgslCommand("ViewSetBoundsH", "ViewSetBoundsH(view, height)", "Set visible world height", "Views")]
    public static void ViewSetBoundsH(double view, double height) { RoomViewport v = View(view); if (v != null) v.SourceHeight = Math.Max(1f, (float)height); }
    [PgslCommand("ViewSetBounds", "ViewSetBounds(view, width, height)", "Set visible world size", "Views")]
    public static void ViewSetBounds(double view, double width, double height)
    {
        ViewSetBoundsW(view, width); ViewSetBoundsH(view, height);
    }

    [PgslCommand("ViewGetViewportX", "ViewGetViewportX(view) -> number", "Viewport left edge", "Views")]
    public static double ViewGetViewportX(double view) => View(view)?.PortX ?? 0;
    [PgslCommand("ViewGetViewportY", "ViewGetViewportY(view) -> number", "Viewport top edge", "Views")]
    public static double ViewGetViewportY(double view) => View(view)?.PortY ?? 0;
    [PgslCommand("ViewGetViewportW", "ViewGetViewportW(view) -> number", "Viewport width in pixels", "Views")]
    public static double ViewGetViewportW(double view) => View(view)?.PortWidth ?? 0;
    [PgslCommand("ViewGetViewportH", "ViewGetViewportH(view) -> number", "Viewport height in pixels", "Views")]
    public static double ViewGetViewportH(double view) => View(view)?.PortHeight ?? 0;
    [PgslCommand("ViewSetViewportX", "ViewSetViewportX(view, x)", "Set viewport left edge", "Views")]
    public static void ViewSetViewportX(double view, double x) { RoomViewport v = View(view); if (v != null) v.PortX = (int)x; }
    [PgslCommand("ViewSetViewportY", "ViewSetViewportY(view, y)", "Set viewport top edge", "Views")]
    public static void ViewSetViewportY(double view, double y) { RoomViewport v = View(view); if (v != null) v.PortY = (int)y; }
    [PgslCommand("ViewSetViewportW", "ViewSetViewportW(view, width)", "Set viewport width", "Views")]
    public static void ViewSetViewportW(double view, double width) { RoomViewport v = View(view); if (v != null) v.PortWidth = Math.Max(1, (int)width); }
    [PgslCommand("ViewSetViewportH", "ViewSetViewportH(view, height)", "Set viewport height", "Views")]
    public static void ViewSetViewportH(double view, double height) { RoomViewport v = View(view); if (v != null) v.PortHeight = Math.Max(1, (int)height); }
    [PgslCommand("ViewSetViewport", "ViewSetViewport(view, x, y, width, height)", "Set viewport destination rectangle", "Views")]
    public static void ViewSetViewport(double view, double x, double y, double width, double height)
    {
        ViewSetViewportX(view, x); ViewSetViewportY(view, y); ViewSetViewportW(view, width); ViewSetViewportH(view, height);
    }

    [PgslCommand("ViewGetFollow", "ViewGetFollow(view) -> string", "Object name followed by a viewport", "Views")]
    public static string ViewGetFollow(double view) => View(view)?.FollowTarget ?? string.Empty;
    [PgslCommand("ViewSetFollow", "ViewSetFollow(view, objectName)", "Set the object a viewport follows", "Views")]
    public static void ViewSetFollow(double view, string objectName) { RoomViewport v = View(view); if (v != null) v.FollowTarget = objectName?.Trim() ?? string.Empty; }
    [PgslCommand("ViewGetFollowBorderH", "ViewGetFollowBorderH(view) -> number", "Horizontal follow dead-zone", "Views")]
    public static double ViewGetFollowBorderH(double view) => View(view)?.FollowMarginX ?? 0;
    [PgslCommand("ViewGetFollowBorderV", "ViewGetFollowBorderV(view) -> number", "Vertical follow dead-zone", "Views")]
    public static double ViewGetFollowBorderV(double view) => View(view)?.FollowMarginY ?? 0;
    [PgslCommand("ViewSetFollowBorder", "ViewSetFollowBorder(view, horizontal, vertical)", "Set follow dead-zone", "Views")]
    public static void ViewSetFollowBorder(double view, double horizontal, double vertical)
    {
        RoomViewport v = View(view); if (v == null) return;
        v.FollowMarginX = Math.Max(0f, (float)horizontal); v.FollowMarginY = Math.Max(0f, (float)vertical);
    }
    [PgslCommand("ViewGetFollowSpeedH", "ViewGetFollowSpeedH(view) -> number", "Horizontal follow speed", "Views")]
    public static double ViewGetFollowSpeedH(double view) => View(view)?.FollowSpeedX ?? 0;
    [PgslCommand("ViewGetFollowSpeedV", "ViewGetFollowSpeedV(view) -> number", "Vertical follow speed", "Views")]
    public static double ViewGetFollowSpeedV(double view) => View(view)?.FollowSpeedY ?? 0;
    [PgslCommand("ViewSetFollowSpeed", "ViewSetFollowSpeed(view, horizontal, vertical)", "Set follow speed; -1 snaps", "Views")]
    public static void ViewSetFollowSpeed(double view, double horizontal, double vertical)
    {
        RoomViewport v = View(view); if (v == null) return;
        v.FollowSpeedX = (float)horizontal; v.FollowSpeedY = (float)vertical;
    }

    [PgslCommand("ViewSetZoom", "ViewSetZoom(view, zoom)", "Set 2D zoom while preserving viewport centre", "Views")]
    public static void ViewSetZoom(double view, double zoom)
    {
        if (!TryView(view, out RoomViewport v, out int i)) return;
        float z = Math.Max(0.01f, (float)zoom);
        Vector3 p = ViewPosition(i, v);
        float cx = p.X + v.SourceWidth * 0.5f, cy = p.Y + v.SourceHeight * 0.5f;
        v.SourceWidth = Math.Max(1f, v.PortWidth / z);
        v.SourceHeight = Math.Max(1f, v.PortHeight / z);
        SetViewPosition(i, v, new Vector3(cx - v.SourceWidth * 0.5f, cy - v.SourceHeight * 0.5f, p.Z));
    }
    [PgslCommand("ViewGetZoom", "ViewGetZoom(view) -> number", "Current 2D zoom", "Views")]
    public static double ViewGetZoom(double view)
    {
        RoomViewport v = View(view); return v == null || v.SourceWidth <= 0f ? 1 : v.PortWidth / v.SourceWidth;
    }
    [PgslCommand("ViewShake", "ViewShake(view, magnitude, durationSeconds)", "Apply a decaying camera shake", "Views")]
    public static void ViewShake(double view, double magnitude, double durationSeconds)
    {
        if (!TryView(view, out _, out int index)) return;
        ViewRenderer()?.StartViewportShake(index, Math.Max(0f, (float)magnitude), Math.Max(0f, (float)durationSeconds));
    }

    [PgslCommand("ViewGetFov", "ViewGetFov(view) -> number", "3D camera field of view in degrees", "Views")]
    public static double ViewGetFov(double view) => (ActiveGameContext?.Camera?.FieldOfView ?? (MathF.PI / 3f)) * 180.0 / Math.PI;
    [PgslCommand("ViewSetFov", "ViewSetFov(view, degrees)", "Set 3D camera field of view", "Views")]
    public static void ViewSetFov(double view, double degrees)
    {
        if (View(view) != null && ActiveGameContext?.Camera != null)
            ActiveGameContext.Camera.FieldOfView = (float)(Math.Clamp(degrees, 1, 179) * Math.PI / 180.0);
    }

    [PgslCommand("ViewGetFrustumNear", "ViewGetFrustumNear(view) -> number", "3D camera near clipping plane", "Views")]
    public static double ViewGetFrustumNear(double view) => ActiveGameContext?.Camera?.NearPlane ?? 0.1f;
    [PgslCommand("ViewSetFrustumNear", "ViewSetFrustumNear(view, distance)", "Set 3D camera near clipping plane", "Views")]
    public static void ViewSetFrustumNear(double view, double distance)
    {
        if (View(view) != null && ActiveGameContext?.Camera != null)
            ActiveGameContext.Camera.NearPlane = (float)Math.Max(0.01, distance);
    }

    [PgslCommand("ViewGetFrustumFar", "ViewGetFrustumFar(view) -> number", "3D camera far clipping plane", "Views")]
    public static double ViewGetFrustumFar(double view) => ActiveGameContext?.Camera?.FarPlane ?? 2000f;
    [PgslCommand("ViewSetFrustumFar", "ViewSetFrustumFar(view, distance)", "Set 3D camera far clipping plane", "Views")]
    public static void ViewSetFrustumFar(double view, double distance)
    {
        if (View(view) != null && ActiveGameContext?.Camera != null)
            ActiveGameContext.Camera.FarPlane = (float)Math.Max(1.0, distance);
    }
    [PgslCommand("ViewGetPitch", "ViewGetPitch(view) -> number", "3D camera pitch", "Views")]
    public static double ViewGetPitch(double view) => ActiveGameContext?.Camera?.Pitch ?? 0;
    [PgslCommand("ViewSetPitch", "ViewSetPitch(view, degrees)", "Set 3D camera pitch", "Views")]
    public static void ViewSetPitch(double view, double degrees) { if (View(view) != null && ActiveGameContext?.Camera != null) ActiveGameContext.Camera.Pitch = (float)Math.Clamp(degrees, -89.9, 89.9); }
    [PgslCommand("ViewGetYaw", "ViewGetYaw(view) -> number", "3D camera yaw", "Views")]
    public static double ViewGetYaw(double view) => ActiveGameContext?.Camera?.Yaw ?? 0;
    [PgslCommand("ViewSetYaw", "ViewSetYaw(view, degrees)", "Set 3D camera yaw", "Views")]
    public static void ViewSetYaw(double view, double degrees) { if (View(view) != null && ActiveGameContext?.Camera != null) ActiveGameContext.Camera.Yaw = (float)degrees; }

    [PgslCommand("ViewMoveForward", "ViewMoveForward(view, distance)", "Move a 3D view along camera forward", "Views")]
    public static void ViewMoveForward(double view, double distance) => MoveView(view, ActiveGameContext?.Camera?.Forward ?? Vector3.UnitZ, distance);
    [PgslCommand("ViewMoveRight", "ViewMoveRight(view, distance)", "Move a 3D view along camera right", "Views")]
    public static void ViewMoveRight(double view, double distance) => MoveView(view, ActiveGameContext?.Camera?.Right ?? Vector3.UnitX, distance);
    [PgslCommand("ViewMoveUp", "ViewMoveUp(view, distance)", "Move a view along world up", "Views")]
    public static void ViewMoveUp(double view, double distance) => MoveView(view, Vector3.UnitY, distance);
    [PgslCommand("CameraMoveForward", "CameraMoveForward(distance)", "Move the active view forward", "Views")]
    public static void CameraMoveForward(double distance) => ViewMoveForward(_activeView, distance);
    [PgslCommand("CameraMoveRight", "CameraMoveRight(distance)", "Move the active view right", "Views")]
    public static void CameraMoveRight(double distance) => ViewMoveRight(_activeView, distance);
    [PgslCommand("CameraMoveUp", "CameraMoveUp(distance)", "Move the active view up", "Views")]
    public static void CameraMoveUp(double distance) => ViewMoveUp(_activeView, distance);

    private static void MoveView(double view, Vector3 direction, double distance)
    {
        if (!TryView(view, out RoomViewport v, out int i)) return;
        Vector3 p = ViewPosition(i, v) + direction * (float)distance;
        SetViewPosition(i, v, p);
    }

    [PgslCommand("ViewReset", "ViewReset(view)", "Reset live position to the room's authored source", "Views")]
    public static void ViewReset(double view)
    {
        if (!TryView(view, out RoomViewport v, out int i)) return;
        ViewRenderer()?.SetViewportPosition(i, new Vector3(v.SourceX, v.SourceY, v.SourceZ));
    }

    [PgslCommand("ViewGet", "ViewGet(view, property) -> number", "Get a numeric viewport property by name", "Views")]
    public static double ViewGet(double view, string property) => (property ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "posx" => ViewGetPosX(view), "posy" => ViewGetPosY(view), "posz" => ViewGetPosZ(view),
        "boundsw" => ViewGetBoundsW(view), "boundsh" => ViewGetBoundsH(view),
        "viewportx" => ViewGetViewportX(view), "viewporty" => ViewGetViewportY(view),
        "viewportw" => ViewGetViewportW(view), "viewporth" => ViewGetViewportH(view),
        "zoom" => ViewGetZoom(view), "enabled" => ViewGetEnabled(view) ? 1 : 0,
        "fov" => ViewGetFov(view), "pitch" => ViewGetPitch(view), "yaw" => ViewGetYaw(view), _ => 0,
    };

    [PgslCommand("ViewSet", "ViewSet(view, property, value)", "Set a numeric viewport property by name", "Views")]
    public static void ViewSet(double view, string property, double value)
    {
        switch ((property ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "posx": ViewSetPosX(view, value); break; case "posy": ViewSetPosY(view, value); break; case "posz": ViewSetPosZ(view, value); break;
            case "boundsw": ViewSetBoundsW(view, value); break; case "boundsh": ViewSetBoundsH(view, value); break;
            case "viewportx": ViewSetViewportX(view, value); break; case "viewporty": ViewSetViewportY(view, value); break;
            case "viewportw": ViewSetViewportW(view, value); break; case "viewporth": ViewSetViewportH(view, value); break;
            case "zoom": ViewSetZoom(view, value); break; case "enabled": ViewSetEnabled(view, value != 0); break;
            case "fov": ViewSetFov(view, value); break; case "pitch": ViewSetPitch(view, value); break; case "yaw": ViewSetYaw(view, value); break;
        }
    }
}
