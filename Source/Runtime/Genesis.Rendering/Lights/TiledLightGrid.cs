using System;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Lights;

/// <summary>
/// CPU tiled light assignment for R7.5. Scene lights live in a structured buffer; each screen
/// tile stores up to <see cref="MaxLightsPerTile"/> indices so a pixel evaluates a bounded set.
/// </summary>
public static class TiledLightDefaults
{
    public const int TileGridSize = 16;
    public const int MaxLightsPerTile = 32;
    public const int TileCount = TileGridSize * TileGridSize;
    public const uint EmptyLightIndex = 0xFFFFFFFFu;
}

/// <summary>One GPU light record (3×float4 = 48 bytes), matching HLSL ClusterPointLight.</summary>
public struct ClusterPointLightGpu
{
    public Vector4 PosRadius;
    public Vector4 ColorIntensity;
    public Vector4 FalloffPad;
}

/// <summary>Builds per-tile index lists from world-space point lights for the current frame.</summary>
public sealed class TiledLightGrid
{
    private readonly uint[] _indices = new uint[TiledLightDefaults.TileCount * TiledLightDefaults.MaxLightsPerTile];
    private readonly int[] _counts = new int[TiledLightDefaults.TileCount];
    private readonly float[] _weakest = new float[TiledLightDefaults.TileCount];

    public ReadOnlySpan<uint> Indices => _indices;
    public int TileGridSize => TiledLightDefaults.TileGridSize;
    public int MaxLightsPerTile => TiledLightDefaults.MaxLightsPerTile;

    public void Clear()
    {
        Array.Fill(_indices, TiledLightDefaults.EmptyLightIndex);
        Array.Clear(_counts);
        Array.Clear(_weakest);
    }

    /// <summary>
    /// Assigns each light into overlapping screen tiles using a conservative sphere projection.
    /// When a tile is full, keeps the lights closest to the camera.
    /// </summary>
    public void Build(
        ReadOnlySpan<ClusterPointLightGpu> lights,
        in Matrix4x4 viewProjection,
        int viewportWidth,
        int viewportHeight,
        Vector3 cameraPos)
    {
        Clear();
        if (lights.Length == 0 || viewportWidth <= 0 || viewportHeight <= 0)
            return;

        float invW = 1f / viewportWidth;
        float invH = 1f / viewportHeight;
        int grid = TiledLightDefaults.TileGridSize;
        int maxPer = TiledLightDefaults.MaxLightsPerTile;

        for (int lightIndex = 0; lightIndex < lights.Length; lightIndex++)
        {
            ref readonly ClusterPointLightGpu light = ref lights[lightIndex];
            Vector3 pos = new(light.PosRadius.X, light.PosRadius.Y, light.PosRadius.Z);
            float radius = MathF.Max(light.PosRadius.W, 0.001f);
            float camWeight = 1f / (1f + Vector3.DistanceSquared(cameraPos, pos));

            if (!TryProjectSphere(pos, radius, viewProjection, out float minX, out float minY, out float maxX, out float maxY))
                continue;

            // Expand by one texel of screen space so borderline spheres still hit edge tiles.
            minX = Math.Clamp(minX - invW, 0f, 1f);
            minY = Math.Clamp(minY - invH, 0f, 1f);
            maxX = Math.Clamp(maxX + invW, 0f, 1f);
            maxY = Math.Clamp(maxY + invH, 0f, 1f);

            int x0 = Math.Clamp((int)MathF.Floor(minX * grid), 0, grid - 1);
            int y0 = Math.Clamp((int)MathF.Floor(minY * grid), 0, grid - 1);
            int x1 = Math.Clamp((int)MathF.Floor(maxX * grid), 0, grid - 1);
            int y1 = Math.Clamp((int)MathF.Floor(maxY * grid), 0, grid - 1);

            for (int ty = y0; ty <= y1; ty++)
            {
                for (int tx = x0; tx <= x1; tx++)
                {
                    int tile = ty * grid + tx;
                    int count = _counts[tile];
                    int baseIndex = tile * maxPer;
                    if (count < maxPer)
                    {
                        _indices[baseIndex + count] = (uint)lightIndex;
                        _counts[tile] = count + 1;
                        if (count == 0 || camWeight < _weakest[tile])
                            _weakest[tile] = camWeight;
                        continue;
                    }

                    if (camWeight <= _weakest[tile])
                        continue;

                    // Replace the weakest entry in this tile.
                    int weakestSlot = 0;
                    float weakestWeight = float.MaxValue;
                    for (int slot = 0; slot < maxPer; slot++)
                    {
                        uint existing = _indices[baseIndex + slot];
                        if (existing >= (uint)lights.Length)
                            continue;
                        Vector3 ePos = new(
                            lights[(int)existing].PosRadius.X,
                            lights[(int)existing].PosRadius.Y,
                            lights[(int)existing].PosRadius.Z);
                        float w = 1f / (1f + Vector3.DistanceSquared(cameraPos, ePos));
                        if (w < weakestWeight)
                        {
                            weakestWeight = w;
                            weakestSlot = slot;
                        }
                    }

                    _indices[baseIndex + weakestSlot] = (uint)lightIndex;
                    _weakest[tile] = camWeight;
                }
            }
        }
    }

