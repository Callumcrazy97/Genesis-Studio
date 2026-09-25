using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Streaming;

namespace Genesis.World
{
    /// <summary>
    /// Sparse voxel storage keyed by chunk coordinate. Thread-safe reads and writes.
    /// </summary>
    public sealed class VoxelWorld
    {
        private readonly Dictionary<(int x, int y, int z), VoxelChunk> _chunks = new Dictionary<(int, int, int), VoxelChunk>();
        private readonly object _gate = new object();

        public event Action<int, int, int, VoxelBlock> BlockChanged;

        public int ChunkCount
        {
            get { lock (_gate) return _chunks.Count; }
        }

        public VoxelBlock GetBlock(int wx, int wy, int wz)
        {
            if (!WorldToChunk(wx, wy, wz, out int cx, out int cy, out int cz, out int lx, out int ly, out int lz))
                return VoxelBlock.Air;

            lock (_gate)
            {
                return _chunks.TryGetValue((cx, cy, cz), out VoxelChunk chunk)
                    ? chunk.Get(lx, ly, lz)
                    : VoxelBlock.Air;
            }
        }

        public void SetBlock(int wx, int wy, int wz, VoxelBlock block) =>
            TrySetBlock(wx, wy, wz, block, out _);

        public bool TrySetBlock(int wx, int wy, int wz, VoxelBlock block, out VoxelBlock previous)
        {
            previous = VoxelBlock.Air;
            if (!WorldToChunk(wx, wy, wz, out int cx, out int cy, out int cz, out int lx, out int ly, out int lz))
                return false;

            lock (_gate)
            {
                if (!_chunks.TryGetValue((cx, cy, cz), out VoxelChunk chunk))
                    return false;

                previous = chunk.Get(lx, ly, lz);
                if (previous == block) return true;

                chunk.Set(lx, ly, lz, block);
                MarkBorderNeighborsDirty(chunk, lx, ly, lz);
                BlockChanged?.Invoke(wx, wy, wz, block);
                return true;
            }
        }

        public VoxelChunk GetOrCreateChunk(int cx, int cy, int cz)
        {
            lock (_gate)
                return GetOrCreateChunkUnlocked(cx, cy, cz);
        }

        public void AddGeneratedChunk(VoxelChunk chunk)
        {
            lock (_gate)
            {
                var key = (chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ);
                if (!_chunks.ContainsKey(key))
                    _chunks.Add(key, chunk);
            }
        }

        public bool TryGetChunk(int cx, int cy, int cz, out VoxelChunk chunk)
        {
            lock (_gate)
                return _chunks.TryGetValue((cx, cy, cz), out chunk);
        }

        public bool HasChunk(int cx, int cy, int cz)
        {
            lock (_gate)
                return _chunks.ContainsKey((cx, cy, cz));
        }

        public bool RemoveChunk(int cx, int cy, int cz)
        {
            lock (_gate)
                return _chunks.Remove((cx, cy, cz));
        }

        public VoxelSnapshot TryCaptureSnapshot(int cx, int cy, int cz)
        {
            lock (_gate)
            {
                if (!_chunks.TryGetValue((cx, cy, cz), out VoxelChunk chunk))
                    return null;

                var blocks = VoxelSnapshotPool.Rent();
                int baseX = cx * VoxelChunk.Size;
                int baseY = cy * VoxelChunk.Size;
                int baseZ = cz * VoxelChunk.Size;

                for (int lz = -VoxelSnapshot.Halo; lz < VoxelChunk.Size + VoxelSnapshot.Halo; lz++)
                {
                    for (int ly = -VoxelSnapshot.Halo; ly < VoxelChunk.Size + VoxelSnapshot.Halo; ly++)
                    {
                        for (int lx = -VoxelSnapshot.Halo; lx < VoxelChunk.Size + VoxelSnapshot.Halo; lx++)
                        {
                            int idx = VoxelSnapshot.Index(lx + VoxelSnapshot.Halo, ly + VoxelSnapshot.Halo, lz + VoxelSnapshot.Halo);
                            if (VoxelChunk.InBounds(lx, ly, lz))
                            {
                                blocks.blocks[idx] = chunk.Get(lx, ly, lz);
                                blocks.missing[idx] = false;
                            }
                            else
                            {
                                WorldToChunk(baseX + lx, baseY + ly, baseZ + lz,
                                    out int ncx, out int ncy, out int ncz, out int nlx, out int nly, out int nlz);
                                bool loaded = _chunks.TryGetValue((ncx, ncy, ncz), out VoxelChunk neighbor);
                                blocks.missing[idx] = !loaded;
                                blocks.blocks[idx] = loaded ? neighbor.Get(nlx, nly, nlz) : VoxelBlock.Air;
                            }
                        }
                    }
                }

                return new VoxelSnapshot(cx, cy, cz, blocks.blocks, blocks.missing);
            }
        }

