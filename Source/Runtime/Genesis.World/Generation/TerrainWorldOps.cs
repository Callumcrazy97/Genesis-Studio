using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace Genesis.World.Generation;

/// <summary>A square-celled grid of heights in metres, row-major (<c>z * Width + x</c>).</summary>
public sealed class HeightGrid
{
    public HeightGrid(int width, int depth, float cellSize, float originX, float originZ)
    {
        if (width < 2 || depth < 2) throw new ArgumentOutOfRangeException(nameof(width), "A height grid needs at least 2 x 2 samples.");
        Width = width;
        Depth = depth;
        CellSize = cellSize;
        OriginX = originX;
        OriginZ = originZ;
        Values = new float[width * depth];
    }

    public int Width { get; }
    public int Depth { get; }
    public float CellSize { get; }
    public float OriginX { get; }
    public float OriginZ { get; }
    public float[] Values { get; }

    public float this[int x, int z]
    {
        get => Values[z * Width + x];
        set => Values[z * Width + x] = value;
    }

    public float WorldX(int x) => OriginX + x * CellSize;
    public float WorldZ(int z) => OriginZ + z * CellSize;

    /// <summary>Bilinear height at a world position, clamped to the grid.</summary>
    public float Sample(float worldX, float worldZ)
    {
        float gx = Math.Clamp((worldX - OriginX) / CellSize, 0f, Width - 1.001f);
        float gz = Math.Clamp((worldZ - OriginZ) / CellSize, 0f, Depth - 1.001f);
        int x = (int)gx, z = (int)gz;
        float tx = gx - x, tz = gz - z;
        int i = z * Width + x;
        float top = Values[i] + (Values[i + 1] - Values[i]) * tx;
        float bottom = Values[i + Width] + (Values[i + Width + 1] - Values[i + Width]) * tx;
        return top + (bottom - top) * tz;
    }

    /// <summary>Slope in degrees from central differences at a sample.</summary>
    public float SlopeDegrees(int x, int z)
    {
        int l = Math.Max(x - 1, 0), r = Math.Min(x + 1, Width - 1), n = Math.Max(z - 1, 0), s = Math.Min(z + 1, Depth - 1);
        float dx = (this[r, z] - this[l, z]) / ((r - l) * CellSize);
        float dz = (this[x, s] - this[x, n]) / ((s - n) * CellSize);
        return MathF.Atan(MathF.Sqrt(dx * dx + dz * dz)) * (180f / MathF.PI);
    }

    /// <summary>A coarser copy that keeps every <paramref name="factor"/>th sample.</summary>
    public HeightGrid Downsample(int factor)
    {
        factor = Math.Max(1, factor);
        int width = (Width - 1) / factor + 1, depth = (Depth - 1) / factor + 1;
        var coarse = new HeightGrid(width, depth, CellSize * factor, OriginX, OriginZ);
        Parallel.For(0, depth, z =>
        {
            for (int x = 0; x < width; x++)
                coarse.Values[z * width + x] = Values[Math.Min(z * factor, Depth - 1) * Width + Math.Min(x * factor, Width - 1)];
        });
        return coarse;
    }

    /// <summary>
    /// Adds the change between two coarse grids to this one, interpolated. Whole-terrain passes
    /// (erosion, sink filling) run on a coarse copy and are applied back this way, so fine detail
    /// already present is kept.
    /// </summary>
    public void AddDifference(HeightGrid before, HeightGrid after)
    {
        Parallel.For(0, Depth, z =>
        {
            float wz = WorldZ(z);
            for (int x = 0; x < Width; x++)
            {
                float wx = WorldX(x);
                Values[z * Width + x] += after.Sample(wx, wz) - before.Sample(wx, wz);
            }
        });
    }
}

/// <summary>One point along a traced river, upstream to downstream.</summary>
public readonly record struct RiverPoint(Vector3 Position, float Width, float Depth);

/// <summary>A river from its source to where it meets the sea or a larger river.</summary>
public sealed class TracedRiver
{
    public List<RiverPoint> Points { get; } = new();
    public float BasinSquareKilometres { get; set; }
}

/// <summary>A levelled site suitable for buildings.</summary>
public readonly record struct TerrainSite(Vector3 Centre, float Radius);

