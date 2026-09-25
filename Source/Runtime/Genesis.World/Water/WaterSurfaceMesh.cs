using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;
using MeshData = Genesis.World.MeshData;

namespace Genesis.World.Water
{
    /// <summary>Builds flat water-surface grids and river ribbon meshes.</summary>
    public static class WaterSurfaceMesh
    {
        public static MeshData BuildLakeGrid(Vector3 center, float sizeX, float sizeZ, float surfaceY, int resolution)
        {
            int resX = Math.Clamp(resolution, 2, 128);
            int resZ = Math.Clamp(resolution, 2, 128);
            return BuildGrid(center - new Vector3(sizeX * 0.5f, 0f, sizeZ * 0.5f), sizeX, sizeZ, surfaceY, resX, resZ);
        }

        /// <summary>
        /// Builds only the visible water surface. Depth is resolved by the water shader and the
        /// physics volume remains separate, avoiding opaque rectangular walls at shorelines.
        /// </summary>
        public static MeshData BuildVisual(WaterBody body, Vector3 cameraPos, WaterBodySimulation simulation = null)
        {
            if (body == null)
                return default;

            if (body.Kind == WaterBodyKind.Waterfall)
                return WaterfallMesh.BuildSheet(body.Waterfall);

            bool footprint = TryGetFootprintBits(body, out byte[] bits);
            MeshData data = footprint
                ? BuildFootprintSurface(body, bits)
                : body.SimulationEnabled && simulation != null
                    ? simulation.BuildMesh()
                    : body.Kind switch
                    {
                        WaterBodyKind.Ocean => BuildOceanTile(
                            cameraPos, body.OceanTileSize, body.SurfaceY, body.GridResolution),
                        WaterBodyKind.Lake or WaterBodyKind.Reservoir => BuildLakeGrid(
                            body.Center, body.SizeX, body.SizeZ, body.SurfaceY, body.GridResolution),
                        WaterBodyKind.River => BuildRiverRibbon(
                            body.SplinePoints, body.SplineWidths, body.RiverWidth, body.SurfaceY,
                            body.RiverSegmentsPerSpan, body.SplineUsesPointHeights),
                        _ => default,
                    };

            if (body.Kind == WaterBodyKind.River && body.Cascades is { Length: > 0 })
            {
                foreach (WaterfallParams cascade in body.Cascades)
                    data = Combine(data, WaterfallMesh.BuildSheet(cascade));
            }

            return data;
        }

        /// <summary>Four walls and a floor from the surface down to <paramref name="depth"/>.</summary>
        public static MeshData BuildVolumeHull(Vector3 center, float sizeX, float sizeZ, float surfaceY, float depth)
        {
            float halfX = MathF.Max(0.05f, sizeX) * 0.5f;
            float halfZ = MathF.Max(0.05f, sizeZ) * 0.5f;
            float bottomY = surfaceY - MathF.Max(0.05f, depth);
            var verts = new List<MeshVertex>(20);
            var indices = new List<ushort>(30);

            Vector3 topNxNz = new(center.X - halfX, surfaceY, center.Z - halfZ);
            Vector3 topPxNz = new(center.X + halfX, surfaceY, center.Z - halfZ);
            Vector3 topPxPz = new(center.X + halfX, surfaceY, center.Z + halfZ);
            Vector3 topNxPz = new(center.X - halfX, surfaceY, center.Z + halfZ);
            Vector3 botNxNz = new(center.X - halfX, bottomY, center.Z - halfZ);
            Vector3 botPxNz = new(center.X + halfX, bottomY, center.Z - halfZ);
            Vector3 botPxPz = new(center.X + halfX, bottomY, center.Z + halfZ);
            Vector3 botNxPz = new(center.X - halfX, bottomY, center.Z + halfZ);

            AddQuad(verts, indices, botNxNz, botPxNz, topPxNz, topNxNz, -Vector3.UnitZ);
            AddQuad(verts, indices, botPxNz, botPxPz, topPxPz, topPxNz, Vector3.UnitX);
            AddQuad(verts, indices, botPxPz, botNxPz, topNxPz, topPxPz, Vector3.UnitZ);
            AddQuad(verts, indices, botNxPz, botNxNz, topNxNz, topNxPz, -Vector3.UnitX);
            AddQuad(verts, indices, botNxNz, botNxPz, botPxPz, botPxNz, -Vector3.UnitY);

            return new MeshData
            {
                Vertices = verts.ToArray(),
                Indices = indices.ToArray(),
            };
        }

