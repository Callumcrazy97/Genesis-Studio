using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    public struct AudioComponent : IComponent
    {
        /// <summary>Project-relative <c>.audio.json</c> resource.</summary>
        public string Asset;
        public int   SoundIndex;  // index into the sound registry; -1 = none
        public float Volume;      // 0–1
        public float Pitch;       // 1.0 = normal
        public bool  AutoPlay;
        public bool  Spatial;
        public bool  Looping;
        public bool  Playing;
    }
}
