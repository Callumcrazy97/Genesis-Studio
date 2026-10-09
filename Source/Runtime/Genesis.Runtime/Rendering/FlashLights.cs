using System;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Short point lights a game fires and forgets (PGSL <c>LightFlash</c>): a muzzle flash, an
    /// explosion, a spark. Each starts at full brightness, fades to nothing over its life and then
    /// removes itself. They are given to the renderer every frame as flash lights
    /// (<see cref="IRenderController.AddFlashLight"/>): lit like any point light, never shadowed.
    /// </summary>
    /// <remarks>
    /// Ages follow game time: the host advances them once a frame before the game's update (so a
    /// flash added in a Step event is drawn at full brightness in that frame, however short its
    /// life), and not while the game is paused. A room change or a new game clears them.
    /// </remarks>
    public static class FlashLights
    {
        /// <summary>Flashes alive at once; a new one past this replaces the one closest to its end.</summary>
        public const int MaxFlashes = 256;

        /// <summary>The longest life a flash may have, in seconds; a longer light is a lamp.</summary>
        public const float MaxLife = 60f;

        private struct Flash
        {
            public int Id;
            public Vector3 Position;
            public Vector3 Color;
            public float Intensity, Radius, Falloff, Life, Age;
        }

        private static readonly object Gate = new();
        private static readonly Flash[] Active = new Flash[MaxFlashes];
        private static int _count;
        private static int _nextId;

        /// <summary>Flashes alive now.</summary>
        public static int Count
        {
            get { lock (Gate) return _count; }
        }

        /// <summary>
        /// A new flash; its id, or 0 when nothing would be lit (no life, radius or brightness, or a
        /// number that is not finite).
        /// </summary>
        public static int Add(Vector3 position, Vector3 color, float intensity, float radius, float life, float falloff = 2f)
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
                || !float.IsFinite(intensity) || !float.IsFinite(radius) || !float.IsFinite(life)
                || intensity <= 0f || radius <= 0f || life <= 0f)
                return 0;
            if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z)) color = Vector3.One;
            lock (Gate)
            {
                int slot = _count < MaxFlashes ? _count++ : NearestToEnd();
                int id = ++_nextId;
                if (id <= 0) id = _nextId = 1;
                Active[slot] = new Flash
                {
                    Id = id,
                    Position = position,
                    Color = Vector3.Clamp(color, Vector3.Zero, Vector3.One),
                    Intensity = MathF.Min(intensity, 10000f),
                    Radius = Math.Clamp(radius, 0.01f, 1000f),
                    Falloff = float.IsFinite(falloff) ? Math.Clamp(falloff, 0.05f, 16f) : 2f,
                    Life = MathF.Min(life, MaxLife),
                    Age = 0f,
                };
                return id;
            }
        }

        /// <summary>Moves a flash (one that follows a moving muzzle); false when it is gone.</summary>
        public static bool Move(int id, Vector3 position)
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return false;
            lock (Gate)
            {
                int index = IndexOf(id);
                if (index < 0) return false;
                Active[index].Position = position;
                return true;
            }
        }

        /// <summary>Puts a flash out at once; false when it is gone already.</summary>
        public static bool Remove(int id)
        {
            lock (Gate)
            {
                int index = IndexOf(id);
                if (index < 0) return false;
                Active[index] = Active[--_count];
                return true;
            }
        }

        /// <summary>Whether a flash is still alive.</summary>
        public static bool Exists(int id)
        {
            lock (Gate) return IndexOf(id) >= 0;
        }

        /// <summary>Puts every flash out (a room change).</summary>
        public static void Clear()
        {
            lock (Gate) _count = 0;
        }

        /// <summary>Puts every flash out and starts ids again (a new game).</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _count = 0;
                _nextId = 0;
            }
        }

        /// <summary>How bright a flash is at an age, from 1 when it starts to 0 at the end of its life.</summary>
        public static float Brightness(float age, float life)
        {
            if (!(life > 0f) || !(age < life)) return 0f;
            float left = 1f - MathF.Max(age, 0f) / life;
            return left * left;
        }

        /// <summary>Ages every flash by the game time a frame took and removes those that have burnt out.</summary>
        public static void Advance(float seconds)
        {
            if (!float.IsFinite(seconds) || seconds <= 0f) return;
            lock (Gate)
            {
                for (int i = _count - 1; i >= 0; i--)
                {
                    Active[i].Age += seconds;
                    if (Active[i].Age >= Active[i].Life) Active[i] = Active[--_count];
                }
            }
        }

        /// <summary>Gives the renderer this frame's flashes; returns how many.</summary>
        public static int Submit(IRenderController renderer)
        {
            if (renderer == null) return 0;
            lock (Gate)
            {
                int submitted = 0;
                for (int i = 0; i < _count; i++)
                {
                    ref readonly Flash flash = ref Active[i];
                    float brightness = Brightness(flash.Age, flash.Life);
                    if (brightness <= 0f) continue;
                    renderer.AddFlashLight(flash.Position, flash.Color, flash.Radius, flash.Intensity * brightness, flash.Falloff);
                    submitted++;
                }
                return submitted;
            }
        }

        private static int IndexOf(int id)
        {
            if (id <= 0) return -1;
            for (int i = 0; i < _count; i++)
                if (Active[i].Id == id) return i;
            return -1;
        }

        private static int NearestToEnd()
        {
            int nearest = 0;
            float least = float.MaxValue;
            for (int i = 0; i < _count; i++)
            {
                float left = Active[i].Life - Active[i].Age;
                if (left < least)
                {
                    least = left;
                    nearest = i;
                }
            }
            return nearest;
        }
    }
}
