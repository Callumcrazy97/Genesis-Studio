using System.Collections.Generic;
using System.Drawing;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// An <see cref="IPgslDrawSurface"/> that records what a script drew instead of rasterising it.
/// </summary>
/// <remarks>
/// Two consumers, one recorder: the headless dimension gates assert against these lists, and the
/// Object Editor's sandbox repaints them with GDI+ so a designer sees exactly what their Draw event
/// emitted. Keeping it in one place means the sandbox preview and the build gate cannot show
/// different pictures of the same script.
///
/// Recording rather than rendering also makes a failure specific: a test can say "the circle call
/// went missing" instead of "the image changed".
/// </remarks>
public sealed class PgslRecordingDrawSurface : IPgslDrawSurface
{
    public readonly record struct RectRecord(float X, float Y, float W, float H, bool Filled, Color Color);
    public readonly record struct CircleRecord(float X, float Y, float Radius, bool Filled, Color Color);
    public readonly record struct LineRecord(float X1, float Y1, float X2, float Y2, float Thickness, Color Color);
    public readonly record struct TextRecord(string Text, float X, float Y, float Size, Color Color);
    public readonly record struct SpriteRecord(string Sprite, float X, float Y, int Frame, float Angle, float Alpha);
    public readonly record struct CubeRecord(float X, float Y, float Z, float SX, float SY, float SZ, Color Color);
    public readonly record struct SphereRecord(float X, float Y, float Z, float Radius, Color Color);
    public readonly record struct ModelRecord(string Name, float X, float Y, float Z, float Scale, Color Color);
    public readonly record struct PointRecord(float X, float Y, Color Color);

    public List<RectRecord> Rectangles { get; } = [];
    public List<CircleRecord> Circles { get; } = [];
    public List<LineRecord> Lines { get; } = [];
    public List<TextRecord> Texts { get; } = [];
    public List<SpriteRecord> Sprites { get; } = [];
    public List<CubeRecord> Cubes { get; } = [];
    public List<SphereRecord> Spheres { get; } = [];
    public List<ModelRecord> Models { get; } = [];
    public List<PointRecord> Points { get; } = [];
    public Color? ClearedTo { get; private set; }
    public int Clears { get; private set; }

    /// <summary>3D commands no-op unless a 3D pass is live; callers set this to exercise them.</summary>
    public bool Is3DActive { get; set; }

    /// <summary>Every recorded 2D primitive, for "did it draw anything at all" checks.</summary>
    public int TotalShapes => Rectangles.Count + Circles.Count + Lines.Count + Points.Count;

    /// <summary>Every recorded call of any kind.</summary>
    public int TotalCalls => TotalShapes + Texts.Count + Sprites.Count + Cubes.Count + Spheres.Count + Models.Count + Clears;

    public void Reset()
    {
        Rectangles.Clear();
        Circles.Clear();
        Lines.Clear();
        Texts.Clear();
        Sprites.Clear();
        Cubes.Clear();
        Spheres.Clear();
        Models.Clear();
        Points.Clear();
        ClearedTo = null;
        Clears = 0;
    }

    public void Clear(Color color)
    {
        Clears++;
        ClearedTo = color;
    }

    public void DrawPoint(float x, float y, Color color) => Points.Add(new PointRecord(x, y, color));

    public void DrawLine(float x1, float y1, float x2, float y2, Color color, float thickness = 1f) =>
        Lines.Add(new LineRecord(x1, y1, x2, y2, thickness, color));

    public void DrawRectangle(Color color, RectangleF rect) =>
        Rectangles.Add(new RectRecord(rect.X, rect.Y, rect.Width, rect.Height, false, color));

    public void FillRectangle(Color color, RectangleF rect) =>
        Rectangles.Add(new RectRecord(rect.X, rect.Y, rect.Width, rect.Height, true, color));

    public void FillCircle(Color color, float centerX, float centerY, float radius) =>
        Circles.Add(new CircleRecord(centerX, centerY, radius, true, color));

    public void DrawCircle(Color color, float centerX, float centerY, float radius, float thickness = 1f) =>
        Circles.Add(new CircleRecord(centerX, centerY, radius, false, color));

    public void DrawText(string text, string font, float size, Color color, Rectangle bounds) =>
        Texts.Add(new TextRecord(text, bounds.X, bounds.Y, size, color));

    public void DrawSprite(
        string spriteName, float x, float y, int frame, float xscale, float yscale, float angle, Color blend, float alpha) =>
        Sprites.Add(new SpriteRecord(spriteName, x, y, frame, angle, alpha));

    public void QueueCube3D(float x, float y, float z, float sx, float sy, float sz, Color color, float alpha) =>
        Cubes.Add(new CubeRecord(x, y, z, sx, sy, sz, color));

    public void QueueSphere3D(float x, float y, float z, float radius, Color color, float alpha) =>
        Spheres.Add(new SphereRecord(x, y, z, radius, color));

    public void QueueModel3D(string modelName, float x, float y, float z, float scale, Color color, float alpha) =>
        Models.Add(new ModelRecord(modelName, x, y, z, scale, color));
}
