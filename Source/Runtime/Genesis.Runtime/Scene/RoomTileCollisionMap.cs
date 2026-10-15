using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Runtime.Assets;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Scene;

/// <summary>
/// Collision for authored 2D tile layers, independent of drawing and ECS placeholder entities.
/// Axis sweeps prevent tunnelling, and a body already overlapping a tile is not pushed deeper into it.
/// Rotated tiles use conservative world-space bounds (not slopes).
/// Descriptor-backed layers use the Image Editor's solid tile indices; raw textures use all cells.
/// </summary>
public sealed class RoomTileCollisionMap
{
    private const int BucketSize = 128;
    private const float Epsilon = .0001f;
    /// <summary>How close two tile faces must be to count as one surface or a shared seam.</summary>
    private const float SurfaceTolerance = .01f;
    private static readonly ConditionalWeakTable<RoomAsset, RoomTileCollisionMap> Cache = new();
    [ThreadStatic] private static List<RectangleF> t_nearby;
    private readonly Dictionary<(int X, int Y), List<int>> _buckets = new();
    private readonly List<RectangleF> _solids = new();
    private readonly List<Layer> _layers = new();
    private sealed record Layer(RoomNode Node, Matrix4x4 Matrix, Matrix4x4 Inverse, int Columns);

    public static RoomTileCollisionMap Get(RoomAsset room, string project) => room == null ? null
        : Cache.GetValue(room, value => new RoomTileCollisionMap(value, project));
    public static void Invalidate(RoomAsset room) { if (room != null) Cache.Remove(room); }
    public static void ClearCache() => Cache.Clear();
    public int SolidCount => _solids.Count;
    public IReadOnlyList<RectangleF> Solids => _solids;

