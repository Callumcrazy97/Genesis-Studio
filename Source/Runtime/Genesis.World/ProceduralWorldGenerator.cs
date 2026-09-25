using System;
using System.Collections.Generic;

namespace Genesis.World
{
    /// <summary>
    /// Infinite procedural overworld: heightmap biomes, caves, ore pockets, lakes, and oceans.
    /// </summary>
    public sealed class ProceduralWorldGenerator : IVoxelGenerator
    {
        private readonly Dictionary<(int, int), (int MinCy, int MaxCy)> _columnCache = new Dictionary<(int, int), (int, int)>();

        public int Seed { get; set; } = 1337;
        public int SeaLevel { get; set; } = 62;
        public int BedrockDepth { get; set; } = 4;
        public int MaxSurfaceHeight { get; set; } = 96;
        public bool GenerateCaves { get; set; } = true;
        public bool GenerateTrees { get; set; } = true;
        public bool GenerateFlowers { get; set; } = true;

        public void GetColumnChunkRange(int chunkX, int chunkZ, out int minChunkY, out int maxChunkY)
        {
            if (_columnCache.TryGetValue((chunkX, chunkZ), out (int MinCy, int MaxCy) cached))
            {
                minChunkY = cached.MinCy;
                maxChunkY = cached.MaxCy;
                return;
            }

            int baseX = chunkX * VoxelChunk.Size;
            int baseZ = chunkZ * VoxelChunk.Size;
            int minSurface = int.MaxValue;
            int maxSurface = int.MinValue;

            ReadOnlySpan<(int lx, int lz)> samples = stackalloc (int, int)[]
            {
                (0, 0), (15, 0), (0, 15), (15, 15), (8, 8),
            };

            foreach ((int lx, int lz) in samples)
            {
                int surface = SurfaceHeight(baseX + lx, baseZ + lz);
                minSurface = Math.Min(minSurface, surface);
                maxSurface = Math.Max(maxSurface, surface);
            }

            minChunkY = 0;
            maxChunkY = Math.Max(0, (maxSurface + 10) / VoxelChunk.Size);
            if (minSurface <= SeaLevel)
                minChunkY = Math.Min(minChunkY, Math.Max(0, (SeaLevel - 16) / VoxelChunk.Size));

            _columnCache[(chunkX, chunkZ)] = (minChunkY, maxChunkY);
        }

        public void Generate(VoxelChunk chunk)
        {
            int baseX = chunk.ChunkX * VoxelChunk.Size;
            int baseY = chunk.ChunkY * VoxelChunk.Size;
            int baseZ = chunk.ChunkZ * VoxelChunk.Size;

            for (int lz = 0; lz < VoxelChunk.Size; lz++)
            {
                for (int lx = 0; lx < VoxelChunk.Size; lx++)
                {
                    int wx = baseX + lx;
                    int wz = baseZ + lz;

                    int surface = SurfaceHeight(wx, wz);
                    float temp = Noise.Fbm2(Seed + 100, wx * 0.0015f, wz * 0.0015f, 3);
                    float moisture = Noise.Fbm2(Seed + 200, wx * 0.0015f + 500f, wz * 0.0015f, 3);
                    bool beach = surface <= SeaLevel + 2 && moisture > 0.45f;
                    bool snowy = surface > SeaLevel + 28 || temp < 0.32f;

                    for (int ly = 0; ly < VoxelChunk.Size; ly++)
                    {
                        int wy = baseY + ly;
                        VoxelBlock block = SampleColumn(wx, wy, wz, surface, temp, moisture, beach, snowy);
                        chunk.Set(lx, ly, lz, block);
                    }
                }
            }

            if (GenerateTrees)
                ScatterTrees(chunk, baseX, baseY, baseZ);

            if (GenerateFlowers)
                ScatterFlowers(chunk, baseX, baseY, baseZ);

            chunk.Dirty = true;
        }

