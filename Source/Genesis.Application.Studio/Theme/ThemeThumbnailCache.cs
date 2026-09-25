using System.Drawing;
using System.Drawing.Drawing2D;

namespace Genesis.Application.Studio.Theme;

/// <summary>Process-lifetime, cover-fit thumbnails for the image-theme gallery.</summary>
/// <remarks>
/// WinForms repaints owner-drawn controls often. Scaling the full backdrop during every paint made
/// merely moving another window across Preferences do image work, so cards ask this cache for one
/// fixed bitmap instead. <see cref="ThemeCatalog.RefreshImages"/> clears it when a user replaces or
/// adds a file, which also prevents a replaced image retaining its old preview.
/// </remarks>
internal static class ThemeThumbnailCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<ThumbnailKey, Bitmap?> Thumbnails = [];

    internal static int CachedCount
    {
        get
        {
            lock (Gate)
            {
                return Thumbnails.Count;
            }
        }
    }

    internal static Bitmap? Get(ThemeImage theme, Size size)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        ThumbnailKey key = new(
            Path.GetFullPath(theme.Path).ToUpperInvariant(), size.Width, size.Height);

        lock (Gate)
        {
            if (Thumbnails.TryGetValue(key, out Bitmap? cached))
            {
                return cached;
            }

            Bitmap? thumbnail = Create(theme.Picture, size);
            Thumbnails.Add(key, thumbnail);
            return thumbnail;
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            foreach (Bitmap? thumbnail in Thumbnails.Values)
            {
                thumbnail?.Dispose();
            }

            Thumbnails.Clear();
        }
    }

    private static Bitmap? Create(Image? picture, Size size)
    {
        if (picture is null)
        {
            return null;
        }

        Bitmap thumbnail = new(size.Width, size.Height);
        using Graphics graphics = Graphics.FromImage(thumbnail);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        Rectangle target = new(Point.Empty, size);
        Rectangle source = CoverSource(picture.Size, size);
        graphics.DrawImage(picture, target, source, GraphicsUnit.Pixel);
        return thumbnail;
    }

    private static Rectangle CoverSource(Size source, Size target)
    {
        double sourceAspect = (double)source.Width / source.Height;
        double targetAspect = (double)target.Width / target.Height;
        if (sourceAspect > targetAspect)
        {
            int width = (int)Math.Round(source.Height * targetAspect);
            return new Rectangle((source.Width - width) / 2, 0, width, source.Height);
        }

        int height = (int)Math.Round(source.Width / targetAspect);
        return new Rectangle(0, (source.Height - height) / 2, source.Width, height);
    }

    private readonly record struct ThumbnailKey(string Path, int Width, int Height);
}
