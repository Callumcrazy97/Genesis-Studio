using System;
using System.Numerics;

namespace Genesis.Rendering.Lights;

/// <summary>
/// Clustered light culling constants shared by <see cref="ClusteredLightGrid"/> and the forward pixel
/// shader. Clusters are 16×16 screen tiles × 24 depth slices distributed exponentially over a fixed
/// clip-w range, so the CPU and GPU agree on slicing without extra constants. An orthographic view
/// (clip w = 1) simply lands every pixel and light in one slice.
/// </summary>
public static class ClusteredLightDefaults
{
    public const int TileGridSize = 16;
    public const int DepthSlices = 24;
    public const float NearDepth = 0.25f;
    public const float FarDepth = 1000f;
    public const int ClusterCount = TileGridSize * TileGridSize * DepthSlices;
    /// <summary>Most lights one cluster can list; beyond it the weakest (by camera weight) are dropped.</summary>
    public const int MaxLightsPerCluster = 64;
    /// <summary>Header words (offset, count per cluster) at the front of the packed buffer.</summary>
    public const int HeaderWords = ClusterCount * 2;
    /// <summary>Packed light-index capacity after the headers.</summary>
    public const int IndexCapacity = 65536;
    public const int BufferWords = HeaderWords + IndexCapacity;

    /// <summary>Slices per doubling of depth: DepthSlices / log2(FarDepth / NearDepth).</summary>
    public static readonly float SliceScale = DepthSlices / MathF.Log2(FarDepth / NearDepth);

    /// <summary>Depth slice for a clip-space w (view depth under a perspective projection).</summary>
    public static int SliceForDepth(float clipW)
    {
        float scaled = MathF.Log2(MathF.Max(clipW, 1e-4f) / NearDepth) * SliceScale;
        return Math.Clamp((int)MathF.Floor(scaled), 0, DepthSlices - 1);
    }
}

/// <summary>
/// Builds per-cluster light lists for the current view, packed as one buffer: for each cluster an
/// (offset, count) header, then the light indices. Lights are binned strongest first, so a full
/// cluster (or a full buffer) drops the weakest lights rather than whichever were submitted last.
/// Depth slicing means a lantern at the back of a hall no longer costs every pixel in front of it
/// that happens to share its screen tile.
/// </summary>
public sealed class ClusteredLightGrid
{
    private readonly uint[] _buffer = new uint[ClusteredLightDefaults.BufferWords];
    private readonly int[] _counts = new int[ClusteredLightDefaults.ClusterCount];
    private int[] _order = new int[64];
    private float[] _sortKeys = new float[64];
    private int[] _rank = new int[64];
    private int[] _boxes = new int[64 * 6];
    private int _indexCount;

    /// <summary>Headers followed by the packed indices actually used.</summary>
    public ReadOnlySpan<uint> Buffer => _buffer.AsSpan(0, ClusteredLightDefaults.HeaderWords + _indexCount);

    /// <summary>Light indices written after the headers.</summary>
    public int IndexCount => _indexCount;

    /// <summary>Lights listed by the cluster at (tile x, tile y, slice).</summary>
    public ReadOnlySpan<uint> ClusterLights(int tileX, int tileY, int slice)
    {
        int cluster = ClusterIndex(tileX, tileY, slice);
        uint offset = _buffer[cluster * 2];
        uint count = _buffer[cluster * 2 + 1];
        return _buffer.AsSpan((int)offset, (int)count);
    }

    public static int ClusterIndex(int tileX, int tileY, int slice) =>
        (slice * ClusteredLightDefaults.TileGridSize + tileY) * ClusteredLightDefaults.TileGridSize + tileX;

    public void Clear()
    {
        Array.Clear(_buffer, 0, ClusteredLightDefaults.HeaderWords);
        _indexCount = 0;
    }

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

        EnsureScratch(lights.Length);
        int grid = ClusteredLightDefaults.TileGridSize;
        float invW = 1f / viewportWidth;
        float invH = 1f / viewportHeight;
        // Clip w = dot(position, fourth column): the view depth for a perspective projection.
        Vector3 wAxis = new(viewProjection.M14, viewProjection.M24, viewProjection.M34);
        float wAxisLength = wAxis.Length();

