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

        foreach (string file in Directory.EnumerateFiles(directory))
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

            if (!owned)
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
