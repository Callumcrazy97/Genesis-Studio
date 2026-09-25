using System;

namespace Genesis.World.Water
{
    /// <summary>
    /// AF3.1 conserved indoor fill: volume → free-surface height from a horizontal cross-section
    /// curve (tapered cups, baths). Simpson integration avoids a voxel/mesh volume. Ported from
    /// Aetherforge Fluid lab <c>Reservoir</c> — no marching cubes.
    /// </summary>
    public sealed class Reservoir
    {
        private readonly float _baseY;
        private readonly float _maxHeight;
        private readonly Func<float, float> _areaAtLevel;
        private float _volume;

        public Reservoir(float baseY, float maxHeight, Func<float, float> areaAtLevel, float initialLevel)
        {
            if (maxHeight <= 0f) throw new ArgumentOutOfRangeException(nameof(maxHeight));
            _baseY = baseY;
            _maxHeight = maxHeight;
            _areaAtLevel = areaAtLevel ?? throw new ArgumentNullException(nameof(areaAtLevel));
            SetLevel(initialLevel);
        }

        public float BaseY => _baseY;
        public float MaxLevel => _baseY + _maxHeight;
        public float MaxHeight => _maxHeight;
        public float Volume => _volume;
        public float Level { get; private set; }
        public float HeadAbove(float y) => MathF.Max(0f, Level - y);

        public void Reset(float level) => SetLevel(level);

        public void AddVolume(float cubicMetres)
        {
            _volume = MathF.Max(0f, _volume + cubicMetres);
            SolveLevel();
        }

        public float RemoveVolume(float cubicMetres)
        {
            float removed = MathF.Min(_volume, MathF.Max(0f, cubicMetres));
            _volume -= removed;
            SolveLevel();
            return removed;
        }

        public float OverflowVolume()
        {
            float maxVolume = VolumeAtHeight(_maxHeight);
            return MathF.Max(0f, _volume - maxVolume);
        }

        public float RemoveOverflow()
        {
            float overflow = OverflowVolume();
            if (overflow > 0f)
            {
                _volume -= overflow;
                SolveLevel();
            }

            return overflow;
        }

        private void SetLevel(float level)
        {
            float height = Math.Clamp(level - _baseY, 0f, _maxHeight);
            _volume = VolumeAtHeight(height);
            Level = _baseY + height;
        }

        private void SolveLevel()
        {
            float lo = 0f;
            float hi = _maxHeight;
            for (int i = 0; i < 20; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (VolumeAtHeight(mid) < _volume) lo = mid;
                else hi = mid;
            }

            Level = _baseY + (lo + hi) * 0.5f;
        }

        private float VolumeAtHeight(float height)
        {
            height = Math.Clamp(height, 0f, _maxHeight);
            // Simpson integration handles tapered cups and rounded tubs without a mesh volume.
            const int steps = 24;
            float h = height / steps;
            if (h <= 0f) return 0f;
            float sum = _areaAtLevel(0f) + _areaAtLevel(height);
            for (int i = 1; i < steps; i++)
            {
                float y = i * h;
                sum += _areaAtLevel(y) * (i % 2 == 0 ? 2f : 4f);
            }

            return sum * h / 3f;
        }
    }
}
