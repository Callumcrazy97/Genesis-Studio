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
using Genesis.Runtime.Scene;
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
        Vector3 min = Vector3.Zero, max = new(game.Room.Settings.Width, 0,
            game.Room.Dimension == Genesis.Runtime.Scene.RoomDimension.TwoD ? game.Room.Settings.Height : game.Room.Settings.Depth);
        var terrain = game.Scene?.Subsystems.OfType<RoomTerrainSubsystem>().FirstOrDefault();
        if (game.Room.Dimension != Genesis.Runtime.Scene.RoomDimension.TwoD && terrain != null
            && terrain.TryGetNavigationBounds(out var terrainMin, out var terrainMax)) { min = terrainMin; max = terrainMax; }
        float extent = MathF.Max(max.X - min.X, max.Z - min.Z);
        // Engine.Navigation.Mesh.CellSize when set, never finer than the grid's million cells allow.
        float cell = _bakeCell > 0 ? MathF.Max((float)_bakeCell, extent / 1024f) : MathF.Max(1, extent / 512);
        NavMeshBakeGrid(min.X, min.Z, Math.Max(1, Math.Ceiling((max.X - min.X) / cell)), Math.Max(1, Math.Ceiling((max.Z - min.Z) / cell)), cell);
    }

    // ── Engine.Navigation.Mesh: the agent the next bake is made for ──────────────────────

    private static double _bakeRadius = .35, _bakeHeight = 1.8, _bakeSlope = 45, _bakeStep = .45, _bakeCell;

    internal static void ResetNavigationBake()
    {
        _bakeRadius = .35; _bakeHeight = 1.8; _bakeSlope = 45; _bakeStep = .45; _bakeCell = 0;
        _navigationOn = true;
    }

    private static bool _navigationOn = true;

    [PgslCommand("Enabled", "Engine.Navigation.Enabled = false", "Whether navigation agents move along their routes (true unless set); off, every agent holds where it is with its route kept, for a pause or a cutscene", "Navigation", Namespace = "Engine.Navigation", Flat = false)]
    public static bool NavigationEnabled { get => _navigationOn; set => _navigationOn = value; }

    [PgslCommand("Enabled", "Engine.Navigation.Agent.Enabled = false", "Whether this instance's navigation agent moves along its route (true unless set); off, it holds where it is with its route kept: stunned, talking, waiting at a door", "Navigation", Namespace = "Engine.Navigation.Agent", Flat = false)]
    public static bool NavigationAgentEnabled
    {
        get => AgentIfAny()?.Enabled ?? true;
        set { if (Agent() is { } agent) agent.Enabled = value; }
    }

    [PgslCommand("AgentRadius", "Engine.Navigation.Mesh.AgentRadius = 0.5", "How wide the agents the next bake is made for are, 0 to 10 metres (0.35 unless set): walls and edges are kept this far away", "Navigation", Namespace = "Engine.Navigation.Mesh", Flat = false)]
    public static double NavigationMeshAgentRadius { get => _bakeRadius; set { if (double.IsFinite(value)) _bakeRadius = Math.Clamp(value, 0, 10); } }

    [PgslCommand("AgentHeight", "Engine.Navigation.Mesh.AgentHeight = 2.5", "How tall the agents the next bake is made for are, 0.1 to 50 metres (1.8 unless set): floors need this much headroom", "Navigation", Namespace = "Engine.Navigation.Mesh", Flat = false)]
    public static double NavigationMeshAgentHeight { get => _bakeHeight; set { if (double.IsFinite(value)) _bakeHeight = Math.Clamp(value, 0.1, 50); } }

    [PgslCommand("MaxSlope", "Engine.Navigation.Mesh.MaxSlope = 30", "The steepest ground the next bake lets agents walk, 0 to 89 degrees (45 unless set)", "Navigation", Namespace = "Engine.Navigation.Mesh", Flat = false)]
    public static double NavigationMeshMaxSlope { get => _bakeSlope; set { if (double.IsFinite(value)) _bakeSlope = Math.Clamp(value, 0, 89); } }

    [PgslCommand("StepHeight", "Engine.Navigation.Mesh.StepHeight = 0.3", "The highest step the next bake lets agents climb, 0 to 10 metres (0.45 unless set)", "Navigation", Namespace = "Engine.Navigation.Mesh", Flat = false)]
    public static double NavigationMeshStepHeight { get => _bakeStep; set { if (double.IsFinite(value)) _bakeStep = Math.Clamp(value, 0, 10); } }

    [PgslCommand("CellSize", "Engine.Navigation.Mesh.CellSize = 0.5", "How fine a grid Engine.Navigation.Bake() lays over the room, 0 to 100 metres a cell (0 unless set: worked out from the room's size, 512 cells across); finer finds narrower ways at more memory", "Navigation", Namespace = "Engine.Navigation.Mesh", Flat = false)]
    public static double NavigationMeshCellSize { get => _bakeCell; set { if (double.IsFinite(value)) _bakeCell = Math.Clamp(value, 0, 100); } }

    /// <summary>The running room's navigation mesh, null without one (for checks).</summary>
    internal static NavMeshData? NavigationMeshData => Navigation()?.Query?.Data;

    [PgslCommand("Baked", "Engine.Navigation.Baked", "Whether the room has a navigation mesh to find routes on: baked this run, or loaded from beside the room (read)", "Navigation", Namespace = "Engine.Navigation", Flat = false)]
    public static bool NavigationBaked => Navigation()?.Query != null;

    [PgslCommand("NavMeshBakeGrid", "NavMeshBakeGrid(originX, originZ, widthCells, depthCells, cellSize)", "Bake explicit navigation bounds for Engine.Navigation.Mesh's agent (0.35 m radius, 1.8 m height, 45 degree slopes and 0.45 m steps unless set)", "Navigation")]
    public static void NavMeshBakeGrid(double originX, double originZ, double widthCells, double depthCells, double cellSize)
    {
        var game = ActiveGameContext; var session = Navigation();
        if (game?.World == null || session == null) return;
        if (!Finite3(originX, originZ, cellSize) || !double.IsFinite(widthCells) || !double.IsFinite(depthCells)
            || widthCells < 1 || depthCells < 1 || widthCells * depthCells > 1_048_576
            || widthCells != Math.Truncate(widthCells) || depthCells != Math.Truncate(depthCells))
            throw new ArgumentOutOfRangeException(nameof(widthCells), "Invalid navigation grid dimensions.");
        var obstacles = new List<NavigationObstacle>();
        bool planarXY = game.Room?.Dimension == Genesis.Runtime.Scene.RoomDimension.TwoD;
        game.World.Query<RigidBodyComponent, Transform3DComponent>((entity, ref body, ref transform) =>
        {
            if (!body.Collision || body.IsSensor || body.Motion != PhysicsMotionType.Static) return;
            Vector3 half = body.HalfExtents;
            Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(transform.Rotation);
            Vector3 extent = new(Math.Abs(rotation.M11) * half.X + Math.Abs(rotation.M21) * half.Y + Math.Abs(rotation.M31) * half.Z,
                Math.Abs(rotation.M12) * half.X + Math.Abs(rotation.M22) * half.Y + Math.Abs(rotation.M32) * half.Z,
                Math.Abs(rotation.M13) * half.X + Math.Abs(rotation.M23) * half.Y + Math.Abs(rotation.M33) * half.Z);
            Vector3 lower = transform.Position - extent, upper = transform.Position + extent;
            if (planarXY && game.World.Has<SpritePhysicsBindingComponent>(entity))
            {
                Vector3 pixelLower = SpritePhysicsBinding.ToPixels(lower), pixelUpper = SpritePhysicsBinding.ToPixels(upper);
                obstacles.Add(new(new Vector3(pixelLower.X, -1, pixelUpper.Y), new Vector3(pixelUpper.X, 2, pixelLower.Y)));
                return;
            }
            obstacles.Add(planarXY
                ? new(new Vector3(lower.X, -1, lower.Y), new Vector3(upper.X, 2, upper.Y))
                : new(lower, upper));
        });
        if (planarXY && game.Room is not null)
            foreach (var tile in new RoomTileCollisionMap(game.Room, ProjectPath).Solids)
                obstacles.Add(new(new Vector3(tile.Left, -1, tile.Top), new Vector3(tile.Right, 2, tile.Bottom)));
        // In a 3D room the tops of static boxes an agent fits on are floors of their own.
        var data = NavMeshBuilder.Build((float)originX, (float)originZ, (int)widthCells, (int)depthCells, (float)cellSize,
            planarXY ? (_, _) => 0f : NavigationGround(game), obstacles,
            agentRadius: (float)_bakeRadius, agentHeight: (float)_bakeHeight, maxStep: (float)_bakeStep, maxSlope: (float)_bakeSlope, floors: !planarXY);
        session.Query = new NavMeshQuery(data); session.Paths.Clear();
        game.World.Query<NavMeshAgentComponent>((entity, ref agent) => agent.Agent?.SetPath([]));
        string? file = NavigationFile();
        if (file != null) data.Save(file);
    }
    /// <summary>
    /// The ground's height for a bake. A terrain's hole has no ground: its height reads as not a
    /// number, so its cells (and those whose edges reach into it) are not walkable.
    /// </summary>
    private static Func<float, float, float> NavigationGround(IGameContext game)
    {
        RoomTerrainSubsystem? terrains = FirstTerrains();
        if (terrains is not { HoleCount: > 0, HolesEnabled: true }) return game.GetTerrainHeight;
        return terrains.NavigationHeightAt;
    }

    [PgslCommand("NavMeshSetAreaCost", "NavMeshSetAreaCost(x1, z1, x2, z2, cost) -> number",
        "Make the navigation cells in a rectangle cheaper or dearer to cross (a road 0.5, mud 3; 1 is normal, 0.05 to 1000), so routes prefer or avoid them; in a 2D room z is y. Set after NavMeshBake or a room load. Returns how many cells changed", "Navigation")]
    public static double NavMeshSetAreaCost(double x1, double z1, double x2, double z2, double cost)
    {
        var session = Navigation();
        if (session?.Query == null || !Finite3(x1, z1, cost) || !double.IsFinite(x2) || !double.IsFinite(z2)) return 0;
        // Routes already found keep their way; the next route found follows the costs.
        return session.Query.SetAreaCost((float)x1, (float)z1, (float)x2, (float)z2, (float)cost);
    }

    [PgslCommand("NavMeshGetAreaCost", "NavMeshGetAreaCost(x, z) -> number", "The area cost of the navigation cell at a point (1 unless set); in a 2D room z is y", "Navigation")]
    public static double NavMeshGetAreaCost(double x, double z) =>
        Navigation()?.Query?.AreaCostAt(new Vector3((float)x, 0, (float)z)) ?? 1;

    [PgslCommand("NavMeshAddObstacle", "NavMeshAddObstacle(x1, y1, z1, x2, y2, z2) -> number",
        "Block the navigation floors an agent would touch a box on (a cart parked, a door shut, a tree fallen) until NavMeshRemoveObstacle: routes found from then on go round it. In a 2D room x, y are the corners and z is not used. Returns its id; 0 without a mesh", "Navigation")]
    public static double NavMeshAddObstacle(double x1, double y1, double z1, double x2, double y2, double z2)
    {
        var session = Navigation();
        if (session?.Query == null || !Finite3(x1, y1, z1) || !Finite3(x2, y2, z2)) return 0;
        bool planarXY = ActiveGameContext?.Room?.Dimension == Genesis.Runtime.Scene.RoomDimension.TwoD;
        return planarXY
            ? session.Query.AddObstacle(new Vector3((float)x1, -1, (float)y1), new Vector3((float)x2, 2, (float)y2))
            : session.Query.AddObstacle(new Vector3((float)x1, (float)y1, (float)z1), new Vector3((float)x2, (float)y2, (float)z2));
    }

    [PgslCommand("NavMeshRemoveObstacle", "NavMeshRemoveObstacle(obstacleId) -> bool", "Take away an obstacle NavMeshAddObstacle placed; false when there is no such obstacle", "Navigation")]
    public static bool NavMeshRemoveObstacle(double id) =>
        double.IsFinite(id) && id >= 1 && id <= int.MaxValue && Navigation()?.Query?.RemoveObstacle((int)id) == true;

    [PgslCommand("NavMeshAgentSetRadius", "NavMeshAgentSetRadius(radius)",
        "How wide this agent is (its radius): its routes keep that far from walls and edges, so a cart or a giant takes the wide way round. 0 (the default) is the size the mesh was baked for, 0.35", "Navigation")]
    public static void NavMeshAgentSetRadius(double radius) { if (Agent() is { } agent && double.IsFinite(radius)) agent.Radius = (float)Math.Clamp(radius, 0, 100); }

    [PgslCommand("NavMeshAddLink", "NavMeshAddLink(x1, y1, z1, x2, y2, z2, bothWays?, cost?) -> number",
        "Let routes go straight between two points walking cannot join (a jump down, a gap, a ladder, a door): one way unless bothWays (the default), for cost (the link's length when left out; more makes routes walk round instead). In a 2D room x, y are the point and z is not used. Set after NavMeshBake or a room load. Returns the link's id, 0 when an end is off the grid or cannot be stood on", "Navigation")]
    public static double NavMeshAddLink(double x1, double y1, double z1, double x2, double y2, double z2, bool bothWays = true, double cost = 0)
    {
        var session = Navigation();
        if (session?.Query == null || !Finite3(x1, y1, z1) || !Finite3(x2, y2, z2) || !double.IsFinite(cost)) return 0;
        bool planarXY = ActiveGameContext?.Room?.Dimension == Genesis.Runtime.Scene.RoomDimension.TwoD;
        Vector3 start = planarXY ? new((float)x1, 0, (float)y1) : new((float)x1, (float)y1, (float)z1);
        Vector3 end = planarXY ? new((float)x2, 0, (float)y2) : new((float)x2, (float)y2, (float)z2);
        return session.Query.AddLink(start, end, bothWays, (float)cost);
    }

    [PgslCommand("NavMeshRemoveLink", "NavMeshRemoveLink(linkId) -> bool", "Take a link away (routes already found keep it); false when there is no such link", "Navigation")]
    public static bool NavMeshRemoveLink(double id) =>
        double.IsFinite(id) && id >= 1 && id <= int.MaxValue && Navigation()?.Query?.RemoveLink((int)id) == true;

    [PgslCommand("NavMeshPathGetWaypointLink", "NavMeshPathGetWaypointLink(pathId, index) -> number",
        "The link a route crosses from this waypoint to the next (its id from NavMeshAddLink), 0 where it is walked", "Navigation")]
    public static double NavMeshPathGetWaypointLink(double id, double index) =>
        NavigationPath(id) is NavMeshRoute route && double.IsFinite(index) && index == Math.Truncate(index) && index >= 0 && index < int.MaxValue
            ? route.LinkAt((int)index) : 0;

    [PgslCommand("NavMeshAgentGetLink", "NavMeshAgentGetLink() -> number",
        "The link this agent is crossing (it goes straight to the far end; play a jump or climb), 0 while it walks", "Navigation")]
    public static double NavMeshAgentGetLink() => Agent()?.CurrentLink ?? 0;

    [PgslCommand("NavMeshAgentGetLinkProgress", "NavMeshAgentGetLinkProgress() -> number",
        "How far across its link this agent is, 0 to 1 (for an arc or an animation's time); 0 while it walks", "Navigation")]
    public static double NavMeshAgentGetLinkProgress() => Agent()?.LinkProgress ?? 0;

    [PgslCommand("NavMeshPathFind", "NavMeshPathFind(startX, startY, startZ, endX, endY, endZ, radius?) -> number", "Find a smoothed route (for an agent of radius, keeping that far from edges; the bake's 0.35 when left out); -1 means no route or mesh", "Navigation")]
    public static double NavMeshPathFind(double startX, double startY, double startZ, double endX, double endY, double endZ, double radius = 0)
    {
        var session = Navigation();
        if (session?.Query == null || !Finite3(startX, startY, startZ) || !Finite3(endX, endY, endZ)) return -1;
        var path = FindPath(session, new((float)startX, (float)startY, (float)startZ),
            new((float)endX, (float)endY, (float)endZ), ActiveGameContext?.Room?.Dimension == Genesis.Runtime.Scene.RoomDimension.TwoD,
            double.IsFinite(radius) ? (float)Math.Clamp(radius, 0, 100) : 0f);
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
    // The current instance's navigation agent when it has one; reading a value never attaches one.
    private static NavMeshAgent? AgentIfAny()
    {
        var ctx = GetContext(); var world = ActiveGameContext?.World;
        if (ctx == null || world == null) return null;
        var entity = world.GetEntity(ctx.InstanceId);
        return world.IsAlive(entity) && world.Has<NavMeshAgentComponent>(entity) ? world.GetRef<NavMeshAgentComponent>(entity).Agent : null;
    }

    [PgslCommand("Speed", "Engine.Navigation.Agent.Speed = speed", "How fast this instance's navigation agent moves, world units a second, 0 to 10000 (3.8 unless set)", "Navigation", Namespace = "Engine.Navigation.Agent", Flat = false)]
    public static double NavigationAgentSpeed { get => AgentIfAny()?.Speed ?? 3.8; set => NavMeshAgentSetSpeed(value); }

    [PgslCommand("StoppingDistance", "Engine.Navigation.Agent.StoppingDistance = distance", "How close to its destination this instance's agent counts as arrived, 0 to 100 (0.1 unless set)", "Navigation", Namespace = "Engine.Navigation.Agent", Flat = false)]
    public static double NavigationAgentStoppingDistance
    {
        get => AgentIfAny()?.StoppingDistance ?? 0.1;
        set { if (Agent() is { } agent && double.IsFinite(value)) agent.StoppingDistance = (float)Math.Clamp(value, 0, 100); }
    }

    private static NavMeshAgent? Agent()
    {
        var ctx = GetContext(); var world = ActiveGameContext?.World;
        if (ctx == null || world == null) return null;
        var entity = world.GetEntity(ctx.InstanceId);
        if (!world.IsAlive(entity)) return null;
        if (!world.Has<NavMeshAgentComponent>(entity)) world.Set(entity, new NavMeshAgentComponent
            { Agent = new NavMeshAgent { PlanarXY = ActiveGameContext?.Room?.Dimension == Genesis.Runtime.Scene.RoomDimension.TwoD } });
        return world.GetRef<NavMeshAgentComponent>(entity).Agent;
    }
    [PgslCommand("NavMeshAgentSetDestination", "NavMeshAgentSetDestination(x, y, z)", "Attach a navigation agent and request a destination on the current mesh", "Navigation")]
    public static void NavMeshAgentSetDestination(double x, double y, double z)
    {
        var ctx = GetContext(); var agent = Agent(); var session = Navigation();
        if (ctx == null || agent == null || !Finite3(x, y, z)) return;
        agent.SetPath(session is null || session.Query is null ? [] : FindPath(session,
            new((float)ctx.X, (float)ctx.Y, (float)ctx.Z), new((float)x, (float)y, agent.PlanarXY ? (float)ctx.Z : (float)z), agent.PlanarXY, agent.Radius));
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

    // ── Engine.Navigation.Avoidance: how this instance's agent keeps clear of others ─────

    // The current instance's avoidance as it is; reading a value never attaches one.
    private static CrowdAgentComponent CrowdIfAny()
    {
        var context = GetContext(); var world = ActiveGameContext?.World;
        if (context == null || world == null) return DefaultCrowd();
        var entity = world.GetEntity(context.InstanceId);
        return world.IsAlive(entity) && world.Has<CrowdAgentComponent>(entity) ? world.GetRef<CrowdAgentComponent>(entity) : DefaultCrowd();
    }

    [PgslCommand("Radius", "Engine.Navigation.Avoidance.Radius = 0.6", "How much room this instance's agent keeps from other agents, 0.01 to 100 metres (0.35 unless set); setting it turns avoidance on", "Navigation", Namespace = "Engine.Navigation.Avoidance", Flat = false)]
    public static double NavigationAvoidanceRadius
    {
        get => CrowdIfAny().Radius;
        set
        {
            if (!double.IsFinite(value) || !TryCrowd(out var world, out var entity, out var crowd)) return;
            crowd.Radius = (float)Math.Clamp(value, .01, 100);
            crowd.NeighborDistance = MathF.Max(crowd.NeighborDistance, crowd.Radius * 2f);
            world.Set(entity, crowd);
        }
    }

    [PgslCommand("Priority", "Engine.Navigation.Avoidance.Priority = 90", "Who gives way, 0 to 100 (50 unless set): this instance's agent steers aside more for agents above it and less for those below, so a guard of 90 walks through a crowd of 50; setting it turns avoidance on", "Navigation", Namespace = "Engine.Navigation.Avoidance", Flat = false)]
    public static double NavigationAvoidancePriority
    {
        get => CrowdNavigationSolver.UsualPriority + CrowdIfAny().PriorityOffset;
        set
        {
            if (!double.IsFinite(value) || !TryCrowd(out var world, out var entity, out var crowd)) return;
            crowd.PriorityOffset = (float)Math.Clamp(value, 0, 100) - CrowdNavigationSolver.UsualPriority;
            world.Set(entity, crowd);
        }
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
        // With Engine.Navigation off every agent holds where it is, its route kept.
        if (!_navigationOn || !NavigationSessions.TryGetValue(scene, out var session)) return;
        if (!NavigationDebugTelemetry.ShouldAdvanceAi())
        {
            NavigationDebugTelemetry.Capture(scene, session.Query?.Data);
            return;
        }
        UpdatePathingRoutines(scene, session, dt);
        session.CrowdSamples.Clear();
        Vector3 camera = scene.Camera3D.Position;
        // The delegate-free query: the lambda was a closure (scene, camera, dt, session) every frame.
        foreach (var item in scene.World.Query<TransformComponent, NavMeshAgentComponent>())
        {
            var entity = item.Entity;
            ref TransformComponent transform = ref item.C1;
            ref NavMeshAgentComponent component = ref item.C2;
            if (NavigationDebugTelemetry.IsPreviewDriven(scene, entity.Id)) continue;
            if (component.Agent == null || !scene.World.Has<CrowdAgentComponent>(entity)) continue;
            ref CrowdAgentComponent crowd = ref scene.World.GetRef<CrowdAgentComponent>(entity);
            NormalizeCrowd(ref crowd);
            if (!crowd.Enabled)
            {
                crowd.Steering = Vector3.Zero;
                crowd.Velocity = Vector3.Zero;
                continue;
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
                component.Agent.PlanarXY ? new Vector3(position.X, 0, position.Y) : position,
                component.Agent.PlanarXY ? new Vector3(crowd.Velocity.X, 0, crowd.Velocity.Y) : crowd.Velocity,
                crowd.Radius,
                crowd.NeighborDistance,
                crowd.AvoidanceStrength,
                crowd.MaxNeighbors,
                recalculate,
                crowd.Steering,
                CrowdNavigationSolver.UsualPriority + crowd.PriorityOffset));
        }
        session.CrowdSolver.Solve(session.CrowdSamples);

        scene.World.Query<TransformComponent, NavMeshAgentComponent>((entity, ref transform, ref component) =>
        {
            if (NavigationDebugTelemetry.IsPreviewDriven(scene, entity.Id)) return;
            if (component.Agent is not { Enabled: true }) return;
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
            bool planarXY = component.Agent.PlanarXY;
            float agentRadius = component.Agent.Radius;
            Vector3 to = component.Agent.Update(from, dt, (a, b) =>
            {
                Vector3 navA = planarXY ? new Vector3(a.X, 0, a.Y) : a;
                Vector3 navB = planarXY ? new Vector3(b.X, 0, b.Y) : b;
                if (session.Query != null && !session.Query.CanTravel(navA, navB, agentRadius)) return false;
                Vector3 delta = b - a; float length = delta.Length();
                return length < 1e-5f || scene.Physics?.Raycast(scene.World, a + (planarXY ? Vector3.Zero : Vector3.UnitY * .9f), delta,
                    length + .35f, out _, entity) != true;
            }, avoidance);
            // The floor the agent is on (a deck, an upper storey or the ground under it).
            int cell = session.Query?.Data.NodeAt(component.Agent.PlanarXY ? new Vector3(to.X, 0, to.Y) : to) ?? -1;
            // On a link the agent goes straight from one end's height to the other's.
            if (!component.Agent.PlanarXY && session.Query != null && cell >= 0 && component.Agent.CurrentLink == 0)
                to.Y = session.Query.Data.NodeHeight(cell);
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