        int visible = 0;
        for (int i = 0; i < lights.Length; i++)
        {
            ref readonly ClusterPointLightGpu light = ref lights[i];
            Vector3 pos = new(light.PosRadius.X, light.PosRadius.Y, light.PosRadius.Z);
            float radius = MathF.Max(light.PosRadius.W, 0.001f);
            float centreW = Vector3.Dot(pos, wAxis) + viewProjection.M44;
            float reachW = radius * wAxisLength;
            if (centreW + reachW <= 0f)
                continue; // wholly behind the camera
            if (!TiledLightGrid.TryProjectSphere(pos, radius, viewProjection,
                    out float minX, out float minY, out float maxX, out float maxY))
                continue;

            minX = Math.Clamp(minX - invW, 0f, 1f);
            minY = Math.Clamp(minY - invH, 0f, 1f);
            maxX = Math.Clamp(maxX + invW, 0f, 1f);
            maxY = Math.Clamp(maxY + invH, 0f, 1f);
            int box = visible * 6;
            _boxes[box + 0] = Math.Clamp((int)MathF.Floor(minX * grid), 0, grid - 1);
            _boxes[box + 1] = Math.Clamp((int)MathF.Floor(maxX * grid), 0, grid - 1);
            _boxes[box + 2] = Math.Clamp((int)MathF.Floor(minY * grid), 0, grid - 1);
            _boxes[box + 3] = Math.Clamp((int)MathF.Floor(maxY * grid), 0, grid - 1);
            _boxes[box + 4] = ClusteredLightDefaults.SliceForDepth(centreW - reachW);
            _boxes[box + 5] = ClusteredLightDefaults.SliceForDepth(centreW + reachW);
            _order[visible] = i;
            _sortKeys[visible] = -CameraWeight(light, cameraPos); // ascending sort = strongest first
            _rank[visible] = visible;
            visible++;
        }
        if (visible == 0)
            return;

        // Strongest first, so per-cluster and buffer caps drop the weakest lights. Sorting keys
        // with an items array allocates nothing (this runs every frame the view or lights change).
        Array.Sort(_sortKeys, _rank, 0, visible);

        // Pass 1: per-cluster counts (capped).
        Array.Clear(_counts);
        for (int n = 0; n < visible; n++)
        {
            int box = _rank[n] * 6;
            for (int slice = _boxes[box + 4]; slice <= _boxes[box + 5]; slice++)
                for (int ty = _boxes[box + 2]; ty <= _boxes[box + 3]; ty++)
                    for (int tx = _boxes[box + 0]; tx <= _boxes[box + 1]; tx++)
                    {
                        int cluster = ClusterIndex(tx, ty, slice);
                        if (_counts[cluster] < ClusteredLightDefaults.MaxLightsPerCluster) _counts[cluster]++;
                    }
        }

        // Offsets, truncating clusters once the packed buffer is full.
        uint next = ClusteredLightDefaults.HeaderWords;
        uint end = ClusteredLightDefaults.BufferWords;
        for (int cluster = 0; cluster < ClusteredLightDefaults.ClusterCount; cluster++)
        {
            uint count = (uint)Math.Min(_counts[cluster], (int)(end - next));
            _buffer[cluster * 2] = next;
            _buffer[cluster * 2 + 1] = 0;
            _counts[cluster] = (int)count; // becomes the per-cluster capacity for pass 2
            next += count;
        }
        _indexCount = (int)(next - ClusteredLightDefaults.HeaderWords);

        // Pass 2: fill in rank order.
        for (int n = 0; n < visible; n++)
        {
            int visibleIndex = _rank[n];
            uint lightIndex = (uint)_order[visibleIndex];
            int box = visibleIndex * 6;
            for (int slice = _boxes[box + 4]; slice <= _boxes[box + 5]; slice++)
                for (int ty = _boxes[box + 2]; ty <= _boxes[box + 3]; ty++)
                    for (int tx = _boxes[box + 0]; tx <= _boxes[box + 1]; tx++)
                    {
                        int cluster = ClusterIndex(tx, ty, slice);
                        uint written = _buffer[cluster * 2 + 1];
                        if (written >= (uint)_counts[cluster]) continue;
                        _buffer[_buffer[cluster * 2] + written] = lightIndex;
                        _buffer[cluster * 2 + 1] = written + 1;
                    }
        }
    }

    private void EnsureScratch(int lights)
    {
        if (_order.Length >= lights) return;
        int size = Math.Max(lights, _order.Length * 2);
        _order = new int[size];
        _sortKeys = new float[size];
        _rank = new int[size];
        _boxes = new int[size * 6];
    }

    private static float CameraWeight(in ClusterPointLightGpu light, Vector3 cameraPos)
    {
        Vector3 pos = new(light.PosRadius.X, light.PosRadius.Y, light.PosRadius.Z);
        float intensity = MathF.Max(light.ColorIntensity.W, 0.001f);
        float radius = MathF.Max(light.PosRadius.W, 0.001f);
        return intensity * radius * radius / (1f + Vector3.DistanceSquared(cameraPos, pos));
    }
}
