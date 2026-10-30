#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Modeling;

/// <summary>
/// One model instance's look-ats and reaches (Engine.Animation.LookAt, Engine.Animation.Reach): a
/// head that turns to a point or another instance within its yaw and pitch limits, a hand whose
/// arm bends to bring it to one, each blending in and out over its own time. They go on top of
/// whatever poses the model (a clip, a layer, an Animation Controller) and never replace it; a
/// model that has none is drawn exactly as before.
/// </summary>
/// <remarks>
/// Targets are worked out once a frame after the scripts have run (<see cref="UpdateScene"/>), in
/// the model's own space, so every draw and socket of that frame poses the same way.
/// </remarks>
public sealed class ModelBoneTargets
{
    public const string DefaultLookBone = "Head";
    public const string DefaultReachBone = "RightHand";
    public const float DefaultBlendSeconds = 0.25f;
    public const float DefaultYawLimit = 70f;
    public const float DefaultPitchLimit = 40f;

    /// <summary>One bone looking at, or reaching for, a point or an instance.</summary>
    public sealed class Aim
    {
        private float _from, _goal, _elapsed;

        internal Aim(string bone, bool look)
        {
            Bone = bone;
            IsLook = look;
        }

        /// <summary>The bone that looks, or the end of the chain that reaches (a hand).</summary>
        public string Bone { get; }
        public bool IsLook { get; }

        /// <summary>The instance aimed at; 0 aims at <see cref="Point"/>.</summary>
        public int TargetInstance { get; set; }

        /// <summary>A bone or socket of the target instance aimed at; "" aims at its position.</summary>
        public string TargetBone { get; set; } = "";

        /// <summary>The world point aimed at when there is no target instance.</summary>
        public Vector3 Point { get; set; }

        /// <summary>Metres added above the point or instance aimed at (eye height on an instance with no model).</summary>
        public float Height { get; set; }

        /// <summary>How much of the way the bone turns once blended in, 0 to 1.</summary>
        public float Weight { get; set; } = 1f;

        /// <summary>Seconds a change of weight, on or off takes.</summary>
        public float BlendSeconds { get; set; } = DefaultBlendSeconds;

        /// <summary>Degrees a looking bone may turn across from the way the pose faces it.</summary>
        public float YawLimit { get; set; } = DefaultYawLimit;

        /// <summary>Degrees a looking bone may turn up or down.</summary>
        public float PitchLimit { get; set; } = DefaultPitchLimit;

        /// <summary>Whether it is blending towards <see cref="Weight"/>; off blends it out, and then it goes.</summary>
        public bool On { get; set; }

        /// <summary>How much it turns the bone now, as blended (0 to 1).</summary>
        public float Amount { get; private set; }

        /// <summary>The point in the model's own space, as last worked out; valid while <see cref="Resolved"/>.</summary>
        internal Vector3 ModelPoint;
        internal bool Resolved;
        internal Vector3? LastSeen;

        // Eased towards the weight (or 0 when off) over the blend time, from where it was when the goal changed.
        internal void Advance(float dt)
        {
            float goal = On ? (float.IsFinite(Weight) ? Math.Clamp(Weight, 0f, 1f) : 1f) : 0f;
            if (goal != _goal)
            {
                _from = Amount;
                _goal = goal;
                _elapsed = 0f;
            }
            _elapsed += float.IsFinite(dt) ? MathF.Max(0f, dt) : 0f;
            float seconds = float.IsFinite(BlendSeconds) ? MathF.Max(0f, BlendSeconds) : 0f;
            float t = seconds > 0f ? Math.Clamp(_elapsed / seconds, 0f, 1f) : 1f;
            Amount = _from + (_goal - _from) * t * t * (3f - 2f * t);
        }

        internal bool Finished => !On && Amount <= 0f && _goal <= 0f;
    }

    private readonly Dictionary<string, Aim> _looks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Aim> _reaches = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _gone = new();

    /// <summary>The bone Engine.Animation.LookAt's values belong to.</summary>
    public string LookBone { get; set; } = DefaultLookBone;

    /// <summary>The bone Engine.Animation.Reach's values belong to.</summary>
    public string ReachBone { get; set; } = DefaultReachBone;

    /// <summary>The way the model faces in its own space; a looking bone faces this way in the bind pose. +Z unless set.</summary>
    public Vector3 Forward { get; set; } = Vector3.UnitZ;

    /// <summary>True while some look or reach turns a bone: the pose is then this instance's own to draw.</summary>
    public bool Posing { get; private set; }

