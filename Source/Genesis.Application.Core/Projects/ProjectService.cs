using System.Text.Json;
using System.Text.RegularExpressions;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Projects;

public sealed partial class ProjectService
{
    public const string ProjectExtension = ".genesisproj";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly string[] InitialFolders =
    [
        "Assets",
        "Build",
        "Packages",
        "ProjectSettings",
        ".genesis/Backups",
        ".genesis/Cache",
        ".genesis/Logs",
        ".genesis/Trash",
    ];

    public ProjectSession CreateProject(
        string parentDirectory,
        string projectName,
        string template = "Blank",
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        string safeName = SanitizeProjectName(projectName);
        string projectRoot = Path.Combine(Path.GetFullPath(parentDirectory), safeName);
        if (Directory.Exists(projectRoot) && Directory.EnumerateFileSystemEntries(projectRoot).Any())
        {
            if (overwrite)
            {
                Directory.Delete(projectRoot, recursive: true);
            }
            else
            {
                throw new IOException($"The project folder '{projectRoot}' already exists and is not empty.");
            }
        }

        Directory.CreateDirectory(projectRoot);
        foreach (string folder in InitialFolders)
        {
            Directory.CreateDirectory(Path.Combine(
                projectRoot,
                folder.Replace('/', Path.DirectorySeparatorChar)));
        }

        string normalizedTemplate = NormalizeTemplate(template);
        ProjectManifest manifest = new()
        {
            ProjectId = Guid.NewGuid().ToString("N"),
            Name = safeName,
            DefaultNamespace = BuildNamespace(safeName),
            Template = normalizedTemplate,
            EnabledPacks = PacksForTemplate(normalizedTemplate),
        };
        TextureGroupCatalog.ApplyToManifest(manifest);

        string projectFile = Path.Combine(projectRoot, safeName + ProjectExtension);
        WriteManifest(projectFile, manifest);
        WriteProjectSettings(projectRoot);
        ProjectSession session = new(projectRoot, projectFile, manifest);
        ResourceFolderPolicy.EnsureRoots(session);
        if (normalizedTemplate == Templates.LuigisMansionTemplate.TemplateId)
        {
            Templates.LuigisMansionTemplate.Apply(session);
        }
        else if (normalizedTemplate == Templates.TwoDShowcaseTemplate.TemplateId)
        {
            Templates.TwoDShowcaseTemplate.Apply(session);
        }
        else if (normalizedTemplate == "2D")
        {
            // A template's job is that F5 works before you have typed anything, so this fills the
            // project with a complete playable platformer rather than an empty room.
            Templates.PlatformerTemplate.Apply(session);
        }
        else if (normalizedTemplate == "2DDungeonCrawler")
        {
            Templates.CryptsOfGenesisTemplate.Apply(session);
        }
        else if (normalizedTemplate is "3DNatureWalk" or "NatureWalk" or "Nature")
        {
            Templates.NatureWalkTemplate.Apply(session);
        }
        else if (normalizedTemplate is "3D" or "3DSandbox")
        {
            Templates.SandboxTemplate.Apply(session);
        }
        else
        {
            ResourceService resources = new(session);
            resources.CreateResource(
                Path.Combine(session.AssetsPath, "Rooms"),
                ResourceKind.Room,
                "Start");
        }

        // Templates update their start room after the initial manifest is created. Persist that
        // choice so opening the project and pressing F5 launches the playable room immediately.
        ResourceNameMigration.Upgrade(session);
        WriteManifest(projectFile, manifest);

        return session;
    }

    public ProjectSession OpenProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string fullPath = Path.GetFullPath(projectPath);
        string projectFile;

        if (Directory.Exists(fullPath))
        {
            string[] matches = Directory.GetFiles(fullPath, "*" + ProjectExtension, SearchOption.TopDirectoryOnly);
            projectFile = matches.Length switch
            {
                0 => throw new FileNotFoundException(
                    $"No {ProjectExtension} file exists in '{fullPath}'."),
                1 => matches[0],
                _ => throw new InvalidDataException(
                    $"Multiple {ProjectExtension} files exist in '{fullPath}'."),
            };
        }
        else
        {
            projectFile = fullPath;
        }

        if (!File.Exists(projectFile))
        {
            throw new FileNotFoundException("The Genesis project file does not exist.", projectFile);
        }

