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
/// Axis sweeps prevent tunnelling. Rotated tiles use conservative world-space bounds (not slopes).
/// Descriptor-backed layers use the Image Editor's solid tile indices; raw textures use all cells.
/// </summary>
public sealed class RoomTileCollisionMap
{
    private const int BucketSize = 128;
    private const float Epsilon = .0001f;
    private static readonly ConditionalWeakTable<RoomAsset, RoomTileCollisionMap> Cache = new();
    private readonly Dictionary<(int X, int Y), List<int>> _buckets = new();
    private readonly List<RectangleF> _solids = new();
    private readonly List<Layer> _layers = new();
    private sealed record Layer(RoomNode Node, Matrix4x4 Matrix, Matrix4x4 Inverse, int Columns);

    public static RoomTileCollisionMap Get(RoomAsset room, string project) => room == null ? null
        : Cache.GetValue(room, value => new RoomTileCollisionMap(value, project));
    public static void Invalidate(RoomAsset room) { if (room != null) Cache.Remove(room); }
    public static void ClearCache() => Cache.Clear();
    public int SolidCount => _solids.Count;

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

    /// <summary>Return the allowed displacement on one axis, stopping flush against solid tiles.</summary>
    public float Sweep(RectangleF body, float amount, bool horizontal)
    {
        if (!Valid(body) || !float.IsFinite(amount)) return 0;
        RectangleF end = body;
        end.Offset(horizontal ? amount : 0, horizontal ? 0 : amount);
        RectangleF swept = RectangleF.Union(body, end);
        swept.Inflate(Epsilon, Epsilon);
        float permitted = amount;
        foreach (RectangleF solid in Candidates(swept))
        {
            bool overlap = horizontal ? body.Bottom > solid.Top + Epsilon && body.Top < solid.Bottom - Epsilon
                : body.Right > solid.Left + Epsilon && body.Left < solid.Right - Epsilon;
            if (!overlap) continue;
            float lead = horizontal ? body.Right : body.Bottom, trail = horizontal ? body.Left : body.Top;
            float near = horizontal ? solid.Left : solid.Top, far = horizontal ? solid.Right : solid.Bottom;
            if (amount > 0 && lead <= near + Epsilon) permitted = MathF.Min(permitted, MathF.Max(0, near - lead));
            if (amount < 0 && trail >= far - Epsilon) permitted = MathF.Max(permitted, MathF.Min(0, far - trail));
        }
        return permitted;
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
