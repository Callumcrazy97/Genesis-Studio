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
    }
}
