using System.Collections.Generic;

namespace Genesis.Application.Core.Resources;

/// <summary>
/// Discovers sidecar files that belong with a resource (meta, images, audio, layers, rigs).
/// </summary>
public static class ResourceAssociates
{
    private static readonly string[] HiddenImplementationSuffixes =
    [
        ".meta",
        ".gterrain",
        ".gfoliage",
        ".nature.json",
        ".terrain.json.nature",
        ".terrain.nature",
        ".mesh",
        ".mesh.bin",
        ".bakedmesh",
        ".cache",
        ".terrainentity.json",
        ".terrainpart.json",
    ];

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tga",
        ".wav", ".mp3", ".ogg", ".flac",
        ".fbx", ".obj", ".gltf", ".glb", ".dae", ".blend",
        ".gmodel", ".rig", ".bin",
    };

    public static string GetStem(string resourcePath)
    {
        string fileName = Path.GetFileName(resourcePath);
        ResourceDefinition? definition = ResourceDefinitions.FromPath(fileName);
        return definition is null
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName[..^definition.Extension.Length];
    }

    public static bool IsHiddenImplementationFile(string path)
    {
        string name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string suffix in HiddenImplementationSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Legacy/temporary terrain payloads sometimes sit beside the canonical
        // .terrain.json document as "Ground.terrain". They are data, not the user-visible
        // Terrain resource, so the Assets tree and Finder hide them unless/until a proper
        // Terrain resource wrapper is authored.
        if (name.EndsWith(".terrain", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool IsDesignerVisibleResourcePath(string path) =>
        ResourceDefinitions.FromPath(path) is not null
        && !IsHiddenImplementationFile(path);

    public static string GetSpriteDataDirectory(string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        string fullPath = Path.GetFullPath(resourcePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The sprite resource has no parent directory.");
        }

        return Path.Combine(directory, GetStem(fullPath) + ".spritedata");
    }

    /// <summary>Owned source dependencies and extracted textures for an imported Model.</summary>
    public static string GetModelDataDirectory(string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        string fullPath = Path.GetFullPath(resourcePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The model resource has no parent directory.");
        return Path.Combine(directory, GetStem(fullPath) + ".modeldata");
    }

    /// <summary>Folder holding an object's per-event PGSL scripts.</summary>
    public static string GetObjectEventDirectory(string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        string fullPath = Path.GetFullPath(resourcePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The object resource has no parent directory.");
        }

        return Path.Combine(directory, GetStem(fullPath));
    }

    public static IReadOnlyList<string> Find(string resourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        if (Directory.Exists(resourcePath))
        {
            return [];
        }

        string fullPath = Path.GetFullPath(resourcePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        string stem = GetStem(fullPath);
        List<string> results = [];
        string meta = fullPath + ".meta";
        if (File.Exists(meta))
        {
            results.Add(meta);
        }

        // Whole-file sidecars that append a suffix to the complete resource file name
        // (same convention as .meta). ".gterrain" is the terrain editor's binary heights.
        string terrainData = fullPath + ".gterrain";
        if (File.Exists(terrainData))
        {
            results.Add(terrainData);
        }

        string legacyTerrainPayload = Path.Combine(directory, stem + ".terrain");
        if (File.Exists(legacyTerrainPayload))
        {
            results.Add(legacyTerrainPayload);
        }

        string terrainNature = fullPath + ".nature.json";
        string terrainParts = fullPath + ".parts";
        if (Directory.Exists(terrainParts)) results.Add(terrainParts);
        if (File.Exists(terrainNature))
        {
            results.Add(terrainNature);
        }

        string legacyTerrainNature = fullPath + ".nature";
        if (File.Exists(legacyTerrainNature))
        {
            results.Add(legacyTerrainNature);
        }

        if (ResourceDefinitions.FromPath(fullPath)?.Kind == ResourceKind.Image)
        {
            string spriteDataDirectory = GetSpriteDataDirectory(fullPath);
            if (Directory.Exists(spriteDataDirectory))
            {
                results.Add(spriteDataDirectory);
            }
        }

        if (ResourceDefinitions.FromPath(fullPath)?.Kind == ResourceKind.Model)
        {
            string modelDataDirectory = GetModelDataDirectory(fullPath);
            if (Directory.Exists(modelDataDirectory)) results.Add(modelDataDirectory);
        }

        // An object's event scripts live in a folder named after it. They belong to the object, not
        // to the project, so the folder is an associate and the tree hides it — otherwise every
        // object appeared twice, once as itself and once as a folder full of loose event files.
        if (ResourceDefinitions.FromPath(fullPath)?.Kind == ResourceKind.GameObject)
        {
            string eventDirectory = GetObjectEventDirectory(fullPath);
            if (Directory.Exists(eventDirectory))
            {
                results.Add(eventDirectory);
            }
        }

        string[] files = Directory.EnumerateFiles(directory).ToArray();
        List<string> longerStems = LongerResourceStems(stem, files);
        foreach (string file in files)
        {
            string candidate = Path.GetFullPath(file);
            if (string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, meta, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string baseName = Path.GetFileNameWithoutExtension(candidate);
            string extension = Path.GetExtension(candidate);
            bool owned =
                string.Equals(baseName, stem, StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith(stem + "__layer_", StringComparison.OrdinalIgnoreCase);

            if (!owned || BelongsToLongerResource(Path.GetFileName(candidate), longerStems))
            {
                continue;
            }

            if (MediaExtensions.Contains(extension) ||
                extension.Equals(".meta", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    /// <summary>
    /// The files and folders directly inside one folder, listed once. <see cref="Find(string, FolderListing)"/>
    /// uses it so that finding the associates of every resource in a folder does not list the
    /// folder again for each resource: that made the Assets tree of a folder holding thousands of
    /// resources take seconds.
    /// </summary>
    public sealed class FolderListing
    {
        public FolderListing(string directory, IEnumerable<string> files, IEnumerable<string> directories)
        {
            Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            Files = new HashSet<string>(files.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            Directories = new HashSet<string>(directories.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            ByBaseName = Files
                .Select(file => (BaseName: Path.GetFileNameWithoutExtension(file), Path: file))
                .OrderBy(entry => entry.BaseName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>Lists <paramref name="directory"/> now.</summary>
        public static FolderListing Read(string directory) =>
            new(directory, System.IO.Directory.EnumerateFiles(directory), System.IO.Directory.EnumerateDirectories(directory));

        public string Directory { get; }
        internal HashSet<string> Files { get; }
        internal HashSet<string> Directories { get; }
        internal (string BaseName, string Path)[] ByBaseName { get; }
    }

    /// <summary>
    /// The same associates as <see cref="Find(string)"/> (possibly in another order) for a resource
    /// directly inside the listed folder, answered from the listing instead of the disk.
    /// </summary>
    public static IReadOnlyList<string> Find(string resourcePath, FolderListing listing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePath);
        ArgumentNullException.ThrowIfNull(listing);
        string fullPath = Path.GetFullPath(resourcePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is null || !string.Equals(directory, listing.Directory, StringComparison.OrdinalIgnoreCase))
            return Find(resourcePath);
        if (listing.Directories.Contains(fullPath)) return [];

        string stem = GetStem(fullPath);
        List<string> results = [];
        void AddFile(string path) { if (listing.Files.Contains(path)) results.Add(path); }
        void AddDirectory(string path) { if (listing.Directories.Contains(path)) results.Add(path); }

        string meta = fullPath + ".meta";
        AddFile(meta);
        AddFile(fullPath + ".gterrain");
        AddFile(Path.Combine(directory, stem + ".terrain"));
        AddDirectory(fullPath + ".parts");
        AddFile(fullPath + ".nature.json");
        AddFile(fullPath + ".nature");
        ResourceKind? kind = ResourceDefinitions.FromPath(fullPath)?.Kind;
        if (kind == ResourceKind.Image) AddDirectory(Path.Combine(directory, stem + ".spritedata"));
        if (kind == ResourceKind.Model) AddDirectory(Path.Combine(directory, stem + ".modeldata"));
        if (kind == ResourceKind.GameObject) AddDirectory(Path.Combine(directory, stem));

        // Owned siblings have a base name equal to the stem or starting with "stem_", so they all
        // start with the stem: one contiguous run of the listing sorted by base name.
        (string BaseName, string Path)[] sorted = listing.ByBaseName;
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (string.Compare(sorted[middle].BaseName, stem, StringComparison.OrdinalIgnoreCase) < 0) low = middle + 1;
            else high = middle;
        }
        int end = low;
        while (end < sorted.Length && sorted[end].BaseName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) end++;
        // A longer-named resource's own files sort into the same run, so the run holds every one.
        List<string> longerStems = LongerResourceStems(stem, sorted[low..end].Select(entry => entry.Path));
        for (int index = low; index < end; index++)
        {
            (string baseName, string candidate) = sorted[index];
            if (string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate, meta, StringComparison.OrdinalIgnoreCase))
                continue;
            bool owned =
                string.Equals(baseName, stem, StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase) ||
                baseName.StartsWith(stem + "__layer_", StringComparison.OrdinalIgnoreCase);
            if (!owned || BelongsToLongerResource(Path.GetFileName(candidate), longerStems)) continue;
            string extension = Path.GetExtension(candidate);
            if (MediaExtensions.Contains(extension) || extension.Equals(".meta", StringComparison.OrdinalIgnoreCase))
                results.Add(candidate);
        }

        return results;
    }

    // A name starting "stem_" usually belongs to the resource ("Hero_1.png" is a frame of Hero), but
    // not when another resource in the folder has that longer name: "music_village_night.wav" and
    // its .meta belong to "music_village_night", and deleting or renaming "music_village" took them.
    private static List<string> LongerResourceStems(string stem, IEnumerable<string> files)
    {
        List<string> stems = [];
        foreach (string file in files)
        {
            if (ResourceDefinitions.FromPath(file) is null) continue;
            string other = GetStem(file);
            if (other.Length > stem.Length + 1 && other.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase))
                stems.Add(other);
        }
        return stems;
    }

    private static bool BelongsToLongerResource(string fileName, List<string> longerStems)
    {
        foreach (string other in longerStems)
            if (fileName.Length > other.Length &&
                fileName.StartsWith(other, StringComparison.OrdinalIgnoreCase) &&
                fileName[other.Length] is '_' or '.')
                return true;
        return false;
    }

    public static IReadOnlyList<string> FindGeneratedFrames(string resourcePath)
    {
        string dataDirectory = GetSpriteDataDirectory(resourcePath);
        if (!Directory.Exists(dataDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories)
            .Where(path => MediaExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string RemapAssociatePath(string associatePath, string oldStem, string newStem, string destinationDirectory)
    {
        string fileName = Path.GetFileName(associatePath);
        string remapped = ReplaceStemPrefix(fileName, oldStem, newStem);
        return Path.Combine(destinationDirectory, remapped);
    }

    public static string? FindPrimaryImage(string resourcePath)
    {
        string? directory = Path.GetDirectoryName(resourcePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string stem = GetStem(resourcePath);
        string[] ordered =
        [
            Path.Combine(directory, stem + ".png"),
            Path.Combine(directory, stem + ".jpg"),
            Path.Combine(directory, stem + ".jpeg"),
            Path.Combine(directory, stem + ".bmp"),
            Path.Combine(directory, stem + ".webp"),
            Path.Combine(directory, stem + ".gif"),
            Path.Combine(directory, stem + ".tga"),
        ];

        foreach (string path in ordered)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        foreach (string generatedFrame in FindGeneratedFrames(resourcePath))
        {
            if (IsImageExtension(Path.GetExtension(generatedFrame)))
            {
                return generatedFrame;
            }
        }

        foreach (string associate in Find(resourcePath))
        {
            if (!File.Exists(associate))
            {
                continue;
            }

            string extension = Path.GetExtension(associate);
            if (IsImageExtension(extension))
            {
                return associate;
            }
        }

        return null;
    }

    private static bool IsImageExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".tga", StringComparison.OrdinalIgnoreCase);

    private static string ReplaceStemPrefix(string fileName, string oldStem, string newStem)
    {
        if (fileName.StartsWith(oldStem, StringComparison.OrdinalIgnoreCase))
        {
            return newStem + fileName[oldStem.Length..];
        }

        return fileName;
    }
}
