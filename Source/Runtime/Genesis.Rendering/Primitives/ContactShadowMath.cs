using System;
using System.Numerics;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.4 CPU reference for short-range screen-space contact shadows (the ForestLight
/// <c>screenContactShadow</c> march). Pure math for headless gates — no GPU devices.
/// </summary>
/// <remarks>
/// The march runs in the same synthetic view space <see cref="GtaoMath"/> uses (positive Z into
/// the scene, 60° vertical FOV) and compares depths in depth-buffer units, which is what makes the
/// ForestLight thickness constants meaningful. The result is an *occlusion* term: 0 means nothing
/// blocked the ray to the light, higher means more contact shadow. That is the opposite polarity
/// to <see cref="GtaoMath.EvaluateHorizonContact"/>, which returns a lit multiplier — the two are
/// composited differently (AO darkens ambient, this darkens only the sun term) and must never be
/// multiplied together as if both were ambient occlusion.
/// </remarks>
public static class ContactShadowMath
{
    public const int StepCount = 12;
    public const float DefaultMaxDistance = 2.4f;
    public const float DefaultStrength = 0.85f;

    /// <summary>
    /// Strength used when GTAO is also on. GTAO already darkens the same creases, so the contact
    /// term is pulled back rather than stacked at full force.
    /// </summary>
    public const float StrengthWithGtao = 0.70f;

    /// <summary>ForestLight clamps the march so a contact shadow never reaches pure black.</summary>
    public const float MaxOcclusion = 0.92f;

    /// <summary>Ray origin offset along the surface normal, in world units.</summary>
    public const float NormalBias = 0.05f;

    /// <summary>Composite strength for the sun-gated multiply in the fog post pass.</summary>
    public static float EffectiveStrength(bool gtaoEnabled) =>
        gtaoEnabled ? StrengthWithGtao : DefaultStrength;

    /// <summary>
    /// Converts the engine's sun direction (<c>Mesh3DState.LightDirection</c> / <c>EngineCB
    /// LightDirEnabled.xyz</c>, which stores the direction light *travels*) into the unit vector
    /// the march has to follow — toward the light, matching ForestLight's <c>directionalL</c>.
    /// </summary>
    public static Vector3 DirectionTowardLight(Vector3 lightTravelDirection)
    {
        Vector3 toLight = -lightTravelDirection;
        float length = toLight.Length();
        return length < 1e-8f ? new Vector3(0f, 1f, 0f) : toLight / length;
    }

    /// <summary>March parameter for one step; steps are spaced quadratically via t².</summary>
    public static float StepParameter(int stepIndex) =>
        (Math.Clamp(stepIndex, 0, StepCount - 1) + 0.65f) / StepCount;

    /// <summary>Depth-buffer-unit tolerance treated as "the occluder is this thick" at t.</summary>
    public static float SampleThickness(float t) => 0.00045f + t * 0.00145f;

    /// <summary>
    /// Marches <see cref="StepCount"/> samples from the pixel toward the light and returns the
    /// contact occlusion in 0..<see cref="MaxOcclusion"/>. Sky pixels return 0.
    /// </summary>
    /// <param name="lightTravelDirection">Engine convention: the direction light travels.</param>
    public static float EvaluateOcclusion(
        ReadOnlySpan<float> depthGrid,
        int width,
        int height,
        int centerX,
        int centerY,
        float nearPlane,
        float farPlane,
        Vector3 lightTravelDirection,
        float maxDistance = DefaultMaxDistance)
    {
        if (width < 2 || height < 2 || depthGrid.Length < width * height)
            return 0f;

        centerX = Math.Clamp(centerX, 0, width - 1);
        centerY = Math.Clamp(centerY, 0, height - 1);
        float centerDepth = depthGrid[centerY * width + centerX];
        if (centerDepth >= 0.99999f)
            return 0f;

        float near = MathF.Max(0.01f, nearPlane);
        float far = MathF.Max(near + 1f, farPlane);
        float tanHalf = MathF.Tan(MathF.PI / 6f); // ~60° vertical FOV, as in GtaoMath
        float aspect = width / (float)height;

        float viewZ = LinearizeDepth(centerDepth, near, far);
        Vector3 center = ViewPos(centerX, centerY, viewZ, width, height, tanHalf, aspect);
        Vector3 alongX = ViewPos(
            centerX + 1, centerY,
            LinearizeDepth(SampleDepth(depthGrid, width, height, centerX + 1, centerY), near, far),
            width, height, tanHalf, aspect);
        Vector3 alongY = ViewPos(
            centerX, centerY + 1,
            LinearizeDepth(SampleDepth(depthGrid, width, height, centerX, centerY + 1), near, far),
            width, height, tanHalf, aspect);

        Vector3 normal = Normalize(Vector3.Cross(alongX - center, alongY - center));
        // View space runs +Z into the scene, so a camera-facing normal has negative Z. Depth-
        // derived normals flip sign on silhouettes; forcing the sign keeps the bias from pushing
        // the ray origin *into* the surface, which would self-occlude every pixel.
        if (normal.Z > 0f)
            normal = -normal;

        Vector3 origin = center + normal * NormalBias;
        Vector3 toLight = DirectionTowardLight(lightTravelDirection);
        float rayLength = MathF.Max(maxDistance, 0f);
        float occlusion = 0f;

        for (int stepIndex = 0; stepIndex < StepCount; stepIndex++)
        {
            float t = (stepIndex + 0.65f) / StepCount;
            Vector3 samplePos = origin + toLight * (rayLength * t * t);
            if (samplePos.Z <= near)
                continue;

            if (!ProjectToPixel(samplePos, width, height, tanHalf, aspect, out int sx, out int sy))
                continue;

            float sceneDepth = MinNeighbourhoodDepth(depthGrid, width, height, sx, sy);
            if (sceneDepth >= 0.99999f)
                continue;

            float rayDepth = DelinearizeDepth(samplePos.Z, near, far);
            float separation = rayDepth - sceneDepth;
            float thickness = SampleThickness(t);
            float hit = SmoothStep(thickness, thickness * 3.2f, separation);
            occlusion = MathF.Max(occlusion, hit * (1f - t * 0.58f));
        }

        return Math.Clamp(occlusion, 0f, MaxOcclusion);
    }

    /// <summary>Depth-buffer value to positive view-space Z.</summary>
    public static float LinearizeDepth(float depth01, float nearPlane, float farPlane)
    {
        float z = depth01 * 2f - 1f;
        float n = MathF.Max(0.01f, nearPlane);
        float f = MathF.Max(n + 1f, farPlane);
        return (2f * n * f) / MathF.Max(f + n - z * (f - n), 1e-5f);
    }

    /// <summary>
    /// Inverse of <see cref="LinearizeDepth"/>. The march advances in world units but compares in
    /// depth-buffer units, so every sample position has to be pushed back through the curve.
    /// </summary>
    public static float DelinearizeDepth(float viewZ, float nearPlane, float farPlane)
    {
        float n = MathF.Max(0.01f, nearPlane);
        float f = MathF.Max(n + 1f, farPlane);
        float z = MathF.Max(viewZ, 1e-5f);
        float ndc = (f + n - (2f * n * f) / z) / (f - n);
        return Math.Clamp(ndc * 0.5f + 0.5f, 0f, 1f);
    }

    private static bool ProjectToPixel(
        Vector3 viewPos, int width, int height, float tanHalf, float aspect, out int x, out int y)
    {
        x = 0;
        y = 0;
        float denom = MathF.Max(viewPos.Z * tanHalf, 1e-5f);
        float ndcX = viewPos.X / MathF.Max(denom * aspect, 1e-5f);
        float ndcY = viewPos.Y / denom;
        float u = ndcX * 0.5f + 0.5f;
        float v = 0.5f - ndcY * 0.5f;
        if (u < 0f || u > 1f || v < 0f || v > 1f)
            return false;

        x = Math.Clamp((int)MathF.Round(u * width - 0.5f), 0, width - 1);
        y = Math.Clamp((int)MathF.Round(v * height - 0.5f), 0, height - 1);
        return true;
    }

    /// <summary>
    /// Nearest of the tap and its four cross neighbours. A single tap makes the march miss thin
    /// occluders that fall between half-res pixels; taking the minimum biases toward "something
    /// is in front here", which is the stable choice for a short-range shadow.
    /// </summary>
    private static float MinNeighbourhoodDepth(
        ReadOnlySpan<float> grid, int width, int height, int x, int y)
    {
        float min = SampleDepth(grid, width, height, x, y);
        min = MathF.Min(min, SampleDepth(grid, width, height, x - 1, y));
        min = MathF.Min(min, SampleDepth(grid, width, height, x + 1, y));
        min = MathF.Min(min, SampleDepth(grid, width, height, x, y - 1));
        min = MathF.Min(min, SampleDepth(grid, width, height, x, y + 1));
        return min;
    }

    private static Vector3 ViewPos(
        int x, int y, float viewZ, int width, int height, float tanHalf, float aspect)
    {
        float u = (x + 0.5f) / width;
        float v = (y + 0.5f) / height;
        float ndcX = u * 2f - 1f;
        float ndcY = 1f - v * 2f;
        return new Vector3(ndcX * viewZ * tanHalf * aspect, ndcY * viewZ * tanHalf, viewZ);
    }

    private static float SampleDepth(ReadOnlySpan<float> grid, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        return grid[y * width + x];
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(1e-7f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static Vector3 Normalize(Vector3 v)
    {
        float length = v.Length();
        return length < 1e-8f ? new Vector3(0f, 0f, -1f) : v / length;
    }
}
