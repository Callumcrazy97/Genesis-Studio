#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Runtime.Debugger;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Project;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;
public static partial class PgslCommands
{
    private sealed class NavigationSession
    {
        public string RoomId = "";
        public NavMeshQuery? Query;
        public readonly Dictionary<int, IReadOnlyList<Vector3>> Paths = [];
        public readonly List<CrowdAgentSample> CrowdSamples = [];
        public readonly CrowdNavigationSolver CrowdSolver = new();
        public readonly Dictionary<int, PathingRoutineState> Routines = [];
        public int NextPath = 1;
    }
    private static readonly ConditionalWeakTable<RuntimeScene, NavigationSession> NavigationSessions = new();
    internal static void ClearNavigation(RuntimeScene scene) => NavigationSessions.Remove(scene);
    private static NavigationSession? Navigation()
    {
        var scene = ActiveGameContext?.Scene;
        if (scene == null) return null;
        var session = NavigationSessions.GetOrCreateValue(scene);
        string roomId = ActiveGameContext?.Room?.Id ?? "";
        if (session.RoomId != roomId)
        {
            session.RoomId = roomId; session.Query = null; session.Paths.Clear(); session.Routines.Clear();
            string? file = NavigationFile();
            if (file != null && File.Exists(file))
            {
                try { session.Query = new NavMeshQuery(NavMeshData.Load(file)); }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                { ActiveGameContext?.Log("Navigation sidecar could not be loaded: " + ex.Message); }
            }
        }
        return session;
    }
    private static string? NavigationFile()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath) || ActiveGameContext?.Room == null) return null;
        string room = ProjectRoomResolver.ResolveRoomFile(ProjectPath, ActiveGameContext.Room.Name);
        return File.Exists(room) ? room + ".navmesh" : null;
    }
    [PgslCommand("NavMeshBake", "NavMeshBake()", "Bake a navigation grid from live terrain bounds and static colliders; save beside the room", "Navigation")]
    public static void NavMeshBake()
    {
        var game = ActiveGameContext;
        if (game?.Room == null) return;
        Vector3 min = Vector3.Zero, max = new(game.Room.Settings.Width, 0, game.Room.Settings.Depth);
        var terrain = game.Scene?.Subsystems.OfType<RoomTerrainSubsystem>().FirstOrDefault();
        if (terrain != null && terrain.TryGetNavigationBounds(out var terrainMin, out var terrainMax)) { min = terrainMin; max = terrainMax; }
        float cell = MathF.Max(1, MathF.Max(max.X - min.X, max.Z - min.Z) / 512);
        NavMeshBakeGrid(min.X, min.Z, Math.Ceiling((max.X - min.X) / cell), Math.Ceiling((max.Z - min.Z) / cell), cell);
    }
    [PgslCommand("NavMeshBakeGrid", "NavMeshBakeGrid(originX, originZ, widthCells, depthCells, cellSize)", "Bake explicit navigation bounds with 0.35m agent radius, 1.8m height and 45 degree slope limit", "Navigation")]
    public static void NavMeshBakeGrid(double originX, double originZ, double widthCells, double depthCells, double cellSize)
    {
        var game = ActiveGameContext; var session = Navigation();
        if (game?.World == null || session == null) return;
        if (!Finite3(originX, originZ, cellSize) || !double.IsFinite(widthCells) || !double.IsFinite(depthCells)
            || widthCells < 1 || depthCells < 1 || widthCells * depthCells > 1_048_576
            || widthCells != Math.Truncate(widthCells) || depthCells != Math.Truncate(depthCells))
            throw new ArgumentOutOfRangeException(nameof(widthCells), "Invalid navigation grid dimensions.");
        var obstacles = new List<NavigationObstacle>();
        game.World.Query<RigidBodyComponent, Transform3DComponent>((entity, ref body, ref transform) =>
        {
            if (!body.Collision || body.IsSensor || body.Motion != PhysicsMotionType.Static) return;
            Vector3 half = body.HalfExtents;
            Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(transform.Rotation);
            Vector3 extent = new(Math.Abs(rotation.M11) * half.X + Math.Abs(rotation.M21) * half.Y + Math.Abs(rotation.M31) * half.Z,
                Math.Abs(rotation.M12) * half.X + Math.Abs(rotation.M22) * half.Y + Math.Abs(rotation.M32) * half.Z,
                Math.Abs(rotation.M13) * half.X + Math.Abs(rotation.M23) * half.Y + Math.Abs(rotation.M33) * half.Z);
            obstacles.Add(new(transform.Position - extent, transform.Position + extent));
        });
        var data = NavMeshBuilder.Build((float)originX, (float)originZ, (int)widthCells, (int)depthCells, (float)cellSize,
            game.GetTerrainHeight, obstacles);
        session.Query = new NavMeshQuery(data); session.Paths.Clear();
        game.World.Query<NavMeshAgentComponent>((entity, ref agent) => agent.Agent?.SetPath([]));
        string? file = NavigationFile();
        if (file != null) data.Save(file);
    }
    [PgslCommand("NavMeshPathFind", "NavMeshPathFind(startX, startY, startZ, endX, endY, endZ) -> number", "Find a smoothed route; -1 means no route or mesh", "Navigation")]
    public static double NavMeshPathFind(double startX, double startY, double startZ, double endX, double endY, double endZ)
    {
        var session = Navigation();
        if (session?.Query == null || !Finite3(startX, startY, startZ) || !Finite3(endX, endY, endZ)) return -1;
        var path = session.Query.FindPath(new((float)startX, (float)startY, (float)startZ), new((float)endX, (float)endY, (float)endZ));
        if (path.Count == 0) return -1;
        if (session.Paths.Count >= 4096) throw new InvalidOperationException("Free unused navigation paths before requesting more.");
        int id = checked(session.NextPath++); session.Paths[id] = path; return id;
    }
    private static IReadOnlyList<Vector3>? NavigationPath(double id) => double.IsFinite(id) && id >= 1 && id <= int.MaxValue && id == Math.Truncate(id)
        && Navigation()?.Paths.TryGetValue((int)id, out var path) == true ? path : null;
    private static Vector3 NavigationWaypoint(double id, double index) => NavigationPath(id) is { } path && double.IsFinite(index)
        && index == Math.Truncate(index) && index >= 0 && index < path.Count ? path[(int)index] : Vector3.Zero;
    [PgslCommand("NavMeshPathGetWaypointCount", "NavMeshPathGetWaypointCount(pathId) -> number", "Read the waypoint count", "Navigation")]
    public static double NavMeshPathGetWaypointCount(double id) => NavigationPath(id)?.Count ?? 0;
    [PgslCommand("NavMeshPathGetWaypointX", "NavMeshPathGetWaypointX(pathId, index) -> number", "Waypoint X", "Navigation")]
    public static double NavMeshPathGetWaypointX(double id, double index) => NavigationWaypoint(id, index).X;
    [PgslCommand("NavMeshPathGetWaypointY", "NavMeshPathGetWaypointY(pathId, index) -> number", "Waypoint Y", "Navigation")]
    public static double NavMeshPathGetWaypointY(double id, double index) => NavigationWaypoint(id, index).Y;
    [PgslCommand("NavMeshPathGetWaypointZ", "NavMeshPathGetWaypointZ(pathId, index) -> number", "Waypoint Z", "Navigation")]
    public static double NavMeshPathGetWaypointZ(double id, double index) => NavigationWaypoint(id, index).Z;
    [PgslCommand("NavMeshPathFree", "NavMeshPathFree(pathId)", "Release a path handle", "Navigation")]
    public static void NavMeshPathFree(double id) { if (NavigationPath(id) != null) Navigation()?.Paths.Remove((int)id); }
    private static NavMeshAgent? Agent()
    {
        var ctx = GetContext(); var world = ActiveGameContext?.World;
        if (ctx == null || world == null) return null;
        var entity = world.GetEntity(ctx.InstanceId);
        if (!world.IsAlive(entity)) return null;
        if (!world.Has<NavMeshAgentComponent>(entity)) world.Set(entity, new NavMeshAgentComponent { Agent = new NavMeshAgent() });
        return world.GetRef<NavMeshAgentComponent>(entity).Agent;
    }
    [PgslCommand("NavMeshAgentSetDestination", "NavMeshAgentSetDestination(x, y, z)", "Attach a navigation agent and request a destination on the current mesh", "Navigation")]
    public static void NavMeshAgentSetDestination(double x, double y, double z)
    {
        var ctx = GetContext(); var agent = Agent(); var session = Navigation();
        if (ctx == null || agent == null || !Finite3(x, y, z)) return;
        agent.SetPath(session?.Query?.FindPath(new((float)ctx.X, (float)ctx.Y, (float)ctx.Z), new((float)x, (float)y, (float)z)) ?? []);
    }
    [PgslCommand("NavMeshAgentSetSpeed", "NavMeshAgentSetSpeed(speed)", "Set movement speed in world units per second", "Navigation")]
    public static void NavMeshAgentSetSpeed(double speed) { if (Agent() is { } agent && double.IsFinite(speed)) agent.Speed = (float)Math.Clamp(speed, 0, 10000); }
    [PgslCommand("NavMeshAgentGetDistanceRemaining", "NavMeshAgentGetDistanceRemaining() -> number", "Remaining route distance; -1 when no path exists", "Navigation")]
    public static double NavMeshAgentGetDistanceRemaining() => Agent()?.DistanceRemaining ?? -1;
    [PgslCommand("NavMeshAgentHasArrived", "NavMeshAgentHasArrived() -> bool", "True only after reaching a valid destination", "Navigation")]
    public static bool NavMeshAgentHasArrived() => Agent()?.HasArrived ?? false;

    [PgslCommand("NavMeshAgentConfigureAvoidance", "NavMeshAgentConfigureAvoidance(radius, neighborDistance, strength, maxNeighbors)",
        "Enable local avoidance and configure this agent's generic separation limits", "Navigation")]
    public static void NavMeshAgentConfigureAvoidance(
        double radius,
        double neighborDistance,
        double strength,
        double maxNeighbors)
    {
        if (!Finite3(radius, neighborDistance, strength) || !double.IsFinite(maxNeighbors)
            || !TryCrowd(out var world, out var entity, out var crowd)) return;
        crowd.Enabled = true;
        crowd.Radius = (float)Math.Clamp(radius, .01, 100);
        crowd.NeighborDistance = (float)Math.Clamp(neighborDistance, crowd.Radius * 2, 1000);
        crowd.AvoidanceStrength = (float)Math.Clamp(strength, 0, 2);
        crowd.MaxNeighbors = (int)Math.Clamp(Math.Truncate(maxNeighbors), 1, 64);
        world.Set(entity, crowd);
    }

    [PgslCommand("NavMeshAgentConfigureUpdateRates", "NavMeshAgentConfigureUpdateRates(nearHz, farHz, farDistance, avoidanceDistance)",
        "Set distance-scaled local-avoidance update rates; movement itself remains continuous", "Navigation")]
    public static void NavMeshAgentConfigureUpdateRates(
        double nearHz,
        double farHz,
        double farDistance,
        double avoidanceDistance)
    {
        if (!Finite3(nearHz, farHz, farDistance) || !double.IsFinite(avoidanceDistance)
            || !TryCrowd(out var world, out var entity, out var crowd)) return;
        crowd.NearUpdateHz = (float)Math.Clamp(nearHz, 1, 240);
        crowd.FarUpdateHz = (float)Math.Clamp(farHz, .25, crowd.NearUpdateHz);
        crowd.FarDistance = (float)Math.Clamp(farDistance, 0, 100000);
        crowd.AvoidanceDistance = (float)Math.Clamp(avoidanceDistance, crowd.FarDistance, 100000);
        crowd.UpdateCountdown = 0f;
        world.Set(entity, crowd);
    }

    [PgslCommand("NavMeshAgentSetAvoidanceEnabled", "NavMeshAgentSetAvoidanceEnabled(enabled)",
        "Enable or disable this agent's local avoidance", "Navigation")]
    public static void NavMeshAgentSetAvoidanceEnabled(bool enabled)
    {
        if (!TryCrowd(out var world, out var entity, out var crowd)) return;
        crowd.Enabled = enabled;
        if (!enabled) crowd.Steering = Vector3.Zero;
        world.Set(entity, crowd);
    }

    private static bool TryCrowd(
        out Genesis.Runtime.ECS.World world,
        out Genesis.Shared.ECS.Entity entity,
        out CrowdAgentComponent crowd)
    {
        world = ActiveGameContext?.World!;
        var context = GetContext();
        entity = world is null || context is null
            ? Genesis.Shared.ECS.Entity.Null
            : world.GetEntity(context.InstanceId);
        if (world is null || !world.IsAlive(entity))
        {
            crowd = default;
            return false;
        }
        _ = Agent();
        crowd = world.Has<CrowdAgentComponent>(entity)
            ? world.GetRef<CrowdAgentComponent>(entity)
            : DefaultCrowd();
        return true;
    }

    internal static void UpdateNavigation(RuntimeScene scene, float dt)
    {
        if (!NavigationSessions.TryGetValue(scene, out var session)) return;
        if (!NavigationDebugTelemetry.ShouldAdvanceAi())
        {
            NavigationDebugTelemetry.Capture(scene, session.Query?.Data);
            return;
        }
        UpdatePathingRoutines(scene, session, dt);
        session.CrowdSamples.Clear();
        Vector3 camera = scene.Camera3D.Position;
        scene.World.Query<TransformComponent, NavMeshAgentComponent>((entity, ref transform, ref component) =>
        {
            if (component.Agent == null || !scene.World.Has<CrowdAgentComponent>(entity)) return;
            ref CrowdAgentComponent crowd = ref scene.World.GetRef<CrowdAgentComponent>(entity);
            NormalizeCrowd(ref crowd);
            if (!crowd.Enabled)
            {
                crowd.Steering = Vector3.Zero;
                crowd.Velocity = Vector3.Zero;
                return;
            }

            Vector3 position = new(transform.X, transform.Y, transform.Z);
            float distance = Vector3.Distance(position, camera);
            crowd.UpdateCountdown -= MathF.Max(0f, dt);
            bool inRange = crowd.AvoidanceDistance <= 0f || distance <= crowd.AvoidanceDistance;
            bool recalculate = inRange && crowd.UpdateCountdown <= 0f;
            if (!inRange) crowd.Steering = Vector3.Zero;
            if (recalculate)
            {
                float frequency = distance >= crowd.FarDistance ? crowd.FarUpdateHz : crowd.NearUpdateHz;
                crowd.UpdateCountdown = 1f / MathF.Max(.25f, frequency);
            }
            session.CrowdSamples.Add(new CrowdAgentSample(
                entity.Id,
                position,
                crowd.Velocity,
                crowd.Radius,
                crowd.NeighborDistance,
                crowd.AvoidanceStrength,
                crowd.MaxNeighbors,
                recalculate,
                crowd.Steering));
        });
        session.CrowdSolver.Solve(session.CrowdSamples);

        scene.World.Query<TransformComponent, NavMeshAgentComponent>((entity, ref transform, ref component) =>
        {
            if (component.Agent == null) return;
            Vector3 from = new(transform.X, transform.Y, transform.Z);
            Vector3 avoidance = Vector3.Zero;
            bool hasCrowd = scene.World.Has<CrowdAgentComponent>(entity);
            if (hasCrowd)
            {
                ref CrowdAgentComponent crowd = ref scene.World.GetRef<CrowdAgentComponent>(entity);
                if (crowd.Enabled && session.CrowdSolver.TryGetSteering(entity.Id, out Vector3 steering))
                    crowd.Steering = steering;
                avoidance = crowd.Enabled ? crowd.Steering : Vector3.Zero;
            }
            Vector3 to = component.Agent.Update(from, dt, (a, b) =>
            {
                if (session.Query != null && !session.Query.CanTravel(a, b)) return false;
                Vector3 delta = b - a; float length = delta.Length();
                return length < 1e-5f || scene.Physics?.Raycast(scene.World, a + Vector3.UnitY * .9f, delta,
                    length + .35f, out _, entity) != true;
            }, avoidance);
            int cell = session.Query?.Data.Cell(to) ?? -1;
            if (session.Query != null && cell >= 0) to.Y = session.Query.Data.Heights[cell];
            transform.X = to.X; transform.Y = to.Y; transform.Z = to.Z;
            if (hasCrowd)
            {
                ref CrowdAgentComponent crowd = ref scene.World.GetRef<CrowdAgentComponent>(entity);
                crowd.Velocity = dt > 1e-6f ? (to - from) / dt : Vector3.Zero;
            }
            if (from != to) SynchronizeTransform(scene.World, entity, Vector3.One);
        });
        NavigationDebugTelemetry.Capture(scene, session.Query?.Data);
    }

    private static CrowdAgentComponent DefaultCrowd() => new()
    {
        Enabled = true,
        Radius = .35f,
        NeighborDistance = 3f,
        AvoidanceStrength = .75f,
        MaxNeighbors = 12,
        NearUpdateHz = 30f,
        FarUpdateHz = 6f,
        FarDistance = 35f,
        AvoidanceDistance = 250f,
    };

    private static void NormalizeCrowd(ref CrowdAgentComponent crowd)
    {
        if (!float.IsFinite(crowd.Radius) || crowd.Radius <= 0f) crowd.Radius = .35f;
        if (!float.IsFinite(crowd.NeighborDistance) || crowd.NeighborDistance < crowd.Radius * 2f)
            crowd.NeighborDistance = MathF.Max(3f, crowd.Radius * 2f);
        if (!float.IsFinite(crowd.AvoidanceStrength)) crowd.AvoidanceStrength = .75f;
        crowd.AvoidanceStrength = Math.Clamp(crowd.AvoidanceStrength, 0f, 2f);
        crowd.MaxNeighbors = Math.Clamp(crowd.MaxNeighbors <= 0 ? 12 : crowd.MaxNeighbors, 1, 64);
        if (!float.IsFinite(crowd.NearUpdateHz) || crowd.NearUpdateHz <= 0f) crowd.NearUpdateHz = 30f;
        if (!float.IsFinite(crowd.FarUpdateHz) || crowd.FarUpdateHz <= 0f) crowd.FarUpdateHz = 6f;
        crowd.FarUpdateHz = MathF.Min(crowd.NearUpdateHz, crowd.FarUpdateHz);
        if (!float.IsFinite(crowd.FarDistance) || crowd.FarDistance < 0f) crowd.FarDistance = 35f;
        if (!float.IsFinite(crowd.AvoidanceDistance) || crowd.AvoidanceDistance < crowd.FarDistance)
            crowd.AvoidanceDistance = MathF.Max(250f, crowd.FarDistance);
        if (!float.IsFinite(crowd.UpdateCountdown)) crowd.UpdateCountdown = 0f;
        if (!Finite(crowd.Steering)) crowd.Steering = Vector3.Zero;
        if (!Finite(crowd.Velocity)) crowd.Velocity = Vector3.Zero;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
