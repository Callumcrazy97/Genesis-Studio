using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Runtime.Project;

/// <summary>
/// The room's terrains as a game changes them while it runs (Engine.Terrain): the ground and its
/// scattered trees and rocks drawn or not, how far and how densely they and the grass reach, how
/// far collision is built round moving bodies, and what the ground is like at a point. Each
/// terrain keeps its own authored values; the game's are multiples of them, 1 unless set.
/// </summary>
public sealed partial class RoomTerrainSubsystem
{
    /// <summary>Whether the terrains' ground (and their paths) is drawn (Engine.Terrain.Enabled); collision stays.</summary>
    public bool GroundDrawn { get; set; } = true;

    /// <summary>Whether the terrains' scattered trees and rocks are drawn (Engine.Terrain.Scatter.Enabled).</summary>
    public bool ScatterDrawn { get; set; } = true;

    /// <summary>The room's terrain distance: how near the camera a streamed terrain is read (Engine.Terrain.Detail.Distance).</summary>
    public float TerrainDistance
    {
        get => _terrainDistance;
        set => _terrainDistance = float.IsFinite(value) ? Math.Clamp(value, 0f, 1000000f) : _terrainDistance;
    }

    // ── Grass (Engine.Terrain.Grass) ────────────────────────────────────────────────

    /// <summary>Grass on or off for every terrain, or null for each terrain's own rule.</summary>
    public bool? GrassOn { get; set; }

    /// <summary>Tufts as a multiple of each rule's own, 0.05 to 4.</summary>
    public float GrassDensity { get; set; } = 1f;

    /// <summary>How far grass reaches, as a multiple of each rule's own, 0.1 to 10.</summary>
    public float GrassDistance { get; set; } = 1f;

    /// <summary>How tall the tufts grow, as a multiple of each rule's own, 0.1 to 4.</summary>
    public float GrassHeight { get; set; } = 1f;

    // The rule a terrain's grass grows from: its own, or a copy changed by the game's values.
    // The copy is kept while neither changes, so the grass is not grown again each frame.
    private TerrainGrassRuleSettings GrassRuleFor(Entry entry)
    {
        TerrainGrassRuleSettings rule = entry.Nature?.GrassRule;
        if (rule == null || GrassOn == null && GrassDensity == 1f && GrassDistance == 1f && GrassHeight == 1f) return rule;
        if (entry.GameGrassRule != null && entry.GameGrassSource != null && entry.GameGrassSource.SameAs(rule)
            && entry.GameGrassRule.Enabled == (GrassOn ?? rule.Enabled)
            && entry.GameGrassRule.Radius == Clamp(rule.Radius * GrassDistance, 4f, 2000f)
            && entry.GameGrassRule.Spacing == Clamp(rule.Spacing / MathF.Sqrt(GrassDensity), 0.1f, 16f)
            && entry.GameGrassRule.MaximumScale == Clamp(rule.MaximumScale * GrassHeight, 0.05f, 16f))
            return entry.GameGrassRule;
        TerrainGrassRuleSettings changed = rule.Clone();
        changed.Enabled = GrassOn ?? rule.Enabled;
        changed.Radius = Clamp(rule.Radius * GrassDistance, 4f, 2000f);
        changed.Spacing = Clamp(rule.Spacing / MathF.Sqrt(GrassDensity), 0.1f, 16f);
        changed.MinimumScale = Clamp(rule.MinimumScale * GrassHeight, 0.05f, 16f);
        changed.MaximumScale = Clamp(rule.MaximumScale * GrassHeight, 0.05f, 16f);
        entry.GameGrassSource = rule.Clone();
        return entry.GameGrassRule = changed;
    }

    private static float Clamp(float value, float minimum, float maximum) => Math.Clamp(value, minimum, maximum);

    // ── Scatter (Engine.Terrain.Scatter) ────────────────────────────────────────────

    private float _scatterDistance = 1f, _scatterDensity = 1f;
    private bool _scatterShadows = true;

    /// <summary>How far the scattered trees and rocks are drawn, as a multiple of each terrain's own, 0.1 to 10.</summary>
    public float ScatterDistance
    {
        get => _scatterDistance;
        set
        {
            if (!float.IsFinite(value)) return;
            float next = Clamp(value, 0.1f, 10f), change = next / _scatterDistance;
            _scatterDistance = next;
            foreach (Entry entry in _entries)
            {
                if (entry.Scatter is not { } scatter) continue;
                scatter.NearDistance *= change;
                scatter.MidDistance *= change;
                scatter.MaximumDistance *= change;
                if (_scatterShadows) scatter.ShadowDistance *= change;
            }
        }
    }

