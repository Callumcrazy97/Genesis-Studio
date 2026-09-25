namespace Genesis.World
{
    /// <summary>Fills a chunk with procedurally generated or loaded voxel data.</summary>
    public interface IVoxelGenerator
    {
        void GetColumnChunkRange(int chunkX, int chunkZ, out int minChunkY, out int maxChunkY);
        void Generate(VoxelChunk chunk);
    }
}
