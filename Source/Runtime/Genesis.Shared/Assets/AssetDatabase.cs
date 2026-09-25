using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Shared.Assets;

/// <summary>
/// GUID compatibility view over the same global resource-name catalog used by Studio and Engine.
/// Names are case-insensitively unique across kinds; filesystem paths remain internal identity data.
/// </summary>
public sealed class AssetDatabase
{
    private readonly string _projectPath;
    private readonly object _lock = new();

    private Dictionary<Guid, AssetEntry> _byGuid;
    private Dictionary<(AssetKind, string), AssetEntry> _byName;   // case-insensitive name via comparer
    private Dictionary<string, AssetEntry> _byPath;                 // normalized relative path
    private bool _scanned;

    /// <summary>Raised whenever an entry is added, removed, or has its GUID/metadata changed. (Editor refreshes UI on this.)</summary>
    public event Action<Guid> EntryChanged;

    public AssetDatabase(string projectPath)
    {
        _projectPath = projectPath ?? string.Empty;
        _byName = new Dictionary<(AssetKind, string), AssetEntry>(new NameKeyComparer());
    }

    /// <summary>Absolute project root this database covers.</summary>
    public string ProjectPath => _projectPath;

    // ── Indexes (lazy-scanned) ──────────────────────────────────────────────────

    public IReadOnlyDictionary<Guid, AssetEntry> ByGuid
    {
        get { EnsureScanned(); lock (_lock) return _byGuid; }
    }

    public IReadOnlyDictionary<(AssetKind Kind, string Name), AssetEntry> ByName
    {
        get { EnsureScanned(); lock (_lock) return _byName; }
    }

    public IReadOnlyDictionary<string, AssetEntry> ByPath
    {
        get { EnsureScanned(); lock (_lock) return _byPath; }
    }

    // ── Resolve ──────────────────────────────────────────────────────────────────

    /// <summary>Resolve an <see cref="AssetRef"/> to its entry, or null if the GUID is unknown.</summary>
    public AssetEntry Resolve(AssetRef reference)
    {
        if (reference.IsEmpty) return null;
        EnsureScanned();
        lock (_lock)
        {
            if (_byGuid.TryGetValue(reference.Guid, out var e)) return e;
            // Backward-compat fallback: ref only carries a name + kind.
            if (!string.IsNullOrEmpty(reference.Name) && reference.Kind != AssetKind.Unknown
                && _byName.TryGetValue((reference.Kind, reference.Name), out e))
                return e;
            return null;
        }
    }

    /// <summary>Resolve by raw GUID hex string.</summary>
    public AssetEntry ResolveGuid(string guidHex)
        => Guid.TryParse(guidHex, out Guid g) && g != Guid.Empty ? Resolve(AssetRef.Of(g)) : null;

    /// <summary>Resolve by (kind, name). Used during migration from name-string references and for user-typed names.</summary>
    public AssetEntry ResolveName(AssetKind kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        EnsureScanned();
        if (kind is AssetKind.Background or AssetKind.Texture or AssetKind.Material) kind = AssetKind.Sprite;
        lock (_lock) return kind == AssetKind.Unknown ? _byGuid.Values.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            : _byName.TryGetValue((kind, name), out var entry) ? entry : null;
    }

    /// <summary>Resolve a public resource name without requiring a type-specific namespace.</summary>
    public AssetEntry ResolveName(string name) => ResolveName(AssetKind.Unknown, name);

