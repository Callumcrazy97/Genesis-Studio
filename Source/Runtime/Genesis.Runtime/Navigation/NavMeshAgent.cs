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
    public bool PlanarXY { get; set; }
    public float Speed { get; set; } = 3.8f;
    public float StoppingDistance { get; set; } = .1f;
    /// <summary>How wide the agent is: routes keep this far from edges (0: the size the mesh was baked for).</summary>
    public float Radius { get; set; }
    public IReadOnlyList<Vector3> Path { get; private set; } = [];
    public int Waypoint { get; private set; }
    public bool HasPath => Path.Count > 0;
    public bool HasArrived { get; private set; }
    public float DistanceRemaining { get; private set; } = -1;
    // The route's links (NavMeshRoute.LinkAt), when it crosses any.
    private NavMeshRoute? _route;
    /// <summary>The link the agent is crossing (going straight to its far end), 0 while it walks.</summary>
    public int CurrentLink { get; private set; }
    /// <summary>How far across <see cref="CurrentLink"/> the agent is, 0 to 1; 0 while it walks.</summary>
    public float LinkProgress { get; private set; }
    public void SetPath(IReadOnlyList<Vector3> path)
    {
        Path = new List<Vector3>(path); Waypoint = 0; HasArrived = false;
        _route = path is NavMeshRoute { HasLinks: true } route ? route : null;
        CurrentLink = 0; LinkProgress = 0;
        DistanceRemaining = path.Count == 0 ? -1 : PathLength(path[0]);
    }
    // The link on the way to a waypoint: the stretch from the one before it.
    private int LinkTo(int waypoint) => _route?.LinkAt(waypoint - 1) ?? 0;
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
            if (LinkTo(Waypoint) != 0)
            {
                // A link is crossed straight, whatever is in the way: walking could not cross it.
                position += forward * step; budget -= step;
                continue;
            }
            Vector3 desired = forward + (PlanarXY ? new Vector3(localAvoidance.X, localAvoidance.Z, 0)
                : new Vector3(localAvoidance.X, 0f, localAvoidance.Z));
            if (desired.LengthSquared() < 1e-8f) desired = forward;
            else desired = Vector3.Normalize(desired);
            Vector3 next = position + desired * step;
            if (canMove != null && !canMove(position, next))
            {
                // Try deterministic forward/side probes so a newly-arrived dynamic obstacle does
                // not freeze the agent until somebody performs another full path query.
                Vector3 side = Vector3.Cross(PlanarXY ? Vector3.UnitZ : Vector3.UnitY, forward);
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
        CurrentLink = HasArrived || Waypoint < 1 ? 0 : LinkTo(Waypoint);
        if (CurrentLink == 0) LinkProgress = 0;
        else
        {
            float length = Vector3.Distance(Path[Waypoint - 1], Path[Waypoint]);
            LinkProgress = length < 1e-5f ? 1f : Math.Clamp(1f - Vector3.Distance(position, Path[Waypoint]) / length, 0f, 1f);
        }
        return position;
    }
    private float PathLength(Vector3 position)
    {
        float sum = 0;
        for (int i = Waypoint; i < Path.Count; i++) { sum += Vector3.Distance(position, Path[i]); position = Path[i]; }
        return sum;
    }
}
