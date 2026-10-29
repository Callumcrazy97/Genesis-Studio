using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Debugger;

/// <summary>
/// What drew the GUI this frame, for the debug screen's GUI page: each instance's Draw GUI event
/// and each C# behaviour's HUD, how many draw calls it made and the area it covered, and the User
/// Interface resources it drew. Off unless the screen shows the page or outlines the layers; off,
/// the GUI is drawn exactly as before with one field read per frame.
/// </summary>
public static class GuiLayerTrace
{
    /// <summary>One User Interface resource drawn by a layer.</summary>
    public sealed class UiDraw
    {
        public string Asset = "";
        public int Elements;
        public RectangleF Bounds;
        /// <summary>The GUI size it was laid out for.</summary>
        public SizeF Target;
    }

    /// <summary>One instance's (or behaviour's) GUI drawing in a frame.</summary>
    public sealed class Layer
    {
        /// <summary>"Draw GUI" (a PGSL event) or "HUD" (a C# behaviour).</summary>
        public string Pass = "";
        public string Owner = "";
        public int Instance;
        public int DrawCalls;
        public RectangleF Bounds = RectangleF.Empty;
        public readonly List<UiDraw> Documents = new();

        internal void Grow(RectangleF area)
        {
            if (area.Width <= 0 && area.Height <= 0) return;
            Bounds = Bounds.IsEmpty ? area : RectangleF.Union(Bounds, area);
        }
    }

    /// <summary>True while the debug screen wants the GUI traced (its GUI page, or outlined layers).</summary>
    public static bool Enabled { get; set; }

    private static List<Layer> _building = new(), _last = new();
    private static Layer _open;
    private static readonly TracingHudCanvas Canvas = new();

    /// <summary>The layers of the last finished frame, in the order they drew.</summary>
    public static IReadOnlyList<Layer> Last => _last;

    /// <summary>The GUI size of the last frame traced.</summary>
    public static Size Target { get; private set; }

    /// <summary>Ends the frame: what was traced becomes <see cref="Last"/>. The debug screen calls it as it draws.</summary>
    public static void EndFrame()
    {
        (_last, _building) = (_building, _last);
        _building.Clear();
        _open = null;
    }

    /// <summary>Forgets every layer (a new game, or the trace turned off).</summary>
    public static void Clear()
    {
        _building.Clear();
        _last.Clear();
        _open = null;
    }

    /// <summary>The canvas to give this frame's GUI drawing so its calls are counted; the same canvas when not tracing.</summary>
    public static IHudCanvas Wrap(IHudCanvas hud)
    {
        if (!Enabled || hud == null) return hud;
        if (hud is TracingHudCanvas) return hud;
        Canvas.Inner = hud;
        Target = new Size(hud.Width, hud.Height);
        return Canvas;
    }

    public static void BeginLayer(string pass, string owner, int instance)
    {
        if (!Enabled) return;
        _open = new Layer { Pass = pass ?? "", Owner = string.IsNullOrEmpty(owner) ? "(unnamed)" : owner, Instance = instance };
    }

    public static void EndLayer()
    {
        Layer layer = _open;
        _open = null;
        // A layer that drew nothing is not listed: most instances have no GUI to draw this frame.
        if (layer != null && (layer.DrawCalls > 0 || layer.Documents.Count > 0)) _building.Add(layer);
    }

    /// <summary>A User Interface resource the open layer drew (DrawUi).</summary>
    public static void NoteUi(string asset, int elements, RectangleF bounds, SizeF target)
    {
        if (!Enabled || _open == null) return;
        _open.Documents.Add(new UiDraw { Asset = asset ?? "", Elements = elements, Bounds = bounds, Target = target });
        _open.Grow(bounds);
    }

    private static void Count(RectangleF area)
    {
        Layer layer = _open;
        if (layer == null) return;
        layer.DrawCalls++;
        layer.Grow(area);
    }

