using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;

namespace Genesis.Runtime.Project;

public sealed partial class RoomTerrainSubsystem
{
    /// <summary>What each terrain's grass around the camera held and drew in the last frame.</summary>
    public IReadOnlyList<TerrainGrassStatistics> GrassRuleStatistics =>
        _entries.Select(entry => entry.GrassField?.Statistics ?? default).ToArray();

    /// <summary>The first terrain's own grass rule (as its nature document has it); null without one.</summary>
    public TerrainGrassRuleSettings FirstGrassRule
    {
        get
        {
            foreach (Entry entry in _entries)
                if (entry.Nature?.GrassRule is { } rule) return rule;
            return null;
        }
    }

    /// <summary>Each terrain's grass look as drawn in the last frame (for checks).</summary>
    public IReadOnlyList<TerrainGrassLook> GrassLooks =>
        _entries.Where(entry => entry.GrassField != null).Select(entry => entry.GrassField.Look).ToArray();

    /// <summary>
    /// Grows and draws grass around the camera when the terrain's nature document switches the
    /// rule on. Growth is budgeted per frame inside the field, so this never holds a frame up.
    /// </summary>
    private void SubmitGrassRule(
        Entry entry,
        Vector3 camera,
        Matrix4x4 viewProjection,
        Matrix4x4 placement,
        IRenderController renderer)
    {
        TerrainGrassRuleSettings rule = GrassRuleFor(entry);
        if (rule is not { Enabled: true } || entry.Terrain == null || renderer == null)
        {
            if (entry.GrassField != null)
            {
                entry.GrassField.Dispose();
                entry.GrassField = null;
            }

            return;
        }

        // Reloaded heights, paint, paths or rule: grow the grass again from the new ones.
        if (entry.GrassField != null
            && (!ReferenceEquals(entry.GrassField.Terrain, entry.Terrain) || !entry.GrassField.GrowsFrom(rule)
                || !entry.GrassField.ClearsPaths(entry.Nature.Paths)))
        {
            entry.GrassField.Dispose();
            entry.GrassField = null;
        }

        entry.GrassField ??= new TerrainGrassField(entry.Terrain, rule, entry.Nature.WaterBodies, entry.Nature.Paths);
        entry.GrassField.Look = GrassLookFor(entry, rule, renderer);
        entry.GrassField.Update(camera, placement);
        // The renderer draws a fixed number of instances a frame and drops the rest. Grass takes
        // only what the scattered foliage and the forests and rocks have left, less a reserve for
        // everything else, so switching it on never costs a tree.
        int taken = entry.FoliagePerformance.SubmittedInstances + (entry.Scatter?.Statistics.NearCopies ?? 0);
        int room = RenderCapacityDefaults.MeshInstanceCap - GrassInstanceReserve - taken;
        entry.GrassField.Draw(renderer, camera, viewProjection, Math.Max(0, room), GrassPushersOn ? _grassPushers : null);
    }

    /// <summary>Instances grass leaves for objects, characters and particles.</summary>
    private const int GrassInstanceReserve = 4096;

    // ── The grass shader's texture ─────────────────────────────────────────────────

    /// <summary>The rule's blade texture (an Image resource), loaded once per name and renderer.</summary>
    private TextureHandle GrassTexture(Entry entry, string name, IRenderController renderer)
    {
        if (string.IsNullOrWhiteSpace(name) || renderer == null) return TextureHandle.Invalid;
        if (entry.GrassTexture.IsValid && string.Equals(entry.GrassTextureName, name, StringComparison.OrdinalIgnoreCase)
            && ReferenceEquals(entry.GrassTextureRenderer, renderer))
            return entry.GrassTexture;
        entry.GrassTextureName = name;
        entry.GrassTextureRenderer = renderer;
        entry.GrassTexture = TextureHandle.Invalid;
        try
        {
            // Lit 3D colour: decoded from sRGB, with mipmaps, as the terrain's own picture is.
            TextureHandle texture = _game?.LoadTexture(name, Genesis.Shared.Materials.TextureColorSpace.Srgb) ?? TextureHandle.Invalid;
            if (!texture.IsValid)
            {
                string path = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(_projectPath, name, 0);
                if (File.Exists(path)) texture = renderer.LoadTexture(path, Genesis.Shared.Materials.TextureColorSpace.Srgb);
            }

            entry.GrassTexture = texture;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidDataException)
        {
            Console.WriteLine($"[Terrain] The grass texture '{name}' could not be loaded: {exception.Message}");
        }

        return entry.GrassTexture;
    }

    // ── Pushers: what parts the grass ──────────────────────────────────────────────

    private readonly GrassPushers _grassPushers = new();
    private readonly Dictionary<long, (Vector3 Position, float Radius)> _scriptPushers = new();
    private readonly List<(long Id, Vector3 Position, float Radius, float Distance)> _characterPushers = new();
    private readonly Stopwatch _pusherClock = Stopwatch.StartNew();
    private double _lastPusherSeconds = -1;

    /// <summary>Most animated characters that part the grass at once, nearest the camera first.</summary>
    public const int MaximumCharacterPushers = 24;

    /// <summary>Whether pushers bend the grass and small plants at all (Engine.Terrain.Grass.Pushers.Enabled).</summary>
    public bool GrassPushersOn { get; set; } = true;

    /// <summary>Whether animated characters part the grass by themselves (Engine.Terrain.Grass.Pushers.Characters).</summary>
    public bool GrassCharactersPush { get; set; } = true;

