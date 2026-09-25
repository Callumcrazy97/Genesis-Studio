using System.Numerics;

namespace Genesis.Application.Editors.Image.Imaging;

public enum ImageBlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Add,
    Subtract,
    Darken,
    Lighten,
}

public enum ImageMaterialChannel
{
    Color,
    Normal,
    Roughness,
    Metallic,
    Emission,
    Height,
    Occlusion,
}

public sealed class ImageLayerBuffer
{
    public override string ToString()=>Name;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Layer";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public bool AlphaLocked { get; set; }
    public bool Clipping { get; set; }
    public float Opacity { get; set; } = 1f;
    public ImageBlendMode BlendMode { get; set; }
    public ImageMaterialChannel Channel { get; set; }
    public byte[] Pixels { get; set; } = [];

    public ImageLayerBuffer Clone() => new()
    {
        Id = Id,
        Name = Name,
        Visible = Visible,
        Locked = Locked,
        AlphaLocked = AlphaLocked,
        Clipping = Clipping,
        Opacity = Opacity,
        BlendMode = BlendMode,
        Channel = Channel,
        Pixels = (byte[])Pixels.Clone(),
    };
}

public sealed class ImageFrameBuffer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Frame";
    public int DurationMilliseconds { get; set; } = 100;
    public List<ImageLayerBuffer> Layers { get; } = [];

    public ImageFrameBuffer Clone()
    {
        ImageFrameBuffer copy = new()
        {
            Id = Guid.NewGuid(),
            Name = Name + " Copy",
            DurationMilliseconds = DurationMilliseconds,
        };
        copy.Layers.AddRange(Layers.Select(layer => layer.Clone()));
        return copy;
    }
}

/// <summary>
/// Editable lossless RGBA workspace. Sprite metadata is owned by Core's ImageDocumentSession;
/// this class owns editor-only frame/layer pixel buffers and dirty composite caching.
/// </summary>
public sealed class ImageWorkspace
{
    private byte[]? _composite;
    private long _compositeAtVersion = -1;
    private int _compositeFrameIndex = -1;
    private ImageMaterialChannel _compositeChannel;
    private long _version;

    public ImageWorkspace(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16_384 || height > 16_384)
            throw new ArgumentOutOfRangeException(nameof(width), "Canvas dimensions must be 1..16384.");
        Width = width;
        Height = height;
        Selection = new ImageSelectionMask(width, height);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public List<ImageFrameBuffer> Frames { get; } = [];
    public int SelectedFrameIndex { get; set; }
    public int SelectedLayerIndex { get; set; }
    public ImageSelectionMask Selection { get; private set; }
    public long Version => _version;

    public ImageFrameBuffer? CurrentFrame =>
        Frames.Count == 0 ? null : Frames[Math.Clamp(SelectedFrameIndex, 0, Frames.Count - 1)];

    public ImageLayerBuffer? CurrentLayer
    {
        get
        {
            ImageFrameBuffer? frame = CurrentFrame;
            return frame == null || frame.Layers.Count == 0
                ? null
                : frame.Layers[Math.Clamp(SelectedLayerIndex, 0, frame.Layers.Count - 1)];
        }
    }

    public static ImageWorkspace CreateBlank(int width, int height, Color fill)
    {
        ImageWorkspace workspace = new(width, height);
        ImageFrameBuffer frame = new() { Name = "Frame 1" };
        ImageLayerBuffer layer = new()
        {
            Name = "Layer 1",
            Pixels = new byte[width * height * 4],
        };
        if (fill.A > 0)
            RasterOperations.Clear(layer.Pixels, width, height, fill);
        frame.Layers.Add(layer);
        workspace.Frames.Add(frame);
        workspace.Touch();
        return workspace;
    }

    /// <summary>Mutates the shared workspace in place so viewer and detached editor both refresh.</summary>
    public void ReplaceWith(ImageWorkspace source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Width = source.Width;
        Height = source.Height;
        Frames.Clear();
        foreach (ImageFrameBuffer frame in source.Frames)
        {
            ImageFrameBuffer copy = new()
            {
                Id = frame.Id,
                Name = frame.Name,
                DurationMilliseconds = frame.DurationMilliseconds,
            };
            foreach (ImageLayerBuffer layer in frame.Layers)
            {
                copy.Layers.Add(new ImageLayerBuffer
                {
                    Id = layer.Id,
                    Name = layer.Name,
                    Visible = layer.Visible,
                    Locked = layer.Locked,
                    AlphaLocked = layer.AlphaLocked,
                    Clipping = layer.Clipping,
                    Opacity = layer.Opacity,
                    BlendMode = layer.BlendMode,
                    Channel = layer.Channel,
                    Pixels = (byte[])layer.Pixels.Clone(),
                });
            }
            Frames.Add(copy);
        }
        SelectedFrameIndex = Math.Clamp(source.SelectedFrameIndex, 0, Math.Max(0, Frames.Count - 1));
        SelectedLayerIndex = Math.Clamp(source.SelectedLayerIndex, 0, Math.Max(0, CurrentFrame?.Layers.Count - 1 ?? 0));
        Selection = new ImageSelectionMask(Width, Height);
        _composite = null;
        InvalidateComposite();
        Touch();
    }

