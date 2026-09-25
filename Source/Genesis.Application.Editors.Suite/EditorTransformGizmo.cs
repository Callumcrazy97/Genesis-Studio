using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

public enum EditorGizmoMode
{
    Move,
    Rotate,
    Scale,
}

public enum EditorGizmoSpace
{
    World,
    Local,
}

public readonly record struct EditorGizmoHit(int AxisIndex, EditorGizmoMode Mode);

/// <summary>
/// Shared 2D/3D transform gizmo drawing and 3D axis hit-testing. Room keeps node-specific 2D
/// scale/rotate handles; every editor uses this for XYZ axes and status text.
/// </summary>
public static class EditorTransformGizmo
{
    public static readonly RenderColor AxisX = new(0.90f, 0.28f, 0.30f);
    public static readonly RenderColor AxisY = new(0.28f, 0.78f, 0.38f);
    public static readonly RenderColor AxisZ = new(0.32f, 0.52f, 0.95f);

    public static string StatusHint(
        EditorGizmoMode mode,
        EditorGizmoSpace space,
        bool snap,
        int? activeAxis = null)
    {
        string axis = activeAxis switch
        {
            0 => "X",
            1 => "Y",
            2 => "Z",
            _ => "none",
        };
        return $"{mode} · {space.ToString().ToLowerInvariant()} · snap {(snap ? "on" : "off")} · axis {axis}";
    }

    public static Vector3[] Axes(EditorGizmoSpace space, Vector3 localEulerDegrees)
    {
        if (space == EditorGizmoSpace.World)
        {
            return [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        }

        Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(
            localEulerDegrees.Y * MathF.PI / 180f,
            localEulerDegrees.X * MathF.PI / 180f,
            localEulerDegrees.Z * MathF.PI / 180f);
        return
        [
            Vector3.TransformNormal(Vector3.UnitX, rotation),
            Vector3.TransformNormal(Vector3.UnitY, rotation),
            Vector3.TransformNormal(Vector3.UnitZ, rotation),
        ];
    }

    public static void Draw3D(
        EditorViewport3D viewport,
        IRenderController renderer,
        Vector3 origin,
        float axisLength,
        EditorGizmoMode mode,
        EditorGizmoSpace space = EditorGizmoSpace.World,
        Vector3 localEulerDegrees = default,
        bool includeZ = true,
        int? activeAxis = null)
    {
        Draw3D(
            viewport,
            renderer,
            origin,
            Axes(space, localEulerDegrees),
            axisLength,
            mode,
            includeZ,
            activeAxis);
    }

    public static void Draw3D(
        EditorViewport3D viewport,
        IRenderController renderer,
        Vector3 origin,
        Vector3[] axes,
        float axisLength,
        EditorGizmoMode mode,
        bool includeZ = true,
        int? activeAxis = null)
    {
        Vector3 originSurface = viewport.WorldToSurface(origin);
        if (originSurface.Z is < 0f or > 1f)
        {
            return;
        }

        if (mode == EditorGizmoMode.Rotate)
        {
            DrawCircleOutline(renderer, new Vector2(originSurface.X, originSurface.Y), 70f, AxisY, 2f);
        }

        RenderColor[] colors = [AxisX, AxisY, AxisZ];
        int count = Math.Min(includeZ ? 3 : 2, axes.Length);
        for (int i = 0; i < count; i++)
        {
            Vector3 axis = axes[i];
            if (axis.LengthSquared() < 1e-6f)
            {
                continue;
            }

            axis = Vector3.Normalize(axis);
            Vector3 tipSurface = viewport.WorldToSurface(origin + axis * axisLength);
            if (tipSurface.Z is < 0f or > 1f)
            {
                continue;
            }

            RenderColor color = activeAxis == i ? RenderColor.White : colors[i];
            renderer.DrawLine(
                originSurface.X, originSurface.Y, tipSurface.X, tipSurface.Y, color, 2.5f, depth: -9000);
            if (mode == EditorGizmoMode.Scale)
            {
                renderer.DrawRect(tipSurface.X - 5f, tipSurface.Y - 5f, 10f, 10f, color, filled: true, depth: -9001);
            }
            else if (mode == EditorGizmoMode.Move)
            {
                DrawArrowHead(
                    renderer,
                    new Vector2(originSurface.X, originSurface.Y),
                    new Vector2(tipSurface.X, tipSurface.Y),
                    color);
            }
            else
            {
                DrawCircle(renderer, new Vector2(tipSurface.X, tipSurface.Y), 4.5f, color, color);
            }
        }
    }

    public static void Draw2DAxes(
        EditorViewport3D viewport,
        IRenderController renderer,
        Vector2 origin,
        bool includeZ = false)
    {
        Vector2 center = viewport.World2DToSurface(origin);
        renderer.DrawLine(center.X, center.Y, center.X + 44f, center.Y, AxisX, 2.5f, depth: -9000);
        renderer.DrawLine(center.X, center.Y, center.X, center.Y - 44f, AxisY, 2.5f, depth: -9000);
        if (includeZ)
        {
            renderer.DrawLine(center.X, center.Y, center.X + 22f, center.Y + 22f, AxisZ, 2.5f, depth: -9000);
        }
    }

    public static EditorGizmoHit? HitTest3D(
        EditorViewport3D viewport,
        PointF surface,
        Vector3 origin,
        float axisLength,
        EditorGizmoMode mode,
        EditorGizmoSpace space = EditorGizmoSpace.World,
        Vector3 localEulerDegrees = default,
        bool includeZ = true,
        float threshold = 9f)
        => HitTest3D(
            viewport,
            surface,
            origin,
            Axes(space, localEulerDegrees),
            axisLength,
            mode,
            includeZ,
            threshold);

    public static EditorGizmoHit? HitTest3D(
        EditorViewport3D viewport,
        PointF surface,
        Vector3 origin,
        Vector3[] axes,
        float axisLength,
        EditorGizmoMode mode,
        bool includeZ = true,
        float threshold = 9f)
    {
        Vector3 originSurface = viewport.WorldToSurface(origin);
        int count = Math.Min(includeZ ? 3 : 2, axes.Length);
        for (int i = 0; i < count; i++)
        {
            Vector3 axis = axes[i];
            if (axis.LengthSquared() < 1e-6f)
            {
                continue;
            }

            axis = Vector3.Normalize(axis);
            Vector3 tip = origin + axis * axisLength;
            Vector3 tipSurface = viewport.WorldToSurface(tip);
            float distance = DistancePointSegment(
                new Vector2(surface.X, surface.Y),
                new Vector2(originSurface.X, originSurface.Y),
                new Vector2(tipSurface.X, tipSurface.Y));
            if (distance < threshold)
            {
                return new EditorGizmoHit(i, mode);
            }
        }

        return null;
    }

    public static bool TryProjectAxisToSurface(
        EditorViewport3D viewport,
        Vector3 origin,
        Vector3 axis,
        float axisLength,
        out Vector2 surfaceDirection,
        out float surfaceLength)
    {
        surfaceDirection = default;
        surfaceLength = 0f;
        if (axis.LengthSquared() < 1e-6f)
        {
            return false;
        }

        axis = Vector3.Normalize(axis);
        Vector3 start = viewport.WorldToSurface(origin);
        Vector3 end = viewport.WorldToSurface(origin + axis * axisLength);
        if (start.Z is < 0f or > 1f || end.Z is < 0f or > 1f)
        {
            return false;
        }

        Vector2 delta = new(end.X - start.X, end.Y - start.Y);
        surfaceLength = delta.Length();
        if (surfaceLength < 1f)
        {
            return false;
        }

        surfaceDirection = delta / surfaceLength;
        return true;
    }

    public static float DistancePointSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-6f)
        {
            return Vector2.Distance(point, a);
        }

