using Genesis.Application.Core.Projects;

namespace Genesis.Application.Core.Resources;

public sealed record ResourceRootFolder(string Name, ResourceKind Kind);

/// <summary>The single source of truth for the protected, typed project resource roots.</summary>
public static class ResourceFolderPolicy
{
    public static IReadOnlyList<ResourceRootFolder> Roots { get; } = Array.AsReadOnly<ResourceRootFolder>(
    [
        new("Sprites", ResourceKind.Image),
        new("Audio", ResourceKind.Audio),
        new("Objects", ResourceKind.GameObject),
        new("Particles", ResourceKind.Particle),
        new("Shaders", ResourceKind.Shader),
        new("Scripts", ResourceKind.PgslScript),
        new("Physics", ResourceKind.Physics),
        new("Terrain", ResourceKind.Terrain),
        new("Rooms", ResourceKind.Room),
        new("Notes", ResourceKind.Note),
        new("Models", ResourceKind.Model),
        new("Paths", ResourceKind.Pathing),
        new("User Interfaces", ResourceKind.UserInterface),
    ]);

    public static void EnsureRoots(ProjectSession project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (ResourceRootFolder root in Roots)
        {
            string path = Path.Combine(project.AssetsPath, root.Name);
            if (File.Exists(path))
                throw new IOException($"'{path}' is a file, but a protected resource folder is required there.");
            RejectLinks(project.AssetsPath, path);
            Directory.CreateDirectory(path);
        }
    }

    public static string RootFor(ProjectSession project, ResourceKind kind)
    {
        ResourceRootFolder root = Roots.FirstOrDefault(root => root.Kind == kind)
            ?? throw new InvalidOperationException($"{kind} is not a standalone resource type.");
        return Path.Combine(project.AssetsPath, root.Name);
    }

    public static ResourceRootFolder? GetRoot(ProjectSession project, string path)
    {
        string relative = Path.GetRelativePath(project.AssetsPath, Path.GetFullPath(path));
        string first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return Roots.FirstOrDefault(root => string.Equals(root.Name, first, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsProtected(ProjectSession project, string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Same(full, project.AssetsPath)) return true;
        return Roots.Any(root => Same(full, Path.Combine(project.AssetsPath, root.Name)));
    }

    public static void RequireMutable(ProjectSession project, string path)
    {
        if (IsProtected(project, path))
            throw new InvalidOperationException("Protected root resource folders cannot be renamed, deleted, moved, copied or cut. Create a subfolder inside the appropriate root instead.");
    }

    public static string Destination(ProjectSession project, string folder, ResourceKind kind)
    {
        string full = Path.GetFullPath(folder);
        // New/Import at the Assets heading is a convenience, never an untyped resource.
        if (Same(full, project.AssetsPath)) return RootFor(project, kind);
        ResourceRootFolder? root = GetRoot(project, full);
        if (root?.Kind != kind)
            throw new InvalidOperationException($"{ResourceDefinitions.Get(kind).DisplayName} resources belong in '{Path.GetFileName(RootFor(project, kind))}' or one of its subfolders.");
        return full;
    }

    public static void RequireSubfolderParent(ProjectSession project, string folder)
    {
        if (GetRoot(project, folder) is null)
            throw new InvalidOperationException("Choose a protected resource folder first. Custom folders can be created inside it, not beside the resource roots.");
    }

    /// <summary>Do not allow a junction/symbolic link to bypass the Assets boundary or recurse forever.</summary>
    internal static void RejectLinks(string assetsRoot, string path)
    {
        string root = Path.GetFullPath(assetsRoot);
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Linked resource paths are not supported: " + current);
            if (Same(current, root)) break;
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool Same(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
}