    public static ImageWorkspace FromRgba(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("Image byte count does not match dimensions.", nameof(rgba));
        ImageWorkspace workspace = CreateBlank(width, height, Color.Transparent);
        workspace.CurrentLayer!.Pixels = rgba.AsSpan(0, width * height * 4).ToArray();
        workspace.Touch();
        return workspace;
    }

    public ImageLayerBuffer AddLayer(string? name = null)
    {
        ImageFrameBuffer frame = EnsureFrame();
        ImageLayerBuffer layer = new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Layer {frame.Layers.Count + 1}" : name,
            Pixels = new byte[Width * Height * 4],
        };
        frame.Layers.Add(layer);
        SelectedLayerIndex = frame.Layers.Count - 1;
        Touch();
        return layer;
    }

    public ImageFrameBuffer AddFrame(bool duplicateCurrent)
    {
        ImageFrameBuffer frame = duplicateCurrent && CurrentFrame != null
            ? CurrentFrame.Clone()
            : new ImageFrameBuffer { Name = $"Frame {Frames.Count + 1}" };
        if (frame.Layers.Count == 0)
        {
            frame.Layers.Add(new ImageLayerBuffer
            {
                Name = "Layer 1",
                Pixels = new byte[Width * Height * 4],
            });
        }
        Frames.Add(frame);
        SelectedFrameIndex = Frames.Count - 1;
        SelectedLayerIndex = 0;
        Touch();
        return frame;
    }

    public void DeleteFrame(int index)
    {
        if ((uint)index >= (uint)Frames.Count) return;
        Frames.RemoveAt(index);
        SelectedFrameIndex = Math.Clamp(SelectedFrameIndex, 0, Math.Max(0, Frames.Count - 1));
        Touch();
    }

    public void MoveFrame(int from, int to)
    {
        if ((uint)from >= (uint)Frames.Count || (uint)to >= (uint)Frames.Count || from == to) return;
        ImageFrameBuffer frame = Frames[from];
        Frames.RemoveAt(from);
        Frames.Insert(to, frame);
        SelectedFrameIndex = to;
        Touch();
    }

    public void CropCanvas(Rectangle bounds)
    {
        bounds = Rectangle.Intersect(bounds, new Rectangle(0, 0, Width, Height));
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        foreach (ImageFrameBuffer frame in Frames)
        {
            foreach (ImageLayerBuffer layer in frame.Layers)
                layer.Pixels = CropRegion(layer.Pixels, Width, Height, bounds);
        }

        Width = bounds.Width;
        Height = bounds.Height;
        Selection.ResizeForCanvas(bounds.Width, bounds.Height);
        InvalidateComposite();
        Touch();
    }

    /// <summary>
    /// Changes the canvas size, keeping existing pixels and placing them at <paramref name="offset"/>.
    /// </summary>
    /// <remarks>
    /// Growing is not the same as scaling: nothing is resampled, so pixel art survives a canvas
    /// change intact. The offset is what the anchor grid in the resize dialog chooses — the whole
    /// question "where does my existing art end up when the canvas gets bigger" has no default
    /// answer that is right for everyone, so it is asked rather than assumed.
    /// </remarks>
    public void ResizeCanvas(int width, int height, Point offset)
    {
        int newWidth = Math.Max(1, width);
        int newHeight = Math.Max(1, height);
        if (newWidth == Width && newHeight == Height && offset == Point.Empty)
        {
            return;
        }

        foreach (ImageFrameBuffer frame in Frames)
        {
            foreach (ImageLayerBuffer layer in frame.Layers)
            {
                layer.Pixels = CopyInto(layer.Pixels, Width, Height, newWidth, newHeight, offset);
            }
        }

        Width = newWidth;
        Height = newHeight;
        Selection.ResizeForCanvas(newWidth, newHeight);
        InvalidateComposite();
        Touch();
    }

    private static byte[] CopyInto(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        Point offset)
    {
        byte[] result = new byte[targetWidth * targetHeight * 4];
        for (int y = 0; y < sourceHeight; y++)
        {
            int targetY = y + offset.Y;
            if ((uint)targetY >= (uint)targetHeight) continue;

            for (int x = 0; x < sourceWidth; x++)
            {
                int targetX = x + offset.X;
                if ((uint)targetX >= (uint)targetWidth) continue;

                Buffer.BlockCopy(
                    source,
                    ((y * sourceWidth) + x) * 4,
                    result,
                    ((targetY * targetWidth) + targetX) * 4,
                    4);
            }
        }

        return result;
    }

    internal void RestoreCanvasState(
        int width,
        int height,
        byte[] selectionMask,
        IReadOnlyList<WorkspaceSnapshot.FrameSnapshot> frames)
    {
        Width = width;
        Height = height;
        Selection.RestoreMask(selectionMask, width, height);
        for (int frameIndex = 0; frameIndex < Frames.Count && frameIndex < frames.Count; frameIndex++)
        {
            ImageFrameBuffer frame = Frames[frameIndex];
            WorkspaceSnapshot.FrameSnapshot source = frames[frameIndex];
            frame.Name = source.Name;
            frame.DurationMilliseconds = source.DurationMilliseconds;
            for (int layerIndex = 0; layerIndex < frame.Layers.Count && layerIndex < source.Layers.Count; layerIndex++)
                frame.Layers[layerIndex].Pixels = (byte[])source.Layers[layerIndex].Pixels.Clone();
        }
        InvalidateComposite();
        Touch();
    }

    /// <summary>Raised whenever the workspace content version advances (draw, fill, layer
    /// edits…). The Image Viewer subscribes so edits made in the separate Image Editor window
    /// appear live without re-activating the viewer tab.</summary>
    public event EventHandler? Changed;

    public void Touch()
    {
        _version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void InvalidateComposite() => _compositeAtVersion = long.MinValue;

    public byte[] CompositeCurrentFrame(ImageMaterialChannel channel = ImageMaterialChannel.Color)
    {
        int frameIndex = Math.Clamp(SelectedFrameIndex, 0, Math.Max(0, Frames.Count - 1));
        if (_composite != null
            && _compositeAtVersion == _version
            && _compositeFrameIndex == frameIndex
            && _compositeChannel == channel)
            return _composite;

        // Reallocated when the canvas size changes, not just cleared: the cache outlives a resize,
        // and a buffer still sized for the old canvas is handed to the viewport as though it were
        // the new one. Growing then throws ("dimensions do not match its byte buffer") and
        // shrinking silently composites into a buffer too large — both from the same stale array.
        int required = Width * Height * 4;
        if (_composite is null || _composite.Length != required)
        {
            _composite = new byte[required];
        }
        else
        {
            Array.Clear(_composite);
        }
        ImageFrameBuffer? frame = Frames.Count == 0 ? null : Frames[frameIndex];
        if (frame != null)
        {
            foreach (ImageLayerBuffer layer in frame.Layers)
            {
                if (!layer.Visible || layer.Channel != channel || layer.Pixels.Length == 0)
                    continue;
                if (layer.Pixels.Length == _composite.Length)
                {
                    CompositeLayer(_composite, layer.Pixels, layer.Opacity, layer.BlendMode);
                    continue;
                }

                byte[] normalized = NormalizeLayerPixels(layer.Pixels, Width, Height);
                if (normalized.Length == _composite.Length)
                    CompositeLayer(_composite, normalized, layer.Opacity, layer.BlendMode);
            }
        }
        _compositeAtVersion = _version;
        _compositeFrameIndex = frameIndex;
        _compositeChannel = channel;
        return _composite;
    }

    public (int Width, int Height, byte[] Pixels) CompositeCurrentFrameFor(
        int frameIndex,
        ImageMaterialChannel channel = ImageMaterialChannel.Color,
        ImageLayerBuffer? previewLayer = null, byte[]? previewPixels = null,
        bool allChannels = false, Func<ImageLayerBuffer,bool>? include = null, bool includeHidden = false)
    {
        if ((uint)frameIndex >= (uint)Frames.Count)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        byte[] pixels = new byte[Width * Height * 4];
        foreach (ImageLayerBuffer layer in Frames[frameIndex].Layers)
        {
            if ((!layer.Visible && !includeHidden) || (!allChannels && layer.Channel != channel) || layer.Pixels.Length == 0 || (include != null && !include(layer)))
                continue;
            byte[] source = layer == previewLayer && previewPixels != null ? previewPixels : layer.Pixels.Length == pixels.Length
                ? layer.Pixels
                : NormalizeLayerPixels(layer.Pixels, Width, Height);
            if (source.Length != pixels.Length)
                continue;
            CompositeLayer(pixels, source, layer.Opacity, layer.BlendMode);
        }
        return (Width, Height, pixels);
    }

    private ImageFrameBuffer EnsureFrame()
    {
        if (CurrentFrame != null) return CurrentFrame;
        return AddFrame(duplicateCurrent: false);
    }

    private static void CompositeLayer(byte[] destination, byte[] source, float opacity, ImageBlendMode mode)
        => Genesis.Runtime.Imaging.PixelLayerCompositor.Composite(destination, source, opacity,
            (Genesis.Runtime.Imaging.PixelBlendMode)mode);

    private static byte[] NormalizeLayerPixels(byte[] source, int width, int height)
    {
        int expected = checked(width * height * 4);
        if (source.Length == expected)
            return source;

        byte[] normalized = new byte[expected];
        if (source.Length < expected)
        {
            Array.Copy(source, normalized, source.Length);
            return normalized;
        }

        int sourceWidth = GuessWidth(source.Length, width, height);
        if (sourceWidth <= 0)
        {
            Array.Copy(source, normalized, expected);
            return normalized;
        }

        int sourceHeight = source.Length / (sourceWidth * 4);
        for (int y = 0; y < height; y++)
        {
            int sourceY = y < sourceHeight ? y : sourceHeight - 1;
            for (int x = 0; x < width; x++)
            {
                int sourceX = x < sourceWidth ? x : sourceWidth - 1;
                int sourceIndex = (sourceY * sourceWidth + sourceX) * 4;
                int destinationIndex = (y * width + x) * 4;
                normalized[destinationIndex] = source[sourceIndex];
                normalized[destinationIndex + 1] = source[sourceIndex + 1];
                normalized[destinationIndex + 2] = source[sourceIndex + 2];
                normalized[destinationIndex + 3] = source[sourceIndex + 3];
            }
        }

        return normalized;
    }

    private static byte[] CropRegion(byte[] pixels, int sourceWidth, int sourceHeight, Rectangle bounds)
    {
        byte[] result = new byte[bounds.Width * bounds.Height * 4];
        for (int y = 0; y < bounds.Height; y++)
        {
            int sourceY = bounds.Top + y;
            if ((uint)sourceY >= (uint)sourceHeight) continue;
            int sourceOffset = (sourceY * sourceWidth + bounds.Left) * 4;
            int destinationOffset = y * bounds.Width * 4;
            int rowBytes = bounds.Width * 4;
            if (sourceOffset + rowBytes <= pixels.Length)
                Buffer.BlockCopy(pixels, sourceOffset, result, destinationOffset, rowBytes);
        }
        return result;
    }

    private static int GuessWidth(int byteLength, int fallbackWidth, int fallbackHeight)
    {
        if (fallbackWidth > 0 && byteLength == fallbackWidth * fallbackHeight * 4)
            return fallbackWidth;

        int pixels = byteLength / 4;
        if (pixels <= 0)
            return 0;

        int width = (int)MathF.Round(MathF.Sqrt(pixels));
        while (width > 1 && pixels % width != 0)
            width--;
        return width > 0 ? width : fallbackWidth;
    }
}

