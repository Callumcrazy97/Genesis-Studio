using System;
using System.Numerics;
using Genesis.Runtime.Scene;
using Genesis.World.Terrain;

namespace Genesis.Runtime.Project;

/// <summary>
/// Terrains laid edge to edge make one ground: when a terrain joins the room (at the start, or as
/// the camera nears it), the heights along each edge it shares with a terrain already there are
/// made the same, the two heights' average, so no seam shows and nothing falls through between
/// them. A large world can then be built from tiles, each a Terrain of its own, which the room
/// reads as the camera comes near (its terrain distance). Terrains meet this way when they are not
/// turned, are scaled alike and share a cell size, and their grids line up along the edge.
/// </summary>
public sealed partial class RoomTerrainSubsystem
{
    /// <summary>Edges made to meet so far (for checks).</summary>
    public int StitchedEdges { get; private set; }

    /// <summary>Every water body of the room's terrains loaded now (Engine.Water.Surface sets their look).</summary>
    public void ForEachWaterBody(Action<Genesis.World.Water.WaterBody> visit)
    {
        foreach (Entry entry in _entries)
            foreach (WaterRuntime water in entry.Waters)
                visit(water.Body);
    }

    // A joining terrain stitched to every terrain already in the room.
    private void StitchWithNeighbours(Entry joined, RuntimeScene scene)
    {
        foreach (Entry other in _entries)
        {
            if (ReferenceEquals(other, joined) || other.Terrain == null || joined.Terrain == null) continue;
            if (!Stitch(joined.Terrain, RoomHierarchyTransforms.World(joined.Room, joined.Node),
                    other.Terrain, RoomHierarchyTransforms.World(other.Room, other.Node),
                    out (int MinX, int MinZ, int MaxX, int MaxZ) joinedEdge, out (int MinX, int MinZ, int MaxX, int MaxZ) otherEdge)) continue;
            StitchedEdges++;
            joined.Ground?.RebuildRegion(joinedEdge.MinX, joinedEdge.MinZ, joinedEdge.MaxX, joinedEdge.MaxZ);
            other.Ground?.RebuildRegion(otherEdge.MinX, otherEdge.MinZ, otherEdge.MaxX, otherEdge.MaxZ);
            RefreshCollision(scene, joined);
            RefreshCollision(scene, other);
        }
    }

    // A terrain's collision after its edge moved: made again at once where it stood (something may
    // be standing on it), let go where a worker was still preparing it (it is prepared again).
    private static void RefreshCollision(RuntimeScene scene, Entry entry)
    {
        if (entry.ColliderRegistrationId != 0 && scene?.Physics != null)
        {
            scene.Physics.UnregisterStaticSurface(entry.ColliderRegistrationId);
            entry.ColliderRegistrationId = RegisterTerrainCollider(scene.Physics, entry);
        }
        else if (entry.ColliderRegistrationId == 0)
        {
            DiscardPreparedCollider(entry);
        }
    }

