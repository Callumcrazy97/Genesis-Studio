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

            // Past the step cap the backlog is dropped (the simulation slows down under overload)
            // rather than carried forward. Carrying it made every later frame run the maximum
            // number of steps and pushed the interpolation alpha above 1 into extrapolation.
            if (FixedDelta > 0f && _accumulator >= FixedDelta)
                _accumulator %= FixedDelta;

            InterpolationAlpha = FixedDelta > 0f ? Math.Clamp(_accumulator / FixedDelta, 0f, 1f) : 0f;
            return steps;
        }

        public void Reset() => _accumulator = 0f;
    }
}