    /// <summary>The key of this instance's own skin palette while it is <see cref="Posing"/>.</summary>
    internal string PaletteKey { get; } = "bones:" + Guid.NewGuid().ToString("N");

    public IEnumerable<Aim> Looks => _looks.Values;
    public IEnumerable<Aim> Reaches => _reaches.Values;

    /// <summary>The look of a bone; with <paramref name="create"/>, made (off) when it has none.</summary>
    public Aim? Look(string? bone, bool create = false) => Find(_looks, bone, create, look: true);

    /// <summary>The reach of a chain's end bone; with <paramref name="create"/>, made (off) when it has none.</summary>
    public Aim? Reach(string? bone, bool create = false) => Find(_reaches, bone, create, look: false);

    private static Aim? Find(Dictionary<string, Aim> aims, string? bone, bool create, bool look)
    {
        string name = bone?.Trim() ?? "";
        if (name.Length == 0) return null;
        if (aims.TryGetValue(name, out Aim? aim)) return aim;
        if (!create || aims.Count >= 64) return null;
        return aims[name] = new Aim(name, look);
    }

    /// <summary>Blends every look (<paramref name="looks"/>) or reach out over the time given (negative: each its own).</summary>
    public void StopAll(bool looks, float blendSeconds)
    {
        foreach (Aim aim in (looks ? _looks : _reaches).Values)
        {
            if (blendSeconds >= 0f && float.IsFinite(blendSeconds)) aim.BlendSeconds = blendSeconds;
            aim.On = false;
        }
    }

    // ── Which instances have them, and the frame's work ─────────────────────────────────

    private static readonly ConditionalWeakTable<EcsWorld, HashSet<int>> Users = new();
    private static int _made;
    [ThreadStatic] private static List<int>? _leaving;

    /// <summary>
    /// An instance's look-ats and reaches; with <paramref name="create"/>, made the first time (with
    /// an animator that plays nothing when it has none, so its pose is as it was).
    /// </summary>
    public static ModelBoneTargets? Of(EcsWorld? world, Entity entity, bool create)
    {
        if (world is null || !world.IsAlive(entity)) return null;
        if (world.Has<ModelAnimatorComponent>(entity) && world.GetRef<ModelAnimatorComponent>(entity).Targets is { } found) return found;
        if (!create) return null;
        // Not playing, so the instance does not read as playing a clip it is not.
        if (!world.Has<ModelAnimatorComponent>(entity))
            world.Set(entity, new ModelAnimatorComponent { ClipName = "", PreviousClipName = "", PlaybackSpeed = 1f, Loop = true });
        var made = new ModelBoneTargets();
        world.GetRef<ModelAnimatorComponent>(entity).Targets = made;
        lock (Users)
        {
            Users.GetValue(world, _ => new HashSet<int>()).Add(entity.Id);
            _made++;
        }
        return made;
    }

    /// <summary>
    /// Works out every look and reach in the scene for this frame: blends them on by
    /// <paramref name="dt"/> and finds where their targets are in each model's own space.
    /// RuntimeScene calls this once a frame, after the scripts.
    /// </summary>
    public static void UpdateScene(RuntimeScene scene, float dt)
    {
        if (Volatile.Read(ref _made) == 0 || scene?.World is not { } world) return;
        HashSet<int>? users;
        lock (Users)
        {
            if (!Users.TryGetValue(world, out users) || users.Count == 0) return;
        }
        List<int> leaving = _leaving ??= new List<int>();
        leaving.Clear();
        foreach (int id in users)
        {
            Entity entity = world.GetEntity(id);
            if (!world.IsAlive(entity) || !world.Has<ModelAnimatorComponent>(entity)
                || world.GetRef<ModelAnimatorComponent>(entity).Targets is not { } targets)
            {
                leaving.Add(id);
                continue;
            }
            targets.Update(world, entity, dt);
        }
        foreach (int id in leaving) users.Remove(id);
        leaving.Clear();
    }

    /// <summary>Blends this instance's looks and reaches on by <paramref name="dt"/> and finds their targets.</summary>
    public void Update(EcsWorld world, Entity self, float dt)
    {
        bool placed = TryToModel(world, self, out Matrix4x4 toModel);
        bool posing = false;
        posing |= Step(_looks, world, placed, toModel, dt);
        posing |= Step(_reaches, world, placed, toModel, dt);
        Posing = posing;
    }

