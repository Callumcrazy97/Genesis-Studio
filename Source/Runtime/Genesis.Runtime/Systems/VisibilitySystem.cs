using System;
using System.Numerics;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime.Culling;
using Genesis.Runtime.ECS;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Systems
{
    /// <summary>
    /// Auto frustum + distance + sub-pixel visibility (R7.10). Writes
    /// <see cref="VisibilityComponent.LastResult"/> before LOD / frame budget / mesh collect.
    /// Large entity sets batch sphere frustum tests through <see cref="VisibilityJobs"/>.
    /// </summary>
    public sealed class VisibilitySystem : IEcsSystem
    {
        /// <summary>Projected sphere diameter below this (pixels) is culled when sub-pixel cull is on.</summary>
        public const float DefaultSubPixelCullPixels = 1f;

        /// <summary>Use parallel frustum jobs at or above this entity count.</summary>
        public const int ParallelFrustumThreshold = 64;

        private readonly RuntimeScene _scene;
        private Entity[] _entities = new Entity[64];
        private Vector3[] _centers = new Vector3[64];
        private float[] _radii = new float[64];
        private byte[] _frustumVisible = new byte[64];

        public VisibilitySystem(RuntimeScene scene) => _scene = scene;

        public int Priority => SystemPhase.Visibility;

        public void Update(IEcsWorld world, float dt)
        {
            _ = dt;
            bool frustumOn = RenderAutoState.FrustumCulling;
            bool distanceOn = RenderAutoState.DistanceCulling;
            bool subPixelOn = RenderAutoState.SubPixelCulling;
            float subPixelPx = MathF.Max(0.25f, RenderAutoState.SubPixelCullPixels);
            Vector3 cameraPos = _scene.Camera3D.Position;
            float farPlane = MathF.Max(_scene.Camera3D.NearPlane + 1f, _scene.Camera3D.FarPlane);
            float viewportHeight = MathF.Max(1f, _scene.RenderViewportHeightPixels);
            float fov = Math.Clamp(_scene.Camera3D.FieldOfView, 0.05f, MathF.PI - 0.05f);
            CameraFrustum cameraFrustum = new(_scene.Camera3D.ViewProjection);

            int count = 0;
            world.Query<Transform3DComponent, VisibilityComponent>((Entity entity, ref Transform3DComponent transform, ref VisibilityComponent visibility) =>
            {
                if (!visibility.Enabled || !LifecycleAllows(world, entity))
                {
                    visibility.LastResult = VisibilityResult.Disabled;
                    return;
                }

                if (!frustumOn && !distanceOn && !subPixelOn)
                {
                    visibility.LastResult = VisibilityResult.Visible;
                    return;
                }

                EnsureCapacity(count + 1);
                ResolveSphere(in transform, in visibility, out Vector3 center, out float radius);
                _entities[count] = entity;
                _centers[count] = center;
                _radii[count] = radius;
                count++;
            });

            if (count == 0)
                return;

            if (frustumOn)
            {
                if (count >= ParallelFrustumThreshold)
                {
                    Frustum jobFrustum = new(_scene.Camera3D.ViewProjection);
                    VisibilityJobs.TestSpheres(
                        jobFrustum, _centers, _radii, _frustumVisible, count, parallel: true);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                        _frustumVisible[i] = cameraFrustum.ContainsSphere(_centers[i], _radii[i]) ? (byte)1 : (byte)0;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                    _frustumVisible[i] = 1;
            }

            for (int i = 0; i < count; i++)
            {
                ref VisibilityComponent visibility = ref world.GetRef<VisibilityComponent>(_entities[i]);
                if (_frustumVisible[i] == 0)
                {
                    visibility.LastResult = VisibilityResult.FrustumCulled;
                    RenderAutoState.CulledByFrustum++;
                    continue;
                }

                float distance = Vector3.Distance(cameraPos, _centers[i]);
                float maxDistance = visibility.MaxDrawDistance > 0f
                    ? visibility.MaxDrawDistance
                    : farPlane;
                if (distanceOn && distance - _radii[i] > maxDistance)
                {
                    visibility.LastResult = VisibilityResult.DistanceCulled;
                    RenderAutoState.CulledByDistance++;
                    continue;
                }

                if (subPixelOn
                    && IsSubPixelCulled(distance, _radii[i], viewportHeight, fov, subPixelPx))
                {
                    visibility.LastResult = VisibilityResult.SubPixelCulled;
                    RenderAutoState.CulledBySubPixel++;
                    continue;
                }

                visibility.LastResult = VisibilityResult.Visible;
            }
        }

        /// <summary>Projected sphere diameter in pixels (same focal model as <see cref="LodSystem"/>).</summary>
        public static float ProjectedDiameterPixels(
            float distance,
            float radius,
            float viewportHeight,
            float fieldOfView)
        {
            float safeDistance = MathF.Max(0.001f, distance);
            float focalPixels = MathF.Max(1f, viewportHeight) / (2f * MathF.Tan(fieldOfView * 0.5f));
            return 2f * radius * focalPixels / safeDistance;
        }

        public static bool IsSubPixelCulled(
            float distance,
            float radius,
            float viewportHeight,
            float fieldOfView,
            float thresholdPixels = DefaultSubPixelCullPixels)
        {
            return ProjectedDiameterPixels(distance, radius, viewportHeight, fieldOfView)
                < MathF.Max(0.25f, thresholdPixels);
        }

        private void EnsureCapacity(int needed)
        {
            if (_entities.Length >= needed) return;
            int cap = Math.Max(needed, _entities.Length * 2);
            Array.Resize(ref _entities, cap);
            Array.Resize(ref _centers, cap);
            Array.Resize(ref _radii, cap);
            Array.Resize(ref _frustumVisible, cap);
        }

        private static void ResolveSphere(
            in Transform3DComponent transform,
            in VisibilityComponent visibility,
            out Vector3 center,
            out float radius)
        {
            Vector3 scaledOffset = new(
                visibility.BoundsOffset.X * transform.Scale.X,
                visibility.BoundsOffset.Y * transform.Scale.Y,
                visibility.BoundsOffset.Z * transform.Scale.Z);
            center = transform.Position + Vector3.Transform(scaledOffset, transform.Rotation);

            float scale = MaxAbsScale(transform.Scale);
            float localRadius = visibility.BoundsRadius > 0f ? visibility.BoundsRadius : 0.5f;
            radius = localRadius * scale;
        }

        private static bool LifecycleAllows(IEcsWorld world, Entity entity)
        {
            if (!world.Has<EntityLifecycleComponent>(entity))
                return true;

            return world.GetRef<EntityLifecycleComponent>(entity).Enabled;
        }

        private static float MaxAbsScale(Vector3 scale)
        {
            float x = scale.X == 0f ? 1f : MathF.Abs(scale.X);
            float y = scale.Y == 0f ? 1f : MathF.Abs(scale.Y);
            float z = scale.Z == 0f ? 1f : MathF.Abs(scale.Z);
            return MathF.Max(x, MathF.Max(y, z));
        }
    }
}