    /// <summary>
    /// How far round an animated character's feet grass bends, in metres at scale 1, 0.1 to 8
    /// (Engine.Terrain.Grass.Pushers.CharacterRadius): wide enough to read from a third-person camera.
    /// </summary>
    public float GrassCharacterRadius
    {
        get => _grassCharacterRadius;
        set => _grassCharacterRadius = float.IsFinite(value) ? Math.Clamp(value, 0.1f, 8f) : _grassCharacterRadius;
    }

    private float _grassCharacterRadius = 1.2f;

    /// <summary>The points that bend grass this frame (for checks).</summary>
    public IReadOnlyList<GrassPushPoint> GrassPushPoints => _grassPushers.Points;

    /// <summary>Sets (or moves) a game's pusher: grass within <paramref name="radius"/> metres of it bends away.</summary>
    public void SetGrassPusher(long id, Vector3 position, float radius)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z) || !float.IsFinite(radius)) return;
        if (radius <= 0f) _scriptPushers.Remove(id);
        else _scriptPushers[id] = (position, Math.Clamp(radius, 0.05f, 16f));
    }

    /// <summary>Removes a game's pusher; the grass it bent springs back.</summary>
    public bool RemoveGrassPusher(long id) => _scriptPushers.Remove(id);

    /// <summary>Removes every pusher a game set.</summary>
    public void ClearGrassPushers() => _scriptPushers.Clear();

    /// <summary>How many pushers a game has set.</summary>
    public int GrassPusherCount => _scriptPushers.Count;

    /// <summary>
    /// This frame's pushers: the game's own, and each animated character near the camera (a model
    /// playing an animation: the hero, people, animals) at its feet, with the trails they leave.
    /// </summary>
    private void GatherGrassPushers(RuntimeScene scene, Vector3 camera)
    {
        double now = _pusherClock.Elapsed.TotalSeconds;
        float delta = _lastPusherSeconds < 0 ? 0f : (float)Math.Min(now - _lastPusherSeconds, 0.25);
        _lastPusherSeconds = now;
        _grassPushers.Begin(delta, SpringBackFor());
        if (!GrassPushersOn)
        {
            _grassPushers.End();
            return;
        }

        foreach (KeyValuePair<long, (Vector3 Position, float Radius)> pusher in _scriptPushers)
            _grassPushers.Add(-1 - pusher.Key, pusher.Value.Position, pusher.Value.Radius);

        if (GrassCharactersPush && scene?.World != null)
        {
            _characterPushers.Clear();
            _pusherCamera = camera;
            _pusherReach = CharacterPusherReach();
            scene.World.Query(_collectCharacterPusher ??= CollectCharacterPusher);
            if (_characterPushers.Count > MaximumCharacterPushers)
                _characterPushers.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            for (int i = 0; i < _characterPushers.Count && i < MaximumCharacterPushers; i++)
                _grassPushers.Add(_characterPushers[i].Id, _characterPushers[i].Position, _characterPushers[i].Radius);
        }

        _grassPushers.End();
    }

    private Vector3 _pusherCamera;
    private float _pusherReach;
    private ComponentAction<Transform3DComponent, ModelAnimatorComponent> _collectCharacterPusher;

    /// <summary>An animated model near the camera parts the grass round its feet, <see cref="GrassCharacterRadius"/> across.</summary>
    private void CollectCharacterPusher(Entity entity, ref Transform3DComponent transform, ref ModelAnimatorComponent animator)
    {
        // Only a model actually animated (a clip or a controller): a prop that merely could be is not a character.
        if (string.IsNullOrEmpty(animator.ClipName) && animator.Controller == null) return;
        float distance = Vector3.Distance(transform.Position, _pusherCamera);
        if (!(distance <= _pusherReach)) return;
        float size = MathF.Max(MathF.Abs(transform.Scale.X), MathF.Abs(transform.Scale.Z));
        float radius = Math.Clamp(_grassCharacterRadius * (size > 0f ? size : 1f), 0.1f, 16f);
        _characterPushers.Add((((long)entity.Version << 32) | (uint)entity.Id, transform.Position, radius, distance));
    }

    /// <summary>How far from the camera characters part grass: as far as any terrain grows it, at least 40 m.</summary>
    private float CharacterPusherReach()
    {
        float reach = 40f;
        foreach (Entry entry in _entries)
            if (entry.Nature?.GrassRule is { Enabled: true } rule) reach = MathF.Max(reach, rule.Radius * GrassDistance);
        return reach;
    }

    /// <summary>The spring-back time: the game's, else the first terrain's rule's.</summary>
    private float SpringBackFor()
    {
        if (GameGrassLook.SpringBackSeconds is float seconds) return seconds;
        foreach (Entry entry in _entries)
            if (entry.Nature?.GrassRule is { } rule) return rule.SpringBackSeconds;
        return 0.8f;
    }

    /// <summary>How far pushers bend the terrain's bending scatter layers: the game's push strength, else its rule's.</summary>
    private float ScatterPushStrength(Entry entry)
    {
        if (!GrassPushersOn) return 0f;
        TerrainGrassRuleSettings rule = entry.Nature?.GrassRule;
        if (rule != null && !rule.PushersEnabled) return 0f;
        return GameGrassLook.PushStrength ?? rule?.PushStrength ?? 0.8f;
    }
}
