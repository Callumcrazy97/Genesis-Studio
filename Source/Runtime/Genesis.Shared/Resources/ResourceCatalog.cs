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

    /// <summary>
    /// Where an exported game keeps the identity of its resources (each .meta's guid and
    /// resourceName, with the .meta's size): the first launch of a fresh install reads this one
    /// file instead of opening every .meta. An antivirus scans each newly written file on its first
    /// open, about 4 ms a file on the PC this was measured on: a game with 4,145 resources spent 16 s
    /// on them before its window opened. A .meta of another size, or written after the index (a
    /// mod), is read as before.
    /// </summary>
    public const string IndexFile = ".genesis/resource-catalog.txt";
    private const string IndexHeader = "genesis-resource-catalog 1";

    private sealed record MetaRecord(long Length, Guid AssetId, string ResourceName);

    private sealed class ScanState
    {
        public string Root;
        public Dictionary<string, MetaRecord> Index;
        public DateTime IndexWritten;
        // The files the catalog lists, in the order it finds them, each with its .meta when it has one.
        public readonly List<(string File, ResourceType Type, string MetaPath, FileInfo Meta)> Files = new();
        public int IndexHits, MetaReads;
    }

    /// <summary>How the most recent catalog was built, for a log: how many identities came from the index, how many .meta files were read.</summary>
    public static string LastBuildReport { get; private set; } = string.Empty;

    private ResourceCatalog(string projectRoot) : this(projectRoot, useIndex: true, records: null) { }

    private ResourceCatalog(string projectRoot, bool useIndex, List<(string Relative, MetaRecord Record)> records)
    {
        ProjectRoot = projectRoot;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var state = new ScanState { Root = projectRoot };
        if (useIndex) ReadIndex(state);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string assets = Path.Combine(projectRoot, "Assets");
        if (Directory.Exists(assets)) Scan(assets, state, seen);
        foreach (string folder in LegacyRoots)
        {
            string path = Path.Combine(projectRoot, folder);
            if (Directory.Exists(path)) Scan(path, state, seen);
        }
        // Small standalone/editor preview projects can place their descriptors at the root.
        if (Directory.Exists(projectRoot))
            foreach (string file in Directory.EnumerateFiles(projectRoot)) AddFile(file, state, seen, allowText: false);
        List<NamedResource> entries = Identify(state, records);
        Entries = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
        _names = Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        _paths = Entries.ToDictionary(e => Path.GetFullPath(e.FullPath), e => e, StringComparer.OrdinalIgnoreCase);
        LastBuildReport = $"resource catalog: {Entries.Count} resources in {clock.ElapsedMilliseconds} ms ("
            + (state.Index != null ? $"{state.IndexHits} identities from {IndexFile}, " : string.Empty)
            + $"{state.MetaReads} .meta files read)";
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

    /// <summary>
    /// True when this project's catalog is already built and lists <paramref name="path"/>. The catalog
    /// depends only on resource file names and their .meta identity files, so rewriting the content
    /// of a listed resource leaves it valid and needs no synchronous rescan of the whole project.
    /// </summary>
    public static bool IsCatalogued(string projectRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(path)) return false;
        if (!Catalogs.TryGetValue(Path.GetFullPath(projectRoot), out Lazy<ResourceCatalog> lazy) || !lazy.IsValueCreated)
            return false;
        return lazy.Value._paths.ContainsKey(Path.GetFullPath(path));
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
            if (named.Length != 1) throw new InvalidDataException(DescribeDuplicate(named, ProjectRoot));
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

    /// <summary>Every name more than one resource uses, each with its resources (a Model and an Object called the same).</summary>
    public IReadOnlyList<NamedResource[]> DuplicateNames => _names.Values.Where(named => named.Length > 1).ToArray();

    /// <summary>Which resources share a name, by kind and path, and how to put it right.</summary>
    public static string DescribeDuplicate(IReadOnlyList<NamedResource> named, string projectRoot)
    {
        string root = string.IsNullOrEmpty(projectRoot) ? string.Empty : Path.GetFullPath(projectRoot);
        string Where(NamedResource resource) => root.Length > 0 && IsInside(resource.FullPath, root)
            ? Path.GetRelativePath(root, resource.FullPath)
            : resource.FullPath;
        string list = string.Join(", ", named.Select(resource => $"{resource.Type} ({Where(resource)})"));
        return $"Resource name '{named[0].Name}' is used by {named.Count} resources: {list}. Names are shared by every kind of resource, "
            + "so scripts and rooms can name them without saying the kind; rename one in Studio (Rename updates the references to it).";
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

    private static void Scan(string directory, ScanState state, HashSet<string> seen)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string file in Directory.EnumerateFiles(directory)) AddFile(file, state, seen, allowText: true);
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith(".spritedata", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".modeldata", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".parts", StringComparison.OrdinalIgnoreCase)
                || File.Exists(child + ".object.json")) continue; // Private event programs belong to their Object.
            Scan(child, state, seen);
        }
    }

    // Lists a resource file. Its name and guid are found afterwards (Identify), from its .meta.
    private static void AddFile(string file, ScanState state, HashSet<string> seen, bool allowText)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return;
        ResourceType type = TypeOf(file);
        if (type == ResourceType.Unknown || (!allowText && (type is ResourceType.Script or ResourceType.Note))) return;
        string full = Path.GetFullPath(file);
        if (!seen.Add(full)) return;
        string metaPath = file + ".meta";
        var meta = new FileInfo(metaPath);
        state.Files.Add((file, type, metaPath, meta.Exists ? meta : null));
    }

    /// <summary>
    /// Each listed file's name and guid, from its .meta: through the index when the .meta is the one
    /// the index describes. The .meta files still to be read are read on several threads (an
    /// antivirus scanning a newly written file holds its first open, and scans run side by side); a
    /// bad one is reported as it always was, the first in the listing's order.
    /// </summary>
    private static List<NamedResource> Identify(ScanState state, List<(string Relative, MetaRecord Record)> records)
    {
        int count = state.Files.Count;
        var identities = new (Guid AssetId, string ResourceName)[count];
        var failures = new Exception[count];
        var toRead = new List<int>();
        for (int i = 0; i < count; i++)
        {
            FileInfo meta = state.Files[i].Meta;
            if (meta == null) continue;
            if (state.Index != null
                && state.Index.TryGetValue(Path.GetRelativePath(state.Root, state.Files[i].File).Replace('\\', '/'), out MetaRecord known)
                && known.Length == meta.Length
                && meta.LastWriteTimeUtc <= state.IndexWritten.AddSeconds(2))
            {
                identities[i] = (known.AssetId, known.ResourceName);
                state.IndexHits++;
                continue;
            }
            toRead.Add(i);
        }

        state.MetaReads = toRead.Count;
        void Read(int i)
        {
            try
            {
                JObject metadata = JObject.Parse(File.ReadAllText(state.Files[i].MetaPath));
                Guid.TryParse((string)metadata["guid"], out Guid assetId);
                identities[i] = (assetId, (string)metadata["resourceName"]);
            }
            catch (Exception exception)
            {
                failures[i] = exception;
            }
        }
        if (toRead.Count < 64) foreach (int i in toRead) Read(i);
        else System.Threading.Tasks.Parallel.ForEach(toRead,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) }, Read);

        var entries = new List<NamedResource>(count);
        for (int i = 0; i < count; i++)
        {
            (string file, ResourceType type, _, FileInfo meta) = state.Files[i];
            string name = FormatName(file);
            Guid assetId = Guid.Empty;
            if (meta != null)
            {
                if (failures[i] is Newtonsoft.Json.JsonException error) throw new InvalidDataException("Invalid resource identity metadata for '" + name + "'.", error);
                if (failures[i] != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[i]).Throw();
                assetId = identities[i].AssetId;
                string authored = identities[i].ResourceName;
                if (!string.IsNullOrWhiteSpace(authored)) name = ValidateName(authored);
                records?.Add((Path.GetRelativePath(state.Root, file).Replace('\\', '/'), new MetaRecord(meta.Length, assetId, authored ?? string.Empty)));
            }
            entries.Add(new NamedResource(name, Path.GetFullPath(file), type, Definitions.First(d => file.EndsWith(d.Extension, StringComparison.OrdinalIgnoreCase)).Extension) { AssetId = assetId });
        }
        return entries;
    }

    private static void ReadIndex(ScanState state)
    {
        string path = Path.Combine(state.Root, IndexFile.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0] != IndexHeader) return;
            var index = new Dictionary<string, MetaRecord>(lines.Length, StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length != 4 || !Guid.TryParse(parts[2], out Guid assetId)
                    || !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long length)) continue;
                index[parts[0]] = new MetaRecord(length, assetId, parts[3]);
            }
            state.Index = index;
            state.IndexWritten = file.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without the index every .meta is read, as for a project in Studio.
        }
    }

    /// <summary>
    /// Writes <see cref="IndexFile"/> for a project as it is now: an export does, once its files are
    /// final. Returns how many resources' identities it holds.
    /// </summary>
    public static int WriteIndex(string projectRoot)
    {
        string root = Path.GetFullPath(projectRoot);
        var records = new List<(string Relative, MetaRecord Record)>();
        _ = new ResourceCatalog(root, useIndex: false, records);
        string path = Path.Combine(root, IndexFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var text = new System.Text.StringBuilder(IndexHeader).Append('\n');
        int written = 0;
        foreach ((string relative, MetaRecord record) in records)
        {
            // A path or name holding a tab or a line break cannot be written; that .meta is read instead.
            if (relative.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0 || record.ResourceName.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0) continue;
            text.Append(relative).Append('\t')
                .Append(record.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                .Append(record.AssetId.ToString("N")).Append('\t')
                .Append(record.ResourceName).Append('\n');
            written++;
        }
        File.WriteAllText(path, text.ToString(), new System.Text.UTF8Encoding(false));
        Invalidate(root);
        return written;
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
