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
    /// <summary>What a game's edit does to the ground under its brush.</summary>
    public enum GroundEdit { Raise, Lower, Flatten, Smooth, Paint }

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
                default: continue;
            }

            float cx = (local.X - terrain.OriginX) / terrain.CellSize, cz = (local.Z - terrain.OriginZ) / terrain.CellSize;
            float cells = reach / terrain.CellSize;
            entry.Ground?.RebuildRegion(
                Math.Max(0, (int)MathF.Floor(cx - cells)), Math.Max(0, (int)MathF.Floor(cz - cells)),
                Math.Min(terrain.ResolutionX - 1, (int)MathF.Ceiling(cx + cells)), Math.Min(terrain.ResolutionZ - 1, (int)MathF.Ceiling(cz + cells)));
            entry.GrassField?.Refresh();
            if (edit != GroundEdit.Paint) Resolid(scene, entry);
            changed = true;
        }
        if (changed) GroundEdits++;
        return changed;
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
