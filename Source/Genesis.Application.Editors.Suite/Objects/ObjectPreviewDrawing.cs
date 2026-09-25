using System.Numerics;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>Replays the last Draw event while paused, without executing user code again.</summary>
internal sealed class ObjectPreviewDrawRecording : IPgslDrawSurface
{
    private readonly List<Action<IPgslDrawSurface>> _draws = [];
    private IPgslDrawSurface _surface = null!;
    public bool Is3DActive => _surface.Is3DActive;
    public void Begin(IPgslDrawSurface surface) { _surface = surface; _draws.Clear(); }
    public void Reset() => _draws.Clear();
    public void Replay(IPgslDrawSurface surface) { foreach (var draw in _draws) draw(surface); }
    private void Record(Action<IPgslDrawSurface> draw) { _draws.Add(draw); draw(_surface); }
    public void Clear(Color color) => Record(surface => surface.Clear(color));
    public void DrawPoint(float x, float y, Color color) => Record(surface => surface.DrawPoint(x, y, color));
    public void DrawLine(float x1, float y1, float x2, float y2, Color color, float thickness = 1) => Record(surface => surface.DrawLine(x1, y1, x2, y2, color, thickness));
    public void DrawRectangle(Color color, RectangleF rect) => Record(surface => surface.DrawRectangle(color, rect));
    public void FillRectangle(Color color, RectangleF rect) => Record(surface => surface.FillRectangle(color, rect));
    public void FillCircle(Color color, float x, float y, float radius) => Record(surface => surface.FillCircle(color, x, y, radius));
    public void DrawCircle(Color color, float x, float y, float radius, float thickness = 1) => Record(surface => surface.DrawCircle(color, x, y, radius, thickness));
    public void DrawText(string text, string font, float size, Color color, Rectangle bounds) => Record(surface => surface.DrawText(text, font, size, color, bounds));
    public void DrawSprite(string name, float x, float y, int frame, float sx, float sy, float angle, Color color, float alpha) => Record(surface => surface.DrawSprite(name, x, y, frame, sx, sy, angle, color, alpha));
    public void QueueCube3D(float x, float y, float z, float sx, float sy, float sz, Color color, float alpha) => Record(surface => surface.QueueCube3D(x, y, z, sx, sy, sz, color, alpha));
    public void QueueSphere3D(float x, float y, float z, float radius, Color color, float alpha) => Record(surface => surface.QueueSphere3D(x, y, z, radius, color, alpha));
    public void QueueModel3D(string model, float x, float y, float z, float scale, Color color, float alpha) => Record(surface => surface.QueueModel3D(model, x, y, z, scale, color, alpha));
    public void QueueModelTransform3D(string model, string shader, float x, float y, float z, float sx, float sy, float sz, float rx, float ry, float rz, Color color, float alpha)
        => Record(surface => surface.QueueModelTransform3D(model, shader, x, y, z, sx, sy, sz, rx, ry, rz, color, alpha));
    public void QueuePointLight3D(float x, float y, float z, float radius, float intensity, Color color, float falloff = 2)
        => Record(surface => surface.QueuePointLight3D(x, y, z, radius, intensity, color, falloff));
}

internal sealed class ObjectPreviewHud : IHudCanvas
{
    private readonly List<Action<IHudCanvas>> _draws = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public void Reset(int width, int height) { Width = width; Height = height; _draws.Clear(); }
    public void Replay(IHudCanvas canvas) { foreach (var draw in _draws) draw(canvas); }
    public void Text(string text, float x, float y, float size, Vector4 color) => _draws.Add(canvas => canvas.Text(text, x, y, size, color));
    public void TextCentered(string text, float x, float y, float width, float size, Vector4 color) => _draws.Add(canvas => canvas.TextCentered(text, x, y, width, size, color));
    public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) => _draws.Add(canvas => canvas.Rect(x, y, w, h, color, filled));
    public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) => _draws.Add(canvas => canvas.Line(x1, y1, x2, y2, color, thickness));
}
