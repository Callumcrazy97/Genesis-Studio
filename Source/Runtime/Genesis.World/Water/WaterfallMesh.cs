using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;
using MeshData = Genesis.World.MeshData;

namespace Genesis.World.Water
{
    /// <summary>Vertical/curved waterfall sheet mesh with downward UV flow.</summary>
    public static class WaterfallMesh
    {
        public static MeshData BuildSheet(WaterfallParams desc)
        {
            if (desc == null)
                return default;

            int segs = Math.Clamp(desc.VerticalSegments, 2, 64);
            var topL = desc.TopLeft;
            var topR = desc.TopRight;
            var botL = desc.BottomLeft;
            var botR = desc.BottomRight;
            Vector3 flow = desc.FlowDirection;
            flow.Y = 0f;
            flow = flow.LengthSquared() > 1e-6f ? Vector3.Normalize(flow) : Vector3.UnitZ;
            float crest = MathF.Max(0f, desc.CrestRadius);

            var verts = new List<MeshVertex>((segs + 1) * 2);
            var indices = new List<ushort>(segs * 6);

            for (int i = 0; i <= segs; i++)
            {
                float t = i / (float)segs;
                Vector3 left = CrestCurve(topL, botL, flow, crest, t);
                Vector3 right = CrestCurve(topR, botR, flow, crest, t);

                Vector3 edge = right - left;
                float nextT = MathF.Min(1f, t + 1f / segs);
                Vector3 tangent = CrestCurve((topL + topR) * .5f, (botL + botR) * .5f, flow, crest, nextT)
                    - CrestCurve((topL + topR) * .5f, (botL + botR) * .5f, flow, crest, MathF.Max(0f, t - 1f / segs));
                Vector3 cross = Vector3.Cross(edge, tangent);
                Vector3 normal = cross.LengthSquared() < 1e-6f
                    ? Vector3.UnitZ
                    : Vector3.Normalize(cross);

                float v = t;
                verts.Add(new MeshVertex
                {
                    Position = left,
                    Normal = normal,
                    Color = Vector4.One,
                    UV = new Vector2(0f, v),
                });
                verts.Add(new MeshVertex
                {
                    Position = right,
                    Normal = normal,
                    Color = Vector4.One,
                    UV = new Vector2(1f, v),
                });
            }

            for (int i = 0; i < segs; i++)
            {
                int i0 = i * 2;
                int i1 = i0 + 1;
                int i2 = i0 + 2;
                int i3 = i0 + 3;
                indices.Add((ushort)i0);
                indices.Add((ushort)i2);
                indices.Add((ushort)i1);
                indices.Add((ushort)i1);
                indices.Add((ushort)i2);
                indices.Add((ushort)i3);
            }

            return new MeshData
            {
                Vertices = verts.ToArray(),
                Indices = indices.ToArray(),
            };
        }

        private static Vector3 CrestCurve(Vector3 top, Vector3 bottom, Vector3 flow, float radius, float t)
        {
            Vector3 p1 = top + flow * radius;
            Vector3 p2 = bottom - flow * radius * .18f + Vector3.UnitY * MathF.Min(radius, MathF.Abs(top.Y - bottom.Y) * .2f);
            float u = 1f - t;
            return u * u * u * top + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * bottom;
        }

        public static MeshData BuildSheetFromWidth(Vector3 topCenter, float width, float height, int segments, float facingYawRadians = 0f)
        {
            float half = width * 0.5f;
            float c = MathF.Cos(facingYawRadians);
            float s = MathF.Sin(facingYawRadians);
            var right = new Vector3(c, 0f, -s);

            var topL = topCenter - right * half;
            var topR = topCenter + right * half;
            var botL = topL - Vector3.UnitY * height;
            var botR = topR - Vector3.UnitY * height;

            return BuildSheet(new WaterfallParams
            {
                TopLeft = topL,
                TopRight = topR,
                BottomLeft = botL,
                BottomRight = botR,
                VerticalSegments = segments,
            });
        }
    }
}
