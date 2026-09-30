using System;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Ink outline: an optional term of the final composite (see FogPostShaders.InkOutline). It has
    // no pass or target of its own — the composite already reads scene depth — so all the renderer
    // does is pack InkOutlineSettings into the composite's constants.
    internal sealed partial class ForwardRenderer
    {
        /// <summary>Reference height the authored line width is expressed against.</summary>
        private const float InkReferenceHeight = 1080f;

        private void PackInkOutline(ref FogPostCB constants, int viewHeight)
        {
            // Left at zero the shader skips the whole block, which is the default for every project.
            if (!InkOutlineSettings.Enabled || InkOutlineSettings.Opacity <= 0f || viewHeight <= 0)
                return;

            // The test measures 1 / view depth, which only a perspective projection provides.
            bool perspective = MathF.Abs(_proj.M34) > 1e-6f && MathF.Abs(_proj.M22) > 1e-6f;
            if (!perspective)
                return;

            float width = InkOutlineSettings.WidthPixels * (viewHeight / InkReferenceHeight);
            // Three tap rings cover three pixels; wider lines space the rings out instead.
            int tapStep = Math.Max(1, (int)MathF.Ceiling(width / 3f));
            float radiansPerPixel = 2f / (MathF.Abs(_proj.M22) * viewHeight);

            constants.InkParams = new Vector4(
                InkOutlineSettings.Opacity,
                MathF.Max(width, 0.5f),
                InkOutlineSettings.DepthStep,
                InkOutlineSettings.CreaseAngleDegrees * (MathF.PI / 180f));
            constants.InkColor = new Vector4(InkOutlineSettings.Color, radiansPerPixel);
            constants.InkFade = new Vector4(
                InkOutlineSettings.FullWidthDistance,
                InkOutlineSettings.FarDistance,
                InkOutlineSettings.FarOpacity,
                tapStep);
        }
    }
}