        public static MeshData Combine(MeshData a, MeshData b)
        {
            if (a.Vertices == null || a.Vertices.Length == 0)
                return b;
            if (b.Vertices == null || b.Vertices.Length == 0)
                return a;

            int vertCount = a.Vertices.Length + b.Vertices.Length;
            if (vertCount > 65535)
                return a;

            var vertices = new MeshVertex[vertCount];
            Array.Copy(a.Vertices, 0, vertices, 0, a.Vertices.Length);
            Array.Copy(b.Vertices, 0, vertices, a.Vertices.Length, b.Vertices.Length);

            int aIndexCount = a.Indices == null ? 0 : a.Indices.Length;
            int bIndexCount = b.Indices == null ? 0 : b.Indices.Length;
            var indices = new ushort[aIndexCount + bIndexCount];
            if (aIndexCount > 0)
                Array.Copy(a.Indices, 0, indices, 0, aIndexCount);
            int baseVertex = a.Vertices.Length;
            for (int i = 0; i < bIndexCount; i++)
                indices[aIndexCount + i] = (ushort)(b.Indices[i] + baseVertex);

            return new MeshData { Vertices = vertices, Indices = indices };
        }

        public static float VerticalSpan(MeshData data)
        {
            if (data.Vertices == null || data.Vertices.Length == 0)
                return 0f;
            float minY = data.Vertices[0].Position.Y;
            float maxY = minY;
            for (int i = 1; i < data.Vertices.Length; i++)
            {
                float y = data.Vertices[i].Position.Y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            return maxY - minY;
        }

        private static bool TryGetFootprintBits(WaterBody body, out byte[] bits)
        {
            bits = null;
            if (body == null
                || body.FootprintWidth <= 0
                || body.FootprintHeight <= 0
                || body.FootprintCellSize <= 0.0001f
                || string.IsNullOrEmpty(body.Footprint))
            {
                return false;
            }

            try
            {
                bits = Convert.FromBase64String(body.Footprint);
            }
            catch (FormatException)
            {
                return false;
            }

            return bits != null && bits.Length > 0;
        }

        private static bool FootprintCell(byte[] bits, int width, int height, int x, int z)
        {
            if (bits == null || (uint)x >= (uint)width || (uint)z >= (uint)height) return false;
            int i = z * width + x;
            int bi = i >> 3;
            if ((uint)bi >= (uint)bits.Length) return false;
            return (bits[bi] & (1 << (i & 7))) != 0;
        }

        private static MeshData BuildFootprintSurface(WaterBody body, byte[] bits)
        {
            int sourceWidth = body.FootprintWidth;
            int sourceHeight = body.FootprintHeight;
            int stride = Math.Max(1, (int)MathF.Ceiling(Math.Max(sourceWidth, sourceHeight) / 120f));
            int width = (sourceWidth + stride - 1) / stride;
            int height = (sourceHeight + stride - 1) / stride;
            float cell = body.FootprintCellSize * stride;
            float y = body.SurfaceY;
            var verts = new List<MeshVertex>();
            var indices = new List<ushort>();
            var vertexLookup = new Dictionary<(int X, int Z), ushort>();
            for (int z = -1; z < height; z++)
            for (int x = -1; x < width; x++)
            {
                int mask = (Marked(x, z) ? 1 : 0)
                         | (Marked(x + 1, z) ? 2 : 0)
                         | (Marked(x + 1, z + 1) ? 4 : 0)
                         | (Marked(x, z + 1) ? 8 : 0);
                if (mask == 0) continue;

                (int X, int Z) p00 = (2 * x + 1, 2 * z + 1);
                (int X, int Z) p10 = (2 * x + 3, 2 * z + 1);
                (int X, int Z) p11 = (2 * x + 3, 2 * z + 3);
                (int X, int Z) p01 = (2 * x + 1, 2 * z + 3);
                (int X, int Z) bottom = (2 * x + 2, 2 * z + 1);
                (int X, int Z) right = (2 * x + 3, 2 * z + 2);
                (int X, int Z) top = (2 * x + 2, 2 * z + 3);
                (int X, int Z) left = (2 * x + 1, 2 * z + 2);

                switch (mask)
                {
                    case 1: AddPolygon(p00, bottom, left); break;
                    case 2: AddPolygon(bottom, p10, right); break;
                    case 3: AddPolygon(p00, p10, right, left); break;
                    case 4: AddPolygon(right, p11, top); break;
                    case 5: AddPolygon(p00, bottom, left); AddPolygon(right, p11, top); break;
                    case 6: AddPolygon(bottom, p10, p11, top); break;
                    case 7: AddPolygon(p00, p10, p11, top, left); break;
                    case 8: AddPolygon(left, top, p01); break;
                    case 9: AddPolygon(p00, bottom, top, p01); break;
                    case 10: AddPolygon(bottom, p10, right); AddPolygon(left, top, p01); break;
                    case 11: AddPolygon(p00, p10, right, top, p01); break;
                    case 12: AddPolygon(left, right, p11, p01); break;
                    case 13: AddPolygon(p00, bottom, right, p11, p01); break;
                    case 14: AddPolygon(bottom, p10, p11, p01, left); break;
                    case 15: AddPolygon(p00, p10, p11, p01); break;
                }
            }

            return new MeshData { Vertices = verts.ToArray(), Indices = indices.ToArray() };

            bool Marked(int x, int z)
            {
                if ((uint)x >= (uint)width || (uint)z >= (uint)height) return false;
                int sourceX0 = x * stride;
                int sourceZ0 = z * stride;
                int sourceX1 = Math.Min(sourceWidth, sourceX0 + stride);
                int sourceZ1 = Math.Min(sourceHeight, sourceZ0 + stride);
                for (int sourceZ = sourceZ0; sourceZ < sourceZ1; sourceZ++)
                for (int sourceX = sourceX0; sourceX < sourceX1; sourceX++)
                    if (FootprintCell(bits, sourceWidth, sourceHeight, sourceX, sourceZ)) return true;
                return false;
            }

            void AddPolygon(params (int X, int Z)[] points)
            {
                if (points.Length < 3) return;
                ushort first = AddVertex(points[0]);
                for (int i = 1; i < points.Length - 1; i++)
                {
                    indices.Add(first);
                    indices.Add(AddVertex(points[i]));
                    indices.Add(AddVertex(points[i + 1]));
                }
            }

            ushort AddVertex((int X, int Z) point)
            {
                if (vertexLookup.TryGetValue(point, out ushort existing)) return existing;
                if (verts.Count >= ushort.MaxValue)
                    throw new InvalidOperationException("Water footprint exceeds the 16-bit mesh vertex budget.");
                float worldX = body.FootprintOriginX + point.X * cell * .5f;
                float worldZ = body.FootprintOriginZ + point.Z * cell * .5f;
                bool boundary = (point.X & 1) == 0 || (point.Z & 1) == 0;
                float alpha = boundary ? .18f : 1f;
                ushort index = (ushort)verts.Count;
                verts.Add(new MeshVertex
                {
                    Position = new Vector3(worldX, y, worldZ),
                    Normal = Vector3.UnitY,
                    Color = new Vector4(1f, 1f, 1f, alpha),
                    UV = new Vector2(
                        Math.Clamp((worldX - body.FootprintOriginX) / MathF.Max(cell, width * cell), 0f, 1f),
                        Math.Clamp((worldZ - body.FootprintOriginZ) / MathF.Max(cell, height * cell), 0f, 1f)),
                });
                vertexLookup[point] = index;
                return index;
            }
        }

        private static MeshData BuildFootprintHull(WaterBody body, byte[] bits, float depth)
        {
            int width = body.FootprintWidth;
            int height = body.FootprintHeight;
            float cell = body.FootprintCellSize;
            float topY = body.SurfaceY;
            float bottomY = topY - MathF.Max(0.05f, depth);
            var verts = new List<MeshVertex>();
            var indices = new List<ushort>();
            for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                if (!FootprintCell(bits, width, height, x, z)) continue;
                if (verts.Count + 20 > 65000) return BuildVolumeHull(body.Center, body.SizeX, body.SizeZ, topY, depth);

                float x0 = body.FootprintOriginX + x * cell;
                float z0 = body.FootprintOriginZ + z * cell;
                float x1 = x0 + cell;
                float z1 = z0 + cell;
                AddQuad(
                    verts, indices,
                    new Vector3(x0, bottomY, z0),
                    new Vector3(x0, bottomY, z1),
                    new Vector3(x1, bottomY, z1),
                    new Vector3(x1, bottomY, z0),
                    -Vector3.UnitY);

                if (!FootprintCell(bits, width, height, x, z - 1))
                {
                    AddQuad(
                        verts, indices,
                        new Vector3(x0, bottomY, z0),
                        new Vector3(x1, bottomY, z0),
                        new Vector3(x1, topY, z0),
                        new Vector3(x0, topY, z0),
                        -Vector3.UnitZ);
                }

                if (!FootprintCell(bits, width, height, x, z + 1))
                {
                    AddQuad(
                        verts, indices,
                        new Vector3(x1, bottomY, z1),
                        new Vector3(x0, bottomY, z1),
                        new Vector3(x0, topY, z1),
                        new Vector3(x1, topY, z1),
                        Vector3.UnitZ);
                }

                if (!FootprintCell(bits, width, height, x - 1, z))
                {
                    AddQuad(
                        verts, indices,
                        new Vector3(x0, bottomY, z1),
                        new Vector3(x0, bottomY, z0),
                        new Vector3(x0, topY, z0),
                        new Vector3(x0, topY, z1),
                        -Vector3.UnitX);
                }

                if (!FootprintCell(bits, width, height, x + 1, z))
                {
                    AddQuad(
                        verts, indices,
                        new Vector3(x1, bottomY, z0),
                        new Vector3(x1, bottomY, z1),
                        new Vector3(x1, topY, z1),
                        new Vector3(x1, topY, z0),
                        Vector3.UnitX);
                }
            }

            return new MeshData { Vertices = verts.ToArray(), Indices = indices.ToArray() };
        }

