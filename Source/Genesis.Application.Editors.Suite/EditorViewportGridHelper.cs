using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Shared editor grid drawing for 3D viewports. Projects world-space lines on the XZ plane (with
/// optional vertical risers) to screen-space overlay lines, matching the Room/Model/Terrain editors.
/// </summary>
internal static class EditorViewportGridHelper
{
    /// <summary>
    /// Draws a 3D grid centred on the origin: horizontal lines on Y=0 and vertical lines at each
    /// intersection up to <paramref name="verticalExtent"/>.
    /// </summary>
    public static void DrawGrid3D(
        EditorViewport3D viewport,
        IRenderController renderer,
        float cellSize,
        ReadOnlySpan<float> rgba,
        float horizontalExtent,
        float verticalExtent,
        float lineThickness = 1f)
    {
        if (cellSize < 0.01f || rgba.Length < 4) return;

        RenderColor color = new(rgba[0], rgba[1], rgba[2], rgba[3]);
        float half = MathF.Max(cellSize, horizontalExtent);
        int lines = Math.Clamp((int)MathF.Ceiling(half / cellSize), 1, 128);
        float span = lines * cellSize;

        for (int i = -lines; i <= lines; i++)
        {
            float offset = i * cellSize;
            DrawWorldLine(viewport, renderer, new Vector3(-span, 0f, offset), new Vector3(span, 0f, offset), color, lineThickness);
            DrawWorldLine(viewport, renderer, new Vector3(offset, 0f, -span), new Vector3(offset, 0f, span), color, lineThickness);

            if (verticalExtent <= 0f) continue;

            DrawWorldLine(
                viewport,
                renderer,
                new Vector3(offset, 0f, 0f),
                new Vector3(offset, verticalExtent, 0f),
                color,
                lineThickness * 0.85f);
            DrawWorldLine(
                viewport,
                renderer,
                new Vector3(0f, 0f, offset),
                new Vector3(0f, verticalExtent, offset),
                color,
                lineThickness * 0.85f);
        }
    }

    private static void DrawWorldLine(
        EditorViewport3D viewport,
        IRenderController renderer,
        Vector3 a,
        Vector3 b,
        RenderColor color,
        float thickness)
    {
        Vector3 sa = viewport.WorldToSurface(a);
        Vector3 sb = viewport.WorldToSurface(b);
        if (sa.Z is <= 0f or >= 1f || sb.Z is <= 0f or >= 1f) return;
        renderer.DrawLine(sa.X, sa.Y, sb.X, sb.Y, color, thickness, depth: -9100);
    }
}