        private VoxelBlock GetBlockUnlocked(int wx, int wy, int wz)
        {
            WorldToChunk(wx, wy, wz, out int cx, out int cy, out int cz, out int lx, out int ly, out int lz);
            return _chunks.TryGetValue((cx, cy, cz), out VoxelChunk chunk) ? chunk.Get(lx, ly, lz) : VoxelBlock.Air;
        }

        public static BoundingBox ChunkBounds(int cx, int cy, int cz)
        {
            Vector3 min = ChunkOrigin(cx, cy, cz);
            return new BoundingBox(min, min + new Vector3(VoxelChunk.Size));
        }

        public void MarkChunkDirty(int cx, int cy, int cz)
        {
            lock (_gate)
            {
                if (_chunks.TryGetValue((cx, cy, cz), out VoxelChunk chunk))
                    chunk.Dirty = true;
            }
        }

        private void MarkBorderNeighborsDirty(VoxelChunk chunk, int lx, int ly, int lz)
        {
            if (lx == 0) MarkChunkDirty(chunk.ChunkX - 1, chunk.ChunkY, chunk.ChunkZ);
            if (lx == VoxelChunk.Size - 1) MarkChunkDirty(chunk.ChunkX + 1, chunk.ChunkY, chunk.ChunkZ);
            if (ly == 0) MarkChunkDirty(chunk.ChunkX, chunk.ChunkY - 1, chunk.ChunkZ);
            if (ly == VoxelChunk.Size - 1) MarkChunkDirty(chunk.ChunkX, chunk.ChunkY + 1, chunk.ChunkZ);
            if (lz == 0) MarkChunkDirty(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ - 1);
            if (lz == VoxelChunk.Size - 1) MarkChunkDirty(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ + 1);
        }

        private VoxelChunk GetOrCreateChunkUnlocked(int cx, int cy, int cz)
        {
            var key = (cx, cy, cz);
            if (!_chunks.TryGetValue(key, out VoxelChunk chunk))
            {
                chunk = new VoxelChunk(cx, cy, cz);
                _chunks.Add(key, chunk);
            }
            return chunk;
        }

        public static bool WorldToChunk(int wx, int wy, int wz,
            out int cx, out int cy, out int cz, out int lx, out int ly, out int lz)
        {
            cx = FloorDiv(wx, VoxelChunk.Size);
            cy = FloorDiv(wy, VoxelChunk.Size);
            cz = FloorDiv(wz, VoxelChunk.Size);
            lx = Mod(wx, VoxelChunk.Size);
            ly = Mod(wy, VoxelChunk.Size);
            lz = Mod(wz, VoxelChunk.Size);
            return true;
        }

        public static Vector3 ChunkOrigin(int cx, int cy, int cz) =>
            new Vector3(cx * VoxelChunk.Size, cy * VoxelChunk.Size, cz * VoxelChunk.Size);

        public static Vector3 BlockCenter(int wx, int wy, int wz) =>
            new Vector3(wx + 0.5f, wy + 0.5f, wz + 0.5f);

        private static int FloorDiv(int value, int size)
        {
            int q = value / size;
            int r = value % size;
            if (r < 0) q--;
            return q;
        }

        private static int Mod(int value, int size)
        {
            int r = value % size;
            return r < 0 ? r + size : r;
        }
    }
}
