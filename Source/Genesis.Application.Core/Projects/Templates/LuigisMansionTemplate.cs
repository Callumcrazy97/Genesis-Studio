using System.IO.Compression;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>Native PGSL campaign and editable resources. No separate executable or GML game loop.</summary>
public static class LuigisMansionTemplate
{
    public const string TemplateId = "LuigisMansion";
    public const int CurrentRevision = 7;
    public const string StartRoom = "Assets/Rooms/00 - A Light in the Dark.room.json";

    public static void Apply(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string archivePath = Path.Combine(AppContext.BaseDirectory, "Templates", "LuigisMansion.zip");
        if (File.Exists(archivePath))
        {
            // Prefer the current package even if an older build left loose template files.
            CopyPackedAssets(archivePath, session);
        }
        else
        {
            // Compatibility with previously published loose-content builds.
            string bundle = Path.Combine(AppContext.BaseDirectory, "Projects", "Templates", "Assets", "LuigisMansion");
            string assets = Path.Combine(bundle, "Assets");
            if (!Directory.Exists(assets))
                throw new DirectoryNotFoundException("Luigi's Mansion template assets are missing. Rebuild Studio to create Templates/LuigisMansion.zip.");
            foreach (string source in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(session.RootPath, Path.GetRelativePath(bundle, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }
        }
        ResourceFolderPolicy.EnsureRoots(session);
        session.Manifest.StartRoom = StartRoom;
        session.Manifest.Description = "A Light in the Dark: six haunted 2D chapters, torch-and-Poltergust combat, and editable PGSL.";
        session.Manifest.Runtime.AllowEscapeToClose = false;
        session.Manifest.Rendering.FogEnabled = false;
        session.Manifest.TemplateRevision = CurrentRevision;
    }

    /// <summary>
    /// Applies only template-owned files touched by a Luigi template hotfix. Existing files are
    /// backed up under .genesis before replacement; arbitrary user resources are never swept.
    /// </summary>
    public static bool UpgradeIfNeeded(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Manifest.TemplateRevision >= CurrentRevision) return false;

        string archivePath = Path.Combine(AppContext.BaseDirectory, "Templates", "LuigisMansion.zip");
        if (!File.Exists(archivePath)) return false;

        string backupRoot = Path.Combine(session.InternalPath, "Backups", "TemplateHotfix7");
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('\\', '/');
            if (relative.EndsWith('/') || !IsHotfix7OwnedPath(relative)) continue;
            string destination = Path.GetFullPath(Path.Combine(session.AssetsPath, relative.Replace('/', Path.DirectorySeparatorChar)));
            string assetsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.AssetsPath)) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe Luigi template hotfix path: " + relative);

            if (File.Exists(destination))
            {
                string backup = Path.Combine(backupRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(destination, backup, overwrite: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        session.Manifest.TemplateRevision = CurrentRevision;
        session.Manifest.ModifiedUtc = DateTime.UtcNow;
        return true;
    }

    private static bool IsHotfix7OwnedPath(string relative)
    {
        if (relative.StartsWith("Sprites/Gameplay/Luigi ", StringComparison.OrdinalIgnoreCase)
            && relative.Contains(" Aim ", StringComparison.OrdinalIgnoreCase)) return true;
        return relative is
            "Objects/Luigi/Create.pgsl" or
            "Objects/Luigi/Step.pgsl" or
            "Objects/Toad/Create.pgsl" or
            "Objects/Toad/Step.pgsl" or
            "Objects/Treasure Chest/Create.pgsl" or
            "Objects/Treasure Chest/Step.pgsl" or
            "Objects/Gold Ghost/Create.pgsl" or
            "Objects/Gold Ghost/Step.pgsl" or
            "Objects/Gallery Wraith/Create.pgsl" or
            "Objects/Gallery Wraith/Step.pgsl" or
            "Objects/Portrait Keeper/Create.pgsl" or
            "Objects/Portrait Keeper/Step.pgsl" or
            "Objects/Campaign Director/Create.pgsl" or
            "Objects/Campaign Director/Step.pgsl" or
            "Objects/Campaign Director/DrawGui.pgsl" or
            "Objects/Mansion Menu/Create.pgsl" or
            "Objects/Mansion Menu/Step.pgsl" or
            "Objects/Mansion Menu/DrawGui.pgsl" or
            "Notes/Hotfix 7 - Restored gameplay.md";
    }

    private static void CopyPackedAssets(string archivePath, ProjectSession session)
    {
        // The archive is rooted at Assets, not at the complete source-template folder.
        // Never replace the new project's identity/manifest or unpack back into bin.
        string assetsRoot = Path.GetFullPath(session.AssetsPath);
        string assetsPrefix = Path.TrimEndingDirectorySeparator(assetsRoot) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        List<(ZipArchiveEntry Entry, string Destination)> files = [];
        HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('\\', '/');
            if (relative.EndsWith('/')) continue;
            string[] segments = relative.Split('/');
            if (Path.IsPathRooted(relative)
                || relative.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) >= 0
                || segments.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
                throw new InvalidDataException("Invalid template asset path: " + entry.FullName);

            string destination = Path.GetFullPath(Path.Combine(assetsRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase) || !destinations.Add(destination))
                throw new InvalidDataException("Unsafe or duplicate template asset path: " + entry.FullName);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new IOException("Template installation would overwrite an existing resource: " + destination);
            files.Add((entry, destination));
        }
        string startRoom = Path.GetFullPath(Path.Combine(session.RootPath, StartRoom.Replace('/', Path.DirectorySeparatorChar)));
        if (!destinations.Contains(startRoom))
            throw new InvalidDataException("The Luigi's Mansion template bundle does not contain its start room. Rebuild Studio.");

        // Preflight parent/file conflicts before writing any template resources.
        foreach ((ZipArchiveEntry _, string destination) in files)
        {
            string? parent = Path.GetDirectoryName(destination);
            while (parent is not null && !string.Equals(parent, assetsRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (destinations.Contains(parent) || File.Exists(parent))
                    throw new InvalidDataException("A template asset conflicts with a resource folder: " + parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        foreach ((ZipArchiveEntry entry, string destination) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }
}
