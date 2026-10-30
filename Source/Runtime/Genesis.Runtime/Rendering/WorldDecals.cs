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
            public bool SeeThrough;
            public int Layer;
            public string NormalImage, RoughnessImage;
            // The object it stays on (an entity id, 0 for none), and where it sits in that object's frame.
            public int Follow;
            public Vector3 LocalPosition, LocalNormal;
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

        /// <summary>Whether decals are drawn at all (Engine.Decal.Enabled); kept while off. On at each room's start.</summary>
        public static bool Enabled { get; set; } = true;

        private static int _defaultLayer;

        /// <summary>The layer a new decal gets (Engine.Decal.Layer), 0 at each room's start.</summary>
        public static int DefaultLayer
        {
            get => _defaultLayer;
            set => _defaultLayer = Math.Clamp(value, 0, MeshDrawCall.DecalLayerCount - 1);
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
                    Layer = _defaultLayer,
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

        /// <summary>
        /// Puts a decal on a see-through surface (glass): drawn as a flat picture in its own plane
        /// after the see-through draws instead of projected onto the opaque world (see
        /// <see cref="DecalDrawCall.SeeThrough"/>); false when there is no such decal.
        /// </summary>
        public static bool SetSeeThrough(int id, bool seeThrough) => Edit(id, decal => decal.SeeThrough = seeThrough);

        /// <summary>
        /// The decal's layer (0 to <see cref="MeshDrawCall.DecalLayerCount"/> - 1): it shows only on
        /// surfaces that take that layer (Engine.Model.DecalLayers). False for a layer out of range.
        /// </summary>
        public static bool SetLayer(int id, int layer) =>
            layer >= 0 && layer < MeshDrawCall.DecalLayerCount && Edit(id, decal => decal.Layer = layer);

        /// <summary>A normal map for the decal (an Image resource or a TextureCreate number; empty for none).</summary>
        public static bool SetNormalMap(int id, string image) => Edit(id, decal => decal.NormalImage = image?.Trim() ?? string.Empty);

        /// <summary>A roughness map for the decal (red: 0 smooth to 1 matte; empty for none).</summary>
        public static bool SetRoughnessMap(int id, string image) => Edit(id, decal => decal.RoughnessImage = image?.Trim() ?? string.Empty);

        /// <summary>A decal's normal and roughness map names (empty for none); false when there is no such decal.</summary>
        public static bool TryGetMaps(int id, out string normalMap, out string roughnessMap)
        {
            lock (Gate)
            {
                int index = IndexOf(id);
                normalMap = index < 0 ? string.Empty : Decals[index].NormalImage ?? string.Empty;
                roughnessMap = index < 0 ? string.Empty : Decals[index].RoughnessImage ?? string.Empty;
                return index >= 0;
            }
        }

        /// <summary>A decal's layer, or -1 when there is no such decal.</summary>
        public static int LayerOf(int id)
        {
            lock (Gate)
            {
                int index = IndexOf(id);
                return index < 0 ? -1 : Decals[index].Layer;
            }
        }

        /// <summary>Whether a decal is drawn on see-through surfaces; false when there is no such decal.</summary>
        public static bool IsSeeThrough(int id)
        {
            lock (Gate)
            {
                int index = IndexOf(id);
                return index >= 0 && Decals[index].SeeThrough;
            }
        }

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

        /// <summary>
        /// Keeps a decal on an object that moves (a door, a cart, a crate): from now on it is placed
        /// from the object's transform each frame by <see cref="UpdateFollowers"/>,
        /// and goes when the object does. False when there is no such decal or the transform is unusable.
        /// </summary>
        public static bool Follow(int id, int entityId, Matrix4x4 entityWorld)
        {
            if (entityId <= 0 || !Matrix4x4.Invert(entityWorld, out Matrix4x4 inverse)) return false;
            return Edit(id, decal =>
            {
                decal.Follow = entityId;
                decal.LocalPosition = Vector3.Transform(decal.Position, inverse);
                decal.LocalNormal = Vector3.TransformNormal(decal.Normal, inverse);
            });
        }

        /// <summary>
        /// Where an object is in the world, as a following decal needs it: its model's own transform
        /// (pivot and model scale included) when it draws a model, else its position, turns and scale.
        /// Null when the object is gone.
        /// </summary>
        public static Matrix4x4? ObjectWorld(Genesis.Runtime.ECS.World world, int entityId)
        {
            if (world == null || entityId <= 0) return null;
            Genesis.Shared.ECS.Entity entity = world.GetEntity(entityId);
            if (!world.IsAlive(entity) || !world.Has<Genesis.Runtime.ECS.Components.TransformComponent>(entity)) return null;
            ref Genesis.Runtime.ECS.Components.TransformComponent transform = ref world.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
            if (world.Has<Genesis.Runtime.ECS.Components.ModelRendererComponent>(entity))
            {
                // With the model's pivot, as it is drawn: a decal that followed the plain transform sat
                // a pivot's length away from a model drawn about its feet or its centre.
                ref Genesis.Runtime.ECS.Components.ModelRendererComponent model = ref world.GetRef<Genesis.Runtime.ECS.Components.ModelRendererComponent>(entity);
                Genesis.Runtime.Modeling.GModelAsset asset = string.IsNullOrWhiteSpace(model.ModelAsset) ? null
                    : Genesis.Runtime.Modeling.RuntimeModelAssetRegistry.Shared.Load(Genesis.Runtime.Scripting.PgslCommands.ProjectPath, model.ModelAsset);
                return Genesis.Runtime.Modeling.RuntimeModelRenderSystem.DrawMatrix(asset, transform, model);
            }
            float Scale(float value) => MathF.Abs(value) < 0.0001f ? 1f : value;
            const float radians = MathF.PI / 180f;
            return Matrix4x4.CreateScale(Scale(transform.ScaleX), Scale(transform.ScaleY), Scale(transform.ScaleZ))
                * Matrix4x4.CreateRotationX(transform.RotationX * radians)
                * Matrix4x4.CreateRotationY(transform.RotationY * radians)
                * Matrix4x4.CreateRotationZ((transform.RotationZ + transform.Rotation) * radians)
                * Matrix4x4.CreateTranslation(transform.X, transform.Y, transform.Z);
        }
        /// <summary>
        /// An object's world as its own script has it mid-event: a script's x, y and z reach the
        /// transform only when its event ends, so a decal it makes follow itself in the same event
        /// (DecalFollow in Create, after moving) is placed from where the script put it.
        /// </summary>
        public static Matrix4x4 AtScriptPosition(Matrix4x4 objectWorld, Vector3 transformPosition, Vector3 scriptPosition)
        {
            Vector3 shift = scriptPosition - transformPosition;
            if (Finite(shift)) objectWorld.Translation += shift;
            return objectWorld;
        }

        /// <summary>Stops a decal following its object; it stays where it is now.</summary>
        public static bool Unfollow(int id) => Edit(id, decal => decal.Follow = 0);

        /// <summary>
        /// Places every following decal from its object's transform this frame (null when the object
        /// is gone, which removes the decal). Called once a frame before <see cref="Submit"/>.
        /// </summary>
        public static void UpdateFollowers(Func<int, Matrix4x4?> worldOf)
        {
            if (worldOf == null) return;
            lock (Gate)
            {
                int kept = 0;
                for (int i = 0; i < Decals.Count; i++)
                {
                    Decal decal = Decals[i];
                    if (decal.Follow != 0)
                    {
                        if (worldOf(decal.Follow) is not { } world) continue;
                        Vector3 normal = Vector3.TransformNormal(decal.LocalNormal, world);
                        decal.Position = Vector3.Transform(decal.LocalPosition, world);
                        if (normal.LengthSquared() > 1e-10f) decal.Normal = Vector3.Normalize(normal);
                    }
                    Decals[kept++] = decal;
                }
                if (kept < Decals.Count) Decals.RemoveRange(kept, Decals.Count - kept);
            }
        }

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

        /// <summary>Where a decal is now and which way it faces; false when there is no such decal.</summary>
        public static bool TryGetPlacement(int id, out Vector3 position, out Vector3 normal)
        {
            lock (Gate)
            {
                int index = IndexOf(id);
                position = index >= 0 ? Decals[index].Position : default;
                normal = index >= 0 ? Decals[index].Normal : default;
                return index >= 0;
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
                _defaultLayer = 0;
                Enabled = true;
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
            if (renderer == null || !Enabled) return 0;
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
                        SeeThrough = decal.SeeThrough,
                        Layer = decal.Layer,
                        NormalMap = MapPicture(renderer, projectPath, decal.NormalImage),
                        RoughnessMap = MapPicture(renderer, projectPath, decal.RoughnessImage),
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

        // A normal or roughness map: data, not colours (an Image resource is read without decoding).
        // Kept by name for the frame like the pictures, under its own key.
        private static TextureHandle MapPicture(IRenderController renderer, string projectPath, string image)
        {
            if (string.IsNullOrEmpty(image)) return TextureHandle.Invalid;
            string key = "\u0001map:" + image;
            if (Pictures.TryGetValue(key, out TextureHandle known)) return known;
            TextureHandle map = TextureHandle.Invalid;
            if (double.TryParse(image, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && number >= 1 && number <= int.MaxValue && number == Math.Floor(number) && ScriptTextures.Exists((int)number))
                map = ScriptTextures.Resolve((int)number, renderer, out _);
            else
            {
                try
                {
                    map = ObjectDrawPass.ResolveImageTexture(renderer, projectPath ?? Genesis.Runtime.Scripting.PgslCommands.ProjectPath,
                        image, Genesis.Shared.Materials.TextureColorSpace.Linear);
                }
                catch (Exception)
                {
                    map = TextureHandle.Invalid;
                }
            }
            Pictures[key] = map;
            return map;
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
