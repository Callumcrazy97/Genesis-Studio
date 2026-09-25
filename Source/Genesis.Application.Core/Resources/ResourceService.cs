using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;

namespace Genesis.Application.Core.Resources;

public sealed class ResourceService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public ResourceService(ProjectSession project)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        Directory.CreateDirectory(project.AssetsPath);
        ResourceFolderPolicy.EnsureRoots(project);
    }

    public ProjectSession Project { get; }

    public string AssetsRoot => Project.AssetsPath;

    public ResourceClipboardSnapshot Clipboard { get; private set; } =
        ResourceClipboardSnapshot.Empty;

    public event EventHandler<ResourceChangedEventArgs>? Changed;

    /// <summary>The host can require saved documents before a project-wide reference transaction.</summary>
    public Action? BeforeReferenceEdit { get; set; }

    public ResourceItem BuildTree()
    {
        ResourceFolderPolicy.EnsureRoots(Project);
        ResourceNames.Invalidate(Project.RootPath);
        return BuildFolderNode(AssetsRoot);
    }

    public ResourceLibraryTagSnapshot SetLibraryTags(ResourceLibraryTagSnapshot expected, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(expected);
        string source = RequireItem(expected.ResourcePath);
        ResourceFolderPolicy.RequireMutable(Project, source);
        if (Directory.Exists(source) || ResourceDefinitions.FromPath(source) is null
            || !PathsEqual(expected.AssetsRoot, AssetsRoot))
            throw new InvalidOperationException("Select a resource in this project to edit its library tags.");
        ResourceLibraryTagSnapshot result = ResourceLibraryTags.Apply(expected, tags);
        if (result.Revision != expected.Revision) Notify(ResourceChangeKind.Refreshed, source);
        return result;
    }

    public string CreateFolder(string parentFolder, string name)
    {
        string parent = RequireFolder(parentFolder);
        ResourceFolderPolicy.RequireSubfolderParent(Project, parent);
        string safeName = ValidateName(name);
        string destination = GetUniquePath(Path.Combine(parent, safeName), isFolder: true);
        Directory.CreateDirectory(destination);
        Notify(ResourceChangeKind.Created, destination);
        return destination;
    }

    public string CreateResource(string parentFolder, ResourceKind kind, string name)
    {
        if (kind is ResourceKind.Unknown or ResourceKind.Folder)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A concrete resource type is required.");
        }

        string parent = ResourceFolderPolicy.Destination(Project, RequireFolder(parentFolder), kind);
        string safeName = ResourceNames.ValidateName(name);
        ResourceDefinition definition = ResourceDefinitions.Get(kind);
        string withoutDuplicateExtension = safeName.EndsWith(
            definition.Extension,
            StringComparison.OrdinalIgnoreCase)
            ? safeName[..^definition.Extension.Length]
            : safeName;

        string requestedPath = Path.Combine(parent, withoutDuplicateExtension + definition.Extension);
        string destination = GetUniquePath(requestedPath, isFolder: false);
        File.WriteAllText(destination, definition.DefaultContent);
        WriteMetadata(destination, kind, GetDisplayName(destination), Guid.NewGuid(), DateTime.UtcNow);
        Notify(ResourceChangeKind.Created, destination);
        return destination;
    }

    public string Rename(string sourcePath, string newName)
    {
        string source = RequireItem(sourcePath);
        ResourceFolderPolicy.RequireMutable(Project, source);
        if (!Directory.Exists(source))
        {
            // Public identity is independent of storage. Moving private sidecars here would break
            // shared cels, event folders and externally authored references to those payloads.
            BeforeReferenceEdit?.Invoke();
            ResourceReferenceOperations.Rename(Project, source, newName);
            Notify(ResourceChangeKind.Renamed, source, source);
            return source;
        }
        if (PathsEqual(source, AssetsRoot)) throw new InvalidOperationException("The Assets root cannot be renamed.");
        string safeName = ValidateName(newName);
        string destination = Path.Combine(Path.GetDirectoryName(source)!, safeName);
        EnsureInsideAssets(destination, allowRoot: false);
        if (source.Equals(destination, StringComparison.Ordinal)) return source;
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("A folder with that name already exists.");
        Directory.Move(source, destination);
        Notify(ResourceChangeKind.Renamed, destination, source);
        return destination;
    }

    public string Copy(string sourcePath, string destinationFolder)
    {
        string source = RequireItem(sourcePath);
        ResourceFolderPolicy.RequireMutable(Project, source);
        string destinationRoot = ValidateTransfer(source, destinationFolder);
        if (Directory.Exists(source) && IsSameOrDescendant(destinationRoot, source))
            throw new InvalidOperationException("A folder cannot be copied into itself.");
        bool isFolder = Directory.Exists(source);
        var before = ResourceNames.For(Project.RootPath);
        string filename = !isFolder && before.Find(source) is { } sourceResource
            ? sourceResource.Name + sourceResource.Extension : Path.GetFileName(source);
        string destination = GetUniquePath(Path.Combine(destinationRoot, filename), isFolder);

        if (isFolder)
        {
            using ResourceFileTransaction transaction = new();
            transaction.Copy(source, destination);
            RegenerateMetadata(destination);
            ResourceReferenceOperations.RebindCopies(Project, before, source, destination, transaction);
            transaction.Commit();
        }
        else
        {
            IReadOnlyList<string> associates = ResourceAssociates.Find(source);
            string oldStem = ResourceAssociates.GetStem(source);
            string newStem = ResourceAssociates.GetStem(destination);
            string destinationDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("The resource has no parent folder.");

            CopyResourceSet(
                source,
                destination,
                associates,
                oldStem,
                newStem,
                destinationDirectory,
                before);
        }

        Notify(ResourceChangeKind.Copied, destination, source);
        return destination;
    }

    public string Move(string sourcePath, string destinationFolder)
    {
        string source = RequireItem(sourcePath);
        ResourceFolderPolicy.RequireMutable(Project, source);
        string destinationRoot = ValidateTransfer(source, destinationFolder);
        if (PathsEqual(source, AssetsRoot))
        {
            throw new InvalidOperationException("The Assets root cannot be moved.");
        }

        if (Directory.Exists(source) && IsSameOrDescendant(destinationRoot, source))
        {
            throw new InvalidOperationException("A folder cannot be moved into itself.");
        }

        string currentParent = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException("The resource has no parent folder.");
        if (PathsEqual(currentParent, destinationRoot))
        {
            return source;
        }

        bool isFolder = Directory.Exists(source);
        string destination = Path.Combine(destinationRoot, Path.GetFileName(source));
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("The destination already contains an item with this storage name.");

        if (isFolder)
        {
            Directory.Move(source, destination);
        }
        else
        {
            IReadOnlyList<string> associates = ResourceAssociates.Find(source);
            string oldStem = ResourceAssociates.GetStem(source);
            string newStem = ResourceAssociates.GetStem(destination);
            string destinationDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("The resource has no parent folder.");

            MoveResourceSet(
                source,
                destination,
                associates,
                oldStem,
                newStem,
                destinationDirectory);
        }

        Notify(ResourceChangeKind.Moved, destination, source);
        return destination;
    }

    public string Duplicate(string sourcePath)
    {
        string source = RequireItem(sourcePath);
        string parent = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException("The resource has no parent folder.");
        return Copy(source, parent);
    }

    public string MoveToTrash(string sourcePath)
    {
        string source = RequireItem(sourcePath);
        ResourceFolderPolicy.RequireMutable(Project, source);
        if (PathsEqual(source, AssetsRoot))
        {
            throw new InvalidOperationException("The Assets root cannot be deleted.");
        }

        string relative = Path.GetRelativePath(AssetsRoot, source);
        string trashRoot = Path.Combine(
            Project.InternalPath,
            "Trash",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        string destination = Path.Combine(trashRoot, relative);
        string? destinationParent = Path.GetDirectoryName(destination);
        if (destinationParent is null)
        {
            throw new InvalidOperationException("The trash destination is invalid.");
        }

        Directory.CreateDirectory(destinationParent);
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            IReadOnlyList<string> associates = ResourceAssociates.Find(source);
            string oldStem = ResourceAssociates.GetStem(source);
            string newStem = ResourceAssociates.GetStem(destination);
            string destinationDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("The trash destination is invalid.");

            MoveResourceSet(
                source,
                destination,
                associates,
                oldStem,
                newStem,
                destinationDirectory);
        }

        Notify(ResourceChangeKind.Deleted, destination, source);
        return destination;
    }

    /// <summary>
    /// Imports external files into Assets, wrapping media into resource definitions when needed.
    /// Associated media keeps the resource stem so rename/move/delete stay coherent.
    /// </summary>
    public IReadOnlyList<string> ImportFiles(string parentFolder, IEnumerable<string> sourceFiles)
    {
        string parent = RequireFolder(parentFolder);
        ArgumentNullException.ThrowIfNull(sourceFiles);
        string[] sources = sourceFiles.Where(source => !string.IsNullOrWhiteSpace(source) && File.Exists(source)).ToArray();
        string[] destinations = sources.Select(source =>
            ResourceFolderPolicy.Destination(Project, parent, ImportKind(source))).ToArray();
        List<string> imported = [];
        for (int index = 0; index < sources.Length; index++)
            imported.Add(ImportSingleFile(destinations[index], sources[index]));

        return imported;
    }

    public void SetClipboard(ResourceClipboardOperation operation, IEnumerable<string> sourcePaths)
    {
        if (operation == ResourceClipboardOperation.None)
        {
            Clipboard = ResourceClipboardSnapshot.Empty;
            return;
        }

        string[] paths = sourcePaths.Select(RequireItem)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string path in paths) ResourceFolderPolicy.RequireMutable(Project, path);

        Clipboard = paths.Length == 0
            ? ResourceClipboardSnapshot.Empty
            : new ResourceClipboardSnapshot(operation, paths);
    }

    public IReadOnlyList<string> Paste(string destinationFolder)
    {
        string destination = RequireFolder(destinationFolder);
        if (!Clipboard.HasItems)
        {
            return [];
        }

        foreach (string source in Clipboard.SourcePaths)
            if (File.Exists(source) || Directory.Exists(source)) ValidateTransfer(source, destination);

        List<string> results = [];
        foreach (string source in Clipboard.SourcePaths)
        {
            if (!File.Exists(source) && !Directory.Exists(source))
            {
                continue;
            }

            results.Add(
                Clipboard.Operation == ResourceClipboardOperation.Cut
                    ? Move(source, destination)
                    : Copy(source, destination));
        }

        if (Clipboard.Operation == ResourceClipboardOperation.Cut)
        {
            Clipboard = ResourceClipboardSnapshot.Empty;
        }

        return results;
    }

    private ResourceItem BuildFolderNode(string folder)
    {
        List<ResourceItem> children = [];
        List<string> files = [];
        foreach (string file in Directory.EnumerateFiles(folder)
                     .Where(path => !ResourceAssociates.IsHiddenImplementationFile(path))
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            files.Add(file);
        }

        HashSet<string> knownResources = new(
            files.Where(ResourceAssociates.IsDesignerVisibleResourcePath),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> hiddenAssociates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string resourcePath in knownResources)
        {
            foreach (string associate in ResourceAssociates.Find(resourcePath))
            {
                hiddenAssociates.Add(Path.GetFullPath(associate));
            }
        }

        foreach (string directory in Directory.EnumerateDirectories(folder)
                     .Where(path => !hiddenAssociates.Contains(Path.GetFullPath(path)) &&
                         (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            children.Add(BuildFolderNode(directory));
        }

        foreach (string file in files)
        {
            if (hiddenAssociates.Contains(Path.GetFullPath(file)))
            {
                continue;
            }

            ResourceDefinition? definition = ResourceDefinitions.FromPath(file);
            // The Assets tree is a project-resource view, not a filesystem explorer. Runtime
            // payloads and editor sidecars (.gterrain, .gfoliage, .nature.json, imported source
            // files, caches, etc.) stay beside their owning resource on disk but must not appear
            // as separate things a user can open, rename, or delete independently.
            if (definition is null)
            {
                continue;
            }

            ResourceKind kind = definition.Kind;
            Guid id = EnsureMetadata(file, kind);
            children.Add(new ResourceItem
            {
                Name = ResourceNames.Name(Project.RootPath, file),
                FullPath = file,
                RelativePath = ToAssetRelative(file),
                Kind = kind,
                IsFolder = false,
                AssetId = id,
                LibraryTags = ResourceLibraryTags.ReadOrEmpty(file),
            });
        }

        return new ResourceItem
        {
            Name = PathsEqual(folder, AssetsRoot) ? "Assets" : Path.GetFileName(folder),
            FullPath = folder,
            RelativePath = PathsEqual(folder, AssetsRoot) ? string.Empty : ToAssetRelative(folder),
            Kind = ResourceKind.Folder,
            IsFolder = true,
            IsProtectedRoot = ResourceFolderPolicy.IsProtected(Project, folder),
            AllowedResourceKind = ResourceFolderPolicy.GetRoot(Project, folder)?.Kind,
            Children = children,
        };
    }

    private string ImportSingleFile(string parentFolder, string sourcePath)
    {
        string source = Path.GetFullPath(sourcePath);
        string extension = Path.GetExtension(source);
        string stem = Path.GetFileNameWithoutExtension(source);

        if (ResourceDefinitions.FromPath(source) is not null)
        {
            string destination = GetUniquePath(
                Path.Combine(parentFolder, Path.GetFileName(source)),
                isFolder: false);
            ResourceKind kind =
                ResourceDefinitions.FromPath(destination)?.Kind ?? ResourceKind.Unknown;
            IReadOnlyList<string> associates = ResourceAssociates.Find(source)
                .Where(path => !PathsEqual(path, source + ".meta"))
                .ToArray();
            string oldStem = ResourceAssociates.GetStem(source);
            string newStem = ResourceAssociates.GetStem(destination);
            string destinationDirectory = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException("The import destination has no parent folder.");

            using ResourceFileTransaction transaction = new();
            transaction.Copy(source, destination);
            CopyAssociates(
                transaction,
                associates,
                oldStem,
                newStem,
                destinationDirectory);
            RemapSpriteAssociateReferences(destination, oldStem, newStem, transaction);
            transaction.WriteNewText(
                MetadataPath(destination),
                SerializeMetadata(
                    kind,
                    GetDisplayName(destination),
                    Guid.NewGuid(),
                    DateTime.UtcNow,
                    ResourceLibraryTags.ReadOrEmpty(source)));
            transaction.Commit();
            Notify(ResourceChangeKind.Created, destination);
            return destination;
        }

        if (IsImageExtension(extension))
        {
            return ImportWrappedMedia(parentFolder, source, stem, extension, ResourceKind.Image, "source");
        }

        if (IsAudioExtension(extension))
        {
            return ImportWrappedMedia(parentFolder, source, stem, extension, ResourceKind.Audio, "source");
        }

        if (IsModelExtension(extension))
        {
            return ImportWrappedMedia(parentFolder, source, stem, extension, ResourceKind.Model, "source");
        }

        throw new InvalidOperationException("Unsupported standalone import. Import an image, sound, model or Genesis resource file; implementation files belong to their owning resource.");
    }

    private static ResourceKind ImportKind(string source)
    {
        ResourceDefinition? definition = ResourceDefinitions.FromPath(source);
        if (definition is not null) return definition.Kind;
        string extension = Path.GetExtension(source);
        if (IsImageExtension(extension)) return ResourceKind.Image;
        if (IsAudioExtension(extension)) return ResourceKind.Audio;
        if (IsModelExtension(extension)) return ResourceKind.Model;
        throw new InvalidOperationException($"'{Path.GetFileName(source)}' is not a supported standalone resource.");
    }

    public bool CanTransfer(string sourcePath, string destinationFolder)
    {
        try { _ = ValidateTransfer(RequireItem(sourcePath), destinationFolder); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return false; }
    }

    private string ValidateTransfer(string source, string destinationFolder)
    {
        ResourceFolderPolicy.RequireMutable(Project, source);
        string destination = RequireFolder(destinationFolder);
        if (Directory.Exists(source))
        {
            if (IsSameOrDescendant(destination, source))
                throw new InvalidOperationException("A folder cannot be copied or moved into itself.");
            ResourceRootFolder? sourceRoot = ResourceFolderPolicy.GetRoot(Project, source);
            ResourceRootFolder? targetRoot = ResourceFolderPolicy.GetRoot(Project, destination);
            if (sourceRoot is null || targetRoot?.Kind != sourceRoot.Kind)
                throw new InvalidOperationException("Subfolders can only be moved or copied within the same resource type.");
            // A linked descendant would escape the transaction's project boundary.
            Stack<string> pending = new();
            pending.Push(source);
            while (pending.TryPop(out string? directory))
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    ResourceFolderPolicy.RejectLinks(AssetsRoot, entry);
                    if (Directory.Exists(entry)) pending.Push(entry);
                }
            return destination;
        }
        ResourceKind kind = ResourceDefinitions.FromPath(source)?.Kind
            ?? throw new InvalidOperationException("Move the owning resource, not its implementation files.");
        return ResourceFolderPolicy.Destination(Project, destination, kind);
    }

    /// <summary>Copies a model source and its dependencies into an owned, new directory without creating a resource.</summary>
    public static string CopyModelSource(string sourcePath, string newDirectory)
    {
        using var conversion = ModelSourceConversion.Convert(sourcePath);
        using var transaction = new ResourceFileTransaction();
        transaction.CreateDirectory(newDirectory);
        string converted = conversion.Path;
        string extension = Path.GetExtension(converted);
        if (!PathsEqual(sourcePath, converted)) transaction.Copy(sourcePath, Path.Combine(newDirectory, "original" + Path.GetExtension(sourcePath)));
        if (extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase)) CopyGltfDependencies(converted, newDirectory, transaction);
        string destination = Path.Combine(newDirectory, "source" + extension);
        transaction.Copy(converted, destination); transaction.Commit(); return destination;
    }

    private string ImportWrappedMedia(
        string parentFolder,
        string sourcePath,
        string stem,
        string mediaExtension,
        ResourceKind kind,
        string sourceProperty)
    {
        string originalSource = sourcePath;
        using ModelSourceConversion? conversion = kind == ResourceKind.Model ? ModelSourceConversion.Convert(sourcePath) : null;
        if (conversion is not null) { sourcePath = conversion.Path; mediaExtension = Path.GetExtension(sourcePath); }
        ResourceDefinition definition = ResourceDefinitions.Get(kind);
        string resourcePath = GetUniquePath(
            Path.Combine(parentFolder, stem + definition.Extension),
            isFolder: false);
        string resourceStem = ResourceAssociates.GetStem(resourcePath);
        string content;
        string mediaDestination;
        using ResourceFileTransaction transaction = new();

        if (kind == ResourceKind.Image)
        {
            string spriteDataDirectory = ResourceAssociates.GetSpriteDataDirectory(resourcePath);
            mediaDestination = Path.Combine(spriteDataDirectory, "frame-0000" + mediaExtension);
            string mediaRelative = Path.Combine(
                    Path.GetFileName(spriteDataDirectory),
                    Path.GetFileName(mediaDestination))
                .Replace('\\', '/');
            ImageDocument document = ImageDocument.CreateDefault();
            document.Import.Source = mediaRelative;
            document.Frames.Add(new ImageFrame
            {
                Id = "frame-0000",
                Name = "Frame 1",
                Source = mediaRelative,
            });
            document.Layers.Add(new ImageLayer
            {
                Id = "layer-0000",
                Name = "Layer 1",
                Cels =
                [
                    new ImageCel
                    {
                        FrameId = "frame-0000",
                        Source = mediaRelative,
                    },
                ],
            });
            content = ImageDocumentSerializer.Serialize(document);
        }
        else
        {
            bool model = kind == ResourceKind.Model;
            if (model)
            {
                string modelData = ResourceAssociates.GetModelDataDirectory(resourcePath);
                transaction.CreateDirectory(modelData);
                if (!PathsEqual(originalSource, sourcePath))
                    transaction.Copy(originalSource, Path.Combine(modelData, "original" + Path.GetExtension(originalSource)));
                mediaDestination = Path.Combine(modelData, "source" + mediaExtension);
                if (mediaExtension.Equals(".gltf", StringComparison.OrdinalIgnoreCase))
                    CopyGltfDependencies(sourcePath, modelData, transaction);
            }
            else
            {
                mediaDestination = Path.Combine(parentFolder, resourceStem + mediaExtension);
            }
            string mediaRelative = Path.GetRelativePath(parentFolder, mediaDestination).Replace('\\', '/');
            content = definition.DefaultContent.Replace(
                $"\"{sourceProperty}\":null",
                $"\"{sourceProperty}\":\"{mediaRelative}\"",
                StringComparison.Ordinal);
        }

        transaction.WriteNewText(resourcePath, content);
        if (kind == ResourceKind.Image)
        {
            transaction.CreateDirectory(
                Path.GetDirectoryName(mediaDestination)
                ?? throw new InvalidOperationException("The sprite data path is invalid."));
        }

        transaction.Copy(sourcePath, mediaDestination);
        transaction.WriteNewText(
            MetadataPath(resourcePath),
            SerializeMetadata(kind, GetDisplayName(resourcePath), Guid.NewGuid(), DateTime.UtcNow));
        transaction.Commit();
        if (kind == ResourceKind.Model)
        {
            // Create the Player-ready asset while the importer still owns the operation. Waiting
            // until a preview happens made untouched imports fail only after F5 was pressed.
            ProjectModelCooker.CookOne(resourcePath);
        }
        Notify(ResourceChangeKind.Created, resourcePath);
        return resourcePath;
    }

    private static void CopyGltfDependencies(
        string gltfPath,
        string modelDataDirectory,
        ResourceFileTransaction transaction)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(gltfPath));
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(gltfPath)) ?? ".";
        string sourcePrefix = sourceDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string destinationPrefix = Path.GetFullPath(modelDataDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        HashSet<string> copied = new(StringComparer.OrdinalIgnoreCase);

        CopyUris("buffers");
        CopyUris("images");

        void CopyUris(string propertyName)
        {
            if (!document.RootElement.TryGetProperty(propertyName, out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("uri", out JsonElement uriJson)) continue;
                string uri = uriJson.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(uri)
                    || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    || Uri.TryCreate(uri, UriKind.Absolute, out _)) continue;
                string relative = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
                string source = Path.GetFullPath(Path.Combine(sourceDirectory, relative));
                string destination = Path.GetFullPath(Path.Combine(modelDataDirectory, relative));
                if (!source.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)
                    || !destination.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"glTF dependency escapes its source directory: '{uri}'.");
                if (!File.Exists(source)) throw new FileNotFoundException("A glTF dependency is missing.", source);
                if (copied.Add(destination)) transaction.Copy(source, destination);
            }
        }
    }

    private static bool IsImageExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".tga", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudioExtension(string extension) =>
        extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".flac", StringComparison.OrdinalIgnoreCase);

    private static bool IsModelExtension(string extension) =>
        extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".obj", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".dae", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".blend", StringComparison.OrdinalIgnoreCase);

    private void MoveResourceSet(
        string source,
        string destination,
        IReadOnlyList<string> associates,
        string oldStem,
        string newStem,
        string destinationDirectory,
        bool refreshMetadata = false)
    {
        using ResourceFileTransaction transaction = new();
        transaction.Move(source, destination);
        foreach (string associate in associates)
        {
            if (!File.Exists(associate) && !Directory.Exists(associate))
            {
                continue;
            }

            string associateDestination = ResourceAssociates.RemapAssociatePath(
                associate,
                oldStem,
                newStem,
                destinationDirectory);
            if (PathsEqual(associate, associateDestination))
            {
                continue;
            }

            transaction.Move(associate, associateDestination);
        }

        RemapSpriteAssociateReferences(destination, oldStem, newStem, transaction);
        RemapModelSourceReference(destination, oldStem, newStem, transaction);
        RemapTerrainPartReferences(source, destination, transaction);
        if (refreshMetadata)
        {
            RefreshMetadataDisplayName(destination, transaction);
        }

        transaction.Commit();
    }

    private void CopyResourceSet(
        string source,
        string destination,
        IReadOnlyList<string> associates,
        string oldStem,
        string newStem,
        string destinationDirectory,
        Genesis.Shared.Assets.ResourceCatalog before)
    {
        using ResourceFileTransaction transaction = new();
        transaction.Copy(source, destination);
        CopyAssociates(transaction, associates, oldStem, newStem, destinationDirectory);
        RemapSpriteAssociateReferences(destination, oldStem, newStem, transaction);
        RemapModelSourceReference(destination, oldStem, newStem, transaction);
        RemapTerrainPartReferences(source, destination, transaction);
        RegenerateMetadata(destination);
        ResourceReferenceOperations.RebindCopies(Project, before, source, destination, transaction);
        transaction.Commit();
    }

    private void RemapTerrainPartReferences(string source, string destination, ResourceFileTransaction transaction)
    {
        if (ResourceDefinitions.FromPath(destination)?.Kind != ResourceKind.Terrain) return;
        string previous = Path.GetRelativePath(Project.RootPath, source + ".parts").Replace('\\', '/') + "/";
        string replacement = Path.GetRelativePath(Project.RootPath, destination + ".parts").Replace('\\', '/') + "/";
        if (previous.Equals(replacement, StringComparison.OrdinalIgnoreCase)) return;
        foreach (string path in new[] { destination, destination + ".nature.json" })
        {
            if (!File.Exists(path)) continue;
            JsonNode? document = JsonNode.Parse(File.ReadAllText(path));
            bool changed = false;
            void Visit(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    foreach (var entry in obj.ToArray())
                    {
                        if (Remap(entry.Value) is string value) { obj[entry.Key] = value; changed = true; }
                        else Visit(entry.Value);
                    }
                }
                else if (node is JsonArray array)
                {
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (Remap(array[i]) is string value) { array[i] = value; changed = true; }
                        else Visit(array[i]);
                    }
                }
            }
            string? Remap(JsonNode? node)
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out string? text)) return null;
                string normalized = text.Replace('\\', '/');
                return normalized.StartsWith(previous, StringComparison.OrdinalIgnoreCase)
                    ? replacement + normalized[previous.Length..] : null;
            }
            Visit(document);
            if (changed) transaction.WriteText(path, document!.ToJsonString(_jsonOptions));
        }
    }

    private static void CopyAssociates(
        ResourceFileTransaction transaction,
        IReadOnlyList<string> associates,
        string oldStem,
        string newStem,
        string destinationDirectory)
    {
        foreach (string associate in associates)
        {
            if (!File.Exists(associate) && !Directory.Exists(associate))
            {
                continue;
            }

            string destination = ResourceAssociates.RemapAssociatePath(
                associate,
                oldStem,
                newStem,
                destinationDirectory);
            transaction.Copy(associate, destination);
        }
    }

    private static void RemapSpriteAssociateReferences(
        string resourcePath,
        string oldStem,
        string newStem,
        ResourceFileTransaction transaction)
    {
        if (string.Equals(oldStem, newStem, StringComparison.OrdinalIgnoreCase) ||
            ResourceDefinitions.FromPath(resourcePath)?.Kind != ResourceKind.Image)
        {
            return;
        }

        ImageDocument document = ImageDocumentSerializer.LoadAtomic(resourcePath).Document;
        document.Import.Source = RemapRelativeAssociatePath(document.Import.Source, oldStem, newStem);
        foreach (ImageFrame frame in document.Frames)
        {
            frame.Source = RemapRelativeAssociatePath(frame.Source, oldStem, newStem);
        }

        foreach (ImageLayer layer in document.Layers)
        {
            foreach (ImageCel cel in layer.Cels)
            {
                cel.Source = RemapRelativeAssociatePath(cel.Source, oldStem, newStem);
            }
        }

        foreach (ImageMaterialChannelDefinition channel in document.MaterialChannels)
        {
            channel.Source = RemapRelativeAssociatePath(channel.Source, oldStem, newStem);
        }

        transaction.WriteText(resourcePath, ImageDocumentSerializer.Serialize(document));
    }

    private static void RemapModelSourceReference(
        string resourcePath,
        string oldStem,
        string newStem,
        ResourceFileTransaction transaction)
    {
        if (string.Equals(oldStem, newStem, StringComparison.OrdinalIgnoreCase)
            || ResourceDefinitions.FromPath(resourcePath)?.Kind != ResourceKind.Model) return;
        JsonObject document = JsonNode.Parse(File.ReadAllText(resourcePath)) as JsonObject ?? new JsonObject();
        string source = document["source"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source)) return;
        document["source"] = RemapRelativeAssociatePath(source, oldStem, newStem);
        transaction.WriteText(resourcePath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? RemapRelativeAssociatePath(
        string? relativePath,
        string oldStem,
        string newStem)
    {
        if (relativePath is null)
        {
            return null;
        }

        string normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith(oldStem, StringComparison.OrdinalIgnoreCase)
            ? newStem + normalized[oldStem.Length..]
            : normalized;
    }

    private Guid EnsureMetadata(string resourcePath, ResourceKind kind)
    {
        string metadataPath = MetadataPath(resourcePath);
        if (File.Exists(metadataPath))
        {
            try
            {
                // Identity validation must not deserialize optional library classification.
                // A malformed tag list is not permission to replace a valid resource GUID.
                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                if (metadata.RootElement.ValueKind == JsonValueKind.Object
                    && metadata.RootElement.TryGetProperty("guid", out JsonElement guid)
                    && guid.ValueKind == JsonValueKind.String
                    && Guid.TryParseExact(guid.GetString(), "N", out Guid existing))
                {
                    return existing;
                }
            }
            catch (JsonException)
            {
                // Replaced below with valid metadata; validation reports malformed files separately.
            }
        }

        Guid id = Guid.NewGuid();
        WriteMetadata(resourcePath, kind, GetDisplayName(resourcePath), id, DateTime.UtcNow);
        return id;
    }

    private void WriteMetadata(
        string resourcePath,
        ResourceKind kind,
        string displayName,
        Guid id,
        DateTime createdUtc,
        IReadOnlyList<string>? libraryTags = null)
    {
        File.WriteAllText(
            MetadataPath(resourcePath),
            SerializeMetadata(kind, displayName, id, createdUtc, libraryTags));
        ResourceLibraryTags.Invalidate(resourcePath);
    }

    private string SerializeMetadata(
        ResourceKind kind,
        string displayName,
        Guid id,
        DateTime createdUtc,
        IReadOnlyList<string>? libraryTags = null)
    {
        AssetMetadata metadata = new()
        {
            Guid = id.ToString("N"),
            Kind = kind.ToString(),
            DisplayName = displayName,
            ResourceName = displayName,
            LibraryTags = ResourceLibraryTags.Normalize(libraryTags ?? []).ToList(),
            CreatedUtc = createdUtc,
            ModifiedUtc = DateTime.UtcNow,
        };
        return JsonSerializer.Serialize(metadata, _jsonOptions);
    }

    private void RefreshMetadataDisplayName(
        string resourcePath,
        ResourceFileTransaction? transaction = null)
    {
        ResourceDefinition? definition = ResourceDefinitions.FromPath(resourcePath);
        ResourceKind kind = definition?.Kind ?? ResourceKind.Unknown;
        string metaPath = MetadataPath(resourcePath);
        Guid id = Guid.NewGuid();
        DateTime created = DateTime.UtcNow;
        string publicName = GetDisplayName(resourcePath);

        if (File.Exists(metaPath))
        {
            try
            {
                AssetMetadata? current = JsonSerializer.Deserialize<AssetMetadata>(
                    File.ReadAllText(metaPath),
                    _jsonOptions);
                if (current is not null)
                {
                    Guid.TryParseExact(current.Guid, "N", out id);
                    created = current.CreatedUtc;
                    publicName = current.ResourceName ?? current.DisplayName ?? publicName;
                }
            }
            catch (JsonException)
            {
                // Invalid metadata is repaired while preserving the resource itself.
            }
        }

        if (id == Guid.Empty)
        {
            id = Guid.NewGuid();
        }

        string content = SerializeMetadata(kind, publicName, id, created, ResourceLibraryTags.ReadOrEmpty(resourcePath));
        if (transaction is null)
        {
            File.WriteAllText(metaPath, content);
        }
        else
        {
            transaction.WriteText(metaPath, content);
        }
    }

    private void RegenerateMetadata(string copiedPath)
    {
        if (Directory.Exists(copiedPath))
        {
            string[] files = Directory.EnumerateFiles(
                    copiedPath,
                    "*",
                    SearchOption.AllDirectories)
                .ToArray();
            HashSet<string> associateFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (string resource in files.Where(
                         path => ResourceDefinitions.FromPath(path) is not null))
            {
                foreach (string associate in ResourceAssociates.Find(resource))
                {
                    if (Directory.Exists(associate))
                    {
                        foreach (string nested in Directory.EnumerateFiles(
                                     associate,
                                     "*",
                                     SearchOption.AllDirectories))
                        {
                            associateFiles.Add(Path.GetFullPath(nested));
                        }
                    }
                    else
                    {
                        associateFiles.Add(Path.GetFullPath(associate));
                    }
                }
            }

            foreach (string file in files.Where(
                         path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                                 !associateFiles.Contains(Path.GetFullPath(path))))
            {
                RegenerateMetadata(file);
            }

            return;
        }

        ResourceDefinition? definition = ResourceDefinitions.FromPath(copiedPath);
        ResourceNames.Invalidate(Project.RootPath);
        string originalName = GetDisplayName(copiedPath);
        if (File.Exists(copiedPath + ".meta"))
        {
            JsonNode? metadata = JsonNode.Parse(File.ReadAllText(copiedPath + ".meta"));
            originalName = metadata?["resourceName"]?.GetValue<string>() ?? originalName;
        }
        string publicName = ResourceNames.For(Project.RootPath).UniqueName(originalName, copiedPath);
        WriteMetadata(
            copiedPath,
            definition?.Kind ?? ResourceKind.Unknown,
            publicName,
            Guid.NewGuid(),
            DateTime.UtcNow,
            ResourceLibraryTags.ReadOrEmpty(copiedPath));
    }

    private string RequireFolder(string path)
    {
        string fullPath = EnsureInsideAssets(path, allowRoot: true);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Resource folder '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    private string RequireItem(string path)
    {
        string fullPath = EnsureInsideAssets(path, allowRoot: true);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected resource no longer exists.", fullPath);
        }

        return fullPath;
    }

    private string EnsureInsideAssets(string path, bool allowRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetFullPath(AssetsRoot).TrimEnd(Path.DirectorySeparatorChar);
        if ((allowRoot && PathsEqual(fullPath, root)) ||
            fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            ResourceFolderPolicy.RejectLinks(AssetsRoot, fullPath);
            return fullPath;
        }

        throw new UnauthorizedAccessException("Resource operations are restricted to the project Assets folder.");
    }

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string trimmed = name.Trim();
        if (trimmed is "." or ".." ||
            trimmed.EndsWith('.') ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{name}' is not a valid resource name.", nameof(name));
        }

        return trimmed;
    }

    private static string GetDisplayName(string path)
    {
        string fileName = Path.GetFileName(path);
        ResourceDefinition? definition = ResourceDefinitions.FromPath(fileName);
        return definition is null
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName[..^definition.Extension.Length];
    }

    private string GetUniquePath(string requestedPath, bool isFolder)
    {
        EnsureInsideAssets(requestedPath, allowRoot: false);
        if (!isFolder && ResourceDefinitions.FromPath(requestedPath) is { } resourceDefinition)
        {
            ResourceNames.Invalidate(Project.RootPath);
            string unique = ResourceNames.For(Project.RootPath).UniqueName(GetDisplayName(requestedPath));
            requestedPath = Path.Combine(Path.GetDirectoryName(requestedPath)!, unique + resourceDefinition.Extension);
        }
        if (!File.Exists(requestedPath) && !Directory.Exists(requestedPath))
        {
            return requestedPath;
        }

        string? parent = Path.GetDirectoryName(requestedPath);
        if (parent is null)
        {
            throw new InvalidOperationException("The resource destination has no parent folder.");
        }

        string fileName = Path.GetFileName(requestedPath);
        ResourceDefinition? definition = isFolder ? null : ResourceDefinitions.FromPath(fileName);
        string extension = isFolder
            ? string.Empty
            : definition?.Extension ?? Path.GetExtension(fileName);
        string stem = isFolder
            ? fileName
            : fileName[..^extension.Length];

        for (int index = 2; index < 10_000; index++)
        {
            string candidate = Path.Combine(parent, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)
                && (isFolder || !ResourceNames.For(Project.RootPath).Entries.Any(entry =>
                    string.Equals(entry.Name, GetDisplayName(candidate), StringComparison.OrdinalIgnoreCase))))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to find a unique resource name.");
    }

    private string ToAssetRelative(string path) =>
        Path.GetRelativePath(AssetsRoot, path).Replace('\\', '/');

    private static string MetadataPath(string resourcePath) => resourcePath + ".meta";

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendant(string candidate, string potentialParent)
    {
        string candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
        string parentFull = Path.GetFullPath(potentialParent).TrimEnd(Path.DirectorySeparatorChar);
        return PathsEqual(candidateFull, parentFull) ||
               candidateFull.StartsWith(
                   parentFull + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void Notify(ResourceChangeKind kind, string path, string? previousPath = null)
    {
        ResourceNames.Invalidate(Project.RootPath);
        Changed?.Invoke(this, new ResourceChangedEventArgs(kind, path, previousPath));
    }
}

public sealed class ResourceChangedEventArgs(
    ResourceChangeKind kind,
    string path,
    string? previousPath) : EventArgs
{
    public ResourceChangeKind Kind { get; } = kind;

    public string Path { get; } = path;

    public string? PreviousPath { get; } = previousPath;
}

public enum ResourceChangeKind
{
    Created,
    Renamed,
    Copied,
    Moved,
    Deleted,
    Refreshed,
}
