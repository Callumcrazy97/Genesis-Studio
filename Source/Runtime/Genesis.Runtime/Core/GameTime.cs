using System;

namespace Genesis.Runtime.Core
{
    public sealed class GameTime
    {
        public float Delta { get; private set; }
        public float Total { get; private set; }
        public long  FrameCount { get; private set; }

        public const float MaxDelta = 0.05f;

        public void Advance(float rawDeltaSeconds)
        {
            Delta = Math.Clamp(rawDeltaSeconds, 0.0001f, MaxDelta);
            Total += Delta;
            FrameCount++;
        }

        public void Step(float fixedDelta) => Advance(fixedDelta);
    }
}
