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
    public const int LayerCount = TerrainAsset.MaximumPaintLayers;

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
        // Layers 5-8: the second splat plane, absent (all zero) until one of them is painted.
        byte[] second = terrain.ExtendedSplatmapData;
        int at = (z * terrain.ResolutionX + x) * 4;
        for (int layer = 0; layer < 4; layer++)
            weights[4 + layer] = second is null ? 0f : second[at + layer] * scale;
    }
}
