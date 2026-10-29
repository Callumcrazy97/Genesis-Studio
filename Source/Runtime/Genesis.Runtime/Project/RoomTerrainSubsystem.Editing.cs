using System;
using System.Numerics;
using Genesis.Runtime.Scene;
using Genesis.World.Terrain;

namespace Genesis.Runtime.Project;

/// <summary>
/// A game changes its ground as it plays (Engine.Terrain.Edit): a crater where a shell lands, a
/// trench a player digs, a path worn to dirt. The Terrain editor's own brushes do the work, at a
/// point in the world, on every terrain of the room under it; the ground's look, its grass and
/// its collision follow at once. The change lasts while the room runs; the resource is untouched.
/// </summary>
public sealed partial class RoomTerrainSubsystem
{
    /// <summary>What a game's edit does to the ground under its brush. Hole cuts the ground away; Fill puts it back.</summary>
    public enum GroundEdit { Raise, Lower, Flatten, Smooth, Paint, Hole, Fill }

    /// <summary>Edits made so far (for checks).</summary>
    public int GroundEdits { get; private set; }

    /// <summary>The room's terrains loaded now, in Room order (for checks).</summary>
    internal TerrainAsset[] LoadedTerrains => _entries.ConvertAll(entry => entry.Terrain).ToArray();

