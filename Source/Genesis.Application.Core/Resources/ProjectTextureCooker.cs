using Genesis.Application.Core.Images;
using Genesis.Rendering.Textures;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Resources;

/// <summary>
/// Makes the GPU-ready copies (BC7, normal maps BC5) of a project's model textures beside their
/// sources, so a game uploads compressed textures instead of four bytes a texel. The source stays
/// authoritative: a copy older than its source is ignored by the runtime and made again here.
/// Studio cooks in the background when a model is imported or an Image used as a texture is saved.
/// </summary>
public static class ProjectTextureCooker
{
    private static readonly object Gate = new();
    private static readonly Queue<Func<int>> Work = new();
    private static readonly HashSet<string> Queued = new(StringComparer.OrdinalIgnoreCase);
    private static Task _worker = Task.CompletedTask;
    private static bool _running;

    /// <summary>
    /// Whether queued cooks run. Studio turns it on; tools and tests cook explicitly instead, so no
    /// worker writes into a folder they are about to delete.
    /// </summary>
    public static bool BackgroundCooking { get; set; }

    /// <summary>Textures cooked by the background worker since Studio started.</summary>
    public static int CookedInBackground { get; private set; }

    /// <summary>Completes when everything queued so far has been cooked (for tests and export).</summary>
    public static Task Idle
    {
        get { lock (Gate) return _worker; }
    }

    /// <summary>Cooks a model's material textures: colour, emission and ORM as BC7, the normal map as BC5.</summary>
    public static int CookModel(string modelResourcePath)
    {
        string projectRoot = ProjectRootOf(modelResourcePath);
        if (projectRoot.Length == 0) return 0;
        GModelAsset model;
        try { model = StudioModelResourceLoader.LoadReadOnly(modelResourcePath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException)
        {
            return 0;
        }
        int cooked = 0;
        foreach (GModelMaterial material in model.Materials)
        {
            if (material is null) continue;
            cooked += CookTexture(projectRoot, material.AlbedoTexture, CookedTextureFormat.Bc7Color) ? 1 : 0;
            cooked += CookTexture(projectRoot, material.EmissiveTexture, CookedTextureFormat.Bc7Color) ? 1 : 0;
            cooked += CookTexture(projectRoot, material.MetallicRoughnessTexture, CookedTextureFormat.Bc7Color) ? 1 : 0;
            cooked += CookTexture(projectRoot, material.NormalTexture, CookedTextureFormat.Bc5Normal) ? 1 : 0;
        }
        return cooked;
    }

    /// <summary>Cooks an Image enabled for use as a model texture (and the normal map it names).</summary>
    public static int CookImage(string imageDocumentPath)
    {
        string projectRoot = ProjectRootOf(imageDocumentPath);
        if (projectRoot.Length == 0 || !File.Exists(imageDocumentPath)) return 0;
        ImageDocument document;
        try { document = ImageDocumentSerializer.LoadAtomic(imageDocumentPath).Document; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            return 0;
        }
        if (!document.Usage.Supports(ImageUsage.Texture)) return 0;
        int cooked = 0;
        try
        {
            string frame = SpriteAssetLoader.ResolveFrameTexturePath(imageDocumentPath, SpriteAssetLoader.Load(imageDocumentPath), 0);
            cooked += CookFile(frame, CookedTextureFormat.Bc7Color) ? 1 : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or InvalidOperationException)
        {
        }
        cooked += CookTexture(projectRoot, document.Usage.Material.NormalMap, CookedTextureFormat.Bc5Normal) ? 1 : 0;
        return cooked;
    }

    /// <summary>Queues a model's textures for the background worker; a model already waiting is not queued twice.</summary>
    public static void QueueModel(string modelResourcePath) => Queue("model:" + modelResourcePath, () => CookModel(modelResourcePath));

    /// <summary>Queues an Image for the background worker.</summary>
    public static void QueueImage(string imageDocumentPath) => Queue("image:" + imageDocumentPath, () => CookImage(imageDocumentPath));

    /// <summary>
    /// Cooks one texture (a project resource name or path, an Image or a picture file) unless a
    /// fresh copy in that format already exists. False when nothing was cooked.
    /// </summary>
    public static bool CookTexture(string projectRoot, string? texture, CookedTextureFormat format)
    {
        if (string.IsNullOrWhiteSpace(texture)) return false;
        try
        {
            string file = TexturePathResolver.Resolve(projectRoot, texture);
            if (!string.IsNullOrWhiteSpace(file) && SpriteAssetLoader.IsSpriteDescriptorPath(file))
                file = SpriteAssetLoader.ResolveFrameTexturePath(file, SpriteAssetLoader.Load(file), 0);
            return CookFile(file, format);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool CookFile(string? file, CookedTextureFormat format)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return false;
            if (CookedTextureManifestStore.TryResolve(Path.GetFullPath(file), out _, out CookedTextureManifest fresh)
                && fresh.Format == format)
                return false;
            CookedTextureCooker.Cook(file, format);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // A picture that cannot be cooked is still drawn from its source.
            return false;
        }
    }

    private static void Queue(string key, Func<int> cook)
    {
        if (!BackgroundCooking || string.IsNullOrWhiteSpace(key)) return;
        lock (Gate)
        {
            if (!Queued.Add(key)) return;
            Work.Enqueue(() =>
            {
                lock (Gate) Queued.Remove(key);
                return cook();
            });
            if (!_running)
            {
                _running = true;
                _worker = Task.Run(Drain);
            }
        }
    }

    // One cook at a time, below the editor's own work: an encode is CPU-heavy.
    private static void Drain()
    {
        while (true)
        {
            Func<int> next;
            lock (Gate)
            {
                if (Work.Count == 0)
                {
                    _running = false;
                    return;
                }
                next = Work.Dequeue();
            }
            try { CookedInBackground += next(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Never let a bad texture stop the queue.
            }
        }
    }

    private static string ProjectRootOf(string resourcePath)
    {
        try
        {
            DirectoryInfo? directory = new FileInfo(Path.GetFullPath(resourcePath)).Directory;
            while (directory is not null)
            {
                if (directory.EnumerateFiles("*.genesisproj", SearchOption.TopDirectoryOnly).Any())
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return string.Empty;
    }
}
