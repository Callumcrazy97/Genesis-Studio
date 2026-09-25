namespace Genesis.Application.Editors.Image.Imaging;

internal static class ImageSelectionClipboard
{
    private static bool _editorOwned;
    private static uint _ownedSequence;

    public static byte[]? Pixels { get; private set; }
    public static int Width { get; private set; }
    public static int Height { get; private set; }
    public static bool HasContent => Pixels is { Length: > 0 } && Width > 0 && Height > 0;

    /// <summary>
    /// True when the last Copy/Cut in this editor still owns the Win32 clipboard sequence.
    /// A later copy from another app changes the sequence and clears this preference.
    /// </summary>
    public static bool IsEditorOwned =>
        _editorOwned
        && HasContent
        && NativeClipboardSequence.Current == _ownedSequence;

    /// <summary>
    /// Loads pixels that did not come from this editor — a bitmap off the system clipboard.
    /// </summary>
    /// <remarks>
    /// Paste has one implementation regardless of where the pixels came from: an external image is
    /// put here first and then goes through the same floating-selection path as an internal copy,
    /// so it can be nudged into place, undone, and committed identically.
    /// </remarks>
    public static void SetContent(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("Clipboard pixel buffer does not match its dimensions.", nameof(rgba));

        Pixels = (byte[])rgba.Clone();
        Width = width;
        Height = height;
        ClearEditorOwnership();
    }

    public static void MarkEditorOwned()
    {
        _editorOwned = HasContent;
        _ownedSequence = NativeClipboardSequence.Current;
    }

    public static void ClearEditorOwnership() => _editorOwned = false;

    public static void Copy(ImageLayerBuffer layer, ImageSelectionMask selection, int canvasWidth, int canvasHeight)
    {
        Rectangle bounds = selection.GetBounds();
        if (bounds.IsEmpty) return;

        byte[] buffer = new byte[bounds.Width * bounds.Height * 4];
        for (int y = 0; y < bounds.Height; y++)
        {
            for (int x = 0; x < bounds.Width; x++)
            {
                int canvasX = bounds.Left + x;
                int canvasY = bounds.Top + y;
                int destination = (y * bounds.Width + x) * 4;
                if (!selection.Contains(canvasX, canvasY))
                    continue;
                Color pixel = GetPixel(layer.Pixels, canvasWidth, canvasHeight, canvasX, canvasY);
                buffer[destination] = pixel.R;
                buffer[destination + 1] = pixel.G;
                buffer[destination + 2] = pixel.B;
                buffer[destination + 3] = pixel.A;
            }
        }

        Pixels = buffer;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    public static void Paste(
        ImageLayerBuffer layer,
        ImageSelectionMask selection,
        int canvasWidth,
        int canvasHeight,
        Point anchor)
    {
        if (!HasContent || Pixels == null) return;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int canvasX = anchor.X + x;
                int canvasY = anchor.Y + y;
                if ((uint)canvasX >= (uint)canvasWidth || (uint)canvasY >= (uint)canvasHeight) continue;
                int source = (y * Width + x) * 4;
                if (Pixels[source + 3] == 0) continue;
                if (!selection.Contains(canvasX, canvasY)) continue;
                SetPixel(
                    layer.Pixels,
                    canvasWidth,
                    canvasHeight,
                    canvasX,
                    canvasY,
                    Color.FromArgb(Pixels[source + 3], Pixels[source], Pixels[source + 1], Pixels[source + 2]),
                    1f,
                    selection: selection);
            }
        }
    }

    public static void ClearSelectionPixels(ImageLayerBuffer layer, ImageSelectionMask selection, int canvasWidth, int canvasHeight)
    {
        Rectangle bounds = selection.GetBounds();
        if (bounds.IsEmpty) return;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (!selection.Contains(x, y)) continue;
                RasterOperations.SetPixel(layer.Pixels, canvasWidth, canvasHeight, x, y, Color.Transparent, 1f, erase: true, selection: selection);
            }
        }
    }

    private static Color GetPixel(byte[] rgba, int width, int height, int x, int y) =>
        RasterOperations.GetPixel(rgba, width, height, x, y);

    private static void SetPixel(
        byte[] rgba,
        int width,
        int height,
        int x,
        int y,
        Color color,
        float opacity,
        ImageSelectionMask? selection = null) =>
        RasterOperations.SetPixel(rgba, width, height, x, y, color, opacity, selection: selection);
}

/// <summary>Win32 clipboard sequence so we can tell our Copy from a later external copy.</summary>
internal static class NativeClipboardSequence
{
    public static uint Current
    {
        get
        {
            try
            {
                return GetClipboardSequenceNumber();
            }
            catch
            {
                return 0;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
