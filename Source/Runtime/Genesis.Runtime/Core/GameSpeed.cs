using System;

namespace Genesis.Runtime.Core
{
    /// <summary>
    /// How fast game time runs against real time. 1 is normal; 0.25 is slow motion; 0 holds
    /// physics, scripts' delta time and animation still while the game goes on drawing (a
    /// hit-stop). Input, sound and the network keep real time.
    /// </summary>
    public static class GameSpeed
    {
        /// <summary>The fastest game time may run.</summary>
        public const float Maximum = 8f;

        private static float _scale = 1f;

        /// <summary>The multiplier applied to every update's elapsed time, from 0 to <see cref="Maximum"/>.</summary>
        public static float Scale
        {
            get => _scale;
            set => _scale = float.IsFinite(value) ? Math.Clamp(value, 0f, Maximum) : 1f;
        }
    }
}
