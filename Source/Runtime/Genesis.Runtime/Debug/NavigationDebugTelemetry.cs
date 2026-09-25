#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Project;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;

namespace Genesis.Runtime.Debugger;

public sealed class NavigationDebugAgent
{
    public int EntityId { get; internal set; }
    public string Name { get; internal set; } = string.Empty;
    public Vector3 Position { get; internal set; }
    public Vector3 Destination { get; internal set; }
    public Vector3 Velocity { get; internal set; }
    public Vector3 Steering { get; internal set; }
    public float Radius { get; internal set; }
    public float Speed { get; internal set; }
    public float DistanceRemaining { get; internal set; }
    public int WaypointIndex { get; internal set; }
    public int WaypointCount { get; internal set; }
    public string RoutineName { get; internal set; } = "Direct Navigation";
    public string State { get; internal set; } = string.Empty;
    public IReadOnlyList<Vector3> Path { get; internal set; } = [];
}

public sealed class NavigationDebugSnapshot
{
    internal readonly List<NavigationDebugAgent> MutableAgents = [];
    public IReadOnlyList<NavigationDebugAgent> Agents => MutableAgents;
    public NavMeshData? NavMesh { get; internal set; }
    public long Revision { get; internal set; }
}

/// <summary>Opt-in, backend-neutral navigation telemetry consumed by the unified F6 debugger.</summary>
public static class NavigationDebugTelemetry
{
    public const string InitialPanelEnvironmentVariable = "GENESIS_DEBUG_PANEL";
    public const string DebugPathingEnvironmentVariable = "GENESIS_DEBUG_PATHING_ASSET";
    public const string AiNavigationPanelValue = "AI_NAVIGATION";

    private sealed record RoutineInfo(string Name, int WaypointCount, int WaypointIndex);
    private sealed class PreviewAgent
    {
        public int EntityId;
        public int Waypoint = -1;
        public int Direction = 1;
        public float Wait;
        public uint RandomState;
    }
    private sealed class PreviewDriver
    {
        public PathingAsset Asset = new();
        public NavMeshData? NavMesh;
        public NavMeshQuery? Query;
        public readonly List<PreviewAgent> Agents = [];
        public float Elapsed;
    }
    private sealed class SceneDebugState
    {
        public readonly Dictionary<int, RoutineInfo> Routines = [];
        public PreviewDriver? Preview;
        public bool PreviewInitialized;
    }

    private static readonly ConditionalWeakTable<RuntimeScene, NavigationDebugSnapshot> Snapshots = new();
    private static readonly ConditionalWeakTable<RuntimeScene, SceneDebugState> SceneStates = new();
    private static int _aiPaused;
    private static int _aiStepRequested;
    private static int _aiStepActive;
    public static bool Enabled { get; set; }
    public static bool IsAiPaused => Volatile.Read(ref _aiPaused) != 0;

    public static void PauseAi() => Interlocked.Exchange(ref _aiPaused, 1);
    public static void ResumeAi()
    {
        Interlocked.Exchange(ref _aiPaused, 0);
        Interlocked.Exchange(ref _aiStepRequested, 0);
        Interlocked.Exchange(ref _aiStepActive, 0);
    }
    public static void StepAi()
    {
        Interlocked.Exchange(ref _aiPaused, 1);
        Interlocked.Exchange(ref _aiStepRequested, 1);
    }

    internal static void BeginFrame() =>
        Interlocked.Exchange(ref _aiStepActive, Interlocked.Exchange(ref _aiStepRequested, 0));

    internal static bool ShouldAdvanceAi() => !IsAiPaused || Volatile.Read(ref _aiStepActive) != 0;

    public static void BindScene(RuntimeScene? scene)
    {
        if (scene is null) return;
        _ = SceneStates.GetOrCreateValue(scene);
    }

    internal static void SetRoutine(RuntimeScene? scene, int entityId, string? name, int waypointCount, int waypointIndex = 0)
    {
        if (scene is null || entityId < 0) return;
        SceneDebugState state = SceneStates.GetOrCreateValue(scene);
        state.Routines[entityId] = new RoutineInfo(
            string.IsNullOrWhiteSpace(name) ? "Navigation" : name,
            Math.Max(0, waypointCount),
            Math.Max(0, waypointIndex));
    }

