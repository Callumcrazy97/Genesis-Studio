using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    public struct ParticleComponent : IComponent
    {
        /// <summary>Project-relative <c>.particle.json</c> resource used by the runtime simulation.</summary>
        public string Asset;
        public int   ParticleTypeId;  // index into the particle-type registry; -1 = none
        public float EmitRate;        // particles spawned per second
        public bool  HasEmitRateOverride; // distinguishes an explicit scripted zero from the asset's rate
        public float RateScale;       // multiplier applied to the asset's authored rate
        public bool  FollowEntity;    // new particles originate at the owning entity
        public bool  Emitting;
        /// <summary>Set to fire a one-off burst again; the engine clears it once it has.</summary>
        public bool  Restart;
        /// <summary>The entity is destroyed once the effect has played out. See Particles.ParticleBursts.</summary>
        public bool  RemoveWhenDone;
        /// <summary>With <see cref="RemoveWhenDone"/>: how long a continuous effect emits before it is left to die away.</summary>
        public float EmitSeconds;
        /// <summary>Seconds since the effect began, or was last restarted.</summary>
        public float Age;
        /// <summary>
        /// The wind this emitter's particles drift on, in metres a second along X and Z, in place
        /// of the wind the effect was authored with. Null leaves the effect as authored. The
        /// engine sets it on the weather it draws, so rain and snow lean with the room's wind.
        /// </summary>
        public System.Numerics.Vector2? Wind;
        /// <summary>
        /// The emitter's look changed as the game plays (Engine.Particle.Emitter): its particles'
        /// size, speed, life and gravity times the effect's own, and a colour its colours are
        /// multiplied by. Null leaves the effect as authored.
        /// </summary>
        public ParticleLook? Look;
    }

    /// <summary>An emitter's look against its effect's own: each 1 (and a white tint) leaves it as authored.</summary>
    public readonly record struct ParticleLook(float Size, float Speed, float Life, float Gravity, System.Numerics.Vector3 Tint)
    {
        public static ParticleLook AsAuthored => new(1f, 1f, 1f, 1f, System.Numerics.Vector3.One);
    }

    /// <summary>
    /// How many particles every effect makes, 0.1 to 1 of what it was authored with (1 unless set;
    /// Engine.Particle.Quality): fewer on a slow machine, each effect still recognisable.
    /// </summary>
    public static class ParticleQuality
    {
        private static float _amount = 1f;

        public static float Amount
        {
            get => _amount;
            set => _amount = float.IsFinite(value) ? System.Math.Clamp(value, 0.1f, 1f) : 1f;
        }

        /// <summary>Whether effects run and are drawn (Engine.Particle.Enabled); off, each stops where it is.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Whether new one-off bursts start (Engine.Particle.Bursts.Enabled).</summary>
        public static bool Bursts { get; set; } = true;

        /// <summary>Whether a 2D room's particles start and are drawn (Engine.Particle.Particles2D.Enabled).</summary>
        public static bool Particles2D { get; set; } = true;

        /// <summary>Whether effects' particles collide with the ground and geometry (Engine.Particle.Gpu.Collision.Enabled).</summary>
        public static bool Collisions { get; set; } = true;

        public static void Reset()
        {
            _amount = 1f;
            Enabled = Bursts = Particles2D = Collisions = true;
        }
    }
}
