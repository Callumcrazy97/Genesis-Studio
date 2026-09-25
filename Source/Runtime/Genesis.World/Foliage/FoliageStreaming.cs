using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Genesis.Shared.Rendering;

namespace Genesis.World.Foliage;

public readonly record struct FoliageRenderKey(FoliageSpecies Species, bool NearLod);

public sealed class FoliageRenderBatch
{
    internal FoliageRenderBatch(FoliageRenderKey key) => Key = key;

    public FoliageRenderKey Key { get; }
    public List<FoliageInstance> Instances { get; } = new();
    public int TrianglesPerInstance => FoliageGeometry.TriangleCount(Key.Species, Key.NearLod);
}

public readonly record struct FoliagePerformanceSnapshot(
    int AuthoredInstances,
    int TotalCells,
    int VisibleCells,
    int VisibleInstances,
    int SubmittedInstances,
    int DistanceCulledInstances,
    int FrustumCulledInstances,
    int BudgetCulledInstances,
    int Batches,
    long SubmittedTriangles,
    long ResidentBytes,
    long UploadBytes,
    int EffectiveInstanceBudget,
    float AdaptiveScale,
    double PlanningMilliseconds,
    double ObservedGpuMilliseconds)
{
    public bool TriangleBudgetHit { get; init; }
    public bool InstanceBudgetHit { get; init; }
    public bool ResidentMemoryBudgetHit { get; init; }
    public bool UploadMemoryBudgetHit { get; init; }
    public bool GpuBudgetHit { get; init; }
}

public sealed class FoliageFramePlan
{
    internal readonly List<FoliageRenderBatch> MutableBatches = new(14);

    public IReadOnlyList<FoliageRenderBatch> Batches => MutableBatches;
    public FoliagePerformanceSnapshot Snapshot { get; internal set; }
}

/// <summary>
/// Spatially indexes an authored foliage field once, then produces a deterministic, nearest-first
/// render plan for each camera. Whole cells are rejected before their instances are visited and the
/// surviving instances are grouped into at most one GPU instance batch per species/LOD pair.
/// </summary>
public sealed class FoliageStreamingPlanner
{
    public const int GpuInstanceBytes = 96;

    private sealed class Cell
    {
        public int X;
        public int Z;
        public Vector3 Minimum = new(float.MaxValue);
        public Vector3 Maximum = new(float.MinValue);
        public readonly List<int> InstanceIndices = new();
        public float DistanceSquared;
    }

    private readonly FoliageField _field;
    private readonly float _cellSize;
    private readonly List<Cell> _cells = new();
    private readonly List<Cell> _visibleCells = new();
    private readonly Dictionary<FoliageRenderKey, FoliageRenderBatch> _batches = new();
    private readonly FoliageFramePlan _plan = new();

    public FoliageStreamingPlanner(FoliageField field, float cellSize)
    {
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _cellSize = Math.Clamp(cellSize, 4f, 512f);
        BuildCells();
    }

    public int CellCount => _cells.Count;
    public long ResidentBytes => (long)_field.Instances.Count
        * FoliageScatterSettings.EstimatedResidentBytesPerInstance
        + (long)_cells.Count * 96L;

