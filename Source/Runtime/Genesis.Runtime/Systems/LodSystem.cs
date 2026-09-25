using System;
using System.Numerics;
using Genesis.Runtime.ECS;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using GlobalLodPolicy = Genesis.Shared.Rendering.LodPolicy;

namespace Genesis.Runtime.Systems
{
    /// <summary>
    /// Chooses the local LOD level for visible ECS drawables. The result is intentionally kept
    /// separate from <see cref="LodComponent.Bias"/> so the later frame-budget controller cannot
    /// ratchet its own emergency coarsening into the component's hysteresis state.
    /// </summary>
    public sealed class LodSystem : IEcsSystem
    {
        private const float DefaultScreenErrorPixels = 2f;
        private const float DefaultHysteresisFraction = 0.14f;
        private readonly RuntimeScene _scene;

        public LodSystem(RuntimeScene scene) => _scene = scene;

        public int Priority => SystemPhase.Lod;

        public void Update(IEcsWorld world, float dt)
        {
            Vector3 cameraPosition = _scene.Camera3D.Position;
            float viewportHeight = MathF.Max(1f, _scene.RenderViewportHeightPixels);
            float fov = Math.Clamp(_scene.Camera3D.FieldOfView, 0.05f, MathF.PI - 0.05f);

            world.Query<Transform3DComponent, LodComponent>((Entity entity, ref Transform3DComponent transform, ref LodComponent lod) =>
            {
                if (!LifecycleAllows(world, entity) || !VisibilityAllows(world, entity))
                    return;

                if (lod.Policy == LodSelectionPolicy.Never)
                {
                    lod.Selected = 0;
                    lod.SelectionValid = 1;
                    return;
                }

                if (lod.Policy == LodSelectionPolicy.Fixed)
                {
                    lod.Selected = (byte)Math.Min(3, (int)lod.Selected);
                    lod.SelectionValid = 1;
                    return;
                }

                float distance = Vector3.Distance(cameraPosition, transform.Position);
                byte candidate = lod.Policy == LodSelectionPolicy.Distance
                    ? SelectDistanceLevel(distance, in lod)
                    : SelectScreenLevel(distance, transform.Scale, world, entity, viewportHeight, fov, in lod);

                lod.Selected = ApplyHysteresis(lod, candidate, distance, transform.Scale, world, entity, viewportHeight, fov);
                lod.SelectionValid = 1;
            });
        }

        /// <summary>Distance-only selector used by tests and authoring diagnostics.</summary>
        public static byte SelectDistanceLevel(float distance, in LodComponent lod)
        {
            ResolveDistanceThresholds(in lod, out float t0, out float t1, out float t2);
            return distance < t0 ? (byte)0 : distance < t1 ? (byte)1 : distance < t2 ? (byte)2 : (byte)3;
        }

        /// <summary>
        /// Returns a projected-radius LOD using the component's screen-error target. Higher viewport
        /// resolution or narrower FOV retains detail further away, unlike raw world-distance bands.
        /// </summary>
        public static byte SelectScreenLevel(
            float distance,
            Vector3 scale,
            IEcsWorld world,
            Entity entity,
            float viewportHeight,
            float fieldOfView,
            in LodComponent lod)
        {
            float localRadius = 0.5f;
            if (world.Has<VisibilityComponent>(entity))
            {
                float authored = world.GetRef<VisibilityComponent>(entity).BoundsRadius;
                if (authored > 0f)
                    localRadius = authored;
            }

            float radius = localRadius * MaxAbsScale(scale);
            float safeDistance = MathF.Max(0.001f, distance);
            float focalPixels = MathF.Max(1f, viewportHeight) / (2f * MathF.Tan(fieldOfView * 0.5f));
            float projectedRadiusPixels = radius * focalPixels / safeDistance;
            float target = lod.ScreenErrorPixels > 0f ? lod.ScreenErrorPixels : DefaultScreenErrorPixels;

            // Four stable bands from one authored screen-error target. These are deliberately powers
            // of two so each coarser level roughly halves the projected-detail requirement.
            if (projectedRadiusPixels >= target * 8f) return 0;
            if (projectedRadiusPixels >= target * 4f) return 1;
            if (projectedRadiusPixels >= target * 2f) return 2;
            return 3;
        }

