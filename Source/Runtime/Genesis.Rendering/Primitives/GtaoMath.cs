using System;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// AF1.2 CPU reference for horizon/contact AO and depth-aware bilateral weights.
/// Pure math for headless gates — no GPU devices.
/// </summary>
public static class GtaoMath
{
    public const int DirectionCount = 8;
    public const int StepCount = 4;

    /// <summary>
    /// Synthetic view-space sample: positive Z is into the scene (LH clip depth mapped to Z).
    /// Returns occlusion in 0..1 where 1 = fully lit (no AO).
    /// </summary>
    public static float EvaluateHorizonContact(
        ReadOnlySpan<float> depthGrid,
        int width,
        int height,
        int centerX,
        int centerY,
        float nearPlane,
        float farPlane)
    {
        if (width < 2 || height < 2 || depthGrid.Length < width * height)
            return 1f;

        centerX = Math.Clamp(centerX, 0, width - 1);
        centerY = Math.Clamp(centerY, 0, height - 1);
        float centerDepth = depthGrid[centerY * width + centerX];
        if (centerDepth >= 0.99999f)
            return 1f;

        float viewZ = LinearizeDepth(centerDepth, nearPlane, farPlane);
        float tanHalf = MathF.Tan(MathF.PI / 6f); // ~60° vertical FOV
        float aspect = width / (float)height;
        Float3 center = ViewPos(centerX, centerY, viewZ, width, height, tanHalf, aspect);

        // Approximate view-space normal from neighbouring view positions.
        Float3 px = ViewPos(centerX + 1, centerY,
            LinearizeDepth(SampleDepth(depthGrid, width, height, centerX + 1, centerY), nearPlane, farPlane),
            width, height, tanHalf, aspect);
        Float3 py = ViewPos(centerX, centerY + 1,
            LinearizeDepth(SampleDepth(depthGrid, width, height, centerX, centerY + 1), nearPlane, farPlane),
            width, height, tanHalf, aspect);
        var normal = Normalize(Cross(Sub(py, center), Sub(px, center)));

        float projectedRadius = Math.Clamp(28f / MathF.Sqrt(viewZ + 0.35f), 3f, 52f);
        float horizonSum = 0f;
        float contactSum = 0f;

        for (int directionIndex = 0; directionIndex < DirectionCount; directionIndex++)
        {
            float angle = (directionIndex + 0.5f) * (MathF.PI * 2f / DirectionCount);
            float dirX = MathF.Cos(angle);
            float dirY = MathF.Sin(angle);
            float horizon = 0f;
            float directionalContact = 0f;

            for (int stepIndex = 1; stepIndex <= StepCount; stepIndex++)
            {
                float step01 = stepIndex / (float)StepCount;
                float pixelRadius = projectedRadius * (0.20f + 0.80f * step01 * step01);
                int sx = (int)MathF.Round(centerX + dirX * pixelRadius);
                int sy = (int)MathF.Round(centerY + dirY * pixelRadius);
                if (sx <= 0 || sx >= width - 1 || sy <= 0 || sy >= height - 1)
                    continue;

                float sampleDepth = depthGrid[sy * width + sx];
                if (sampleDepth >= 0.99999f)
                    continue;

                float sampleZ = LinearizeDepth(sampleDepth, nearPlane, farPlane);
                Float3 samplePos = ViewPos(sx, sy, sampleZ, width, height, tanHalf, aspect);
                Float3 delta = Sub(samplePos, center);
                float distanceSquared = Dot(delta, delta);
                if (distanceSquared < 0.000004f || distanceSquared > 36f)
                    continue;

                float invDist = 1f / MathF.Sqrt(distanceSquared);
                float distanceToSample = distanceSquared * invDist;
                var sampleDir = Normalize(delta);
                float normalHorizon = MathF.Max(Dot(normal, sampleDir) - 0.045f, 0f);
                float falloff = 1f - SmoothStep(0.30f, 6f, distanceToSample);
                horizon = MathF.Max(horizon, normalHorizon * falloff);

                if (stepIndex <= 2)
                {
                    float contactFalloff = 1f - SmoothStep(0.035f, 1.10f, distanceToSample);
                    directionalContact = MathF.Max(directionalContact, normalHorizon * contactFalloff);
                }
            }

            horizonSum += horizon;
            contactSum += directionalContact;
        }

        float horizonOcclusion = horizonSum / DirectionCount;
        float contactOcclusion = contactSum / DirectionCount;
        float distanceFade = 1f - SmoothStep(70f, 150f, viewZ);
        float ao = 1f - (horizonOcclusion * 1.55f + contactOcclusion * 0.95f) * distanceFade;
        ao = Math.Clamp(ao, 0.055f, 1f);
        return MathF.Pow(ao, 1.18f);
    }

    /// <summary>Depth-edge weight used by the bilateral blur (no normal map required).</summary>
    public static float BilateralDepthWeight(float centerLinearDepth, float sampleLinearDepth)
        => MathF.Exp(-MathF.Abs(sampleLinearDepth - centerLinearDepth) * 2.8f);

    public static float LinearizeDepth(float depth01, float nearPlane, float farPlane)
    {
        float z = depth01 * 2f - 1f;
        float n = MathF.Max(0.01f, nearPlane);
        float f = MathF.Max(n + 1f, farPlane);
        return (2f * n * f) / MathF.Max(f + n - z * (f - n), 1e-5f);
    }

    private static Float3 ViewPos(
        int x, int y, float viewZ, int width, int height, float tanHalf, float aspect)
    {
        float u = (x + 0.5f) / width;
        float v = (y + 0.5f) / height;
        float ndcX = u * 2f - 1f;
        float ndcY = 1f - v * 2f;
        return new Float3(ndcX * viewZ * tanHalf * aspect, ndcY * viewZ * tanHalf, viewZ);
    }

    private static float SampleDepth(ReadOnlySpan<float> grid, int w, int h, int x, int y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        return grid[y * w + x];
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(1e-5f, edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private readonly struct Float3
    {
        public readonly float X, Y, Z;
        public Float3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    private static Float3 Normalize(Float3 v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (len < 1e-8f) return new Float3(0f, 0f, 1f);
        return new Float3(v.X / len, v.Y / len, v.Z / len);
    }

    private static Float3 Sub(Float3 a, Float3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static Float3 Cross(Float3 a, Float3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static float Dot(Float3 a, Float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
