#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Runtime.Navigation;

public readonly record struct CrowdAgentSample(
    int EntityId,
    Vector3 Position,
    Vector3 Velocity,
    float Radius,
    float NeighborDistance,
    float AvoidanceStrength,
    int MaxNeighbors,
    bool Recalculate,
    Vector3 PreviousSteering);

/// <summary>
/// Deterministic XZ spatial-hash local avoidance. The solver reuses its buckets and output map,
/// avoiding pairwise all-agent scans and per-agent heap allocations.
/// </summary>
public sealed class CrowdNavigationSolver
{
    private readonly Dictionary<(int X, int Z), List<int>> _buckets = [];
    private readonly Stack<List<int>> _bucketPool = [];
    private readonly Dictionary<int, Vector3> _steering = [];

    public void Solve(IReadOnlyList<CrowdAgentSample> agents)
    {
        RecycleBuckets();
        _steering.Clear();
        if (agents.Count == 0) return;

        float cellSize = 0.5f;
        for (int index = 0; index < agents.Count; index++)
            cellSize = MathF.Max(cellSize, agents[index].NeighborDistance);
        cellSize = Math.Clamp(cellSize, 0.5f, 64f);

        Span<Neighbor> nearest = stackalloc Neighbor[64];
        for (int index = 0; index < agents.Count; index++)
        {
            (int X, int Z) cell = Cell(agents[index].Position, cellSize);
            if (!_buckets.TryGetValue(cell, out List<int>? bucket))
            {
                bucket = _bucketPool.Count > 0 ? _bucketPool.Pop() : [];
                _buckets.Add(cell, bucket);
            }
            bucket.Add(index);
        }

        for (int index = 0; index < agents.Count; index++)
        {
            CrowdAgentSample agent = agents[index];
            if (!agent.Recalculate)
            {
                _steering[agent.EntityId] = agent.PreviousSteering;
                continue;
            }

            int nearestCount = 0;
            int maxNeighbors = Math.Clamp(agent.MaxNeighbors, 1, nearest.Length);
            int radius = Math.Max(1, (int)MathF.Ceiling(agent.NeighborDistance / cellSize));
            (int X, int Z) origin = Cell(agent.Position, cellSize);
            float maximumDistanceSquared = agent.NeighborDistance * agent.NeighborDistance;

            for (int z = -radius; z <= radius; z++)
            for (int x = -radius; x <= radius; x++)
            {
                if (!_buckets.TryGetValue((origin.X + x, origin.Z + z), out List<int>? bucket)) continue;
                foreach (int otherIndex in bucket)
                {
                    CrowdAgentSample other = agents[otherIndex];
                    if (other.EntityId == agent.EntityId) continue;
                    Vector3 predictedOther = other.Position + other.Velocity * 0.2f;
                    Vector3 offset = agent.Position - predictedOther;
                    offset.Y = 0f;
                    float distanceSquared = offset.LengthSquared();
                    if (distanceSquared > maximumDistanceSquared) continue;
                    InsertNearest(nearest, ref nearestCount, maxNeighbors,
                        new Neighbor(other.EntityId, offset, distanceSquared, other.Radius));
                }
            }

            Vector3 separation = Vector3.Zero;
            for (int neighborIndex = 0; neighborIndex < nearestCount; neighborIndex++)
            {
                Neighbor neighbor = nearest[neighborIndex];
                float distance = MathF.Sqrt(neighbor.DistanceSquared);
                Vector3 away;
                if (distance > 1e-4f)
                {
                    away = neighbor.Offset / distance;
                }
                else
                {
                    float angle = ((agent.EntityId * 73856093) ^ (neighbor.EntityId * 19349663))
                        * (MathF.PI * 2f / int.MaxValue);
                    away = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
                    distance = 0f;
                }

                float contactDistance = MathF.Max(0.01f, agent.Radius + neighbor.Radius);
                float rangeWeight = 1f - Math.Clamp(distance / MathF.Max(contactDistance, agent.NeighborDistance), 0f, 1f);
                float overlapWeight = distance < contactDistance
                    ? 1f + (contactDistance - distance) / contactDistance
                    : 0f;
                separation += away * (rangeWeight + overlapWeight);
            }

            if (separation.LengthSquared() > 1e-8f)
                separation = Vector3.Normalize(separation) * Math.Clamp(agent.AvoidanceStrength, 0f, 2f);
            _steering[agent.EntityId] = separation;
        }
    }

    public bool TryGetSteering(int entityId, out Vector3 steering) => _steering.TryGetValue(entityId, out steering);

    private void RecycleBuckets()
    {
        foreach (List<int> bucket in _buckets.Values)
        {
            bucket.Clear();
            _bucketPool.Push(bucket);
        }
        _buckets.Clear();
    }

    private static (int X, int Z) Cell(Vector3 position, float size) =>
        ((int)MathF.Floor(position.X / size), (int)MathF.Floor(position.Z / size));

    private static void InsertNearest(Span<Neighbor> neighbors, ref int count, int capacity, Neighbor value)
    {
        int insertion = count;
        while (insertion > 0 && neighbors[insertion - 1].DistanceSquared > value.DistanceSquared)
            insertion--;
        if (insertion >= capacity) return;
        int last = Math.Min(count, capacity - 1);
        for (int index = last; index > insertion; index--) neighbors[index] = neighbors[index - 1];
        neighbors[insertion] = value;
        count = Math.Min(capacity, count + 1);
    }

    private readonly record struct Neighbor(int EntityId, Vector3 Offset, float DistanceSquared, float Radius);
}
