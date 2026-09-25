using System;

namespace Genesis.World
{
    /// <summary>
    /// Converts render/simulation distance (in chunk columns) to world-space horizons.
    /// Render distance N sets the camera far plane to N × <see cref="VoxelChunk.Size"/> blocks
    /// (e.g. 32 chunks → 512 m), which drives frustum culling, streaming load/unload, LOD, and fog.
    /// </summary>
    public static class RenderDistance
    {
        public static float FarPlaneBlocks(int renderDistanceChunks)
            => Math.Max(1, renderDistanceChunks) * VoxelChunk.Size;

        public static float SimulationHorizonBlocks(int simulationDistanceChunks)
            => Math.Max(1, simulationDistanceChunks) * VoxelChunk.Size;

        public static int ChunksFromFarPlane(float farPlaneBlocks)
            => Math.Max(1, (int)MathF.Floor(farPlaneBlocks / VoxelChunk.Size));
    }
}
