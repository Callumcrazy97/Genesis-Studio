using System.Text.Json;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Projects;

public sealed class ProjectValidator
{
    private readonly ProjectService _projectService = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public IReadOnlyList<ProjectValidationIssue> Validate(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        List<ProjectValidationIssue> issues = [.. _projectService.Validate(session)];
        if (!Directory.Exists(session.AssetsPath))
        {
            return issues;
        }

        string[] resourceFiles = Directory.EnumerateFiles(
                session.AssetsPath,
                "*",
                SearchOption.AllDirectories)
            .Where(path => !ResourceAssociates.IsHiddenImplementationFile(path))
            .ToArray();
        HashSet<string> associateFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (string resource in resourceFiles.Where(
                     ResourceAssociates.IsDesignerVisibleResourcePath))
        {
            foreach (string associate in ResourceAssociates.Find(resource))
            {
                if (Directory.Exists(associate))
                {
                    foreach (string nested in Directory.EnumerateFiles(
                                 associate,
                                 "*",
                                 SearchOption.AllDirectories))
                    {
                        associateFiles.Add(Path.GetFullPath(nested));
                    }
                }
                else
                {
                    associateFiles.Add(Path.GetFullPath(associate));
                }
            }
        }

        Dictionary<Guid, string> guidOwners = [];
        foreach (string metadataFile in Directory.EnumerateFiles(
                     session.AssetsPath,
                     "*.meta",
                     SearchOption.AllDirectories)
                 .Where(path => !associateFiles.Contains(
                     Path.GetFullPath(path[..^".meta".Length]))))
        {
            ValidateMetadata(metadataFile, guidOwners, issues);
        }

        foreach (string resourceFile in resourceFiles.Where(
                     path => !associateFiles.Contains(Path.GetFullPath(path))
                             && ResourceAssociates.IsDesignerVisibleResourcePath(path)))
        {
            if (!File.Exists(resourceFile + ".meta"))
            {
                issues.Add(new ProjectValidationIssue(
                    ProjectValidationSeverity.Warning,
                    "ASSET_META_MISSING",
                    "The resource has no GUID metadata sidecar. Refresh Assets to repair it.",
                    resourceFile));
            }
        }

        return issues;
    }

    private void ValidateMetadata(
        string metadataFile,
        Dictionary<Guid, string> guidOwners,
        List<ProjectValidationIssue> issues)
    {
        try
        {
            AssetMetadata? metadata = JsonSerializer.Deserialize<AssetMetadata>(
                File.ReadAllText(metadataFile),
                _jsonOptions);
            if (metadata is null || !Guid.TryParseExact(metadata.Guid, "N", out Guid id))
            {
                issues.Add(new ProjectValidationIssue(
                    ProjectValidationSeverity.Error,
                    "ASSET_GUID_INVALID",
                    "The asset metadata does not contain a valid 32-character GUID.",
                    metadataFile));
                return;
            }

            if (guidOwners.TryGetValue(id, out string? existingOwner))
            {
                issues.Add(new ProjectValidationIssue(
                    ProjectValidationSeverity.Error,
                    "ASSET_GUID_DUPLICATE",
                    $"The asset GUID is also used by '{existingOwner}'.",
                    metadataFile));
            }
            else
            {
                guidOwners[id] = metadataFile;
            }
        }
        catch (JsonException exception)
        {
            issues.Add(new ProjectValidationIssue(
                ProjectValidationSeverity.Error,
                "ASSET_META_INVALID_JSON",
                exception.Message,
                metadataFile));
        }
        catch (IOException exception)
        {
            issues.Add(new ProjectValidationIssue(
                ProjectValidationSeverity.Error,
                "ASSET_META_UNREADABLE",
                exception.Message,
                metadataFile));
        }
    }
}
