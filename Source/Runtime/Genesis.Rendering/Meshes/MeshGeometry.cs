using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Meshes
{
    public static class MeshGeometry
    {
        // ── Cube ─────────────────────────────────────────────────────────────────
        // Counter-clockwise from outside: cross(edge1, edge2) agrees with the outward normal.
        public static (MeshVertex[] verts, ushort[] indices) BuildCube(RenderColor color, float size = 1f)
        {
            float hx = size * 0.5f, hy = size * 0.5f, hz = size * 0.5f;
            var v4 = new Vector4(color.R, color.G, color.B, color.A);
            var verts = new List<MeshVertex>(24);
            var indices = new List<ushort>(36);

            void Face(Vector3 normal, Vector3 up, Vector3 right, Vector3 center)
            {
                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new MeshVertex { Position = center - right + up, Normal = normal, Color = v4, UV = new Vector2(0, 0) });
                verts.Add(new MeshVertex { Position = center + right + up, Normal = normal, Color = v4, UV = new Vector2(1, 0) });
                verts.Add(new MeshVertex { Position = center + right - up, Normal = normal, Color = v4, UV = new Vector2(1, 1) });
                verts.Add(new MeshVertex { Position = center - right - up, Normal = normal, Color = v4, UV = new Vector2(0, 1) });
                indices.Add(baseIdx);
                indices.Add((ushort)(baseIdx + 1));
                indices.Add((ushort)(baseIdx + 2));
                indices.Add(baseIdx);
                indices.Add((ushort)(baseIdx + 2));
                indices.Add((ushort)(baseIdx + 3));
            }

            Face(Vector3.UnitY,  new Vector3(0, 0, hz),  new Vector3(hx, 0, 0),  new Vector3(0, hy, 0));
            Face(-Vector3.UnitY, new Vector3(0, 0, -hz), new Vector3(hx, 0, 0),  new Vector3(0, -hy, 0));
            Face(Vector3.UnitX,  new Vector3(0, hy, 0),  new Vector3(0, 0, hz),  new Vector3(hx, 0, 0));
            Face(-Vector3.UnitX, new Vector3(0, hy, 0),  new Vector3(0, 0, -hz), new Vector3(-hx, 0, 0));
            Face(Vector3.UnitZ,  new Vector3(0, hy, 0),  new Vector3(-hx, 0, 0), new Vector3(0, 0, hz));
            Face(-Vector3.UnitZ, new Vector3(0, hy, 0),  new Vector3(hx, 0, 0),  new Vector3(0, 0, -hz));

            return (verts.ToArray(), indices.ToArray());
        }

        /// <summary>Unit cube with UVs mapped to an atlas tile region (u0,v0)–(u1,v1).</summary>
        public static (MeshVertex[] verts, ushort[] indices) BuildCubeWithAtlasUv(
            RenderColor color, float u0, float v0, float u1, float v1, float size = 1f)
        {
            float hx = size * 0.5f, hy = size * 0.5f, hz = size * 0.5f;
            var v4 = new Vector4(color.R, color.G, color.B, color.A);
            var verts = new List<MeshVertex>(24);
            var indices = new List<ushort>(36);

            void Face(Vector3 normal, Vector3 up, Vector3 right, Vector3 center)
            {
                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new MeshVertex { Position = center - right + up, Normal = normal, Color = v4, UV = new Vector2(u0, v0) });
                verts.Add(new MeshVertex { Position = center + right + up, Normal = normal, Color = v4, UV = new Vector2(u1, v0) });
                verts.Add(new MeshVertex { Position = center + right - up, Normal = normal, Color = v4, UV = new Vector2(u1, v1) });
                verts.Add(new MeshVertex { Position = center - right - up, Normal = normal, Color = v4, UV = new Vector2(u0, v1) });
                indices.Add(baseIdx);
                indices.Add((ushort)(baseIdx + 1));
                indices.Add((ushort)(baseIdx + 2));
                indices.Add(baseIdx);
                indices.Add((ushort)(baseIdx + 2));
                indices.Add((ushort)(baseIdx + 3));
            }

            Face(Vector3.UnitY,  new Vector3(0, 0, hz),  new Vector3(hx, 0, 0),  new Vector3(0, hy, 0));
            Face(-Vector3.UnitY, new Vector3(0, 0, -hz), new Vector3(hx, 0, 0),  new Vector3(0, -hy, 0));
            Face(Vector3.UnitX,  new Vector3(0, hy, 0),  new Vector3(0, 0, hz),  new Vector3(hx, 0, 0));
            Face(-Vector3.UnitX, new Vector3(0, hy, 0),  new Vector3(0, 0, -hz), new Vector3(-hx, 0, 0));
            Face(Vector3.UnitZ,  new Vector3(0, hy, 0),  new Vector3(-hx, 0, 0), new Vector3(0, 0, hz));
            Face(-Vector3.UnitZ, new Vector3(0, hy, 0),  new Vector3(hx, 0, 0),  new Vector3(0, 0, -hz));

            return (verts.ToArray(), indices.ToArray());
        }

        // ── Floor quad ───────────────────────────────────────────────────────────
        public static (MeshVertex[] verts, ushort[] indices) BuildFloor(RenderColor color, float size = 500f, float uvTile = 50f)
        {
            float h  = size * 0.5f;
            var   v4 = new Vector4(color.R, color.G, color.B, color.A);
            var verts = new MeshVertex[]
            {
                new(){ Position = new(-h,0,-h), Normal = Vector3.UnitY, Color = v4, UV = new(0,    0   ) },
                new(){ Position = new( h,0,-h), Normal = Vector3.UnitY, Color = v4, UV = new(uvTile,0   ) },
                new(){ Position = new( h,0, h), Normal = Vector3.UnitY, Color = v4, UV = new(uvTile,uvTile) },
                new(){ Position = new(-h,0, h), Normal = Vector3.UnitY, Color = v4, UV = new(0,    uvTile) },
            };
            // The face normal derived from the indices must agree with +Y, as it does for the
            // cube's top face and every other primitive. A previous visual-only workaround
            // inverted just the floor, making it disappear on DX12 and any strict culling path.
            var indices = new ushort[] { 0, 2, 1, 0, 3, 2 };
            return (verts, indices);
        }

        /// <summary>
        /// A real vertex-coloured checker floor. Unlike an unlit checker pixel shader, this mesh
        /// passes through the normal material lighting path, so local point lights visibly pool
        /// across it and projected/contact shadows remain readable.
        /// </summary>
        public static (MeshVertex[] verts, ushort[] indices) BuildCheckerFloor(
            RenderColor dark,
            RenderColor light,
            int tiles = 48,
            float tileSize = 4f)
        {
            tiles = Math.Clamp(tiles, 2, 127);
            tileSize = MathF.Max(0.01f, tileSize);
            int tileCount = tiles * tiles;
            var vertices = new MeshVertex[tileCount * 4];
            var indices = new ushort[tileCount * 6];
            float origin = -tiles * tileSize * 0.5f;
            int vertex = 0;
            int index = 0;

            for (int z = 0; z < tiles; z++)
            for (int x = 0; x < tiles; x++)
            {
                float x0 = origin + x * tileSize;
                float z0 = origin + z * tileSize;
                float x1 = x0 + tileSize;
                float z1 = z0 + tileSize;
                RenderColor tile = ((x + z) & 1) == 0 ? dark : light;
                Vector4 color = new(tile.R, tile.G, tile.B, tile.A);
                ushort start = (ushort)vertex;
                vertices[vertex++] = new MeshVertex { Position = new Vector3(x0, 0f, z0), Normal = Vector3.UnitY, Color = color, UV = Vector2.Zero };
                vertices[vertex++] = new MeshVertex { Position = new Vector3(x1, 0f, z0), Normal = Vector3.UnitY, Color = color, UV = Vector2.UnitX };
                vertices[vertex++] = new MeshVertex { Position = new Vector3(x1, 0f, z1), Normal = Vector3.UnitY, Color = color, UV = Vector2.One };
                vertices[vertex++] = new MeshVertex { Position = new Vector3(x0, 0f, z1), Normal = Vector3.UnitY, Color = color, UV = Vector2.UnitY };
                indices[index++] = start;
                indices[index++] = (ushort)(start + 2);
                indices[index++] = (ushort)(start + 1);
                indices[index++] = start;
                indices[index++] = (ushort)(start + 3);
                indices[index++] = (ushort)(start + 2);
            }

            return (vertices, indices);
        }

        // ── UV sphere ────────────────────────────────────────────────────────────
        public static (MeshVertex[] verts, ushort[] indices) BuildSphere(RenderColor color, float radius = 0.5f, int stacks = 12, int slices = 16)
        {
            var v4    = new Vector4(color.R, color.G, color.B, color.A);
            int vRows = stacks + 1;
            int vCols = slices + 1;
            var verts = new MeshVertex[vRows * vCols];
            int vi    = 0;
            for (int i = 0; i <= stacks; i++)
            {
                float phi = MathF.PI * i / stacks;
                for (int j = 0; j <= slices; j++)
                {
                    float theta = 2f * MathF.PI * j / slices;
                    var   n     = new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta));
                    verts[vi++] = new MeshVertex
                    {
                        Position = n * radius,
                        Normal   = n,
                        Color    = v4,
                        UV       = new Vector2((float)j / slices, (float)i / stacks),
                    };
                }
            }
            var idxList = new System.Collections.Generic.List<ushort>(stacks * slices * 6);
            for (int i = 0; i < stacks; i++)
            {
                for (int j = 0; j < slices; j++)
                {
                    ushort a = (ushort)(i * vCols + j);
                    ushort b = (ushort)(a + vCols);
                    // Winding matches EngineTest Primitives.Sphere (LH clockwise-front faces).
                    idxList.Add(a);
                    idxList.Add((ushort)(a + 1));
                    idxList.Add(b);
                    idxList.Add(b);
                    idxList.Add((ushort)(a + 1));
                    idxList.Add((ushort)(b + 1));
                }
            }
            return (verts, idxList.ToArray());
        }

        // ── Capped cylinder ──────────────────────────────────────────────────────
        public static (MeshVertex[] verts, ushort[] indices) BuildCylinder(RenderColor color, float radius = 0.5f, float height = 1f, int slices = 16)
        {
            var v4  = new Vector4(color.R, color.G, color.B, color.A);
            var vList   = new System.Collections.Generic.List<MeshVertex>();
            var idxList = new System.Collections.Generic.List<ushort>();

            float hy = height * 0.5f;

            // Side strip
            for (int j = 0; j <= slices; j++)
            {
                float theta = 2f * MathF.PI * j / slices;
                float cx    = MathF.Cos(theta);
                float cz    = MathF.Sin(theta);
                var   n     = new Vector3(cx, 0, cz);
                float u     = (float)j / slices;
                vList.Add(new MeshVertex { Position = new(cx * radius, -hy, cz * radius), Normal = n, Color = v4, UV = new(u, 1) });
                vList.Add(new MeshVertex { Position = new(cx * radius,  hy, cz * radius), Normal = n, Color = v4, UV = new(u, 0) });
            }
            for (int j = 0; j < slices; j++)
            {
                int b = j * 2;
                // Outward winding must agree with the radial vertex normals. The previous order
                // pointed inward while the caps pointed outward, so back-face culling removed a
                // different half of the primitive on different APIs/camera angles.
                idxList.Add((ushort)b);
                idxList.Add((ushort)(b + 1));
                idxList.Add((ushort)(b + 2));
                idxList.Add((ushort)(b + 1));
                idxList.Add((ushort)(b + 3));
                idxList.Add((ushort)(b + 2));
            }

            // Top cap (+Y)
            int capCenter = vList.Count;
            vList.Add(new MeshVertex { Position = new(0, hy, 0), Normal = Vector3.UnitY, Color = v4, UV = new(0.5f, 0.5f) });
            for (int j = 0; j <= slices; j++)
            {
                float theta = 2f * MathF.PI * j / slices;
                vList.Add(new MeshVertex { Position = new(MathF.Cos(theta) * radius, hy, MathF.Sin(theta) * radius), Normal = Vector3.UnitY, Color = v4, UV = new(MathF.Cos(theta) * 0.5f + 0.5f, MathF.Sin(theta) * 0.5f + 0.5f) });
            }
            // The ±Y caps must wind the same way BuildCube's ±Y faces do (see NEXT-024): the
            // reverse order made both caps back-facing, which rendered them solid black even with
            // nothing above them to cast a shadow (NEXT-031).
            for (int j = 0; j < slices; j++)
            {
                idxList.Add((ushort)capCenter);
                idxList.Add((ushort)(capCenter + 2 + j));
                idxList.Add((ushort)(capCenter + 1 + j));
            }

            // Bottom cap (-Y)
            int botCenter = vList.Count;
            vList.Add(new MeshVertex { Position = new(0, -hy, 0), Normal = -Vector3.UnitY, Color = v4, UV = new(0.5f, 0.5f) });
            for (int j = 0; j <= slices; j++)
            {
                float theta = 2f * MathF.PI * j / slices;
                vList.Add(new MeshVertex { Position = new(MathF.Cos(theta) * radius, -hy, MathF.Sin(theta) * radius), Normal = -Vector3.UnitY, Color = v4, UV = new(MathF.Cos(theta) * 0.5f + 0.5f, MathF.Sin(theta) * 0.5f + 0.5f) });
            }
            // Mirror of the corrected top cap (this face points -Y, so the opposite order).
            for (int j = 0; j < slices; j++)
            {
                idxList.Add((ushort)botCenter);
                idxList.Add((ushort)(botCenter + 1 + j));
                idxList.Add((ushort)(botCenter + 2 + j));
            }

            return (vList.ToArray(), idxList.ToArray());
        }

        // ── Cone / pyramid ────────────────────────────────────────────────────
        public static (MeshVertex[] verts, ushort[] indices) BuildCone(
            RenderColor color,
            float radius = 0.5f,
            float height = 1f,
            int slices = 24)
        {
            slices = Math.Clamp(slices, 3, 128);
            var tint = new Vector4(color.R, color.G, color.B, color.A);
            var vertices = new List<MeshVertex>(slices * 3 + 2);
            var indices = new List<ushort>(slices * 6);
            float halfHeight = height * 0.5f;
            float slope = radius / MathF.Max(0.001f, height);

            // Separate side vertices keep the conical normals independent from the flat base.
            for (int slice = 0; slice <= slices; slice++)
            {
                float angle = MathF.Tau * slice / slices;
                float cos = MathF.Cos(angle);
                float sin = MathF.Sin(angle);
                Vector3 normal = Vector3.Normalize(new Vector3(cos, slope, sin));
                float u = slice / (float)slices;
                vertices.Add(new MeshVertex
                {
                    Position = new Vector3(cos * radius, -halfHeight, sin * radius),
                    Normal = normal,
                    Color = tint,
                    UV = new Vector2(u, 1f),
                });
                vertices.Add(new MeshVertex
                {
                    Position = new Vector3(0f, halfHeight, 0f),
                    Normal = normal,
                    Color = tint,
                    UV = new Vector2(u, 0f),
                });
            }
            for (int slice = 0; slice < slices; slice++)
            {
                ushort bottom = (ushort)(slice * 2);
                indices.Add(bottom);
                indices.Add((ushort)(bottom + 1));
                indices.Add((ushort)(bottom + 2));
            }

            int centre = vertices.Count;
            vertices.Add(new MeshVertex
            {
                Position = new Vector3(0f, -halfHeight, 0f),
                Normal = -Vector3.UnitY,
                Color = tint,
                UV = new Vector2(0.5f),
            });
            for (int slice = 0; slice <= slices; slice++)
            {
                float angle = MathF.Tau * slice / slices;
                float cos = MathF.Cos(angle);
                float sin = MathF.Sin(angle);
                vertices.Add(new MeshVertex
                {
                    Position = new Vector3(cos * radius, -halfHeight, sin * radius),
                    Normal = -Vector3.UnitY,
                    Color = tint,
                    UV = new Vector2(cos * 0.5f + 0.5f, sin * 0.5f + 0.5f),
                });
            }
            for (int slice = 0; slice < slices; slice++)
            {
                indices.Add((ushort)centre);
                indices.Add((ushort)(centre + 1 + slice));
                indices.Add((ushort)(centre + 2 + slice));
            }
            return (vertices.ToArray(), indices.ToArray());
        }

        /// <summary>A square-based pyramid centred vertically at the origin.</summary>
        public static (MeshVertex[] verts, ushort[] indices) BuildPyramid(
            RenderColor color,
            float radius = 0.5f,
            float height = 1f) => BuildCone(color, radius, height, slices: 4);

        /// <summary>A small torus used for rings, rotation guides and authored model resources.</summary>
        public static (MeshVertex[] verts, ushort[] indices) BuildTorus(
            RenderColor color,
            float majorRadius = 0.5f,
            float minorRadius = 0.035f,
            int majorSegments = 32,
            int minorSegments = 6)
        {
            majorSegments = Math.Clamp(majorSegments, 3, 128);
            minorSegments = Math.Clamp(minorSegments, 3, 32);
            var tint = new Vector4(color.R, color.G, color.B, color.A);
            int columns = minorSegments + 1;
            var vertices = new MeshVertex[(majorSegments + 1) * columns];
            var indices = new List<ushort>(majorSegments * minorSegments * 6);
            int vertex = 0;
            for (int major = 0; major <= majorSegments; major++)
            {
                float a = MathF.Tau * major / majorSegments;
                Vector3 radial = new(MathF.Cos(a), 0f, MathF.Sin(a));
                for (int minor = 0; minor <= minorSegments; minor++)
                {
                    float b = MathF.Tau * minor / minorSegments;
                    Vector3 normal = Vector3.Normalize(radial * MathF.Cos(b) + Vector3.UnitY * MathF.Sin(b));
                    vertices[vertex++] = new MeshVertex
                    {
                        Position = radial * (majorRadius + minorRadius * MathF.Cos(b))
                            + Vector3.UnitY * (minorRadius * MathF.Sin(b)),
                        Normal = normal,
                        Color = tint,
                        UV = new Vector2(major / (float)majorSegments, minor / (float)minorSegments),
                    };
                }
            }
            for (int major = 0; major < majorSegments; major++)
            for (int minor = 0; minor < minorSegments; minor++)
            {
                ushort a = (ushort)(major * columns + minor);
                ushort b = (ushort)((major + 1) * columns + minor);
                indices.Add(a);
                indices.Add((ushort)(a + 1));
                indices.Add(b);
                indices.Add(b);
                indices.Add((ushort)(a + 1));
                indices.Add((ushort)(b + 1));
            }
            return (vertices, indices.ToArray());
        }

        // ── Billboard quad ───────────────────────────────────────────────────────
        // Unit quad centred at origin in the XY plane, facing +Z.
        // UV params select a sub-region of the texture (0..1 full, or a flipbook cell).
        // Transform with CreateScale(x,y,1) * CreateBillboard before submitting.
        public static (MeshVertex[] verts, ushort[] indices) BuildQuad(
            RenderColor color,
            float u0 = 0f, float v0 = 0f, float u1 = 1f, float v1 = 1f)
        {
            var v4 = new Vector4(color.R, color.G, color.B, color.A);
            var verts = new MeshVertex[]
            {
                new() { Position = new(-0.5f, -0.5f, 0f), Normal = Vector3.UnitZ, Color = v4, UV = new(u0, v1) },
                new() { Position = new( 0.5f, -0.5f, 0f), Normal = Vector3.UnitZ, Color = v4, UV = new(u1, v1) },
                new() { Position = new( 0.5f,  0.5f, 0f), Normal = Vector3.UnitZ, Color = v4, UV = new(u1, v0) },
                new() { Position = new(-0.5f,  0.5f, 0f), Normal = Vector3.UnitZ, Color = v4, UV = new(u0, v0) },
            };
            var indices = new ushort[] { 0, 2, 1, 0, 3, 2 };
            return (verts, indices);
        }

        /// <summary>Registers a full-UV billboard quad.</summary>
        public static MeshHandle RegisterQuad(IRenderController r, RenderColor color)
        {
            var (v, i) = BuildQuad(color);
            return r.RegisterMesh(v, i);
        }

        /// <summary>
        /// Registers a billboard quad whose UVs point to a sub-region of the texture.
        /// Used for flipbook animation — each frame cell (col, row) in a sprite sheet maps
        /// to UV (col/cols, row/rows) .. ((col+1)/cols, (row+1)/rows).
        /// </summary>
        public static MeshHandle RegisterQuadUV(
            IRenderController r, RenderColor color,
            float u0, float v0, float u1, float v1)
        {
            var (v, i) = BuildQuad(color, u0, v0, u1, v1);
            return r.RegisterMesh(v, i);
        }

        // ── Register helpers ─────────────────────────────────────────────────────
        public static MeshHandle RegisterCube(IRenderController r, RenderColor color, float size = 1f)
        {
            var (v, i) = BuildCube(color, size);
            return r.RegisterMesh(v, i);
        }

        public static MeshHandle RegisterFloor(IRenderController r, RenderColor color, float size = 500f)
        {
            var (v, i) = BuildFloor(color, size);
            return r.RegisterMesh(v, i);
        }

        public static MeshHandle RegisterSphere(IRenderController r, RenderColor color, float radius = 0.5f)
        {
            var (v, i) = BuildSphere(color, radius);
            return r.RegisterMesh(v, i);
        }

        public static MeshHandle RegisterCylinder(IRenderController r, RenderColor color, float radius = 0.5f, float height = 1f)
        {
            var (v, i) = BuildCylinder(color, radius, height);
            return r.RegisterMesh(v, i);
        }

        public static MeshHandle RegisterCone(IRenderController r, RenderColor color, float radius = 0.5f, float height = 1f)
        {
            var (v, i) = BuildCone(color, radius, height);
            return r.RegisterMesh(v, i);
        }

        public static MeshHandle RegisterPyramid(IRenderController r, RenderColor color, float radius = 0.5f, float height = 1f)
        {
            var (v, i) = BuildPyramid(color, radius, height);
            return r.RegisterMesh(v, i);
        }

        public static MeshHandle RegisterTorus(IRenderController r, RenderColor color, float majorRadius = 0.5f, float minorRadius = 0.035f)
        {
            var (v, i) = BuildTorus(color, majorRadius, minorRadius);
            return r.RegisterMesh(v, i);
        }

        // ── Extruded sprite (2D pixel art → chunky 3D voxel mesh) ───────────────────

        /// <summary>
        /// Builds a chunky 3D mesh from a sprite: an NxN sample of the atlas tile is voxelised,
        /// each opaque cell contributing front/back faces plus sides where it borders empty space,
        /// giving the flat sprite real depth. Pixel colour is baked into vertex colour (no texture).
        /// Quads are double-sided, so it shows regardless of cull convention. Centred at the origin.
        /// </summary>
        public static (MeshVertex[] verts, ushort[] indices) BuildExtrudedSprite(
            byte[] rgba, int atlasW, int atlasH, int tileX, int tileY, int tileW, int tileH,
            int grid, float size, float depth, byte alphaCutoff = 48)
        {
            grid = Math.Max(2, grid);
            var verts = new List<MeshVertex>(grid * grid * 4);
            var indices = new List<ushort>(grid * grid * 12);

            float cell = size / grid;
            float ox = -size * 0.5f, oyTop = size * 0.5f, hz = depth * 0.5f;

            (bool op, Vector4 col) Sample(int gx, int gy)
            {
                if (gx < 0 || gx >= grid || gy < 0 || gy >= grid) return (false, default);
                int sx = tileX + (int)((gx + 0.5f) / grid * tileW);
                int sy = tileY + (int)((gy + 0.5f) / grid * tileH);
                if ((uint)sx >= atlasW || (uint)sy >= atlasH) return (false, default);
                int idx = (sy * atlasW + sx) * 4;
                byte a = rgba[idx + 3];
                if (a < alphaCutoff) return (false, default);
                return (true, new Vector4(rgba[idx] / 255f, rgba[idx + 1] / 255f, rgba[idx + 2] / 255f, 1f));
            }

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Vector4 col)
            {
                ushort s = (ushort)verts.Count;
                verts.Add(new MeshVertex { Position = a, Normal = n, Color = col, UV = new Vector2(0, 0) });
                verts.Add(new MeshVertex { Position = b, Normal = n, Color = col, UV = new Vector2(1, 0) });
                verts.Add(new MeshVertex { Position = c, Normal = n, Color = col, UV = new Vector2(1, 1) });
                verts.Add(new MeshVertex { Position = d, Normal = n, Color = col, UV = new Vector2(0, 1) });
                indices.Add(s); indices.Add((ushort)(s + 2)); indices.Add((ushort)(s + 1));
                indices.Add(s); indices.Add((ushort)(s + 3)); indices.Add((ushort)(s + 2));
            }

            for (int gy = 0; gy < grid; gy++)
                for (int gx = 0; gx < grid; gx++)
                {
                    var (op, col) = Sample(gx, gy);
                    if (!op) continue;
                    float x0 = ox + gx * cell, x1 = x0 + cell;
                    float y1 = oyTop - gy * cell, y0 = y1 - cell;   // grid row 0 = top of the sprite
                    Vector4 side = new Vector4(col.X * 0.82f, col.Y * 0.82f, col.Z * 0.82f, 1f);

                    Quad(new(x0, y0, hz), new(x1, y0, hz), new(x1, y1, hz), new(x0, y1, hz), Vector3.UnitZ, col);
                    Quad(new(x0, y0, -hz), new(x1, y0, -hz), new(x1, y1, -hz), new(x0, y1, -hz), -Vector3.UnitZ, col);
                    if (!Sample(gx - 1, gy).op) Quad(new(x0, y0, -hz), new(x0, y0, hz), new(x0, y1, hz), new(x0, y1, -hz), -Vector3.UnitX, side);
                    if (!Sample(gx + 1, gy).op) Quad(new(x1, y0, hz), new(x1, y0, -hz), new(x1, y1, -hz), new(x1, y1, hz), Vector3.UnitX, side);
                    if (!Sample(gx, gy - 1).op) Quad(new(x0, y1, hz), new(x1, y1, hz), new(x1, y1, -hz), new(x0, y1, -hz), Vector3.UnitY, side);
                    if (!Sample(gx, gy + 1).op) Quad(new(x0, y0, -hz), new(x1, y0, -hz), new(x1, y0, hz), new(x0, y0, hz), -Vector3.UnitY, side);
                }

            if (verts.Count == 0) // fully transparent tile → tiny placeholder so the buffer is valid
                Quad(new(-0.01f, -0.01f, 0), new(0.01f, -0.01f, 0), new(0.01f, 0.01f, 0), new(-0.01f, 0.01f, 0), Vector3.UnitZ, Vector4.One);

            return (verts.ToArray(), indices.ToArray());
        }

        public static MeshHandle RegisterExtrudedSprite(
            IRenderController r, byte[] rgba, int atlasW, int atlasH,
            int tileX, int tileY, int tileW, int tileH, int grid, float size, float depth)
        {
            var (v, i) = BuildExtrudedSprite(rgba, atlasW, atlasH, tileX, tileY, tileW, tileH, grid, size, depth);
            return r.RegisterMesh(v, i);
        }
    }
}