    /// <summary>
    /// Edits the ground of every terrain under the brush at world <paramref name="x"/>,
    /// <paramref name="z"/>: raises or lowers it by <paramref name="amount"/> metres at the centre,
    /// flattens it toward the world height <paramref name="amount"/>, smooths it by
    /// <paramref name="amount"/> (0 to 1), or paints layer <paramref name="layer"/> (1 to 8) at
    /// <paramref name="amount"/> strength. The brush falls off smoothly to its
    /// <paramref name="radius"/>. False when no terrain lies under it.
    /// </summary>
    public bool Edit(RuntimeScene scene, GroundEdit edit, float x, float z, float radius, float amount, int layer = 1)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !(radius > 0f) || !float.IsFinite(amount)) return false;
        bool changed = false;
        foreach (Entry entry in _entries)
        {
            if (entry.Terrain is not { } terrain) continue;
            (Vector3 min, Vector3 max) = EntryBounds(entry);
            if (x + radius < min.X || x - radius > max.X || z + radius < min.Z || z - radius > max.Z) continue;
            RoomTransform place = RoomHierarchyTransforms.World(entry.Room, entry.Node);
            if (!Matrix4x4.Invert(Placement(entry), out Matrix4x4 toTerrain)) continue;

            // The brush in the terrain's own units.
            Vector3 local = Vector3.Transform(new Vector3(x, 0f, z), toTerrain);
            float across = MathF.Max(1e-4f, (MathF.Abs(place.ScaleX) + MathF.Abs(place.ScaleZ)) * 0.5f);
            float up = MathF.Abs(place.ScaleY) > 1e-6f ? place.ScaleY : 1f;
            float reach = radius / across;
            switch (edit)
            {
                case GroundEdit.Raise: terrain.ApplySculptBrush(local.X, local.Z, reach, amount / up, raise: true); break;
                case GroundEdit.Lower: terrain.ApplySculptBrush(local.X, local.Z, reach, amount / up, raise: false); break;
                case GroundEdit.Flatten: terrain.ApplyFlattenBrush(local.X, local.Z, reach, 1f, (amount - place.Y) / up); break;
                case GroundEdit.Smooth: terrain.ApplySmoothBrush(local.X, local.Z, reach, Math.Clamp(amount, 0f, 1f)); break;
                case GroundEdit.Paint: terrain.ApplyPaintBrush(local.X, local.Z, reach, Math.Clamp(amount, 0f, 1f), Math.Clamp(layer - 1, 0, TerrainAsset.MaximumPaintLayers - 1)); break;
                case GroundEdit.Hole:
                case GroundEdit.Fill:
                    if (terrain.ApplyHoleBrush(local.X, local.Z, reach, fill: edit == GroundEdit.Fill) == 0) continue;
                    break;
                default: continue;
            }

            float cx = (local.X - terrain.OriginX) / terrain.CellSize, cz = (local.Z - terrain.OriginZ) / terrain.CellSize;
            float cells = reach / terrain.CellSize;
            int minX = Math.Max(0, (int)MathF.Floor(cx - cells)), minZ = Math.Max(0, (int)MathF.Floor(cz - cells));
            int maxX = Math.Min(terrain.ResolutionX - 1, (int)MathF.Ceiling(cx + cells)), maxZ = Math.Min(terrain.ResolutionZ - 1, (int)MathF.Ceiling(cz + cells));
            if (edit is GroundEdit.Hole or GroundEdit.Fill) AfterHoles(scene, entry, minX, minZ, maxX, maxZ);
            else entry.Ground?.RebuildRegion(minX, minZ, maxX, maxZ);
            entry.GrassField?.Refresh();
            if (edit is not (GroundEdit.Paint or GroundEdit.Hole or GroundEdit.Fill)) Resolid(scene, entry);
            changed = true;
        }
        if (changed) GroundEdits++;
        return changed;
    }

    // ── Holes (Engine.Terrain.Holes) ───────────────────────────────────────────────

    /// <summary>True when the ground of a terrain at world x, z is cut away (a hole), with nothing under it in the same terrain.</summary>
    public bool IsHoleAt(float worldX, float worldZ)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ)) return false;
        foreach (Entry entry in _entries)
        {
            if (entry.Terrain is not { HasHoles: true } terrain || !Matrix4x4.Invert(Placement(entry), out Matrix4x4 toTerrain)) continue;
            Vector3 local = Vector3.Transform(new Vector3(worldX, 0f, worldZ), toTerrain);
            if (terrain.IsHoleAt(local.X, local.Z)) return true;
        }

        return false;
    }

    /// <summary>Cells cut away across the room's terrains.</summary>
    public int HoleCount
    {
        get
        {
            int count = 0;
            foreach (Entry entry in _entries) count += entry.Terrain?.HoleCount ?? 0;
            return count;
        }
    }

    /// <summary>Whether the terrains' holes are cut (true unless set); false draws, collides and walks solid ground over them.</summary>
    public bool HolesEnabled { get; private set; } = true;

    /// <summary>Cuts the holes again, or covers them, on every terrain of the room.</summary>
    public void SetHolesEnabled(RuntimeScene scene, bool enabled)
    {
        if (enabled == HolesEnabled) return;
        HolesEnabled = enabled;
        foreach (Entry entry in _entries)
        {
            if (entry.Terrain is not { } terrain) continue;
            terrain.HolesEnabled = enabled;
            if (terrain.HoleCount == 0) continue;
            AfterHoles(scene, entry, 0, 0, terrain.ResolutionX - 1, terrain.ResolutionZ - 1);
            entry.GrassField?.Refresh();
        }
    }

    // Holes changed under a rectangle of samples: the ground's triangles there, its collision and
    // the scattered copies (placed again, without the ones now over a hole).
    private void AfterHoles(RuntimeScene scene, Entry entry, int minX, int minZ, int maxX, int maxZ)
    {
        entry.Ground?.RebuildHoleRegion(minX - 1, minZ - 1, maxX, maxZ);
        Resolid(scene, entry);
        if (entry.Scatter != null && entry.Nature != null)
        {
            float near = entry.Scatter.NearDistance, mid = entry.Scatter.MidDistance;
            float far = entry.Scatter.MaximumDistance, shadow = entry.Scatter.ShadowDistance;
            entry.Scatter.Dispose();
            entry.Scatter = new TerrainScatterRenderer(_projectPath, entry.Terrain, entry.Nature.ScatterLayers)
            {
                NearDistance = near, MidDistance = mid, MaximumDistance = far, ShadowDistance = shadow,
            };
            entry.ScatterColliders?.Clear(scene?.Physics);
            entry.ScatterColliders = null;
        }
    }

    // Collision after the ground moved: a small terrain's made again where it stood; a large one's
    // tiles let go, made again by the next step (the ground under each body at once).
    private static void Resolid(RuntimeScene scene, Entry entry)
    {
        if (entry.ColliderTiles != null)
        {
            entry.ColliderTiles.Clear(scene?.Physics);
            entry.ColliderTiles.Dispose();
            entry.ColliderTiles = null;
            return;
        }
        RefreshCollision(scene, entry);
    }
}