        /// <summary>Scatters cross-sprite plants (poppy/bluebell, occasional saplings) on grass tops.</summary>
        private void ScatterFlowers(VoxelChunk chunk, int baseX, int baseY, int baseZ)
        {
            for (int lz = 0; lz < VoxelChunk.Size; lz++)
            {
                for (int lx = 0; lx < VoxelChunk.Size; lx++)
                {
                    int wx = baseX + lx;
                    int wz = baseZ + lz;
                    int surface = SurfaceHeight(wx, wz);

                    int ly = surface + 1 - baseY;          // plant sits one block above the grass
                    if (ly < 0 || ly >= VoxelChunk.Size) continue;
                    if (chunk.Get(lx, ly, lz) != VoxelBlock.Air) continue;   // don't overwrite trunks/leaves

                    // Surface must be plain grass (no plants on sand/snow/beach).
                    float temp = Noise.Fbm2(Seed + 100, wx * 0.0015f, wz * 0.0015f, 3);
                    float moisture = Noise.Fbm2(Seed + 200, wx * 0.0015f + 500f, wz * 0.0015f, 3);
                    bool beach = surface <= SeaLevel + 2 && moisture > 0.45f;
                    bool snowy = surface > SeaLevel + 28 || temp < 0.32f;
                    if (SampleColumn(wx, surface, wz, surface, temp, moisture, beach, snowy) != VoxelBlock.Grass)
                        continue;

                    // ~9% of eligible columns get a plant; patches cluster via low-frequency noise.
                    float density = Noise.Value(Seed + 1300, wx * 0.42f, wz * 0.42f);
                    if (density < 0.91f) continue;

                    float pick = Noise.Value(Seed + 1400, wx * 0.73f, wz * 0.61f);
                    VoxelBlock plant =
                        pick < 0.46f ? VoxelBlock.Poppy :
                        pick < 0.90f ? VoxelBlock.Bluebell :
                        pick < 0.96f ? VoxelBlock.OakSapling :
                                       VoxelBlock.BirchSapling;

                    chunk.Set(lx, ly, lz, plant);
                }
            }
        }

        private VoxelBlock SampleColumn(int wx, int wy, int wz, int surface, float temp, float moisture, bool beach, bool snowy)
        {
            if (wy <= 0)
                return wy < BedrockDepth ? VoxelBlock.Bedrock : VoxelBlock.Stone;

            if (wy > surface && wy <= SeaLevel)
                return VoxelBlock.Water;

            if (wy > surface)
                return VoxelBlock.Air;

            if (GenerateCaves && wy > 8 && wy < surface - 2)
            {
                float cave = Noise.Fbm3(Seed + 400, wx * 0.045f, wy * 0.045f, wz * 0.045f, 3);
                if (cave > 0.68f)
                    return wy <= SeaLevel ? VoxelBlock.Water : VoxelBlock.Air;
            }

            int depth = surface - wy;
            if (depth == 0)
            {
                if (beach || (surface <= SeaLevel + 1 && moisture > 0.4f))
                    return VoxelBlock.Sand;
                if (snowy)
                    return VoxelBlock.Snow;
                if (temp > 0.62f && moisture < 0.38f)
                    return VoxelBlock.Sand;
                return VoxelBlock.Grass;
            }

            if (depth <= 3)
                return beach ? VoxelBlock.Sand : VoxelBlock.Dirt;

            if (depth <= 6 && moisture > 0.55f)
                return VoxelBlock.Clay;

            if (wy < 24 && Noise.Value(Seed + 900, wx * 0.17f, wz * 0.17f) > 0.93f)
                return VoxelBlock.Gravel;

            return VoxelBlock.Stone;
        }

        public int GetSurfaceHeight(int wx, int wz) => SurfaceHeight(wx, wz);

        private int SurfaceHeight(int wx, int wz)
        {
            float continent = Noise.Fbm2(Seed, wx * 0.0018f, wz * 0.0018f, 5);
            float hills = Noise.Fbm2(Seed + 50, wx * 0.008f, wz * 0.008f, 4) * 18f;
            float ridges = Noise.Fbm2(Seed + 80, wx * 0.015f, wz * 0.015f, 3) * 8f;
            float mountains = MathF.Max(0f, continent - 0.55f) * 55f;

            int height = (int)MathF.Round(SeaLevel - 14f + continent * 30f + hills + ridges + mountains);
            return Math.Clamp(height, 4, MaxSurfaceHeight);
        }

        private void ScatterTrees(VoxelChunk chunk, int baseX, int baseY, int baseZ)
        {
            for (int lz = 2; lz < VoxelChunk.Size - 2; lz++)
            {
                for (int lx = 2; lx < VoxelChunk.Size - 2; lx++)
                {
                    int wx = baseX + lx;
                    int wz = baseZ + lz;
                    int surface = SurfaceHeight(wx, wz);
                    if (surface < SeaLevel + 2 || surface > MaxSurfaceHeight - 8)
                        continue;

                    float treeChance = Noise.Value(Seed + 700, wx * 0.31f, wz * 0.31f);
                    if (treeChance < 0.965f)
                        continue;

                    float temp = Noise.Fbm2(Seed + 100, wx * 0.0015f, wz * 0.0015f, 3);
                    if (temp < 0.34f || surface <= SeaLevel + 1)
                        continue;

                    int trunkBase = surface + 1;
                    if (trunkBase < baseY || trunkBase + 5 >= baseY + VoxelChunk.Size)
                        continue;

                    int ly = trunkBase - baseY;
                    for (int h = 0; h < 5; h++)
                        chunk.Set(lx, ly + h, lz, VoxelBlock.Wood);

                    int crownY = ly + 4;
                    for (int dz = -2; dz <= 2; dz++)
                    for (int dy = -1; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) > 4) continue;
                        chunk.Set(lx + dx, crownY + dy, lz + dz, VoxelBlock.Leaves);
                    }
                }
            }
        }
    }
}
