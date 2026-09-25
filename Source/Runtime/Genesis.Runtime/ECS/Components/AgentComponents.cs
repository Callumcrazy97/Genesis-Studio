using System.Numerics;
using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components;

public enum WildlifePreset : byte
{
    Grazer,
    Forager,
    Predator,
    Aquatic,
    Bird,
}

public enum AgentActivity : byte
{
    Idle,
    Wander,
    SeekFood,
    SeekWater,
    Flee,
    Hunt,
    Rest,
}

/// <summary>Reusable locomotion/decision state shared by authored NPCs and procedural wildlife.</summary>
public struct AgentComponent : IComponent
{
    public Vector3 Velocity;
    public Vector3 Target;
    public float MaxSpeed;
    public float Acceleration;
    public float ArrivalRadius;
    public float DecisionTimer;
    public int DecisionCounter;
    public AgentActivity Activity;
}

/// <summary>Small, serialisable wildlife model. Needs are normalised to 0..1.</summary>
public struct WildlifeComponent : IComponent
{
    public WildlifePreset Preset;
    public Vector3 Home;
    public float HomeRadius;
    public float Hunger;
    public float Thirst;
    public float Fear;
    public float Energy;
    public float HungerRate;
    public float ThirstRate;
    public float AwarenessRadius;
    public int Seed;
}