    /// <summary>Whether the scattered trees and rocks cast shadows.</summary>
    public bool ScatterShadows
    {
        get => _scatterShadows;
        set
        {
            if (value == _scatterShadows) return;
            _scatterShadows = value;
            foreach (Entry entry in _entries)
                if (entry.Scatter is { } scatter) scatter.ShadowDistance = value ? 110f * _scatterDistance : 0f;
        }
    }

    /// <summary>
    /// How many trees and rocks are scattered, as a multiple of each layer's own, 0 to 4. The copies
    /// are placed again from the rules (their colliders too), as when a terrain is reloaded.
    /// </summary>
    public float ScatterDensity => _scatterDensity;

    /// <summary>Sets <see cref="ScatterDensity"/>, placing every terrain's copies again.</summary>
    public void SetScatterDensity(RuntimeScene scene, float density)
    {
        if (!float.IsFinite(density)) return;
        density = Clamp(density, 0f, 4f);
        if (density == _scatterDensity) return;
        _scatterDensity = density;
        foreach (Entry entry in _entries)
        {
            if (entry.Nature == null || entry.Terrain == null) continue;
            entry.AuthoredScatter ??= TerrainNatureSerializer.Clone(entry.Nature.ScatterLayers);
            var layers = TerrainNatureSerializer.Clone(entry.AuthoredScatter);
            foreach (TerrainScatterLayer layer in layers) layer.DensityPerHectare *= density;
            entry.Nature.ScatterLayers = layers;
            float near = entry.Scatter?.NearDistance ?? 300f, mid = entry.Scatter?.MidDistance ?? 1300f;
            float far = entry.Scatter?.MaximumDistance ?? 12000f, shadow = entry.Scatter?.ShadowDistance ?? 110f;
            entry.Scatter?.Dispose();
            entry.Scatter = new TerrainScatterRenderer(_projectPath, entry.Terrain, layers)
            {
                NearDistance = near, MidDistance = mid, MaximumDistance = far, ShadowDistance = shadow,
            };
            entry.ScatterColliders?.Clear(scene?.Physics);
            entry.ScatterColliders = null;
        }
    }

    /// <summary>Each terrain's grass rule and scatter as drawn now (for checks).</summary>
    internal (TerrainGrassRuleSettings Grass, List<TerrainScatterLayer> Scatter, float ScatterNear, float ScatterShadow)[] LookNow() =>
        _entries.ConvertAll(entry => (GrassRuleFor(entry), entry.Nature?.ScatterLayers, entry.Scatter?.NearDistance ?? 0f, entry.Scatter?.ShadowDistance ?? 0f)).ToArray();

    // ── What the ground is like at a point ──────────────────────────────────────────

    /// <summary>The ground's surface at world x, z: its height and which way it faces. False where no terrain is.</summary>
    public bool TrySurface(float worldX, float worldZ, out float height, out Vector3 normal)
    {
        height = 0f;
        normal = Vector3.UnitY;
        bool found = false;
        foreach (Entry entry in _entries)
        {
            (Vector3 min, Vector3 max) = EntryBounds(entry);
            if (worldX < min.X || worldX > max.X || worldZ < min.Z || worldZ > max.Z
                || !TerrainSurfaceRaycast.Raycast(entry.Terrain, Placement(entry), new Vector3(worldX, max.Y + 1f, worldZ),
                    -Vector3.UnitY, out TerrainSurfaceHit hit)) continue;
            if (found && hit.Position.Y <= height) continue;
            height = hit.Position.Y;
            normal = hit.Normal;
            found = true;
        }
        return found;
    }

    /// <summary>The painted layer (1 to 8) most of the ground at world x, z is; 0 where no terrain is.</summary>
    public int LayerAt(float worldX, float worldZ)
    {
        foreach (Entry entry in _entries)
        {
            if (entry.Terrain is not { } terrain || !Matrix4x4.Invert(Placement(entry), out Matrix4x4 toTerrain)) continue;
            Vector3 local = Vector3.Transform(new Vector3(worldX, 0f, worldZ), toTerrain);
            int x = (int)MathF.Round((local.X - terrain.OriginX) / terrain.CellSize), z = (int)MathF.Round((local.Z - terrain.OriginZ) / terrain.CellSize);
            if (x < 0 || z < 0 || x >= terrain.ResolutionX || z >= terrain.ResolutionZ) continue;
            int best = 0;
            byte most = 0;
            for (int layer = 0; layer < TerrainAsset.MaximumPaintLayers; layer++)
            {
                byte weight = terrain.GetLayerWeight(x, z, layer);
                if (weight > most) { most = weight; best = layer; }
            }
            return best + 1;
        }
        return 0;
    }
}
