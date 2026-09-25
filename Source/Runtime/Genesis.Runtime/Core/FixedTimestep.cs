using System;

namespace Genesis.Runtime.Core
{
    public sealed class FixedTimestep
    {
        public float FixedDelta { get; set; } = 1f / 60f;
        public int   MaxStepsPerFrame { get; set; } = 8;

        public float InterpolationAlpha { get; private set; }

        private float _accumulator;

        public int Advance(float frameDelta)
        {
            frameDelta = Math.Clamp(frameDelta, 0f, 0.25f);
            _accumulator += frameDelta;

            int steps = 0;
            while (_accumulator >= FixedDelta && steps < MaxStepsPerFrame)
            {
                _accumulator -= FixedDelta;
                steps++;
            }

            InterpolationAlpha = FixedDelta > 0f ? _accumulator / FixedDelta : 0f;
            return steps;
        }

        public void Reset() => _accumulator = 0f;
    }
}
