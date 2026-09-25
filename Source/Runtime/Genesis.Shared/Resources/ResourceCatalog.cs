using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Genesis.Shared.Assets;

/// <summary>The type of a named authoring resource. Payload files and object events are not resources.</summary>
public enum ResourceType
{
    Unknown, Image, Audio, Object, Room, Model, Shader, Particle, Physics,
    Terrain, TerrainEntity, Pathing, UserInterface, Script, Note
}

/// <summary>Public identity and private storage location. Only Name is a resource reference.</summary>
public sealed record NamedResource(string Name, string FullPath, ResourceType Type, string Extension)
{
    public string StorageName => ResourceCatalog.FormatName(FullPath);
    /// <summary>Private stable identity for editor bookmarks/dependencies; public references remain Name.</summary>
    public Guid AssetId { get; init; }
}

/// <summary>
/// One project-wide, case-insensitive namespace shared by Studio, Engine and scripting.
/// Indexes descriptors, never their PNG/WAV payloads, event scripts or editor caches.
/// A resource's optional metadata resourceName is authoritative; the filename is only storage.
/// Rebuilds are explicit, not recursive directory scans in per-frame resource lookups.
/// </summary>
public sealed class ResourceCatalog
{
    public static readonly (string Extension, ResourceType Type)[] Definitions =
    {
        (".terrainentity.json", ResourceType.TerrainEntity),
        (".particle.json", ResourceType.Particle), (".physics.json", ResourceType.Physics),
        (".terrain.json", ResourceType.Terrain), (".shader.json", ResourceType.Shader),
        (".object.json", ResourceType.Object), (".image.json", ResourceType.Image),
        (".audio.json", ResourceType.Audio), (".model.json", ResourceType.Model),
        (".room.json", ResourceType.Room), (".pathing.json", ResourceType.Pathing),
        (".ui.json", ResourceType.UserInterface), (".pathing", ResourceType.Pathing),
        (".pgsl", ResourceType.Script), (".cs", ResourceType.Script), (".md", ResourceType.Note)
    };
    private static readonly string[] LegacyRoots =
    { "Sprites", "Images", "Audio", "Objects", "Particles", "Shaders", "Scripts", "Physics", "Terrain", "Rooms", "Notes", "Models", "Paths", "User Interfaces", "UI" };
    private static readonly ConcurrentDictionary<string, Lazy<ResourceCatalog>> Catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NamedResource[]> _names;
    private readonly Dictionary<string, NamedResource> _paths;
    public string ProjectRoot { get; }
    public IReadOnlyList<NamedResource> Entries { get; }
    public IEnumerable<IGrouping<string, NamedResource>> Conflicts => Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);

    private ResourceCatalog(string projectRoot)
    {
        ProjectRoot = projectRoot;
        var entries = new List<NamedResource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string assets = Path.Combine(projectRoot, "Assets");
        if (Directory.Exists(assets)) Scan(assets, entries, seen);
        foreach (string folder in LegacyRoots)
        {
            string path = Path.Combine(projectRoot, folder);
            if (Directory.Exists(path)) Scan(path, entries, seen);
        }
        // Small standalone/editor preview projects can place their descriptors at the root.
        if (Directory.Exists(projectRoot))
            foreach (string file in Directory.EnumerateFiles(projectRoot)) AddFile(file, entries, seen, allowText: false);
        Entries = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        _names = Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        _paths = Entries.ToDictionary(e => Path.GetFullPath(e.FullPath), e => e, StringComparer.OrdinalIgnoreCase);
    }

    public static ResourceCatalog For(string projectRoot)
    {
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(projectRoot) ? "." : projectRoot);
        return Catalogs.GetOrAdd(root, r => new Lazy<ResourceCatalog>(() => new ResourceCatalog(r))).Value;
    }

    public static void Invalidate(string projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(projectRoot)) Catalogs.TryRemove(Path.GetFullPath(projectRoot), out _);
    }

    public static ResourceType TypeOf(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return ResourceType.Unknown;
        foreach (var definition in Definitions)
            if (filename.EndsWith(definition.Extension, StringComparison.OrdinalIgnoreCase)) return definition.Type;
        return ResourceType.Unknown;
    }

    /// <summary>Formats old storage references for display. Already-bare names, including dots, are preserved.</summary>
    public static string FormatName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string name = value.Trim().Replace('\\', '/');
        name = name.Substring(name.LastIndexOf('/') + 1);
        foreach (var definition in Definitions)
            if (name.EndsWith(definition.Extension, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - definition.Extension.Length);
        // Imported payload names may appear in source pickers, never compound descriptor suffixes.
        foreach (string suffix in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".tga", ".wav", ".ogg", ".mp3", ".flac", ".gmodel", ".glb", ".gltf", ".fbx", ".obj", ".blend", ".hlsl", ".json" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - suffix.Length);
        return name;
    }

    public static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A resource name is required.", nameof(name));
        string value = name.Trim();
        if (value.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0
            || value.Any(char.IsControl) || value.EndsWith(".", StringComparison.Ordinal)
            || value is "." or ".." || !string.Equals(value, FormatName(value), StringComparison.Ordinal)
            || Definitions.Where(d => d.Extension.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Any(d => value.EndsWith(d.Extension[..^5], StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use a resource name only, with no folder or file extension.", nameof(name));
        string stem = value.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("This name is reserved by the operating system.", nameof(name));
        return value;
    }

    /// <summary>Find a globally unique name. The optional type checks the result, not a separate namespace.</summary>
    public NamedResource Find(string reference, ResourceType expected = ResourceType.Unknown)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        string value = reference.Trim();
        if (_names.TryGetValue(value, out NamedResource[] named))
        {
            if (named.Length != 1) throw new InvalidDataException($"Resource name '{value}' is not unique. Rename the duplicate resources before running the project.");
            return expected == ResourceType.Unknown || named[0].Type == expected ? named[0] : null;
        }
        // Read compatibility for existing documents. New authoring only emits Name.
        if (!LooksLikeStorageReference(value)) return null;
        string path;
        try
        {
            value = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(ProjectRoot, value));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
        if (!IsInside(path, ProjectRoot) || HasLinkedParent(path, ProjectRoot)) return null;
        if (_paths.TryGetValue(path, out NamedResource found))
            return expected == ResourceType.Unknown || found.Type == expected ? found : null;
        // Old descriptors sometimes store just the physical filename. Never select the first of duplicates.
        if (value.IndexOf(Path.DirectorySeparatorChar) < 0)
        {
            NamedResource[] matches = Entries.Where(e => string.Equals(Path.GetFileName(e.FullPath), value, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1 && (expected == ResourceType.Unknown || matches[0].Type == expected)) return matches[0];
        }
        return null;
    }

    public static string Resolve(string projectRoot, string reference, ResourceType expected = ResourceType.Unknown) => For(projectRoot).Find(reference, expected)?.FullPath ?? string.Empty;
    public static string Name(string projectRoot, string reference, ResourceType expected = ResourceType.Unknown) => For(projectRoot).Find(reference, expected)?.Name ?? FormatName(reference);
    public static bool LooksLikeStorageReference(string value) => !string.IsNullOrWhiteSpace(value)
        && value.IndexOfAny(new[] { '\r', '\n', '\0', '"', '<', '>', '|', '*', '?' }) < 0
        && TypeOf(value) != ResourceType.Unknown;

    /// <summary>Storage boundary for payload readers. Resource inputs resolve first; raw payload paths remain private.</summary>
    public static string ResolveFile(string projectRoot, string reference, ResourceType expected = ResourceType.Unknown)
    {
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;
        string resource = Resolve(projectRoot, reference, expected);
        if (resource.Length > 0) return resource;
        // A wrong-type or unknown descriptor must not leak through as a raw file.
        if (TypeOf(reference) != ResourceType.Unknown || !LooksLikeStorageReference(reference) && !Path.HasExtension(reference)) return string.Empty;
        try
        {
            string path = Path.GetFullPath(Path.Combine(projectRoot, reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            return IsInside(path, projectRoot) && !HasLinkedParent(path, projectRoot) && File.Exists(path) ? path : string.Empty;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return string.Empty; }
    }

    public string UniqueName(string requested, string exceptPath = null)
    {
        string stem = ValidateName(requested);
        var used = new HashSet<string>(Entries.Where(e => !string.Equals(e.FullPath, exceptPath, StringComparison.OrdinalIgnoreCase)).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        string name = stem;
        for (int i = 2; used.Contains(name); i++) name = stem + " (" + i + ")";
        return name;
    }

    public static string FindProjectRoot(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return string.Empty;
        string current = Directory.Exists(file) ? Path.GetFullPath(file) : Path.GetDirectoryName(Path.GetFullPath(file));
        while (!string.IsNullOrEmpty(current))
        {
            if (string.Equals(Path.GetFileName(current), "Assets", StringComparison.OrdinalIgnoreCase)) return Path.GetDirectoryName(current) ?? current;
            if (Directory.Exists(current) && Directory.EnumerateFiles(current, "*.genesisproj", SearchOption.TopDirectoryOnly).Any()) return current;
            current = Path.GetDirectoryName(current);
        }
        return Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty;
    }

    private static void Scan(string directory, List<NamedResource> entries, HashSet<string> seen)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string file in Directory.EnumerateFiles(directory)) AddFile(file, entries, seen, allowText: true);
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith(".spritedata", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".modeldata", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".parts", StringComparison.OrdinalIgnoreCase)
                || File.Exists(child + ".object.json")) continue; // Private event programs belong to their Object.
            Scan(child, entries, seen);
        }
    }

    private static void AddFile(string file, List<NamedResource> entries, HashSet<string> seen, bool allowText)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return;
        ResourceType type = TypeOf(file);
        if (type == ResourceType.Unknown || (!allowText && (type is ResourceType.Script or ResourceType.Note))) return;
        string full = Path.GetFullPath(file);
        if (!seen.Add(full)) return;
        string name = FormatName(file);
        Guid assetId = Guid.Empty;
        if (File.Exists(file + ".meta"))
        {
            try
            {
                JObject metadata = JObject.Parse(File.ReadAllText(file + ".meta"));
                Guid.TryParse((string)metadata["guid"], out assetId);
                string authored = (string)metadata["resourceName"];
                if (!string.IsNullOrWhiteSpace(authored)) name = ValidateName(authored);
            }
            catch (Newtonsoft.Json.JsonException error) { throw new InvalidDataException("Invalid resource identity metadata for '" + name + "'.", error); }
        }
        entries.Add(new NamedResource(name, full, type, Definitions.First(d => file.EndsWith(d.Extension, StringComparison.OrdinalIgnoreCase)).Extension) { AssetId = assetId });
    }

    public static bool IsInside(string path, string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(path).StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasLinkedParent(string path, string root)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (string current = path; !string.IsNullOrEmpty(current) && !string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