        private static MeshData FallbackFootprintAabb(WaterBody body) =>
            BuildLakeGrid(body.Center, body.SizeX, body.SizeZ, body.SurfaceY, Math.Max(2, body.GridResolution));

        private static void AddQuad(
            List<MeshVertex> verts,
            List<ushort> indices,
            Vector3 a,
            Vector3 b,
            Vector3 c,
            Vector3 d,
            Vector3 normal)
        {
            int start = verts.Count;
            verts.Add(new MeshVertex { Position = a, Normal = normal, Color = Vector4.One, UV = new Vector2(0f, 1f) });
            verts.Add(new MeshVertex { Position = b, Normal = normal, Color = Vector4.One, UV = new Vector2(1f, 1f) });
            verts.Add(new MeshVertex { Position = c, Normal = normal, Color = Vector4.One, UV = new Vector2(1f, 0f) });
            verts.Add(new MeshVertex { Position = d, Normal = normal, Color = Vector4.One, UV = new Vector2(0f, 0f) });
            indices.Add((ushort)start);
            indices.Add((ushort)(start + 1));
            indices.Add((ushort)(start + 2));
            indices.Add((ushort)start);
            indices.Add((ushort)(start + 2));
            indices.Add((ushort)(start + 3));
        }

        /// <summary>Camera-following ocean tile (single grid snapped to tile boundaries).</summary>
        public static MeshData BuildOceanTile(Vector3 cameraPos, float tileSize, float surfaceY, int resolution)
        {
            float half = tileSize * 0.5f;
            float snapX = MathF.Floor(cameraPos.X / tileSize) * tileSize;
            float snapZ = MathF.Floor(cameraPos.Z / tileSize) * tileSize;
            var origin = new Vector3(snapX - half, surfaceY, snapZ - half);
            return BuildGrid(origin, tileSize, tileSize, surfaceY, resolution, resolution);
        }