    private static RectangleF TextArea(string text, float x, float y, float size) =>
        new(x, y, (text?.Length ?? 0) * size * 0.55f, size * 1.3f);

    /// <summary>Passes every call to the real canvas, counting each into the open layer.</summary>
    private sealed class TracingHudCanvas : IHudCanvas
    {
        public IHudCanvas Inner;

        public int Width => Inner.Width;
        public int Height => Inner.Height;
        public bool SupportsSprites => Inner.SupportsSprites;

        public void Text(string text, float x, float y, float size, Vector4 color)
        { Count(TextArea(text, x, y, size)); Inner.Text(text, x, y, size, color); }

        public void Text(string text, float x, float y, float size, Vector4 color, string font)
        { Count(TextArea(text, x, y, size)); Inner.Text(text, x, y, size, color, font); }

        public void Text(string text, float x, float y, float size, Vector4 color, string font, float tracking)
        { Count(TextArea(text, x, y, size)); Inner.Text(text, x, y, size, color, font, tracking); }

        public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color)
        { Count(new RectangleF(centerX - width * 0.5f, y, width, size * 1.3f)); Inner.TextCentered(text, centerX, y, width, size, color); }

        public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color, string font)
        { Count(new RectangleF(centerX - width * 0.5f, y, width, size * 1.3f)); Inner.TextCentered(text, centerX, y, width, size, color, font); }

        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true)
        { Count(new RectangleF(x, y, w, h)); Inner.Rect(x, y, w, h, color, filled); }

        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f)
        { Count(RectangleF.FromLTRB(MathF.Min(x1, x2), MathF.Min(y1, y2), MathF.Max(x1, x2) + 1f, MathF.Max(y1, y2) + 1f)); Inner.Line(x1, y1, x2, y2, color, thickness); }

        public Vector2 MeasureText(string text, float size, string font = null) => Inner.MeasureText(text, size, font);

        public void SetClip(float x, float y, float width, float height) => Inner.SetClip(x, y, width, height);

        public void SetBlendLinear(bool linear) => Inner.SetBlendLinear(linear);

        public void Sprite(in SpriteDrawCall call)
        {
            Count(new RectangleF(call.X - call.OriginX, call.Y - call.OriginY, call.Width * (call.ScaleX == 0 ? 1 : call.ScaleX),
                call.Height * (call.ScaleY == 0 ? 1 : call.ScaleY)));
            Inner.Sprite(call);
        }

        public void Rects(ReadOnlySpan<GuiRectangle> rectangles)
        {
            if (rectangles.IsEmpty) return;
            RectangleF area = new(rectangles[0].X, rectangles[0].Y, rectangles[0].Width, rectangles[0].Height);
            foreach (GuiRectangle rectangle in rectangles) area = RectangleF.Union(area, new RectangleF(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height));
            Count(area);
            Inner.Rects(rectangles);
        }

        public void Circle(float centerX, float centerY, float radius, Vector4 color, bool filled = true, float thickness = 1.5f)
        { Count(new RectangleF(centerX - radius, centerY - radius, radius * 2f, radius * 2f)); Inner.Circle(centerX, centerY, radius, color, filled, thickness); }

        public void Arc(float centerX, float centerY, float radius, float startDegrees, float sweepDegrees, Vector4 color, float thickness = 1.5f)
        { Count(new RectangleF(centerX - radius, centerY - radius, radius * 2f, radius * 2f)); Inner.Arc(centerX, centerY, radius, startDegrees, sweepDegrees, color, thickness); }

        public void Polygon(ReadOnlySpan<Vector2> points, Vector4 color, bool filled = true, float thickness = 1.5f)
        {
            if (!points.IsEmpty)
            {
                Vector2 min = points[0], max = points[0];
                foreach (Vector2 point in points) { min = Vector2.Min(min, point); max = Vector2.Max(max, point); }
                Count(RectangleF.FromLTRB(min.X, min.Y, max.X, max.Y));
            }
            Inner.Polygon(points, color, filled, thickness);
        }
    }
}
