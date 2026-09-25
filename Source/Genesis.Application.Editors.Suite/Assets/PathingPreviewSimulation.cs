using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Navigation;

namespace Genesis.Application.Editors.Suite.Assets;

internal sealed class PathingPreviewAgent
{
    public int Id;
    public string Name = string.Empty;
    public Vector3 Position;
    public Vector3 Velocity;
    public Vector3 Steering;
    public Vector3 Destination;
    public int Waypoint;
    public int Direction = 1;
    public float Wait;
    public float DistanceRemaining;
    public List<Vector3> SearchPath = [];
    public int SearchPoint;
    public Vector3 RequestedDestination;
    public string State => Wait > 0f ? "Waiting" : Velocity.LengthSquared() > 1e-5f ? "Moving" : "Idle";
}

/// <summary>Non-destructive 60 Hz editor simulation using the same crowd solver as runtime.</summary>
internal sealed class PathingPreviewSimulation
{
    private readonly CrowdNavigationSolver _crowd = new();
    private readonly List<CrowdAgentSample> _samples = [];
    private uint _wanderState = 0x9E3779B9u;
    private NavMeshData? _queryMesh;
    private NavMeshQuery? _query;
    private float _elapsed;

    public List<PathingPreviewAgent> Agents { get; } = [];
    public bool CollisionWarning { get; private set; }

    public void Reset(PathingAsset asset, NavMeshData? mesh)
    {
        Agents.Clear();
        _wanderState = 0x9E3779B9u;
        _elapsed = 0f;
        if (!ReferenceEquals(_queryMesh, mesh))
        {
            _queryMesh = mesh;
            _query = mesh is null ? null : new NavMeshQuery(mesh);
        }
        PathingRoute route = asset.Route;
        Vector3 origin = route.Waypoints.Count > 0 ? route.Waypoints[0].Position : Vector3.Zero;
        int count = Math.Clamp(asset.PreviewAgentCount, 1, 128);
        for (int index = 0; index < count; index++)
        {
            float ring = index == 0 ? 0f : .42f + .15f * (index / 8);
            float angle = index * 2.399963f;
            Vector3 position = origin + new Vector3(MathF.Cos(angle) * ring, 0f, MathF.Sin(angle) * ring);
            position.Y = Height(mesh, position, origin.Y);
            Agents.Add(new PathingPreviewAgent
            {
                Id = index + 1,
                Name = string.IsNullOrWhiteSpace(asset.TargetObject) ? $"Agent {index + 1}" : $"{DisplayName(asset.TargetObject)} {index + 1}",
                Position = position,
                Destination = position,
                Waypoint = route.Waypoints.Count > 1 ? index % route.Waypoints.Count : 0,
                Direction = 1,
            });
        }
        CollisionWarning = route.Waypoints.Any(point => !Walkable(mesh, point.Position));
    }

    public void Step(PathingAsset asset, NavMeshData? mesh, float dt)
    {
        if (!float.IsFinite(dt) || dt <= 0f || Agents.Count == 0) return;
        _elapsed += dt;
        PathingRoute route = asset.Route;
        _samples.Clear();
        foreach (PathingPreviewAgent agent in Agents)
            _samples.Add(new CrowdAgentSample(agent.Id, agent.Position, agent.Velocity, .35f, 3f, .85f, 12, true, agent.Steering));
        _crowd.Solve(_samples);

        for (int index = 0; index < Agents.Count; index++)
        {
            PathingPreviewAgent agent = Agents[index];
            if (_crowd.TryGetSteering(agent.Id, out Vector3 steering)) agent.Steering = steering;
            if (agent.Wait > 0f)
            {
                agent.Wait = MathF.Max(0f, agent.Wait - dt);
                agent.Velocity = Vector3.Zero;
                continue;
            }

            Vector3 requestedDestination = Destination(asset, agent, index);
            agent.Destination = requestedDestination;
            bool search = mesh is not null && route.Mode != PathingRouteMode.WaypointPatrol;
            if (search && (agent.SearchPath.Count == 0
                || Vector3.DistanceSquared(agent.RequestedDestination, requestedDestination) > .04f
                || agent.SearchPoint >= agent.SearchPath.Count))
            {
                agent.SearchPath = _query?.FindPath(agent.Position, requestedDestination).ToList() ?? [];
                agent.SearchPoint = 0;
                agent.RequestedDestination = requestedDestination;
            }
            Vector3 destination = search && agent.SearchPoint < agent.SearchPath.Count
                ? agent.SearchPath[agent.SearchPoint]
                : requestedDestination;
            Vector3 delta = destination - agent.Position;
            delta.Y = 0f;
            float distance = delta.Length();
            agent.DistanceRemaining = distance
                + SearchRemaining(agent)
                + (route.Mode is PathingRouteMode.WaypointPatrol or PathingRouteMode.NavMeshSearch
                    ? Remaining(route, agent.Waypoint, agent.Direction) : 0f);
            if (distance <= MathF.Max(.03f, route.StoppingDistance))
            {
                if (search && agent.SearchPoint + 1 < agent.SearchPath.Count)
                {
                    agent.SearchPoint++;
                    agent.Velocity = Vector3.Zero;
                    continue;
                }
                float arrivedWait = route.Waypoints.Count > 0 && agent.Waypoint >= 0 && agent.Waypoint < route.Waypoints.Count
                    ? MathF.Max(route.DefaultWaitSeconds, route.Waypoints[agent.Waypoint].WaitSeconds)
                    : route.DefaultWaitSeconds;
                Advance(route, agent);
                agent.Wait = arrivedWait;
                agent.SearchPath.Clear();
                agent.SearchPoint = 0;
                agent.Velocity = Vector3.Zero;
                continue;
            }

            Vector3 desired = delta / MathF.Max(distance, 1e-5f) + agent.Steering;
            if (desired.LengthSquared() > 1e-6f) desired = Vector3.Normalize(desired);
            Vector3 previous = agent.Position;
            float travel = MathF.Min(distance, MathF.Max(0f, route.SpeedAt(_elapsed)) * dt);
            Vector3 next = previous + desired * travel;
            int cell = mesh?.Cell(next) ?? -1;
            if (mesh is not null && (cell < 0 || !mesh.Walkable[cell]))
            {
                CollisionWarning = true;
                next = previous;
            }
            else next.Y = Height(mesh, next, destination.Y);
            agent.Position = next;
            agent.Velocity = dt > 1e-6f ? (next - previous) / dt : Vector3.Zero;
        }
    }

