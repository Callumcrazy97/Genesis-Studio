using System.Numerics;
using Genesis.World.Terrain;

namespace Genesis.Application.Core.Terrain;

/// <summary>One triangle of a model's ground and the paint layer its material is painted with.</summary>
public readonly record struct GroundTriangle(Vector3 A, Vector3 B, Vector3 C, int Layer);

/// <summary>
/// The paint of a Terrain made from a model's ground (<c>terrain.fromModel</c>). A sample is painted
/// with the layers of the surfaces over its whole cell, each in proportion to the share of the cell
/// it covers, not with the one layer under its centre point. A strip narrower than a cell (a path's
/// 0.2 m earth rim on a 0.25 m grid) is then a thin share of the paint along its whole length;
/// painted from the centre points alone it came and went with where the grid happened to fall, and
/// a path edge showed as a broken band.
/// </summary>
public static class TerrainModelPaint
{
    /// <summary>Most subsamples a cell is measured with along each side.</summary>
    public const int MaximumSubsamples = 8;
    /// <summary>Marks a subsample no surface covers.</summary>
    private const byte NoLayer = 255;

    private struct Prepared
    {
        public Vector3 A, B, C;
        public byte Layer;
        public float Area;
        public int X0, X1, Z0, Z1;
    }

    /// <summary>
    /// Paints every sample of <paramref name="terrain"/>. Each cell is measured at
    /// <paramref name="subsamples"/> x <paramref name="subsamples"/> points; at each the highest
    /// surface (as the heights are taken) gives its layer, and the layers' shares become the sample's
    /// weights. Where no surface covers any of a cell, the sample takes its entry of
    /// <paramref name="centreLayers"/>, the layer found under its centre (gaps filled from the nearest
    /// ground). One subsample paints each sample with its centre layer alone.
    /// </summary>
    public static void Paint(TerrainAsset terrain, IReadOnlyList<GroundTriangle> triangles, int[] centreLayers, int subsamples)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(centreLayers);
        int resX = terrain.ResolutionX, resZ = terrain.ResolutionZ;
        if (centreLayers.Length != resX * resZ) throw new ArgumentException("There is one centre layer for each sample.", nameof(centreLayers));
        subsamples = Math.Clamp(subsamples, 1, MaximumSubsamples);
        if (subsamples == 1 || triangles.Count == 0)
        {
            for (int at = 0; at < centreLayers.Length; at++) terrain.SetLayerAt(at, centreLayers[at]);
            return;
        }

        // Subsample k along x lies at ((k + 0.5) / subsamples - 0.5) cells from the origin, so each
        // sample's cell, a cell wide about its point, holds subsamples x subsamples of them.
        float cell = terrain.CellSize, originX = terrain.OriginX, originZ = terrain.OriginZ;
        int width = resX * subsamples, depth = resZ * subsamples;
        var prepared = new List<Prepared>(triangles.Count);
        var rows = new List<int>?[resZ];
        foreach (GroundTriangle triangle in triangles)
        {
            float area = (triangle.B.X - triangle.A.X) * (triangle.C.Z - triangle.A.Z) - (triangle.C.X - triangle.A.X) * (triangle.B.Z - triangle.A.Z);
            if (MathF.Abs(area) < 1e-9f) continue; // seen edge-on from above: a wall, not ground
            float minX = MathF.Min(triangle.A.X, MathF.Min(triangle.B.X, triangle.C.X)), maxX = MathF.Max(triangle.A.X, MathF.Max(triangle.B.X, triangle.C.X));
            float minZ = MathF.Min(triangle.A.Z, MathF.Min(triangle.B.Z, triangle.C.Z)), maxZ = MathF.Max(triangle.A.Z, MathF.Max(triangle.B.Z, triangle.C.Z));
            var item = new Prepared
            {
                A = triangle.A, B = triangle.B, C = triangle.C, Area = area, Layer = (byte)Math.Clamp(triangle.Layer, 0, TerrainAsset.MaximumPaintLayers - 1),
                X0 = Math.Max(0, (int)MathF.Ceiling(((minX - originX) / cell + 0.5f) * subsamples - 0.5f)),
                X1 = Math.Min(width - 1, (int)MathF.Floor(((maxX - originX) / cell + 0.5f) * subsamples - 0.5f)),
                Z0 = Math.Max(0, (int)MathF.Ceiling(((minZ - originZ) / cell + 0.5f) * subsamples - 0.5f)),
                Z1 = Math.Min(depth - 1, (int)MathF.Floor(((maxZ - originZ) / cell + 0.5f) * subsamples - 0.5f)),
            };
            if (item.X0 > item.X1 || item.Z0 > item.Z1) continue; // between subsamples: nothing of it is measured
            int index = prepared.Count;
            prepared.Add(item);
            for (int row = item.Z0 / subsamples; row <= item.Z1 / subsamples; row++)
                (rows[row] ??= []).Add(index);
        }