    internal static void UpdateRoutineWaypoint(RuntimeScene? scene, int entityId, int waypointIndex)
    {
        if (scene is null || !SceneStates.TryGetValue(scene, out SceneDebugState? state)
            || !state.Routines.TryGetValue(entityId, out RoutineInfo? routine)) return;
        state.Routines[entityId] = routine with { WaypointIndex = Math.Max(0, waypointIndex) };
    }

    internal static void ClearRoutine(RuntimeScene? scene, int entityId)
    {
        if (scene is not null && SceneStates.TryGetValue(scene, out SceneDebugState? state))
            state.Routines.Remove(entityId);
    }

    public static NavigationDebugSnapshot? Get(RuntimeScene? scene) =>
        scene is not null && Snapshots.TryGetValue(scene, out NavigationDebugSnapshot? snapshot) ? snapshot : null;

    internal static void AdvanceDebugPreview(RuntimeScene? scene, float dt)
    {
        if (!Enabled || scene is null || !float.IsFinite(dt) || dt <= 0f) return;
        SceneDebugState state = SceneStates.GetOrCreateValue(scene);
        if (!state.PreviewInitialized)
        {
            state.PreviewInitialized = true;
            state.Preview = BuildDebugPreview(scene, state);
        }
        PreviewDriver? preview = state.Preview;
        if (preview is null) return;
        if (!ShouldAdvanceAi())
        {
            Capture(scene, preview.NavMesh);
            return;
        }

        preview.Elapsed += dt;
        foreach (PreviewAgent previewAgent in preview.Agents)
        {
            Entity entity = scene.World.GetEntity(previewAgent.EntityId);
            if (!scene.World.IsAlive(entity) || !scene.World.Has<TransformComponent>(entity)
                || !scene.World.Has<NavMeshAgentComponent>(entity)) continue;
            ref TransformComponent transform = ref scene.World.GetRef<TransformComponent>(entity);
            ref NavMeshAgentComponent component = ref scene.World.GetRef<NavMeshAgentComponent>(entity);
            NavMeshAgent agent = component.Agent;
            if (agent is null) continue;
            PathingRoute route = preview.Asset.Route;
            agent.Speed = route.SpeedAt(preview.Elapsed);
            agent.StoppingDistance = route.StoppingDistance;
            if (previewAgent.Wait > 0f)
            {
                previewAgent.Wait = MathF.Max(0f, previewAgent.Wait - dt);
                SetCrowdMotion(scene, entity, Vector3.Zero, Vector3.Zero);
                continue;
            }

            Vector3 position = new(transform.X, transform.Y, transform.Z);
            if (!agent.HasPath || agent.HasArrived)
            {
                if (!TryChoosePreviewDestination(scene, preview, previewAgent, entity, position, out Vector3 destination))
                {
                    SetCrowdMotion(scene, entity, Vector3.Zero, Vector3.Zero);
                    continue;
                }
                IReadOnlyList<Vector3> path = preview.Query?.FindPath(position, destination) ?? [destination];
                agent.SetPath(path.Count > 0 ? path : [destination]);
            }

            Vector3 steering = PreviewSteering(scene, preview, previewAgent, position);
            Vector3 next = agent.Update(position, dt,
                preview.Query is null ? null : (from, to) => preview.Query.CanTravel(from, to), steering);
            int cell = preview.NavMesh?.Cell(next) ?? -1;
            if (preview.NavMesh is not null && cell >= 0) next.Y = preview.NavMesh.Heights[cell];
            transform.X = next.X;
            transform.Y = next.Y;
            transform.Z = next.Z;
            SetCrowdMotion(scene, entity, dt > 1e-6f ? (next - position) / dt : Vector3.Zero, steering);
            UpdateRoutineWaypoint(scene, entity.Id, previewAgent.Waypoint);
        }
        Capture(scene, preview.NavMesh);
    }

