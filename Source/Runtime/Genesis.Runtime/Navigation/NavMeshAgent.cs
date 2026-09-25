#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.Navigation;
public struct NavMeshAgentComponent : IComponent { public NavMeshAgent Agent; }

/// <summary>
/// Optional local-avoidance and distance-scaled update settings for a navigation agent. These
/// fields describe movement capacity only; projects decide what each entity represents.
/// </summary>
public struct CrowdAgentComponent : IComponent
{
    public bool Enabled;
    public float Radius;
    public float NeighborDistance;
    public float AvoidanceStrength;
    public int MaxNeighbors;
    public float NearUpdateHz;
    public float FarUpdateHz;
    public float FarDistance;
    public float AvoidanceDistance;
    public float UpdateCountdown;
    public Vector3 Steering;
    public Vector3 Velocity;
}

public sealed class NavMeshAgent
{
    public float Speed { get; set; } = 3.8f;
    public float StoppingDistance { get; set; } = .1f;
    public IReadOnlyList<Vector3> Path { get; private set; } = [];
    public int Waypoint { get; private set; }
    public bool HasPath => Path.Count > 0;
    public bool HasArrived { get; private set; }
    public float DistanceRemaining { get; private set; } = -1;
    public void SetPath(IReadOnlyList<Vector3> path)
    {
        Path = new List<Vector3>(path); Waypoint = 0; HasArrived = false;
        DistanceRemaining = path.Count == 0 ? -1 : PathLength(path[0]);
    }
    public Vector3 Update(
        Vector3 position,
        float dt,
        Func<Vector3, Vector3, bool>? canMove = null,
        Vector3 localAvoidance = default)
    {
        if (!HasPath || HasArrived || !float.IsFinite(dt) || dt <= 0) return position;
        float budget = MathF.Max(0, float.IsFinite(Speed) ? Speed : 0) * dt;
        while (Waypoint < Path.Count)
        {
            Vector3 delta = Path[Waypoint] - position;
            float distance = delta.Length();
            float stop = Waypoint == Path.Count - 1 ? MathF.Max(0, StoppingDistance) : .001f;
            if (distance <= stop) { Waypoint++; continue; }
            if (budget <= 0) break;
            float step = MathF.Min(budget, distance);
            Vector3 forward = delta / distance;
            Vector3 desired = forward + new Vector3(localAvoidance.X, 0f, localAvoidance.Z);
            if (desired.LengthSquared() < 1e-8f) desired = forward;
            else desired = Vector3.Normalize(desired);
            Vector3 next = position + desired * step;
            if (canMove != null && !canMove(position, next))
            {
                // Try deterministic forward/side probes so a newly-arrived dynamic obstacle does
                // not freeze the agent until somebody performs another full path query.
                Vector3 side = Vector3.Cross(Vector3.UnitY, forward);
                if (side.LengthSquared() < 1e-6f) break;
                side = Vector3.Normalize(side);
                Vector3 right = position + Vector3.Normalize(forward * .35f + side * .94f) * step;
                Vector3 left = position + Vector3.Normalize(forward * .35f - side * .94f) * step;
                if (canMove(position, right)) next = right;
                else if (canMove(position, left)) next = left;
                else break;
            }
            position = next; budget -= step;
        }
        HasArrived = Waypoint >= Path.Count;
        DistanceRemaining = HasArrived ? 0 : PathLength(position);
        return position;
    }
    private float PathLength(Vector3 position)
    {
        float sum = 0;
        for (int i = Waypoint; i < Path.Count; i++) { sum += Vector3.Distance(position, Path[i]); position = Path[i]; }
        return sum;
    }
}
