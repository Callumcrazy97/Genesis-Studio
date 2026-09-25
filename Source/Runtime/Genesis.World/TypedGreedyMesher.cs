using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.World
{
    /// <summary>
    /// Greedy mesher: mask keyed by block + face + tint; UVs tiled per block via
    /// <see cref="ChunkMesher.MapFaceUV"/> (atlas sampler wraps).
    /// </summary>
    internal static class TypedGreedyMesher
    {
        private static readonly Vector3[] FaceNormals =
        {
            Vector3.UnitX, -Vector3.UnitX,
            Vector3.UnitY, -Vector3.UnitY,
            Vector3.UnitZ, -Vector3.UnitZ,
        };

        public static void BuildSolid(VoxelSnapshot snapshot, VoxelPalette palette,
            List<MeshVertex> vertices, List<ushort> indices) =>
            BuildSolidRect(snapshot, palette, vertices, indices, 0, 0, snapshot.SizeX, snapshot.SizeZ);

        public static void BuildSolid(VoxelSnapshotSubView view, VoxelPalette palette,
            List<MeshVertex> vertices, List<ushort> indices) =>
            BuildSolidRect(view.Parent, palette, vertices, indices, view.Ox, view.Oz, view.SizeX, view.SizeZ);

        private static void BuildSolidRect(VoxelSnapshot snapshot, VoxelPalette palette,
            List<MeshVertex> vertices, List<ushort> indices, int originX, int originZ, int sizeX, int sizeZ)
        {
            int[] sizes = { sizeX, snapshot.SizeY, sizeZ };
            var mask = new int[sizeX * sizeZ];
            var flip = new bool[sizeX * sizeZ];
            var pos = new int[3];

            VoxelBlock GetBlock(int x, int y, int z) => snapshot.Get(x, y, z);
            bool IsMissing(int x, int y, int z) => snapshot.IsRegionChunkMissing(x, y, z);

            for (int d = 0; d < 3; d++)
            {
                int u = (d + 1) % 3;
                int v = (d + 2) % 3;
                int maskW = sizes[u];
                int maskH = sizes[v];
                if (mask.Length < maskW * maskH)
                {
                    mask = new int[maskW * maskH];
                    flip = new bool[maskW * maskH];
                }
                int faceIdx = d * 2;

                for (int slice = -1; slice < sizes[d]; slice++)
                {
                    Array.Clear(mask, 0, maskW * maskH);
                    Array.Clear(flip, 0, maskW * maskH);

                    for (pos[v] = 0; pos[v] < sizes[v]; pos[v]++)
                    {
                        for (pos[u] = 0; pos[u] < sizes[u]; pos[u]++)
                        {
                            int idx = pos[u] + pos[v] * maskW;
                            pos[d] = slice;
                            VoxelBlock a = GetBlock(pos[0], pos[1], pos[2]);

                            pos[d] = slice + 1;
                            VoxelBlock b = GetBlock(pos[0], pos[1], pos[2]);
                            pos[d] = slice;

                            mask[idx] = 0;
                            flip[idx] = false;

                            if (IsSolid(a) && ShouldRenderFace(IsMissing, pos, d, slice, a, b, blockAtLowerSlice: true))
                            {
                                pos[d] = slice;
                                mask[idx] = PackKey(snapshot, pos, a, faceIdx, palette);
                                flip[idx] = false;
                            }
                            else if (IsSolid(b) && ShouldRenderFace(IsMissing, pos, d, slice, b, a, blockAtLowerSlice: false))
                            {
                                pos[d] = slice + 1;
                                mask[idx] = PackKey(snapshot, pos, b, faceIdx ^ 1, palette);
                                flip[idx] = true;
                                pos[d] = slice;
                            }
                        }
                    }

                    for (int j = 0; j < maskH; j++)
                    {
                        for (int i = 0; i < maskW; )
                        {
                            int idx = i + j * maskW;
                            int key = mask[idx];
                            if (key == 0) { i++; continue; }

                            bool f0 = flip[idx];
                            int w = 1;
                            while (i + w < maskW && mask[i + w + j * maskW] == key && flip[i + w + j * maskW] == f0)
                                w++;

                            int h = 1;
                            bool done = false;
                            while (j + h < maskH)
                            {
                                for (int k = 0; k < w; k++)
                                {
                                    int id2 = i + k + (j + h) * maskW;
                                    if (mask[id2] != key || flip[id2] != f0) { done = true; break; }
                                }
                                if (done) break;
                                h++;
                            }

                            for (int dj = 0; dj < h; dj++)
                            for (int di = 0; di < w; di++)
                                mask[i + di + (j + dj) * maskW] = 0;

                            UnpackKey(key, out VoxelBlock block, out int emitFace);
                            EmitQuad(snapshot, palette, d, u, v, slice, emitFace, i, j, w, h, f0, block,
                                FaceNormals[emitFace], vertices, indices);
                            i += w;
                        }
                    }
                }
            }
        }

        private static bool IsSolid(VoxelBlock b) =>
            b != VoxelBlock.Air
            && !VoxelBlocks.IsLiquid(b)
            && !VoxelBlocks.IsCross(b)
            && !VoxelBlocks.IsCarpet(b)
            && !VoxelBlocks.IsBed(b);

        private static bool ShouldRenderFace(VoxelBlock block, VoxelBlock neighbor)
        {
            if (neighbor == VoxelBlock.Air) return true;
            if (VoxelBlocks.IsLiquid(neighbor)) return true;
            return !VoxelBlocks.IsOpaque(neighbor);
        }

        private static bool ShouldRenderFace(Func<int, int, int, bool> isMissing, int[] pos, int d, int slice,
            VoxelBlock block, VoxelBlock neighbor, bool blockAtLowerSlice)
        {
            if (neighbor == VoxelBlock.Air)
            {
                int nx = pos[0], ny = pos[1], nz = pos[2];
                if (blockAtLowerSlice)
                {
                    if (d == 0) nx++;
                    else if (d == 1) ny++;
                    else nz++;
                }

                if (isMissing(nx, ny, nz))
                    return true;
            }

            return ShouldRenderFace(block, neighbor);
        }

        private static int PackKey(VoxelSnapshot snapshot, int[] pos, VoxelBlock block, int face, VoxelPalette palette)
        {
            int wy = snapshot.ChunkY * VoxelChunk.Size + pos[1];
            Vector4 color = face == 2 ? palette.Top(block, wy) : palette.Side(block);
            if (palette.UvLookup != null)
            {
                var uv = palette.UvLookup(block, face);
                color = ChunkMesher.PackAtlasBounds(uv.Min, uv.Max);
            }

            int cq = ((int)(color.X * 31) & 31) << 24
                   | ((int)(color.Y * 31) & 31) << 19
                   | ((int)(color.Z * 31) & 31) << 14
                   | ((int)(color.W * 7) & 7) << 11;
            return ((int)block & 0xFF) | (face << 8) | cq;
        }

        private static void UnpackKey(int key, out VoxelBlock block, out int face)
        {
            block = (VoxelBlock)(key & 0xFF);
            face = (key >> 8) & 7;
        }

        private static void EmitQuad(VoxelSnapshot snapshot, VoxelPalette palette,
            int d, int u, int v, int slice, int emitFace, int i, int j, int w, int h, bool flipped,
            VoxelBlock block, Vector3 normal, List<MeshVertex> vertices, List<ushort> indices)
        {
            var bp = new int[3];
            bp[u] = i;
            bp[v] = j;
            bp[d] = flipped ? slice + 1 : slice;

            int bx = bp[0], by = bp[1], bz = bp[2];
            int wy = snapshot.ChunkY * VoxelChunk.Size + by;

            Vector4 color = emitFace == 2 ? palette.Top(block, wy) : palette.Side(block);
            Vector2 uvMin = new(0, 0), uvMax = new(1, 1);
            if (palette.UvLookup != null)
            {
                var uv = palette.UvLookup(block, emitFace);
                uvMin = uv.Min;
                uvMax = uv.Max;
                color = ChunkMesher.PackAtlasBounds(uvMin, uvMax);
            }

            Vector3 origin = new(bx, by, bz);
            Span<Vector3> corners = stackalloc Vector3[4];
            BuildCorners(emitFace, bx, by, bz, w, h, u, v, corners);
            ChunkMesher.AppendFaceQuad(vertices, indices, corners, normal, color, emitFace, origin, uvMin, uvMax);
        }

        private static void BuildCorners(int face, int bx, int by, int bz, int w, int h, int u, int v, Span<Vector3> corners)
        {
            float ew = ExtentAlong(face, u, v, w, h, 0);
            float eh = ExtentAlong(face, u, v, w, h, 1);

            switch (face)
            {
                case 0: // +X
                    corners[0] = new Vector3(bx + 1, by, bz);
                    corners[1] = new Vector3(bx + 1, by + ew, bz);
                    corners[2] = new Vector3(bx + 1, by + ew, bz + eh);
                    corners[3] = new Vector3(bx + 1, by, bz + eh);
                    break;
                case 1: // -X
                    corners[0] = new Vector3(bx, by, bz + eh);
                    corners[1] = new Vector3(bx, by + ew, bz + eh);
                    corners[2] = new Vector3(bx, by + ew, bz);
                    corners[3] = new Vector3(bx, by, bz);
                    break;
                case 2: // +Y
                    corners[0] = new Vector3(bx, by + 1, bz + eh);
                    corners[1] = new Vector3(bx + ew, by + 1, bz + eh);
                    corners[2] = new Vector3(bx + ew, by + 1, bz);
                    corners[3] = new Vector3(bx, by + 1, bz);
                    break;
                case 3: // -Y
                    corners[0] = new Vector3(bx, by, bz);
                    corners[1] = new Vector3(bx + ew, by, bz);
                    corners[2] = new Vector3(bx + ew, by, bz + eh);
                    corners[3] = new Vector3(bx, by, bz + eh);
                    break;
                case 4: // +Z
                    corners[0] = new Vector3(bx + ew, by, bz + 1);
                    corners[1] = new Vector3(bx + ew, by + eh, bz + 1);
                    corners[2] = new Vector3(bx, by + eh, bz + 1);
                    corners[3] = new Vector3(bx, by, bz + 1);
                    break;
                default: // -Z
                    corners[0] = new Vector3(bx, by, bz);
                    corners[1] = new Vector3(bx, by + eh, bz);
                    corners[2] = new Vector3(bx + ew, by + eh, bz);
                    corners[3] = new Vector3(bx + ew, by, bz);
                    break;
            }
        }

        private static float ExtentAlong(int face, int u, int v, int w, int h, int tangentIndex)
        {
            int axisA, axisB;
            switch (face)
            {
                case 0:
                case 1: axisA = 1; axisB = 2; break;
                case 2:
                case 3: axisA = 0; axisB = 2; break;
                default: axisA = 0; axisB = 1; break;
            }

            int want = tangentIndex == 0 ? axisA : axisB;
            if (want == u) return w;
            if (want == v) return h;
            return tangentIndex == 0 ? h : w;
        }
    }
}