    private static PreviewDriver? BuildDebugPreview(RuntimeScene scene, SceneDebugState state)
    {
        string path = Environment.GetEnvironmentVariable(DebugPathingEnvironmentVariable) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        PathingAsset asset;
        try { asset = PathingAssetSerializer.Load(path); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }

        NavMeshData? navMesh = null;
        string project = Environment.GetEnvironmentVariable("GENESIS_PROJECT_PATH") ?? string.Empty;
        string room = Environment.GetEnvironmentVariable("GENESIS_START_ROOM") ?? asset.TargetRoom;
        string? roomFile = ProjectRoomResolver.ResolveRoomFile(project, room);
        string navMeshFile = string.IsNullOrWhiteSpace(roomFile) ? string.Empty : roomFile + ".navmesh";
        try { if (File.Exists(navMeshFile)) navMesh = NavMeshData.Load(navMeshFile); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException) { }

        PreviewDriver preview = new()
        {
            Asset = asset,
            NavMesh = navMesh,
            Query = navMesh is null ? null : new NavMeshQuery(navMesh),
        };
        scene.World.Query<TransformComponent>((Entity entity, ref TransformComponent transform) =>
        {
            if (!ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry? draw)
                || !MatchesReference(draw.Prefab, asset.TargetObject)
                || scene.World.Has<NavMeshAgentComponent>(entity)) return;
            scene.World.Set(entity, new NavMeshAgentComponent { Agent = new NavMeshAgent() });
            if (!scene.World.Has<CrowdAgentComponent>(entity))
                scene.World.Set(entity, DefaultCrowd());
            PreviewAgent previewAgent = new() { EntityId = entity.Id, RandomState = unchecked((uint)(entity.Id * 747796405) + 2891336453u) };
            preview.Agents.Add(previewAgent);
            state.Routines[entity.Id] = new RoutineInfo(asset.Name, asset.Route.Waypoints.Count, 0);
        });
        return preview.Agents.Count > 0 ? preview : null;
    }

    private static bool TryChoosePreviewDestination(RuntimeScene scene, PreviewDriver preview, PreviewAgent agent,
        Entity entity, Vector3 position, out Vector3 destination)
    {
        PathingRoute route = preview.Asset.Route;
        destination = position;
        if (route.Mode == PathingRouteMode.WanderRadius)
        {
            float angle = NextRandom(agent) * MathF.Tau;
            float radius = MathF.Sqrt(NextRandom(agent)) * route.WanderRadius;
            Vector3 origin = route.Waypoints.Count > 0 ? route.Waypoints[0].Position : position;
            destination = origin + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            agent.Wait = route.DefaultWaitSeconds;
            return true;
        }
        if (route.Mode == PathingRouteMode.FollowLeader)
        {
            if (!TryFindEntity(scene, route.FollowTarget, entity, out Vector3 target)) return false;
            Vector3 delta = target - position;
            if (delta.LengthSquared() <= route.FollowOffset * route.FollowOffset) return false;
            destination = target - Vector3.Normalize(delta) * route.FollowOffset;
            return true;
        }
        if (route.Waypoints.Count == 0) return false;
        if (agent.Waypoint < 0) agent.Waypoint = 0;
        else if (!AdvancePreviewWaypoint(agent, route)) return false;
        destination = route.Waypoints[agent.Waypoint].Position;
        agent.Wait = MathF.Max(route.DefaultWaitSeconds, route.Waypoints[agent.Waypoint].WaitSeconds);
        return true;
    }

    private static bool AdvancePreviewWaypoint(PreviewAgent agent, PathingRoute route)
    {
        int next = agent.Waypoint + agent.Direction;
        if (next >= 0 && next < route.Waypoints.Count) { agent.Waypoint = next; return true; }
        return route.LoopMode switch
        {
            PathingLoopMode.Loop => SetWaypoint(agent, agent.Direction > 0 ? 0 : route.Waypoints.Count - 1),
            PathingLoopMode.PingPong when route.Waypoints.Count > 1 => ReverseWaypoint(agent, route.Waypoints.Count),
            _ => false,
        };
    }

    private static bool SetWaypoint(PreviewAgent agent, int value) { agent.Waypoint = value; return true; }
    private static bool ReverseWaypoint(PreviewAgent agent, int count)
    {
        agent.Direction *= -1;
        agent.Waypoint = Math.Clamp(agent.Waypoint + agent.Direction, 0, count - 1);
        return true;
    }

    private static Vector3 PreviewSteering(RuntimeScene scene, PreviewDriver preview, PreviewAgent current, Vector3 position)
    {
        Vector3 steering = Vector3.Zero;
        foreach (PreviewAgent other in preview.Agents)
        {
            if (other.EntityId == current.EntityId) continue;
            Entity entity = scene.World.GetEntity(other.EntityId);
            if (!scene.World.IsAlive(entity) || !scene.World.Has<TransformComponent>(entity)) continue;
            ref TransformComponent transform = ref scene.World.GetRef<TransformComponent>(entity);
            Vector3 delta = position - new Vector3(transform.X, transform.Y, transform.Z);
            delta.Y = 0f;
            float length = delta.Length();
            if (length > .001f && length < 1.2f) steering += delta / length * (1f - length / 1.2f);
        }
        return steering.LengthSquared() > 1f ? Vector3.Normalize(steering) : steering;
    }

    private static void SetCrowdMotion(RuntimeScene scene, Entity entity, Vector3 velocity, Vector3 steering)
    {
        if (!scene.World.Has<CrowdAgentComponent>(entity)) return;
        ref CrowdAgentComponent crowd = ref scene.World.GetRef<CrowdAgentComponent>(entity);
        crowd.Velocity = velocity;
        crowd.Steering = steering;
    }

    private static CrowdAgentComponent DefaultCrowd() => new()
    {
        Enabled = true, Radius = .35f, NeighborDistance = 3f, AvoidanceStrength = .75f,
        MaxNeighbors = 12, NearUpdateHz = 30f, FarUpdateHz = 6f, FarDistance = 35f, AvoidanceDistance = 250f,
    };

    private static bool TryFindEntity(RuntimeScene scene, string name, Entity self, out Vector3 position)
    {
        bool found = false;
        Vector3 result = default;
        scene.World.Query<TransformComponent>((Entity entity, ref TransformComponent transform) =>
        {
            if (found || entity == self || !ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry? draw)) return;
            if (!string.Equals(draw.InstanceName, name, StringComparison.OrdinalIgnoreCase)
                && !MatchesReference(draw.Prefab, name)) return;
            result = new Vector3(transform.X, transform.Y, transform.Z);
            found = true;
        });
        position = result;
        return found;
    }

    private static bool MatchesReference(string? actual, string? expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)) return false;
        string a = DisplayName(actual);
        string b = DisplayName(expected);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || string.Equals(actual.Replace('\\', '/'), expected.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static float NextRandom(PreviewAgent agent)
    {
        uint value = agent.RandomState == 0 ? 0x9E3779B9u : agent.RandomState;
        value ^= value << 13; value ^= value >> 17; value ^= value << 5;
        agent.RandomState = value;
        return (value & 0x00FFFFFFu) / 16777216f;
    }

    internal static void Capture(RuntimeScene scene, NavMeshData? navMesh)
    {
        if (!Enabled) return;
        NavigationDebugSnapshot snapshot = Snapshots.GetOrCreateValue(scene);
        SceneDebugState debugState = SceneStates.GetOrCreateValue(scene);
        snapshot.MutableAgents.Clear();
        snapshot.NavMesh = navMesh;
        scene.World.Query<TransformComponent, NavMeshAgentComponent>((Entity entity, ref TransformComponent transform,
            ref NavMeshAgentComponent component) =>
        {
            if (component.Agent is null) return;
            CrowdAgentComponent crowd = scene.World.Has<CrowdAgentComponent>(entity)
                ? scene.World.GetRef<CrowdAgentComponent>(entity) : default;
            string name = $"Entity {entity.Id}";
            if (ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry? assets))
                name = !string.IsNullOrWhiteSpace(assets.InstanceName) ? assets.InstanceName
                    : !string.IsNullOrWhiteSpace(assets.Prefab) ? DisplayName(assets.Prefab) : name;
            IReadOnlyList<Vector3> path = component.Agent.Path;
            RoutineInfo routine = debugState.Routines.TryGetValue(entity.Id, out RoutineInfo? known)
                ? known
                : new RoutineInfo("Direct Navigation", path.Count, component.Agent.Waypoint);
            snapshot.MutableAgents.Add(new NavigationDebugAgent
            {
                EntityId = entity.Id,
                Name = name,
                Position = new Vector3(transform.X, transform.Y, transform.Z),
                Destination = path.Count > 0 ? path[^1] : new Vector3(transform.X, transform.Y, transform.Z),
                Velocity = crowd.Velocity,
                Steering = crowd.Steering,
                Radius = crowd.Radius > 0f ? crowd.Radius : .35f,
                Speed = component.Agent.Speed,
                DistanceRemaining = component.Agent.DistanceRemaining,
                WaypointIndex = routine.WaypointIndex,
                WaypointCount = routine.WaypointCount,
                RoutineName = routine.Name,
                State = component.Agent.HasArrived ? "Arrived" : component.Agent.HasPath ? "Moving" : "Idle",
                Path = path.ToArray(),
            });
        });
        snapshot.Revision++;
    }

    private static string DisplayName(string path)
    {
        return ResourceNames.FormatName(path);
    }
}