    /// <summary>
    /// Makes the edge <paramref name="a"/> and <paramref name="b"/> share (placed by
    /// <paramref name="placeA"/> and <paramref name="placeB"/>) meet at the average of their heights.
    /// False when they share no edge or cannot meet (turned, scaled or gridded differently); the
    /// sample rectangles changed in each otherwise.
    /// </summary>
    public static bool Stitch(TerrainAsset a, RoomTransform placeA, TerrainAsset b, RoomTransform placeB,
        out (int MinX, int MinZ, int MaxX, int MaxZ) edgeA, out (int MinX, int MinZ, int MaxX, int MaxZ) edgeB)
    {
        edgeA = edgeB = default;
        if (a == null || b == null || !Plain(placeA) || !Plain(placeB)) return false;
        if (MathF.Abs(placeA.ScaleX - placeB.ScaleX) > 1e-4f || MathF.Abs(placeA.ScaleZ - placeB.ScaleZ) > 1e-4f) return false;
        float cellX = a.CellSize * placeA.ScaleX, cellZ = a.CellSize * placeA.ScaleZ;
        if (MathF.Abs(a.CellSize - b.CellSize) > 1e-4f * a.CellSize || !(cellX > 0f) || !(cellZ > 0f)) return false;

        // Each grid's first sample and last in the world.
        float ax0 = placeA.X + a.OriginX * placeA.ScaleX, az0 = placeA.Z + a.OriginZ * placeA.ScaleZ;
        float bx0 = placeB.X + b.OriginX * placeB.ScaleX, bz0 = placeB.Z + b.OriginZ * placeB.ScaleZ;
        float ax1 = ax0 + (a.ResolutionX - 1) * cellX, az1 = az0 + (a.ResolutionZ - 1) * cellZ;
        float bx1 = bx0 + (b.ResolutionX - 1) * cellX, bz1 = bz0 + (b.ResolutionZ - 1) * cellZ;
        float tolerance = MathF.Min(cellX, cellZ) * 0.05f;

        // An edge running along Z (A's right on B's left, or A's left on B's right).
        int columnA = -1, columnB = -1;
        if (MathF.Abs(ax1 - bx0) <= tolerance) { columnA = a.ResolutionX - 1; columnB = 0; }
        else if (MathF.Abs(ax0 - bx1) <= tolerance) { columnA = 0; columnB = b.ResolutionX - 1; }
        if (columnA >= 0 && Aligned(az0, bz0, cellZ))
        {
            float from = MathF.Max(az0, bz0), to = MathF.Min(az1, bz1);
            if (to - from < cellZ * 0.5f) return false;
            int startA = (int)MathF.Round((from - az0) / cellZ), endA = (int)MathF.Round((to - az0) / cellZ);
            int offset = (int)MathF.Round((az0 - bz0) / cellZ);
            for (int za = startA; za <= endA; za++)
                Meet(a, placeA, columnA, za, b, placeB, columnB, za + offset);
            edgeA = (columnA, startA, columnA, endA);
            edgeB = (columnB, startA + offset, columnB, endA + offset);
            return true;
        }

        // An edge running along X (A's far side on B's near side, or the reverse).
        int rowA = -1, rowB = -1;
        if (MathF.Abs(az1 - bz0) <= tolerance) { rowA = a.ResolutionZ - 1; rowB = 0; }
        else if (MathF.Abs(az0 - bz1) <= tolerance) { rowA = 0; rowB = b.ResolutionZ - 1; }
        if (rowA >= 0 && Aligned(ax0, bx0, cellX))
        {
            float from = MathF.Max(ax0, bx0), to = MathF.Min(ax1, bx1);
            if (to - from < cellX * 0.5f) return false;
            int startA = (int)MathF.Round((from - ax0) / cellX), endA = (int)MathF.Round((to - ax0) / cellX);
            int offset = (int)MathF.Round((ax0 - bx0) / cellX);
            for (int xa = startA; xa <= endA; xa++)
                Meet(a, placeA, xa, rowA, b, placeB, xa + offset, rowB);
            edgeA = (startA, rowA, endA, rowA);
            edgeB = (startA + offset, rowB, endA + offset, rowB);
            return true;
        }
        return false;
    }

    // Not turned, and not flattened to nothing.
    private static bool Plain(RoomTransform place) =>
        MathF.Abs(place.RotationX) < 1e-3f && MathF.Abs(place.RotationY) < 1e-3f && MathF.Abs(place.RotationZ) < 1e-3f
        && MathF.Abs(place.ScaleY) > 1e-6f && place.ScaleX > 0f && place.ScaleZ > 0f;

    // Two grids' samples coincide along the edge.
    private static bool Aligned(float startA, float startB, float cell)
    {
        float steps = (startA - startB) / cell;
        return MathF.Abs(steps - MathF.Round(steps)) < 0.05f;
    }

    // One pair of edge samples set to the average of their heights in the world.
    private static void Meet(TerrainAsset a, RoomTransform placeA, int xa, int za, TerrainAsset b, RoomTransform placeB, int xb, int zb)
    {
        if (xa < 0 || za < 0 || xa >= a.ResolutionX || za >= a.ResolutionZ || xb < 0 || zb < 0 || xb >= b.ResolutionX || zb >= b.ResolutionZ) return;
        float worldA = placeA.Y + a.GetHeight(xa, za) * placeA.ScaleY;
        float worldB = placeB.Y + b.GetHeight(xb, zb) * placeB.ScaleY;
        float meet = (worldA + worldB) * 0.5f;
        a.SetHeight(xa, za, (meet - placeA.Y) / placeA.ScaleY);
        b.SetHeight(xb, zb, (meet - placeB.Y) / placeB.ScaleY);
    }
}
