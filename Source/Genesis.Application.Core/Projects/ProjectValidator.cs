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

        // Rooms and scripts name resources without their kind, so two kinds sharing a name stop the game.
        foreach (Genesis.Shared.Assets.NamedResource[] named in Genesis.Shared.Assets.ResourceCatalog.For(session.RootPath).DuplicateNames)
        {
            issues.Add(new ProjectValidationIssue(
                ProjectValidationSeverity.Error,
                "RESOURCE_NAME_DUPLICATE",
                Genesis.Shared.Assets.ResourceCatalog.DescribeDuplicate(named, session.RootPath),
                named[0].FullPath));
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

        issues.AddRange(ValidateShaderNames(resourceFiles.Where(
            path => path.EndsWith(".shader.json", StringComparison.OrdinalIgnoreCase))));
        return issues;
    }

    /// <summary>
    /// Warns about code Shader resources that declare an HLSL reserved word (line, point, sample...)
    /// as a name. The compiler only says "unexpected token", and a post effect that fails is never
    /// drawn, so a game would otherwise just lose the effect.
    /// </summary>
    public static IEnumerable<ProjectValidationIssue> ValidateShaderNames(IEnumerable<string> shaderFiles)
    {
        foreach (string shaderFile in shaderFiles)
        {
            Genesis.Shared.Assets.ShaderAssetDocument document;
            try
            {
                document = Genesis.Shared.Assets.ShaderAssetDocument.Load(shaderFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                continue; // an unreadable resource is reported by its editor and by the draw that uses it
            }

            if (document.AuthoringMode != Genesis.Shared.Assets.ShaderAuthoringMode.Code) continue;
            HashSet<string> sources = new(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(document.Source)) sources.Add(document.Source);
            foreach (Genesis.Shared.Assets.ShaderPassDefinition pass in document.Passes ?? [])
            {
                if (pass.Enabled && !string.IsNullOrWhiteSpace(pass.Source)) sources.Add(pass.Source);
            }

            foreach (string source in sources)
            {
                var uses = Genesis.Shared.Assets.HlslReservedNames.Find(source);
                if (uses.Count == 0) continue;
                yield return new ProjectValidationIssue(
                    ProjectValidationSeverity.Warning,
                    "SHADER_RESERVED_NAME",
                    "The shader will not compile: " + Genesis.Shared.Assets.HlslReservedNames.Describe(uses),
                    shaderFile);
            }
        }
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
