using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Shared.Rendering
{
    /// <summary>
    /// Unified LOD distance policy consumed by mesh LOD selection and voxel remesh.
    /// Engine.SetLOD* writes here; cull/collect readers use Current.
    /// </summary>
    public sealed class LodPolicy
    {
        public static LodPolicy Current { get; } = new();

        public float Near { get; set; } = 100f;
        public float Mid { get; set; } = 500f;
        public float Far { get; set; } = 1000f;
        public bool ScalingEnabled { get; set; } = true;
        public float Scale { get; set; } = 1f;

        public float EffectiveNear => ScalingEnabled ? Near * Scale : Near;
        public float EffectiveMid => ScalingEnabled ? Mid * Scale : Mid;
        public float EffectiveFar => ScalingEnabled ? Far * Scale : Far;

        public int SelectLevel(float distance)
        {
            if (distance < EffectiveNear) return 0;
            if (distance < EffectiveMid) return 1;
            if (distance < EffectiveFar) return 2;
            return 3;
        }
    }

    /// <summary>Per-frame GPU-side auto-system toggles shared with RendererOptions.</summary>
    public static class RenderAutoState
    {
        public static bool FrustumCulling { get; set; } = true;
        /// <summary>Cull ECS drawables past the camera far plane / authored max draw distance (R7.10).</summary>
        public static bool DistanceCulling { get; set; } = true;
        /// <summary>Cull ECS drawables whose projected diameter is below <see cref="SubPixelCullPixels"/> (R7.10).</summary>
        public static bool SubPixelCulling { get; set; } = true;
        /// <summary>Projected sphere diameter threshold in pixels for sub-pixel cull.</summary>
        public static float SubPixelCullPixels { get; set; } = 1f;
        public static bool OcclusionCulling { get; set; }
        public static bool GpuCulling { get; set; }
        /// <summary>True only during pipeline Submit — Engine/PGSL Draw* enqueue when set.</summary>
        public static bool AllowDrawSubmit { get; set; }
        public static int CulledByFrustum;
        public static int CulledByDistance;
        public static int CulledBySubPixel;
        public static int CulledByOcclusion;
        public static int SubmittedMeshes;
        public static int ShadowCastersSubmitted;
        public static int DrawSubmitRejected;

        public static void ResetFrameCounters()
        {
            CulledByFrustum = 0;
            CulledByDistance = 0;
            CulledBySubPixel = 0;
            CulledByOcclusion = 0;
            SubmittedMeshes = 0;
            ShadowCastersSubmitted = 0;
            DrawSubmitRejected = 0;
        }
    }

    /// <summary>
    /// Large static AABBs (terrain/voxel chunks) for software occlusion rasterization.
    /// Updated by <c>SetChunkBounds</c>; Visibility pass reads them each frame.
    /// </summary>
    public static class OccluderBoundsRegistry
    {
        private static readonly Dictionary<int, (Vector3 Min, Vector3 Max)> Boxes = new();

        public static int Count => Boxes.Count;

        public static void Clear() => Boxes.Clear();

        public static void Set(int chunkId, Vector3 min, Vector3 max)
        {
            if (chunkId <= 0) return;
            Boxes[chunkId] = (min, max);
        }

        public static void Remove(int chunkId) => Boxes.Remove(chunkId);

        public static void ForEach(Action<Vector3, Vector3> action)
        {
            if (action == null) return;
            foreach (var kv in Boxes)
                action(kv.Value.Min, kv.Value.Max);
        }
    }

    public static class BoundsHelper
    {
        public static void SphereFromAabb(Vector3 min, Vector3 max, out Vector3 center, out float radius)
        {
            center = (min + max) * 0.5f;
            radius = Vector3.Distance(center, max);
        }

        public static float BoundingRadiusFromScale(float scaleX, float scaleY, float scaleZ, float baseRadius = 0.5f)
        {
            float sx = scaleX == 0f ? 1f : MathF.Abs(scaleX);
            float sy = scaleY == 0f ? 1f : MathF.Abs(scaleY);
            float sz = scaleZ == 0f ? 1f : MathF.Abs(scaleZ);
            return baseRadius * MathF.Max(sx, MathF.Max(sy, sz));
        }
    }
}
