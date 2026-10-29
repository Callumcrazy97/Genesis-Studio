using System;
using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// A draw surface that moves every 2D draw by an offset before handing it on (PGSL DrawSetOffset):
/// a menu laid out from (0, 0) can be drawn on a second screen or panel without changing its code.
/// 3D queues pass through untouched. One instance per thread is reused, so drawing allocates nothing.
/// </summary>
internal sealed class OffsetDrawSurface : IPgslDrawSurface
{
    [ThreadStatic] private static OffsetDrawSurface _shared;

    private IPgslDrawSurface _inner;
    private float _x, _y;

    public static IPgslDrawSurface Wrap(IPgslDrawSurface inner, float x, float y)
    {
        OffsetDrawSurface surface = _shared ??= new OffsetDrawSurface();
        surface._inner = inner; surface._x = x; surface._y = y;
        return surface;
    }

    private RectangleF Move(RectangleF r) => new(r.X + _x, r.Y + _y, r.Width, r.Height);
    private Rectangle Move(Rectangle r) => new((int)MathF.Round(r.X + _x), (int)MathF.Round(r.Y + _y), r.Width, r.Height);

    public void Clear(Color color) => _inner.Clear(color);
    public void DrawPoint(float x, float y, Color color) => _inner.DrawPoint(x + _x, y + _y, color);
    public void DrawLine(float x1, float y1, float x2, float y2, Color color, float thickness = 1f) =>
        _inner.DrawLine(x1 + _x, y1 + _y, x2 + _x, y2 + _y, color, thickness);
    public void DrawRectangle(Color color, RectangleF rect) => _inner.DrawRectangle(color, Move(rect));
    public void FillRectangle(Color color, RectangleF rect) => _inner.FillRectangle(color, Move(rect));
    public void FillCircle(Color color, float centerX, float centerY, float radius) => _inner.FillCircle(color, centerX + _x, centerY + _y, radius);
    public void DrawCircle(Color color, float centerX, float centerY, float radius, float thickness = 1f) =>
        _inner.DrawCircle(color, centerX + _x, centerY + _y, radius, thickness);
    public void DrawText(string text, string font, float size, Color color, Rectangle bounds) => _inner.DrawText(text, font, size, color, Move(bounds));
    public void DrawUiText(string text, string font, float size, Color color, Rectangle bounds, bool centered) =>
        _inner.DrawUiText(text, font, size, color, Move(bounds), centered);
    public void DrawSprite(string spriteName, float x, float y, int frame, float xscale, float yscale, float angle, Color blend, float alpha) =>
        _inner.DrawSprite(spriteName, x + _x, y + _y, frame, xscale, yscale, angle, blend, alpha);
    public void DrawSpriteRectangle(string spriteName, RectangleF destination, int frame, Color blend, float alpha) =>
        _inner.DrawSpriteRectangle(spriteName, Move(destination), frame, blend, alpha);
    public void DrawTextRun(string text, string font, float size, Color color, float x, float y, float tracking) =>
        _inner.DrawTextRun(text, font, size, color, x + _x, y + _y, tracking);
    public Vector2 MeasureText(string text, string font, float size, float tracking) => _inner.MeasureText(text, font, size, tracking);
    public void SetClip(RectangleF clip) => _inner.SetClip(clip.IsEmpty ? clip : Move(clip));
    public void SetBlendLinear(bool linear) => _inner.SetBlendLinear(linear);
    public bool BlendLinear => _inner.BlendLinear;
    public void DrawSpritePart(string spriteName, int frame, RectangleF source, RectangleF destination, Color blend, float alpha) =>
        _inner.DrawSpritePart(spriteName, frame, source, Move(destination), blend, alpha);

    public void FillRectangles(ReadOnlySpan<GuiRectangle> rectangles)
    {
        foreach (GuiRectangle r in rectangles)
        {
            Vector4 c = Vector4.Clamp(r.Color, Vector4.Zero, Vector4.One) * 255f;
            _inner.FillRectangle(Color.FromArgb((int)MathF.Round(c.W), (int)MathF.Round(c.X), (int)MathF.Round(c.Y), (int)MathF.Round(c.Z)),
                new RectangleF(r.X + _x, r.Y + _y, r.Width, r.Height));
        }
    }

    public void DrawSpriteParts(string spriteName, ReadOnlySpan<GuiSpritePart> parts, Color blend)
    {
        foreach (GuiSpritePart p in parts)
            _inner.DrawSpritePart(spriteName, p.Frame, RectangleF.FromLTRB(p.U0, p.V0, p.U1, p.V1),
                new RectangleF(p.X + _x, p.Y + _y, p.Width, p.Height), blend, p.Alpha);
    }

    public void DrawScriptTexture(int texture, RectangleF source, RectangleF destination, float angle, Color blend, float alpha) =>
        _inner.DrawScriptTexture(texture, source, Move(destination), angle, blend, alpha);
    public void DrawModelGui(string modelName, RectangleF destination, float yaw, float pitch, float zoom, string clip, float time, float alpha) =>
        _inner.DrawModelGui(modelName, Move(destination), yaw, pitch, zoom, clip, time, alpha);

    public void QueueScriptMesh3D(int meshId, Matrix4x4 world, string image, string shader, Color tint, float alpha, ScriptMeshDrawOptions options) =>
        _inner.QueueScriptMesh3D(meshId, world, image, shader, tint, alpha, options);
    public bool Is3DActive => _inner.Is3DActive;
    public void QueueCube3D(float x, float y, float z, float sx, float sy, float sz, Color color, float alpha) => _inner.QueueCube3D(x, y, z, sx, sy, sz, color, alpha);
    public void QueueSphere3D(float x, float y, float z, float radius, Color color, float alpha) => _inner.QueueSphere3D(x, y, z, radius, color, alpha);
    public void QueueModel3D(string modelName, float x, float y, float z, float scale, Color color, float alpha) => _inner.QueueModel3D(modelName, x, y, z, scale, color, alpha);
    public void QueueModelTransform3D(string modelName, string shaderName, float x, float y, float z, float sx, float sy, float sz,
        float xrot, float yrot, float zrot, Color color, float alpha) =>
        _inner.QueueModelTransform3D(modelName, shaderName, x, y, z, sx, sy, sz, xrot, yrot, zrot, color, alpha);
    public void QueueBeam3D(float x1, float y1, float z1, float x2, float y2, float z2, float width, Color color, float alpha) =>
        _inner.QueueBeam3D(x1, y1, z1, x2, y2, z2, width, color, alpha);
    public void QueuePointLight3D(float x, float y, float z, float radius, float intensity, Color color, float falloff = 2f) =>
        _inner.QueuePointLight3D(x, y, z, radius, intensity, color, falloff);
    public void DrawText3D(string text, float x, float y, float z, string font, float size, Color color, Matrix4x4 view, Matrix4x4 projection) =>
        _inner.DrawText3D(text, x, y, z, font, size, color, view, projection);
}
