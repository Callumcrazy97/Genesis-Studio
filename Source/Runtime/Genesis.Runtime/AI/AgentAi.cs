using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using Genesis.World.Navigation;
using Genesis.World.Terrain;

namespace Genesis.Runtime.AI;

[Flags]
public enum AgentFacts : ulong
{
    None = 0,
    Hungry = 1UL << 0,
    Thirsty = 1UL << 1,
    Tired = 1UL << 2,
    Threatened = 1UL << 3,
    HasFood = 1UL << 4,
    NearWater = 1UL << 5,
    Safe = 1UL << 6,
    Rested = 1UL << 7,
}

public sealed record GoapAction(
    string Name,
    AgentFacts Requires,
    AgentFacts Forbids,
    AgentFacts Adds,
    AgentFacts Removes,
    float Cost = 1f)
{
    public bool CanApply(AgentFacts state) => (state & Requires) == Requires && (state & Forbids) == 0;
    public AgentFacts Apply(AgentFacts state) => (state | Adds) & ~Removes;
}

/// <summary>Deterministic bounded uniform-cost GOAP planner for lightweight runtime agents.</summary>
public static class GoapPlanner
{
    private sealed record Node(AgentFacts State, float Cost, List<GoapAction> Plan);

    public static IReadOnlyList<GoapAction> Plan(AgentFacts initial, AgentFacts goal,
        IEnumerable<GoapAction> actions, int maxExpanded = 256)
    {
        GoapAction[] ordered = (actions ?? Array.Empty<GoapAction>())
            .OrderBy(action => action.Cost).ThenBy(action => action.Name, StringComparer.Ordinal).ToArray();
        var frontier = new PriorityQueue<Node, (float Cost, int Steps, string Tie)>();
        var best = new Dictionary<AgentFacts, float> { [initial] = 0f };
        frontier.Enqueue(new Node(initial, 0f, new List<GoapAction>()), (0f, 0, string.Empty));
        int expanded = 0;
        while (frontier.Count > 0 && expanded++ < Math.Max(1, maxExpanded))
        {
            Node node = frontier.Dequeue();
            if ((node.State & goal) == goal) return node.Plan;
            foreach (GoapAction action in ordered)
            {
                if (!action.CanApply(node.State)) continue;
                AgentFacts next = action.Apply(node.State);
                float cost = node.Cost + MathF.Max(0.001f, action.Cost);
                if (best.TryGetValue(next, out float known) && known <= cost) continue;
                best[next] = cost;
                var plan = new List<GoapAction>(node.Plan) { action };
                frontier.Enqueue(new Node(next, cost, plan), (cost, plan.Count, action.Name));
            }
        }
        return Array.Empty<GoapAction>();
    }
}

public enum BehaviorStatus : byte { Running, Success, Failure }

public abstract class BehaviorNode<TContext>
{
    public abstract BehaviorStatus Tick(TContext context, float deltaSeconds);
}

public sealed class BehaviorCondition<TContext> : BehaviorNode<TContext>
{
    private readonly Func<TContext, bool> _predicate;
    public BehaviorCondition(Func<TContext, bool> predicate) => _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
    public override BehaviorStatus Tick(TContext context, float deltaSeconds) => _predicate(context) ? BehaviorStatus.Success : BehaviorStatus.Failure;
}

public sealed class BehaviorAction<TContext> : BehaviorNode<TContext>
{
    private readonly Func<TContext, float, BehaviorStatus> _action;
    public BehaviorAction(Func<TContext, float, BehaviorStatus> action) => _action = action ?? throw new ArgumentNullException(nameof(action));
    public override BehaviorStatus Tick(TContext context, float deltaSeconds) => _action(context, deltaSeconds);
}

