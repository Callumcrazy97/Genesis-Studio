using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Genesis.Shared.Assets;

/// <summary>
/// An exported game's index of its resources' identities, so the first launch of a fresh install
/// does not open every resource's .meta.
/// </summary>
/// <remarks>
/// An antivirus scans each newly written file on its first open, about 4 ms a file on the PC this
/// was measured on: a game with 4,145 resources spent 16 s on their .meta files before its window
/// opened, every time it was freshly exported or installed. The export writes
/// <see cref="IndexFile"/> (each .meta's guid and resourceName, with its size) once its files are
/// final, and the catalog takes a resource's identity from it while the .meta is the one the index
/// describes: the same size and not written after the index. Any other .meta (a mod, a project
/// edited after an export) is read as before, and a project without the file (Studio) reads them all.
/// </remarks>
public sealed partial class ResourceCatalog
{
    /// <summary>Where an exported game keeps the identities of its resources, relative to its folder.</summary>
    public const string IndexFile = ".genesis/resource-catalog.txt";
    private const string IndexHeader = "genesis-resource-catalog 1";

    private sealed record IndexedMeta(long Length, DateTime IndexWrittenUtc, Guid AssetId, string ResourceName);

    // Resource file (full path) -> what the index says about its .meta. Entries are checked against
    // the .meta each time they are used, so one left from an older index is never trusted.
    private static readonly ConcurrentDictionary<string, IndexedMeta> IndexedMetas = new(StringComparer.OrdinalIgnoreCase);
    // Project root -> the time of the index file last read for it.
    private static readonly ConcurrentDictionary<string, DateTime> IndexesRead = new(StringComparer.OrdinalIgnoreCase);

    // A catalog is built on one thread, start to end, so its counts are kept per thread.
    [ThreadStatic] private static bool _ignoreIndex;
    [ThreadStatic] private static int _indexHits;
    [ThreadStatic] private static int _metaReads;
    [ThreadStatic] private static bool _indexFound;
    [ThreadStatic] private static long _buildStarted;

    /// <summary>How the most recent catalog was built, for a log: identities taken from the index, .meta files read.</summary>
    public static string LastBuildReport { get; private set; } = string.Empty;

    private static void BeginIndexedBuild(string projectRoot)
    {
        _indexHits = 0;
        _metaReads = 0;
        _buildStarted = Stopwatch.GetTimestamp();
        _indexFound = !_ignoreIndex && ReadIndex(projectRoot);
    }

    private static void EndIndexedBuild(int resources)
    {
        LastBuildReport = $"resource catalog: {resources} resources in {Stopwatch.GetElapsedTime(_buildStarted).TotalMilliseconds:F0} ms ("
            + (_indexFound ? $"{_indexHits} identities from {IndexFile}, " : string.Empty)
            + $"{_metaReads} .meta files read)";
    }

    private static void CountMetaRead() => _metaReads++;

    private static bool TryIndexedIdentity(string fullPath, out Guid assetId, out string resourceName)
    {
        assetId = Guid.Empty;
        resourceName = null;
        if (!_indexFound || _ignoreIndex || !IndexedMetas.TryGetValue(fullPath, out IndexedMeta known)) return false;
        var meta = new FileInfo(fullPath + ".meta");
        // Two seconds for file systems that keep times coarsely (FAT keeps two-second steps).
        if (!meta.Exists || meta.Length != known.Length || meta.LastWriteTimeUtc > known.IndexWrittenUtc.AddSeconds(2)) return false;
        assetId = known.AssetId;
        resourceName = known.ResourceName;
        _indexHits++;
        return true;
    }

    // Reads the project's index when it has one not read already. True when the project has one.
    private static bool ReadIndex(string projectRoot)
    {
        string root = CatalogKey(projectRoot);
        string path = Path.Combine(root, IndexFile.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return false;
            DateTime written = file.LastWriteTimeUtc;
            if (IndexesRead.TryGetValue(root, out DateTime read) && read == written) return true;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            // What an earlier index of this project said no longer counts.
            string inside = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string key in IndexedMetas.Keys)
                if (key.StartsWith(inside, StringComparison.OrdinalIgnoreCase)) IndexedMetas.TryRemove(key, out _);
            IndexesRead.TryRemove(root, out _);
            if (lines.Length == 0 || lines[0] != IndexHeader) return false;
            for (int i = 1; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split('\t');
                if (parts.Length != 4 || !Guid.TryParse(parts[2], out Guid assetId)
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long length)) continue;
                string resource;
                try { resource = Path.GetFullPath(Path.Combine(root, parts[0].Replace('/', Path.DirectorySeparatorChar))); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                // Only files inside the game.
                if (!IsInside(resource, root)) continue;
                IndexedMetas[resource] = new IndexedMeta(length, written, assetId, parts[3]);
            }

            IndexesRead[root] = written;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without the index every .meta is read, as for a project in Studio.
            return false;
        }
    }

    /// <summary>
    /// Writes <see cref="IndexFile"/> for a project as its files are now (an export does, once they
    /// are final). Returns how many resources' identities it holds. A .meta that cannot be read
    /// throws, as the catalog does.
    /// </summary>
    public static int WriteIndex(string projectRoot)
    {
        string root = CatalogKey(projectRoot);
        ResourceCatalog catalog;
        bool ignored = _ignoreIndex;
        _ignoreIndex = true;
        try { catalog = new ResourceCatalog(root); }
        finally { _ignoreIndex = ignored; }

        var text = new StringBuilder(IndexHeader).Append('\n');
        int written = 0;
        foreach (NamedResource entry in catalog.Entries)
        {
            var meta = new FileInfo(entry.FullPath + ".meta");
            if (!meta.Exists) continue;
            string relative = Path.GetRelativePath(root, entry.FullPath).Replace('\\', '/');
            // The name the .meta gave, or none when the resource goes by its file's name.
            string authored = string.Equals(entry.Name, FormatName(entry.FullPath), StringComparison.Ordinal) ? string.Empty : entry.Name;
            // A path or name holding a tab or a line break cannot be written; that .meta is read instead.
            if (relative.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0 || authored.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0) continue;
            text.Append(relative).Append('\t')
                .Append(meta.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(entry.AssetId.ToString("N")).Append('\t')
                .Append(authored).Append('\n');
            written++;
        }

        string path = Path.Combine(root, IndexFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        IndexesRead.TryRemove(root, out _);
        Invalidate(root);
        return written;
    }
}
