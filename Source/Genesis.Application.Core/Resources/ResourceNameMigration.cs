using System.Text;
using Genesis.Application.Core.Projects;
using Genesis.Shared.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Core.Resources;

/// <summary>
/// Backup-first identity/reference upgrade. Storage filenames, sidecars and source pixels are
/// unchanged. Older same-stem, different-type resources receive distinct public names.
/// Every edit is prepared before writing; the transaction rolls back on an I/O failure.
/// </summary>
public static class ResourceNameMigration
{
    public const int Version = 1;

    public static bool Upgrade(ProjectSession project)
    {
        ResourceNames.Invalidate(project.RootPath);
        ResourceCatalog before = ResourceNames.For(project.RootPath);
        if (project.Manifest.ResourceNamesVersion >= Version && !before.Conflicts.Any()) return false;
        Dictionary<string, string> names = PlanNames(before.Entries);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byPath = before.Entries.ToDictionary(e => e.FullPath, StringComparer.OrdinalIgnoreCase);
        var report = new StringBuilder("Genesis resource-name migration\n\nOnly names are public references. Resource filenames and payloads remain private storage.\n\n");
        int renamed = 0;
        foreach (NamedResource entry in before.Entries)
        {
            string name = names[entry.FullPath];
            if (name != entry.Name) { report.Append(entry.Name).Append(" [").Append(entry.Type).Append("] -> ").AppendLine(name); renamed++; }
            string meta = entry.FullPath + ".meta";
            JObject data = File.Exists(meta) ? JObject.Parse(File.ReadAllText(meta)) : new JObject
            {
                ["schemaVersion"] = 1, ["guid"] = Guid.NewGuid().ToString("N"),
                ["kind"] = KindName(entry.Type), ["createdUtc"] = DateTime.UtcNow,
            };
            if ((string?)data["resourceName"] == name && (string?)data["displayName"] == name) continue;
            data["resourceName"] = name; data["displayName"] = name;
            data["modifiedUtc"] = DateTime.UtcNow;
            files[meta] = data.ToString(Formatting.Indented);
        }

        string Rewrite(string file, string text)
        {
            string Map(string value, ResourceType expected)
            {
                NamedResource? entry = null;
                if (ResourceNames.LooksLikeStorageReference(value))
                {
                    string relative = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    foreach (string parent in new[] { project.RootPath, Path.GetDirectoryName(file)! })
                    {
                        string full = Path.GetFullPath(Path.Combine(parent, relative));
                        if (byPath.TryGetValue(full, out NamedResource? exact) && (expected == ResourceType.Unknown || exact.Type == expected)) { entry = exact; break; }
                    }
                    if (entry is null)
                    {
                        NamedResource[] candidates = before.Entries.Where(e => string.Equals(Path.GetFileName(e.FullPath), value, StringComparison.OrdinalIgnoreCase)
                            && (expected == ResourceType.Unknown || e.Type == expected)).ToArray();
                        if (candidates.Length == 1) entry = candidates[0];
                    }
                }
                else
                {
                    NamedResource[] candidates = before.Entries.Where(e => (string.Equals(e.Name, value, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.StorageName, value, StringComparison.OrdinalIgnoreCase)) && (expected == ResourceType.Unknown || e.Type == expected)).ToArray();
                    if (candidates.Length == 1) entry = candidates[0];
                    else if (candidates.Length > 1 && expected != ResourceType.Unknown)
                        throw new InvalidDataException($"The reference '{value}' identifies more than one {expected} resource. Give these resources distinct names before migrating.");
                }
                return entry is null ? value : names[entry.FullPath];
            }
            return ResourceReferenceRewriter.RewriteDocument(file, text, Map);
        }

        var documents = new HashSet<string>(before.Entries.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (NamedResource entry in before.Entries.Where(e => e.Type == ResourceType.Object))
        {
            string? folder = Genesis.Runtime.Scripting.ObjectEventStore.FolderFor(entry.FullPath);
            if (folder is not null && Directory.Exists(folder))
                foreach (string script in Directory.EnumerateFiles(folder, "*.pgsl")) documents.Add(script);
        }
        // Model/terrain authoring sidecars can contain references too, but never touch bulk payload bytes.
        foreach (NamedResource entry in before.Entries)
            foreach (string associate in ResourceAssociates.Find(entry.FullPath))
                if (File.Exists(associate) && associate.EndsWith(".nature.json", StringComparison.OrdinalIgnoreCase)) documents.Add(associate);
        foreach (string file in documents)
        {
            string text = File.ReadAllText(file);
            string changed = Rewrite(file, text);
            if (text != changed) files[file] = changed;
        }
        string manifestText = System.Text.Json.JsonSerializer.Serialize(project.Manifest,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, WriteIndented = true });
        JObject manifest = JObject.Parse(Rewrite(project.ProjectFile, manifestText));
        manifest["resourceNamesVersion"] = Version;
        files[project.ProjectFile] = manifest.ToString(Formatting.Indented);
        report.AppendLine().Append("Disambiguated names: ").AppendLine(renamed.ToString())
            .Append("Changed text/metadata files: ").AppendLine(files.Count.ToString());
        ResourceReferenceOperations.Backup(project, files.Keys, "Migration", report.ToString());
        using (ResourceFileTransaction transaction = new())
        {
            foreach ((string file, string text) in files) transaction.WriteText(file, text);
            transaction.Commit();
        }
        project.Manifest.ResourceNamesVersion = Version;
        project.Manifest.StartRoom = (string?)manifest["startRoom"] ?? project.Manifest.StartRoom;
        project.Manifest.ProjectIcon = (string?)manifest["projectIcon"] ?? project.Manifest.ProjectIcon;
        ResourceNames.Invalidate(project.RootPath);
        if (ResourceNames.For(project.RootPath).Conflicts.Any()) throw new InvalidDataException("Resource-name migration left a duplicate name; restore the migration backup before continuing.");
        return true;
    }

    /// <summary>Deterministic without changing an existing unique name. Objects retain common gameplay names.</summary>
    public static Dictionary<string, string> PlanNames(IReadOnlyList<NamedResource> entries)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, NamedResource> group in entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            NamedResource[] ordered = group.OrderBy(e => e.Type == ResourceType.Object ? 0 : e.Type == ResourceType.Image ? 1 : 2)
                .ThenBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                NamedResource entry = ordered[i];
                string name = entry.Name;
                if (i > 0)
                {
                    string stem = name + (entry.Type == ResourceType.Image ? "Sprite" : entry.Type.ToString());
                    name = stem; int suffix = 2;
                    while (!reserved.Add(name)) name = stem + suffix++;
                }
                ResourceNames.ValidateName(name);
                result[entry.FullPath] = name;
            }
        }
        return result;
    }

    private static string KindName(ResourceType type) => type switch
    {
        ResourceType.Object => nameof(ResourceKind.GameObject), ResourceType.Script => nameof(ResourceKind.PgslScript),
        _ => type.ToString(),
    };
}