/// <summary>
/// Whole-terrain operations for generated worlds: erosion, drainage and rivers, natural painting
/// and finding level ground. They work on plain height grids and know nothing about any game.
/// </summary>
public static class TerrainWorldOps
{
    // ── Erosion ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Particle hydraulic erosion: water droplets run downhill, pick sediment up on steep ground
    /// and drop it where they slow, cutting gullies and filling valley floors.
    /// </summary>
    /// <param name="strength">0 leaves the terrain unchanged; 1 is a strongly weathered look.</param>
    public static void Erode(HeightGrid grid, float strength, int seed)
    {
        strength = Math.Clamp(strength, 0f, 2f);
        if (strength <= 0f) return;
        int width = grid.Width, depth = grid.Depth;
        float[] map = grid.Values;
        int droplets = (int)Math.Min(6_000_000L, (long)(width * (long)depth * 0.35 * strength));
        const int radius = 3, lifetime = 36;
        const float inertia = 0.06f, capacityFactor = 4f, minimumCapacity = 0.01f, depositSpeed = 0.3f,
            erodeSpeed = 0.3f, evaporate = 0.02f, gravity = 4f;
        float cell = grid.CellSize;

        // Brush weights, shared by every droplet.
        var brushOffsets = new List<(int Dx, int Dz, float Weight)>();
        float weightSum = 0f;
        for (int dz = -radius; dz <= radius; dz++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance >= radius) continue;
            float weight = 1f - distance / radius;
            brushOffsets.Add((dx, dz, weight));
            weightSum += weight;
        }

