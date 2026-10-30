using System.ComponentModel;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The resource commands: what the Assets dock does (folders, create, rename, move, copy,
/// duplicate, delete, import), the Finder, a project-wide text replace, reading a resource, and the
/// project's checks. Paths are project-relative, with forward slashes.
/// </summary>
public static class ResourceCommands
{
    public static IEnumerable<IAutomationCommand> All() =>
    [
        new ProjectInfo(), new ProjectCheck(), new ResourceTree(), new FolderCreate(), new ResourceCreate(),
        new ResourceRename(), new ResourceMove(), new ResourceCopy(), new ResourceDuplicate(), new ResourceDelete(),
        new ResourceImport(), new ResourceFind(), new ResourceRead(), new TextReplacePreview(), new TextReplace(),
    ];

    private static JsonObject Describe(AutomationContext context, ResourceItem item) => new()
    {
        ["name"] = item.Name,
        ["path"] = context.Relative(item.FullPath),
        ["kind"] = item.Kind.ToString(),
    };

    private static ResourceItem Existing(AutomationContext context, string path) =>
        context.Find(path) ?? throw new AutomationException($"There is no resource or folder at '{path}'.");

    private static string Folder(AutomationContext context, string? path)
    {
        string full = context.Resolve(string.IsNullOrWhiteSpace(path) ? "Assets" : path);
        if (!Directory.Exists(full)) throw new AutomationException($"There is no folder at '{path}'.");
        return full;
    }

    public sealed class ProjectInfo : AutomationCommand<NoInput>
    {
        public override string Name => "project.info";
        public override string Description => "The open project: its name, folder, and how many resources of each kind it has.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, NoInput input)
        {
            var counts = new JsonObject();
            foreach (var group in AutomationContext.Walk(context.Resources.BuildTree()).Where(i => !i.IsFolder).GroupBy(i => i.Kind).OrderBy(g => g.Key.ToString()))
                counts[group.Key.ToString()] = group.Count();
            return AutomationResult.Success("The project " + context.Project.Manifest.Name + ".", new JsonObject
            {
                ["name"] = context.Project.Manifest.Name,
                ["root"] = context.Root,
                ["resources"] = counts,
            });
        }
    }

    public sealed class ProjectCheck : AutomationCommand<NoInput>
    {
        public override string Name => "project.check";
        public override string Description => "Runs the project check and the strict script check Studio's Run makes, and lists what they find.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, NoInput input)
        {
            IReadOnlyList<AutomationIssue> issues = context.Check();
            int errors = issues.Count(i => i.Severity == "error");
            var result = AutomationResult.Success(errors == 0 ? $"No errors ({issues.Count} warnings)." : $"{errors} errors, {issues.Count - errors} warnings.");
            result.Issues.AddRange(issues);
            return result;
        }
    }

    public sealed class TreeInput
    {
        [Description("Folder to list, project-relative (default Assets).")] public string? Folder { get; set; }
        [Description("How many levels of folders to go into (default 3, 0 = only the folder itself).")] public int? Depth { get; set; }
    }

    public sealed class ResourceTree : AutomationCommand<TreeInput>
    {
        public override string Name => "resource.tree";
        public override string Description => "Lists the resources and folders under a folder, with their kinds.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, TreeInput input)
        {
            ResourceItem root = string.IsNullOrWhiteSpace(input.Folder) ? context.Resources.BuildTree() : Existing(context, input.Folder);
            return AutomationResult.Success("The tree under " + context.Relative(root.FullPath) + ".", Node(root, input.Depth ?? 3));

            JsonObject Node(ResourceItem item, int depth)
            {
                JsonObject node = Describe(context, item);
                if (item.IsFolder && depth > 0)
                    node["children"] = new JsonArray([.. item.Children.Select(child => (JsonNode?)Node(child, depth - 1))]);
                return node;
            }
        }
    }

    public sealed class FolderInput
    {
        [Description("Folder to make it in, project-relative (default Assets)."), RequiredInput] public string Parent { get; set; } = "Assets";
        [Description("The new folder's name."), RequiredInput] public string Name { get; set; } = string.Empty;
    }

    public sealed class FolderCreate : AutomationCommand<FolderInput>
    {
        public override string Name => "folder.create";
        public override string Description => "Makes a folder in the project's Assets. A folder that is already there is refused (not made again as \"Name (2)\"): use it.";

        protected override AutomationResult Execute(AutomationContext context, FolderInput input)
        {
            string path = context.CreateFolder(Folder(context, input.Parent), input.Name);
            return AutomationResult.Success("Made the folder " + context.Relative(path) + ".", new JsonObject { ["path"] = context.Relative(path) }, context.Relative(path));
        }
    }

    public sealed class CreateInput
    {
        [Description("What to make: Image, Audio, Shader, PgslScript, GameObject, Room, Model, Particle, Physics, Terrain, UserInterface, Note..."), RequiredInput]
        public ResourceKind Kind { get; set; }
        [Description("Folder to make it in, project-relative (default Assets).")] public string? Folder { get; set; }
        [Description("The new resource's name."), RequiredInput] public string Name { get; set; } = string.Empty;
        [Description(AutomationContext.ReplaceDescription)] public bool? Replace { get; set; }
    }

    public sealed class ResourceCreate : AutomationCommand<CreateInput>
    {
        public override string Name => "resource.create";
        public override string Description => "Makes a new resource of a kind, as the Assets dock's New does, with Studio's defaults. A name that is taken (names are shared by every kind) is refused, naming the resource that has it, unless replace is true.";

        protected override AutomationResult Execute(AutomationContext context, CreateInput input)
        {
            if (input.Kind is ResourceKind.Unknown or ResourceKind.Folder) throw new AutomationException("Use folder.create for folders, and name a resource kind.");
            string path = context.CreateResource(Folder(context, input.Folder), input.Kind, input.Name, input.Replace == true);
            return AutomationResult.Success($"Made the {input.Kind} {context.Relative(path)}.", new JsonObject { ["path"] = context.Relative(path) }, context.Relative(path));
        }
    }

    public sealed class RenameInput
    {
        [Description("The resource or folder, project-relative."), RequiredInput] public string Path { get; set; } = string.Empty;
        [Description("Its new name (without the extension)."), RequiredInput] public string NewName { get; set; } = string.Empty;
    }

    public sealed class ResourceRename : AutomationCommand<RenameInput>
    {
        public override string Name => "resource.rename";
        public override string Description => "Renames a resource or folder. A resource is known by its name: rooms, scripts and other resources that name it are updated, and so is an Object's own script binding (its ScriptClass). " +
            "Its file keeps the name it was made with (Assets/Objects/Hero.object.json stays), as Studio's Rename leaves it: commands that take a path still take that path, and a new resource cannot be made at that file. " +
            "Text that only mentions the old name (dialogue, comments, variable names) is not changed; text.replacePreview finds it. A folder is renamed itself.";

        protected override AutomationResult Execute(AutomationContext context, RenameInput input)
        {
            ResourceItem item = Existing(context, input.Path);
            string oldName = item.Name;
            string path = context.Resources.Rename(item.FullPath, input.NewName);
            // A resource is named in its metadata; its file may keep its own name.
            string newName = context.Find(context.Relative(path))?.Name ?? input.NewName;
            string message = $"Renamed {oldName} to {newName}.";
            if (!item.IsFolder && !ResourceAssociates.GetStem(path).Equals(newName, StringComparison.OrdinalIgnoreCase))
                message += $" Its file keeps the name {context.Relative(path)}: use that path in commands that take one, and the name everywhere else.";
            return AutomationResult.Success(message, new JsonObject { ["path"] = context.Relative(path), ["name"] = newName },
                [.. new[] { input.Path, context.Relative(path) }.Distinct(StringComparer.OrdinalIgnoreCase)]);
        }
    }

    public sealed class MoveInput
    {
        [Description("The resource or folder, project-relative."), RequiredInput] public string Path { get; set; } = string.Empty;
        [Description("The folder to put it in, project-relative."), RequiredInput] public string Folder { get; set; } = string.Empty;
    }

    public sealed class ResourceMove : AutomationCommand<MoveInput>
    {
        public override string Name => "resource.move";
        public override string Description => "Moves a resource or folder into another folder.";

        protected override AutomationResult Execute(AutomationContext context, MoveInput input)
        {
            ResourceItem item = Existing(context, input.Path);
            string path = context.Resources.Move(item.FullPath, Folder(context, input.Folder));
            return AutomationResult.Success("Moved to " + context.Relative(path) + ".", new JsonObject { ["path"] = context.Relative(path) }, input.Path, context.Relative(path));
        }
    }

    public sealed class ResourceCopy : AutomationCommand<MoveInput>
    {
        public override string Name => "resource.copy";
        public override string Description => "Copies a resource or folder into another folder (the copy gets a new identity). A copy is the one command that takes the next free name, \"Name (2)\", when its name is taken; the result gives the path it got.";

        protected override AutomationResult Execute(AutomationContext context, MoveInput input)
        {
            ResourceItem item = Existing(context, input.Path);
            string path = context.Resources.Copy(item.FullPath, Folder(context, input.Folder));
            return AutomationResult.Success("Copied to " + context.Relative(path) + ".", new JsonObject { ["path"] = context.Relative(path) }, context.Relative(path));
        }
    }

    public sealed class PathInput
    {
        [Description("The resource or folder, project-relative."), RequiredInput] public string Path { get; set; } = string.Empty;
    }

    public sealed class ResourceDuplicate : AutomationCommand<PathInput>
    {
        public override string Name => "resource.duplicate";
        public override string Description => "Duplicates a resource beside itself, as the Assets dock's Duplicate does: the copy is named \"Name (2)\" (the next free number), and the result gives the path it got.";

        protected override AutomationResult Execute(AutomationContext context, PathInput input)
        {
            ResourceItem item = Existing(context, input.Path);
            string path = context.Resources.Duplicate(item.FullPath);
            return AutomationResult.Success("Duplicated as " + context.Relative(path) + ".", new JsonObject { ["path"] = context.Relative(path) }, context.Relative(path));
        }
    }

    public sealed class ResourceDelete : AutomationCommand<PathInput>
    {
        public override string Name => "resource.delete";
        public override string Description => "Moves a resource or folder to the project's trash (it can be brought back from there).";
        public override bool Destructive => true;

        protected override AutomationResult Execute(AutomationContext context, PathInput input)
        {
            ResourceItem item = Existing(context, input.Path);
            if (item.IsProtectedRoot) throw new AutomationException($"'{input.Path}' is one of the project's own folders and cannot be removed.");
            string trash = context.Resources.MoveToTrash(item.FullPath);
            return AutomationResult.Success($"Moved {input.Path} to the trash.", new JsonObject { ["trash"] = trash }, input.Path);
        }
    }

    public sealed class ImportInput
    {
        [Description("Files to bring in: full paths anywhere on the machine (images, sounds, models...)."), RequiredInput] public string[] Files { get; set; } = [];
        [Description("Folder to put them in, project-relative (default Assets).")] public string? Folder { get; set; }
        [Description("Each file makes a resource named after the file. " + AutomationContext.ReplaceDescription)] public bool? Replace { get; set; }
    }

    public sealed class ResourceImport : AutomationCommand<ImportInput>
    {
        public override string Name => "resource.import";
        public override string Description => "Imports files from anywhere as resources, as dropping them on the Assets dock does (each becomes the right kind). " +
            "A file whose resource name is taken is refused, with nothing imported, unless replace is true.";

        protected override AutomationResult Execute(AutomationContext context, ImportInput input)
        {
            string[] missing = input.Files.Where(file => !File.Exists(file)).ToArray();
            if (missing.Length > 0) throw new AutomationException("These files do not exist: " + string.Join(", ", missing));
            IReadOnlyList<string> made = context.ImportFiles(Folder(context, input.Folder), input.Files, input.Replace == true);
            string[] relative = [.. made.Select(context.Relative)];
            return AutomationResult.Success($"Imported {made.Count} resources.", new JsonArray([.. relative.Select(p => (JsonNode?)p)]), relative);
        }
    }

    public sealed class FindInput
    {
        [Description("What to look for: part of a name, a path, or (with contents) text inside resources."), RequiredInput] public string Term { get; set; } = string.Empty;
        [Description("Folder to look in, project-relative (default Assets).")] public string? Folder { get; set; }
        [Description("Also look inside scripts and resource files (default false).")] public bool Contents { get; set; }
        [Description("Only these kinds (default all).")] public ResourceKind[]? Kinds { get; set; }
        [Description("Most results (default 100).")] public int? Max { get; set; }
    }

    public sealed class ResourceFind : AutomationCommand<FindInput>
    {
        public override string Name => "resource.find";
        public override string Description => "Finds resources by name, path or (with contents) the text inside them, as Studio's Finder does.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, FindInput input)
        {
            ResourceItem tree = context.Resources.BuildTree();
            var response = new ResourceSearchService().Search(tree, new ResourceSearchQuery
            {
                Term = input.Term,
                ScopePath = Folder(context, input.Folder),
                SearchContents = input.Contents,
                Kinds = input.Kinds ?? [],
                MaximumResults = Math.Clamp(input.Max ?? 100, 1, 2000),
            });
            var results = new JsonArray();
            foreach (ResourceSearchResult hit in response.Results)
            {
                JsonObject row = Describe(context, hit.Resource);
                row["match"] = hit.MatchLabel;
                if (hit.LineNumber is int line) row["line"] = line;
                if (!string.IsNullOrWhiteSpace(hit.Preview)) row["preview"] = hit.Preview;
                results.Add((JsonNode?)row);
            }
            return AutomationResult.Success($"{response.Results.Count} found{(response.Truncated ? " (more not shown)" : string.Empty)}.", results);
        }
    }

    public sealed class ReadInput
    {
        [Description("The resource file, project-relative (a .pgsl script, a .json resource, a .md note...)."), RequiredInput] public string Path { get; set; } = string.Empty;
        [Description("Most characters to return (default 20000).")] public int? MaxCharacters { get; set; }
    }

    public sealed class ResourceRead : AutomationCommand<ReadInput>
    {
        public override string Name => "resource.read";
        public override string Description => "Returns the text of a resource file: a script, a resource's JSON, a note.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, ReadInput input)
        {
            string full = context.Resolve(input.Path);
            if (!File.Exists(full)) throw new AutomationException($"There is no file at '{input.Path}'.");
            string text = File.ReadAllText(full);
            int max = Math.Clamp(input.MaxCharacters ?? 20000, 100, 2_000_000);
            bool cut = text.Length > max;
            return AutomationResult.Success(cut ? $"The first {max} of {text.Length} characters." : $"{text.Length} characters.",
                new JsonObject { ["path"] = input.Path, ["text"] = cut ? text[..max] : text, ["truncated"] = cut });
        }
    }

    public sealed class ReplaceInput
    {
        [Description("The text to find (exact)."), RequiredInput] public string Find { get; set; } = string.Empty;
        [Description("What to put in its place.")] public string Replace { get; set; } = string.Empty;
        [Description("Folder to change, project-relative (default Assets).")] public string? Folder { get; set; }
        [Description("File types to change (default .pgsl and .json).")] public string[]? Extensions { get; set; }
        [Description("Match upper and lower case exactly (default true).")] public bool? MatchCase { get; set; }
    }

    private static IEnumerable<(string File, string Text, int Count)> Matches(AutomationContext context, ReplaceInput input)
    {
        if (input.Find.Length == 0) throw new AutomationException("Say what to find.");
        StringComparison comparison = input.MatchCase is false ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var extensions = new HashSet<string>(input.Extensions is { Length: > 0 } given ? given.Select(e => e.StartsWith('.') ? e : "." + e) : [".pgsl", ".json"], StringComparer.OrdinalIgnoreCase);
        string root = Folder(context, input.Folder);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(file))) continue;
            if (file.Contains(Path.DirectorySeparatorChar + ".genesis" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(file).Length > ResourceSearchService.MaximumContentFileBytes) continue;
            string text = File.ReadAllText(file);
            int count = 0;
            for (int at = text.IndexOf(input.Find, comparison); at >= 0; at = text.IndexOf(input.Find, at + input.Find.Length, comparison)) count++;
            if (count > 0) yield return (file, text, count);
        }
    }

    public sealed class TextReplacePreview : AutomationCommand<ReplaceInput>
    {
        public override string Name => "text.replacePreview";
        public override string Description => "Shows what text.replace would change: each file, how many times, and the lines before and after. Changes nothing.";
        public override bool ChangesProject => false;

        protected override AutomationResult Execute(AutomationContext context, ReplaceInput input)
        {
            StringComparison comparison = input.MatchCase is false ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var files = new JsonArray();
            int total = 0;
            foreach ((string file, string text, int count) in Matches(context, input).Take(500))
            {
                total += count;
                var lines = new JsonArray();
                string[] split = text.Split('\n');
                for (int i = 0; i < split.Length && lines.Count < 5; i++)
                {
                    if (split[i].IndexOf(input.Find, comparison) < 0) continue;
                    lines.Add((JsonNode?)new JsonObject
                    {
                        ["line"] = i + 1,
                        ["before"] = split[i].TrimEnd('\r'),
                        ["after"] = Replace(split[i], input.Find, input.Replace, comparison).TrimEnd('\r'),
                    });
                }
                files.Add((JsonNode?)new JsonObject { ["path"] = context.Relative(file), ["count"] = count, ["lines"] = lines });
            }
            return AutomationResult.Success($"{total} matches in {files.Count} files.", files);
        }
    }

    public sealed class TextReplace : AutomationCommand<ReplaceInput>
    {
        public override string Name => "text.replace";
        public override string Description => "Replaces text in every matching file under a folder (scripts and resource JSON by default), backing each file up first. Call text.replacePreview first.";
        public override bool Destructive => true;

        protected override AutomationResult Execute(AutomationContext context, ReplaceInput input)
        {
            StringComparison comparison = input.MatchCase is false ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var changed = new List<string>();
            int total = 0;
            foreach ((string file, string text, int count) in Matches(context, input).ToList())
            {
                ResourceBackupService.BackupBeforeOverwrite(file);
                File.WriteAllText(file, Replace(text, input.Find, input.Replace, comparison));
                changed.Add(context.Relative(file));
                total += count;
            }
            return AutomationResult.Success($"Replaced {total} matches in {changed.Count} files.", null, [.. changed]);
        }
    }

    private static string Replace(string text, string find, string replacement, StringComparison comparison) =>
        comparison == StringComparison.Ordinal ? text.Replace(find, replacement, StringComparison.Ordinal) : text.Replace(find, replacement, StringComparison.OrdinalIgnoreCase);
}