        // One row of samples at a time: the highest surface at each of its subsamples.
        float[] top = new float[width * subsamples];
        byte[] layerAt = new byte[top.Length];
        int[] counts = new int[TerrainAsset.MaximumPaintLayers];
        byte[] weights = new byte[TerrainAsset.MaximumPaintLayers];
        for (int z = 0; z < resZ; z++)
        {
            Array.Fill(top, float.NegativeInfinity);
            Array.Fill(layerAt, NoLayer);
            int firstRow = z * subsamples;
            if (rows[z] is { } inRow)
                foreach (int index in inRow)
                {
                    Prepared t = prepared[index];
                    for (int kz = Math.Max(t.Z0, firstRow); kz <= Math.Min(t.Z1, firstRow + subsamples - 1); kz++)
                    {
                        float pz = originZ + ((kz + 0.5f) / subsamples - 0.5f) * cell;
                        int rowStart = (kz - firstRow) * width;
                        for (int kx = t.X0; kx <= t.X1; kx++)
                        {
                            float px = originX + ((kx + 0.5f) / subsamples - 0.5f) * cell;
                            float u = ((t.B.X - px) * (t.C.Z - pz) - (t.C.X - px) * (t.B.Z - pz)) / t.Area;
                            float v = ((t.C.X - px) * (t.A.Z - pz) - (t.A.X - px) * (t.C.Z - pz)) / t.Area;
                            float w = 1f - u - v;
                            if (u < -1e-5f || v < -1e-5f || w < -1e-5f) continue;
                            float y = u * t.A.Y + v * t.B.Y + w * t.C.Y;
                            if (y <= top[rowStart + kx]) continue;
                            top[rowStart + kx] = y;
                            layerAt[rowStart + kx] = t.Layer;
                        }
                    }
                }

            for (int x = 0; x < resX; x++)
            {
                Array.Clear(counts);
                int covered = 0;
                for (int sz = 0; sz < subsamples; sz++)
                for (int sx = 0; sx < subsamples; sx++)
                {
                    byte layer = layerAt[sz * width + x * subsamples + sx];
                    if (layer == NoLayer) continue;
                    counts[layer]++;
                    covered++;
                }
                int at = z * resX + x;
                if (covered == 0) { terrain.SetLayerAt(at, centreLayers[at]); continue; }
                SetShares(terrain, at, counts, covered, weights);
            }
        }
    }

    // Weights of 0-255 in proportion to the counts, adding up to 255 (the largest remainders take
    // the rounding); a cell wholly one layer is that layer alone.
    private static void SetShares(TerrainAsset terrain, int sample, int[] counts, int covered, byte[] weights)
    {
        int sole = -1, given = 0;
        for (int layer = 0; layer < counts.Length; layer++)
        {
            weights[layer] = (byte)(counts[layer] * 255 / covered);
            given += weights[layer];
            if (counts[layer] == covered) sole = layer;
        }
        if (sole >= 0) { terrain.SetLayerAt(sample, sole); return; }
        while (given < 255)
        {
            int best = -1, bestRemainder = -1;
            for (int layer = 0; layer < counts.Length; layer++)
            {
                int remainder = counts[layer] * 255 % covered;
                if (counts[layer] > 0 && remainder > bestRemainder) { best = layer; bestRemainder = remainder; }
            }
            if (best < 0) break;
            weights[best]++;
            counts[best] = 0; // takes no second share
            given++;
        }
        // Layers 5-8 need the second plane; once it exists every sample writes both.
        byte[] first = terrain.SplatmapData;
        bool second = terrain.HasExtendedLayers;
        for (int layer = 4; layer < weights.Length && !second; layer++) second = weights[layer] > 0;
        byte[]? extra = second ? terrain.EnsureExtendedSplatmap() : null;
        int offset = sample * 4;
        for (int layer = 0; layer < 4; layer++)
        {
            first[offset + layer] = weights[layer];
            if (extra is not null) extra[offset + layer] = weights[layer + 4];
        }
    }
}