    private Vector3 Destination(PathingAsset asset, PathingPreviewAgent agent, int agentIndex)
    {
        PathingRoute route = asset.Route;
        if (route.Mode == PathingRouteMode.FollowLeader && agentIndex > 0)
        {
            PathingPreviewAgent leader = Agents[agentIndex - 1];
            Vector3 behind = leader.Velocity.LengthSquared() > 1e-6f ? -Vector3.Normalize(leader.Velocity) : -Vector3.UnitZ;
            return leader.Position + behind * MathF.Max(.1f, route.FollowOffset);
        }
        if (route.Mode == PathingRouteMode.WanderRadius)
        {
            if (Vector3.DistanceSquared(agent.Position, agent.Destination) <= MathF.Max(.1f, route.StoppingDistance) * MathF.Max(.1f, route.StoppingDistance))
            {
                float angle = Random01() * MathF.Tau;
                float radius = MathF.Sqrt(Random01()) * route.WanderRadius;
                Vector3 origin = route.Waypoints.Count > 0 ? route.Waypoints[0].Position : Vector3.Zero;
                agent.Destination = origin + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            }
            return agent.Destination;
        }
        if (route.Waypoints.Count == 0) return agent.Position;
        agent.Waypoint = Math.Clamp(agent.Waypoint, 0, route.Waypoints.Count - 1);
        return route.Waypoints[agent.Waypoint].Position;
    }

    private static void Advance(PathingRoute route, PathingPreviewAgent agent)
    {
        if (route.Mode == PathingRouteMode.WanderRadius) { agent.Destination = agent.Position; return; }
        if (route.Waypoints.Count <= 1) return;
        int next = agent.Waypoint + agent.Direction;
        if (next >= 0 && next < route.Waypoints.Count) { agent.Waypoint = next; return; }
        switch (route.LoopMode)
        {
            case PathingLoopMode.Loop: agent.Waypoint = agent.Direction > 0 ? 0 : route.Waypoints.Count - 1; break;
            case PathingLoopMode.PingPong: agent.Direction *= -1; agent.Waypoint = Math.Clamp(agent.Waypoint + agent.Direction, 0, route.Waypoints.Count - 1); break;
            case PathingLoopMode.Once: agent.Waypoint = agent.Direction > 0 ? route.Waypoints.Count - 1 : 0; break;
        }
    }

    private static float Remaining(PathingRoute route, int waypoint, int direction)
    {
        if (route.Waypoints.Count < 2) return 0f;
        float total = 0f;
        int index = Math.Clamp(waypoint, 0, route.Waypoints.Count - 1);
        while (true)
        {
            int next = index + direction;
            if (next < 0 || next >= route.Waypoints.Count) break;
            total += Vector3.Distance(route.Waypoints[index].Position, route.Waypoints[next].Position);
            index = next;
        }
        return total;
    }

    private static float SearchRemaining(PathingPreviewAgent agent)
    {
        float total = 0f;
        for (int index = Math.Max(0, agent.SearchPoint); index + 1 < agent.SearchPath.Count; index++)
            total += Vector3.Distance(agent.SearchPath[index], agent.SearchPath[index + 1]);
        return total;
    }

    private static bool Walkable(NavMeshData? mesh, Vector3 point)
    {
        if (mesh is null) return true;
        int cell = mesh.Cell(point);
        return cell >= 0 && mesh.Walkable[cell];
    }

    private static float Height(NavMeshData? mesh, Vector3 point, float fallback)
    {
        int cell = mesh?.Cell(point) ?? -1;
        return mesh is not null && cell >= 0 ? mesh.Heights[cell] : fallback;
    }

    private float Random01()
    {
        _wanderState ^= _wanderState << 13;
        _wanderState ^= _wanderState >> 17;
        _wanderState ^= _wanderState << 5;
        return (_wanderState & 0x00FFFFFFu) / 16777216f;
    }

    private static string DisplayName(string path)
    {
        return ResourceDisplayName.Format(path);
    }
}