    public FoliageFramePlan Build(
        Vector3 camera,
        in Matrix4x4 viewProjection,
        in Matrix4x4 placement,
        FoliageScatterSettings settings,
        double observedGpuMilliseconds = 0d)
    {
        settings ??= new FoliageScatterSettings();
        settings.Normalize();
        long started = Stopwatch.GetTimestamp();
        ResetPlan();

        CameraFrustum frustum = new(viewProjection);
        int distanceCulled = 0;
        int frustumCulled = 0;
        foreach (Cell cell in _cells)
        {
            TransformBounds(cell.Minimum, cell.Maximum, placement, out Vector3 minimum, out Vector3 maximum);
            float distanceSquared = DistanceSquaredToBounds(camera, minimum, maximum);
            if (distanceSquared > settings.FarDistance * settings.FarDistance)
            {
                distanceCulled += cell.InstanceIndices.Count;
                continue;
            }
            if (!frustum.IntersectsAabb(minimum, maximum))
            {
                frustumCulled += cell.InstanceIndices.Count;
                continue;
            }
            cell.DistanceSquared = distanceSquared;
            _visibleCells.Add(cell);
        }
        _visibleCells.Sort(static (left, right) =>
        {
            int distance = left.DistanceSquared.CompareTo(right.DistanceSquared);
            if (distance != 0) return distance;
            int x = left.X.CompareTo(right.X);
            return x != 0 ? x : left.Z.CompareTo(right.Z);
        });

        bool gpuOver = observedGpuMilliseconds > settings.TargetGpuMilliseconds
            && settings.TargetGpuMilliseconds > 0f;
        float adaptiveScale = gpuOver
            ? Math.Clamp((float)(settings.TargetGpuMilliseconds / observedGpuMilliseconds), 0.25f, 1f)
            : 1f;
        int uploadLimited = (int)Math.Floor(
            settings.GpuUploadBudgetMegabytes * 1024f * 1024f / GpuInstanceBytes);
        int effectiveInstanceBudget = Math.Max(1, Math.Min(
            settings.VisibleInstanceBudget,
            Math.Min(uploadLimited, (int)MathF.Floor(settings.VisibleInstanceBudget * adaptiveScale))));

        int visibleInstances = 0;
        int submitted = 0;
        int budgetCulled = 0;
        long triangles = 0;
        bool triangleBudgetHit = false;
        bool instanceBudgetHit = false;
        foreach (Cell cell in _visibleCells)
        {
            foreach (int index in cell.InstanceIndices)
            {
                FoliageInstance instance = _field.Instances[index];
                Vector3 worldPosition = Vector3.Transform(instance.Position, placement);
                float distance = Vector3.Distance(camera, worldPosition);
                if (distance > settings.FarDistance)
                {
                    distanceCulled++;
                    continue;
                }

                visibleInstances++;
                bool nearLod = distance <= settings.NearDistance;
                int instanceTriangles = FoliageGeometry.TriangleCount(instance.Species, nearLod);
                if (submitted >= effectiveInstanceBudget)
                {
                    instanceBudgetHit = true;
                    budgetCulled++;
                    continue;
                }
                if (triangles + instanceTriangles > settings.TriangleBudget)
                {
                    triangleBudgetHit = true;
                    budgetCulled++;
                    continue;
                }

                FoliageRenderKey key = new(instance.Species, nearLod);
                if (!_batches.TryGetValue(key, out FoliageRenderBatch batch))
                {
                    batch = new FoliageRenderBatch(key);
                    _batches.Add(key, batch);
                    _plan.MutableBatches.Add(batch);
                }
                batch.Instances.Add(instance);
                submitted++;
                triangles += instanceTriangles;
            }
        }
        _plan.MutableBatches.Sort(static (left, right) =>
        {
            int species = left.Key.Species.CompareTo(right.Key.Species);
            return species != 0 ? species : right.Key.NearLod.CompareTo(left.Key.NearLod);
        });

        long residentBudget = (long)(settings.ResidentMemoryBudgetMegabytes * 1024f * 1024f);
        long uploadBudget = (long)(settings.GpuUploadBudgetMegabytes * 1024f * 1024f);
        long uploadBytes = (long)submitted * GpuInstanceBytes;
        double elapsed = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        _plan.Snapshot = new FoliagePerformanceSnapshot(
            _field.Instances.Count,
            _cells.Count,
            _visibleCells.Count,
            visibleInstances,
            submitted,
            distanceCulled,
            frustumCulled,
            budgetCulled,
            _plan.MutableBatches.Count,
            triangles,
            ResidentBytes,
            uploadBytes,
            effectiveInstanceBudget,
            adaptiveScale,
            elapsed,
            observedGpuMilliseconds)
        {
            TriangleBudgetHit = triangleBudgetHit,
            InstanceBudgetHit = instanceBudgetHit,
            ResidentMemoryBudgetHit = ResidentBytes > residentBudget,
            UploadMemoryBudgetHit = uploadBytes >= uploadBudget,
            GpuBudgetHit = gpuOver,
        };
        return _plan;
    }

    private void BuildCells()
    {
        var lookup = new Dictionary<(int X, int Z), Cell>();
        for (int index = 0; index < _field.Instances.Count; index++)
        {
            FoliageInstance instance = _field.Instances[index];
            int x = (int)MathF.Floor((instance.Position.X - _field.Minimum.X) / _cellSize);
            int z = (int)MathF.Floor((instance.Position.Z - _field.Minimum.Y) / _cellSize);
            if (!lookup.TryGetValue((x, z), out Cell cell))
            {
                cell = new Cell { X = x, Z = z };
                lookup.Add((x, z), cell);
                _cells.Add(cell);
            }
            float radius = MathF.Max(0.5f, instance.Scale * 1.5f);
            Vector3 extent = new(radius, MathF.Max(1f, instance.Scale * 3f), radius);
            cell.Minimum = Vector3.Min(cell.Minimum, instance.Position - extent);
            cell.Maximum = Vector3.Max(cell.Maximum, instance.Position + extent);
            cell.InstanceIndices.Add(index);
        }
        _cells.Sort(static (left, right) =>
        {
            int x = left.X.CompareTo(right.X);
            return x != 0 ? x : left.Z.CompareTo(right.Z);
        });
    }

    private void ResetPlan()
    {
        _visibleCells.Clear();
        foreach (FoliageRenderBatch batch in _plan.MutableBatches) batch.Instances.Clear();
        _plan.MutableBatches.Clear();
        _batches.Clear();
    }

    private static float DistanceSquaredToBounds(Vector3 point, Vector3 minimum, Vector3 maximum)
    {
        float x = Math.Max(minimum.X - point.X, Math.Max(0f, point.X - maximum.X));
        float y = Math.Max(minimum.Y - point.Y, Math.Max(0f, point.Y - maximum.Y));
        float z = Math.Max(minimum.Z - point.Z, Math.Max(0f, point.Z - maximum.Z));
        return x * x + y * y + z * z;
    }

    private static void TransformBounds(
        Vector3 localMinimum,
        Vector3 localMaximum,
        in Matrix4x4 placement,
        out Vector3 minimum,
        out Vector3 maximum)
    {
        minimum = new Vector3(float.MaxValue);
        maximum = new Vector3(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 local = new(
                (corner & 1) == 0 ? localMinimum.X : localMaximum.X,
                (corner & 2) == 0 ? localMinimum.Y : localMaximum.Y,
                (corner & 4) == 0 ? localMinimum.Z : localMaximum.Z);
            Vector3 world = Vector3.Transform(local, placement);
            minimum = Vector3.Min(minimum, world);
            maximum = Vector3.Max(maximum, world);
        }
    }
}
