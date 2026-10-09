using System;
using System.Drawing;
using System.Numerics;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// How a script mesh is drawn (PGSL DrawMeshSet*): the defaults are an ordinary lit, shadowed,
    /// fogged, one-sided mesh that is see-through only when its alpha is below 1.
    /// </summary>
    public struct ScriptMeshDrawOptions
    {
        public bool NoCastShadow;
        public bool NoReceiveShadow;
        public bool NoFog;
        /// <summary>Both sides drawn (no back-face culling): leaves, crossed plants, flat panels.</summary>
        public bool TwoSided;
        /// <summary>Blended and not writing depth even at full alpha (glass, water).</summary>
        public bool Transparent;
        /// <summary>The mesh's own colours added over its lighting: 0 none, 1 fully self-lit.</summary>
        public float Glow;
        /// <summary>The calling instance's shader values by parameter name (ShaderSetParameter); may be null.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, float[]> ShaderParameters;
        /// <summary>The calling instance's shader texture overrides by resource name; may be null.</summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, string> ShaderResources;
    }

    /// <summary>2D/3D draw surface used by PGSL commands during sandbox and play mode.</summary>
    public interface IPgslDrawSurface
    {
        void Clear(Color color);
        void DrawPoint(float x, float y, Color color);
        void DrawLine(float x1, float y1, float x2, float y2, Color color, float thickness = 1f);
        void DrawRectangle(Color color, RectangleF rect);
        void FillRectangle(Color color, RectangleF rect);
        /// <summary>Filled circle. Implementations may approximate with a triangle fan / segments.</summary>
        void FillCircle(Color color, float centerX, float centerY, float radius);
        /// <summary>Circle outline of the given stroke thickness.</summary>
        void DrawCircle(Color color, float centerX, float centerY, float radius, float thickness = 1f);
        void DrawText(string text, string font, float size, Color color, Rectangle bounds);
        /// <summary>UI text is vertically centred in its authored bounds; buttons also centre horizontally.</summary>
        void DrawUiText(string text, string font, float size, Color color, Rectangle bounds, bool centered)
            => DrawText(text, font, size, color, bounds);
        void DrawSprite(string spriteName, float x, float y, int frame, float xscale, float yscale, float angle, Color blend, float alpha);
        /// <summary>Draw an image into a GUI rectangle, independent of its gameplay sprite origin.</summary>
        void DrawSpriteRectangle(string spriteName, RectangleF destination, int frame, Color blend, float alpha);
        /// <summary>
        /// Text with its top-left corner at (x, y) and <paramref name="tracking"/> pixels of letter
        /// spacing between glyphs.
        /// </summary>
        void DrawTextRun(string text, string font, float size, Color color, float x, float y, float tracking)
            => DrawText(text, font, size, color, new Rectangle((int)x, (int)y, 4096, 4096));

        /// <summary>The width and line height <see cref="DrawTextRun"/> would draw this text at.</summary>
        Vector2 MeasureText(string text, string font, float size, float tracking)
            => Genesis.Shared.Overlay.GlyphAtlas.Measure(text, font, size, bold: false, tracking);

        /// <summary>Limits later GUI drawing to a rectangle; an empty rectangle draws everywhere again.</summary>
        void SetClip(RectangleF clip) { }

        /// <summary>
        /// Whether later GUI drawing blends in linear light (DrawSetBlendLinear) rather than on the
        /// stored sRGB values. Surfaces that cannot ignore it.
        /// </summary>
        void SetBlendLinear(bool linear) { }

        /// <summary>What <see cref="SetBlendLinear"/> last set.</summary>
        bool BlendLinear => false;

        /// <summary>
        /// Part of an image frame into a rectangle: <paramref name="source"/> is in fractions of the
        /// frame (0 to 1), so (0.25, 0, 0.5, 1) is the middle half of its width.
        /// </summary>
        void DrawSpritePart(string spriteName, int frame, RectangleF source, RectangleF destination, Color blend, float alpha)
            => DrawSpriteRectangle(spriteName, destination, frame, blend, alpha);

        /// <summary>
        /// Many filled rectangles in order (PGSL DrawRectanglesFromList). Surfaces that keep batches
        /// send them on as one; the default draws each with <see cref="FillRectangle"/>.
        /// </summary>
        void FillRectangles(ReadOnlySpan<GuiRectangle> rectangles)
        {
            foreach (GuiRectangle r in rectangles)
            {
                Vector4 c = Vector4.Clamp(r.Color, Vector4.Zero, Vector4.One) * 255f;
                FillRectangle(Color.FromArgb((int)MathF.Round(c.W), (int)MathF.Round(c.X), (int)MathF.Round(c.Y), (int)MathF.Round(c.Z)),
                    new RectangleF(r.X, r.Y, r.Width, r.Height));
            }
        }

        /// <summary>
        /// Many parts of one image in order (PGSL DrawSpritePartsFromList), each as
        /// <see cref="DrawSpritePart"/> would draw it.
        /// </summary>
        void DrawSpriteParts(string spriteName, ReadOnlySpan<GuiSpritePart> parts, Color blend)
        {
            foreach (GuiSpritePart p in parts)
                DrawSpritePart(spriteName, p.Frame, RectangleF.FromLTRB(p.U0, p.V0, p.U1, p.V1),
                    new RectangleF(p.X, p.Y, p.Width, p.Height), blend, p.Alpha);
        }

        /// <summary>
        /// A texture a script paints (PGSL TextureCreate) drawn like an image: <paramref name="source"/>
        /// in fractions of the texture into <paramref name="destination"/>, turned by
        /// <paramref name="angle"/> degrees about the destination's top-left corner. Surfaces that
        /// cannot draw one ignore it.
        /// </summary>
        void DrawScriptTexture(int texture, RectangleF source, RectangleF destination, float angle, Color blend, float alpha) { }

        /// <summary>
        /// A Model drawn into a GUI rectangle, turned by yaw and pitch (degrees) and framed to fit
        /// (zoom 1), optionally posed at a clip's time. Drawn in GUI order like an image.
        /// </summary>
        void DrawModelGui(string modelName, RectangleF destination, float yaw, float pitch, float zoom, string clip, float time, float alpha) { }

        /// <summary>
        /// A script-built mesh (PGSL MeshCreate) drawn in a 3D room, textured by an Image, optionally
        /// through a mesh Shader resource, with the script's mesh draw options.
        /// </summary>
        void QueueScriptMesh3D(int meshId, System.Numerics.Matrix4x4 world, string image, string shader, Color tint, float alpha,
            ScriptMeshDrawOptions options) { }

        bool Is3DActive { get; }
        void QueueCube3D(float x, float y, float z, float sx, float sy, float sz, Color color, float alpha);
        void QueueSphere3D(float x, float y, float z, float radius, Color color, float alpha);
        void QueueModel3D(string modelName, float x, float y, float z, float scale, Color color, float alpha);

        /// <summary>
        /// Draw a real model resource with a complete transform and optional authored mesh shader.
        /// The default keeps older recording surfaces source-compatible while live render surfaces
        /// use the full resource path.
        /// </summary>
        void QueueModelTransform3D(
            string modelName,
            string shaderName,
            float x, float y, float z,
            float sx, float sy, float sz,
            float xrot, float yrot, float zrot,
            Color color,
            float alpha) => QueueModel3D(modelName, x, y, z, MathF.Max(MathF.Abs(sx), MathF.Max(MathF.Abs(sy), MathF.Abs(sz))), color, alpha);

        /// <summary>Add an omnidirectional light during the active 3D draw pass.</summary>
        void QueuePointLight3D(
            float x, float y, float z,
            float radius,
            float intensity,
            Color color,
            float falloff = 2f) { }

        /// <summary>Projects a world point through the active camera and draws a crisp overlay label.</summary>
        void DrawText3D(
            string text,
            float x, float y, float z,
            string font,
            float size,
            Color color,
            Matrix4x4 view,
            Matrix4x4 projection) { }
    }
}