public sealed class ImageSelectionMask
{
    private byte[] _mask;

    public ImageSelectionMask(int width, int height)
    {
        Width = width;
        Height = height;
        _mask = new byte[width * height];
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasSelection { get; private set; }

    public bool Contains(int x, int y) =>
        !HasSelection || ((uint)x < (uint)Width && (uint)y < (uint)Height && _mask[y * Width + x] != 0);

    public void Clear()
    {
        Array.Clear(_mask);
        HasSelection = false;
    }

    public void SelectAll()
    {
        Array.Fill(_mask, (byte)255);
        HasSelection = true;
    }

    public void SetRectangle(Rectangle rectangle, bool add, bool subtract)
    {
        if (!add && !subtract) Array.Clear(_mask);
        Rectangle bounds = Rectangle.Intersect(rectangle, new Rectangle(0, 0, Width, Height));
        for (int y = bounds.Top; y < bounds.Bottom; y++)
            for (int x = bounds.Left; x < bounds.Right; x++)
                _mask[y * Width + x] = subtract ? (byte)0 : (byte)255;
        HasSelection = _mask.Any(value => value != 0);
    }

    public void SetEllipse(Rectangle rectangle, bool add, bool subtract)
    {
        if (!add && !subtract) Array.Clear(_mask);
        Rectangle bounds = Rectangle.Intersect(rectangle, new Rectangle(0, 0, Width, Height));
        if (bounds.IsEmpty) return;
        float rx = Math.Max(0.5f, bounds.Width * 0.5f);
        float ry = Math.Max(0.5f, bounds.Height * 0.5f);
        float cx = bounds.Left + rx;
        float cy = bounds.Top + ry;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                float nx = (x + 0.5f - cx) / rx;
                float ny = (y + 0.5f - cy) / ry;
                if (nx * nx + ny * ny > 1f) continue;
                _mask[y * Width + x] = subtract ? (byte)0 : (byte)255;
            }
        }
        HasSelection = _mask.Any(value => value != 0);
    }

    public void SetPolygon(IReadOnlyList<Point> points, bool add, bool subtract)
    {
        if (points.Count < 3) return;
        if (!add && !subtract) Array.Clear(_mask);
        int minX = Width;
        int minY = Height;
        int maxX = -1;
        int maxY = -1;
        foreach (Point point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }
        Rectangle bounds = Rectangle.Intersect(
            Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1),
            new Rectangle(0, 0, Width, Height));
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (!ContainsPointInPolygon(x + 0.5f, y + 0.5f, points)) continue;
                _mask[y * Width + x] = subtract ? (byte)0 : (byte)255;
            }
        }
        HasSelection = _mask.Any(value => value != 0);
    }

    public void Offset(int deltaX, int deltaY)
    {
        if (!HasSelection) return;
        byte[] shifted = new byte[_mask.Length];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (_mask[y * Width + x] == 0) continue;
                int nx = x + deltaX;
                int ny = y + deltaY;
                if ((uint)nx >= (uint)Width || (uint)ny >= (uint)Height) continue;
                shifted[ny * Width + nx] = 255;
            }
        }
        _mask = shifted;
        HasSelection = _mask.Any(value => value != 0);
    }

    public void SetPixel(int x, int y, bool add)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        _mask[y * Width + x] = 255;
        if (add || HasSelection) HasSelection = true;
    }

    public void ResizeForCanvas(int width, int height)
    {
        Width = width;
        Height = height;
        _mask = new byte[width * height];
        HasSelection = false;
    }

    public byte[] CloneMask() => (byte[])_mask.Clone();

    public void RestoreMask(byte[] mask, int width, int height)
    {
        Width = width;
        Height = height;
        _mask = mask.Length == width * height ? (byte[])mask.Clone() : new byte[width * height];
        HasSelection = _mask.Any(value => value != 0);
    }

    public void Invert()
    {
        for (int i = 0; i < _mask.Length; i++)
            _mask[i] = (byte)(255 - _mask[i]);
        HasSelection = _mask.Any(value => value != 0);
    }

    public void FloodSelectFrom(
        byte[] rgba,
        int width,
        int height,
        int startX,
        int startY,
        int tolerance,
        bool add,
        bool subtract)
    {
        if ((uint)startX >= (uint)width || (uint)startY >= (uint)height) return;
        Color target = RasterOperations.GetPixel(rgba, width, height, startX, startY);
        if (!add && !subtract) Array.Clear(_mask);
        bool[] visited = new bool[width * height];
        Queue<Point> queue = new();
        queue.Enqueue(new Point(startX, startY));
        while (queue.Count > 0)
        {
            Point point = queue.Dequeue();
            if ((uint)point.X >= (uint)width || (uint)point.Y >= (uint)height) continue;
            int index = point.Y * width + point.X;
            if (visited[index]) continue;
            visited[index] = true;
            if (RasterOperations.ColorDistance(GetPixel(rgba, width, height, point.X, point.Y), target) > tolerance)
                continue;
            _mask[index] = subtract ? (byte)0 : (byte)255;
            queue.Enqueue(new Point(point.X - 1, point.Y));
            queue.Enqueue(new Point(point.X + 1, point.Y));
            queue.Enqueue(new Point(point.X, point.Y - 1));
            queue.Enqueue(new Point(point.X, point.Y + 1));
        }
        HasSelection = _mask.Any(value => value != 0);
    }

    private static Color GetPixel(byte[] rgba, int width, int height, int x, int y) =>
        RasterOperations.GetPixel(rgba, width, height, x, y);

    public ReadOnlySpan<byte> AsSpan() => _mask;

    private static bool ContainsPointInPolygon(float x, float y, IReadOnlyList<Point> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Point a = polygon[i];
            Point b = polygon[j];
            bool intersects = (a.Y > y) != (b.Y > y)
                && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y + float.Epsilon) + a.X;
            if (intersects) inside = !inside;
        }
        return inside;
    }

    public Rectangle GetBounds()
    {
        if (!HasSelection) return Rectangle.Empty;
        int minX = Width;
        int minY = Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (_mask[y * Width + x] == 0) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}
