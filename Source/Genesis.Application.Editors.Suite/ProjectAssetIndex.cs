using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Assets;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

/// <summary>One project resource surfaced to editor pickers (object palette, sprite pickers…).</summary>
public sealed record ProjectAssetEntry(string DisplayName, string FullPath, string ProjectRelativePath, ResourceKind Kind)
{
    public string Reference => DisplayName;
    public Guid AssetId { get; init; }
    public IReadOnlyList<string> LibraryTags { get; init; } = [];
}

/// <summary>
/// Lightweight project resource enumeration for editor pickers. Uses the cached global namespace — no recursive filesystem scan per picker or property edit.
/// </summary>
public static class ProjectAssetIndex
{
    private sealed record ImageUsageCacheEntry(long WriteTicks, ImageUsage Allowed);
    private static readonly object ImageUsageGate = new();
    private static readonly Dictionary<string, ImageUsageCacheEntry> ImageUsageCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ProjectAssetEntry> Enumerate(string projectRoot, ResourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return [];
        ResourceType type = TypeFor(kind);
        return ResourceNames.For(projectRoot).Entries.Where(e => e.Type == type)
            .Select(e => new ProjectAssetEntry(e.Name, e.FullPath, Path.GetRelativePath(projectRoot, e.FullPath).Replace('\\', '/'), kind) { AssetId = e.AssetId, LibraryTags = ResourceLibraryTags.ReadOrEmpty(e.FullPath) })
            .ToArray();
    }

    /// <summary>True only when an Image resource explicitly enables the requested authoring use.</summary>
    public static bool SupportsImageUsage(ProjectAssetEntry entry, ImageUsage usage)
    {
        if (entry.Kind != ResourceKind.Image || usage == ImageUsage.None || string.IsNullOrWhiteSpace(entry.FullPath))
            return usage == ImageUsage.None;
        if (!File.Exists(entry.FullPath)) return false;

        long writeTicks;
        try { writeTicks = File.GetLastWriteTimeUtc(entry.FullPath).Ticks; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }

        ImageUsageCacheEntry? cached;
        lock (ImageUsageGate)
        {
            if (ImageUsageCache.TryGetValue(entry.FullPath, out cached) && cached.WriteTicks == writeTicks)
                return (cached.Allowed & usage) == usage;
        }

        ImageUsage allowed;
        try
        {
            allowed = ImageDocumentSerializer.LoadAtomic(entry.FullPath).Document.Usage.Allowed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }

        lock (ImageUsageGate) ImageUsageCache[entry.FullPath] = new ImageUsageCacheEntry(writeTicks, allowed);
        return (allowed & usage) == usage;
    }

    public static IReadOnlyList<ProjectAssetEntry> EnumerateImagesForUsage(string projectRoot, ImageUsage usage) =>
        Enumerate(projectRoot, ResourceKind.Image).Where(entry => SupportsImageUsage(entry, usage)).ToArray();

    public static ResourceType TypeFor(ResourceKind kind) => kind switch
    {
        ResourceKind.GameObject => ResourceType.Object,
        ResourceKind.PgslScript => ResourceType.Script,
        _ => Enum.TryParse(kind.ToString(), out ResourceType type) ? type : ResourceType.Unknown,
    };

    public static string ResolveReference(string projectRoot, string? name, ResourceKind kind = ResourceKind.Unknown) =>
        ResourceNames.Resolve(projectRoot, name ?? string.Empty, TypeFor(kind));

    /// <summary>Resolves private first-frame pixels from a public sprite resource name.</summary>
    public static string? ResolveSpriteImage(string projectRoot, string? spriteReference)
    {
        if (string.IsNullOrWhiteSpace(spriteReference))
        {
            return null;
        }

        string descriptor = SpriteAssetLoader.ResolveDescriptorPath(projectRoot, spriteReference);
        if (!string.IsNullOrWhiteSpace(descriptor) && File.Exists(descriptor))
        {
            if (descriptor.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    SpriteRuntimeAsset asset = SpriteAssetLoader.Load(descriptor);
                    string frame = SpriteAssetLoader.ResolveFrameTexturePath(descriptor, asset, 0);
                    if (!string.IsNullOrWhiteSpace(frame)
                        && File.Exists(frame)
                        && IsRasterImagePath(frame))
                    {
                        return frame;
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
                {
                    return null;
                }

                // Fresh Images have a descriptor but no pixel file yet. Do not fall through to
                // TexturePathResolver — it resolves the .image.json itself, and GDI then throws
                // ExternalException when the Inspector tries Image.FromFile on JSON.
                return null;
            }

            return IsRasterImagePath(descriptor) ? descriptor : null;
        }

        string direct = TexturePathResolver.Resolve(projectRoot, spriteReference);
        return string.IsNullOrWhiteSpace(direct) || !IsRasterImagePath(direct) ? null : direct;
    }

    private static bool IsRasterImagePath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tga", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opens a full <see cref="ResourceService"/> for a project root, for editor surfaces that
    /// need GUID-tracked resource lifecycle (create/rename/trash with associates and metadata)
    /// rather than the plain file enumeration <see cref="Enumerate"/> gives pickers.
    /// </summary>
    public static ResourceService OpenResourceService(string projectRoot)
    {
        ProjectService projectService = new();
        ProjectSession session = projectService.OpenProject(projectRoot);
        return new ResourceService(session);
    }
}

/// <summary>Per-renderer texture cache keyed by image path (editors share placed-object art).</summary>
public sealed class ViewportTextureCache
{
    private readonly Dictionary<string, (TextureHandle Handle, int Width, int Height)> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private IRenderController? _renderer;

    public bool TryGet(IRenderController renderer, string? imagePath, out TextureHandle handle, out int width, out int height)
    {
        handle = TextureHandle.Invalid;
        width = height = 32;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return false;
        }

        if (!ReferenceEquals(_renderer, renderer))
        {
            // Renderer was recreated (handle recreate) — old handles are dead.
            _cache.Clear();
            _renderer = renderer;
        }

        if (!_cache.TryGetValue(imagePath, out (TextureHandle Handle, int Width, int Height) entry) || !entry.Handle.IsValid)
        {
            TextureHandle loaded = renderer.LoadTexture(imagePath);
            if (!loaded.IsValid)
            {
                return false;
            }

            ReadImageSize(imagePath, out int w, out int h);
            entry = (loaded, w, h);
            _cache[imagePath] = entry;
        }

        handle = entry.Handle;
        width = entry.Width;
        height = entry.Height;
        return true;
    }

    public void Invalidate(string imagePath) => _cache.Remove(imagePath);

    public void Clear() => _cache.Clear();

    private static void ReadImageSize(string path, out int width, out int height)
    {
        width = height = 32;
        try
        {
            byte[] header = new byte[26];
            using FileStream stream = File.OpenRead(path);
            int read = stream.Read(header, 0, header.Length);
            if (read > 24 && header[1] == (byte)'P' && header[2] == (byte)'N' && header[3] == (byte)'G')
            {
                width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                return;
            }

            using var image = System.Drawing.Image.FromFile(path);
            width = image.Width;
            height = image.Height;
        }
        catch (Exception exception) when (exception is IOException or OutOfMemoryException or ArgumentException)
        {
            width = height = 32;
        }
    }
}
