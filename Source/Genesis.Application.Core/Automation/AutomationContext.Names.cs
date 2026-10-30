using System.Text.Json.Nodes;
using Genesis.Application.Core.Resources;
using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Automation;

/// <summary>An existing resource a command moved to the project's trash because the caller said to replace it.</summary>
public sealed record ReplacedResource(string Name, string Kind, string File, string Trash)
{
    public JsonObject ToJson() => new() { ["name"] = Name, ["kind"] = Kind, ["path"] = File, ["trash"] = Trash };
}

/// <summary>
/// Making things under the name asked for. A resource name is shared by every kind of resource, and
/// the Assets dock's New quietly makes "Name (2)" when it is taken; a caller that asked for "Hero" and
/// was given "Hero (2)" goes on to use the old Hero. A command that makes a resource asks here instead:
/// a taken name is refused, naming the resource that has it, unless the caller said <c>replace</c>,
/// which moves that resource (of the same kind) to the project's trash first, so it can be brought back,
/// and makes the new one in its place.
/// </summary>
public sealed partial class AutomationContext
{
    /// <summary>What every command's <c>replace</c> input says.</summary>
    public const string ReplaceDescription =
        "What to do when a resource of this name exists: false (default) refuses and names it; true moves that resource (it must be the same kind) to the project's trash first and makes this one in its place.";

    private readonly List<ReplacedResource> _replaced = [];

    /// <summary>What the command being run has moved to the trash to make room for its own resource.</summary>
    public IReadOnlyList<ReplacedResource> Replaced => _replaced;

    internal void ForgetReplaced() => _replaced.Clear();

    /// <summary>
    /// Makes a resource as the Assets dock's New does, but under exactly the name asked for: when the
    /// name or the file is taken it throws (naming what holds it) instead of making "Name (2)". With
    /// <paramref name="replace"/> a resource of the same kind that has the name goes to the trash first.
    /// <paramref name="offerReplace"/> is false for a command that has no replace to offer.
    /// </summary>
    public string CreateResource(string folder, ResourceKind kind, string name, bool replace = false, bool offerReplace = true)
    {
        (NamedResource? old, string file) = Plan(folder, kind, name, replace, offerReplace);
        if (old is not null) SendToTrash(old);
        string made = Resources.CreateResource(folder, kind, name);
        if (!SamePath(made, file))
        {
            // The check above rules this out; if the dock ever picks another file, say so, as a
            // different resource under a different name is exactly what this exists to prevent.
            Resources.MoveToTrash(made);
            throw new AutomationException($"The {ResourceDefinitions.Get(kind).DisplayName} could not be made as '{name}'; nothing was left behind.");
        }
        return made;
    }

    /// <summary>
    /// Throws when <see cref="CreateResource"/> would refuse, and changes nothing: for a command with
    /// work to do before it makes its resource (a terrain's heights, a sound's samples).
    /// </summary>
    public void RequireNewResource(string folder, ResourceKind kind, string name, bool replace = false, bool offerReplace = true) =>
        Plan(folder, kind, name, replace, offerReplace);

    /// <summary>
    /// Imports files as the Assets dock's drop does, with the same rule: a file whose resource name is
    /// taken is refused (the whole import, before any file comes in) unless <paramref name="replace"/>.
    /// </summary>
    public IReadOnlyList<string> ImportFiles(string folder, IReadOnlyList<string> files, bool replace = false)
    {
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var replacing = new List<NamedResource>();
        foreach (string file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;
            ResourceKind kind = ResourceService.ImportKind(file);
            string name = ResourceDefinitions.FromPath(file) is { } definition
                ? Path.GetFileName(file)[..^definition.Extension.Length]
                : Path.GetFileNameWithoutExtension(file);
            if (!claimed.TryAdd(name, file))
                throw new AutomationException($"'{Path.GetFileName(claimed[name])}' and '{Path.GetFileName(file)}' would both make a resource named '{name}': import them one at a time, or give one of the files another name.");
            try
            {
                if (Plan(folder, kind, name, replace, offerReplace: true).Existing is { } old) replacing.Add(old);
            }
            catch (ArgumentException)
            {
                // A file name that cannot be a resource name fails in the import itself, as it always has.
            }
        }
        foreach (NamedResource old in replacing) SendToTrash(old);
        return Resources.ImportFiles(folder, files);
    }

