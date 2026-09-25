using System;
using System.Collections.Concurrent;

namespace Genesis.World
{
    /// <summary>Pools halo snapshot buffers to cut GC pressure during chunk remeshing.</summary>
    internal static class VoxelSnapshotPool
    {
        private static readonly ConcurrentBag<VoxelBlock[]> _blocks = new();
        private static readonly ConcurrentBag<bool[]> _missing = new();

        public static (VoxelBlock[] blocks, bool[] missing) Rent()
        {
            if (!_blocks.TryTake(out VoxelBlock[] blocks))
                blocks = new VoxelBlock[VoxelSnapshot.Volume];
            else
                Array.Clear(blocks, 0, blocks.Length);

            if (!_missing.TryTake(out bool[] missing))
                missing = new bool[VoxelSnapshot.Volume];
            else
                Array.Clear(missing, 0, missing.Length);

            return (blocks, missing);
        }

        public static void Return(VoxelBlock[] blocks, bool[] missing)
        {
            if (blocks != null && blocks.Length == VoxelSnapshot.Volume)
                _blocks.Add(blocks);
            if (missing != null && missing.Length == VoxelSnapshot.Volume)
                _missing.Add(missing);
        }
    }
}
