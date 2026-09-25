using System.Numerics;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Adapts the backend-neutral <see cref="IOverlayCanvas"/> to <see cref="IHudCanvas"/>, which is
    /// the surface project scripts and the debug overlay draw against.
    /// </summary>
    /// <remarks>
    /// This was <c>D2dHudCanvas</c> and wrapped a Direct2D-typed overlay, which quietly made every
    /// scripted HUD a Direct3D-only feature. Only the wrapped type changed; scripts are unaffected.
    /// </remarks>
    public sealed class OverlayHudCanvas : IHudCanvas
    {
        private readonly IOverlayCanvas _overlay;

        public OverlayHudCanvas(IOverlayCanvas overlay, int width, int height)
        {
            _overlay = overlay;
            Width = width;
            Height = height;
        }

        public int Width { get; }

        public int Height { get; }

        public void Text(string text, float x, float y, float size, Vector4 color)
            => _overlay.DrawText(text, new Vector2(x, y), size, color);

        public void TextCentered(string text, float centerX, float y, float width, float size, Vector4 color)
            => _overlay.DrawTextCentered(text, centerX, y, width, size, color);

        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true)
            => _overlay.DrawRect(x, y, w, h, color, filled: filled);

        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f)
            => _overlay.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), color, thickness);
    }
}