    /// <summary>Makes a folder; one that is already there is refused, not made again as "Name (2)".</summary>
    public string CreateFolder(string parentFolder, string name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length > 0 && trimmed is not ("." or "..") && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
        {
            string target = Path.Combine(parentFolder, trimmed);
            if (Directory.Exists(target))
                throw new AutomationException($"The folder {Relative(target)} already exists. Put things in it, or choose another name.");
            if (File.Exists(target))
                throw new AutomationException($"{Relative(target)} is a file, so a folder cannot have that name. Choose another name.");
        }
        return Resources.CreateFolder(parentFolder, name!);
    }

    /// <summary>What would be in the way of a new resource: the one to replace (when allowed) and the file it will have; or an explanation.</summary>
    private (NamedResource? Existing, string File) Plan(string folder, ResourceKind kind, string name, bool replace, bool offerReplace)
    {
        string stem = ResourceNames.ValidateName(name);
        ResourceDefinition definition = ResourceDefinitions.Get(kind);
        string parent = ResourceFolderPolicy.Destination(Project, Path.GetFullPath(folder), kind);
        string file = Path.Combine(parent, stem + definition.Extension);

        ResourceNames.Invalidate(Root);
        ResourceCatalog catalog = ResourceNames.For(Root);
        NamedResource[] named = [.. catalog.Entries.Where(entry => string.Equals(entry.Name, stem, StringComparison.OrdinalIgnoreCase))];
        if (named.Length > 1) throw new AutomationException(ResourceCatalog.DescribeDuplicate(named, Root));
        NamedResource? existing = named.Length == 1 ? named[0] : null;

        if (existing is not null)
        {
            string where = Relative(existing.FullPath);
            ResourceKind? existingKind = ResourceDefinitions.FromPath(existing.FullPath)?.Kind;
            string existingDisplay = existingKind is ResourceKind found ? ResourceDefinitions.Get(found).DisplayName : existing.Type.ToString();
            if (existingKind != kind)
                throw new AutomationException($"The name '{stem}' is already used by the {existingDisplay} {where}. Names are shared by every kind of resource, so a new {definition.DisplayName} cannot have it: choose another name.");
            if (!replace)
                throw new AutomationException($"The {existingDisplay} '{stem}' already exists ({where}), so a new one is not made under that name." + (offerReplace
                    ? " Choose another name, or call again with \"replace\": true to move the old one to the project's trash and make this one in its place."
                    : " Choose another name."));
        }

        // A renamed resource keeps its file name, so a free name can still have its file taken.
        if ((File.Exists(file) || Directory.Exists(file)) && (existing is null || !SamePath(existing.FullPath, file)))
        {
            NamedResource? holder = catalog.Entries.FirstOrDefault(entry => SamePath(entry.FullPath, file));
            string holds = holder is null
                ? $"The file {Relative(file)} already exists"
                : $"The file {Relative(file)} is the file of the {ResourceDefinitions.FromPath(holder.FullPath)?.DisplayName ?? holder.Type.ToString()} '{holder.Name}' (a resource keeps its file name when it is renamed)";
            throw new AutomationException($"{holds}, so a new {definition.DisplayName} named '{stem}' cannot be made there. Choose another name or folder.");
        }
        return (existing, file);
    }

    private void SendToTrash(NamedResource old)
    {
        string kind = ResourceDefinitions.FromPath(old.FullPath)?.DisplayName ?? old.Type.ToString();
        string file = Relative(old.FullPath);
        string trash = Resources.MoveToTrash(old.FullPath);
        _replaced.Add(new ReplacedResource(old.Name, kind, file, Relative(trash)));
    }

    /// <summary>A sentence saying what was replaced, for the result of the command that did it.</summary>
    internal string DescribeReplaced() =>
        string.Join(" ", _replaced.Select(r => $"Replaced the {r.Kind} '{r.Name}' ({r.File}); the old one is in the project's trash at {r.Trash}."));

    private static bool SamePath(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
}