        float t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }

    public static void DrawCircle(
        IRenderController renderer,
        Vector2 center,
        float radius,
        RenderColor fill,
        RenderColor stroke)
    {
        renderer.DrawRect(center.X - radius, center.Y - radius, radius * 2f, radius * 2f, fill, filled: true, depth: -9001);
        renderer.DrawRect(center.X - radius, center.Y - radius, radius * 2f, radius * 2f, stroke, filled: false, depth: -9002);
    }

    private static void DrawArrowHead(
        IRenderController renderer,
        Vector2 origin,
        Vector2 tip,
        RenderColor color)
    {
        Vector2 direction = tip - origin;
        if (direction.LengthSquared() < 1f)
        {
            DrawCircle(renderer, tip, 4.5f, color, color);
            return;
        }

        direction = Vector2.Normalize(direction);
        Vector2 side = new(-direction.Y, direction.X);
        Vector2 basePoint = tip - direction * 10f;
        renderer.DrawLine(tip.X, tip.Y, basePoint.X + side.X * 5f, basePoint.Y + side.Y * 5f, color, 2f, -9001);
        renderer.DrawLine(tip.X, tip.Y, basePoint.X - side.X * 5f, basePoint.Y - side.Y * 5f, color, 2f, -9001);
    }

    public static void DrawCircleOutline(
        IRenderController renderer,
        Vector2 center,
        float radius,
        RenderColor color,
        float thickness)
    {
        const int steps = 28;
        Vector2 previous = center + new Vector2(radius, 0f);
        for (int i = 1; i <= steps; i++)
        {
            float angle = i / (float)steps * MathF.PI * 2f;
            Vector2 next = center + new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
            renderer.DrawLine(previous.X, previous.Y, next.X, next.Y, color, thickness, depth: -9000);
            previous = next;
        }
    }
}