public sealed class BehaviorSequence<TContext> : BehaviorNode<TContext>
{
    private readonly IReadOnlyList<BehaviorNode<TContext>> _children;
    public BehaviorSequence(params BehaviorNode<TContext>[] children) => _children = children ?? Array.Empty<BehaviorNode<TContext>>();
    public override BehaviorStatus Tick(TContext context, float deltaSeconds)
    {
        foreach (BehaviorNode<TContext> child in _children)
        {
            BehaviorStatus status = child.Tick(context, deltaSeconds);
            if (status != BehaviorStatus.Success) return status;
        }
        return BehaviorStatus.Success;
    }
}

public sealed class BehaviorSelector<TContext> : BehaviorNode<TContext>
{
    private readonly IReadOnlyList<BehaviorNode<TContext>> _children;
    public BehaviorSelector(params BehaviorNode<TContext>[] children) => _children = children ?? Array.Empty<BehaviorNode<TContext>>();
    public override BehaviorStatus Tick(TContext context, float deltaSeconds)
    {
        foreach (BehaviorNode<TContext> child in _children)
        {
            BehaviorStatus status = child.Tick(context, deltaSeconds);
            if (status != BehaviorStatus.Failure) return status;
        }
        return BehaviorStatus.Failure;
    }
}

public readonly record struct UtilityOption<T>(T Value, float Score, int Order = 0);

public static class UtilitySelection
{
    public static T Choose<T>(IEnumerable<UtilityOption<T>> options, T fallback = default) =>
        (options ?? Array.Empty<UtilityOption<T>>())
        .OrderByDescending(option => float.IsFinite(option.Score) ? option.Score : float.MinValue)
        .ThenBy(option => option.Order)
        .Select(option => option.Value)
        .FirstOrDefault(fallback);
}

public static class Steering
{
    public static Vector3 Arrive(Vector3 position, Vector3 target, Vector3 velocity,
        float maxSpeed, float acceleration, float arrivalRadius, float deltaSeconds)
    {
        Vector3 delta = target - position;
        float distance = delta.Length();
        if (distance < 0.0001f) return Vector3.Zero;
        float desiredSpeed = MathF.Max(0f, maxSpeed) * Math.Clamp(distance / MathF.Max(0.01f, arrivalRadius), 0.08f, 1f);
        Vector3 desired = delta / distance * desiredSpeed;
        Vector3 change = desired - velocity;
        float maxChange = MathF.Max(0f, acceleration) * MathF.Max(0f, deltaSeconds);
        if (change.LengthSquared() > maxChange * maxChange && maxChange > 0f)
            change = Vector3.Normalize(change) * maxChange;
        Vector3 result = velocity + change;
        return result.LengthSquared() > maxSpeed * maxSpeed && maxSpeed > 0f ? Vector3.Normalize(result) * maxSpeed : result;
    }

    public static Vector3 Avoid(Vector3 velocity, Vector3 normal, float strength)
    {
        if (normal.LengthSquared() < 0.0001f) return velocity;
        normal = Vector3.Normalize(normal);
        float into = Vector3.Dot(velocity, -normal);
        return into > 0f ? velocity + normal * into * Math.Clamp(strength, 0f, 2f) : velocity;
    }
}

/// <summary>World-query-driven needs/utility/steering system for authored wildlife components.</summary>
public sealed class WildlifeSystem : IEcsSystem
{
    private readonly RuntimeScene _scene;
    public WildlifeSystem(RuntimeScene scene) => _scene = scene ?? throw new ArgumentNullException(nameof(scene));
    public int Priority => 160;

