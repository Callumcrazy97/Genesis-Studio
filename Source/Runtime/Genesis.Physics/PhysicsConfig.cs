using System;

namespace Genesis.Physics;

public sealed class PhysicsConfig
{
    public string BodyType { get; set; } = "Dynamic";
    public double Density { get; set; } = 1.0;
    public double Friction { get; set; } = 0.5;
    public double Restitution { get; set; } = 0.1;
    public bool IsSensor { get; set; }
    public string Notes { get; set; } = string.Empty;

    // Phase 4 scaling defaults.
    public bool EnableThreadDispatcher { get; set; } = true;
    public int DispatcherThreadCount { get; set; } = Math.Max(1, Environment.ProcessorCount);
    public bool AllowSleep { get; set; } = true;
    public float SleepThreshold { get; set; } = 0.25f;
    public float AirDrag { get; set; } = 0.15f;
    public float MaxVelocity { get; set; } = 45f;
    public float GravityStrength { get; set; } = 1f;
}
