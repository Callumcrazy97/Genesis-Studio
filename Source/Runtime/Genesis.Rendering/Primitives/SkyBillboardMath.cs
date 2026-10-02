using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives
{
    /// <summary>Where the sun's quad goes so that it is a round disc of one size wherever it is on screen.</summary>
    public static class SkyBillboardMath
    {
        /// <summary>
        /// The world matrix for a unit quad that shows the sun.
        /// </summary>
        /// <remarks>
        /// A flat picture stretches what is far from its centre: a round thing at an angle from
        /// the view direction is drawn longer along the line out from the centre (by one over the
        /// cosine squared) and wider across it (by one over the cosine). A quad that simply faces
        /// the camera therefore shows the sun as an ellipse near the edge of a wide view. This
        /// shrinks the quad by the same amounts, so the disc stays round and the same size.
        /// </remarks>
        /// <param name="towardSun">Unit vector from the camera to the sun.</param>
        /// <param name="cameraForward">The way the camera looks.</param>
        /// <param name="size">The quad's width and height when the sun is in the middle of the view.</param>
        public static Matrix4x4 SunQuad(Vector3 cameraPosition, Vector3 cameraForward, Vector3 towardSun, float distance, float size)
        {
            Vector3 toSun = towardSun.LengthSquared() > 1e-8f ? Vector3.Normalize(towardSun) : Vector3.UnitY;
            Vector3 forward = cameraForward.LengthSquared() > 1e-8f ? Vector3.Normalize(cameraForward) : toSun;
            float cosine = Vector3.Dot(toSun, forward);
            // The line on the quad that runs out from the middle of the view.
            Vector3 outward = forward - toSun * cosine;
            if (outward.LengthSquared() < 1e-8f)
            {
                // Dead centre: nothing to correct, and any pair of axes will do.
                outward = Vector3.Cross(toSun, MathF.Abs(toSun.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
                cosine = 1f;
            }

            outward = Vector3.Normalize(outward);
            Vector3 across = Vector3.Normalize(Vector3.Cross(toSun, outward));
            // Behind or far to the side the correction would shrink the sun to nothing; it is off screen there anyway.
            float shrink = Math.Clamp(cosine, 0.2f, 1f);
            Vector3 x = across * (size * shrink);
            Vector3 y = outward * (size * shrink * shrink);
            Vector3 z = -toSun;
            Vector3 position = cameraPosition + toSun * distance;
            return new Matrix4x4(
                x.X, x.Y, x.Z, 0f,
                y.X, y.Y, y.Z, 0f,
                z.X, z.Y, z.Z, 0f,
                position.X, position.Y, position.Z, 1f);
        }
    }
}
