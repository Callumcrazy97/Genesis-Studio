using System.Drawing;
using System.Numerics;
using Genesis.Rendering.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Renders HUD overlays on top of the Room Viewport:
/// top-left room info, dashed room boundary, ghost preview + tooltip,
/// selection bounding box, and view bounds.
/// </summary>
public sealed class RoomViewportOverlay
{
    private readonly RoomEditorControl _editor;

    public RoomViewportOverlay(RoomEditorControl editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    public void Draw2D(IRenderController renderer, EditorViewport3D viewport)
    {
        RoomAsset room = _editor.Room;
        float roomW = room.Settings.Width;
        float roomH = room.Settings.Height;
        float zoom = MathF.Max(0.0001f, viewport.Zoom2D);

        // 1. Outside Void Shading
        if (_editor.ShowRoomBounds)
        {
            RenderColor voidColor = new(0f, 0f, 0f, 0.45f);
            float ext = 20000f;
            renderer.DrawRect(-ext, -ext, ext * 2f, ext, voidColor, filled: true, depth: 9500); // top
            renderer.DrawRect(-ext, roomH, ext * 2f, ext, voidColor, filled: true, depth: 9500); // bottom
            renderer.DrawRect(-ext, 0f, ext, roomH, voidColor, filled: true, depth: 9500); // left
            renderer.DrawRect(roomW, 0f, ext, roomH, voidColor, filled: true, depth: 9500); // right

            // 2. Dashed Blue Room Boundary
            RenderColor boundsColor = new(
                _editor.RoomBoundsColor.R / 255f,
                _editor.RoomBoundsColor.G / 255f,
                _editor.RoomBoundsColor.B / 255f,
                0.95f);
            float thick = 2f / zoom;
            DrawDashedLine(renderer, new Vector2(0f, 0f), new Vector2(roomW, 0f), boundsColor, thick, zoom);
            DrawDashedLine(renderer, new Vector2(roomW, 0f), new Vector2(roomW, roomH), boundsColor, thick, zoom);
            DrawDashedLine(renderer, new Vector2(roomW, roomH), new Vector2(0f, roomH), boundsColor, thick, zoom);
            DrawDashedLine(renderer, new Vector2(0f, roomH), new Vector2(0f, 0f), boundsColor, thick, zoom);
        }

        // 3. Runtime Viewport Bounds (if enabled)
        if (room.UsesViewports)
        {
            RenderColor viewCamColor = new(0.35f, 0.85f, 1f, 0.8f);
            foreach (RoomViewport vp in room.Viewports)
            {
                if (vp.Enabled)
                {
                    renderer.DrawRect(vp.SourceX, vp.SourceY, vp.SourceWidth, vp.SourceHeight, viewCamColor, filled: false, depth: 80);
                }
            }
        }
    }

    public void DrawOverlay(IRenderController renderer, EditorViewport3D viewport)
    {
        RoomAsset room = _editor.Room;
        float zoom = MathF.Max(0.0001f, viewport.Zoom2D);

        // 1. Top-Left HUD Info Box
        DrawTopLeftHud(renderer, room);

        // 2. Ghost Preview + Tooltip
        RoomPlacementController placement = _editor.Placement;
        if (viewport.Mode2D && placement.IsArmed && placement.GhostWorld is Vector2 ghostPos)
        {
            DrawGhostTooltip(renderer, viewport, placement, ghostPos, room);
        }
    }

    private void DrawTopLeftHud(IRenderController renderer, RoomAsset room)
    {
        // HUD container in swapchain space (top-left: 16, 16)
        float x = 16f;
        float y = 16f;
        float w = 276f;
        float h = 50f;

        renderer.DrawRect(x, y, w, h, new RenderColor(0.08f, 0.10f, 0.14f, 0.82f), filled: true, depth: -9500);
        renderer.DrawRect(x, y, w, h, new RenderColor(0.22f, 0.26f, 0.35f, 0.90f), filled: false, depth: -9501);
        renderer.DrawText($"{room.Name} · {room.Nodes.Count} instances", x + 10, y + 8, 12, new RenderColor(.88f, .91f, .96f, 1));
        string selection = _editor.SelectedNodes.Count == 1 ? _editor.SelectedNodes[0].Name : _editor.SelectedNodes.Count > 1 ? $"{_editor.SelectedNodes.Count} selected" : "No selection";
        renderer.DrawText($"{renderer.BackendName} · {selection}", x + 10, y + 29, 11, new RenderColor(.63f, .69f, .79f, 1));
    }

    private static void DrawGhostTooltip(
        IRenderController renderer,
        EditorViewport3D viewport,
        RoomPlacementController placement,
        Vector2 worldPos,
        RoomAsset room)
    {
        Vector2 surface = viewport.World2DToSurface(worldPos);
        float tx = surface.X + 16f;
        float ty = surface.Y + 16f;
        float tw = 120f;
        float th = 40f;

        // Tooltip dark card
        renderer.DrawRect(tx, ty, tw, th, new RenderColor(0.06f, 0.08f, 0.12f, 0.88f), filled: true, depth: -9600);
        renderer.DrawRect(tx, ty, tw, th, new RenderColor(0.35f, 0.55f, 0.95f, 0.85f), filled: false, depth: -9601);
    }

    private static void DrawDashedLine(
        IRenderController renderer,
        Vector2 start,
        Vector2 end,
        RenderColor color,
        float thickness,
        float zoom)
    {
        Vector2 diff = end - start;
        float len = diff.Length();
        if (len <= 0.001f) return;
        Vector2 dir = diff / len;

        float dashLen = 12f / zoom;
        float gapLen = 8f / zoom;
        float step = dashLen + gapLen;

        for (float d = 0f; d < len; d += step)
        {
            float segEnd = MathF.Min(d + dashLen, len);
            Vector2 p1 = start + dir * d;
            Vector2 p2 = start + dir * segEnd;
            renderer.DrawLine(p1.X, p1.Y, p2.X, p2.Y, color, thickness, depth: 90);
        }
    }
}
