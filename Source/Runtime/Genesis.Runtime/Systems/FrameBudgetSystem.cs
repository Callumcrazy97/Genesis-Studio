using System;
using Genesis.Runtime.ECS;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Runtime.Systems
{
    /// <summary>
    /// Runtime policy for the global drawable budget. It is disabled by default so existing rooms
    /// retain their authored behaviour until a project/editor explicitly opts into adaptive LOD.
    /// </summary>
    public sealed class FrameBudgetSettings
    {
        /// <summary>Master switch. Disabled by default during the Phase 1 foundation work.</summary>
        public bool Enabled { get; set; }

        /// <summary>Maximum estimated visible triangles before the global LOD bias tightens.</summary>
        public long TriangleBudget { get; set; } = 4_000_000;

        /// <summary>GPU target used when timestamp data is available.</summary>
        public float TargetFramesPerSecond { get; set; } = 75f;

        /// <summary>Small tolerance above the target before GPU time is considered over budget.</summary>
        public float OverBudgetMultiplier { get; set; } = 1.05f;

        /// <summary>
        /// Bias may relax only while every available budget signal stays below this fraction of its
        /// target. One half is deliberate: dropping one bias level can roughly double submitted cost.
        /// </summary>
        public float RelaxBudgetFraction { get; set; } = 0.50f;

        /// <summary>Consecutive comfortably-under-budget frames required before relaxing one level.</summary>
        public int RelaxFrames { get; set; } = 45;

        public double TargetGpuMilliseconds
            => TargetFramesPerSecond > 0f && float.IsFinite(TargetFramesPerSecond)
                ? 1_000.0 / TargetFramesPerSecond
                : 0.0;
    }

    /// <summary>Diagnostics produced by <see cref="FrameBudgetSystem"/> each update.</summary>
    public struct FrameBudgetSnapshot
    {
        public bool Enabled;
        public byte GlobalLodBias;
        public int VisibleLodEntities;
        public long LocalTriangleCost;
        public long EffectiveTriangleCost;
        public double LastGpuMilliseconds;
        public bool TriangleOverBudget;
        public bool GpuOverBudget;
        public int HeadroomFrames;
    }

    /// <summary>
    /// Global arbiter above per-entity LOD selection. Local selectors decide what each object would
    /// like to draw; this system decides whether the whole visible set is affordable together.
    /// </summary>
    /// <remarks>
    /// The feedback is intentionally asymmetric, carrying over the useful AetherForge behaviour:
    /// tighten by at most one LOD level in a single frame when either measured GPU time or estimated
    /// triangle cost is over budget, but relax by one level only after sustained deep headroom.
    /// This avoids a controller that continually hunts around the budget boundary and damages 1% lows.
    /// </remarks>
    public sealed class FrameBudgetSystem : IEcsSystem
    {
        private readonly FrameBudgetSettings _settings = new FrameBudgetSettings();
        private byte _globalBias;
        private int _headroomFrames;
        private double _lastGpuMilliseconds;
        private FrameBudgetSnapshot _snapshot;

        public int Priority => SystemPhase.FrameBudget;
        public FrameBudgetSettings Settings => _settings;
        public FrameBudgetSnapshot Snapshot => _snapshot;

        /// <summary>
        /// Supplies the most recently resolved GPU frame time. Backends are allowed to report zero
        /// until their timestamp ring resolves; zero is treated as "not available", not as free GPU time.
        /// </summary>
        public void ObserveGpuMilliseconds(double milliseconds)
        {
            if (!double.IsFinite(milliseconds) || milliseconds <= 0.0)
            {
                _lastGpuMilliseconds = 0.0;
                return;
            }

            _lastGpuMilliseconds = Math.Clamp(milliseconds, 0.1, 250.0);
        }

        public void Update(IEcsWorld world, float dt)
        {
            if (!_settings.Enabled)
            {
                _globalBias = 0;
                _headroomFrames = 0;
                ApplyBias(world, 0, out int disabledVisible, out long disabledLocal, out long disabledEffective);
                _snapshot = new FrameBudgetSnapshot
                {
                    Enabled = false,
                    GlobalLodBias = 0,
                    VisibleLodEntities = disabledVisible,
                    LocalTriangleCost = disabledLocal,
                    EffectiveTriangleCost = disabledEffective,
                    LastGpuMilliseconds = _lastGpuMilliseconds,
                    HeadroomFrames = 0,
                };
                return;
            }

            Measure(world, _globalBias, out int visibleCount, out long localCost, out long currentEffectiveCost);

            long triangleBudget = Math.Max(0L, _settings.TriangleBudget);
            double targetGpuMs = _settings.TargetGpuMilliseconds;
            double overBudgetMultiplier = Math.Clamp(
                double.IsFinite(_settings.OverBudgetMultiplier) ? _settings.OverBudgetMultiplier : 1.05,
                1.0,
                2.0);

            bool triangleOverBudget = triangleBudget > 0 && currentEffectiveCost > triangleBudget;
            bool gpuOverBudget = _lastGpuMilliseconds > 0.0 && targetGpuMs > 0.0 &&
                                 _lastGpuMilliseconds > targetGpuMs * overBudgetMultiplier;

            // There is nothing for LOD bias to improve if no visible LOD entity took part in the set.
            if (visibleCount > 0 && (triangleOverBudget || gpuOverBudget))
            {
                _globalBias = (byte)Math.Min(3, _globalBias + 1);
                _headroomFrames = 0;
            }
            else if (_globalBias > 0 && visibleCount > 0 && HasDeepHeadroom(currentEffectiveCost, triangleBudget, targetGpuMs))
            {
                int requiredFrames = Math.Max(1, _settings.RelaxFrames);
                _headroomFrames++;
                if (_headroomFrames >= requiredFrames)
                {
                    _globalBias--;
                    _headroomFrames = 0;
                }
            }
            else
            {
                _headroomFrames = 0;
            }

            ApplyBias(world, _globalBias, out visibleCount, out localCost, out long effectiveCost);
            _snapshot = new FrameBudgetSnapshot
            {
                Enabled = true,
                GlobalLodBias = _globalBias,
                VisibleLodEntities = visibleCount,
                LocalTriangleCost = localCost,
                EffectiveTriangleCost = effectiveCost,
                LastGpuMilliseconds = _lastGpuMilliseconds,
                TriangleOverBudget = triangleOverBudget,
                GpuOverBudget = gpuOverBudget,
                HeadroomFrames = _headroomFrames,
            };
        }

        private bool HasDeepHeadroom(long effectiveCost, long triangleBudget, double targetGpuMs)
        {
            double fraction = Math.Clamp(
                float.IsFinite(_settings.RelaxBudgetFraction) ? _settings.RelaxBudgetFraction : 0.5f,
                0.1,
                0.9);

            bool trianglesComfortable = triangleBudget <= 0 || effectiveCost < triangleBudget * fraction;
            bool gpuComfortable = _lastGpuMilliseconds <= 0.0 || targetGpuMs <= 0.0 ||
                                  _lastGpuMilliseconds < targetGpuMs * fraction;
            return trianglesComfortable && gpuComfortable;
        }

        private static void Measure(
            IEcsWorld world,
            byte bias,
            out int visibleCount,
            out long localCost,
            out long effectiveCost)
        {
            int count = 0;
            long local = 0;
            long effective = 0;

            world.Query<LodComponent>((Entity entity, ref LodComponent lod) =>
            {
                if (!Participates(world, entity, in lod))
                    return;

                count++;
                local = SaturatingAdd(local, lod.CostForLevel(lod.Selected));
                int effectiveLevel = Math.Min(3, lod.Selected + bias);
                effective = SaturatingAdd(effective, lod.CostForLevel(effectiveLevel));
            });

            visibleCount = count;
            localCost = local;
            effectiveCost = effective;
        }

        private static void ApplyBias(
            IEcsWorld world,
            byte bias,
            out int visibleCount,
            out long localCost,
            out long effectiveCost)
        {
            int count = 0;
            long local = 0;
            long effective = 0;

            world.Query<LodComponent>((Entity entity, ref LodComponent lod) =>
            {
                // Bias is runtime-owned state. Clear/set it for every LOD component so disabling the
                // controller or moving an entity out of view cannot leave stale emergency coarsening.
                lod.Bias = bias;

                if (!Participates(world, entity, in lod))
                    return;

                count++;
                local = SaturatingAdd(local, lod.CostForLevel(lod.Selected));
                effective = SaturatingAdd(effective, lod.EffectiveCost);
            });

            visibleCount = count;
            localCost = local;
            effectiveCost = effective;
        }

        private static bool Participates(IEcsWorld world, Entity entity, in LodComponent lod)
        {
            if (lod.SelectionValid == 0)
                return false;

            if (world.Has<EntityLifecycleComponent>(entity) &&
                !world.GetRef<EntityLifecycleComponent>(entity).Enabled)
                return false;

            if (!world.Has<VisibilityComponent>(entity))
                return true;

            VisibilityComponent visibility = world.GetRef<VisibilityComponent>(entity);
            return visibility.Enabled && visibility.LastResult == VisibilityResult.Visible;
        }

        private static long SaturatingAdd(long current, int value)
        {
            long nonNegative = Math.Max(0, value);
            return current > long.MaxValue - nonNegative ? long.MaxValue : current + nonNegative;
        }
    }
}
