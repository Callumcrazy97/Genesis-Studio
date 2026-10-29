using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;

namespace Genesis.Runtime.Debugger;

/// <summary>
/// Where the debug screen's drawings over the world go: the 3D camera's view and projection, or
/// the views a 2D room's last frame drew (window rectangle, offset and zoom each).
/// </summary>
internal readonly struct DebugProjection
{
    private const float NearW = 0.01f;

    public readonly Matrix4x4 ViewProjection;
    public readonly int Width, Height;
    public readonly bool Has3D;
    public readonly IReadOnlyList<RoomRenderSubsystem.View2D> Views2D;
    /// <summary>The 3D camera's position.</summary>
    public readonly Vector3 Eye;

    private DebugProjection(Matrix4x4 viewProjection, int width, int height, bool has3D, IReadOnlyList<RoomRenderSubsystem.View2D> views2D, Vector3 eye)
    {
        ViewProjection = viewProjection;
        Width = width;
        Height = height;
        Has3D = has3D;
        Views2D = views2D;
        Eye = eye;
    }

    /// <summary>True when the scene is a 2D room that drew at least one view.</summary>
    public bool Is2D => Views2D is { Count: > 0 };

    public static DebugProjection For(RuntimeScene scene, int width, int height)
    {
        IReadOnlyList<RoomRenderSubsystem.View2D> views = null;
        if (scene != null)
            foreach (ISceneSubsystem subsystem in scene.Subsystems)
                if (subsystem is RoomRenderSubsystem { IsTwoD: true } room) { views = room.Views2D; break; }
        Camera3D camera = scene?.Camera3D;
        return new DebugProjection(camera?.ViewProjection ?? Matrix4x4.Identity, width, height, camera != null && views == null, views,
            camera?.Position ?? Vector3.Zero);
    }

    /// <summary>A point of the 3D world on the screen; false behind the camera.</summary>
    public bool TryPoint(Vector3 world, out Vector2 screen)
    {
        screen = default;
        if (!Has3D) return false;
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), ViewProjection);
        if (clip.W < NearW) return false;
        screen = ToScreen(clip);
        return true;
    }

    /// <summary>
    /// A segment of the 3D world on the screen, cut where it passes behind the camera; false when
    /// none of it is in front, or all of it is off one side of the screen.
    /// </summary>
    public bool TrySegment(Vector3 a, Vector3 b, out Vector2 screenA, out Vector2 screenB)
    {
        screenA = screenB = default;
        if (!Has3D) return false;
        Vector4 ca = Vector4.Transform(new Vector4(a, 1f), ViewProjection);
        Vector4 cb = Vector4.Transform(new Vector4(b, 1f), ViewProjection);
        if (ca.W < NearW && cb.W < NearW) return false;
        if (ca.W < NearW) ca = Vector4.Lerp(ca, cb, (NearW - ca.W) / (cb.W - ca.W));
        else if (cb.W < NearW) cb = Vector4.Lerp(cb, ca, (NearW - cb.W) / (ca.W - cb.W));
        // Wholly off one side of the view: nothing to draw.
        if ((ca.X > ca.W && cb.X > cb.W) || (ca.X < -ca.W && cb.X < -cb.W)
            || (ca.Y > ca.W && cb.Y > cb.W) || (ca.Y < -ca.W && cb.Y < -cb.W)) return false;
        screenA = ToScreen(ca);
        screenB = ToScreen(cb);
        return true;
    }

    private Vector2 ToScreen(Vector4 clip)
    {
        float x = clip.X / clip.W, y = clip.Y / clip.W;
        // Far off-screen points are pulled in so a canvas is never handed a huge coordinate.
        x = Math.Clamp(x, -64f, 64f);
        y = Math.Clamp(y, -64f, 64f);
        return new Vector2((x * 0.5f + 0.5f) * Width, (1f - (y * 0.5f + 0.5f)) * Height);
    }

    /// <summary>A point of a 2D room (pixels) on the screen in one of its views.</summary>
    public static Vector2 Map(in RoomRenderSubsystem.View2D view, float x, float y) =>
        new(view.Port.X + view.OffsetX + x * view.Zoom, view.Port.Y + view.OffsetY + y * view.Zoom);

    /// <summary>The view under a window point, or the first view.</summary>
    public int ViewAt(Vector2 screen)
    {
        if (!Is2D) return -1;
        for (int index = 0; index < Views2D.Count; index++)
            if (Views2D[index].Port.Contains((int)screen.X, (int)screen.Y)) return index;
        return 0;
    }

    /// <summary>A window point as a 2D room's pixels, through the view under it.</summary>
    public bool TryRoomPoint(Vector2 screen, out Vector2 room)
    {
        room = default;
        int index = ViewAt(screen);
        if (index < 0) return false;
        RoomRenderSubsystem.View2D view = Views2D[index];
        float zoom = Math.Abs(view.Zoom) < 1e-5f ? 1f : view.Zoom;
        room = new Vector2((screen.X - view.Port.X - view.OffsetX) / zoom, (screen.Y - view.Port.Y - view.OffsetY) / zoom);
        return true;
    }

    /// <summary>The 3D ray under a window point, from the camera.</summary>
    public bool TryRay(Vector2 screen, out Vector3 origin, out Vector3 direction)
    {
        origin = direction = default;
        if (!Has3D || !Matrix4x4.Invert(ViewProjection, out Matrix4x4 inverse)) return false;
        float x = screen.X / Math.Max(1, Width) * 2f - 1f;
        float y = 1f - screen.Y / Math.Max(1, Height) * 2f;
        // Both depth conventions: whichever end is nearer is the ray's start.
        Vector4 a = Vector4.Transform(new Vector4(x, y, 0f, 1f), inverse);
        Vector4 b = Vector4.Transform(new Vector4(x, y, 1f, 1f), inverse);
        if (Math.Abs(a.W) < 1e-8f || Math.Abs(b.W) < 1e-8f) return false;
        Vector3 pa = new Vector3(a.X, a.Y, a.Z) / a.W, pb = new Vector3(b.X, b.Y, b.Z) / b.W;
        bool aNear = Vector3.DistanceSquared(pa, Eye) <= Vector3.DistanceSquared(pb, Eye);
        origin = aNear ? pa : pb;
        Vector3 along = (aNear ? pb : pa) - origin;
        if (along.LengthSquared() < 1e-12f) return false;
        direction = Vector3.Normalize(along);
        return true;
    }
}