    public RoomTileCollisionMap(RoomAsset room, string project,
        Func<string, SpriteRuntimeAsset> resolve = null)
    {
        if (room == null || room.Dimension != RoomDimension.TwoD) return;
        Span<Vector3> local = stackalloc Vector3[4];
        foreach (RoomNode node in room.Nodes)
        {
            RoomTileLayerData tile = node.TileLayer;
            if (node.Kind != RoomNodeKind.TileLayer || tile == null || !RoomHierarchyTransforms.IsActive(room, node)
                || tile.CellWidth <= 0 || tile.CellHeight <= 0) continue;
            Matrix4x4 matrix = RoomHierarchyTransforms.Matrix(RoomHierarchyTransforms.World(room, node));
            if (!Matrix4x4.Invert(matrix, out Matrix4x4 inverse)) continue;
            SpriteRuntimeAsset image = resolve != null ? resolve(tile.Tileset)
                : Genesis.Runtime.Spatial.SpriteCollisionBounds.Load(project, tile.Tileset);
            int columns = image == null ? 1 : Math.Max(1,
                (image.Canvas.Width - tile.Margin * 2 + tile.Separation) / (tile.CellWidth + tile.Separation));
            _layers.Add(new Layer(node, matrix, inverse, columns));
            if (!tile.CollisionEnabled) continue;
            HashSet<int> selected = image == null ? null : new HashSet<int>(image.Usage?.Tileset?.Collision ?? new List<int>());
            foreach (RoomTileCell cell in tile.Cells)
            {
                if (selected != null && !selected.Contains(cell.TileY * columns + cell.TileX)) continue;
                CellLocalCorners(tile, cell, local);
                Vector3 a = Vector3.Transform(local[0], matrix);
                Vector3 b = Vector3.Transform(local[1], matrix);
                Vector3 c = Vector3.Transform(local[2], matrix);
                Vector3 d = Vector3.Transform(local[3], matrix);
                RectangleF box = RectangleF.FromLTRB(MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)),
                    MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)),
                    MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)),
                    MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)));
                Add(box);
            }
        }
    }

    private void Add(RectangleF box)
    {
        if (!Valid(box) || box.Width <= 0 || box.Height <= 0) return;
        int left = Bucket(box.Left), right = Bucket(box.Right - Epsilon);
        int top = Bucket(box.Top), bottom = Bucket(box.Bottom - Epsilon);
        if ((long)(right - left + 1) * (bottom - top + 1) > 65536)
            throw new InvalidOperationException("A collision tile is too large. Check its room transform scale.");
        int id = _solids.Count;
        _solids.Add(box);
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
            {
                if (!_buckets.TryGetValue((x, y), out List<int> bucket)) _buckets[(x, y)] = bucket = new();
                bucket.Add(id);
            }
    }

    private IEnumerable<RectangleF> Candidates(RectangleF box)
    {
        if (!Valid(box)) yield break;
        int left = Bucket(box.Left), right = Bucket(box.Right);
        int top = Bucket(box.Top), bottom = Bucket(box.Bottom);
        if ((long)(right - left + 1) * (bottom - top + 1) > 65536)
        {
            foreach (RectangleF solid in _solids) yield return solid;
            yield break;
        }
        HashSet<int> seen = new();
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                if (_buckets.TryGetValue((x, y), out List<int> bucket))
                    foreach (int id in bucket)
                        if (seen.Add(id)) yield return _solids[id];
    }

    public bool Intersects(RectangleF body)
    {
        foreach (RectangleF solid in Candidates(body))
            if (body.IntersectsWith(solid)) return true;
        return false;
    }

    /// <summary>
    /// Return the allowed displacement on one axis, stopping flush against solid tiles. A body that
    /// already overlaps a tile (its angle, scale or frame grew its mask, or it was placed there) is swept
    /// by <see cref="SweepOverlapping"/>; any other body gets exactly the plain sweep.
    /// </summary>
    public float Sweep(RectangleF body, float amount, bool horizontal)
    {
        if (!Valid(body) || !float.IsFinite(amount)) return 0;
        RectangleF end = body;
        end.Offset(horizontal ? amount : 0, horizontal ? 0 : amount);
        RectangleF swept = RectangleF.Union(body, end);
        swept.Inflate(Epsilon, Epsilon);
        float permitted = amount;
        bool overlapping = false;
        foreach (RectangleF solid in Candidates(swept))
        {
            bool overlap = horizontal ? body.Bottom > solid.Top + Epsilon && body.Top < solid.Bottom - Epsilon
                : body.Right > solid.Left + Epsilon && body.Left < solid.Right - Epsilon;
            if (!overlap) continue;
            float lead = horizontal ? body.Right : body.Bottom, trail = horizontal ? body.Left : body.Top;
            float near = horizontal ? solid.Left : solid.Top, far = horizontal ? solid.Right : solid.Bottom;
            if (amount > 0 && lead <= near + Epsilon) permitted = MathF.Min(permitted, MathF.Max(0, near - lead));
            else if (amount < 0 && trail >= far - Epsilon) permitted = MathF.Max(permitted, MathF.Min(0, far - trail));
            else if (lead > near + Epsilon && trail < far - Epsilon) overlapping = true;
        }
        return overlapping && amount != 0 ? SweepOverlapping(body, amount, horizontal, swept) : permitted;
    }

    /// <summary>
    /// The sweep of a body that already overlaps a tile. Moving out of any tile stays free, but:
    /// it is not pushed further in through a surface it reaches into from outside (a floor's top under a
    /// body poking into it, while it falls) when that is the tile's shallowest way out, as a
    /// separating-axis test picks the contact, so a body a pixel into a wall's side still moves up and down;
    /// and a tile that only continues, past a seam, the surface the body reaches into (the next floor tile,
    /// the next tile down a wall) does not stop it, so it slides along instead of catching on each tile.
    /// </summary>
    private float SweepOverlapping(RectangleF body, float amount, bool horizontal, RectangleF swept)
    {
        bool positive = amount > 0;
        // Only tiles touching the swept area matter: the surfaces and seams these rules look at all lie
        // within the body's own span across the move.
        RectangleF region = RectangleF.Inflate(swept, SurfaceTolerance, SurfaceTolerance);
        List<RectangleF> nearby = t_nearby ??= new List<RectangleF>();
        nearby.Clear();
        foreach (RectangleF solid in Candidates(region))
            if (solid.IntersectsWith(region)) nearby.Add(solid);
        Span(body, horizontal, out float b0, out float b1, out float bp0, out float bp1);
        float permitted = amount;
        foreach (RectangleF solid in nearby)
        {
            Span(solid, horizontal, out float s0, out float s1, out float sp0, out float sp1);
            if (bp1 <= sp0 + Epsilon || bp0 >= sp1 - Epsilon) continue;
            if (positive ? b1 <= s0 + Epsilon : b0 >= s1 - Epsilon)
            {
                if (!ContinuesSurface(nearby, horizontal, positive, bp0, bp1, s0, s1, sp0, sp1))
                    permitted = positive ? MathF.Min(permitted, MathF.Max(0, s0 - b1)) : MathF.Max(permitted, MathF.Min(0, s1 - b0));
            }
            else if (b1 > s0 + Epsilon && b0 < s1 - Epsilon
                && PushesDeeper(nearby, horizontal, positive, b0, b1, bp0, bp1, s0, s1, sp0, sp1)) return 0;
        }
        return permitted;
    }

    /// <summary>
    /// Whether moving on would push the body deeper into a tile it overlaps (s0..s1 along the move,
    /// sp0..sp1 across it): the body reaches in through the face it moves toward, part of it still
    /// outside, that face is open (not against another tile), and the depth is no more than the way out
    /// across the surface, measured along every tile sharing it so seams are not mistaken for edges.
    /// </summary>
    private static bool PushesDeeper(List<RectangleF> nearby, bool horizontal, bool positive,
        float b0, float b1, float bp0, float bp1, float s0, float s1, float sp0, float sp1)
    {
        if (positive ? b0 >= s0 - Epsilon : b1 <= s1 + Epsilon) return false;
        float face = positive ? s0 : s1;
        float depth = positive ? b1 - s0 : s1 - b0;
        float through = positive ? s1 - b0 : b1 - s0;
        if (depth >= through
            || Covered(nearby, horizontal, positive, face, MathF.Max(bp0, sp0), MathF.Min(bp1, sp1))) return false;
        float run0 = sp0, run1 = sp1;
        for (bool grown = true; grown;)
        {
            grown = false;
            foreach (RectangleF other in nearby)
            {
                Span(other, horizontal, out float o0, out float o1, out float op0, out float op1);
                if (MathF.Abs((positive ? o0 : o1) - face) > SurfaceTolerance
                    || op0 > run1 + SurfaceTolerance || op1 < run0 - SurfaceTolerance) continue;
                if (op0 < run0) { run0 = op0; grown = true; }
                if (op1 > run1) { run1 = op1; grown = true; }
            }
        }
        return depth <= MathF.Min(bp1 - run0, run1 - bp0);
    }

    /// <summary>
    /// Whether a tile ahead (t0..t1 along the move, tp0..tp1 across it) only continues, past a seam, a
    /// surface the body already reaches into across one of its sides: another tile ends where it begins,
    /// with that side at the same place and as deep as the body reaches.
    /// </summary>
    private static bool ContinuesSurface(List<RectangleF> nearby, bool horizontal, bool positive,
        float bp0, float bp1, float t0, float t1, float tp0, float tp1)
    {
        bool low = bp0 < tp0 - Epsilon && tp0 < bp1 - Epsilon;
        bool high = bp0 < tp1 - Epsilon && tp1 < bp1 - Epsilon;
        if (low == high) return false; // across neither side, or both: the tile's whole face is in the way
        float side = low ? tp0 : tp1, reach = low ? bp1 : bp0, face = positive ? t0 : t1;
        foreach (RectangleF other in nearby)
        {
            Span(other, horizontal, out float o0, out float o1, out float op0, out float op1);
            if (MathF.Abs((positive ? o1 : o0) - face) > SurfaceTolerance) continue;
            if (low ? MathF.Abs(op0 - side) <= SurfaceTolerance && op1 >= reach - SurfaceTolerance
                : MathF.Abs(op1 - side) <= SurfaceTolerance && op0 <= reach + SurfaceTolerance) return true;
        }
        return false;
    }

    /// <summary>Whether tiles ending at <paramref name="face"/> on the body's side cover from..to across it.</summary>
    private static bool Covered(List<RectangleF> nearby, bool horizontal, bool positive, float face, float from, float to)
    {
        for (bool grown = true; grown && from < to - SurfaceTolerance;)
        {
            grown = false;
            foreach (RectangleF other in nearby)
            {
                Span(other, horizontal, out float o0, out float o1, out float op0, out float op1);
                if (MathF.Abs((positive ? o1 : o0) - face) > SurfaceTolerance
                    || op0 > from + SurfaceTolerance || op1 <= from) continue;
                from = op1; grown = true;
            }
        }
        return from >= to - SurfaceTolerance;
    }

    /// <summary>A box's extent along the movement axis (a0..a1) and across it (p0..p1).</summary>
    private static void Span(RectangleF box, bool horizontal, out float a0, out float a1, out float p0, out float p1)
    {
        a0 = horizontal ? box.Left : box.Top; a1 = horizontal ? box.Right : box.Bottom;
        p0 = horizontal ? box.Top : box.Left; p1 = horizontal ? box.Bottom : box.Right;
    }

    public bool TryCell(float x, float y, string layerName, out RoomNode node, out RoomTileCell cell, out int index)
    {
        node = null; cell = null; index = -1;
        if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            Layer layer = _layers[i];
            if (!string.IsNullOrEmpty(layerName) && !string.Equals(layer.Node.Name, layerName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(layer.Node.Id, layerName, StringComparison.OrdinalIgnoreCase)) continue;
            Vector3 local = Vector3.Transform(new Vector3(x, y, 0), layer.Inverse);
            RoomTileLayerData tiles = layer.Node.TileLayer;

            // Search front-to-back because individually moved/resized tiles may overlap and no longer
            // occupy only their original integer grid cell. Plain grid tiles still take the same path
            // semantically; this just makes world queries agree with what the room actually draws.
            for (int c = tiles.Cells.Count - 1; c >= 0; c--)
            {
                RoomTileCell candidate = tiles.Cells[c];
                if (!CellContainsLocalPoint(tiles, candidate, local.X, local.Y)) continue;
                node = layer.Node; cell = candidate; index = candidate.TileY * layer.Columns + candidate.TileX;
                return true;
            }
        }
        return false;
    }

    private static void CellLocalCorners(RoomTileLayerData layer, RoomTileCell cell, Span<Vector3> corners)
    {
        float scaleX = MathF.Max(0.001f, MathF.Abs(cell.ScaleX));
        float scaleY = MathF.Max(0.001f, MathF.Abs(cell.ScaleY));
        float halfX = layer.CellWidth * scaleX * .5f;
        float halfY = layer.CellHeight * scaleY * .5f;
        float centerX = (cell.X + .5f) * layer.CellWidth + cell.OffsetX;
        float centerY = (cell.Y + .5f) * layer.CellHeight + cell.OffsetY;
        float radians = cell.Rotation * MathF.PI / 180f;
        float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
        Span<Vector2> local = stackalloc Vector2[4]
        {
            new(-halfX, -halfY), new(halfX, -halfY), new(-halfX, halfY), new(halfX, halfY),
        };
        for (int i = 0; i < 4; i++)
        {
            Vector2 value = local[i];
            corners[i] = new Vector3(centerX + value.X * cos - value.Y * sin, centerY + value.X * sin + value.Y * cos, 0);
        }
    }

    private static bool CellContainsLocalPoint(RoomTileLayerData layer, RoomTileCell cell, float x, float y)
    {
        float centerX = (cell.X + .5f) * layer.CellWidth + cell.OffsetX;
        float centerY = (cell.Y + .5f) * layer.CellHeight + cell.OffsetY;
        float radians = -cell.Rotation * MathF.PI / 180f;
        float dx = x - centerX, dy = y - centerY;
        float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
        float localX = dx * cos - dy * sin;
        float localY = dx * sin + dy * cos;
        float halfX = layer.CellWidth * MathF.Max(0.001f, MathF.Abs(cell.ScaleX)) * .5f;
        float halfY = layer.CellHeight * MathF.Max(0.001f, MathF.Abs(cell.ScaleY)) * .5f;
        return MathF.Abs(localX) <= halfX + Epsilon && MathF.Abs(localY) <= halfY + Epsilon;
    }

    private static int Bucket(float value) => (int)MathF.Floor(Math.Clamp(value, -100000000, 100000000) / BucketSize);
    private static bool Valid(RectangleF box) => float.IsFinite(box.X) && float.IsFinite(box.Y)
        && float.IsFinite(box.Right) && float.IsFinite(box.Bottom) && box.Width > 0 && box.Height > 0;
}
