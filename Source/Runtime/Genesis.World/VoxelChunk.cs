using System;

namespace Genesis.World
{
    /// <summary>
    /// A fixed 16³ block column. Coordinates are local 0..15 on each axis.
    /// World Y is the vertical axis (Minecraft-style).
    /// </summary>
    public sealed class VoxelChunk
    {
        public const int Size = 16;
        public const int Volume = Size * Size * Size;

        private readonly VoxelBlock[] _blocks = new VoxelBlock[Volume];

        public int ChunkX { get; }
        public int ChunkY { get; }
        public int ChunkZ { get; }

        public bool Dirty { get; set; } = true;

        public VoxelChunk(int chunkX, int chunkY, int chunkZ)
        {
            ChunkX = chunkX;
            ChunkY = chunkY;
            ChunkZ = chunkZ;
        }

        public VoxelBlock Get(int x, int y, int z) =>
            InBounds(x, y, z) ? _blocks[Index(x, y, z)] : VoxelBlock.Air;

        public void Set(int x, int y, int z, VoxelBlock block)
        {
            if (!InBounds(x, y, z)) return;
            int i = Index(x, y, z);
            if (_blocks[i] == block) return;
            _blocks[i] = block;
            Dirty = true;
        }

        public void Fill(VoxelBlock block)
        {
            Array.Fill(_blocks, block);
            Dirty = true;
        }

        public static int Index(int x, int y, int z) => x + Size * (y + Size * z);

        public static bool InBounds(int x, int y, int z) =>
            (uint)x < Size && (uint)y < Size && (uint)z < Size;

        public ReadOnlySpan<VoxelBlock> Blocks => _blocks;
    }
}