        private static byte ApplyHysteresis(
            in LodComponent lod,
            byte candidate,
            float distance,
            Vector3 scale,
            IEcsWorld world,
            Entity entity,
            float viewportHeight,
            float fieldOfView)
        {
            if (lod.SelectionValid == 0 || lod.Selected > 3 || lod.Selected == candidate)
                return candidate;

            float hysteresis = lod.Hysteresis == 0
                ? DefaultHysteresisFraction
                : Math.Clamp(lod.Hysteresis / 100f, 0f, 0.35f);

            byte previous = lod.Selected;
            if (lod.Policy == LodSelectionPolicy.Distance)
            {
                ResolveDistanceThresholds(in lod, out float t0, out float t1, out float t2);
                return ApplyDistanceHysteresis(previous, distance, t0, t1, t2, hysteresis);
            }

            // Screen selection's signal decreases as the object gets smaller. Re-evaluate the
            // projected-size signal and apply the same asymmetric entry/exit band around boundaries.
            float localRadius = 0.5f;
            if (world.Has<VisibilityComponent>(entity))
            {
                float authored = world.GetRef<VisibilityComponent>(entity).BoundsRadius;
                if (authored > 0f)
                    localRadius = authored;
            }

            float radius = localRadius * MaxAbsScale(scale);
            float safeDistance = MathF.Max(0.001f, distance);
            float focalPixels = MathF.Max(1f, viewportHeight) / (2f * MathF.Tan(fieldOfView * 0.5f));
            float projectedRadiusPixels = radius * focalPixels / safeDistance;
            float target = lod.ScreenErrorPixels > 0f ? lod.ScreenErrorPixels : DefaultScreenErrorPixels;
            return ApplyScreenHysteresis(previous, projectedRadiusPixels, target, hysteresis);
        }

        private static byte ApplyDistanceHysteresis(
            byte previous,
            float distance,
            float t0,
            float t1,
            float t2,
            float hysteresis)
        {
            byte selected = previous;
            if (selected == 0 && distance > t0 * (1f + hysteresis)) selected = 1;
            else if (selected == 1)
            {
                if (distance < t0 * (1f - hysteresis)) selected = 0;
                else if (distance > t1 * (1f + hysteresis)) selected = 2;
            }
            else if (selected == 2)
            {
                if (distance < t1 * (1f - hysteresis)) selected = 1;
                else if (distance > t2 * (1f + hysteresis)) selected = 3;
            }
            else if (selected == 3 && distance < t2 * (1f - hysteresis)) selected = 2;
            return selected;
        }

        private static byte ApplyScreenHysteresis(
            byte previous,
            float projectedRadiusPixels,
            float target,
            float hysteresis)
        {
            float b0 = target * 8f;
            float b1 = target * 4f;
            float b2 = target * 2f;
            byte selected = previous;

            if (selected == 0 && projectedRadiusPixels < b0 * (1f - hysteresis)) selected = 1;
            else if (selected == 1)
            {
                if (projectedRadiusPixels > b0 * (1f + hysteresis)) selected = 0;
                else if (projectedRadiusPixels < b1 * (1f - hysteresis)) selected = 2;
            }
            else if (selected == 2)
            {
                if (projectedRadiusPixels > b1 * (1f + hysteresis)) selected = 1;
                else if (projectedRadiusPixels < b2 * (1f - hysteresis)) selected = 3;
            }
            else if (selected == 3 && projectedRadiusPixels > b2 * (1f + hysteresis)) selected = 2;
            return selected;
        }

        private static void ResolveDistanceThresholds(
            in LodComponent lod,
            out float t0,
            out float t1,
            out float t2)
        {
            if (lod.Distance0 > 0f && lod.Distance1 > lod.Distance0 && lod.Distance2 > lod.Distance1)
            {
                t0 = lod.Distance0;
                t1 = lod.Distance1;
                t2 = lod.Distance2;
                return;
            }

            t0 = GlobalLodPolicy.Current.EffectiveNear;
            t1 = GlobalLodPolicy.Current.EffectiveMid;
            t2 = GlobalLodPolicy.Current.EffectiveFar;
        }

        private static bool LifecycleAllows(IEcsWorld world, Entity entity)
        {
            if (!world.Has<EntityLifecycleComponent>(entity))
                return true;
            return world.GetRef<EntityLifecycleComponent>(entity).Enabled;
        }

        private static bool VisibilityAllows(IEcsWorld world, Entity entity)
        {
            if (!world.Has<VisibilityComponent>(entity))
                return true;

            VisibilityComponent visibility = world.GetRef<VisibilityComponent>(entity);
            return visibility.Enabled && visibility.LastResult == VisibilityResult.Visible;
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