    public void Update(Genesis.Shared.ECS.IEcsWorld world, float dt)
    {
        IWorldQuery query = _scene.WorldQuery;
        if (query == null || dt <= 0f) return;
        world.Query<TransformComponent, WildlifeComponent, AgentComponent>((Entity entity, ref TransformComponent transform,
            ref WildlifeComponent wildlife, ref AgentComponent agent) =>
        {
            float step = Math.Clamp(dt, 0f, 0.1f);
            wildlife.Hunger = Math.Clamp(wildlife.Hunger + MathF.Max(0f, wildlife.HungerRate) * step, 0f, 1f);
            wildlife.Thirst = Math.Clamp(wildlife.Thirst + MathF.Max(0f, wildlife.ThirstRate) * step, 0f, 1f);
            wildlife.Fear = Math.Clamp(wildlife.Fear - step * 0.08f, 0f, 1f);
            wildlife.Energy = Math.Clamp(wildlife.Energy - step * (agent.Velocity.Length() * 0.003f), 0f, 1f);
            agent.DecisionTimer -= step;
            Vector3 position = new(transform.X, transform.Y, transform.Z);
            if (agent.DecisionTimer <= 0f || Vector3.DistanceSquared(position, agent.Target) <= agent.ArrivalRadius * agent.ArrivalRadius)
                ChooseActivity(entity.Id, query, position, ref wildlife, ref agent);

            Vector3 target = agent.Target;
            TerrainPathSample path = query.SamplePath(position.X, position.Z);
            if (path.IsValid && path.Distance < MathF.Max(path.Width * 1.5f, 3f) && agent.Activity is AgentActivity.Wander or AgentActivity.SeekFood)
                target = Vector3.Lerp(target, new Vector3(path.ClosestPoint.X, target.Y, path.ClosestPoint.Z), 0.22f);
            float speed = MathF.Max(0.1f, agent.MaxSpeed) * (agent.Activity == AgentActivity.Flee ? 1.45f : 1f);
            agent.Velocity = Steering.Arrive(position, target, agent.Velocity, speed,
                MathF.Max(0.1f, agent.Acceleration), MathF.Max(0.2f, agent.ArrivalRadius), step);
            agent.Velocity.Y = 0f;
            position += agent.Velocity * step;

            WorldBounds bounds = query.Manifest.Bounds;
            position.X = Math.Clamp(position.X, bounds.Minimum.X, bounds.Maximum.X);
            position.Z = Math.Clamp(position.Z, bounds.Minimum.Y, bounds.Maximum.Y);
            TerrainSurfaceSample surface = query.SampleTerrain(position.X, position.Z);
            if (wildlife.Preset != WildlifePreset.Bird)
                position.Y = wildlife.Preset == WildlifePreset.Aquatic
                    ? query.WaterSurfaceHeightAt(position.X, position.Z) ?? surface.Height
                    : surface.Height;
            else
                position.Y = MathF.Max(position.Y, surface.Height + 8f);
            transform.X = position.X; transform.Y = position.Y; transform.Z = position.Z;

            if (agent.Activity == AgentActivity.SeekWater && query.WaterDepthAt(position.X, position.Z) > 0.02f)
                wildlife.Thirst = Math.Clamp(wildlife.Thirst - step * 0.55f, 0f, 1f);
            if (agent.Activity == AgentActivity.SeekFood && Vector3.DistanceSquared(position, agent.Target) < 4f)
                wildlife.Hunger = Math.Clamp(wildlife.Hunger - step * 0.35f, 0f, 1f);
            if (agent.Activity == AgentActivity.Rest)
                wildlife.Energy = Math.Clamp(wildlife.Energy + step * 0.2f, 0f, 1f);
        });
    }

