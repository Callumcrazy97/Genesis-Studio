using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

/// <summary>Viewport HUD chips and RGB triad shared by 3D editors.</summary>
internal static class EditorViewportHudOverlay
{
    public static void Draw(
        EditorViewport3D viewport,
        IRenderController renderer,
        Editor3DSession session)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(session);

        float x = 12f;
        const float y = 10f;
        x = Chip(renderer, x, y, "Perspective", on: true);
        x = Chip(renderer, x, y, session.Lit ? "Lit" : "Unlit", session.Lit);
        Chip(renderer, x, y, "Grid", session.ShowGrid);

        float zoom = Math.Clamp(80f / MathF.Max(8f, viewport.Camera.Distance), 0.1f, 8f);
        renderer.DrawText($"{zoom:0.0}x", 12f, 32f, 11f, new RenderColor(0.85f, 0.88f, 0.94f, 0.85f));

        DrawAxisTriad(renderer, 28f, viewport.SurfaceHeight - 28f);
    }

    private static float Chip(IRenderController renderer, float x, float y, string label, bool on)
    {
        float width = 12f + label.Length * 6.4f;
        RenderColor fill = on
            ? new RenderColor(
                EditorChrome.Accent.R / 255f,
                EditorChrome.Accent.G / 255f,
                EditorChrome.Accent.B / 255f,
                0.85f)
            : new RenderColor(0.12f, 0.14f, 0.18f, 0.72f);
        renderer.DrawRect(x, y, width, 18f, fill, filled: true, depth: -9800);
        renderer.DrawText(label, x + 6f, y + 3f, 11f, RenderColor.White);
        return x + width + 6f;
    }

    private static void DrawAxisTriad(IRenderController renderer, float originX, float originY)
    {
        renderer.DrawLine(originX, originY, originX + 18f, originY, EditorTransformGizmo.AxisX, 2f, depth: -9801);
        renderer.DrawLine(originX, originY, originX, originY - 18f, EditorTransformGizmo.AxisY, 2f, depth: -9801);
        renderer.DrawLine(originX, originY, originX + 12f, originY + 12f, EditorTransformGizmo.AxisZ, 2f, depth: -9801);
    }
}
