using System;
using System.Numerics;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Debugger
{
    /// <summary>
    /// High-precision rolling frame-time history and visual sparkline renderer for the debug overlay.
    /// Tracks micro-stutters, GC spikes, and target budget baselines without allocating memory.
    /// </summary>
    public sealed class DebugFrameGraph
    {
        public const int DefaultCapacity = 120;

        private readonly float[] _samples;
        private int _head;
        private int _count;

        public float CurrentMs { get; private set; }
        public float MinMs { get; private set; }
        public float MaxMs { get; private set; }
        public float AvgMs { get; private set; }
        public float SmoothedFps { get; private set; }

        public DebugFrameGraph(int capacity = DefaultCapacity)
        {
            _samples = new float[Math.Max(30, capacity)];
        }

        public void Record(float dtSeconds)
        {
            float ms = dtSeconds * 1000f;
            CurrentMs = ms;
            _samples[_head] = ms;
            _head = (_head + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;

            // Calculate min, max, average
            float min = float.MaxValue;
            float max = float.MinValue;
            float sum = 0f;

            for (int i = 0; i < _count; i++)
            {
                float v = _samples[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }

            MinMs = min <= 0f ? 0f : min;
            MaxMs = max <= 0f ? 0f : max;
            AvgMs = _count > 0 ? sum / _count : 0f;
            SmoothedFps = AvgMs > 0.001f ? 1000f / AvgMs : 0f;
        }

        /// <summary>
        /// Draws a sparkline graph into the given HUD canvas.
        /// </summary>
        public void Draw(IHudCanvas hud, float x, float y, float width, float height)
        {
            if (hud == null || _count == 0 || width <= 10f || height <= 10f) return;

            // Background panel
            hud.Rect(x, y, width, height, new Vector4(0.06f, 0.08f, 0.12f, 0.88f), filled: true);

            // Frame budget reference lines: 16.67ms (60fps) and 33.33ms (30fps)
            float maxScale = MathF.Max(35f, MaxMs * 1.15f);
            float y60 = y + height - (16.667f / maxScale * height);
            float y30 = y + height - (33.333f / maxScale * height);

            if (y60 > y && y60 < y + height)
            {
                hud.Rect(x, y60, width, 1f, new Vector4(0.35f, 0.85f, 0.45f, 0.4f), filled: true);
                hud.Text("60 FPS", x + width - 42f, y60 - 10f, 9f, new Vector4(0.35f, 0.85f, 0.45f, 0.8f));
            }

            if (y30 > y && y30 < y + height)
            {
                hud.Rect(x, y30, width, 1f, new Vector4(0.95f, 0.75f, 0.25f, 0.4f), filled: true);
                hud.Text("30 FPS", x + width - 42f, y30 - 10f, 9f, new Vector4(0.95f, 0.75f, 0.25f, 0.8f));
            }

            // Draw bars
            float barWidth = MathF.Max(1.5f, (width - 8f) / _samples.Length);
            float graphBottom = y + height - 2f;
            float graphStartX = x + 4f;

            for (int i = 0; i < _count; i++)
            {
                int sampleIndex = (_head - _count + i + _samples.Length) % _samples.Length;
                float sampleMs = _samples[sampleIndex];

                float barHeight = Math.Clamp((sampleMs / maxScale) * (height - 6f), 1f, height - 4f);
                float barX = graphStartX + (i * barWidth);
                float barY = graphBottom - barHeight;

                Vector4 barColor = sampleMs <= 16.7f
                    ? new Vector4(0.35f, 0.85f, 0.45f, 0.85f)
                    : sampleMs <= 33.4f
                        ? new Vector4(0.95f, 0.75f, 0.25f, 0.85f)
                        : new Vector4(0.95f, 0.35f, 0.3f, 0.95f);

                hud.Rect(barX, barY, MathF.Max(1f, barWidth - 0.5f), barHeight, barColor, filled: true);
            }

            // Summary text footer
            string summary = $"Avg: {AvgMs:F1}ms  Min: {MinMs:F1}ms  Max: {MaxMs:F1}ms";
            hud.Text(summary, x + 6f, y + 4f, 10f, new Vector4(0.85f, 0.9f, 0.95f, 0.9f));
        }
    }
}
