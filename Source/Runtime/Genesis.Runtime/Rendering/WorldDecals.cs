using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Decals a game leaves on the world (PGSL <c>DecalAdd</c>): bullet holes, scorch marks, blood.
    /// Each is a picture projected onto whatever opaque surface lies around a point (walls, models,
    /// terrain), facing along a normal, and lasts until its life runs out (fading over the end of
    /// it), it is removed, or the limit makes room for a newer one by taking the oldest.
    /// </summary>
    /// <remarks>
    /// Decals stay where they were put in the world: one on a moving object stays behind unless the
    /// game moves it with <c>DecalMove</c> each frame. Ages follow game time (the host advances them
    /// once a frame, not while paused); a room change or a new game clears them. They are given to
    /// the renderer each frame (<see cref="IRenderController.DrawDecals"/>) with their pictures
    /// resolved there: a project Image resource by name, or a texture a script painted
    /// (<c>TextureCreate</c>) by its number.
    /// </remarks>
    public static class WorldDecals
    {
        public const int DefaultLimit = 256;
        public const int MaxLimit = ForwardDecalLimit;

        // Matches the renderer's per-frame cap.
        private const int ForwardDecalLimit = 4096;

        private sealed class Decal
        {
            public int Id;
            public string Image;
            public Vector3 Position;
            public Vector3 Normal;
            public float Width, Height, Depth;
            public float Rotation;
            public Vector4 Color = Vector4.One;
            public float Life;
            public float Fade = -1f;
            public float Age;
            public DecalBlend Blend;
        }

        private static readonly object Gate = new();
        private static readonly List<Decal> Decals = new();
        private static readonly Dictionary<string, TextureHandle> Pictures = new(StringComparer.Ordinal);
        private static DecalDrawCall[] _calls = new DecalDrawCall[64];
        private static int _limit = DefaultLimit;
        private static int _nextId;

        /// <summary>Decals in the world now.</summary>
        public static int Count
        {
            get { lock (Gate) return Decals.Count; }
        }

        /// <summary>How many decals may be in the world at once; adding past it removes the oldest.</summary>
        public static int Limit
        {
            get { lock (Gate) return _limit; }
        }

        /// <summary>
        /// A new decal: <paramref name="image"/> on the surface at <paramref name="position"/> facing
        /// <paramref name="normal"/>, <paramref name="size"/> across, lasting <paramref name="life"/>
        /// seconds (0 or less: until removed). Its id, or 0 when it could not be placed.
        /// </summary>
        public static int Add(string image, Vector3 position, Vector3 normal, float size, float life)
        {
            if (!Finite(position) || !Finite(normal) || normal.LengthSquared() < 1e-10f) return 0;
            if (!float.IsFinite(size) || size <= 0f) return 0;
            size = MathF.Min(size, 1000f);
            lock (Gate)
            {
                if (_limit <= 0) return 0;
                while (Decals.Count >= _limit) Decals.RemoveAt(0);
                int id = ++_nextId;
                if (id <= 0) id = _nextId = 1;
                Decals.Add(new Decal
                {
                    Id = id,
                    Image = image?.Trim() ?? string.Empty,
                    Position = position,
                    Normal = Vector3.Normalize(normal),
                    Width = size,
                    Height = size,
                    // Half as deep as it is wide: enough for the bumps of a wall or the ground,
                    // little enough not to wrap round a corner or onto feet passing through.
                    Depth = size * 0.5f,
                    Life = float.IsFinite(life) && life > 0f ? life : 0f,
                });
                return id;
            }
        }

        /// <summary>Tint (0-1 each, as picked) and opacity (0-1); false when there is no such decal.</summary>
        public static bool SetColor(int id, Vector3 color, float alpha) => Edit(id, decal =>
        {
            decal.Color = new Vector4(
                float.IsFinite(color.X) ? Math.Clamp(color.X, 0f, 1f) : 1f,
                float.IsFinite(color.Y) ? Math.Clamp(color.Y, 0f, 1f) : 1f,
                float.IsFinite(color.Z) ? Math.Clamp(color.Z, 0f, 1f) : 1f,
                float.IsFinite(alpha) ? Math.Clamp(alpha, 0f, 1f) : 1f);
        });

        /// <summary>Turns the picture about its normal, in degrees anticlockwise as seen from the front.</summary>
        public static bool SetRotation(int id, float degrees) =>
            float.IsFinite(degrees) && Edit(id, decal => decal.Rotation = degrees % 360f);

        /// <summary>Width and height of the picture and the depth of the box it is projected through.</summary>
        public static bool SetSize(int id, float width, float height, float depth) =>
            float.IsFinite(width) && float.IsFinite(height) && float.IsFinite(depth)
            && width > 0f && height > 0f && depth > 0f
            && Edit(id, decal =>
            {
                decal.Width = MathF.Min(width, 1000f);
                decal.Height = MathF.Min(height, 1000f);
                decal.Depth = MathF.Min(depth, 1000f);
            });

        public static bool SetBlend(int id, DecalBlend blend) => Edit(id, decal => decal.Blend = blend);

        /// <summary>How many seconds at the end of its life a decal takes to fade out; less than 0 uses the default.</summary>
        public static bool SetFade(int id, float seconds) =>
            float.IsFinite(seconds) && Edit(id, decal => decal.Fade = seconds < 0f ? -1f : seconds);

        /// <summary>Gives a decal a new life from now (0 or less: until removed).</summary>
        public static bool SetLife(int id, float seconds) =>
            float.IsFinite(seconds) && Edit(id, decal =>
            {
                decal.Life = seconds > 0f ? seconds : 0f;
                decal.Age = 0f;
            });

        /// <summary>Moves a decal (one that follows a moving object); false when there is no such decal.</summary>
        public static bool Move(int id, Vector3 position, Vector3 normal) =>
            Finite(position) && Finite(normal) && normal.LengthSquared() >= 1e-10f
            && Edit(id, decal =>
            {
                decal.Position = position;
                decal.Normal = Vector3.Normalize(normal);
            });

        public static bool Remove(int id)
        {
            lock (Gate)
            {
                int index = IndexOf(id);
                if (index < 0) return false;
                Decals.RemoveAt(index);
                return true;
            }
        }

        public static bool Exists(int id)
        {
            lock (Gate) return IndexOf(id) >= 0;
        }

        /// <summary>Removes every decal (a room change).</summary>
        public static void Clear()
        {
            lock (Gate) Decals.Clear();
        }

        /// <summary>Removes every decal, puts the limit back and starts ids again (a new game).</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                Decals.Clear();
                Pictures.Clear();
                _limit = DefaultLimit;
                _nextId = 0;
            }
        }

        /// <summary>How many decals may be in the world at once (0 to <see cref="MaxLimit"/>); the oldest go first.</summary>
        public static void SetLimit(int limit)
        {
            lock (Gate)
            {
                _limit = Math.Clamp(limit, 0, MaxLimit);
                int excess = Decals.Count - _limit;
                if (excess > 0) Decals.RemoveRange(0, excess);
            }
        }

        /// <summary>The default fade for a life: the last quarter of it, at most two seconds.</summary>
        public static float DefaultFade(float life) => life > 0f ? MathF.Min(2f, life * 0.25f) : 0f;

        /// <summary>How opaque a decal is at an age: 1 until its fade begins, then down to 0 at the end of its life.</summary>
        public static float Opacity(float age, float life, float fade)
        {
            if (!(life > 0f)) return 1f;
            float left = life - age;
            if (left <= 0f) return 0f;
            float span = fade < 0f ? DefaultFade(life) : MathF.Min(fade, life);
            return span > 0f ? Math.Clamp(left / span, 0f, 1f) : 1f;
        }

        /// <summary>Ages every decal by the game time a frame took and removes those whose life is over.</summary>
        public static void Advance(float seconds)
        {
            if (!float.IsFinite(seconds) || seconds <= 0f) return;
            lock (Gate)
            {
                int kept = 0;
                for (int i = 0; i < Decals.Count; i++)
                {
                    Decal decal = Decals[i];
                    decal.Age += seconds;
                    if (decal.Life > 0f && decal.Age >= decal.Life) continue;
                    Decals[kept++] = decal;
                }
                if (kept < Decals.Count) Decals.RemoveRange(kept, Decals.Count - kept);
            }
        }

        /// <summary>
        /// Gives the renderer this frame's decals, oldest first, with their pictures; returns how
        /// many. A decal whose picture cannot be found is left out.
        /// </summary>
        public static int Submit(IRenderController renderer, string projectPath)
        {
            if (renderer == null) return 0;
            lock (Gate)
            {
                if (Decals.Count == 0) return 0;
                if (_calls.Length < Decals.Count) _calls = new DecalDrawCall[Math.Max(Decals.Count, _calls.Length * 2)];
                Pictures.Clear();
                int count = 0;
                foreach (Decal decal in Decals)
                {
                    float opacity = decal.Color.W * Opacity(decal.Age, decal.Life, decal.Fade);
                    if (opacity <= 0f) continue;
                    if (!Pictures.TryGetValue(decal.Image, out TextureHandle picture))
                    {
                        picture = ResolvePicture(renderer, projectPath, decal.Image);
                        Pictures[decal.Image] = picture;
                    }
                    if (!picture.IsValid) continue;
                    _calls[count++] = new DecalDrawCall
                    {
                        Texture = picture,
                        Position = decal.Position,
                        Normal = decal.Normal,
                        Tangent = Tangent(decal.Normal, decal.Rotation),
                        Width = decal.Width,
                        Height = decal.Height,
                        Depth = decal.Depth,
                        Color = new Vector4(decal.Color.X, decal.Color.Y, decal.Color.Z, opacity),
                        Blend = decal.Blend,
                    };
                }
                Pictures.Clear();
                if (count > 0) renderer.DrawDecals(_calls.AsSpan(0, count));
                return count;
            }
        }

        /// <summary>
        /// The picture's left-to-right direction: level on a wall (its top is up), towards +X on a
        /// floor or ceiling, then turned by the rotation about the normal.
        /// </summary>
        public static Vector3 Tangent(Vector3 normal, float rotationDegrees)
        {
            Vector3 reference = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(reference, normal));
            if (rotationDegrees == 0f) return tangent;
            Vector3 bitangent = Vector3.Cross(normal, tangent);
            float radians = rotationDegrees * MathF.PI / 180f;
            return Vector3.Normalize(tangent * MathF.Cos(radians) + bitangent * MathF.Sin(radians));
        }

        private static TextureHandle ResolvePicture(IRenderController renderer, string projectPath, string image)
        {
            if (string.IsNullOrEmpty(image)) return TextureHandle.Invalid;
            // A number is a texture the script painted (TextureCreate); a name is an Image resource.
            if (double.TryParse(image, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && number >= 1 && number <= int.MaxValue && number == Math.Floor(number)
                && ScriptTextures.Exists((int)number))
                return ScriptTextures.Resolve((int)number, renderer, out _);
            try
            {
                return ObjectDrawPass.ResolveImageTexture(renderer, projectPath ?? Genesis.Runtime.Scripting.PgslCommands.ProjectPath, image);
            }
            catch (Exception)
            {
                return TextureHandle.Invalid;
            }
        }

        private static bool Edit(int id, Action<Decal> change)
        {
            lock (Gate)
            {
                int index = IndexOf(id);
                if (index < 0) return false;
                change(Decals[index]);
                return true;
            }
        }

        private static int IndexOf(int id)
        {
            if (id <= 0) return -1;
            // Newest last: the decal a script just added and now adjusts is found at once.
            for (int i = Decals.Count - 1; i >= 0; i--)
                if (Decals[i].Id == id) return i;
            return -1;
        }

        private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
