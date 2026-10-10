using System.Numerics;

namespace Genesis.Shared.Interfaces
{
    /// <summary>How a decal's picture goes over the surface under it.</summary>
    public enum DecalBlend : byte
    {
        /// <summary>
        /// Multiplies the lit surface by the picture: it darkens and tints and keeps the surface's
        /// own light and shade (bullet holes, scorch marks, blood, dirt). White changes nothing.
        /// </summary>
        Multiply = 0,

        /// <summary>Paints the picture's own colours over the surface, unlit (signs, paint, graffiti).</summary>
        Alpha = 1,

        /// <summary>Adds the picture's colours as light (embers, hot metal, glowing marks).</summary>
        Additive = 2,
    }

    /// <summary>
    /// One projected decal for one frame (<see cref="IRenderController.DrawDecals"/>): a box around
    /// a point on a surface, <see cref="Width"/> across the picture, <see cref="Height"/> up it and
    /// <see cref="Depth"/> through the surface, whose picture lands on whatever opaque surface lies
    /// inside it.
    /// </summary>
    public struct DecalDrawCall
    {
        /// <summary>The picture; an invalid handle paints a plain rectangle of <see cref="Color"/>.</summary>
        public TextureHandle Texture;

        /// <summary>The middle of the decal, on the surface.</summary>
        public Vector3 Position;

        /// <summary>Out of the surface, towards where the decal is seen from.</summary>
        public Vector3 Normal;

        /// <summary>Across the picture, left to right; made perpendicular to <see cref="Normal"/>.</summary>
        public Vector3 Tangent;

        public float Width;
        public float Height;
        public float Depth;

        /// <summary>Tint (red, green, blue 0-1, as picked) and opacity (0-1, fading included).</summary>
        public Vector4 Color;

        public DecalBlend Blend;
    }
}
