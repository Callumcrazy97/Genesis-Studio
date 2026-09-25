using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;

namespace Genesis.Application.Studio.Resources;

/// <summary>
/// Bounded UI-thread cache. Paint only reads icons. Prepare decodes off-thread without touching
/// this cache; Accept transfers ownership on the UI thread. Warm is a synchronous batch/test API,
/// not used by the interactive Resource Browser. Missing/corrupt previews are negatively cached.
/// </summary>
public sealed class ResourceThumbnailCache : IDisposable
{
    internal const int Capacity = 512;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _age = new();
    private readonly string _projectRoot;
    private readonly int _size;
    private readonly Bitmap _folder;
    private readonly Bitmap _missing;
    private bool _disposed;

    public ResourceThumbnailCache(string projectRoot, int size = 16)
    {
        _projectRoot = projectRoot ?? string.Empty;
        _size = Math.Clamp(size, 8, 128);
        _folder = Placeholder(Color.FromArgb(210, 170, 80));
        _missing = Placeholder(Color.FromArgb(120, 140, 180));
    }

    internal int Count => _entries.Count;
    internal bool Contains(ResourceItem item) => item.IsFolder || _entries.ContainsKey(item.FullPath);

    /// <summary>No disk reads, JSON parsing, bitmap decoding or new work is performed in paint.</summary>
    public Image GetCachedIcon(ResourceItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (item.IsFolder) return _folder;
        if (!_entries.TryGetValue(item.FullPath, out Entry? entry)) return _missing;
        _age.Remove(entry.Age); _age.AddLast(entry.Age);
        return entry.Image ?? _missing;
    }

    /// <summary>Synchronous helper retained for existing export/headless callers.</summary>
    public void Warm(ResourceItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Contains(item)) return;
        using PreparedThumbnail prepared = Prepare(item);
        Accept(prepared);
    }

    /// <summary>Worker-safe. All file access and GDI decoding is confined to the worker.</summary>
    internal PreparedThumbnail Prepare(ResourceItem item)
    {
        string? imagePath = null;
        try
        {
            imagePath = ResolveImagePath(item);
            if (imagePath is null) return new(item.FullPath, null, null);
            // Prevent an archive/reference sheet from allocating an unbounded decoded bitmap.
            if (new FileInfo(imagePath).Length > 32 * 1024 * 1024) return new(item.FullPath, imagePath, null);
            using FileStream stream = new(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Image source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            if ((long)source.Width * source.Height > 32L * 1024 * 1024) return new(item.FullPath, imagePath, null);
            return new(item.FullPath, imagePath, ResizeSquare(source));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or InvalidDataException or NotSupportedException
            or OutOfMemoryException or ExternalException or System.Text.Json.JsonException)
        {
            return new(item.FullPath, imagePath, null);
        }
    }

    /// <summary>UI-thread only; transfers the prepared bitmap, never shares a worker-owned bitmap.</summary>
    internal void Accept(PreparedThumbnail prepared)
    {
        if (_disposed) return; // Caller still owns/disposes an obsolete or closing result.
        if (_entries.Remove(prepared.ResourcePath, out Entry? previous))
        { _age.Remove(previous.Age); previous.Image?.Dispose(); }
        while (_entries.Count >= Capacity && _age.First is { } oldest)
        {
            Entry entry = _entries[oldest.Value]; _entries.Remove(oldest.Value);
            _age.RemoveFirst(); entry.Image?.Dispose();
        }
        LinkedListNode<string> age = _age.AddLast(prepared.ResourcePath);
        _entries.Add(prepared.ResourcePath, new(prepared.TakeImage(), prepared.SourcePath, age));
    }

    public string? ResolvedImageFor(ResourceItem item) =>
        _entries.TryGetValue(item.FullPath, out Entry? entry) ? entry.SourcePath : null;

    public void Invalidate()
    {
        foreach (Entry entry in _entries.Values) entry.Image?.Dispose();
        _entries.Clear(); _age.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Invalidate(); _folder.Dispose(); _missing.Dispose();
    }

    private string? ResolveImagePath(ResourceItem item)
    {
        // Use the same name-resolved, first-frame path as the shared picker. This also
        // follows sibling-frame aliases instead of assuming a local pixel sidecar exists.
        if (item.Kind == ResourceKind.Image)
        {
            string? frame = ProjectAssetIndex.ResolveSpriteImage(_projectRoot, item.Name);
            if (frame is not null) return frame;
        }
        if (item.Kind == ResourceKind.GameObject)
        {
            string? reference = ObjectResourceReader.ReadImagePath(item.FullPath);
            string? bound = ProjectAssetIndex.ResolveSpriteImage(_projectRoot, reference)
                ?? ObjectResourceReader.ResolveImageFile(item.FullPath, _projectRoot);
            if (bound is not null) return bound;
        }
        if (item.Kind is ResourceKind.Image or ResourceKind.GameObject or ResourceKind.Unknown)
        {
            string? associate = ResourceAssociates.FindPrimaryImage(item.FullPath);
            if (associate is not null) return associate;
        }
        string extension = Path.GetExtension(item.FullPath).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" ? item.FullPath : null;
    }

    private Bitmap ResizeSquare(Image source)
    {
        Bitmap result = new(_size, _size, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(result);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = Math.Min((float)_size / source.Width, (float)_size / source.Height);
            int width = Math.Max(1, (int)(source.Width * scale)), height = Math.Max(1, (int)(source.Height * scale));
            graphics.DrawImage(source, new Rectangle((_size - width) / 2, (_size - height) / 2, width, height));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private Bitmap Placeholder(Color colour)
    {
        Bitmap bitmap = new(_size, _size, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using SolidBrush brush = new(colour);
        graphics.FillRectangle(brush, 2, 2, _size - 4, _size - 4);
        return bitmap;
    }

    private sealed record Entry(Bitmap? Image, string? SourcePath, LinkedListNode<string> Age);

    internal sealed class PreparedThumbnail(string resourcePath, string? sourcePath, Bitmap? image) : IDisposable
    {
        private Bitmap? _image = image;
        internal string ResourcePath { get; } = resourcePath;
        internal string? SourcePath { get; } = sourcePath;
        internal Bitmap? TakeImage() { Bitmap? result = _image; _image = null; return result; }
        public void Dispose() { _image?.Dispose(); _image = null; }
    }
}
