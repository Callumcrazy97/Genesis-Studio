using System;
using System.Drawing;
using System.Numerics;

namespace Genesis.Shared.Interfaces
{
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
        void DrawSprite(string spriteName, float x, float y, int frame, float xscale, float yscale, float angle, Color blend, float alpha);
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
