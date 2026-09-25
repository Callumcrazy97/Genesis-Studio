#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Runtime.Debugger;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    private enum PathingRoutineKind { Route, Wander }

    private sealed class PathingRoutineState
    {
        public PathingRoutineKind Kind;
        public PathingAsset? Asset;
        public PathingLoopMode LoopMode;
        public Vector3 Origin;
        public float Radius;
        public float WaitMin;
        public float WaitMax;
        public float WaitRemaining;
        public int Waypoint = -1;
        public int Direction = 1;
        public bool SegmentActive;
        public uint RandomState;
        public float RepathCountdown;
        public float Elapsed;
    }

    [PgslCommand("PathFollow", "PathFollow(routeAsset, loopMode)",
        "Follow a reusable Pathing resource; loopMode is Loop, PingPong, Once, or an empty string to use the asset", "Navigation")]
    public static void PathFollow(string routeAsset, string loopMode)
    {
        NavigationSession? session = Navigation();
        PgslContext? context = GetContext();
        Genesis.Runtime.ECS.World? world = ActiveGameContext?.World;
        NavMeshAgent? agent = Agent();
        if (session is null || context is null || world is null || agent is null) return;
        string path = ResolvePathingAsset(routeAsset);
        if (string.IsNullOrWhiteSpace(path))
        {
            ActiveGameContext?.Log($"Pathing resource was not found: {routeAsset}");
            return;
        }
        PathingAsset asset;
        try { asset = PathingAssetSerializer.Load(path); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            ActiveGameContext?.Log($"Pathing resource could not be loaded: {exception.Message}");
            return;
        }
        Entity entity = world.GetEntity(context.InstanceId);
        if (!world.IsAlive(entity)) return;
        PathingLoopMode resolvedLoop = Enum.TryParse(loopMode, true, out PathingLoopMode parsed)
            ? parsed : asset.Route.LoopMode;
        agent.Speed = asset.Route.SpeedAt(0f);
        agent.StoppingDistance = asset.Route.StoppingDistance;
        agent.SetPath([]);
        session.Routines[entity.Id] = new PathingRoutineState
        {
            Kind = PathingRoutineKind.Route,
            Asset = asset,
            LoopMode = resolvedLoop,
            Origin = new Vector3((float)context.X, (float)context.Y, (float)context.Z),
            RandomState = Seed(entity.Id),
        };
        NavigationDebugTelemetry.SetRoutine(ActiveGameContext?.Scene, entity.Id, asset.Name, asset.Route.Waypoints.Count);
        if (!string.IsNullOrWhiteSpace(asset.Route.AnimationState))
            AnimationStatePlay(asset.Route.AnimationState, true, .12);
    }

    [PgslCommand("PathWander", "PathWander(radius, waitMin, waitMax)",
        "Wander around the current position using the active NavMesh and local avoidance", "Navigation")]
    public static void PathWander(double radius, double waitMin, double waitMax)
    {
        NavigationSession? session = Navigation();
        PgslContext? context = GetContext();
        Genesis.Runtime.ECS.World? world = ActiveGameContext?.World;
        NavMeshAgent? agent = Agent();
        if (session is null || context is null || world is null || agent is null
            || !double.IsFinite(radius) || !double.IsFinite(waitMin) || !double.IsFinite(waitMax)) return;
        Entity entity = world.GetEntity(context.InstanceId);
        if (!world.IsAlive(entity)) return;
        float min = (float)Math.Clamp(Math.Min(waitMin, waitMax), 0, 86_400);
        float max = (float)Math.Clamp(Math.Max(waitMin, waitMax), min, 86_400);
        agent.SetPath([]);
        session.Routines[entity.Id] = new PathingRoutineState
        {
            Kind = PathingRoutineKind.Wander,
            Origin = new Vector3((float)context.X, (float)context.Y, (float)context.Z),
            Radius = (float)Math.Clamp(radius, 0, 100_000),
            WaitMin = min,
            WaitMax = max,
            RandomState = Seed(entity.Id),
        };
        NavigationDebugTelemetry.SetRoutine(ActiveGameContext?.Scene, entity.Id, "Wander", 0);
    }

    [PgslCommand("PathSeek", "PathSeek(targetX, targetY, targetZ)",
        "Use NavMesh A* to seek a world-space destination", "Navigation")]
    public static void PathSeek(double targetX, double targetY, double targetZ)
    {
        RemoveCurrentRoutine();
        NavMeshAgentSetDestination(targetX, targetY, targetZ);
        PgslContext? context = GetContext();
        if (context is not null)
            NavigationDebugTelemetry.SetRoutine(ActiveGameContext?.Scene, context.InstanceId, "Seek", 1);
    }

    [PgslCommand("PathStop", "PathStop()", "Stop the current route or navigation request immediately", "Navigation")]
    public static void PathStop()
    {
        RemoveCurrentRoutine();
        Agent()?.SetPath([]);
    }

    private static void RemoveCurrentRoutine()
    {
        PgslContext? context = GetContext();
        NavigationSession? session = Navigation();
        if (context is not null)
        {
            session?.Routines.Remove(context.InstanceId);
            NavigationDebugTelemetry.ClearRoutine(ActiveGameContext?.Scene, context.InstanceId);
        }
    }

    private static void UpdatePathingRoutines(RuntimeScene scene, NavigationSession session, float dt)
    {
        if (session.Routines.Count == 0) return;
        List<int>? remove = null;
        foreach ((int entityId, PathingRoutineState state) in session.Routines)
        {
            Entity entity = scene.World.GetEntity(entityId);
            if (!scene.World.IsAlive(entity) || !scene.World.Has<TransformComponent>(entity)
                || !scene.World.Has<NavMeshAgentComponent>(entity))
            {
                (remove ??= []).Add(entityId);
                continue;
            }
            ref NavMeshAgentComponent component = ref scene.World.GetRef<NavMeshAgentComponent>(entity);
            if (component.Agent is null) { (remove ??= []).Add(entityId); continue; }
            ref TransformComponent transform = ref scene.World.GetRef<TransformComponent>(entity);
            Vector3 position = new(transform.X, transform.Y, transform.Z);
            state.Elapsed += MathF.Max(0f, dt);
            if (state.WaitRemaining > 0f)
            {
                state.WaitRemaining = MathF.Max(0f, state.WaitRemaining - MathF.Max(0f, dt));
                continue;
            }
            if (state.SegmentActive && component.Agent.HasArrived)
            {
                state.SegmentActive = false;
                state.WaitRemaining = WaitAfterArrival(state);
                continue;
            }
            if (state.SegmentActive && component.Agent.HasPath) continue;

            if (state.Kind == PathingRoutineKind.Wander)
            {
                if (!StartWanderSegment(session, state, position, component.Agent))
                    state.WaitRemaining = MathF.Max(.2f, state.WaitMin);
                else state.SegmentActive = true;
                continue;
            }

            PathingRoute? route = state.Asset?.Route;
            if (route is null) { (remove ??= []).Add(entityId); continue; }
            NavigationDebugTelemetry.UpdateRoutineWaypoint(scene, entityId, Math.Max(0, state.Waypoint));
            component.Agent.Speed = route.SpeedAt(state.Elapsed);
            component.Agent.StoppingDistance = route.StoppingDistance;
            if (route.Mode == PathingRouteMode.WanderRadius)
            {
                state.Kind = PathingRoutineKind.Wander;
                state.Origin = route.Waypoints.Count > 0 ? route.Waypoints[0].Position : position;
                state.Radius = route.WanderRadius;
                state.WaitMin = route.DefaultWaitSeconds;
                state.WaitMax = route.DefaultWaitSeconds;
                continue;
            }
            if (route.Mode == PathingRouteMode.FollowLeader)
            {
                state.RepathCountdown -= MathF.Max(0f, dt);
                if (state.RepathCountdown > 0f) continue;
                state.RepathCountdown = .2f;
                if (!TryFindNamedEntity(scene, route.FollowTarget, entity, out Vector3 target)) continue;
                Vector3 delta = target - position;
                if (delta.Length() <= MathF.Max(route.FollowOffset, route.StoppingDistance)) continue;
                Vector3 end = target - Vector3.Normalize(delta) * route.FollowOffset;
                component.Agent.SetPath(FindPath(session, position, end));
                state.SegmentActive = component.Agent.HasPath;
                continue;
            }
            if (!AdvanceWaypoint(state, route))
            {
                (remove ??= []).Add(entityId);
                component.Agent.SetPath([]);
                continue;
            }
            Vector3 destination = route.Waypoints[state.Waypoint].Position;
            component.Agent.SetPath(FindPath(session, position, destination));
            state.SegmentActive = component.Agent.HasPath;
        }
        if (remove is not null)
        {
            foreach (int id in remove)
            {
                session.Routines.Remove(id);
                NavigationDebugTelemetry.ClearRoutine(scene, id);
            }
        }
    }

    private static bool AdvanceWaypoint(PathingRoutineState state, PathingRoute route)
    {
        int count = route.Waypoints.Count;
        if (count == 0) return false;
        if (state.Waypoint < 0) { state.Waypoint = 0; return true; }
        int next = state.Waypoint + state.Direction;
        if (next >= 0 && next < count) { state.Waypoint = next; return true; }
        switch (state.LoopMode)
        {
            case PathingLoopMode.Loop: state.Waypoint = state.Direction > 0 ? 0 : count - 1; return true;
            case PathingLoopMode.PingPong when count > 1:
                state.Direction *= -1; state.Waypoint = Math.Clamp(state.Waypoint + state.Direction, 0, count - 1); return true;
            default: return false;
        }
    }

    private static float WaitAfterArrival(PathingRoutineState state)
    {
        if (state.Kind == PathingRoutineKind.Wander)
            return Lerp(state.WaitMin, state.WaitMax, NextRandom(state));
        PathingRoute? route = state.Asset?.Route;
        if (route is null || state.Waypoint < 0 || state.Waypoint >= route.Waypoints.Count) return 0f;
        return MathF.Max(route.DefaultWaitSeconds, route.Waypoints[state.Waypoint].WaitSeconds);
    }

    private static bool StartWanderSegment(NavigationSession session, PathingRoutineState state, Vector3 position, NavMeshAgent agent)
    {
        if (state.Radius <= 0f) return false;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            float angle = NextRandom(state) * MathF.Tau;
            float radius = MathF.Sqrt(NextRandom(state)) * state.Radius;
            Vector3 target = state.Origin + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            IReadOnlyList<Vector3> path = FindPath(session, position, target);
            if (path.Count == 0) continue;
            agent.SetPath(path);
            return true;
        }
        return false;
    }

    private static IReadOnlyList<Vector3> FindPath(NavigationSession session, Vector3 from, Vector3 to) =>
        session.Query is null ? [to] : session.Query.FindPath(from, to);

    private static bool TryFindNamedEntity(RuntimeScene scene, string name, Entity self, out Vector3 position)
    {
        position = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        bool found = false;
        Vector3 resolved = default;
        scene.World.Query<TransformComponent>((Entity entity, ref TransformComponent transform) =>
        {
            if (found || entity == self || !ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry? assets)) return;
            bool matches = string.Equals(assets.InstanceName, name, StringComparison.OrdinalIgnoreCase)
                || Genesis.Runtime.Scene.ObjectNameMatcher.Matches(assets.Prefab, name);
            if (!matches) return;
            resolved = new Vector3(transform.X, transform.Y, transform.Z);
            found = true;
        });
        position = resolved;
        return found;
    }

    private static string ResolvePathingAsset(string reference)
    {
        return ResourceNames.Resolve(ProjectPath, reference, ResourceType.Pathing);
    }

    private static uint Seed(int id) => unchecked((uint)(id * 747796405) + 2891336453u);
    private static float NextRandom(PathingRoutineState state)
    {
        uint value = state.RandomState == 0 ? 0x9E3779B9u : state.RandomState;
        value ^= value << 13; value ^= value >> 17; value ^= value << 5;
        state.RandomState = value;
        return (value & 0x00FFFFFFu) / 16777216f;
    }
    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);
}
