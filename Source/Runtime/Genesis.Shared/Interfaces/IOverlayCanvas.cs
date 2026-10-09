using System.Numerics;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// Backend-neutral overlay surface for crisp text and simple vector HUD elements composited
    /// over the finished frame.
    /// </summary>
    /// <remarks>
    /// <para>This replaces the Direct2D-typed overlay every caller used to receive. The drawing
    /// methods keep the same names, argument order and defaults as that type, so the switch is a
    /// parameter-type change at each call site rather than a rewrite — but nothing on this contract
    /// names an API, which is the whole point: a boot splash, a debug banner or a scripted HUD is
    /// authored once and looks the same on Direct3D, Vulkan and OpenGL.</para>
    ///
    /// <para><b>Coordinates are pixels, origin top-left</b>, matching the swap chain rather than any
    /// backend's clip space. Text is positioned by its <i>top</i> edge, not its baseline, because
    /// that is what HUD layout code actually reasons about.</para>
    /// </remarks>
    public interface IOverlayCanvas
    {
        /// <summary>Overlay width in pixels — the surface being composited over.</summary>
        int Width { get; }

        /// <summary>Overlay height in pixels.</summary>
        int Height { get; }

        /// <summary>Draws left-aligned text with its top-left corner at <paramref name="position"/>.</summary>
        void DrawText(
            string text,
            Vector2 position,
            float size,
            Vector4 color,
            string fontFamily = "Segoe UI",
            bool bold = false,
            float maxWidth = 4096f,
            float maxHeight = 4096f);

        /// <summary>Draws text horizontally centred on <paramref name="centerX"/>, top edge at <paramref name="y"/>.</summary>
        void DrawTextCentered(
            string text,
            float centerX,
            float y,
            float width,
            float size,
            Vector4 color,
            string fontFamily = "Segoe UI",
            bool bold = false);

        /// <summary>Draws a straight line between two points.</summary>
        void DrawLine(Vector2 from, Vector2 to, Vector4 color, float width = 1.5f);

        /// <summary>Draws a rectangle, outlined by default.</summary>
        void DrawRect(
            float x,
            float y,
            float width,
            float height,
            Vector4 color,
            float strokeWidth = 1.5f,
            bool filled = false);

        /// <summary>Draws a rectangle from a position and size.</summary>
        void DrawRect(
            Vector2 position,
            Vector2 size,
            Vector4 color,
            float strokeWidth = 1.5f,
            bool filled = false);

        /// <summary>
        /// Left-aligned text with <paramref name="tracking"/> pixels of letter spacing added between
        /// glyphs. Canvases without letter spacing draw it unspaced.
        /// </summary>
        void DrawTrackedText(string text, Vector2 position, float size, Vector4 color, string fontFamily, float tracking)
            => DrawText(text, position, size, color, fontFamily);

        /// <summary>
        /// Limits later draws to a screen rectangle (x, y, width, height); a zero width or height
        /// clears the limit. Canvases that cannot clip ignore it.
        /// </summary>
        void SetClip(Vector4 clip) { }

        /// <summary>
        /// Whether later draws blend in linear light (the screen decoded from sRGB, blended and
        /// encoded again) rather than on the stored sRGB values. Canvases that cannot ignore it.
        /// </summary>
        void SetBlendLinear(bool linear) { }

        /// <summary>Whether <see cref="DrawSprite"/> draws; canvases that cannot leave sprites to the sprite pass.</summary>
        bool SupportsSprites => false;

        /// <summary>A textured sprite in screen pixels, drawn in order with this canvas's text and shapes.</summary>
        void DrawSprite(in SpriteDrawCall call) { }

        /// <summary>
        /// Many filled rectangles in order, as one command where the canvas can (PGSL
        /// DrawRectanglesFromList); otherwise each is drawn as a filled <see cref="DrawRect(float, float, float, float, Vector4, float, bool)"/>.
        /// </summary>
        void DrawFilledRects(System.ReadOnlySpan<GuiRectangle> rectangles)
        {
            foreach (GuiRectangle rectangle in rectangles)
                DrawRect(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, rectangle.Color, filled: true);
        }
    }
}
