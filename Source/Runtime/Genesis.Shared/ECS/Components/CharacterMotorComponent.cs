using Genesis.Shared.ECS;

namespace Genesis.Shared.ECS.Components;

public enum CharacterMotorState
{
    Falling,
    Grounded,
    Swimming,
}

/// <summary>Persistable authoring and live state for the runtime character motor.</summary>
public struct CharacterMotorComponent : IComponent
{
    public float WalkSpeed;
    public float SprintMultiplier;
    public float GroundAcceleration;
    public float AirAcceleration;
    public float JumpSpeed;
    public float StepHeight;
    public float MaximumSlopeDegrees;
    public float SwimSpeed;
    public float SwimAcceleration;
    public float SwimVerticalSpeed;
    public float SwimDrag;
    public float SurfaceOffset;
    public CharacterMotorState State;
    public float SubmergedFraction;
    public bool WasInWater;
    public bool EnteredWater;
    public bool ExitedWater;

    public static CharacterMotorComponent Default => new()
    {
        WalkSpeed = 8f,
        SprintMultiplier = 1.6f,
        GroundAcceleration = 48f,
        AirAcceleration = 16f,
        JumpSpeed = 6.5f,
        StepHeight = 0.35f,
        MaximumSlopeDegrees = 48f,
        SwimSpeed = 5f,
        SwimAcceleration = 18f,
        SwimVerticalSpeed = 4f,
        SwimDrag = 2.5f,
        SurfaceOffset = 0.15f,
        State = CharacterMotorState.Falling,
    };
}
