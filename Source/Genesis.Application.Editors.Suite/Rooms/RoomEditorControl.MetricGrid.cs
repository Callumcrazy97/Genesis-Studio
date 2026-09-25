using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

public readonly record struct RoomMetricGridLine(Vector3 From, Vector3 To, bool Major, bool Axis);

/// <summary>A world-anchored metric grid with bounded screen density, independent of snap precision.</summary>
public readonly record struct RoomMetricGridLayout(float RequestedSpacing, float DisplaySpacing,
    long FirstX, long LastX, long FirstZ, long LastZ)
{
    public float MajorSpacing => DisplaySpacing * 5f;
    public int LineCount => checked((int)(LastX - FirstX + LastZ - FirstZ + 2));

    public static RoomMetricGridLayout Create(float spacing, Vector3 centre, float distance,
        int surfaceHeight, float fieldOfViewDegrees)
    {
        spacing = float.IsFinite(spacing) ? Math.Clamp(spacing, .01f, 4096f) : 1f;
        distance = float.IsFinite(distance) ? Math.Clamp(distance, .1f, 10_000_000f) : 20f;
        float fov = float.IsFinite(fieldOfViewDegrees) ? Math.Clamp(fieldOfViewDegrees, 1f, 170f) : 60f;
        float metresPerPixel = 2f * distance * MathF.Tan(fov * MathF.PI / 360f) / Math.Max(1, surfaceHeight);
        float halfExtent = MathF.Max(spacing * 10f, distance * 1.25f);
        float displayed = spacing;
        // Keep at most 642 lines. The selected snap increment remains unchanged when zoomed out.
        while (displayed / MathF.Max(.000001f, metresPerPixel) < 3f || halfExtent / displayed > 160f)
            displayed *= 5f;
        int halfLines = Math.Clamp((int)MathF.Ceiling(halfExtent / displayed), 1, 160);
        long x = (long)Math.Floor(SafeCoordinate(centre.X) / displayed);
        long z = (long)Math.Floor(SafeCoordinate(centre.Z) / displayed);
        return new(spacing, displayed, x - halfLines, x + halfLines, z - halfLines, z + halfLines);
    }

    public IEnumerable<RoomMetricGridLine> Lines()
    {
        float minX = FirstX * DisplaySpacing, maxX = LastX * DisplaySpacing;
        float minZ = FirstZ * DisplaySpacing, maxZ = LastZ * DisplaySpacing;
        for (long x = FirstX; x <= LastX; x++)
            yield return new(new(x * DisplaySpacing, 0, minZ), new(x * DisplaySpacing, 0, maxZ), x % 5 == 0, x == 0);
        for (long z = FirstZ; z <= LastZ; z++)
            yield return new(new(minX, 0, z * DisplaySpacing), new(maxX, 0, z * DisplaySpacing), z % 5 == 0, z == 0);
    }

    private static double SafeCoordinate(float value) => float.IsFinite(value) ? Math.Clamp((double)value, -100_000_000, 100_000_000) : 0;
}

public sealed partial class RoomEditorControl
{
    public RoomMetricGridLayout? LastMetricGridLayout { get; private set; }
    public int LastMetricGridDrawnLines { get; private set; }

    private void DrawRoomMetricGrid(IRenderController renderer)
    {
        Vector3 centre = _viewport.Camera.Target;
        bool hasCameraWorld = Matrix4x4.Invert(_viewport.ViewMatrix, out Matrix4x4 cameraWorld);
        Vector3 eye = hasCameraWorld
            ? cameraWorld.Translation : _viewport.Camera.Eye;
        if (GameCameraPreviewState is not null)
            centre = eye + (hasCameraWorld ? -new Vector3(cameraWorld.M31, cameraWorld.M32, cameraWorld.M33) : _viewport.Camera.Forward) * 20f;
        centre.Y = 0;
        RoomMetricGridLayout layout = RoomMetricGridLayout.Create(MetricGridSize, centre,
            Vector3.Distance(eye, centre), _viewport.SurfaceHeight, _viewport.FieldOfViewDegrees);
        LastMetricGridLayout = layout;
        LastMetricGridDrawnLines = 0;
        Matrix4x4 viewProjection = _viewport.ViewMatrix * _viewport.ProjectionMatrix;
        foreach (RoomMetricGridLine line in layout.Lines())
        {
            if (!TryProjectGridLine(line, viewProjection, _viewport.SurfaceWidth, _viewport.SurfaceHeight, out Vector2 a, out Vector2 b)) continue;
            float alpha = GridColor[3] * (line.Axis ? 1.35f : line.Major ? 1f : .42f);
            RenderColor colour = new(GridColor[0], GridColor[1], GridColor[2], Math.Clamp(alpha, 0, 1));
            renderer.DrawLine(a.X, a.Y, b.X, b.Y, colour, line.Axis ? 1.4f : line.Major ? 1f : .75f, depth: -9100);
            LastMetricGridDrawnLines++;
        }
        if (layout.DisplaySpacing > layout.RequestedSpacing * 1.001f)
        {
            renderer.DrawText(FormattableString.Invariant($"Grid {layout.DisplaySpacing:0.###} m · snap {layout.RequestedSpacing:0.###} m"),
                12, _viewport.SurfaceHeight - 24, 11, ToRenderColor(EditorChrome.Muted, .85f));
        }
    }

    private static bool TryProjectGridLine(RoomMetricGridLine line, Matrix4x4 viewProjection, int width, int height,
        out Vector2 from, out Vector2 to)
    {
        from = to = default;
        Vector4 a = Vector4.Transform(new Vector4(line.From, 1), viewProjection);
        Vector4 b = Vector4.Transform(new Vector4(line.To, 1), viewProjection);
        float first = 0, last = 1;
        if (!Clip(a.W + a.X, b.W + b.X) || !Clip(a.W - a.X, b.W - b.X)
            || !Clip(a.W + a.Y, b.W + b.Y) || !Clip(a.W - a.Y, b.W - b.Y)
            || !Clip(a.Z, b.Z) || !Clip(a.W - a.Z, b.W - b.Z)) return false;
        Vector4 start = Vector4.Lerp(a, b, first), end = Vector4.Lerp(a, b, last);
        if (start.W <= .000001f || end.W <= .000001f) return false;
        from = new((start.X / start.W * .5f + .5f) * width, (.5f - start.Y / start.W * .5f) * height);
        to = new((end.X / end.W * .5f + .5f) * width, (.5f - end.Y / end.W * .5f) * height);
        return float.IsFinite(from.X) && float.IsFinite(from.Y) && float.IsFinite(to.X) && float.IsFinite(to.Y);

        bool Clip(float startDistance, float endDistance)
        {
            if (!float.IsFinite(startDistance) || !float.IsFinite(endDistance)) return false;
            if (startDistance < 0 && endDistance < 0) return false;
            if (startDistance < 0) first = MathF.Max(first, startDistance / (startDistance - endDistance));
            else if (endDistance < 0) last = MathF.Min(last, startDistance / (startDistance - endDistance));
            return first <= last;
        }
    }
}
