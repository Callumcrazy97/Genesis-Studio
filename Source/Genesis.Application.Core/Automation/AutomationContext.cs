using System.Text.RegularExpressions;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Automation;

/// <summary>
/// The project a run of commands works on, and the services they share: its resources, the checks
/// run after each change, and the audit log every change is written to
/// (<c>.genesis/Automation/audit.log</c>). Paths given to commands are relative to the project
/// (<c>Assets/Objects/Hero.object.json</c>) and never leave it.
/// </summary>
public sealed partial class AutomationContext
{
    private AutomationContext(ProjectSession project)
    {
        Project = project;
        Resources = new ResourceService(project);
    }

    public ProjectSession Project { get; }

    public ResourceService Resources { get; }

    public string Root => Project.RootPath;

    public string AuditLogPath => Path.Combine(Project.InternalPath, "Automation", "audit.log");

    /// <summary>Who is asking, written to the audit log ("nova", "mcp", "cli", "test").</summary>
    public string Caller { get; set; } = "automation";

    public static AutomationContext Open(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return new AutomationContext(new ProjectService().OpenProject(Path.GetFullPath(projectRoot)));
    }

    public static AutomationContext For(ProjectSession project) => new(project ?? throw new ArgumentNullException(nameof(project)));

    /// <summary>A project-relative path made full; throws when it would leave the project.</summary>
    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Root;
        string trimmed = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(Root, trimmed));
        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!(full + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new AutomationException($"'{relativePath}' is outside the project.");
        return full;
    }

    /// <summary>A full path as the project-relative path commands use, with forward slashes.</summary>
    public string Relative(string fullPath) =>
        Path.GetRelativePath(Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The resource tree's node for a path, or null.</summary>
    public ResourceItem? Find(string relativePath)
    {
        string full = Resolve(relativePath);
        return Walk(Resources.BuildTree()).FirstOrDefault(item => string.Equals(Path.GetFullPath(item.FullPath), full, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<ResourceItem> Walk(ResourceItem root)
    {
        yield return root;
        foreach (ResourceItem child in root.Children)
            foreach (ResourceItem item in Walk(child))
                yield return item;
    }

    /// <summary>
    /// The project check and the strict script check Studio's Run makes, as issues. Run after every
    /// change, so a command's caller hears at once what it broke.
    /// </summary>
    public IReadOnlyList<AutomationIssue> Check()
    {
        var issues = new List<AutomationIssue>();
        foreach (ProjectValidationIssue issue in new ProjectService().Validate(Project))
        {
            issues.Add(new AutomationIssue(issue.Severity.ToString().ToLowerInvariant(), "project", issue.Message,
                issue.Path is null ? null : Relative(Path.IsPathRooted(issue.Path) ? issue.Path : Path.Combine(Root, issue.Path))));
        }
        var scripts = Genesis.Runtime.Scripting.PgslScriptValidator.ValidateProject(Root, strict: true);
        foreach (string error in scripts.Errors) issues.Add(ScriptIssue("error", error));
        foreach (string warning in scripts.Warnings) issues.Add(ScriptIssue("warning", warning));
        return issues;
    }

    // The checker's two forms: "Assets\Objects\Guide\Step.pgsl (line 17, column 1): message" and
    // "Assets\Scripts\Lib.pgsl: parse error - Unexpected token ';' at line 2:13."
    private AutomationIssue ScriptIssue(string severity, string text)
    {
        Match path = ScriptPath().Match(text);
        if (!path.Success) return new AutomationIssue(severity, "scripts", text);
        Match where = ScriptLine().Match(text, path.Length);
        string message = text[path.Length..].TrimStart(' ', ':');
        message = ScriptLocationPrefix().Replace(message, string.Empty).Trim();
        return new AutomationIssue(severity, "scripts", message, path.Groups["path"].Value.Trim().Replace('\\', '/'),
            where.Success ? int.Parse(where.Groups["line"].Value) : null, where.Success ? int.Parse(where.Groups["column"].Value) : null);
    }

    [GeneratedRegex(@"^(?<path>.+?\.pgsl)", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPath();

    [GeneratedRegex(@"line (?<line>\d+)(?:, column |:)(?<column>\d+)")]
    private static partial Regex ScriptLine();

    [GeneratedRegex(@"^\(line \d+, column \d+\):\s*")]
    private static partial Regex ScriptLocationPrefix();

    /// <summary>One line per change: when, who, the command, its input and what it changed.</summary>
    public void Audit(string command, string input, AutomationResult result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AuditLogPath)!);
            string changed = result.Changed.Count == 0 ? "-" : string.Join(", ", result.Changed);
            File.AppendAllText(AuditLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{Caller}\t{command}\t{(result.Ok ? "ok" : "failed")}\t{changed}\t{input}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The audit log never stops the work it records.
        }
    }
}

/// <summary>A command could not do what it was asked; its message goes back to the caller as the result.</summary>
public sealed class AutomationException(string message) : Exception(message);
