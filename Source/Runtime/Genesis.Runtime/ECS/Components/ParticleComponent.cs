using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    public struct ParticleComponent : IComponent
    {
        /// <summary>Project-relative <c>.particle.json</c> resource used by the runtime simulation.</summary>
        public string Asset;
        public int   ParticleTypeId;  // index into the particle-type registry; -1 = none
        public float EmitRate;        // particles spawned per second
        public float RateScale;       // multiplier applied to the asset's authored rate
        public bool  FollowEntity;    // new particles originate at the owning entity
        public bool  Emitting;
    }
}
