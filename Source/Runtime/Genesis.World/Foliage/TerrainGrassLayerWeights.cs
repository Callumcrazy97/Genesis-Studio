using System;
using Genesis.World.Terrain;

namespace Genesis.World.Foliage;

/// <summary>
/// How much of each painted layer lies on one height sample, for rules that read the paint
/// (grass around the camera). The one place such rules read paint weights from.
/// </summary>
public static class TerrainGrassLayerWeights
{
    /// <summary>Painted layers a terrain can hold.</summary>
    public const int LayerCount = 8;

    /// <summary>
    /// Fills <paramref name="weights"/> (at least <see cref="LayerCount"/> long) with the weights,
    /// 0 to 1, of the painted layers at height sample (<paramref name="x"/>, <paramref name="z"/>).
    /// A sample outside the terrain reads as its nearest edge sample.
    /// </summary>
    public static void Sample(TerrainAsset terrain, int x, int z, Span<float> weights)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        if (weights.Length < LayerCount)
            throw new ArgumentException($"Room for {LayerCount} weights is needed.", nameof(weights));

        x = Math.Clamp(x, 0, terrain.ResolutionX - 1);
        z = Math.Clamp(z, 0, terrain.ResolutionZ - 1);
        (byte r, byte g, byte b, byte a) = terrain.GetSplat(x, z);
        const float scale = 1f / 255f;
        weights[0] = r * scale;
        weights[1] = g * scale;
        weights[2] = b * scale;
        weights[3] = a * scale;
        // Layers 4-7: filled from the second splat plane once it exists
        weights[4] = 0f;
        weights[5] = 0f;
        weights[6] = 0f;
        weights[7] = 0f;
    }
}