    private static bool TryProjectSphere(
        Vector3 center,
        float radius,
        in Matrix4x4 viewProjection,
        out float minX,
        out float minY,
        out float maxX,
        out float maxY)
    {
        minX = minY = 0f;
        maxX = maxY = 1f;

        // Sample the sphere AABB corners in clip space and take the screen AABB of those that
        // survive the near plane. Conservative and CPU-cheap for DX11 tile lists.
        Span<Vector3> corners = stackalloc Vector3[8];
        corners[0] = center + new Vector3(-radius, -radius, -radius);
        corners[1] = center + new Vector3(-radius, -radius, radius);
        corners[2] = center + new Vector3(-radius, radius, -radius);
        corners[3] = center + new Vector3(-radius, radius, radius);
        corners[4] = center + new Vector3(radius, -radius, -radius);
        corners[5] = center + new Vector3(radius, -radius, radius);
        corners[6] = center + new Vector3(radius, radius, -radius);
        corners[7] = center + new Vector3(radius, radius, radius);

        float xMin = float.PositiveInfinity, yMin = float.PositiveInfinity;
        float xMax = float.NegativeInfinity, yMax = float.NegativeInfinity;
        int accepted = 0;
        for (int i = 0; i < corners.Length; i++)
        {
            Vector4 clip = Vector4.Transform(new Vector4(corners[i], 1f), viewProjection);
            if (clip.W <= 1e-4f)
                continue;
            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float sx = ndcX * 0.5f + 0.5f;
            float sy = 1f - (ndcY * 0.5f + 0.5f);
            xMin = MathF.Min(xMin, sx);
            yMin = MathF.Min(yMin, sy);
            xMax = MathF.Max(xMax, sx);
            yMax = MathF.Max(yMax, sy);
            accepted++;
        }

        if (accepted == 0)
            return false;

        minX = Math.Clamp(xMin, 0f, 1f);
        minY = Math.Clamp(yMin, 0f, 1f);
        maxX = Math.Clamp(xMax, 0f, 1f);
        maxY = Math.Clamp(yMax, 0f, 1f);
        return maxX >= minX && maxY >= minY;
    }

    /// <summary>Pick the strongest lights by camera weight when the scene exceeds the soft cap.</summary>
    public static int SelectStrongest(
        Span<ClusterPointLightGpu> lights,
        int count,
        int cap,
        Vector3 cameraPos)
    {
        if (count <= cap)
            return count;

        // Partial selection sort of the first `cap` by descending camera weight.
        for (int i = 0; i < cap; i++)
        {
            int best = i;
            float bestW = CameraWeight(lights[i], cameraPos);
            for (int j = i + 1; j < count; j++)
            {
                float w = CameraWeight(lights[j], cameraPos);
                if (w > bestW)
                {
                    bestW = w;
                    best = j;
                }
            }

            if (best != i)
            {
                (lights[i], lights[best]) = (lights[best], lights[i]);
            }
        }

        return cap;
    }

    private static float CameraWeight(in ClusterPointLightGpu light, Vector3 cameraPos)
    {
        Vector3 pos = new(light.PosRadius.X, light.PosRadius.Y, light.PosRadius.Z);
        float intensity = MathF.Max(light.ColorIntensity.W, 0.001f);
        float radius = MathF.Max(light.PosRadius.W, 0.001f);
        return intensity * radius * radius / (1f + Vector3.DistanceSquared(cameraPos, pos));
    }
}