        /// <summary>Ribbon mesh following a polyline/spline at the surface height.</summary>
        public static MeshData BuildRiverRibbon(ReadOnlySpan<Vector3> splinePoints, float width, float surfaceY, int segmentsPerSpan)
            => BuildRiverRibbon(splinePoints, ReadOnlySpan<float>.Empty, width, surfaceY, segmentsPerSpan, false);

        public static MeshData BuildRiverRibbon(ReadOnlySpan<Vector3> splinePoints, ReadOnlySpan<float> widths,
            float width, float surfaceY, int segmentsPerSpan, bool usePointHeights)
        {
            if (splinePoints.Length < 2)
                return default;

            int segs = Math.Max(segmentsPerSpan, 2);
            var samples = new List<Vector3>();
            var sampledWidths = new List<float>();
            SampleSpline(splinePoints, widths, MathF.Max(.1f, width), segs, samples, sampledWidths);

            if (samples.Count < 2)
                return default;

            var verts = new List<MeshVertex>(samples.Count * 2);
            var indices = new List<ushort>(Math.Max(0, (samples.Count - 1) * 6));

            float totalLen = 0f;
            for (int i = 1; i < samples.Count; i++)
                totalLen += Vector3.Distance(samples[i - 1], samples[i]);
            if (totalLen < 0.001f)
                totalLen = 1f;

            float accLen = 0f;
            for (int i = 0; i < samples.Count; i++)
            {
                Vector3 p = samples[i];
                if (!usePointHeights) p.Y = surfaceY;

                Vector3 tangent;
                if (i == 0)
                    tangent = samples[1] - samples[0];
                else if (i == samples.Count - 1)
                    tangent = samples[i] - samples[i - 1];
                else
                    tangent = samples[i + 1] - samples[i - 1];

                tangent.Y = 0f;
                if (tangent.LengthSquared() < 1e-6f)
                    tangent = Vector3.UnitZ;
                tangent = Vector3.Normalize(tangent);

                Vector3 side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, tangent));
                float halfW = sampledWidths[i] * 0.5f;

