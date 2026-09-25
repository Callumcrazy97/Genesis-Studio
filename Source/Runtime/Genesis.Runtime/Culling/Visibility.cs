using System;
using System.Numerics;
using Genesis.Rendering.D3dMath;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Culling
{
    /// <summary>
    /// Coarse software depth buffer for large occluders (terrain/static AABBs).
    /// Culled objects never enter FrameRenderQueue when Engine occlusion is on.
    /// </summary>
    public sealed class SoftwareOcclusionBuffer
    {
        private readonly int _width;
        private readonly int _height;
        private readonly float[] _depth;

        public SoftwareOcclusionBuffer(int width = 64, int height = 36)
        {
            _width = Math.Max(8, width);
            _height = Math.Max(8, height);
            _depth = new float[_width * _height];
        }

        public void Clear()
        {
            Array.Fill(_depth, 1f);
        }

        public void RasterizeOccluderAabb(in Matrix4x4 viewProjection, Vector3 min, Vector3 max)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            corners[0] = new(min.X, min.Y, min.Z);
            corners[1] = new(max.X, min.Y, min.Z);
            corners[2] = new(min.X, max.Y, min.Z);
            corners[3] = new(max.X, max.Y, min.Z);
            corners[4] = new(min.X, min.Y, max.Z);
            corners[5] = new(max.X, min.Y, max.Z);
            corners[6] = new(min.X, max.Y, max.Z);
            corners[7] = new(max.X, max.Y, max.Z);

            float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f, nearest = 1f;
            int valid = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector4 clip = Vector4.Transform(new Vector4(corners[i], 1f), viewProjection);
                if (clip.W <= 1e-4f) continue;
                float ndcX = clip.X / clip.W;
                float ndcY = clip.Y / clip.W;
                float ndcZ = clip.Z / clip.W;
                float u = ndcX * 0.5f + 0.5f;
                float v = 1f - (ndcY * 0.5f + 0.5f);
                if (u < 0f || u > 1f || v < 0f || v > 1f) continue;
                minX = MathF.Min(minX, u);
                minY = MathF.Min(minY, v);
                maxX = MathF.Max(maxX, u);
                maxY = MathF.Max(maxY, v);
                nearest = MathF.Min(nearest, Math.Clamp(ndcZ * 0.5f + 0.5f, 0f, 1f));
                valid++;
            }

            if (valid == 0) return;

            int x0 = Math.Clamp((int)(minX * (_width - 1)), 0, _width - 1);
            int y0 = Math.Clamp((int)(minY * (_height - 1)), 0, _height - 1);
            int x1 = Math.Clamp((int)(maxX * (_width - 1)), 0, _width - 1);
            int y1 = Math.Clamp((int)(maxY * (_height - 1)), 0, _height - 1);
            for (int y = y0; y <= y1; y++)
            {
                int row = y * _width;
                for (int x = x0; x <= x1; x++)
                {
                    int idx = row + x;
                    if (nearest < _depth[idx])
                        _depth[idx] = nearest;
                }
            }
        }

        public bool IsOccluded(in Matrix4x4 viewProjection, Vector3 center, float radius)
        {
            Vector4 clip = Vector4.Transform(new Vector4(center, 1f), viewProjection);
            if (clip.W <= 1e-4f) return false;
            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float ndcZ = clip.Z / clip.W;
            float u = ndcX * 0.5f + 0.5f;
            float v = 1f - (ndcY * 0.5f + 0.5f);
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;

            int x = Math.Clamp((int)(u * (_width - 1)), 0, _width - 1);
            int y = Math.Clamp((int)(v * (_height - 1)), 0, _height - 1);
            float sample = _depth[y * _width + x];
            float depth = Math.Clamp(ndcZ * 0.5f + 0.5f, 0f, 1f);
            // Occluded when sample is meaningfully closer than the test sphere.
            return sample + 0.002f < depth - (radius * 0.0005f);
        }
    }

    /// <summary>Frustum + optional software occlusion test used before queue submit.</summary>
    public static class Visibility
    {
        public static readonly SoftwareOcclusionBuffer Occluders = new();

        public static bool IsVisible(
            in Frustum frustum,
            in Matrix4x4 viewProjection,
            Vector3 center,
            float radius,
            bool testOcclusion)
        {
            if (!frustum.ContainsSphere(center, radius))
            {
                RenderAutoState.CulledByFrustum++;
                return false;
            }

            if (testOcclusion && RenderAutoState.OcclusionCulling && Occluders.IsOccluded(viewProjection, center, radius))
            {
                RenderAutoState.CulledByOcclusion++;
                return false;
            }

            return true;
        }

        /// <summary>Visibility mark for read-only sphere tests (use <see cref="VisibilityJobs"/> for parallel).</summary>
        public static void TestMany(
            in Frustum frustum,
            ReadOnlySpan<Vector3> centers,
            ReadOnlySpan<float> radii,
            Span<byte> visible)
        {
            if (centers.Length != radii.Length || centers.Length != visible.Length)
                throw new ArgumentException("Visibility spans must match.");

            for (int i = 0; i < centers.Length; i++)
                visible[i] = frustum.ContainsSphere(centers[i], radii[i]) ? (byte)1 : (byte)0;
        }
    }
}
