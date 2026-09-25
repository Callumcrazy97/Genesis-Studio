using System.Numerics;

namespace Genesis.Shared.Assets
{
    /// <summary>Serializable physics world settings (JSON-ready).</summary>
    public sealed class PhysicsWorldAsset
    {
        /// <summary>Multiplier on standard gravity (9.81 m/s²). 1 = Earth-like.</summary>
        public float GravityStrength { get; set; } = 1f;

        /// <summary>World-space gravity direction (normalized at use time).</summary>
        public Vector3 GravityDirection { get; set; } = new(0f, -1f, 0f);

        /// <summary>Assigned to bodies whose weight is unset (≤ 0).</summary>
        public float DefaultWeight { get; set; } = 1f;

        /// <summary>Air-resistance strength. Heavier bodies retain momentum longer.</summary>
        public float AirDrag { get; set; } = 0.15f;

        /// <summary>Maximum linear speed for dynamic bodies (m/s). 0 disables clamping.</summary>
        public float MaxVelocity { get; set; } = 45f;

        /// <summary>When false, bodies stay awake so stacks keep colliding.</summary>
        public bool AllowSleep { get; set; }

        public float SleepThreshold { get; set; } = 0.25f;

        /// <summary>Contact solver iterations (used when the world is created).</summary>
        public int SolverIterations { get; set; } = 12;

        /// <summary>Maximum depenetration speed for contact recovery.</summary>
        public float RecoveryVelocity { get; set; } = 4f;
    }
}