    private bool Step(Dictionary<string, Aim> aims, EcsWorld world, bool placed, Matrix4x4 toModel, float dt)
    {
        bool posing = false;
        _gone.Clear();
        foreach (Aim aim in aims.Values)
        {
            aim.Advance(dt);
            if (aim.Finished)
            {
                _gone.Add(aim.Bone);
                continue;
            }
            Vector3? seen = placed ? TargetWorld(world, aim) : null;
            aim.Resolved = seen.HasValue;
            if (seen is { } point) aim.ModelPoint = Vector3.Transform(point, toModel);
            posing |= aim.Resolved && aim.Amount > 0f;
        }
        foreach (string bone in _gone) aims.Remove(bone);
        return posing;
    }

    // Where an aim's target is in the world now; once an instance aimed at is gone, where it was last.
    private static Vector3? TargetWorld(EcsWorld world, Aim aim)
    {
        Vector3 above = Vector3.UnitY * (float.IsFinite(aim.Height) ? aim.Height : 0f);
        if (aim.TargetInstance <= 0) return aim.LastSeen = aim.Point + above;
        Entity target = world.GetEntity(aim.TargetInstance);
        if (world.IsAlive(target))
        {
            if (aim.TargetBone.Length > 0 && ModelInstance.TryGetSocketPosition(world, target, aim.TargetBone, out Vector3 bone))
                return aim.LastSeen = bone + above;
            if (world.Has<TransformComponent>(target))
            {
                ref TransformComponent transform = ref world.GetRef<TransformComponent>(target);
                return aim.LastSeen = new Vector3(transform.X, transform.Y, transform.Z) + above;
            }
        }
        return aim.LastSeen;
    }

    // The world to the model's own space, the space its bones are in: the inverse of the draw's
    // transform with the model's pivot.
    private static bool TryToModel(EcsWorld world, Entity self, out Matrix4x4 toModel)
    {
        toModel = Matrix4x4.Identity;
        if (!world.IsAlive(self) || !world.Has<TransformComponent>(self) || !world.Has<ModelRendererComponent>(self)) return false;
        ref TransformComponent transform = ref world.GetRef<TransformComponent>(self);
        ref ModelRendererComponent model = ref world.GetRef<ModelRendererComponent>(self);
        if (string.IsNullOrWhiteSpace(model.ModelAsset)) return false;
        Matrix4x4 drawn = RuntimeModelRenderSystem.DrawMatrix(RuntimeModelAssetRegistry.Shared.Load(Scripting.PgslCommands.ProjectPath, model.ModelAsset), transform, model);
        return Matrix4x4.Invert(drawn, out toModel);
    }

    // ── The pose ─────────────────────────────────────────────────────────────────────────

    [ThreadStatic] private static Matrix4x4[]? _world;
    [ThreadStatic] private static bool[]? _resolved;

    /// <summary>
    /// Puts the reaches, then the looks, on a local pose of <paramref name="asset"/> (after its
    /// clips, layers and controller). Leaves the pose alone while nothing is <see cref="Posing"/>.
    /// </summary>
    public void Apply(GModelAsset asset, Matrix4x4[] locals)
    {
        if (!Posing || asset?.Rig?.Bones is not { Count: > 0 } bones || locals.Length != bones.Count) return;
        int count = bones.Count;
        if (_world == null || _world.Length != count) { _world = new Matrix4x4[count]; _resolved = new bool[count]; }
        Matrix4x4[] world = _world;
        bool[] resolved = _resolved!;
        foreach (Aim reach in _reaches.Values)
        {
            if (!reach.Resolved || reach.Amount <= 0f) continue;
            int end = BonePoseMath.Find(bones, reach.Bone);
            if (end < 0) continue;
            BonePoseMath.ModelSpace(bones, locals, world, resolved);
            BonePoseMath.TwoBoneReach(bones, locals, world, end, reach.ModelPoint, reach.Amount);
        }
        Vector3 forward = Forward.LengthSquared() > 1e-12f ? Vector3.Normalize(Forward) : Vector3.UnitZ;
        foreach (Aim look in _looks.Values)
        {
            if (!look.Resolved || look.Amount <= 0f) continue;
            int bone = BonePoseMath.Find(bones, look.Bone);
            if (bone < 0) continue;
            BonePoseMath.ModelSpace(bones, locals, world, resolved);
            BonePoseMath.LookAt(bones, locals, world, bone, look.ModelPoint, forward,
                Limit(look.YawLimit, 180f), Limit(look.PitchLimit, 89f), look.Amount);
        }

        static float Limit(float degrees, float most) => (float.IsFinite(degrees) ? Math.Clamp(degrees, 0f, most) : most) * MathF.PI / 180f;
    }
}
