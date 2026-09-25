namespace Genesis.Application.Editors.Image.Imaging;

public sealed class ImageFloatingSelection
{
    public ImageFloatingSelection(byte[] pixels, int width, int height, Point origin, byte[]? selectionMask = null)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
            throw new ArgumentException("Floating selection dimensions do not match pixel buffer.");
        Pixels = (byte[])pixels.Clone();
        Width = width;
        Height = height;
        Origin = origin;
        _selectionMask = selectionMask;
    }

    public byte[] Pixels { get; }
    private readonly byte[]? _selectionMask;
    public int Width { get; }
    public int Height { get; }
    public Point Origin { get; set; }

    public Rectangle Bounds => new(Origin.X, Origin.Y, Width, Height);

    public static ImageFloatingSelection? FromClipboard(Point origin)
    {
        if (!ImageSelectionClipboard.HasContent || ImageSelectionClipboard.Pixels == null)
            return null;
        return new ImageFloatingSelection(
            ImageSelectionClipboard.Pixels,
            ImageSelectionClipboard.Width,
            ImageSelectionClipboard.Height,
            origin);
    }

    public static ImageFloatingSelection Lift(
        ImageLayerBuffer layer,
        ImageSelectionMask selection,
        int canvasWidth,
        int canvasHeight)
    {
        Rectangle bounds = selection.GetBounds();
        if (bounds.IsEmpty)
            throw new InvalidOperationException("Cannot lift an empty selection.");

        byte[] buffer = new byte[bounds.Width * bounds.Height * 4];
        byte[] mask = new byte[bounds.Width * bounds.Height];
        for (int y = 0; y < bounds.Height; y++)
        {
            for (int x = 0; x < bounds.Width; x++)
            {
                int canvasX = bounds.Left + x;
                int canvasY = bounds.Top + y;
                int destination = (y * bounds.Width + x) * 4;
                if (!selection.Contains(canvasX, canvasY))
                    continue;
                mask[y*bounds.Width+x] = 255;
                Color pixel = RasterOperations.GetPixel(layer.Pixels, canvasWidth, canvasHeight, canvasX, canvasY);
                buffer[destination] = pixel.R;
                buffer[destination + 1] = pixel.G;
                buffer[destination + 2] = pixel.B;
                buffer[destination + 3] = pixel.A;
            }
        }

        ImageSelectionClipboard.ClearSelectionPixels(layer, selection, canvasWidth, canvasHeight);
        return new ImageFloatingSelection(buffer, bounds.Width, bounds.Height, bounds.Location,mask);
    }

    public void ApplySelectionMask(ImageSelectionMask selection, int canvasWidth, int canvasHeight)
    {
        selection.Clear();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int source = (y * Width + x) * 4;
                if ((_selectionMask?[y*Width+x] ?? Pixels[source+3]) == 0) continue;
                int canvasX = Origin.X + x;
                int canvasY = Origin.Y + y;
                if ((uint)canvasX >= (uint)canvasWidth || (uint)canvasY >= (uint)canvasHeight)
                    continue;
                selection.SetPixel(canvasX, canvasY, add: true);
            }
        }
    }

    public void Stamp(ImageLayerBuffer layer, int canvasWidth, int canvasHeight, ImageSelectionMask? selection = null)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int source = (y * Width + x) * 4;
                if (Pixels[source + 3] == 0) continue;
                int canvasX = Origin.X + x;
                int canvasY = Origin.Y + y;
                if ((uint)canvasX >= (uint)canvasWidth || (uint)canvasY >= (uint)canvasHeight)
                    continue;
                if (selection?.Contains(canvasX, canvasY) == false) continue;
                RasterOperations.SetPixel(
                    layer.Pixels,
                    canvasWidth,
                    canvasHeight,
                    canvasX,
                    canvasY,
                    Color.FromArgb(Pixels[source + 3], Pixels[source], Pixels[source + 1], Pixels[source + 2]),
                    1f);
            }
        }
    }

    public void CompositeOnto(byte[] rgba, int canvasWidth, int canvasHeight)
    {
        if (rgba.Length != canvasWidth * canvasHeight * 4) return;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int source = (y * Width + x) * 4;
                byte alpha = Pixels[source + 3];
                if (alpha == 0) continue;
                int canvasX = Origin.X + x;
                int canvasY = Origin.Y + y;
                if ((uint)canvasX >= (uint)canvasWidth || (uint)canvasY >= (uint)canvasHeight)
                    continue;
                int destination = (canvasY * canvasWidth + canvasX) * 4;
                float sa = alpha / 255f;
                float da = rgba[destination + 3] / 255f;
                float outA = sa + da * (1f - sa);
                if (outA <= 0f) continue;
                for (int channel = 0; channel < 3; channel++)
                {
                    float sourceValue = Pixels[source + channel] / 255f;
                    float destValue = rgba[destination + channel] / 255f;
                    float blended = (sourceValue * sa + destValue * da * (1f - sa)) / outA;
                    rgba[destination + channel] = (byte)Math.Clamp((int)MathF.Round(blended * 255f), 0, 255);
                }
                rgba[destination + 3] = (byte)Math.Clamp((int)MathF.Round(outA * 255f), 0, 255);
            }
        }
    }
}
