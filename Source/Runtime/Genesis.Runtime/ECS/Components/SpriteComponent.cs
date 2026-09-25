using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    public struct SpriteComponent : IComponent
    {
        public int   SpriteIndex;  // index into the sprite registry
        public int   ImageIndex;   // current animation frame
        public float ImageSpeed;   // playback speed multiplier (1 = authored timing)
        public float PlaybackElapsedMs;
        public int   AnimationTagIndex; // -1 = all frames, otherwise index into sprite tags
        /// <summary>-1 uses the tag's authored loop flag; 0/1 is a live PGSL override.</summary>
        public int   AnimationLoopOverride;
        public int   PlaybackStepDirection; // ping-pong direction within clip (+1 / -1)
        public float Alpha;        // 0–1
        public int   Depth;        // draw order; lower = drawn on top
    }
}