                if (i > 0)
                    accLen += Vector3.Distance(samples[i], samples[i - 1]);
                float u = accLen / totalLen;

                var color = Vector4.One;
                verts.Add(new MeshVertex
                {
                    Position = p - side * halfW,
                    Normal = Vector3.UnitY,
                    Color = color,
                    // U spans bank-to-bank; V follows accumulated spline distance so
                    // scrolling the material in +V follows every bend.
                    UV = new Vector2(0f, u),
                });
                verts.Add(new MeshVertex
                {
                    Position = p + side * halfW,
                    Normal = Vector3.UnitY,
                    Color = color,
                    UV = new Vector2(1f, u),
                });
            }

            for (int i = 0; i < samples.Count - 1; i++)
            {
                int i0 = i * 2;
                int i1 = i0 + 1;
                int i2 = i0 + 2;
                int i3 = i0 + 3;
                indices.Add((ushort)i0);
                indices.Add((ushort)i3);
                indices.Add((ushort)i1);
                indices.Add((ushort)i0);
                indices.Add((ushort)i2);
                indices.Add((ushort)i3);
            }

            return new MeshData
            {
                Vertices = verts.ToArray(),
                Indices = indices.ToArray(),
            };
        }

        private static MeshData BuildGrid(Vector3 origin, float sizeX, float sizeZ, float surfaceY, int resX, int resZ)
        {
            int vertCount = (resX + 1) * (resZ + 1);
            var verts = new MeshVertex[vertCount];
            var indices = new List<ushort>(resX * resZ * 6);

            int vi = 0;
            for (int z = 0; z <= resZ; z++)
            {
                float vz = origin.Z + (sizeZ * z) / resZ;
                float tv = (float)z / resZ;
                for (int x = 0; x <= resX; x++)
                {
                    float vx = origin.X + (sizeX * x) / resX;
                    float tu = (float)x / resX;
                    verts[vi++] = new MeshVertex
                    {
                        Position = new Vector3(vx, surfaceY, vz),
                        Normal = Vector3.UnitY,
                        Color = Vector4.One,
                        UV = new Vector2(tu, tv),
                    };
                }
            }

            int row = resX + 1;
            for (int z = 0; z < resZ; z++)
            {
                for (int x = 0; x < resX; x++)
                {
                    int i0 = z * row + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + row;
                    int i3 = i2 + 1;
                    indices.Add((ushort)i0);
                    indices.Add((ushort)i3);
                    indices.Add((ushort)i1);
                    indices.Add((ushort)i0);
                    indices.Add((ushort)i2);
                    indices.Add((ushort)i3);
                }
            }

            return new MeshData { Vertices = verts, Indices = indices.ToArray() };
        }

        private static void SampleSpline(ReadOnlySpan<Vector3> points, int segmentsPerSpan, List<Vector3> output)
        {
            if (points.Length == 2)
            {
                output.Add(points[0]);
                for (int s = 1; s < segmentsPerSpan; s++)
                {
                    float t = s / (float)segmentsPerSpan;
                    output.Add(Vector3.Lerp(points[0], points[1], t));
                }
                output.Add(points[1]);
                return;
            }

            output.Add(points[0]);
            for (int i = 0; i < points.Length - 1; i++)
            {
                Vector3 p0 = i > 0 ? points[i - 1] : points[i];
                Vector3 p1 = points[i];
                Vector3 p2 = points[i + 1];
                Vector3 p3 = i + 2 < points.Length ? points[i + 2] : points[i + 1];

                for (int s = 1; s <= segmentsPerSpan; s++)
                {
                    float t = s / (float)segmentsPerSpan;
                    output.Add(BezierSpline(p0, p1, p2, p3, t));
                }
            }
        }

        private static void SampleSpline(ReadOnlySpan<Vector3> points, ReadOnlySpan<float> widths, float fallbackWidth,
            int segmentsPerSpan, List<Vector3> output, List<float> outputWidths)
        {
            SampleSpline(points, segmentsPerSpan, output);
            if (output.Count == 0) return;
            int spans = Math.Max(1, points.Length - 1);
            for (int i = 0; i < output.Count; i++)
            {
                float path = i / (float)Math.Max(1, output.Count - 1) * spans;
                int span = Math.Clamp((int)MathF.Floor(path), 0, spans - 1);
                float t = Math.Clamp(path - span, 0f, 1f);
                float a = span < widths.Length ? MathF.Max(.1f, widths[span]) : fallbackWidth;
                float b = span + 1 < widths.Length ? MathF.Max(.1f, widths[span + 1]) : a;
                outputWidths.Add(float.Lerp(a, b, t));
            }
        }

        private static Vector3 BezierSpline(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            Vector3 b0 = p1;
            Vector3 b1 = p1 + (p2 - p0) / 6f;
            Vector3 b2 = p2 - (p3 - p1) / 6f;
            Vector3 b3 = p2;
            float u = 1f - t;
            return u * u * u * b0
                + 3f * u * u * t * b1
                + 3f * u * t * t * b2
                + t * t * t * b3;
        }
    }
}
