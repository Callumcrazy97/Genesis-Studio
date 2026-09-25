using System.Text.Json;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Settings;
using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Projects;

/// <summary>
/// Project-owned Texture Groups. Installation preferences may seed defaults for new projects;
/// the live list travels with the <see cref="ProjectManifest"/> so Player and Studio agree.
/// </summary>
public static class TextureGroupCatalog
{
    public const string DefaultName = TextureGroupDefaults.DefaultName;
    public const int DefaultAtlasSize = TextureGroupDefaults.DefaultAtlasSize;
    public const int LargeAtlasSize = TextureGroupDefaults.LargeAtlasSize;

    public static string NormalizeOrDefault(string? name) =>
        TextureGroupDefaults.NormalizeOrDefault(name);

    public static int NormalizeAtlasSize(int size) =>
        TextureGroupDefaults.NormalizeAtlasSize(size);

    /// <summary>
    /// Guarantees exactly one Default group exists, named <see cref="DefaultName"/>, and that every
    /// other entry has a unique non-empty name and a legal atlas size.
    /// </summary>
    public static List<TextureGroupDefinition> Ensure(IList<TextureGroupDefinition>? groups, int defaultAtlasSize = DefaultAtlasSize)
    {
        List<TextureGroupDefinition> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int atlas = NormalizeAtlasSize(defaultAtlasSize);

        if (groups is not null)
        {
            foreach (TextureGroupDefinition group in groups)
            {
                string name = NormalizeOrDefault(group.Name);
                if (string.Equals(name, DefaultName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seen.Add(name))
                    continue;
                result.Add(new TextureGroupDefinition
                {
                    Name = name,
                    AtlasSize = NormalizeAtlasSize(group.AtlasSize),
                    IsDefault = false,
                });
            }
        }

        result.Insert(0, new TextureGroupDefinition
        {
            Name = DefaultName,
            AtlasSize = atlas,
            IsDefault = true,
        });
        return result;
    }

    public static void ApplyToManifest(ProjectManifest manifest, int defaultAtlasSize = DefaultAtlasSize)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Runtime ??= new ProjectRuntimeSettings();
        manifest.Runtime.TextureGroups = Ensure(manifest.Runtime.TextureGroups, defaultAtlasSize);
        manifest.Runtime.DefaultTextureGroupName = DefaultName;
        manifest.Runtime.DefaultTextureGroupSize = NormalizeAtlasSize(
            manifest.Runtime.DefaultTextureGroupSize > 0
                ? manifest.Runtime.DefaultTextureGroupSize
                : defaultAtlasSize);
    }

    public static IReadOnlyList<string> Names(ProjectManifest manifest)
    {
        ApplyToManifest(manifest);
        return manifest.Runtime.TextureGroups.Select(g => g.Name).ToArray();
    }

    public static bool TryGet(ProjectManifest manifest, string name, out TextureGroupDefinition group)
    {
        ApplyToManifest(manifest);
        string key = NormalizeOrDefault(name);
        foreach (TextureGroupDefinition candidate in manifest.Runtime.TextureGroups)
        {
            if (string.Equals(candidate.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                group = candidate;
                return true;
            }
        }

        group = manifest.Runtime.TextureGroups[0];
        return false;
    }

    public static bool Contains(ProjectManifest manifest, string name) =>
        TryGet(manifest, name, out _);

    public static bool TryAdd(ProjectManifest manifest, string name, int atlasSize, out string error)
    {
        ApplyToManifest(manifest);
        string trimmed = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            error = "Texture Group name cannot be empty.";
            return false;
        }

        if (string.Equals(trimmed, DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{DefaultName}' already exists and cannot be duplicated.";
            return false;
        }

        if (Contains(manifest, trimmed))
        {
            error = $"Texture Group '{trimmed}' already exists.";
            return false;
        }

        manifest.Runtime.TextureGroups.Add(new TextureGroupDefinition
        {
            Name = trimmed,
            AtlasSize = NormalizeAtlasSize(atlasSize),
            IsDefault = false,
        });
        error = string.Empty;
        return true;
    }

    public static bool IsDefault(string? name) =>
        TextureGroupDefaults.IsDefault(name);

    /// <summary>
    /// Removes a custom group. Member images are reassigned to Default. The Default group cannot
    /// be deleted.
    /// </summary>
    public static TextureGroupDeleteResult TryDelete(
        ProjectSession session,
        ProjectService projects,
        string groupName)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(projects);
        ApplyToManifest(session.Manifest);

        string name = NormalizeOrDefault(groupName);
        if (IsDefault(name))
        {
            return new TextureGroupDeleteResult(false, 0, $"Cannot delete '{DefaultName}'.");
        }

        int removed = session.Manifest.Runtime.TextureGroups.RemoveAll(g =>
            string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase) && !g.IsDefault);
        if (removed == 0)
        {
            return new TextureGroupDeleteResult(false, 0, $"Texture Group '{name}' was not found.");
        }

