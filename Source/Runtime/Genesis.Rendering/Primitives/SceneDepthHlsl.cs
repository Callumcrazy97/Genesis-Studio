namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// HLSL every shader that reads the scene's depth buffer shares, so that each one works
    /// whichever way round depth is stored.
    /// </summary>
    /// <remarks>
    /// A shader defines <c>GENESIS_DEPTH_REVERSED</c> as an expression over its own constants
    /// (true when the scene stores 1 at the near plane and 0 at the far plane) and then includes
    /// <see cref="Helpers"/>. World positions need no helper: a shader is given the inverse of
    /// the matrix the scene was drawn with, so the stored depth goes straight into it.
    /// </remarks>
    public static class SceneDepthHlsl
    {
        public const string Helpers = @"
// Scene depth as it is stored: 0 near and 1 far, or the other way round when depth is reversed.
float SceneDepthFar() { return GENESIS_DEPTH_REVERSED ? 0.0 : 1.0; }
float SceneDepthNear() { return GENESIS_DEPTH_REVERSED ? 1.0 : 0.0; }
// Nothing was drawn here: the buffer still holds the value it was cleared to.
bool SceneDepthIsSky(float d) { return GENESIS_DEPTH_REVERSED ? d <= 0.0 : d >= 0.9999999; }
// How far depth a lies behind depth b, in stored units; negative when a is in front of b.
float SceneDepthBehind(float a, float b) { return GENESIS_DEPTH_REVERSED ? b - a : a - b; }
// Of two stored depths, the one nearer the camera.
float SceneDepthNearest(float a, float b) { return GENESIS_DEPTH_REVERSED ? max(a, b) : min(a, b); }
// Distance along the view axis, from a stored depth and the camera's near and far planes.
float SceneDepthToView(float d, float n, float f)
{
    return GENESIS_DEPTH_REVERSED
        ? n * f / max(n + d * (f - n), 1e-12)
        : n * f / max(f - d * (f - n), 1e-12);
}
// A value proportional to one over the view distance, straight from the stored depth: flat
// surfaces are straight lines in it, which is what an edge detector wants.
float SceneDepthInverse(float d, float n, float f)
{
    return GENESIS_DEPTH_REVERSED ? d + n / max(f - n, 1e-4) : f / max(f - n, 1e-4) - d;
}
";
    }
}
