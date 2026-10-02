namespace Genesis.Shared.Rendering
{
    /// <summary>
    /// How the scene's depth buffer is stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A depth buffer stores far more distinct values close to the camera than far from it. On a
    /// view of kilometres, with a near plane of centimetres, distant ground, water and shore run
    /// out of values and flicker through each other. Reversed depth stores 1 at the near plane
    /// and 0 at the far plane in a floating-point buffer: floating point has most of its values
    /// near zero, which is now the far distance, and the two effects cancel. A near plane of ten
    /// centimetres then works at any view distance.
    /// </para>
    /// <para>
    /// Shadow maps keep the usual order. The software renderer keeps it too.
    /// </para>
    /// </remarks>
    public static class DepthPrecision
    {
        /// <summary>
        /// The choice: Preferences, <c>GENESIS_REVERSED_DEPTH</c> or
        /// <c>Engine.Rendering.ReversedDepth</c>. On unless turned off. A renderer picks a change
        /// up at its next frame.
        /// </summary>
        public static bool ReversedDepthRequested { get; set; } = true;

        /// <summary>
        /// True while the renderer drawing the scene stores depth reversed. Cameras read this to
        /// decide whether a long view must move its near plane out.
        /// </summary>
        public static bool ReversedDepthInUse { get; set; } = true;
    }
}