        int reassigned = ReassignImages(session.AssetsPath, name, DefaultName);
        projects.Save(session);
        return new TextureGroupDeleteResult(true, reassigned, string.Empty);
    }

    /// <summary>
    /// Ensures the manifest has a Default group, then writes <see cref="DefaultName"/> into every
    /// <c>.image.json</c> whose TextureGroup is missing or blank.
    /// </summary>
    public static TextureGroupBackfillResult EnsureAndBackfill(
        ProjectSession session,
        ProjectService projects,
        int defaultAtlasSize = DefaultAtlasSize)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(projects);

        string beforeFingerprint = Fingerprint(session.Manifest.Runtime?.TextureGroups);
        ApplyToManifest(session.Manifest, defaultAtlasSize);
        ProjectRuntimeSettings runtime = session.Manifest.Runtime
            ?? throw new InvalidOperationException("ApplyToManifest did not materialise Runtime.");
        if (!string.Equals(beforeFingerprint, Fingerprint(runtime.TextureGroups), StringComparison.Ordinal))
            projects.Save(session);

        int scanned = 0;
        int updated = 0;
        if (!Directory.Exists(session.AssetsPath))
        {
            return new TextureGroupBackfillResult(scanned, updated, runtime.TextureGroups.Count);
        }

        foreach (string path in Directory.EnumerateFiles(
                     session.AssetsPath, "*.image.json", SearchOption.AllDirectories))
        {
            scanned++;
            try
            {
                if (TryBackfillImageFile(path, session.Manifest))
                    updated++;
            }
            catch
            {
                // Skip corrupt descriptors; Validate reports them separately.
            }
        }

        return new TextureGroupBackfillResult(scanned, updated, runtime.TextureGroups.Count);
    }

    private static bool TryBackfillImageFile(string path, ProjectManifest manifest)
    {
        string assigned = ResolveAssignedGroup(manifest, null);
        string raw = File.ReadAllText(path);
        bool hasProperty =
            raw.Contains("\"TextureGroup\"", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("\"textureGroup\"", StringComparison.OrdinalIgnoreCase);

        try
        {
            ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(path);
            ImageDocument document = loaded.Document;
            assigned = ResolveAssignedGroup(manifest, document.TextureGroup);
            bool groupChanged = !string.Equals(
                document.TextureGroup, assigned, StringComparison.Ordinal);
            if (!groupChanged && hasProperty)
                return false;

            document.TextureGroup = assigned;
            ImageDocumentSerializer.SaveAtomic(path, document);
            return true;
        }
        catch (Exception)
        {
            if (hasProperty)
                return false;

            // Descriptors that fail full validation still need the group string on disk so
            // Player and Studio agree; inject without re-validating the whole document.
            InjectTextureGroupProperty(path, assigned);
            return true;
        }
    }

    private static void InjectTextureGroupProperty(string path, string textureGroup)
    {
        using JsonDocument source = JsonDocument.Parse(File.ReadAllText(path));
        if (source.RootElement.ValueKind != JsonValueKind.Object)
            return;

        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                bool wroteGroup = false;
                foreach (JsonProperty property in source.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "textureGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteString("textureGroup", textureGroup);
                        wroteGroup = true;
                        continue;
                    }

                    property.WriteTo(writer);
                }

                if (!wroteGroup)
                    writer.WriteString("textureGroup", textureGroup);
                writer.WriteEndObject();
            }

            File.Copy(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string Fingerprint(IList<TextureGroupDefinition>? groups)
    {
        if (groups is null || groups.Count == 0)
            return string.Empty;
        return string.Join('|', groups.Select(g =>
            $"{NormalizeOrDefault(g.Name)}:{NormalizeAtlasSize(g.AtlasSize)}:{(g.IsDefault ? 1 : 0)}"));
    }

    public static string ResolveAssignedGroup(ProjectManifest manifest, string? textureGroup)
    {
        ApplyToManifest(manifest);
        string name = NormalizeOrDefault(textureGroup);
        return Contains(manifest, name) ? name : DefaultName;
    }

    private static int ReassignImages(string assetsPath, string fromGroup, string toGroup)
    {
        if (!Directory.Exists(assetsPath))
            return 0;

        int count = 0;
        foreach (string path in Directory.EnumerateFiles(
                     assetsPath, "*.image.json", SearchOption.AllDirectories))
        {
            try
            {
                ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(path);
                if (!string.Equals(
                        NormalizeOrDefault(loaded.Document.TextureGroup),
                        fromGroup,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                loaded.Document.TextureGroup = toGroup;
                ImageDocumentSerializer.SaveAtomic(path, loaded.Document);
                count++;
            }
            catch
            {
                // Skip corrupt descriptors.
            }
        }

        return count;
    }
}

public sealed record TextureGroupBackfillResult(int Scanned, int Updated, int GroupCount);

public sealed record TextureGroupDeleteResult(bool Succeeded, int ImagesReassigned, string Error);