        uint state = (uint)(seed * 747796405 + 2891336453u) | 1u;
        float Next()
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state & 0xFFFFFF) / 16777216f;
        }

        for (int i = 0; i < droplets; i++)
        {
            float px = Next() * (width - 2), pz = Next() * (depth - 2);
            float dirX = 0f, dirZ = 0f, speed = 1f, water = 1f, sediment = 0f;
            for (int step = 0; step < lifetime; step++)
            {
                int nodeX = (int)px, nodeZ = (int)pz;
                float cellX = px - nodeX, cellZ = pz - nodeZ;
                int index = nodeZ * width + nodeX;
                float nw = map[index], ne = map[index + 1], sw = map[index + width], se = map[index + width + 1];
                float gradX = (ne - nw) * (1 - cellZ) + (se - sw) * cellZ;
                float gradZ = (sw - nw) * (1 - cellX) + (se - ne) * cellX;
                float height = nw * (1 - cellX) * (1 - cellZ) + ne * cellX * (1 - cellZ) + sw * (1 - cellX) * cellZ + se * cellX * cellZ;

                dirX = dirX * inertia - gradX * (1 - inertia);
                dirZ = dirZ * inertia - gradZ * (1 - inertia);
                float length = MathF.Sqrt(dirX * dirX + dirZ * dirZ);
                if (length < 1e-6f) break;
                dirX /= length; dirZ /= length;
                px += dirX; pz += dirZ;
                if (px < radius || px >= width - radius - 1 || pz < radius || pz >= depth - radius - 1) break;

                int newX = (int)px, newZ = (int)pz;
                float newCellX = px - newX, newCellZ = pz - newZ;
                int newIndex = newZ * width + newX;
                float newHeight = map[newIndex] * (1 - newCellX) * (1 - newCellZ) + map[newIndex + 1] * newCellX * (1 - newCellZ)
                    + map[newIndex + width] * (1 - newCellX) * newCellZ + map[newIndex + width + 1] * newCellX * newCellZ;
                // Heights are metres and a step is one cell, so the drop is scaled to a slope.
                float delta = (newHeight - height) / cell;

                float capacity = MathF.Max(-delta * speed * water * capacityFactor, minimumCapacity);
                if (sediment > capacity || delta > 0f)
                {
                    float deposit = delta > 0f ? MathF.Min(delta, sediment) : (sediment - capacity) * depositSpeed;
                    sediment -= deposit;
                    float amount = deposit * cell;
                    map[index] += amount * (1 - cellX) * (1 - cellZ);
                    map[index + 1] += amount * cellX * (1 - cellZ);
                    map[index + width] += amount * (1 - cellX) * cellZ;
                    map[index + width + 1] += amount * cellX * cellZ;
                }
                else
                {
                    float erode = MathF.Min((capacity - sediment) * erodeSpeed, -delta);
                    float amount = erode * cell / weightSum;
                    foreach ((int dx, int dz, float weight) in brushOffsets)
                    {
                        int at = (nodeZ + dz) * width + nodeX + dx;
                        if ((uint)at < (uint)map.Length) map[at] -= amount * weight;
                    }

                    sediment += erode;
                }

                speed = MathF.Sqrt(MathF.Max(0f, speed * speed + delta * -gravity));
                water *= 1f - evaporate;
            }
        }
    }

    // ── Drainage ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Raises every hollow that cannot drain until water can run from each cell to the sea
    /// (or the grid edge), with a slight fall so the route downhill is never ambiguous.
    /// </summary>
    /// <returns>For each cell, the neighbouring cell it drains to; -1 for the sea and edges.</returns>
    public static int[] FillSinks(HeightGrid grid, float seaLevel)
    {
        int width = grid.Width, depth = grid.Depth;
        float[] height = grid.Values;
        var receiver = new int[height.Length];
        var closed = new bool[height.Length];
        var open = new PriorityQueue<int, float>();
        Array.Fill(receiver, -1);
        float epsilon = grid.CellSize * 0.0015f;

        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
        {
            int index = z * width + x;
            bool edge = x == 0 || z == 0 || x == width - 1 || z == depth - 1;
            if (!edge && height[index] > seaLevel) continue;
            closed[index] = true;
            open.Enqueue(index, height[index]);
        }

        Span<int> offsetX = stackalloc int[] { -1, 1, 0, 0, -1, 1, -1, 1 };
        Span<int> offsetZ = stackalloc int[] { 0, 0, -1, 1, -1, -1, 1, 1 };
        while (open.TryDequeue(out int current, out _))
        {
            int cx = current % width, cz = current / width;
            for (int k = 0; k < 8; k++)
            {
                int nx = cx + offsetX[k], nz = cz + offsetZ[k];
                if ((uint)nx >= (uint)width || (uint)nz >= (uint)depth) continue;
                int neighbour = nz * width + nx;
                if (closed[neighbour]) continue;
                closed[neighbour] = true;
                float fall = k < 4 ? epsilon : epsilon * 1.4142f;
                if (height[neighbour] < height[current] + fall) height[neighbour] = height[current] + fall;
                receiver[neighbour] = current;
                open.Enqueue(neighbour, height[neighbour]);
            }
        }

        // Prefer the steepest neighbour over the flood order: it follows valleys more naturally.
        Parallel.For(1, depth - 1, z =>
        {
            for (int x = 1; x < width - 1; x++)
            {
                int index = z * width + x;
                if (receiver[index] < 0) continue;
                float best = 0f;
                int choice = receiver[index];
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int neighbour = (z + dz) * width + x + dx;
                    float drop = (height[index] - height[neighbour]) / (dx != 0 && dz != 0 ? 1.4142f : 1f);
                    if (drop > best) { best = drop; choice = neighbour; }
                }

                receiver[index] = choice;
            }
        });
        return receiver;
    }

    /// <summary>Area draining through each cell, in cells (each cell counts itself).</summary>
    public static float[] FlowAccumulation(HeightGrid grid, int[] receiver)
    {
        float[] height = grid.Values;
        int[] order = new int[height.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => height[b].CompareTo(height[a]));
        float[] area = new float[height.Length];
        Array.Fill(area, 1f);
        foreach (int index in order)
        {
            int next = receiver[index];
            if (next >= 0) area[next] += area[index];
        }

        return area;
    }

    /// <summary>
    /// Traces the largest rivers: for each of the biggest outlets to the sea, the main stream is
    /// followed uphill to its source, then its larger tributaries.
    /// </summary>
    /// <param name="count">Rivers reaching the sea; each may bring tributaries.</param>
    /// <param name="minimumBasinSquareKilometres">Drainage area at which a stream becomes a river.</param>
    public static List<TracedRiver> TraceRivers(HeightGrid grid, int[] receiver, float[] area, float seaLevel,
        int count, float minimumBasinSquareKilometres, float tributaryBasinSquareKilometres = 0f)
    {
        var rivers = new List<TracedRiver>();
        if (count <= 0) return rivers;
        int width = grid.Width;
        float[] height = grid.Values;
        float cellArea = grid.CellSize * grid.CellSize / 1_000_000f;
        float threshold = MathF.Max(1f, minimumBasinSquareKilometres / cellArea);
        float tributaryThreshold = tributaryBasinSquareKilometres > 0f
            ? MathF.Max(1f, tributaryBasinSquareKilometres / cellArea)
            : threshold * 2.5f;

        // Donors of each cell, so a stream can be followed uphill.
        var donorStart = new int[height.Length + 1];
        foreach (int next in receiver) if (next >= 0) donorStart[next + 1]++;
        for (int i = 0; i < height.Length; i++) donorStart[i + 1] += donorStart[i];
        var donors = new int[donorStart[height.Length]];
        var fill = new int[height.Length];
        for (int i = 0; i < receiver.Length; i++)
        {
            int next = receiver[i];
            if (next >= 0) donors[donorStart[next] + fill[next]++] = i;
        }

        // Outlets: land cells that drain straight into the sea or off the edge.
        var outlets = new List<int>();
        for (int i = 0; i < receiver.Length; i++)
        {
            int next = receiver[i];
            if (next < 0 || area[i] < threshold || height[i] <= seaLevel) continue;
            if (receiver[next] < 0 || height[next] <= seaLevel) outlets.Add(i);
        }

        outlets.Sort((a, b) => area[b].CompareTo(area[a]));
        var onRiver = new bool[height.Length];
        var pending = new Queue<(int Start, float Limit)>();
        int mainRivers = 0;
        foreach (int outlet in outlets)
        {
            if (mainRivers >= count) break;
            if (onRiver[outlet]) continue;
            pending.Enqueue((outlet, threshold));
            mainRivers++;
            while (pending.TryDequeue(out (int Start, float Limit) item))
            {
                var cells = new List<int>();
                int current = item.Start;
                while (true)
                {
                    cells.Add(current);
                    onRiver[current] = true;
                    int best = -1;
                    float bestArea = 0f;
                    for (int k = donorStart[current]; k < donorStart[current + 1]; k++)
                    {
                        int donor = donors[k];
                        if (area[donor] > bestArea) { bestArea = area[donor]; best = donor; }
                    }

                    for (int k = donorStart[current]; k < donorStart[current + 1]; k++)
                    {
                        int donor = donors[k];
                        if (donor != best && area[donor] >= tributaryThreshold && !onRiver[donor])
                            pending.Enqueue((donor, tributaryThreshold));
                    }

                    if (best < 0 || bestArea < item.Limit) break;
                    current = best;
                }

                if (cells.Count < 6) continue;
                cells.Reverse(); // source first

                // Continue a few cells past the junction or shore so the water meets what it joins.
                int tail = receiver[cells[^1]];
                for (int extra = 0; extra < 3 && tail >= 0; extra++)
                {
                    cells.Add(tail);
                    tail = receiver[tail];
                }

                var river = new TracedRiver { BasinSquareKilometres = area[item.Start] * cellArea };
                float bed = float.MaxValue;
                foreach (int cell in cells)
                {
                    float basin = area[Math.Min(cell, area.Length - 1)] * cellArea;
                    float riverWidth = Math.Clamp(3.5f + 7.5f * MathF.Sqrt(basin), 4f, 34f);
                    float riverDepth = 0.9f + riverWidth * 0.07f;
                    // The bed only ever falls on the way down, so water never has to run uphill.
                    bed = MathF.Min(bed, MathF.Max(height[cell], seaLevel - riverDepth * 0.5f) - riverDepth);
                    river.Points.Add(new RiverPoint(
                        new Vector3(grid.WorldX(cell % width), bed, grid.WorldZ(cell / width)), riverWidth, riverDepth));
                }

                Smooth(river.Points);
                rivers.Add(river);
            }
        }

        return rivers;
    }

    private static void Smooth(List<RiverPoint> points)
    {
        if (points.Count < 4) return;
        for (int pass = 0; pass < 3; pass++)
        {
            var smoothed = new List<RiverPoint>(points.Count) { points[0] };
            for (int i = 1; i < points.Count - 1; i++)
            {
                Vector3 position = points[i - 1].Position * 0.25f + points[i].Position * 0.5f + points[i + 1].Position * 0.25f;
                // Keep the bed from rising after smoothing.
                position.Y = MathF.Min(position.Y, smoothed[^1].Position.Y);
                smoothed.Add(points[i] with { Position = position });
            }

            RiverPoint last = points[^1];
            smoothed.Add(last with { Position = new Vector3(last.Position.X, MathF.Min(last.Position.Y, smoothed[^1].Position.Y), last.Position.Z) });
            points.Clear();
            points.AddRange(smoothed);
        }
    }

    /// <summary>Cuts a river's channel and gentle banks into a height grid.</summary>
    public static void CarveRiver(HeightGrid grid, TracedRiver river)
    {
        float[] height = grid.Values;
        for (int i = 0; i < river.Points.Count - 1; i++)
        {
            RiverPoint a = river.Points[i], b = river.Points[i + 1];
            float reach = MathF.Max(a.Width, b.Width) * 1.9f + grid.CellSize;
            int minX = Math.Max(0, (int)((MathF.Min(a.Position.X, b.Position.X) - reach - grid.OriginX) / grid.CellSize));
            int maxX = Math.Min(grid.Width - 1, (int)((MathF.Max(a.Position.X, b.Position.X) + reach - grid.OriginX) / grid.CellSize) + 1);
            int minZ = Math.Max(0, (int)((MathF.Min(a.Position.Z, b.Position.Z) - reach - grid.OriginZ) / grid.CellSize));
            int maxZ = Math.Min(grid.Depth - 1, (int)((MathF.Max(a.Position.Z, b.Position.Z) + reach - grid.OriginZ) / grid.CellSize) + 1);
            Vector2 start = new(a.Position.X, a.Position.Z), end = new(b.Position.X, b.Position.Z);
            Vector2 along = end - start;
            float lengthSquared = MathF.Max(along.LengthSquared(), 1e-6f);
            for (int z = minZ; z <= maxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 point = new(grid.WorldX(x), grid.WorldZ(z));
                float t = Math.Clamp(Vector2.Dot(point - start, along) / lengthSquared, 0f, 1f);
                float distance = Vector2.Distance(point, start + along * t);
                float halfWidth = (a.Width + (b.Width - a.Width) * t) * 0.5f;
                float depth = a.Depth + (b.Depth - a.Depth) * t;
                float bed = a.Position.Y + (b.Position.Y - a.Position.Y) * t;
                float across = distance / halfWidth;
                if (across >= 3.6f) continue;
                int index = z * grid.Width + x;
                // A rounded bed inside the channel; outside it the bank climbs back to the land.
                float channel = bed + depth * MathF.Min(1f, across * across);
                float blend = across <= 1f ? 0f : Smoothstep(1f, 3.6f, across);
                float target = channel + (height[index] - channel) * blend;
                if (target < height[index]) height[index] = target;
            }
        }
    }

    // ── Painting ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Paints four natural layers from height and slope: 0 grass, 1 rock, 2 sand, 3 snow.
    /// </summary>
    /// <param name="splat">RGBA weights, four bytes per sample, written in place.</param>
    public static void PaintNatural(HeightGrid grid, byte[] splat, float beachHeight, float snowHeight,
        float rockSlopeDegrees, int seed)
    {
        int width = grid.Width, depth = grid.Depth;
        Parallel.For(0, depth, z =>
        {
            for (int x = 0; x < width; x++)
            {
                float height = grid[x, z];
                float slope = grid.SlopeDegrees(x, z);
                float wx = grid.WorldX(x), wz = grid.WorldZ(z);
                float wobble = Noise.Fbm2(seed + 71, wx * 0.006f, wz * 0.006f, 3);
                float fine = Noise.Fbm2(seed + 133, wx * 0.04f, wz * 0.04f, 2);
                float rock = Smoothstep(rockSlopeDegrees - 7f, rockSlopeDegrees + 7f, slope + fine * 5f);
                float snow = Smoothstep(snowHeight - 30f, snowHeight + 45f, height + wobble * 45f)
                    * (1f - Smoothstep(rockSlopeDegrees + 6f, rockSlopeDegrees + 20f, slope));
                float sand = 1f - Smoothstep(beachHeight - 1.5f, beachHeight + 2.5f + wobble * 1.5f, height);
                sand *= 1f - rock * 0.6f;
                rock *= 1f - snow;
                float grass = MathF.Max(0f, 1f - rock - snow - sand);
                float total = MathF.Max(0.0001f, grass + rock + sand + snow);
                int at = (z * width + x) * 4;
                splat[at] = (byte)(grass / total * 255f + 0.5f);
                splat[at + 1] = (byte)(rock / total * 255f + 0.5f);
                splat[at + 2] = (byte)(sand / total * 255f + 0.5f);
                splat[at + 3] = (byte)(snow / total * 255f + 0.5f);
            }
        });
    }

    /// <summary>Blends one layer in around a point, for example bare earth under a settlement.</summary>
    public static void PaintDisc(HeightGrid grid, byte[] splat, Vector2 centre, float radius, int layer, float strength)
    {
        layer = Math.Clamp(layer, 0, 3);
        int minX = Math.Max(0, (int)((centre.X - radius - grid.OriginX) / grid.CellSize));
        int maxX = Math.Min(grid.Width - 1, (int)((centre.X + radius - grid.OriginX) / grid.CellSize) + 1);
        int minZ = Math.Max(0, (int)((centre.Y - radius - grid.OriginZ) / grid.CellSize));
        int maxZ = Math.Min(grid.Depth - 1, (int)((centre.Y + radius - grid.OriginZ) / grid.CellSize) + 1);
        Span<float> weights = stackalloc float[4];
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            float distance = Vector2.Distance(new Vector2(grid.WorldX(x), grid.WorldZ(z)), centre);
            float amount = (1f - Smoothstep(radius * 0.55f, radius, distance)) * Math.Clamp(strength, 0f, 1f);
            if (amount <= 0f) continue;
            int at = (z * grid.Width + x) * 4;
            float total = 0f;
            for (int c = 0; c < 4; c++)
            {
                weights[c] = c == layer ? splat[at + c] / 255f + (1f - splat[at + c] / 255f) * amount : splat[at + c] / 255f * (1f - amount);
                total += weights[c];
            }

            for (int c = 0; c < 4; c++) splat[at + c] = (byte)(weights[c] / MathF.Max(total, 0.0001f) * 255f + 0.5f);
        }
    }

    // ── Sites ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds well-separated places that are level enough to build on, preferring ground near
    /// water, and returns them best first.
    /// </summary>
    /// <param name="waterDistance">Optional: metres from each sample to the nearest river or shore.</param>
    public static List<TerrainSite> FindSites(HeightGrid grid, int count, float minimumHeight, float maximumHeight,
        float radius, float minimumSeparation, int seed, Func<float, float, float> waterDistance = null)
    {
        var sites = new List<TerrainSite>();
        if (count <= 0) return sites;
        int step = Math.Max(1, (int)(radius * 0.5f / grid.CellSize));
        int reach = Math.Max(1, (int)(radius / grid.CellSize));
        int probe = Math.Max(1, reach / 4);
        var candidates = new List<(float Score, Vector3 Centre)>();
        for (int z = reach + step; z < grid.Depth - reach - step; z += step)
        for (int x = reach + step; x < grid.Width - reach - step; x += step)
        {
            float centre = grid[x, z];
            if (centre < minimumHeight || centre > maximumHeight) continue;
            float low = centre, high = centre, sum = 0f;
            int samples = 0;
            for (int dz = -reach; dz <= reach; dz += probe)
            for (int dx = -reach; dx <= reach; dx += probe)
            {
                if (dx * dx + dz * dz > reach * reach) continue;
                float value = grid[x + dx, z + dz];
                low = MathF.Min(low, value); high = MathF.Max(high, value);
                sum += value; samples++;
            }

            float relief = high - low;
            if (low < minimumHeight || relief > radius * 0.22f) continue;
            float wx = grid.WorldX(x), wz = grid.WorldZ(z);
            float water = waterDistance?.Invoke(wx, wz) ?? 0f;
            // Level ground first, then closeness to water, with a little variety.
            float score = relief / radius * 6f + MathF.Min(water, 2000f) / 800f + Hash01(x, z, seed) * 0.9f;
            candidates.Add((score, new Vector3(wx, sum / samples, wz)));
        }

        candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
        float separationSquared = minimumSeparation * minimumSeparation;
        foreach ((_, Vector3 centre) in candidates)
        {
            if (sites.Count >= count) break;
            bool clear = true;
            foreach (TerrainSite existing in sites)
            {
                float dx = existing.Centre.X - centre.X, dz = existing.Centre.Z - centre.Z;
                if (dx * dx + dz * dz < separationSquared) { clear = false; break; }
            }

            if (clear) sites.Add(new TerrainSite(centre, radius));
        }

        return sites;
    }

    /// <summary>Levels the ground around a site, easing back into the surrounding land.</summary>
    public static void Flatten(HeightGrid grid, TerrainSite site, float evenness = 0.85f)
    {
        float outer = site.Radius * 1.5f;
        int minX = Math.Max(0, (int)((site.Centre.X - outer - grid.OriginX) / grid.CellSize));
        int maxX = Math.Min(grid.Width - 1, (int)((site.Centre.X + outer - grid.OriginX) / grid.CellSize) + 1);
        int minZ = Math.Max(0, (int)((site.Centre.Z - outer - grid.OriginZ) / grid.CellSize));
        int maxZ = Math.Min(grid.Depth - 1, (int)((site.Centre.Z + outer - grid.OriginZ) / grid.CellSize) + 1);
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            float dx = grid.WorldX(x) - site.Centre.X, dz = grid.WorldZ(z) - site.Centre.Z;
            float distance = MathF.Sqrt(dx * dx + dz * dz);
            float amount = (1f - Smoothstep(site.Radius * 0.75f, outer, distance)) * Math.Clamp(evenness, 0f, 1f);
            if (amount <= 0f) continue;
            int index = z * grid.Width + x;
            grid.Values[index] += (site.Centre.Y - grid.Values[index]) * amount;
        }
    }

    /// <summary>
    /// Positions for objects around a site: spread outwards in a loose spiral, each kept a minimum
    /// distance from the others, turned to face the centre.
    /// </summary>
    public static List<(Vector3 Position, float YawDegrees)> ArrangeAroundSite(HeightGrid grid, TerrainSite site,
        int count, float innerRadius, float outerRadius, float spacing, int seed, IList<Vector3> occupied = null)
    {
        var placed = new List<(Vector3, float)>();
        innerRadius = Math.Clamp(innerRadius, 0f, site.Radius);
        outerRadius = Math.Clamp(outerRadius, innerRadius, site.Radius);
        float spacingSquared = spacing * spacing;
        for (int attempt = 0; attempt < count * 40 && placed.Count < count; attempt++)
        {
            float t = (attempt + 0.5f) / (count * 4f);
            float ring = innerRadius + (outerRadius - innerRadius) * MathF.Sqrt(Math.Clamp(t % 1f, 0f, 1f));
            float angle = attempt * 2.39996323f + Hash01(attempt, seed, 17) * 0.9f;
            float x = site.Centre.X + MathF.Cos(angle) * ring, z = site.Centre.Z + MathF.Sin(angle) * ring;
            bool clear = true;
            if (occupied != null)
                foreach (Vector3 other in occupied)
                    if ((other.X - x) * (other.X - x) + (other.Z - z) * (other.Z - z) < spacingSquared) { clear = false; break; }
            if (!clear) continue;
            Vector3 position = new(x, grid.Sample(x, z), z);
            // Face the centre, give or take, so doors open onto the green.
            float yaw = MathF.Atan2(site.Centre.X - x, site.Centre.Z - z) * (180f / MathF.PI)
                + (Hash01(attempt, seed, 91) - 0.5f) * 50f;
            placed.Add((position, yaw));
            occupied?.Add(position);
        }

        return placed;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static float Smoothstep(float edge0, float edge1, float value)
    {
        if (MathF.Abs(edge1 - edge0) < 1e-9f) return value < edge0 ? 0f : 1f;
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float Hash01(int a, int b, int c)
    {
        uint hash = unchecked((uint)(a * 374761393 + b * 668265263 + c * 2147483647));
        hash = (hash ^ (hash >> 13)) * 1274126177u;
        return ((hash ^ (hash >> 16)) & 0xFFFFFF) / 16777216f;
    }
}
