using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.World
{
    public struct ChunkMeshData
    {
        public MeshData Solid;
        public MeshData Liquid;
        public MeshData Cross;
    }

    public struct MeshData
    {
        public MeshVertex[] Vertices;
        public ushort[] Indices;
    }

    /// <summary>Builds render meshes from chunks — exposed faces only, separate liquid pass.</summary>
    public static class ChunkMesher
    {
        /// <summary>When true, solid faces use the typed greedy mesher (test harness toggle).</summary>
        public static bool UseTypedGreedySolid { get; set; }

        private static readonly Vector3[] FaceNormals =
        {
            Vector3.UnitX, -Vector3.UnitX,
            Vector3.UnitY, -Vector3.UnitY,
            Vector3.UnitZ, -Vector3.UnitZ,
        };

        private static readonly (int dx, int dy, int dz)[] FaceOffsets =
        {
            (1, 0, 0), (-1, 0, 0),
            (0, 1, 0), (0, -1, 0),
            (0, 0, 1), (0, 0, -1),
        };

        public static ChunkMeshData Build(VoxelWorld world, VoxelChunk chunk, VoxelPalette palette)
        {
            VoxelSnapshot snapshot = world.TryCaptureSnapshot(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ);
            if (snapshot == null)
                return default;
            return BuildFromSnapshot(snapshot, palette);
        }

        public static ChunkMeshData BuildFromSnapshot(VoxelSnapshot snapshot, VoxelPalette palette)
        {
            MeshBuildScratch s = MeshBuildScratch.Get();
            s.Clear();

            for (int z = 0; z < VoxelChunk.Size; z++)
            {
                for (int y = 0; y < VoxelChunk.Size; y++)
                {
                    for (int x = 0; x < VoxelChunk.Size; x++)
                    {
                        VoxelBlock block = snapshot.Get(x, y, z);
                        if (block == VoxelBlock.Air) continue;

                        int wy = snapshot.ChunkY * VoxelChunk.Size + y;
                        bool liquid = VoxelBlocks.IsLiquid(block);

                        if (VoxelBlocks.IsCross(block))
                        {
                            if (palette.SkipCrossMeshLookup == null || !palette.SkipCrossMeshLookup(block))
                            {
                                if (block == VoxelBlock.Torch)
                                    AddTorchCross(s.CrossVerts, s.CrossIndices, x, y, z, palette);
                                else
                                    AddCross(s.CrossVerts, s.CrossIndices, x, y, z, block, palette);
                            }
                            continue;
                        }

                        if (VoxelBlocks.IsCarpet(block))
                        {
                            AddCarpet(s.SolidVerts, s.SolidIndices, x, y, z, block, palette, wy);
                            continue;
                        }

                        if (VoxelBlocks.IsBed(block))
                        {
                            AddBed(snapshot, s.SolidVerts, s.SolidIndices, x, y, z, block, palette, wy);
                            continue;
                        }

                        if (!liquid)
                        {
                            for (int face = 0; face < 6; face++)
                            {
                                (int dx, int dy, int dz) = FaceOffsets[face];
                                int nx = x + dx, ny = y + dy, nz = z + dz;
                                VoxelBlock neighbor = snapshot.Get(nx, ny, nz);
                                // Missing streaming neighbours should not erase the world shell.
                                // Prefer temporary overdraw at chunk borders over suppressing every
                                // exterior face in a fully solid surface chunk.
                                if (neighbor != VoxelBlock.Air && !VoxelBlocks.IsLiquid(neighbor) && VoxelBlocks.IsOpaque(neighbor))
                                    continue;
                                AddFace(s.SolidVerts, s.SolidIndices, x, y, z, face, block, wy, palette, null);
                            }
                            continue;
                        }

                        for (int face = 0; face < 6; face++)
                        {
                            (int dx, int dy, int dz) = FaceOffsets[face];
                            VoxelBlock neighbor = snapshot.Get(x + dx, y + dy, z + dz);
                            if (!ShouldRenderLiquidFace(block, neighbor)) continue;
                            VoxelBlock floor = snapshot.Get(x, y - 1, z);
                            Vector4 waterTint = palette.WaterFloorTintLookup != null
                                ? palette.WaterFloorTintLookup(floor)
                                : palette.Top(block, wy);
                            AddLiquidFace(s.LiquidVerts, s.LiquidIndices, snapshot, x, y, z, face, block, wy, palette, waterTint);
                        }
                    }
                }
            }

            if (UseTypedGreedySolid)
                TypedGreedyMesher.BuildSolid(snapshot, palette, s.SolidVerts, s.SolidIndices);

            return new ChunkMeshData
            {
                Solid = new MeshData { Vertices = s.SolidVerts.ToArray(), Indices = s.SolidIndices.ToArray() },
                Liquid = new MeshData { Vertices = s.LiquidVerts.ToArray(), Indices = s.LiquidIndices.ToArray() },
                Cross = new MeshData { Vertices = s.CrossVerts.ToArray(), Indices = s.CrossIndices.ToArray() },
            };
        }

        /// <summary>
        /// Distance LOD: groups S×S×S blocks (S = 2^lod) into macro-cells and emits one S-sized face
        /// per exposed macro-face — far fewer triangles than full res. Liquid/cross detail is dropped
        /// (already distance-culled). Border faces sample the 1-block halo so adjacent chunks stay
        /// watertight. Reuses the same atlas-tiled quad emission as the greedy mesher.
        /// </summary>
        public static ChunkMeshData BuildLod(VoxelSnapshot snapshot, VoxelPalette palette, int lod)
        {
            MeshBuildScratch s = MeshBuildScratch.Get();
            s.LodVerts.Clear();
            s.LodIndices.Clear();
            s.LodCounts.Clear();

            var verts = s.LodVerts;
            var indices = s.LodIndices;
            var counts = s.LodCounts;
            int S = 1 << Math.Clamp(lod, 1, 3);
            int G = VoxelChunk.Size / S;

            for (int mz = 0; mz < G; mz++)
                for (int my = 0; my < G; my++)
                    for (int mx = 0; mx < G; mx++)
                    {
                        VoxelBlock rep = LodDominant(snapshot, mx * S, my * S, mz * S, S, counts);
                        if (rep == VoxelBlock.Air) continue;
                        int wy = snapshot.ChunkY * VoxelChunk.Size + my * S;
                        for (int face = 0; face < 6; face++)
                        {
                            if (LodNeighborOccludes(snapshot, mx, my, mz, S, G, face, counts)) continue;
                            AppendLodFace(verts, indices, mx * S, my * S, mz * S, S, face, rep, wy, palette);
                        }
                    }

            var empty = new MeshData { Vertices = Array.Empty<MeshVertex>(), Indices = Array.Empty<ushort>() };
            return new ChunkMeshData
            {
                Solid = new MeshData { Vertices = verts.ToArray(), Indices = indices.ToArray() },
                Liquid = empty,
                Cross = empty,
            };
        }

        private static VoxelBlock LodDominant(VoxelSnapshot snap, int ox, int oy, int oz, int S, Dictionary<VoxelBlock, int> counts)
        {
            counts.Clear();
            for (int z = 0; z < S; z++)
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        VoxelBlock b = snap.Get(ox + x, oy + y, oz + z);
                        if (b == VoxelBlock.Air || !VoxelBlocks.IsSolid(b)) continue;
                        if (VoxelBlocks.IsCross(b) || VoxelBlocks.IsCarpet(b) || VoxelBlocks.IsBed(b)) continue;
                        counts.TryGetValue(b, out int c);
                        counts[b] = c + 1;
                    }
            VoxelBlock best = VoxelBlock.Air;
            int bestC = 0;
            foreach (var kv in counts)
                if (kv.Value > bestC) { bestC = kv.Value; best = kv.Key; }
            return best;
        }

        private static bool LodNeighborOccludes(VoxelSnapshot snap, int mx, int my, int mz, int S, int G, int face, Dictionary<VoxelBlock, int> counts)
        {
            int nx = mx, ny = my, nz = mz;
            switch (face) { case 0: nx++; break; case 1: nx--; break; case 2: ny++; break; case 3: ny--; break; case 4: nz++; break; default: nz--; break; }

            int rx = Math.Clamp(nx * S, -1, VoxelChunk.Size);
            int ry = Math.Clamp(ny * S, -1, VoxelChunk.Size);
            int rz = Math.Clamp(nz * S, -1, VoxelChunk.Size);

            if (snap.IsRegionChunkMissing(rx, ry, rz))
                return false;
            
            if (nx >= 0 && nx < G && ny >= 0 && ny < G && nz >= 0 && nz < G)
                return LodDominant(snap, nx * S, ny * S, nz * S, S, counts) != VoxelBlock.Air;

            // Border macro-cell: sample the 1-block halo across the whole face. Occluded only if
            // every halo block is opaque, otherwise emit (prefer overdraw over holes).
            for (int b = 0; b < S; b++)
                for (int a = 0; a < S; a++)
                {
                    int sx, sy, sz;
                    switch (face)
                    {
                        case 0:  sx = VoxelChunk.Size; sy = my * S + a; sz = mz * S + b; break;
                        case 1:  sx = -1;              sy = my * S + a; sz = mz * S + b; break;
                        case 2:  sy = VoxelChunk.Size; sx = mx * S + a; sz = mz * S + b; break;
                        case 3:  sy = -1;              sx = mx * S + a; sz = mz * S + b; break;
                        case 4:  sz = VoxelChunk.Size; sx = mx * S + a; sy = my * S + b; break;
                        default: sz = -1;              sx = mx * S + a; sy = my * S + b; break;
                    }
                    VoxelBlock nb = snap.Get(sx, sy, sz);
                    if (nb == VoxelBlock.Air || !VoxelBlocks.IsSolid(nb)) return false;
                    if (!VoxelBlocks.IsOpaque(nb) && nb != VoxelBlock.Leaves && nb != VoxelBlock.BirchLeaves) return false;
                }
            return true;
        }

        private static void AppendLodFace(List<MeshVertex> verts, List<ushort> indices,
            int ox, int oy, int oz, int S, int face, VoxelBlock block, int worldY, VoxelPalette palette)
        {
            // Distant LOD faces span S blocks. The full-res path tiles the atlas with frac(worldPos),
            // but across an S-block face that fracs S times in one quad — the sawtooth UV defeats
            // mipmapping under heavy minification and samples collapsed/black texels (the LOD black
            // patches). Instead, sample a single representative texel (the atlas tile centre) with a
            // constant UV: the face is flat-shaded with the block's colour, mips behave, and tiling
            // detail isn't visible at LOD distance anyway.
            Vector2 uvCenter = new(0.5f, 0.5f);
            if (palette.UvLookup != null)
            {
                var uv = palette.UvLookup(block, face);
                uvCenter = (uv.Min + uv.Max) * 0.5f;
            }
            Vector4 color = Vector4.One; // W >= 0 → shader uses the non-tiled (vertex-UV) sample path

            Vector3 o = new(ox, oy, oz);
            float s = S;
            Span<Vector3> c = stackalloc Vector3[4];
            switch (face)
            {
                case 0:  c[0] = o + new Vector3(s, 0, 0); c[1] = o + new Vector3(s, s, 0); c[2] = o + new Vector3(s, s, s); c[3] = o + new Vector3(s, 0, s); break;
                case 1:  c[0] = o + new Vector3(0, 0, s); c[1] = o + new Vector3(0, s, s); c[2] = o + new Vector3(0, s, 0); c[3] = o + new Vector3(0, 0, 0); break;
                case 2:  c[0] = o + new Vector3(0, s, s); c[1] = o + new Vector3(s, s, s); c[2] = o + new Vector3(s, s, 0); c[3] = o + new Vector3(0, s, 0); break;
                case 3:  c[0] = o + new Vector3(0, 0, 0); c[1] = o + new Vector3(s, 0, 0); c[2] = o + new Vector3(s, 0, s); c[3] = o + new Vector3(0, 0, s); break;
                case 4:  c[0] = o + new Vector3(s, 0, s); c[1] = o + new Vector3(s, s, s); c[2] = o + new Vector3(0, s, s); c[3] = o + new Vector3(0, 0, s); break;
                default: c[0] = o + new Vector3(0, 0, 0); c[1] = o + new Vector3(0, s, 0); c[2] = o + new Vector3(s, s, 0); c[3] = o + new Vector3(s, 0, 0); break;
            }

            Vector3 normal = FaceNormals[face];
            ushort start = (ushort)verts.Count;
            for (int i = 0; i < 4; i++)
                verts.Add(new MeshVertex { Position = c[i], Normal = normal, Color = color, UV = uvCenter });
            indices.Add(start);
            indices.Add((ushort)(start + 1));
            indices.Add((ushort)(start + 2));
            indices.Add(start);
            indices.Add((ushort)(start + 2));
            indices.Add((ushort)(start + 3));
        }

        private static bool ShouldRenderSolidFace(VoxelBlock block, VoxelBlock neighbor)
        {
            if (neighbor == VoxelBlock.Air) return true;
            if (VoxelBlocks.IsLiquid(neighbor)) return true;
            return !VoxelBlocks.IsOpaque(neighbor);
        }

        private static bool ShouldRenderLiquidFace(VoxelBlock block, VoxelBlock neighbor)
        {
            if (neighbor == block) return false;
            if (neighbor == VoxelBlock.Air) return true;
            if (VoxelBlocks.IsLiquid(neighbor)) return neighbor != block;
            return false;
        }

        private static void AddLiquidFace(List<MeshVertex> vertices, List<ushort> indices,
            VoxelSnapshot snapshot, int x, int y, int z, int face, VoxelBlock block, int worldY,
            VoxelPalette palette, Vector4 tint)
        {
            Vector3 n = FaceNormals[face];
            if (face == 2)
            {
                Vector2 flow = ComputeLiquidFlow(snapshot, x, y, z);
                if (flow.LengthSquared() > 0.01f)
                {
                    flow = Vector2.Normalize(flow);
                    n = Vector3.Normalize(new Vector3(flow.X, 1f, flow.Y));
                }
            }
            AddFace(vertices, indices, x, y, z, face, block, worldY, palette, tint, n);
        }

        private static Vector2 ComputeLiquidFlow(VoxelSnapshot snap, int x, int y, int z)
        {
            Vector2 flow = Vector2.Zero;
            if (snap.Get(x, y - 1, z) == VoxelBlock.Air)
                return new Vector2(0f, -1f);

            AccumulateLiquidFlow(ref flow, snap, x, y, z, x + 1, y, z);
            AccumulateLiquidFlow(ref flow, snap, x, y, z, x - 1, y, z);
            AccumulateLiquidFlow(ref flow, snap, x, y, z, x, y, z + 1);
            AccumulateLiquidFlow(ref flow, snap, x, y, z, x, y, z - 1);

            if (flow.LengthSquared() < 0.01f && snap.Get(x, y - 1, z) == VoxelBlock.Water)
                flow = new Vector2(0f, 1f);
            return flow;
        }

        private static void AccumulateLiquidFlow(ref Vector2 flow, VoxelSnapshot snap,
            int x, int y, int z, int nx, int ny, int nz)
        {
            if (!VoxelBlocks.IsLiquid(snap.Get(nx, ny, nz))) return;
            if (snap.Get(nx, ny - 1, nz) == VoxelBlock.Air)
            {
                flow += new Vector2(nx - x, nz - z) * 2f;
                return;
            }
            if (!VoxelBlocks.IsLiquid(snap.Get(nx, ny - 1, nz)))
                flow += new Vector2(nx - x, nz - z);
        }

        private static void AddFace(List<MeshVertex> vertices, List<ushort> indices,
            int x, int y, int z, int face, VoxelBlock block, int worldY, VoxelPalette palette, Vector4? tintOverride)
            => AddFace(vertices, indices, x, y, z, face, block, worldY, palette, tintOverride, FaceNormals[face]);

        private static void AddFace(List<MeshVertex> vertices, List<ushort> indices,
            int x, int y, int z, int face, VoxelBlock block, int worldY, VoxelPalette palette, Vector4? tintOverride, Vector3 n)
        {
            Vector4 color = tintOverride ?? (face == 2 ? palette.Top(block, worldY) : palette.Side(block));

            Vector2 uvMin = new Vector2(0, 0);
            Vector2 uvMax = new Vector2(1, 1);
            bool atlasTiled = palette.UvLookup != null && tintOverride == null;
            if (palette.UvLookup != null)
            {
                var uv = palette.UvLookup(block, face);
                uvMin = uv.Min;
                uvMax = uv.Max;
                if (atlasTiled)
                    color = PackAtlasBounds(uvMin, uvMax);
                else if (tintOverride == null)
                    color = palette.TintLookup != null ? palette.TintLookup(block, face, worldY) : Vector4.One;
            }

            Vector3 origin = new Vector3(x, y, z);
            ReadOnlySpan<Vector3> corners = face switch
            {
                0 => stackalloc Vector3[] { origin + new Vector3(1, 0, 0), origin + new Vector3(1, 1, 0), origin + new Vector3(1, 1, 1), origin + new Vector3(1, 0, 1) },
                1 => stackalloc Vector3[] { origin + new Vector3(0, 0, 1), origin + new Vector3(0, 1, 1), origin + new Vector3(0, 1, 0), origin + new Vector3(0, 0, 0) },
                2 => stackalloc Vector3[] { origin + new Vector3(0, 1, 1), origin + new Vector3(1, 1, 1), origin + new Vector3(1, 1, 0), origin + new Vector3(0, 1, 0) },
                3 => stackalloc Vector3[] { origin + new Vector3(0, 0, 0), origin + new Vector3(1, 0, 0), origin + new Vector3(1, 0, 1), origin + new Vector3(0, 0, 1) },
                4 => stackalloc Vector3[] { origin + new Vector3(1, 0, 1), origin + new Vector3(1, 1, 1), origin + new Vector3(0, 1, 1), origin + new Vector3(0, 0, 1) },
                _ => stackalloc Vector3[] { origin + new Vector3(0, 0, 0), origin + new Vector3(0, 1, 0), origin + new Vector3(1, 1, 0), origin + new Vector3(1, 0, 0) },
            };

            ushort start = (ushort)vertices.Count;
            for (int i = 0; i < 4; i++)
            {
                Vector2 uv = atlasTiled ? Vector2.Zero : MapFaceUV(corners[i], origin, face, uvMin, uvMax);
                vertices.Add(new MeshVertex { Position = corners[i], Normal = n, Color = color, UV = uv });
            }

            indices.Add(start);
            indices.Add((ushort)(start + 1));
            indices.Add((ushort)(start + 2));
            indices.Add(start);
            indices.Add((ushort)(start + 2));
            indices.Add((ushort)(start + 3));
        }

        /// <summary>Carpet rendering: a thin slab hugging the floor (top + four short sides).</summary>
        private static void AddCarpet(List<MeshVertex> vertices, List<ushort> indices,
            int x, int y, int z, VoxelBlock block, VoxelPalette palette, int worldY)
        {
            const float h = 0.125f; // 2/16 block tall (~2 voxels)
            Vector3 o = new Vector3(x, y, z);

            void Quad(ReadOnlySpan<Vector3> c, int face, Vector3 n)
            {
                Vector2 uvMin = new Vector2(0, 0), uvMax = new Vector2(1, 1);
                Vector4 color;
                bool atlasTiled = palette.UvLookup != null;
                if (palette.UvLookup != null)
                {
                    var uv = palette.UvLookup(block, face);
                    uvMin = uv.Min; uvMax = uv.Max;
                    color = PackAtlasBounds(uvMin, uvMax);
                }
                else color = face == 2 ? palette.Top(block, worldY) : palette.Side(block);

                ushort start = (ushort)vertices.Count;
                for (int i = 0; i < 4; i++)
                {
                    Vector2 uv = atlasTiled ? Vector2.Zero : MapFaceUV(c[i], o, face, uvMin, uvMax);
                    vertices.Add(new MeshVertex { Position = c[i], Normal = n, Color = color, UV = uv });
                }
                indices.Add(start); indices.Add((ushort)(start + 1)); indices.Add((ushort)(start + 2));
                indices.Add(start); indices.Add((ushort)(start + 2)); indices.Add((ushort)(start + 3));
            }

            Quad(stackalloc Vector3[] { o + new Vector3(0, h, 1), o + new Vector3(1, h, 1), o + new Vector3(1, h, 0), o + new Vector3(0, h, 0) }, 2, Vector3.UnitY);   // top
            Quad(stackalloc Vector3[] { o + new Vector3(1, 0, 0), o + new Vector3(1, h, 0), o + new Vector3(1, h, 1), o + new Vector3(1, 0, 1) }, 0, Vector3.UnitX);    // +X
            Quad(stackalloc Vector3[] { o + new Vector3(0, 0, 1), o + new Vector3(0, h, 1), o + new Vector3(0, h, 0), o + new Vector3(0, 0, 0) }, 1, -Vector3.UnitX);   // -X
            Quad(stackalloc Vector3[] { o + new Vector3(1, 0, 1), o + new Vector3(1, h, 1), o + new Vector3(0, h, 1), o + new Vector3(0, 0, 1) }, 4, Vector3.UnitZ);    // +Z
            Quad(stackalloc Vector3[] { o + new Vector3(0, 0, 0), o + new Vector3(0, h, 0), o + new Vector3(1, h, 0), o + new Vector3(1, 0, 0) }, 5, -Vector3.UnitZ);   // -Z
        }

        /// <summary>Bed furniture: a low mattress box, plus a raised pillow on the head half.
        /// Orientation comes from whichever neighbour is the partner half.</summary>
        private static void AddBed(VoxelSnapshot snap, List<MeshVertex> verts, List<ushort> indices,
            int x, int y, int z, VoxelBlock block, VoxelPalette palette, int worldY)
        {
            Vector3 o = new Vector3(x, y, z);
            const float top = 0.5625f;
            AddBox(verts, indices, o, new Vector3(0f, 0f, 0f), new Vector3(1f, top, 1f), block, palette, worldY);

            if (block != VoxelBlock.WhiteBedHead) return;

            // Find the foot neighbour so the pillow can sit at the opposite (outer) end.
            int fdx = 0, fdz = 0;
            if (snap.Get(x + 1, y, z) == VoxelBlock.WhiteBedFoot) fdx = 1;
            else if (snap.Get(x - 1, y, z) == VoxelBlock.WhiteBedFoot) fdx = -1;
            else if (snap.Get(x, y, z + 1) == VoxelBlock.WhiteBedFoot) fdz = 1;
            else if (snap.Get(x, y, z - 1) == VoxelBlock.WhiteBedFoot) fdz = -1;
            else fdz = 1; // fallback orientation

            int pdx = -fdx, pdz = -fdz; // pillow points away from the foot
            const float py0 = top, py1 = 0.78f;
            Vector3 mn, mx;
            if (pdz != 0)
            {
                float z0 = pdz > 0 ? 0.62f : 0.04f, z1 = pdz > 0 ? 0.96f : 0.38f;
                mn = new Vector3(0.15f, py0, z0); mx = new Vector3(0.85f, py1, z1);
            }
            else
            {
                float x0 = pdx > 0 ? 0.62f : 0.04f, x1 = pdx > 0 ? 0.96f : 0.38f;
                mn = new Vector3(x0, py0, 0.15f); mx = new Vector3(x1, py1, 0.85f);
            }
            AddBox(verts, indices, o, mn, mx, block, palette, worldY);
        }

        /// <summary>Axis-aligned cuboid (6 faces) in local cell coordinates [0..1]; UVs from the block atlas.</summary>
        private static void AddBox(List<MeshVertex> verts, List<ushort> indices,
            Vector3 o, Vector3 mn, Vector3 mx, VoxelBlock block, VoxelPalette palette, int worldY)
        {
            Vector3 a = o + mn, b = o + mx;

            void Face(int face, ReadOnlySpan<Vector3> c, Vector3 n)
            {
                Vector2 uvMin = new Vector2(0, 0), uvMax = new Vector2(1, 1);
                Vector4 color;
                bool atlasTiled = palette.UvLookup != null;
                if (palette.UvLookup != null)
                {
                    var uv = palette.UvLookup(block, face);
                    uvMin = uv.Min; uvMax = uv.Max;
                    color = PackAtlasBounds(uvMin, uvMax);
                }
                else color = face == 2 ? palette.Top(block, worldY) : palette.Side(block);

                ushort start = (ushort)verts.Count;
                for (int i = 0; i < 4; i++)
                {
                    Vector2 uv = atlasTiled ? Vector2.Zero : MapFaceUV(c[i], o, face, uvMin, uvMax);
                    verts.Add(new MeshVertex { Position = c[i], Normal = n, Color = color, UV = uv });
                }
                indices.Add(start); indices.Add((ushort)(start + 1)); indices.Add((ushort)(start + 2));
                indices.Add(start); indices.Add((ushort)(start + 2)); indices.Add((ushort)(start + 3));
            }

            Face(0, stackalloc Vector3[] { new Vector3(b.X, a.Y, a.Z), new Vector3(b.X, b.Y, a.Z), new Vector3(b.X, b.Y, b.Z), new Vector3(b.X, a.Y, b.Z) }, Vector3.UnitX);
            Face(1, stackalloc Vector3[] { new Vector3(a.X, a.Y, b.Z), new Vector3(a.X, b.Y, b.Z), new Vector3(a.X, b.Y, a.Z), new Vector3(a.X, a.Y, a.Z) }, -Vector3.UnitX);
            Face(2, stackalloc Vector3[] { new Vector3(a.X, b.Y, b.Z), new Vector3(b.X, b.Y, b.Z), new Vector3(b.X, b.Y, a.Z), new Vector3(a.X, b.Y, a.Z) }, Vector3.UnitY);
            Face(3, stackalloc Vector3[] { new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, a.Y, a.Z), new Vector3(b.X, a.Y, b.Z), new Vector3(a.X, a.Y, b.Z) }, -Vector3.UnitY);
            Face(4, stackalloc Vector3[] { new Vector3(b.X, a.Y, b.Z), new Vector3(b.X, b.Y, b.Z), new Vector3(a.X, b.Y, b.Z), new Vector3(a.X, a.Y, b.Z) }, Vector3.UnitZ);
            Face(5, stackalloc Vector3[] { new Vector3(a.X, a.Y, a.Z), new Vector3(a.X, b.Y, a.Z), new Vector3(b.X, b.Y, a.Z), new Vector3(b.X, a.Y, a.Z) }, -Vector3.UnitZ);
        }

        /// <summary>Placed torch: two narrow crossed planes anchored to the block floor (not full-block X).</summary>
        private static void AddTorchCross(List<MeshVertex> vertices, List<ushort> indices,
            int x, int y, int z, VoxelPalette palette)
        {
            Vector2 uvMin = new(0, 0), uvMax = new(1, 1);
            if (palette.UvLookup != null)
            {
                var uv = palette.UvLookup(VoxelBlock.Torch, 4);
                uvMin = uv.Min;
                uvMax = uv.Max;
            }

            Vector3 o = new(x, y, z);
            const float hw = 0.0625f;
            const float h = 0.625f;
            Vector3 n = Vector3.UnitY;

            ReadOnlySpan<Vector3> qa = stackalloc Vector3[]
            {
                o + new Vector3(0.5f - hw, 0f, 0.5f - hw),
                o + new Vector3(0.5f + hw, 0f, 0.5f + hw),
                o + new Vector3(0.5f + hw, h, 0.5f + hw),
                o + new Vector3(0.5f - hw, h, 0.5f - hw),
            };
            ReadOnlySpan<Vector3> qb = stackalloc Vector3[]
            {
                o + new Vector3(0.5f - hw, 0f, 0.5f + hw),
                o + new Vector3(0.5f + hw, 0f, 0.5f - hw),
                o + new Vector3(0.5f + hw, h, 0.5f - hw),
                o + new Vector3(0.5f - hw, h, 0.5f + hw),
            };
            AddQuadDoubleSided(vertices, indices, qa, uvMin, uvMax, n);
            AddQuadDoubleSided(vertices, indices, qb, uvMin, uvMax, n);
        }

        /// <summary>Cross/plant rendering: two double-sided diagonal alpha-tested quads (flowers).</summary>
        private static void AddCross(List<MeshVertex> vertices, List<ushort> indices,
            int x, int y, int z, VoxelBlock block, VoxelPalette palette)
        {
            Vector2 uvMin = new Vector2(0, 0), uvMax = new Vector2(1, 1);
            if (palette.UvLookup != null) { var uv = palette.UvLookup(block, 4); uvMin = uv.Min; uvMax = uv.Max; }

            Vector3 o = new Vector3(x, y, z);
            Vector3 n = Vector3.UnitY;
            ReadOnlySpan<Vector3> qa = stackalloc Vector3[]
            { o + new Vector3(0, 0, 0), o + new Vector3(1, 0, 1), o + new Vector3(1, 1, 1), o + new Vector3(0, 1, 0) };
            ReadOnlySpan<Vector3> qb = stackalloc Vector3[]
            { o + new Vector3(1, 0, 0), o + new Vector3(0, 0, 1), o + new Vector3(0, 1, 1), o + new Vector3(1, 1, 0) };
            AddQuadDoubleSided(vertices, indices, qa, uvMin, uvMax, n);
            AddQuadDoubleSided(vertices, indices, qb, uvMin, uvMax, n);
        }

        private static void AddQuadDoubleSided(List<MeshVertex> vertices, List<ushort> indices,
            ReadOnlySpan<Vector3> c, Vector2 uvMin, Vector2 uvMax, Vector3 n)
        {
            Span<Vector2> uvs = stackalloc Vector2[]
            {
                new Vector2(uvMin.X, uvMax.Y), // bottom-left
                new Vector2(uvMax.X, uvMax.Y), // bottom-right
                new Vector2(uvMax.X, uvMin.Y), // top-right
                new Vector2(uvMin.X, uvMin.Y), // top-left
            };
            ushort start = (ushort)vertices.Count;
            for (int i = 0; i < 4; i++)
                vertices.Add(new MeshVertex { Position = c[i], Normal = n, Color = Vector4.One, UV = uvs[i] });
            // front winding
            indices.Add(start); indices.Add((ushort)(start + 1)); indices.Add((ushort)(start + 2));
            indices.Add(start); indices.Add((ushort)(start + 2)); indices.Add((ushort)(start + 3));
            // back winding (so the cross is visible from both sides)
            indices.Add(start); indices.Add((ushort)(start + 2)); indices.Add((ushort)(start + 1));
            indices.Add(start); indices.Add((ushort)(start + 3)); indices.Add((ushort)(start + 2));
        }

        internal static Vector4 PackAtlasBounds(Vector2 uvMin, Vector2 uvMax) =>
            new Vector4(uvMin.X, uvMin.Y, uvMax.X, -uvMax.Y);

        internal static void AppendFaceQuad(List<MeshVertex> vertices, List<ushort> indices,
            ReadOnlySpan<Vector3> corners, Vector3 normal, Vector4 color, int face, Vector3 origin,
            Vector2 uvMin, Vector2 uvMax)
        {
            bool atlasTiled = color.W < 0f;
            ushort start = (ushort)vertices.Count;
            for (int i = 0; i < 4; i++)
            {
                Vector2 uv = atlasTiled ? Vector2.Zero : MapFaceUV(corners[i], origin, face, uvMin, uvMax);
                vertices.Add(new MeshVertex { Position = corners[i], Normal = normal, Color = color, UV = uv });
            }

            indices.Add(start);
            indices.Add((ushort)(start + 1));
            indices.Add((ushort)(start + 2));
            indices.Add(start);
            indices.Add((ushort)(start + 2));
            indices.Add((ushort)(start + 3));
        }

        /// <summary>Maps block-face corners to atlas UVs with world-up aligned to texture-up.</summary>
        internal static Vector2 MapFaceUV(Vector3 corner, Vector3 origin, int face, Vector2 uvMin, Vector2 uvMax)
        {
            float u, v;
            switch (face)
            {
                case 2: // +Y top
                    u = corner.X - origin.X;
                    v = corner.Z - origin.Z;
                    break;
                case 3: // -Y bottom
                    u = corner.X - origin.X;
                    v = 1f - (corner.Z - origin.Z);
                    break;
                case 0: // +X
                    u = corner.Z - origin.Z;
                    v = 1f - (corner.Y - origin.Y);
                    break;
                case 1: // -X
                    u = 1f - (corner.Z - origin.Z);
                    v = 1f - (corner.Y - origin.Y);
                    break;
                case 4: // +Z
                    u = 1f - (corner.X - origin.X);
                    v = 1f - (corner.Y - origin.Y);
                    break;
                default: // -Z
                    u = corner.X - origin.X;
                    v = 1f - (corner.Y - origin.Y);
                    break;
            }

            return new Vector2(
                uvMin.X + (uvMax.X - uvMin.X) * u,
                uvMin.Y + (uvMax.Y - uvMin.Y) * v);
        }

        /// <summary>Thread-local mesh build lists — reused across background mesh jobs.</summary>
        private sealed class MeshBuildScratch
        {
            [ThreadStatic] private static MeshBuildScratch _tls;

            public readonly List<MeshVertex> SolidVerts = new(2048);
            public readonly List<ushort> SolidIndices = new(3072);
            public readonly List<MeshVertex> LiquidVerts = new(512);
            public readonly List<ushort> LiquidIndices = new(768);
            public readonly List<MeshVertex> CrossVerts = new(256);
            public readonly List<ushort> CrossIndices = new(384);
            public readonly List<MeshVertex> LodVerts = new(256);
            public readonly List<ushort> LodIndices = new(384);
            public readonly Dictionary<VoxelBlock, int> LodCounts = new(16);

            public static MeshBuildScratch Get() => _tls ??= new MeshBuildScratch();

            public void Clear()
            {
                SolidVerts.Clear();
                SolidIndices.Clear();
                LiquidVerts.Clear();
                LiquidIndices.Clear();
                CrossVerts.Clear();
                CrossIndices.Clear();
            }
        }
    }
}