        string json = File.ReadAllText(projectFile);
        ProjectManifest? manifest = JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidDataException("The Genesis project manifest is empty.");
        }

        string root = Path.GetDirectoryName(projectFile)
            ?? throw new InvalidDataException("The project file has no parent directory.");

        ProjectSession session = new(root, projectFile, manifest);
        // Template hotfixes are deliberately narrow and backup-first. This lets an already-created
        // showcase project receive fixes without asking the user to delete/recreate the project.
        if (string.Equals(manifest.Template, Templates.LuigisMansionTemplate.TemplateId, StringComparison.OrdinalIgnoreCase)
            && Templates.LuigisMansionTemplate.UpgradeIfNeeded(session))
        {
            WriteManifest(projectFile, manifest);
        }
        // Add missing roots without moving existing resources or breaking legacy references.
        ResourceFolderPolicy.EnsureRoots(session);
        ResourceNameMigration.Upgrade(session);
        TextureGroupCatalog.EnsureAndBackfill(session, this);
        return session;
    }

    public IReadOnlyList<ProjectValidationIssue> Validate(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        List<ProjectValidationIssue> issues = [];

        if (!Directory.Exists(session.AssetsPath))
        {
            issues.Add(new ProjectValidationIssue(
                ProjectValidationSeverity.Error,
                "PROJECT_ASSETS_MISSING",
                "The project Assets folder is missing.",
                session.AssetsPath));
        }

        if (session.Manifest.SchemaVersion > ProjectManifest.CurrentSchemaVersion)
        {
            issues.Add(new ProjectValidationIssue(
                ProjectValidationSeverity.Error,
                "PROJECT_SCHEMA_NEWER",
                "This project was created by a newer Genesis Application version.",
                session.ProjectFile));
        }

        if (!Guid.TryParseExact(session.Manifest.ProjectId, "N", out _))
        {
            issues.Add(new ProjectValidationIssue(
                ProjectValidationSeverity.Error,
                "PROJECT_ID_INVALID",
                "The project ID must be a 32-character GUID.",
                session.ProjectFile));
        }

        foreach (var conflict in ResourceNames.For(session.RootPath).Conflicts)
            issues.Add(new ProjectValidationIssue(ProjectValidationSeverity.Error, "RESOURCE_NAME_DUPLICATE",
                $"Resource name '{conflict.Key}' must be unique across the whole project.", session.AssetsPath));
        string startRoom = ResourceNames.Resolve(session.RootPath, session.Manifest.StartRoom, ResourceType.Room);
        if (!File.Exists(startRoom))
        {
            issues.Add(new ProjectValidationIssue(
                ProjectValidationSeverity.Error,
                "PROJECT_START_ROOM_MISSING",
                "The configured start room does not exist.",
                startRoom));
        }

        return issues;
    }

    public void Save(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Manifest.StartRoom = ResourceNames.Name(session.RootPath, session.Manifest.StartRoom, ResourceType.Room);
        session.Manifest.ProjectIcon = ResourceNames.Name(session.RootPath, session.Manifest.ProjectIcon, ResourceType.Image);
        session.Manifest.ModifiedUtc = DateTime.UtcNow;
        WriteManifest(session.ProjectFile, session.Manifest);
    }

    private static void WriteManifest(string projectFile, ProjectManifest manifest)
    {
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(projectFile, json);
    }

    private static void WriteProjectSettings(string projectRoot)
    {
        string file = Path.Combine(projectRoot, "ProjectSettings", "editor.json");
        const string content =
            """
            {
              "schemaVersion": 1,
              "workspacePreset": "Default",
              "assetSerialization": "Text",
              "colorSpace": "Linear"
            }
            """;
        File.WriteAllText(file, content);
    }

    public static string SanitizeProjectName(string projectName)
    {
        string trimmed = projectName.Trim();
        string safe = InvalidProjectNameCharacters().Replace(trimmed, "-").Trim(' ', '.', '-');
        if (string.IsNullOrWhiteSpace(safe))
        {
            throw new ArgumentException("The project name contains no valid characters.", nameof(projectName));
        }

        return safe;
    }

    private static string BuildNamespace(string name)
    {
        string compact = NonIdentifierCharacters().Replace(name, string.Empty);
        if (string.IsNullOrEmpty(compact))
        {
            return "Game";
        }

        return char.IsDigit(compact[0]) ? "Game" + compact : compact;
    }

    private static string NormalizeTemplate(string template) =>
        template.Trim().ToUpperInvariant() switch
        {
            "2D" => "2D",
            "LUIGISMANSION" or "ALIGHTINTHEDARK" => Templates.LuigisMansionTemplate.TemplateId,
            "2DSHOWCASE" or "MUSHROOMMEADOW" => Templates.TwoDShowcaseTemplate.TemplateId,
            "2DDUNGEONCRAWLER" or "DUNGEONCRAWLER" or "CRYPTSOFGENESIS" or "CRYPTS" => "2DDungeonCrawler",
            "3DNATUREWALK" or "NATUREWALK" or "NATURE" => "3DNatureWalk",
            "3D" => "3D",
            "3DSANDBOX" => "3DSandbox",
            "VOXEL" => "Voxel",
            _ => "Blank",
        };

    private static List<string> PacksForTemplate(string template) =>
        template switch
        {
            "2D" or "2DShowcase" => ["core", "rendering", "physics", "audio"],
            "2DDungeonCrawler" or "LuigisMansion" => ["core", "rendering", "physics", "audio", "particles"],
            "3DNatureWalk" or "3DSandbox" => ["core", "rendering", "physics", "audio", "terrain", "particles"],
            "3D" => ["core", "rendering", "physics", "audio", "terrain"],
            "Voxel" => ["core", "rendering", "physics", "audio", "terrain", "voxel"],
            _ => ["core", "rendering", "physics"],
        };

    [GeneratedRegex("""[<>:"/\\|?*\x00-\x1F]""")]
    private static partial Regex InvalidProjectNameCharacters();

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NonIdentifierCharacters();
}
