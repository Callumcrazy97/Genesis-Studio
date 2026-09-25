using Genesis.Application.Core.Images;

namespace Genesis.Application.Editors.Image.Imaging;

public sealed class PixelStrokeRecorder
{
    private const int TileSize = 64;
    private readonly ImageWorkspace _workspace;
    private readonly ImageLayerBuffer _layer;
    private readonly Dictionary<Point, CapturedTile> _tiles = [];

    public PixelStrokeRecorder(ImageWorkspace workspace, ImageLayerBuffer layer)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
    }

    public void Capture(Rectangle bounds)
    {
        Rectangle canvas = new(0, 0, _workspace.Width, _workspace.Height);
        Rectangle clipped = Rectangle.Intersect(bounds, canvas);
        if (clipped.IsEmpty) return;
        int startX = clipped.Left / TileSize;
        int endX = (clipped.Right - 1) / TileSize;
        int startY = clipped.Top / TileSize;
        int endY = (clipped.Bottom - 1) / TileSize;
        for (int ty = startY; ty <= endY; ty++)
        {
            for (int tx = startX; tx <= endX; tx++)
            {
                Point key = new(tx, ty);
                if (_tiles.ContainsKey(key)) continue;
                Rectangle tileBounds = new(
                    tx * TileSize,
                    ty * TileSize,
                    Math.Min(TileSize, _workspace.Width - tx * TileSize),
                    Math.Min(TileSize, _workspace.Height - ty * TileSize));
                _tiles.Add(key, new CapturedTile(tileBounds, Read(tileBounds)));
            }
        }
    }

    public IImageDocumentCommand Complete(string description)
    {
        List<WorkspaceTileDelta> deltas = [];
        foreach (CapturedTile tile in _tiles.Values)
        {
            byte[] after = Read(tile.Bounds);
            if (!tile.Before.AsSpan().SequenceEqual(after))
                deltas.Add(new WorkspaceTileDelta(tile.Bounds, tile.Before, after));
        }
        return new WorkspacePixelTileCommand(description, _workspace, _layer, deltas);
    }

    private byte[] Read(Rectangle bounds)
    {
        byte[] result = new byte[bounds.Width * bounds.Height * 4];
        int rowBytes = bounds.Width * 4;
        for (int y = 0; y < bounds.Height; y++)
        {
            int sourceOffset = ((bounds.Y + y) * _workspace.Width + bounds.X) * 4;
            Buffer.BlockCopy(_layer.Pixels, sourceOffset, result, y * rowBytes, rowBytes);
        }
        return result;
    }

    private sealed record CapturedTile(Rectangle Bounds, byte[] Before);
}

public sealed record WorkspaceTileDelta(Rectangle Bounds, byte[] Before, byte[] After);

public sealed class WorkspacePixelTileCommand : IImageDocumentCommand
{
    private readonly ImageWorkspace _workspace;
    private readonly ImageLayerBuffer _layer;
    private readonly IReadOnlyList<WorkspaceTileDelta> _deltas;

    public WorkspacePixelTileCommand(
        string description,
        ImageWorkspace workspace,
        ImageLayerBuffer layer,
        IReadOnlyList<WorkspaceTileDelta> deltas)
    {
        Description = description;
        _workspace = workspace;
        _layer = layer;
        _deltas = deltas;
        EstimatedByteSize = deltas.Sum(delta => (long)delta.Before.Length + delta.After.Length);
    }

    public string Description { get; }
    public long EstimatedByteSize { get; }
    public void Execute(ImageDocumentSession session) => Apply(after: true);
    public void Undo(ImageDocumentSession session) => Apply(after: false);

    private void Apply(bool after)
    {
        foreach (WorkspaceTileDelta delta in _deltas)
        {
            byte[] source = after ? delta.After : delta.Before;
            int rowBytes = delta.Bounds.Width * 4;
            for (int y = 0; y < delta.Bounds.Height; y++)
            {
                int destinationOffset = ((delta.Bounds.Y + y) * _workspace.Width + delta.Bounds.X) * 4;
                Buffer.BlockCopy(source, y * rowBytes, _layer.Pixels, destinationOffset, rowBytes);
            }
        }
        _workspace.Touch();
    }
}