    /// <summary>Private storage compatibility for editor file events and old content.</summary>
    public AssetEntry ResolvePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;
        EnsureScanned();
        string norm = NormalizeRelative(relativePath);
        lock (_lock) return _byPath.TryGetValue(norm, out var e) ? e : null;
    }

    /// <summary>Enumerate entries of a given kind (or all kinds when <see cref="AssetKind.Unknown"/>).</summary>
    public IEnumerable<AssetEntry> Enumerate(AssetKind kind = AssetKind.Unknown)
    {
        EnsureScanned();
        if (kind is AssetKind.Background or AssetKind.Texture or AssetKind.Material) kind = AssetKind.Sprite;
        lock (_lock)
        {
            return kind == AssetKind.Unknown
                ? _byGuid.Values.ToList()
                : _byGuid.Values.Where(e => e.Kind == kind).ToList();
        }
    }

    /// <summary>True if the project has been scanned at least once.</summary>
    public bool IsScanned => Volatile.Read(ref _scanned);

    // ── Mutation / invalidation ──────────────────────────────────────────────────

    /// <summary>Drop all caches; the next access rescans from disk.</summary>
    public void Invalidate()
    {
        ResourceCatalog.Invalidate(_projectPath);
        lock (_lock)
        {
            _byGuid = null;
            _byName = new Dictionary<(AssetKind, string), AssetEntry>(new NameKeyComparer());
            _byPath = null;
            Volatile.Write(ref _scanned, false);
        }
    }

    /// <summary>Force a rescan now and return the fresh entry count.</summary>
    public int Refresh()
    {
        Invalidate();
        EnsureScanned();
        lock (_lock) return _byGuid.Count;
    }

    // ── Scan ────────────────────────────────────────────────────────────────────

    private void EnsureScanned()
    {
        if (Volatile.Read(ref _scanned)) return;
        lock (_lock)
        {
            if (Volatile.Read(ref _scanned)) return;
            ScanInternal();
            Volatile.Write(ref _scanned, true);
        }
    }

    private void ScanInternal()
    {
        var byGuid = new Dictionary<Guid, AssetEntry>();
        var byName = new Dictionary<(AssetKind, string), AssetEntry>(new NameKeyComparer());
        var byPath = new Dictionary<string, AssetEntry>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_projectPath) && Directory.Exists(_projectPath))
        {
            ResourceCatalog catalog = ResourceCatalog.For(_projectPath);
            if (catalog.Conflicts.Any())
                throw new InvalidDataException("Resource names must be unique across all types in the project.");
            foreach (NamedResource resource in catalog.Entries)
            {
                string meta = resource.FullPath + ".meta";
                Guid guid = AssetMetaFile.ReadGuid(meta);
                if (guid == Guid.Empty)
                {
                    // Read-only builds still have stable identities without modifying game content.
                    byte[] hash = System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(resource.Name.ToUpperInvariant()));
                    guid = new Guid(hash.AsSpan(0, 16));
                }
                var entry = new AssetEntry
                {
                    Guid = guid, Name = resource.Name,
                    Kind = resource.Type == ResourceType.Image ? AssetKind.Sprite
                        : Enum.TryParse(resource.Type.ToString(), out AssetKind kind) ? kind : AssetKind.Unknown,
                    RelativePath = ToRelative(resource.FullPath), AbsolutePath = resource.FullPath,
                    Folder = Path.GetDirectoryName(ToRelative(resource.FullPath)) ?? string.Empty,
                    MetaPath = meta,
                };
                Index(byGuid, byName, byPath, entry);
            }
        }
        _byGuid = byGuid;
        _byName = byName;
        _byPath = byPath;
    }

    private static void Index(
        Dictionary<Guid, AssetEntry> byGuid,
        Dictionary<(AssetKind, string), AssetEntry> byName,
        Dictionary<string, AssetEntry> byPath,
        AssetEntry entry)
    {
        if (!byGuid.TryAdd(entry.Guid, entry))
            throw new InvalidDataException($"Duplicate resource identity for '{entry.Name}'. Reimport the duplicate with a new identity.");

        var nameKey = (entry.Kind, entry.Name);
        if (!byName.TryAdd(nameKey, entry))
            throw new InvalidDataException($"Duplicate resource name '{entry.Name}'.");

        if (!string.IsNullOrEmpty(entry.RelativePath))
        {
            string norm = NormalizeRelative(entry.RelativePath);
            if (!byPath.ContainsKey(norm))
                byPath[norm] = entry;
        }
    }



    private string ComputeFolder(string kindRoot, string metaPath)
    {
        string dir = Path.GetDirectoryName(metaPath);
        if (string.IsNullOrEmpty(dir)) return null;
        string rel = Path.GetRelativePath(kindRoot, dir);
        return rel == "." ? null : rel.Replace('\\', '/');
    }

    private string ToRelative(string absolute)
    {
        if (string.IsNullOrEmpty(absolute)) return null;
        string rel = Path.GetRelativePath(_projectPath, absolute);
        return rel.Replace('\\', '/');
    }

    private static string NormalizeRelative(string relative)
        => relative.Replace('\\', '/').TrimStart('.', '/');

    // ── game.assets.json (production writer) ─────────────────────────────────────
    // Fills the documented gap: until now the project index was emitted only by
    // literal-string scaffolders. The database now owns a GUID-aware writer.

    /// <summary>Write <c>game.assets.json</c> at the project root from the scanned entries.</summary>
    public void WriteGameAssetsJson()
    {
        EnsureScanned();
        if (string.IsNullOrEmpty(_projectPath)) return;

        var assets = new List<JObject>();
        lock (_lock)
        {
            foreach (var e in _byGuid.Values.OrderBy(a => a.Kind.ToString()).ThenBy(a => a.Name))
            {
                assets.Add(new JObject(
                    new JProperty("name", e.Name),
                    new JProperty("type", AssetKindNames.TypeLabel(e.Kind) ?? e.Kind.ToString()),
                    new JProperty("guid", e.Guid.ToString(AssetRef.GuidFormat)),
                    new JProperty("path", e.RelativePath)
                ));
            }
        }

        var doc = new JObject(
            new JProperty("version", 3),
            new JProperty("assets", new JArray(assets))
        );

        string outPath = Path.Combine(_projectPath, "game.assets.json");
        File.WriteAllText(outPath, doc.ToString(Formatting.Indented));
    }

    // ── Notifications ──────────────────────────────────────────────────────────

    /// <summary>Notify that a single entry changed (called by editors after save/rename). Rescans lazily.</summary>
    public void NotifyChanged(Guid guid)
    {
        Invalidate();
        EntryChanged?.Invoke(guid);
    }

    // ── Name-key comparer (case-insensitive on the name half) ───────────────────

    private sealed class NameKeyComparer : IEqualityComparer<(AssetKind Kind, string Name)>
    {
        public bool Equals((AssetKind Kind, string Name) x, (AssetKind Kind, string Name) y)
            => x.Kind == y.Kind
               && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((AssetKind Kind, string Name) obj)
            => HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name ?? string.Empty));
    }
}
