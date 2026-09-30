using System;
using System.Numerics;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// Optional ink outline drawn by the final composite, for illustrated and cel-shaded projects.
    /// Off by default, so existing projects and golden images are unchanged. Like the other
    /// post-processing switches it is process-wide: a game turns it on once, from PGSL
    /// (<c>Engine.Rendering.InkOutline*</c>) or from a C# behaviour.
    /// </summary>
    /// <remarks>
    /// The lines come from scene depth alone (there is no normal buffer). Device depth is affine
    /// across a plane in screen space, so its second difference is zero on every flat surface at
    /// any viewing angle and non-zero only where the surface steps (a silhouette) or bends (a
    /// crease). Surfaces that do not write depth — particles, alpha-blended materials — are never
    /// outlined.
    /// </remarks>
    public static class InkOutlineSettings
    {
        public const float DefaultWidthPixels = 2f;
        public const float DefaultCreaseAngleDegrees = 32f;
        public const float DefaultDepthStep = 0.012f;
        public const float DefaultFullWidthDistance = 6f;
        public const float DefaultFarDistance = 40f;
        public const float DefaultFarOpacity = 0.6f;

        public static bool Enabled { get; private set; }

        /// <summary>Line width in pixels at 1080p; scaled with the viewport height.</summary>
        public static float WidthPixels { get; private set; } = DefaultWidthPixels;

        /// <summary>0 draws nothing, 1 draws the ink colour at full strength.</summary>
        public static float Opacity { get; private set; } = 1f;

        /// <summary>Display-space ink colour (not lit, not tonemapped).</summary>
        public static Vector3 Color { get; private set; } = new Vector3(0.10f, 0.06f, 0.04f);

        /// <summary>Creases sharper than this are inked; shallower bends are left clean.</summary>
        public static float CreaseAngleDegrees { get; private set; } = DefaultCreaseAngleDegrees;

        /// <summary>Relative depth jump that counts as a silhouette (0.012 = a 1.2% step).</summary>
        public static float DepthStep { get; private set; } = DefaultDepthStep;

        /// <summary>Lines keep their full width up to this view distance, in metres.</summary>
        public static float FullWidthDistance { get; private set; } = DefaultFullWidthDistance;

        /// <summary>Lines have thinned to one pixel and to <see cref="FarOpacity"/> by this distance.</summary>
        public static float FarDistance { get; private set; } = DefaultFarDistance;

        /// <summary>Opacity multiplier reached at <see cref="FarDistance"/>.</summary>
        public static float FarOpacity { get; private set; } = DefaultFarOpacity;

        public static void Configure(
            bool enabled,
            float widthPixels = DefaultWidthPixels,
            float opacity = 1f,
            float creaseAngleDegrees = DefaultCreaseAngleDegrees,
            float depthStep = DefaultDepthStep,
            float fullWidthDistance = DefaultFullWidthDistance,
            float farDistance = DefaultFarDistance,
            float farOpacity = DefaultFarOpacity)
        {
            Enabled = enabled;
            WidthPixels = Math.Clamp(Finite(widthPixels, DefaultWidthPixels), 0.5f, 6f);
            Opacity = Math.Clamp(Finite(opacity, 1f), 0f, 1f);
            CreaseAngleDegrees = Math.Clamp(Finite(creaseAngleDegrees, DefaultCreaseAngleDegrees), 2f, 89f);
            DepthStep = Math.Clamp(Finite(depthStep, DefaultDepthStep), 0.0005f, 0.5f);
            FullWidthDistance = Math.Max(0f, Finite(fullWidthDistance, DefaultFullWidthDistance));
            FarDistance = Math.Max(FullWidthDistance + 0.01f, Finite(farDistance, DefaultFarDistance));
            FarOpacity = Math.Clamp(Finite(farOpacity, DefaultFarOpacity), 0f, 1f);
        }

        public static void SetColor(float r, float g, float b)
        {
            Color = new Vector3(
                Math.Clamp(Finite(r, 0f), 0f, 1f),
                Math.Clamp(Finite(g, 0f), 0f, 1f),
                Math.Clamp(Finite(b, 0f), 0f, 1f));
        }

        /// <summary>Restores the shipped defaults (outline off).</summary>
        public static void Reset()
        {
            Configure(false);
            Color = new Vector3(0.10f, 0.06f, 0.04f);
        }

        private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
    }
}
