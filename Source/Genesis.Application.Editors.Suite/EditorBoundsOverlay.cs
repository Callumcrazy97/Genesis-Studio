using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

/// <summary>Wire AABB for a selected 3D editor object.</summary>
internal static class EditorBoundsOverlay
{
    public static void DrawAabb(
        EditorViewport3D viewport,
        IRenderController renderer,
        Vector3 min,
        Vector3 max,
        RenderColor? color = null)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(renderer);

        RenderColor stroke = color ?? new RenderColor(0.55f, 0.82f, 1f, 0.9f);
        Vector3[] corners =
        [
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(max.X, min.Y, max.Z),
            new(min.X, min.Y, max.Z),
            new(min.X, max.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(max.X, max.Y, max.Z),
            new(min.X, max.Y, max.Z),
        ];

        Draw(corners[0], corners[1]);
        Draw(corners[1], corners[2]);
        Draw(corners[2], corners[3]);
        Draw(corners[3], corners[0]);
        Draw(corners[4], corners[5]);
        Draw(corners[5], corners[6]);
        Draw(corners[6], corners[7]);
        Draw(corners[7], corners[4]);
        Draw(corners[0], corners[4]);
        Draw(corners[1], corners[5]);
        Draw(corners[2], corners[6]);
        Draw(corners[3], corners[7]);
        return;

        void Draw(Vector3 a, Vector3 b)
        {
            Vector3 sa = viewport.WorldToSurface(a);
            Vector3 sb = viewport.WorldToSurface(b);
            if (sa.Z is <= 0f or >= 1f || sb.Z is <= 0f or >= 1f)
            {
                return;
            }

            renderer.DrawLine(sa.X, sa.Y, sb.X, sb.Y, stroke, 1.5f, depth: -8990);
        }
    }
}