    private static void ChooseActivity(int entityId, IWorldQuery query, Vector3 position,
        ref WildlifeComponent wildlife, ref AgentComponent agent)
    {
        AgentActivity activity = UtilitySelection.Choose(new[]
        {
            new UtilityOption<AgentActivity>(AgentActivity.Flee, wildlife.Fear * 1.6f, 0),
            new UtilityOption<AgentActivity>(AgentActivity.SeekWater, wildlife.Thirst * 1.25f, 1),
            new UtilityOption<AgentActivity>(AgentActivity.SeekFood, wildlife.Hunger, 2),
            new UtilityOption<AgentActivity>(AgentActivity.Rest, (1f - wildlife.Energy) * 0.8f, 3),
            new UtilityOption<AgentActivity>(AgentActivity.Wander, 0.28f, 4),
        }, AgentActivity.Wander);
        agent.Activity = activity;
        agent.DecisionCounter++;
        agent.DecisionTimer = 1.4f + Hash01(entityId, agent.DecisionCounter, wildlife.Seed) * 2.6f;
        agent.ArrivalRadius = agent.ArrivalRadius <= 0f ? 1.2f : agent.ArrivalRadius;
        agent.MaxSpeed = agent.MaxSpeed <= 0f ? PresetSpeed(wildlife.Preset) : agent.MaxSpeed;
        agent.Acceleration = agent.Acceleration <= 0f ? agent.MaxSpeed * 2.4f : agent.Acceleration;
        agent.Target = activity switch
        {
            AgentActivity.SeekWater => NearestWater(query, position) ?? WanderTarget(query, wildlife.Home, wildlife.HomeRadius, entityId, agent.DecisionCounter, wildlife.Seed),
            AgentActivity.SeekFood => NearestFood(query, position, wildlife.AwarenessRadius) ?? WanderTarget(query, wildlife.Home, wildlife.HomeRadius, entityId, agent.DecisionCounter, wildlife.Seed),
            AgentActivity.Rest => wildlife.Home,
            AgentActivity.Flee => WanderTarget(query, position, MathF.Max(12f, wildlife.HomeRadius * 0.5f), entityId, agent.DecisionCounter + 91, wildlife.Seed),
            _ => WanderTarget(query, wildlife.Home, wildlife.HomeRadius, entityId, agent.DecisionCounter, wildlife.Seed),
        };
    }

    private static Vector3? NearestWater(IWorldQuery query, Vector3 position)
    {
        TerrainWaterDefinition water = query.Manifest.WaterBodies
            .OrderBy(candidate => Vector3.DistanceSquared(candidate.Center, position)).FirstOrDefault();
        return water == null ? null : new Vector3(water.Center.X, water.SurfaceHeight, water.Center.Z);
    }

    private static Vector3? NearestFood(IWorldQuery query, Vector3 position, float radius)
    {
        WorldPointOfInterest poi = query.PointsNear(position, MathF.Max(8f, radius))
            .FirstOrDefault(candidate => candidate.Category.Contains("food", StringComparison.OrdinalIgnoreCase)
                || candidate.Category.Contains("graze", StringComparison.OrdinalIgnoreCase)
                || candidate.Category.Contains("forage", StringComparison.OrdinalIgnoreCase));
        return poi?.Position;
    }

    private static Vector3 WanderTarget(IWorldQuery query, Vector3 home, float radius, int entityId, int decision, int seed)
    {
        float r = MathF.Max(4f, radius);
        float angle = Hash01(entityId, decision, seed) * MathF.Tau;
        float distance = MathF.Sqrt(Hash01(entityId + 17, decision + 31, seed)) * r;
        float x = home.X + MathF.Cos(angle) * distance;
        float z = home.Z + MathF.Sin(angle) * distance;
        WorldBounds bounds = query.Manifest.Bounds;
        x = Math.Clamp(x, bounds.Minimum.X, bounds.Maximum.X);
        z = Math.Clamp(z, bounds.Minimum.Y, bounds.Maximum.Y);
        return new Vector3(x, query.SampleTerrain(x, z).Height, z);
    }

    private static float PresetSpeed(WildlifePreset preset) => preset switch
    {
        WildlifePreset.Predator => 6.5f,
        WildlifePreset.Bird => 8f,
        WildlifePreset.Aquatic => 3.2f,
        WildlifePreset.Grazer => 3.8f,
        _ => 4.2f,
    };

    private static float Hash01(int a, int b, int seed)
    {
        uint h = unchecked((uint)(a * 374761393 + b * 668265263 + seed * 69069));
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }
}
