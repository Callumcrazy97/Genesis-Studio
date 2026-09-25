using System.Text;
using System.Security.Cryptography;
using Genesis.Application.Core.Projects;
using Genesis.Shared.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Core.Resources;

/// <summary>Transactional public-identity operations. A rename never relocates private pixels or event programs.</summary>
public static class ResourceReferenceOperations
{
    public static void Rename(ProjectSession project, string resourceFile, string requestedName)
    {
        string name = ResourceNames.ValidateName(requestedName);
        ResourceNames.Invalidate(project.RootPath);
        ResourceCatalog catalog = ResourceNames.For(project.RootPath);
        NamedResource resource = catalog.Find(resourceFile)
            ?? throw new FileNotFoundException("The selected resource no longer exists.", resourceFile);
        if (catalog.Entries.Any(e => !string.Equals(e.FullPath, resource.FullPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A resource named '{name}' already exists. Names are unique across every resource type.");
        if (string.Equals(resource.Name, name, StringComparison.Ordinal)) return;

        var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string document in Documents(project.RootPath, catalog).Append(project.ProjectFile))
        {
            if (!File.Exists(document)) continue;
            string before = File.ReadAllText(document);
            string Map(string value, ResourceType expected)
            {
                NamedResource? entry = catalog.Find(value, expected);
                if (entry is null && ResourceNames.LooksLikeStorageReference(value))
                {
                    string local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!, value.Replace('/', Path.DirectorySeparatorChar)));
                    if (ResourceNames.IsInside(local, project.RootPath)) entry = catalog.Find(local, expected);
                }
                return entry is null ? value : string.Equals(entry.FullPath, resource.FullPath, StringComparison.OrdinalIgnoreCase) ? name : entry.Name;
            }
            string after = ResourceReferenceRewriter.RewriteDocument(document, before, Map);
            // This is an identity label, not a child-node label or dialogue string.
            if (document.Equals(resource.FullPath, StringComparison.OrdinalIgnoreCase)
                && ResourceNames.TypeOf(document) is not (ResourceType.Script or ResourceType.Note))
            {
                JObject json = JObject.Parse(after);
                if ((string?)json["name"] == resource.Name) { json["name"] = name; after = json.ToString(Formatting.Indented); }
            }
            if (after != before) writes[document] = after;
        }
        string metadata = resource.FullPath + ".meta";
        JObject identity = File.Exists(metadata) ? JObject.Parse(File.ReadAllText(metadata)) : new JObject
        {
            ["guid"] = Guid.NewGuid().ToString("N"), ["kind"] = resource.Type.ToString(), ["createdUtc"] = DateTime.UtcNow,
        };
        identity["resourceName"] = name; identity["displayName"] = name; identity["modifiedUtc"] = DateTime.UtcNow;
        writes[metadata] = identity.ToString(Formatting.Indented);
        Backup(project, writes.Keys, "Rename", $"{resource.Name} -> {name}");
        using (var transaction = new ResourceFileTransaction())
        {
            foreach ((string path, string text) in writes) transaction.WriteText(path, text);
            transaction.Commit();
        }
        if (writes.TryGetValue(project.ProjectFile, out string? manifestText))
        {
            JObject manifest = JObject.Parse(manifestText);
            project.Manifest.StartRoom = (string?)manifest["startRoom"] ?? project.Manifest.StartRoom;
            project.Manifest.ProjectIcon = (string?)manifest["projectIcon"] ?? project.Manifest.ProjectIcon;
        }
        ResourceNames.Invalidate(project.RootPath);
    }

    internal static void RebindCopies(ProjectSession project, ResourceCatalog before, string source, string destination, ResourceFileTransaction transaction)
    {
        ResourceNames.Invalidate(project.RootPath);
        ResourceCatalog after = ResourceNames.For(project.RootPath);
        bool folder = Directory.Exists(source);
        var copies = new Dictionary<string, NamedResource>(StringComparer.OrdinalIgnoreCase);
        foreach (NamedResource original in before.Entries.Where(entry => folder
                     ? ResourceNames.IsInside(entry.FullPath, source)
                     : string.Equals(entry.FullPath, source, StringComparison.OrdinalIgnoreCase)))
        {
            string copiedFile = folder ? Path.Combine(destination, Path.GetRelativePath(source, original.FullPath)) : destination;
            if (after.Find(copiedFile) is { } copy) copies[original.FullPath] = copy;
        }
        var documents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string original, NamedResource copy) in copies)
        {
            documents[copy.FullPath] = original;
            if (copy.Type == ResourceType.Object)
            {
                string events = Genesis.Runtime.Scripting.ObjectEventStore.FolderFor(copy.FullPath);
                string oldEvents = Genesis.Runtime.Scripting.ObjectEventStore.FolderFor(original);
                if (Directory.Exists(events))
                    foreach (string program in Directory.EnumerateFiles(events, "*.pgsl"))
                        documents[program] = Path.Combine(oldEvents, Path.GetFileName(program));
            }
        }
        foreach ((string document, string originalDocument) in documents)
        {
            string text = File.ReadAllText(document);
            string Map(string value, ResourceType type)
            {
                NamedResource? original = before.Find(value, type);
                if (original is null && ResourceNames.LooksLikeStorageReference(value))
                {
                    string local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(originalDocument)!, value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                    if (ResourceNames.IsInside(local, project.RootPath)) original = before.Find(local, type);
                }
                return original is null ? value : copies.TryGetValue(original.FullPath, out NamedResource? copy) ? copy.Name : original.Name;
            }
            string normalized = ResourceReferenceRewriter.RewriteDocument(document, text, Map);
            if (normalized != text) transaction.WriteText(document, normalized);
        }
        ResourceNames.Invalidate(project.RootPath);
    }

    internal static IEnumerable<string> Documents(string root, ResourceCatalog catalog)
    {
        var paths = new HashSet<string>(catalog.Entries.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (NamedResource resource in catalog.Entries)
        {
            if (resource.Type == ResourceType.Object)
            {
                string folder = Genesis.Runtime.Scripting.ObjectEventStore.FolderFor(resource.FullPath);
                if (Directory.Exists(folder))
                    foreach (string script in Directory.EnumerateFiles(folder, "*.pgsl")) paths.Add(script);
            }
            if (File.Exists(resource.FullPath + ".nature.json")) paths.Add(resource.FullPath + ".nature.json");
        }
        return paths;
    }

    /// <summary>Short hashed backup paths avoid repeating long resource paths under another deep prefix.</summary>
    internal static string Backup(ProjectSession project, IEnumerable<string> paths, string operation, string report)
    {
        string backup = Path.Combine(project.InternalPath, "Backups", "ResourceNames", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(backup, "Files"));
        var entries = new JArray();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!ResourceNames.IsInside(path, project.RootPath)) throw new UnauthorizedAccessException("The resource edit leaves the project.");
            string relative = Path.GetRelativePath(project.RootPath, path).Replace('\\', '/');
            bool existed = File.Exists(path);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)))[..24] + ".bak";
            if (existed) File.Copy(path, Path.Combine(backup, "Files", key), overwrite: false);
            entries.Add(new JObject { ["path"] = relative, ["existed"] = existed, ["backup"] = existed ? "Files/" + key : null });
        }
        File.WriteAllText(Path.Combine(backup, "Index.json"), new JObject { ["operation"] = operation, ["files"] = entries }.ToString(Formatting.Indented));
        File.WriteAllText(Path.Combine(backup, "Report.txt"), report);
        return backup;
    }
}
