using System.Drawing;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Suite.Assets.Painting;

/// <summary>
/// One dab of the Model editor's texture brush. Painting is the Image editor's own brush
/// (<see cref="RasterOperations.StampBrush"/>, its size, hardness and opacity), unchanged. The eraser
/// uses the same footprint to move pixels back toward the image as it was when painting began,
/// rather than to transparent: on a model, erasing a stroke means taking it off the texture.
/// </summary>
public static class ModelPaintBrush
{
    /// <summary>Every pixel one dab can change, for undo capture.</summary>
    public static Rectangle Bounds(int x, int y, ImageBrushSettings settings)
    {
        int size = Math.Clamp(settings.Size, 1, 1024);
        int reach = (int)MathF.Ceiling(Math.Max(.5f, size * .5f)) + 1;
        return new Rectangle(x - reach, y - reach, reach * 2 + 1, reach * 2 + 1);
    }

    public static void Paint(byte[] pixels, int width, int height, int x, int y, Color colour, ImageBrushSettings settings) =>
        RasterOperations.StampBrush(pixels, width, height, x, y, colour, settings, erase: false, alphaLock: false, selection: null);

    /// <summary>Moves the dab's pixels toward <paramref name="original"/> by the brush's coverage there.</summary>
    public static void Restore(byte[] pixels, byte[] original, int width, int height, int x, int y, ImageBrushSettings settings)
    {
        if (original.Length != pixels.Length) return;
        Rectangle bounds = Bounds(x, y, settings);
        // The brush's coverage, drawn by the Image editor's brush into a small clear mask: white
        // over transparent leaves each pixel's alpha at exactly the strength the brush paints with.
        byte[] mask = new byte[bounds.Width * bounds.Height * 4];
        RasterOperations.StampBrush(mask, bounds.Width, bounds.Height, x - bounds.X, y - bounds.Y, Color.White, settings,
            erase: false, alphaLock: false, selection: null);
        for (int row = 0; row < bounds.Height; row++)
        {
            int py = bounds.Y + row;
            if ((uint)py >= (uint)height) continue;
            for (int column = 0; column < bounds.Width; column++)
            {
                int px = bounds.X + column;
                byte coverage = mask[(row * bounds.Width + column) * 4 + 3];
                if (coverage == 0 || (uint)px >= (uint)width) continue;
                float weight = coverage / 255f;
                int index = (py * width + px) * 4;
                for (int channel = 0; channel < 4; channel++)
                {
                    int current = pixels[index + channel];
                    pixels[index + channel] = (byte)Math.Clamp((int)MathF.Round(current + (original[index + channel] - current) * weight), 0, 255);
                }
            }
        }
    }
}
