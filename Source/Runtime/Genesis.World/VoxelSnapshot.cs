namespace Genesis.World
{
    /// <summary>
    /// Single-lock snapshot of a chunk plus a one-block halo for cross-chunk face culling.
    /// </summary>
    public sealed class VoxelSnapshot
    {
        public const int Halo = 1;
        public const int Stride = VoxelChunk.Size + Halo * 2;
        public const int Volume = Stride * Stride * Stride;

        public int ChunkX { get; }
        public int ChunkY { get; }
        public int ChunkZ { get; }
        public int SizeX => VoxelChunk.Size;
        public int SizeY => VoxelChunk.Size;
        public int SizeZ => VoxelChunk.Size;
        public VoxelBlock[] Blocks { get; }
        public bool[] MissingChunks { get; }

        internal VoxelSnapshot(int cx, int cy, int cz, VoxelBlock[] blocks, bool[] missingChunks = null)
        {
            ChunkX = cx;
            ChunkY = cy;
            ChunkZ = cz;
            Blocks = blocks;
            MissingChunks = missingChunks;
        }

        public void ReturnToPool() => VoxelSnapshotPool.Return(Blocks, MissingChunks);

        public VoxelBlock Get(int lx, int ly, int lz) => Blocks[Index(lx + Halo, ly + Halo, lz + Halo)];

        public bool IsRegionChunkMissing(int lx, int ly, int lz)
        {
            if (MissingChunks == null) return false;
            return MissingChunks[Index(lx + Halo, ly + Halo, lz + Halo)];
        }

        /// <summary>Builds a test snapshot with optional interior fill (halo stays air).</summary>
        public static VoxelSnapshot ForTesting(int cx, int cy, int cz, VoxelBlock interiorFill = VoxelBlock.Air)
        {
            var blocks = new VoxelBlock[Volume];
            var missing = new bool[Volume];
            if (interiorFill != VoxelBlock.Air)
            {
                for (int z = 0; z < VoxelChunk.Size; z++)
                for (int y = 0; y < VoxelChunk.Size; y++)
                for (int x = 0; x < VoxelChunk.Size; x++)
                    blocks[Index(x + Halo, y + Halo, z + Halo)] = interiorFill;
            }
            return new VoxelSnapshot(cx, cy, cz, blocks, missing);
        }

        public static int Index(int x, int y, int z) => x + Stride * (y + Stride * z);
    }

    public readonly struct VoxelSnapshotSubView
    {
        public VoxelSnapshot Parent { get; }
        public int Ox { get; }
        public int Oz { get; }
        public int SizeX { get; }
        public int SizeZ { get; }

        public VoxelSnapshotSubView(VoxelSnapshot parent, int ox, int oz, int sizeX, int sizeZ)
        {
            Parent = parent;
            Ox = ox;
            Oz = oz;
            SizeX = sizeX;
            SizeZ = sizeZ;
        }
    }
}
