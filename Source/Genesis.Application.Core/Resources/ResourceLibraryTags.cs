using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Genesis.Application.Core.Resources;

/// <summary>An optimistic edit token for one resource's library metadata, not its document.</summary>
public sealed record ResourceLibraryTagSnapshot(
    string AssetsRoot, string ResourcePath, Guid AssetId, IReadOnlyList<string> Tags, string Revision);

/// <summary>
/// Library classification lives in the existing identity sidecar. It never edits animation tags,
/// object gameplay tags, resource names, or source pixels. Writes preserve all other JSON fields.
/// </summary>
public static class ResourceLibraryTags
{
    public const int MaximumTags = 32;
    public const int MaximumTagLength = 48;
    public const int MaximumMetadataBytes = 256 * 1024;
    private const int MaximumCachedResources = 4096;
    private sealed record CachedTags(long Ticks, long Length, IReadOnlyList<string> Tags);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CachedTags> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ParseInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 4096) throw new ArgumentException("Library tags are limited to 4,096 input characters.", nameof(text));
        return Normalize(text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    public static IReadOnlyList<string> Normalize(IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (string value in tags)
        {
            if (++count > 1024) throw new ArgumentException("Too many library tag values.", nameof(tags));
            if (value is null) throw new ArgumentException("A library tag cannot be null.", nameof(tags));
            string tag = string.Join(" ", value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Normalize(NormalizationForm.FormC);
            if (tag.Length == 0) continue;
            if (tag.Length > MaximumTagLength || value.Any(char.IsControl)
                || tag.IndexOfAny([',', ';', '"', '\\']) >= 0)
                throw new ArgumentException($"Each library tag must be at most {MaximumTagLength} characters, without quotes, commas, semicolons, backslashes or control characters.", nameof(tags));
            unique.Add(tag);
            if (unique.Count > MaximumTags)
                throw new ArgumentException($"A resource can have at most {MaximumTags} library tags.", nameof(tags));
        }
        return Array.AsReadOnly(unique.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>Bounded, timestamp-cached metadata only. A broken sidecar does not break browsing.</summary>
    public static IReadOnlyList<string> ReadOrEmpty(string resourcePath)
    {
        try
        {
            string path = Path.GetFullPath(resourcePath) + ".meta";
            FileInfo info = new(path);
            if (!info.Exists || info.Length > MaximumMetadataBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) return [];
            long ticks = info.LastWriteTimeUtc.Ticks, length = info.Length;
            lock (CacheGate)
                if (Cache.TryGetValue(path, out CachedTags? cached) && cached.Ticks == ticks && cached.Length == length)
                    return cached.Tags;
            JsonObject root = ReadObject(ReadBounded(path));
            IReadOnlyList<string> tags = ReadTags(root);
            lock (CacheGate)
            {
                if (Cache.Count >= MaximumCachedResources && !Cache.ContainsKey(path)) Cache.Clear();
                Cache[path] = new(ticks, length, tags);
            }
            return tags;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        { return []; }
    }

    public static void Invalidate(string resourcePath)
    {
        lock (CacheGate) Cache.Remove(Path.GetFullPath(resourcePath) + ".meta");
    }

    public static ResourceLibraryTagSnapshot Capture(string assetsRoot, string resourcePath, Guid expectedId = default)
    {
        string root = Path.GetFullPath(assetsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string path = Path.GetFullPath(resourcePath);
        RequireResource(root, path);
        byte[] bytes = ReadBounded(path + ".meta");
        JsonObject metadata = ReadObject(bytes);
        Guid id = ReadIdentity(metadata);
        if (expectedId != Guid.Empty && id != expectedId)
            throw new InvalidOperationException("The selected resource was replaced. Select it again before editing its library tags.");
        return new(root, path, id, ReadTags(metadata), Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>Checks identity and the complete metadata revision before an atomic same-folder replacement.</summary>
    public static ResourceLibraryTagSnapshot Apply(ResourceLibraryTagSnapshot expected, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(expected);
        IReadOnlyList<string> normalized = Normalize(tags);
        ResourceLibraryTagSnapshot current = Capture(expected.AssetsRoot, expected.ResourcePath, expected.AssetId);
        if (!string.Equals(current.Revision, expected.Revision, StringComparison.Ordinal))
            throw new InvalidOperationException("Resource metadata changed while library tags were being edited. Reopen the tag editor to keep those changes.");
        if (Equal(current.Tags, normalized)) return current;

        string metadataPath = current.ResourcePath + ".meta";
        byte[] original = ReadBounded(metadataPath);
        if (Convert.ToHexString(SHA256.HashData(original)) != current.Revision)
            throw new InvalidOperationException("Resource metadata changed. Reopen the library tag editor.");
        JsonObject metadata = ReadObject(original);
        JsonArray values = new();
        foreach (string tag in normalized) values.Add(tag);
        metadata["libraryTags"] = values;
        metadata["modifiedUtc"] = DateTime.UtcNow;
        byte[] next = Encoding.UTF8.GetBytes(metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (next.Length > MaximumMetadataBytes) throw new InvalidDataException("The resource metadata is too large to edit safely.");

        // Short temporary filename: imported resources can already approach Windows path limits.
        string temporary = Path.Combine(Path.GetDirectoryName(metadataPath)!, ".tags-" + Guid.NewGuid().ToString("N")[..12] + ".tmp");
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(next); output.Flush(flushToDisk: true); }
            RequireResource(current.AssetsRoot, current.ResourcePath);
            if (Convert.ToHexString(SHA256.HashData(ReadBounded(metadataPath))) != current.Revision)
                throw new InvalidOperationException("Resource metadata changed before tags could be saved. Nothing was replaced.");
            File.Replace(temporary, metadataPath, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        Invalidate(current.ResourcePath);
        return new(current.AssetsRoot, current.ResourcePath, current.AssetId, normalized,
            Convert.ToHexString(SHA256.HashData(next)));
    }

    public static bool Equal(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
        first.SequenceEqual(second, StringComparer.OrdinalIgnoreCase);

    private static byte[] ReadBounded(string path)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > MaximumMetadataBytes) throw new InvalidDataException("Resource metadata exceeds the library-tag safety limit.");
        using MemoryStream bytes = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length + read > MaximumMetadataBytes) throw new InvalidDataException("Resource metadata changed size during reading.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private static JsonObject ReadObject(byte[] bytes)
    {
        // Existing project metadata can include a UTF-8 BOM from an external editor.
        ReadOnlySpan<byte> json = bytes;
        if (json.Length >= 3 && json[0] == 0xef && json[1] == 0xbb && json[2] == 0xbf) json = json[3..];
        JsonObject root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Resource metadata must be an object.");
        if (root["schemaVersion"] is JsonNode version && version.GetValue<int>() > AssetMetadata.CurrentSchemaVersion)
            throw new InvalidDataException("This metadata was written by a newer Studio version. Library tags were not changed.");
        return root;
    }

    private static Guid ReadIdentity(JsonObject root)
    {
        if (root["guid"] is not JsonValue value || !value.TryGetValue(out string? text)
            || !Guid.TryParse(text, out Guid id) || id == Guid.Empty)
            throw new InvalidDataException("The resource has no valid metadata identity. Refresh the Assets browser before editing tags.");
        return id;
    }

    private static IReadOnlyList<string> ReadTags(JsonObject root)
    {
        if (root["libraryTags"] is null) return [];
        if (root["libraryTags"] is not JsonArray array) throw new InvalidDataException("Library tags must be a list of text values.");
        List<string> tags = [];
        foreach (JsonNode? node in array)
        {
            if (node is not JsonValue value || !value.TryGetValue(out string? text) || text is null)
                throw new InvalidDataException("A library tag is not text.");
            tags.Add(text);
        }
        return Normalize(tags);
    }

    private static void RequireResource(string root, string path)
    {
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path) || Directory.Exists(path))
            throw new UnauthorizedAccessException("Library tags can only be edited for an existing resource in this project's Assets folder.");
        string? cursor = Path.GetDirectoryName(path);
        while (cursor is not null && cursor.Length >= root.Length)
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Library tag writes cannot follow linked resource folders.");
            if (string.Equals(cursor, root, StringComparison.OrdinalIgnoreCase)) break;
            cursor = Path.GetDirectoryName(cursor);
        }
        foreach (string file in new[] { path, path + ".meta" })
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Library tag writes cannot follow linked resource files.");
    }
}
